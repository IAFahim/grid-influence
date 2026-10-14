using Xunit;

namespace Gi.Tests;

public sealed class ApiExamples
{
    private static readonly byte Box1 = Stamp.Box(1, 1, 16);
    private static readonly byte Box2 = Stamp.Box(2, 2, 40);
    private static readonly byte Box4 = Stamp.Box(4, 4, 50);
    private static readonly byte Box8 = Stamp.Box(8, 8, 50);
    private static readonly byte Box16 = Stamp.Box(16, 16, 90);
    private static readonly byte Dome6 = Stamp.Dome(6, 40);
    private static readonly byte Bell8 = Stamp.Bell(8, 8, 80);
    private static readonly byte Bell12 = Stamp.Bell(12, 12, 40);

    [Fact]
    public void Worlds_ClearFreeAndLayerBudget()
    {
        var w = World.New();
        var g = Grid.New(w, 8, 0f, 0f, 256f);
        var l = Layer.New(w);
        World.Place(w, l, 128.5f, 64f, Dome6, 8);
        World.Process(w);
        Assert.NotEqual(0, World.Query(w, g, l, 128, 64));

        World.Clear(w);
        Assert.Equal(0, World.Query(w, g, l, 128, 64));
        World.Place(w, l, 10f, 10f, Dome6, 8);
        World.Process(w);
        Assert.NotEqual(0, World.Query(w, g, l, 10, 10));

        Assert.Equal(32, World.MaxLayers);
        Assert.True(World.Free(w));
        Assert.False(World.Free(w));
    }

    [Fact]
    public void Worlds_ProcessAllAndEnumerateIds()
    {
        var before = World.CopyWorldIds(Span<byte>.Empty);

        var first = World.New();
        var firstGrid = Grid.New(first, 7, 0f, 0f, 128f);
        var firstLayer = Layer.New(first);
        World.Place(first, firstLayer, 64.5f, 64.5f, Box4, 4);

        var second = World.New();
        var secondGrid = Grid.New(second, 7, 0f, 0f, 128f);
        var secondLayer = Layer.New(second);
        World.Place(second, secondLayer, 64.5f, 64.5f, Box4, 2);

        var ids = new byte[256];
        var live = World.CopyWorldIds(ids);
        Assert.Equal(before + 2, live);
        Assert.True(ids.AsSpan(0, live).IndexOf(first) >= 0);
        Assert.True(ids.AsSpan(0, live).IndexOf(second) >= 0);
        Assert.Equal(live, World.CopyWorldIds(Span<byte>.Empty));

        World.ProcessAll();
        Assert.Equal(4 * 50, World.Query(first, firstGrid, firstLayer, 64, 64));
        Assert.Equal(2 * 50, World.Query(second, secondGrid, secondLayer, 64, 64));
        Assert.Equal(World.Tick(first), World.Tick(second));

        Assert.True(World.Free(first));
        Assert.True(World.Free(second));
        Assert.Equal(before, World.CopyWorldIds(Span<byte>.Empty));
    }

    [Fact]
    public void Worlds_RevisionTokenMovesOnClearRewindFreeAndReuse()
    {
        Assert.Equal(0, World.Revision(byte.MaxValue));
        var w = World.New();
        Grid.New(w, 6, 0f, 0f, 64f);
        var l = Layer.New(w);
        var revision = World.Revision(w);

        var source = World.Place(w, l, 10.5f, 10.5f, Box1, 4);
        World.Process(w);
        World.Move(w, source, 20.5f, 20.5f);
        Assert.Equal(revision, World.Revision(w));

        World.Clear(w);
        Assert.NotEqual(revision, World.Revision(w));

        var cleared = World.Revision(w);
        World.Record(w);
        World.Place(w, l, 20.5f, 20.5f, Box1, 4);
        World.Process(w);
        World.Rewind(w);
        World.Process(w);
        Assert.NotEqual(cleared, World.Revision(w));

        var rewound = World.Revision(w);
        Assert.True(World.Free(w));
        Assert.NotEqual(rewound, World.Revision(w));

        var freed = World.Revision(w);
        var reused = World.New();
        if (reused == w) Assert.NotEqual(freed, World.Revision(reused));
        Assert.True(World.Free(reused));
    }

