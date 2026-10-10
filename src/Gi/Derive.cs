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
    Mask,
}

internal struct LayerRecipe
{
    public LayerOp Op;
    public byte A;
    public byte B;
    public byte Shift;
    public int WeightA;
    public int WeightB;
    public short Low;
    public short High;
    public uint Reads;
}

public static unsafe partial class World
{
    private const int MaxWeight = short.MaxValue;
    private const int MaxShift = 15;

    internal static byte AddDerived(byte world, LayerOp op, byte a, byte b, int weightA, int weightB, int shift, short low, short high)
    {
        var w = GetContext(world);
        if (w == null) throw new ArgumentOutOfRangeException(nameof(world));
        if (a >= w->LayerCount) throw new ArgumentOutOfRangeException(nameof(a));
        if (b >= w->LayerCount) throw new ArgumentOutOfRangeException(nameof(b));
        if (weightA < -MaxWeight || weightA > MaxWeight) throw new ArgumentOutOfRangeException(nameof(weightA));
        if (weightB < -MaxWeight || weightB > MaxWeight) throw new ArgumentOutOfRangeException(nameof(weightB));
        if (shift < 0 || shift > MaxShift) throw new ArgumentOutOfRangeException(nameof(shift));
        if (low > high) throw new ArgumentOutOfRangeException(nameof(low));

        var reads = w->Recipes[a].Reads | w->Recipes[b].Reads;
        var id = AddLayer(world);
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
            Reads = reads,
        };
        w->DerivedCount++;
        return id;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsDerived(WorldCtx* w, int layer) => w->Recipes[layer].Op != LayerOp.Base;

    #if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    #endif
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
                if (la->Dirty.Length == 0 && lb->Dirty.Length == 0) continue;

