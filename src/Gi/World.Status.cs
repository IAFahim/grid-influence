namespace Gi;

public static unsafe partial class World
{
    public enum WriteStatus : byte
    {
        Applied,
        StaleSource,
        NonFinite,
        InvalidLayer,
        InvalidStamp,
        WrongResidency,
        CapacityReached,
    }

    public enum ReadStatus : byte
    {
        Complete,
        Partial,
        Outside,
        InvalidLayer,
        LengthMismatch,
    }

    public static ReadStatus SenseColumn(
        byte world, byte layer, float* xs, float* ys, SourceId* excludes, int count, short* values, ulong* whole)
        => ColumnStatus(world, layer, count, xs == null || ys == null || values == null,
            SenseColumnCore(world, layer, xs, ys, excludes, count, values, whole), whole);

    public static ReadStatus SenseColumn(byte world, byte layer, float* xs, float* ys, int count, short* values, ulong* whole)
        => ColumnStatus(world, layer, count, xs == null || ys == null || values == null,
            SenseColumnCore(world, layer, xs, ys, count, values, whole), whole);

    public static ReadStatus SenseGradientColumn(
        byte world, byte layer, float* xs, float* ys, SourceId* excludes, int count,
        float* gradientXs, float* gradientYs, ulong* whole)
        => ColumnStatus(world, layer, count, xs == null || ys == null || gradientXs == null || gradientYs == null,
            SenseGradientColumnCore(world, layer, xs, ys, excludes, count, gradientXs, gradientYs, whole), whole);

    public static ReadStatus SenseGradientColumn(
        byte world, byte layer, float* xs, float* ys, SourceId* excludes, int count,
        short* values, float* gradientXs, float* gradientYs, ulong* whole)
        => ColumnStatus(world, layer, count,
            xs == null || ys == null || values == null || gradientXs == null || gradientYs == null,
            SenseGradientColumnCore(world, layer, xs, ys, excludes, count, values, gradientXs, gradientYs, whole), whole);

    public static ReadStatus SenseAreaColumn(
        byte world, byte layer, float* xs, float* ys, float* reaches, SourceId* excludes, int count, long* totals, ulong* whole)
        => ColumnStatus(world, layer, count, xs == null || ys == null || reaches == null || totals == null,
            SenseAreaColumnCore(world, layer, xs, ys, reaches, excludes, count, totals, whole), whole);

    public static ReadStatus SenseMaxColumn(
        byte world, byte layer, float* xs, float* ys, float* reaches, int count,
        short* values, float* bestXs, float* bestYs, ulong* whole)
        => ColumnStatus(world, layer, count,
            xs == null || ys == null || reaches == null || values == null || bestXs == null || bestYs == null,
            SenseMaxColumnCore(world, layer, xs, ys, reaches, count, values, bestXs, bestYs, whole), whole);

    public static ReadStatus SenseNearestColumn(
        byte world, byte layer, float* xs, float* ys, float* reaches, short* thresholds, int count,
        short* values, float* nearestXs, float* nearestYs, ulong* found)
        => ColumnStatus(world, layer, count,
            xs == null || ys == null || reaches == null || thresholds == null ||
            values == null || nearestXs == null || nearestYs == null,
            SenseNearestColumnCore(world, layer, xs, ys, reaches, thresholds, count, values, nearestXs, nearestYs, found),
            null);

    private static ReadStatus ColumnStatus(byte world, byte layer, int count, bool missing, bool complete, ulong* whole)
    {
        var w = GetContext(world);
        if (w == null || layer >= w->LayerCount) return ReadStatus.InvalidLayer;
        if (count <= 0) return ReadStatus.Complete;
        if (missing) return ReadStatus.LengthMismatch;
        if (complete) return ReadStatus.Complete;
        if (whole == null) return ReadStatus.Partial;

        var words = count >> 6;
        for (var i = 0; i < words; i++)
            if (whole[i] != 0) return ReadStatus.Partial;

        var tail = count & 63;
        return tail != 0 && (whole[words] & ((1ul << tail) - 1)) != 0 ? ReadStatus.Partial : ReadStatus.Outside;
    }
}
