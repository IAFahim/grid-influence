using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

internal static unsafe class FloorSweepKernel
{
    public const int ListPadding = 8;

    public static int Run(ulong* current, ulong* deposited, int count, int* list, int* packTable)
        => Avx2.IsSupported ? Vector(current, deposited, count, list, packTable) : Scalar(current, deposited, count, list);

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static int Vector(ulong* current, ulong* deposited, int count, int* list, int* packTable)
    {
        var found = 0;
        var i = 0;
        var indices = Vector256.Create(0, 1, 2, 3, 4, 5, 6, 7);
        var step = Vector256.Create(8);
        for (; i + 8 <= count; i += 8)
        {
            var low = Vector256.Equals(Vector256.Load(current + i), Vector256.Load(deposited + i));
            var high = Vector256.Equals(Vector256.Load(current + i + 4), Vector256.Load(deposited + i + 4));
            var moved = ~(low.ExtractMostSignificantBits() | (high.ExtractMostSignificantBits() << 4)) & 0xFFu;
            var packed = Avx2.PermuteVar8x32(indices, Vector256.Load(packTable + (moved << 3)));
            packed.Store(list + found);
            found += BitOperations.PopCount(moved);
            indices += step;
        }

        for (; i < count; i++)
            if (current[i] != deposited[i])
                list[found++] = i;
        return found;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static int Scalar(ulong* current, ulong* deposited, int count, int* list)
    {
        var found = 0;
        for (var i = 0; i < count; i++)
            if (current[i] != deposited[i])
                list[found++] = i;
        return found;
    }

    public static void BuildPackTable(int* packTable)
    {
        for (var mask = 0; mask < 256; mask++)
        {
            var slot = 0;
            for (var bit = 0; bit < 8; bit++)
                if ((mask & (1 << bit)) != 0)
                    packTable[mask * 8 + slot++] = bit;
        }
    }
}

internal sealed unsafe class FloorSweepSuite : FloorSuite
{
    private const int Count = FloorShape.Splats;
    private const int Block = 32768;
    private const int Repeats = 30;

    private static readonly (string Name, int PerMillion)[] Rates =
    [
        ("sparse-100ppm", 100),
        ("moved-10pct", 100_000),
        ("moved-all", 1_000_000),
    ];

    private readonly ulong* deposited;
    private readonly ulong*[] current = new ulong*[Rates.Length];
    private readonly int[] moved = new int[Rates.Length];
    private readonly int* list;
    private readonly int* scalarList;
    private readonly int* packTable;
    private readonly FloorCase[] cases;

    public FloorSweepSuite()
    {
        deposited = Arena.Alloc<ulong>(Count);
        list = Arena.Alloc<int>(Count + FloorSweepKernel.ListPadding);
        scalarList = Arena.Alloc<int>(Count + FloorSweepKernel.ListPadding);
        packTable = Arena.Alloc<int>(256 * 8);
        FloorSweepKernel.BuildPackTable(packTable);
        var rng = new FloorRandom(0x5A);
        for (var i = 0; i < Count; i++) deposited[i] = rng.Next();
        var built = new List<FloorCase>();
        for (var k = 0; k < Rates.Length; k++)
        {
            current[k] = Arena.Alloc<ulong>(Count);
            for (var i = 0; i < Count; i++)
            {
                var hit = (int)(rng.Next() % 1_000_000) < Rates[k].PerMillion;
                current[k][i] = hit ? deposited[i] ^ (rng.Next() | 1) : deposited[i];
                if (hit) moved[k]++;
            }

            built.Add(Sweeping(k));
        }

        built.Add(new FloorCase("P8", "anchor-sweep-moved-10pct-L2-resident", Block * Repeats,
            () =>
            {
                for (var r = 0; r < Repeats; r++) FloorSweepKernel.Run(current[1], deposited, Block, list, packTable);
            },
            () =>
            {
                var found = FloorSweepKernel.Run(current[1], deposited, Block, list, packTable);
                return FloorHash.Add(FloorHash.Of(list, found), (ulong)found);
            }));
        cases = [.. built];
    }

    public override string Id => "P8";

    public override IReadOnlyList<FloorCase> Cases => cases;

    public override void Receipt(FloorReceipts receipts)
    {
        for (var k = 0; k < Rates.Length; k++)
        {
            var expected = new List<int>();
            for (var i = 0; i < Count; i++) if (current[k][i] != deposited[i]) expected.Add(i);
            receipts.Check($"floor-p8-{Rates[k].Name}-reference-counts-moved-slots", expected.Count == moved[k] && expected.Count > 0);

            var scalarCount = FloorSweepKernel.Scalar(current[k], deposited, Count, scalarList);
            receipts.Check($"floor-p8-{Rates[k].Name}-scalar-matches-reference",
                scalarCount == expected.Count && new ReadOnlySpan<int>(scalarList, scalarCount).SequenceEqual(expected.ToArray()));
            if (!Avx2.IsSupported)
            {
                receipts.Skip($"floor-p8-{Rates[k].Name}-vector-matches-reference", "avx2 unavailable");
                continue;
            }

            var vectorCount = FloorSweepKernel.Vector(current[k], deposited, Count, list, packTable);
            receipts.Check($"floor-p8-{Rates[k].Name}-vector-matches-reference",
                vectorCount == expected.Count && new ReadOnlySpan<int>(list, vectorCount).SequenceEqual(expected.ToArray()));
        }

        var tailOk = true;
        if (Avx2.IsSupported)
            foreach (var count in new[] { 1, 7, 8, 9, 15, 1003 })
            {
                var vectorCount = FloorSweepKernel.Vector(current[1], deposited, count, list, packTable);
                var scalarCount = FloorSweepKernel.Scalar(current[1], deposited, count, scalarList);
                tailOk &= vectorCount == scalarCount && new ReadOnlySpan<int>(list, vectorCount).SequenceEqual(new ReadOnlySpan<int>(scalarList, scalarCount));
            }

        if (Avx2.IsSupported) receipts.Check("floor-p8-vector-handles-partial-last-block", tailOk);
        else receipts.Skip("floor-p8-vector-handles-partial-last-block", "avx2 unavailable");
    }

    private FloorCase Sweeping(int k)
        => new("P8", $"anchor-sweep-{Rates[k].Name}", Count,
            () => FloorSweepKernel.Run(current[k], deposited, Count, list, packTable),
            () =>
            {
                var found = FloorSweepKernel.Run(current[k], deposited, Count, list, packTable);
                return FloorHash.Add(FloorHash.Of(list, found), (ulong)found);
            });
}
