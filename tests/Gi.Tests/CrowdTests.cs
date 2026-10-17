using Xunit;

namespace Gi.Tests;

public sealed unsafe class CrowdTests
{
    private static class Catalog
    {
        public static readonly byte Box8 = Stamp.Box(8, 8, 60);
        public static readonly byte Box7 = Stamp.Box(7, 7, 60);
        public static readonly byte Box9 = Stamp.Box(9, 9, 55);
        public static readonly byte Box10 = Stamp.Box(10, 10, 60);
        public static readonly byte Box12 = Stamp.Box(12, 12, 60);
        public static readonly byte[] Mixed = BakeMixed();

        private static byte[] BakeMixed()
        {
            var samples = new sbyte[6 * 9];
            for (var i = 0; i < samples.Length; i++) samples[i] = (sbyte)(i * 17 % 40 - 20);
            return
            [
                Stamp.Box(6, 10, 70),
                Stamp.Box(4, 4, -50),
                Stamp.Tent(12, 12, 80),
                Stamp.Bell(14, 14, 90),
                Stamp.New(samples, 6, 9),
                Stamp.Disk(6, 60),
                Stamp.Cone(7, 70),
                Stamp.Dome(5, -80, 120),
            ];
        }
    }

    private static void Drift(float[] xs, float[] ys, int i, int frame)
    {
        xs[i] = (xs[i] * 1.00013f + i * 0.371f + frame * 1.137f) % 996f + 14f;
        ys[i] = (ys[i] * 1.00029f + i * 0.113f + frame * 2.071f) % 996f + 14f;
    }

    private static void MoveAll(byte w, World.SourceId[] ids, float[] xs, float[] ys, int frame)
    {
        for (var i = 0; i < ids.Length; i++)
        {
            Drift(xs, ys, i, frame);
            World.Move(w, ids[i], xs[i], ys[i]);
        }
    }

    private static void StepIncremental(byte w, World.SourceId[] ids, float[] xs, float[] ys, int frame, int batch)
    {
        for (var start = 0; start < ids.Length; start += batch)
        {
            var end = Math.Min(start + batch, ids.Length);
            for (var i = start; i < end; i++)
            {
                Drift(xs, ys, i, frame);
                World.Move(w, ids[i], xs[i], ys[i]);
            }

            World.Process(w);
        }
    }

    private static bool FieldsEqual(byte wa, byte ga, byte la, byte wb, byte gb, byte lb, int cells, short[] scratch)
    {
        var half = scratch.Length / 2;
        fixed (short* p = scratch)
        {
            World.QueryRegion(wa, ga, la, 0, 0, cells, cells, p);
            World.QueryRegion(wb, gb, lb, 0, 0, cells, cells, p + half);
        }

        for (var i = 0; i < cells * cells; i++)
            if (scratch[i] != scratch[half + i]) return false;
        return true;
    }

    [Fact]
    public void Crowded_boxes_all_moving_match_incremental_on_every_cell()
    {
        const int cells = 1024;
        var w = World.New();
        var g = Grid.New(w, 10, 0f, 0f, 1024f);
        var l = Layer.New(w);
        var box = Catalog.Box8;
        var rng = new Random(7);
        const int count = 20_000;
        var crowdedIds = new World.SourceId[count];
        var steppedIds = new World.SourceId[count];
        var crowdedXs = new float[count];
        var crowdedYs = new float[count];
        var steppedXs = new float[count];
        var steppedYs = new float[count];
        for (var i = 0; i < count; i++)
        {
            crowdedXs[i] = steppedXs[i] = (float)(rng.NextDouble() * 996 + 14);
            crowdedYs[i] = steppedYs[i] = (float)(rng.NextDouble() * 996 + 14);
        }

        var oracle = World.New();
        var go = Grid.New(oracle, 10, 0f, 0f, 1024f);
        var lo = Layer.New(oracle);
        for (var start = 0; start < count; start += 500)
        {
            var end = Math.Min(start + 500, count);
            for (var i = start; i < end; i++)
            {
                crowdedIds[i] = World.Place(w, l, crowdedXs[i], crowdedYs[i], box, 8);
                steppedIds[i] = World.Place(oracle, lo, steppedXs[i], steppedYs[i], box, 8);
            }

            World.Process(oracle);
        }

        World.Process(w);
        var scratch = new short[2 * cells * cells];
        Assert.True(FieldsEqual(w, g, l, oracle, go, lo, cells, scratch));

        for (var frame = 0; frame < 6; frame++)
        {
            MoveAll(w, crowdedIds, crowdedXs, crowdedYs, frame);
            World.Process(w);
            StepIncremental(oracle, steppedIds, steppedXs, steppedYs, frame, 500);
            Assert.True(FieldsEqual(w, g, l, oracle, go, lo, cells, scratch));
        }
    }

