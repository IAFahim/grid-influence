using Xunit;

namespace Gi.Tests;

public sealed unsafe class PoolAvailabilityTests
{
    [Fact]
    public void TryAcquire_RefusesOneProcessorWithoutCreatingThePool()
    {
        var saved = ResolvePool.Processors;
        var created = ResolvePool.Created;
        try
        {
            ResolvePool.Processors = 1;
            for (var i = 0; i < 4; i++) Assert.False(ResolvePool.TryAcquire());
            Assert.Equal(created, ResolvePool.Created);
        }
        finally
        {
            ResolvePool.Processors = saved;
        }

        if (saved < 2) return;
        Assert.True(ResolvePool.TryAcquire());
        ResolvePool.Release();
    }

    [Fact]
    public void OneProcessor_RunsPooledSizeColumnsInlineWithIdenticalResults()
    {
        const int count = ResolvePool.ColumnThreshold + 37;
        var world = World.New();
        Grid.New(world, 8, 0f, 0f, 256f);
        var layer = Layer.New(world);
        var box = Stamp.Box(12, 12, 60);
        for (var y = 8; y < 256; y += 16)
        for (var x = 8; x < 256; x += 16)
            Assert.True(World.Place(world, layer, x, y, box, 5) >= 0);
        World.Process(world);

        var xs = new float[count];
        var ys = new float[count];
        for (var i = 0; i < count; i++)
        {
            xs[i] = i * 0.0301f % 255f;
            ys[i] = i * 0.0173f % 255f;
        }

        var words = (count + 63) >> 6;
        var inline = new short[count];
        var inlineWhole = new ulong[words];
        var pooled = new short[count];
        var pooledWhole = new ulong[words];
        var saved = ResolvePool.Processors;
        var created = ResolvePool.Created;
        fixed (float* xp = xs, yp = ys)
        fixed (short* ip = inline, pp = pooled)
        fixed (ulong* iw = inlineWhole, pw = pooledWhole)
        {
            try
            {
                ResolvePool.Processors = 1;
                World.SenseColumn(world, layer, xp, yp, count, ip, iw);
                Assert.Equal(created, ResolvePool.Created);
            }
            finally
            {
                ResolvePool.Processors = saved;
            }

            World.SenseColumn(world, layer, xp, yp, count, pp, pw);
        }

        Assert.True(inline.AsSpan().SequenceEqual(pooled));
        Assert.True(inlineWhole.AsSpan().SequenceEqual(pooledWhole));
        World.Free(world);
        Assert.True(Stamp.Free(box));
    }

    [Fact]
    public void SingleProcessorHost_NeverCreatesThePool()
    {
        if (Environment.ProcessorCount != 1) return;
        Assert.False(ResolvePool.Created);
        Assert.False(ResolvePool.TryAcquire());
        Assert.False(ResolvePool.Created);
    }
}
