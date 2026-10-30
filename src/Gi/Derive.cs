using System.Runtime.CompilerServices;
#if NET
using System.Runtime.Intrinsics;
#endif

namespace Gi;

internal enum LayerOp : byte
{
    Base,
    Sum,
    Min,
    Max,
    Mask
}

internal struct LayerRecipe
{
    public LayerOp Op;
    public byte A;
    public byte B;
    public byte Shift;
    public byte PhaseBits;
    public byte TurnBits;
    public ushort TurnMask;
    public int AnchorMask;
    public int WeightA;
    public int WeightB;
    public short Low;
    public short High;
    public uint Reads;
    public byte Transient;
}

public static unsafe partial class World
{
    private static TileRect WholeTile => new()
    {
        X0 = 0,
        Y0 = 0,
        X1 = TileBake.TileSize,
        Y1 = TileBake.TileSize
    };

    [MethodImpl(Jit.Hot)]
    internal static byte AddDerived(byte world, LayerOp op, byte a, byte b, int weightA, int weightB, int shift,
        short low, short high)
    {
        var w = GetContext(world);
        if (w == null) throw new ArgumentOutOfRangeException(nameof(world));
        if (a >= w->LayerCount) throw new ArgumentOutOfRangeException(nameof(a));
        if (b >= w->LayerCount) throw new ArgumentOutOfRangeException(nameof(b));
        if (!ValidationRules.IsWeight(weightA)) throw new ArgumentOutOfRangeException(nameof(weightA));
        if (!ValidationRules.IsWeight(weightB)) throw new ArgumentOutOfRangeException(nameof(weightB));
        if (!ValidationRules.IsShift(shift)) throw new ArgumentOutOfRangeException(nameof(shift));
        if (low > high) throw new ArgumentOutOfRangeException(nameof(low));

        var reads = w->Recipes[a].Reads | w->Recipes[b].Reads;
        var id = AddLayer(world, Layer.Precision.Exact);
        for (var gi = 0; gi < w->GridCount; gi++)
        {
            var g = w->Grids + gi;
            g->Layers[id].Derived = 1;
            MarkLive(g, id, g->Layers + a);
            if (b != a) MarkLive(g, id, g->Layers + b);
        }

        w->Recipes[id] = new LayerRecipe
        {
            Op = op,
            A = a,
            B = b,
            Shift = (byte)shift,
            WeightA = weightA,
            WeightB = weightB,
            Low = low,
            High = high,
            Reads = reads
        };
        w->DerivedCount++;
        return id;
    }

    [MethodImpl(Jit.Inline)]
    internal static bool IsDerived(WorldCtx* w, int layer)
    {
        return w->Recipes[layer].Op != LayerOp.Base;
    }

    [MethodImpl(Jit.Hot)]
    private static bool IsTransient(WorldCtx* w, int layer)
    {
        return w->Recipes[layer].Transient != 0;
    }

    [MethodImpl(Jit.Hot)]
    private static void PrimeDerived(WorldCtx* w)
    {
        for (var d = 0; d < w->LayerCount; d++)
        {
            var r = w->Recipes + d;
            if (r->Op == LayerOp.Base) continue;

            for (var gi = 0; gi < w->GridCount; gi++)
            {
                var g = w->Grids + gi;
                var first = g->Layers + r->A;
                var second = g->Layers + r->B;
                if (first->Dirty.Length == 0 && second->Dirty.Length == 0) continue;

                var ld = EnsureDirty(g, (byte)d);
                PrimeSource(g, ld, first, IsTransient(w, r->A));
                if (r->B != r->A) PrimeSource(g, ld, second, IsTransient(w, r->B));
            }
        }
    }

