using Xunit;

namespace GridInfluence.Tests;

public sealed class RasterizerOracleTests
{
    private static int[,] Paint(Stamp stamp, Int2 boxMin, Int2 boxSize)
    {
        var estimate = Rasterizer.EstimateSpanCount(stamp.Shape);
        var buffer = new WeightedRect[Math.Max(estimate, 1)];
        int count;
        unsafe
        {
            fixed (WeightedRect* pointer = buffer)
            {
                var sink = new SpanSink(pointer, estimate);
                Rasterizer.Emit(stamp, ref sink);
                count = sink.Count;
            }
        }

        var grid = new int[boxSize.X, boxSize.Y];
        for (var i = 0; i < count; i++)
        {
            var b = buffer[i].Bounds;
            for (var y = b.Min.Y; y < b.Max.Y; y++)
            for (var x = b.Min.X; x < b.Max.X; x++)
            {
                var lx = x - boxMin.X;
                var ly = y - boxMin.Y;
                if ((uint)lx < (uint)boxSize.X && (uint)ly < (uint)boxSize.Y) grid[lx, ly] += buffer[i].Weight;
            }
        }

        return grid;
    }

    private static IEnumerable<(int X, int Y)> Covered(int[,] grid)
    {
        for (var y = 0; y < grid.GetLength(1); y++)
        for (var x = 0; x < grid.GetLength(0); x++)
        {
            if (grid[x, y] != 0) yield return (x, y);
        }
    }

    private static bool DiscCell(int dx, int dy, int radius) => dx * dx + (long)dy * dy <= (long)radius * radius;

    private static bool CapsuleCell(int px, int py, Int2 a, Int2 b, int radius)
    {
        var axis = b - a;
        var lengthSquared = (long)axis.X * axis.X + (long)axis.Y * axis.Y;
        var qx = px - a.X;
        var qy = py - a.Y;
        var distanceSquared = qx * qx + (long)qy * qy;
        if (lengthSquared == 0) return distanceSquared <= (long)radius * radius;

        var projection = qx * axis.X + (long)qy * axis.Y;
        if (projection <= 0) return distanceSquared <= (long)radius * radius;
        if (projection >= lengthSquared)
        {
            var ex = px - b.X;
            var ey = py - b.Y;
            return ex * ex + (long)ey * ey <= (long)radius * radius;
        }

        return distanceSquared * lengthSquared - projection * projection
               <= (long)radius * radius * lengthSquared;
    }

    private static bool SectorCell(int dx, int dy, int radius, Int2 d0, Int2 d1)
    {
        if (!DiscCell(dx, dy, radius)) return false;

        return d0.X * (long)dy - d0.Y * (long)dx >= 0 && d1.X * (long)dy - d1.Y * (long)dx <= 0;
    }

    [Fact]
    public void SolidRect_CoversExactRectangle()
    {
        var shape = InfluenceShape.SolidRect(new Int2(-2, -1), new Int2(4, 3), 7);
        var grid = Paint(new Stamp(shape, new Int2(10, 10)), new Int2(6, 7), new Int2(12, 8));

        for (var y = 0; y < grid.GetLength(1); y++)
        for (var x = 0; x < grid.GetLength(0); x++)
        {
            var expected = x >= 2 && x < 6 && y >= 2 && y < 5 ? 7 : 0;
            Assert.Equal(expected, grid[x, y]);
        }
    }

    [Fact]
    public void RectShell_CoversOutlineOnly()
    {
        var shape = InfluenceShape.RectShell(Int2.Zero, new Int2(6, 6), 2, 3);
        var grid = Paint(new Stamp(shape, Int2.Zero), Int2.Zero, new Int2(8, 8));

        for (var y = 0; y < grid.GetLength(1); y++)
        for (var x = 0; x < grid.GetLength(0); x++)
        {
            var insideOuter = x >= 0 && x < 6 && y >= 0 && y < 6;
            var insideInner = x >= 2 && x < 4 && y >= 2 && y < 4;
            var expected = insideOuter && !insideInner ? 3 : 0;
            Assert.Equal(expected, grid[x, y]);
        }
    }

