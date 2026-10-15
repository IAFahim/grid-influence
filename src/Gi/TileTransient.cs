using System.Runtime.CompilerServices;
#if NET
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
#endif

namespace Gi;

internal struct WideSum
{
    internal ulong Low;
    internal long High;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Add(long value)
    {
        var before = Low;
        Low += (ulong)value;
        High += (value >> 63) + (Low < before ? 1 : 0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Add(WideSum value)
    {
        var before = Low;
        Low += value.Low;
        High += value.High + (Low < before ? 1 : 0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal readonly long RoundQ40()
        => (long)((Low >> 40) | ((ulong)High << 24)) +
            (((long)(Low & 1099511627775UL) + (High >> 63)) >= 549755813888L ? 1 : 0);
}

internal struct AxisImpulse
{
    internal int Offset;
    internal long Weight;
}

internal static unsafe partial class TileBake
{
    internal const int SmoothFastLimit = 4088;
    private const int SmoothDirectCells = 64;

    internal static byte EmitTransient(
        Placement* p, int gain, int tileX0, int tileY0,
        long* difference, long* dense, long* tent, long* bell, long* tentImpulses, long* bellImpulses)
    {
        if (gain == 0) return 0;
        switch (p->Plane)
        {
            case Plane.Difference:
                EmitBox(difference, tileX0, tileY0, p->Px, p->Py, p->Fx, p->Fy, p->ExtentX, p->ExtentY, p->V, gain);
                return 0;
            case Plane.Dense:
                EmitDense64(dense, tileX0, tileY0, p, gain);
                return 0;
            case Plane.Tent:
                return EmitAdaptiveSmooth(tent, tentImpulses, tileX0, tileY0, p, gain) ? (byte)1 : (byte)0;
            case Plane.Bell:
                return EmitAdaptiveSmooth(bell, bellImpulses, tileX0, tileY0, p, gain) ? (byte)2 : (byte)0;
        }
        return 0;
    }

    internal static byte EmitTransientWide(
        Placement* p, int gain, int tileX0, int tileY0,
        long* difference, long* dense, WideSum* tent, WideSum* bell, WideSum* tentImpulses, WideSum* bellImpulses)
    {
        if (gain == 0) return 0;
        switch (p->Plane)
        {
            case Plane.Difference:
                EmitBox(difference, tileX0, tileY0, p->Px, p->Py, p->Fx, p->Fy, p->ExtentX, p->ExtentY, p->V, gain);
                return 0;
            case Plane.Dense:
                EmitDense64(dense, tileX0, tileY0, p, gain);
                return 0;
            case Plane.Tent:
                return EmitAdaptiveSmoothWide(tent, tentImpulses, tileX0, tileY0, p, gain) ? (byte)1 : (byte)0;
            case Plane.Bell:
                return EmitAdaptiveSmoothWide(bell, bellImpulses, tileX0, tileY0, p, gain) ? (byte)2 : (byte)0;
        }
        return 0;
    }

    internal static void EmitBox(
        long* difference, int tileX0, int tileY0,
        int px, int py, int fx, int fy, int extentX, int extentY, StampVariant* v, int gain)
    {
        var bxLo = stackalloc int[3];
        var bxHi = stackalloc int[3];
        var bxWeight = stackalloc int[3];
        var byLo = stackalloc int[3];
        var byHi = stackalloc int[3];
        var byWeight = stackalloc int[3];
        var liveX = ClipBands(px, fx, extentX, tileX0, tileX0 + TileSize, bxLo, bxHi, bxWeight);
        var liveY = ClipBands(py, fy, extentY, tileY0, tileY0 + TileSize, byLo, byHi, byWeight);
        for (var y = 0; y < liveY; y++)
        for (var x = 0; x < liveX; x++)
        {
            var value = (long)RoundQ16(v->Constant * bxWeight[x] * byWeight[y]) * gain;
            difference[byLo[y] * DiffPitch + bxLo[x]] += value;
            difference[byLo[y] * DiffPitch + bxHi[x]] -= value;
            difference[byHi[y] * DiffPitch + bxLo[x]] -= value;
            difference[byHi[y] * DiffPitch + bxHi[x]] += value;
        }
    }

    private static void EmitDense64(long* dense, int tileX0, int tileY0, Placement* p, int gain)
    {
        var x0 = Math.Max(p->X0, tileX0);
        var x1 = Math.Min(p->X1, tileX0 + TileSize);
        var y0 = Math.Max(p->Y0, tileY0);
        var y1 = Math.Min(p->Y1, tileY0 + TileSize);
        if (p->Turned)
        {
            for (var y = y0; y < y1; y++)
            {
                var dy = (long)y * CellHalf + CellHalf / 2 - p->CentreY;
                var row = dense + (y - tileY0) * TileSize - tileX0;
                for (var x = x0; x < x1; x++)
                    row[x] += (long)DenseAt(p, (long)x * CellHalf + CellHalf / 2 - p->CentreX, dy) * gain;
            }
            return;
        }

        if (x1 <= x0 || y1 <= y0) return;
        var v = p->V;
        if (p->Sampling == 256)
        {
            var w00 = (256 - p->Fx) * (256 - p->Fy);
            var w10 = p->Fx * (256 - p->Fy);
            var w01 = (256 - p->Fx) * p->Fy;
            var w11 = p->Fx * p->Fy;
            for (var y = y0; y < y1; y++)
            {
                var source = v->Data + (y - p->Py) * v->Pitch + x0 - p->Px;
                var upper = source - v->Pitch;
                var row = dense + (y - tileY0) * TileSize + x0 - tileX0;
                for (var x = 0; x < x1 - x0; x++)
                {
                    var numerator = source[x] * w00 + source[x - 1] * w10 + upper[x] * w01 + upper[x - 1] * w11;
                    row[x] += (long)RoundQ16(numerator) * gain;
                }
            }
            return;
        }

        var step = 256L * 65536 / p->Sampling;
        var level = 0;
        while (level < v->MipCount && (step >> (level + 1)) >= 65536) level++;
        var data = v->Data;
        var width = v->Width;
        var height = v->Height;
        if (level > 0)
        {
            var block = v->Mips;
            for (var i = 0; i < level; i++)
            {
                width = (width + 1) >> 1;
                height = (height + 1) >> 1;
                data = block + width + 3;
                block += (width + 2) * (height + 2);
            }
        }
        var pitch = width + 2;
        var u0 = -(long)p->Fx * 65536 / p->Sampling;
        var v0 = -(long)p->Fy * 65536 / p->Sampling;
        for (var y = y0; y < y1; y++)
        {
            var vq = (v0 + (y - p->Py) * step) >> level;
            var iy = (int)(vq >> 16);
            if (iy < -1 || iy >= height) continue;
            var fy = (int)((vq >> 8) & 255);
            var row0 = data + iy * pitch;
            var row1 = row0 + pitch;
            var destination = dense + (y - tileY0) * TileSize - tileX0;
            for (var x = x0; x < x1; x++)
            {
                var uq = (u0 + (x - p->Px) * step) >> level;
                var ix = (int)(uq >> 16);
                if (ix < -1 || ix >= width) continue;
                var fx = (int)((uq >> 8) & 255);
                var top = row0[ix] * (256 - fx) + row0[ix + 1] * fx;
                var bottom = row1[ix] * (256 - fx) + row1[ix + 1] * fx;
                destination[x] += (long)RoundQ16(top * (256 - fy) + bottom * fy) * gain;
            }
        }
    }

    internal static int CollectImpulses(in SmoothAxis axis, int windowStart, AxisImpulse* impulses)
    {
        var order = axis.Bell ? 3 : 2;
        var candidates = ImpulseRun(windowStart, windowStart, order) |
            ImpulseRun(windowStart, axis.First, order) |
            ImpulseRun(windowStart, axis.Last + 1, order);
        if (!axis.Bell)
            candidates |= ImpulseRun(windowStart, (int)-(axis.Base >> (BitLength(axis.Step) - 1)), order);
        var count = 0;
        while (candidates != 0)
        {
#if NET
            var offset = System.Numerics.BitOperations.TrailingZeroCount(candidates);
#else
            var offset = 0;
            var remaining = candidates;
            while ((remaining & 1) == 0) { remaining >>= 1; offset++; }
#endif
            candidates &= candidates - 1;
            var cell = windowStart + offset;
            var weight = Profile(axis, windowStart, cell) - order * Profile(axis, windowStart, cell - 1);
            weight += order == 2
                ? Profile(axis, windowStart, cell - 2)
                : 3 * Profile(axis, windowStart, cell - 2) - Profile(axis, windowStart, cell - 3);
            if (weight == 0) continue;
            impulses[count].Offset = offset;
            impulses[count++].Weight = weight;
        }
        return count;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long Profile(in SmoothAxis axis, int start, int cell) => cell < start || cell < axis.First || cell > axis.Last ? 0 : axis.Weight(cell);

    private static uint ImpulseRun(int start, int breakpoint, int order)
    {
        var mask = 0u;
        for (var i = 0; i < order; i++)
        {
            var offset = (long)breakpoint + i - start;
            if ((ulong)offset < TileSize) mask |= 1u << (int)offset;
        }
        return mask;
    }

    internal static void EmitSmoothImpulses(long* impulses, StampKind kind, int tileX0, int tileY0,
        int px, int py, int fx, int fy, int extentX, int extentY, StampVariant* v, int gain)
    {
        var ax = Axis(kind, px, fx, extentX);
        var ay = Axis(kind, py, fy, extentY);
        EmitSmoothImpulses(impulses, tileX0, tileY0, ax, ay, (long)v->Constant * gain * Normalizer(ax, ay));
    }

    private static void EmitSmoothImpulses(long* impulses, int tileX0, int tileY0,
        in SmoothAxis ax, in SmoothAxis ay, long scale)
    {
        var xs = stackalloc AxisImpulse[6];
        var ys = stackalloc AxisImpulse[6];
        var nx = CollectImpulses(ax, tileX0, xs);
        var ny = CollectImpulses(ay, tileY0, ys);
        for (var y = 0; y < ny; y++)
        {
            var row = impulses + ys[y].Offset * TileSize;
            var wy = scale * ys[y].Weight;
            for (var x = 0; x < nx; x++) row[xs[x].Offset] += wy * xs[x].Weight;
        }
    }

    private static bool EmitAdaptiveSmooth(long* sums, long* impulses, int tileX0, int tileY0, Placement* p, int gain)
    {
        if (p->Turned)
        {
            EmitTurnedSmooth(sums, tileX0, tileY0, p, gain);
            return false;
        }
        var ax = Axis(p->Kind, p->Px, p->Fx, p->ExtentX);
        var ay = Axis(p->Kind, p->Py, p->Fy, p->ExtentY);
        var x0 = Math.Max(ax.First, tileX0);
        var x1 = Math.Min(ax.Last + 1, tileX0 + TileSize);
        var y0 = Math.Max(ay.First, tileY0);
        var y1 = Math.Min(ay.Last + 1, tileY0 + TileSize);
        if (x1 <= x0 || y1 <= y0) return false;
        var scale = (long)p->V->Constant * gain * p->Curve;
        if (impulses != null && (x1 - x0) * (y1 - y0) > SmoothDirectCells)
        {
            EmitSmoothImpulses(impulses, tileX0, tileY0, ax, ay, scale);
            return true;
        }
        EmitSmoothDirect(sums, tileX0, tileY0, ax, ay, x0, x1, y0, y1, scale);
        return false;
    }

    private static void EmitSmoothDirect(long* sums, int tileX0, int tileY0, in SmoothAxis ax, in SmoothAxis ay,
        int x0, int x1, int y0, int y1, long scale)
    {
        var wx = stackalloc long[TileSize];
        for (var x = x0; x < x1; x++) wx[x - x0] = ax.Weight(x);
        for (var y = y0; y < y1; y++)
        {
            var row = sums + (y - tileY0) * TileSize - tileX0;
            var wy = scale * ay.Weight(y);
            for (var x = x0; x < x1; x++) row[x] += wy * wx[x - x0];
        }
    }

    private static bool EmitAdaptiveSmoothWide(WideSum* sums, WideSum* impulses, int tileX0, int tileY0, Placement* p, int gain)
    {
        var x0 = Math.Max(p->X0, tileX0);
        var x1 = Math.Min(p->X1, tileX0 + TileSize);
        var y0 = Math.Max(p->Y0, tileY0);
        var y1 = Math.Min(p->Y1, tileY0 + TileSize);
        if (x1 <= x0 || y1 <= y0) return false;
        if (p->Turned)
        {
            for (var y = y0; y < y1; y++)
            {
                var dy = (long)y * CellHalf + CellHalf / 2 - p->CentreY;
                var row = sums + (y - tileY0) * TileSize - tileX0;
                for (var x = x0; x < x1; x++)
                    row[x].Add(SmoothAt(p, (long)x * CellHalf + CellHalf / 2 - p->CentreX, dy) * gain);
            }
            return false;
        }
        var ax = Axis(p->Kind, p->Px, p->Fx, p->ExtentX);
        var ay = Axis(p->Kind, p->Py, p->Fy, p->ExtentY);
        var scale = (long)p->V->Constant * gain * p->Curve;
        if (impulses != null && (x1 - x0) * (y1 - y0) > SmoothDirectCells)
        {
            var xs = stackalloc AxisImpulse[6];
            var ys = stackalloc AxisImpulse[6];
            var nx = CollectImpulses(ax, tileX0, xs);
            var ny = CollectImpulses(ay, tileY0, ys);
            for (var y = 0; y < ny; y++)
            {
                var row = impulses + ys[y].Offset * TileSize;
                var wy = scale * ys[y].Weight;
                for (var x = 0; x < nx; x++) row[xs[x].Offset].Add(wy * xs[x].Weight);
            }
            return true;
        }
        var wx = stackalloc long[TileSize];
        for (var x = x0; x < x1; x++) wx[x - x0] = ax.Weight(x);
        for (var y = y0; y < y1; y++)
        {
            var row = sums + (y - tileY0) * TileSize - tileX0;
            var wy = scale * ay.Weight(y);
            for (var x = x0; x < x1; x++) row[x].Add(wy * wx[x - x0]);
        }
        return false;
    }

    internal static void IntegrateSmooth(long* impulses, long* sums, StampKind kind)
    {
        var order = kind == StampKind.Bell ? 3 : 2;
        for (var pass = 0; pass < order; pass++)
        {
            for (var y = 1; y < TileSize; y++) AddSmoothRow(impulses + y * TileSize, impulses + (y - 1) * TileSize);
            for (var y = 0; y < TileSize; y++) PrefixSmoothRow(impulses + y * TileSize);
        }
        for (var y = 0; y < TileSize; y++) AddSmoothRow(sums + y * TileSize, impulses + y * TileSize);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void PrefixSmoothRow(long* row)
    {
        var x = 0;
#if NET
        if (Avx2.IsSupported)
        {
            var carry = Vector256<long>.Zero;
            for (; x < TileSize; x += 4)
            {
                var value = PrefixLong256(Avx.LoadVector256(row + x)) + carry;
                Avx.Store(row + x, value);
                carry = Avx2.Permute4x64(value, 0xff);
            }
            return;
        }
        if (Sse2.IsSupported || AdvSimd.IsSupported)
        {
            var carry = Vector128<long>.Zero;
            for (; x < TileSize; x += 2)
            {
                if (Sse2.IsSupported)
                {
                    var value = Sse2.LoadVector128(row + x);
                    value += Sse2.ShiftLeftLogical128BitLane(value.AsByte(), 8).AsInt64();
                    value += carry;
                    Sse2.Store(row + x, value);
                    carry = Sse2.UnpackHigh(value, value);
                }
                else
                {
                    var value = AdvSimd.LoadVector128(row + x);
                    value += AdvSimd.ExtractVector128(Vector128<byte>.Zero, value.AsByte(), 8).AsInt64();
                    value += carry;
                    AdvSimd.Store(row + x, value);
                    carry = Vector128.Create(value.GetElement(1));
                }
            }
            return;
        }
#endif
        var scalar = 0L;
        for (; x < TileSize; x++) { scalar += row[x]; row[x] = scalar; }
    }

#if NET
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<long> PrefixLong256(Vector256<long> value)
    {
        value += Avx2.ShiftLeftLogical128BitLane(value.AsByte(), 8).AsInt64();
        return value + Avx2.Blend(Vector256<int>.Zero, Avx2.Permute4x64(value, 0x55).AsInt32(), 0xf0).AsInt64();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<long> RoundSmooth256(Vector256<long> value)
    {
        var fraction = (value & Vector256.Create(1099511627775L)) + Vector256.ShiftRightArithmetic(value, 63);
        return Vector256.ShiftRightArithmetic(value, 40) - Vector256.GreaterThan(fraction, Vector256.Create(549755813887L));
    }
#endif

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddSmoothRow(long* destination, long* source)
    {
        var x = 0;
#if NET
        if (Avx2.IsSupported)
        {
            for (; x < TileSize; x += 4) Avx.Store(destination + x, Avx.LoadVector256(destination + x) + Avx.LoadVector256(source + x));
        }
        else if (Sse2.IsSupported || AdvSimd.IsSupported)
        {
            for (; x < TileSize; x += 2)
            {
                if (Sse2.IsSupported) Sse2.Store(destination + x, Sse2.LoadVector128(destination + x) + Sse2.LoadVector128(source + x));
                else AdvSimd.Store(destination + x, AdvSimd.LoadVector128(destination + x) + AdvSimd.LoadVector128(source + x));
            }
        }
#endif
        for (; x < TileSize; x++) destination[x] += source[x];
    }

    internal static void IntegrateSmooth(WideSum* impulses, WideSum* sums, StampKind kind)
    {
        var order = kind == StampKind.Bell ? 3 : 2;
        for (var pass = 0; pass < order; pass++)
        {
            for (var y = 1; y < TileSize; y++)
            {
                var row = impulses + y * TileSize;
                var above = row - TileSize;
                for (var x = 0; x < TileSize; x++) row[x].Add(above[x]);
            }
            for (var y = 0; y < TileSize; y++)
            {
                var row = impulses + y * TileSize;
                var carry = default(WideSum);
                for (var x = 0; x < TileSize; x++) { carry.Add(row[x]); row[x] = carry; }
            }
        }
        for (var i = 0; i < TileSize * TileSize; i++) sums[i].Add(impulses[i]);
    }

    internal static bool ResolveTransient(long* difference, long* dense, long* tent, long* bell,
        long* tentImpulses, long* bellImpulses, short* output, long* pageSum, short* pageMax, int* rowSums = null, short* rowMax = null, short* pageMin = null)
    {
        if (tentImpulses != null) IntegrateSmooth(tentImpulses, tent, StampKind.Tent);
        if (bellImpulses != null) IntegrateSmooth(bellImpulses, bell, StampKind.Bell);
#if NET
        if (Avx2.IsSupported) return ResolveTransient256(difference, dense, tent, bell, output, pageSum, pageMax, rowSums, rowMax, pageMin);
#endif
        var previous = stackalloc long[TileSize];
        new Span<long>(previous, TileSize).Clear();
        var sum = 0L;
        var maximum = short.MinValue;
        var minimum = short.MaxValue;
        var any = false;
        for (var y = 0; y < TileSize; y++)
        {
            var carry = 0L;
            var rowSum = 0;
            var rowPeak = short.MinValue;
            for (var x = 0; x < TileSize; x++)
            {
                var i = y * TileSize + x;
                if (difference != null) carry += difference[y * DiffPitch + x];
                previous[x] += carry;
                var total = previous[x] + (dense == null ? 0 : dense[i]) +
                    (tent == null ? 0 : RoundQ40(tent[i])) + (bell == null ? 0 : RoundQ40(bell[i]));
                var cell = (short)Math.Clamp(total, short.MinValue, short.MaxValue);
                output[i] = cell;
                sum += cell;
                rowSum += cell;
                if (cell > rowPeak) rowPeak = cell;
                if (cell < minimum) minimum = cell;
                any |= total != 0;
            }
            if (rowSums != null) rowSums[y] = rowSum;
            if (rowMax != null) rowMax[y] = rowPeak;
            if (rowPeak > maximum) maximum = rowPeak;
        }
        if (pageMin != null) *pageMin = minimum;
        *pageSum = sum;
        *pageMax = maximum;
        return any;
    }

#if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool ResolveTransient256(long* difference, long* dense, long* tent, long* bell,
        short* output, long* pageSum, short* pageMax, int* rowSums, short* rowMax, short* pageMin)
    {
        var previous = stackalloc long[TileSize];
        new Span<long>(previous, TileSize).Clear();
        var cellOrder = Vector256.Create(0, 2, 4, 6, 0, 2, 4, 6);
        var low = Vector256.Create((long)short.MinValue);
        var high = Vector256.Create((long)short.MaxValue);
        var sum = Vector128<int>.Zero;
        var maximum = Vector128.Create((int)short.MinValue);
        var minimum = Vector128.Create((int)short.MaxValue);
        for (var y = 0; y < TileSize; y++)
        {
            var carry = Vector256<long>.Zero;
            var rowSum = Vector128<int>.Zero;
            var rowPeak = Vector128.Create((int)short.MinValue);
            for (var x = 0; x < TileSize; x += 4)
            {
                var i = y * TileSize + x;
                var total = Vector256<long>.Zero;
                if (difference != null)
                {
                    var prefix = PrefixLong256(Avx.LoadVector256(difference + y * DiffPitch + x)) + carry;
                    carry = Avx2.Permute4x64(prefix, 0xff);
                    total = prefix + Avx.LoadVector256(previous + x);
                    Avx.Store(previous + x, total);
                }
                if (dense != null) total += Avx.LoadVector256(dense + i);
                if (tent != null) total += RoundSmooth256(Avx.LoadVector256(tent + i));
                if (bell != null) total += RoundSmooth256(Avx.LoadVector256(bell + i));
                total = Vector256.Min(Vector256.Max(total, low), high);
                var cells = Avx2.PermuteVar8x32(total.AsInt32(), cellOrder).GetLower();
                Pack4(output + i, cells);
                rowSum += cells;
                rowPeak = Vector128.Max(rowPeak, cells);
                minimum = Vector128.Min(minimum, cells);
            }
            sum += rowSum;
            maximum = Vector128.Max(maximum, rowPeak);
            if (rowSums != null) rowSums[y] = rowSum[0] + rowSum[1] + rowSum[2] + rowSum[3];
            if (rowMax != null) rowMax[y] = (short)Math.Max(Math.Max(rowPeak[0], rowPeak[1]), Math.Max(rowPeak[2], rowPeak[3]));
        }
        *pageSum = (long)sum[0] + sum[1] + sum[2] + sum[3];
        var max = (short)Math.Max(Math.Max(maximum[0], maximum[1]), Math.Max(maximum[2], maximum[3]));
        var min = (short)Math.Min(Math.Min(minimum[0], minimum[1]), Math.Min(minimum[2], minimum[3]));
        *pageMax = max;
        if (pageMin != null) *pageMin = min;
        return max != 0 || min != 0;
    }
#endif

    internal static bool ResolveTransientWide(long* difference, long* dense, WideSum* tent, WideSum* bell,
        WideSum* tentImpulses, WideSum* bellImpulses, short* output, long* pageSum, short* pageMax, int* rowSums = null, short* rowMax = null, short* pageMin = null)
    {
        if (tentImpulses != null) IntegrateSmooth(tentImpulses, tent, StampKind.Tent);
        if (bellImpulses != null) IntegrateSmooth(bellImpulses, bell, StampKind.Bell);
        var previous = stackalloc long[TileSize];
        new Span<long>(previous, TileSize).Clear();
        var sum = 0L;
        var maximum = short.MinValue;
        var minimum = short.MaxValue;
        var any = false;
        for (var y = 0; y < TileSize; y++)
        {
            var carry = 0L;
            var rowSum = 0;
            var rowPeak = short.MinValue;
            for (var x = 0; x < TileSize; x++)
            {
                var i = y * TileSize + x;
                if (difference != null) carry += difference[y * DiffPitch + x];
                previous[x] += carry;
                var total = previous[x] + (dense == null ? 0 : dense[i]) +
                    (tent == null ? 0 : tent[i].RoundQ40()) + (bell == null ? 0 : bell[i].RoundQ40());
                var cell = (short)Math.Clamp(total, short.MinValue, short.MaxValue);
                output[i] = cell;
                sum += cell;
                rowSum += cell;
                if (cell > rowPeak) rowPeak = cell;
                if (cell < minimum) minimum = cell;
                any |= total != 0;
            }
            if (rowSums != null) rowSums[y] = rowSum;
            if (rowMax != null) rowMax[y] = rowPeak;
            if (rowPeak > maximum) maximum = rowPeak;
        }
        if (pageMin != null) *pageMin = minimum;
        *pageSum = sum;
        *pageMax = maximum;
        return any;
    }
}