    [MethodImpl(Jit.Hot)]
    private static void PrimeSource(GridCtx* g, LayerData* ld, LayerData* source, bool transient)
    {
        if (source->Derived != 0) return;

        var tiles = source->Dirty.Pointer;
        var rects = source->Rects.Pointer;
        var count = source->Dirty.Length;
        for (var i = 0; i < count; i++)
            GrowPrime(g, ld, tiles[i], transient ? WholeTile : rects[i]);
    }

    [MethodImpl(Jit.Hot)]
    private static int PrimeIndex(GridCtx* g, LayerData* ld, int tile)
    {
        var index = ld->InDirty[tile];
        if (index != 0) return index - 1;

        var n = ld->Dirty.Length;
        ld->Dirty.Resize(n + 1);
        ld->Rects.Resize(n + 1);
        ld->Dirty.Pointer[n] = tile;
        ld->Rects.Pointer[n] = new TileRect
        {
            X0 = TileBake.TileSize,
            Y0 = TileBake.TileSize,
            X1 = 0,
            Y1 = 0
        };
        ld->InDirty[tile] = n + 1;
        return n;
    }

    [MethodImpl(Jit.Hot)]
    private static void GrowPrime(GridCtx* g, LayerData* ld, int tile, TileRect add)
    {
        var index = PrimeIndex(g, ld, tile);
        var rect = ld->Rects.Pointer + index;
        if (add.X0 < rect->X0) rect->X0 = add.X0;
        if (add.Y0 < rect->Y0) rect->Y0 = add.Y0;
        if (add.X1 > rect->X1) rect->X1 = add.X1;
        if (add.Y1 > rect->Y1) rect->Y1 = add.Y1;
    }

    [MethodImpl(Jit.Hot)]
    private static void MarkDerivedInput(GridCtx* g, LayerData* ld, LayerData* source)
    {
        var tiles = source->Dirty.Pointer;
        var rects = source->Rects.Pointer;
        var count = source->Dirty.Length;
        for (var i = 0; i < count; i++) GrowRect(g, ld, tiles[i], rects[i]);
    }

    [MethodImpl(Jit.Hot)]
    private static void MarkLateInput(GridCtx* g, LayerData* ld, LayerData* source)
    {
        var tiles = source->Dirty.Pointer;
        var count = source->Dirty.Length;
        for (var i = 0; i < count; i++) GrowRect(g, ld, tiles[i], WholeTile);
    }

    [MethodImpl(Jit.Hot)]
    private static void GrowRect(GridCtx* g, LayerData* ld, int tile, TileRect add)
    {
        var index = DirtyIndex(g, ld, tile);
        var rect = ld->Rects.Pointer + index;
        if (add.X0 < rect->X0) rect->X0 = add.X0;
        if (add.Y0 < rect->Y0) rect->Y0 = add.Y0;
        if (add.X1 > rect->X1) rect->X1 = add.X1;
        if (add.Y1 > rect->Y1) rect->Y1 = add.Y1;
    }