    [Fact]
    public void Disc_MatchesIntegerCircleMembership()
    {
        const int radius = 7;
        var shape = InfluenceShape.Disc(new Int2(20, 20), radius, 5);
        var grid = Paint(new Stamp(shape, Int2.Zero), Int2.Zero, new Int2(30, 30));

        for (var y = 0; y < grid.GetLength(1); y++)
        for (var x = 0; x < grid.GetLength(0); x++)
        {
            var expected = DiscCell(x - 20, y - 20, radius) ? 5 : 0;
            Assert.Equal(expected, grid[x, y]);
        }
    }

    [Fact]
    public void Annulus_CoversRingOnly()
    {
        var shape = InfluenceShape.Annulus(new Int2(12, 12), 6, 3, 9);
        var grid = Paint(new Stamp(shape, Int2.Zero), Int2.Zero, new Int2(20, 20));

        for (var y = 0; y < grid.GetLength(1); y++)
        for (var x = 0; x < grid.GetLength(0); x++)
        {
            var dx = x - 12;
            var dy = y - 12;
            var expected = DiscCell(dx, dy, 6) && !DiscCell(dx, dy, 3) ? 9 : 0;
            Assert.Equal(expected, grid[x, y]);
        }
    }

    [Theory]
    [InlineData(3, 1, 8)]
    [InlineData(1, 5, 6)]
    [InlineData(9, 9, 5)]
    public void Capsule_MatchesSegmentDistance(int axisX, int axisY, int radius)
    {
        var a = new Int2(10, 10);
        var b = a + new Int2(axisX, axisY);
        var shape = InfluenceShape.Capsule(a, b, radius, 4);
        var grid = Paint(new Stamp(shape, Int2.Zero), Int2.Zero, new Int2(24, 24));

        for (var y = 0; y < grid.GetLength(1); y++)
        for (var x = 0; x < grid.GetLength(0); x++)
        {
            var expected = CapsuleCell(x, y, a, b, radius) ? 4 : 0;
            Assert.Equal(expected, grid[x, y]);
        }
    }

    [Fact]
    public void ThickLine_RasterizesLikeCapsule()
    {
        var a = new Int2(4, 4);
        var b = new Int2(7, 14);
        var line = Paint(new Stamp(InfluenceShape.ThickLine(a, b, 3, 6), Int2.Zero), Int2.Zero, new Int2(16, 20));
        var capsule = Paint(new Stamp(InfluenceShape.Capsule(a, b, 3, 6), Int2.Zero), Int2.Zero, new Int2(16, 20));

        Assert.Equal(capsule, line);
    }

    [Fact]
    public void Ellipse_MatchesImplicitCurveMembership()
    {
        var radii = new Int2(8, 4);
        var center = new Int2(10, 8);
        var shape = InfluenceShape.Ellipse(center, radii, 2);
        var grid = Paint(new Stamp(shape, Int2.Zero), Int2.Zero, new Int2(22, 18));

        for (var y = 0; y < grid.GetLength(1); y++)
        for (var x = 0; x < grid.GetLength(0); x++)
        {
            var dx = (double)(x - center.X) / radii.X;
            var dy = (double)(y - center.Y) / radii.Y;
            var expected = dx * dx + dy * dy <= 1.0 ? 2 : 0;
            Assert.Equal(expected, grid[x, y]);
        }
    }

    [Fact]
    public void RoundedRect_CutsCornersWithCircleArcs()
    {
        const int radius = 4;
        var min = new Int2(2, 2);
        var size = new Int2(14, 10);
        var shape = InfluenceShape.RoundedRect(min, size, radius, 8);
        var grid = Paint(new Stamp(shape, Int2.Zero), Int2.Zero, new Int2(20, 18));

        for (var y = 0; y < grid.GetLength(1); y++)
        for (var x = 0; x < grid.GetLength(0); x++)
        {
            var inside = x >= min.X && x < min.X + size.X && y >= min.Y && y < min.Y + size.Y;
            var expected = 0;
            if (inside)
            {
                var cx = x < min.X + radius ? min.X + radius : x >= min.X + size.X - radius ? min.X + size.X - radius - 1 : -1;
                var cy = y < min.Y + radius ? min.Y + radius : y >= min.Y + size.Y - radius ? min.Y + size.Y - radius - 1 : -1;
                var outsideCorner = cx >= 0 && cy >= 0
                                    && (long)(cx - x) * (cx - x) + (long)(cy - y) * (cy - y) > (long)radius * radius;
                expected = outsideCorner ? 0 : 8;
            }

            Assert.Equal(expected, grid[x, y]);
        }
    }

