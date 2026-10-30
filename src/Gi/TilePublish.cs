using System.Runtime.CompilerServices;
#if NET
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
#endif

namespace Gi;

internal static unsafe partial class TileBake
{
    private const int PublishWholeTile = TileSize * TileSize / 2;

    [MethodImpl(Jit.Hot)]
    internal static ResolveOutcome Publish(PlaneStore* store, TilePlanes* tile, int x0, int y0, int x1, int y1,
        int tick)
    {
        var cells = store->Cells(*tile);
        if ((cells.Summary->CarryI | cells.Summary->HighT | cells.Summary->HighB) != 0)
            return PublishWide(store, tile, x0, y0, x1, y1, tick);
#if NET
        if (Avx2.IsSupported) return Publish256(cells, x0, y0, x1, y1, tick);
        if (Vector) return Publish128(cells, x0, y0, x1, y1, tick);
#endif
        return PublishScalar(cells, x0, y0, x1, y1, tick);
    }

    [MethodImpl(Jit.Hot)]
    internal static ResolveOutcome PublishScalar(PlaneCells t, int x0, int y0, int x1, int y1, int tick)
    {
        if ((x1 - x0) * (y1 - y0) >= PublishWholeTile)
        {
            x0 = 0;
            y0 = 0;
            x1 = TileSize;
            y1 = TileSize;
        }

        var tentOut = stackalloc int[TileSize];
        var bellOut = stackalloc int[TileSize];
        if (t.T == null) new Span<int>(tentOut, TileSize).Clear();
        if (t.B == null) new Span<int>(bellOut, TileSize).Clear();

        var changed = false;
        for (var y = y0; y < y1; y++)
        {
            if (t.T != null) SmoothRow(t.T, y, tentOut);
            if (t.B != null) SmoothRow(t.B, y, bellOut);
            var row = y * TileSize;
            changed |= RowScalar(t.I + row, tentOut, bellOut, t.Page + row, x0, x1);
        }

        UpdateSummaries(t, x0, y0, x1, y1);
        if (changed) t.Summary->Epoch = tick;
        return Outcome(Occupied(t.Summary), changed);
    }

    [MethodImpl(Jit.Hot)]
    private static bool RowScalar(int* i, int* tentOut, int* bellOut, short* page, int x0, int x1)
    {
        var changed = false;
        for (var x = x0; x < x1; x++)
        {
            var total = (long)i[x] + tentOut[x] + bellOut[x];
            var cell = (short)Math.Clamp(total, short.MinValue, short.MaxValue);
            if (cell != page[x]) changed = true;
            page[x] = cell;
        }

        return changed;
    }

    [MethodImpl(Jit.Hot)]
    internal static bool AllZero(int* values, int count)
    {
        var i = 0;
#if NET
        if (Vector256.IsHardwareAccelerated)
        {
            var seen = Vector256<int>.Zero;
            for (; i + 8 <= count; i += 8) seen |= Vector256.Load(values + i);
            if (seen != Vector256<int>.Zero) return false;
        }
#endif
        for (; i < count; i++)
            if (values[i] != 0)
                return false;

        return true;
    }

    [MethodImpl(Jit.Hot)]
    internal static bool AllZero(long* values, int count)
    {
        var i = 0;
#if NET
        if (Vector256.IsHardwareAccelerated)
        {
            var seen = Vector256<long>.Zero;
            for (; i + 4 <= count; i += 4) seen |= Vector256.Load(values + i);
            if (seen != Vector256<long>.Zero) return false;
        }
#endif
        for (; i < count; i++)
            if (values[i] != 0)
                return false;

        return true;
    }