    [Fact]
    public void Grids_EnumerateIdsAndInspectSpecs()
    {
        var w = World.New();
        Assert.Equal(0, Grid.Count(w));
        Assert.Equal(0, Grid.CopyIds(w, Span<byte>.Empty));

        var steering = Grid.New(w, 9, 0f, 0f, 512f);
        var minimap = Grid.New(w, 7, 0f, 0f, 512f);
        var region = Grid.New(w, 8, 256f, 256f, 128f);

        Assert.Equal(3, Grid.Count(w));
        var ids = new byte[4];
        Assert.Equal(3, Grid.CopyIds(w, ids));
        Assert.Equal(new[] { steering, minimap, region }, ids.AsSpan(0, 3).ToArray());
        Assert.Equal(3, Grid.CopyIds(w, new byte[2]));

        Assert.True(Grid.TryGetSpec(w, region, out var power, out var x, out var y, out var size));
        Assert.Equal(8, power);
        Assert.Equal(256f, x);
        Assert.Equal(256f, y);
        Assert.Equal(128f, size);
        Assert.True(Grid.TryGetSpec(w, minimap, out power, out _, out _, out size));
        Assert.Equal(7, power);
        Assert.Equal(512f, size);

        Assert.False(Grid.TryGetSpec(w, 31, out _, out _, out _, out _));
        Assert.Equal(0, Grid.Count(byte.MaxValue));
        Assert.False(Grid.TryGetSpec(byte.MaxValue, 0, out _, out _, out _, out _));

        Assert.True(World.Free(w));
        Assert.Equal(0, Grid.Count(w));
        Assert.False(Grid.TryGetSpec(w, region, out _, out _, out _, out _));
    }

    [Fact]
    public void Layers_CountAndEnumerateIds()
    {
        var w = World.New();
        Assert.Equal(0, Layer.Count(w));

        var food = Layer.New(w);
        var threat = Layer.New(w);
        var safety = Layer.Sum(w, food, 1, threat, -2);

        Assert.Equal(3, Layer.Count(w));
        var ids = new byte[3];
        Assert.Equal(3, Layer.CopyIds(w, ids));
        Assert.Equal(new[] { food, threat, safety }, ids);
        Assert.Equal(3, Layer.CopyIds(w, Span<byte>.Empty));
        Assert.Equal(0, Layer.Count(byte.MaxValue));
        Assert.Equal(0, Layer.CopyIds(byte.MaxValue, ids));

        Assert.True(World.Free(w));
        Assert.Equal(0, Layer.Count(w));
    }

    [Fact]
    public void Grids_SeveralScalesAndOffsets()
    {
        var w = World.New();
        var steering = Grid.New(w, 9, 0f, 0f, 512f);
        var minimap = Grid.New(w, 7, 0f, 0f, 512f);
        var region = Grid.New(w, 8, 256f, 256f, 128f);
        var l = Layer.New(w);
        World.Place(w, l, 300f, 300f, Box16, 4);
        World.Process(w);

        Assert.NotEqual(0L, World.Query(w, steering, l, 0, 0, 512, 512));
        Assert.Equal(World.Query(w, steering, l, 0, 0, 512, 512), World.Query(w, minimap, l, 0, 0, 128, 128) * 16);
        Assert.NotEqual(0L, World.Query(w, region, l, 0, 0, 128, 128));
    }

