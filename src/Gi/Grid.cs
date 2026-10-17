namespace Gi;

public static class Grid
{
    public static byte New(byte world, int power, float x, float y, float size)
        => World.AddGrid(world, power, x, y, size);

    public static int Count(byte world) => World.GridCount(world);

    public static int CopyIds(byte world, Span<byte> destination)
        => World.CopyGridIds(world, destination);

    public static bool TryGetSpec(byte world, byte grid, out int power, out float x, out float y, out float size)
        => World.TryGetGridSpec(world, grid, out power, out x, out y, out size);

    public static int SpatialKeys(byte world, byte grid, ReadOnlySpan<float> xs, ReadOnlySpan<float> ys, Span<uint> keys)
        => World.SpatialKeys(world, grid, xs, ys, keys);

    public static bool SortByKeys(ReadOnlySpan<uint> keys, Span<int> permutation, Span<ulong> scratch)
        => SpatialOrder.Sort(keys, permutation, scratch);

    public static float Disorder(ReadOnlySpan<uint> keys) => SpatialOrder.Disorder(keys);
}
