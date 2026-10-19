using Xunit;

namespace Gi.Tests;

public class SpatialOrderTests
{
    private static uint Interleave(int a, int b, int bits)
    {
        uint result = 0;
        for (var bit = 0; bit < bits; bit++)
        {
            result |= (uint)((a >> bit) & 1) << (2 * bit);
            result |= (uint)((b >> bit) & 1) << (2 * bit + 1);
        }

        return result;
    }

    [Theory]
    [InlineData(5, 0f, 0f, 32f)]
    [InlineData(10, -16f, 8f, 1024f)]
    [InlineData(14, 0f, 0f, 16384f)]
    public void KeysAreTheTileMortonCodeThenTheCellMortonCode(int power, float originX, float originY, float size)
    {
        var world = World.New();
        try
        {
            var grid = Grid.New(world, power, originX, originY, size);
            var rng = new Random(power);
            const int count = 4000;
            var xs = new float[count];
            var ys = new float[count];
            for (var i = 0; i < count; i++)
            {
                xs[i] = originX + (float)rng.NextDouble() * size;
                ys[i] = originY + (float)rng.NextDouble() * size;
            }

            xs[0] = float.NaN;
            xs[1] = originX - 1f;
            ys[2] = originY + size + 1f;
            xs[3] = float.PositiveInfinity;
            var keys = new uint[count];
            Assert.Equal(count, Grid.SpatialKeys(world, grid, xs, ys, keys));
            var scale = (1 << power) / size;
            for (var i = 0; i < count; i++)
            {
                if (i < 4)
                {
                    Assert.Equal(uint.MaxValue, keys[i]);
                    continue;
                }

                var cx = (int)Math.Floor((xs[i] - originX) * scale);
                var cy = (int)Math.Floor((ys[i] - originY) * scale);
                cx = Math.Clamp(cx, 0, (1 << power) - 1);
                cy = Math.Clamp(cy, 0, (1 << power) - 1);
                var expected = (Interleave(cx >> 5, cy >> 5, 9) << 10) | Interleave(cx & 31, cy & 31, 5);
                Assert.True(keys[i] == expected, $"key {keys[i]:x} vs {expected:x} at ({xs[i]},{ys[i]})");
            }
        }
        finally
        {
            World.Free(world);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2049)]
    [InlineData(100_000)]
    public void SortProducesTheStablePermutationWithoutAllocating(int count)
    {
        var rng = new Random(count + 3);
        var keys = new uint[count];
        for (var i = 0; i < count; i++)
            keys[i] = rng.Next(4) switch
            {
                0 => (uint)rng.Next(16),
                1 => (uint)rng.Next(),
                2 => uint.MaxValue - (uint)rng.Next(8),
                _ => (uint)(rng.Next() << 1) ^ (uint)rng.Next()
            };

        var permutation = new int[count];
        var scratch = new ulong[2 * count];
        Assert.True(Grid.SortByKeys(keys, permutation, scratch));
        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.True(Grid.SortByKeys(keys, permutation, scratch));
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);

        var expected = Enumerable.Range(0, count).OrderBy(i => keys[i]).ThenBy(i => i).ToArray();
        Assert.Equal(expected, permutation);
        if (count > 0) Assert.False(Grid.SortByKeys(keys, permutation, new ulong[2 * count - 1]));
    }

    [Fact]
    public void DisorderCountsAdjacentPairsWhoseTileDescends()
    {
        var sorted = Enumerable.Range(0, 100).Select(i => ((uint)(i / 2) << 10) | (uint)(i % 7)).ToArray();
        var reversed = sorted.Reverse().ToArray();
        Assert.Equal(0f, Grid.Disorder(sorted));
        Assert.Equal(0f, Grid.Disorder(Array.Empty<uint>()));
        Assert.True(Grid.Disorder(reversed) > 0.45f);
        Assert.Equal(1f, Grid.Disorder(new uint[] { 5 << 10, 4 << 10, 3 << 10, 2 << 10, 1 << 10 }));
        var withinTile = new uint[] { 9, 3, 7, 1, 8 };
        Assert.Equal(0f, Grid.Disorder(withinTile));
    }
}