    [Fact]
    public void Stamps_AllKindsShareBoxUnits()
    {
        var w = World.New();
        var g = Grid.New(w, 8, 0f, 0f, 256f);
        var l = Layer.New(w);
        var stamps = new[]
        {
            Stamp.Box(16, 16, 90), Stamp.Tent(16, 16, 90), Stamp.Bell(16, 16, 90),
            Stamp.Disk(8, 90), Stamp.Cone(8, 90), Stamp.Dome(8, 90),
        };
        foreach (var stamp in stamps)
        {
            var id = World.Place(w, l, 128.5f, 128.5f, stamp, 2);
            World.Process(w);
            Assert.Equal(180, World.Query(w, g, l, 128, 128));
            World.Remove(w, id);
            World.Process(w);
            Assert.True(Stamp.Free(stamp));
        }

        var samples = new sbyte[6 * 6];
        for (var i = 0; i < samples.Length; i++) samples[i] = 5;
        var raster = Stamp.New(samples, 6, 6);
        var painted = World.Place(w, l, 64.5f, 64.5f, raster, 3);
        World.Process(w);
        Assert.Equal(3 * 5, World.Query(w, g, l, 64, 64));
        World.Remove(w, painted);
        World.Process(w);
        Assert.True(Stamp.Free(raster));
    }

    [Fact]
    public void Layers_AllRecipes()
    {
        var w = World.New();
        var g = Grid.New(w, 7, 0f, 0f, 128f);
        var food = Layer.New(w);
        var threat = Layer.New(w);
        var mine = Layer.New(w);
        var theirs = Layer.New(w);
        World.Place(w, food, 64.5f, 64.5f, Box4, 6);
        World.Place(w, threat, 64.5f, 64.5f, Box4, 2);
        World.Place(w, mine, 64.5f, 64.5f, Box4, 3);
        World.Place(w, theirs, 64.5f, 64.5f, Box4, 5);
        World.Process(w);

        var safety = Layer.Sum(w, food, 1, threat, -2);
        var calm = Layer.Sum(w, threat, -1);
        var both = Layer.Min(w, mine, theirs);
        var either = Layer.Max(w, both, calm);
        var grazing = Layer.Mask(w, food, threat, short.MinValue, 40);
        World.Process(w);

        Assert.Equal(6 * 50 - 2 * 2 * 50, World.Query(w, g, safety, 64, 64));
        Assert.Equal(-2 * 50, World.Query(w, g, calm, 64, 64));
        Assert.Equal(3 * 50, World.Query(w, g, both, 64, 64));
        Assert.Equal(3 * 50, World.Query(w, g, either, 64, 64));
        Assert.Equal(0, World.Query(w, g, grazing, 64, 64));

        var quartered = Layer.Sum(w, safety, 1, shift: 2);
        World.Process(w);
        Assert.Equal((6 * 50 - 2 * 2 * 50) >> 2, World.Query(w, g, quartered, 64, 64));
    }

    [Fact]
    public void Sources_LifecycleAndGainClamp()
    {
        var w = World.New();
        var g = Grid.New(w, 7, 0f, 0f, 128f);
        var l = Layer.New(w);
        var me = World.Place(w, l, 64.5f, 64.5f, Dome6, 8);
        World.Move(w, me, 66.5f, 64.5f);
        World.Turn(w, me, MathF.PI / 4f);
        World.Scale(w, me, 1.5f);
        World.SetGain(w, me, 100);
        World.Process(w);
        Assert.Equal(16 * 40, World.Query(w, g, l, 66, 64));

        World.SetGain(w, me, -4);
        World.Process(w);
        Assert.Equal(-4 * 40, World.Query(w, g, l, 66, 64));

        World.Remove(w, me);
        World.Process(w);
        Assert.Equal(0, World.Query(w, g, l, 66, 64));
    }

