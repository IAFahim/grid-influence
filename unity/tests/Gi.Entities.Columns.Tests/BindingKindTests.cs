using System;
using System.Threading.Tasks;
using Gi.Entities.Tests.Support;
using Xunit;
using GiWorld = Gi.World;

namespace Gi.Entities.Columns.Tests;

public sealed unsafe class BindingKindTests
{
    private static void Scene(ulong seed, GiScenarioWorld setup, out GiRandom rng, out float[] xs, out float[] ys)
    {
        rng = new GiRandom(seed);
        for (var i = 0; i < 30; i++)
            GiWorld.Place(setup.World, setup.Layers[i % 2], 6 + rng.Next(52) + rng.Next(4) * 0.25f, 6 + rng.Next(52),
                setup.Stamps[rng.Next(6)], (sbyte)(1 + rng.Next(12)));
        GiWorld.Process(setup.World);

        var rows = 200 + rng.Next(400);
        xs = new float[rows];
        ys = new float[rows];
        for (var i = 0; i < rows; i++)
        {
            xs[i] = -8 + rng.Next(80) + rng.Next(8) * 0.125f;
            ys[i] = -8 + rng.Next(80) + rng.Next(8) * 0.125f;
        }
    }

    private static float[] Reaches(GiRandom rng, int rows)
    {
        var reaches = new float[rows];
        for (var i = 0; i < rows; i++) reaches[i] = 10 + rng.Next(40) + rng.Next(4) * 0.25f;
        return reaches;
    }

    [Fact]
    public void AreaBatchesEqualTrySenseAreaOverTwoHundredSeeds()
    {
        for (ulong seed = 1; seed <= 200; seed++)
        {
            using var setup = GiScenarioWorld.Create(GiScenarioProfile.Standard());
            Scene(seed, setup, out var rng, out var xs, out var ys);
            var rows = xs.Length;
            var layer = setup.Layers[3];
            var reaches = Reaches(rng, rows);

            foreach (var batch in new[] { 64, 192, 4096 })
            {
                var totals = new long[rows];
                var whole = new ulong[GiSensePass.Words(rows)];
                fixed (float* x = xs, y = ys, r = reaches)
                fixed (long* t = totals)
                fixed (ulong* w = whole)
                {
                    var xa = (nint)x;
                    var ya = (nint)y;
                    var ra = (nint)r;
                    var ta = (nint)t;
                    var wa = (nint)w;
                    var world = setup.World;
                    Parallel.For(0, GiSensePass.Batches(rows, batch), b =>
                        GiSensePass.ReadAreaBatch(world, layer, (float*)xa, (float*)ya, (float*)ra, null, rows, batch, b,
                            (long*)ta, (ulong*)wa));
                }

                for (var i = 0; i < rows; i++)
                {
                    var covered = (whole[i >> 6] >> (i & 63) & 1ul) != 0ul;
                    var ok = GiWorld.TrySenseArea(setup.World, layer, xs[i], ys[i], reaches[i], out var expected);
                    Assert.True(ok == covered, $"seed {seed} batch {batch} row {i} ({xs[i]},{ys[i]}) scalar={ok} bit={covered}");
                    Assert.Equal(expected, totals[i]);
                }
            }
        }
    }

