using System.Runtime.CompilerServices;
#if NET
using System.Numerics;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
#endif

namespace Gi;

internal unsafe struct FusedClass
{
    public int* Directory;
    public int Value;
    public int OffsetX;
    public int OffsetY;
    public int Width;
    public int Height;
    public byte Stamp;
}

internal unsafe struct FusedPlan
{
    public FusedClass* Classes;
    public int ClassCount;
    public int* Region;
    public int* ZeroPage;
    public int TilesPerSide;
    public int Tick;
}

internal unsafe struct FusedTile
{
    public int Tile;
    public short* Page;
    public TileSummary* Summary;
    public ResolveOutcome Outcome;
    public byte Fallback;
    public short Max;
}

internal static unsafe class FusedBox
{
    internal const int Edge = 8;
    internal const int StageStride = Edge + TileBake.TileSize + Edge;

    private const int Size = TileBake.TileSize;
    private const int Cells = Size * Size;
    private const long Limit = short.MaxValue - 1;

    [MethodImpl(Jit.Inline)]
    internal static bool Supports(in FusedClass c)
    {
        return c.OffsetX + c.Width - 1 <= Edge && -c.OffsetX <= Edge;
    }

    [MethodImpl(Jit.Hot)]
    internal static void Run(FusedPlan* plan, FusedTile* task)
    {
        var stage = stackalloc int[Size * StageStride];
        var accumulator = stackalloc int[Cells];
        var pages = stackalloc int*[9];
        var tps = plan->TilesPerSide;
        var tx = task->Tile % tps;
        var ty = task->Tile / tps;
        long bound = 0;
        for (var c = 0; c < plan->ClassCount; c++)
        {
            var cl = plan->Classes + c;
            Locate(plan, cl->Directory, tx, ty, pages);
            var peak = Vertical(pages, stage, cl->OffsetY, cl->Height);
            bound += (long)peak * cl->Width * Math.Abs((long)cl->Value);
            if (bound > Limit)
            {
                task->Fallback = 1;
                return;
            }

            Horizontal(stage, accumulator, cl->OffsetX, cl->Width, cl->Value, c == 0);
        }

        Settle(accumulator, task, plan->Tick);
    }

    [MethodImpl(Jit.Hot)]
    private static void Locate(FusedPlan* plan, int* directory, int tx, int ty, int** pages)
    {
        var tps = plan->TilesPerSide;
        for (var row = 0; row < 3; row++)
        for (var column = 0; column < 3; column++)
        {
            var x = tx + column - 1;
            var y = ty + row - 1;
            var entry = (uint)x < (uint)tps && (uint)y < (uint)tps ? directory[y * tps + x] : 0;
            pages[row * 3 + column] = entry == 0 ? plan->ZeroPage : plan->Region + (entry - 1) * HistState.PageInts;
        }
    }

    [MethodImpl(Jit.Inline)]
    private static int* RowOf(int** pages, int column, int q)
    {
        return pages[((q >> TileBake.TileBits) + 1) * 3 + column] + (q & (Size - 1)) * Size;
    }

