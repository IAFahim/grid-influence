using System.Runtime.CompilerServices;
#if NET
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
#endif

namespace Gi;

internal static unsafe partial class TileBake
{
    internal const int PublishWholeTile = TileSize * TileSize / 2;

    internal static ResolveOutcome Publish(PlaneStore* store, TilePlanes* tile, int x0, int y0, int x1, int y1, int tick)
    {
        var cells = store->Cells(*tile);
#if NET
        if (Avx2.IsSupported) return Publish256(cells, x0, y0, x1, y1, tick);
        if (Vector) return Publish128(cells, x0, y0, x1, y1, tick);
#endif
        return PublishScalar(cells, x0, y0, x1, y1, tick);
    }

    #if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    #endif
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

        var any = false;
        var changed = false;
        for (var y = y0; y < y1; y++)
        {
            if (t.T != null) SmoothRow(t.T, y, tentOut);
            if (t.B != null) SmoothRow(t.B, y, bellOut);
            var row = y * TileSize;
            changed |= RowScalar(t.I + row, tentOut, bellOut, t.Page + row, x0, x1, ref any);
        }

        UpdateSummaries(t, x0, y0, x1, y1);
        if (changed) t.Summary->Epoch = tick;
        return Outcome(any, changed);
    }

#if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
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

        var acc = Vector128<int>.Zero;
        var differs = Vector128<short>.Zero;
        var bound = Vector128.Create(ExactBound);
        var guard = Vector128.Create(ExactMask);
        var anyScalar = false;
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
                changed |= RowScalar(iRow, tentOut, bellOut, pageRow, x0, x1, ref anyScalar);
                continue;
            }

            for (var x = x0; x < vectorEnd; x += 8)
            {
                var totalA = Load128(iRow + x) + Load128(tentOut + x) + Load128(bellOut + x);
                var totalB = Load128(iRow + x + 4) + Load128(tentOut + x + 4) + Load128(bellOut + x + 4);
                acc |= totalA | totalB;
                var cells = PackCells(totalA, totalB);
                var published = LoadShorts(pageRow + x);
                differs |= ~Vector128.Equals(published, cells);
                StoreShorts(pageRow + x, cells);
            }

            changed |= RowScalar(iRow, tentOut, bellOut, pageRow, vectorEnd, x1, ref anyScalar);
        }

        UpdateSummaries(t, x0, y0, x1, y1);
        var any = anyScalar || !Vector128.EqualsAll(acc, Vector128<int>.Zero);
        changed |= !Vector128.EqualsAll(differs, Vector128<short>.Zero);
        if (changed) t.Summary->Epoch = tick;
        return Outcome(any, changed);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static ResolveOutcome Publish256(PlaneCells t, int x0, int y0, int x1, int y1, int tick)
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

        var acc = Vector256<int>.Zero;
        var differs = Vector128<short>.Zero;
        var bound = Vector256.Create(ExactBound);
        var guard = Vector256.Create(ExactMask);
        var anyScalar = false;
        var changed = false;
        for (var y = y0; y < y1; y++)
        {
            if (t.T != null) SmoothRow(t.T, y, tentOut);
            if (t.B != null) SmoothRow(t.B, y, bellOut);
            var row = y * TileSize;
            var iRow = t.I + row;
            var pageRow = t.Page + row;
            var vectorEnd = x1 - ((x1 - x0) & 7);

            var wide = Vector256<int>.Zero;
            for (var x = x0; x < vectorEnd; x += 8) wide |= Avx.LoadVector256(iRow + x) + bound;
            if (!Vector256.EqualsAll(wide & guard, Vector256<int>.Zero))
            {
                changed |= RowScalar(iRow, tentOut, bellOut, pageRow, x0, x1, ref anyScalar);
                continue;
            }

            for (var x = x0; x < vectorEnd; x += 8)
            {
                var total = Avx.LoadVector256(iRow + x) + Avx.LoadVector256(tentOut + x) + Avx.LoadVector256(bellOut + x);
                acc |= total;
                var packed = Avx2.PackSignedSaturate(total, total);
                var cells = Avx2.Permute4x64(packed.AsInt64(), 0xd8).GetLower().AsInt16();
                var published = Sse2.LoadVector128(pageRow + x);
                differs |= ~Vector128.Equals(published, cells);
                Sse2.Store(pageRow + x, cells);
            }

            changed |= RowScalar(iRow, tentOut, bellOut, pageRow, vectorEnd, x1, ref anyScalar);
        }

        UpdateSummaries(t, x0, y0, x1, y1);
        var any = anyScalar || !Vector256.EqualsAll(acc, Vector256<int>.Zero);
        changed |= !Vector128.EqualsAll(differs, Vector128<short>.Zero);
        if (changed) t.Summary->Epoch = tick;
        return Outcome(any, changed);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> PackCells(Vector128<int> lower, Vector128<int> upper)
    {
        if (Sse2.IsSupported) return Sse2.PackSignedSaturate(lower, upper).AsInt16();
        var narrowed = AdvSimd.ExtractNarrowingSaturateLower(lower);
        return AdvSimd.ExtractNarrowingSaturateUpper(narrowed, upper);
    }
