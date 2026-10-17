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
        var stamps = new List<byte> { Stamp.Box(5, 5, 40), Stamp.Tent(7, 7, -35), Stamp.Disk(5, 50) };
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

            const int count = 5000;
            var xs = new float[count];
            var ys = new float[count];
            var excludes = new World.SourceId[count];
            for (var i = 0; i < count; i++)
            {
                xs[i] = (float)(rng.NextDouble() * 150.0 - 11.0);
                ys[i] = (float)(rng.NextDouble() * 150.0 - 11.0);
                if (i % 7 == 0) xs[i] = rng.Next(-2, 130);
                if (i % 11 == 0) ys[i] = rng.Next(0, 4) == 0 ? 0f : rng.Next(0, 129);
                excludes[i] = i % 5 == 0 ? ids[rng.Next(ids.Length)] : default;
            }

            var words = (count + 63) >> 6;
            foreach (var useExcludes in new[] { false, true })
            {
                var values = new short[count];
                var gx = new float[count];
                var gy = new float[count];
                var whole = new ulong[words];
                Array.Fill(whole, ulong.MaxValue);
                var refValues = new short[count];
                var refGx = new float[count];
                var refGy = new float[count];
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
                    complete = (World.SenseGradientColumn(w, layer, px, py, ex, count, pv, pgx, pgy, pw) == World.ReadStatus.Complete);
                    var valuesComplete = (World.SenseColumn(w, layer, px, py, ex, count, prv, prwv) == World.ReadStatus.Complete);
                    var gradientComplete = (World.SenseGradientColumn(w, layer, px, py, ex, count, prgx, prgy, prwg) == World.ReadStatus.Complete);
                    refComplete = valuesComplete && gradientComplete;
                }

                Assert.Equal(refComplete, complete);
                Assert.Equal(refValues, values);
                Assert.Equal(refGx, gx);
                Assert.Equal(refGy, gy);
                for (var i = 0; i < count; i++)
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
