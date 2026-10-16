using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

internal unsafe struct FloorPublishPlanes
{
    public int* Exact;
    public long* Tent;
    public long* Bell;
    public short* Old;
    public short* New;
    public long* Sum;
    public short* Max;
    public int* Argmax;
    public int* RowSum;
    public short* RowMax;
    public byte* Changed;
}

internal static unsafe class FloorPublishKernel
{
    public const int Rows = FloorShape.TileSize;
    public const int RowCells = FloorShape.TileSize;
    public const int TileCells = FloorShape.TileCells;
    private const long Half = 1L << 39;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int RoundQ40(long value) => (int)((value + Half + (value >> 63)) >> 40);

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void Scalar(in FloorPublishPlanes p, int tiles)
    {
        for (var tile = 0; tile < tiles; tile++)
        {
            var changed = false;
            var sum = 0L;
            var best = short.MinValue;
            var bestAt = 0;
            for (var r = 0; r < Rows; r++)
            {
                var rowSum = 0;
                var rowMax = short.MinValue;
                for (var c = 0; c < RowCells; c++)
                {
                    var n = tile * TileCells + r * RowCells + c;
                    var total = (long)p.Exact[n] + RoundQ40(p.Tent[n]) + RoundQ40(p.Bell[n]);
                    var cell = (short)Math.Clamp(total, short.MinValue, short.MaxValue);
                    changed |= cell != p.Old[n];
                    p.New[n] = cell;
                    rowSum += cell;
                    if (cell > rowMax) rowMax = cell;
                    if (cell > best)
                    {
                        best = cell;
                        bestAt = r * RowCells + c;
                    }
                }

                p.RowSum[tile * Rows + r] = rowSum;
                p.RowMax[tile * Rows + r] = rowMax;
                sum += rowSum;
            }

            p.Sum[tile] = sum;
            p.Max[tile] = best;
            p.Argmax[tile] = bestAt;
            p.Changed[tile] = changed ? (byte)1 : (byte)0;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void Vector(in FloorPublishPlanes p, int tiles)
    {
        for (var tile = 0; tile < tiles; tile++)
        {
            var at = tile * TileCells;
            var changed = Vector256<short>.Zero;
            var sums = Vector256<int>.Zero;
            for (var group = 0; group < Rows; group += 8)
            {
                var rowBase = tile * Rows + group;
                var cellBase = at + group * RowCells;
                var m0 = Row(p, cellBase, ref changed, p.RowMax + rowBase);
                var m1 = Row(p, cellBase + RowCells, ref changed, p.RowMax + rowBase + 1);
                var m2 = Row(p, cellBase + 2 * RowCells, ref changed, p.RowMax + rowBase + 2);
                var m3 = Row(p, cellBase + 3 * RowCells, ref changed, p.RowMax + rowBase + 3);
                var m4 = Row(p, cellBase + 4 * RowCells, ref changed, p.RowMax + rowBase + 4);
                var m5 = Row(p, cellBase + 5 * RowCells, ref changed, p.RowMax + rowBase + 5);
                var m6 = Row(p, cellBase + 6 * RowCells, ref changed, p.RowMax + rowBase + 6);
                var m7 = Row(p, cellBase + 7 * RowCells, ref changed, p.RowMax + rowBase + 7);
                var q0 = Avx2.HorizontalAdd(Avx2.HorizontalAdd(m0, m1), Avx2.HorizontalAdd(m2, m3));
                var q1 = Avx2.HorizontalAdd(Avx2.HorizontalAdd(m4, m5), Avx2.HorizontalAdd(m6, m7));
                var rows = Avx2.Permute2x128(q0, q1, 0x20) + Avx2.Permute2x128(q0, q1, 0x31);
                rows.Store(p.RowSum + rowBase);
                sums += rows;
            }

            p.Sum[tile] = Vector256.Sum(sums);
            p.Changed[tile] = Vector256.EqualsAll(changed, Vector256<short>.Zero) ? (byte)0 : (byte)1;
            var lowRows = Vector256.Load(p.RowMax + tile * Rows);
            var highRows = Vector256.Load(p.RowMax + tile * Rows + 16);
            var best = MaxOf(Avx2.Max(lowRows, highRows));
            var target = Vector256.Create(best);
            var rowMask = Vector256.Equals(lowRows, target).ExtractMostSignificantBits()
                          | (Vector256.Equals(highRows, target).ExtractMostSignificantBits() << 16);
            var row = BitOperations.TrailingZeroCount(rowMask);
            var cells = p.New + at + row * RowCells;
            var columnMask = Vector256.Equals(Vector256.Load(cells), target).ExtractMostSignificantBits()
                             | (Vector256.Equals(Vector256.Load(cells + 16), target).ExtractMostSignificantBits() << 16);
            p.Max[tile] = best;
            p.Argmax[tile] = row * RowCells + BitOperations.TrailingZeroCount(columnMask);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> Row(in FloorPublishPlanes p, int offset, ref Vector256<short> changed, short* rowMax)
    {
        var a = Total(p, offset);
        var b = Total(p, offset + 8);
        var c = Total(p, offset + 16);
        var d = Total(p, offset + 24);
        var low = Avx2.Permute4x64(Avx2.PackSignedSaturate(a, b).AsInt64(), 0xD8).AsInt16();
        var high = Avx2.Permute4x64(Avx2.PackSignedSaturate(c, d).AsInt64(), 0xD8).AsInt16();
        changed |= (low ^ Vector256.Load(p.Old + offset)) | (high ^ Vector256.Load(p.Old + offset + 16));
        low.Store(p.New + offset);
        high.Store(p.New + offset + 16);
        *rowMax = MaxOf(Avx2.Max(low, high));
        var ones = Vector256.Create((short)1);
        return Avx2.MultiplyAddAdjacent(low, ones) + Avx2.MultiplyAddAdjacent(high, ones);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> Total(in FloorPublishPlanes p, int offset)
    {
        var exact = Vector256.Min(
            Vector256.Max(Vector256.Load(p.Exact + offset), Vector256.Create(-(1 << 30))), Vector256.Create(1 << 30));
        return exact + Round(p.Tent + offset) + Round(p.Bell + offset);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> Round(long* source)
    {
        var half = Vector256.Create(Half);
        var low = Vector256.Load(source);
        var high = Vector256.Load(source + 4);
        low += half + Vector256.LessThan(low, Vector256<long>.Zero);
        high += half + Vector256.LessThan(high, Vector256<long>.Zero);
        var upper = Avx.Shuffle(low.AsSingle(), high.AsSingle(), 0xDD);
        return Avx2.ShiftRightArithmetic(Avx2.Permute4x64(upper.AsInt64(), 0xD8).AsInt32(), 8);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static short MaxOf(Vector256<short> value)
    {
        var folded = Sse2.Max(value.GetLower(), value.GetUpper());
        var inverted = Sse2.Xor(folded, Vector128.Create((short)0x7FFF)).AsUInt16();
        return (short)(Sse41.MinHorizontal(inverted).GetElement(0) ^ 0x7FFF);
    }

    public static void Run(in FloorPublishPlanes p, int tiles)
    {
        if (Avx2.IsSupported && Sse41.IsSupported) Vector(p, tiles);
        else Scalar(p, tiles);
    }

    public static void Reference(in FloorPublishPlanes p, int tiles)
    {
        for (var tile = 0; tile < tiles; tile++)
        {
            var values = new short[TileCells];
            for (var i = 0; i < TileCells; i++)
            {
                var n = tile * TileCells + i;
                var total = p.Exact[n] + Rounded(p.Tent[n]) + Rounded(p.Bell[n]);
                values[i] = (short)(total > short.MaxValue ? short.MaxValue : total < short.MinValue ? short.MinValue : total);
                p.New[n] = values[i];
            }

            var best = values.Max();
            var tileSum = 0L;
            for (var r = 0; r < Rows; r++)
            {
                var row = values.AsSpan(r * RowCells, RowCells);
                var rowSum = 0;
                foreach (var v in row) rowSum += v;
                p.RowSum[tile * Rows + r] = rowSum;
                p.RowMax[tile * Rows + r] = row.ToArray().Max();
                tileSum += rowSum;
            }

            p.Sum[tile] = tileSum;
            p.Max[tile] = best;
            p.Argmax[tile] = Array.IndexOf(values, best);
            var changed = false;
            for (var i = 0; i < TileCells; i++) changed |= values[i] != p.Old[tile * TileCells + i];
            p.Changed[tile] = changed ? (byte)1 : (byte)0;
        }
    }

    private static long Rounded(long value)
        => value >= 0 ? (value + Half) >> 40 : -((-value + Half) >> 40);
}

internal sealed unsafe class FloorPublishSuite : FloorSuite
{
    private const int Tiles = FloorShape.Tiles;
    private const int Cells = FloorShape.Cells;
    private const int BlockTiles = 32;
    private const int Blocks = Tiles / BlockTiles;

    private readonly FloorPublishPlanes _planes;
    private readonly FloorPublishPlanes _expected;
    private readonly FloorCase[] _cases;

    public FloorPublishSuite()
    {
        _planes = Allocate();
        _expected = Allocate();
        _expected.Exact = _planes.Exact;
        _expected.Tent = _planes.Tent;
        _expected.Bell = _planes.Bell;
        _expected.Old = _planes.Old;
        Fill();
        FloorPublishKernel.Reference(_expected, Tiles);
        var perturb = new FloorRandom(0x57);
        for (var tile = 0; tile < Tiles; tile++)
        {
            for (var i = 0; i < FloorShape.TileCells; i++)
                _planes.Old[tile * FloorShape.TileCells + i] = _expected.New[tile * FloorShape.TileCells + i];
            if ((tile & 1) == 1) _planes.Old[tile * FloorShape.TileCells + perturb.Below(FloorShape.TileCells)]++;
        }

        new Span<short>(_expected.New, Cells).Clear();
        FloorPublishKernel.Reference(_expected, Tiles);
        _cases =
        [
            new FloorCase("P5", "publish-1024-tiles", Cells,
                () => FloorPublishKernel.Run(_planes, Tiles),
                () =>
                {
                    FloorPublishKernel.Run(_planes, Tiles);
                    return Hash(_planes);
                }),
            new FloorCase("P5", "publish-32-tiles-x32-L2-resident", Cells,
                () =>
                {
                    for (var b = 0; b < Blocks; b++) FloorPublishKernel.Run(_planes, BlockTiles);
                },
                () =>
                {
                    Clear(_planes);
                    FloorPublishKernel.Run(_planes, BlockTiles);
                    return Hash(_planes);
                }),
        ];
    }

    public override string Id => "P5";

    public override IReadOnlyList<FloorCase> Cases => _cases;

    public override void Receipt(FloorReceipts receipts)
    {
        var changedTiles = 0;
        for (var tile = 0; tile < Tiles; tile++) changedTiles += _expected.Changed[tile];
        receipts.Check("floor-p5-reference-has-changed-and-unchanged-tiles", changedTiles == Tiles / 2);
        var saturated = 0;
        for (var i = 0; i < Cells; i++) if (_expected.New[i] == short.MaxValue || _expected.New[i] == short.MinValue) saturated++;
        receipts.Check("floor-p5-reference-saturates-both-signs", saturated > 0);

        Clear(_planes);
        FloorPublishKernel.Scalar(_planes, Tiles);
        receipts.Check("floor-p5-scalar-matches-reference", Same(_planes, _expected));
        if (Avx2.IsSupported && Sse41.IsSupported)
        {
            Clear(_planes);
            FloorPublishKernel.Vector(_planes, Tiles);
            receipts.Check("floor-p5-vector-matches-reference", Same(_planes, _expected));
        }
        else receipts.Skip("floor-p5-vector-matches-reference", "avx2 unavailable");

        Clear(_planes);
        FloorPublishKernel.Run(_planes, BlockTiles);
        var cellsInBlock = BlockTiles * FloorShape.TileCells;
        var rowsInBlock = BlockTiles * FloorShape.TileSize;
        receipts.Check("floor-p5-block-matches-leading-tiles",
            new ReadOnlySpan<short>(_planes.New, cellsInBlock).SequenceEqual(new ReadOnlySpan<short>(_expected.New, cellsInBlock))
            && new ReadOnlySpan<long>(_planes.Sum, BlockTiles).SequenceEqual(new ReadOnlySpan<long>(_expected.Sum, BlockTiles))
            && new ReadOnlySpan<short>(_planes.Max, BlockTiles).SequenceEqual(new ReadOnlySpan<short>(_expected.Max, BlockTiles))
            && new ReadOnlySpan<int>(_planes.Argmax, BlockTiles).SequenceEqual(new ReadOnlySpan<int>(_expected.Argmax, BlockTiles))
            && new ReadOnlySpan<int>(_planes.RowSum, rowsInBlock).SequenceEqual(new ReadOnlySpan<int>(_expected.RowSum, rowsInBlock))
            && new ReadOnlySpan<short>(_planes.RowMax, rowsInBlock).SequenceEqual(new ReadOnlySpan<short>(_expected.RowMax, rowsInBlock))
            && new ReadOnlySpan<byte>(_planes.Changed, BlockTiles).SequenceEqual(new ReadOnlySpan<byte>(_expected.Changed, BlockTiles)));
    }

    private FloorPublishPlanes Allocate() => new()
    {
        Exact = Arena.Alloc<int>(Cells),
        Tent = Arena.Alloc<long>(Cells),
        Bell = Arena.Alloc<long>(Cells),
        Old = Arena.Alloc<short>(Cells),
        New = Arena.Alloc<short>(Cells),
        Sum = Arena.Alloc<long>(Tiles),
        Max = Arena.Alloc<short>(Tiles),
        Argmax = Arena.Alloc<int>(Tiles),
        RowSum = Arena.Alloc<int>(Tiles * FloorShape.TileSize),
        RowMax = Arena.Alloc<short>(Tiles * FloorShape.TileSize),
        Changed = Arena.Alloc<byte>(Tiles),
    };

    private void Fill()
    {
        var rng = new FloorRandom(0x55);
        for (var tile = 0; tile < Tiles; tile++)
        {
            var plateau = tile % 7 == 3;
            var empty = tile % 11 == 5;
            for (var i = 0; i < FloorShape.TileCells; i++)
            {
                var n = tile * FloorShape.TileCells + i;
                if (empty)
                    continue;
                if (plateau)
                {
                    _planes.Exact[n] = 5000;
                    continue;
                }

                _planes.Exact[n] = Exact(ref rng);
                _planes.Tent[n] = Smooth(ref rng);
                _planes.Bell[n] = Smooth(ref rng);
            }
        }
    }

    private static int Exact(ref FloorRandom rng)
    {
        var kind = (int)(rng.Next() & 127);
        return kind switch
        {
            0 => int.MaxValue - rng.Below(1000),
            1 => int.MinValue + rng.Below(1000),
            2 => (1 << 30) + rng.Below(1 << 20),
            3 => -(1 << 30) - rng.Below(1 << 20),
            _ => rng.Below(6000) - 1500,
        };
    }

    private static long Smooth(ref FloorRandom rng)
    {
        var kind = (int)(rng.Next() & 255);
        const long limit = 1L << 62;
        return kind switch
        {
            0 => limit - rng.Below(1000),
            1 => -limit + rng.Below(1000),
            2 => ((long)(rng.Below(4000) - 2000) << 40) + (1L << 39),
            3 => -((long)rng.Below(2000) << 40) - (1L << 39),
            4 => limit,
            5 => -limit,
            _ => ((long)(rng.Below(4000) - 2000) << 40) + (long)(rng.Next() & ((1UL << 40) - 1)) - (1L << 39),
        };
    }

    private static void Clear(in FloorPublishPlanes p)
    {
        new Span<short>(p.New, Cells).Clear();
        new Span<long>(p.Sum, Tiles).Clear();
        new Span<short>(p.Max, Tiles).Clear();
        new Span<int>(p.Argmax, Tiles).Clear();
        new Span<int>(p.RowSum, Tiles * FloorShape.TileSize).Clear();
        new Span<short>(p.RowMax, Tiles * FloorShape.TileSize).Clear();
        new Span<byte>(p.Changed, Tiles).Clear();
    }

    private static bool Same(in FloorPublishPlanes a, in FloorPublishPlanes b)
        => new ReadOnlySpan<short>(a.New, Cells).SequenceEqual(new ReadOnlySpan<short>(b.New, Cells))
           && new ReadOnlySpan<long>(a.Sum, Tiles).SequenceEqual(new ReadOnlySpan<long>(b.Sum, Tiles))
           && new ReadOnlySpan<short>(a.Max, Tiles).SequenceEqual(new ReadOnlySpan<short>(b.Max, Tiles))
           && new ReadOnlySpan<int>(a.Argmax, Tiles).SequenceEqual(new ReadOnlySpan<int>(b.Argmax, Tiles))
           && new ReadOnlySpan<int>(a.RowSum, Tiles * FloorShape.TileSize).SequenceEqual(new ReadOnlySpan<int>(b.RowSum, Tiles * FloorShape.TileSize))
           && new ReadOnlySpan<short>(a.RowMax, Tiles * FloorShape.TileSize).SequenceEqual(new ReadOnlySpan<short>(b.RowMax, Tiles * FloorShape.TileSize))
           && new ReadOnlySpan<byte>(a.Changed, Tiles).SequenceEqual(new ReadOnlySpan<byte>(b.Changed, Tiles));

    private static ulong Hash(in FloorPublishPlanes p)
    {
        var h = FloorHash.Of(p.New, Cells);
        h = FloorHash.Add(h, FloorHash.Of(p.Sum, Tiles));
        h = FloorHash.Add(h, FloorHash.Of(p.Max, Tiles));
        h = FloorHash.Add(h, FloorHash.Of(p.Argmax, Tiles));
        h = FloorHash.Add(h, FloorHash.Of(p.RowSum, Tiles * FloorShape.TileSize));
        h = FloorHash.Add(h, FloorHash.Of(p.RowMax, Tiles * FloorShape.TileSize));
        return FloorHash.Add(h, FloorHash.Of(p.Changed, Tiles));
    }
}
