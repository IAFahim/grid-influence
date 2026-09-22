using Xunit;

namespace GridInfluence.Tests;

public sealed unsafe class EngineTests
{
    private static int RoundQ16(int value)
        => value < 0 ? -((-value + 32768) >> 16) : (value + 32768) >> 16;

    private readonly struct Band
    {
        public readonly int Start, End, W;
        public Band(int s, int e, int w) { Start = s; End = e; W = w; }
    }

    private static Band[] Bands(int length, int phase)
    {
        if (phase == 0) return [new Band(0, length, 256)];
        if (length == 1) return [new Band(0, 1, 256 - phase), new Band(1, 2, phase)];
        return [new Band(0, 1, 256 - phase), new Band(1, length, 256), new Band(length, length + 1, phase)];
    }

    private sealed class Oracle
    {
        public int X, Y, Px, Py, Fx, Fy, Gain;
        public sbyte[]? Samples;
        public int W, H;
        public sbyte Constant;
        public bool Raster;

        public static Oracle Box(float wx, float wy, float ox, float oy, float scale,
            int w, int h, sbyte value, int gain)
        {
            var o = new Oracle { W = w, H = h, Constant = value, Gain = gain };
            o.Position(wx, wy, ox, oy, scale);
            return o;
        }

        public static Oracle Rast(float wx, float wy, float ox, float oy, float scale,
            sbyte[] samples, int w, int h, int gain)
        {
            var o = new Oracle { W = w, H = h, Samples = samples, Gain = gain, Raster = true };
            o.Position(wx, wy, ox, oy, scale);
            return o;
        }

        private void Position(float wx, float wy, float ox, float oy, float scale)
        {
            X = (int)MathF.Floor((wx - ox) * scale * 256f) - (W * 128);
            Y = (int)MathF.Floor((wy - oy) * scale * 256f) - (H * 128);
            Px = X >> 8;
            Py = Y >> 8;
            Fx = X & 255;
            Fy = Y & 255;
        }

        private int Sample(int ix, int iy)
            => (uint)ix < (uint)W && (uint)iy < (uint)H && Samples != null ? Samples[iy * W + ix] : 0;

        public long Contribution(int cx, int cy)
        {
            var sx = cx - Px;
            var sy = cy - Py;
            if (Raster)
            {
                if (sx < 0 || sx > W || sy < 0 || sy > H) return 0;
                var w00 = (256 - Fx) * (256 - Fy);
                var w10 = Fx * (256 - Fy);
                var w01 = (256 - Fx) * Fy;
                var w11 = Fx * Fy;
                return (long)RoundQ16(
                    Sample(sx, sy) * w00 + Sample(sx - 1, sy) * w10 +
                    Sample(sx, sy - 1) * w01 + Sample(sx - 1, sy - 1) * w11) * Gain;
            }

            var xw = 0;
            foreach (var b in Bands(W, Fx))
                if (sx >= b.Start && sx < b.End) { xw = b.W; break; }
            var yw = 0;
            foreach (var b in Bands(H, Fy))
                if (sy >= b.Start && sy < b.End) { yw = b.W; break; }
            if (xw == 0 || yw == 0) return 0;
            return (long)RoundQ16(Constant * xw * yw) * Gain;
        }
    }

    private static short Expected(List<Oracle> sources, int cx, int cy)
    {
        var sum = 0L;
        foreach (var s in sources) sum += s.Contribution(cx, cy);
        return (short)Math.Clamp(sum, short.MinValue, short.MaxValue);
    }

    [Fact]
    public void Box_MatchesOracle()
    {
        var w = World.New();
        var g = Grid.New(w, power: 8, x: 0f, y: 0f, size: 256f);
        var l = Layer.New(w);
        var stamp = Stamp.Box(16, 16, 100);
        var sources = new List<Oracle>();
        var rng = new Random(42);
        for (var i = 0; i < 200; i++)
        {
            var x = (float)(rng.NextDouble() * 240 + 8);
            var y = (float)(rng.NextDouble() * 240 + 8);
            World.Place(w, l, x, y, stamp, 8);
            sources.Add(Oracle.Box(x, y, 0f, 0f, 1f, 16, 16, 100, 8));
        }

        World.Process(w);
        for (var i = 0; i < 400; i++)
        {
            var cx = rng.Next(256);
            var cy = rng.Next(256);
            Assert.Equal(Expected(sources, cx, cy), World.Query(w, g, l, cx, cy));
        }
    }

    [Fact]
    public void Raster_MatchesOracle()
    {
        var w = World.New();
        var g = Grid.New(w, power: 8, x: 0f, y: 0f, size: 256f);
        var l = Layer.New(w);
        var samples = new sbyte[7 * 9];
        for (var i = 0; i < samples.Length; i++) samples[i] = (sbyte)((i * 37 % 21) - 10);
        var stamp = Stamp.New(samples, 7, 9);
        var sources = new List<Oracle>();
        var rng = new Random(7);
        for (var i = 0; i < 60; i++)
        {
            var x = (float)(rng.NextDouble() * 248);
            var y = (float)(rng.NextDouble() * 248);
            var gain = 1 + rng.Next(16);
            World.Place(w, l, x, y, stamp, gain);
            sources.Add(Oracle.Rast(x, y, 0f, 0f, 1f, samples, 7, 9, gain));
        }

        World.Process(w);
        for (var i = 0; i < 400; i++)
        {
            var cx = rng.Next(256);
            var cy = rng.Next(256);
            Assert.Equal(Expected(sources, cx, cy), World.Query(w, g, l, cx, cy));
        }
    }

