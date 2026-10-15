using Xunit;

namespace Gi.Tests;

public sealed unsafe class ColumnBudgetTests : IDisposable
{
    private readonly byte _world = World.New();
    private readonly byte _grid;
    private readonly byte _layer;
    private readonly byte _stamp = Stamp.Box(7, 5, 37);

    public ColumnBudgetTests()
    {
        _grid = Grid.New(_world, 7, 0f, 0f, 128f);
        _layer = Layer.New(_world);
    }

    public void Dispose()
    {
        World.Free(_world);
        Assert.True(Stamp.Free(_stamp));
    }

    [Theory]
    [InlineData(8193)]
    [InlineData(8209)]
    [InlineData(8255)]
    [InlineData(9217)]
    [InlineData(10001)]
    public void PooledColumnChunksKeepEveryMaskBitForUnevenCounts(int count)
    {
        var source = World.Place(_world, _layer, 8.5f, 8.5f, _stamp, 1);
        World.Process(_world);
        var since = World.Tick(_world);
        World.Move(_world, source, 9.5f, 9.5f);
        World.Process(_world);
        Assert.True(World.Changed(_world, _layer, 9.5f, 9.5f, 0f, since));

        var xs = new float[count];
        var ys = new float[count];
        var values = new short[count];
        var words = new ulong[(count + 63) >> 6];
        Array.Fill(xs, 9.5f);
        Array.Fill(ys, 9.5f);
        var previousSerial = ResolvePool.SerialOnly;
        ResolvePool.SerialOnly = false;
        try
        {
            fixed (float* xp = xs, yp = ys)
            fixed (short* vp = values)
            fixed (ulong* wp = words)
            {
                for (var repeat = 0; repeat < 32; repeat++)
                {
                    Array.Fill(words, ulong.MaxValue);
                    Assert.True(World.SenseColumn(_world, _layer, xp, yp, count, vp, wp));
                    AssertFullMask(words, count);
                    Array.Fill(words, ulong.MaxValue);
                    Assert.Equal(count, World.ChangedMask(_world, _layer, xp, yp, 0f, since, count, wp));
                    AssertFullMask(words, count);
                }
            }
        }
        finally
        {
            ResolvePool.SerialOnly = previousSerial;
        }

        Assert.All(values, value => Assert.Equal((short)37, value));
    }

    [Fact]
    public void ColumnMasksOverwriteEarlierResultsAndClearPadding()
    {
        const int count = 65;
        var xs = stackalloc float[count];
        var ys = stackalloc float[count];
        var excludes = stackalloc int[count];
        var values = stackalloc short[count];
        var gx = stackalloc float[count];
        var gy = stackalloc float[count];
        var words = stackalloc ulong[3];
        for (var i = 0; i < count; i++)
        {
            xs[i] = i % 3 == 0 ? -1f : 1.5f;
            ys[i] = 1.5f;
            excludes[i] = -1;
        }

        for (var mode = 0; mode < 3; mode++)
        {
            words[0] = words[1] = words[2] = ulong.MaxValue;
            var complete = mode == 0
                ? World.SenseColumn(_world, _layer, xs, ys, count, values, words)
                : mode == 1
                    ? World.SenseColumn(_world, _layer, xs, ys, excludes, count, values, words)
                    : World.SenseGradientColumn(_world, _layer, xs, ys, excludes, count, gx, gy, words);
            Assert.False(complete);
            for (var i = 0; i < count; i++)
                Assert.Equal(i % 3 != 0, (words[i >> 6] & (1ul << (i & 63))) != 0);
            Assert.Equal(1ul, words[1]);
            Assert.Equal(ulong.MaxValue, words[2]);
        }

        words[0] = words[1] = ulong.MaxValue;
        Assert.Equal(0, World.ChangedMask(_world, _layer, xs, ys, 0f, 0, count, words));
        Assert.Equal(0ul, words[0]);
        Assert.Equal(0ul, words[1]);
        Assert.Equal(ulong.MaxValue, words[2]);
        Assert.False(World.SenseColumn(_world, _layer, xs, ys, count, values, null));
        Assert.False(World.SenseGradientColumn(_world, _layer, xs, ys, null, count, gx, gy, null));
    }

