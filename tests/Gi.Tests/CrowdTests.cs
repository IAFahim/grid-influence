using Xunit;

namespace Gi.Tests;

public sealed unsafe class CrowdTests
{
    private static class Catalog
    {
        public static readonly byte Box8 = Gi.Stamp.Box(8, 8, 60);
        public static readonly byte Box7 = Gi.Stamp.Box(7, 7, 60);
        public static readonly byte Box9 = Gi.Stamp.Box(9, 9, 55);
        public static readonly byte Box10 = Gi.Stamp.Box(10, 10, 60);
        public static readonly byte Box12 = Gi.Stamp.Box(12, 12, 60);
        public static readonly byte[] Mixed = BakeMixed();

        private static byte[] BakeMixed()
        {
            var samples = new sbyte[6 * 9];
            for (var i = 0; i < samples.Length; i++) samples[i] = (sbyte)(i * 17 % 40 - 20);
            return
            [
                Gi.Stamp.Box(6, 10, 70),
                Gi.Stamp.Box(4, 4, -50),
                Gi.Stamp.Tent(12, 12, 80),
                Gi.Stamp.Bell(14, 14, 90),
                Gi.Stamp.New(samples, 6, 9),
                Gi.Stamp.Disk(6, 60),
                Gi.Stamp.Cone(7, 70),
                Gi.Stamp.Dome(5, -80, 120),
            ];
        }
    }

    private static void Drift(float[] xs, float[] ys, int i, int frame)
    {
        xs[i] = (xs[i] * 1.00013f + i * 0.371f + frame * 1.137f) % 996f + 14f;
        ys[i] = (ys[i] * 1.00029f + i * 0.113f + frame * 2.071f) % 996f + 14f;
    }

    private static void MoveAll(byte w, int[] ids, float[] xs, float[] ys, int frame)
    {
        for (var i = 0; i < ids.Length; i++)
        {
            Drift(xs, ys, i, frame);
            Gi.World.Move(w, ids[i], xs[i], ys[i]);
        }
    }

    private static void StepIncremental(byte w, int[] ids, float[] xs, float[] ys, int frame, int batch)
    {
        for (var start = 0; start < ids.Length; start += batch)
        {
            var end = Math.Min(start + batch, ids.Length);
            for (var i = start; i < end; i++)
            {
                Drift(xs, ys, i, frame);
                Gi.World.Move(w, ids[i], xs[i], ys[i]);
            }

            Gi.World.Process(w);
        }
    }

    private static bool FieldsEqual(byte wa, byte ga, byte la, byte wb, byte gb, byte lb, int cells, short[] scratch)
    {
        var half = scratch.Length / 2;
        fixed (short* p = scratch)
        {
            Gi.World.QueryRegion(wa, ga, la, 0, 0, cells, cells, p);
            Gi.World.QueryRegion(wb, gb, lb, 0, 0, cells, cells, p + half);
        }

        for (var i = 0; i < cells * cells; i++)
            if (scratch[i] != scratch[half + i]) return false;
        return true;
    }

