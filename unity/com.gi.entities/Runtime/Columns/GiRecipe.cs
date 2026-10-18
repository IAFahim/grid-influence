using System;
using GiWorld = Gi.World;

namespace Gi.Entities
{
    public struct GiRecipeStep
    {
        public int Layer;
        public GiLayerKind Kind;
        public int First;
        public int Second;
        public short A;
        public short B;
        public int FirstWeight;
        public int SecondWeight;
        public int Shift;
        public short Low;
        public short High;
        public int Raw;
        public short Value;
        public short Actual;

        public bool Matches => Value == Actual;
    }

    public static class GiRecipe
    {
        public const int MaxLayers = 32;

        public static short Combine(in GiLayerSpec spec, int a, int b, out int raw)
        {
            raw = 0;
            switch (spec.Kind)
            {
                case GiLayerKind.Sum:
                    raw = spec.FirstWeight * a + spec.SecondWeight * b;
                    var shifted = spec.Shift == 0 ? raw : (raw + (1 << (spec.Shift - 1)) + (raw >> 31)) >> spec.Shift;
                    return (short)Math.Clamp(shifted, short.MinValue, short.MaxValue);
                case GiLayerKind.Min:
                    return (short)Math.Min(a, b);
                case GiLayerKind.Max:
                    return (short)Math.Max(a, b);
                default:
                    return b >= spec.MaskLow && b <= spec.MaskHigh ? (short)a : (short)0;
            }
        }

        public static bool IsDerived(GiLayerKind kind)
        {
            return kind != GiLayerKind.Persistent && kind != GiLayerKind.Transient;
        }

        public static int Trace(byte world, ReadOnlySpan<GiLayerSpec> specs, ReadOnlySpan<byte> ids, int layer, float x, float y,
            Span<GiRecipeStep> steps)
        {
            var count = 0;
            var visited = 0u;
            Visit(world, specs, ids, layer, x, y, steps, ref count, ref visited);
            return count;
        }

        private static void Visit(byte world, ReadOnlySpan<GiLayerSpec> specs, ReadOnlySpan<byte> ids, int layer, float x, float y,
            Span<GiRecipeStep> steps, ref int count, ref uint visited)
        {
            if (layer < 0 || layer >= specs.Length || layer >= ids.Length || layer >= MaxLayers) return;

            var spec = specs[layer];
            if (!IsDerived(spec.Kind)) return;

            var bit = 1u << layer;
            if ((visited & bit) != 0u) return;

            visited |= bit;
            Visit(world, specs, ids, spec.First, x, y, steps, ref count, ref visited);
            if (spec.Second != spec.First) Visit(world, specs, ids, spec.Second, x, y, steps, ref count, ref visited);

            if (count >= steps.Length) return;
            if (spec.First >= ids.Length || spec.Second >= ids.Length) return;

            GiWorld.TrySense(world, ids[spec.First], x, y, out var a);
            GiWorld.TrySense(world, ids[spec.Second], x, y, out var b);
            GiWorld.TrySense(world, ids[layer], x, y, out var actual);
            var value = Combine(spec, a, b, out var raw);
            steps[count++] = new GiRecipeStep
            {
                Layer = layer,
                Kind = spec.Kind,
                First = spec.First,
                Second = spec.Second,
                A = a,
                B = b,
                FirstWeight = spec.FirstWeight,
                SecondWeight = spec.SecondWeight,
                Shift = spec.Shift,
                Low = spec.MaskLow,
                High = spec.MaskHigh,
                Raw = raw,
                Value = value,
                Actual = actual,
            };
        }
    }
}
