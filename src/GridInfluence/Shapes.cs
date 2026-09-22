namespace GridInfluence;

public enum ShapeKind : byte
{
    SolidRect,
    RectShell,
    Disc,
    Annulus,
    Capsule,
    Ellipse,
    RoundedRect,
    ThickLine,
    Sector,
    Painted
}

public readonly struct InfluenceShape
{
    public readonly ShapeKind Kind;
    public readonly sbyte Weight;

    private InfluenceShape(ShapeKind kind, sbyte weight, Int2 a, Int2 b, int p, int q, Int2 aux)
    {
        Kind = kind;
        Weight = weight;
        RectMin = a;
        RectSize = b;
        ShellThickness = p;
        AnnulusInnerRadius = q;
        SectorDir1 = aux;
    }

    public Int2 RectMin { get; }

    public Int2 RectSize { get; }

    public Int2 ShellMin => RectMin;
    public Int2 ShellSize => RectSize;
    public int ShellThickness { get; }

    public Int2 DiscCenter => RectMin;
    public int DiscRadius => ShellThickness;

    public Int2 AnnulusCenter => RectMin;
    public int AnnulusOuterRadius => ShellThickness;
    public int AnnulusInnerRadius { get; }

    public Int2 CapsuleStart => RectMin;
    public Int2 CapsuleEnd => RectSize;
    public int CapsuleRadius => ShellThickness;

    public Int2 EllipseCenter => RectMin;
    public Int2 EllipseRadii => RectSize;

    public Int2 RoundedRectMin => RectMin;
    public Int2 RoundedRectSize => RectSize;
    public int RoundedRectRadius => ShellThickness;

    public Int2 ThickLineStart => RectMin;
    public Int2 ThickLineEnd => RectSize;
    public int ThickLineRadius => ShellThickness;

    public Int2 SectorCenter => RectMin;
    public Int2 SectorDir0 => RectSize;
    public int SectorRadius => ShellThickness;
    public Int2 SectorDir1 { get; }

    public InfluenceShape WithWeight(sbyte weight)
        => new(Kind, weight, RectMin, RectSize, ShellThickness, AnnulusInnerRadius, SectorDir1);

    public bool TryScaleWeight(float clipWeight, out InfluenceShape scaled)
    {
        var scaledWeight = (int)MathF.Round(Weight * clipWeight, MidpointRounding.AwayFromZero);
        if (scaledWeight == 0)
        {
            scaled = default;
            return false;
        }

        scaled = WithWeight((sbyte)Math.Clamp(scaledWeight, sbyte.MinValue, sbyte.MaxValue));
        return true;
    }

    public InfluenceShape Negated() => WithWeight((sbyte)-Weight);

    public InfluenceShape Rotated(Quarter quarter)
    {
        if (quarter == Quarter.R0) return this;

        return Kind.IsBox() ? RotateBox(quarter) : RotateRound(quarter);
    }

    public InfluenceShape Inset(int amount)
    {
        if (amount <= 0) return this;

        return Kind.IsBox() ? InsetBox(amount) : InsetRound(amount);
    }

    private InfluenceShape RotateBox(Quarter quarter)
    {
        return Kind switch
        {
            ShapeKind.RectShell => RotateRectShell(quarter),
            ShapeKind.RoundedRect => RotateRoundedRect(quarter),
            _ => RotateSolidRect(quarter)
        };
    }

    private InfluenceShape RotateRound(Quarter quarter)
    {
        return Kind switch
        {
            ShapeKind.Disc => Disc(ShapeRotation.RotatePoint(DiscCenter, quarter), DiscRadius, Weight),
            ShapeKind.Annulus => Annulus(
                ShapeRotation.RotatePoint(AnnulusCenter, quarter), AnnulusOuterRadius, AnnulusInnerRadius, Weight),
            ShapeKind.Capsule => Capsule(
                ShapeRotation.RotatePoint(CapsuleStart, quarter),
                ShapeRotation.RotatePoint(CapsuleEnd, quarter),
                CapsuleRadius,
                Weight),
            ShapeKind.Ellipse => Ellipse(
                ShapeRotation.RotatePoint(EllipseCenter, quarter),
                ShapeRotation.RotateExtent(EllipseRadii, quarter),
                Weight),
            ShapeKind.ThickLine => ThickLine(
                ShapeRotation.RotatePoint(ThickLineStart, quarter),
                ShapeRotation.RotatePoint(ThickLineEnd, quarter),
                ThickLineRadius,
                Weight),
            ShapeKind.Sector => Sector(
                ShapeRotation.RotatePoint(SectorCenter, quarter),
                SectorRadius,
                ShapeRotation.RotatePoint(SectorDir0, quarter),
                ShapeRotation.RotatePoint(SectorDir1, quarter),
                Weight),
            _ => this
        };
    }

    private InfluenceShape RotateSolidRect(Quarter quarter)
    {
        var (min, size) = ShapeRotation.RotateRect(RectMin, RectSize, quarter);
        return SolidRect(min, size, Weight);
    }

    private InfluenceShape RotateRectShell(Quarter quarter)
    {
        var (min, size) = ShapeRotation.RotateRect(ShellMin, ShellSize, quarter);
        return RectShell(min, size, ShellThickness, Weight);
    }

    private InfluenceShape RotateRoundedRect(Quarter quarter)
    {
        var (min, size) = ShapeRotation.RotateRect(RoundedRectMin, RoundedRectSize, quarter);
        return RoundedRect(min, size, RoundedRectRadius, Weight);
    }

    private InfluenceShape InsetBox(int d)
    {
        var one = new Int2(d, d);
        var two = new Int2(2 * d, 2 * d);
        return Kind switch
        {
            ShapeKind.RectShell => RectShell(ShellMin + one, ShellSize - two, ShellThickness, Weight),
            ShapeKind.RoundedRect => RoundedRect(
                RoundedRectMin + one,
                RoundedRectSize - two,
                Math.Max(0, RoundedRectRadius - d),
                Weight),
            _ => SolidRect(RectMin + one, RectSize - two, Weight)
        };
    }

    private InfluenceShape InsetRound(int d)
    {
        return Kind switch
        {
            ShapeKind.Disc => Disc(DiscCenter, DiscRadius - d, Weight),
            ShapeKind.Annulus => Annulus(AnnulusCenter, AnnulusOuterRadius - d, AnnulusInnerRadius, Weight),
            ShapeKind.Capsule => Capsule(CapsuleStart, CapsuleEnd, CapsuleRadius - d, Weight),
            ShapeKind.Ellipse => Ellipse(EllipseCenter, EllipseRadii - new Int2(d, d), Weight),
            ShapeKind.ThickLine => ThickLine(ThickLineStart, ThickLineEnd, ThickLineRadius - d, Weight),
            ShapeKind.Sector => Sector(SectorCenter, SectorRadius - d, SectorDir0, SectorDir1, Weight),
            _ => this
        };
    }

    public static InfluenceShape SolidRect(Int2 min, Int2 size, sbyte weight)
        => new(ShapeKind.SolidRect, weight, min, size, 0, 0, Int2.Zero);

    public static InfluenceShape RectShell(Int2 min, Int2 size, int thickness, sbyte weight)
        => new(ShapeKind.RectShell, weight, min, size, thickness, 0, Int2.Zero);

    public static InfluenceShape Disc(Int2 center, int radius, sbyte weight)
        => new(ShapeKind.Disc, weight, center, Int2.Zero, radius, 0, Int2.Zero);

    public static InfluenceShape Annulus(Int2 center, int outerRadius, int innerRadius, sbyte weight)
        => new(ShapeKind.Annulus, weight, center, Int2.Zero, outerRadius, innerRadius, Int2.Zero);

    public static InfluenceShape Capsule(Int2 start, Int2 end, int radius, sbyte weight)
        => new(ShapeKind.Capsule, weight, start, end, radius, 0, Int2.Zero);

    public static InfluenceShape Ellipse(Int2 center, Int2 radii, sbyte weight)
        => new(ShapeKind.Ellipse, weight, center, radii, 0, 0, Int2.Zero);

    public static InfluenceShape RoundedRect(Int2 min, Int2 size, int radius, sbyte weight)
        => new(ShapeKind.RoundedRect, weight, min, size, radius, 0, Int2.Zero);

    public static InfluenceShape ThickLine(Int2 start, Int2 end, int radius, sbyte weight)
        => new(ShapeKind.ThickLine, weight, start, end, radius, 0, Int2.Zero);

    public static InfluenceShape Sector(Int2 center, int radius, Int2 dir0, Int2 dir1, sbyte weight)
        => new(ShapeKind.Sector, weight, center, dir0, radius, 0, dir1);
}

