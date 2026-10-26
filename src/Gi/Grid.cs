using System.Runtime.CompilerServices;
namespace Gi;

public static class Grid
{
    [MethodImpl(Jit.Hot)]
    public static byte New(byte world, int power, float x, float y, float size)
    {
        return World.AddGrid(world, power, x, y, size);
    }

    [MethodImpl(Jit.Hot)]
    public static int Count(byte world)
    {
        return World.GridCount(world);
    }

    [MethodImpl(Jit.Hot)]
    public static int CopyIds(byte world, Span<byte> destination)
    {
        return World.CopyGridIds(world, destination);
    }

    [MethodImpl(Jit.Hot)]
    public static bool TryGetSpec(byte world, byte grid, out int power, out float x, out float y, out float size)
    {
        return World.TryGetGridSpec(world, grid, out power, out x, out y, out size);
    }

    [MethodImpl(Jit.Hot)]
    public static int SpatialKeys(byte world, byte grid, ReadOnlySpan<float> xs, ReadOnlySpan<float> ys,
        Span<uint> keys)
    {
        return World.SpatialKeys(world, grid, xs, ys, keys);
    }

    [MethodImpl(Jit.Hot)]
    public static bool SortByKeys(ReadOnlySpan<uint> keys, Span<int> permutation, Span<ulong> scratch)
    {
        return SpatialOrder.Sort(keys, permutation, scratch);
    }

    [MethodImpl(Jit.Hot)]
    public static float Disorder(ReadOnlySpan<uint> keys)
    {
        return SpatialOrder.Disorder(keys);
    }
}
