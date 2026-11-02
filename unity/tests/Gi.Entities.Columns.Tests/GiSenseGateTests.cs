using System;
using Gi.Entities.Tests.Support;
using Xunit;
using GiWorld = Gi.World;

namespace Gi.Entities.Columns.Tests;

public sealed unsafe class GiSenseGateTests
{
    private static GiGridSpec[] Specs(GiScenarioProfile profile)
    {
        var grids = new GiGridSpec[profile.Grids.Length];
        for (var i = 0; i < grids.Length; i++)
            grids[i] = new GiGridSpec { Power = profile.Grids[i].Power, X = profile.Grids[i].X, Y = profile.Grids[i].Y, Size = profile.Grids[i].Size };
        return grids;
    }

    private static void Probes(GiRandom rng, int rows, out float[] xs, out float[] ys, out float[] reaches)
    {
        xs = new float[rows];
        ys = new float[rows];
        reaches = new float[rows];
        for (var i = 0; i < rows; i++)
        {
            xs[i] = -8 + rng.Next(80) + rng.Next(8) * 0.125f;
            ys[i] = -8 + rng.Next(80) + rng.Next(8) * 0.125f;
            reaches[i] = 4 + rng.Next(20) + rng.Next(4) * 0.25f;
        }
    }

    private static void Bounds(float[] xs, float[] ys, float[] reaches, float constant, int rows,
        out float minX, out float minY, out float maxX, out float maxY)
    {
        fixed (float* x = xs, y = ys)
        {
            fixed (float* r = reaches)
            {
                GiSensePass.GateBounds(rows == 0 ? null : x, rows == 0 ? null : y, reaches == null ? null : r,
                    constant, rows, out minX, out minY, out maxX, out maxY);
            }
        }
    }

    private static bool Closed(byte world, byte layer, GiGridSpec[] specs, byte[] ids, int since,
        float minX, float minY, float maxX, float maxY)
    {
        var scratch = new int[GiSensePass.GateScratchLimit];
        fixed (GiGridSpec* g = specs)
        fixed (byte* id = ids)
        fixed (int* s = scratch)
        {
            return GiSensePass.GateClosed(world, layer, g, id, ids.Length, since, minX, minY, maxX, maxY, s, scratch.Length);
        }
    }

    [Fact]
    public void BoundsCoverEveryProbeDisk()
    {
        var rng = new GiRandom(11);
        for (var trial = 0; trial < 50; trial++)
        {
            var rows = 1 + rng.Next(300);
            Probes(rng, rows, out var xs, out var ys, out var reaches);
            Bounds(xs, ys, reaches, 0f, rows, out var minX, out var minY, out var maxX, out var maxY);
            for (var i = 0; i < rows; i++)
            {
                Assert.True(minX <= xs[i] - reaches[i], $"trial {trial} row {i}");
                Assert.True(minY <= ys[i] - reaches[i], $"trial {trial} row {i}");
                Assert.True(maxX >= xs[i] + reaches[i], $"trial {trial} row {i}");
                Assert.True(maxY >= ys[i] + reaches[i], $"trial {trial} row {i}");
            }
        }
    }

    [Fact]
    public void BoundsWithConstantReachMatchPerRowReach()
    {
        var rng = new GiRandom(12);
        var rows = 200;
        Probes(rng, rows, out var xs, out var ys, out var reaches);
        Bounds(xs, ys, reaches, 0f, rows, out var aMinX, out var aMinY, out var aMaxX, out var aMaxY);
        Bounds(xs, ys, null, 24f, rows, out var bMinX, out var bMinY, out var bMaxX, out var bMaxY);
        for (var i = 0; i < rows; i++)
        {
            Assert.True(bMinX <= xs[i] - reaches[i] && bMaxX >= xs[i] + reaches[i]);
            Assert.True(bMinY <= ys[i] - reaches[i] && bMaxY >= ys[i] + reaches[i]);
        }

        Assert.True(aMinX >= bMinX && aMaxX <= bMaxX);
    }