    [MethodImpl(Jit.Hot)]
    internal static bool PageAny(short* page)
    {
#if NET
        if (Avx2.IsSupported)
        {
            var a = Vector256<short>.Zero;
            var b = Vector256<short>.Zero;
            var c = Vector256<short>.Zero;
            var d = Vector256<short>.Zero;
            for (var i = 0; i < TileSize * TileSize; i += 64)
            {
                a |= Avx.LoadVector256(page + i);
                b |= Avx.LoadVector256(page + i + 16);
                c |= Avx.LoadVector256(page + i + 32);
                d |= Avx.LoadVector256(page + i + 48);
            }

            return !Vector256.EqualsAll((a | b) | (c | d), Vector256<short>.Zero);
        }

        if (Vector)
        {
            var a = Vector128<short>.Zero;
            var b = Vector128<short>.Zero;
            var c = Vector128<short>.Zero;
            var d = Vector128<short>.Zero;
            for (var i = 0; i < TileSize * TileSize; i += 32)
            {
                a |= LoadShorts(page + i);
                b |= LoadShorts(page + i + 8);
                c |= LoadShorts(page + i + 16);
                d |= LoadShorts(page + i + 24);
            }

            return !Vector128.EqualsAll((a | b) | (c | d), Vector128<short>.Zero);
        }
#endif
        var any = false;
        for (var i = 0; i < TileSize * TileSize; i++) any |= page[i] != 0;
        return any;
    }

#if NET
    [MethodImpl(Jit.Hot)]
    internal static ResolveOutcome Publish128(PlaneCells t, int x0, int y0, int x1, int y1, int tick)
    {
        if ((x1 - x0) * (y1 - y0) >= PublishWholeTile)
        {
            x0 = 0;
            y0 = 0;
            x1 = TileSize;
            y1 = TileSize;
        }

        var tentOut = stackalloc int[TileSize];
        var bellOut = stackalloc int[TileSize];
        if (t.T == null) new Span<int>(tentOut, TileSize).Clear();
        if (t.B == null) new Span<int>(bellOut, TileSize).Clear();

        var differs = Vector128<short>.Zero;
        var bound = Vector128.Create(ExactBound);
        var guard = Vector128.Create(ExactMask);
        var changed = false;
        for (var y = y0; y < y1; y++)
        {
            if (t.T != null) SmoothRow(t.T, y, tentOut);
            if (t.B != null) SmoothRow(t.B, y, bellOut);
            var row = y * TileSize;
            var iRow = t.I + row;
            var pageRow = t.Page + row;
            var vectorEnd = x1 - ((x1 - x0) & 7);

            var wide = Vector128<int>.Zero;
            for (var x = x0; x < vectorEnd; x += 4) wide |= Load128(iRow + x) + bound;
            if (!Vector128.EqualsAll(wide & guard, Vector128<int>.Zero))
            {
                changed |= RowScalar(iRow, tentOut, bellOut, pageRow, x0, x1);
                continue;
            }

            for (var x = x0; x < vectorEnd; x += 8)
            {
                var totalA = Load128(iRow + x) + Load128(tentOut + x) + Load128(bellOut + x);
                var totalB = Load128(iRow + x + 4) + Load128(tentOut + x + 4) + Load128(bellOut + x + 4);
                var cells = PackCells(totalA, totalB);
                var published = LoadShorts(pageRow + x);
                differs |= ~Vector128.Equals(published, cells);
                StoreShorts(pageRow + x, cells);
            }

            changed |= RowScalar(iRow, tentOut, bellOut, pageRow, vectorEnd, x1);
        }

        UpdateSummaries(t, x0, y0, x1, y1);
        changed |= !Vector128.EqualsAll(differs, Vector128<short>.Zero);
        if (changed) t.Summary->Epoch = tick;
        return Outcome(Occupied(t.Summary), changed);
    }

    [MethodImpl(Jit.Hot)]
    internal static ResolveOutcome Publish256(PlaneCells t, int x0, int y0, int x1, int y1, int tick)
    {
        if ((x1 - x0) * (y1 - y0) >= PublishWholeTile)
        {
            x0 = 0;
            y0 = 0;
            x1 = TileSize;
            y1 = TileSize;
        }

        var planes = t.T != null || t.B != null;
        if (!planes && x0 == 0 && x1 == TileSize) return PublishRows256(t, y0, y1, tick);
        if (planes && x0 == 0 && x1 == TileSize) return PublishPlanes256(t, y0, y1, tick);

        var tentOut = stackalloc int[TileSize];
        var bellOut = stackalloc int[TileSize];
        if (t.T == null) new Span<int>(tentOut, TileSize).Clear();
        if (t.B == null) new Span<int>(bellOut, TileSize).Clear();

        var differs = Vector128<short>.Zero;
        var bound = Vector256.Create(ExactBound);
        var guard = Vector256.Create(ExactMask);
        var changed = false;
        for (var y = y0; y < y1; y++)
        {
            if (t.T != null) SmoothRow(t.T, y, tentOut);
            if (t.B != null) SmoothRow(t.B, y, bellOut);
            var row = y * TileSize;
            var iRow = t.I + row;
            var pageRow = t.Page + row;
            var vectorEnd = x1 - ((x1 - x0) & 7);

            if (planes)
            {
                var wide = Vector256<int>.Zero;
                for (var x = x0; x < vectorEnd; x += 8) wide |= Avx.LoadVector256(iRow + x) + bound;
                if (!Vector256.EqualsAll(wide & guard, Vector256<int>.Zero))
                {
                    changed |= RowScalar(iRow, tentOut, bellOut, pageRow, x0, x1);
                    continue;
                }
            }

            for (var x = x0; x < vectorEnd; x += 8)
            {
                var total =
                    Avx.LoadVector256(iRow + x) + Avx.LoadVector256(tentOut + x) + Avx.LoadVector256(bellOut + x);
                var packed = Avx2.PackSignedSaturate(total, total);
                var cells = Avx2.Permute4x64(packed.AsInt64(), 0xd8).GetLower().AsInt16();
                var published = Sse2.LoadVector128(pageRow + x);
                differs |= ~Vector128.Equals(published, cells);
                Sse2.Store(pageRow + x, cells);
            }

            if (vectorEnd < x1) changed |= RowScalar(iRow, tentOut, bellOut, pageRow, vectorEnd, x1);
        }

        UpdateSummaries(t, x0, y0, x1, y1);
        changed |= !Vector128.EqualsAll(differs, Vector128<short>.Zero);
        if (changed) t.Summary->Epoch = tick;
        return Outcome(Occupied(t.Summary), changed);
    }

