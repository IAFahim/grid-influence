using System.Runtime.CompilerServices;

namespace Gi;

internal enum Plane : byte
{
    Difference,
    Dense,
    Tent,
    Bell,
}

internal unsafe struct Placement
{
    public StampVariant* V;
    public StampKind Kind;
    public Plane Plane;
    public bool Turned;
    public bool Arc;
    public int Px;
    public int Py;
    public int Fx;
    public int Fy;
    public int ExtentX;
    public int ExtentY;
    public int X0;
    public int Y0;
    public int X1;
    public int Y1;
    public int Sampling;
    public long CentreX;
    public long CentreY;
    public int Cos;
    public int Sin;
    public long SupportU;
    public long SupportV;
    public long Reach;
    public long ReachSquared;
    public long Inner;
    public long Outer;
    public long Inverse;
    public int Reduce;
    public long Bound;
    public long Curve;
    public long HalfU;
    public long HalfV;
    public int ShiftU;
    public int ShiftV;
    public long SampleUx;
    public long SampleUy;
    public long SampleU0;
    public long SampleVx;
    public long SampleVy;
    public long SampleV0;
    public sbyte* Data;
    public int Width;
    public int Height;
    public int Pitch;
    public int Level;

    public readonly bool Reaches(int cx, int cy) => cx >= X0 && cy >= Y0 && cx < X1 && cy < Y1;
}

internal static unsafe partial class TileBake
{
    private const int CellHalf = 512;
    private const int QuarterTurn = 16384;
    private const int SinA1 = 25736;
    private const int SinA3 = -10583;
    private const int SinA5 = 1231;

    internal static int Sin(int angle)
    {
        var a = angle & 0xFFFF;
        var quadrant = a >> 14;
        var t = a & (QuarterTurn - 1);
        if ((quadrant & 1) != 0) t = QuarterTurn - t;
        var x2 = (t * t) >> 14;
        var s = (t * (SinA1 + ((x2 * (SinA3 + ((x2 * SinA5) >> 14))) >> 14))) >> 14;
        return quadrant >= 2 ? -s : s;
    }

    internal static int Cos(int angle) => Sin(angle + QuarterTurn);

