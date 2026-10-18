using System;
using System.Threading.Tasks;
using Gi.Entities.Tests.Support;
using Xunit;
using GiWorld = Gi.World;

namespace Gi.Entities.Columns.Tests;

public sealed unsafe class NearestFoundTests
{
    private const short ExcludingThreshold = 2000;

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

    private static void Run(GiScenarioWorld setup, byte layer, float[] xs, float[] ys, float[] reaches, short[] thresholds,
        short[] values, float[] nearestXs, float[] nearestYs, ulong[] found)
    {
        var rows = xs.Length;
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
            Parallel.For(0, GiSensePass.Batches(rows, 192), b =>
                GiSensePass.ReadNearestBatch(world, layer, (float*)xa, (float*)ya, (float*)ra, (short*)ta, rows, 192, b,
                    (short*)va, (float*)nxa, (float*)nya, (ulong*)fa));
        }
    }

    [Fact]
    public void FoundEqualsTrySenseNearestOverOneHundredSeeds()
    {
        for (ulong seed = 1; seed <= 100; seed++)
        {
            using var setup = GiScenarioWorld.Create(GiScenarioProfile.Standard());
            Scene(seed, setup, out var rng, out var xs, out var ys);
            var rows = xs.Length;
            var layer = setup.Layers[1];
            var reaches = new float[rows];
            for (var i = 0; i < rows; i++) reaches[i] = 10 + rng.Next(40) + rng.Next(4) * 0.25f;
            var mixed = new short[rows];
            for (var i = 0; i < rows; i++) mixed[i] = (short)rng.Next(9);
            var excluding = new short[rows];
            for (var i = 0; i < rows; i++) excluding[i] = ExcludingThreshold;

            foreach (var thresholds in new[] { mixed, excluding })
            {
                var values = new short[rows];
                var nearestXs = new float[rows];
                var nearestYs = new float[rows];
                var found = new ulong[GiSensePass.Words(rows)];
                Run(setup, layer, xs, ys, reaches, thresholds, values, nearestXs, nearestYs, found);

                var misses = 0;
                for (var i = 0; i < rows; i++)
                {
                    var isFound = (found[i >> 6] >> (i & 63) & 1ul) != 0ul;
                    GiWorld.TrySenseNearest(setup.World, layer, xs[i], ys[i], reaches[i], thresholds[i], out var expected,
                        out var ex, out var ey);
                    Assert.True(isFound == (expected != short.MinValue),
                        $"seed {seed} threshold {thresholds[0]} row {i} ({xs[i]},{ys[i]}) scalar={expected} found={isFound}");
                    Assert.Equal(expected, values[i]);
                    Assert.Equal(BitConverter.SingleToInt32Bits(ex), BitConverter.SingleToInt32Bits(nearestXs[i]));
                    Assert.Equal(BitConverter.SingleToInt32Bits(ey), BitConverter.SingleToInt32Bits(nearestYs[i]));
                    if (!isFound) misses++;
                }

                if (thresholds[0] == ExcludingThreshold) Assert.Equal(rows, misses);
                else Assert.InRange(misses, 1, rows - 1);
            }
        }
    }
}