    [MethodImpl(Jit.Hot)]
    private static int Vertical(int** pages, int* stage, int offsetY, int height)
    {
        var first = -offsetY - height + 1;
#if NET
        if (Avx2.IsSupported) return VerticalWide(pages, stage, first, height);
#endif
        return VerticalScalar(pages, stage, first, height);
    }

#if NET
    [MethodImpl(Jit.Hot)]
    private static int VerticalWide(int** pages, int* stage, int first, int height)
    {
        var peak = Vector256<int>.Zero;
        var l = Vector256<int>.Zero;
        var c0 = Vector256<int>.Zero;
        var c1 = Vector256<int>.Zero;
        var c2 = Vector256<int>.Zero;
        var c3 = Vector256<int>.Zero;
        var r = Vector256<int>.Zero;
        for (var j = 0; j < height; j++)
        {
            var left = RowOf(pages, 0, first + j) + Size - Edge;
            var center = RowOf(pages, 1, first + j);
            var right = RowOf(pages, 2, first + j);
            l += Vector256.Load(left);
            c0 += Vector256.Load(center);
            c1 += Vector256.Load(center + 8);
            c2 += Vector256.Load(center + 16);
            c3 += Vector256.Load(center + 24);
            r += Vector256.Load(right);
        }

        for (var y = 0;;)
        {
            var row = stage + y * StageStride;
            l.Store(row);
            c0.Store(row + Edge);
            c1.Store(row + Edge + 8);
            c2.Store(row + Edge + 16);
            c3.Store(row + Edge + 24);
            r.Store(row + Edge + Size);
            peak = Vector256.Max(peak, Vector256.Max(Vector256.Max(Vector256.Abs(l), Vector256.Abs(r)),
                Vector256.Max(Vector256.Max(Vector256.Abs(c0), Vector256.Abs(c1)),
                    Vector256.Max(Vector256.Abs(c2), Vector256.Abs(c3)))));
            if (++y == Size) break;

            var add = first + y + height - 1;
            var drop = first + y - 1;
            var addLeft = RowOf(pages, 0, add) + Size - Edge;
            var dropLeft = RowOf(pages, 0, drop) + Size - Edge;
            var addCenter = RowOf(pages, 1, add);
            var dropCenter = RowOf(pages, 1, drop);
            var addRight = RowOf(pages, 2, add);
            var dropRight = RowOf(pages, 2, drop);
            l += Vector256.Load(addLeft) - Vector256.Load(dropLeft);
            c0 += Vector256.Load(addCenter) - Vector256.Load(dropCenter);
            c1 += Vector256.Load(addCenter + 8) - Vector256.Load(dropCenter + 8);
            c2 += Vector256.Load(addCenter + 16) - Vector256.Load(dropCenter + 16);
            c3 += Vector256.Load(addCenter + 24) - Vector256.Load(dropCenter + 24);
            r += Vector256.Load(addRight) - Vector256.Load(dropRight);
        }

        var best = 0;
        for (var lane = 0; lane < 8; lane++) best = Math.Max(best, peak.GetElement(lane));
        return best;
    }
#endif

    [MethodImpl(Jit.Hot)]
    private static int VerticalScalar(int** pages, int* stage, int first, int height)
    {
        var peak = 0;
        for (var slot = 0; slot < StageStride; slot++)
        {
            var column = slot - Edge;
            var page = column < 0 ? 0 : column < Size ? 1 : 2;
            var offset = column & (Size - 1);
            var sum = 0;
            for (var j = 0; j < height; j++) sum += RowOf(pages, page, first + j)[offset];
            for (var y = 0;;)
            {
                stage[y * StageStride + slot] = sum;
                var magnitude = sum < 0 ? -sum : sum;
                if (magnitude > peak) peak = magnitude;
                if (++y == Size) break;

                sum += RowOf(pages, page, first + y + height - 1)[offset] - RowOf(pages, page, first + y - 1)[offset];
            }
        }

        return peak;
    }

    [MethodImpl(Jit.Hot)]
    private static void Horizontal(int* stage, int* accumulator, int offsetX, int width, int value, bool initial)
    {
        var start = Edge - offsetX - width + 1;
#if NET
        if (Avx2.IsSupported)
        {
            var scale = Vector256.Create(value);
            for (var y = 0; y < Size; y++)
            {
                var source = stage + y * StageStride + start;
                var target = accumulator + y * Size;
                for (var block = 0; block < Size; block += 8)
                {
                    var sum = Vector256.Load(source + block);
                    for (var k = 1; k < width; k++) sum += Vector256.Load(source + block + k);
                    sum *= scale;
                    if (!initial) sum += Vector256.Load(target + block);
                    sum.Store(target + block);
                }
            }

            return;
        }
#endif
        for (var y = 0; y < Size; y++)
        {
            var source = stage + y * StageStride + start;
            var target = accumulator + y * Size;
            var sum = 0;
            for (var k = 0; k < width; k++) sum += source[k];
            for (var x = 0;;)
            {
                target[x] = initial ? sum * value : target[x] + sum * value;
                if (++x == Size) break;

                sum += source[x + width - 1] - source[x - 1];
            }
        }
    }

