namespace Gi;

public static unsafe partial class World
{
    public struct TransformRange
    {
        public SourceId* Sources;
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
                var slot = (int)id.Slot;
                var x = xs[i];
                var y = ys[i];
                if ((uint)slot >= (uint)source->Count || source->Alive[slot] == 0 ||
                    id.Generation != source->Gen[slot] || !float.IsFinite(x) || !float.IsFinite(y))
                {
                    if (invalid != null) invalid[i >> 6] |= 1ul << (i & 63);
                    continue;
                }

                var oldX = source->X[slot];
                var oldY = source->Y[slot];
                var oldAngle = source->Angle[slot];
                var oldScale = source->Scale[slot];
                var angle = oldAngle;
                if (headings != null && float.IsFinite(headings[i]))
                    angle = QuantizeSourceBatchAngle(headings[i]);
                var scale = oldScale;
                if (scales != null && float.IsFinite(scales[i]))
                    scale = QuantizeSourceBatchScale(scales[i]);
                var rawPosChanged = *(int*)&x != *(int*)&oldX || *(int*)&y != *(int*)&oldY;
                if (!rawPosChanged && angle == oldAngle && scale == oldScale) continue;

                var layer = source->Layer[slot];
                var posChanged = rawPosChanged && PositionChanged(context, layer, oldX, oldY, x, y);
                var angChanged = angle != oldAngle && AngleChanged(context, layer, oldAngle, angle);
                var scaleChanged = scale != oldScale;
                var changed = posChanged || angChanged || scaleChanged;
                if (changed)
                    BeforeWriteState(context, source, slot, new SourceBatchState
                    {
                        PositionX = posChanged ? oldX : x,
                        PositionY = posChanged ? oldY : y,
                        Angle = posChanged || angChanged ? oldAngle : angle,
                        Scale = posChanged || angChanged || scaleChanged ? oldScale : scale,
                        Stamp = source->Stamp[slot],
                        Layer = layer,
                        Gain = source->Gain[slot],
                        Alive = source->Alive[slot],
                        Generation = source->Gen[slot]
                    });
                source->X[slot] = x;
                source->Y[slot] = y;
                source->Angle[slot] = angle;
                source->Scale[slot] = scale;
                if (changed) MarkChanged(context, source, slot);
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

        context->BatchCommands.ResizeUninitialized(totalRows);
        for (var row = 0; row < totalRows; row++)
            context->BatchCommands.Pointer[row] = new SourceBatchLink { Entry = -1, Next = -1 };

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
                var slot = (int)id.Slot;
                var x = xs[i];
                var y = ys[i];
                if ((uint)slot >= (uint)source->Count || source->Alive[slot] == 0 ||
                    id.Generation != source->Gen[slot] || !float.IsFinite(x) || !float.IsFinite(y))
                {
                    if (invalid != null) invalid[i >> 6] |= 1ul << (i & 63);
                    continue;
                }

                var entryIndex = GetSourceBatchEntry(context, slot, ref entries);
                var entry = context->BatchSources.Pointer + entryIndex;
                if (entry->FirstCommand < 0)
                {
                    entry->State = ReadSourceBatchState(source, slot);
                    entry->FirstCommand = row;
                }

                context->BatchCommands.Pointer[row].Entry = entryIndex;
                var fold = &entry->State;
                if (*(int*)&x != *(int*)&fold->PositionX || *(int*)&y != *(int*)&fold->PositionY)
                {
                    if (PositionChanged(context, fold->Layer, fold->PositionX, fold->PositionY, x, y))
                        MarkSourceBatchDeposit(entry, row, in *fold);
                    fold->PositionX = x;
                    fold->PositionY = y;
                }

                if (headings != null && float.IsFinite(headings[i]))
                {
                    var angle = QuantizeSourceBatchAngle(headings[i]);
                    if (angle != fold->Angle)
                    {
                        if (AngleChanged(context, fold->Layer, fold->Angle, angle))
                            MarkSourceBatchDeposit(entry, row, in *fold);
                        fold->Angle = angle;
                    }
                }

                if (scales != null && float.IsFinite(scales[i]))
                {
                    var next = QuantizeSourceBatchScale(scales[i]);
                    if (next != fold->Scale)
                    {
                        MarkSourceBatchDeposit(entry, row, in *fold);
                        fold->Scale = next;
                    }
                }
            }

            rowBase += range.Count;
        }

        context->BatchSources.Resize(entries);
        ApplySourceBatchRange(context, totalRows, 0, entries);
    }
}
