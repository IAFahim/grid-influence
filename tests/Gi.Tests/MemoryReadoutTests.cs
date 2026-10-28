using Xunit;

namespace Gi.Tests;

public sealed unsafe class MemoryReadoutTests
{
    private const int CoarsePower = 8;
    private const int FinePower = 10;
    private const float CoarseSize = 1 << CoarsePower;
    private const float FineSize = 1 << FinePower;
    private const int CoarseCells = 1 << CoarsePower;
    private const int FineCells = 1 << FinePower;

    private static void Read(byte world, out long storeBytes, out int liveTiles, out int pages, out int kernelEntries,
        out int liveSources)
    {
        World.Memory(world, out storeBytes, out liveTiles, out pages, out kernelEntries, out liveSources);
    }

    private static void WalkInternals(
        byte world, out long storeBytes, out int directoryTiles, out int counterTiles, out int pages,
        out int liveSources)
    {
        storeBytes = 0;
        directoryTiles = 0;
        counterTiles = 0;
        pages = 0;
        liveSources = 0;
        var w = World.GetContext(world);
        Assert.True(w != null);
        for (var gi = 0; gi < w->GridCount; gi++)
        {
            var g = w->Grids + gi;
            for (var li = 0; li < w->LayerCount; li++)
            {
                var ld = g->Layers + li;
                storeBytes += ld->Store.Bytes;
                counterTiles += ld->LivePages;
                var directory = ld->Directory;
                if (directory == null) continue;

                for (var tile = 0; tile < g->TileCount; tile++)
                {
                    var index = directory[tile];
                    if (index < 0) continue;

                    directoryTiles++;
                    var set = (TilePlanes*)ld->Sets.Unit(index);
                    pages += 2 + (set->I >= 0 ? 1 : 0) + (set->T >= 0 ? 1 : 0) + (set->B >= 0 ? 1 : 0);
                }
            }
        }

        var s = &w->Sources;
        for (var i = 0; i < s->Count; i++)
            if (s->Alive[i] != 0)
                liveSources++;
    }

    private static float Coordinate(StampVariant* v, bool xAxis, int cell, int phase)
    {
        var origin = xAxis ? v->OriginQ8X : v->OriginQ8Y;
        return (((long)cell << 8) + phase - (((long)origin * 256) >> 8) + 0.5f) / 256f;
    }

    [Fact]
    public void EmptyWorlds_ReadZero()
    {
        var world = World.New();
        try
        {
            Read(world, out var storeBytes, out var liveTiles, out var pages, out var kernelEntries,
                out var liveSources);
            Assert.Equal(0L, storeBytes);
            Assert.Equal(0, liveTiles);
            Assert.Equal(0, pages);
            Assert.Equal(0, kernelEntries);
            Assert.Equal(0, liveSources);

            Grid.New(world, CoarsePower, 0f, 0f, CoarseSize);
            Grid.New(world, FinePower, 0f, 0f, FineSize);
            Layer.New(world);
            Layer.New(world, Layer.Residency.Transient, Layer.Precision.Exact);
            Layer.Sum(world, 0, 1);
            Read(world, out storeBytes, out liveTiles, out pages, out kernelEntries, out liveSources);
            Assert.Equal(0L, storeBytes);
            Assert.Equal(0, liveTiles);
            Assert.Equal(0, pages);
            Assert.Equal(0, kernelEntries);
            Assert.Equal(0, liveSources);
        }
        finally
        {
            World.Free(world);
        }

        Read(byte.MaxValue, out var unreachableBytes, out var unreachableTiles, out var unreachablePages,
            out var unreachableKernels, out var unreachableSources);
        Assert.Equal(0L, unreachableBytes);
        Assert.Equal(0, unreachableTiles);
        Assert.Equal(0, unreachablePages);
        Assert.Equal(0, unreachableKernels);
        Assert.Equal(0, unreachableSources);
    }