    [Fact]
    public void AreaExcludesMatchTrySenseAreaWithAnExcludeOverTwoHundredSeeds()
    {
        for (ulong seed = 1; seed <= 200; seed++)
        {
            using var setup = GiScenarioWorld.Create(GiScenarioProfile.Standard());
            Scene(seed, setup, out var rng, out var xs, out var ys);
            var rows = xs.Length;
            var layer = setup.Layers[3];
            var reaches = Reaches(rng, rows);
            var ids = new GiWorld.SourceId[rows];
            for (var i = 0; i < rows; i++)
                ids[i] = i % 3 == 0
                    ? GiWorld.Place(setup.World, setup.Layers[i % 2], xs[i], ys[i], setup.Stamps[0], 5)
                    : default;

            var totals = new long[rows];
            var whole = new ulong[GiSensePass.Words(rows)];
            fixed (float* x = xs, y = ys, r = reaches)
            fixed (GiWorld.SourceId* e = ids)
            fixed (long* t = totals)
            fixed (ulong* w = whole)
            {
                var xa = (nint)x;
                var ya = (nint)y;
                var ra = (nint)r;
                var ea = (nint)e;
                var ta = (nint)t;
                var wa = (nint)w;
                var world = setup.World;
                for (var b = 0; b < GiSensePass.Batches(rows, 192); b++)
                    GiSensePass.ReadAreaBatch(world, layer, (float*)xa, (float*)ya, (float*)ra, (GiWorld.SourceId*)ea, rows, 192,
                        b, (long*)ta, (ulong*)wa);
            }

            for (var i = 0; i < rows; i++)
            {
                var covered = (whole[i >> 6] >> (i & 63) & 1ul) != 0ul;
                var ok = ids[i].Generation != 0
                    ? GiWorld.TrySenseArea(setup.World, layer, xs[i], ys[i], reaches[i], ids[i], out var expected)
                    : GiWorld.TrySenseArea(setup.World, layer, xs[i], ys[i], reaches[i], out expected);
                Assert.True(ok == covered, $"seed {seed} row {i} ({xs[i]},{ys[i]}) scalar={ok} bit={covered}");
                Assert.Equal(expected, totals[i]);
            }
        }
    }

    [Fact]
    public void MaxBatchesEqualTrySenseMaxOverTwoHundredSeeds()
    {
        for (ulong seed = 1; seed <= 200; seed++)
        {
            using var setup = GiScenarioWorld.Create(GiScenarioProfile.Standard());
            Scene(seed, setup, out var rng, out var xs, out var ys);
            var rows = xs.Length;
            var layer = setup.Layers[4];
            var reaches = Reaches(rng, rows);

            foreach (var batch in new[] { 64, 192, 4096 })
            {
                var values = new short[rows];
                var bestXs = new float[rows];
                var bestYs = new float[rows];
                var whole = new ulong[GiSensePass.Words(rows)];
                fixed (float* x = xs, y = ys, r = reaches, bx = bestXs, by = bestYs)
                fixed (short* v = values)
                fixed (ulong* w = whole)
                {
                    var xa = (nint)x;
                    var ya = (nint)y;
                    var ra = (nint)r;
                    var va = (nint)v;
                    var bxa = (nint)bx;
                    var bya = (nint)by;
                    var wa = (nint)w;
                    var world = setup.World;
                    Parallel.For(0, GiSensePass.Batches(rows, batch), b =>
                        GiSensePass.ReadMaxBatch(world, layer, (float*)xa, (float*)ya, (float*)ra, rows, batch, b,
                            (short*)va, (float*)bxa, (float*)bya, (ulong*)wa));
                }

                for (var i = 0; i < rows; i++)
                {
                    var covered = (whole[i >> 6] >> (i & 63) & 1ul) != 0ul;
                    var ok = GiWorld.TrySenseMax(setup.World, layer, xs[i], ys[i], reaches[i], out var expected, out var ex,
                        out var ey);
                    Assert.True(ok == covered, $"seed {seed} batch {batch} row {i} ({xs[i]},{ys[i]}) scalar={ok} bit={covered}");
                    Assert.Equal(expected, values[i]);
                    Assert.Equal(BitConverter.SingleToInt32Bits(ex), BitConverter.SingleToInt32Bits(bestXs[i]));
                    Assert.Equal(BitConverter.SingleToInt32Bits(ey), BitConverter.SingleToInt32Bits(bestYs[i]));
                }
            }
        }
    }

