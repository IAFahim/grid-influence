using Gi.Validation;
using Xunit;

namespace Gi.Tests;

public class CapTests
{
    [Fact]
    public void FootprintsPastFiveHundredTwelveCellsAreRejectedAtPlaceAndScale()
    {
        var world = World.New();
        var big = Stamp.Box(256, 256, 1);
        var medium = Stamp.Box(100, 100, 1);
        try
        {
            Grid.New(world, 10, 0f, 0f, 256f);
            var layer = Layer.New(world);
            World.Place(world, layer, 100f, 100f, big, 1, out var status);
            Assert.Equal(World.WriteStatus.InvalidStamp, status);

            var id = World.Place(world, layer, 128f, 128f, medium, 1, out status);
            Assert.Equal(World.WriteStatus.Applied, status);
            Assert.Equal(World.WriteStatus.Applied, World.Scale(world, id, 1.25f));
            Assert.Equal(World.WriteStatus.CapacityReached, World.Scale(world, id, 1.4f));
            World.Process(world);
            Assert.Equal(World.WriteStatus.Applied, World.Scale(world, id, 1f));
        }
        finally
        {
            World.Free(world);
            Stamp.Free(big);
            Stamp.Free(medium);
        }
    }

    [Fact]
    public void ReachPastFourThousandNinetySixCellsIsRejected()
    {
        var world = World.New();
        var stamp = Stamp.Box(4, 4, 3);
        try
        {
            Grid.New(world, 14, 0f, 0f, 16384f);
            var layer = Layer.New(world);
            World.Place(world, layer, 8000f, 8000f, stamp, 1);
            World.Process(world);
            Assert.True(World.TrySenseArea(world, layer, 8000f, 8000f, 4096f, out var total));
            Assert.True(total > 0);
            Assert.False(World.TrySenseArea(world, layer, 8000f, 8000f, 4096.5f, out total));
            Assert.Equal(0, total);
            Assert.False(World.TrySenseMax(world, layer, 8000f, 8000f, 5000f, out var value, out _, out _));
            Assert.Equal(0, value);
            Assert.False(World.Changed(world, layer, 8000f, 8000f, 5000f, -1));
        }
        finally
        {
            World.Free(world);
            Stamp.Free(stamp);
        }
    }

    [Fact]
    public void ValidationMirrorsTheCaps()
    {
        Assert.True(GiValidation.TryFootprint(512, 512, out _));
        Assert.False(GiValidation.TryFootprint(513, 4, out var error));
        Assert.NotNull(error);
        Assert.True(GiValidation.TryReach(4096f, out _));
        Assert.False(GiValidation.TryReach(4096.5f, out _));
        Assert.False(GiValidation.TryReach(float.NaN, out _));
    }
}
