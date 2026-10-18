using System;
using Gi.Entities.Tests.Support;
using Xunit;
using GiGrid = Gi.Grid;
using GiLayer = Gi.Layer;
using GiStamp = Gi.Stamp;
using GiWorld = Gi.World;

namespace Gi.Entities.Columns.Tests;

public sealed class ProfileEditorCoreTests
{
    private static GiGridSpec[][] Configurations()
    {
        return new[]
        {
            new[] { new GiGridSpec { Power = 6, X = 0, Y = 0, Size = 64 } },
            new[] { new GiGridSpec { Power = 5, X = -16.5f, Y = 8.25f, Size = 40 }, new GiGridSpec { Power = 7, X = 10, Y = 10, Size = 24 } },
            new[] { new GiGridSpec { Power = 8, X = 100, Y = -50, Size = 300 }, new GiGridSpec { Power = 6, X = 120, Y = -30, Size = 33.3f }, new GiGridSpec { Power = 5, X = 0, Y = 0, Size = 7 } },
            new[] { new GiGridSpec { Power = 10, X = -1000, Y = -1000, Size = 2048 } },
        };
    }

    [Fact]
    public void TheCoverageMapAgreesWithCoversAtEveryPointOfAProbeLattice()
    {
        foreach (var grids in Configurations())
        {
            var world = GiWorld.New();
            try
            {
                foreach (var grid in grids) GiGrid.New(world, grid.Power, grid.X, grid.Y, grid.Size);

                var covered = 0;
                var outside = 0;
                for (var iy = -40; iy <= 440; iy++)
                for (var ix = -40; ix <= 440; ix++)
                {
                    var x = -1100f + ix * 7.5f / 1.0f + (ix % 3) * 0.125f;
                    var y = -1100f + iy * 7.5f / 1.0f + (iy % 5) * 0.0625f;
                    var expected = GiWorld.Covers(world, x, y);
                    Assert.True(expected == GiGridCoverage.Covers(grids, x, y), $"({x},{y})");
                    if (expected) covered++;
                    else outside++;
                }

                Assert.True(covered > 0 && outside > 0);
                foreach (var grid in grids)
                {
                    foreach (var edge in new[] { 0f, 1f / 256f, 0.5f, grid.Size - 1f / 128f, grid.Size - 1e-3f, grid.Size, grid.Size + 1e-3f, -1e-3f })
                    foreach (var other in new[] { 0f, grid.Size * 0.5f })
                    {
                        Assert.Equal(GiWorld.Covers(world, grid.X + edge, grid.Y + other), GiGridCoverage.Covers(grids, grid.X + edge, grid.Y + other));
                        Assert.Equal(GiWorld.Covers(world, grid.X + other, grid.Y + edge), GiGridCoverage.Covers(grids, grid.X + other, grid.Y + edge));
                    }
                }
            }
            finally
            {
                GiWorld.Free(world);
            }
        }
    }

    [Fact]
    public void TheFinestGridWinsWhereGridsOverlapAndTheMapNamesIt()
    {
        var grids = new[]
        {
            new GiGridSpec { Power = 6, X = 0, Y = 0, Size = 64 },
            new GiGridSpec { Power = 7, X = 16, Y = 16, Size = 32 },
        };
        Assert.Equal(0, GiGridCoverage.Finest(grids, 4f, 4f));
        Assert.Equal(1, GiGridCoverage.Finest(grids, 20f, 20f));
        Assert.Equal(-1, GiGridCoverage.Finest(grids, 100f, 100f));

        var map = new byte[8 * 8];
        GiGridCoverage.Map(grids, -16f, -16f, 80f, 80f, 8, 8, map);
        Assert.Equal(0, map[0]);
        Assert.Equal(1, map[2 * 8 + 2]);
        Assert.Equal(2, map[3 * 8 + 3]);
        Assert.Equal(2, map[4 * 8 + 4]);
        Assert.Equal(1, map[6 * 8 + 6]);
        Assert.Equal(0, map[7 * 8 + 7]);
        Assert.Throws<ArgumentException>(() => GiGridCoverage.Map(grids, 0, 0, 1, 1, 8, 8, new byte[3]));
    }

