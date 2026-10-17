using Xunit;

namespace Gi.Tests;

public unsafe class FusedColumnEdgeTests
{
    [Theory]
    [InlineData(61, 5, 0, 1)]
    [InlineData(62, 6, 0, 1)]
    [InlineData(63, 7, 1, 1)]
    [InlineData(64, 8, 0, 2)]
    [InlineData(65, 8, 1, 2)]
    [InlineData(66, 9, 0, 1)]
    public void VectorColumnEqualsPerPointReadsAtTileAndGridEdges(int seed, int power, int kind, int clusters)
    {
        var rng = new Random(seed);
        var size = 1 << power;
        var w = World.New();
        var stamps = new List<byte> { Stamp.Box(5, 5, 40), Stamp.Tent(7, 7, -35), Stamp.Disk(5, 50, 360) };
        try
        {
            Grid.New(w, power, 0f, 0f, size);
            var layer = Layer.New(w, kind == 0 ? Layer.Precision.Cell : Layer.Precision.Exact);
            for (var i = 0; i < 60; i++)
            {
                var center = rng.Next(clusters) == 0 ? size / 3f : size * 0.8f;
                var x = Math.Clamp(center + (float)(rng.NextDouble() - 0.5) * size / 4f, 0f, size - 1f);
                var y = Math.Clamp(center + (float)(rng.NextDouble() - 0.5) * size / 4f, 0f, size - 1f);
                World.Place(w, layer, x, y, stamps[rng.Next(stamps.Count)], (sbyte)rng.Next(-5, 6));
            }

            World.Process(w);

            const int Count = 8192;
            var xs = new float[Count];
            var ys = new float[Count];
            var tiles = size / 32;
            for (var i = 0; i < Count; i += 8)
            {
                var tx = rng.Next(tiles);
                var ty = rng.Next(tiles);
                var mode = rng.Next(6);
                for (var lane = 0; lane < 8 && i + lane < Count; lane++)
                {
                    var lx = EdgeBiased(rng, mode);
                    var ly = EdgeBiased(rng, mode);
                    var x = tx * 32 + lx + (float)rng.NextDouble() * 0.99f;
                    var y = ty * 32 + ly + (float)rng.NextDouble() * 0.99f;
                    if (mode == 5 && lane % 3 == 0) x = (float)rng.NextDouble() * size * 1.1f - size * 0.05f;
                    xs[i + lane] = x;
                    ys[i + lane] = y;
                }
            }

            var values = new short[Count];
            var gx = new float[Count];
            var gy = new float[Count];
            var whole = new ulong[(Count + 63) >> 6];
            Array.Fill(whole, ulong.MaxValue);
            bool complete;
            fixed (float* px = xs)
            fixed (float* py = ys)
            fixed (short* pv = values)
            fixed (float* pgx = gx)
            fixed (float* pgy = gy)
            fixed (ulong* pw = whole)
                complete = (World.SenseGradientColumn(w, layer, px, py, null, Count, pv, pgx, pgy, pw) == World.ReadStatus.Complete);

            var expectedComplete = true;
            for (var i = 0; i < Count; i++)
            {
                var valueFilled = World.TrySense(w, layer, xs[i], ys[i], out var value);
                var gradientFilled = World.TrySenseGradient(w, layer, xs[i], ys[i], out var dx, out var dy);
                var filled = valueFilled && gradientFilled;
                expectedComplete &= filled;
                var at = $"lane {i} ({i & 7}) x={xs[i]} y={ys[i]}";
                Assert.True(value == values[i], $"{at} value {value} vs {values[i]}");
                Assert.True(dx == gx[i], $"{at} dx {dx} vs {gx[i]}");
                Assert.True(dy == gy[i], $"{at} dy {dy} vs {gy[i]}");
                Assert.True(filled == (((whole[i >> 6] >> (i & 63)) & 1) == 1), $"{at} whole {filled}");
            }

            Assert.Equal(expectedComplete, complete);
        }
        finally
        {
            World.Free(w);
            foreach (var s in stamps) Stamp.Free(s);
        }
    }

    private static int EdgeBiased(Random rng, int mode) => mode switch
    {
        0 => rng.Next(32),
        1 => rng.Next(2) == 0 ? 0 : 31,
        2 => rng.Next(4) switch { 0 => 0, 1 => 1, 2 => 30, _ => 31 },
        3 => rng.Next(3) == 0 ? 31 : rng.Next(32),
        4 => rng.Next(3) == 0 ? 0 : rng.Next(32),
        _ => rng.Next(32),
    };
}
