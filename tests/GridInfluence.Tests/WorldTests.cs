using System.Runtime.InteropServices;
using Xunit;

namespace GridInfluence.Tests;

public sealed unsafe class WorldTests
{
    private struct Data
    {
        public Float2* Pos; public float* Bounds; public byte* Stamps; public byte* Fades;
    }

    private static Data Make(int n, float worldSize, Random rng)
    {
        var d = new Data
        {
            Pos = (Float2*)NativeMemory.AlignedAlloc((nuint)(n * sizeof(Float2)), 64),
            Bounds = (float*)NativeMemory.AlignedAlloc((nuint)(n * sizeof(float)), 64),
            Stamps = (byte*)NativeMemory.Alloc((nuint)n),
            Fades = (byte*)NativeMemory.Alloc((nuint)n),
        };
        for (var i = 0; i < n; i++)
        {
            d.Pos[i] = new Float2((float)(rng.NextDouble() * (worldSize - 36) + 18),
                                  (float)(rng.NextDouble() * (worldSize - 36) + 18));
            d.Bounds[i] = 8f;
            d.Stamps[i] = 1;
            d.Fades[i] = 0;
        }
        return d;
    }

    [Fact]
    public void Cell_MatchesWorldSpaceOracle()
    {
        var w = World.New();
        var g = World.Grid(w, power: 8, x: 0f, y: 0f, size: 256f);
        var l = World.Layer(w);
        var s = Stamps.Box(100);
        var rng = new Random(42);
        var d = Make(200, 256f, rng);
        for (var i = 0; i < 200; i++) d.Stamps[i] = s;
        World.Queue(w, l, d.Pos, d.Bounds, d.Stamps, d.Fades, 200);
        World.Apply(w);
        for (var i = 0; i < 30; i++)
        {
            var px = (int)d.Pos[i].X; var py = (int)d.Pos[i].Y;
            var expected = 0L;
            for (var j = 0; j < 200; j++)
            {
                var mx = (int)d.Pos[j].X; var my = (int)d.Pos[j].Y;
                var mr = (int)d.Bounds[j];
                var dx = Math.Abs(px - mx); var dy = Math.Abs(py - my);
                if (dx <= mr && dy <= mr) expected += 100;
            }
            Assert.Equal((short)Math.Clamp(expected, short.MinValue, short.MaxValue),
                World.Cell(w, g, l, px, py));
        }
    }

    [Fact]
    public void EdgeMarks_BleedIntoNeighbourGrid()
    {
        var w = World.New();
        var g0 = World.Grid(w, power: 8, x: 0f, y: 0f, size: 256f);
        var g1 = World.Grid(w, power: 8, x: 256f, y: 0f, size: 256f);
        var l = World.Layer(w);
        var s = Stamps.Box(100);
        var pos = (Float2*)NativeMemory.AlignedAlloc((nuint)sizeof(Float2), 64);
        var bounds = (float*)NativeMemory.AlignedAlloc((nuint)sizeof(float), 64);
        var stamps = (byte*)NativeMemory.Alloc(1);
        var fades = (byte*)NativeMemory.Alloc(1);
        pos[0] = new Float2(252f, 100f);
        bounds[0] = 8f; stamps[0] = s; fades[0] = 0;
        World.Queue(w, l, pos, bounds, stamps, fades, 1);
        World.Apply(w);
        Assert.Equal(100, World.Cell(w, g0, l, 252, 100));
        Assert.Equal(100, World.Cell(w, g1, l, 0, 100));
        Assert.Equal(100, World.Cell(w, g1, l, 4, 100));
        Assert.Equal(0, World.Cell(w, g1, l, 20, 100));
    }

    [Fact]
    public void DifferentResolutions_ScaleBoundsIndependently()
    {
        var w = World.New();
        var g0 = World.Grid(w, power: 8, x: 0f, y: 0f, size: 256f);
        var g1 = World.Grid(w, power: 6, x: 256f, y: 0f, size: 256f);
        var l = World.Layer(w);
        var s = Stamps.Box(100);
        var pos = (Float2*)NativeMemory.AlignedAlloc((nuint)(2 * sizeof(Float2)), 64);
        var bounds = (float*)NativeMemory.AlignedAlloc((nuint)(2 * sizeof(float)), 64);
        var stamps = (byte*)NativeMemory.Alloc(2);
        var fades = (byte*)NativeMemory.Alloc(2);
        pos[0] = new Float2(128f, 128f); bounds[0] = 8f;
        pos[1] = new Float2(300f, 128f); bounds[1] = 8f;
        stamps[0] = s; stamps[1] = s; fades[0] = 0; fades[1] = 0;
        World.Queue(w, l, pos, bounds, stamps, fades, 2);
        World.Apply(w);
        Assert.Equal(100, World.Cell(w, g0, l, 128, 128));
        Assert.Equal(0, World.Cell(w, g0, l, 128 + 20, 128));
        Assert.Equal(100, World.Cell(w, g1, l, 11, 32));
        Assert.Equal(0, World.Cell(w, g1, l, 11 + 8, 32));
    }

