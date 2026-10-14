namespace Gi;

public static class Layer
{
    public enum Derivation : byte
    {
        Base,
        Sum,
        Min,
        Max,
        Mask,
    }

    public readonly struct Recipe
    {
        public readonly Derivation Op;
        public readonly byte FirstLayer;
        public readonly byte SecondLayer;
        public readonly int FirstWeight;
        public readonly int SecondWeight;
        public readonly int Shift;
        public readonly short MaskLow;
        public readonly short MaskHigh;

        internal Recipe(Derivation op, byte firstLayer, byte secondLayer, int firstWeight, int secondWeight, int shift, short maskLow, short maskHigh)
        {
            Op = op;
            FirstLayer = firstLayer;
            SecondLayer = secondLayer;
            FirstWeight = firstWeight;
            SecondWeight = secondWeight;
            Shift = shift;
            MaskLow = maskLow;
            MaskHigh = maskHigh;
        }
    }

    public static byte New(byte world) => World.AddLayer(world);

    public static int Count(byte world) => World.LayerCount(world);

    public static int CopyIds(byte world, Span<byte> destination)
        => World.CopyLayerIds(world, destination);

    public static unsafe bool TryGetRecipe(byte world, byte layer, out Recipe recipe)
    {
        recipe = default;
        var w = World.GetContext(world);
        if (w == null || layer >= w->LayerCount) return false;

        var r = w->Recipes + layer;
        recipe = new Recipe(
            (Derivation)r->Op, r->A, r->B, r->WeightA, r->WeightB, r->Shift, r->Low, r->High);
        return true;
    }

    public static byte Sum(byte world, byte a, int weightA, byte b, int weightB, int shift = 0)
        => World.AddDerived(world, LayerOp.Sum, a, b, weightA, weightB, shift, 0, 0);

    public static byte Sum(byte world, byte a, int weight, int shift = 0)
    {
        if (!ValidationRules.IsWeight(weight)) throw new ArgumentOutOfRangeException(nameof(weight));
        return World.AddDerived(world, LayerOp.Sum, a, a, weight, 0, shift, 0, 0);
    }

    public static byte Min(byte world, byte a, byte b)
        => World.AddDerived(world, LayerOp.Min, a, b, 0, 0, 0, 0, 0);

    public static byte Max(byte world, byte a, byte b)
        => World.AddDerived(world, LayerOp.Max, a, b, 0, 0, 0, 0, 0);

    public static byte Mask(byte world, byte a, byte b, short min, short max)
    {
        if (min > max) throw new ArgumentOutOfRangeException(nameof(min));
        return World.AddDerived(world, LayerOp.Mask, a, b, 0, 0, 0, min, max);
    }
}