    internal static bool Place(float x, float y, float originX, float originY, int scaleQ8, int size,
        StampVariant* v, int angle, int scale, out Placement p)
    {
        p = default;
        var sampling = (int)Math.Min((long)scaleQ8 * scale >> 8, int.MaxValue);
        if (sampling == 0) return false;

        var round = v->Kind >= StampKind.Disk;
        var turned = round || angle != 0;
        Footprint(x, y, originX, originY, scaleQ8, sampling, size, v, v->Kind == StampKind.ConstantRectangle && !turned,
            out p.Px, out p.Py, out p.Fx, out p.Fy, out p.ExtentX, out p.ExtentY,
            out p.X0, out p.Y0, out p.X1, out p.Y1);
        p.V = v;
        p.Sampling = sampling;
        p.Kind = Effective(v, p.ExtentX, p.ExtentY);
        p.Turned = turned;
        p.Plane = p.Kind switch
        {
            StampKind.ConstantRectangle => turned ? Plane.Dense : Plane.Difference,
            StampKind.Tent => Plane.Tent,
            StampKind.Bell => Plane.Bell,
            _ => Plane.Dense,
        };

        if (!turned)
        {
            if (p.Plane is Plane.Tent or Plane.Bell)
                p.Curve = Normalizer(Axis(p.Kind, p.Px, p.Fx, p.ExtentX), Axis(p.Kind, p.Py, p.Fy, p.ExtentY));
            return true;
        }

        p.CentreX = 2L * ((long)p.Px * 256 + p.Fx) + p.ExtentX;
        p.CentreY = 2L * ((long)p.Py * 256 + p.Fy) + p.ExtentY;
        p.Cos = Cos(angle);
        p.Sin = Sin(angle);
        if (round)
        {
            p.Arc = v->Arc != 0;
            p.Reach = p.Kind == StampKind.Disk ? p.ExtentX : Math.Max(p.ExtentX, CellHalf);
            p.ReachSquared = p.Reach * p.Reach;
            p.Bound = p.Kind == StampKind.Disk ? p.Reach + CellHalf / 2 : p.Reach;
            if (p.Kind == StampKind.Disk)
            {
                p.Inner = p.Reach > CellHalf / 2 - 1 ? (p.Reach - (CellHalf / 2 - 1)) * (p.Reach - (CellHalf / 2 - 1)) : 0;
                p.Outer = p.Bound * p.Bound;
            }
            else if (p.Kind == StampKind.Cone)
            {
                p.Inverse = ((1L << 48) + p.Reach - 1) / p.Reach;
            }
            else
            {
                p.Reduce = Math.Max(0, BitLength(p.ReachSquared) - 46);
                var reduced = p.ReachSquared >> p.Reduce;
                p.Inverse = ((1L << 62) - 1) / reduced + 1;
            }

            long x0, y0, x1, y1;
            if (p.Arc)
            {
                SectorBounds(p.CentreX, p.CentreY, p.Bound, angle, v->Arc, out x0, out y0, out x1, out y1);
                x0 -= CellHalf;
                y0 -= CellHalf;
                x1 += CellHalf;
                y1 += CellHalf;
            }
            else
            {
                x0 = p.CentreX - p.Bound;
                y0 = p.CentreY - p.Bound;
                x1 = p.CentreX + p.Bound;
                y1 = p.CentreY + p.Bound;
            }

            Cells(x0, x1, size, out p.X0, out p.X1);
            Cells(y0, y1, size, out p.Y0, out p.Y1);
            return true;
        }

        p.SupportU = p.ExtentX;
        p.SupportV = p.ExtentY;
        if (p.Plane is Plane.Tent or Plane.Bell)
        {
            var bell = p.Kind == StampKind.Bell;
            var halfU = Math.Max(256L, p.ExtentX >> 1);
            var halfV = Math.Max(256L, p.ExtentY >> 1);
            p.ShiftU = Math.Clamp(BitLength(halfU) - (bell ? 8 : 15), 0, 7);
            p.ShiftV = Math.Clamp(BitLength(halfV) - (bell ? 8 : 15), 0, 7);
            p.HalfU = halfU >> p.ShiftU;
            p.HalfV = halfV >> p.ShiftV;
            var peak = (bell ? p.HalfU * p.HalfU : p.HalfU) * (bell ? p.HalfV * p.HalfV : p.HalfV);
            p.Curve = ((1L << 40) + peak / 2) / peak;
            p.SupportU = 2 * halfU;
            p.SupportV = 2 * halfV;
        }
        else if (p.Kind == StampKind.Raster)
        {
            var step = 256L * 65536 / sampling;
            var data = v->Data;
            var w = v->Width;
            var h = v->Height;
            var mips = v->Mips;
            while (p.Level < v->MipCount && (step >> (p.Level + 1)) >= 65536)
            {
                w = (w + 1) >> 1;
                h = (h + 1) >> 1;
                data = mips + (w + 2) + 1;
                mips += (w + 2) * (h + 2);
                p.Level++;
            }

            p.Data = data;
            p.Width = w;
            p.Height = h;
            p.Pitch = w + 2;
            p.SupportU += 2L * sampling + CellHalf / 2;
            p.SupportV += 2L * sampling + CellHalf / 2;
            p.SampleUx = FloorDiv((long)p.Cos << 33, sampling);
            p.SampleUy = FloorDiv((long)p.Sin << 33, sampling);
            p.SampleU0 = ((long)v->Width << 47) - FloorDiv(1L << 55, sampling);
            p.SampleVx = FloorDiv(-(long)p.Sin << 33, sampling);
            p.SampleVy = FloorDiv((long)p.Cos << 33, sampling);
            p.SampleV0 = ((long)v->Height << 47) - FloorDiv(1L << 55, sampling);
        }

        var c = Math.Abs((long)p.Cos);
        var s = Math.Abs((long)p.Sin);
        var ax = ((c * p.SupportU + s * p.SupportV) >> 14) + CellHalf;
        var ay = ((s * p.SupportU + c * p.SupportV) >> 14) + CellHalf;
        Cells(p.CentreX - ax, p.CentreX + ax, size, out p.X0, out p.X1);
        Cells(p.CentreY - ay, p.CentreY + ay, size, out p.Y0, out p.Y1);
        return true;
    }

    private static void Cells(long low, long high, int size, out int first, out int end)
    {
        first = (int)Math.Clamp(FloorDiv(low - CellHalf / 2, CellHalf), -1, size + 1L);
        end = (int)Math.Clamp(FloorDiv(high - CellHalf / 2, CellHalf) + 1, -1, size + 1L);
    }

