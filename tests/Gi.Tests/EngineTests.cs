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

    private static Band[] Bands(long phase, long extent)
    {
        var end = phase + extent;
        var full = (int)(end >> 8);
        var tail = (int)(end & 255);
        var bands = new List<Band>();
        if (phase == 0) bands.Add(new Band(0, full, 256));
        else
        {
            bands.Add(new Band(0, 1, 256 - (int)phase));
            bands.Add(new Band(1, full, 256));
        }

        if (tail != 0) bands.Add(new Band(full, full + 1, tail));
        bands.RemoveAll(b => b.End <= b.Start);
        return [.. bands];
    }

    private static sbyte Average(int sum, int n)
        => (sbyte)(sum < 0 ? -((-sum + (n >> 1)) / n) : (sum + (n >> 1)) / n);

    private sealed class Oracle
    {
        private int _px, _py, _fx, _fy, _gain;
        private sbyte[]? _samples;
        private int _w, _h;
        private sbyte _constant;
        private bool _raster;
        private int _scaleQ8 = 256;
        private long _extX, _extY;
        private List<(int w, int h, sbyte[] data)> _mips = [];

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
            o.BuildMips();
            return o;
        }

        private void Position(float wx, float wy, float ox, float oy, float scale)
        {
            _scaleQ8 = (int)(scale * 256f);
            var leadX = (long)(int)MathF.Floor((wx - ox) * _scaleQ8) + ((long)-(_w * 128) * _scaleQ8 >> 8);
            var leadY = (long)(int)MathF.Floor((wy - oy) * _scaleQ8) + ((long)-(_h * 128) * _scaleQ8 >> 8);
            _px = (int)(leadX >> 8);
            _py = (int)(leadY >> 8);
            _fx = (int)(leadX & 255);
            _fy = (int)(leadY & 255);
            _extX = (long)_w * _scaleQ8;
            _extY = (long)_h * _scaleQ8;
        }

        private void BuildMips()
        {
            var sw = _w;
            var sh = _h;
            var source = _samples!;
            while (sw > 1 || sh > 1)
            {
                var dw = (sw + 1) >> 1;
                var dh = (sh + 1) >> 1;
                var down = new sbyte[dw * dh];
                for (var y = 0; y < dh; y++)
                for (var x = 0; x < dw; x++)
                {
                    var sum = 0;
                    for (var dy = 0; dy < 2; dy++)
                    for (var dx = 0; dx < 2; dx++)
                    {
                        var sx = x * 2 + dx;
                        var sy = y * 2 + dy;
                        if (sx < sw && sy < sh) sum += source[sy * sw + sx];
                    }

                    down[y * dw + x] = Average(sum, 4);
                }

                _mips.Add((dw, dh, down));
                source = down;
                sw = dw;
                sh = dh;
            }
        }

        private int Sample(int ix, int iy)
            => (uint)ix < (uint)_w && (uint)iy < (uint)_h && _samples != null ? _samples[iy * _w + ix] : 0;

        public long Contribution(int cx, int cy)
        {
            var sx = cx - _px;
            var sy = cy - _py;
            if (_raster) return RasterContribution(sx, sy);

            var xw = 0;
            foreach (var b in Bands(_fx, _extX))
                if (sx >= b.Start && sx < b.End) { xw = b.W; break; }
            var yw = 0;
            foreach (var b in Bands(_fy, _extY))
                if (sy >= b.Start && sy < b.End) { yw = b.W; break; }
            if (xw == 0 || yw == 0) return 0;
            return (long)RoundQ16(_constant * xw * yw) * _gain;
        }

        private long RasterContribution(int sx, int sy)
        {
            var spanX = (int)((_fx + _extX + 255) >> 8);
            var spanY = (int)((_fy + _extY + 255) >> 8);
            if ((uint)sx >= (uint)spanX || (uint)sy >= (uint)spanY) return 0;

            if (_scaleQ8 == 256)
            {
                var w00 = (256 - _fx) * (256 - _fy);
                var w10 = _fx * (256 - _fy);
                var w01 = (256 - _fx) * _fy;
                var w11 = _fx * _fy;
                return (long)RoundQ16(
                    Sample(sx, sy) * w00 + Sample(sx - 1, sy) * w10 +
                    Sample(sx, sy - 1) * w01 + Sample(sx - 1, sy - 1) * w11) * _gain;
            }

            var step = 256L * 65536 / _scaleQ8;
            var level = 0;
            while (level < _mips.Count && (step >> (level + 1)) >= 65536) level++;
            int w, h;
            sbyte[] data;
            if (level == 0)
            {
                w = _w;
                h = _h;
                data = _samples!;
            }
            else
            {
                (w, h, data) = _mips[level - 1];
            }

            int Tap(int ix, int iy)
                => (uint)ix < (uint)w && (uint)iy < (uint)h ? data[iy * w + ix] : 0;

            var uq = (-(long)_fx * 65536 / _scaleQ8 + (long)sx * step) >> level;
            var vq = (-(long)_fy * 65536 / _scaleQ8 + (long)sy * step) >> level;
            var ix = (int)(uq >> 16);
            var iy = (int)(vq >> 16);
            var fx2 = (int)((uq >> 8) & 255);
            var fy2 = (int)((vq >> 8) & 255);
            var top = Tap(ix, iy) * (256 - fx2) + Tap(ix + 1, iy) * fx2;
            var bottom = Tap(ix, iy + 1) * (256 - fx2) + Tap(ix + 1, iy + 1) * fx2;
            return (long)RoundQ16(top * (256 - fy2) + bottom * fy2) * _gain;
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
    public void RasterStamps_MatchOracle_AcrossGridScales()
    {
        var w = World.New();
        var gOne = Grid.New(w, power: 8, x: 0f, y: 0f, size: 256f);
        var gDouble = Grid.New(w, power: 9, x: 0f, y: 0f, size: 256f);
        var gHalf = Grid.New(w, power: 7, x: 0f, y: 0f, size: 256f);
        var gQuarter = Grid.New(w, power: 6, x: 0f, y: 0f, size: 256f);
        var l = Layer.New(w);
        var samples = new sbyte[9 * 7];
        for (var i = 0; i < samples.Length; i++) samples[i] = (sbyte)((i * 29 % 37) - 18);
        var stamp = Stamp.New(samples, 9, 7);

        var one = new List<Oracle>();
        var dbl = new List<Oracle>();
        var half = new List<Oracle>();
        var quarter = new List<Oracle>();
        float[] xs = [100.3f, 13.9f, 247.6f];
        float[] ys = [88.7f, 201.4f, 5.2f];
        for (var i = 0; i < 3; i++)
        {
            World.Place(w, l, xs[i], ys[i], stamp, 5);
            one.Add(Oracle.Rast(xs[i], ys[i], 0f, 0f, 1f, samples, 9, 7, 5));
            dbl.Add(Oracle.Rast(xs[i], ys[i], 0f, 0f, 2f, samples, 9, 7, 5));
            half.Add(Oracle.Rast(xs[i], ys[i], 0f, 0f, 0.5f, samples, 9, 7, 5));
            quarter.Add(Oracle.Rast(xs[i], ys[i], 0f, 0f, 0.25f, samples, 9, 7, 5));
        }

        World.Process(w);
        var rng = new Random(29);
        for (var q = 0; q < 4; q++)
        {
            var size = q == 0 ? 256 : q == 1 ? 512 : q == 2 ? 128 : 64;
            var grid = q == 0 ? gOne : q == 1 ? gDouble : q == 2 ? gHalf : gQuarter;
            var list = q == 0 ? one : q == 1 ? dbl : q == 2 ? half : quarter;
            for (var i = 0; i < 400; i++)
            {
                var cx = rng.Next(size);
                var cy = rng.Next(size);
                var expected = Expected(list, cx, cy);
                var actual = World.Query(w, grid, l, cx, cy);
                Assert.True(expected == actual, $"grid{q} size={size} cell=({cx},{cy}) expected={expected} actual={actual}");
            }

            for (var cy = 0; cy < size; cy++)
            for (var cx = 0; cx < size; cx++)
            {
                var expected = Expected(list, cx, cy);
                var actual = World.Query(w, grid, l, cx, cy);
                Assert.True(expected == actual, $"grid{q} size={size} cell=({cx},{cy}) expected={expected} actual={actual}");
            }
        }
    }

    [Fact]
    public void BoxStamps_CrossGridSums_ConserveWorldIntegral()
    {
        var w = World.New();
        var gFine = Grid.New(w, power: 8, x: 0f, y: 0f, size: 256f);
        var gHalf = Grid.New(w, power: 7, x: 0f, y: 0f, size: 256f);
        var gQuarter = Grid.New(w, power: 6, x: 0f, y: 0f, size: 256f);
        var l = Layer.New(w);
        var stamp = Stamp.Box(16, 16, 100);
        for (var i = 0; i < 12; i++)
            World.Place(w, l, 20f + i * 16, 24f + (i % 5) * 32, stamp, 8);

        World.Process(w);
        var fine = World.Query(w, gFine, l, 0, 0, 256, 256);
        var half = World.Query(w, gHalf, l, 0, 0, 128, 128);
        var quarter = World.Query(w, gQuarter, l, 0, 0, 64, 64);
        Assert.Equal(fine / 4, half);
        Assert.Equal(fine / 16, quarter);
    }

    [Fact]
    public unsafe void StampMips_BuildBoxAverages()
    {
        var samples = new sbyte[4 * 4]
        {
            100, 60, 20, -20,
            60, 20, -20, -60,
            20, -20, -60, -100,
            -20, -60, -100, 127,
        };
        var stamp = Stamp.New(samples, 4, 4);
        var v = StampCatalog.Get(stamp);
        Assert.Equal(2, v->MipCount);
        Assert.Equal((sbyte)60, v->Mips[5]);
        Assert.Equal((sbyte)-20, v->Mips[6]);
        Assert.Equal((sbyte)-20, v->Mips[9]);
        Assert.Equal((sbyte)-33, v->Mips[10]);
        Assert.Equal((sbyte)-3, v->Mips[20]);
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
        var pageSum = 0L;
        var pageMax = short.MinValue;
        new Span<int>(difference, TileBake.DiffRows * TileBake.DiffPitch).Clear();
        new Span<int>(previous, 32).Clear();
        new Span<int>(expectedPrevious, 32).Clear();
        for (var y = 0; y < 32; y++)
        for (var x = 0; x < 32; x++)
        {
            difference[y * TileBake.DiffPitch + x] = ((y * 37 + x * 13) % 41 - 20) * 4096;
            dense[y * 32 + x] = ((y * 19 + x * 31) % 17 - 8) * 70000;
        }

        Assert.True(TileBake.Resolve(difference, dense, previous, output, &pageSum, &pageMax));
        var expectedSum = 0L;
        var expectedMax = short.MinValue;
        for (var y = 0; y < 32; y++)
        {
            var carry = 0;
            for (var x = 0; x < 32; x++)
            {
                carry += difference[y * TileBake.DiffPitch + x];
                expectedPrevious[x] += carry;
                var expected = (short)Math.Clamp(expectedPrevious[x] + dense[y * 32 + x], short.MinValue, short.MaxValue);
                Assert.Equal(expected, output[y * 32 + x]);
                expectedSum += expected;
                if (expected > expectedMax) expectedMax = expected;
            }
        }

        Assert.Equal(expectedSum, pageSum);
        Assert.Equal(expectedMax, pageMax);

        new Span<int>(difference, TileBake.DiffRows * TileBake.DiffPitch).Clear();
        new Span<int>(dense, 32 * 32).Clear();
        new Span<int>(previous, 32).Clear();
        Assert.False(TileBake.Resolve(difference, dense, previous, output, &pageSum, &pageMax));
        for (var i = 0; i < 32 * 32; i++) Assert.Equal(0, output[i]);
        Assert.Equal(0L, pageSum);
        Assert.Equal((short)0, pageMax);
    }

    [Fact]
    public unsafe void Inspection_AccountsForRetainedCapacityAfterClear()
    {
        var w = World.New();
        var g = Grid.New(w, 6, 0f, 0f, 64f);
        var l = Layer.New(w);
        var empty = Stats.Inspection.Read(w);
        World.Place(w, l, 16f, 16f, Stamp.Box(4, 4, 60), 2);
        var pending = Stats.Inspection.Read(w);
        Assert.Equal(1, pending.LiveSources);
        Assert.Equal(0, pending.LiveTiles);
        Assert.Equal(0, pending.DirtyTiles);
        Assert.Equal(64 * 17, pending.SourceBytes);
        Assert.Equal(0, pending.MapBytes + pending.DifferenceBytes + pending.DenseBytes + pending.PageBytes);
        World.Process(w);
        var processed = Stats.Inspection.Read(w);
        Assert.Equal(1, processed.LiveTiles);
        Assert.Equal(0, processed.DirtyTiles);
        Assert.Equal(World.BlockBytes, 6528);
        Assert.Equal(World.BlockBytes, processed.DifferenceBytes + processed.DenseBytes + processed.DensePointerBytes + processed.PageBytes + processed.PageSumBytes);
        Assert.Equal(4, processed.DirtyFlagBytes);
        Assert.Equal(1920, World.Query(w, g, l, 0, 0, 64, 64));
        World.Clear(w);
        var cleared = Stats.Inspection.Read(w);
        Assert.Equal(0, cleared.SourceSlots);
        Assert.Equal(64, cleared.SourceCapacity);
        Assert.Equal(0, cleared.LiveTiles);
        Assert.Equal(0, cleared.MapBytes);
        Assert.Equal(empty.WorldBytes + 64 * 17 + 4 + 64 + 16 * sizeof(DepositOp) + 64 * sizeof(int), cleared.WorldBytes);
    }

    [Fact]
    public void Inspection_DenseAllocatesLazilyPerRasterTile()
    {
        var w = World.New();
        var g = Grid.New(w, 6, 0f, 0f, 64f);
        var l = Layer.New(w);
        var samples = new sbyte[6 * 6];
        for (var i = 0; i < samples.Length; i++) samples[i] = (sbyte)(i % 7);
        World.Place(w, l, 16f, 16f, Stamp.Box(4, 4, 60), 2);
        World.Place(w, l, 40f, 40f, Stamp.New(samples, 6, 6), 2);

        var pending = Stats.Inspection.Read(w);
        Assert.Equal(0, pending.LiveTiles);
        Assert.Equal(0, pending.RasterTiles);
        Assert.Equal(0, pending.DenseBytes);
        Assert.Equal(0, pending.DensePointerBytes);

        World.Process(w);
        var processed = Stats.Inspection.Read(w);
        Assert.Equal(2, processed.LiveTiles);
        Assert.Equal(1, processed.RasterTiles);
        Assert.Equal((long)World.DenseBytes, processed.DenseBytes);
        Assert.Equal(2 * (World.SumOffset - World.DensePtrOffset), processed.DensePointerBytes);
        var expected = 0;
        for (var y = 0; y < 6; y++)
        for (var x = 0; x < 6; x++) expected += samples[y * 6 + x];
        Assert.Equal(2 * expected, World.Query(w, g, l, 32, 32, 32, 32));

        World.Clear(w);
        var cleared = Stats.Inspection.Read(w);
        Assert.Equal(0, cleared.RasterTiles);
        Assert.Equal(0, cleared.DenseBytes);
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

    [Fact]
    public void RegionQuery_PartialSumsMatchCellScan()
    {
        var w = World.New();
        var g = Grid.New(w, power: 8, x: 0f, y: 0f, size: 256f);
        var l = Layer.New(w);
        var box = Stamp.Box(14, 14, 80);
        var samples = new sbyte[6 * 9];
        for (var i = 0; i < samples.Length; i++) samples[i] = (sbyte)(i * 17 % 40 - 20);
        var raster = Stamp.New(samples, 6, 9);
        var rng = new Random(63);
        for (var i = 0; i < 300; i++)
        {
            var x = (float)(rng.NextDouble() * 256);
            var y = (float)(rng.NextDouble() * 256);
            World.Place(w, l, x, y, rng.Next(2) == 0 ? box : raster, 4 + rng.Next(13));
        }

        World.Process(w);
        (int x, int y, int width, int height)[] regions =
        {
            (0, 0, 256, 256),
            (1, 1, 254, 254),
            (0, 0, 255, 256),
            (17, 0, 222, 256),
            (31, 33, 130, 127),
            (33, 65, 1, 1),
            (5, 250, 200, 10),
            (-10, -10, 300, 300),
            (250, 250, 10, 10),
        };

        foreach (var (x, y, width, height) in regions)
        {
            var scan = 0L;
            for (var row = 0; row < height; row++)
            for (var col = 0; col < width; col++)
            {
                var cy = y + row;
                var cx = x + col;
                if ((uint)cx < 256 && (uint)cy < 256) scan += World.Query(w, g, l, cx, cy);
            }

            Assert.Equal(scan, World.Query(w, g, l, x, y, width, height));
        }
    }

    [Fact]
    public void QueryAt_MatchesDepositMappingAtNonPowerOfTwoScale()
    {
        var w = World.New();
        var g = Grid.New(w, 14, 0f, 0f, 10000f);
        var l = Layer.New(w);
        var stamp = Stamp.Box(4, 4, 60);
        var scale = 16384f / 10000f;
        var scaleQ8 = (int)(scale * 256f);
        var rng = new Random(97);
        var xs = new float[200];
        var ys = new float[200];
        var diverged = 0;
        for (var i = 0; i < 200; i++)
        {
            xs[i] = (float)(rng.NextDouble() * 9800 + 100);
            ys[i] = (float)(rng.NextDouble() * 9800 + 100);
            World.Place(w, l, xs[i], ys[i], stamp, 6);
            var cx = (int)MathF.Floor(xs[i] * scaleQ8) >> 8;
            var cy = (int)MathF.Floor(ys[i] * scaleQ8) >> 8;
            var floatCx = (int)MathF.Floor(xs[i] * scale);
            var floatCy = (int)MathF.Floor(ys[i] * scale);
            if (cx != floatCx || cy != floatCy) diverged++;
        }

        World.Process(w);
        Assert.True(diverged > 0);
        for (var i = 0; i < 200; i++)
        {
            var cx = (int)MathF.Floor(xs[i] * scaleQ8) >> 8;
            var cy = (int)MathF.Floor(ys[i] * scaleQ8) >> 8;
            Assert.Equal(World.Query(w, g, l, cx, cy), World.QueryAt(w, g, l, xs[i], ys[i]));
        }
    }

    [Fact]
    public void SignedGain_SubtractsExactly()
    {
        var w = World.New();
        var g = Grid.New(w, 6, 0f, 0f, 64f);
        var red = Layer.New(w);
        var blue = Layer.New(w);
        var stamp = Stamp.Box(8, 8, 50);
        var pos = World.Place(w, red, 32f, 32f, stamp, 8);
        var neg = World.Place(w, blue, 32f, 32f, stamp, -3);
        World.Process(w);
        Assert.Equal(400, World.Query(w, g, red, 32, 32));
        Assert.Equal(-150, World.Query(w, g, blue, 32, 32));
        Assert.Equal(-150 * 64, World.Query(w, g, blue, 0, 0, 64, 64));

        World.SetGain(w, pos, -8);
        World.SetGain(w, neg, 3);
        World.Process(w);
        Assert.Equal(-400, World.Query(w, g, red, 32, 32));
        Assert.Equal(150, World.Query(w, g, blue, 32, 32));

        World.SetGain(w, pos, 20);
        World.SetGain(w, neg, -20);
        World.Process(w);
        Assert.Equal(16 * 50, World.Query(w, g, red, 32, 32));
        Assert.Equal(-16 * 50, World.Query(w, g, blue, 32, 32));
    }

    [Fact]
    public void SourceIds_ReuseSlotsAndRejectStaleHandles()
    {
        var w = World.New();
        var g = Grid.New(w, 6, 0f, 0f, 64f);
        var l = Layer.New(w);
        var stamp = Stamp.Box(4, 4, 40);
        var survivor = World.Place(w, l, 12f, 12f, stamp, 5);
        var first = -1;
        for (var i = 0; i < 500; i++)
        {
            var id = World.Place(w, l, 40f, 40f, stamp, 3);
            if (i == 0) first = id;
            World.Remove(w, id);
        }

        Assert.InRange(Stats.Inspection.Read(w).SourceSlots, 1, 8);
        var reused = World.Place(w, l, 40f, 40f, stamp, 3);
        Assert.NotEqual(first, reused);
        World.Process(w);
        Assert.Equal(5 * 40 * 16 + 3 * 40 * 16, World.Query(w, g, l, 0, 0, 64, 64));

        World.Move(w, first, 8f, 8f);
        World.SetGain(w, first, 1);
        World.Remove(w, first);
        World.Process(w);
        Assert.Equal(5 * 40 * 16 + 3 * 40 * 16, World.Query(w, g, l, 0, 0, 64, 64));

        World.Move(w, survivor, 20f, 20f);
        World.Process(w);
        Assert.Equal(5 * 40, World.Query(w, g, l, 20, 20));
        Assert.Equal(0, World.Query(w, g, l, 12, 12));
    }

    [Fact]
    public void SourceIds_GenerationsAreDeterministicAcrossGrowth()
    {
        var w = World.New();
        var l = Layer.New(w);
        var stamp = Stamp.Box(2, 2, 10);
        Assert.Equal(1 << 24, World.Place(w, l, 1f, 1f, stamp, 1));
        for (var i = 0; i < 200; i++) World.Place(w, l, 2f, 2f, stamp, 1);
        Assert.Equal((1 << 24) | 201, World.Place(w, l, 3f, 3f, stamp, 1));
        World.Clear(w);
        Assert.Equal(2 << 24, World.Place(w, l, 4f, 4f, stamp, 1));
    }

    [Fact]
    public void QueryGradient_MatchesCentralDifferences()
    {
        var w = World.New();
        var g = Grid.New(w, 8, 0f, 0f, 256f);
        var l = Layer.New(w);
        var stamp = Stamp.Box(12, 12, 40);
        var rng = new Random(61);
        var ids = new int[40];
        for (var i = 0; i < ids.Length; i++)
            ids[i] = World.Place(w, l,
                (float)(rng.NextDouble() * 244 + 6), (float)(rng.NextDouble() * 244 + 6),
                stamp, rng.Next(-8, 17));
        World.Process(w);

        for (var i = 0; i < 500; i++)
        {
            var x = (float)(rng.NextDouble() * 260 - 2);
            var y = (float)(rng.NextDouble() * 260 - 2);
            World.QueryGradient(w, g, l, x, y, out var gx, out var gy);
            var cx = (int)MathF.Floor(x * 256f) >> 8;
            var cy = (int)MathF.Floor(y * 256f) >> 8;
            Assert.Equal(World.Query(w, g, l, cx + 1, cy) - World.Query(w, g, l, cx - 1, cy), gx);
            Assert.Equal(World.Query(w, g, l, cx, cy + 1) - World.Query(w, g, l, cx, cy - 1), gy);
        }

        for (var round = 0; round < 3; round++)
        {
            for (var i = round % 2; i < ids.Length; i += 2)
                World.Move(w, ids[i],
                    (float)(rng.NextDouble() * 244 + 6), (float)(rng.NextDouble() * 244 + 6));
            World.Process(w);
            var x = (float)(rng.NextDouble() * 250 + 3);
            var y = (float)(rng.NextDouble() * 250 + 3);
            World.QueryGradient(w, g, l, x, y, out var gx, out var gy);
            var cx = (int)MathF.Floor(x * 256f) >> 8;
            var cy = (int)MathF.Floor(y * 256f) >> 8;
            Assert.Equal(World.Query(w, g, l, cx + 1, cy) - World.Query(w, g, l, cx - 1, cy), gx);
            Assert.Equal(World.Query(w, g, l, cx, cy + 1) - World.Query(w, g, l, cx, cy - 1), gy);
        }

        World.QueryGradient(255, g, l, 1f, 1f, out var ex, out var ey);
        Assert.Equal((0, 0), (ex, ey));
    }

    [Fact]
    public unsafe void DeferredMutations_MatchSteppedProcessing()
    {
        var rng = new Random(71);
        var stampA = Stamp.Box(10, 6, 55);
        var stampB = Stamp.Box(4, 4, 30);
        var samples = new sbyte[49];
        for (var i = 0; i < samples.Length; i++) samples[i] = (sbyte)(i % 11 - 5);
        var stampC = Stamp.New(samples, 7, 7);

        for (var round = 0; round < 6; round++)
        {
            var batched = World.New();
            var stepped = World.New();
            var gb = Grid.New(batched, 8, 0f, 0f, 256f);
            var gs = Grid.New(stepped, 8, 0f, 0f, 256f);
            var lb = Layer.New(batched);
            var ls = Layer.New(stepped);
            var ids = new int[30];
            var live = new bool[30];
            for (var step = 0; step < 120; step++)
            {
                var k = rng.Next(30);
                var stamp = k % 3 == 0 ? stampA : k % 3 == 1 ? stampB : stampC;
                var x = (float)(rng.NextDouble() * 244 + 6);
                var y = (float)(rng.NextDouble() * 244 + 6);
                var gain = rng.Next(-16, 17);
                switch (rng.Next(4))
                {
                    case 0:
                        ids[k] = World.Place(batched, lb, x, y, stamp, gain);
                        World.Place(stepped, ls, x, y, stamp, gain);
                        live[k] = true;
                        break;
                    case 1:
                        if (!live[k] || ids[k] < 0) break;
                        World.Move(batched, ids[k], x, y);
                        World.Move(stepped, ids[k], x, y);
                        break;
                    case 2:
                        if (!live[k] || ids[k] < 0) break;
                        World.SetGain(batched, ids[k], gain);
                        World.SetGain(stepped, ids[k], gain);
                        break;
                    default:
                        if (!live[k] || ids[k] < 0) break;
                        World.Remove(batched, ids[k]);
                        World.Remove(stepped, ids[k]);
                        live[k] = false;
                        break;
                }

                World.Process(stepped);
            }

            World.Process(batched);
            var fieldA = new short[256 * 256];
            var fieldB = new short[256 * 256];
            fixed (short* pa = fieldA, pb = fieldB)
            {
                World.QueryRegion(batched, gb, lb, 0, 0, 256, 256, pa);
                World.QueryRegion(stepped, gs, ls, 0, 0, 256, 256, pb);
            }

            Assert.Equal(fieldB, fieldA);
            var batchedMax = World.QueryMax(batched, gb, lb, out var bx, out var by);
            Assert.Equal(batchedMax, World.Query(batched, gb, lb, bx, by));
        }
    }

    [Fact]
    public unsafe void DeferredPlaceRemoveWindowCollapsesToZero()
    {
        var w = World.New();
        var g = Grid.New(w, 8, 0f, 0f, 256f);
        var l = Layer.New(w);
        var stamp = Stamp.Box(12, 12, 40);
        var ids = new int[200];
        for (var i = 0; i < ids.Length; i++)
            ids[i] = World.Place(w, l, (i * 13.7f) % 240f + 8f, (i * 7.3f) % 240f + 8f, stamp, 7);
        for (var i = 0; i < ids.Length; i++) World.Remove(w, ids[i]);
        var before = Stats.Inspection.Read(w);
        Assert.Equal(0, before.LiveSources);
        World.Process(w);
        var after = Stats.Inspection.Read(w);
        Assert.Equal(0, after.LiveTiles);
        Assert.Equal(0, after.MapBytes);
        Assert.Equal(0, after.DirtyTiles);
        Assert.Equal(0, World.Query(w, g, l, 0, 0, 256, 256));
        Assert.Equal((short)0, World.QueryMax(w, g, l, out _, out _));
        Assert.Equal(0, World.ChangedTiles(w, g, l, null));
    }

    [Fact]
    public unsafe void ChangedTiles_ReportLastProcessDrain()
    {
        var w = World.New();
        var g = Grid.New(w, 6, 0f, 0f, 64f);
        var red = Layer.New(w);
        var blue = Layer.New(w);
        var boxes = stackalloc int[16];

        Assert.Equal(0, World.ChangedTiles(w, g, red, null));
        var a = World.Place(w, red, 32f, 32f, Stamp.Box(16, 16, 50), 4);
        var b = World.Place(w, red, 8f, 8f, Stamp.Box(8, 8, 30), 4);
        World.Process(w);
        var count = World.ChangedTiles(w, g, red, boxes);
        Assert.Equal(4, count);
        Assert.Equal(0, World.ChangedTiles(w, g, blue, null));

        var seen = new HashSet<int>();
        for (var i = 0; i < count; i++)
        {
            Assert.InRange(boxes[i], 0, 3);
            Assert.True(seen.Add(boxes[i]));
        }

        World.Process(w);
        Assert.Equal(0, World.ChangedTiles(w, g, red, null));

        World.Move(w, a, 40f, 40f);
        World.Process(w);
        count = World.ChangedTiles(w, g, red, boxes);
        Assert.Equal(4, count);
        Assert.Equal(0, World.ChangedTiles(w, g, blue, null));

        World.Remove(w, a);
        World.Remove(w, b);
        World.Process(w);
        count = World.ChangedTiles(w, g, red, boxes);
        Assert.Equal(2, count);
        for (var i = 0; i < count; i++) Assert.Equal(0, World.Query(w, g, red, (boxes[i] & 1) * 32, (boxes[i] >> 1) * 32));

        World.Clear(w);
        Assert.Equal(0, World.ChangedTiles(w, g, red, null));
    }

    private static short ScanMax(byte w, byte g, byte l, int size)
    {
        var field = new short[size * size];
        unsafe
        {
            fixed (short* p = field) World.QueryRegion(w, g, l, 0, 0, size, size, p);
        }

        var max = short.MinValue;
        for (var i = 0; i < field.Length; i++)
            if (field[i] > max) max = field[i];
        return max;
    }

    private static void AssertMaxAgreesWithScan(byte w, byte g, byte l, int size)
    {
        var expected = ScanMax(w, g, l, size);
        var first = World.QueryMax(w, g, l, out var x, out var y);
        Assert.Equal(expected, first);
        Assert.Equal(first, World.Query(w, g, l, x, y));

        var repeat = World.QueryMax(w, g, l, out var x2, out var y2);
        Assert.Equal(first, repeat);
        Assert.Equal((x, y), (x2, y2));
    }

    [Fact]
    public void QueryMax_MatchesRowMajorScanAcrossChurn()
    {
        var w = World.New();
        var g = Grid.New(w, 8, 0f, 0f, 256f);
        var l = Layer.New(w);
        var box = Stamp.Box(12, 12, 40);
        var rng = new Random(31);
        var ids = new int[60];
        for (var i = 0; i < ids.Length; i++)
            ids[i] = World.Place(w, l,
                (float)(rng.NextDouble() * 244 + 6),
                (float)(rng.NextDouble() * 244 + 6),
                box, rng.Next(-6, 10));

        World.Process(w);
        for (var round = 0; round < 10; round++)
        {
            AssertMaxAgreesWithScan(w, g, l, 256);

            for (var i = round % 3; i < ids.Length; i += 3)
            {
                if (rng.Next(4) == 0) World.Remove(w, ids[i]);
                else World.Move(w, ids[i],
                    (float)(rng.NextDouble() * 244 + 6),
                    (float)(rng.NextDouble() * 244 + 6));
            }

            World.Place(w, l,
                (float)(rng.NextDouble() * 244 + 6),
                (float)(rng.NextDouble() * 244 + 6),
                box, rng.Next(-6, 10));
            World.Process(w);
        }
    }

    [Fact]
    public void QueryMax_EmptyAndClearedLayersReportZero()
    {
        var w = World.New();
        var g = Grid.New(w, 7, 0f, 0f, 128f);
        var l = Layer.New(w);
        Assert.Equal((short)0, World.QueryMax(w, g, l, out var x0, out var y0));
        Assert.Equal((0, 0), (x0, y0));

        var source = World.Place(w, l, 64f, 64f, Stamp.Box(10, 10, 50), 9);
        World.Process(w);
        Assert.Equal((short)450, World.QueryMax(w, g, l, out _, out _));

        World.Remove(w, source);
        World.Process(w);
        Assert.Equal((short)0, World.QueryMax(w, g, l, out var x1, out var y1));
        Assert.Equal((0, 0), (x1, y1));

        World.Place(w, l, 64f, 64f, Stamp.Box(10, 10, 50), 9);
        World.Process(w);
        World.Clear(w);
        Assert.Equal((short)0, World.QueryMax(w, g, l, out var x2, out var y2));
        Assert.Equal((0, 0), (x2, y2));
    }

    [Fact]
    public void QueryMax_NegativeFieldFindsLeastNegativeCell()
    {
        var w = World.New();
        var g = Grid.New(w, 6, 0f, 0f, 64f);
        var l = Layer.New(w);
        var stamp = Stamp.Box(24, 24, 30);
        for (var cy = 0; cy < 4; cy++)
        for (var cx = 0; cx < 4; cx++)
            World.Place(w, l, 8f + cx * 16, 8f + cy * 16, stamp, -4);

        World.Process(w);
        AssertMaxAgreesWithScan(w, g, l, 64);
        Assert.True(World.QueryMax(w, g, l, out _, out _) < 0);
    }

    [Fact]
    public void QueryMax_SaturatesAndMatchesScan()
    {
        var w = World.New();
        var g = Grid.New(w, 6, 0f, 0f, 64f);
        var l = Layer.New(w);
        var stamp = Stamp.Box(16, 16, 60);
        for (var i = 0; i < 40; i++) World.Place(w, l, 32f, 32f, stamp, 16);

        World.Process(w);
        Assert.Equal(short.MaxValue, World.QueryMax(w, g, l, out _, out _));
        AssertMaxAgreesWithScan(w, g, l, 64);
    }

    [Fact]
    public void QueryMax_TracksEachGridAndLayerIndependently()
    {
        var w = World.New();
        var g1 = Grid.New(w, 8, 0f, 0f, 256f);
        var g2 = Grid.New(w, 7, 64f, 64f, 64f);
        var red = Layer.New(w);
        var blue = Layer.New(w);
        var rng = new Random(47);
        var stamp = Stamp.Box(10, 10, 35);
        for (var i = 0; i < 30; i++)
            World.Place(w, (i & 1) == 0 ? red : blue,
                (float)(rng.NextDouble() * 480 + 16),
                (float)(rng.NextDouble() * 480 + 16),
                stamp, rng.Next(1, 17));

        World.Process(w);
        foreach (var grid in new[] { g1, g2 })
        foreach (var layer in new[] { red, blue })
            AssertMaxAgreesWithScan(w, grid, layer, grid == g1 ? 256 : 128);
    }

    [Fact]
    public void QueryMax_PooledProcessMatchesScan()
    {
        var w = World.New();
        var g = Grid.New(w, 10, 0f, 0f, 1024f);
        var layers = new byte[3];
        for (var i = 0; i < layers.Length; i++) layers[i] = Layer.New(w);
        var stamp = Stamp.Box(20, 20, 25);
        var rng = new Random(53);
        var ids = new int[120];
        for (var round = 0; round < 4; round++)
        {
            for (var i = 0; i < ids.Length; i++)
            {
                var layer = layers[i % layers.Length];
                if (round > 0 && rng.Next(5) == 0)
                {
                    World.Remove(w, ids[i]);
                    ids[i] = World.Place(w, layer,
                        (float)(rng.NextDouble() * 1000 + 12),
                        (float)(rng.NextDouble() * 1000 + 12),
                        stamp, rng.Next(-8, 17));
                }
                else if (round > 0)
                {
                    World.Move(w, ids[i],
                        (float)(rng.NextDouble() * 1000 + 12),
                        (float)(rng.NextDouble() * 1000 + 12));
                }
                else
                {
                    ids[i] = World.Place(w, layer,
                        (float)(rng.NextDouble() * 1000 + 12),
                        (float)(rng.NextDouble() * 1000 + 12),
                        stamp, rng.Next(-8, 17));
                }
            }

            World.Process(w);
            foreach (var layer in layers) AssertMaxAgreesWithScan(w, g, layer, 1024);
        }
    }
}