    [Fact]
    public void Crowded_all_stamp_kinds_turns_and_scales_match_incremental()
    {
        const int cells = 512;
        var w = World.New();
        var g = Grid.New(w, 9, 0f, 0f, 512f);
        var coarse = Grid.New(w, 7, 0f, 0f, 512f);
        var l = Layer.New(w);
        var other = Layer.New(w);
        var stamps = Catalog.Mixed;

        var oracle = World.New();
        var go = Grid.New(oracle, 9, 0f, 0f, 512f);
        var gc = Grid.New(oracle, 7, 0f, 0f, 512f);
        var lo = Layer.New(oracle);
        var otherO = Layer.New(oracle);
        const int count = 400;
        var rng = new Random(31);
        var crowdedIds = new World.SourceId[count];
        var steppedIds = new World.SourceId[count];
        var crowdedXs = new float[count];
        var crowdedYs = new float[count];
        var steppedXs = new float[count];
        var steppedYs = new float[count];
        for (var i = 0; i < count; i++)
        {
            var stamp = stamps[rng.Next(stamps.Length)];
            var gain = (sbyte)rng.Next(-16, 17);
            var angle = rng.Next(65536) * (360f / 65536f);
            var scale = (200 + rng.Next(700)) / 256f;
            crowdedXs[i] = steppedXs[i] = (float)(rng.NextDouble() * 480 + 16);
            crowdedYs[i] = steppedYs[i] = (float)(rng.NextDouble() * 480 + 16);
            crowdedIds[i] = World.Place(w, l, crowdedXs[i], crowdedYs[i], stamp, gain);
            steppedIds[i] = World.Place(oracle, lo, steppedXs[i], steppedYs[i], stamp, gain);
            World.Turn(w, crowdedIds[i], angle);
            World.Turn(oracle, steppedIds[i], angle);
            World.Scale(w, crowdedIds[i], scale);
            World.Scale(oracle, steppedIds[i], scale);
            if ((i & 3) == 0)
            {
                World.Place(w, other, crowdedXs[i] + 3f, crowdedYs[i] - 2f, stamp, 3);
                World.Place(oracle, otherO, steppedXs[i] + 3f, steppedYs[i] - 2f, stamp, 3);
            }

            if ((i % 20) == 19) World.Process(oracle);
        }

        World.Process(w);
        var scratch = new short[2 * cells * cells];
        Assert.True(FieldsEqual(w, g, l, oracle, go, lo, cells, scratch));
        Assert.True(FieldsEqual(w, g, other, oracle, go, otherO, cells, scratch));

        for (var frame = 0; frame < 6; frame++)
        {
            MoveAll(w, crowdedIds, crowdedXs, crowdedYs, frame);
            World.Process(w);
            StepIncremental(oracle, steppedIds, steppedXs, steppedYs, frame, 20);
            Assert.True(FieldsEqual(w, g, l, oracle, go, lo, cells, scratch));
            Assert.Equal(World.Query(w, g, l, 0, 0, cells, cells), World.Query(oracle, go, lo, 0, 0, cells, cells));
            Assert.Equal(World.Query(w, coarse, l, 0, 0, 128, 128), World.Query(oracle, gc, lo, 0, 0, 128, 128));
            Assert.Equal(World.QueryMax(w, g, l, out _, out _), World.QueryMax(oracle, go, lo, out _, out _));

            for (var i = 0; i < count; i++)
            {
                if ((i & 7) != 0) continue;
                World.Remove(w, crowdedIds[i]);
                World.Remove(oracle, steppedIds[i]);
            }

            World.Process(w);
            World.Process(oracle);
            Assert.True(FieldsEqual(w, g, l, oracle, go, lo, cells, scratch));
        }
    }

