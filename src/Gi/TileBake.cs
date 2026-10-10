using System.Runtime.CompilerServices;
#if NET
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
#endif

namespace Gi;

internal static unsafe class TileBake
{
    internal const int TileBits = 5;
    internal const int TileSize = 32;
    internal const int DiffPitch = 33;
    internal const int DiffRows = 33;

#if NET
    private static bool Vector => Sse2.IsSupported || AdvSimd.IsSupported;
#endif

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int RoundQ16(int value)
        => (value + 32768 + (value >> 31)) >> 16;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int RoundQ24(long value)
        => (int)((value + 8388608 + (value >> 63)) >> 24);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Footprint(
        float wx, float wy, float originX, float originY, int scaleQ8, int sizeQ8,
        StampVariant* v,
        out int px, out int py, out int fx, out int fy,
        out int extentX, out int extentY,
        out int x0, out int y0, out int x1, out int y1)
    {
        var leadX = (long)(int)MathF.Floor((wx - originX) * scaleQ8) + ((long)v->OriginQ8X * scaleQ8 >> 8);
        var leadY = (long)(int)MathF.Floor((wy - originY) * scaleQ8) + ((long)v->OriginQ8Y * scaleQ8 >> 8);
        px = (int)(leadX >> 8);
        py = (int)(leadY >> 8);
        fx = (int)(leadX & 255);
        fy = (int)(leadY & 255);
        extentX = (int)Math.Min((long)v->Width * scaleQ8, sizeQ8);
        extentY = (int)Math.Min((long)v->Height * scaleQ8, sizeQ8);
        x0 = px;
        y0 = py;
        x1 = px + (int)((fx + extentX + 255) >> 8);
        y1 = py + (int)((fy + extentY + 255) >> 8);
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

    internal const int MaxTentImpulses = 6;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int TentWeight(int cell, int first, int peakCell, int last, int up, int down)
    {
        if (cell < first || cell > last) return 0;
        if (cell <= peakCell) return up * (cell - first + 1);
        return up * (peakCell - first + 1) - down * (cell - peakCell);
    }

    private static void TentGeometry(
        int origin, int phase, int extent,
        out int first, out int peakCell, out int last, out int up, out int down, out int tail)
    {
        var half = Math.Max(1, extent >> 1);
        var peak = (origin << 8) + phase + (extent >> 1);
        first = ((peak - half) >> 8) + 1;
        last = (peak + half - 1) >> 8;
        peakCell = peak >> 8;
        if (last < first)
        {
            first = peakCell;
            last = peakCell;
        }

        if (peakCell < first) peakCell = first;
        if (peakCell > last) peakCell = last;
        var rise = peakCell - first + 1;
        var fall = last + 1 - peakCell;
        up = 65536 / rise;
        down = up * rise / fall;
        tail = up * rise - down * (fall - 1);
    }

    private static int TentSlope(int cell, int first, int peakCell, int last, int up, int down, int tail)
    {
        if (cell < first || cell > last + 1) return 0;
        if (cell <= peakCell) return up;
        return cell <= last ? -down : -tail;
    }

    private static int TentImpulses(
        int origin, int phase, int extent, int tileLo,
        int* cells, int* deltas)
    {
        TentGeometry(origin, phase, extent, out var first, out var peakCell, out var last, out var up, out var down, out var tail);
        var tileHi = tileLo + TileSize;
        var anchor = TentWeight(tileLo, first, peakCell, last, up, down);
        var count = 0;
        cells[count] = 0;
        deltas[count] = anchor;
        count++;
        var entry = TentSlope(tileLo + 1, first, peakCell, last, up, down, tail) - anchor;
        if (entry != 0)
        {
            cells[count] = 1;
            deltas[count] = entry;
            count++;
        }

        for (var c = tileLo + 2; c <= Math.Min(last + 2, tileHi - 1); c++)
        {
            var change = TentSlope(c, first, peakCell, last, up, down, tail) -
                TentSlope(c - 1, first, peakCell, last, up, down, tail);
            if (change == 0) continue;

            cells[count] = c - tileLo;
            deltas[count] = change;
            count++;
        }

        return count;
    }

    internal static void EmitTent(
        long* tent, int tileX0, int tileY0,
        int px, int py, int fx, int fy, int extentX, int extentY, StampVariant* v, int gain)
    {
        if (gain == 0) return;

        var xCells = stackalloc int[MaxTentImpulses];
        var xDeltas = stackalloc int[MaxTentImpulses];
        var yCells = stackalloc int[MaxTentImpulses];
        var yDeltas = stackalloc int[MaxTentImpulses];
        var liveX = TentImpulses(px, fx, extentX, tileX0, xCells, xDeltas);
        var liveY = TentImpulses(py, fy, extentY, tileY0, yCells, yDeltas);

        var scale = (long)v->Constant * gain;
        for (var y = 0; y < liveY; y++)
        {
            var row = yCells[y] * DiffPitch;
            var dy = yDeltas[y];
            for (var x = 0; x < liveX; x++)
            {
                var value = scale * xDeltas[x] * dy;
                if (value == 0) continue;

                tent[row + xCells[x]] += value;
            }
        }
    }

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

    private static void TentRow(long* tent, int y, long* tp, long* tq, int* tentOut)
    {
        var row = tent + y * DiffPitch;
        var run = 0L;
        var run2 = 0L;
        for (var x = 0; x < TileSize; x++)
        {
            run += row[x];
            tp[x] += run;
            run2 += tp[x];
            tq[x] += run2;
            tentOut[x] = RoundQ24(tq[x]);
        }
    }

    #if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    #endif
    internal static bool Resolve(int* difference, int* dense, int* previousRow, long* tent, short* output, long* pageSum, short* pageMax)
    {
        var tentOut = stackalloc int[TileSize];
        var tp = stackalloc long[TileSize];
        var tq = stackalloc long[TileSize];
        if (tent == null)
        {
            new Span<int>(tentOut, TileSize).Clear();
        }
        else
        {
            new Span<long>(tp, TileSize).Clear();
            new Span<long>(tq, TileSize).Clear();
        }

#if NET
        if (Avx2.IsSupported) return Resolve256(difference, dense, previousRow, tent, tp, tq, tentOut, output, pageSum, pageMax);

        var acc = Vector128<int>.Zero;
        var sum = Vector128<int>.Zero;
        var maximum = Vector128.Create(int.MinValue);
        var vector = Vector;
#endif
        var any = false;
        var cellSum = 0L;
        var cellMax = short.MinValue;
        for (var y = 0; y < TileSize; y++)
        {
            var diffRow = difference + y * DiffPitch;
            var denseRow = dense + y * TileSize;
            var outRow = output + y * TileSize;
            if (tent != null) TentRow(tent, y, tp, tq, tentOut);
            var carry = 0;
            var x = 0;
#if NET
            if (vector)
            {
                for (; x + 4 <= TileSize; x += 4)
                {
                    var d = Load128(diffRow + x);
                    var h = Prefix128(d) + Vector128.Create(carry);
                    carry = h[3];
                    var boxes = h + Load128(previousRow + x);
                    Store128(previousRow + x, boxes);
                    var total = boxes + Load128(denseRow + x) + Load128(tentOut + x);
                    acc |= total;
                    Pack4(outRow + x, total);
                    var widened4 = Widened4(total);
                    sum += widened4;
                    maximum = Vector128.Max(maximum, widened4);
                }
            }
            else
#endif
            {
                for (; x < TileSize; x++)
                {
                    carry += diffRow[x];
                    var boxes = carry + previousRow[x];
                    previousRow[x] = boxes;
                    var total = boxes + denseRow[x] + tentOut[x];
                    any |= total != 0;
                    var cell = (short)Math.Clamp(total, short.MinValue, short.MaxValue);
                    outRow[x] = cell;
                    if (cell > cellMax) cellMax = cell;
                    cellSum += cell;
                }
            }
        }

#if NET
        if (vector)
        {
            *pageSum = sum[0] + sum[1] + sum[2] + sum[3];
            *pageMax = (short)Math.Max(Math.Max(maximum[0], maximum[1]), Math.Max(maximum[2], maximum[3]));
            return !Vector128.EqualsAll(acc, Vector128<int>.Zero);
        }
#endif

        *pageSum = cellSum;
        *pageMax = cellMax;
        return any;
    }

#if NET
    #if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    #endif
    private static bool Resolve256(int* difference, int* dense, int* previousRow, long* tent, long* tp, long* tq, int* tentOut, short* output, long* pageSum, short* pageMax)
    {
        var acc = Vector256<int>.Zero;
        var fourth = Vector256.Create(3);
        var last = Vector256.Create(7);
        var sumLo = Vector128<int>.Zero;
        var sumHi = Vector128<int>.Zero;
        var maximum = Vector128.Create(short.MinValue);
        for (var y = 0; y < TileSize; y++)
        {
            var diffRow = difference + y * DiffPitch;
            var denseRow = dense + y * TileSize;
            var outRow = output + y * TileSize;
            if (tent != null) TentRow(tent, y, tp, tq, tentOut);
            var carry = Vector256<int>.Zero;
            for (var x = 0; x < TileSize; x += 8)
            {
                var h = Avx.LoadVector256(diffRow + x);
                h += Avx2.ShiftLeftLogical128BitLane(h.AsByte(), 4).AsInt32();
                h += Avx2.ShiftLeftLogical128BitLane(h.AsByte(), 8).AsInt32();
                h += Avx2.Blend(Vector256<int>.Zero, Avx2.PermuteVar8x32(h, fourth), 0xf0);
                h += carry;
                carry = Avx2.PermuteVar8x32(h, last);
                var boxes = h + Avx.LoadVector256(previousRow + x);
                Avx.Store(previousRow + x, boxes);
                var total = boxes + Avx.LoadVector256(denseRow + x) + Avx.LoadVector256(tentOut + x);
                acc |= total;
                var packed = Avx2.PackSignedSaturate(total, total);
                var cells = Avx2.Permute4x64(packed.AsInt64(), 0xd8).GetLower().AsInt16();
                Sse2.Store(outRow + x, cells);
                maximum = Sse2.Max(maximum, cells);
                var widened = Vector128.Widen(cells);
                sumLo += widened.Item1;
                sumHi += widened.Item2;
            }
        }

        *pageSum = (long)sumLo[0] + sumLo[1] + sumLo[2] + sumLo[3] +
            sumHi[0] + sumHi[1] + sumHi[2] + sumHi[3];
        *pageMax = HorizontalMax16(maximum);
        return !Vector256.EqualsAll(acc, Vector256<int>.Zero);
    }

    private static short HorizontalMax16(Vector128<short> v)
    {
        var best = v[0];
        for (var i = 1; i < 8; i++)
            if (v[i] > best) best = v[i];
        return best;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Widened4(Vector128<int> v)
    {
        if (Sse2.IsSupported) return Vector128.Widen(Sse2.PackSignedSaturate(v, v)).Item1;
        return Vector128.Widen(AdvSimd.ExtractNarrowingSaturateLower(v).ToVector128()).Item1;
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
#endif
}
