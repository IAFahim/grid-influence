using Xunit;

namespace Gi.Tests;

public class SenseMaxTests
{
    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    public void DiskMaxMatchesABruteForceScanWithTheDocumentedTieBreak(int seed, bool derived)
    {
        var rng = new Random(seed);
        var world = World.New();
        var stamps = new[] { Stamp.Box(6, 6, 9), Stamp.Box(3, 9, -4), Stamp.Tent(9, 9, 20), Stamp.Disk(5, 7) };
        try
        {
            var grid = Grid.New(world, 8, 0f, 0f, 256f);
            var baseLayer = Layer.New(world, Layer.Precision.Cell);
            var twin = Layer.New(world, Layer.Precision.Cell);
            var layer = derived ? Layer.Sum(world, baseLayer, 1, twin, 1) : baseLayer;
            for (var i = 0; i < 260; i++)
                World.Place(world, rng.Next(2) == 0 ? baseLayer : twin, rng.Next(0, 256) + 0.5f, rng.Next(0, 256) + 0.5f,
                    stamps[rng.Next(stamps.Length)], (sbyte)rng.Next(-3, 4));
            World.Process(world);

            var page = new short[256 * 256];
            unsafe
            {
                fixed (short* p = page) World.QueryRegion(world, grid, layer, 0, 0, 256, 256, p);
            }

            for (var trial = 0; trial < 400; trial++)
            {
                var kx = rng.Next(0, 256);
                var ky = rng.Next(0, 256);
                var reach = rng.Next(1, 90);
                var bestValue = int.MinValue;
                var bestX = 0;
                var bestY = 0;
                for (var cy = Math.Max(0, ky - reach); cy <= Math.Min(255, ky + reach); cy++)
                for (var cx = Math.Max(0, kx - reach); cx <= Math.Min(255, kx + reach); cx++)
                {
                    if ((cx - kx) * (cx - kx) + (cy - ky) * (cy - ky) > reach * reach) continue;
                    var value = page[cy * 256 + cx];
                    if (value > bestValue || (value == bestValue && (cy < bestY || (cy == bestY && cx < bestX))))
                    {
                        bestValue = value;
                        bestX = cx;
                        bestY = cy;
                    }
                }

                World.TrySenseMax(world, layer, kx + 0.5f, ky + 0.5f, reach, out var got, out var gotX, out var gotY);
                Assert.True(
                    got == bestValue && gotX.Equals(bestX + 0.5f) && gotY.Equals(bestY + 0.5f),
                    $"centre ({kx},{ky}) reach {reach}: expected {bestValue} at ({bestX},{bestY}) got {got} at ({gotX - 0.5f},{gotY - 0.5f})");
            }
        }
        finally
        {
            World.Free(world);
            foreach (var s in stamps) Stamp.Free(s);
        }
    }
}