    [Fact]
    public void Crowded_boxes_all_moving_match_incremental_on_every_cell()
    {
        const int cells = 1024;
        var w = Gi.World.New();
        var g = Gi.Grid.New(w, 10, 0f, 0f, 1024f);
        var l = Gi.Layer.New(w);
        var box = Catalog.Box8;
        var rng = new Random(7);
        const int count = 20_000;
        var crowdedIds = new int[count];
        var steppedIds = new int[count];
        var crowdedXs = new float[count];
        var crowdedYs = new float[count];
        var steppedXs = new float[count];
        var steppedYs = new float[count];
        for (var i = 0; i < count; i++)
        {
            crowdedXs[i] = steppedXs[i] = (float)(rng.NextDouble() * 996 + 14);
            crowdedYs[i] = steppedYs[i] = (float)(rng.NextDouble() * 996 + 14);
        }

        var oracle = Gi.World.New();
        var go = Gi.Grid.New(oracle, 10, 0f, 0f, 1024f);
        var lo = Gi.Layer.New(oracle);
        for (var start = 0; start < count; start += 500)
        {
            var end = Math.Min(start + 500, count);
            for (var i = start; i < end; i++)
            {
                crowdedIds[i] = Gi.World.Place(w, l, crowdedXs[i], crowdedYs[i], box, 8);
                steppedIds[i] = Gi.World.Place(oracle, lo, steppedXs[i], steppedYs[i], box, 8);
            }

            Gi.World.Process(oracle);
        }

        Gi.World.Process(w);
        var scratch = new short[2 * cells * cells];
        Assert.True(FieldsEqual(w, g, l, oracle, go, lo, cells, scratch));

        for (var frame = 0; frame < 6; frame++)
        {
            MoveAll(w, crowdedIds, crowdedXs, crowdedYs, frame);
            Gi.World.Process(w);
            StepIncremental(oracle, steppedIds, steppedXs, steppedYs, frame, 500);
            Assert.True(FieldsEqual(w, g, l, oracle, go, lo, cells, scratch));
        }
    }

    [Fact]
    public void Crowded_all_stamp_kinds_turns_and_scales_match_incremental()
    {
        const int cells = 512;
        var w = Gi.World.New();
        var g = Gi.Grid.New(w, 9, 0f, 0f, 512f);
        var coarse = Gi.Grid.New(w, 7, 0f, 0f, 512f);
        var l = Gi.Layer.New(w);
        var other = Gi.Layer.New(w);
        var stamps = Catalog.Mixed;

        var oracle = Gi.World.New();
        var go = Gi.Grid.New(oracle, 9, 0f, 0f, 512f);
        var gc = Gi.Grid.New(oracle, 7, 0f, 0f, 512f);
        var lo = Gi.Layer.New(oracle);
        var otherO = Gi.Layer.New(oracle);
        const int count = 400;
        var rng = new Random(31);
        var crowdedIds = new int[count];
        var steppedIds = new int[count];
        var crowdedXs = new float[count];
        var crowdedYs = new float[count];
        var steppedXs = new float[count];
        var steppedYs = new float[count];
        for (var i = 0; i < count; i++)
        {
            var stamp = stamps[rng.Next(stamps.Length)];
            var gain = rng.Next(-16, 17);
            var angle = rng.Next(65536) * (360f / 65536f);
            var scale = (200 + rng.Next(700)) / 256f;
            crowdedXs[i] = steppedXs[i] = (float)(rng.NextDouble() * 480 + 16);
            crowdedYs[i] = steppedYs[i] = (float)(rng.NextDouble() * 480 + 16);
            crowdedIds[i] = Gi.World.Place(w, l, crowdedXs[i], crowdedYs[i], stamp, gain);
            steppedIds[i] = Gi.World.Place(oracle, lo, steppedXs[i], steppedYs[i], stamp, gain);
            Gi.World.Turn(w, crowdedIds[i], angle);
            Gi.World.Turn(oracle, steppedIds[i], angle);
            Gi.World.Scale(w, crowdedIds[i], scale);
            Gi.World.Scale(oracle, steppedIds[i], scale);
            if ((i & 3) == 0)
            {
                Gi.World.Place(w, other, crowdedXs[i] + 3f, crowdedYs[i] - 2f, stamp, 3);
                Gi.World.Place(oracle, otherO, steppedXs[i] + 3f, steppedYs[i] - 2f, stamp, 3);
            }

            if ((i % 20) == 19) Gi.World.Process(oracle);
        }

        Gi.World.Process(w);
        var scratch = new short[2 * cells * cells];
        Assert.True(FieldsEqual(w, g, l, oracle, go, lo, cells, scratch));
        Assert.True(FieldsEqual(w, g, other, oracle, go, otherO, cells, scratch));

        for (var frame = 0; frame < 6; frame++)
        {
            MoveAll(w, crowdedIds, crowdedXs, crowdedYs, frame);
            Gi.World.Process(w);
            StepIncremental(oracle, steppedIds, steppedXs, steppedYs, frame, 20);
            Assert.True(FieldsEqual(w, g, l, oracle, go, lo, cells, scratch));
            Assert.Equal(Gi.World.Query(w, g, l, 0, 0, cells, cells), Gi.World.Query(oracle, go, lo, 0, 0, cells, cells));
            Assert.Equal(Gi.World.Query(w, coarse, l, 0, 0, 128, 128), Gi.World.Query(oracle, gc, lo, 0, 0, 128, 128));
            Assert.Equal(Gi.World.QueryMax(w, g, l, out _, out _), Gi.World.QueryMax(oracle, go, lo, out _, out _));

            for (var i = 0; i < count; i++)
            {
                if ((i & 7) != 0) continue;
                Gi.World.Remove(w, crowdedIds[i]);
                Gi.World.Remove(oracle, steppedIds[i]);
            }

            Gi.World.Process(w);
            Gi.World.Process(oracle);
            Assert.True(FieldsEqual(w, g, l, oracle, go, lo, cells, scratch));
        }
    }

