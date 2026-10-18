using System;
using GiWorld = Gi.World;

namespace Gi.Entities
{
    public struct GiContribution
    {
        public GiWorld.SourceId Source;
        public int Value;
        public float X;
        public float Y;
        public byte Layer;
        public byte Stamp;
        public sbyte Gain;
    }

    public struct GiExplanation
    {
        public bool Ok;
        public short Total;
        public long Sum;
        public int Count;
        public int Contributors;
        public bool Saturated;

        public long Rest => Total - Sum;
    }

    public static class GiExplain
    {
        public const int Clamp = short.MaxValue;

        public static GiExplanation Cell(byte world, byte layer, float x, float y, ReadOnlySpan<GiWorld.SourceId> sources,
            Span<GiContribution> output)
        {
            var result = new GiExplanation { Ok = GiWorld.TrySense(world, layer, x, y, out var total), Total = total };
            if (!result.Ok) return result;

            result.Saturated = Math.Abs((int)total) >= Clamp;
            for (var i = 0; i < sources.Length; i++)
            {
                var id = sources[i];
                if (!GiWorld.TryGetSourceSpec(world, id, out var spec)) continue;

                GiWorld.TrySense(world, layer, x, y, id, out var excluded);
                var value = total - excluded;
                if (value == 0) continue;

                result.Contributors++;
                result.Sum += value;
                Insert(output, ref result.Count, new GiContribution
                {
                    Source = id,
                    Value = value,
                    X = spec.X,
                    Y = spec.Y,
                    Layer = spec.Layer,
                    Stamp = spec.Stamp,
                    Gain = spec.Gain,
                });
            }

            return result;
        }

        public static bool Precedes(in GiContribution left, in GiContribution right)
        {
            var a = Math.Abs(left.Value);
            var b = Math.Abs(right.Value);
            if (a != b) return a > b;

            if (left.Source.Slot != right.Source.Slot) return left.Source.Slot < right.Source.Slot;
            return left.Source.Generation < right.Source.Generation;
        }

        private static void Insert(Span<GiContribution> output, ref int count, in GiContribution candidate)
        {
            if (output.Length == 0) return;

            var at = count;
            while (at > 0 && Precedes(candidate, output[at - 1])) at--;
            if (count == output.Length)
            {
                if (at == count) return;

                count--;
            }

            for (var i = count; i > at; i--) output[i] = output[i - 1];
            output[at] = candidate;
            count++;
        }
    }
}
