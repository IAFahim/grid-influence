#if NET
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Gi;

internal static unsafe class SimdOps
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static short HorizontalMax(Vector256<short> vector)
    {
        return HorizontalMax(Vector128.Max(vector.GetLower(), vector.GetUpper()));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static short HorizontalMax(Vector128<short> vector)
    {
        var halves = Vector128.Max(vector, Vector128.Shuffle(vector, Vector128.Create((short)4, 5, 6, 7, 0, 1, 2, 3)));
        var quarters =
            Vector128.Max(halves, Vector128.Shuffle(halves, Vector128.Create((short)2, 3, 0, 1, 6, 7, 4, 5)));
        return Vector128.Max(quarters, Vector128.Shuffle(quarters, Vector128.Create((short)1, 0, 3, 2, 5, 4, 7, 6)))
            .ToScalar();
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static int FirstEqualFlat(short* values, short target, int count)
    {
        var index = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            var goal = Vector256.Create(target);
            for (; index + 16 <= count; index += 16)
            {
                var mask = Vector256.Equals(Vector256.Load(values + index), goal).ExtractMostSignificantBits();
                if (mask != 0) return index + BitOperations.TrailingZeroCount(mask);
            }
        }

        if (Vector128.IsHardwareAccelerated)
        {
            var goal = Vector128.Create(target);
            for (; index + 8 <= count; index += 8)
            {
                var mask = Vector128.Equals(Vector128.Load(values + index), goal).ExtractMostSignificantBits();
                if (mask != 0) return index + BitOperations.TrailingZeroCount(mask);
            }
        }

        for (; index < count; index++)
            if (values[index] == target)
                return index;
        return count;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static int FirstEqualRows(short* rows, int stride, short target, int laneCount, int rowCount)
    {
        if (Vector128.IsHardwareAccelerated && laneCount <= 8)
        {
            var goal = Vector128.Create(target);
            var laneMask = (1u << laneCount) - 1u;
            for (var y = 0; y < rowCount; y++)
            {
                var row = rows + y * stride;
                var mask = Vector128.Equals(Vector128.Load(row), goal).ExtractMostSignificantBits() & laneMask;
                if (mask != 0) return y * stride + BitOperations.TrailingZeroCount(mask);
            }

            return rowCount * stride;
        }

        for (var y = 0; y < rowCount; y++)
        {
            var row = rows + y * stride;
            var found = FirstEqualSpan(row, 0, laneCount, target);
            if (found < laneCount) return y * stride + found;
        }

        return rowCount * stride;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static int FirstEqualSpan(short* values, int first, int end, short target)
    {
        var count = end - first;
        var index = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            var goal = Vector256.Create(target);
            for (; index + 16 <= count; index += 16)
            {
                var mask = Vector256.Equals(Vector256.Load(values + first + index), goal).ExtractMostSignificantBits();
                if (mask != 0) return first + index + BitOperations.TrailingZeroCount(mask);
            }
        }

        if (Vector128.IsHardwareAccelerated)
        {
            var goal = Vector128.Create(target);
            for (; index + 8 <= count; index += 8)
            {
                var mask = Vector128.Equals(Vector128.Load(values + first + index), goal).ExtractMostSignificantBits();
                if (mask != 0) return first + index + BitOperations.TrailingZeroCount(mask);
            }
        }

        for (; index < count; index++)
            if (values[first + index] == target)
                return first + index;
        return end;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static short SegmentMax(short* row, int first, int end)
    {
        if (Vector256.IsHardwareAccelerated)
        {
            var indicesLow = Vector256.Create((short)0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15);
            var indicesHigh = indicesLow + Vector256.Create((short)16);
            var from = Vector256.Create((short)first);
            var to = Vector256.Create((short)end);
            var floor = Vector256.Create(short.MinValue);
            var keepLow = Vector256.GreaterThanOrEqual(indicesLow, from) & Vector256.LessThan(indicesLow, to);
            var keepHigh = Vector256.GreaterThanOrEqual(indicesHigh, from) & Vector256.LessThan(indicesHigh, to);
            return HorizontalMax(Vector256.Max(
                Vector256.ConditionalSelect(keepLow, Vector256.Load(row), floor),
                Vector256.ConditionalSelect(keepHigh, Vector256.Load(row + 16), floor)));
        }

        if (Vector128.IsHardwareAccelerated)
        {
            var indices = Vector128.Create((short)0, 1, 2, 3, 4, 5, 6, 7);
            var step = Vector128.Create((short)8);
            var from = Vector128.Create((short)first);
            var to = Vector128.Create((short)end);
            var floor = Vector128.Create(short.MinValue);
            var best = floor;
            for (var offset = 0; offset < 32; offset += 8)
            {
                var keep = Vector128.GreaterThanOrEqual(indices, from) & Vector128.LessThan(indices, to);
                best = Vector128.Max(best, Vector128.ConditionalSelect(keep, Vector128.Load(row + offset), floor));
                indices += step;
            }

            return HorizontalMax(best);
        }

        var maximum = row[first];
        for (var i = first + 1; i < end; i++)
            if (row[i] > maximum)
                maximum = row[i];
        return maximum;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> PairSums(Vector256<short> values, Vector256<short> ones)
    {
        if (Avx2.IsSupported) return Avx2.MultiplyAddAdjacent(values, ones);

        var (low, high) = Vector256.Widen(values);
        return low + high;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static long RaggedSum(short* rows, int* rowSums, int tileX, int rowCount, int* rowLow, int* rowHigh)
    {
        if (Vector256.IsHardwareAccelerated)
        {
            var indicesLow = Vector256.Create((short)0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15);
            var indicesHigh = indicesLow + Vector256.Create((short)16);
            var ones = Vector256.Create((short)1);
            var total = Vector256<int>.Zero;
            var whole = 0L;
            for (var r = 0; r < rowCount; r++)
            {
                var first = Math.Max(rowLow[r], tileX) - tileX;
                var end = Math.Min(rowHigh[r], tileX + TileBake.TileSize) - tileX;
                if (end <= first) continue;

                if (rowSums != null && end - first == TileBake.TileSize)
                {
                    whole += rowSums[r];
                    continue;
                }

                var from = Vector256.Create((short)first);
                var to = Vector256.Create((short)end);
                var row = rows + r * TileBake.TileSize;
                var low =
                    Vector256.Load(row) & Vector256.GreaterThanOrEqual(indicesLow, from) &
                    Vector256.LessThan(indicesLow, to);
                var high =
                    Vector256.Load(row + 16) & Vector256.GreaterThanOrEqual(indicesHigh, from) &
                    Vector256.LessThan(indicesHigh, to);
                total += PairSums(low, ones) + PairSums(high, ones);
            }

            return whole + Vector256.Sum(total);
        }

        if (Vector128.IsHardwareAccelerated)
        {
            var baseIndices = Vector128.Create((short)0, 1, 2, 3, 4, 5, 6, 7);
            var step = Vector128.Create((short)8);
            var total = Vector128<int>.Zero;
            var whole = 0L;
            for (var r = 0; r < rowCount; r++)
            {
                var first = Math.Max(rowLow[r], tileX) - tileX;
                var end = Math.Min(rowHigh[r], tileX + TileBake.TileSize) - tileX;
                if (end <= first) continue;

                if (rowSums != null && end - first == TileBake.TileSize)
                {
                    whole += rowSums[r];
                    continue;
                }

                var from = Vector128.Create((short)first);
                var to = Vector128.Create((short)end);
                var row = rows + r * TileBake.TileSize;
                var indices = baseIndices;
                for (var offset = 0; offset < TileBake.TileSize; offset += 8)
                {
                    var masked = Vector128.Load(row + offset) &
                                 Vector128.GreaterThanOrEqual(indices, from) &
                                 Vector128.LessThan(indices, to);
                    var (w0, w1) = Vector128.Widen(masked);
                    total += w0 + w1;
                    indices += step;
                }
            }

            return whole + Vector128.Sum(total);
        }

        var sum = 0L;
        for (var r = 0; r < rowCount; r++)
        {
            var first = Math.Max(rowLow[r], tileX) - tileX;
            var end = Math.Min(rowHigh[r], tileX + TileBake.TileSize) - tileX;
            if (end <= first) continue;

            if (rowSums != null && end - first == TileBake.TileSize)
            {
                sum += rowSums[r];
                continue;
            }

            var row = rows + r * TileBake.TileSize;
            for (var i = first; i < end; i++) sum += row[i];
        }

        return sum;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static long LinearDelta(short* page, int* core, int top, int rowCount, int* rowLow, int* rowHigh)
    {
        const int columns = TileBake.TileSize;
        var delta = 0L;
        if (page != null && Vector256.IsHardwareAccelerated)
        {
            var floor = Vector256.Create((int)short.MinValue);
            var ceiling = Vector256.Create((int)short.MaxValue);
            var total = Vector256<int>.Zero;
            for (var r = 0; r < rowCount; r++)
            {
                var row = (top + r) * columns;
                var end = rowHigh[r];
                var c = rowLow[r];
                for (; c + 16 <= end; c += 16)
                {
                    var (page0, page1) = Vector256.Widen(Vector256.Load(page + row + c));
                    var absent0 = Vector256.Min(Vector256.Max(page0 - Vector256.Load(core + row + c), floor), ceiling);
                    var absent1 =
                        Vector256.Min(Vector256.Max(page1 - Vector256.Load(core + row + c + 8), floor), ceiling);
                    total += absent0 - page0 + absent1 - page1;
                }

                for (; c < end; c++)
                {
                    var value = page[row + c];
                    delta += Saturate((long)value - core[row + c]) - value;
                }
            }

            return delta + Vector256.Sum(total);
        }

        if (page != null && Vector128.IsHardwareAccelerated)
        {
            var floor = Vector128.Create((int)short.MinValue);
            var ceiling = Vector128.Create((int)short.MaxValue);
            var total = Vector128<int>.Zero;
            for (var r = 0; r < rowCount; r++)
            {
                var row = (top + r) * columns;
                var end = rowHigh[r];
                var c = rowLow[r];
                for (; c + 8 <= end; c += 8)
                {
                    var (page0, page1) = Vector128.Widen(Vector128.Load(page + row + c));
                    var absent0 = Vector128.Min(Vector128.Max(page0 - Vector128.Load(core + row + c), floor), ceiling);
                    var absent1 =
                        Vector128.Min(Vector128.Max(page1 - Vector128.Load(core + row + c + 4), floor), ceiling);
                    total += absent0 - page0 + absent1 - page1;
                }

                for (; c < end; c++)
                {
                    var value = page[row + c];
                    delta += Saturate((long)value - core[row + c]) - value;
                }
            }

            return delta + Vector128.Sum(total);
        }

        for (var r = 0; r < rowCount; r++)
        {
            var row = (top + r) * columns;
            for (var c = rowLow[r]; c < rowHigh[r]; c++)
            {
                var value = page == null ? (short)0 : page[row + c];
                delta += Saturate((long)value - core[row + c]) - value;
            }
        }

        return delta;
    }

    private static long Saturate(long value)
    {
        return Math.Clamp(value, short.MinValue, short.MaxValue);
    }
}
#endif