    private static void SectorBounds(long cx, long cy, long reach, int facing, int half,
        out long x0, out long y0, out long x1, out long y1)
    {
        x0 = cx;
        y0 = cy;
        x1 = cx;
        y1 = cy;
        Extend(cx, cy, reach, facing - half, ref x0, ref y0, ref x1, ref y1);
        Extend(cx, cy, reach, facing + half, ref x0, ref y0, ref x1, ref y1);
        for (var axis = 0; axis < 4 * QuarterTurn; axis += QuarterTurn)
        {
            var off = ((axis - facing + 2 * QuarterTurn) & 0xFFFF) - 2 * QuarterTurn;
            if (Math.Abs(off) <= half) Extend(cx, cy, reach, axis, ref x0, ref y0, ref x1, ref y1);
        }
    }

    private static void Extend(long cx, long cy, long reach, int angle, ref long x0, ref long y0, ref long x1, ref long y1)
    {
        var x = cx + ((reach * Cos(angle)) >> 14);
        var y = cy + ((reach * Sin(angle)) >> 14);
        x0 = Math.Min(x0, x);
        y0 = Math.Min(y0, y);
        x1 = Math.Max(x1, x);
        y1 = Math.Max(y1, y);
    }

    internal static bool Touches(Placement* p, int tileX0, int tileY0)
    {
        if (!p->Turned) return true;

        var lx = (long)tileX0 * CellHalf;
        var ly = (long)tileY0 * CellHalf;
        var hx = lx + TileSize * CellHalf;
        var hy = ly + TileSize * CellHalf;
        if (p->Kind >= StampKind.Disk)
        {
            var nx = Math.Clamp(p->CentreX, lx, hx) - p->CentreX;
            var ny = Math.Clamp(p->CentreY, ly, hy) - p->CentreY;
            var reach = p->Bound + CellHalf;
            return nx * nx + ny * ny <= reach * reach;
        }

        var minU = long.MaxValue;
        var maxU = long.MinValue;
        var minV = long.MaxValue;
        var maxV = long.MinValue;
        for (var corner = 0; corner < 4; corner++)
        {
            var dx = ((corner & 1) == 0 ? lx : hx) - p->CentreX;
            var dy = ((corner & 2) == 0 ? ly : hy) - p->CentreY;
            var u = (dx * p->Cos + dy * p->Sin) >> 14;
            var v = (dy * p->Cos - dx * p->Sin) >> 14;
            minU = Math.Min(minU, u);
            maxU = Math.Max(maxU, u);
            minV = Math.Min(minV, v);
            maxV = Math.Max(maxV, v);
        }

        var mu = p->SupportU + CellHalf;
        var mv = p->SupportV + CellHalf;
        return maxU >= -mu && minU <= mu && maxV >= -mv && minV <= mv;
    }

    #if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    #endif
    internal static void EmitTurnedDense(int* dense, int tileX0, int tileY0, Placement* p, int gain)
    {
        var x0 = Math.Max(p->X0, tileX0);
        var x1 = Math.Min(p->X1, tileX0 + TileSize);
        var y0 = Math.Max(p->Y0, tileY0);
        var y1 = Math.Min(p->Y1, tileY0 + TileSize);
        for (var cy = y0; cy < y1; cy++)
        {
            var dy = (long)cy * CellHalf + CellHalf / 2 - p->CentreY;
            var row = dense + (cy - tileY0) * TileSize - tileX0;
            for (var cx = x0; cx < x1; cx++)
            {
                var value = DenseAt(p, (long)cx * CellHalf + CellHalf / 2 - p->CentreX, dy);
                if (value != 0) row[cx] += value * gain;
            }
        }
    }

    #if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    #endif
    internal static void EmitTurnedSmooth(long* sums, int tileX0, int tileY0, Placement* p, int gain)
    {
        var x0 = Math.Max(p->X0, tileX0);
        var x1 = Math.Min(p->X1, tileX0 + TileSize);
        var y0 = Math.Max(p->Y0, tileY0);
        var y1 = Math.Min(p->Y1, tileY0 + TileSize);
        for (var cy = y0; cy < y1; cy++)
        {
            var dy = (long)cy * CellHalf + CellHalf / 2 - p->CentreY;
            var row = sums + (cy - tileY0) * TileSize - tileX0;
            for (var cx = x0; cx < x1; cx++)
            {
                var value = SmoothAt(p, (long)cx * CellHalf + CellHalf / 2 - p->CentreX, dy);
                if (value != 0) row[cx] += value * gain;
            }
        }
    }

    internal static int TurnedDenseAt(Placement* p, int cx, int cy)
        => DenseAt(p, (long)cx * CellHalf + CellHalf / 2 - p->CentreX, (long)cy * CellHalf + CellHalf / 2 - p->CentreY);

