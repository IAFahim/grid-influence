using Xunit;

namespace Gi.Tests;

public sealed unsafe class ColumnShareTests : IDisposable
{
    private const int Repeats = 200;
    private const float Extent = 256f;

    private readonly byte _world = World.New();
    private readonly byte _layer;
    private readonly byte _box = Stamp.Box(12, 12, 60);
    private readonly int _since;

    public ColumnShareTests()
    {
        Grid.New(_world, 8, 0f, 0f, Extent);
        _layer = Layer.New(_world);
        _since = World.Tick(_world);
        for (var y = 8; y < 256; y += 16)
        for (var x = 8; x < 256; x += 16)
            Assert.True(World.Place(_world, _layer, x, y, _box, 5) >= 0);
        World.Process(_world);
    }

    public void Dispose()
    {
        World.Free(_world);
        Assert.True(Stamp.Free(_box));
    }

    public static TheoryData<int> Counts() =>
    [
        ResolvePool.ColumnThreshold + 1,
        ResolvePool.ColumnThreshold + 37,
        ResolvePool.ColumnThreshold + 63,
        ResolvePool.ColumnThreshold + 65,
        ResolvePool.ColumnThreshold + 1000,
        8 * ResolvePool.ColumnThreshold + 1,
    ];

    private static void Positions(int count, float[] xs, float[] ys)
    {
        var random = new Random(count);
        for (var i = 0; i < count; i++)
        {
            xs[i] = i % 16 == 5 ? -40f : (float)(random.NextDouble() * (Extent - 1f));
            ys[i] = (float)(random.NextDouble() * (Extent - 1f));
        }
    }

    [Fact]
    public void Pool_IsAvailableForColumnBatches()
    {
        Assert.False(ResolvePool.SerialOnly);
        var acquired = ResolvePool.TryAcquire();
        if (acquired) ResolvePool.Release();
        Assert.Equal(Environment.ProcessorCount > 1, acquired);
    }

    [Fact]
    public void WarmPooledColumns_AllocateNothing()
    {
        const int count = ResolvePool.ColumnThreshold + 37;
        var xs = new float[count];
        var ys = new float[count];
        Positions(count, xs, ys);
        var values = new short[count];
        var whole = new ulong[(count + 63) >> 6];
        var mask = new ulong[(count + 63) >> 6];

        fixed (float* xp = xs, yp = ys)
        fixed (short* vp = values)
        fixed (ulong* wp = whole, mp = mask)
        {
            for (var warm = 0; warm < 8; warm++)
            {
                World.SenseColumn(_world, _layer, xp, yp, count, vp, wp);
                World.ChangedMask(_world, _layer, xp, yp, 6f, _since, count, mp);
            }

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var repeat = 0; repeat < 64; repeat++)
            {
                World.SenseColumn(_world, _layer, xp, yp, count, vp, wp);
                World.ChangedMask(_world, _layer, xp, yp, 6f, _since, count, mp);
            }

            Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before);
        }
    }

    [Theory]
    [MemberData(nameof(Counts))]
    public void SenseColumn_PooledMatchesSerial_AcrossRepeats(int count)
    {
        var xs = new float[count];
        var ys = new float[count];
        Positions(count, xs, ys);
        var words = (count + 63) >> 6;
        var serialValues = new short[count];
        var serialWhole = new ulong[words];
        var pooledValues = new short[count];
        var pooledWhole = new ulong[words];
        bool serialAll;

        fixed (float* xp = xs, yp = ys)
        fixed (short* sv = serialValues, pv = pooledValues)
        fixed (ulong* sw = serialWhole, pw = pooledWhole)
        {
            ResolvePool.SerialOnly = true;
            try
            {
                serialAll = World.SenseColumn(_world, _layer, xp, yp, count, sv, sw);
            }
            finally
            {
                ResolvePool.SerialOnly = false;
            }

            Assert.False(serialAll);
            for (var repeat = 0; repeat < Repeats; repeat++)
            {
                new Span<short>(pooledValues).Fill(-1);
                var pooledAll = World.SenseColumn(_world, _layer, xp, yp, count, pv, pw);
                Assert.Equal(serialAll, pooledAll);
                Assert.True(serialValues.AsSpan().SequenceEqual(pooledValues), $"values differ at repeat {repeat}");
                Assert.True(serialWhole.AsSpan().SequenceEqual(pooledWhole), $"mask differs at repeat {repeat}");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Counts))]
    public void ChangedMask_PooledMatchesSerial_AcrossRepeats(int count)
    {
        var xs = new float[count];
        var ys = new float[count];
        Positions(count, xs, ys);
        var words = (count + 63) >> 6;
        var serialMask = new ulong[words];
        var pooledMask = new ulong[words];
        int serialHits;

        fixed (float* xp = xs, yp = ys)
        fixed (ulong* sm = serialMask, pm = pooledMask)
        {
            ResolvePool.SerialOnly = true;
            try
            {
                serialHits = World.ChangedMask(_world, _layer, xp, yp, 6f, _since, count, sm);
            }
            finally
            {
                ResolvePool.SerialOnly = false;
            }

            Assert.InRange(serialHits, count / 2, count - 1);
            for (var repeat = 0; repeat < Repeats; repeat++)
            {
                var pooledHits = World.ChangedMask(_world, _layer, xp, yp, 6f, _since, count, pm);
                Assert.Equal(serialHits, pooledHits);
                Assert.True(serialMask.AsSpan().SequenceEqual(pooledMask), $"mask differs at repeat {repeat}");
            }
        }
    }
}
