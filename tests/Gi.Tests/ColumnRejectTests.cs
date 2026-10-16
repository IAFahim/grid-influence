using Xunit;

namespace Gi.Tests;

public sealed unsafe class ColumnRejectTests : IDisposable
{
    private const float Extent = 64f;
    private const short StaleShort = 0x2BAD;
    private const long StaleLong = 0x2BAD_2BAD_2BAD;
    private const float StaleFloat = 7777f;

    private readonly byte _world = World.New();
    private readonly byte _layer;
    private readonly byte _freed;

    public ColumnRejectTests()
    {
        Grid.New(_world, 6, 0f, 0f, Extent);
        _layer = Layer.New(_world);
        _freed = World.New();
        World.Free(_freed);
    }

    public void Dispose() => World.Free(_world);

    public static TheoryData<int, int> Rejections()
    {
        var data = new TheoryData<int, int>();
        foreach (var count in new[] { 1, 63, 64, 65, 200 })
        foreach (var kind in new[] { 0, 1 })
            data.Add(count, kind);
        return data;
    }

    private (byte World, byte Layer) Rejected(int kind) => kind == 0 ? (_freed, _layer) : (_world, (byte)200);

    private static void Queries(int count, float[] xs, float[] ys)
    {
        for (var i = 0; i < count; i++)
        {
            var row = i / 4;
            xs[i] = 1.5f + i % 50;
            ys[i] = 2.5f + row % 50;
        }
    }

    private static ulong[] Staled(int count)
    {
        var words = new ulong[((count + 63) >> 6) + 1];
        Array.Fill(words, ulong.MaxValue);
        return words;
    }

    private static void AssertMaskCleared(ulong[] words, int count)
    {
        var used = (count + 63) >> 6;
        for (var i = 0; i < used; i++) Assert.Equal(0ul, words[i]);
        Assert.Equal(ulong.MaxValue, words[used]);
    }

    [Theory]
    [MemberData(nameof(Rejections))]
    public void SenseColumn_Rejected_WritesWhatAnUnfilledRowHolds(int count, int kind)
    {
        var (world, layer) = Rejected(kind);
        var xs = new float[count];
        var ys = new float[count];
        Queries(count, xs, ys);
        var values = new short[count];
        var wide = new short[count];
        var whole = Staled(count);
        var wideWhole = Staled(count);
        Array.Fill(values, StaleShort);
        Array.Fill(wide, StaleShort);

        fixed (float* xp = xs, yp = ys)
        fixed (short* vp = values, wp = wide)
        fixed (ulong* mp = whole, wm = wideWhole)
        {
            Assert.False(World.SenseColumn(world, layer, xp, yp, count, vp, mp));
            Assert.False(World.SenseColumn(world, layer, xp, yp, null, count, wp, wm));
        }

        for (var i = 0; i < count; i++)
        {
            Assert.False(World.TrySense(world, layer, xs[i], ys[i], out var expected));
            Assert.Equal(expected, values[i]);
            Assert.Equal(expected, wide[i]);
        }

        AssertMaskCleared(whole, count);
        AssertMaskCleared(wideWhole, count);
    }

    [Theory]
    [MemberData(nameof(Rejections))]
    public void SenseGradientColumn_Rejected_WritesWhatAnUnfilledRowHolds(int count, int kind)
    {
        var (world, layer) = Rejected(kind);
        var xs = new float[count];
        var ys = new float[count];
        Queries(count, xs, ys);
        var gx = new float[count];
        var gy = new float[count];
        var whole = Staled(count);
        Array.Fill(gx, StaleFloat);
        Array.Fill(gy, StaleFloat);

        fixed (float* xp = xs, yp = ys, gxp = gx, gyp = gy)
        fixed (ulong* mp = whole)
            Assert.False(World.SenseGradientColumn(world, layer, xp, yp, null, count, gxp, gyp, mp));

        for (var i = 0; i < count; i++)
        {
            Assert.False(World.TrySenseGradient(world, layer, xs[i], ys[i], out var ex, out var ey));
            Assert.Equal(ex, gx[i]);
            Assert.Equal(ey, gy[i]);
        }

        AssertMaskCleared(whole, count);
    }

    [Theory]
    [MemberData(nameof(Rejections))]
    public void SenseAreaColumn_Rejected_WritesWhatAnUnfilledRowHolds(int count, int kind)
    {
        var (world, layer) = Rejected(kind);
        var xs = new float[count];
        var ys = new float[count];
        Queries(count, xs, ys);
        var reaches = new float[count];
        Array.Fill(reaches, 3f);
        var totals = new long[count];
        var whole = Staled(count);
        Array.Fill(totals, StaleLong);

        fixed (float* xp = xs, yp = ys, rp = reaches)
        fixed (long* tp = totals)
        fixed (ulong* mp = whole)
            Assert.False(World.SenseAreaColumn(world, layer, xp, yp, rp, null, count, tp, mp));

        for (var i = 0; i < count; i++)
        {
            Assert.False(World.TrySenseArea(world, layer, xs[i], ys[i], reaches[i], out var expected));
            Assert.Equal(expected, totals[i]);
        }

        AssertMaskCleared(whole, count);
    }