    [Fact]
    public void TheMemoryEstimateIsAnUpperBoundOnWhatGiReportsForRandomWorlds()
    {
        var worstRatio = 0.0;
        for (ulong seed = 1; seed <= 250; seed++)
        {
            var rng = new GiRandom(seed);
            var power = 5 + rng.Next(5);
            var world = GiWorld.New();
            var stamps = new[] { GiStamp.Box(5, 5, 20), GiStamp.Tent(9, 9, 60), GiStamp.Disk(6, 40), GiStamp.Bell(33, 33, 80) };
            try
            {
                GiGrid.New(world, power, 0f, 0f, 1 << power);
                GiLayer.Precision.TryCreate(new[] { 8, 1, 2, 0 }[rng.Next(4)], 16, out var precision);
                var layer = GiLayer.New(world, GiLayer.Residency.Persistent, precision);
                var size = 1 << power;
                var sources = 1 + rng.Next(rng.Next(3) == 0 ? 400 : 60);
                for (var i = 0; i < sources; i++)
                    GiWorld.Place(world, layer, rng.Next(size), rng.Next(size), stamps[rng.Next(4)], (sbyte)(1 + rng.Next(8)));
                GiWorld.Process(world);

                GiWorld.Memory(world, out var bytes, out var live, out _, out _, out _);
                var estimate = GiProfileEstimate.Layer(live);
                Assert.True(bytes <= estimate, $"seed {seed}: store {bytes} B exceeds the estimate {estimate} B for {live} live tiles");
                Assert.True(live <= GiProfileEstimate.Tiles(power));
                if (bytes > 0) worstRatio = Math.Max(worstRatio, estimate / (double)bytes);
            }
            finally
            {
                GiWorld.Free(world);
                foreach (var stamp in stamps) GiStamp.Free(stamp);
            }
        }

        Assert.True(worstRatio < 6.0, $"the bound is {worstRatio:F2} times the measured store at its loosest");
    }

    [Fact]
    public void TheWorstCaseSumsEveryGridAndLayer()
    {
        var spec = GiScenarioProfile.Standard().ToSpec();
        var expected = 0L;
        foreach (var grid in spec.Grids) expected += spec.Layers.Length * (GiProfileEstimate.FixedBytes + GiProfileEstimate.TileBytes * GiProfileEstimate.Tiles(grid.Power));
        Assert.Equal(expected, GiProfileEstimate.Worst(spec));
        Assert.Equal(1L, GiProfileEstimate.Tiles(5));
        Assert.Equal(4L, GiProfileEstimate.Tiles(6));
        Assert.Equal(262144L, GiProfileEstimate.Tiles(14));
        Assert.Equal("512 B", GiProfileEstimate.Format(512));
        Assert.Equal("3 KiB", GiProfileEstimate.Format(3 * 1024 + 5));
        Assert.Equal("5 MiB", GiProfileEstimate.Format(5L * 1024 * 1024));
        Assert.Equal("2 GiB", GiProfileEstimate.Format(2L * 1024 * 1024 * 1024));
    }

    [Fact]
    public void EveryLayerGetsAThumbnailFromARealWorldAndTheWorldIsFreed()
    {
        var spec = GiScenarioProfile.Standard().ToSpec();
        var worlds = GiOrphans.LiveWorlds();
        Assert.True(GiProfileThumbnails.TryRender(spec, out var layers, out var error), error);
        Assert.Equal(worlds, GiOrphans.LiveWorlds());
        Assert.Equal(spec.Layers.Length, layers.Length);
        foreach (var layer in layers) Assert.Equal(GiProfileThumbnails.Side * GiProfileThumbnails.Side, layer.Length);

        bool Any(short[] cells)
        {
            foreach (var value in cells)
                if (value != 0)
                    return true;
            return false;
        }

        for (var l = 0; l < spec.Layers.Length; l++)
        {
            if (spec.Layers[l].Kind == GiLayerKind.Persistent) Assert.True(Any(layers[l]), $"layer {l} is persistent and has sources");
            if (spec.Layers[l].Kind == GiLayerKind.Transient) Assert.False(Any(layers[l]), $"layer {l} is transient and has no splats");
        }

        Assert.True(Any(layers[0]));
        Assert.True(Any(layers[3]), "the sum layer reads two persistent layers");
        Assert.False(Any(layers[2]), "a transient layer has nothing placed on it");

        Assert.True(GiProfileThumbnails.TryRender(spec, out var again, out _));
        for (var l = 0; l < layers.Length; l++) Assert.Equal(layers[l], again[l]);
    }

    [Fact]
    public void AProfileThatDoesNotBuildExplainsWhy()
    {
        var spec = GiScenarioProfile.Standard().ToSpec();
        spec.Grids[0].Power = 3;
        Assert.False(GiProfileThumbnails.TryRender(spec, out var layers, out var error));
        Assert.Null(layers);
        Assert.Contains("InvalidGrid", error);
    }
}