public readonly struct FieldStamp
{
    public readonly InfluenceShape Shape;
    public readonly Int2 Origin;

    public FieldStamp(InfluenceShape shape, Int2 origin)
    {
        Shape = shape;
        Origin = origin;
    }

    public FieldStamp Negated() => new(Shape.WithWeight((sbyte)-Shape.Weight), Origin);
}

public enum Quarter : byte
{
    R0,
    R90,
    R180,
    R270
}

public static class ShapeRotation
{
    public static Int2 RotatePoint(Int2 p, Quarter q)
    {
        return q switch
        {
            Quarter.R90 => new Int2(p.Y, -p.X),
            Quarter.R180 => new Int2(-p.X, -p.Y),
            Quarter.R270 => new Int2(-p.Y, p.X),
            _ => p
        };
    }

    public static Int2 RotateExtent(Int2 size, Quarter q)
        => q is Quarter.R90 or Quarter.R270 ? new Int2(size.Y, size.X) : size;

    public static (Int2 Min, Int2 Size) RotateRect(Int2 min, Int2 size, Quarter q)
    {
        var maxInclusive = min + size - new Int2(1, 1);
        var a = RotatePoint(min, q);
        var b = RotatePoint(maxInclusive, q);
        var newMin = Int2.Min(a, b);
        var newMax = Int2.Max(a, b);
        return (newMin, newMax - newMin + new Int2(1, 1));
    }
}

public static class ShapeKinds
{
    public static bool IsBox(this ShapeKind kind)
        => kind is ShapeKind.SolidRect or ShapeKind.RectShell or ShapeKind.RoundedRect;
}