    [Fact]
    public void NearestBatchesEqualTrySenseNearestOverTwoHundredSeeds()
    {
        for (ulong seed = 1; seed <= 200; seed++)
        {
            using var setup = GiScenarioWorld.Create(GiScenarioProfile.Standard());
            Scene(seed, setup, out var rng, out var xs, out var ys);
            var rows = xs.Length;
            var layer = setup.Layers[1];
            var reaches = Reaches(rng, rows);
            var thresholds = new short[rows];
            for (var i = 0; i < rows; i++) thresholds[i] = (short)rng.Next(9);

            foreach (var batch in new[] { 64, 192, 4096 })
            {
                var values = new short[rows];
                var nearestXs = new float[rows];
                var nearestYs = new float[rows];
                var found = new ulong[GiSensePass.Words(rows)];
                fixed (float* x = xs, y = ys, r = reaches, nx = nearestXs, ny = nearestYs)
                fixed (short* v = values, t = thresholds)
                fixed (ulong* f = found)
                {
                    var xa = (nint)x;
                    var ya = (nint)y;
                    var ra = (nint)r;
                    var va = (nint)v;
                    var ta = (nint)t;
                    var nxa = (nint)nx;
                    var nya = (nint)ny;
                    var fa = (nint)f;
                    var world = setup.World;
                    Parallel.For(0, GiSensePass.Batches(rows, batch), b =>
                        GiSensePass.ReadNearestBatch(world, layer, (float*)xa, (float*)ya, (float*)ra, (short*)ta, rows, batch,
                            b, (short*)va, (float*)nxa, (float*)nya, (ulong*)fa));
                }

                for (var i = 0; i < rows; i++)
                {
                    var isFound = (found[i >> 6] >> (i & 63) & 1ul) != 0ul;
                    GiWorld.TrySenseNearest(setup.World, layer, xs[i], ys[i], reaches[i], thresholds[i], out var expected,
                        out var ex, out var ey);
                    Assert.True(isFound == (expected != short.MinValue),
                        $"seed {seed} batch {batch} row {i} ({xs[i]},{ys[i]}) scalar={expected} found={isFound}");
                    Assert.Equal(expected, values[i]);
                    Assert.Equal(BitConverter.SingleToInt32Bits(ex), BitConverter.SingleToInt32Bits(nearestXs[i]));
                    Assert.Equal(BitConverter.SingleToInt32Bits(ey), BitConverter.SingleToInt32Bits(nearestYs[i]));
                }
            }
        }
    }

    [Fact]
    public void ChangedBatchesEqualChangedOverTwoHundredSeeds()
    {
        for (ulong seed = 1; seed <= 200; seed++)
        {
            using var setup = GiScenarioWorld.Create(GiScenarioProfile.Standard());
            Scene(seed, setup, out var rng, out var xs, out var ys);
            var rows = xs.Length;
            var layer = setup.Layers[1];
            var reach = 16 + rng.Next(30);
            var since = rng.Next(3);

            foreach (var batch in new[] { 64, 192, 4096 })
            {
                var words = new ulong[GiSensePass.Words(rows)];
                fixed (float* x = xs, y = ys)
                fixed (ulong* w = words)
                {
                    var xa = (nint)x;
                    var ya = (nint)y;
                    var wa = (nint)w;
                    var world = setup.World;
                    Parallel.For(0, GiSensePass.Batches(rows, batch), b =>
                        GiSensePass.ReadChangedBatch(world, layer, (float*)xa, (float*)ya, reach, since, rows, batch, b,
                            (ulong*)wa));
                }

                for (var i = 0; i < rows; i++)
                {
                    var woke = (words[i >> 6] >> (i & 63) & 1ul) != 0ul;
                    var expected = GiWorld.Changed(setup.World, layer, xs[i], ys[i], reach, since);
                    Assert.True(woke == expected, $"seed {seed} batch {batch} row {i} ({xs[i]},{ys[i]}) scalar={expected} bit={woke}");
                }
            }
        }
    }