                var ld = EnsureDirty(g, (byte)d);
                MarkAll(ld, la);
                if (r->B != r->A) MarkAll(ld, lb);
                var dirty = ld->Dirty.Pointer;
                var count = ld->Dirty.Length;
                for (var i = 0; i < count; i++)
                {
                    var tile = dirty[i];
                    ld->InDirty[tile] = 0;
                    var pa = la->Pages.TryGet(tile, out var ba) ? (short*)(ba + PageOffset) : null;
                    var pb = lb->Pages.TryGet(tile, out var bb) ? (short*)(bb + PageOffset) : null;
                    var exists = ld->Pages.TryGet(tile, out var block);
                    if (pa == null && pb == null)
                    {
                        if (exists)
                        {
                            ld->Pages.Remove(tile);
                            FreeBlock(block);
                        }

                        ld->Max.Update(g->TilesPerSide, tile, 0);
                        continue;
                    }

                    var target = exists ? block : NewHeader();
                    if (Combine(r, pa, pb, (short*)(target + PageOffset), (long*)(target + SumOffset), (short*)(target + MaxOffset)))
                    {
                        if (!exists) ld->Pages.Put(tile, target);
                        ld->Max.Update(g->TilesPerSide, tile, *(short*)(target + MaxOffset));
                        continue;
                    }

                    if (exists) ld->Pages.Remove(tile);
                    FreeBlock(target);
                    ld->Max.Update(g->TilesPerSide, tile, 0);
                }
            }
        }
    }

    private static void MarkAll(LayerData* target, LayerData* source)
    {
        var tiles = source->Dirty.Pointer;
        var count = source->Dirty.Length;
        for (var i = 0; i < count; i++) MarkDirty(target, tiles[i]);
    }

    private static byte* NewHeader()
    {
        var block = (byte*)NativeHeap.AlignedAlloc(HeaderBytes);
        new Span<byte>(block + SumOffset, HeaderBytes - SumOffset).Clear();
        return block;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
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

    #if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    #endif
    private static bool Combine(LayerRecipe* r, short* inputA, short* inputB, short* output, long* pageSum, short* pageMax)
    {
        var a = inputA == null ? (short*)Runtime.ZeroDense : inputA;
        var b = inputB == null ? (short*)Runtime.ZeroDense : inputB;
#if NET
        if (Vector256.IsHardwareAccelerated) return Combine256(r, a, b, output, pageSum, pageMax);
        if (Vector128.IsHardwareAccelerated) return Combine128(r, a, b, output, pageSum, pageMax);
#endif
        var any = false;
        var sum = 0L;
        var max = short.MinValue;
        for (var i = 0; i < Cells; i++)
        {
            var cell = CombineCell(r, a[i], b[i]);
            output[i] = cell;
            any |= cell != 0;
            sum += cell;
            if (cell > max) max = cell;
        }

        *pageSum = sum;
        *pageMax = max;
        return any;
    }

#if NET
    #if NET
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    #endif
    private static bool Combine256(LayerRecipe* r, short* a, short* b, short* output, long* pageSum, short* pageMax)
    {
        var any = Vector256<short>.Zero;
        var top = Vector256.Create(short.MinValue);
        var acc = Vector256<int>.Zero;
        var wa = Vector256.Create(r->WeightA);
        var wb = Vector256.Create(r->WeightB);
        var shift = r->Shift;
        var bias = Vector256.Create(shift == 0 ? 0 : 1 << (shift - 1));
        var floor = Vector256.Create((int)short.MinValue);
        var ceiling = Vector256.Create((int)short.MaxValue);
        var low = Vector256.Create(r->Low);
        var high = Vector256.Create(r->High);
        var op = r->Op;
        for (var i = 0; i < Cells; i += 16)
        {
            var va = Vector256.Load(a + i);
            var vb = Vector256.Load(b + i);
            Vector256<short> cells;
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

                cells = Vector256.Narrow(Vector256.Min(Vector256.Max(v0, floor), ceiling), Vector256.Min(Vector256.Max(v1, floor), ceiling));
            }
            else if (op == LayerOp.Min)
            {
                cells = Vector256.Min(va, vb);
            }
            else if (op == LayerOp.Max)
            {
                cells = Vector256.Max(va, vb);
            }
            else
            {
                cells = va & Vector256.GreaterThanOrEqual(vb, low) & Vector256.LessThanOrEqual(vb, high);
            }

            cells.Store(output + i);
            any |= cells;
            top = Vector256.Max(top, cells);
            var (s0, s1) = Vector256.Widen(cells);
            acc += s0 + s1;
        }

        *pageSum = Vector256.Sum(acc);
        var max = short.MinValue;
        for (var k = 0; k < Vector256<short>.Count; k++)
            if (top.GetElement(k) > max) max = top.GetElement(k);
        *pageMax = max;
        return !Vector256.EqualsAll(any, Vector256<short>.Zero);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool Combine128(LayerRecipe* r, short* a, short* b, short* output, long* pageSum, short* pageMax)
    {
        var any = Vector128<short>.Zero;
        var top = Vector128.Create(short.MinValue);
        var acc = Vector128<int>.Zero;
        var wa = Vector128.Create(r->WeightA);
        var wb = Vector128.Create(r->WeightB);
        var shift = r->Shift;
        var bias = Vector128.Create(shift == 0 ? 0 : 1 << (shift - 1));
        var floor = Vector128.Create((int)short.MinValue);
        var ceiling = Vector128.Create((int)short.MaxValue);
        var low = Vector128.Create(r->Low);
        var high = Vector128.Create(r->High);
        var op = r->Op;
        for (var i = 0; i < Cells; i += 8)
        {
            var va = Vector128.Load(a + i);
            var vb = Vector128.Load(b + i);
            Vector128<short> cells;
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

                cells = Vector128.Narrow(Vector128.Min(Vector128.Max(v0, floor), ceiling), Vector128.Min(Vector128.Max(v1, floor), ceiling));
            }
            else if (op == LayerOp.Min)
            {
                cells = Vector128.Min(va, vb);
            }
            else if (op == LayerOp.Max)
            {
                cells = Vector128.Max(va, vb);
            }
            else
            {
                cells = va & Vector128.GreaterThanOrEqual(vb, low) & Vector128.LessThanOrEqual(vb, high);
            }

            cells.Store(output + i);
            any |= cells;
            top = Vector128.Max(top, cells);
            var (s0, s1) = Vector128.Widen(cells);
            acc += s0 + s1;
        }

        *pageSum = Vector128.Sum(acc);
        var max = short.MinValue;
        for (var k = 0; k < Vector128<short>.Count; k++)
            if (top.GetElement(k) > max) max = top.GetElement(k);
        *pageMax = max;
        return !Vector128.EqualsAll(any, Vector128<short>.Zero);
    }
#endif
}