    [Fact]
    public void Crowded_frames_that_change_nothing_report_no_changed_tiles()
    {
        var w = Gi.World.New();
        var g = Gi.Grid.New(w, 8, 0f, 0f, 256f);
        var l = Gi.Layer.New(w);
        var box = Catalog.Box9;
        var rng = new Random(41);
        const int count = 200;
        var ids = new int[count];
        var xs = new float[count];
        var ys = new float[count];
        for (var i = 0; i < count; i++)
        {
            xs[i] = (float)(rng.NextDouble() * 240 + 8);
            ys[i] = (float)(rng.NextDouble() * 240 + 8);
            ids[i] = Gi.World.Place(w, l, xs[i], ys[i], box, 6);
        }

        Gi.World.Process(w);
        var initial = Gi.World.ChangedTiles(w, g, l, null);
        Assert.True(initial > 0);

        for (var i = 0; i < count; i++)
        {
            var leadX = (int)MathF.Floor(xs[i] * 256f);
            var leadY = (int)MathF.Floor(ys[i] * 256f);
            Gi.World.Move(w, ids[i], (leadX + 0.5f) / 256f, (leadY + 0.5f) / 256f);
        }

        Gi.World.Process(w);
        var idle = Gi.World.Tick(w);
        Assert.Equal(0, Gi.World.ChangedTiles(w, g, l, null));
        Assert.Equal(initial, Gi.World.ChangedTiles(w, g, l, 0, null));

        Gi.World.Move(w, ids[0], 128f, 128f);
        Gi.World.Process(w);
        var changed = Gi.World.ChangedTiles(w, g, l, null);
        Assert.True(changed > 0 && changed <= 8, $"changed={changed}");
        Assert.Equal(changed, Gi.World.ChangedTiles(w, g, l, idle, null));
    }

    [Fact]
    public void Crowded_removals_clear_dissipated_tiles()
    {
        const int cells = 256;
        var w = Gi.World.New();
        var g = Gi.Grid.New(w, 8, 0f, 0f, 256f);
        var l = Gi.Layer.New(w);
        var box = Catalog.Box7;
        var rng = new Random(53);
        const int count = 300;
        var ids = new int[count];
        var xs = new float[count];
        var ys = new float[count];
        for (var i = 0; i < count; i++)
        {
            xs[i] = (float)(rng.NextDouble() * 240 + 8);
            ys[i] = (float)(rng.NextDouble() * 240 + 8);
            ids[i] = Gi.World.Place(w, l, xs[i], ys[i], box, 5);
        }

        Gi.World.Process(w);

        for (var i = 0; i < count; i += 2) Gi.World.Remove(w, ids[i]);
        Gi.World.Process(w);

        var oracle = Gi.World.New();
        var go = Gi.Grid.New(oracle, 8, 0f, 0f, 256f);
        var lo = Gi.Layer.New(oracle);
        for (var i = 1; i < count; i += 2) Gi.World.Place(oracle, lo, xs[i], ys[i], box, 5);
        Gi.World.Process(oracle);

        var scratch = new short[2 * cells * cells];
        Assert.True(FieldsEqual(w, g, l, oracle, go, lo, cells, scratch));

        for (var i = 1; i < count; i += 2) Gi.World.Remove(w, ids[i]);
        Gi.World.Process(w);
        Assert.Equal(0L, Gi.World.Query(w, g, l, 0, 0, cells, cells));
        Assert.Equal((short)0, Gi.World.QueryMax(w, g, l, out _, out _));
    }