    [Fact]
    public void ValueAndGradientExcludesMatchTheScalarCallsOverTwoHundredSeeds()
    {
        for (ulong seed = 1; seed <= 200; seed++)
        {
            using var setup = GiScenarioWorld.Create(GiScenarioProfile.Standard());
            Scene(seed, setup, out _, out var xs, out var ys);
            var rows = xs.Length;
            var layer = setup.Layers[0];
            var ids = new GiWorld.SourceId[rows];
            for (var i = 0; i < rows; i++)
                ids[i] = i % 3 == 0
                    ? GiWorld.Place(setup.World, setup.Layers[0], xs[i], ys[i], setup.Stamps[1], 6)
                    : default;
            GiWorld.Process(setup.World);

            var values = new short[rows];
            var gradientValues = new short[rows];
            var gx = new float[rows];
            var gy = new float[rows];
            var vx = new float[rows];
            var vy = new float[rows];
            var valueWhole = new ulong[GiSensePass.Words(rows)];
            var gradientWhole = new ulong[GiSensePass.Words(rows)];
            var bothWhole = new ulong[GiSensePass.Words(rows)];
            fixed (float* x = xs, y = ys, px = gx, py = gy, qx = vx, qy = vy)
            fixed (short* v = values, g = gradientValues)
            fixed (GiWorld.SourceId* e = ids)
            fixed (ulong* w1 = valueWhole, w2 = gradientWhole, w3 = bothWhole)
            {
                for (var b = 0; b < GiSensePass.Batches(rows, 192); b++)
                {
                    GiSensePass.ReadBatch(GiBindingKind.Value, setup.World, layer, x, y, e, rows, 192, b, v, null, null, w1);
                    GiSensePass.ReadBatch(GiBindingKind.Gradient, setup.World, layer, x, y, e, rows, 192, b, null, px, py, w2);
                    GiSensePass.ReadBatch(GiBindingKind.ValueGradient, setup.World, layer, x, y, e, rows, 192, b, g, qx, qy, w3);
                }
            }

            for (var i = 0; i < rows; i++)
            {
                var excluded = ids[i].Generation != 0;
                var okValue = excluded
                    ? GiWorld.TrySense(setup.World, layer, xs[i], ys[i], ids[i], out var value)
                    : GiWorld.TrySense(setup.World, layer, xs[i], ys[i], out value);
                var okGradient = excluded
                    ? GiWorld.TrySenseGradient(setup.World, layer, xs[i], ys[i], ids[i], out var ex, out var ey)
                    : GiWorld.TrySenseGradient(setup.World, layer, xs[i], ys[i], out ex, out ey);
                Assert.True(okValue == ((valueWhole[i >> 6] >> (i & 63) & 1ul) != 0ul), $"seed {seed} row {i} value coverage");
                Assert.Equal(value, values[i]);
                Assert.True(okGradient == ((gradientWhole[i >> 6] >> (i & 63) & 1ul) != 0ul), $"seed {seed} row {i} gradient coverage");
                Assert.Equal(ex, gx[i]);
                Assert.Equal(ey, gy[i]);
                Assert.Equal(value, gradientValues[i]);
                Assert.Equal(ex, vx[i]);
                Assert.Equal(ey, vy[i]);
                Assert.Equal(gradientWhole[i >> 6] >> (i & 63) & 1ul, bothWhole[i >> 6] >> (i & 63) & 1ul);
            }
        }
    }

    [Fact]
    public void TheDispatcherEqualsTheSeparateBatchCallsForEveryKind()
    {
        var kinds = new[]
        {
            GiBindingKind.Value, GiBindingKind.Gradient, GiBindingKind.ValueGradient, GiBindingKind.Area, GiBindingKind.Max,
            GiBindingKind.Nearest, GiBindingKind.Changed,
        };
        foreach (var kind in kinds)
        {
            for (ulong seed = 1; seed <= 60; seed++)
            {
                using var setup = GiScenarioWorld.Create(GiScenarioProfile.Standard());
                Scene(seed, setup, out var rng, out var xs, out var ys);
                var rows = xs.Length;
                var layer = setup.Layers[kind == GiBindingKind.Area || kind == GiBindingKind.Max ? 3 : 1];
                var reaches = Reaches(rng, rows);
                var thresholds = new short[rows];
                for (var i = 0; i < rows; i++) thresholds[i] = (short)(1 + rng.Next(6));

                var viaDispatcher = new Outputs(rows);
                var viaDirect = new Outputs(rows);
                fixed (float* x = xs, y = ys, r = reaches)
                fixed (short* th = thresholds)
                {
                    Run(kind, setup.World, layer, x, y, r, th, rows, viaDispatcher, true);
                    Run(kind, setup.World, layer, x, y, r, th, rows, viaDirect, false);
                }

                Assert.Equal(viaDirect.Values, viaDispatcher.Values);
                Assert.Equal(viaDirect.VectorXs, viaDispatcher.VectorXs);
                Assert.Equal(viaDirect.VectorYs, viaDispatcher.VectorYs);
                Assert.Equal(viaDirect.Totals, viaDispatcher.Totals);
                Assert.Equal(viaDirect.Whole, viaDispatcher.Whole);
            }
        }
    }

