using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

internal unsafe struct FloorDiskField
{
    public short* Pages;
    public int* Directory;
    public int* RowSums;
    public short* GreaterEqual;
    public short* LessEqual;
    public int TilesPerSide;
    public int Side;
}

internal static unsafe class FloorDiskKernel
{
    public const int MaxRadius = 64;

    public static void HalfWidths(int radius, int* halfWidths)
    {
        var width = radius;
        var squared = radius * radius;
        for (var dy = 0; dy <= radius; dy++)
        {
            while (width * width + dy * dy > squared) width--;
            halfWidths[dy] = width;
        }
    }

    public static long Sum(in FloorDiskField field, int cx, int cy, int radius)
        => Avx2.IsSupported ? SumVector(field, cx, cy, radius) : SumScalar(field, cx, cy, radius);

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static long SumVector(in FloorDiskField field, int cx, int cy, int radius)
    {
        if ((uint)radius > MaxRadius) return 0;
        var pages = field.Pages;
        var directory = field.Directory;
        var rowSums = field.RowSums;
        var tilesPerSide = field.TilesPerSide;
        var greaterEqual = field.GreaterEqual;
        var lessEqual = field.LessEqual;
        var acc = Vector256<int>.Zero;
        var whole = 0L;
        var width = radius;
        var squared = radius * radius;
        for (var dy = 0; dy <= radius; dy++)
        {
            while (width * width + dy * dy > squared) width--;
            var x0 = cx - width;
            var x1 = cx + width;
            var first = x0 >> FloorShape.TileBits;
            var last = x1 >> FloorShape.TileBits;
            var up = cy + dy;
            var down = cy - dy;
            var upTiles = directory + (up >> FloorShape.TileBits) * tilesPerSide;
            var downTiles = directory + (down >> FloorShape.TileBits) * tilesPerSide;
            var upRow = (up & (FloorShape.TileSize - 1)) << FloorShape.TileBits;
            var downRow = (down & (FloorShape.TileSize - 1)) << FloorShape.TileBits;
            var distinct = dy != 0;
            if (first == last)
            {
                var ge = greaterEqual + (FloorShape.TileSize - (x0 & (FloorShape.TileSize - 1)));
                var le = lessEqual + (FloorShape.TileSize - 1 - (x1 & (FloorShape.TileSize - 1)));
                var m0 = Vector256.Load(ge) & Vector256.Load(le);
                var m1 = Vector256.Load(ge + 16) & Vector256.Load(le + 16);
                acc += Masked(pages, upTiles[first], upRow, m0, m1);
                if (distinct) acc += Masked(pages, downTiles[first], downRow, m0, m1);
                continue;
            }

            var leftMask0 = Vector256.Load(greaterEqual + (FloorShape.TileSize - (x0 & (FloorShape.TileSize - 1))));
            var leftMask1 = Vector256.Load(greaterEqual + (FloorShape.TileSize - (x0 & (FloorShape.TileSize - 1))) + 16);
            var rightMask0 = Vector256.Load(lessEqual + (FloorShape.TileSize - 1 - (x1 & (FloorShape.TileSize - 1))));
            var rightMask1 = Vector256.Load(lessEqual + (FloorShape.TileSize - 1 - (x1 & (FloorShape.TileSize - 1))) + 16);
            acc += Masked(pages, upTiles[first], upRow, leftMask0, leftMask1);
            acc += Masked(pages, upTiles[last], upRow, rightMask0, rightMask1);
            if (distinct)
            {
                acc += Masked(pages, downTiles[first], downRow, leftMask0, leftMask1);
                acc += Masked(pages, downTiles[last], downRow, rightMask0, rightMask1);
            }

            for (var t = first + 1; t < last; t++)
            {
                var upPage = upTiles[t];
                if (upPage >= 0) whole += rowSums[(upPage << FloorShape.TileBits) + (upRow >> FloorShape.TileBits)];
                if (!distinct) continue;
                var downPage = downTiles[t];
                if (downPage >= 0) whole += rowSums[(downPage << FloorShape.TileBits) + (downRow >> FloorShape.TileBits)];
            }
        }

        return whole + Vector256.Sum(acc);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> Masked(short* pages, int page, int rowOffset, Vector256<short> mask0, Vector256<short> mask1)
    {
        if (page < 0) return Vector256<int>.Zero;
        var cells = pages + ((nint)page << 10) + rowOffset;
        var ones = Vector256.Create((short)1);
        return Avx2.MultiplyAddAdjacent(Vector256.Load(cells) & mask0, ones)
               + Avx2.MultiplyAddAdjacent(Vector256.Load(cells + 16) & mask1, ones);
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static long SumScalar(in FloorDiskField field, int cx, int cy, int radius)
    {
        if ((uint)radius > MaxRadius) return 0;
        var halfWidths = stackalloc int[MaxRadius + 1];
        HalfWidths(radius, halfWidths);
        var total = 0L;
        for (var dy = 0; dy <= radius; dy++)
        {
            var h = halfWidths[dy];
            total += RowScalar(field, cy + dy, cx - h, cx + h);
            if (dy != 0) total += RowScalar(field, cy - dy, cx - h, cx + h);
        }

        return total;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long RowScalar(in FloorDiskField field, int y, int x0, int x1)
    {
        var tileRow = (y >> FloorShape.TileBits) * field.TilesPerSide;
        var ly = y & (FloorShape.TileSize - 1);
        var first = x0 >> FloorShape.TileBits;
        var last = x1 >> FloorShape.TileBits;
        var total = 0L;
        for (var t = first; t <= last; t++)
        {
            var page = field.Directory[tileRow + t];
            if (page < 0) continue;
            var lo = t == first ? x0 & (FloorShape.TileSize - 1) : 0;
            var hi = t == last ? x1 & (FloorShape.TileSize - 1) : FloorShape.TileSize - 1;
            if (lo == 0 & hi == FloorShape.TileSize - 1)
            {
                total += field.RowSums[(page << FloorShape.TileBits) + ly];
                continue;
            }

            var cells = field.Pages + ((nint)page << 10) + (ly << FloorShape.TileBits);
            for (var x = lo; x <= hi; x++) total += cells[x];
        }

        return total;
    }
}

internal sealed unsafe class FloorDisksSuite : FloorSuite
{
    private const int Disks = 1000;
    private static readonly int[] Radii = [16, 32, 64];

    private readonly FloorDiskField field;
    private readonly int*[] centersX = new int*[Radii.Length];
    private readonly int*[] centersY = new int*[Radii.Length];
    private readonly long* sums;
    private readonly FloorCase[] cases;

    public FloorDisksSuite()
    {
        var side = FloorShape.Size;
        var tilesPerSide = FloorShape.TilesPerSide;
        var rng = new FloorRandom(0x58);
        var pages = Arena.Alloc<short>(FloorShape.Cells);
        var directory = Arena.Alloc<int>(FloorShape.Tiles);
        var rowSums = Arena.Alloc<int>(FloorShape.Tiles * FloorShape.TileSize);
        var greaterEqual = Arena.Alloc<short>(2 * FloorShape.TileSize);
        var lessEqual = Arena.Alloc<short>(2 * FloorShape.TileSize);
        for (var i = 0; i < FloorShape.TileSize; i++)
        {
            greaterEqual[FloorShape.TileSize + i] = -1;
            lessEqual[i] = -1;
        }

        var order = new int[FloorShape.Tiles];
        for (var i = 0; i < order.Length; i++) order[i] = i;
        for (var i = order.Length - 1; i > 0; i--)
        {
            var j = rng.Below(i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }

        for (var i = 0; i < FloorShape.Cells; i++) pages[i] = (short)(rng.Below(1001) - 500);
        var used = 0;
        for (var t = 0; t < FloorShape.Tiles; t++) directory[t] = rng.Below(32) == 0 ? -1 : order[used++];
        for (var page = 0; page < FloorShape.Tiles; page++)
        for (var row = 0; row < FloorShape.TileSize; row++)
        {
            var sum = 0;
            for (var x = 0; x < FloorShape.TileSize; x++) sum += pages[page * FloorShape.TileCells + row * FloorShape.TileSize + x];
            rowSums[page * FloorShape.TileSize + row] = sum;
        }

        field = new FloorDiskField
        {
            Pages = pages, Directory = directory, RowSums = rowSums, GreaterEqual = greaterEqual,
            LessEqual = lessEqual, TilesPerSide = tilesPerSide, Side = side,
        };
        sums = Arena.Alloc<long>(Disks);
        var list = new List<FloorCase>();
        for (var k = 0; k < Radii.Length; k++)
        {
            var radius = Radii[k];
            centersX[k] = Arena.Alloc<int>(Disks);
            centersY[k] = Arena.Alloc<int>(Disks);
            for (var i = 0; i < Disks; i++)
            {
                centersX[k][i] = radius + rng.Below(side - 2 * radius);
                centersY[k][i] = radius + rng.Below(side - 2 * radius);
            }

            list.Add(Summing(k));
        }

        cases = [.. list];
    }

    public override string Id => "P7";

    public override IReadOnlyList<FloorCase> Cases => cases;

    public override void Receipt(FloorReceipts receipts)
    {
        var spansOk = true;
        var halfWidths = stackalloc int[FloorDiskKernel.MaxRadius + 1];
        for (var r = 0; r <= FloorDiskKernel.MaxRadius; r++)
        {
            FloorDiskKernel.HalfWidths(r, halfWidths);
            for (var dy = 0; dy <= r; dy++)
            {
                var exact = (int)Math.Sqrt(r * r - dy * dy);
                while (exact * exact > r * r - dy * dy) exact--;
                while ((exact + 1) * (exact + 1) <= r * r - dy * dy) exact++;
                spansOk &= halfWidths[dy] == exact;
            }
        }

        receipts.Check("floor-p7-spans-match-integer-sqrt-for-radii-0-to-64", spansOk);

        var flat = Flatten();
        var rng = new FloorRandom(0x59);
        var side = FloorShape.Size;
        var scalarOk = true;
        var vectorOk = true;
        foreach (var radius in new[] { 0, 1, 2, 5, 16, 32, 64 })
        for (var i = 0; i < 300; i++)
        {
            var cx = radius + rng.Below(side - 2 * radius);
            var cy = radius + rng.Below(side - 2 * radius);
            var expected = Naive(flat, cx, cy, radius);
            scalarOk &= FloorDiskKernel.SumScalar(field, cx, cy, radius) == expected;
            if (Avx2.IsSupported) vectorOk &= FloorDiskKernel.SumVector(field, cx, cy, radius) == expected;
        }

        for (var k = 0; k < Radii.Length; k++)
        for (var i = 0; i < Disks; i++)
        {
            var expected = Naive(flat, centersX[k][i], centersY[k][i], Radii[k]);
            scalarOk &= FloorDiskKernel.SumScalar(field, centersX[k][i], centersY[k][i], Radii[k]) == expected;
            if (Avx2.IsSupported) vectorOk &= FloorDiskKernel.SumVector(field, centersX[k][i], centersY[k][i], Radii[k]) == expected;
        }

        receipts.Check("floor-p7-scalar-disk-sums-match-cell-scan", scalarOk);
        if (Avx2.IsSupported) receipts.Check("floor-p7-vector-disk-sums-match-cell-scan", vectorOk);
        else receipts.Skip("floor-p7-vector-disk-sums-match-cell-scan", "avx2 unavailable");
    }

    private FloorCase Summing(int k)
        => new("P7", $"r{Radii[k]}-1000-disks", Disks,
            () => Run(k),
            () =>
            {
                Run(k);
                return FloorHash.Of(sums, Disks);
            });

    private void Run(int k)
    {
        var radius = Radii[k];
        var xs = centersX[k];
        var ys = centersY[k];
        for (var i = 0; i < Disks; i++) sums[i] = FloorDiskKernel.Sum(field, xs[i], ys[i], radius);
    }

    private short[] Flatten()
    {
        var flat = new short[FloorShape.Cells];
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

    private long Naive(short[] flat, int cx, int cy, int radius)
    {
        var total = 0L;
        for (var y = cy - radius; y <= cy + radius; y++)
        for (var x = cx - radius; x <= cx + radius; x++)
            if ((x - cx) * (x - cx) + (y - cy) * (y - cy) <= radius * radius)
                total += flat[y * field.Side + x];
        return total;
    }
}
