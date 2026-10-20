using Xunit;

namespace Gi.Tests;

public unsafe class RegionQueryTests
{
    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    public void RegionAndRectangleSumsEqualPerCellQueriesAtAnyEdge(int seed, bool derived)
    {
        var rng = new Random(seed);
        var world = World.New();
        var box = Stamp.Box(5, 7, 11);
        var tent = Stamp.Tent(12, 12, 30);
        try
        {
            var grid = Grid.New(world, 7, 0f, 0f, 128f);
            var a = Layer.New(world, Layer.Precision.Cell);
            var b = Layer.New(world, Layer.Precision.Cell);
            var layer = derived ? Layer.Sum(world, a, 1, b, -1) : a;
            for (var i = 0; i < 120; i++)
                World.Place(world, rng.Next(2) == 0 ? a : b, rng.Next(0, 128) + 0.5f, rng.Next(0, 128) + 0.5f,
                    rng.Next(2) == 0 ? box : tent, (sbyte)rng.Next(-3, 4));
            World.Process(world);

            for (var trial = 0; trial < 400; trial++)
            {
                var x = rng.Next(-40, 150);
                var y = rng.Next(-40, 150);
                var width = rng.Next(1, 160);
                var height = rng.Next(1, 160);
                var region = new short[width * height];
                fixed (short* p = region)
                {
                    World.QueryRegion(world, grid, layer, x, y, width, height, p);
                }

                long expectedSum = 0;
                for (var row = 0; row < height; row++)
                for (var col = 0; col < width; col++)
                {
                    var expected = World.Query(world, grid, layer, x + col, y + row);
                    Assert.True(region[row * width + col] == expected,
                        $"({x},{y}) {width}x{height} cell ({col},{row})");
                    expectedSum += expected;
                }

                Assert.Equal(expectedSum, World.Query(world, grid, layer, x, y, width, height));
            }
        }
        finally
        {
            World.Free(world);
            Stamp.Free(box);
            Stamp.Free(tent);
        }
    }
}