    private sealed class Outputs
    {
        public readonly short[] Values;
        public readonly float[] VectorXs;
        public readonly float[] VectorYs;
        public readonly long[] Totals;
        public readonly ulong[] Whole;

        public Outputs(int rows)
        {
            Values = new short[rows];
            VectorXs = new float[rows];
            VectorYs = new float[rows];
            Totals = new long[rows];
            Whole = new ulong[GiSensePass.Words(rows)];
        }
    }

    private static void Run(GiBindingKind kind, byte world, byte layer, float* x, float* y, float* r, short* th, int rows,
        Outputs outputs, bool dispatcher)
    {
        fixed (short* v = outputs.Values)
        fixed (float* ox = outputs.VectorXs, oy = outputs.VectorYs)
        fixed (long* t = outputs.Totals)
        fixed (ulong* w = outputs.Whole)
        {
            var columns = new GiSenseColumns
            {
                Xs = x, Ys = y, Reaches = r, Thresholds = th, Values = v, VectorXs = ox, VectorYs = oy, Totals = t, Whole = w, Rows = rows,
            };
            for (var b = 0; b < GiSensePass.Batches(rows, 192); b++)
            {
                if (dispatcher)
                {
                    GiSensePass.ReadColumns(kind, world, layer, columns, 17f, 1, 192, b);
                    continue;
                }

                switch (kind)
                {
                    case GiBindingKind.Area:
                        GiSensePass.ReadAreaBatch(world, layer, x, y, r, null, rows, 192, b, t, w);
                        break;
                    case GiBindingKind.Max:
                        GiSensePass.ReadMaxBatch(world, layer, x, y, r, rows, 192, b, v, ox, oy, w);
                        break;
                    case GiBindingKind.Nearest:
                        GiSensePass.ReadNearestBatch(world, layer, x, y, r, th, rows, 192, b, v, ox, oy, w);
                        break;
                    case GiBindingKind.Changed:
                        GiSensePass.ReadChangedBatch(world, layer, x, y, 17f, 1, rows, 192, b, w);
                        break;
                    default:
                        GiSensePass.ReadBatch(kind, world, layer, x, y, rows, 192, b,
                            kind == GiBindingKind.Gradient ? null : v, kind == GiBindingKind.Value ? null : ox,
                            kind == GiBindingKind.Value ? null : oy, w);
                        break;
                }
            }
        }
    }

    [Fact]
    public void NearestWithNothingInReachWritesMinValueAndClearsFound()
    {
        using var setup = GiScenarioWorld.Create(GiScenarioProfile.Standard());
        GiWorld.Place(setup.World, setup.Layers[0], 20, 20, setup.Stamps[0], 8);
        GiWorld.Process(setup.World);

        var xs = new float[] { 2, 2, 2 };
        var ys = new float[] { 2, 4, 6 };
        var reaches = new[] { 1f, 1.5f, 2f };
        var thresholds = new short[] { 1, 1, 1 };
        var values = new short[3];
        var nearestXs = new float[3];
        var nearestYs = new float[3];
        var found = new ulong[GiSensePass.Words(3)];
        fixed (float* x = xs, y = ys, r = reaches, nx = nearestXs, ny = nearestYs)
        fixed (short* v = values, t = thresholds)
        fixed (ulong* f = found)
        {
            GiSensePass.ReadNearest(setup.World, setup.Layers[0], x, y, r, t, 3, v, nx, ny, f);
        }

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(short.MinValue, values[i]);
            Assert.Equal(0ul, found[i >> 6] & (1ul << (i & 63)));
        }
    }
}
