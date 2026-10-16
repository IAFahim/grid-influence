namespace Gi;

public static unsafe partial class World
{
    public struct TransformRange
    {
        public int* Sources;
        public float* Xs;
        public float* Ys;
        public float* Headings;
        public float* Scales;
        public ulong* Invalid;
        public int Count;
        public int InvalidWords;
    }

    public static BatchStatus SyncTransforms(Token token, ReadOnlySpan<TransformRange> ranges)
    {
        var status = ValidateToken(token, out var context);
        return status != BatchStatus.Applied ? status : SyncTransformRanges(context, ranges);
    }

    internal static BatchStatus SyncTransformRanges(WorldCtx* context, ReadOnlySpan<TransformRange> ranges)
    {
        long totalRows = 0;
        for (var index = 0; index < ranges.Length; index++)
        {
            ref readonly var range = ref ranges[index];
            if (range.Count < 0 || range.InvalidWords < 0) return BatchStatus.InvalidRange;
            if (range.Count > 0 && (range.Sources == null || range.Xs == null || range.Ys == null))
                return BatchStatus.InvalidRange;
            if (range.Invalid != null && range.InvalidWords < (range.Count + 63) >> 6)
                return BatchStatus.InvalidRange;
            totalRows += range.Count;
            if (totalRows > MaxSourceSlots) return BatchStatus.TooManyCommands;
        }

        for (var writer = 0; writer < ranges.Length; writer++)
        {
            ref readonly var mask = ref ranges[writer];
            if (mask.Invalid == null || mask.Count == 0) continue;
            var maskBytes = (long)((mask.Count + 63) >> 6) * sizeof(ulong);
            for (var reader = 0; reader < ranges.Length; reader++)
            {
                ref readonly var columns = ref ranges[reader];
                if (columns.Count == 0) continue;
                var rowBytes = (long)columns.Count;
                if (BorrowOverlap(mask.Invalid, maskBytes, columns.Sources, rowBytes * sizeof(int)) ||
                    BorrowOverlap(mask.Invalid, maskBytes, columns.Xs, rowBytes * sizeof(float)) ||
                    BorrowOverlap(mask.Invalid, maskBytes, columns.Ys, rowBytes * sizeof(float)) ||
                    BorrowOverlap(mask.Invalid, maskBytes, columns.Headings, rowBytes * sizeof(float)) ||
                    BorrowOverlap(mask.Invalid, maskBytes, columns.Scales, rowBytes * sizeof(float)) ||
                    (reader != writer && BorrowOverlap(
                        mask.Invalid, maskBytes, columns.Invalid, ((long)columns.Count + 63) / 64 * sizeof(ulong))))
                    return BatchStatus.OverlappingBuffers;
            }
        }

        for (var index = 0; index < ranges.Length; index++)
        {
            ref readonly var range = ref ranges[index];
            if (range.Invalid == null || range.Count == 0) continue;
            new Span<ulong>(range.Invalid, (range.Count + 63) >> 6).Clear();
        }

        if (totalRows == 0) return BatchStatus.Applied;
        if (totalRows < LargeBatchThreshold)
        {
            ApplySmallTransformRanges(context, ranges);
            return BatchStatus.Applied;
        }

        ApplyLargeTransformRanges(context, ranges, (int)totalRows);
        return BatchStatus.Applied;
    }

    private static bool BorrowOverlap(void* first, long firstBytes, void* second, long secondBytes)
    {
        if (firstBytes <= 0 || secondBytes <= 0 || first == null || second == null) return false;
        var a = (long)first;
        var b = (long)second;
        return a < b + secondBytes && b < a + firstBytes;
    }

