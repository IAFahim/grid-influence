using System.Runtime.CompilerServices;
#if NET
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
    public ulong Hash;
    public byte Fallback;
    public short Max;
}

internal static unsafe class FusedBox
{
    internal const int Edge = 8;
    internal const int StageStride = Edge + TileBake.TileSize + Edge;

    private const int Size = TileBake.TileSize;
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
        var pages = stackalloc int*[9];
        var tps = plan->TilesPerSide;
        var tx = task->Tile % tps;
        var ty = task->Tile / tps;
        var page = task->Page;
        var count = plan->ClassCount;
        long bound = 0;
#if NET
        if (Avx2.IsSupported)
        {
            var difference = Vector256<short>.Zero;
            for (var c = 0; c < count; c++)
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

                if (c == count - 1)
                {
                    ApplyLastWide(stage, page, cl->OffsetX, cl->Width, cl->Value, c == 0, task, plan->Tick, ref difference);
                    return;
                }

                difference |= ApplyWide(stage, page, cl->OffsetX, cl->Width, cl->Value, c == 0);
            }

            return;
        }
#endif
        var scalarDifference = false;
        for (var c = 0; c < count; c++)
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

            if (c == count - 1)
            {
                ApplyLastScalar(stage, page, cl->OffsetX, cl->Width, cl->Value, c == 0, task, plan->Tick, ref scalarDifference);
                return;
            }

            scalarDifference |= ApplyScalar(stage, page, cl->OffsetX, cl->Width, cl->Value, c == 0);
        }
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

#if NET
    [MethodImpl(Jit.Hot)]
    private static Vector256<short> ApplyWide(int* stage, short* page, int offsetX, int width, int value, bool initial)
    {
        var start = Edge - offsetX - width + 1;
        var scale = Vector256.Create(value);
        var difference = Vector256<short>.Zero;
        for (var y = 0; y < Size; y++)
        {
            var source = stage + y * StageStride + start;
            var target = page + y * Size;
            var a = Vector256.Load(source);
            var b = Vector256.Load(source + 8);
            var c = Vector256.Load(source + 16);
            var d = Vector256.Load(source + 24);
            for (var k = 1; k < width; k++)
            {
                a += Vector256.Load(source + k);
                b += Vector256.Load(source + 8 + k);
                c += Vector256.Load(source + 16 + k);
                d += Vector256.Load(source + 24 + k);
            }

            var low = Avx2.Permute4x64(
                Avx2.PackSignedSaturate(a * scale, b * scale).AsInt64(), 0xd8).AsInt16();
            var high = Avx2.Permute4x64(
                Avx2.PackSignedSaturate(c * scale, d * scale).AsInt64(), 0xd8).AsInt16();
            if (initial)
            {
                difference |= (low ^ Vector256.Load(target)) | (high ^ Vector256.Load(target + 16));
                low.Store(target);
                high.Store(target + 16);
            }
            else
            {
                var priorLow = Vector256.Load(target);
                var priorHigh = Vector256.Load(target + 16);
                low += priorLow;
                high += priorHigh;
                difference |= (priorLow ^ low) | (priorHigh ^ high);
                low.Store(target);
                high.Store(target + 16);
            }
        }

        return difference;
    }

    [MethodImpl(Jit.Hot)]
    private static void ApplyLastWide(int* stage, short* page, int offsetX, int width, int value, bool initial,
        FusedTile* task, int tick, ref Vector256<short> difference)
    {
        var start = Edge - offsetX - width + 1;
        var scale = Vector256.Create(value);
        var ones = Vector256.Create((short)1);
        var seen = Vector256<short>.Zero;
        var hash = (TileHash.Offset ^ (ulong)task->Tile) * TileHash.Prime;
        var summary = task->Summary;
        for (var y = 0; y < Size; y++)
        {
            var source = stage + y * StageStride + start;
            var target = page + y * Size;
            var a = Vector256.Load(source);
            var b = Vector256.Load(source + 8);
            var c = Vector256.Load(source + 16);
            var d = Vector256.Load(source + 24);
            for (var k = 1; k < width; k++)
            {
                a += Vector256.Load(source + k);
                b += Vector256.Load(source + 8 + k);
                c += Vector256.Load(source + 16 + k);
                d += Vector256.Load(source + 24 + k);
            }

            var low = Avx2.Permute4x64(
                Avx2.PackSignedSaturate(a * scale, b * scale).AsInt64(), 0xd8).AsInt16();
            var high = Avx2.Permute4x64(
                Avx2.PackSignedSaturate(c * scale, d * scale).AsInt64(), 0xd8).AsInt16();
            if (initial)
            {
                difference |= (low ^ Vector256.Load(target)) | (high ^ Vector256.Load(target + 16));
            }
            else
            {
                var priorLow = Vector256.Load(target);
                var priorHigh = Vector256.Load(target + 16);
                low += priorLow;
                high += priorHigh;
                difference |= (priorLow ^ low) | (priorHigh ^ high);
            }

            low.Store(target);
            high.Store(target + 16);
            seen |= low | high;
            var words = (ulong*)target;
            for (var word = 0; word < 8; word++) hash = (hash ^ words[word]) * TileHash.Prime;

            summary->RowSums[y] = Vector256.Sum(Avx2.MultiplyAddAdjacent(low, ones) + Avx2.MultiplyAddAdjacent(high, ones));
            var peak = Vector256.Max(low, high);
            var narrow = Vector128.Max(peak.GetLower(), peak.GetUpper());
            narrow = Vector128.Max(narrow, Vector128.Shuffle(narrow, Vector128.Create(4, 5, 6, 7, 0, 1, 2, 3)));
            narrow = Vector128.Max(narrow, Vector128.Shuffle(narrow, Vector128.Create(2, 3, 0, 1, 6, 7, 4, 5)));
            narrow = Vector128.Max(narrow, Vector128.Shuffle(narrow, Vector128.Create(1, 0, 3, 2, 5, 4, 7, 6)));
            summary->RowMaxima[y] = narrow.ToScalar();
        }

        FinishTile(page, summary, task, tick, hash, seen != Vector256<short>.Zero, difference != Vector256<short>.Zero);
    }