#endif

    private static bool RowScalar(int* i, int* tentOut, int* bellOut, short* page, int x0, int x1, ref bool any)
    {
        var changed = false;
        for (var x = x0; x < x1; x++)
        {
            var total = (long)i[x] + tentOut[x] + bellOut[x];
            if (total != 0) any = true;
            var cell = (short)Math.Clamp(total, short.MinValue, short.MaxValue);
            if (cell != page[x]) changed = true;
            page[x] = cell;
        }

        return changed;
    }

    private static void UpdateSummaries(PlaneCells t, int x0, int y0, int x1, int y1)
    {
        var summary = t.Summary;
        var page = t.Page;
        var sum = summary->Sum;
        for (var y = y0; y < y1; y++)
        {
            var row = page + y * TileSize;
            var rowSum = 0;
            var rowMax = short.MinValue;
            for (var x = 0; x < TileSize; x++)
            {
                rowSum += row[x];
                if (row[x] > rowMax) rowMax = row[x];
            }

            sum += rowSum - summary->RowSums[y];
            summary->RowSums[y] = rowSum;
            summary->RowMaxima[y] = rowMax;
        }

        summary->Sum = sum;
        var max = summary->Max;
        var ax = (int)summary->ArgMaxX;
        var ay = (int)summary->ArgMaxY;
        if (ay >= y0 && ay < y1 && ax >= x0 && ax < x1 && page[ay * TileSize + ax] < max)
        {
            RescanMax(t, out max, out ax, out ay);
            summary->Max = max;
            summary->ArgMaxX = (short)ax;
            summary->ArgMaxY = (short)ay;
            return;
        }

        var candidate = short.MinValue;
        var cx = 0;
        var cy = 0;
        for (var y = y0; y < y1; y++)
        {
            var row = page + y * TileSize;
            for (var x = x0; x < x1; x++)
                if (row[x] > candidate)
                {
                    candidate = row[x];
                    cx = x;
                    cy = y;
                }
        }

        if (candidate > max)
        {
            summary->Max = candidate;
            summary->ArgMaxX = (short)cx;
            summary->ArgMaxY = (short)cy;
        }
        else if (candidate == max && (cy < ay || (cy == ay && cx < ax)))
        {
            summary->ArgMaxX = (short)cx;
            summary->ArgMaxY = (short)cy;
        }
    }

    private static void RescanMax(PlaneCells t, out short max, out int ax, out int ay)
    {
        var page = t.Page;
        max = page[0];
        ax = 0;
        ay = 0;
        for (var y = 0; y < TileSize; y++)
        {
            var row = page + y * TileSize;
            for (var x = 0; x < TileSize; x++)
                if (row[x] > max)
                {
                    max = row[x];
                    ax = x;
                    ay = y;
                }
        }
    }
}