    [Fact]
    public void Crowded_base_keeps_derived_layers_exact()
    {
        const int cells = 256;
        var w = Gi.World.New();
        var g = Gi.Grid.New(w, 8, 0f, 0f, 256f);
        var l = Gi.Layer.New(w);
        var quiet = Gi.Layer.New(w);
        var sum = Gi.Layer.Sum(w, l, 2, quiet, 1);
        var max = Gi.Layer.Max(w, l, quiet);
        var mask = Gi.Layer.Mask(w, l, quiet, 50, 300);
        var box = Catalog.Box10;
        var rng = new Random(59);
        const int count = 250;
        var ids = new int[count];
        var xs = new float[count];
        var ys = new float[count];
        for (var i = 0; i < count; i++)
        {
            xs[i] = (float)(rng.NextDouble() * 240 + 8);
            ys[i] = (float)(rng.NextDouble() * 240 + 8);
            ids[i] = Gi.World.Place(w, l, xs[i], ys[i], box, 5);
            Gi.World.Place(w, quiet, 256f - xs[i], 256f - ys[i], box, 2);
        }

        Gi.World.Process(w);

        var oracle = Gi.World.New();
        var go = Gi.Grid.New(oracle, 8, 0f, 0f, 256f);
        var lo = Gi.Layer.New(oracle);
        var qo = Gi.Layer.New(oracle);
        var so = Gi.Layer.Sum(oracle, lo, 2, qo, 1);
        var mo = Gi.Layer.Max(oracle, lo, qo);
        var ko = Gi.Layer.Mask(oracle, lo, qo, 50, 300);
        var oracleIds = new int[count];
        var oracleXs = new float[count];
        var oracleYs = new float[count];
        for (var i = 0; i < count; i++)
        {
            oracleXs[i] = xs[i];
            oracleYs[i] = ys[i];
            oracleIds[i] = Gi.World.Place(oracle, lo, oracleXs[i], oracleYs[i], box, 5);
            Gi.World.Place(oracle, qo, 256f - oracleXs[i], 256f - oracleYs[i], box, 2);
        }

        Gi.World.Process(oracle);

        var scratch = new short[2 * cells * cells];
        Assert.True(FieldsEqual(w, g, sum, oracle, go, so, cells, scratch));
        Assert.True(FieldsEqual(w, g, max, oracle, go, mo, cells, scratch));
        Assert.True(FieldsEqual(w, g, mask, oracle, go, ko, cells, scratch));

        for (var frame = 0; frame < 5; frame++)
        {
            MoveAll(w, ids, xs, ys, frame);
            Gi.World.Process(w);
            StepIncremental(oracle, oracleIds, oracleXs, oracleYs, frame, 20);
            Assert.True(FieldsEqual(w, g, sum, oracle, go, so, cells, scratch));
            Assert.True(FieldsEqual(w, g, max, oracle, go, mo, cells, scratch));
            Assert.True(FieldsEqual(w, g, mask, oracle, go, ko, cells, scratch));
        }
    }