    [Fact]
    public void NullColumnInputsAreRejectedBeforeOutputsChange()
    {
        float x = 1.5f, y = 1.5f, gx = 123f, gy = 456f;
        short value = 789;
        ulong words = ulong.MaxValue;
        Assert.False(World.SenseColumn(_world, _layer, null, &y, 1, &value, &words));
        Assert.False(World.SenseColumn(_world, _layer, &x, null, 1, &value, &words));
        Assert.False(World.SenseColumn(_world, _layer, &x, &y, 1, null, &words));
        Assert.False(World.SenseColumn(_world, _layer, null, &y, null, 1, &value, &words));
        Assert.False(World.SenseGradientColumn(_world, _layer, &x, null, null, 1, &gx, &gy, &words));
        Assert.False(World.SenseGradientColumn(_world, _layer, &x, &y, null, 1, null, &gy, &words));
        Assert.False(World.SenseGradientColumn(_world, _layer, &x, &y, null, 1, &gx, null, &words));
        Assert.Equal(0, World.ChangedMask(_world, _layer, null, &y, 1f, 0, 1, &words));
        Assert.Equal(0, World.ChangedMask(_world, _layer, &x, null, 1f, 0, 1, &words));
        Assert.Equal(0, World.ChangedMask(_world, _layer, &x, &y, 1f, 0, 1, null));
        Assert.True(World.SenseColumn(_world, _layer, null, null, 0, null, null));
        Assert.True(World.SenseColumn(_world, _layer, null, null, null, -1, null, null));
        Assert.True(World.SenseGradientColumn(_world, _layer, null, null, null, 0, null, null, null));
        Assert.Equal((short)789, value);
        Assert.Equal(123f, gx);
        Assert.Equal(456f, gy);
        Assert.Equal(ulong.MaxValue, words);
    }

