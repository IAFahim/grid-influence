using Xunit;

namespace Gi.Tests;

public unsafe class FieldViewTests
{
    [Theory]
    [InlineData(61)]
    [InlineData(62)]
    [InlineData(63)]
    public void ViewReadsEqualQueryOnEveryCellAndGradientAndWorldPoint(int seed)
    {
        var rng = new Random(seed);
        var w = World.New();
        var stamps = new List<byte> { Stamp.Box(5, 5, 40), Stamp.Tent(7, 7, -35), Stamp.Disk(5, 50, 360) };
        try
        {
            Grid.New(w, 7, 0f, 0f, 128f);
            Grid.New(w, 6, 20f, 24f, 64f);
            var a = Layer.New(w, Layer.Precision.Exact);
            var b = Layer.New(w, Layer.Precision.Cell);
            var sum = Layer.Sum(w, a, 2, b, -1, 0);
            for (var i = 0; i < 260; i++)
                World.Place(w, rng.Next(2) == 0 ? a : b, (float)(rng.NextDouble() * 120.0 + 4.0), (float)(rng.NextDouble() * 120.0 + 4.0),
                    stamps[rng.Next(stamps.Count)], (sbyte)rng.Next(-5, 6));
            World.Process(w);

            foreach (var layer in new[] { a, b, sum })
            foreach (byte grid in new byte[] { 0, 1 })
            {
                var view = World.View(w, grid, layer);
                var size = view.Size;
                Assert.True(size > 0);
                for (var y = -3; y < size + 3; y++)
                for (var x = -3; x < size + 3; x++)
                {
                    Assert.Equal(World.Query(w, grid, layer, x, y), view[x, y]);
                    view.Gradient(x, y, out var gx, out var gy);
                    Assert.Equal(World.Query(w, grid, layer, x + 1, y) - World.Query(w, grid, layer, x - 1, y), gx);
                    Assert.Equal(World.Query(w, grid, layer, x, y + 1) - World.Query(w, grid, layer, x, y - 1), gy);
                }

                for (var i = 0; i < 2000; i++)
                {
                    var x = (float)(rng.NextDouble() * 170.0 - 20.0);
                    var y = (float)(rng.NextDouble() * 170.0 - 20.0);
                    Assert.Equal(World.QueryAt(w, grid, layer, x, y), view.At(x, y));
                    var found = view.TryCell(x, y, out var cx, out var cy);
                    Grid.TryGetSpec(w, grid, out _, out var ox, out var oy, out var worldSize);
                    var dx = x - ox;
                    var dy = y - oy;
                    if (MathF.Abs(dx) > 1e-3f && MathF.Abs(dy) > 1e-3f && MathF.Abs(dx - worldSize) > 1e-3f && MathF.Abs(dy - worldSize) > 1e-3f)
                        Assert.Equal(dx >= 0f && dy >= 0f && dx < worldSize && dy < worldSize, found);
                    if (found) Assert.Equal(World.QueryAt(w, grid, layer, x, y), view[cx, cy]);
                    else Assert.Equal(0, World.QueryAt(w, grid, layer, x, y));
                }
            }

            Assert.Equal(0, World.View(w, 5, a).Size);
            Assert.Equal(0, World.View(w, 0, 99).Size);
            var freed = World.New();
            World.Free(freed);
            Assert.Equal(0, World.View(freed, 0, a).Size);
            Assert.Equal(0, World.View(freed, 0, a)[3, 3]);
        }
        finally
        {
            World.Free(w);
            foreach (var s in stamps) Stamp.Free(s);
        }
    }
}