    [Fact]
    public void Crowded_frames_rewind_exactly()
    {
        var w = Gi.World.New();
        var g = Gi.Grid.New(w, 8, 0f, 0f, 256f);
        var l = Gi.Layer.New(w);
        var box = Catalog.Box9;
        var rng = new Random(61);
        const int count = 200;
        var ids = new int[count];
        var xs = new float[count];
        var ys = new float[count];
        for (var i = 0; i < count; i++)
        {
            xs[i] = (float)(rng.NextDouble() * 240 + 8);
            ys[i] = (float)(rng.NextDouble() * 240 + 8);
            ids[i] = Gi.World.Place(w, l, xs[i], ys[i], box, 5);
        }

        Gi.World.Process(w);
        var baseline = Gi.World.Query(w, g, l, 0, 0, 256, 256);
        Gi.World.Record(w);
        for (var frame = 0; frame < 4; frame++)
        {
            MoveAll(w, ids, xs, ys, frame);
            Gi.World.Process(w);
        }

        Assert.NotEqual(baseline, Gi.World.Query(w, g, l, 0, 0, 256, 256));
        Gi.World.Rewind(w);
        Gi.World.Process(w);
        Assert.Equal(baseline, Gi.World.Query(w, g, l, 0, 0, 256, 256));

        MoveAll(w, ids, xs, ys, 9);
        Gi.World.Process(w);
        Assert.NotEqual(baseline, Gi.World.Query(w, g, l, 0, 0, 256, 256));
    }

    [Fact]
    public void Sense_column_matches_try_sense_per_agent()
    {
        var w = Gi.World.New();
        Gi.Grid.New(w, 6, 0f, 0f, 1024f);
        Gi.Grid.New(w, 8, 0f, 0f, 256f);
        Gi.Grid.New(w, 8, 128.5f, 64.25f, 256f);
        Gi.Grid.New(w, 9, 300f, 300f, 256f);
        var l = Gi.Layer.New(w);
        var stamps = new byte[] { Catalog.Box12, Catalog.Mixed[2], Catalog.Mixed[3] };

        var rng = new Random(71);
        for (var i = 0; i < 700; i++)
            Gi.World.Place(w, l, (float)(rng.NextDouble() * 1040 - 8), (float)(rng.NextDouble() * 1040 - 8),
                stamps[rng.Next(stamps.Length)], rng.Next(-16, 17));
        Gi.World.Process(w);

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
            allWhole = Gi.World.SenseColumn(w, l, xp, yp, count, vp, hp);
        }

        var anyIncomplete = false;
        for (var i = 0; i < count; i++)
        {
            var expected = Gi.World.TrySense(w, l, xs[i], ys[i], out var value);
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
        var w = Gi.World.New();
        var l = Gi.Layer.New(w);
        var box = Catalog.Box12;
        var rng = new Random(73);
        var ids = new int[900];
        for (var i = 0; i < ids.Length; i++)
            ids[i] = Gi.World.Place(w, l, (float)(rng.NextDouble() * 256), (float)(rng.NextDouble() * 256), box, 6);
        Gi.World.Process(w);
        var since = Gi.World.Tick(w);

        for (var i = 0; i < ids.Length; i += 3)
            Gi.World.Move(w, ids[i], (float)(rng.NextDouble() * 256), (float)(rng.NextDouble() * 256));
        Gi.World.Process(w);

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
            hits = Gi.World.ChangedMask(w, l, xp, yp, 24f, since, count, wp);
        }

        var expectedCount = 0;
        for (var i = 0; i < count; i++)
        {
            var changed = Gi.World.Changed(w, l, xs[i], ys[i], 24f, since);
            Assert.Equal(changed, (words[i >> 6] & (1ul << (i & 63))) != 0);
            if (changed) expectedCount++;
        }

