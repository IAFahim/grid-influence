using Xunit;

namespace Gi.Tests;

public sealed unsafe class ReferenceFieldTests
{
    [Fact]
    public void MinifiedRasterOutsidePaddedSampleRangeContributesZero()
    {
        var world = World.New();
        var stamp = Stamp.New(new sbyte[] { 11, 37, -23, 61 }, 2, 2);
        try
        {
            var grid = Grid.New(world, 5, 0f, 0f, 64f);
            var layer = Layer.New(world);
            var source = World.Place(world, layer, (2 * 256 + 222 + 192) / 128f, (2 * 256 + 222 + 192) / 128f, stamp,
                1);
            World.Scale(world, source, 1.5f);
            World.Process(world);
            Assert.Equal((short)0, World.Query(world, grid, layer, 2, 2));
            Assert.Equal((short)0, World.Query(world, grid, layer, 3, 2));
            Assert.Equal((short)0, World.Query(world, grid, layer, 2, 3));
            Assert.NotEqual((short)0, World.Query(world, grid, layer, 3, 3));
            Assert.True(World.TrySense(world, layer, 5f, 5f, source, out var excluded));
            Assert.Equal((short)0, excluded);
        }
        finally
        {
            World.Free(world);
            Assert.True(Stamp.Free(stamp));
        }
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(1.1f)]
    public void HugeBellFallbackMatchesIndependentRasterSamples(float radians)
    {
        var world = World.New();
        var handle = Stamp.Bell(128, 128, 51);
        try
        {
            var grid = new ReferenceGrid(32, 0, 0, 512);
            var gridHandle = Grid.New(world, 5, grid.X, grid.Y, grid.Extent);
            Layer.New(world);
            Layer.New(world);
            Layer.Sum(world, 0, 2, 1, -3, 1);
            Layer.Min(world, 0, 2);
            Layer.Max(world, 0, 1);
            Layer.Mask(world, 2, 4, -12, 80);
            var stamp = new ReferenceStamp(Kernel.Bell, 128, 128, 51, 360, Mips(128, 128, BellPixels(128, 128, 51)));
            var source = new Source { Stamp = stamp, X = 8.25f, Y = 7.875f, Radians = radians, Gain = -3 };
            source.Handle = World.Place(world, 0, source.X, source.Y, handle, (sbyte)source.Gain);
            World.Turn(world, source.Handle, radians);
            World.Process(world);
            CheckFrame(world, gridHandle, grid, [source], 0, 1);
        }
        finally
        {
            World.Free(world);
            Assert.True(Stamp.Free(handle));
        }
    }


    private static Pixels[] Mips(int width, int height, sbyte[] samples)
    {
        var levels = new List<Pixels> { new(width, height, samples) };
        while (width > 1 || height > 1)
        {
            var previous = levels[^1];
            width = (width + 1) / 2;
            height = (height + 1) / 2;
            samples = new sbyte[width * height];
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                samples[y * width + x] = (sbyte)RoundDivide(previous.At(2 * x, 2 * y) + previous.At(2 * x + 1, 2 * y) +
                                                            previous.At(2 * x, 2 * y + 1) +
                                                            previous.At(2 * x + 1, 2 * y + 1), 4);
            levels.Add(new Pixels(width, height, samples));
        }

        return levels.ToArray();
    }

    private static sbyte[] BellPixels(int width, int height, sbyte value)
    {
        var samples = new sbyte[width * height];
        var denominator = (long)width * width * height * height;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var dx = 2L * x + 1 - width;
            var dy = 2L * y + 1 - height;
            samples[y * width + x] =
                (sbyte)RoundDivide(value * ((long)width * width - dx * dx) * ((long)height * height - dy * dy),
                    denominator);
        }

