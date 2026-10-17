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

        ApplyTransformRanges(context, ranges);
        return BatchStatus.Applied;
    }

    private static bool BorrowOverlap(void* first, long firstBytes, void* second, long secondBytes)
    {
        if (firstBytes <= 0 || secondBytes <= 0 || first == null || second == null) return false;
        var a = (long)first;
        var b = (long)second;
        return a < b + secondBytes && b < a + firstBytes;
    }

    private static void ApplyTransformRanges(WorldCtx* context, ReadOnlySpan<TransformRange> ranges)
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

                WriteMoveExact(context, source, slot, x, y);
                if (headings != null && float.IsFinite(headings[i]))
                    WriteTurn(context, source, slot, QuantizeAngle(headings[i]));
                if (scales != null && float.IsFinite(scales[i]))
                    WriteScale(context, source, slot, QuantizeScale(scales[i]));
            }
        }
    }
}
