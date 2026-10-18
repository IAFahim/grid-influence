using System;
using Gi.Entities.Tests.Support;
using Gi.Validation;
using Xunit;
using GiGrid = Gi.Grid;
using GiLayer = Gi.Layer;
using GiStamp = Gi.Stamp;
using GiWorld = Gi.World;

namespace Gi.Entities.Columns.Tests;

public sealed unsafe class StampPreviewTests
{
    private static int DefinedStamps()
    {
        var count = 0;
        for (var stamp = 1; stamp < 256; stamp++)
            if (GiStamp.TryGetSpec((byte)stamp, out _))
                count++;
        return count;
    }

    private static GiStampSpec RandomStamp(ref GiRandom rng, out sbyte[] cells)
    {
        cells = Array.Empty<sbyte>();
        switch (rng.Next(7))
        {
            case 0: return new GiStampSpec { Shape = GiStampShape.Box, Width = (ushort)(1 + rng.Next(14)), Height = (ushort)(1 + rng.Next(14)), Value = (sbyte)(rng.Next(120) - 60) };
            case 1: return new GiStampSpec { Shape = GiStampShape.Tent, Width = (ushort)(2 + rng.Next(12)), Height = (ushort)(2 + rng.Next(12)), Value = (sbyte)(1 + rng.Next(100)) };
            case 2: return new GiStampSpec { Shape = GiStampShape.Bell, Width = (ushort)(3 + rng.Next(10)), Height = (ushort)(3 + rng.Next(10)), Value = (sbyte)(1 + rng.Next(100)) };
            case 3: return new GiStampSpec { Shape = GiStampShape.Disk, Radius = (ushort)(1 + rng.Next(9)), Arc = (ushort)(30 + rng.Next(331)), Value = (sbyte)(1 + rng.Next(100)) };
            case 4: return new GiStampSpec { Shape = GiStampShape.Cone, Radius = (ushort)(2 + rng.Next(9)), Arc = 360, Value = (sbyte)(1 + rng.Next(100)) };
            case 5: return new GiStampSpec { Shape = GiStampShape.Dome, Radius = (ushort)(2 + rng.Next(9)), Arc = (ushort)(90 + rng.Next(271)), Value = (sbyte)(1 + rng.Next(100)) };
            default:
                cells = new sbyte[16];
                for (var i = 0; i < cells.Length; i++) cells[i] = (sbyte)(rng.Next(200) - 100);
                return new GiStampSpec { Shape = GiStampShape.Raster, Width = 4, Height = 4, CellOffset = 0, CellCount = 16 };
        }
    }

    private static ulong Independent(GiStampSpec spec, sbyte[] cells, in GiPreviewPose pose)
    {
        var registry = new GiStampRegistry();
        Assert.True(registry.TryAcquire(spec, cells, out var stamp));
        var world = GiWorld.New();
        try
        {
            GiGrid.New(world, GiStampPreview.Power, 0f, 0f, GiStampPreview.Cells);
            var exact = GiLayer.New(world, GiLayer.Precision.Exact);
            var cell = GiLayer.New(world, GiLayer.Precision.Cell);
            var x = GiStampPreview.Center + pose.PhaseX;
            var y = GiStampPreview.Center + pose.PhaseY;
            var layers = new[] { exact, cell };
            foreach (var layer in layers)
            {
                var id = GiWorld.Place(world, layer, x, y, stamp, GiStampPreview.Gain);
                GiWorld.Turn(world, id, pose.AngleRadians);
                GiWorld.Scale(world, id, pose.Scale);
            }

            GiWorld.Process(world);
            var hash = 14695981039346656037UL;
            foreach (var layer in layers)
            {
                var values = new short[GiStampPreview.Cells * GiStampPreview.Cells];
                fixed (short* p = values) GiWorld.QueryRegion(world, 0, layer, 0, 0, GiStampPreview.Cells, GiStampPreview.Cells, p);
                foreach (var value in values) hash = (hash ^ (ushort)value) * 1099511628211UL;
            }

            return hash;
        }
        finally
        {
            GiWorld.Free(world);
            registry.Release(stamp);
        }
    }

    [Fact]
    public void ThePreviewHashEqualsARuntimeWorldPlacingTheSameStampOverFiftySeeds()
    {
        for (ulong seed = 1; seed <= 50; seed++)
        {
            var rng = new GiRandom(seed);
            var spec = RandomStamp(ref rng, out var cells);
            var pose = new GiPreviewPose(rng.Next(8) * 0.125f, rng.Next(8) * 0.125f, rng.Next(16) * 0.39269908f + 0.000024f,
                0.5f + rng.Next(6) * 0.25f);
            var exact = new short[GiStampPreview.Cells * GiStampPreview.Cells];
            var cell = new short[GiStampPreview.Cells * GiStampPreview.Cells];

            using var preview = new GiStampPreview();
            Assert.True(preview.Set(spec, cells, out var error), error);
            var viaPreview = preview.Render(pose, exact, cell, out var bounds);
            Assert.Equal(Independent(spec, cells, pose), viaPreview);
            Assert.False(bounds.Empty, $"seed {seed} drew nothing");
        }
    }

