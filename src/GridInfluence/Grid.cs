namespace GridInfluence;

public static class Grid
{
    public static byte New(byte world, int power, float x, float y, float size)
        => World.AddGrid(world, power, x, y, size);
}