    [Fact]
    public void AStampUnderTheProbeKeepsTheGateOpenAndAFarProbeClosesIt()
    {
        var profile = GiScenarioProfile.Standard();
        var specs = Specs(profile);
        using var setup = GiScenarioWorld.Create(profile);
        var layer = setup.Layers[0];
        GiWorld.Place(setup.World, layer, 10f, 10f, setup.Stamps[0], 8);
        GiWorld.Process(setup.World);
        var tick = GiWorld.Tick(setup.World);

        Assert.True(Closed(setup.World, layer, specs, setup.Grids, tick - 1, 9f, 9f, 11f, 11f) == false);
        Assert.True(Closed(setup.World, layer, specs, setup.Grids, tick - 1, 50f, 50f, 52f, 52f));
    }

    [Fact]
    public void TheGateClosesOnlyWhenNoProbeDiskChanged()
    {
        var kinds = new[] { 0, 1, 2, 3, 4, 5, 6 };
        for (ulong seed = 1; seed <= 25; seed++)
        {
            var profile = GiScenarioProfile.Standard();
            var specs = Specs(profile);
            using var setup = GiScenarioWorld.Create(profile);
            var rng = new GiRandom(seed);
            var placed = new GiWorld.SourceId[40];
            for (var i = 0; i < placed.Length; i++)
                placed[i] = GiWorld.Place(setup.World, setup.Layers[i % 2], 6 + rng.Next(52), 6 + rng.Next(52),
                    setup.Stamps[rng.Next(6)], (sbyte)(1 + rng.Next(10)));
            GiWorld.Process(setup.World);

            var rows = 120;
            Probes(rng, rows, out var xs, out var ys, out var reaches);
            var prevTick = GiWorld.Tick(setup.World);
            var layers = new[] { setup.Layers[0], setup.Layers[1], setup.Layers[3], setup.Layers[4] };

            for (var step = 0; step < 12; step++)
            {
                if (step % 3 == 2)
                {
                    var victim = rng.Next(placed.Length);
                    if (placed[victim].Generation != 0 && rng.Next(2) == 0) GiWorld.Remove(setup.World, placed[victim]);
                    else
                    {
                        placed[victim] = GiWorld.Place(setup.World, setup.Layers[victim % 2], 6 + rng.Next(52),
                            6 + rng.Next(52), setup.Stamps[rng.Next(6)], (sbyte)(1 + rng.Next(10)));
                    }

                    GiWorld.SetGain(setup.World, placed[rng.Next(placed.Length)], (sbyte)(1 + rng.Next(10)));
                }

                GiWorld.Process(setup.World);
                var tick = GiWorld.Tick(setup.World);
                Bounds(xs, ys, reaches, 4f, rows, out var minX, out var minY, out var maxX, out var maxY);

                foreach (var kind in kinds)
                {
                    var layer = layers[kind % layers.Length];
                    var since = kind == 6 ? prevTick - 3 : prevTick;
                    var closed = Closed(setup.World, layer, specs, setup.Grids, since, minX, minY, maxX, maxY);
                    if (!closed) continue;

                    for (var i = 0; i < rows; i++)
                    {
                        var reach = kind is 2 or 3 or 4 or 6 ? reaches[i] : 0f;
                        if (kind == 6) reach = 4f;
                        Assert.False(GiWorld.Changed(setup.World, layer, xs[i], ys[i], reach, since),
                            $"seed {seed} step {step} kind {kind} row {i}");
                    }
                }

                if (step % 3 != 2) prevTick = tick;
            }
        }
    }