    [MethodImpl(Jit.Hot)]
    private static void Derive(WorldCtx* w)
    {
        for (var d = 0; d < w->LayerCount; d++)
        {
            var r = w->Recipes + d;
            if (r->Op == LayerOp.Base) continue;

            for (var gi = 0; gi < w->GridCount; gi++)
            {
                var g = w->Grids + gi;
                var la = g->Layers + r->A;
                var lb = g->Layers + r->B;
                var own = g->Layers + d;
                if (la->Dirty.Length == 0 && lb->Dirty.Length == 0 && own->Dirty.Length == 0) continue;

                var ld = EnsureDirty(g, (byte)d);
                if (la->Derived != 0) MarkDerivedInput(g, ld, la);
                else if (IsTransient(w, r->A)) MarkLateInput(g, ld, la);
                else MarkAll(g, ld, la);
                if (r->B != r->A)
                {
                    if (lb->Derived != 0) MarkDerivedInput(g, ld, lb);
                    else if (IsTransient(w, r->B)) MarkLateInput(g, ld, lb);
                    else MarkAll(g, ld, lb);
                }

                var dirty = ld->Dirty.Pointer;
                var rects = ld->Rects.Pointer;
                var count = ld->Dirty.Length;
                var kept = 0;
                for (var i = 0; i < count; i++)
                {
                    var tile = dirty[i];
                    var rect = rects[i];
                    ld->InDirty[tile] = 0;
                    var pa = la->PageOf(tile);
                    var pb = lb->PageOf(tile);
                    var set = ld->SetOf(tile);
                    ResolveOutcome outcome;
                    if (pa == null && pb == null)
                    {
                        var existed = set != null;
                        var page = existed ? ld->Store.Cells(*set).Page : null;
                        outcome = existed && TileBake.PageAny(page) ? ResolveOutcome.Changed : ResolveOutcome.None;
                        if (existed) DropPage(ld, tile);

                        ld->Max.Update(g->TilesPerSide, tile, 0);
                        if (existed) RefreshTileHash(g, ld, tile);
                    }
                    else
                    {
                        if (set == null) set = EnsureSet(g, ld, tile);
                        var cells = ld->Store.Cells(*set);
                        var summary = cells.Summary;
                        var rowFirst = 0;
                        var rowLast = TileBake.TileSize;
                        if (summary->ArgMaxX != 0 && rect.Y1 > rect.Y0)
                        {
                            rowFirst = Math.Max(rect.Y0, 0);
                            rowLast = Math.Min(rect.Y1, TileBake.TileSize);
                        }
                        else
                        {
                            summary->ArgMaxX = 1;
                        }

                        outcome = CombineRows(r, pa, pb, cells, rowFirst, rowLast);
                        if ((outcome & ResolveOutcome.Empty) == 0)
                        {
                            ld->Max.Update(g->TilesPerSide, tile, cells.Summary->Max);
                        }
                        else
                        {
                            DropPage(ld, tile);
                            ld->Max.Update(g->TilesPerSide, tile, 0);
                        }

                        if ((outcome & (ResolveOutcome.Empty | ResolveOutcome.Changed)) != 0)
                            RefreshTileHash(g, ld, tile);
                    }

                    if ((outcome & ResolveOutcome.Changed) != 0)
                    {
                        dirty[kept] = tile;
                        rects[kept] = rect;
                        kept++;
                    }
                }

                ld->Dirty.Resize(kept);
            }
        }

        for (var d = 0; d < w->LayerCount; d++)
        {
            if (w->Recipes[d].Op == LayerOp.Base) continue;

            for (var gi = 0; gi < w->GridCount; gi++) w->Grids[gi].Layers[d].Rects.Resize(0);
        }
    }

    [MethodImpl(Jit.Hot)]
    private static void MarkLive(GridCtx* g, byte layer, LayerData* source)
    {
        if (source->LivePages == 0) return;

        var target = EnsureDirty(g, layer);
        var directory = source->Directory;
        for (var tile = 0; tile < g->TileCount; tile++)
            if (directory[tile] >= 0)
                MarkDirty(g, target, tile);
    }

    [MethodImpl(Jit.Hot)]
    private static void MarkAll(GridCtx* g, LayerData* target, LayerData* source)
    {
        var tiles = source->Dirty.Pointer;
        var count = source->Dirty.Length;
        for (var i = 0; i < count; i++) MarkDirty(g, target, tiles[i]);
    }

    [MethodImpl(Jit.Inline)]
    private static short CombineCell(LayerRecipe* r, int a, int b)
    {
        switch (r->Op)
        {
            case LayerOp.Sum:
            {
                var v = r->WeightA * a + r->WeightB * b;
                if (r->Shift != 0) v = (v + (1 << (r->Shift - 1)) + (v >> 31)) >> r->Shift;
                return (short)Math.Clamp(v, short.MinValue, short.MaxValue);
            }
            case LayerOp.Min:
                return (short)Math.Min(a, b);
            case LayerOp.Max:
                return (short)Math.Max(a, b);
            default:
                return b >= r->Low && b <= r->High ? (short)a : (short)0;
        }
    }

