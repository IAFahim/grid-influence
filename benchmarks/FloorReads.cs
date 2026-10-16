using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

internal unsafe struct FloorField
{
    public short* Pages;
    public int* Directory;
    public int TilesPerSide;
    public int TileShift;
    public int Side;
    public bool Prefetch;
}

internal static unsafe class FloorReadKernel
{
    public const float PerUnit = 0.5f;
    public const int Ahead = 16;
    public const int VectorAhead = 32;
    public const int QueryPadding = 4 * Ahead + 8;

    public static void Read(in FloorField field, int* cells, int count, short* values, float* gradientX, float* gradientY)
    {
        var done = Avx2.IsSupported ? ReadVector(field, cells, count, values, gradientX, gradientY) : 0;
        ReadScalar(field, cells, done, count, values, gradientX, gradientY);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static int ReadVector(in FloorField field, int* cells, int count, short* values, float* gradientX, float* gradientY)
    {
        var directory = field.Directory;
        var bytes = (int*)field.Pages;
        var zero = Vector256<int>.Zero;
        var none = Vector256.Create(-1);
        var tileMask = Vector256.Create(1023);
        var columnMask = Vector256.Create(FloorShape.TileSize - 1);
        var last = Vector256.Create(FloorShape.TileSize - 1);
        var half = Vector256.Create(PerUnit);
        var ahead = stackalloc int[8];
        var i = 0;
        for (; i + 8 <= count; i += 8)
        {
            if (Sse.IsSupported && field.Prefetch)
            {
                var later = Vector256.Load(cells + i + VectorAhead);
                var laterPage = Avx2.GatherMaskVector256(none, directory,
                    Vector256.ShiftRightArithmetic(later, 10), Vector256.GreaterThanOrEqual(later, zero), 4);
                Vector256.Store(Vector256.ShiftLeft(laterPage, 11) + Vector256.ShiftLeft(later & tileMask, 1), ahead);
                for (var lane = 0; lane < 8; lane++) Sse.Prefetch0((byte*)bytes + ahead[lane]);
            }

            var cell = Vector256.Load(cells + i);
            var present = Vector256.GreaterThanOrEqual(cell, zero);
            var page = Avx2.GatherMaskVector256(none, directory, Vector256.ShiftRightArithmetic(cell, 10), present, 4);
            var local = cell & tileMask;
            var column = local & columnMask;
            var row = Vector256.ShiftRightLogical(local, FloorShape.TileBits);
            var interior = Vector256.GreaterThan(column, zero) & Vector256.LessThan(column, last)
                           & Vector256.GreaterThan(row, zero) & Vector256.LessThan(row, last);
            var gather = interior & Vector256.GreaterThanOrEqual(page, zero);
            var offset = Vector256.ShiftLeft(page, 11) + Vector256.ShiftLeft(local, 1);
            var west = Avx2.GatherMaskVector256(zero, bytes, offset - Vector256.Create(2), gather, 1);
            var east = Avx2.GatherMaskVector256(zero, bytes, offset, gather, 1);
            var north = Avx2.GatherMaskVector256(zero, bytes, offset - Vector256.Create(2 * FloorShape.TileSize), gather, 1);
            var south = Avx2.GatherMaskVector256(zero, bytes, offset + Vector256.Create(2 * FloorShape.TileSize), gather, 1);
            var centre = Vector256.ShiftRightArithmetic(west, 16);
            var left = Vector256.ShiftRightArithmetic(Vector256.ShiftLeft(west, 16), 16);
            var right = Vector256.ShiftRightArithmetic(east, 16);
            var up = Vector256.ShiftRightArithmetic(Vector256.ShiftLeft(north, 16), 16);
            var down = Vector256.ShiftRightArithmetic(Vector256.ShiftLeft(south, 16), 16);
            Vector256.Store(Vector256.ConvertToSingle(right - left) * half, gradientX + i);
            Vector256.Store(Vector256.ConvertToSingle(down - up) * half, gradientY + i);
            var packed = Avx2.Permute4x64(Avx2.PackSignedSaturate(centre, centre).AsInt64(), 0xD8).AsInt16();
            packed.GetLower().Store(values + i);
            var border = Vector256.AndNot(present, interior).ExtractMostSignificantBits();
            while (border != 0)
            {
                var lane = BitOperations.TrailingZeroCount(border);
                Edge(field, cells[i + lane], i + lane, values, gradientX, gradientY);
                border &= border - 1;
            }
        }

        return i;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void ReadScalar(in FloorField field, int* cells, int from, int to, short* values, float* gradientX, float* gradientY)
    {
        var directory = field.Directory;
        var pages = field.Pages;
        for (var i = from; i < to; i++)
        {
            if (Sse.IsSupported && field.Prefetch)
            {
                Sse.Prefetch0(directory + (Math.Max(cells[i + 2 * Ahead], 0) >> 10));
                var near = Math.Max(cells[i + Ahead], 0);
                var nearPage = directory[near >> 10];
                if (nearPage >= 0) Sse.Prefetch0(pages + ((nint)nearPage << 10) + (near & 1023));
            }

            var cell = cells[i];
            if (cell < 0)
            {
                values[i] = 0;
                gradientX[i] = 0f;
                gradientY[i] = 0f;
                continue;
            }

            var local = cell & 1023;
            var page = directory[cell >> 10];
            if (page >= 0 & (uint)((local & 31) - 1) < 30u & (uint)((local >> 5) - 1) < 30u)
            {
                var p = pages + ((nint)page << 10) + local;
                values[i] = *p;
                gradientX[i] = (p[1] - p[-1]) * PerUnit;
                gradientY[i] = (p[32] - p[-32]) * PerUnit;
            }
            else Edge(field, cell, i, values, gradientX, gradientY);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Edge(in FloorField field, int cell, int i, short* values, float* gradientX, float* gradientY)
    {
        var tile = cell >> 10;
        var local = cell & 1023;
        var lx = local & (FloorShape.TileSize - 1);
        var ly = local >> FloorShape.TileBits;
        var tx = tile & (field.TilesPerSide - 1);
        var ty = tile >> field.TileShift;
        var page = field.Directory[tile];
        var here = page < 0 ? null : field.Pages + ((nint)page << 10);
        var west = lx > 0 ? Inside(here, local - 1) : Cross(field, tx - 1, ty, FloorShape.TileSize - 1, ly);
        var east = lx < FloorShape.TileSize - 1 ? Inside(here, local + 1) : Cross(field, tx + 1, ty, 0, ly);
        var north = ly > 0 ? Inside(here, local - FloorShape.TileSize) : Cross(field, tx, ty - 1, lx, FloorShape.TileSize - 1);
        var south = ly < FloorShape.TileSize - 1 ? Inside(here, local + FloorShape.TileSize) : Cross(field, tx, ty + 1, lx, 0);
        values[i] = Inside(here, local);
        gradientX[i] = (east - west) * PerUnit;
        gradientY[i] = (south - north) * PerUnit;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static short Inside(short* page, int local) => page == null ? (short)0 : page[local];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static short Cross(in FloorField field, int tx, int ty, int lx, int ly)
    {
        if (((uint)tx >= (uint)field.TilesPerSide) | ((uint)ty >= (uint)field.TilesPerSide)) return 0;
        var page = field.Directory[(ty << field.TileShift) + tx];
        return page < 0 ? (short)0 : field.Pages[((nint)page << 10) + (ly << FloorShape.TileBits) + lx];
    }
}

internal sealed unsafe class FloorReadsSuite : FloorSuite
{
    private const int Count = FloorShape.Splats;
    private const int BlockQueries = 16384;
    private const int BlockRepeats = 61;

    private readonly Layout _large;
    private readonly Layout _small;
    private readonly short* _values;
    private readonly float* _gradientX;
    private readonly float* _gradientY;
    private readonly FloorCase[] _cases;

    public FloorReadsSuite()
    {
        _values = Arena.Alloc<short>(Count);
        _gradientX = Arena.Alloc<float>(Count);
        _gradientY = Arena.Alloc<float>(Count);
        var rng = new FloorRandom(0x56);
        _large = Build(4096, ref rng);
        _small = Build(1024, ref rng);
        _cases =
        [
            Reading("random-32MB", _large.Field, _large.Random),
            Reading("coherent-32MB", _large.Field, _large.Coherent),
            Reading("random-2MB", _small.Field, _small.Random),
            Reading("coherent-2MB", _small.Field, _small.Coherent),
            new FloorCase("P6", "coherent-2MB-L2-resident", BlockQueries * BlockRepeats,
                () =>
                {
                    for (var r = 0; r < BlockRepeats; r++)
                        FloorReadKernel.Read(_small.Field, _small.Coherent, BlockQueries, _values, _gradientX, _gradientY);
                },
                () =>
                {
                    FloorReadKernel.Read(_small.Field, _small.Coherent, BlockQueries, _values, _gradientX, _gradientY);
                    var h = FloorHash.Of(_values, BlockQueries);
                    h = FloorHash.Add(h, FloorHash.Of(_gradientX, BlockQueries));
                    return FloorHash.Add(h, FloorHash.Of(_gradientY, BlockQueries));
                }),
        ];
    }

    public override string Id => "P6";

    public override IReadOnlyList<FloorCase> Cases => _cases;

    public override void Receipt(FloorReceipts receipts)
    {
        foreach (var (name, layout) in new[] { ("32MB", _large), ("2MB", _small) })
        {
            var flat = Flatten(layout.Field);
            foreach (var (order, cells) in new[] { ("random", (nint)layout.Random), ("coherent", (nint)layout.Coherent) })
            {
                var queries = (int*)cells;
                Verify(receipts, $"floor-p6-{order}-{name}-scalar-matches-flat-field", layout, queries, Count, flat,
                    () => FloorReadKernel.ReadScalar(layout.Field, queries, 0, Count, _values, _gradientX, _gradientY));
                if (Avx2.IsSupported)
                    Verify(receipts, $"floor-p6-{order}-{name}-vector-matches-flat-field", layout, queries, Count, flat,
                        () => FloorReadKernel.Read(layout.Field, queries, Count, _values, _gradientX, _gradientY));
                else receipts.Skip($"floor-p6-{order}-{name}-vector-matches-flat-field", "avx2 unavailable");
            }

            if (name != "2MB") continue;
            Verify(receipts, "floor-p6-block-matches-leading-queries", layout, layout.Coherent, BlockQueries, flat,
                () => FloorReadKernel.Read(layout.Field, layout.Coherent, BlockQueries, _values, _gradientX, _gradientY));
            Verify(receipts, "floor-p6-unaligned-count-matches-flat-field", layout, layout.Random, 1003, flat,
                () => FloorReadKernel.Read(layout.Field, layout.Random, 1003, _values, _gradientX, _gradientY));
        }
    }

    private FloorCase Reading(string variant, FloorField field, int* cells)
        => new("P6", variant, Count,
            () => FloorReadKernel.Read(field, cells, Count, _values, _gradientX, _gradientY),
            () =>
            {
                FloorReadKernel.Read(field, cells, Count, _values, _gradientX, _gradientY);
                var h = FloorHash.Of(_values, Count);
                h = FloorHash.Add(h, FloorHash.Of(_gradientX, Count));
                return FloorHash.Add(h, FloorHash.Of(_gradientY, Count));
            });

    private void Verify(FloorReceipts receipts, string name, Layout layout, int* cells, int count, short[] flat, Action run)
    {
        new Span<short>(_values, Count).Clear();
        new Span<float>(_gradientX, Count).Clear();
        new Span<float>(_gradientY, Count).Clear();
        run();
        var side = layout.Field.Side;
        var tiles = layout.Field.TilesPerSide;
        var ok = true;
        var outside = 0;
        var absentTile = 0;
        for (var i = 0; i < count && ok; i++)
        {
            var cell = cells[i];
            int x = 0, y = 0;
            var inside = cell >= 0;
            if (inside)
            {
                var tile = cell / FloorShape.TileCells;
                var local = cell % FloorShape.TileCells;
                x = tile % tiles * FloorShape.TileSize + local % FloorShape.TileSize;
                y = tile / tiles * FloorShape.TileSize + local / FloorShape.TileSize;
                if (layout.Field.Directory[tile] < 0) absentTile++;
            }
            else outside++;

            short At(int px, int py) => inside && px >= 0 && py >= 0 && px < side && py < side ? flat[py * side + px] : (short)0;
            ok = _values[i] == At(x, y)
                 && _gradientX[i] == (At(x + 1, y) - At(x - 1, y)) * 0.5f
                 && _gradientY[i] == (At(x, y + 1) - At(x, y - 1)) * 0.5f;
        }

        receipts.Check(name, ok && (count < 100_000 || outside > 0 && absentTile > 0));
    }

    private short[] Flatten(in FloorField field)
    {
        var flat = new short[field.Side * field.Side];
        for (var ty = 0; ty < field.TilesPerSide; ty++)
        for (var tx = 0; tx < field.TilesPerSide; tx++)
        {
            var page = field.Directory[ty * field.TilesPerSide + tx];
            if (page < 0) continue;
            for (var y = 0; y < FloorShape.TileSize; y++)
            for (var x = 0; x < FloorShape.TileSize; x++)
                flat[(ty * FloorShape.TileSize + y) * field.Side + tx * FloorShape.TileSize + x] =
                    field.Pages[page * FloorShape.TileCells + y * FloorShape.TileSize + x];
        }

        return flat;
    }

    private Layout Build(int side, ref FloorRandom rng)
    {
        var tilesPerSide = side / FloorShape.TileSize;
        var tiles = tilesPerSide * tilesPerSide;
        var pages = Arena.Alloc<short>((long)tiles * FloorShape.TileCells);
        var directory = Arena.Alloc<int>(tiles);
        var order = new int[tiles];
        for (var i = 0; i < tiles; i++) order[i] = i;
        for (var i = tiles - 1; i > 0; i--)
        {
            var j = rng.Below(i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }

        for (var i = 0; i < tiles * FloorShape.TileCells; i++) pages[i] = (short)(rng.Next() >> 48);
        var used = 0;
        for (var t = 0; t < tiles; t++) directory[t] = rng.Below(32) == 0 ? -1 : order[used++];
        var field = new FloorField { Pages = pages, Directory = directory, TilesPerSide = tilesPerSide, TileShift = BitOperations.Log2((uint)tilesPerSide), Side = side, Prefetch = side >= 2048 };

        var cells = new int[Count];
        var keys = new uint[Count];
        var random = Arena.Alloc<int>(Count + FloorReadKernel.QueryPadding);
        var edges = new[] { 0, 1, 30, 31, 32, 33, side - 2, side - 1, -1, side, side - 33, side - 32 };
        for (var i = 0; i < Count; i++)
        {
            var x = i < 144 ? edges[i % 12] : rng.Below(side + 4) - 2;
            var y = i < 144 ? edges[i / 12] : rng.Below(side + 4) - 2;
            cells[i] = (uint)x >= (uint)side || (uint)y >= (uint)side ? -1 : FloorShape.Paged(x, y, tilesPerSide);
            keys[i] = FloorShape.Morton(x + 2, y + 2);
            random[i] = cells[i];
        }

        Array.Sort(keys, cells);
        var coherent = Arena.Alloc<int>(Count + FloorReadKernel.QueryPadding);
        for (var i = 0; i < Count; i++) coherent[i] = cells[i];
        return new Layout(field, random, coherent);
    }

    private readonly struct Layout(FloorField field, int* random, int* coherent)
    {
        public FloorField Field { get; } = field;

        public int* Random { get; } = random;

        public int* Coherent { get; } = coherent;
    }
}