    [Fact]
    public void NegativeWorldPositions_RouteAndConvert()
    {
        var w = World.New();
        var g = World.Grid(w, power: 8, x: -512f, y: -512f, size: 512f);
        var l = World.Layer(w);
        var s = Stamps.Box(100);
        var pos = (Float2*)NativeMemory.AlignedAlloc((nuint)sizeof(Float2), 64);
        var bounds = (float*)NativeMemory.AlignedAlloc((nuint)sizeof(float), 64);
        var stamps = (byte*)NativeMemory.Alloc(1);
        var fades = (byte*)NativeMemory.Alloc(1);
        pos[0] = new Float2(-256f, -256f);
        bounds[0] = 8f; stamps[0] = s; fades[0] = 0;
        World.Queue(w, l, pos, bounds, stamps, fades, 1);
        World.Apply(w);
        Assert.Equal(100, World.Cell(w, g, l, 128, 128));
    }

    [Fact]
    public void FadeStamp_DecaysContributionEachApply()
    {
        var w = World.New();
        var g = World.Grid(w, power: 8, x: 0f, y: 0f, size: 256f);
        var l = World.Layer(w);
        var s = Stamps.Box(100);
        var f = Fade.Stamp(50);
        var pos = (Float2*)NativeMemory.AlignedAlloc((nuint)sizeof(Float2), 64);
        var bounds = (float*)NativeMemory.AlignedAlloc((nuint)sizeof(float), 64);
        var stamps = (byte*)NativeMemory.Alloc(1);
        var fades = (byte*)NativeMemory.Alloc(1);
        pos[0] = new Float2(128f, 128f); bounds[0] = 8f; stamps[0] = s; fades[0] = f;
        World.Queue(w, l, pos, bounds, stamps, fades, 1);
        World.Apply(w);
        var first = World.Cell(w, g, l, 128, 128);
        World.Apply(w);
        var second = World.Cell(w, g, l, 128, 128);
        Assert.Equal(100, first);
        Assert.True(second < first);
    }

    [Fact]
    public void QueuedEntity_MovingPosition_MovesInfluence()
    {
        var w = World.New();
        var g = World.Grid(w, power: 8, x: 0f, y: 0f, size: 256f);
        var l = World.Layer(w);
        var s = Stamps.Box(100);
        var pos = (Float2*)NativeMemory.AlignedAlloc((nuint)sizeof(Float2), 64);
        var bounds = (float*)NativeMemory.AlignedAlloc((nuint)sizeof(float), 64);
        var stamps = (byte*)NativeMemory.Alloc(1);
        var fades = (byte*)NativeMemory.Alloc(1);
        pos[0] = new Float2(50f, 50f); bounds[0] = 8f; stamps[0] = s; fades[0] = 0;
        World.Queue(w, l, pos, bounds, stamps, fades, 1);
        World.Apply(w);
        Assert.Equal(100, World.Cell(w, g, l, 50, 50));
        pos[0] = new Float2(200f, 200f);
        World.Apply(w);
        Assert.Equal(0, World.Cell(w, g, l, 50, 50));
        Assert.Equal(100, World.Cell(w, g, l, 200, 200));
    }
}

public sealed unsafe class WorldParallelTests
{
    [Fact]
    public void SlicedApply_MatchesSerialApply()
    {
        const int n = 3000;
        var pos = (Float2*)NativeMemory.AlignedAlloc((nuint)(n * sizeof(Float2)), 64);
        var bounds = (float*)NativeMemory.AlignedAlloc((nuint)(n * sizeof(float)), 64);
        var stamps = (byte*)NativeMemory.Alloc((nuint)n);
        var fades = (byte*)NativeMemory.Alloc((nuint)n);
        uint rng = 13;
        var stamp = Stamps.Box(100);
        for (var i = 0; i < n; i++)
        {
            rng = rng * 1664525u + 1013904223u;
            pos[i] = new Float2(rng % 2000u + 8f, (rng >> 8) % 220u + 8f);
            bounds[i] = 8f; stamps[i] = stamp; fades[i] = 0;
        }

        var w1 = World.New();
        var g1 = World.Grid(w1, 8, 0f, 0f, 256f);
        for (var i = 1; i < 8; i++) World.Grid(w1, 8, i * 256f, 0f, 256f);
        var l1 = World.Layer(w1);
        World.Queue(w1, l1, pos, bounds, stamps, fades, n);
        World.Apply(w1);

        var w2 = World.New();
        var g2 = World.Grid(w2, 8, 0f, 0f, 256f);
        for (var i = 1; i < 8; i++) World.Grid(w2, 8, i * 256f, 0f, 256f);
        var l2 = World.Layer(w2);
        World.Queue(w2, l2, pos, bounds, stamps, fades, n);
        World.BeginApply(w2);
        var slice = n / 8;
        Parallel.For(0, 8, s => World.ApplySlice(w2, 0, s * slice, slice));

        for (var i = 0; i < 40; i++)
        {
            var gi = (int)(pos[i].X / 256f);
            if (gi >= 8) continue;
            var cx = (int)(pos[i].X - gi * 256f); var cy = (int)pos[i].Y;
            Assert.Equal(World.Cell(w1, (byte)gi, l1, cx, cy), World.Cell(w2, (byte)gi, l2, cx, cy));
        }
    }
}
