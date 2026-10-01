using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Gi;

internal static unsafe class TileBake
{
    internal const int TileBits = 5;
    internal const int TileSize = 32;
    internal const int DiffPitch = 48;
    internal const int DiffRows = 33;

    internal static bool Vector => Sse2.IsSupported || AdvSimd.IsSupported;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int RoundQ16(int value)
        => (value + 32768 + (value >> 31)) >> 16;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Footprint(
        float wx, float wy, float originX, float originY, float scaleQ8,
        StampVariant* v,
        out int px, out int py, out int fx, out int fy,
        out int x0, out int y0, out int x1, out int y1)
    {
        var qx = (int)MathF.Floor((wx - originX) * scaleQ8) + v->OriginQ8X;
        var qy = (int)MathF.Floor((wy - originY) * scaleQ8) + v->OriginQ8Y;
        px = qx >> 8;
        py = qy >> 8;
        fx = qx & 255;
        fy = qy & 255;
        x0 = px;
        y0 = py;
        x1 = px + v->Width + (fx != 0 ? 1 : 0);
        y1 = py + v->Height + (fy != 0 ? 1 : 0);
    }

    internal static void EmitBox(
        int* difference, int tileX0, int tileY0,
        int px, int py, int fx, int fy, StampVariant* v, int gain)
    {
        if (gain == 0) return;

        var tx1 = tileX0 + TileSize;
        var ty1 = tileY0 + TileSize;

        var bxLo = stackalloc int[3];
        var bxHi = stackalloc int[3];
        var bxWeight = stackalloc int[3];
        var liveX = ClipBands(px, v->Width, fx, tileX0, tx1, bxLo, bxHi, bxWeight);

        var byLo = stackalloc int[3];
        var byHi = stackalloc int[3];
        var byWeight = stackalloc int[3];
        var liveY = ClipBands(py, v->Height, fy, tileY0, ty1, byLo, byHi, byWeight);

        var constant = v->Constant;
        for (var y = 0; y < liveY; y++)
        {
            var rowTop = byLo[y] * DiffPitch;
            var rowBottom = byHi[y] * DiffPitch;
            var wy = byWeight[y];
            for (var x = 0; x < liveX; x++)
            {
                var value = RoundQ16(constant * bxWeight[x] * wy) * gain;
                if (value == 0) continue;

                var lx0 = bxLo[x];
                var lx1 = bxHi[x];
                difference[rowTop + lx0] += value;
                difference[rowTop + lx1] -= value;
                difference[rowBottom + lx0] -= value;
                difference[rowBottom + lx1] += value;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ClipBands(
        int origin, int length, int phase, int tileLo, int tileHi,
        int* bandLo, int* bandHi, int* bandWeight)
    {
        if (phase == 0)
        {
            bandLo[0] = Math.Max(origin, tileLo) - tileLo;
            bandHi[0] = Math.Min(origin + length, tileHi) - tileLo;
            bandWeight[0] = 256;
            return bandHi[0] > bandLo[0] ? 1 : 0;
        }

        var live = 0;
        var lo = Math.Max(origin, tileLo) - tileLo;
        var hi = Math.Min(origin + 1, tileHi) - tileLo;
        if (hi > lo)
        {
            bandLo[live] = lo;
            bandHi[live] = hi;
            bandWeight[live] = 256 - phase;
            live++;
        }

        lo = Math.Max(origin + 1, tileLo) - tileLo;
        hi = Math.Min(origin + (length == 1 ? 2 : length), tileHi) - tileLo;
        if (hi > lo)
        {
            bandLo[live] = lo;
            bandHi[live] = hi;
            bandWeight[live] = length == 1 ? phase : 256;
            live++;
        }

        if (length > 1)
        {
            lo = Math.Max(origin + length, tileLo) - tileLo;
            hi = Math.Min(origin + length + 1, tileHi) - tileLo;
            if (hi > lo)
            {
                bandLo[live] = lo;
                bandHi[live] = hi;
                bandWeight[live] = phase;
                live++;
            }
        }

        return live;
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
        var pitch = v->Pitch;

        var sx = gx0 - px;
        var sy = gy0 - py;
        var source = v->Data + sy * pitch + sx;
        var destination = dense + (gy0 - tileY0) * TileSize + (gx0 - tileX0);
        var width = gx1 - gx0;
        var height = gy1 - gy0;

        var vector = Vector;
        var vw00 = Vector128.Create(w00);
        var vw10 = Vector128.Create(w10);
        var vw01 = Vector128.Create(w01);
        var vw11 = Vector128.Create(w11);
        var vhalf = Vector128.Create(32768);
        var vgain = Vector128.Create(gain);
        for (var y = 0; y < height; y++)
        {
            var src = source + y * pitch;
            var upper = src - pitch;
            var dst = destination + y * TileSize;
            var x = 0;
            if (vector)
            {
                for (; x + 4 <= width; x += 4)
                {
                    var numerator =
                        Tap4(src + x) * vw00 +
                        Tap4(src + x - 1) * vw10 +
                        Tap4(upper + x) * vw01 +
                        Tap4(upper + x - 1) * vw11;
                    var rounded = Vector128.ShiftRightArithmetic(
                        numerator + Vector128.ShiftRightArithmetic(numerator, 31) + vhalf, 16);
                    Store128(dst + x, Load128(dst + x) + rounded * vgain);
                }
            }

            for (; x < width; x++)
            {
                var numerator =
                    src[x] * w00 +
                    src[x - 1] * w10 +
                    upper[x] * w01 +
                    upper[x - 1] * w11;
                dst[x] += RoundQ16(numerator) * gain;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Tap4(sbyte* p)
        => Vector128.Widen(Vector128.Widen(Vector128.CreateScalarUnsafe(*(int*)p).AsSByte()).Item1).Item1;

    internal static bool Resolve(int* difference, int* dense, int* previousRow, short* output)
    {
        var acc = Vector128<int>.Zero;
        var any = false;
        var vector = Vector;
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