        return samples;
    }

    private static void CheckFrame(byte world, byte handle, ReferenceGrid grid, List<Source> sources, int seed,
        int trial)
    {
        var prepared = sources.Select(source => new Prepared(source, grid)).ToArray();
        var expected = new short[6][];
        var actual = new short[grid.Cells * grid.Cells];
        for (var layer = 0; layer < expected.Length; layer++) expected[layer] = new short[actual.Length];
        for (var y = 0; y < grid.Cells; y++)
        for (var x = 0; x < grid.Cells; x++)
        {
            var cell = ReferenceCell(prepared, x, y, -1);
            for (var layer = 0; layer < expected.Length; layer++) expected[layer][y * grid.Cells + x] = cell[layer];
        }

        fixed (short* output = actual)
        {
            for (byte layer = 0; layer < expected.Length; layer++)
            {
                World.QueryRegion(world, handle, layer, 0, 0, grid.Cells, grid.Cells, output);
                if (expected[layer].AsSpan().SequenceEqual(actual)) continue;
                var index = 0;
                while (index < actual.Length && actual[index] == expected[layer][index]) index++;
                var descriptions = string.Join("\n",
                    prepared.Select((source, i) =>
                        $"{i}: {source.Source.Stamp.Kind} {source.Source.Stamp.Width}x{source.Source.Stamp.Height} value={source.Source.Stamp.Value} gain={source.Source.Gain} layer={source.Source.Layer} xy=({source.Source.X},{source.Source.Y}) angle={source.Source.Radians} scale={source.Source.Scale} arc={source.Source.Stamp.Arc} alive={source.Source.Alive} contribution={source.At(index % grid.Cells, index / grid.Cells)}"));
                Assert.Fail(
                    $"seed={seed} scenario={trial} grid={grid} layer={layer} cell=({index % grid.Cells},{index / grid.Cells}) expected={expected[layer][index]} actual={actual[index]}\n{descriptions}");
            }
        }

        for (var probe = 0; probe < 3; probe++)
        {
            var x = (trial * 17 + probe * 11) % grid.Cells;
            var y = (trial * 23 + probe * 7) % grid.Cells;
            var wx = grid.X + (x + 0.5f) * 256 / grid.Scale;
            var wy = grid.Y + (y + 0.5f) * 256 / grid.Scale;
            var excluded = probe == 0 ? -1 : probe % prepared.Length;
            var source = excluded < 0 ? default : prepared[excluded].Source.Handle;
            var point = ReferenceCell(prepared, x, y, excluded);
            var left = x == 0 ? default : ReferenceCell(prepared, x - 1, y, excluded);
            var right = x + 1 == grid.Cells ? default : ReferenceCell(prepared, x + 1, y, excluded);
            var below = y == 0 ? default : ReferenceCell(prepared, x, y - 1, excluded);
            var above = y + 1 == grid.Cells ? default : ReferenceCell(prepared, x, y + 1, excluded);
            for (byte layer = 0; layer < expected.Length; layer++)
            {
                Assert.True(World.TrySense(world, layer, wx, wy, source, out var sensed));
                Assert.Equal(point[layer], sensed);
                var complete = World.TrySenseGradient(world, layer, wx, wy, source, out var gx, out var gy);
                Assert.Equal(x > 0 && y > 0 && x + 1 < grid.Cells && y + 1 < grid.Cells, complete);
                Assert.Equal((right[layer] - left[layer]) * (grid.Scale / 512f), gx);
                Assert.Equal((above[layer] - below[layer]) * (grid.Scale / 512f), gy);
            }
        }

        if (trial % 10 != 0) return;
        var centreX = trial * 7 % grid.Cells;
        var centreY = trial * 11 % grid.Cells;
        var radius = trial % 13;
        var worldX = grid.X + (centreX + 0.25f) * 256 / grid.Scale;
        var worldY = grid.Y + (centreY + 0.75f) * 256 / grid.Scale;
        var reach = radius * 256f / grid.Scale;
        long total = 0;
        for (var y = 0; y < grid.Cells; y++)
        for (var x = 0; x < grid.Cells; x++)
        {
            var dx = x * 256L + 128 - (centreX * 256L + 64);
            var dy = y * 256L + 128 - (centreY * 256L + 192);
            if (dx * dx + dy * dy > radius * radius * 65536L && (x != centreX || y != centreY)) continue;
            total += ReferenceCell(prepared, x, y, 0)[2];
        }

        World.TrySenseArea(world, 2, worldX, worldY, reach, prepared[0].Source.Handle, out var area);
        Assert.Equal(RoundDivide(total * 65536, (long)grid.Scale * grid.Scale), area);
    }

    private static CellValues ReferenceCell(Prepared[] sources, int x, int y, int excluded)
    {
        Span<long> dense = stackalloc long[2];
        Span<long> tents = stackalloc long[2];
        Span<long> bells = stackalloc long[2];
        dense.Clear();
        tents.Clear();
        bells.Clear();
        for (var i = 0; i < sources.Length; i++)
        {
            if (i == excluded) continue;
            var source = sources[i];
            var layer = source.Source.Layer;
            var value = source.At(x, y);
            if (source.Kind == Kernel.Tent) tents[layer] += value;
            else if (source.Kind == Kernel.Bell) bells[layer] += value;
            else dense[layer] += value;
        }

        var a = Clamp(dense[0] + RoundDivide(tents[0], 1L << 40) + RoundDivide(bells[0], 1L << 40));
        var b = Clamp(dense[1] + RoundDivide(tents[1], 1L << 40) + RoundDivide(bells[1], 1L << 40));
        var sum = Clamp(RoundDivide(2L * a - 3L * b, 2));
        var min = Math.Min(a, sum);
        var max = Math.Max(a, b);
        return new CellValues(a, b, sum, min, max, max >= -12 && max <= 80 ? sum : (short)0);
    }

    private static short Clamp(long value)
    {
        return (short)Math.Clamp(value, short.MinValue, short.MaxValue);
    }

    private static long RoundDivide(long value, long denominator)
    {
        return value < 0 ? -((-value + denominator / 2) / denominator) : (value + denominator / 2) / denominator;
    }

    private static long FloorDivide(long value, long denominator)
    {
        return value >= 0 ? value / denominator : -((-value + denominator - 1) / denominator);
    }

    private static int BitLength(long value)
    {
        var length = 0;
        while (value != 0)
        {
            value >>= 1;
            length++;
        }

        return length;
    }

    private static long Sine(int angle)
    {
        var quadrant = (angle & 65535) / 16384;
        var position = angle & 16383;
        if ((quadrant & 1) != 0) position = 16384 - position;
        var squared = position * (long)position / 16384;
        var fifth = (squared * 1231) >> 14;
        var cubic = (squared * (-10583 + fifth)) >> 14;
        var value = (position * (25736 + cubic)) >> 14;
        return quadrant < 2 ? value : -value;
    }

    private static long IntegerRoot(long value)
    {
        var remainder = (ulong)value;
        var root = 0ul;
        var bit = 1ul << 62;
        while (bit > remainder) bit >>= 2;
        while (bit != 0)
        {
            if (remainder >= root + bit)
            {
                remainder -= root + bit;
                root = (root >> 1) + bit;
            }
            else
            {
                root >>= 1;
            }

            bit >>= 2;
        }

        return (long)root;
    }

    private enum Kernel : byte
    {
        Box,
        Raster,
        Tent,
        Bell,
        Disk,
        Cone
    }

    private sealed record Pixels(int Width, int Height, sbyte[] Values)
    {
        public int At(int x, int y)
        {
            return (uint)x < (uint)Width && (uint)y < (uint)Height ? Values[y * Width + x] : 0;
        }
    }

    private sealed record ReferenceStamp(Kernel Kind, int Width, int Height, sbyte Value, int Arc, Pixels[] Levels);

    private sealed class Source
    {
        public readonly bool Alive = true;
        public readonly int Layer = 0;
        public readonly int Scale = 256;
        public int Gain;
        public World.SourceId Handle;
        public float Radians;
        public required ReferenceStamp Stamp;
        public float X;
        public float Y;
    }

    private readonly record struct ReferenceGrid(int Cells, float X, float Y, int Scale)
    {
        public float Extent => Cells * 256f / Scale;
    }

    private readonly record struct CellValues(short A, short B, short Sum, short Min, short Max, short Mask)
    {
        public short this[int layer] => layer switch { 0 => A, 1 => B, 2 => Sum, 3 => Min, 4 => Max, _ => Mask };
    }

    private sealed class Prepared
    {
        public readonly Kernel Kind;
        public readonly Source Source;
        private readonly int _arc;
        private readonly long _arcCos;
        private readonly long _arcSin;
        private readonly bool _bell;
        private readonly long _centreX;
        private readonly long _centreY;
        private readonly long _cos;
        private readonly long _curve;
        private readonly long _extentX;
        private readonly long _extentY;
        private readonly long _halfX;
        private readonly long _halfY;
        private readonly long _leadX;
        private readonly long _leadY;
        private readonly int _level;
        private readonly int _sampling;
        private readonly int _shiftX;
        private readonly int _shiftY;
        private readonly long _sin;
        private readonly long _step;
        private readonly bool _turned;
        private readonly long _u0;
        private readonly long _ux;
        private readonly long _uy;
        private readonly long _v0;
        private readonly long _vx;
        private readonly long _vy;

        public Prepared(Source source, ReferenceGrid grid)
        {
            Source = source;
            var stamp = source.Stamp;
            _sampling = grid.Scale * source.Scale / 256;
            _leadX = (int)MathF.Floor((source.X - grid.X) * grid.Scale) +
                     FloorDivide(-stamp.Width * 128L * _sampling, 256);
            _leadY = (int)MathF.Floor((source.Y - grid.Y) * grid.Scale) +
                     FloorDivide(-stamp.Height * 128L * _sampling, 256);
            _extentX = stamp.Width * (long)_sampling;
            _extentY = stamp.Height * (long)_sampling;
            Kind = stamp.Kind == Kernel.Bell && (_extentX / 2 >= 32768 || _extentY / 2 >= 32768)
                ? Kernel.Raster
                : stamp.Kind;
            _centreX = 2 * _leadX + _extentX;
            _centreY = 2 * _leadY + _extentY;
            var turns = source.Radians / (2 * Math.PI);
            var angle = (int)((turns - Math.Floor(turns)) * 65536) & 65535;
            _cos = Sine(angle + 16384);
            _sin = Sine(angle);
            _turned = angle != 0 || Kind >= Kernel.Disk;
            _bell = Kind == Kernel.Bell;
            _arc = stamp.Arc == 360 ? 0 : stamp.Arc * 32768 / 360;
            _arcCos = Sine(_arc + 16384);
            _arcSin = Sine(_arc);
            if (Kind is Kernel.Tent or Kernel.Bell)
            {
                var halfX = Math.Max(256, _extentX / 2);
                var halfY = Math.Max(256, _extentY / 2);
                _shiftX = Math.Clamp(BitLength(halfX) - (_bell ? 8 : 15), 0, 7);
                _shiftY = Math.Clamp(BitLength(halfY) - (_bell ? 8 : 15), 0, 7);
                _halfX = halfX >> _shiftX;
                _halfY = halfY >> _shiftY;
                var peak = (_bell ? _halfX * _halfX : _halfX) * (_bell ? _halfY * _halfY : _halfY);
                _curve = ((1L << 40) + peak / 2) / peak;
            }

            if (Kind != Kernel.Raster) return;
            _step = (256L << 16) / _sampling;
            while (_level + 1 < stamp.Levels.Length && _step >> (_level + 1) >= 65536) _level++;
            _ux = FloorDivide(_cos << 33, _sampling);
            _uy = FloorDivide(_sin << 33, _sampling);
            _vx = FloorDivide(-_sin << 33, _sampling);
            _vy = FloorDivide(_cos << 33, _sampling);
            _u0 = ((long)stamp.Width << 47) - FloorDivide(1L << 55, _sampling);
            _v0 = ((long)stamp.Height << 47) - FloorDivide(1L << 55, _sampling);
        }

        public long At(int x, int y)
        {
            if (!Source.Alive || Source.Gain == 0 || _sampling == 0) return 0;
            var stamp = Source.Stamp;
            var dx = x * 512L + 256 - _centreX;
            var dy = y * 512L + 256 - _centreY;
            var u = (dx * _cos + dy * _sin) >> 14;
            var v = (dy * _cos - dx * _sin) >> 14;
            if (Kind == Kernel.Box)
            {
                var wx = _turned
                    ? Math.Clamp(Math.Min(_extentX, u + 256) - Math.Max(-_extentX, u - 256), 0, 512) / 2
                    : Math.Clamp(Math.Min(_leadX + _extentX, (x + 1L) * 256) - Math.Max(_leadX, x * 256L), 0, 256);
                var wy = _turned
                    ? Math.Clamp(Math.Min(_extentY, v + 256) - Math.Max(-_extentY, v - 256), 0, 512) / 2
                    : Math.Clamp(Math.Min(_leadY + _extentY, (y + 1L) * 256) - Math.Max(_leadY, y * 256L), 0, 256);
                return RoundDivide(stamp.Value * wx * wy, 1L << 16) * Source.Gain;
            }

            if (Kind is Kernel.Tent or Kernel.Bell)
            {
                var du = _turned
                    ? u >> (_shiftX + 1)
                    : ((x * 256L + 128) >> _shiftX) - ((_leadX + _extentX / 2) >> _shiftX);
                var dv = _turned
                    ? v >> (_shiftY + 1)
                    : ((y * 256L + 128) >> _shiftY) - ((_leadY + _extentY / 2) >> _shiftY);
                var wx = _bell ? _halfX * _halfX - du * du : _halfX - Math.Abs(du);
                var wy = _bell ? _halfY * _halfY - dv * dv : _halfY - Math.Abs(dv);
                return wx <= 0 || wy <= 0 ? 0 : stamp.Value * (long)Source.Gain * _curve * wx * wy;
            }

            if (Kind == Kernel.Raster)
            {
                long tx, ty;
                if (_turned)
                {
                    tx = (dx * _ux + dy * _uy + _u0) >> 32 >> _level;
                    ty = (dx * _vx + dy * _vy + _v0) >> 32 >> _level;
                }
                else
                {
                    var px = _leadX >> 8;
                    var py = _leadY >> 8;
                    if (x < px || y < py || x * 256L >= _leadX + _extentX || y * 256L >= _leadY + _extentY) return 0;
                    tx = (-(_leadX & 255) * 65536 / _sampling + (x - px) * _step) >> _level;
                    ty = (-(_leadY & 255) * 65536 / _sampling + (y - py) * _step) >> _level;
                }

                var pixels = stamp.Levels[_level];
                var ix = (int)(tx >> 16);
                var iy = (int)(ty >> 16);
                if (ix < -1 || iy < -1 || ix >= pixels.Width || iy >= pixels.Height) return 0;
                var fx = (tx >> 8) & 255;
                var fy = (ty >> 8) & 255;
                var top = pixels.At(ix, iy) * (256 - fx) + pixels.At(ix + 1, iy) * fx;
                var bottom = pixels.At(ix, iy + 1) * (256 - fx) + pixels.At(ix + 1, iy + 1) * fx;
                return RoundDivide(top * (256 - fy) + bottom * fy, 1L << 16) * Source.Gain;
            }

            var reach = Kind == Kernel.Disk ? _extentX : Math.Max(_extentX, 512);
            var squared = dx * dx + dy * dy;
            long weight;
            if (Kind == Kernel.Disk)
            {
                if (squared >= (reach + 256) * (reach + 256)) return 0;
                var covered = Math.Clamp(reach - IntegerRoot(squared) + 256, 0, Math.Min(512, 2 * reach));
                weight = covered * 128;
            }
            else
            {
                var radiusSquared = reach * reach;
                if (squared >= radiusSquared) return 0;
                if (Kind == Kernel.Cone)
                {
                    weight = ((reach - IntegerRoot(squared)) * (((1L << 48) + reach - 1) / reach)) >> 32;
                }
                else
                {
                    var reduction = Math.Max(0, BitLength(radiusSquared) - 46);
                    var denominator = radiusSquared >> reduction;
                    var inverse = ((1L << 62) + denominator - 1) / denominator;
                    weight = (((radiusSquared - squared) >> reduction) * inverse) >> 46;
                }
            }

            var coverage = 256L;
            if (_arc != 0)
            {
                var signedDistance = (Math.Abs(v) * _arcCos - u * _arcSin) >> 14;
                coverage = Math.Clamp(256 - signedDistance, 0, 512) / 2;
            }

            return RoundDivide(stamp.Value * weight * coverage, 1L << 24) * Source.Gain;
        }
    }
}
