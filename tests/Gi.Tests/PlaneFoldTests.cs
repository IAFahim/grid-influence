using Xunit;

namespace Gi.Tests;

public unsafe class PlaneFoldTests
{
    private const float X = 128.5f;
    private const float Y = 64.5f;

    [Theory]
    [InlineData(1 << 21, 1 << 21)]
    [InlineData((1 << 21) + 5, 1 << 21)]
    public void BoxStacksPastInt32SaturateWithTheirSignAndCancelExactly(int positive, int negative)
    {
        var world = World.New();
        var grid = Grid.New(world, 8, 0f, 0f, 256f);
        var layer = Layer.New(world);
        var stamp = Stamp.Box(1, 1, -128);
        try
        {
            for (var i = 0; i < positive; i++) World.Place(world, layer, X, Y, stamp, -16);
            World.Process(world);
            Assert.Equal((short)32767, World.Query(world, grid, layer, 128, 64));

            for (var i = 0; i < negative; i++) World.Place(world, layer, X, Y, stamp, 16);
            World.Process(world);
            Assert.Equal((short)Math.Clamp((positive - negative) * 2048L, short.MinValue, short.MaxValue), World.Query(world, grid, layer, 128, 64));
        }
        finally
        {
            World.Free(world);
            Stamp.Free(stamp);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void TentAndBellStacksPastInt64SaturateAndCancelExactly(int kind)
    {
        var world = World.New();
        var grid = Grid.New(world, 8, 0f, 0f, 256f);
        var layer = Layer.New(world);
        var stamp = kind == 0 ? Stamp.Tent(1, 1, -128) : Stamp.Bell(1, 1, -128);
        try
        {
            var ids = new World.SourceId[1 << 13];
            for (var i = 0; i < ids.Length; i++) ids[i] = World.Place(world, layer, X, Y, stamp, -16);
            World.Process(world);
            Assert.Equal((short)32767, World.Query(world, grid, layer, 128, 64));

            for (var i = 0; i < ids.Length - 1; i++) World.Remove(world, ids[i]);
            World.Process(world);
            Assert.Equal((short)Math.Clamp(2048L, short.MinValue, short.MaxValue), World.Query(world, grid, layer, 128, 64));
        }
        finally
        {
            World.Free(world);
            Stamp.Free(stamp);
        }
    }

    [Fact]
    public void TileSummaryBlockFitsItsSlabUnit()
    {
        Assert.True(sizeof(TileSummary) <= TileSummary.Bytes);
    }
}

public class RoundWideTests
{
    [Fact]
    public void RoundWideEqualsRoundQ40ForEveryValueAndSplit()
    {
        var rng = new Random(77);
        for (var i = 0; i < 2_000_000; i++)
        {
            var value = rng.NextInt64(-(1L << 61), 1L << 61);
            if (i % 5 == 0) value = (rng.NextInt64(-1000, 1000) << 40) + rng.Next(-2, 3) + (rng.Next(2) == 0 ? 1L << 39 : -(1L << 39));
            var expected = TileBake.RoundQ40(value);
            Assert.Equal(expected, TileBake.RoundWide(0, value));
            var high = value >> 40;
            var low = value & ((1L << 40) - 1);
            Assert.Equal(expected, TileBake.RoundWide(high, low));
            var delta = rng.NextInt64(-(1L << 20), 1L << 20);
            Assert.Equal(expected, TileBake.RoundWide(high + delta, low - (delta << 40)));
        }
    }
}
