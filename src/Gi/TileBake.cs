using System.Runtime.CompilerServices;
#if NET
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
#endif

namespace Gi;

[Flags]
internal enum ResolveOutcome : byte
{
    None = 0,
    Empty = 1,
    Changed = 2,
}

internal static unsafe partial class TileBake
{
    internal const int TileBits = 5;
    internal const int TileSize = 32;
    internal const int DiffPitch = 33;
    internal const int DiffRows = 33;

#if NET
    private static bool Vector => Sse2.IsSupported || AdvSimd.IsSupported;
#endif

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int RoundQ16(int value)
        => (value + 32768 + (value >> 31)) >> 16;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int RoundQ40(long value)
        => (int)((value + 549755813888L + (value >> 63)) >> 40);

    private const long MaxExtent = 1L << 30;
    private const float MaxPositionQ8 = 1073741824f;
    private const long MaxTentExtent = (1L << 23) - 2;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int AnchorQ8(float value, float origin, int scaleQ8, int anchorMask)
    {
        var position = (value - origin) * scaleQ8;
        var bounded = MathF.Abs(position) <= MaxPositionQ8
            ? position
            : position > 0f ? MaxPositionQ8 : -MaxPositionQ8;
        return (int)MathF.Floor(bounded) & anchorMask;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void FootprintAnchored(
        int anchorX, int anchorY, int sampling, int size, StampVariant* v, bool clip,
        out int px, out int py, out int fx, out int fy,
        out int extentX, out int extentY,
        out int x0, out int y0, out int x1, out int y1)
    {
        var cap = v->Kind == StampKind.Tent ? MaxTentExtent : MaxExtent;
        SpanAnchored(anchorX, v->OriginQ8X, v->Width, sampling, size, clip, cap, out px, out fx, out extentX, out x0, out x1);
        SpanAnchored(anchorY, v->OriginQ8Y, v->Height, sampling, size, clip, cap, out py, out fy, out extentY, out y0, out y1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SpanAnchored(
        int anchor, int originQ8, int length, int sampling, int size, bool clip, long cap,
        out int cell, out int phase, out int extent, out int first, out int end)
    {
        var lead = anchor + ((long)originQ8 * sampling >> 8);
        var full = Math.Min((long)length * sampling, cap);
        if (clip)
        {
            var last = Math.Min(lead + full, ((long)size << 8) + 256);
            lead = Math.Max(lead, -256);
            full = Math.Max(last - lead, 0);
        }

        cell = (int)(lead >> 8);
        phase = (int)(lead & 255);
        extent = (int)full;
        first = cell;
        end = (int)Math.Min(cell + ((phase + full + 255) >> 8), size + 1L);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool Unclipped(StampVariant* v, Placement* p)
    {
        var cap = v->Kind == StampKind.Tent ? MaxTentExtent : MaxExtent;
        return p->ExtentX == (int)Math.Min((long)v->Width * p->Sampling, cap)
            && p->ExtentY == (int)Math.Min((long)v->Height * p->Sampling, cap);
    }

    #if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    #endif
    internal static void EmitBox(
        int* difference, int tileX0, int tileY0,
        int px, int py, int fx, int fy, int extentX, int extentY, StampVariant* v, int gain)
    {
        if (gain == 0) return;

        var tx1 = tileX0 + TileSize;
        var ty1 = tileY0 + TileSize;

        var bxLo = stackalloc int[3];
        var bxHi = stackalloc int[3];
        var bxWeight = stackalloc int[3];
        var liveX = ClipBands(px, fx, extentX, tileX0, tx1, bxLo, bxHi, bxWeight);

        var byLo = stackalloc int[3];
        var byHi = stackalloc int[3];
        var byWeight = stackalloc int[3];
        var liveY = ClipBands(py, fy, extentY, tileY0, ty1, byLo, byHi, byWeight);

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
        int origin, int phase, int extent, int tileLo, int tileHi,
        int* bandLo, int* bandHi, int* bandWeight)
    {
        var end = phase + extent;
        var full = end >> 8;
        var tail = end & 255;
        var live = 0;
        if (phase == 0)
            live = Band(origin, 0, full, 256, tileLo, tileHi, bandLo, bandHi, bandWeight, live);
        else
        {
            live = Band(origin, 0, 1, 256 - phase, tileLo, tileHi, bandLo, bandHi, bandWeight, live);
            live = Band(origin, 1, full, 256, tileLo, tileHi, bandLo, bandHi, bandWeight, live);
        }

        if (tail != 0)
            live = Band(origin, full, full + 1, tail, tileLo, tileHi, bandLo, bandHi, bandWeight, live);
        return live;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Band(
        int origin, int start, int end, int weight, int tileLo, int tileHi,
        int* bandLo, int* bandHi, int* bandWeight, int live)
    {
        if (end <= start) return live;

        var lo = Math.Max(origin + start, tileLo) - tileLo;
        var hi = Math.Min(origin + end, tileHi) - tileLo;
        if (hi <= lo) return live;

        bandLo[live] = lo;
        bandHi[live] = hi;
        bandWeight[live] = weight;
        return live + 1;
    }

    internal static void EmitTent(
        long* tent, int tileX0, int tileY0,
        int px, int py, int fx, int fy, int extentX, int extentY, StampVariant* v, int gain)
        => EmitSmooth(tent, StampKind.Tent, tileX0, tileY0, px, py, fx, fy, extentX, extentY, v, gain);

    internal static void EmitBell(
        long* bell, int tileX0, int tileY0,
        int px, int py, int fx, int fy, int extentX, int extentY, StampVariant* v, int gain)
        => EmitSmooth(bell, StampKind.Bell, tileX0, tileY0, px, py, fx, fy, extentX, extentY, v, gain);

    #if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    #endif
    internal static void EmitRaster(
        int* dense, int tileX0, int tileY0,
        int px, int py, int fx, int fy, int x1, int y1, int scaleQ8,
        StampVariant* v, int gain)
    {
        if (gain == 0) return;

        var tx1 = tileX0 + TileSize;
        var ty1 = tileY0 + TileSize;
        var gx0 = Math.Max(px, tileX0);
        var gy0 = Math.Max(py, tileY0);
        var gx1 = Math.Min(x1, tx1);
        var gy1 = Math.Min(y1, ty1);
        if (gx1 <= gx0 || gy1 <= gy0) return;

        if (scaleQ8 == 256)
        {
            EmitRasterNative(dense, tileX0, tileY0, px, py, fx, fy, gx0, gy0, gx1, gy1, v, gain);
            return;
        }

        var step = 256L * 65536 / scaleQ8;
        var level = 0;
        while (level < v->MipCount && (step >> (level + 1)) >= 65536) level++;
        var data = v->Data;
        var w = v->Width;
        var h = v->Height;
        if (level > 0)
        {
            var block = v->Mips;
            for (var i = 0; i < level; i++)
            {
                w = (w + 1) >> 1;
                h = (h + 1) >> 1;
                data = block + (w + 2) + 1;
                block += (w + 2) * (h + 2);
            }
        }

        var pitch = w + 2;
        var u0 = -(long)fx * 65536 / scaleQ8;
        var v0 = -(long)fy * 65536 / scaleQ8;
        for (var gy = gy0; gy < gy1; gy++)
        {
            var vq = (v0 + (gy - py) * step) >> level;
            var iy = (int)(vq >> 16);
            var fy2 = (int)((vq >> 8) & 255);
            var row0 = data + iy * pitch;
            var row1 = row0 + pitch;
            var dst = dense + (gy - tileY0) * TileSize + (gx0 - tileX0);
            for (var gx = gx0; gx < gx1; gx++)
            {
                var uq = (u0 + (gx - px) * step) >> level;
                var ix = (int)(uq >> 16);
                var fx2 = (int)((uq >> 8) & 255);
                var top = row0[ix] * (256 - fx2) + row0[ix + 1] * fx2;
                var bottom = row1[ix] * (256 - fx2) + row1[ix + 1] * fx2;
                dst[gx - gx0] += RoundQ16(top * (256 - fy2) + bottom * fy2) * gain;
            }
        }
    }

    private static void EmitRasterNative(
        int* dense, int tileX0, int tileY0,
        int px, int py, int fx, int fy, int gx0, int gy0, int gx1, int gy1,
        StampVariant* v, int gain)
    {
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

#if NET
        var vector = Vector;
        var vw00 = Vector128.Create(w00);
        var vw10 = Vector128.Create(w10);
        var vw01 = Vector128.Create(w01);
        var vw11 = Vector128.Create(w11);
        var vhalf = Vector128.Create(32768);
        var vgain = Vector128.Create(gain);
#endif
        for (var y = 0; y < height; y++)
        {
            var src = source + y * pitch;
            var upper = src - pitch;
            var dst = destination + y * TileSize;
            var x = 0;
#if NET
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
#endif

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

#if NET
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Tap4(sbyte* p)
        => Vector128.Widen(Vector128.Widen(Vector128.CreateScalarUnsafe(*(int*)p).AsSByte()).Item1).Item1;
#endif

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SmoothRow(long* sums, int y, int* output)
    {
        var row = sums + y * TileSize;
        for (var x = 0; x < TileSize; x++) output[x] = RoundQ40(row[x]);
    }

#if NET
    private const int ExactBound = 1 << 29;
    private const int ExactMask = int.MinValue | (1 << 30);
#endif

    internal static ResolveOutcome Outcome(bool any, bool changed)
        => (any ? ResolveOutcome.None : ResolveOutcome.Empty) | (changed ? ResolveOutcome.Changed : ResolveOutcome.None);

#if NET
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
    private static Vector128<short> LoadShorts(short* p)
    {
        if (Sse2.IsSupported) return Sse2.LoadVector128(p);
        return AdvSimd.LoadVector128(p);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreShorts(short* p, Vector128<short> v)
    {
        if (Sse2.IsSupported) Sse2.Store(p, v);
        else AdvSimd.Store(p, v);
    }

#endif
}