    [Fact]
    public void Crowded_frames_that_change_nothing_report_no_changed_tiles()
    {
        var w = World.New();
        var g = Grid.New(w, 8, 0f, 0f, 256f);
        var l = Layer.New(w);
        var box = Catalog.Box9;
        var rng = new Random(41);
        const int count = 200;
        var ids = new World.SourceId[count];
        var xs = new float[count];
        var ys = new float[count];
        for (var i = 0; i < count; i++)
        {
            xs[i] = (float)(rng.NextDouble() * 240 + 8);
            ys[i] = (float)(rng.NextDouble() * 240 + 8);
            ids[i] = World.Place(w, l, xs[i], ys[i], box, 6);
        }

        World.Process(w);
        var initial = World.ChangedTiles(w, g, l, null);
        Assert.True(initial > 0);

        for (var i = 0; i < count; i++)
        {
            var leadX = (int)MathF.Floor(xs[i] * 256f);
            var leadY = (int)MathF.Floor(ys[i] * 256f);
            World.Move(w, ids[i], (leadX + 0.5f) / 256f, (leadY + 0.5f) / 256f);
        }

        World.Process(w);
        var idle = World.Tick(w);
        Assert.Equal(0, World.ChangedTiles(w, g, l, null));
        Assert.Equal(initial, World.ChangedTiles(w, g, l, 0, null));

        World.Move(w, ids[0], 128f, 128f);
        World.Process(w);
        var changed = World.ChangedTiles(w, g, l, null);
        Assert.True(changed > 0 && changed <= 8, $"changed={changed}");
        Assert.Equal(changed, World.ChangedTiles(w, g, l, idle, null));
    }

    [Fact]
    public void Crowded_removals_clear_dissipated_tiles()
    {
        const int cells = 256;
        var w = World.New();
        var g = Grid.New(w, 8, 0f, 0f, 256f);
        var l = Layer.New(w);
        var box = Catalog.Box7;
        var rng = new Random(53);
        const int count = 300;
        var ids = new World.SourceId[count];
        var xs = new float[count];
        var ys = new float[count];
        for (var i = 0; i < count; i++)
        {
            xs[i] = (float)(rng.NextDouble() * 240 + 8);
            ys[i] = (float)(rng.NextDouble() * 240 + 8);
            ids[i] = World.Place(w, l, xs[i], ys[i], box, 5);
        }

        World.Process(w);

        for (var i = 0; i < count; i += 2) World.Remove(w, ids[i]);
        World.Process(w);

        var oracle = World.New();
        var go = Grid.New(oracle, 8, 0f, 0f, 256f);
        var lo = Layer.New(oracle);
        for (var i = 1; i < count; i += 2) World.Place(oracle, lo, xs[i], ys[i], box, 5);
        World.Process(oracle);

        var scratch = new short[2 * cells * cells];
        Assert.True(FieldsEqual(w, g, l, oracle, go, lo, cells, scratch));

        for (var i = 1; i < count; i += 2) World.Remove(w, ids[i]);
        World.Process(w);
        Assert.Equal(0L, World.Query(w, g, l, 0, 0, cells, cells));
        Assert.Equal((short)0, World.QueryMax(w, g, l, out _, out _));
    }