    private static void ApplySmallTransformRanges(WorldCtx* context, ReadOnlySpan<TransformRange> ranges)
    {
        var source = &context->Sources;
        for (var r = 0; r < ranges.Length; r++)
        {
            ref readonly var range = ref ranges[r];
            var sources = range.Sources;
            var xs = range.Xs;
            var ys = range.Ys;
            var headings = range.Headings;
            var scales = range.Scales;
            var invalid = range.Invalid;
            for (var i = 0; i < range.Count; i++)
            {
                var id = sources[i];
                var slot = id & SourceIndexMask;
                var x = xs[i];
                var y = ys[i];
                if (id < 0 || slot >= source->Count || source->Alive.Pointer[slot] == 0 ||
                    (uint)id >> 24 != source->Gen.Pointer[slot] || !float.IsFinite(x) || !float.IsFinite(y))
                {
                    if (invalid != null) invalid[i >> 6] |= 1ul << (i & 63);
                    continue;
                }

                var oldX = source->X.Pointer[slot];
                var oldY = source->Y.Pointer[slot];
                var oldAngle = source->Angle.Pointer[slot];
                var oldScale = source->Scale.Pointer[slot];
                var angle = oldAngle;
                if (headings != null && float.IsFinite(headings[i]))
                    angle = QuantizeSourceBatchAngle(headings[i]);
                var scale = oldScale;
                if (scales != null && float.IsFinite(scales[i]))
                    scale = QuantizeSourceBatchScale(scales[i]);
                var rawPosChanged = *(int*)&x != *(int*)&oldX || *(int*)&y != *(int*)&oldY;
                if (!rawPosChanged && angle == oldAngle && scale == oldScale) continue;

                var layer = source->Layer.Pointer[slot];
                var posChanged = rawPosChanged && PositionChanged(context, layer, oldX, oldY, x, y);
                var angChanged = angle != oldAngle && AngleChanged(context, layer, oldAngle, angle);
                var scaleChanged = scale != oldScale;
                var gain = (sbyte)source->Gain.Pointer[slot];
                if (posChanged) EnqueueMutation(context, source, slot, x, y, gain, oldAngle, oldScale, true);
                source->X.Pointer[slot] = x;
                source->Y.Pointer[slot] = y;
                if (angChanged) EnqueueMutation(context, source, slot, x, y, gain, angle, oldScale, true);
                source->Angle.Pointer[slot] = angle;
                if (scaleChanged) EnqueueMutation(context, source, slot, x, y, gain, angle, scale, true);
                source->Scale.Pointer[slot] = scale;
            }
        }
    }