    [Fact]
    public void Time_FadeExpireAndTick()
    {
        var w = World.New();
        var g = Grid.New(w, 7, 0f, 0f, 128f);
        var l = Layer.New(w);
        var me = World.Place(w, l, 64.5f, 64.5f, Box1, 16);
        World.Fade(w, me, 0, 8);
        World.Expire(w, me, 8);
        World.Process(w);
        var start = World.Tick(w);
        Assert.True(World.Query(w, g, l, 64, 64) > 0);

        for (var i = 0; i < 8; i++) World.Process(w);
        Assert.Equal(start + 8, World.Tick(w));
        Assert.Equal(0, World.Query(w, g, l, 64, 64));
    }

    [Fact]
    public void Journal_RecordStopAndRewind()
    {
        var w = World.New();
        var g = Grid.New(w, 7, 0f, 0f, 128f);
        var l = Layer.New(w);
        World.Place(w, l, 20f, 20f, Box4, 4);
        World.Process(w);
        var baseline = World.Query(w, g, l, 0, 0, 128, 128);

        World.Record(w);
        World.Place(w, l, 60f, 60f, Box4, 6);
        World.Process(w);
        World.Rewind(w);
        World.Process(w);
        Assert.Equal(baseline, World.Query(w, g, l, 0, 0, 128, 128));

        World.Record(w);
        World.Place(w, l, 90f, 90f, Box4, 6);
        World.Process(w);
        World.StopRecording(w);
        World.Rewind(w);
        World.Process(w);
        Assert.NotEqual(baseline, World.Query(w, g, l, 0, 0, 128, 128));
    }

    [Fact]
    public unsafe void ReadingCells_EveryQuerySurface()
    {
        var w = World.New();
        var g = Grid.New(w, 8, 0f, 0f, 256f);
        var l = Layer.New(w);
        World.Place(w, l, 64f, 32f, Box8, 6);
        World.Place(w, l, 200f, 200f, Box8, 2);
        World.Process(w);

        Assert.Equal(6 * 50, World.Query(w, g, l, 64, 32));
        Assert.Equal(World.Query(w, g, l, 64, 32), World.QueryAt(w, g, l, 64.5f, 32.25f));
        Assert.Equal((6 * 50 + 2 * 50) * 64, World.Query(w, g, l, 0, 0, 256, 256));
        Assert.Equal(6 * 50 * 64, World.Query(w, g, l, 0, 0, 128, 128));
        Assert.Equal(2 * 50 * 64, World.Query(w, g, l, 150, 150, 100, 100));

        var best = World.QueryMax(w, g, l, out var bx, out var by);
        Assert.Equal(6 * 50, best);
        Assert.Equal(6 * 50, World.Query(w, g, l, bx, by));
        var zone = World.QueryMax(w, g, l, 150, 150, 100, 100, out var zx, out var zy);
        Assert.Equal(2 * 50, zone);
        Assert.Equal(2 * 50, World.Query(w, g, l, zx, zy));

        World.QueryGradient(w, g, l, 60.5f, 32.25f, out var gx, out var gy);
        Assert.True(gx > 0);
        Assert.Equal(0, gy);

        var page = new short[256 * 256];
        fixed (short* p = page)
        {
            World.QueryRegion(w, g, l, 0, 0, 256, 256, p);
            Assert.Equal(World.Query(w, g, l, 64, 32), p[32 * 256 + 64]);
        }
    }

    [Fact]
    public unsafe void WhatChanged_ChangedAndBothChangedTilesForms()
    {
        var w = World.New();
        var g = Grid.New(w, 7, 0f, 0f, 128f);
        var l = Layer.New(w);
        World.Place(w, l, 64.5f, 64.5f, Box8, 4);
        World.Process(w);
        var since = World.Tick(w);
        Assert.False(World.Changed(w, l, 64f, 64f, 24f, since));

        World.Place(w, l, 20f, 20f, Box8, 4);
        World.Process(w);
        Assert.True(World.Changed(w, l, 20f, 20f, 24f, since));
        Assert.False(World.Changed(w, l, 100f, 100f, 24f, since));

        var tiles = stackalloc int[64];
        var count = World.ChangedTiles(w, g, l, since, tiles);
        Assert.True(count > 0);
        var frame = World.ChangedTiles(w, g, l, tiles);
        Assert.Equal(count, frame);
    }

