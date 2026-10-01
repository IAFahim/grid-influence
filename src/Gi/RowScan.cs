using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Gi;

internal static unsafe class RowScan
{
    public static void Prefix(short* row, int count)
    {
        if (Avx2.IsSupported)
        {
            PrefixAvx2(row, count);
            return;
        }
        if (Sse2.IsSupported)
        {
            PrefixSse2(row, count);
            return;
        }
        PrefixScalar(row, count, 0);
    }

    public static void PrefixPair(short* first, short* second, int count)
    {
        if (Avx2.IsSupported)
        {
            PrefixPairAvx2(first, second, count);
            return;
        }
        Prefix(first, count);
        Prefix(second, count);
    }

    private static void PrefixAvx2(short* row, int count)
    {
        var bound = Vector256.Create(32767);
        var carry = 0;
        var x = 0;
        for (; x + Vector256<short>.Count <= count; x += Vector256<short>.Count)
        {
            var block = Vector256.Load(row + x);
            if (carry == 0 && AllZeroAvx2(block)) continue;

            (var lo, var hi) = Vector256.Widen(block);
            lo = ScanAvx2(lo);
            hi = ScanAvx2(hi);
            hi = Avx2.Add(hi, Vector256.Create(lo.GetElement(7)));
            var offset = Vector256.Create(carry);
            lo = Avx2.Add(lo, offset);
            hi = Avx2.Add(hi, offset);
            if (OutOfRangeAvx2(lo, bound) || OutOfRangeAvx2(hi, bound))
            {
                PrefixScalar(row + x, count - x, carry);
                return;
            }

            Vector256.Narrow(lo, hi).Store(row + x);
            carry = hi.GetElement(7);
        }

        PrefixScalar(row + x, count - x, carry);
    }

    private static void PrefixPairAvx2(short* first, short* second, int count)
    {
        var bound = Vector256.Create(32767);
        var lanes = Vector256<short>.Count;
        var carryA = 0;
        var carryB = 0;
        var x = 0;
        for (; x + lanes <= count; x += lanes)
        {
            var blockA = Vector256.Load(first + x);
            var blockB = Vector256.Load(second + x);
            if (carryA == 0 && carryB == 0 && AllZeroAvx2(blockA) && AllZeroAvx2(blockB)) continue;

            (var loA, var hiA) = Vector256.Widen(blockA);
            (var loB, var hiB) = Vector256.Widen(blockB);
            loA = ScanAvx2(loA);
            hiA = ScanAvx2(hiA);
            loB = ScanAvx2(loB);
            hiB = ScanAvx2(hiB);
            hiA = Avx2.Add(hiA, Vector256.Create(loA.GetElement(7)));
            hiB = Avx2.Add(hiB, Vector256.Create(loB.GetElement(7)));
            var offsetA = Vector256.Create(carryA);
            var offsetB = Vector256.Create(carryB);
            loA = Avx2.Add(loA, offsetA);
            hiA = Avx2.Add(hiA, offsetA);
            loB = Avx2.Add(loB, offsetB);
            hiB = Avx2.Add(hiB, offsetB);
            if (OutOfRangeAvx2(loA, bound) || OutOfRangeAvx2(hiA, bound))
            {
                PrefixScalar(first + x, count - x, carryA);
                PrefixScalar(second + x, count - x, carryB);
                return;
            }

            if (OutOfRangeAvx2(loB, bound) || OutOfRangeAvx2(hiB, bound))
            {
                Vector256.Narrow(loA, hiA).Store(first + x);
                PrefixScalar(first + x + lanes, count - x - lanes, hiA.GetElement(7));
                PrefixScalar(second + x, count - x, carryB);
                return;
            }

            Vector256.Narrow(loA, hiA).Store(first + x);
            Vector256.Narrow(loB, hiB).Store(second + x);
            carryA = hiA.GetElement(7);
            carryB = hiB.GetElement(7);
        }

        PrefixScalar(first + x, count - x, carryA);
        PrefixScalar(second + x, count - x, carryB);
    }

    private static bool AllZeroAvx2(Vector256<short> block)
        => Avx2.MoveMask(Avx2.CompareEqual(block, Vector256<short>.Zero).AsByte()) == -1;

    private static bool AllZeroSse2(Vector128<short> block)
        => Sse2.MoveMask(Sse2.CompareEqual(block, Vector128<short>.Zero).AsByte()) == 0xFFFF;

    private static Vector256<int> ScanAvx2(Vector256<int> value)
    {
        var step = Avx2.Add(value, Avx2.ShiftLeftLogical128BitLane(value, 4));
        step = Avx2.Add(step, Avx2.ShiftLeftLogical128BitLane(step, 8));
        var low = Vector256.Create(step.GetElement(3));
        return Avx2.Add(step, Avx2.Permute2x128(Vector256<int>.Zero, low, 0x20));
    }

    private static bool OutOfRangeAvx2(Vector256<int> value, Vector256<int> bound)
        => Avx2.MoveMask(Avx2.CompareGreaterThan(Vector256.Abs(value), bound).AsSingle()) != 0;

    private static void PrefixSse2(short* row, int count)
    {
        var bound = Vector128.Create(32767);
        var carry = 0;
        var x = 0;
        for (; x + Vector128<short>.Count <= count; x += Vector128<short>.Count)
        {
            var block = Vector128.Load(row + x);
            if (carry == 0 && AllZeroSse2(block)) continue;

            (var lo, var hi) = Vector128.Widen(block);
            lo = ScanSse2(lo);
            hi = ScanSse2(hi);
            hi = Sse2.Add(hi, Sse2.Shuffle(lo, 0xFF));
            var offset = Vector128.Create(carry);
            lo = Sse2.Add(lo, offset);
            hi = Sse2.Add(hi, offset);
            if (OutOfRangeSse2(lo, bound) || OutOfRangeSse2(hi, bound))
            {
                PrefixScalar(row + x, count - x, carry);
                return;
            }

            Vector128.Narrow(lo, hi).Store(row + x);
            carry = hi.GetElement(3);
        }

        PrefixScalar(row + x, count - x, carry);
    }

    private static Vector128<int> ScanSse2(Vector128<int> value)
    {
        var step = Sse2.Add(value, Sse2.ShiftLeftLogical128BitLane(value, 4));
        return Sse2.Add(step, Sse2.ShiftLeftLogical128BitLane(step, 8));
    }

    private static bool OutOfRangeSse2(Vector128<int> value, Vector128<int> bound)
        => Sse2.MoveMask(Sse2.CompareGreaterThan(Vector128.Abs(value), bound).AsSingle()) != 0;

    private static void PrefixScalar(short* row, int count, int running)
    {
        for (var x = 0; x < count; x++)
        {
            running += row[x];
            row[x] = (short)Math.Clamp(running, short.MinValue, short.MaxValue);
        }
    }
}
