using System;
using Gi.Entities.Tests.Support;
using Xunit;
using GiGrid = Gi.Grid;
using GiLayer = Gi.Layer;
using GiWorld = Gi.World;

namespace Gi.Entities.Columns.Tests;

public sealed unsafe class FootprintTests
{

    private static readonly float[] NorthCorners = new[] { 8f, 19f, 12f, 19f, 12f, 21f, 8f, 21f };
    private static readonly float[] SouthCorners = new[] { 11f, 18f, 11f, 22f, 9f, 22f, 9f, 18f };
    private const int Power = 7;
    private const float Size = 64f;
    private const int Cells = 1 << Power;

    private static GiStampSpec Stamp(ref GiRandom rng, out sbyte[] cells)
    {
        cells = Array.Empty<sbyte>();
        switch (rng.Next(6))
        {
            case 0: return new GiStampSpec { Shape = GiStampShape.Box, Width = (ushort)(2 + rng.Next(13)), Height = (ushort)(2 + rng.Next(13)), Value = 40 };
            case 1: return new GiStampSpec { Shape = GiStampShape.Tent, Width = (ushort)(3 + rng.Next(12)), Height = (ushort)(3 + rng.Next(12)), Value = 60 };
            case 2: return new GiStampSpec { Shape = GiStampShape.Bell, Width = (ushort)(4 + rng.Next(10)), Height = (ushort)(4 + rng.Next(10)), Value = 80 };
            case 3: return new GiStampSpec { Shape = GiStampShape.Disk, Radius = (ushort)(2 + rng.Next(9)), Arc = 360, Value = 40 };
            case 4: return new GiStampSpec { Shape = GiStampShape.Cone, Radius = (ushort)(3 + rng.Next(8)), Arc = 360, Value = 70 };
            default:
                cells = new sbyte[36];
                for (var i = 0; i < cells.Length; i++) cells[i] = (sbyte)(20 + rng.Next(80));
                return new GiStampSpec { Shape = GiStampShape.Raster, Width = 6, Height = 6, CellOffset = 0, CellCount = 36 };
        }
    }

    private static void Alone(GiStampSpec spec, sbyte[] stampCells, float x, float y, float heading, float scale, out int minX, out int minY,
        out int maxX, out int maxY)
    {
        var registry = new GiStampRegistry();
        Assert.True(registry.TryAcquire(spec, stampCells, out var stamp));
        var world = GiWorld.New();
        try
        {
            GiGrid.New(world, Power, 0f, 0f, Size);
            var layer = GiLayer.New(world, GiLayer.Precision.Exact);
            var id = GiWorld.Place(world, layer, x, y, stamp, 8);
            GiWorld.Turn(world, id, heading);
            GiWorld.Scale(world, id, scale);
            GiWorld.Process(world);
            var values = new short[Cells * Cells];
            fixed (short* p = values) GiWorld.QueryRegion(world, 0, layer, 0, 0, Cells, Cells, p);
            minX = Cells;
            minY = Cells;
            maxX = -1;
            maxY = -1;
            for (var cy = 0; cy < Cells; cy++)
            for (var cx = 0; cx < Cells; cx++)
            {
                if (values[cy * Cells + cx] == 0) continue;

                minX = Math.Min(minX, cx);
                maxX = Math.Max(maxX, cx);
                minY = Math.Min(minY, cy);
                maxY = Math.Max(maxY, cy);
            }
        }
        finally
        {
            GiWorld.Free(world);
            registry.Release(stamp);
        }
    }

    [Fact]
    public void TheFootprintContainsEveryNonzeroCellQueryRegionReportsForTheEmitterAloneOverTwoHundredSeeds()
    {
        var cellsPerUnit = Cells / Size;
        var worstSlack = 0f;
        for (ulong seed = 1; seed <= 200; seed++)
        {
            var rng = new GiRandom(seed);
            var spec = Stamp(ref rng, out var cells);
            var x = 20f + rng.Next(24) + rng.Next(8) * 0.125f;
            var y = 20f + rng.Next(24) + rng.Next(8) * 0.125f;
            var heading = rng.Next(16) * 0.39269908f + 0.000024f;
            var scale = 0.5f + rng.Next(8) * 0.25f;
            Alone(spec, cells, x, y, heading, scale, out var minX, out var minY, out var maxX, out var maxY);
            Assert.True(maxX >= minX, $"seed {seed} drew nothing");

            GiFootprint.WorldRect(spec, x, y, heading, scale, out var rx0, out var ry0, out var rx1, out var ry1);
            var fx0 = rx0 * cellsPerUnit;
            var fx1 = rx1 * cellsPerUnit;
            var fy0 = ry0 * cellsPerUnit;
            var fy1 = ry1 * cellsPerUnit;
            Assert.True(minX >= Math.Floor(fx0) - 1 && maxX + 1 <= Math.Ceiling(fx1) + 1, $"seed {seed} {spec.Shape} x: cells {minX}..{maxX} footprint {fx0}..{fx1}");
            Assert.True(minY >= Math.Floor(fy0) - 1 && maxY + 1 <= Math.Ceiling(fy1) + 1, $"seed {seed} {spec.Shape} y: cells {minY}..{maxY} footprint {fy0}..{fy1}");

            var slack = Math.Max(Math.Max(minX - fx0, fx1 - (maxX + 1)), Math.Max(minY - fy0, fy1 - (maxY + 1)));
            if (spec.Shape == GiStampShape.Box || spec.Shape == GiStampShape.Disk) worstSlack = Math.Max(worstSlack, slack);
        }

        Assert.True(worstSlack < 2.5f, $"a solid stamp's footprint is {worstSlack} cells looser than its nonzero bounds");
    }

    [Fact]
    public void CornersRotateAroundTheEmitterAndRoundStampsUseTheirRadius()
    {
        var box = new GiStampSpec { Shape = GiStampShape.Box, Width = 4, Height = 2, Value = 10 };
        var corners = new float[8];
        GiFootprint.Corners(box, 10f, 20f, 0f, 1f, corners);
        Assert.Equal(NorthCorners, corners);

        GiFootprint.Corners(box, 10f, 20f, (float)(Math.PI / 2), 1f, corners);
        for (var i = 0; i < 8; i++) corners[i] = (float)Math.Round(corners[i], 3);
        Assert.Equal(SouthCorners, corners);

        var disk = new GiStampSpec { Shape = GiStampShape.Disk, Radius = 5, Arc = 360, Value = 10 };
        GiFootprint.HalfExtents(disk, 1.2f, 2f, out var hx, out var hy);
        Assert.Equal((10f, 10f), (hx, hy));
        GiFootprint.HalfExtents(box, (float)(Math.PI / 2), 1f, out hx, out hy);
        Assert.Equal(1f, Math.Round(hx, 3));
        Assert.Equal(2f, Math.Round(hy, 3));
        Assert.Throws<ArgumentException>(() => GiFootprint.Corners(box, 0, 0, 0, 1, new float[3]));
    }
}
