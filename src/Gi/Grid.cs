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
}
