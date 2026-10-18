using Xunit;

namespace Gi.Tests;

public sealed unsafe class LayerHashTests
{
    private const ulong Offset = 0xcbf29ce484222325ul;
    private const ulong Prime = 0x100000001b3ul;

    private static ulong TileOracle(int tile, short[] page, int offset, int stride)
    {
        var any = false;
        for (var y = 0; y < 32 && !any; y++)
            for (var x = 0; x < 32; x++)
                if (page[offset + y * stride + x] != 0)
                {
                    any = true;
                    break;
                }

        if (!any) return 0;

        var hash = (Offset ^ (ulong)tile) * Prime;
        for (var y = 0; y < 32; y++)
        for (var x = 0; x < 32; x += 4)
        {
            var row = offset + y * stride + x;
            var low = (uint)((ushort)page[row] | (ushort)page[row + 1] << 16);
            var high = (uint)((ushort)page[row + 2] | (ushort)page[row + 3] << 16);
            var word = low | (ulong)high << 32;
            hash = (hash ^ word) * Prime;
        }

        return hash;
    }

    private static ulong Recompute(byte world, byte grid, byte layer, int size, short[] page)
    {
        fixed (short* pointer = page)
        {
            World.QueryRegion(world, grid, layer, 0, 0, size, size, pointer);
        }

        var tiles = size >> 5;
        var hash = 0ul;
        for (var ty = 0; ty < tiles; ty++)
        for (var tx = 0; tx < tiles; tx++)
            hash ^= TileOracle(ty * tiles + tx, page, ty * 32 * size + tx * 32, size);
        return hash;
    }

    [Fact]
    public void LayerHashIsZeroForEmptyAndAbsentHandles()
    {
        var world = World.New();
        Grid.New(world, 6, 0f, 0f, 64f);
        var layer = Layer.New(world);
        var stamp = Stamp.Box(4, 4, 20);

        Assert.Equal(0ul, World.LayerHash(world, 0, layer));
        World.Place(world, layer, 10f, 10f, stamp, 3);
        World.Process(world);
        Assert.NotEqual(0ul, World.LayerHash(world, 0, layer));
        Assert.Equal(0ul, World.LayerHash(World.MaxWorlds, 0, layer));
        Assert.Equal(0ul, World.LayerHash(world, 1, layer));
        Assert.Equal(0ul, World.LayerHash(world, 0, (byte)(layer + 1)));
        World.Clear(world);
        Assert.Equal(0ul, World.LayerHash(world, 0, layer));
        World.Process(world);
        Assert.Equal(0ul, World.LayerHash(world, 0, layer));
        Assert.True(World.Free(world));
        Assert.True(Stamp.Free(stamp));
    }

    [Fact]
    public void LayerHashMatchesAFullRecomputeAcrossOperations()
    {
        var world = World.New();
        Grid.New(world, 7, 0f, 0f, 128f);
        Grid.New(world, 6, 4f, 8f, 64f);
        var food = Layer.New(world);
        var threat = Layer.New(world);
        var derived = Layer.Sum(world, food, 1, threat, -1);
        var transient = Layer.New(world, Layer.Residency.Transient, Layer.Precision.Half);
        var box = Stamp.Box(8, 8, 40);
        var cone = Stamp.Cone(6, 60, 180);
        var page = new short[128 * 128];

        var ids = new World.SourceId[24];
        for (var i = 0; i < ids.Length; i++)
            ids[i] = World.Place(world, i % 2 == 0 ? food : threat,
                4f + i * 5.1f, 6f + i * 3.7f, i % 3 == 0 ? box : cone, (sbyte)(1 + i % 5));
        World.Process(world);

        for (var frame = 0; frame < 24; frame++)
        {
            for (var i = 0; i < ids.Length; i += 3)
            {
                World.Move(world, ids[i], 4f + (i * 5.1f + frame * 2.3f) % 120f, 6f + (i * 3.7f + frame * 1.1f) % 120f);
                World.Turn(world, ids[i], frame * 0.05f);
                if (i % 9 == 0) World.SetGain(world, ids[i], (sbyte)(1 + (frame + i) % 7));
            }

            if (frame % 6 == 5) World.Remove(world, ids[frame % ids.Length]);
            World.Splat(world, transient, new[] { 20f + frame * 3f, 90f - frame * 1.5f },
                new[] { 30f + frame * 2f, 70f + frame }, box, 4);
            World.Process(world);

            for (var grid = 0; grid < 2; grid++)
            {
                var size = grid == 0 ? 128 : 64;
                byte[] layers = [food, threat, derived, transient];
                foreach (var layer in layers)
                    Assert.Equal(Recompute(world, (byte)grid, layer, size, page), World.LayerHash(world, (byte)grid, layer));
            }
        }

        foreach (var id in ids) World.Remove(world, id);
        World.Process(world);
        foreach (var layer in new[] { food, threat, derived, transient })
            Assert.Equal(0ul, World.LayerHash(world, 0, layer));

        Assert.True(World.Free(world));
        Assert.True(Stamp.Free(box));
        Assert.True(Stamp.Free(cone));
    }

    [Fact]
    public void LayerHashMatchesAFullRecomputeAfterEveryFuzzStream()
    {
        var profiles = new[] { StreamProfile.Gate, StreamProfile.Corpus, StreamProfile.Kinds, StreamProfile.Transient };
        for (var i = 0; i < 40; i++)
        {
            var setup = StreamGenerator.Generate(2_700_000 + i, (PrecisionMode)(i % 5), profiles[i % 4]);
            StreamRunner.Run(setup, setup.Commands, onPage: (world, _, grid, layer, page) =>
            {
                var size = 1 << setup.Grids[grid].Power;
                Assert.Equal(Recompute(world, grid, layer, size, page), World.LayerHash(world, grid, layer));
            });
        }
    }

    [Fact]
    public void LayerHashAgreesBetweenIdenticallyDrivenWorldsAndSplitsOnDivergence()
    {
        byte Build()
        {
            var world = World.New();
            Grid.New(world, 6, 0f, 0f, 64f);
            Layer.New(world);
            Layer.New(world);
            return world;
        }

        var first = Build();
        var second = Build();
        var stamp = Stamp.Tent(10, 10, 50);
        for (var frame = 0; frame < 20; frame++)
        {
            for (var w = 0; w < 2; w++)
            {
                var world = w == 0 ? first : second;
                World.Place(world, (byte)(frame % 2), 8f + (frame * 7.3f) % 50f, 9f + (frame * 4.9f) % 50f, stamp, 3);
            }

            World.Process(first);
            World.Process(second);
            Assert.Equal(World.LayerHash(first, 0, 0), World.LayerHash(second, 0, 0));
            Assert.Equal(World.LayerHash(first, 0, 1), World.LayerHash(second, 0, 1));
        }

        World.Place(second, 0, 40f, 40f, stamp, 5);
        World.Process(first);
        World.Process(second);
        Assert.NotEqual(World.LayerHash(first, 0, 0), World.LayerHash(second, 0, 0));

        Assert.True(World.Free(first));
        Assert.True(World.Free(second));
        Assert.True(Stamp.Free(stamp));
    }
}