    [MethodImpl(Jit.Hot)]
    private static void Settle(int* accumulator, FusedTile* task, int tick)
    {
        var page = task->Page;
        var summary = task->Summary;
        bool any;
        bool changed;
#if NET
        if (Avx2.IsSupported)
            (any, changed) = SettleWide(accumulator, page, summary);
        else
#endif
            (any, changed) = SettleScalar(accumulator, page, summary);

        long total = 0;
        var best = summary->RowMaxima[0];
        var row = 0;
        for (var y = 0; y < Size; y++)
        {
            total += summary->RowSums[y];
            if (summary->RowMaxima[y] > best)
            {
                best = summary->RowMaxima[y];
                row = y;
            }
        }

        var column = 0;
        while (page[row * Size + column] != best) column++;
        summary->Sum = total;
        summary->Max = best;
        summary->ArgMaxX = (short)column;
        summary->ArgMaxY = (short)row;
        if (changed) summary->Epoch = tick;
        task->Max = best;
        task->Outcome = TileBake.Outcome(any, changed);
    }

#if NET
    [MethodImpl(Jit.Hot)]
    private static (bool Any, bool Changed) SettleWide(int* accumulator, short* page, TileSummary* summary)
    {
        var ones = Vector256.Create((short)1);
        var difference = Vector256<short>.Zero;
        var seen = Vector256<short>.Zero;
        for (var y = 0; y < Size; y++)
        {
            var source = accumulator + y * Size;
            var target = page + y * Size;
            var low = Avx2.Permute4x64(
                Avx2.PackSignedSaturate(Vector256.Load(source), Vector256.Load(source + 8)).AsInt64(), 0xd8).AsInt16();
            var high = Avx2.Permute4x64(
                Avx2.PackSignedSaturate(Vector256.Load(source + 16), Vector256.Load(source + 24)).AsInt64(), 0xd8).AsInt16();
            difference |= (low ^ Vector256.Load(target)) | (high ^ Vector256.Load(target + 16));
            seen |= low | high;
            low.Store(target);
            high.Store(target + 16);
            summary->RowSums[y] = Vector256.Sum(Avx2.MultiplyAddAdjacent(low, ones) + Avx2.MultiplyAddAdjacent(high, ones));
            var peak = Vector256.Max(low, high);
            var narrow = Vector128.Max(peak.GetLower(), peak.GetUpper());
            narrow = Vector128.Max(narrow, Vector128.Shuffle(narrow, Vector128.Create(4, 5, 6, 7, 0, 1, 2, 3)));
            narrow = Vector128.Max(narrow, Vector128.Shuffle(narrow, Vector128.Create(2, 3, 0, 1, 6, 7, 4, 5)));
            narrow = Vector128.Max(narrow, Vector128.Shuffle(narrow, Vector128.Create(1, 0, 3, 2, 5, 4, 7, 6)));
            summary->RowMaxima[y] = narrow.ToScalar();
        }

        return (seen != Vector256<short>.Zero, difference != Vector256<short>.Zero);
    }
#endif

    [MethodImpl(Jit.Hot)]
    private static (bool Any, bool Changed) SettleScalar(int* accumulator, short* page, TileSummary* summary)
    {
        var any = false;
        var changed = false;
        for (var y = 0; y < Size; y++)
        {
            var sum = 0;
            var best = short.MinValue;
            for (var x = 0; x < Size; x++)
            {
                var cell = (short)Math.Clamp(accumulator[y * Size + x], short.MinValue, short.MaxValue);
                if (cell != page[y * Size + x]) changed = true;
                if (cell != 0) any = true;
                page[y * Size + x] = cell;
                sum += cell;
                if (cell > best) best = cell;
            }

            summary->RowSums[y] = sum;
            summary->RowMaxima[y] = best;
        }

        return (any, changed);
    }
}