    [Theory]
    [MemberData(nameof(Rejections))]
    public void SenseMaxColumn_Rejected_WritesWhatAnUnfilledRowHolds(int count, int kind)
    {
        var (world, layer) = Rejected(kind);
        var xs = new float[count];
        var ys = new float[count];
        Queries(count, xs, ys);
        var reaches = new float[count];
        Array.Fill(reaches, 3f);
        var values = new short[count];
        var bx = new float[count];
        var by = new float[count];
        var whole = Staled(count);
        Array.Fill(values, StaleShort);
        Array.Fill(bx, StaleFloat);
        Array.Fill(by, StaleFloat);

        fixed (float* xp = xs, yp = ys, rp = reaches, bxp = bx, byp = by)
        fixed (short* vp = values)
        fixed (ulong* mp = whole)
            Assert.False(World.SenseMaxColumn(world, layer, xp, yp, rp, count, vp, bxp, byp, mp));

        for (var i = 0; i < count; i++)
        {
            Assert.False(World.TrySenseMax(world, layer, xs[i], ys[i], reaches[i], out var ev, out var ex, out var ey));
            Assert.Equal(ev, values[i]);
            Assert.Equal(ex, bx[i]);
            Assert.Equal(ey, by[i]);
        }

        AssertMaskCleared(whole, count);
    }

    [Theory]
    [MemberData(nameof(Rejections))]
    public void SenseNearestColumn_Rejected_WritesWhatAnUnfilledRowHolds(int count, int kind)
    {
        var (world, layer) = Rejected(kind);
        var xs = new float[count];
        var ys = new float[count];
        Queries(count, xs, ys);
        var reaches = new float[count];
        Array.Fill(reaches, 3f);
        var thresholds = new short[count];
        var values = new short[count];
        var nx = new float[count];
        var ny = new float[count];
        var found = Staled(count);
        Array.Fill(values, StaleShort);
        Array.Fill(nx, StaleFloat);
        Array.Fill(ny, StaleFloat);

        fixed (float* xp = xs, yp = ys, rp = reaches, nxp = nx, nyp = ny)
        fixed (short* tp = thresholds, vp = values)
        fixed (ulong* mp = found)
            Assert.False(World.SenseNearestColumn(world, layer, xp, yp, rp, tp, count, vp, nxp, nyp, mp));

        for (var i = 0; i < count; i++)
        {
            Assert.False(World.TrySenseNearest(world, layer, xs[i], ys[i], reaches[i], thresholds[i],
                out var ev, out var ex, out var ey));
            Assert.Equal(ev, values[i]);
            Assert.Equal(ex, nx[i]);
            Assert.Equal(ey, ny[i]);
        }

        AssertMaskCleared(found, count);
    }

    [Theory]
    [MemberData(nameof(Rejections))]
    public void ChangedMask_Rejected_ClearsWordsAndReturnsZero(int count, int kind)
    {
        var (world, layer) = Rejected(kind);
        var xs = new float[count];
        var ys = new float[count];
        Queries(count, xs, ys);
        var words = Staled(count);

        fixed (float* xp = xs, yp = ys)
        fixed (ulong* wp = words)
            Assert.Equal(0, World.ChangedMask(world, layer, xp, yp, 3f, 0, count, wp));

        AssertMaskCleared(words, count);
    }

    [Fact]
    public void SenseColumn_NullColumn_ClearsTheOutputsItWasGiven()
    {
        const int count = 70;
        var xs = new float[count];
        var ys = new float[count];
        Queries(count, xs, ys);
        var values = new short[count];
        var whole = Staled(count);
        Array.Fill(values, StaleShort);

        fixed (float* yp = ys)
        fixed (short* vp = values)
        fixed (ulong* mp = whole)
            Assert.False(World.SenseColumn(_world, _layer, null, yp, null, count, vp, mp));

        Assert.All(values, v => Assert.Equal(0, v));
        AssertMaskCleared(whole, count);
    }

    [Fact]
    public void SenseGradientColumn_NullColumn_ClearsTheOutputsItWasGiven()
    {
        const int count = 70;
        var xs = new float[count];
        var ys = new float[count];
        Queries(count, xs, ys);
        var gx = new float[count];
        var whole = Staled(count);
        Array.Fill(gx, StaleFloat);

        fixed (float* xp = xs, yp = ys, gxp = gx)
        fixed (ulong* mp = whole)
            Assert.False(World.SenseGradientColumn(_world, _layer, xp, yp, null, count, gxp, null, mp));

        Assert.All(gx, v => Assert.Equal(0f, v));
        AssertMaskCleared(whole, count);
    }