    [MethodImpl(Jit.Hot)]
    private static ResolveOutcome CombineRows(LayerRecipe* r, short* inputA, short* inputB, PlaneCells cells, int y0,
        int y1)
    {
        var a = inputA == null ? (short*)Runtime.ZeroDense : inputA;
        var b = inputB == null ? (short*)Runtime.ZeroDense : inputB;
#if NET
        if (Vector256.IsHardwareAccelerated) return CombineRows256(r, a, b, cells, y0, y1);
        if (Vector128.IsHardwareAccelerated) return CombineRows128(r, a, b, cells, y0, y1);
#endif
        return CombineRowsScalar(r, a, b, cells, y0, y1);
    }

    [MethodImpl(Jit.Hot)]
    private static ResolveOutcome CombineRowsScalar(LayerRecipe* r, short* a, short* b, PlaneCells cells, int y0,
        int y1)
    {
        var summary = cells.Summary;
        var sum = summary->Sum;
        var changed = false;
        for (var y = y0; y < y1; y++)
        {
            var rowA = a + y * TileBake.TileSize;
            var rowB = b + y * TileBake.TileSize;
            var row = cells.Page + y * TileBake.TileSize;
            var rowSum = 0;
            var rowMax = short.MinValue;
            for (var x = 0; x < TileBake.TileSize; x++)
            {
                var cell = CombineCell(r, rowA[x], rowB[x]);
                if (cell != row[x]) changed = true;
                row[x] = cell;
                rowSum += cell;
                if (cell > rowMax) rowMax = cell;
            }

            sum += rowSum - summary->RowSums[y];
            summary->RowSums[y] = rowSum;
            summary->RowMaxima[y] = rowMax;
        }

        summary->Sum = sum;
        summary->Max = RowsMaximumScalar(summary->RowMaxima);
        return TileBake.Outcome(TileBake.Occupied(summary), changed);
    }

#if NET
    [MethodImpl(Jit.Hot)]
    private static ResolveOutcome CombineRows256(LayerRecipe* r, short* a, short* b, PlaneCells cells, int y0, int y1)
    {
        var summary = cells.Summary;
        var differs = Vector256<short>.Zero;
        var wa = Vector256.Create(r->WeightA);
        var wb = Vector256.Create(r->WeightB);
        var shift = r->Shift;
        var bias = Vector256.Create(shift == 0 ? 0 : 1 << (shift - 1));
        var floor = Vector256.Create((int)short.MinValue);
        var ceiling = Vector256.Create((int)short.MaxValue);
        var low = Vector256.Create(r->Low);
        var high = Vector256.Create(r->High);
        var op = r->Op;
        var sum = summary->Sum;
        for (var y = y0; y < y1; y++)
        {
            var rowA = a + y * TileBake.TileSize;
            var rowB = b + y * TileBake.TileSize;
            var row = cells.Page + y * TileBake.TileSize;
            var first = CombineBlock(op, Vector256.Load(rowA), Vector256.Load(rowB), wa, wb, shift, bias, floor,
                ceiling, low, high);
            var second = CombineBlock(op, Vector256.Load(rowA + 16), Vector256.Load(rowB + 16), wa, wb, shift, bias,
                floor, ceiling, low, high);
            differs |= ~Vector256.Equals(Vector256.Load(row), first);
            differs |= ~Vector256.Equals(Vector256.Load(row + 16), second);
            first.Store(row);
            second.Store(row + 16);
            var (f0, f1) = Vector256.Widen(first);
            var (s0, s1) = Vector256.Widen(second);
            var rowSum = Vector256.Sum(f0 + f1 + (s0 + s1));
            sum += rowSum - summary->RowSums[y];
            summary->RowSums[y] = rowSum;
            summary->RowMaxima[y] = RowMax(Vector256.Max(first, second));
        }

        summary->Sum = sum;
        summary->Max = RowsMaximum(summary->RowMaxima);
        return TileBake.Outcome(TileBake.Occupied(summary), !Vector256.EqualsAll(differs, Vector256<short>.Zero));
    }

