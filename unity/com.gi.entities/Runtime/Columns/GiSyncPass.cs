using GiWorld = Gi.World;

namespace Gi.Entities
{
    public static unsafe class GiSyncPass
    {
        public const int Categories = 4;
        public const byte Oriented = 1;
        public const byte Scaled = 2;

        public static int Category(bool oriented, bool scaled)
        {
            return (oriented ? Oriented : 0) | (scaled ? Scaled : 0);
        }

        public static int Words(int count)
        {
            return (count + 63) >> 6;
        }

        public static GiApplyStatus Apply(ref GiWorld.Token token, byte world, GiWorld.SourceId* sources, float* xs, float* ys,
            float* headings, float* scales, ulong* invalid, int* categoryStart, int* categoryCount, int* wordStart)
        {
            var ranges = stackalloc GiWorld.TransformRange[Categories];
            var used = 0;
            for (var k = 0; k < Categories; k++)
            {
                var count = categoryCount[k];
                if (count == 0) continue;

                var start = categoryStart[k];
                var words = Words(count);
                var mask = invalid + wordStart[k];

                ranges[used++] = new GiWorld.TransformRange
                {
                    Sources = sources + start,
                    Xs = xs + start,
                    Ys = ys + start,
                    Headings = (k & Oriented) != 0 ? headings + start : null,
                    Scales = (k & Scaled) != 0 ? scales + start : null,
                    Invalid = mask,
                    Count = count,
                    InvalidWords = words,
                };
            }

            if (used == 0) return GiApplyStatus.Applied;

            var batch = GiWorld.SyncTransforms(token, new System.ReadOnlySpan<GiWorld.TransformRange>(ranges, used));
            if (batch == GiWorld.BatchStatus.StaleRevision)
            {
                token = GiWorld.Capture(world);
                batch = GiWorld.SyncTransforms(token, new System.ReadOnlySpan<GiWorld.TransformRange>(ranges, used));
            }

            if (batch == GiWorld.BatchStatus.Applied) return GiApplyStatus.Applied;
            return batch == GiWorld.BatchStatus.StaleRevision ? GiApplyStatus.Stale : GiApplyStatus.Adapter;
        }

        public static bool Rejected(ulong* invalid, int wordStart, int row)
        {
            return (invalid[wordStart + (row >> 6)] >> (row & 63) & 1ul) != 0ul;
        }

        public static GiFaultCode Reason(float x, float y)
        {
            return float.IsNaN(x) || float.IsNaN(y) || float.IsInfinity(x) || float.IsInfinity(y)
                ? GiFaultCode.NonFinite
                : GiFaultCode.StaleSource;
        }
    }
}