#endif

    [MethodImpl(Jit.Hot)]
    private static bool ApplyScalar(int* stage, short* page, int offsetX, int width, int value, bool initial)
    {
        var start = Edge - offsetX - width + 1;
        var changed = false;
        for (var y = 0; y < Size; y++)
        {
            var source = stage + y * StageStride + start;
            var target = page + y * Size;
            var sum = 0;
            for (var k = 0; k < width; k++) sum += source[k];
            for (var x = 0;;)
            {
                var cell = (short)Math.Clamp(sum * value, short.MinValue, short.MaxValue);
                if (initial)
                {
                    changed |= target[x] != cell;
                    target[x] = cell;
                }
                else
                {
                    var prior = target[x];
                    var merged = (short)(prior + cell);
                    changed |= prior != merged;
                    target[x] = merged;
                }

                if (++x == Size) break;

                sum += source[x + width - 1] - source[x - 1];
            }
        }

        return changed;
    }

    [MethodImpl(Jit.Hot)]
    private static void ApplyLastScalar(int* stage, short* page, int offsetX, int width, int value, bool initial,
        FusedTile* task, int tick, ref bool changed)
    {
        var start = Edge - offsetX - width + 1;
        var any = false;
        var hash = (TileHash.Offset ^ (ulong)task->Tile) * TileHash.Prime;
        var summary = task->Summary;
        for (var y = 0; y < Size; y++)
        {
            var source = stage + y * StageStride + start;
            var target = page + y * Size;
            var window = 0;
            var rowSum = 0;
            var best = short.MinValue;
            for (var k = 0; k < width; k++) window += source[k];
            for (var x = 0;;)
            {
                var cell = (short)Math.Clamp(window * value, short.MinValue, short.MaxValue);
                if (initial)
                {
                    changed |= target[x] != cell;
                    target[x] = cell;
                }
                else
                {
                    var prior = target[x];
                    var merged = (short)(prior + cell);
                    changed |= prior != merged;
                    target[x] = merged;
                }

                var final = target[x];
                if (final != 0) any = true;
                rowSum += final;
                if (final > best) best = final;
                if ((x & 3) == 3) hash = (hash ^ *(ulong*)(target + x - 3)) * TileHash.Prime;
                if (++x == Size) break;

                window += source[x + width - 1] - source[x - 1];
            }

            summary->RowSums[y] = rowSum;
            summary->RowMaxima[y] = best;
        }

        FinishTile(page, summary, task, tick, hash, any, changed);
    }

    [MethodImpl(Jit.Hot)]
    private static void FinishTile(short* page, TileSummary* summary, FusedTile* task, int tick, ulong hash,
        bool any, bool changed)
    {
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
        task->Hash = any ? hash : 0;
        task->Max = best;
        task->Outcome = TileBake.Outcome(any, changed);
    }
}