    [MethodImpl(Jit.Hot)]
    private static ResolveOutcome CombineRows128(LayerRecipe* r, short* a, short* b, PlaneCells cells, int y0, int y1)
    {
        var summary = cells.Summary;
        var differs = Vector128<short>.Zero;
        var wa = Vector128.Create(r->WeightA);
        var wb = Vector128.Create(r->WeightB);
        var shift = r->Shift;
        var bias = Vector128.Create(shift == 0 ? 0 : 1 << (shift - 1));
        var floor = Vector128.Create((int)short.MinValue);
        var ceiling = Vector128.Create((int)short.MaxValue);
        var low = Vector128.Create(r->Low);
        var high = Vector128.Create(r->High);
        var op = r->Op;
        var sum = summary->Sum;
        for (var y = y0; y < y1; y++)
        {
            var rowA = a + y * TileBake.TileSize;
            var rowB = b + y * TileBake.TileSize;
            var row = cells.Page + y * TileBake.TileSize;
            var first = CombineBlock(op, Vector128.Load(rowA), Vector128.Load(rowB), wa, wb, shift, bias, floor,
                ceiling, low, high);
            var second = CombineBlock(op, Vector128.Load(rowA + 8), Vector128.Load(rowB + 8), wa, wb, shift, bias,
                floor, ceiling, low, high);
            var third = CombineBlock(op, Vector128.Load(rowA + 16), Vector128.Load(rowB + 16), wa, wb, shift, bias,
                floor, ceiling, low, high);
            var fourth = CombineBlock(op, Vector128.Load(rowA + 24), Vector128.Load(rowB + 24), wa, wb, shift, bias,
                floor, ceiling, low, high);
            differs |= ~Vector128.Equals(Vector128.Load(row), first);
            differs |= ~Vector128.Equals(Vector128.Load(row + 8), second);
            differs |= ~Vector128.Equals(Vector128.Load(row + 16), third);
            differs |= ~Vector128.Equals(Vector128.Load(row + 24), fourth);
            first.Store(row);
            second.Store(row + 8);
            third.Store(row + 16);
            fourth.Store(row + 24);
            var (f0, f1) = Vector128.Widen(first);
            var (s0, s1) = Vector128.Widen(second);
            var (t0, t1) = Vector128.Widen(third);
            var (u0, u1) = Vector128.Widen(fourth);
            var rowSum = Vector128.Sum(f0 + f1 + (s0 + s1) + (t0 + t1) + (u0 + u1));
            sum += rowSum - summary->RowSums[y];
            summary->RowSums[y] = rowSum;
            var widest = Vector128.Max(Vector128.Max(first, second), Vector128.Max(third, fourth));
            summary->RowMaxima[y] = RowMax(widest);
        }

        summary->Sum = sum;
        summary->Max = RowsMaximum(summary->RowMaxima);
        return TileBake.Outcome(TileBake.Occupied(summary), !Vector128.EqualsAll(differs, Vector128<short>.Zero));
    }