    [Fact]
    public void SenseAreaColumn_NullColumn_ClearsTheOutputsItWasGiven()
    {
        const int count = 70;
        var xs = new float[count];
        var ys = new float[count];
        Queries(count, xs, ys);
        var totals = new long[count];
        var whole = Staled(count);
        Array.Fill(totals, StaleLong);

        fixed (float* xp = xs, yp = ys)
        fixed (long* tp = totals)
        fixed (ulong* mp = whole)
            Assert.False(World.SenseAreaColumn(_world, _layer, xp, yp, null, null, count, tp, mp));

        Assert.All(totals, v => Assert.Equal(0L, v));
        AssertMaskCleared(whole, count);
    }

    [Fact]
    public void SenseMaxColumn_NullColumn_ClearsTheOutputsItWasGiven()
    {
        const int count = 70;
        var xs = new float[count];
        var ys = new float[count];
        Queries(count, xs, ys);
        var values = new short[count];
        var bx = new float[count];
        var by = new float[count];
        var whole = Staled(count);
        Array.Fill(values, StaleShort);
        Array.Fill(bx, StaleFloat);
        Array.Fill(by, StaleFloat);

        fixed (float* xp = xs, bxp = bx, byp = by)
        fixed (short* vp = values)
        fixed (ulong* mp = whole)
            Assert.False(World.SenseMaxColumn(_world, _layer, xp, null, null, count, vp, bxp, byp, mp));

        Assert.All(values, v => Assert.Equal(0, v));
        Assert.All(bx, v => Assert.Equal(0f, v));
        Assert.All(by, v => Assert.Equal(0f, v));
        AssertMaskCleared(whole, count);
    }

    [Fact]
    public void SenseNearestColumn_NullColumn_ClearsTheOutputsItWasGiven()
    {
        const int count = 70;
        var xs = new float[count];
        var ys = new float[count];
        Queries(count, xs, ys);
        var reaches = new float[count];
        var values = new short[count];
        var nx = new float[count];
        var ny = new float[count];
        var found = Staled(count);
        Array.Fill(values, StaleShort);
        Array.Fill(nx, StaleFloat);
        Array.Fill(ny, StaleFloat);

        fixed (float* xp = xs, yp = ys, rp = reaches, nxp = nx, nyp = ny)
        fixed (short* vp = values)
        fixed (ulong* mp = found)
            Assert.False(World.SenseNearestColumn(_world, _layer, xp, yp, rp, null, count, vp, nxp, nyp, mp));

        Assert.All(values, v => Assert.Equal(short.MinValue, v));
        Assert.All(nx, v => Assert.Equal(0f, v));
        Assert.All(ny, v => Assert.Equal(0f, v));
        AssertMaskCleared(found, count);
    }

    [Fact]
    public void EmptyBatches_KeepTheirReturnValuesAndWriteNothing()
    {
        var values = new short[1];
        var words = new ulong[1];
        var gx = new float[1];
        words[0] = ulong.MaxValue;
        values[0] = StaleShort;
        gx[0] = StaleFloat;

        fixed (short* vp = values)
        fixed (ulong* wp = words)
        fixed (float* gp = gx)
        {
            foreach (var count in new[] { 0, -1, int.MinValue })
            {
                Assert.False(World.SenseColumn(_freed, _layer, null, null, count, vp, wp));
                Assert.True(World.SenseColumn(_world, _layer, null, null, count, vp, wp));
                Assert.False(World.SenseColumn(_world, 200, null, null, null, count, vp, wp));
                Assert.True(World.SenseColumn(_world, _layer, null, null, null, count, vp, wp));
                Assert.False(World.SenseGradientColumn(_freed, _layer, null, null, null, count, gp, gp, wp));
                Assert.True(World.SenseGradientColumn(_world, _layer, null, null, null, count, gp, gp, wp));
                Assert.Equal(0, World.ChangedMask(_freed, _layer, null, null, 1f, 0, count, wp));
                Assert.Equal(0, World.ChangedMask(_world, _layer, null, null, 1f, 0, count, wp));
            }
        }

        Assert.Equal(ulong.MaxValue, words[0]);
        Assert.Equal(StaleShort, values[0]);
        Assert.Equal(StaleFloat, gx[0]);
    }

    [Fact]
    public void ChangedMask_LastWordIsClearedWhole_WhenWorldIsRejected()
    {
        const int count = 65;
        var xs = new float[count];
        var ys = new float[count];
        var words = Staled(count);

        fixed (float* xp = xs, yp = ys)
        fixed (ulong* wp = words)
            Assert.Equal(0, World.ChangedMask(_freed, _layer, xp, yp, 1f, 0, count, wp));

        Assert.Equal(0ul, words[1]);
        Assert.Equal(ulong.MaxValue, words[2]);
    }
}
