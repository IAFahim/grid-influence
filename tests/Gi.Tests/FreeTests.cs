using System.Diagnostics;
using Xunit;

namespace Gi.Tests;

public sealed class FreeTests
{
    private static long PrivateBytes()
    {
        using var process = Process.GetCurrentProcess();
        return process.PrivateMemorySize64;
    }

    [Fact]
    public void Free_RetiresTheHandle()
    {
        var world = World.New();
        var grid = Grid.New(world, 6, 0f, 0f, 64f);
        var layer = Layer.New(world);
        var stamp = Stamp.Box(3, 3, 10);
        var source = World.Place(world, layer, 10.5f, 10.5f, stamp, 1);
        World.Process(world);
        Assert.Equal(10, World.QueryAt(world, grid, layer, 10.5f, 10.5f));

        Assert.True(World.Free(world));
        Assert.False(World.Free(world));

        Assert.Equal(default, World.Place(world, layer, 10.5f, 10.5f, stamp, 1));
        Assert.Equal(0, World.QueryAt(world, grid, layer, 10.5f, 10.5f));
        Assert.Equal(0, World.Query(world, grid, layer, 10, 10));
        Assert.Equal(0, World.Query(world, grid, layer, 0, 0, 64, 64));
        Assert.Equal(0, World.Tick(world));
        Assert.False(World.Covers(world, 10.5f, 10.5f));
        World.Move(world, source, 20f, 20f);
        World.SetGain(world, source, 2);
        World.Remove(world, source);
        World.Process(world);
        World.Clear(world);
        World.Record(world);
        World.Rewind(world);
        Assert.Throws<ArgumentOutOfRangeException>(() => Grid.New(world, 6, 0f, 0f, 64f));
        Assert.Throws<ArgumentOutOfRangeException>(() => Layer.New(world));
        Assert.True(Stamp.Free(stamp));
    }

    [Fact]
    public void Free_ReleasesTheLowestHandleToTheNextWorld()
    {
        var first = World.New();
        Grid.New(first, 6, 0f, 0f, 64f);
        Layer.New(first);
        var stamp = Stamp.Box(3, 3, 10);
        World.Place(first, 0, 10.5f, 10.5f, stamp, 1);
        World.Process(first);
        Assert.True(World.Free(first));

        var second = World.New();
        Assert.True(second <= first);
        Assert.Equal(0, World.Tick(second));
        Assert.Equal(default, World.Place(second, 0, 10.5f, 10.5f, stamp, 1));
        Assert.Equal(0, Grid.New(second, 6, 0f, 0f, 64f));
        Assert.Equal(0, Layer.New(second));
        Assert.Equal(0, World.QueryAt(second, 0, 0, 10.5f, 10.5f));
        Assert.True(World.Free(second));
        Assert.True(Stamp.Free(stamp));
    }

    [Fact]
    public void Free_LeavesOtherWorldsUntouched()
    {
        var stamp = Stamp.Box(3, 3, 10);
        var kept = World.New();
        var keptGrid = Grid.New(kept, 6, 0f, 0f, 64f);
        var keptLayer = Layer.New(kept);
        var keptSource = World.Place(kept, keptLayer, 10.5f, 10.5f, stamp, 1);
        var freed = World.New();
        Grid.New(freed, 6, 0f, 0f, 64f);
        Layer.New(freed);
        World.Place(freed, 0, 30.5f, 30.5f, stamp, 1);
        World.Process(kept);
        World.Process(freed);

        Assert.True(World.Free(freed));

        Assert.Equal(10, World.QueryAt(kept, keptGrid, keptLayer, 10.5f, 10.5f));
        World.Move(kept, keptSource, 40.5f, 40.5f);
        World.Process(kept);
        Assert.Equal(0, World.QueryAt(kept, keptGrid, keptLayer, 10.5f, 10.5f));
        Assert.Equal(10, World.QueryAt(kept, keptGrid, keptLayer, 40.5f, 40.5f));
        Assert.True(World.Free(kept));
        Assert.True(Stamp.Free(stamp));
    }

    [Fact]
    public void Free_UnblocksStampsTheWorldUsed()
    {
        var world = World.New();
        Grid.New(world, 6, 0f, 0f, 64f);
        Layer.New(world);
        var stamp = Stamp.Box(3, 3, 10);
        var source = World.Place(world, 0, 10.5f, 10.5f, stamp, 1);
        World.Process(world);
        World.Remove(world, source);
        Assert.False(Stamp.Free(stamp));

        Assert.True(World.Free(world));
        Assert.True(Stamp.Free(stamp));
    }

    [Fact]
    public void Free_ReleasesEveryKindOfWorldState()
    {
        var world = World.New();
        Grid.New(world, 9, 0f, 0f, 512f);
        Grid.New(world, 6, 0f, 0f, 512f);
        var food = Layer.New(world);
        var threat = Layer.New(world);
        Layer.Sum(world, food, 1, threat, -2);
        Layer.Max(world, food, threat);
        Layer.Mask(world, food, threat, short.MinValue, 30);
        var samples = new sbyte[16 * 16];
        Array.Fill(samples, (sbyte)7);
        var stamps = new[]
        {
            Stamp.Box(9, 9, 20), Stamp.Tent(12, 12, 40), Stamp.Bell(12, 12, 40), Stamp.Disk(8, 30),
            Stamp.Cone(8, 30, 90), Stamp.Dome(8, 30), Stamp.New(samples, 16, 16)
        };
        var first = default(World.SourceId);
        for (var i = 0; i < 300; i++)
        {
            var id = World.Place(world, (byte)(i % 2), i * 37 % 500, i * 91 % 500, stamps[i % stamps.Length],
                (sbyte)(1 + i % 4));
            Assert.True(id != default);
            if (first == default) first = id;
        }

        World.Record(world);
        World.Process(world);
        var fading = World.Place(world, food, 250f, 250f, stamps[1], 4);
        World.Fade(world, fading, 0, 10);
        World.Expire(world, fading, 10);
        World.Turn(world, first, 1f);
        World.Scale(world, first, 2f);
        World.Move(world, first, 5f, 5f);
        World.Process(world);
        World.Move(world, first, 6f, 6f);

        Assert.True(World.Free(world));
        foreach (var stamp in stamps) Assert.True(Stamp.Free(stamp));
    }

    [Fact]
    public void RepeatedCreationAndFree_NeverExhaustsHandlesOrMemory()
    {
        var box = Stamp.Box(256, 256, 3);
        var baseline = PrivateBytes();
        for (var round = 0; round < 300; round++)
        {
            var world = World.New();
            Grid.New(world, 10, 0f, 0f, 1024f);
            var layer = Layer.New(world);
            Layer.Sum(world, layer, 1);
            for (var i = 0; i < 16; i++)
            {
                var column = i % 4;
                var row = i / 4;
                World.Place(world, layer, 128f + column * 256f, 128f + row * 256, box, 1);
            }

            World.Process(world);
            Assert.True(World.Free(world));
        }

        Assert.True(PrivateBytes() - baseline < 256L << 20);
        Assert.True(Stamp.Free(box));
    }
}