    [MethodImpl(Jit.Inline)]
    private static Vector256<short> CombineBlock(LayerOp op, Vector256<short> va, Vector256<short> vb,
        Vector256<int> wa, Vector256<int> wb, int shift, Vector256<int> bias, Vector256<int> floor,
        Vector256<int> ceiling, Vector256<short> low, Vector256<short> high)
    {
        if (op == LayerOp.Sum)
        {
            var (a0, a1) = Vector256.Widen(va);
            var (b0, b1) = Vector256.Widen(vb);
            var v0 = a0 * wa + b0 * wb;
            var v1 = a1 * wa + b1 * wb;
            if (shift != 0)
            {
                v0 = Vector256.ShiftRightArithmetic(v0 + bias + Vector256.ShiftRightArithmetic(v0, 31), shift);
                v1 = Vector256.ShiftRightArithmetic(v1 + bias + Vector256.ShiftRightArithmetic(v1, 31), shift);
            }

            return Vector256.Narrow(Vector256.Min(Vector256.Max(v0, floor), ceiling),
                Vector256.Min(Vector256.Max(v1, floor), ceiling));
        }

        if (op == LayerOp.Min) return Vector256.Min(va, vb);
        if (op == LayerOp.Max) return Vector256.Max(va, vb);
        return va & Vector256.GreaterThanOrEqual(vb, low) & Vector256.LessThanOrEqual(vb, high);
    }

    [MethodImpl(Jit.Inline)]
    private static Vector128<short> CombineBlock(LayerOp op, Vector128<short> va, Vector128<short> vb,
        Vector128<int> wa, Vector128<int> wb, int shift, Vector128<int> bias, Vector128<int> floor,
        Vector128<int> ceiling, Vector128<short> low, Vector128<short> high)
    {
        if (op == LayerOp.Sum)
        {
            var (a0, a1) = Vector128.Widen(va);
            var (b0, b1) = Vector128.Widen(vb);
            var v0 = a0 * wa + b0 * wb;
            var v1 = a1 * wa + b1 * wb;
            if (shift != 0)
            {
                v0 = Vector128.ShiftRightArithmetic(v0 + bias + Vector128.ShiftRightArithmetic(v0, 31), shift);
                v1 = Vector128.ShiftRightArithmetic(v1 + bias + Vector128.ShiftRightArithmetic(v1, 31), shift);
            }

            return Vector128.Narrow(Vector128.Min(Vector128.Max(v0, floor), ceiling),
                Vector128.Min(Vector128.Max(v1, floor), ceiling));
        }

        if (op == LayerOp.Min) return Vector128.Min(va, vb);
        if (op == LayerOp.Max) return Vector128.Max(va, vb);
        return va & Vector128.GreaterThanOrEqual(vb, low) & Vector128.LessThanOrEqual(vb, high);
    }

    [MethodImpl(Jit.Inline)]
    private static short RowsMaximum(short* maxima)
    {
        if (Vector256.IsHardwareAccelerated)
            return RowMax(Vector256.Max(Vector256.Load(maxima), Vector256.Load(maxima + 16)));

        if (Vector128.IsHardwareAccelerated)
        {
            var pair = Vector128.Max(Vector128.Load(maxima), Vector128.Load(maxima + 8));
            return RowMax(Vector128.Max(pair, Vector128.Max(Vector128.Load(maxima + 16), Vector128.Load(maxima + 24))));
        }

        return RowsMaximumScalar(maxima);
    }

    [MethodImpl(Jit.Inline)]
    private static short RowMax(Vector256<short> v)
    {
        return RowMax(Vector128.Max(v.GetLower(), v.GetUpper()));
    }

    [MethodImpl(Jit.Inline)]
    private static short RowMax(Vector128<short> v)
    {
        v = Vector128.Max(v, Vector128.Shuffle(v, Vector128.Create((short)4, 5, 6, 7, 0, 1, 2, 3)));
        v = Vector128.Max(v, Vector128.Shuffle(v, Vector128.Create((short)2, 3, 0, 1, 6, 7, 4, 5)));
        v = Vector128.Max(v, Vector128.Shuffle(v, Vector128.Create((short)1, 0, 3, 2, 5, 4, 7, 6)));
        return v.ToScalar();
    }
#endif

    [MethodImpl(Jit.Inline)]
    private static short RowsMaximumScalar(short* maxima)
    {
        var best = maxima[0];
        for (var y = 1; y < TileBake.TileSize; y++)
            if (maxima[y] > best) best = maxima[y];
        return best;
    }
}
