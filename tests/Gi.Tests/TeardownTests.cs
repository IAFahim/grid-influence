using System.Runtime.InteropServices;
using Xunit;

namespace Gi.Tests;

public sealed unsafe class TeardownTests
{
    private const int OpenReadOnly = 0;

    [DllImport("libc", SetLastError = true)]
    private static extern int open(byte* path, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int read(int descriptor, byte* buffer, nuint count);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int descriptor);

    private static int ResidentSetKb()
    {
        var path = stackalloc byte[18];
        var text = "/proc/self/status"u8;
        text.CopyTo(new Span<byte>(path, text.Length));
        path[text.Length] = 0;

        var buffer = stackalloc byte[4096];
        var descriptor = open(path, OpenReadOnly);
        if (descriptor < 0) throw new InvalidOperationException("Cannot read /proc/self/status.");

        var total = 0;
        int readCount;
        while (total < 4096 && (readCount = read(descriptor, buffer + total, (nuint)(4096 - total))) > 0) total += readCount;
        close(descriptor);

        var span = new ReadOnlySpan<byte>(buffer, total);
        var marker = "VmRSS:"u8;
        var line = span.IndexOf(marker);
        if (line < 0) throw new InvalidOperationException("VmRSS is missing from /proc/self/status.");

        var cursor = line + marker.Length;
        while (cursor < span.Length && (span[cursor] == (byte)' ' || span[cursor] == (byte)'\t')) cursor++;
        var value = 0;
        while (cursor < span.Length && span[cursor] >= (byte)'0' && span[cursor] <= (byte)'9')
        {
            value = value * 10 + (span[cursor] - (byte)'0');
            cursor++;
        }

        return value;
    }

    private static sbyte[] RasterSamples()
    {
        var samples = new sbyte[8 * 8];
        for (var i = 0; i < samples.Length; i++) samples[i] = (sbyte)(i % 8);
        return samples;
    }

    private static void WarmMeasurementCalls()
    {
        ResidentSetKb();
        ResidentSetKb();
    }

    private static void DrainLiveWorlds()
    {
        Span<byte> ids = stackalloc byte[World.MaxWorlds];
        var count = World.CopyWorldIds(ids);
        for (var i = 0; i < count; i++) World.Free(ids[i]);
    }

    [Fact]
    public void Teardown_IsRefusedWhileAWorldIsLive()
    {
        DrainLiveWorlds();
        var world = World.New();
        var grid = Grid.New(world, 6, 0f, 0f, 64f);
        var layer = Layer.New(world);
        var stamp = Stamp.Box(3, 3, 10);
        World.Place(world, layer, 10.5f, 10.5f, stamp, 1);
        World.Process(world);

        Assert.False(World.Teardown());
        Assert.Equal(10, World.QueryAt(world, grid, layer, 10.5f, 10.5f));
        Assert.True(Stamp.TryGetSpec(stamp, out _));
        var ids = new byte[1];
        Assert.Equal(1, World.CopyWorldIds(ids));
        Assert.Equal(world, ids[0]);

        Assert.True(World.Free(world));
        Assert.True(World.Teardown());
        Assert.False(Stamp.TryGetSpec(stamp, out _));
    }

    [Fact]
    public void Teardown_ReturnsTheStampCatalogToPristine()
    {
        DrainLiveWorlds();
        var box = Stamp.Box(9, 9, 20);
        var tent = Stamp.Tent(12, 12, 40);
        var cone = Stamp.Cone(8, 30, 90);
        var samples = RasterSamples();
        fixed (sbyte* data = samples)
        {
            Assert.NotEqual(0, Stamp.New(data, 8, 8));
        }

        Assert.True(World.Teardown());
        Assert.Equal(1, StampCatalog.Count);
        Assert.Equal(0, NativeHeap.Calls);
        Assert.False(Stamp.TryGetSpec(box, out _));
        Assert.False(Stamp.TryGetSpec(tent, out _));
        Assert.False(Stamp.TryGetSpec(cone, out _));

        var world = World.New();
        Grid.New(world, 6, 0f, 0f, 64f);
        var layer = Layer.New(world);
        var fresh = Stamp.Box(3, 3, 10);
        Assert.Equal(1, fresh);
        World.Place(world, layer, 10.5f, 10.5f, fresh, 1);
        World.Process(world);
        Assert.Equal(10, World.QueryAt(world, 0, layer, 10.5f, 10.5f));
        Assert.True(World.Free(world));
        Assert.True(World.Teardown());
    }