    [Fact]
    public void PopulatedWorld_MatchesInternalCounters()
    {
        var world = World.New();
        var tent = Stamp.Tent(4, 4, 40);
        var box = Stamp.Box(3, 3, 30);
        try
        {
            Grid.New(world, CoarsePower, 0f, 0f, CoarseSize);
            Grid.New(world, FinePower, 0f, 0f, FineSize);
            var persistent = Layer.New(world);
            var transient = Layer.New(world, Layer.Residency.Transient, Layer.Precision.Exact);
            var derived = Layer.Sum(world, persistent, 1);
            Assert.Equal(2, derived);

            var variant = StampCatalog.Get(tent);
            var tents = new[]
            {
                World.Place(world, persistent, Coordinate(variant, true, 40, 128), Coordinate(variant, false, 40, 128),
                    tent, 3),
                World.Place(world, persistent, Coordinate(variant, true, 100, 128), Coordinate(variant, false, 20, 128),
                    tent, 2),
                World.Place(world, persistent, Coordinate(variant, true, 20, 128), Coordinate(variant, false, 90, 128),
                    tent, 1),
                World.Place(world, persistent, Coordinate(variant, true, 150, 128),
                    Coordinate(variant, false, 130, 128), tent, -2),
                World.Place(world, persistent, Coordinate(variant, true, 70, 128), Coordinate(variant, false, 160, 128),
                    tent, 1),
                World.Place(world, persistent, Coordinate(variant, true, 180, 128), Coordinate(variant, false, 70, 128),
                    tent, 2)
            };
            Assert.NotEqual(default, tents[0]);
            Assert.Equal(World.WriteStatus.Applied, World.Remove(world, tents[3]));
            Assert.Equal(World.WriteStatus.Applied, World.Remove(world, tents[4]));
            World.Place(world, persistent, 60.5f, 60.5f, box, 2, out var firstBoxStatus);
            World.Place(world, persistent, 170.5f, 40.5f, box, 1, out var secondBoxStatus);
            Assert.Equal(World.WriteStatus.Applied, firstBoxStatus);
            Assert.Equal(World.WriteStatus.Applied, secondBoxStatus);

            var xs = new float[20];
            var ys = new float[20];
            for (var i = 0; i < xs.Length; i++)
            {
                xs[i] = 10.5f + 7 * i;
                ys[i] = 12.5f + 5 * i;
            }

            Assert.Equal(20, World.Splat(world, transient, xs, ys, tent, 2));
            World.Process(world);

            Read(world, out var storeBytes, out var liveTiles, out var pages, out var kernelEntries,
                out var liveSources);
            WalkInternals(world, out var expectedBytes, out var directoryTiles, out var counterTiles,
                out var expectedPages, out var expectedSources);
            Assert.Equal(expectedBytes, storeBytes);
            Assert.Equal(directoryTiles, liveTiles);
            Assert.Equal(counterTiles, liveTiles);
            Assert.Equal(expectedPages, pages);
            Assert.Equal(expectedSources, liveSources);
            Assert.Equal(6, liveSources);
            Assert.True(liveTiles > 0, $"liveTiles {liveTiles}");
            Assert.True(pages > 2L * liveTiles, $"pages {pages} liveTiles {liveTiles}");
            Assert.True(storeBytes > 0);

            Assert.Equal(2, kernelEntries);
            for (var gi = 0; gi < 2; gi++)
            {
                Assert.True(TileBake.Place(
                    Coordinate(variant, true, 40, 128), Coordinate(variant, false, 40, 128), 0f, 0f, 256,
                    gi == 0 ? CoarseCells : FineCells, variant, 0, 256, Layer.Precision.Exact, out var p));
                var key = new KernelKey(tent, p.Sampling, gi, p.Fx, p.Fy, 0);
                Assert.True(World.GetContext(world)->Kernels.Find(key.Main, key.Turn) != null);
            }
        }
        finally
        {
            World.Free(world);
            Stamp.Free(tent);
            Stamp.Free(box);
        }
    }

    [Fact]
    public void Clear_DropsLiveContentAndRetainsSlabsAndKernels()
    {
        var world = World.New();
        var tent = Stamp.Tent(4, 4, 40);
        try
        {
            Grid.New(world, CoarsePower, 0f, 0f, CoarseSize);
            var persistent = Layer.New(world);
            var transient = Layer.New(world, Layer.Residency.Transient, Layer.Precision.Exact);
            World.Place(world, persistent, 40.5f, 40.5f, tent, 3);
            World.Place(world, persistent, 100.5f, 20.5f, tent, 2);
            World.Splat(world, transient, new[] { 30.5f, 90.5f }, new[] { 30.5f, 80.5f }, tent, 2);
            World.Process(world);

            Read(world, out var bytesBefore, out var tilesBefore, out var pagesBefore, out var kernelsBefore,
                out var sourcesBefore);
            Assert.True(bytesBefore > 0);
            Assert.True(tilesBefore > 0);
            Assert.True(pagesBefore > 0);
            Assert.True(kernelsBefore > 0);
            Assert.Equal(2, sourcesBefore);

            World.Clear(world);
            Read(world, out var bytesAfter, out var tilesAfter, out var pagesAfter, out var kernelsAfter,
                out var sourcesAfter);
            Assert.Equal(0, tilesAfter);
            Assert.Equal(0, pagesAfter);
            Assert.Equal(0, sourcesAfter);
            Assert.Equal(kernelsBefore, kernelsAfter);
            Assert.True(bytesAfter >= bytesBefore, $"{bytesAfter} < {bytesBefore}");
            WalkInternals(world, out var expectedBytes, out var directoryTiles, out var counterTiles,
                out var expectedPages, out var expectedSources);
            Assert.Equal(expectedBytes, bytesAfter);
            Assert.Equal(0, directoryTiles);
            Assert.Equal(0, counterTiles);
            Assert.Equal(0, expectedPages);
            Assert.Equal(0, expectedSources);
        }
        finally
        {
            World.Free(world);
            Stamp.Free(tent);
        }
    }