    [Fact]
    public void NonfiniteColumnQueriesReturnZeroAndClearTheirMaskBits()
    {
        var xs = stackalloc float[5] { float.NaN, 1.5f, float.PositiveInfinity, 1.5f, 1.5f };
        var ys = stackalloc float[5] { 1.5f, float.NaN, 1.5f, float.NegativeInfinity, 1.5f };
        var values = stackalloc short[5];
        var gx = stackalloc float[5];
        var gy = stackalloc float[5];
        ulong words = ulong.MaxValue;
        Assert.False(World.SenseColumn(_world, _layer, xs, ys, 5, values, &words));
        Assert.Equal(16ul, words);
        words = ulong.MaxValue;
        Assert.False(World.SenseGradientColumn(_world, _layer, xs, ys, null, 5, gx, gy, &words));
        Assert.Equal(16ul, words);
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal((short)0, values[i]);
            Assert.Equal(0f, gx[i]);
            Assert.Equal(0f, gy[i]);
        }
    }

    [Fact]
    public void ColumnPointCoordinatesKeepFloorSemanticsAtAdjacentFloats()
    {
        var raster = Stamp.New(new sbyte[] { 17, 61, -42, 99 }, 2, 2);
        try
        {
            World.Place(_world, _layer, 32f, 32f, raster, 1);
            World.Process(_world);
            float[] coordinates = [float.BitDecrement(0f), 0f, float.BitIncrement(0f),
                float.BitDecrement(31f), 31f, float.BitIncrement(31f),
                float.BitDecrement(32f), 32f, float.BitIncrement(32f),
                float.BitDecrement(128f), 128f, float.BitIncrement(128f), float.NaN, float.NegativeInfinity, float.PositiveInfinity];
            var values = new short[coordinates.Length];
            var ys = new float[coordinates.Length];
            Array.Fill(ys, 32f);
            var words = new ulong[1];
            fixed (float* xp = coordinates, yp = ys)
            fixed (short* vp = values)
            fixed (ulong* wp = words)
                World.SenseColumn(_world, _layer, xp, yp, values.Length, vp, wp);
            for (var i = 0; i < coordinates.Length; i++)
            {
                var coordinate = coordinates[i];
                var covered = float.IsFinite(coordinate) && coordinate >= 0f && coordinate < 128f;
                Assert.Equal(covered, (words[0] & (1ul << i)) != 0);
                var expected = covered ? World.Query(_world, _grid, _layer, (int)MathF.Floor(coordinate), 32) : (short)0;
                Assert.Equal(expected, values[i]);
            }
        }
        finally
        {
            World.Clear(_world);
            Assert.True(Stamp.Free(raster));
        }
    }

    [Fact]
    public void SyncColumnsRejectNonfinitePositionsAndNullBuffers()
    {
        var first = World.Place(_world, _layer, 10.5f, 11.5f, _stamp, 1);
        var second = World.Place(_world, _layer, 20.5f, 21.5f, _stamp, 1);
        World.Process(_world);
        var sources = stackalloc int[2] { first, second };
        var xs = stackalloc float[2] { float.NaN, 30.5f };
        var ys = stackalloc float[2] { 31.5f, float.PositiveInfinity };
        World.Sync(_world, sources, xs, ys, 2);
        Assert.True(World.TryGetSourceSpec(_world, first, out var a));
        Assert.True(World.TryGetSourceSpec(_world, second, out var b));
        Assert.Equal(10.5f, a.X);
        Assert.Equal(11.5f, a.Y);
        Assert.Equal(20.5f, b.X);
        Assert.Equal(21.5f, b.Y);

        xs[0] = 32.5f;
        ys[1] = 33.5f;
        World.Sync(_world, null, xs, ys, 2);
        World.Sync(_world, sources, null, ys, 2);
        World.Sync(_world, sources, xs, null, 2);
        World.Sync(_world, sources, xs, ys, 2);
        Assert.True(World.TryGetSourceSpec(_world, first, out a));
        Assert.True(World.TryGetSourceSpec(_world, second, out b));
        Assert.Equal(32.5f, a.X);
        Assert.Equal(31.5f, a.Y);
        Assert.Equal(30.5f, b.X);
        Assert.Equal(33.5f, b.Y);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GradientAndPointColumnsMatchScalarAcrossGridAndTileEdges(bool multipleGrids)
    {
        if (multipleGrids) Grid.New(_world, 7, 32f, 32f, 64f);
        var other = Layer.New(_world);
        var mixed = Layer.Sum(_world, _layer, 2, other, -3);
        var masked = Layer.Mask(_world, mixed, _layer, 1, short.MaxValue);
        var first = World.Place(_world, _layer, 32.5f, 31.5f, _stamp, 5);
        var second = World.Place(_world, other, 31.5f, 32.5f, _stamp, -2);
        var removed = World.Place(_world, _layer, 64.5f, 64.5f, _stamp, 3);
        World.Process(_world);
        World.Move(_world, first, 63.5f, 63.5f);
        World.Remove(_world, removed);
        var fresh = World.Place(_world, _layer, 95.5f, 95.5f, _stamp, 7);

        float[] axis = [-1f, 0.25f, 1.25f, 30.5f, 31.5f, 32.25f, 32.5f, 33.5f, 62.5f, 63.5f, 64.5f, 94.75f, 95.75f, 96f, 127.5f, 128f];
        var count = axis.Length * axis.Length;
        var xs = new float[count];
        var ys = new float[count];
        var excludes = new int[count];
        var values = new short[count];
        var gx = new float[count];
        var gy = new float[count];
        var words = new ulong[(count + 63) >> 6];
        for (var i = 0; i < count; i++)
        {
            xs[i] = axis[i % axis.Length];
            ys[i] = axis[i / axis.Length];
        }

        fixed (float* xp = xs, yp = ys, gxp = gx, gyp = gy)
        fixed (int* ep = excludes)
        fixed (short* vp = values)
        fixed (ulong* wp = words)
        {
            for (var processed = 0; processed < 2; processed++)
            {
                foreach (var layer in new[] { _layer, mixed, masked })
                foreach (var exclude in new[] { -1, first, second, removed, fresh, int.MaxValue })
                {
                    Array.Fill(excludes, exclude);
                    World.SenseColumn(_world, layer, xp, yp, ep, count, vp, wp);
                    for (var i = 0; i < count; i++)
                    {
                        var complete = World.TrySense(_world, layer, xs[i], ys[i], exclude, out var scalar);
                        Assert.Equal(scalar, values[i]);
                        Assert.Equal(complete, (words[i >> 6] & (1ul << (i & 63))) != 0);
                    }

                    World.SenseGradientColumn(_world, layer, xp, yp, ep, count, gxp, gyp, wp);
                    for (var i = 0; i < count; i++)
                    {
                        var complete = World.TrySenseGradient(_world, layer, xs[i], ys[i], exclude, out var scalarX, out var scalarY);
                        Assert.Equal(scalarX, gx[i]);
                        Assert.Equal(scalarY, gy[i]);
                        Assert.Equal(complete, (words[i >> 6] & (1ul << (i & 63))) != 0);
                        if (multipleGrids || exclude >= 0 || xs[i] < 0f || ys[i] < 0f || xs[i] >= 128f || ys[i] >= 128f) continue;
                        var cx = (int)xs[i];
                        var cy = (int)ys[i];
                        Assert.Equal((World.Query(_world, _grid, layer, cx + 1, cy) - World.Query(_world, _grid, layer, cx - 1, cy)) * 0.5f, gx[i]);
                        Assert.Equal((World.Query(_world, _grid, layer, cx, cy + 1) - World.Query(_world, _grid, layer, cx, cy - 1)) * 0.5f, gy[i]);
                    }
                }

                World.Process(_world);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WarmPointGradientAndChangedColumnsAllocateZeroBytes(bool pooled)
    {
        var count = pooled ? 16387 : 65;
        var source = World.Place(_world, _layer, 32.5f, 31.5f, _stamp, 2);
        World.Process(_world);
        var xs = new float[count];
        var ys = new float[count];
        var excludes = new int[count];
        var values = new short[count];
        var gx = new float[count];
        var gy = new float[count];
        var words = new ulong[(count + 63) >> 6];
        for (var i = 0; i < count; i++)
        {
            xs[i] = 1.5f + i % 125;
            ys[i] = 1.5f + i % 123;
            excludes[i] = i % 2 == 0 ? source : -1;
        }

        var previousSerial = ResolvePool.SerialOnly;
        ResolvePool.SerialOnly = !pooled;
        try
        {
            fixed (float* xp = xs, yp = ys, gxp = gx, gyp = gy)
            fixed (int* ep = excludes)
            fixed (short* vp = values)
            fixed (ulong* wp = words)
            {
                for (var repeat = 0; repeat < 16; repeat++)
                {
                    World.SenseColumn(_world, _layer, xp, yp, count, vp, wp);
                    World.SenseColumn(_world, _layer, xp, yp, ep, count, vp, wp);
                    World.SenseGradientColumn(_world, _layer, xp, yp, ep, count, gxp, gyp, wp);
                    World.ChangedMask(_world, _layer, xp, yp, 0f, 0, count, wp);
                }

                var complete = true;
                var hits = 0;
                var before = GC.GetAllocatedBytesForCurrentThread();
                for (var repeat = 0; repeat < 16; repeat++)
                {
                    complete &= World.SenseColumn(_world, _layer, xp, yp, count, vp, wp);
                    complete &= World.SenseColumn(_world, _layer, xp, yp, ep, count, vp, wp);
                    complete &= World.SenseGradientColumn(_world, _layer, xp, yp, ep, count, gxp, gyp, wp);
                    hits += World.ChangedMask(_world, _layer, xp, yp, 0f, 0, count, wp);
                }

                var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                Assert.True(complete);
                Assert.True(hits > 0);
                Assert.Equal(0L, allocated);
            }
        }
        finally
        {
            ResolvePool.SerialOnly = previousSerial;
        }
    }

    private static void AssertFullMask(ulong[] words, int count)
    {
        for (var i = 0; i < words.Length; i++)
        {
            var remainder = count & 63;
            var expected = i == words.Length - 1 && remainder != 0 ? (1ul << remainder) - 1 : ulong.MaxValue;
            Assert.Equal(expected, words[i]);
        }
    }
}