    [MethodImpl(Jit.Hot)]
    private static ResolveOutcome PublishPlanes256(PlaneCells t, int y0, int y1, int tick)
    {
        var summary = t.Summary;
        var tentOut = stackalloc int[TileSize];
        var bellOut = stackalloc int[TileSize];
        if (t.T == null) new Span<int>(tentOut, TileSize).Clear();
        if (t.B == null) new Span<int>(bellOut, TileSize).Clear();

        var differs = Vector256<short>.Zero;
        var bound = Vector256.Create(ExactBound);
        var guard = Vector256.Create(ExactMask);
        var changed = false;
        var sum = summary->Sum;
        for (var y = y0; y < y1; y++)
        {
            if (t.T != null) SmoothRow(t.T, y, tentOut);
            if (t.B != null) SmoothRow(t.B, y, bellOut);
            var iRow = t.I + y * TileSize;
            var pageRow = t.Page + y * TileSize;

            var wide = Vector256<int>.Zero;
            for (var x = 0; x < TileSize; x += 8) wide |= Avx.LoadVector256(iRow + x) + bound;
            if (!Vector256.EqualsAll(wide & guard, Vector256<int>.Zero))
            {
                changed |= RowScalar(iRow, tentOut, bellOut, pageRow, 0, TileSize);
                RowStats(pageRow, out var scalarSum, out var scalarMax);
                sum += scalarSum - summary->RowSums[y];
                summary->RowSums[y] = scalarSum;
                summary->RowMaxima[y] = scalarMax;
                continue;
            }

            PublishPlaneRow(iRow, tentOut, bellOut, pageRow, ref differs, out var rowSum, out var rowMax);
            sum += rowSum - summary->RowSums[y];
            summary->RowSums[y] = rowSum;
            summary->RowMaxima[y] = rowMax;
        }

        if (y1 > y0) CommitSummaries(t, 0, y0, TileSize, y1, sum);
        changed |= !Vector256.EqualsAll(differs, Vector256<short>.Zero);
        if (changed) summary->Epoch = tick;
        return Outcome(Occupied(summary), changed);
    }

    [MethodImpl(Jit.Inline)]
    private static void PublishPlaneRow(int* iRow, int* tentOut, int* bellOut, short* pageRow,
        ref Vector256<short> differs, out int rowSum, out short rowMax)
    {
        var low = PackSixteen(
            Avx.LoadVector256(iRow) + Avx.LoadVector256(tentOut) + Avx.LoadVector256(bellOut),
            Avx.LoadVector256(iRow + 8) + Avx.LoadVector256(tentOut + 8) + Avx.LoadVector256(bellOut + 8));
        var high = PackSixteen(
            Avx.LoadVector256(iRow + 16) + Avx.LoadVector256(tentOut + 16) + Avx.LoadVector256(bellOut + 16),
            Avx.LoadVector256(iRow + 24) + Avx.LoadVector256(tentOut + 24) + Avx.LoadVector256(bellOut + 24));
        differs |= (low ^ Avx.LoadVector256(pageRow)) | (high ^ Avx.LoadVector256(pageRow + 16));
        Avx.Store(pageRow, low);
        Avx.Store(pageRow + 16, high);
        var ones = Vector256.Create((short)1);
        rowSum = Vector256.Sum(Avx2.MultiplyAddAdjacent(low, ones) + Avx2.MultiplyAddAdjacent(high, ones));
        var widest = Vector256.Max(low, high);
        rowMax = RowMaximum(Vector128.Max(widest.GetLower(), widest.GetUpper()));
    }