    [Fact]
    public void FreedWorld_ReadsZeroAndOtherWorldsAreUnaffected()
    {
        var first = World.New();
        var tent = Stamp.Tent(4, 4, 40);
        try
        {
            Grid.New(first, CoarsePower, 0f, 0f, CoarseSize);
            var layer = Layer.New(first);
            World.Place(first, layer, 40.5f, 40.5f, tent, 3);
            World.Process(first);
            Read(first, out var bytes, out var tiles, out var pages, out var kernels, out var sources);
            Assert.True(bytes > 0);
            Assert.True(tiles > 0);
            Assert.True(pages > 0);
            Assert.True(kernels > 0);
            Assert.Equal(1, sources);

            var second = World.New();
            Grid.New(second, CoarsePower, 0f, 0f, CoarseSize);
            var secondLayer = Layer.New(second);
            World.Place(second, secondLayer, 50.5f, 50.5f, tent, 1);
            World.Process(second);
            Read(second, out var secondBytes, out _, out _, out _, out var secondSources);
            Assert.True(secondBytes > 0);
            Assert.Equal(1, secondSources);

            Assert.True(World.Free(second));
            Read(second, out var freedBytes, out var freedTiles, out var freedPages, out var freedKernels,
                out var freedSources);
            Assert.Equal(0L, freedBytes);
            Assert.Equal(0, freedTiles);
            Assert.Equal(0, freedPages);
            Assert.Equal(0, freedKernels);
            Assert.Equal(0, freedSources);

            Read(first, out var bytesAgain, out var tilesAgain, out var pagesAgain, out var kernelsAgain,
                out var sourcesAgain);
            Assert.Equal(bytes, bytesAgain);
            Assert.Equal(tiles, tilesAgain);
            Assert.Equal(pages, pagesAgain);
            Assert.Equal(kernels, kernelsAgain);
            Assert.Equal(sources, sourcesAgain);

            Assert.NotEqual(default, World.Place(first, layer, 80.5f, 80.5f, tent, 1));
            Read(first, out _, out _, out _, out _, out var grownSources);
            Assert.Equal(2, grownSources);
        }
        finally
        {
            World.Free(first);
            Stamp.Free(tent);
        }
    }

