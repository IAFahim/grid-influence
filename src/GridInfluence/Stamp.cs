namespace GridInfluence;

public static unsafe class Stamp
{
    public static byte Circle(sbyte strength) => Register(0, strength);
    public static byte Box(sbyte strength) => Register(1, strength);
    public static byte Ring(sbyte strength) => Register(2, strength);

    private static byte Register(byte shape, sbyte strength)
    {
        var id = World.StampNext();
        World.StampShape[id] = shape;
        World.StampStrength[id] = strength;
        World.StampData[id] = (ushort)((byte)strength | (shape << 8));
        return id;
    }
}
