#if NET
using System.Runtime.CompilerServices;
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
        if ((cells.Summary->CarryI | cells.Summary->HighT | cells.Summary->HighB) != 0)
            return PublishWide(store, tile, x0, y0, x1, y1, tick);
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
        return Outcome(PageAny(t.Page), changed);
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
        return Outcome(PageAny(t.Page), changed);
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

            var wide = Vector256<int>.Zero;
            for (var x = x0; x < vectorEnd; x += 8) wide |= Avx.LoadVector256(iRow + x) + bound;
            if (!Vector256.EqualsAll(wide & guard, Vector256<int>.Zero))
            {
                changed |= RowScalar(iRow, tentOut, bellOut, pageRow, x0, x1);
                continue;
            }

            for (var x = x0; x < vectorEnd; x += 8)
            {
                var total = Avx.LoadVector256(iRow + x) + Avx.LoadVector256(tentOut + x) + Avx.LoadVector256(bellOut + x);
                var packed = Avx2.PackSignedSaturate(total, total);
                var cells = Avx2.Permute4x64(packed.AsInt64(), 0xd8).GetLower().AsInt16();
                var published = Sse2.LoadVector128(pageRow + x);
                differs |= ~Vector128.Equals(published, cells);
                Sse2.Store(pageRow + x, cells);
            }

            changed |= RowScalar(iRow, tentOut, bellOut, pageRow, vectorEnd, x1);
        }

        UpdateSummaries(t, x0, y0, x1, y1);
        changed |= !Vector128.EqualsAll(differs, Vector128<short>.Zero);
        if (changed) t.Summary->Epoch = tick;
        return Outcome(PageAny(t.Page), changed);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> PackCells(Vector128<int> lower, Vector128<int> upper)
    {
        if (Sse2.IsSupported) return Sse2.PackSignedSaturate(lower, upper).AsInt16();
        var narrowed = AdvSimd.ExtractNarrowingSaturateLower(lower);
        return AdvSimd.ExtractNarrowingSaturateUpper(narrowed, upper);
    }
#endif

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
            if (values[i] != 0) return false;

        return true;
    }

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
            if (values[i] != 0) return false;

        return true;
    }

    #if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    #endif
    internal static bool PageAny(short* page)
    {
#if NET
        if (Avx2.IsSupported)
        {
            var wide = Vector256<short>.Zero;
            for (var i = 0; i < TileSize * TileSize; i += 16) wide |= Avx.LoadVector256(page + i);
            return !Vector256.EqualsAll(wide, Vector256<short>.Zero);
        }

        if (Vector)
        {
            var wide = Vector128<short>.Zero;
            for (var i = 0; i < TileSize * TileSize; i += 8) wide |= LoadShorts(page + i);
            return !Vector128.EqualsAll(wide, Vector128<short>.Zero);
        }
#endif
        var any = false;
        for (var i = 0; i < TileSize * TileSize; i++) any |= page[i] != 0;
        return any;
    }
}