    [Fact]
    public void KernelEntries_CountInsertsHonourTheFrameCapAndStayPureUnderReads()
    {
        var world = World.New();
        var tent = Stamp.Tent(4, 4, 40);
        try
        {
            var grid = Grid.New(world, FinePower, 0f, 0f, FineSize);
            var layer = Layer.New(world);
            var variant = StampCatalog.Get(tent);
            var keys = new List<KernelKey>();

            World.SourceId PlaceAt(int phase)
            {
                var x = Coordinate(variant, true, 35, phase);
                var y = Coordinate(variant, false, 41, 128);
                Assert.True(TileBake.Place(x, y, 0f, 0f, 256, FineCells, variant, 0, 256, Layer.Precision.Exact,
                    out var p));
                keys.Add(new KernelKey(tent, p.Sampling, grid, p.Fx, p.Fy, 0));
                var source = World.Place(world, layer, x, y, tent, 2);
                Assert.NotEqual(default, source);
                return source;
            }

            int PresentKeys()
            {
                var kernels = &World.GetContext(world)->Kernels;
                var present = 0;
                foreach (var key in keys)
                    if (kernels->Find(key.Main, key.Turn) != null)
                        present++;

                return present;
            }

            var first = PlaceAt(10);
            World.Process(world);
            Read(world, out _, out _, out _, out var kernelEntries, out _);
            Assert.Equal(1, kernelEntries);
            Assert.Equal(1, PresentKeys());

            var checksum = 0L;
            checksum += World.Query(world, grid, layer, 35, 41);
            var region = stackalloc short[64];
            World.QueryRegion(world, grid, layer, 33, 39, 8, 8, region);
            checksum += region[0];
            checksum += World.QueryAt(world, grid, layer, 35.5f, 41.5f);
            checksum += World.QueryMax(world, grid, layer, out var maxX, out var maxY);
            checksum += World.QueryMax(world, grid, layer, 30, 30, 16, 16, out var bestX, out var bestY);
            checksum += maxX + maxY + bestX + bestY;
            World.QueryGradient(world, grid, layer, 35.5f, 41.5f, out var gradientX, out var gradientY);
            checksum += gradientX + gradientY;
            var tiles = stackalloc int[64];
            checksum += World.ChangedTiles(world, grid, layer, 0, tiles);
            checksum += World.ChangedTiles(world, grid, layer, tiles);
            checksum += World.Covers(world, 35.5f, 41.5f) ? 1 : 0;
            checksum += World.Changed(world, layer, 35.5f, 41.5f, 16f, 0) ? 1 : 0;
            if (World.TrySense(world, layer, 35.5f, 41.5f, out var sense)) checksum += sense;
            if (World.TrySense(world, layer, 35.5f, 41.5f, first, out var excluded)) checksum += excluded;
            if (World.TrySense(world, layer, 35.5f, 41.5f, tent, 2, out var splatExcluded)) checksum += splatExcluded;
            if (World.TrySenseGradient(world, layer, 35.5f, 41.5f, out var senseGx, out var senseGy))
                checksum += (long)(senseGx + senseGy);
            if (World.TrySenseArea(world, layer, 35.5f, 41.5f, 16f, out var area)) checksum += area;
            if (World.TrySenseNearest(world, layer, 35.5f, 41.5f, 16f, 0, out var nearest, out var nearestX,
                    out var nearestY))
                checksum += nearest + (long)(nearestX + nearestY);
            var xs = stackalloc float[8];
            var ys = stackalloc float[8];
            var values = stackalloc short[8];
            var whole = stackalloc ulong[1];
            for (var i = 0; i < 8; i++)
            {
                xs[i] = 30f + i;
                ys[i] = 40f + i;
            }

            World.SenseColumn(world, layer, xs, ys, 8, values, whole);
            checksum += values[0] + (long)(whole[0] & 1ul);
            Assert.NotEqual(0L, checksum);

            Read(world, out _, out _, out _, out kernelEntries, out _);
            Assert.Equal(1, kernelEntries);

            PlaceAt(20);
            PlaceAt(30);
            PlaceAt(40);
            World.Process(world);
            Read(world, out _, out _, out _, out kernelEntries, out _);
            Assert.Equal(4, kernelEntries);
            Assert.Equal(4, PresentKeys());

            var burst = KernelCache.FrameInsertCap + 2;
            for (var phase = 50; phase < 50 + burst; phase++) PlaceAt(phase);
            World.Process(world);
            Read(world, out _, out _, out _, out kernelEntries, out _);
            Assert.Equal(4 + KernelCache.FrameInsertCap, kernelEntries);
            Assert.Equal(4 + KernelCache.FrameInsertCap, PresentKeys());
            Assert.Equal(4 + burst, keys.Count);

            World.FlushKernels();
            Read(world, out _, out _, out _, out kernelEntries, out _);
            Assert.Equal(0, kernelEntries);
            Assert.Equal(0, PresentKeys());
        }
        finally
        {
            World.Free(world);
            Stamp.Free(tent);
        }
    }

    [Fact]
    public void MemoryCall_AllocatesNothing()
    {
        var world = World.New();
        var tent = Stamp.Tent(4, 4, 40);
        try
        {
            Grid.New(world, CoarsePower, 0f, 0f, CoarseSize);
            var layer = Layer.New(world);
            World.Place(world, layer, 40.5f, 40.5f, tent, 3);
            World.Process(world);
            Read(world, out _, out _, out _, out _, out _);

            var checksum = 0L;
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 10_000; i++)
            {
                Read(world, out _, out var liveTiles, out var pages, out var kernelEntries, out var liveSources);
                checksum += liveTiles + pages + kernelEntries + liveSources;
            }

            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
            Assert.True(checksum > 0);
        }
        finally
        {
            World.Free(world);
            Stamp.Free(tent);
        }
    }
}