    [Fact]
    public void SkippedReadsEqualFreshReadsOnEveryKind()
    {
        for (ulong seed = 101; seed <= 112; seed++)
        {
            var profile = GiScenarioProfile.Standard();
            var specs = Specs(profile);
            using var setup = GiScenarioWorld.Create(profile);
            var rng = new GiRandom(seed);
            for (var i = 0; i < 30; i++)
                GiWorld.Place(setup.World, setup.Layers[i % 2], 6 + rng.Next(52) + rng.Next(4) * 0.25f, 6 + rng.Next(52),
                    setup.Stamps[rng.Next(6)], (sbyte)(1 + rng.Next(12)));
            GiWorld.Process(setup.World);

            var rows = 150;
            Probes(rng, rows, out var xs, out var ys, out var reaches);
            var thresholds = new short[rows];
            for (var i = 0; i < rows; i++) thresholds[i] = (short)(1 + rng.Next(6));
            var prevTick = GiWorld.Tick(setup.World);
            var batch = 192;

            var layers = new[] { setup.Layers[0], setup.Layers[3], setup.Layers[4], setup.Layers[1] };
            var kinds = new[] { GiBindingKind.Value, GiBindingKind.Gradient, GiBindingKind.ValueGradient, GiBindingKind.Area, GiBindingKind.Max, GiBindingKind.Nearest, GiBindingKind.Changed };
            var kept = new Stored[kinds.Length];
            for (var k = 0; k < kinds.Length; k++) kept[k] = new Stored(outputs: rows);

            for (var step = 0; step < 8; step++)
            {
                if (step == 4)
                {
                    GiWorld.Place(setup.World, setup.Layers[0], 58f, 58f, setup.Stamps[0], 10);
                    GiWorld.Process(setup.World);
                }
                else if (step != 0)
                {
                    GiWorld.Process(setup.World);
                }

                var tick = GiWorld.Tick(setup.World);
                Bounds(xs, ys, reaches, 4f, rows, out var minX, out var minY, out var maxX, out var maxY);
                for (var k = 0; k < kinds.Length; k++)
                {
                    var layer = layers[k % layers.Length];
                    var since = kinds[k] == GiBindingKind.Changed ? prevTick - 3 : prevTick;
                    var closed = step == 0
                        ? false
                        : Closed(setup.World, layer, specs, setup.Grids, since, minX, minY, maxX, maxY);
                    kept[k].Read(setup.World, layer, kinds[k], xs, ys, reaches, thresholds, batch);
                    if ((step == 1 || step == 2 || step == 3 || step == 6 || step == 7) &&
                        kinds[k] != GiBindingKind.Area && kinds[k] != GiBindingKind.Max &&
                        kinds[k] != GiBindingKind.Changed)
                        Assert.True(closed, $"seed {seed} step {step} kind {kinds[k]} should close");
                    if (closed) kept[k].AssertUnchanged($"seed {seed} step {step} kind {kinds[k]} layer {layer}");
                    kept[k].Commit();
                }

                if (step != 4) prevTick = tick;
            }
        }
    }