    [MethodImpl(Jit.Hot)]
    private static ResolveOutcome PublishRows256(PlaneCells t, int y0, int y1, int tick)
    {
        var summary = t.Summary;
        var differs = Vector256<short>.Zero;
        var delta = Vector128<int>.Zero;
        var tail = 0L;
        var y = y0;
        for (; y + 4 <= y1; y += 4)
        {
            PackRow(t.I + y * TileSize, t.Page + y * TileSize, ref differs, out var sums0, out var maxima0);
            PackRow(t.I + (y + 1) * TileSize, t.Page + (y + 1) * TileSize, ref differs, out var sums1,
                out var maxima1);
            PackRow(t.I + (y + 2) * TileSize, t.Page + (y + 2) * TileSize, ref differs, out var sums2,
                out var maxima2);
            PackRow(t.I + (y + 3) * TileSize, t.Page + (y + 3) * TileSize, ref differs, out var sums3,
                out var maxima3);
            var folded = Avx2.HorizontalAdd(Avx2.HorizontalAdd(sums0, sums1), Avx2.HorizontalAdd(sums2, sums3));
            var rowSums = folded.GetLower() + folded.GetUpper();
            var previous = Sse2.LoadVector128(summary->RowSums + y);
            Sse2.Store(summary->RowSums + y, rowSums);
            delta += rowSums - previous;
            summary->RowMaxima[y] = RowMaximum(maxima0);
            summary->RowMaxima[y + 1] = RowMaximum(maxima1);
            summary->RowMaxima[y + 2] = RowMaximum(maxima2);
            summary->RowMaxima[y + 3] = RowMaximum(maxima3);
        }

        for (; y < y1; y++)
        {
            PackRow(t.I + y * TileSize, t.Page + y * TileSize, ref differs, out var sums, out var maxima);
            var rowSum = Vector256.Sum(sums);
            tail += rowSum - summary->RowSums[y];
            summary->RowSums[y] = rowSum;
            summary->RowMaxima[y] = RowMaximum(maxima);
        }

        CommitSummaries(t, 0, y0, TileSize, y1, summary->Sum + Vector128.Sum(delta) + tail);
        var changed = !Vector256.EqualsAll(differs, Vector256<short>.Zero);
        if (changed) summary->Epoch = tick;
        return Outcome(Occupied(summary), changed);
    }

    [MethodImpl(Jit.Inline)]
    private static void PackRow(int* iRow, short* pageRow, ref Vector256<short> differs, out Vector256<int> sums,
        out Vector128<short> maxima)
    {
        var low = PackSixteen(Avx.LoadVector256(iRow), Avx.LoadVector256(iRow + 8));
        var high = PackSixteen(Avx.LoadVector256(iRow + 16), Avx.LoadVector256(iRow + 24));
        differs |= (low ^ Avx.LoadVector256(pageRow)) | (high ^ Avx.LoadVector256(pageRow + 16));
        Avx.Store(pageRow, low);
        Avx.Store(pageRow + 16, high);
        var ones = Vector256.Create((short)1);
        sums = Avx2.MultiplyAddAdjacent(low, ones) + Avx2.MultiplyAddAdjacent(high, ones);
        var widest = Vector256.Max(low, high);
        maxima = Vector128.Max(widest.GetLower(), widest.GetUpper());
    }

    [MethodImpl(Jit.Inline)]
    private static Vector256<short> PackSixteen(Vector256<int> first, Vector256<int> second)
    {
        return Avx2.Permute4x64(Avx2.PackSignedSaturate(first, second).AsInt64(), 0xd8).AsInt16();
    }

    [MethodImpl(Jit.Inline)]
    private static short RowMaximum(Vector128<short> maxima)
    {
        var flip = Vector128.Create((short)0x7FFF);
        return (short)(Sse41.MinHorizontal((maxima ^ flip).AsUInt16()).ToScalar() ^ 0x7FFF);
    }

    [MethodImpl(Jit.Inline)]
    private static Vector128<short> PackCells(Vector128<int> lower, Vector128<int> upper)
    {
        if (Sse2.IsSupported) return Sse2.PackSignedSaturate(lower, upper).AsInt16();
        var narrowed = AdvSimd.ExtractNarrowingSaturateLower(lower);
        return AdvSimd.ExtractNarrowingSaturateUpper(narrowed, upper);
    }
#endif
}