    [Fact]
    public void RenderingTwiceGivesTheSameHashAndLeavesTheWorldClean()
    {
        using var preview = new GiStampPreview();
        Assert.True(preview.Set(new GiStampSpec { Shape = GiStampShape.Tent, Width = 7, Height = 5, Value = 60 }, Array.Empty<sbyte>(), out _));
        var pose = new GiPreviewPose(0.25f, 0.5f, 0.7f, 1.5f);
        var exact = new short[GiStampPreview.Cells * GiStampPreview.Cells];
        var cell = new short[GiStampPreview.Cells * GiStampPreview.Cells];
        var first = preview.Render(pose, exact, cell, out var firstBounds);
        var other = preview.Render(new GiPreviewPose(0f, 0f, 0f, 3f), exact, cell, out _);
        var second = preview.Render(pose, exact, cell, out var secondBounds);
        Assert.Equal(first, second);
        Assert.NotEqual(first, other);
        Assert.Equal((firstBounds.MinX, firstBounds.MaxX, firstBounds.MinY, firstBounds.MaxY),
            (secondBounds.MinX, secondBounds.MaxX, secondBounds.MinY, secondBounds.MaxY));
    }

    [Fact]
    public void AtMostOneTemporaryStampExistsAtAnyTime()
    {
        var baseline = DefinedStamps();
        var rng = new GiRandom(77);
        using (var preview = new GiStampPreview())
        {
            for (var i = 0; i < 40; i++)
            {
                var spec = RandomStamp(ref rng, out var cells);
                Assert.True(preview.Set(spec, cells, out var error), error);
                Assert.True(preview.TemporaryStamps <= 1);
                Assert.True(DefinedStamps() <= baseline + 1);
            }

            Assert.Equal(1, preview.TemporaryStamps);
            preview.Clear();
            Assert.Equal(0, preview.TemporaryStamps);
            Assert.Equal(baseline, DefinedStamps());
        }

        Assert.Equal(baseline, DefinedStamps());
    }

    [Fact]
    public void ClosingThePreviewFreesItsWorldAndItsStamp()
    {
        var baseline = DefinedStamps();
        var worlds = GiOrphans.LiveWorlds();
        var preview = new GiStampPreview();
        Assert.Equal(worlds + 1, GiOrphans.LiveWorlds());
        Assert.True(preview.Set(new GiStampSpec { Shape = GiStampShape.Disk, Radius = 4, Arc = 360, Value = 30 }, Array.Empty<sbyte>(), out _));
        Assert.Equal(baseline + 1, DefinedStamps());

        preview.Dispose();
        preview.Dispose();
        Assert.Equal(worlds, GiOrphans.LiveWorlds());
        Assert.Equal(baseline, DefinedStamps());
    }

    [Fact]
    public void AnInvalidStampSaysWhyAndDropsTheOldTemporaryStamp()
    {
        using var preview = new GiStampPreview();
        Assert.True(preview.Set(new GiStampSpec { Shape = GiStampShape.Box, Width = 3, Height = 3, Value = 10 }, Array.Empty<sbyte>(), out _));

        var bad = new GiStampSpec { Shape = GiStampShape.Box, Width = 0, Height = 3, Value = 10 };
        Assert.False(preview.Set(bad, Array.Empty<sbyte>(), out var error));
        GiValidation.TryRectangle(0, 3, 10, out var expected);
        Assert.Equal(expected, error);
        Assert.False(preview.HasStamp);
        Assert.Equal(0, preview.TemporaryStamps);

        var exact = new short[GiStampPreview.Cells * GiStampPreview.Cells];
        var cell = new short[GiStampPreview.Cells * GiStampPreview.Cells];
        preview.Render(new GiPreviewPose(0f, 0f, 0f, 1f), exact, cell, out var bounds);
        Assert.True(bounds.Empty);
    }

    [Fact]
    public void TheFootprintAfterScalingUsesTheValidationText()
    {
        var box = new GiStampSpec { Shape = GiStampShape.Box, Width = 100, Height = 100, Value = 10 };
        Assert.True(GiStampPreview.TryFootprint(box, 1f, out var error));
        Assert.Null(error);
        Assert.False(GiStampPreview.TryFootprint(box, 6f, out error));
        GiValidation.TryFootprint(600, 600, out var expected);
        Assert.Equal(expected, error);

        var disk = new GiStampSpec { Shape = GiStampShape.Disk, Radius = 128, Arc = 360, Value = 10 };
        Assert.True(GiStampPreview.TryFootprint(disk, 2f, out _));
        Assert.False(GiStampPreview.TryFootprint(disk, 2.25f, out _));
    }

    [Fact]
    public void ImportingTheSameTextureTwiceGivesIdenticalRasterCells()
    {
        var gray = new byte[64];
        for (var i = 0; i < gray.Length; i++) gray[i] = (byte)(i * 4 + 3);
        var first = GiRasterImport.FromGray(gray, 8, 8);
        var second = GiRasterImport.FromGray(gray, 8, 8);
        Assert.Equal(first, second);
        Assert.Equal((sbyte)-125, first[0]);
        Assert.Equal((sbyte)127, GiRasterImport.FromGray(new byte[] { 255 }, 1, 1)[0]);
        Assert.Equal((sbyte)-128, GiRasterImport.FromGray(new byte[] { 0 }, 1, 1)[0]);
        Assert.Equal((byte)0, GiRasterImport.Gray(255, 255, 255, 0));
        Assert.Equal((byte)255, GiRasterImport.Gray(255, 255, 255, 255));
        Assert.Equal(GiRasterImport.Gray(10, 200, 30, 255), GiRasterImport.Gray(10, 200, 30, 255));
    }
}