    [Fact]
    public void Crowded_base_keeps_derived_layers_exact()
    {
        const int cells = 256;
        var w = World.New();
        var g = Grid.New(w, 8, 0f, 0f, 256f);
        var l = Layer.New(w);
        var quiet = Layer.New(w);
        var sum = Layer.Sum(w, l, 2, quiet, 1);
        var max = Layer.Max(w, l, quiet);
        var mask = Layer.Mask(w, l, quiet, 50, 300);
        var box = Catalog.Box10;
        var rng = new Random(59);
        const int count = 250;
        var ids = new World.SourceId[count];
        var xs = new float[count];
        var ys = new float[count];
        for (var i = 0; i < count; i++)
        {
            xs[i] = (float)(rng.NextDouble() * 240 + 8);
            ys[i] = (float)(rng.NextDouble() * 240 + 8);
            ids[i] = World.Place(w, l, xs[i], ys[i], box, 5);
            World.Place(w, quiet, 256f - xs[i], 256f - ys[i], box, 2);
        }

        World.Process(w);

        var oracle = World.New();
        var go = Grid.New(oracle, 8, 0f, 0f, 256f);
        var lo = Layer.New(oracle);
        var qo = Layer.New(oracle);
        var so = Layer.Sum(oracle, lo, 2, qo, 1);
        var mo = Layer.Max(oracle, lo, qo);
        var ko = Layer.Mask(oracle, lo, qo, 50, 300);
        var oracleIds = new World.SourceId[count];
        var oracleXs = new float[count];
        var oracleYs = new float[count];
        for (var i = 0; i < count; i++)
        {
            oracleXs[i] = xs[i];
            oracleYs[i] = ys[i];
            oracleIds[i] = World.Place(oracle, lo, oracleXs[i], oracleYs[i], box, 5);
            World.Place(oracle, qo, 256f - oracleXs[i], 256f - oracleYs[i], box, 2);
        }

        World.Process(oracle);

        var scratch = new short[2 * cells * cells];
        Assert.True(FieldsEqual(w, g, sum, oracle, go, so, cells, scratch));
        Assert.True(FieldsEqual(w, g, max, oracle, go, mo, cells, scratch));
        Assert.True(FieldsEqual(w, g, mask, oracle, go, ko, cells, scratch));

        for (var frame = 0; frame < 5; frame++)
        {
            MoveAll(w, ids, xs, ys, frame);
            World.Process(w);
            StepIncremental(oracle, oracleIds, oracleXs, oracleYs, frame, 20);
            Assert.True(FieldsEqual(w, g, sum, oracle, go, so, cells, scratch));
            Assert.True(FieldsEqual(w, g, max, oracle, go, mo, cells, scratch));
            Assert.True(FieldsEqual(w, g, mask, oracle, go, ko, cells, scratch));
        }
    }

    [Fact]
    public void Crowded_frames_rewind_exactly()
    {
        var w = World.New();
        var g = Grid.New(w, 8, 0f, 0f, 256f);
        var l = Layer.New(w);
        var box = Catalog.Box9;
        var rng = new Random(61);
        const int count = 200;
        var ids = new World.SourceId[count];
        var xs = new float[count];
        var ys = new float[count];
        for (var i = 0; i < count; i++)
        {
            xs[i] = (float)(rng.NextDouble() * 240 + 8);
            ys[i] = (float)(rng.NextDouble() * 240 + 8);
            ids[i] = World.Place(w, l, xs[i], ys[i], box, 5);
        }

        World.Process(w);
        var baseline = World.Query(w, g, l, 0, 0, 256, 256);
        World.Record(w);
        for (var frame = 0; frame < 4; frame++)
        {
            MoveAll(w, ids, xs, ys, frame);
            World.Process(w);
        }

        Assert.NotEqual(baseline, World.Query(w, g, l, 0, 0, 256, 256));
        World.Rewind(w);
        World.Process(w);
        Assert.Equal(baseline, World.Query(w, g, l, 0, 0, 256, 256));

        MoveAll(w, ids, xs, ys, 9);
        World.Process(w);
        Assert.NotEqual(baseline, World.Query(w, g, l, 0, 0, 256, 256));
    }