        Assert.Equal(expectedCount, hits);
        if ((count & 63) != 0) Assert.Equal(0ul, words[^1] >> (count & 63));
    }

    [Fact]
    public void Sync_matches_move_per_source_and_ignores_stale_handles()
    {
        var w = Gi.World.New();
        var g = Gi.Grid.New(w, 8, 0f, 0f, 256f);
        var l = Gi.Layer.New(w);
        var box = Catalog.Box9;
        var oracle = Gi.World.New();
        var go = Gi.Grid.New(oracle, 8, 0f, 0f, 256f);
        var lo = Gi.Layer.New(oracle);
        var rng = new Random(79);
        const int count = 600;
        var ids = new int[count];
        for (var i = 0; i < count; i++)
        {
            var x = (float)(rng.NextDouble() * 240 + 8);
            var y = (float)(rng.NextDouble() * 240 + 8);
            ids[i] = Gi.World.Place(w, l, x, y, box, 5);
            Gi.World.Place(oracle, lo, x, y, box, 5);
        }

        for (var i = 0; i < count; i += 4)
        {
            Gi.World.Remove(w, ids[i]);
            Gi.World.Remove(oracle, ids[i]);
        }

        var xs = new float[count];
        var ys = new float[count];
        for (var i = 0; i < count; i++)
        {
            xs[i] = (float)(rng.NextDouble() * 240 + 8);
            ys[i] = (float)(rng.NextDouble() * 240 + 8);
        }

        for (var i = 0; i < count; i++)
            if ((i & 3) != 0) Gi.World.Move(oracle, ids[i], xs[i], ys[i]);
        fixed (int* ip = ids)
        fixed (float* xp = xs, yp = ys)
        {
            Gi.World.Sync(w, ip, xp, yp, count);
        }

        Gi.World.Process(w);
        Gi.World.Process(oracle);
        var scratch = new short[2 * 256 * 256];
        Assert.True(FieldsEqual(w, g, l, oracle, go, lo, 256, scratch));
    }

    [Fact]
    public void Warm_crowded_process_allocates_zero_bytes()
    {
        var w = Gi.World.New();
        Gi.Grid.New(w, 10, 0f, 0f, 1024f);
        var l = Gi.Layer.New(w);
        var box = Catalog.Box8;
        var rng = new Random(83);
        const int count = 20_000;
        var ids = new int[count];
        var xs = new float[count];
        var ys = new float[count];
        for (var i = 0; i < count; i++)
        {
            xs[i] = (float)(rng.NextDouble() * 996 + 14);
            ys[i] = (float)(rng.NextDouble() * 996 + 14);
            ids[i] = Gi.World.Place(w, l, xs[i], ys[i], box, 8);
        }

        Gi.World.Process(w);
        MoveAll(w, ids, xs, ys, 0);
        Gi.World.Process(w);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var frame = 1; frame <= 32; frame++)
        {
            MoveAll(w, ids, xs, ys, frame);
            Gi.World.Process(w);
        }

        Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void Warm_column_batches_allocate_zero_bytes()
    {
        var w = Gi.World.New();
        Gi.Grid.New(w, 10, 0f, 0f, 1024f);
        var l = Gi.Layer.New(w);
        var box = Catalog.Box12;
        var rng = new Random(89);
        var ids = new int[4000];
        for (var i = 0; i < ids.Length; i++)
            ids[i] = Gi.World.Place(w, l, (float)(rng.NextDouble() * 1024), (float)(rng.NextDouble() * 1024), box, 6);
        Gi.World.Process(w);
        var since = Gi.World.Tick(w);
        for (var i = 0; i < ids.Length; i += 4)
            Gi.World.Move(w, ids[i], (float)(rng.NextDouble() * 1024), (float)(rng.NextDouble() * 1024));
        Gi.World.Process(w);

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
                Gi.World.SenseColumn(w, l, xp, yp, count, vp, null);
                hits += Gi.World.ChangedMask(w, l, xp, yp, 24f, since, count, hp);
                for (var i = 0; i < 64; i++) acc += vp[i];
            }
        }

        Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.True(hits > 0);
        Assert.True(acc > long.MinValue);
    }
}
