using Xunit;

namespace Gi.Tests;

public class EmptyTileRetentionTests
{
    [Theory]
    [InlineData(0, 3)]
    [InlineData(0, 5)]
    [InlineData(1, 3)]
    [InlineData(1, 6)]
    public void SubUnitResiduesSurviveAPublishThatLeavesTheTileEmpty(int kind, int later)
    {
        var split = Read(kind, later, processBetween: true);
        var together = Read(kind, later, processBetween: false);
        Assert.Equal(together, split);
        Assert.Contains(split, value => value > 0);
    }

    private static int[] Read(int kind, int later, bool processBetween)
    {
        var w = World.New();
        var stamp = kind == 0 ? Stamp.Tent(16, 16, 1) : Stamp.Bell(16, 16, 1);
        try
        {
            Grid.New(w, 6, 0f, 0f, 64f);
            var layer = Layer.New(w);
            World.Place(w, layer, 27.5f, 20.5f, stamp, 1);
            if (processBetween) World.Process(w);
            for (var i = 0; i < later; i++) World.Place(w, layer, 27.5f, 20.5f, stamp, 1);
            World.Process(w);
            var values = new int[12];
            for (var x = 0; x < values.Length; x++) values[x] = World.Query(w, 0, layer, 26 + x, 20);
            return values;
        }
        finally
        {
            World.Free(w);
            Stamp.Free(stamp);
        }
    }
}