    private static void ApplyLargeTransformRanges(WorldCtx* context, ReadOnlySpan<TransformRange> ranges, int totalRows)
    {
        var source = &context->Sources;
        var capacity = Math.Min(totalRows, source->Count);
        context->BatchSources.ResizeUninitialized(capacity);
        var lookupSize = 16;
        while (lookupSize < capacity * 2) lookupSize <<= 1;
        context->BatchLookup.ResizeUninitialized(lookupSize);
        context->BatchLookup.Span.Clear();

        var entries = 0;
        var rowBase = 0;
        for (var r = 0; r < ranges.Length; r++)
        {
            ref readonly var range = ref ranges[r];
            var sources = range.Sources;
            var xs = range.Xs;
            var ys = range.Ys;
            var headings = range.Headings;
            var scales = range.Scales;
            var invalid = range.Invalid;
            for (var i = 0; i < range.Count; i++)
            {
                var row = rowBase + i;
                var id = sources[i];
                var slot = id & SourceIndexMask;
                var x = xs[i];
                var y = ys[i];
                if (id < 0 || slot >= source->Count || source->Alive.Pointer[slot] == 0 ||
                    (uint)id >> 24 != source->Gen.Pointer[slot] || !float.IsFinite(x) || !float.IsFinite(y))
                {
                    if (invalid != null) invalid[i >> 6] |= 1ul << (i & 63);
                    continue;
                }

                var entry = context->BatchSources.Pointer + GetSourceBatchEntry(context, slot, ref entries);
                if (entry->FirstCommand < 0)
                {
                    var state = ReadSourceBatchState(source, slot);
                    entry->State = state;
                    entry->InitialAlive = state.Alive;
                    entry->InitialLayer = state.Layer;
                    entry->InitialGain = state.Gain;
                    entry->Operation = entry->OriginalPending != 0
                        ? context->Ops.Pointer[entry->OriginalPending - 1]
                        : SeedTransformOperation(slot, in state);
                    entry->FirstCommand = row;
                }

                var fold = &entry->State;
                if (*(int*)&x != *(int*)&fold->PositionX || *(int*)&y != *(int*)&fold->PositionY)
                {
                    var posChanged = PositionChanged(context, fold->Layer, fold->PositionX, fold->PositionY, x, y);
                    if (posChanged) BeginTransformDeposit(entry, row);
                    fold->PositionX = x;
                    fold->PositionY = y;
                    if (posChanged) CommitTransformDeposit(entry);
                }

                if (headings != null && float.IsFinite(headings[i]))
                {
                    var angle = QuantizeSourceBatchAngle(headings[i]);
                    if (angle != fold->Angle)
                    {
                        var angChanged = AngleChanged(context, fold->Layer, fold->Angle, angle);
                        if (angChanged) BeginTransformDeposit(entry, row);
                        fold->Angle = angle;
                        if (angChanged) CommitTransformDeposit(entry);
                    }
                }

                if (scales != null && float.IsFinite(scales[i]))
                {
                    var next = QuantizeSourceBatchScale(scales[i]);
                    if (next != fold->Scale)
                    {
                        BeginTransformDeposit(entry, row);
                        fold->Scale = next;
                        CommitTransformDeposit(entry);
                    }
                }
            }

            rowBase += range.Count;
        }

        context->BatchSources.Resize(entries);
        var assigned = context->Ops.Length;
        for (var index = 0; index < entries; index++)
        {
            var entry = context->BatchSources.Pointer + index;
            if (entry->FirstDepositCommand < 0) continue;
            entry->OperationIndex = entry->OriginalPending != 0 ? entry->OriginalPending - 1 : assigned++;
        }

        context->Ops.ResizeUninitialized(assigned);
        context->BatchLayerDeltas.ResizeUninitialized(SourceBatchLayerStride);
        context->BatchLayerDeltas.Span.Clear();
        ApplySourceBatchRange(context, 0, entries, 0);
    }

    private static void BeginTransformDeposit(SourceBatchEntry* entry, int row)
    {
        if (entry->FirstDepositCommand >= 0) return;
        if (entry->OriginalPending == 0)
        {
            entry->Operation.FromX = entry->State.PositionX;
            entry->Operation.FromY = entry->State.PositionY;
            entry->Operation.FromAngle = entry->State.Angle;
            entry->Operation.FromScale = entry->State.Scale;
        }

        entry->FirstDepositCommand = row;
    }

    private static void CommitTransformDeposit(SourceBatchEntry* entry)
    {
        entry->Operation.ToX = entry->State.PositionX;
        entry->Operation.ToY = entry->State.PositionY;
        entry->Operation.ToGain = entry->State.Alive != 0 ? (sbyte)entry->State.Gain : (sbyte)0;
        entry->Operation.ToAngle = entry->State.Angle;
        entry->Operation.ToScale = entry->State.Scale;
        entry->Operation.AliveTo = entry->State.Alive;
    }

    private static DepositOp SeedTransformOperation(int slot, in SourceBatchState state) => new()
    {
        Slot = slot,
        FromX = state.PositionX,
        FromY = state.PositionY,
        FromStamp = state.Stamp,
        FromLayer = state.Layer,
        FromGain = state.Alive != 0 ? (sbyte)state.Gain : (sbyte)0,
        FromAngle = state.Angle,
        FromScale = state.Scale,
        AliveFrom = state.Alive,
        FromGen = state.Generation,
        ToStamp = state.Stamp,
        ToLayer = state.Layer,
        ToGain = state.Alive != 0 ? (sbyte)state.Gain : (sbyte)0,
        ToAngle = state.Angle,
        ToScale = state.Scale,
        AliveTo = state.Alive
    };
}
