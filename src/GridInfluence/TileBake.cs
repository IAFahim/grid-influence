using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace GridInfluence;

internal struct AxisBand
{
    public int Start;
    public int End;
    public int WeightQ8;
}

internal static unsafe class TileBake
{
    internal const int TileBits = 5;
    internal const int TileSize = 32;
    internal const int DiffPitch = 48;
    internal const int DiffRows = 33;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int RoundQ16(int value)
        => value < 0 ? -((-value + 32768) >> 16) : (value + 32768) >> 16;

    internal static void Footprint(
        float wx, float wy, float originX, float originY, float scale,
        StampVariant* v,
        out int px, out int py, out int fx, out int fy,
        out int x0, out int y0, out int x1, out int y1)
    {
        var qx = (int)MathF.Floor((wx - originX) * scale * 256f) + v->OriginQ8X;
        var qy = (int)MathF.Floor((wy - originY) * scale * 256f) + v->OriginQ8Y;
        px = qx >> 8;
        py = qy >> 8;
        fx = qx & 255;
        fy = qy & 255;
        x0 = px;
        y0 = py;
        x1 = px + v->Width + (fx != 0 ? 1 : 0);
        y1 = py + v->Height + (fy != 0 ? 1 : 0);
    }

    internal static int WriteBands(int length, int phase, AxisBand* bands)
    {
        if (phase == 0)
        {
            bands[0] = new AxisBand { Start = 0, End = length, WeightQ8 = 256 };
            return 1;
        }

        bands[0] = new AxisBand { Start = 0, End = 1, WeightQ8 = 256 - phase };
        if (length == 1)
        {
            bands[1] = new AxisBand { Start = 1, End = 2, WeightQ8 = phase };
            return 2;
        }

        bands[1] = new AxisBand { Start = 1, End = length, WeightQ8 = 256 };
        bands[2] = new AxisBand { Start = length, End = length + 1, WeightQ8 = phase };
        return 3;
    }

    internal static void EmitBox(
        int* difference, int tileX0, int tileY0,
        int px, int py, int fx, int fy, StampVariant* v, int gain)
    {
        AxisBand* xs = stackalloc AxisBand[3];
        AxisBand* ys = stackalloc AxisBand[3];
        var nx = WriteBands(v->Width, fx, xs);
        var ny = WriteBands(v->Height, fy, ys);
        var tx1 = tileX0 + TileSize;
        var ty1 = tileY0 + TileSize;

        for (var y = 0; y < ny; y++)
        for (var x = 0; x < nx; x++)
        {
            var value = RoundQ16(v->Constant * xs[x].WeightQ8 * ys[y].WeightQ8) * gain;
            if (value == 0) continue;

            var bx0 = Math.Max(px + xs[x].Start, tileX0);
            var by0 = Math.Max(py + ys[y].Start, tileY0);
            var bx1 = Math.Min(px + xs[x].End, tx1);
            var by1 = Math.Min(py + ys[y].End, ty1);
            if (bx1 <= bx0 || by1 <= by0) continue;

            var lx0 = bx0 - tileX0;
            var ly0 = by0 - tileY0;
            var lx1 = bx1 - tileX0;
            var ly1 = by1 - tileY0;
            difference[ly0 * DiffPitch + lx0] += value;
            difference[ly0 * DiffPitch + lx1] -= value;
            difference[ly1 * DiffPitch + lx0] -= value;
            difference[ly1 * DiffPitch + lx1] += value;
        }
    }

    internal static void EmitRaster(
        int* dense, int tileX0, int tileY0,
        int px, int py, int fx, int fy, StampVariant* v, int gain)
    {
        if (gain == 0) return;

        var tx1 = tileX0 + TileSize;
        var ty1 = tileY0 + TileSize;
        var gx0 = Math.Max(px, tileX0);
        var gy0 = Math.Max(py, tileY0);
        var gx1 = Math.Min(px + v->Width + 1, tx1);
        var gy1 = Math.Min(py + v->Height + 1, ty1);
        if (gx1 <= gx0 || gy1 <= gy0) return;

        var w00 = (256 - fx) * (256 - fy);
        var w10 = fx * (256 - fy);
        var w01 = (256 - fx) * fy;
        var w11 = fx * fy;

        var sx = gx0 - px;
        var sy = gy0 - py;
        var source = v->Data + sy * v->Pitch + sx;
        var destination = dense + (gy0 - tileY0) * TileSize + (gx0 - tileX0);
        var width = gx1 - gx0;
        var height = gy1 - gy0;

        for (var y = 0; y < height; y++)
        {
            var src = source + y * v->Pitch;
            var dst = destination + y * TileSize;
            for (var x = 0; x < width; x++)
            {
                var numerator =
                    src[x] * w00 +
                    src[x - 1] * w10 +
                    src[x - v->Pitch] * w01 +
                    src[x - v->Pitch - 1] * w11;
                dst[x] += RoundQ16(numerator) * gain;
            }
        }
    }