    [Fact]
    public void Sense_GridFreeReads()
    {
        var w = World.New();
        Grid.New(w, 8, 0f, 0f, 256f);
        var food = Layer.New(w);
        var threat = Layer.New(w);
        World.Place(w, food, 128.5f, 128.5f, Bell12, 8);
        var myAura = World.Place(w, threat, 128.5f, 128.5f, Bell8, 4);
        World.Process(w);

        Assert.True(World.Covers(w, 10f, 10f));
        Assert.False(World.Covers(w, -10f, 10f));

        Assert.True(World.TrySense(w, threat, 128.5f, 128.5f, out var withMe));
        Assert.True(World.TrySense(w, threat, 128.5f, 128.5f, myAura, out var others));
        Assert.True(withMe > others);
        Assert.Equal(0, others);

        Assert.True(World.TrySenseArea(w, food, 128.5f, 128.5f, 20f, out var nearby));
        Assert.True(nearby > 0);
        Assert.True(World.TrySenseArea(w, food, 128.5f, 128.5f, 20f, myAura, out var sansMe));
        Assert.Equal(nearby, sansMe);

        Assert.True(World.TrySenseMax(w, food, 120f, 120f, 40f, out var best, out var fx, out var fy));
        Assert.Equal(8 * 40, best);
        Assert.Equal(128.5f, fx, 1f);
        Assert.Equal(128.5f, fy, 1f);

        Assert.True(World.TrySenseNearest(w, food, 100f, 100f, 60f, 100, out var meal, out var mx, out var my));
        Assert.True(meal >= 100);
        Assert.True(mx > 100f);
        Assert.True(my > 100f);

        Assert.True(World.TrySenseGradient(w, food, 124f, 124f, out var gx, out var gy));
        Assert.True(gx > 0f);
        Assert.True(gy > 0f);
        Assert.True(World.TrySenseGradient(w, food, 124f, 124f, myAura, out var sx, out var sy));
        Assert.Equal(gx, sx, 0.001f);
        Assert.Equal(gy, sy, 0.001f);
    }

    [Fact]
    public unsafe void Columns_SenseChangedMaskAndSync()
    {
        var w = World.New();
        Grid.New(w, 8, 0f, 0f, 256f);
        var threat = Layer.New(w);
        var ids = new int[128];
        var xs = new float[128];
        var ys = new float[128];
        for (var i = 0; i < ids.Length; i++)
        {
            xs[i] = 8f + i * 1.9f;
            ys[i] = 8f + i * 1.3f;
            ids[i] = World.Place(w, threat, xs[i], ys[i], Box2, 4);
        }

        World.Process(w);
        var since = World.Tick(w);

        var moved = new float[128];
        fixed (float* xsp = xs, ysp = ys)
        fixed (float* mp = moved)
        fixed (int* idp = ids)
        {
            var values = stackalloc short[128];
            var whole = stackalloc ulong[2];
            Assert.True(World.SenseColumn(w, threat, xsp, ysp, 128, values, whole));
            Assert.True(values[0] >= 4 * 40);
            Assert.Equal(1ul << 0, whole[0] & 1ul);

            var words = stackalloc ulong[2];
            for (var i = 0; i < 128; i++) moved[i] = xs[i] + 1f;
            World.Sync(w, idp, mp, ysp, 128);
            World.Process(w);
            var woke = World.ChangedMask(w, threat, xsp, ysp, 24f, since, 128, words);
            Assert.True(woke > 0);
            Assert.Equal(woke, System.Numerics.BitOperations.PopCount(words[0]) + System.Numerics.BitOperations.PopCount(words[1]));
            for (var i = 0; i < 128; i++) World.Move(w, ids[i], xs[i], ys[i]);
            World.Process(w);
        }
    }
}
