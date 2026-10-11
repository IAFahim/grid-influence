namespace Gi;

public static class Layer
{
    public static byte New(byte world) => World.AddLayer(world);

    public static byte Sum(byte world, byte a, int weightA, byte b, int weightB, int shift = 0)
        => World.AddDerived(world, LayerOp.Sum, a, b, weightA, weightB, shift, 0, 0);

    public static byte Sum(byte world, byte a, int weight, int shift = 0)
        => World.AddDerived(world, LayerOp.Sum, a, a, weight, 0, shift, 0, 0);

    public static byte Min(byte world, byte a, byte b)
        => World.AddDerived(world, LayerOp.Min, a, b, 0, 0, 0, 0, 0);

    public static byte Max(byte world, byte a, byte b)
        => World.AddDerived(world, LayerOp.Max, a, b, 0, 0, 0, 0, 0);

    public static byte Mask(byte world, byte a, byte b, short min, short max)
        => World.AddDerived(world, LayerOp.Mask, a, b, 0, 0, 0, min, max);
}