    internal static bool Resolve(int* difference, int* dense, int* previousRow, short* output)
    {
        var acc = Vector128<int>.Zero;
        var any = false;
        var vector = Sse2.IsSupported || AdvSimd.IsSupported;
        for (var y = 0; y < TileSize; y++)
        {
            var diffRow = difference + y * DiffPitch;
            var denseRow = dense + y * TileSize;
            var outRow = output + y * TileSize;
            var carry = 0;
            var x = 0;
            if (vector)
            {
                for (; x + 4 <= TileSize; x += 4)
                {
                    var d = Load128(diffRow + x);
                    var h = Prefix128(d) + Vector128.Create(carry);
                    carry = h[3];
                    var boxes = h + Load128(previousRow + x);
                    Store128(previousRow + x, boxes);
                    var total = boxes + Load128(denseRow + x);
                    acc |= total;
                    Pack4(outRow + x, total);
                }
            }
            else
            {
                for (; x < TileSize; x++)
                {
                    carry += diffRow[x];
                    var boxes = carry + previousRow[x];
                    previousRow[x] = boxes;
                    var total = boxes + denseRow[x];
                    any |= total != 0;
                    outRow[x] = (short)Math.Clamp(total, short.MinValue, short.MaxValue);
                }
            }
        }

        return vector ? !Vector128.EqualsAll(acc, Vector128<int>.Zero) : any;
    }

    internal static bool PackDense(int* dense, short* output)
    {
        var any = false;
        var i = 0;
        if (Sse2.IsSupported || AdvSimd.IsSupported)
        {
            var acc = Vector128<int>.Zero;
            for (; i + 4 <= TileSize * TileSize; i += 4)
            {
                var total = Load128(dense + i);
                acc |= total;
                Pack4(output + i, total);
            }

            any = !Vector128.EqualsAll(acc, Vector128<int>.Zero);
        }

        for (; i < TileSize * TileSize; i++)
        {
            var total = dense[i];
            any |= total != 0;
            output[i] = (short)Math.Clamp(total, short.MinValue, short.MaxValue);
        }

        return any;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Load128(int* p)
    {
        if (Sse2.IsSupported) return Sse2.LoadVector128(p);
        return AdvSimd.LoadVector128(p);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Store128(int* p, Vector128<int> v)
    {
        if (Sse2.IsSupported) Sse2.Store(p, v);
        else AdvSimd.Store(p, v);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Prefix128(Vector128<int> v)
    {
        if (Sse2.IsSupported)
        {
            v += Sse2.ShiftLeftLogical128BitLane(v.AsByte(), 4).AsInt32();
            v += Sse2.ShiftLeftLogical128BitLane(v.AsByte(), 8).AsInt32();
            return v;
        }

        v += AdvSimd.ExtractVector128(Vector128<byte>.Zero, v.AsByte(), 12).AsInt32();
        v += AdvSimd.ExtractVector128(Vector128<byte>.Zero, v.AsByte(), 8).AsInt32();
        return v;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Pack4(short* destination, Vector128<int> v)
    {
        if (Sse2.IsSupported)
        {
            *(long*)destination = Sse2.PackSignedSaturate(v, v).AsInt64().ToScalar();
            return;
        }

        if (AdvSimd.IsSupported)
        {
            *(long*)destination = AdvSimd.ExtractNarrowingSaturateLower(v).AsInt64().ToScalar();
            return;
        }

        for (var i = 0; i < 4; i++)
            destination[i] = (short)Math.Clamp(v[i], short.MinValue, short.MaxValue);
    }
}