    [Fact]
    public void MixedStamps_CrossTile_MatchesOracle()
    {
        var w = World.New();
        var g = Grid.New(w, power: 8, x: 0f, y: 0f, size: 256f);
        var l = Layer.New(w);
        var box = Stamp.Box(20, 12, 40);
        var samples = new sbyte[40 * 5];
        for (var i = 0; i < samples.Length; i++) samples[i] = (sbyte)(i % 7 - 3);
        var raster = Stamp.New(samples, 40, 5);
        var sources = new List<Oracle>
        {
            Oracle.Box(31.4f, 30.9f, 0f, 0f, 1f, 20, 12, 40, 16),
            Oracle.Rast(30.7f, 33.2f, 0f, 0f, 1f, samples, 40, 5, 5),
            Oracle.Box(0.2f, 0.4f, 0f, 0f, 1f, 20, 12, 40, 16),
            Oracle.Box(255.8f, 250.1f, 0f, 0f, 1f, 20, 12, 40, 16),
        };
        World.Place(w, l, 31.4f, 30.9f, box, 16);
        World.Place(w, l, 30.7f, 33.2f, raster, 5);
        World.Place(w, l, 0.2f, 0.4f, box, 16);
        World.Place(w, l, 255.8f, 250.1f, box, 16);
        World.Process(w);

        for (var cy = 0; cy < 256; cy += 3)
        for (var cx = 0; cx < 256; cx += 3)
            Assert.Equal(Expected(sources, cx, cy), World.Query(w, g, l, cx, cy));
    }

    [Fact]
    public void MultiGrid_DifferentResolutions_MatchOracles()
    {
        var w = World.New();
        var gFine = Grid.New(w, power: 8, x: 0f, y: 0f, size: 256f);
        var gCoarse = Grid.New(w, power: 5, x: 0f, y: 0f, size: 256f);
        var l = Layer.New(w);
        var stamp = Stamp.Box(8, 8, 90);
        var sources = new List<Oracle>();
        var coarse = new List<Oracle>();
        var rng = new Random(11);
        for (var i = 0; i < 40; i++)
        {
            var x = (float)(rng.NextDouble() * 240 + 8);
            var y = (float)(rng.NextDouble() * 240 + 8);
            World.Place(w, l, x, y, stamp, 10);
            sources.Add(Oracle.Box(x, y, 0f, 0f, 1f, 8, 8, 90, 10));
            coarse.Add(Oracle.Box(x, y, 0f, 0f, 32f / 256f, 8, 8, 90, 10));
        }

        World.Process(w);
        for (var i = 0; i < 300; i++)
        {
            var cx = rng.Next(256);
            var cy = rng.Next(256);
            Assert.Equal(Expected(sources, cx, cy), World.Query(w, gFine, l, cx, cy));
        }
        for (var i = 0; i < 100; i++)
        {
            var cx = rng.Next(32);
            var cy = rng.Next(32);
            Assert.Equal(Expected(coarse, cx, cy), World.Query(w, gCoarse, l, cx, cy));
        }
    }

    [Fact]
    public void Saturation_SticksAtInt16Bounds()
    {
        var w = World.New();
        var g = Grid.New(w, power: 6, x: 0f, y: 0f, size: 64f);
        var l = Layer.New(w);
        var stamp = Stamp.Box(4, 4, 127);
        for (var i = 0; i < 200; i++) World.Place(w, l, 32f, 32f, stamp, 16);
        World.Process(w);
        Assert.Equal(32767, World.Query(w, g, l, 32, 32));

        var neg = Stamp.Box(4, 4, -128);
        for (var i = 0; i < 200; i++) World.Place(w, l, 8f, 8f, neg, 16);
        World.Process(w);
        Assert.Equal(-32768, World.Query(w, g, l, 8, 8));
    }

    [Fact]
    public void Remove_RebuildsWithoutSource()
    {
        var w = World.New();
        var g = Grid.New(w, power: 6, x: 0f, y: 0f, size: 64f);
        var l = Layer.New(w);
        var stamp = Stamp.Box(8, 8, 100);
        var a = World.Place(w, l, 32f, 32f, stamp, 8);
        var b = World.Place(w, l, 38f, 32f, stamp, 8);
        World.Process(w);
        Assert.Equal(1600, World.Query(w, g, l, 34, 32));

        World.Remove(w, a);
        World.Process(w);
        Assert.Equal(800, World.Query(w, g, l, 34, 32));
        Assert.Equal(0, World.Query(w, g, l, 30, 32));

        World.Remove(w, b);
        World.Process(w);
        Assert.Equal(0, World.Query(w, g, l, 34, 32));
        Assert.Equal(0, World.Query(w, g, l, 40, 32));
    }

