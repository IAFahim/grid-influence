using Xunit;

namespace Gi.Tests;

public sealed class EngineTests
{
    private static int RoundQ16(int value)
        => value < 0 ? -((-value + 32768) >> 16) : (value + 32768) >> 16;

    private readonly struct Band(int start, int end, int w)
    {
        public readonly int Start = start, End = end, W = w;
    }

    private static Band[] Bands(int length, int phase)
    {
        if (phase == 0) return [new Band(0, length, 256)];
        if (length == 1) return [new Band(0, 1, 256 - phase), new Band(1, 2, phase)];
        return [new Band(0, 1, 256 - phase), new Band(1, length, 256), new Band(length, length + 1, phase)];
    }

    private sealed class Oracle
    {
        private int _x, _y, _px, _py, _fx, _fy, _gain;
        private sbyte[]? _samples;
        private int _w, _h;
        private sbyte _constant;
        private bool _raster;

        public static Oracle Box(float wx, float wy, float ox, float oy, float scale,
            int w, int h, sbyte value, int gain)
        {
            var o = new Oracle { _w = w, _h = h, _constant = value, _gain = gain };
            o.Position(wx, wy, ox, oy, scale);
            return o;
        }

        public static Oracle Rast(float wx, float wy, float ox, float oy, float scale,
            sbyte[] samples, int w, int h, int gain)
        {
            var o = new Oracle { _w = w, _h = h, _samples = samples, _gain = gain, _raster = true };
            o.Position(wx, wy, ox, oy, scale);
            return o;
        }

        private void Position(float wx, float wy, float ox, float oy, float scale)
        {
            _x = (int)MathF.Floor((wx - ox) * scale * 256f) - (_w * 128);
            _y = (int)MathF.Floor((wy - oy) * scale * 256f) - (_h * 128);
            _px = _x >> 8;
            _py = _y >> 8;
            _fx = _x & 255;
            _fy = _y & 255;
        }

        private int Sample(int ix, int iy)
            => (uint)ix < (uint)_w && (uint)iy < (uint)_h && _samples != null ? _samples[iy * _w + ix] : 0;