    [Fact]
    public void Sense_column_matches_try_sense_per_agent()
    {
        var w = World.New();
        Grid.New(w, 6, 0f, 0f, 1024f);
        Grid.New(w, 8, 0f, 0f, 256f);
        Grid.New(w, 8, 128.5f, 64.25f, 256f);
        Grid.New(w, 9, 300f, 300f, 256f);
        var l = Layer.New(w);
        var stamps = new[] { Catalog.Box12, Catalog.Mixed[2], Catalog.Mixed[3] };

        var rng = new Random(71);
        for (var i = 0; i < 700; i++)
            World.Place(w, l, (float)(rng.NextDouble() * 1040 - 8), (float)(rng.NextDouble() * 1040 - 8),
                stamps[rng.Next(stamps.Length)], (sbyte)rng.Next(-16, 17));
        World.Process(w);

        const int count = 10_000;
        var xs = new float[count];
        var ys = new float[count];
        for (var i = 0; i < count; i++)
        {
            xs[i] = (float)(rng.NextDouble() * 1100 - 30);
            ys[i] = (float)(rng.NextDouble() * 1100 - 30);
        }

        var values = new short[count];
        var whole = new ulong[(count + 63) >> 6];
        bool allWhole;
        fixed (float* xp = xs, yp = ys)
        fixed (short* vp = values)
        fixed (ulong* hp = whole)
        {
            allWhole = (World.SenseColumn(w, l, xp, yp, count, vp, hp) == World.ReadStatus.Complete);
        }

        var anyIncomplete = false;
        for (var i = 0; i < count; i++)
        {
            var expected = World.TrySense(w, l, xs[i], ys[i], out var value);
            Assert.Equal(value, values[i]);
            Assert.Equal(expected, (whole[i >> 6] & (1ul << (i & 63))) != 0);
            anyIncomplete |= !expected;
        }

        Assert.Equal(!anyIncomplete, allWhole);
        if ((count & 63) != 0) Assert.Equal(0ul, whole[^1] >> (count & 63));
    }

    [Fact]
    public void Changed_mask_matches_changed_per_agent()
    {
        var w = World.New();
        var l = Layer.New(w);
        var box = Catalog.Box12;
        var rng = new Random(73);
        var ids = new World.SourceId[900];
        for (var i = 0; i < ids.Length; i++)
            ids[i] = World.Place(w, l, (float)(rng.NextDouble() * 256), (float)(rng.NextDouble() * 256), box, 6);
        World.Process(w);
        var since = World.Tick(w);

        for (var i = 0; i < ids.Length; i += 3)
            World.Move(w, ids[i], (float)(rng.NextDouble() * 256), (float)(rng.NextDouble() * 256));
        World.Process(w);

        const int count = 10_000;
        var xs = new float[count];
        var ys = new float[count];
        for (var i = 0; i < count; i++)
        {
            xs[i] = (float)(rng.NextDouble() * 300 - 20);
            ys[i] = (float)(rng.NextDouble() * 300 - 20);
        }

        var words = new ulong[(count + 63) >> 6];
        int hits;
        fixed (float* xp = xs, yp = ys)
        fixed (ulong* wp = words)
        {
            hits = World.ChangedMask(w, l, xp, yp, 24f, since, count, wp);
        }

        var expectedCount = 0;
        for (var i = 0; i < count; i++)
        {
            var changed = World.Changed(w, l, xs[i], ys[i], 24f, since);
            Assert.Equal(changed, (words[i >> 6] & (1ul << (i & 63))) != 0);
            if (changed) expectedCount++;
        }

        Assert.Equal(expectedCount, hits);
        if ((count & 63) != 0) Assert.Equal(0ul, words[^1] >> (count & 63));
    }

