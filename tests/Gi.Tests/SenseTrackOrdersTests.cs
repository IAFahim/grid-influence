using Xunit;

namespace Gi.Tests;

public unsafe class SenseTrackOrdersTests : IDisposable
{
    private readonly byte _box;
    private readonly byte _layer;
    private readonly byte _world = World.New();

    public SenseTrackOrdersTests()
    {
        Grid.New(_world, 8, 0f, 0f, 256f);
        _layer = Layer.New(_world, Layer.Precision.Cell);
        _box = Stamp.Box(4, 4, 24);
        var random = new Random(20261030);
        for (var i = 0; i < 200; i++)
        {
            var x = (float)(random.NextDouble() * 256f * ((i & 1) == 0 ? 0.5 : 1.0));
            var y = (float)(random.NextDouble() * 256f * ((i & 3) == 0 ? 0.4 : 1.0));
            Assert.True(World.Place(_world, _layer, x, y, _box, (sbyte)(i % 5 - 2)) != default);
        }

        World.Process(_world);
    }

    public void Dispose()
    {
        World.Free(_world);
        Assert.True(Stamp.Free(_box));
    }

    [Fact]
    public void RunnyMortonOrderMatchesPerPointReads()
    {
        CompareOrderedReads(20261031, 0.0);
    }

    [Fact]
    public void FrayedMortonOrderMatchesPerPointReads()
    {
        CompareOrderedReads(20261032, 1.0);
    }

    [Fact]
    public void ScatterOrderMatchesPerPointReads()
    {
        var random = new Random(20261033);
        var xs = new float[4000];
        var ys = new float[4000];
        for (var i = 0; i < xs.Length; i++)
        {
            xs[i] = (float)(random.NextDouble() * 260.0 - 2.0);
            ys[i] = (float)(random.NextDouble() * 260.0 - 2.0);
        }

        Compare(_world, _layer, xs, ys, xs.Length);
        Compare(_world, _layer, xs, ys, 63);
        Compare(_world, _layer, xs, ys, 65);
    }

    private void CompareOrderedReads(int seed, double drift)
    {
        var random = new Random(seed);
        const int count = 4000;
        var xs = new float[count];
        var ys = new float[count];
        var keys = new ulong[count];
        for (var i = 0; i < count; i++)
        {
            xs[i] = (float)(random.NextDouble() * 260.0 - 2.0);
            ys[i] = (float)(random.NextDouble() * 260.0 - 2.0);
            keys[i] = Morton((uint)Math.Clamp((int)xs[i], 0, 255), (uint)Math.Clamp((int)ys[i], 0, 255));
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
            sortedX[i] += (float)((random.NextDouble() * 2 - 1) * drift * 32);
            sortedY[i] += (float)((random.NextDouble() * 2 - 1) * drift * 32);
        }

        Compare(_world, _layer, sortedX, sortedY, count);
        Compare(_world, _layer, sortedX, sortedY, count - 3);
        Compare(_world, _layer, sortedX, sortedY, 63);
        Compare(_world, _layer, sortedX, sortedY, 65);
    }

    private static void Compare(byte world, byte layer, float[] xs, float[] ys, int count)
    {
        var fusedValues = new short[count];
        var gradientX = new float[count];
        var gradientY = new float[count];
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
        fixed (short* pf = fusedValues)
        fixed (short* pp = pointValues)
        fixed (float* pgx = gradientX)
        fixed (float* pgy = gradientY)
        fixed (float* pox = gradientOnlyX)
        fixed (float* poy = gradientOnlyY)
        fixed (ulong* pw = whole)
        fixed (ulong* pgw = gradientWhole)
        fixed (ulong* ppw = pointWhole)
        {
            fused = World.SenseGradientColumn(world, layer, px, py, null, count, pf, pgx, pgy, pw);
            gradient = World.SenseGradientColumn(world, layer, px, py, null, count, pox, poy, pgw);
            point = World.SenseColumn(world, layer, px, py, count, pp, ppw);
        }

        Assert.Equal(gradient, fused);

        var expectedComplete = true;
        for (var i = 0; i < count; i++)
        {
            var valueFilled = World.TrySense(world, layer, xs[i], ys[i], out var value);
            var gradientFilled = World.TrySenseGradient(world, layer, xs[i], ys[i], out var dx, out var dy);
            expectedComplete &= valueFilled && gradientFilled;
            var at = $"lane {i} x={xs[i]} y={ys[i]}";
            Assert.True(value == fusedValues[i], $"{at} fused value {value} vs {fusedValues[i]}");
            Assert.True(value == pointValues[i], $"{at} point value {value} vs {pointValues[i]}");
            Assert.True(BitConverter.SingleToInt32Bits(dx) == BitConverter.SingleToInt32Bits(gradientX[i]),
                $"{at} dx {dx} vs {gradientX[i]}");
            Assert.True(BitConverter.SingleToInt32Bits(dy) == BitConverter.SingleToInt32Bits(gradientY[i]),
                $"{at} dy {dy} vs {gradientY[i]}");
            Assert.True(BitConverter.SingleToInt32Bits(dx) == BitConverter.SingleToInt32Bits(gradientOnlyX[i]),
                $"{at} gradient-only dx {dx} vs {gradientOnlyX[i]}");
            Assert.True(BitConverter.SingleToInt32Bits(dy) == BitConverter.SingleToInt32Bits(gradientOnlyY[i]),
                $"{at} gradient-only dy {dy} vs {gradientOnlyY[i]}");
            var bit = 1ul << (i & 63);
            Assert.True(valueFilled == ((pointWhole[i >> 6] & bit) != 0), $"{at} point whole");
            Assert.True(gradientFilled == ((gradientWhole[i >> 6] & bit) != 0), $"{at} gradient whole");
            Assert.True((valueFilled && gradientFilled) == ((whole[i >> 6] & bit) != 0), $"{at} fused whole");
        }

        Assert.Equal(expectedComplete, fused == World.ReadStatus.Complete);
        Assert.Equal(expectedComplete, gradient == World.ReadStatus.Complete);
        Assert.Equal(expectedComplete, point == World.ReadStatus.Complete);
        for (var word = (count + 63) >> 6; word < whole.Length; word++)
        {
            Assert.Equal(0UL, whole[word]);
            Assert.Equal(0UL, gradientWhole[word]);
            Assert.Equal(0UL, pointWhole[word]);
        }

        if ((count & 63) != 0)
        {
            var tail = ~((1ul << (count & 63)) - 1);
            Assert.Equal(0UL, whole[count >> 6] & tail);
            Assert.Equal(0UL, gradientWhole[count >> 6] & tail);
            Assert.Equal(0UL, pointWhole[count >> 6] & tail);
        }
    }

    private static ulong Morton(uint x, uint y)
    {
        return (Spread(x) | ((ulong)Spread(y) << 1)) << 32;
    }

    private static uint Spread(uint value)
    {
        value &= 0xFFFF;
        value = (value | (value << 8)) & 0x00FF00FF;
        value = (value | (value << 4)) & 0x0F0F0F0F;
        value = (value | (value << 2)) & 0x33333333;
        value = (value | (value << 1)) & 0x55555555;
        return value;
    }
}