        public long Contribution(int cx, int cy)
        {
            var sx = cx - _px;
            var sy = cy - _py;
            if (_raster)
            {
                if (sx < 0 || sx > _w || sy < 0 || sy > _h) return 0;
                var w00 = (256 - _fx) * (256 - _fy);
                var w10 = _fx * (256 - _fy);
                var w01 = (256 - _fx) * _fy;
                var w11 = _fx * _fy;
                return (long)RoundQ16(
                    Sample(sx, sy) * w00 + Sample(sx - 1, sy) * w10 +
                    Sample(sx, sy - 1) * w01 + Sample(sx - 1, sy - 1) * w11) * _gain;
            }

            var xw = 0;
            foreach (var b in Bands(_w, _fx))
                if (sx >= b.Start && sx < b.End) { xw = b.W; break; }
            var yw = 0;
            foreach (var b in Bands(_h, _fy))
                if (sy >= b.Start && sy < b.End) { yw = b.W; break; }
            if (xw == 0 || yw == 0) return 0;
            return (long)RoundQ16(_constant * xw * yw) * _gain;
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
    public void Move_RelocatesContributionExactly()
    {
        var w = World.New();
        var g = Grid.New(w, power: 6, x: 0f, y: 0f, size: 64f);
        var l = Layer.New(w);
        var stamp = Stamp.Box(8, 8, 100);
        var s = World.Place(w, l, 32f, 32f, stamp, 8);
        World.Process(w);
        Assert.Equal(800, World.Query(w, g, l, 32, 32));

        World.Move(w, s, 50f, 50f);
        World.Process(w);
        Assert.Equal(0, World.Query(w, g, l, 32, 32));
        Assert.Equal(800, World.Query(w, g, l, 50, 50));
    }

    [Fact]
    public void SetGain_AdjustsContributionExactly()
    {
        var w = World.New();
        var g = Grid.New(w, power: 6, x: 0f, y: 0f, size: 64f);
        var l = Layer.New(w);
        var stamp = Stamp.Box(8, 8, 100);
        var s = World.Place(w, l, 32f, 32f, stamp, 8);
        World.Process(w);
        Assert.Equal(800, World.Query(w, g, l, 32, 32));

        World.SetGain(w, s, 4);
        World.Process(w);
        Assert.Equal(400, World.Query(w, g, l, 32, 32));

        World.SetGain(w, s, 0);
        World.Process(w);
        Assert.Equal(0, World.Query(w, g, l, 32, 32));
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

    [Fact]
    public void InvalidWorld_DoesNotAccessNativeArena()
    {
        Assert.Equal(-1, World.Place(255, 0, 0f, 0f, 1, 1));
        Assert.Equal(0, World.Query(255, 0, 0, 0, 0));
        Assert.Equal(0, World.Query(255, 0, 0, 0, 0, 32, 32));
        Assert.Equal(0, World.QueryAt(255, 0, 0, 0f, 0f));
        World.Move(255, 0, 0f, 0f);
        World.SetGain(255, 0, 1);
        World.Remove(255, 0);
        World.Process(255);
        World.Clear(255);
        Assert.Throws<ArgumentOutOfRangeException>(() => Grid.New(255, 5, 0f, 0f, 32f));
        Assert.Throws<ArgumentOutOfRangeException>(() => Layer.New(255));
        Assert.False(Stats.Inspection.Read(255).Valid);
    }

    [Fact]
    public void RegionQuery_ClipsWithoutIntegerOverflow()
    {
        var w = World.New();
        var g = Grid.New(w, 6, 0f, 0f, 64f);
        var l = Layer.New(w);
        World.Place(w, l, 32f, 32f, Stamp.Box(8, 8, 10), 1);
        World.Process(w);
        Assert.Equal(360, World.Query(w, g, l, 30, 30, int.MaxValue, int.MaxValue));
        Assert.Equal(0, World.Query(w, g, l, int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue));
        Assert.Equal(0, World.Query(w, g, l, int.MinValue, int.MinValue, int.MaxValue, int.MaxValue));
    }

    [Fact]
    public void Move_UnchangedPositionKeepsPagesClean()
    {
        var w = World.New();
        var g = Grid.New(w, 6, 0f, 0f, 64f);
        var l = Layer.New(w);
        var source = World.Place(w, l, 32.25f, 32.5f, Stamp.Box(8, 8, 60), 4);
        World.Process(w);
        var before = Stats.Inspection.Read(w);
        var sum = World.Query(w, g, l, 0, 0, 64, 64);
        World.Move(w, source, 32.25f, 32.5f);
        var after = Stats.Inspection.Read(w);
        Assert.Equal(0, after.DirtyTiles);
        Assert.Equal(before.WorldBytes, after.WorldBytes);
        Assert.Equal(sum, World.Query(w, g, l, 0, 0, 64, 64));
    }

    [Fact]
    public unsafe void PageMap_TombstoneChurnKeepsCapacityBounded()
    {
        var map = new PageMap();
        byte block = 1;
        try
        {
            map.Put(0, &block);
            for (var i = 1; i < 10000; i++)
            {
                map.Put(i, &block);
                Assert.True(map.Remove(i));
                Assert.Equal(16, map.SlotCount);
                Assert.Equal(1, map.Count);
                Assert.True(map.TryGet(0, out var found));
                Assert.True(found == &block);
            }
        }
        finally
        {
            map.Reset();
        }
    }

    [Fact]
    public unsafe void Resolve_MatchesScalarPrefixAcrossVectorLanes()
    {
        var difference = stackalloc int[TileBake.DiffRows * TileBake.DiffPitch];
        var dense = stackalloc int[32 * 32];
        var previous = stackalloc int[32];
        var expectedPrevious = stackalloc int[32];
        var output = stackalloc short[32 * 32];
        new Span<int>(difference, TileBake.DiffRows * TileBake.DiffPitch).Clear();
        new Span<int>(previous, 32).Clear();
        new Span<int>(expectedPrevious, 32).Clear();
        for (var y = 0; y < 32; y++)
        for (var x = 0; x < 32; x++)
        {
            difference[y * TileBake.DiffPitch + x] = ((y * 37 + x * 13) % 41 - 20) * 4096;
            dense[y * 32 + x] = ((y * 19 + x * 31) % 17 - 8) * 70000;
        }

        Assert.True(TileBake.Resolve(difference, dense, previous, output));
        for (var y = 0; y < 32; y++)
        {
            var carry = 0;
            for (var x = 0; x < 32; x++)
            {
                carry += difference[y * TileBake.DiffPitch + x];
                expectedPrevious[x] += carry;
                var expected = (short)Math.Clamp(expectedPrevious[x] + dense[y * 32 + x], short.MinValue, short.MaxValue);
                Assert.Equal(expected, output[y * 32 + x]);
            }
        }

        new Span<int>(difference, TileBake.DiffRows * TileBake.DiffPitch).Clear();
        new Span<int>(dense, 32 * 32).Clear();
        new Span<int>(previous, 32).Clear();
        Assert.False(TileBake.Resolve(difference, dense, previous, output));
        for (var i = 0; i < 32 * 32; i++) Assert.Equal(0, output[i]);
    }

    [Fact]
    public void Inspection_AccountsForRetainedCapacityAfterClear()
    {
        var w = World.New();
        var g = Grid.New(w, 6, 0f, 0f, 64f);
        var l = Layer.New(w);
        var empty = Stats.Inspection.Read(w);
        World.Place(w, l, 16f, 16f, Stamp.Box(4, 4, 60), 2);
        var pending = Stats.Inspection.Read(w);
        Assert.Equal(1, pending.LiveSources);
        Assert.Equal(1, pending.LiveTiles);
        Assert.Equal(1, pending.DirtyTiles);
        Assert.Equal(64 * 12, pending.SourceBytes);
        Assert.Equal(12480, pending.DifferenceBytes + pending.DenseBytes + pending.PageBytes);
        Assert.Equal(4, pending.DirtyFlagBytes);
        Assert.Equal(64, pending.DirtyQueueBytes);
        World.Process(w);
        Assert.Equal(1920, World.Query(w, g, l, 0, 0, 64, 64));
        World.Clear(w);
        var cleared = Stats.Inspection.Read(w);
        Assert.Equal(0, cleared.SourceSlots);
        Assert.Equal(64, cleared.SourceCapacity);
        Assert.Equal(0, cleared.LiveTiles);
        Assert.Equal(0, cleared.MapBytes);
        Assert.Equal(empty.WorldBytes + 64 * 12 + 4 + 64, cleared.WorldBytes);
    }

    [Fact]
    public void FullRegionQuery_SumsLivePagesOnLargeSparseGrid()
    {
        var w = World.New();
        var g = Grid.New(w, 14, 0f, 0f, 16384f);
        var l = Layer.New(w);
        Assert.Equal(0, World.Query(w, g, l, 0, 0, 16384, 16384));
        var a = World.Place(w, l, 31.5f, 31.5f, Stamp.Box(4, 4, 64), 1);
        World.Place(w, l, 16000f, 16000f, Stamp.Box(4, 4, -32), 1);
        World.Process(w);
        Assert.Equal(512, World.Query(w, g, l, 0, 0, 16384, 16384));
        Assert.Equal(512, World.Query(w, g, l, -1, -1, int.MaxValue, int.MaxValue));
        World.Remove(w, a);
        World.Process(w);
        Assert.Equal(-512, World.Query(w, g, l, 0, 0, 16384, 16384));
    }

    [Fact]
    public unsafe void QueryRegion_MatchesCellQuery_AcrossTileBoundaries()
    {
        var w = World.New();
        var g = Grid.New(w, power: 7, x: 0f, y: 0f, size: 128f);
        var l = Layer.New(w);
        var box = Stamp.Box(12, 8, 90);
        var samples = new sbyte[5 * 5];
        for (var i = 0; i < samples.Length; i++) samples[i] = (sbyte)(i * 11 % 30 - 15);
        var raster = Stamp.New(samples, 5, 5);
        var rng = new Random(31);
        for (var i = 0; i < 80; i++)
        {
            World.Place(w, l, (float)(rng.NextDouble() * 128), (float)(rng.NextDouble() * 128), box, 6 + rng.Next(11));
            World.Place(w, l, (float)(rng.NextDouble() * 128), (float)(rng.NextDouble() * 128), raster, 4);
        }

        World.Process(w);

        (int x, int y, int width, int height)[] regions =
        {
            (0, 0, 128, 128),
            (29, 33, 40, 17),
            (126, 126, 5, 5),
            (-3, -4, 10, 9),
            (60, 120, 8, 20),
            (31, 31, 2, 2),
        };

        foreach (var (x, y, width, height) in regions)
        {
            var buffer = new short[width * height];
            fixed (short* p = buffer) World.QueryRegion(w, g, l, x, y, width, height, p);
            for (var row = 0; row < height; row++)
            for (var col = 0; col < width; col++)
                Assert.Equal(World.Query(w, g, l, x + col, y + row), buffer[row * width + col]);
        }
    }

    [Fact]
    public unsafe void QueryRegion_ZeroesUntouchedAndFreedPages()
    {
        var w = World.New();
        var g = Grid.New(w, power: 6, x: 0f, y: 0f, size: 64f);
        var l = Layer.New(w);

        var untouched = new short[64 * 64];
        fixed (short* p = untouched) World.QueryRegion(w, g, l, 0, 0, 64, 64, p);
        Assert.All(untouched, v => Assert.Equal(0, v));

        var source = World.Place(w, l, 32f, 32f, Stamp.Box(8, 8, 50), 6);
        World.Process(w);
        var placed = new short[64 * 64];
        fixed (short* p = placed) World.QueryRegion(w, g, l, 0, 0, 64, 64, p);
        Assert.Equal(300, placed[32 * 64 + 32]);

        World.Remove(w, source);
        World.Process(w);
        var freed = new short[64 * 64];
        fixed (short* p = freed) World.QueryRegion(w, g, l, 0, 0, 64, 64, p);
        Assert.All(freed, v => Assert.Equal(0, v));
    }
}