    [Fact]
    public void Teardown_LeavesStaleHandlesRefused()
    {
        DrainLiveWorlds();
        var world = World.New();
        Grid.New(world, 6, 0f, 0f, 64f);
        var layer = Layer.New(world);
        var stamp = Stamp.Box(3, 3, 10);
        World.Place(world, layer, 10.5f, 10.5f, stamp, 1);
        World.Process(world);
        var token = World.Capture(world);
        Assert.True(token.IsValid);
        Assert.True(World.Free(world));

        Assert.True(World.Teardown());

        Assert.Equal(0, World.Revision(world));
        Assert.False(World.Capture(world).IsValid);
        Assert.Equal(default, World.Place(world, layer, 10.5f, 10.5f, stamp, 1, out var status));
        Assert.Equal(World.WriteStatus.InvalidLayer, status);
        Assert.Equal(0, World.QueryAt(world, 0, layer, 10.5f, 10.5f));
        Assert.Equal(World.BatchStatus.InvalidWorld, World.Process(token));
        Assert.Throws<ArgumentOutOfRangeException>(() => Grid.New(world, 6, 0f, 0f, 64f));
    }

    [Fact]
    public void Teardown_KeepsTokensFromBeforeTheTeardownRefused()
    {
        DrainLiveWorlds();
        var world = World.New();
        Grid.New(world, 6, 0f, 0f, 64f);
        var layer = Layer.New(world);
        var stamp = Stamp.Box(3, 3, 10);
        World.Place(world, layer, 10.5f, 10.5f, stamp, 1);
        World.Process(world);
        var token = World.Capture(world);
        Assert.True(World.Free(world));
        Assert.True(World.Teardown());

        var restart = World.New();
        Assert.Equal(world, restart);
        Grid.New(restart, 6, 0f, 0f, 64f);
        var restartLayer = Layer.New(restart);
        World.Place(restart, restartLayer, 10.5f, 10.5f, Stamp.Box(3, 3, 10), 1);
        World.Process(restart);

        var ids = stackalloc World.SourceId[1];
        var xs = stackalloc float[1];
        var ys = stackalloc float[1];
        xs[0] = 10.5f;
        ys[0] = 10.5f;
        Assert.Equal(World.BatchStatus.StaleRevision, World.Sync(token, ids, xs, ys, 1));
        Assert.Equal(World.BatchStatus.StaleRevision, World.Process(token));

        Assert.True(World.Free(restart));
        Assert.True(World.Teardown());
    }

    [Fact]
    public void Teardown_ReportsZeroMemoryForEveryPreviouslyLiveWorld()
    {
        DrainLiveWorlds();
        var first = World.New();
        var second = World.New();
        Grid.New(first, 6, 0f, 0f, 64f);
        Grid.New(second, 8, 0f, 0f, 256f);
        var firstLayer = Layer.New(first);
        var secondLayer = Layer.New(second);
        var stamp = Stamp.Box(8, 8, 40);
        World.Place(first, firstLayer, 10.5f, 10.5f, stamp, 1);
        World.Place(second, secondLayer, 128f, 128f, stamp, 2);
        World.Process(first);
        World.Process(second);

        World.Memory(first, out var firstBytes, out var firstTiles, out var firstPages, out _, out var firstSources);
        World.Memory(second, out var secondBytes, out var secondTiles, out _, out _, out var secondSources);
        Assert.NotEqual(0L, firstBytes);
        Assert.NotEqual(0, firstTiles);
        Assert.NotEqual(0, firstPages);
        Assert.Equal(1, firstSources);
        Assert.NotEqual(0L, secondBytes);
        Assert.NotEqual(0, secondTiles);
        Assert.Equal(1, secondSources);

        Assert.True(World.Free(first));
        Assert.True(World.Free(second));
        Assert.True(World.Teardown());

        var handles = stackalloc byte[2];
        handles[0] = first;
        handles[1] = second;
        for (var i = 0; i < 2; i++)
        {
            World.Memory(handles[i], out var bytes, out var tiles, out var pages, out var kernels, out var sources);
            Assert.Equal(0L, bytes);
            Assert.Equal(0, tiles);
            Assert.Equal(0, pages);
            Assert.Equal(0, kernels);
            Assert.Equal(0, sources);
        }
    }