    [Fact]
    public void Sector_CoversRightPointingCone()
    {
        const int radius = 9;
        var center = new Int2(4, 4);
        var shape = InfluenceShape.Sector(center, radius, new Int2(1, -1), new Int2(1, 1), 11);
        var grid = Paint(new Stamp(shape, Int2.Zero), Int2.Zero, new Int2(16, 16));

        for (var y = 0; y < grid.GetLength(1); y++)
        for (var x = 0; x < grid.GetLength(0); x++)
        {
            var expected = SectorCell(x - center.X, y - center.Y, radius, new Int2(1, -1), new Int2(1, 1)) ? 11 : 0;
            Assert.Equal(expected, grid[x, y]);
        }
    }

    [Fact]
    public void RotatedDisc_KeepsMembership()
    {
        var shape = InfluenceShape.Disc(new Int2(8, 8), 5, 3);
        var original = Paint(new Stamp(shape, Int2.Zero), Int2.Zero, new Int2(16, 16));
        var rotated = Paint(new Stamp(shape.Rotated(Quarter.R180), new Int2(15, 15)), Int2.Zero, new Int2(16, 16));

        for (var y = 0; y < 16; y++)
        for (var x = 0; x < 16; x++)
            Assert.Equal(original[x, y], rotated[15 - x, 15 - y]);
    }

    [Fact]
    public void RotatedRect_PreservesArea()
    {
        var shape = InfluenceShape.SolidRect(new Int2(1, 2), new Int2(5, 3), 4);
        var rotated = shape.Rotated(Quarter.R90);

        Assert.Equal(new Int2(2, -5), rotated.RectMin);
        Assert.Equal(new Int2(3, 5), rotated.RectSize);
        Assert.Equal(4, rotated.Weight);
    }

    [Fact]
    public void Inset_ShapesAreStrictlySmaller()
    {
        var disc = InfluenceShape.Disc(Int2.Zero, 6, 1).Inset(2);
        Assert.Equal(4, disc.DiscRadius);

        var rect = InfluenceShape.SolidRect(new Int2(1, 1), new Int2(8, 8), 1).Inset(3);
        Assert.Equal(new Int2(4, 4), rect.RectMin);
        Assert.Equal(new Int2(2, 2), rect.RectSize);
    }

    [Fact]
    public void DegenerateShapes_EmitNothing()
    {
        var shapes = new[]
        {
            InfluenceShape.SolidRect(Int2.Zero, new Int2(0, 4), 5),
            InfluenceShape.SolidRect(Int2.Zero, new Int2(4, -1), 5),
            InfluenceShape.Disc(Int2.Zero, -1, 5),
            InfluenceShape.Annulus(Int2.Zero, 3, 3, 5),
            InfluenceShape.Capsule(Int2.Zero, new Int2(1, 1), -2, 5),
            InfluenceShape.Sector(Int2.Zero, -4, new Int2(1, 0), new Int2(0, 1), 5)
        };

        foreach (var shape in shapes)
        {
            var bounds = Rasterizer.Bounds(shape, Int2.Zero);
            Assert.True(bounds.IsEmpty, shape.Kind.ToString());

            var estimate = Rasterizer.EstimateSpanCount(shape);
            Assert.True(estimate == 0, shape.Kind.ToString());
        }
    }

    [Fact]
    public void Annulus_WithInnerZero_HollowCenter()
    {
        var shape = InfluenceShape.Annulus(new Int2(6, 6), 4, 0, 5);
        var grid = Paint(new Stamp(shape, Int2.Zero), Int2.Zero, new Int2(14, 14));

        for (var y = 0; y < grid.GetLength(1); y++)
        for (var x = 0; x < grid.GetLength(0); x++)
        {
            var dx = x - 6;
            var dy = y - 6;
            var expected = DiscCell(dx, dy, 4) && !DiscCell(dx, dy, 0) ? 5 : 0;
            Assert.Equal(expected, grid[x, y]);
        }
    }
}