    [Fact]
    public void SubCellPlacement_SpreadsEdgeWeights()
    {
        var w = World.New();
        var g = Grid.New(w, power: 6, x: 0f, y: 0f, size: 64f);
        var l = Layer.New(w);
        var stamp = Stamp.Box(4, 4, 64);

        World.Place(w, l, 32f, 32f, stamp, 1);
        World.Process(w);
        Assert.Equal(64, World.Query(w, g, l, 31, 32));
        Assert.Equal(0, World.Query(w, g, l, 34, 32));

        var w2 = World.New();
        var g2 = Grid.New(w2, power: 6, x: 0f, y: 0f, size: 64f);
        var l2 = Layer.New(w2);
        World.Place(w2, l2, 32.5f, 32f, stamp, 1);
        World.Process(w2);
        Assert.Equal(32, World.Query(w2, g2, l2, 30, 32));
        Assert.Equal(64, World.Query(w2, g2, l2, 32, 32));
        Assert.Equal(32, World.Query(w2, g2, l2, 34, 32));
        Assert.Equal(0, World.Query(w2, g2, l2, 35, 32));
    }

    [Fact]
    public void NegativeOrigin_RoutesCells()
    {
        var w = World.New();
        var g = Grid.New(w, power: 6, x: -128f, y: -128f, size: 256f);
        var l = Layer.New(w);
        var stamp = Stamp.Box(8, 8, 50);
        World.Place(w, l, -64f, -64f, stamp, 4);
        World.Process(w);
        var cell = (int)((-64f + 128f) * 64 / 256);
        Assert.Equal(200, World.Query(w, g, l, cell, cell));
        Assert.Equal(0, World.Query(w, g, l, cell + 12, cell));
    }

    [Fact]
    public void RegionQuery_SumsCells()
    {
        var w = World.New();
        var g = Grid.New(w, power: 7, x: 0f, y: 0f, size: 128f);
        var l = Layer.New(w);
        var stamp = Stamp.Box(16, 16, 25);
        World.Place(w, l, 40f, 40f, stamp, 4);
        World.Process(w);

        var total = 0L;
        for (var cy = 30; cy < 60; cy++)
        for (var cx = 30; cx < 60; cx++)
            total += World.Query(w, g, l, cx, cy);
        Assert.Equal(total, World.Query(w, g, l, 30, 30, 30, 30));
        Assert.Equal(16 * 16 * 100, total);
    }

    [Fact]
    public void Process_RepeatedIsDeterministic()
    {
        var w = World.New();
        var g = Grid.New(w, power: 7, x: 0f, y: 0f, size: 128f);
        var l = Layer.New(w);
        var stamp = Stamp.Box(12, 12, 77);
        var rng = new Random(3);
        for (var i = 0; i < 100; i++)
            World.Place(w, l, (float)(rng.NextDouble() * 120), (float)(rng.NextDouble() * 120), stamp, 7);

        World.Process(w);
        var a = World.Query(w, g, l, 60, 60);
        var b = World.Query(w, g, l, 5, 90);
        World.Process(w);
        Assert.Equal(a, World.Query(w, g, l, 60, 60));
        Assert.Equal(b, World.Query(w, g, l, 5, 90));
    }

    [Fact]
    public void Clear_RemovesAllInfluence()
    {
        var w = World.New();
        var g = Grid.New(w, power: 6, x: 0f, y: 0f, size: 64f);
        var l = Layer.New(w);
        var stamp = Stamp.Box(8, 8, 100);
        World.Place(w, l, 32f, 32f, stamp, 8);
        World.Process(w);
        Assert.NotEqual(0, World.Query(w, g, l, 32, 32));

        World.Clear(w);
        World.Process(w);
        Assert.Equal(0, World.Query(w, g, l, 32, 32));
    }

    [Fact]
    public void Layers_AreIndependent()
    {
        var w = World.New();
        var g = Grid.New(w, power: 6, x: 0f, y: 0f, size: 64f);
        var l0 = Layer.New(w);
        var l1 = Layer.New(w);
        var stamp = Stamp.Box(8, 8, 60);
        World.Place(w, l0, 32f, 32f, stamp, 4);
        World.Process(w);
        Assert.Equal(240, World.Query(w, g, l0, 32, 32));
        Assert.Equal(0, World.Query(w, g, l1, 32, 32));
    }

    [Fact]
    public void QueryAt_MapsWorldToCell()
    {
        var w = World.New();
        var g = Grid.New(w, power: 7, x: 0f, y: 0f, size: 256f);
        var l = Layer.New(w);
        var stamp = Stamp.Box(8, 8, 100);
        World.Place(w, l, 128f, 128f, stamp, 4);
        World.Process(w);
        Assert.Equal(400, World.QueryAt(w, g, l, 128f, 128f));
        Assert.Equal(World.Query(w, g, l, 64, 64), World.QueryAt(w, g, l, 128f, 128f));
    }
}