    [Fact]
    public void Teardown_IsIdempotentAndRestartMatchesAFirstStart()
    {
        DrainLiveWorlds();
        var world = World.New();
        Grid.New(world, 8, 0f, 0f, 256f);
        var food = Layer.New(world);
        var threat = Layer.New(world);
        var derived = Layer.Sum(world, food, 1, threat, -2);
        var stamp = Stamp.Box(8, 8, 40);
        World.Place(world, threat, 128f, 128f, stamp, 2);
        World.Process(world);
        var before = World.Query(world, 0, derived, 0, 0, 256, 256);
        Assert.True(before < 0);
        Assert.True(World.Free(world));
        Assert.True(World.Teardown());
        Assert.True(World.Teardown());

        var restart = World.New();
        Assert.Equal(world, restart);
        Assert.Equal(0, Grid.New(restart, 8, 0f, 0f, 256f));
        var restartFood = Layer.New(restart);
        var restartThreat = Layer.New(restart);
        var restartDerived = Layer.Sum(restart, restartFood, 1, restartThreat, -2);
        var restartStamp = Stamp.Box(8, 8, 40);
        World.Place(restart, restartThreat, 128f, 128f, restartStamp, 2);
        World.Process(restart);
        Assert.Equal(before, World.Query(restart, 0, restartDerived, 0, 0, 256, 256));
        Assert.True(World.Free(restart));
        Assert.True(World.Teardown());
    }

    [Fact]
    public void Teardown_AlternatedWithUseKeepsRssFlatAcross1000Cycles()
    {
        DrainLiveWorlds();
        const int cycles = 1000;
        const int rssToleranceKb = 2048;

        var samples = RasterSamples();
        var ids = new World.SourceId[48];
        var xs = new float[48];
        var ys = new float[48];

        fixed (sbyte* raster = samples)
        fixed (World.SourceId* idPointer = ids)
        fixed (float* xPointer = xs)
        fixed (float* yPointer = ys)
        {
            UseAndTeardown(raster, idPointer, xPointer, yPointer, out _);
            UseAndTeardown(raster, idPointer, xPointer, yPointer, out var firstFree);
            for (var warm = 0; warm < 199; warm++) UseAndTeardown(raster, idPointer, xPointer, yPointer, out _);
            WarmMeasurementCalls();

            var start = ResidentSetKb();
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            long midFree = 0;
            long lastFree = 0;
            var midRss = 0;
            for (var cycle = 0; cycle < cycles; cycle++)
            {
                UseAndTeardown(raster, idPointer, xPointer, yPointer, out var free);
                if (cycle == 499)
                {
                    midFree = free;
                    midRss = ResidentSetKb();
                }

                if (cycle == cycles - 1) lastFree = free;
            }

            var gcEnd = GC.GetAllocatedBytesForCurrentThread();
            var end = ResidentSetKb();
            Assert.Equal(firstFree, midFree);
            Assert.Equal(firstFree, lastFree);
            Assert.Equal(0L, gcEnd - allocated);
            Assert.InRange(midRss - start, -rssToleranceKb, rssToleranceKb);
            Assert.InRange(end - start, -rssToleranceKb, rssToleranceKb);
        }
    }

    private static void UseAndTeardown(sbyte* raster, World.SourceId* ids, float* xs, float* ys, out long callsAtFree)
    {
        var world = World.New();
        Grid.New(world, 8, 0f, 0f, 256f);
        var food = Layer.New(world);
        var threat = Layer.New(world);
        var derived = Layer.Sum(world, food, 1, threat, -2);
        var box = Stamp.Box(8, 8, 40);
        var tent = Stamp.Tent(12, 12, 40);
        var cone = Stamp.Cone(8, 30, 90);
        var rasterStamp = Stamp.New(raster, 8, 8);
        for (var i = 0; i < 48; i++)
        {
            xs[i] = (i * 37.7f) % 240f + 8f;
            ys[i] = (i * 19.3f) % 240f + 8f;
            var shape = i % 4;
            ids[i] = World.Place(world, threat, xs[i], ys[i],
                shape == 0 ? box : shape == 1 ? tent : shape == 2 ? cone : rasterStamp, 3);
        }

        World.Process(world);
        for (var i = 0; i < 48; i++)
        {
            xs[i] += 4f;
            ys[i] += 2f;
        }

        World.Sync(world, ids, xs, ys, 48);
        World.Process(world);
        if (World.Query(world, 0, derived, 0, 0, 256, 256) >= 0) throw new InvalidOperationException("Derived sum must be negative.");

        World.Free(world);
        callsAtFree = NativeHeap.Calls;
        if (!World.Teardown()) throw new InvalidOperationException("Teardown was refused.");
        if (NativeHeap.Calls != 0) throw new InvalidOperationException("NativeHeap.Calls did not return to pristine.");
    }
}
