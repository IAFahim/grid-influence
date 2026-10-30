using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using Xunit;

namespace Gi.Tests;

public sealed unsafe class OccupancyTests
{
    private const int Size = TileBake.TileSize;
    private const int Cells = Size * Size;

    public static TheoryData<PublishPath, int> PathSeeds()
    {
        var data = new TheoryData<PublishPath, int>();
        foreach (PublishPath path in Enum.GetValues<PublishPath>())
        foreach (var seed in new[] { 11, 29 })
            data.Add(path, seed);
        return data;
    }

    public static TheoryData<PublishPath> Paths()
    {
        var data = new TheoryData<PublishPath>();
        foreach (PublishPath path in Enum.GetValues<PublishPath>()) data.Add(path);
        return data;
    }

    private static bool Supported(PublishPath path)
    {
        return path switch
        {
            PublishPath.Vector128 => Sse2.IsSupported || AdvSimd.IsSupported,
            PublishPath.Vector256 => Avx2.IsSupported,
            _ => true
        };
    }

    [Theory]
    [MemberData(nameof(PathSeeds))]
    public void RectSequences_SummaryOccupancyAlwaysMatchesAPageScan(PublishPath path, int seed)
    {
        if (!Supported(path)) return;

        var random = new Random(seed);
        var i = new int[Cells];
        for (var step = 0; step < 24; step++)
        {
            using var tile = new PlaneTile();
            if (step > 0)
                for (var c = 0; c < Cells; c++) i[c] = random.Next(-4000, 4000);

            tile.CopyI(i);
            AssertPublishAgrees(path, tile, 0, 0, Size, Size, 1);
            for (var round = 0; round < 8; round++)
            {
                int x0, x1;
                if ((round & 1) == 0)
                {
                    x0 = 0;
                    x1 = Size;
                }
                else
                {
                    x0 = random.Next(Size);
                    x1 = Math.Min(Size, x0 + random.Next(1, Size + 1));
                }

                var y0 = random.Next(Size);
                var y1 = Math.Min(Size, y0 + random.Next(1, Size + 1));
                if ((round & 3) == 2)
                {
                    for (var y = y0; y < y1; y++)
                        for (var x = x0; x < x1; x++)
                            i[y * Size + x] = 0;
                }
                else
                {
                    for (var y = y0; y < y1; y++)
                        for (var x = x0; x < x1; x++)
                            i[y * Size + x] = random.Next(-4000, 4000);
                }

                tile.CopyI(i);
                AssertPublishAgrees(path, tile, x0, y0, x1, y1, round + 2);
            }
        }
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public void CancelledSumPage_StillOccupiedUntilEveryCellClears(PublishPath path)
    {
        if (!Supported(path)) return;

        using var tile = new PlaneTile();
        var i = new int[Cells];
        i[5 * Size + 5] = 900;
        i[5 * Size + 9] = -900;
        i[9 * Size + 5] = -700;
        tile.CopyI(i);
        AssertPublishAgrees(path, tile, 0, 0, Size, Size, 1);
        Assert.True(ReadOccupied(tile));

        i[5 * Size + 5] = 0;
        i[5 * Size + 9] = 0;
        tile.CopyI(i);
        AssertPublishAgrees(path, tile, 0, 0, Size, Size, 2);
        Assert.True(ReadOccupied(tile));

        i[9 * Size + 5] = 0;
        tile.CopyI(i);
        AssertPublishAgrees(path, tile, 0, 9, Size, 10, 3);
        Assert.False(ReadOccupied(tile));
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public void AllNegativePage_StaysOccupiedThroughPartialClears(PublishPath path)
    {
        if (!Supported(path)) return;

        using var tile = new PlaneTile();
        var i = new int[Cells];
        for (var y = 0; y < Size; y++)
            for (var x = 0; x < Size; x++)
                i[y * Size + x] = -(1 + (x * 7 + y * 13) % 500);
        tile.CopyI(i);
        AssertPublishAgrees(path, tile, 0, 0, Size, Size, 1);
        Assert.True(ReadOccupied(tile));

        for (var band = 0; band < Size; band += 8)
        {
            for (var y = band; y < band + 8 && y < Size; y++)
                for (var x = 0; x < Size; x++)
                    i[y * Size + x] = 0;
            tile.CopyI(i);
            AssertPublishAgrees(path, tile, 0, band, Size, Math.Min(band + 8, Size), 2 + band);
        }

        Assert.False(ReadOccupied(tile));
    }

    [Fact]
    public void EngineDropsTilesOnlyWhenThePublishedPageIsEmpty()
    {
        var world = World.New();
        var stamp = Stamp.Box(4, 4, 8);
        try
        {
            Grid.New(world, 6, 0f, 0f, 64f);
            var layer = Layer.New(world);
            var kept = World.Place(world, layer, 8.5f, 8.5f, stamp, 1);
            var dropped = World.Place(world, layer, 40.5f, 40.5f, stamp, 1);
            World.Process(world);
            Assert.Equal(2, LivePages(world, layer));

            World.Remove(world, dropped);
            World.Process(world);
            Assert.Equal(1, LivePages(world, layer));
            Assert.NotEqual(0, World.Query(world, 0, layer, 8, 8));
            Assert.Equal(0, World.Query(world, 0, layer, 40, 40));

            World.Remove(world, kept);
            World.Process(world);
            Assert.Equal(0, LivePages(world, layer));
            Assert.Equal(0, World.Query(world, 0, layer, 8, 8));
        }
        finally
        {
            World.Free(world);
            Stamp.Free(stamp);
        }
    }

    private static bool ReadOccupied(PlaneTile tile)
    {
        var cells = tile.Store.Cells(tile.Plane);
        return TileBake.Occupied(cells.Summary);
    }

    private static void AssertPublishAgrees(PublishPath path, PlaneTile tile, int x0, int y0, int x1, int y1, int tick)
    {
        ResolveOutcome outcome;
        fixed (PlaneStore* store = &tile.Store)
        fixed (TilePlanes* planes = &tile.Plane)
        {
            outcome = path switch
            {
#if !GI_NETSTANDARD
                PublishPath.Vector256 => TileBake.Publish256(store->Cells(*planes), x0, y0, x1, y1, tick),
                PublishPath.Vector128 => TileBake.Publish128(store->Cells(*planes), x0, y0, x1, y1, tick),
#endif
                PublishPath.Scalar => TileBake.PublishScalar(store->Cells(*planes), x0, y0, x1, y1, tick),
                _ => TileBake.Publish(store, planes, x0, y0, x1, y1, tick)
            };
        }

        var cells = tile.Store.Cells(tile.Plane);
        var scanned = TileBake.PageAny(cells.Page);
        Assert.Equal(scanned, TileBake.Occupied(cells.Summary));
        Assert.Equal(scanned, (outcome & ResolveOutcome.Empty) == 0);
    }

    private static int LivePages(byte world, byte layer)
    {
        var w = World.GetContext(world);
        Assert.True(w != null);
        return w->Grids[0].Layers[layer].LivePages;
    }

    private sealed class PlaneTile : IDisposable
    {
        public TilePlanes Plane;
        public PlaneStore Store;

        public PlaneTile()
        {
            Plane = Store.Take();
        }

        public void CopyI(int[] values)
        {
            var cells = Store.Cells(Plane);
            fixed (int* source = values)
            {
                Buffer.MemoryCopy(source, cells.I, PlaneStore.Int32Unit, PlaneStore.Int32Unit);
            }
        }

        public void Dispose()
        {
            Store.Give(Plane);
            Store.Reset();
        }
    }
}
