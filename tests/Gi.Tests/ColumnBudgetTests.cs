using Xunit;

namespace Gi.Tests;

public sealed unsafe class ColumnBudgetTests : IDisposable
{
    private readonly byte _grid;
    private readonly byte _layer;
    private readonly byte _stamp = Stamp.Box(7, 5, 37);
    private readonly byte _world = World.New();

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

    [Fact]
    public void ColumnMasksOverwriteEarlierResultsAndClearPadding()
    {
        const int count = 65;
        var xs = stackalloc float[count];
        var ys = stackalloc float[count];
        var excludes = stackalloc World.SourceId[count];
        var values = stackalloc short[count];
        var gx = stackalloc float[count];
        var gy = stackalloc float[count];
        var words = stackalloc ulong[3];
        for (var i = 0; i < count; i++)
        {
            xs[i] = i % 3 == 0 ? -1f : 1.5f;
            ys[i] = 1.5f;
            excludes[i] = default;
        }

        for (var mode = 0; mode < 3; mode++)
        {
            words[0] = words[1] = words[2] = ulong.MaxValue;
            var complete = mode == 0
                ? World.SenseColumn(_world, _layer, xs, ys, count, values, words) == World.ReadStatus.Complete
                : mode == 1
                    ? World.SenseColumn(_world, _layer, xs, ys, excludes, count, values, words) ==
                      World.ReadStatus.Complete
                    : World.SenseGradientColumn(_world, _layer, xs, ys, excludes, count, gx, gy, words) ==
                      World.ReadStatus.Complete;
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
        Assert.False(World.SenseColumn(_world, _layer, xs, ys, count, values, null) == World.ReadStatus.Complete);
        Assert.False(World.SenseGradientColumn(_world, _layer, xs, ys, null, count, gx, gy, null) ==
                     World.ReadStatus.Complete);
    }

    [Fact]
    public void ColumnPointCoordinatesKeepFloorSemanticsAtAdjacentFloats()
    {
        var raster = Stamp.New(new sbyte[] { 17, 61, -42, 99 }, 2, 2);
        try
        {
            World.Place(_world, _layer, 32f, 32f, raster, 1);
            World.Process(_world);
            float[] coordinates =
            [
                float.BitDecrement(0f), 0f, float.BitIncrement(0f),
                float.BitDecrement(31f), 31f, float.BitIncrement(31f),
                float.BitDecrement(32f), 32f, float.BitIncrement(32f),
                float.BitDecrement(128f), 128f, float.BitIncrement(128f), float.NaN, float.NegativeInfinity,
                float.PositiveInfinity
            ];
            var values = new short[coordinates.Length];
            var ys = new float[coordinates.Length];
            Array.Fill(ys, 32f);
            var words = new ulong[1];
            fixed (float* xp = coordinates, yp = ys)
            fixed (short* vp = values)
            fixed (ulong* wp = words)
            {
                World.SenseColumn(_world, _layer, xp, yp, values.Length, vp, wp);
            }

            for (var i = 0; i < coordinates.Length; i++)
            {
                var coordinate = coordinates[i];
                var covered = float.IsFinite(coordinate) && coordinate >= 0f && coordinate < 128f;
                Assert.Equal(covered, (words[0] & (1ul << i)) != 0);
                var expected =
                    covered ? World.Query(_world, _grid, _layer, (int)MathF.Floor(coordinate), 32) : (short)0;
                Assert.Equal(expected, values[i]);
            }
        }
        finally
        {
            World.Clear(_world);
            Assert.True(Stamp.Free(raster));
        }
    }

    [Theory]
    [InlineData(65)]
    [InlineData(16387)]
    public void WarmPointGradientAndChangedColumnsAllocateZeroBytes(int count)
    {
        var source = World.Place(_world, _layer, 32.5f, 31.5f, _stamp, 2);
        World.Process(_world);
        var xs = new float[count];
        var ys = new float[count];
        var excludes = new World.SourceId[count];
        var values = new short[count];
        var gx = new float[count];
        var gy = new float[count];
        var words = new ulong[(count + 63) >> 6];
        for (var i = 0; i < count; i++)
        {
            xs[i] = 1.5f + i % 125;
            ys[i] = 1.5f + i % 123;
            excludes[i] = i % 2 == 0 ? source : default;
        }

        fixed (float* xp = xs, yp = ys, gxp = gx, gyp = gy)
        fixed (World.SourceId* ep = excludes)
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
                complete &= World.SenseColumn(_world, _layer, xp, yp, count, vp, wp) == World.ReadStatus.Complete;
                complete &= World.SenseColumn(_world, _layer, xp, yp, ep, count, vp, wp) == World.ReadStatus.Complete;
                complete &= World.SenseGradientColumn(_world, _layer, xp, yp, ep, count, gxp, gyp, wp) ==
                            World.ReadStatus.Complete;
                hits += World.ChangedMask(_world, _layer, xp, yp, 0f, 0, count, wp);
            }

            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(complete);
            Assert.True(hits > 0);
            Assert.Equal(0L, allocated);
        }
    }
}