    [Fact]
    public void Sync_matches_move_per_source_and_ignores_stale_handles()
    {
        var w = World.New();
        var g = Grid.New(w, 8, 0f, 0f, 256f);
        var l = Layer.New(w);
        var box = Catalog.Box9;
        var oracle = World.New();
        var go = Grid.New(oracle, 8, 0f, 0f, 256f);
        var lo = Layer.New(oracle);
        var rng = new Random(79);
        const int count = 600;
        var ids = new World.SourceId[count];
        for (var i = 0; i < count; i++)
        {
            var x = (float)(rng.NextDouble() * 240 + 8);
            var y = (float)(rng.NextDouble() * 240 + 8);
            ids[i] = World.Place(w, l, x, y, box, 5);
            World.Place(oracle, lo, x, y, box, 5);
        }

        for (var i = 0; i < count; i += 4)
        {
            World.Remove(w, ids[i]);
            World.Remove(oracle, ids[i]);
        }

        var xs = new float[count];
        var ys = new float[count];
        for (var i = 0; i < count; i++)
        {
            xs[i] = (float)(rng.NextDouble() * 240 + 8);
            ys[i] = (float)(rng.NextDouble() * 240 + 8);
        }

        for (var i = 0; i < count; i++)
            if ((i & 3) != 0) World.Move(oracle, ids[i], xs[i], ys[i]);
        fixed (World.SourceId* ip = ids)
        fixed (float* xp = xs, yp = ys)
        {
            World.Sync(w, ip, xp, yp, count);
        }

        World.Process(w);
        World.Process(oracle);
        var scratch = new short[2 * 256 * 256];
        Assert.True(FieldsEqual(w, g, l, oracle, go, lo, 256, scratch));
    }

    [Fact]
    public void Warm_crowded_process_allocates_zero_bytes()
    {
        var w = World.New();
        Grid.New(w, 10, 0f, 0f, 1024f);
        var l = Layer.New(w);
        var box = Catalog.Box8;
        var rng = new Random(83);
        const int count = 20_000;
        var ids = new World.SourceId[count];
        var xs = new float[count];
        var ys = new float[count];
        for (var i = 0; i < count; i++)
        {
            xs[i] = (float)(rng.NextDouble() * 996 + 14);
            ys[i] = (float)(rng.NextDouble() * 996 + 14);
            ids[i] = World.Place(w, l, xs[i], ys[i], box, 8);
        }

        World.Process(w);
        MoveAll(w, ids, xs, ys, 0);
        World.Process(w);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var frame = 1; frame <= 32; frame++)
        {
            MoveAll(w, ids, xs, ys, frame);
            World.Process(w);
        }

        Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void Warm_column_batches_allocate_zero_bytes()
    {
        var w = World.New();
        Grid.New(w, 10, 0f, 0f, 1024f);
        var l = Layer.New(w);
        var box = Catalog.Box12;
        var rng = new Random(89);
        var ids = new World.SourceId[4000];
        for (var i = 0; i < ids.Length; i++)
            ids[i] = World.Place(w, l, (float)(rng.NextDouble() * 1024), (float)(rng.NextDouble() * 1024), box, 6);
        World.Process(w);
        var since = World.Tick(w);
        for (var i = 0; i < ids.Length; i += 4)
            World.Move(w, ids[i], (float)(rng.NextDouble() * 1024), (float)(rng.NextDouble() * 1024));
        World.Process(w);

        const int count = 10_000;
        var xs = new float[count];
        var ys = new float[count];
        for (var i = 0; i < count; i++)
        {
            xs[i] = (float)(rng.NextDouble() * 1100 - 30);
            ys[i] = (float)(rng.NextDouble() * 1100 - 30);
        }

        var values = new short[count];
        var words = new ulong[(count + 63) >> 6];
        long acc = 0;
        var hits = 0;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        fixed (float* xp = xs, yp = ys)
        fixed (short* vp = values)
        fixed (ulong* hp = words)
        {
            for (var round = 0; round < 16; round++)
            {
                World.SenseColumn(w, l, xp, yp, count, vp, null);
                hits += World.ChangedMask(w, l, xp, yp, 24f, since, count, hp);
                for (var i = 0; i < 64; i++) acc += vp[i];
            }
        }

        Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.True(hits > 0);
        Assert.NotEqual(0L, acc);
    }
}
