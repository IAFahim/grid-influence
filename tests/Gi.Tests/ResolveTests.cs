using Xunit;

namespace Gi.Tests;

public sealed class PlaneLimitTests
{
    private const float X = 128.5f;
    private const float Y = 64.5f;
    private const int CellX = 128;
    private const int CellY = 64;

    [Fact]
    public void BoxPlane_IsExactBelow2To20MaxAmplitudeSources_OnOneCell()
    {
        var world = World.New();
        var grid = Grid.New(world, 8, 0f, 0f, 256f);
        var layer = Layer.New(world);
        var stamp = Stamp.Box(1, 1, -128);
        for (var i = 0; i < (1 << 20) - 1; i++)
            World.Place(world, layer, X, Y, stamp, -16);
        World.Process(world);
        Assert.Equal((short)32767, World.Query(world, grid, layer, CellX, CellY));
        World.Free(world);
        Assert.True(Stamp.Free(stamp));
    }

    [Fact]
    public void BoxPlane_SaturatesAt2To20MaxAmplitudeSources_OnOneCell()
    {
        var world = World.New();
        var grid = Grid.New(world, 8, 0f, 0f, 256f);
        var layer = Layer.New(world);
        var stamp = Stamp.Box(1, 1, -128);
        for (var i = 0; i < 1 << 20; i++)
            World.Place(world, layer, X, Y, stamp, -16);
        World.Process(world);
        Assert.Equal((short)32767, World.Query(world, grid, layer, CellX, CellY));
        World.Free(world);
        Assert.True(Stamp.Free(stamp));
    }

    [Fact]
    public void TentPlane_IsExactBelow4096MaxAmplitudeSources_AtOnePeak()
    {
        var world = World.New();
        var grid = Grid.New(world, 8, 0f, 0f, 256f);
        var layer = Layer.New(world);
        var stamp = Stamp.Tent(1, 1, -128);
        for (var i = 0; i < 4095; i++)
            World.Place(world, layer, X, Y, stamp, -16);
        World.Process(world);
        Assert.Equal((short)32767, World.Query(world, grid, layer, CellX, CellY));
        World.Free(world);
        Assert.True(Stamp.Free(stamp));
    }

    [Fact]
    public void TentPlane_SaturatesAt4096MaxAmplitudeSources_AtOnePeak()
    {
        var world = World.New();
        var grid = Grid.New(world, 8, 0f, 0f, 256f);
        var layer = Layer.New(world);
        var stamp = Stamp.Tent(1, 1, -128);
        for (var i = 0; i < 4096; i++)
            World.Place(world, layer, X, Y, stamp, -16);
        World.Process(world);
        Assert.Equal((short)32767, World.Query(world, grid, layer, CellX, CellY));
        World.Free(world);
        Assert.True(Stamp.Free(stamp));
    }

    [Fact]
    public void BellPlane_IsExactBelow4096MaxAmplitudeSources_AtOnePeak()
    {
        var world = World.New();
        var grid = Grid.New(world, 8, 0f, 0f, 256f);
        var layer = Layer.New(world);
        var stamp = Stamp.Bell(1, 1, -128);
        for (var i = 0; i < 4095; i++)
            World.Place(world, layer, X, Y, stamp, -16);
        World.Process(world);
        Assert.Equal((short)32767, World.Query(world, grid, layer, CellX, CellY));
        World.Free(world);
        Assert.True(Stamp.Free(stamp));
    }

    [Fact]
    public void BellPlane_SaturatesAt4096MaxAmplitudeSources_AtOnePeak()
    {
        var world = World.New();
        var grid = Grid.New(world, 8, 0f, 0f, 256f);
        var layer = Layer.New(world);
        var stamp = Stamp.Bell(1, 1, -128);
        for (var i = 0; i < 4096; i++)
            World.Place(world, layer, X, Y, stamp, -16);
        World.Process(world);
        Assert.Equal((short)32767, World.Query(world, grid, layer, CellX, CellY));
        World.Free(world);
        Assert.True(Stamp.Free(stamp));
    }

    [Fact]
    public void ExcludedSaturatedCell_MatchesTheFieldWithoutTheSource_WhenBoxesAndDenseSumPastInt32()
    {
        const int each = 550_000;
        const float px = 128f;
        const float py = 64f;
        var world = World.New();
        var twin = World.New();
        var grids = new byte[2];
        var layers = new byte[2];
        var index = 0;
        foreach (var candidate in new[] { world, twin })
        {
            grids[index] = Grid.New(candidate, 8, 0f, 0f, 256f);
            layers[index] = Layer.New(candidate);
            index++;
        }

        var box = Stamp.Box(2, 2, 127);
        var raster = Stamp.New(new sbyte[] { 127, 127, 127, 126 }, 2, 2);
        var excluded = default(World.SourceId);
        for (var i = 0; i < each; i++)
        {
            var id = World.Place(world, layers[0], px, py, box, 16);
            if (i == 0) excluded = id;
            World.Place(world, layers[0], px, py, raster, 16);
            if (i > 0) World.Place(twin, layers[1], px, py, box, 16);
            World.Place(twin, layers[1], px, py, raster, 16);
        }

        World.Process(world);
        World.Process(twin);
        Assert.Equal((short)32767, World.Query(world, grids[0], layers[0], CellX, CellY));
        Assert.Equal((short)32767, World.Query(twin, grids[1], layers[1], CellX, CellY));

        Assert.True(World.TrySense(world, layers[0], px, py, excluded, out var excludedValue));
        Assert.True(World.TrySense(twin, layers[1], px, py, out var removedValue));
        Assert.Equal((short)32767, removedValue);
        Assert.Equal(removedValue, excludedValue);

        Assert.True(World.TrySenseArea(world, layers[0], px, py, 4f, excluded, out var excludedArea));
        Assert.True(World.TrySenseArea(twin, layers[1], px, py, 4f, out var removedArea));
        Assert.NotEqual(0L, removedArea);
        Assert.Equal(removedArea, excludedArea);

        World.Free(world);
        World.Free(twin);
        Assert.True(Stamp.Free(box));
        Assert.True(Stamp.Free(raster));
    }
}
