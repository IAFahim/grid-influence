using Xunit;

namespace Gi.Tests;

public unsafe class ColumnRunTests
{
    [Theory]
    [InlineData(81, 7, 4000, 0.0, 0)]
    [InlineData(82, 8, 6000, 0.25, 0)]
    [InlineData(83, 9, 9000, 0.5, 1)]
    [InlineData(84, 10, 20000, 0.25, 0)]
    [InlineData(85, 10, 20000, 1.0, 1)]
    [InlineData(86, 6, 3000, 0.1, 0)]
    [InlineData(87, 11, 5000, 0.3, 0)]
    public void MortonRunsWithDriftEqualPerPointReads(int seed, int power, int count, double drift, int kind)
    {
        var rng = new Random(seed);
        var size = 1 << power;
        var w = World.New();
        var stamps = new List<byte> { Stamp.Box(5, 5, 40), Stamp.Tent(7, 7, -35), Stamp.Disk(5, 50) };
        try
        {
            Grid.New(w, power, 0f, 0f, size);
            var layer = Layer.New(w, kind == 0 ? Layer.Precision.Cell : Layer.Precision.Exact);
            var sources = Math.Max(30, size * size / 600);
            for (var i = 0; i < sources; i++)
            {
                var x = (float)(rng.NextDouble() * size * (i % 5 == 0 ? 0.5 : 1.0));
                var y = (float)(rng.NextDouble() * size * (i % 7 == 0 ? 0.5 : 1.0));
                World.Place(w, layer, x, y, stamps[rng.Next(stamps.Count)], (sbyte)rng.Next(-5, 6));
            }

            World.Process(w);

            var xs = new float[count];
            var ys = new float[count];
            var keys = new ulong[count];
            for (var i = 0; i < count; i++)
            {
                xs[i] = (float)(rng.NextDouble() * (size + 4) - 2);
                ys[i] = (float)(rng.NextDouble() * (size + 4) - 2);
                keys[i] = Morton((uint)Math.Clamp((int)xs[i], 0, size - 1), (uint)Math.Clamp((int)ys[i], 0, size - 1));
            }

            var order = new int[count];
            for (var i = 0; i < count; i++) order[i] = i;
            Array.Sort(keys, order);
            var sortedX = new float[count];
            var sortedY = new float[count];
            for (var i = 0; i < count; i++)
            {
                sortedX[i] = xs[order[i]];
                sortedY[i] = ys[order[i]];
            }

            for (var i = 0; i < count; i++)
            {
                sortedX[i] += (float)((rng.NextDouble() * 2 - 1) * drift * 32);
                sortedY[i] += (float)((rng.NextDouble() * 2 - 1) * drift * 32);
            }

            Compare(w, layer, sortedX, sortedY, count);
            Compare(w, layer, sortedX, sortedY, count - 3);
            Compare(w, layer, sortedX, sortedY, 63);
            Compare(w, layer, sortedX, sortedY, 65);
        }
        finally
        {
            World.Free(w);
            foreach (var s in stamps) Stamp.Free(s);
        }
    }

    private static void Compare(byte w, byte layer, float[] xs, float[] ys, int count)
    {
        var values = new short[count];
        var gx = new float[count];
        var gy = new float[count];
        var gradientOnlyX = new float[count];
        var gradientOnlyY = new float[count];
        var pointValues = new short[count];
        var whole = new ulong[(count + 63) >> 6];
        var gradientWhole = new ulong[(count + 63) >> 6];
        var pointWhole = new ulong[(count + 63) >> 6];
        Array.Fill(whole, ulong.MaxValue);
        Array.Fill(gradientWhole, ulong.MaxValue);
        Array.Fill(pointWhole, ulong.MaxValue);
        World.ReadStatus fused;
        World.ReadStatus gradient;
        World.ReadStatus point;
        fixed (float* px = xs)
        fixed (float* py = ys)
        fixed (short* pv = values)
        fixed (short* ppv = pointValues)
        fixed (float* pgx = gx)
        fixed (float* pgy = gy)
        fixed (float* pox = gradientOnlyX)
        fixed (float* poy = gradientOnlyY)
        fixed (ulong* pw = whole)
        fixed (ulong* pgw = gradientWhole)
        fixed (ulong* ppw = pointWhole)
        {
            fused = World.SenseGradientColumn(w, layer, px, py, null, count, pv, pgx, pgy, pw);
            gradient = World.SenseGradientColumn(w, layer, px, py, null, count, pox, poy, pgw);
            point = World.SenseColumn(w, layer, px, py, count, ppv, ppw);
        }

        var expectedFused = true;
        var expectedPoint = true;
        for (var i = 0; i < count; i++)
        {
            var valueFilled = World.TrySense(w, layer, xs[i], ys[i], out var value);
            var gradientFilled = World.TrySenseGradient(w, layer, xs[i], ys[i], out var dx, out var dy);
            expectedFused &= valueFilled && gradientFilled;
            expectedPoint &= valueFilled;
            var at = $"lane {i} x={xs[i]} y={ys[i]}";
            Assert.True(value == values[i], $"{at} fused value {value} vs {values[i]}");
            Assert.True(value == pointValues[i], $"{at} point value {value} vs {pointValues[i]}");
            Assert.True(BitConverter.SingleToInt32Bits(dx) == BitConverter.SingleToInt32Bits(gx[i]),
                $"{at} dx {dx} vs {gx[i]}");
            Assert.True(BitConverter.SingleToInt32Bits(dy) == BitConverter.SingleToInt32Bits(gy[i]),
                $"{at} dy {dy} vs {gy[i]}");
            Assert.True(BitConverter.SingleToInt32Bits(dx) == BitConverter.SingleToInt32Bits(gradientOnlyX[i]),
                $"{at} gradient-only dx {dx} vs {gradientOnlyX[i]}");
            Assert.True(BitConverter.SingleToInt32Bits(dy) == BitConverter.SingleToInt32Bits(gradientOnlyY[i]),
                $"{at} gradient-only dy {dy} vs {gradientOnlyY[i]}");
            var bit = 1ul << (i & 63);
            var gradientBit = (gradientWhole[i >> 6] & bit) != 0;
            Assert.True(valueFilled == ((pointWhole[i >> 6] & bit) != 0), $"{at} point whole");
            Assert.True(gradientFilled == gradientBit, $"{at} gradient whole");
            Assert.True((valueFilled && gradientFilled) == ((whole[i >> 6] & bit) != 0), $"{at} fused whole");
        }

        Assert.Equal(expectedFused, fused == World.ReadStatus.Complete);
        Assert.Equal(expectedPoint, point == World.ReadStatus.Complete);
        Assert.Equal(gradient == World.ReadStatus.Complete, fused == World.ReadStatus.Complete);
        for (var word = (count + 63) >> 6; word < whole.Length; word++) Assert.Equal(0ul, whole[word]);
        if ((count & 63) != 0)
        {
            var tail = ~((1ul << (count & 63)) - 1);
            Assert.Equal(0ul, whole[^1] & tail);
            Assert.Equal(0ul, gradientWhole[^1] & tail);
            Assert.Equal(0ul, pointWhole[^1] & tail);
        }
    }

    private static ulong Morton(uint x, uint y)
    {
        return Spread(x) | (Spread(y) << 1);
    }

    private static ulong Spread(uint value)
    {
        ulong v = value & 0xFFFF;
        v = (v | (v << 8)) & 0x00FF00FF;
        v = (v | (v << 4)) & 0x0F0F0F0F;
        v = (v | (v << 2)) & 0x33333333;
        v = (v | (v << 1)) & 0x55555555;
        return v;
    }
}
