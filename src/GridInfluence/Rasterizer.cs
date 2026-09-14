using System.Runtime.CompilerServices;

namespace GridInfluence;

public unsafe ref struct SpanSink
{
    private readonly WeightedRect* _spans;
    private readonly int _capacity;

    public SpanSink(WeightedRect* spans, int capacity)
    {
        _spans = spans;
        _capacity = capacity;
        Count = 0;
    }

    public int Count { get; private set; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Push(in WeightedRect span)
    {
        if ((uint)Count < (uint)_capacity)
        {
            _spans[Count] = span;
            Count++;
        }
    }

    public void SealRemaining()
    {
        while (Count < _capacity)
        {
            _spans[Count] = WeightedRect.Empty;
            Count++;
        }
    }
}

public static class Rasterizer
{
    public static CellRect Bounds(in InfluenceShape shape, Int2 origin)
        => shape.Kind.IsBox() ? BoundsBox(in shape, origin) : BoundsRound(in shape, origin);

    public static int EstimateSpanCount(in InfluenceShape shape)
        => shape.Kind.IsBox() ? EstimateBox(in shape) : EstimateRound(in shape);

    public static void Emit(in Stamp stamp, ref SpanSink sink)
    {
        var shape = stamp.Shape;
        var origin = stamp.Origin;
        EmitByKind(ref sink, in shape, origin, shape.Weight);
    }

    private static CellRect BoundsBox(in InfluenceShape shape, Int2 origin)
    {
        return shape.Kind switch
        {
            ShapeKind.SolidRect => BoundsSolidRect(in shape, origin),
            ShapeKind.RectShell => BoundsRectShell(in shape, origin),
            _ => BoundsRoundedRect(in shape, origin)
        };
    }

    private static CellRect BoundsRound(in InfluenceShape shape, Int2 origin)
    {
        return shape.Kind switch
        {
            ShapeKind.Disc => BoundsDisc(in shape, origin),
            ShapeKind.Annulus => BoundsAnnulus(in shape, origin),
            ShapeKind.Capsule => BoundsCapsule(in shape, origin),
            ShapeKind.Ellipse => BoundsEllipse(in shape, origin),
            ShapeKind.ThickLine => BoundsThickLine(in shape, origin),
            ShapeKind.Sector => BoundsSector(in shape, origin),
            _ => CellRect.Empty
        };
    }

    private static int EstimateBox(in InfluenceShape shape)
    {
        return shape.Kind switch
        {
            ShapeKind.SolidRect => EstimateSolidRect(in shape),
            ShapeKind.RectShell => EstimateRectShell(in shape),
            _ => EstimateRoundedRect(in shape)
        };
    }

    private static int EstimateRound(in InfluenceShape shape)
    {
        return shape.Kind switch
        {
            ShapeKind.Disc => DiscRowCount(shape.DiscRadius),
            ShapeKind.Annulus => EstimateAnnulus(in shape),
            ShapeKind.Capsule => EstimateCapsule(in shape),
            ShapeKind.Ellipse => EstimateEllipse(in shape),
            ShapeKind.ThickLine => EstimateThickLine(in shape),
            ShapeKind.Sector => EstimateSector(in shape),
            _ => 0
        };
    }

    private static void EmitByKind(ref SpanSink sink, in InfluenceShape shape, Int2 origin, int weight)
    {
        if (shape.Kind.IsBox())
            EmitBox(ref sink, in shape, origin, weight);
        else
            EmitRound(ref sink, in shape, origin, weight);
    }

    private static void EmitBox(ref SpanSink sink, in InfluenceShape shape, Int2 origin, int weight)
    {
        switch (shape.Kind)
        {
            case ShapeKind.SolidRect:
                EmitSolidRect(ref sink, in shape, origin, weight);
                break;
            case ShapeKind.RectShell:
                EmitRectShell(ref sink, in shape, origin, weight);
                break;
            default:
                EmitRoundedRectShape(ref sink, in shape, origin, weight);
                break;
        }
    }

    private static void EmitRound(ref SpanSink sink, in InfluenceShape shape, Int2 origin, int weight)
    {
        switch (shape.Kind)
        {
            case ShapeKind.Disc:
                EmitDiscShape(ref sink, in shape, origin, weight);
                break;
            case ShapeKind.Annulus:
                EmitAnnulus(ref sink, in shape, origin, weight);
                break;
            case ShapeKind.Capsule:
                EmitCapsuleShape(ref sink, in shape, origin, weight);
                break;
            case ShapeKind.Ellipse:
                EmitEllipseShape(ref sink, in shape, origin, weight);
                break;
            case ShapeKind.ThickLine:
                EmitThickLine(ref sink, in shape, origin, weight);
                break;
            case ShapeKind.Sector:
                EmitSectorShape(ref sink, in shape, origin, weight);
                break;
        }
    }

    private static CellRect BoundsSolidRect(in InfluenceShape shape, Int2 origin)
    {
        if (shape.RectSize.X <= 0 || shape.RectSize.Y <= 0) return CellRect.Empty;

        var min = origin + shape.RectMin;
        return new CellRect(min, min + shape.RectSize);
    }

    private static CellRect BoundsRectShell(in InfluenceShape shape, Int2 origin)
    {
        if (shape.ShellSize.X <= 0 || shape.ShellSize.Y <= 0 || shape.ShellThickness <= 0) return CellRect.Empty;

        var min = origin + shape.ShellMin;
        return new CellRect(min, min + shape.ShellSize);
    }

    private static CellRect BoundsDisc(in InfluenceShape shape, Int2 origin)
    {
        if (shape.DiscRadius < 0) return CellRect.Empty;

        var center = origin + shape.DiscCenter;
        var r = shape.DiscRadius;
        return new CellRect(center - new Int2(r, r), center + new Int2(r + 1, r + 1));
    }

    private static CellRect BoundsAnnulus(in InfluenceShape shape, Int2 origin)
    {
        if (shape.AnnulusOuterRadius < 0 || shape.AnnulusInnerRadius >= shape.AnnulusOuterRadius)
            return CellRect.Empty;

        var center = origin + shape.AnnulusCenter;
        var r = shape.AnnulusOuterRadius;
        return new CellRect(center - new Int2(r, r), center + new Int2(r + 1, r + 1));
    }

    private static CellRect BoundsCapsule(in InfluenceShape shape, Int2 origin)
    {
        if (shape.CapsuleRadius < 0) return CellRect.Empty;

        var r = shape.CapsuleRadius;
        var a = origin + shape.CapsuleStart;
        var b = origin + shape.CapsuleEnd;
        return new CellRect(Int2.Min(a, b) - new Int2(r, r), Int2.Max(a, b) + new Int2(r + 1, r + 1));
    }

    private static CellRect BoundsEllipse(in InfluenceShape shape, Int2 origin)
    {
        if (shape.EllipseRadii.X < 0 || shape.EllipseRadii.Y < 0) return CellRect.Empty;

        var center = origin + shape.EllipseCenter;
        var radii = shape.EllipseRadii;
        return new CellRect(center - radii, center + radii + new Int2(1, 1));
    }

    private static CellRect BoundsRoundedRect(in InfluenceShape shape, Int2 origin)
    {
        if (shape.RoundedRectSize.X <= 0 || shape.RoundedRectSize.Y <= 0 || shape.RoundedRectRadius < 0)
            return CellRect.Empty;

        var min = origin + shape.RoundedRectMin;
        return new CellRect(min, min + shape.RoundedRectSize);
    }

    private static CellRect BoundsThickLine(in InfluenceShape shape, Int2 origin)
    {
        if (shape.ThickLineRadius < 0) return CellRect.Empty;

        var r = shape.ThickLineRadius;
        var a = origin + shape.ThickLineStart;
        var b = origin + shape.ThickLineEnd;
        return new CellRect(Int2.Min(a, b) - new Int2(r, r), Int2.Max(a, b) + new Int2(r + 1, r + 1));
    }

    private static CellRect BoundsSector(in InfluenceShape shape, Int2 origin)
    {
        if (shape.SectorRadius < 0) return CellRect.Empty;

        var center = origin + shape.SectorCenter;
        var r = shape.SectorRadius;
        return new CellRect(center - new Int2(r, r), center + new Int2(r + 1, r + 1));
    }

    private static int EstimateSolidRect(in InfluenceShape shape)
        => shape.RectSize.X > 0 && shape.RectSize.Y > 0 ? 1 : 0;

    private static int EstimateRectShell(in InfluenceShape shape)
    {
        if (shape.ShellSize.X <= 0 || shape.ShellSize.Y <= 0 || shape.ShellThickness <= 0) return 0;

        var inner = shape.ShellSize - new Int2(2 * shape.ShellThickness, 2 * shape.ShellThickness);
        return inner.X > 0 && inner.Y > 0 ? 2 : 1;
    }

    private static int EstimateAnnulus(in InfluenceShape shape)
    {
        if (shape.AnnulusOuterRadius < 0 || shape.AnnulusInnerRadius >= shape.AnnulusOuterRadius) return 0;

        var inner = shape.AnnulusInnerRadius >= 0 ? 2 * shape.AnnulusInnerRadius + 1 : 0;
        return IntegerMath.SaturatingAdd(2 * shape.AnnulusOuterRadius + 1, inner);
    }

    private static int EstimateCapsule(in InfluenceShape shape)
    {
        if (shape.CapsuleRadius < 0) return 0;

        var spanY = Math.Abs((long)shape.CapsuleEnd.Y - shape.CapsuleStart.Y);
        return IntegerMath.ClampToInt(spanY + 2L * shape.CapsuleRadius + 3L);
    }

    private static int EstimateEllipse(in InfluenceShape shape)
    {
        if (shape.EllipseRadii.X < 0 || shape.EllipseRadii.Y < 0) return 0;

        return IntegerMath.ClampToInt(2L * shape.EllipseRadii.Y + 1L);
    }

    private static int EstimateRoundedRect(in InfluenceShape shape)
    {
        if (shape.RoundedRectSize.X <= 0 || shape.RoundedRectSize.Y <= 0 || shape.RoundedRectRadius < 0) return 0;

        return shape.RoundedRectSize.Y;
    }

    private static int EstimateThickLine(in InfluenceShape shape)
    {
        if (shape.ThickLineRadius < 0) return 0;

        var spanY = Math.Abs((long)shape.ThickLineEnd.Y - shape.ThickLineStart.Y);
        return IntegerMath.ClampToInt(spanY + 2L * shape.ThickLineRadius + 3L);
    }

    private static int EstimateSector(in InfluenceShape shape)
        => shape.SectorRadius < 0 ? 0 : IntegerMath.ClampToInt(2L * shape.SectorRadius + 1L);

    private static int DiscRowCount(int radius)
        => radius < 0 ? 0 : IntegerMath.ClampToInt(2L * radius + 1L);

    private static void EmitSolidRect(ref SpanSink sink, in InfluenceShape shape, Int2 origin, int weight)
        => EmitRect(ref sink, origin + shape.RectMin, shape.RectSize, weight);

    private static void EmitRectShell(ref SpanSink sink, in InfluenceShape shape, Int2 origin, int weight)
        => EmitShell(ref sink, origin + shape.ShellMin, shape.ShellSize, shape.ShellThickness, weight);

    private static void EmitDiscShape(ref SpanSink sink, in InfluenceShape shape, Int2 origin, int weight)
        => EmitDisc(ref sink, origin + shape.DiscCenter, shape.DiscRadius, weight);

    private static void EmitAnnulus(ref SpanSink sink, in InfluenceShape shape, Int2 origin, int weight)
    {
        if (shape.AnnulusOuterRadius < 0 || shape.AnnulusInnerRadius >= shape.AnnulusOuterRadius) return;

        EmitDisc(ref sink, origin + shape.AnnulusCenter, shape.AnnulusOuterRadius, weight);
        if (shape.AnnulusInnerRadius >= 0)
            EmitDisc(ref sink, origin + shape.AnnulusCenter, shape.AnnulusInnerRadius, -weight);
    }

    private static void EmitCapsuleShape(ref SpanSink sink, in InfluenceShape shape, Int2 origin, int weight)
        => EmitCapsule(ref sink, origin + shape.CapsuleStart, origin + shape.CapsuleEnd, shape.CapsuleRadius, weight);

    private static void EmitEllipseShape(ref SpanSink sink, in InfluenceShape shape, Int2 origin, int weight)
        => EmitEllipse(ref sink, origin + shape.EllipseCenter, shape.EllipseRadii, weight);

    private static void EmitRoundedRectShape(ref SpanSink sink, in InfluenceShape shape, Int2 origin, int weight)
        => EmitRoundedRect(ref sink, origin + shape.RoundedRectMin, shape.RoundedRectSize, shape.RoundedRectRadius, weight);

    private static void EmitThickLine(ref SpanSink sink, in InfluenceShape shape, Int2 origin, int weight)
        => EmitCapsule(
            ref sink,
            origin + shape.ThickLineStart,
            origin + shape.ThickLineEnd,
            shape.ThickLineRadius,
            weight);

    private static void EmitSectorShape(ref SpanSink sink, in InfluenceShape shape, Int2 origin, int weight)
        => EmitSector(ref sink, origin + shape.SectorCenter, shape.SectorRadius, shape.SectorDir0, shape.SectorDir1, weight);

    private static void EmitRect(ref SpanSink sink, Int2 min, Int2 size, int weight)
    {
        if (size.X <= 0 || size.Y <= 0) return;

        sink.Push(new WeightedRect(new CellRect(min, min + size), weight));
    }

    private static void EmitShell(ref SpanSink sink, Int2 min, Int2 size, int thickness, int weight)
    {
        if (size.X <= 0 || size.Y <= 0 || thickness <= 0) return;

        EmitRect(ref sink, min, size, weight);
        var innerMin = min + new Int2(thickness, thickness);
        var innerSize = size - new Int2(2 * thickness, 2 * thickness);
        EmitRect(ref sink, innerMin, innerSize, -weight);
    }

    private static void EmitDisc(ref SpanSink sink, Int2 center, int radius, int weight)
    {
        if (radius < 0) return;

        var r2 = (long)radius * radius;
        var half = 0;
        for (var dy = -radius; dy <= 0; dy++)
            PushSpan(ref sink, center, dy, GrowHalf(ref half, r2 - (long)dy * dy), weight);

        for (var dy = 1; dy <= radius; dy++)
            PushSpan(ref sink, center, dy, ShrinkHalf(ref half, r2 - (long)dy * dy), weight);
    }

    private static int GrowHalf(ref int half, long rem)
    {
        while ((long)(half + 1) * (half + 1) <= rem) half++;

        return half;
    }

    private static int ShrinkHalf(ref int half, long rem)
    {
        while ((long)half * half > rem) half--;

        return half;
    }

    private static void EmitCapsule(ref SpanSink sink, Int2 a, Int2 b, int radius, int weight)
    {
        if (radius < 0) return;

        var capsule = new CapsuleAxis(b - a, radius);
        for (var localY = capsule.LoY; localY <= capsule.HiY; localY++)
            EmitCapsuleRow(ref sink, a, localY, in capsule, weight);
    }

    private static void EmitCapsuleRow(ref SpanSink sink, Int2 a, int localY, in CapsuleAxis capsule, int weight)
    {
        var seedX = Math.Clamp(SeedLocalX(localY, capsule.Axis), capsule.LoX, capsule.HiX);
        if (!Covered(seedX, localY, in capsule)) return;

        var leftX = LeftmostCovered(capsule.LoX, seedX, localY, in capsule);
        var rightX = RightmostCovered(seedX, capsule.HiX, localY, in capsule);
        PushSpan(ref sink, a, localY, leftX, rightX, weight);
    }

    private static int SeedLocalX(int localY, Int2 axis)
    {
        var loY = Math.Min(0, axis.Y);
        var hiY = Math.Max(0, axis.Y);

        if (axis.Y != 0 && localY >= loY && localY <= hiY) return (int)((long)axis.X * localY / axis.Y);

        return Math.Abs(localY) <= Math.Abs(localY - axis.Y) ? 0 : axis.X;
    }

    private static bool Covered(int px, int py, in CapsuleAxis capsule)
    {
        var projection = (long)px * capsule.AxisX + (long)py * capsule.AxisY;
        var distanceToStart = (long)px * px + (long)py * py;

        if (capsule.LengthSquared == 0 || projection <= 0) return distanceToStart <= capsule.RadiusSquared;

        if (projection >= capsule.LengthSquared) return EndCapCovered(px, py, in capsule);

        return distanceToStart * capsule.LengthSquared - projection * projection
               <= capsule.RadiusSquared * capsule.LengthSquared;
    }

    private static bool EndCapCovered(int px, int py, in CapsuleAxis capsule)
    {
        long qx = px - capsule.AxisX;
        long qy = py - capsule.AxisY;
        return qx * qx + qy * qy <= capsule.RadiusSquared;
    }

    private static int LeftmostCovered(int lo, int hi, int localY, in CapsuleAxis capsule)
    {
        while (lo < hi)
        {
            var mid = lo + ((hi - lo) >> 1);
            if (Covered(mid, localY, in capsule))
                hi = mid;
            else
                lo = mid + 1;
        }

        return lo;
    }

    private static int RightmostCovered(int lo, int hi, int localY, in CapsuleAxis capsule)
    {
        while (lo < hi)
        {
            var mid = lo + ((hi - lo + 1) >> 1);
            if (Covered(mid, localY, in capsule))
                lo = mid;
            else
                hi = mid - 1;
        }

        return lo;
    }

    private static void EmitEllipse(ref SpanSink sink, Int2 center, Int2 radii, int weight)
    {
        if (radii.X < 0 || radii.Y < 0) return;

        for (var dy = -radii.Y; dy <= radii.Y; dy++)
            PushSpan(ref sink, center, dy, EllipseHalfWidth(radii.X, radii.Y, dy), weight);
    }

    private static int EllipseHalfWidth(int rx, int ry, int dy)
    {
        if (rx <= 0) return 0;

        if (ry <= 0) return rx;

        var rx2 = (long)rx * rx;
        var ry2 = (long)ry * ry;
        var offset = (long)dy * dy * rx2;
        var limit = rx2 * ry2;
        var lo = 0;
        var hi = rx;
        while (lo < hi) StepEllipseSearch(ref lo, ref hi, ry2, offset, limit);

        return lo;
    }

    private static void StepEllipseSearch(ref int lo, ref int hi, long ry2, long offset, long limit)
    {
        var mid = lo + ((hi - lo + 1) >> 1);
        if ((long)mid * mid * ry2 + offset <= limit)
            lo = mid;
        else
            hi = mid - 1;
    }

    private static void EmitRoundedRect(ref SpanSink sink, Int2 min, Int2 size, int radius, int weight)
    {
        if (size.X <= 0 || size.Y <= 0 || radius < 0) return;

        var r = Math.Min(radius, (Math.Min(size.X, size.Y) - 1) >> 1);
        if (r <= 0)
        {
            EmitRect(ref sink, min, size, weight);
            return;
        }

        for (var ly = 0; ly < size.Y; ly++)
            PushRoundedRow(ref sink, min, size, r, ly, weight);
    }

    private static void PushRoundedRow(ref SpanSink sink, Int2 min, Int2 size, int r, int ly, int weight)
    {
        var y = min.Y + ly;
        var x0 = min.X;
        var x1 = min.X + size.X;
        var bottomBand = ly < r;
        if (bottomBand || ly >= size.Y - r)
        {
            var half = CornerHalfWidth(min, size, r, y, bottomBand);
            x0 = min.X + r - half;
            x1 = min.X + size.X - r + half;
        }

        sink.Push(new WeightedRect(new CellRect(new Int2(x0, y), new Int2(x1, y + 1)), weight));
    }

    private static int CornerHalfWidth(Int2 min, Int2 size, int r, int y, bool bottomBand)
    {
        var centerY = bottomBand ? min.Y + r : min.Y + size.Y - r - 1;
        var dy = y - centerY;
        return (int)IntegerMath.FloorSqrt((long)r * r - (long)dy * dy);
    }

    private static void EmitSector(ref SpanSink sink, Int2 center, int radius, Int2 d0, Int2 d1, int weight)
    {
        if (radius < 0) return;

        var r2 = (long)radius * radius;
        for (var dy = -radius; dy <= radius; dy++)
            PushSectorRow(ref sink, center, dy, r2, d0, d1, weight);
    }

    private static void PushSectorRow(ref SpanSink sink, Int2 center, int dy, long r2, Int2 d0, Int2 d1, int weight)
    {
        var rem = r2 - (long)dy * dy;
        if (rem < 0) return;

        var half = (int)IntegerMath.FloorSqrt(rem);
        var lo = -half;
        var hi = half;
        if (!ClipStartEdge(dy, d0, ref lo, ref hi)) return;
        if (!ClipEndEdge(dy, d1, ref lo, ref hi)) return;
        if (lo > hi) return;

        PushSpan(ref sink, center, dy, lo, hi, weight);
    }

    private static bool ClipStartEdge(int dy, Int2 d0, ref int lo, ref int hi)
    {
        var c0 = (long)d0.X * dy;
        if (d0.Y > 0) hi = (int)Math.Min(hi, IntegerMath.FloorDiv(c0, d0.Y));
        else if (d0.Y < 0) lo = (int)Math.Max(lo, IntegerMath.CeilDiv(c0, d0.Y));
        else if (c0 < 0) return false;

        return true;
    }

    private static bool ClipEndEdge(int dy, Int2 d1, ref int lo, ref int hi)
    {
        var c1 = (long)d1.X * dy;
        if (d1.Y > 0) lo = (int)Math.Max(lo, IntegerMath.CeilDiv(c1, d1.Y));
        else if (d1.Y < 0) hi = (int)Math.Min(hi, IntegerMath.FloorDiv(c1, d1.Y));
        else if (-(long)dy * d1.X < 0) return false;

        return true;
    }

    private static void PushSpan(ref SpanSink sink, Int2 origin, int dy, int half, int weight)
        => PushSpan(ref sink, origin, dy, -half, half, weight);

    private static void PushSpan(ref SpanSink sink, Int2 origin, int dy, int lo, int hi, int weight)
    {
        var y = origin.Y + dy;
        var rect = new CellRect(new Int2(origin.X + lo, y), new Int2(origin.X + hi + 1, y + 1));
        sink.Push(new WeightedRect(rect, weight));
    }

    private readonly struct CapsuleAxis
    {
        public CapsuleAxis(Int2 axis, int radius)
        {
            AxisX = axis.X;
            AxisY = axis.Y;
            LengthSquared = (long)axis.X * axis.X + (long)axis.Y * axis.Y;
            RadiusSquared = (long)radius * radius;
            LoX = Math.Min(0, axis.X) - radius;
            HiX = Math.Max(0, axis.X) + radius;
            LoY = Math.Min(0, axis.Y) - radius;
            HiY = Math.Max(0, axis.Y) + radius;
        }

        public Int2 Axis => new(AxisX, AxisY);

        public int AxisX { get; }
        public int AxisY { get; }
        public long LengthSquared { get; }
        public long RadiusSquared { get; }
        public int LoX { get; }
        public int HiX { get; }
        public int LoY { get; }
        public int HiY { get; }
    }
}