    [Fact]
    public void TheGateAllocatesNothingWarm()
    {
        var profile = GiScenarioProfile.Standard();
        var specs = Specs(profile);
        using var setup = GiScenarioWorld.Create(profile);
        GiWorld.Place(setup.World, setup.Layers[0], 10f, 10f, setup.Stamps[0], 8);
        GiWorld.Process(setup.World);
        var tick = GiWorld.Tick(setup.World);
        var xs = new float[200];
        var ys = new float[200];
        for (var i = 0; i < 200; i++)
        {
            xs[i] = 4 + i * 0.25f;
            ys[i] = 4 + i * 0.125f;
        }

        var scratch = new int[GiSensePass.GateScratchLimit];
        fixed (float* x = xs, y = ys)
        fixed (GiGridSpec* g = specs)
        fixed (byte* id = setup.Grids)
        fixed (int* s = scratch)
        {
            for (var i = 0; i < 10; i++)
            {
                GiSensePass.GateBounds(x, y, null, 0f, 200, out _, out _, out _, out _);
                GiSensePass.GateClosed(setup.World, setup.Layers[0], g, id, setup.Grids.Length, tick - 1, 4f, 4f, 54f, 29f, s, scratch.Length);
            }

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 100; i++)
            {
                GiSensePass.GateBounds(x, y, null, 0f, 200, out var minX, out var minY, out var maxX, out var maxY);
                GiSensePass.GateClosed(setup.World, setup.Layers[0], g, id, setup.Grids.Length, tick - 1, minX, minY, maxX, maxY, s, scratch.Length);
            }

            Assert.Equal(0, (long)(GC.GetAllocatedBytesForCurrentThread() - before));
        }
    }

    private sealed unsafe class Stored
    {
        private readonly short[] _values;
        private readonly float[] _gx;
        private readonly float[] _gy;
        private readonly long[] _totals;
        private readonly ulong[] _whole;
        private readonly short[] _oldValues;
        private readonly float[] _oldGx;
        private readonly float[] _oldGy;
        private readonly long[] _oldTotals;
        private readonly ulong[] _oldWhole;

        public Stored(int outputs)
        {
            _values = new short[outputs];
            _gx = new float[outputs];
            _gy = new float[outputs];
            _totals = new long[outputs];
            _whole = new ulong[GiSensePass.Words(outputs)];
            _oldValues = new short[outputs];
            _oldGx = new float[outputs];
            _oldGy = new float[outputs];
            _oldTotals = new long[outputs];
            _oldWhole = new ulong[GiSensePass.Words(outputs)];
        }

        public void Read(byte world, byte layer, GiBindingKind kind, float[] xs, float[] ys, float[] reaches,
            short[] thresholds, int batch)
        {
            var rows = xs.Length;
            Array.Clear(_values, 0, rows);
            Array.Clear(_gx, 0, rows);
            Array.Clear(_gy, 0, rows);
            Array.Clear(_totals, 0, rows);
            Array.Clear(_whole, 0, _whole.Length);
            fixed (float* x = xs, y = ys, r = reaches)
            fixed (short* t = thresholds)
            fixed (short* v = _values)
            fixed (float* gx = _gx, gy = _gy)
            fixed (long* totals = _totals)
            fixed (ulong* whole = _whole)
            {
                var columns = new GiSenseColumns
                {
                    Xs = x,
                    Ys = y,
                    Reaches = r,
                    Thresholds = t,
                    Values = v,
                    VectorXs = gx,
                    VectorYs = gy,
                    Totals = totals,
                    Whole = whole,
                    Rows = rows,
                };
                for (var b = 0; b < GiSensePass.Batches(rows, batch); b++)
                    GiSensePass.ReadColumns(kind, world, layer, columns, 14f, 0, batch, b);
            }
        }

        public void AssertUnchanged(string context)
        {
            Diff(_oldValues, _values, context + " values");
            Diff(_oldGx, _gx, context + " gx");
            Diff(_oldGy, _gy, context + " gy");
            Diff(_oldTotals, _totals, context + " totals");
            Diff(_oldWhole, _whole, context + " whole");
        }

        private static void Diff(short[] old, short[] current, string what)
        {
            for (var i = 0; i < old.Length; i++)
                if (old[i] != current[i]) throw new Xunit.Sdk.XunitException($"{what} row {i}: {old[i]} != {current[i]}");
        }

        private static void Diff(float[] old, float[] current, string what)
        {
            for (var i = 0; i < old.Length; i++)
                if (BitConverter.SingleToInt32Bits(old[i]) != BitConverter.SingleToInt32Bits(current[i]))
                    throw new Xunit.Sdk.XunitException($"{what} row {i}: {old[i]} != {current[i]}");
        }

        private static void Diff(long[] old, long[] current, string what)
        {
            for (var i = 0; i < old.Length; i++)
                if (old[i] != current[i]) throw new Xunit.Sdk.XunitException($"{what} row {i}: {old[i]} != {current[i]}");
        }

        private static void Diff(ulong[] old, ulong[] current, string what)
        {
            for (var i = 0; i < old.Length; i++)
                if (old[i] != current[i]) throw new Xunit.Sdk.XunitException($"{what} row {i}: {old[i]} != {current[i]}");
        }

        public void Commit()
        {
            Array.Copy(_values, _oldValues, _values.Length);
            Array.Copy(_gx, _oldGx, _gx.Length);
            Array.Copy(_gy, _oldGy, _gy.Length);
            Array.Copy(_totals, _oldTotals, _totals.Length);
            Array.Copy(_whole, _oldWhole, _whole.Length);
        }
    }
}
