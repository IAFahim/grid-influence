using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Xunit;

namespace Gi.Tests;

public sealed unsafe class AllocatorHookTests
{
    private static long _allocations;
    private static long _frees;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void* CountingAllocate(nuint bytes, nuint alignment)
    {
        Interlocked.Increment(ref _allocations);
        return NativeMemory.AlignedAlloc(bytes == 0 ? 1 : bytes, alignment < 8 ? 8 : alignment);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void CountingFree(void* pointer)
    {
        Interlocked.Increment(ref _frees);
        NativeMemory.AlignedFree(pointer);
    }

    private static void DrainLiveWorlds()
    {
        Span<byte> ids = stackalloc byte[World.MaxWorlds];
        var count = World.CopyWorldIds(ids);
        for (var i = 0; i < count; i++) World.Free(ids[i]);
    }

    private static void DrainStamps()
    {
        DrainLiveWorlds();
        for (var id = 1; id < StampCatalog.Count; id++)
            if (StampCatalog.Get((byte)id)->Live != 0) Stamp.Free((byte)id);
        World.Teardown();
    }

    [Fact]
    public void CountingHookSeesEveryAllocationAndFreesThemAll()
    {
        DrainStamps();
        _allocations = 0;
        _frees = 0;
        Assert.True(World.SetAllocator(&CountingAllocate, &CountingFree));

        var world = World.New();
        Grid.New(world, 8, 0f, 0f, 256f);
        Grid.New(world, 6, 0f, 0f, 64f);
        var persistent = Layer.New(world);
        var transient = Layer.New(world, Layer.Residency.Transient, Layer.Precision.Exact);
        var derived = Layer.Sum(world, persistent, 1, persistent, 0);
        var box = Stamp.Box(8, 8, 40);
        var tent = Stamp.Tent(12, 12, 40);
        var cone = Stamp.Cone(8, 30, 90);
        var samples = new sbyte[8 * 8];
        for (var i = 0; i < samples.Length; i++) samples[i] = (sbyte)(i % 8);
        byte raster;
        fixed (sbyte* data = samples)
        {
            raster = Stamp.New(data, 8, 8);
        }

        const int count = 64;
        var ids = new World.SourceId[count];
        var xs = new float[count];
        var ys = new float[count];
        for (var i = 0; i < count; i++)
        {
            xs[i] = (i * 17.3f) % 240f + 12f;
            ys[i] = (i * 9.1f) % 240f + 12f;
            ids[i] = World.Place(world, persistent, xs[i], ys[i], i % 2 == 0 ? box : tent, 3);
        }

        World.Process(world);
        World.Splat(world, transient, xs, ys, cone, 4);
        World.Process(world);

        for (var warm = 0; warm < 12; warm++)
        {
            var step = (warm & 1) == 0 ? 8f : -8f;
            for (var i = 0; i < count; i++)
            {
                xs[i] += step;
                ys[i] += step * 0.5f;
            }

            fixed (World.SourceId* idPointer = ids)
            fixed (float* xPointer = xs)
            fixed (float* yPointer = ys)
            {
                World.Sync(world, idPointer, xPointer, yPointer, count);
            }

            World.Splat(world, transient, xs, ys, raster, 5);
            World.Process(world);
            World.TrySense(world, persistent, 128f, 128f, ids[0], out _);
            World.TrySenseArea(world, persistent, 128f, 128f, 24f, out _);
            Assert.True(World.Query(world, 0, derived, 0, 0, 64, 64) >= 0);
        }

        var hookedAllocations = _allocations;
        var hookedFrees = _frees;
        Assert.True(hookedAllocations > 0);

        for (var frame = 0; frame < 8; frame++)
        {
            var step = (frame & 1) == 0 ? 8f : -8f;
            for (var i = 0; i < count; i++)
            {
                xs[i] += step;
                ys[i] += step * 0.5f;
            }

            fixed (World.SourceId* idPointer = ids)
            fixed (float* xPointer = xs)
            fixed (float* yPointer = ys)
            {
                World.Sync(world, idPointer, xPointer, yPointer, count);
            }

            World.Splat(world, transient, xs, ys, raster, 5);
            World.Process(world);
            World.TrySense(world, persistent, 128f, 128f, ids[0], out _);
            World.TrySenseArea(world, persistent, 128f, 128f, 24f, out _);
        }

        Assert.Equal(hookedAllocations, _allocations);
        Assert.Equal(hookedFrees, _frees);

        Assert.True(World.Free(world));
        Assert.True(Stamp.Free(box));
        Assert.True(Stamp.Free(tent));
        Assert.True(Stamp.Free(cone));
        Assert.True(Stamp.Free(raster));
        Assert.True(World.Teardown());

        Assert.Equal(_allocations, _frees);

        Assert.True(World.SetAllocator(null, null));
        var defaultAllocations = _allocations;
        var defaultFrees = _frees;
        var plain = World.New();
        Grid.New(plain, 6, 0f, 0f, 64f);
        var plainLayer = Layer.New(plain);
        var plainStamp = Stamp.Box(3, 3, 10);
        World.Place(plain, plainLayer, 10.5f, 10.5f, plainStamp, 1);
        World.Process(plain);
        Assert.Equal(10, World.QueryAt(plain, 0, plainLayer, 10.5f, 10.5f));
        Assert.True(World.Free(plain));
        Assert.True(Stamp.Free(plainStamp));
        Assert.True(World.Teardown());
        Assert.Equal(defaultAllocations, _allocations);
        Assert.Equal(defaultFrees, _frees);
    }

    [Fact]
    public void SetAllocatorIsRefusedWhileGiHoldsNativeMemory()
    {
        DrainStamps();
        var world = World.New();
        Grid.New(world, 6, 0f, 0f, 64f);
        var layer = Layer.New(world);
        var stamp = Stamp.Box(3, 3, 10);
        World.Place(world, layer, 10.5f, 10.5f, stamp, 1);
        World.Process(world);

        _allocations = 0;
        _frees = 0;
        Assert.False(World.SetAllocator(&CountingAllocate, &CountingFree));
        Assert.True(NativeHeap.HookAllocate == null);
        Assert.True(NativeHeap.HookFree == null);

        Assert.True(World.Free(world));
        Assert.True(Stamp.Free(stamp));
        Assert.True(World.Teardown());
        Assert.True(World.SetAllocator(&CountingAllocate, &CountingFree));

        var hooked = World.New();
        Grid.New(hooked, 6, 0f, 0f, 64f);
        var hookedLayer = Layer.New(hooked);
        World.Place(hooked, hookedLayer, 10.5f, 10.5f, Stamp.Box(3, 3, 10), 1);
        World.Process(hooked);
        Assert.Equal(10, World.QueryAt(hooked, 0, hookedLayer, 10.5f, 10.5f));
        Assert.True(World.Free(hooked));
        Assert.True(World.Teardown());
        Assert.True(_allocations > 0);
        Assert.Equal(_allocations, _frees);
        Assert.True(World.SetAllocator(null, null));
    }

    [Fact]
    public void SetAllocatorRejectsAHalfNullInstall()
    {
        DrainStamps();
        Assert.Throws<ArgumentException>(() => World.SetAllocator(&CountingAllocate, null));
        Assert.Throws<ArgumentException>(() => World.SetAllocator(null, &CountingFree));
        Assert.True(NativeHeap.HookAllocate == null);
        Assert.True(NativeHeap.HookFree == null);
    }
}
