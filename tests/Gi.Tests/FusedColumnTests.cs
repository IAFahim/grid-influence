using Xunit;

namespace Gi.Tests;

public unsafe class FusedColumnTests
{
    [Theory]
    [InlineData(51, 1)]
    [InlineData(52, 1)]
    [InlineData(53, 2)]
    [InlineData(54, 2)]
    public void ValueGradientOverloadEqualsTheSeparateColumnCalls(int seed, int grids)
    {
        var rng = new Random(seed);
        var w = World.New();
        var stamps = new List<byte> { Stamp.Box(5, 5, 40), Stamp.Tent(7, 7, -35), Stamp.Disk(5, 50, 360) };
        try
        {
            Grid.New(w, 7, 0f, 0f, 128f);
            if (grids == 2) Grid.New(w, 6, 16f, 16f, 64f);
            var layer = Layer.New(w, Layer.Precision.Exact);
            var ids = new World.SourceId[300];
            for (var i = 0; i < ids.Length; i++)
                ids[i] = World.Place(w, layer, (float)(rng.NextDouble() * 128.0), (float)(rng.NextDouble() * 128.0),
                    stamps[rng.Next(stamps.Count)], (sbyte)rng.Next(-5, 6));
            World.Process(w);

            const int Count = 5000;
            var xs = new float[Count];
            var ys = new float[Count];
            var excludes = new World.SourceId[Count];
            for (var i = 0; i < Count; i++)
            {
                xs[i] = (float)(rng.NextDouble() * 150.0 - 11.0);
                ys[i] = (float)(rng.NextDouble() * 150.0 - 11.0);
                if (i % 7 == 0) xs[i] = (float)rng.Next(-2, 130);
                if (i % 11 == 0) ys[i] = rng.Next(0, 4) == 0 ? 0f : (float)rng.Next(0, 129);
                excludes[i] = i % 5 == 0 ? ids[rng.Next(ids.Length)] : default;
            }

            var words = (Count + 63) >> 6;
            foreach (var useExcludes in new[] { false, true })
            {
                var values = new short[Count];
                var gx = new float[Count];
                var gy = new float[Count];
                var whole = new ulong[words];
                Array.Fill(whole, ulong.MaxValue);
                var refValues = new short[Count];
                var refGx = new float[Count];
                var refGy = new float[Count];
                var refWholeValues = new ulong[words];
                var refWholeGradient = new ulong[words];
                bool complete;
                bool refComplete;
                fixed (float* px = xs)
                fixed (float* py = ys)
                fixed (World.SourceId* pe = excludes)
                fixed (short* pv = values)
                fixed (float* pgx = gx)
                fixed (float* pgy = gy)
                fixed (ulong* pw = whole)
                fixed (short* prv = refValues)
                fixed (float* prgx = refGx)
                fixed (float* prgy = refGy)
                fixed (ulong* prwv = refWholeValues)
                fixed (ulong* prwg = refWholeGradient)
                {
                    var ex = useExcludes ? pe : null;
                    complete = World.SenseGradientColumn(w, layer, px, py, ex, Count, pv, pgx, pgy, pw);
                    var valuesComplete = World.SenseColumn(w, layer, px, py, ex, Count, prv, prwv);
                    var gradientComplete = World.SenseGradientColumn(w, layer, px, py, ex, Count, prgx, prgy, prwg);
                    refComplete = valuesComplete && gradientComplete;
                }

                Assert.Equal(refComplete, complete);
                Assert.Equal(refValues, values);
                Assert.Equal(refGx, gx);
                Assert.Equal(refGy, gy);
                for (var i = 0; i < Count; i++)
                {
                    var bit = (whole[i >> 6] >> (i & 63)) & 1;
                    var expected = ((refWholeValues[i >> 6] & refWholeGradient[i >> 6]) >> (i & 63)) & 1;
                    Assert.Equal(expected, bit);
                }
            }
        }
        finally
        {
            World.Free(w);
            foreach (var s in stamps) Stamp.Free(s);
        }
    }
}
