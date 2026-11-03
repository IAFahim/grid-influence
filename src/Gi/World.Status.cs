using System.Runtime.CompilerServices;
namespace Gi;

public static unsafe partial class World
{
    public enum ReadStatus : byte
    {
        Complete,
        Partial,
        Outside,
        InvalidLayer,
        LengthMismatch
    }

    public enum WriteStatus : byte
    {
        Applied,
        StaleSource,
        NonFinite,
        InvalidLayer,
        InvalidStamp,
        WrongResidency,
        CapacityReached
    }

    [MethodImpl(Jit.Hot)]
    public static ReadStatus SenseColumn(byte world, byte layer, float* xs, float* ys, SourceId* excludes, int count, short* values, ulong* whole)
    {
        fixed (GiRoot* root = &Runtime.Root) return SenseColumn(root, world, layer, xs, ys, excludes, count, values, whole);
    }
    [MethodImpl(Jit.Hot)]
    internal static ReadStatus SenseColumn(GiRoot* root, byte world, byte layer, float* xs, float* ys, SourceId* excludes, int count, short* values, ulong* whole)
    {
        return ColumnStatus(root, world, layer, count, xs == null || ys == null || values == null, SenseColumnCore(root, world, layer, xs, ys, excludes, count, values, whole), whole);
    }
    [MethodImpl(Jit.Hot)]
    public static ReadStatus SenseColumn(byte world, byte layer, float* xs, float* ys, int count, short* values, ulong* whole)
    {
        fixed (GiRoot* root = &Runtime.Root) return SenseColumn(root, world, layer, xs, ys, count, values, whole);
    }
    [MethodImpl(Jit.Hot)]
    internal static ReadStatus SenseColumn(GiRoot* root, byte world, byte layer, float* xs, float* ys, int count, short* values, ulong* whole)
    {
        return ColumnStatus(root, world, layer, count, xs == null || ys == null || values == null, SenseColumnCore(root, world, layer, xs, ys, count, values, whole), whole);
    }
    [MethodImpl(Jit.Hot)]
    public static ReadStatus SenseGradientColumn(byte world, byte layer, float* xs, float* ys, SourceId* excludes, int count, float* gradientXs, float* gradientYs, ulong* whole)
    {
        fixed (GiRoot* root = &Runtime.Root) return SenseGradientColumn(root, world, layer, xs, ys, excludes, count, gradientXs, gradientYs, whole);
    }
    [MethodImpl(Jit.Hot)]
    internal static ReadStatus SenseGradientColumn(GiRoot* root, byte world, byte layer, float* xs, float* ys, SourceId* excludes, int count, float* gradientXs, float* gradientYs, ulong* whole)
    {
        return ColumnStatus(root, world, layer, count, xs == null || ys == null || gradientXs == null || gradientYs == null, SenseGradientColumnCore(root, world, layer, xs, ys, excludes, count, gradientXs, gradientYs, whole), whole);
    }
    [MethodImpl(Jit.Hot)]
    public static ReadStatus SenseGradientColumn(byte world, byte layer, float* xs, float* ys, SourceId* excludes, int count, short* values, float* gradientXs, float* gradientYs, ulong* whole)
    {
        fixed (GiRoot* root = &Runtime.Root) return SenseGradientColumn(root, world, layer, xs, ys, excludes, count, values, gradientXs, gradientYs, whole);
    }
    [MethodImpl(Jit.Hot)]
    internal static ReadStatus SenseGradientColumn(GiRoot* root, byte world, byte layer, float* xs, float* ys, SourceId* excludes, int count, short* values, float* gradientXs, float* gradientYs, ulong* whole)
    {
        return ColumnStatus(root, world, layer, count, xs == null || ys == null || values == null || gradientXs == null || gradientYs == null, SenseGradientColumnCore(root, world, layer, xs, ys, excludes, count, values, gradientXs, gradientYs, whole), whole);
    }
    [MethodImpl(Jit.Hot)]
    public static ReadStatus SenseAreaColumn(byte world, byte layer, float* xs, float* ys, float* reaches, SourceId* excludes, int count, long* totals, ulong* whole)
    {
        fixed (GiRoot* root = &Runtime.Root) return SenseAreaColumn(root, world, layer, xs, ys, reaches, excludes, count, totals, whole);
    }
    [MethodImpl(Jit.Hot)]
    internal static ReadStatus SenseAreaColumn(GiRoot* root, byte world, byte layer, float* xs, float* ys, float* reaches, SourceId* excludes, int count, long* totals, ulong* whole)
    {
        return ColumnStatus(root, world, layer, count, xs == null || ys == null || reaches == null || totals == null, SenseAreaColumnCore(root, world, layer, xs, ys, reaches, excludes, count, totals, whole), whole);
    }
    [MethodImpl(Jit.Hot)]
    public static ReadStatus SenseMaxColumn(byte world, byte layer, float* xs, float* ys, float* reaches, int count, short* values, float* bestXs, float* bestYs, ulong* whole)
    {
        fixed (GiRoot* root = &Runtime.Root) return SenseMaxColumn(root, world, layer, xs, ys, reaches, count, values, bestXs, bestYs, whole);
    }
    [MethodImpl(Jit.Hot)]
    internal static ReadStatus SenseMaxColumn(GiRoot* root, byte world, byte layer, float* xs, float* ys, float* reaches, int count, short* values, float* bestXs, float* bestYs, ulong* whole)
    {
        return ColumnStatus(root, world, layer, count, xs == null || ys == null || reaches == null || values == null || bestXs == null || bestYs == null, SenseMaxColumnCore(root, world, layer, xs, ys, reaches, count, values, bestXs, bestYs, whole), whole);
    }
    [MethodImpl(Jit.Hot)]
    public static ReadStatus SenseNearestColumn(byte world, byte layer, float* xs, float* ys, float* reaches, short* thresholds, int count, short* values, float* nearestXs, float* nearestYs, ulong* found)
    {
        fixed (GiRoot* root = &Runtime.Root) return SenseNearestColumn(root, world, layer, xs, ys, reaches, thresholds, count, values, nearestXs, nearestYs, found);
    }
    [MethodImpl(Jit.Hot)]
    internal static ReadStatus SenseNearestColumn(GiRoot* root, byte world, byte layer, float* xs, float* ys, float* reaches, short* thresholds, int count, short* values, float* nearestXs, float* nearestYs, ulong* found)
    {
        return ColumnStatus(root, world, layer, count, xs == null || ys == null || reaches == null || thresholds == null || values == null || nearestXs == null || nearestYs == null, SenseNearestColumnCore(root, world, layer, xs, ys, reaches, thresholds, count, values, nearestXs, nearestYs, found), null);
    }
    [MethodImpl(Jit.Hot)]
    private static ReadStatus ColumnStatus(byte world, byte layer, int count, bool missing, bool complete, ulong* whole)
    {
        fixed (GiRoot* root = &Runtime.Root) return ColumnStatus(root, world, layer, count, missing, complete, whole);
    }
    [MethodImpl(Jit.Hot)]
    internal static ReadStatus ColumnStatus(GiRoot* root, byte world, byte layer, int count, bool missing, bool complete, ulong* whole)
    {
        var w = GetContext(root, world);
        if (w == null || layer >= w->LayerCount) return ReadStatus.InvalidLayer;
        if (count <= 0) return ReadStatus.Complete;
        if (missing) return ReadStatus.LengthMismatch;
        if (complete) return ReadStatus.Complete;
        if (whole == null) return ReadStatus.Partial;
        var words = count >> 6;
        for (var i = 0; i < words; i++)
            if (whole[i] != 0)
                return ReadStatus.Partial;
        var tail = count & 63;
        return tail != 0 && (whole[words] & ((1ul << tail) - 1)) != 0 ? ReadStatus.Partial : ReadStatus.Outside;
    }
}