    internal static long TurnedSmoothAt(Placement* p, int cx, int cy)
        => SmoothAt(p, (long)cx * CellHalf + CellHalf / 2 - p->CentreX, (long)cy * CellHalf + CellHalf / 2 - p->CentreY);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int DenseAt(Placement* p, long dx, long dy)
    {
        if (p->Kind >= StampKind.Disk) return RoundAt(p, dx, dy);
        if (p->Kind != StampKind.ConstantRectangle) return SampleAt(p, dx, dy);

        var u = (dx * p->Cos + dy * p->Sin) >> 14;
        var v = (dy * p->Cos - dx * p->Sin) >> 14;
        return RoundQ16(p->V->Constant * Overlap(u, p->ExtentX) * Overlap(v, p->ExtentY));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Overlap(long offset, int extent)
    {
        var covered = Math.Min(extent, offset + CellHalf / 2) - Math.Max(-extent, offset - CellHalf / 2);
        return (int)(Math.Clamp(covered, 0, CellHalf) >> 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SampleAt(Placement* p, long dx, long dy)
    {
        var tx = ((dx * p->SampleUx + dy * p->SampleUy + p->SampleU0) >> 32) >> p->Level;
        var ty = ((dx * p->SampleVx + dy * p->SampleVy + p->SampleV0) >> 32) >> p->Level;
        var ix = tx >> 16;
        var iy = ty >> 16;
        if (ix < -1 || iy < -1 || ix >= p->Width || iy >= p->Height) return 0;

        var fx = (int)((tx >> 8) & 255);
        var fy = (int)((ty >> 8) & 255);
        var row0 = p->Data + iy * p->Pitch;
        var row1 = row0 + p->Pitch;
        var top = row0[ix] * (256 - fx) + row0[ix + 1] * fx;
        var bottom = row1[ix] * (256 - fx) + row1[ix + 1] * fx;
        return RoundQ16(top * (256 - fy) + bottom * fy);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int RoundAt(Placement* p, long dx, long dy)
    {
        var cover = 256L;
        if (p->Arc)
        {
            var along = (dx * p->Cos + dy * p->Sin) >> 14;
            var across = Math.Abs((dy * p->Cos - dx * p->Sin) >> 14);
            var outside = (across * p->V->ArcCos - along * p->V->ArcSin) >> 14;
            if (outside >= CellHalf / 2) return 0;
            cover = Math.Min(CellHalf / 2 - outside, CellHalf) >> 1;
        }

        var distance = dx * dx + dy * dy;
        long weight;
        if (p->Kind == StampKind.Disk)
        {
            if (distance >= p->Outer) return 0;
            if (distance < p->Inner)
            {
                weight = Math.Min(CellHalf, 2 * p->Reach) << 7;
            }
            else
            {
                var edge = p->Reach - Root(distance) + CellHalf / 2;
                weight = Math.Min(Math.Min(edge, CellHalf), 2 * p->Reach) << 7;
            }
        }
        else
        {
            if (distance >= p->ReachSquared) return 0;
            weight = p->Kind == StampKind.Cone
                ? ((p->Reach - Root(distance)) * p->Inverse) >> 32
                : (((p->ReachSquared - distance) >> p->Reduce) * p->Inverse) >> 46;
        }

        return RoundQ24(p->V->Constant * weight * cover);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long SmoothAt(Placement* p, long dx, long dy)
    {
        var du = ((dx * p->Cos + dy * p->Sin) >> 14) >> (p->ShiftU + 1);
        var dv = ((dy * p->Cos - dx * p->Sin) >> 14) >> (p->ShiftV + 1);
        long wu;
        long wv;
        if (p->Kind == StampKind.Bell)
        {
            wu = p->HalfU * p->HalfU - du * du;
            wv = p->HalfV * p->HalfV - dv * dv;
        }
        else
        {
            wu = p->HalfU - Math.Abs(du);
            wv = p->HalfV - Math.Abs(dv);
        }

        if (wu <= 0 || wv <= 0) return 0;
        return p->V->Constant * p->Curve * wu * wv;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int RoundQ24(long value) => (int)((value + (1L << 23) + (value >> 63)) >> 24);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long FloorDiv(long value, long divisor)
    {
        var q = value / divisor;
        return q * divisor > value ? q - 1 : q;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long Root(long value)
    {
        var root = (long)Math.Sqrt(value);
        if (root * root > value) return root - 1;
        return (root + 1) * (root + 1) <= value ? root + 1 : root;
    }
}
