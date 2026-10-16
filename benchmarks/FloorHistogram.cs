using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

internal static unsafe class FloorHistogramKernel
{
    public const int PrefetchDistance = 16;

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void Accumulate(int* cells, int count, int* histogram, bool prefetch)
    {
        for (var i = 0; i < count; i++)
        {
            if (Sse.IsSupported && prefetch) Sse.Prefetch0(histogram + cells[i + PrefetchDistance]);
            histogram[cells[i]]++;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void AccumulateInterleaved(int* cells, int count, int* even, int* odd, bool prefetch)
    {
        var i = 0;
        for (; i + 2 <= count; i += 2)
        {
            if (Sse.IsSupported && prefetch)
            {
                Sse.Prefetch0(even + cells[i + PrefetchDistance]);
                Sse.Prefetch0(odd + cells[i + PrefetchDistance + 1]);
            }

            even[cells[i]]++;
            odd[cells[i + 1]]++;
        }

        if (i < count) even[cells[i]]++;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void AccumulateCoalesced(int* cells, int count, int* histogram, bool prefetch)
    {
        if (count <= 0) return;
        var previous = cells[0];
        var run = 0;
        for (var i = 0; i < count; i++)
        {
            if (Sse.IsSupported && prefetch) Sse.Prefetch0(histogram + cells[i + PrefetchDistance]);
            var cell = cells[i];
            if (cell == previous)
            {
                run++;
                continue;
            }

            histogram[previous] += run;
            previous = cell;
            run = 1;
        }

        histogram[previous] += run;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void Merge(int* target, int* source, int count)
    {
        var i = 0;
        if (Avx2.IsSupported)
            for (; i + 8 <= count; i += 8)
                (Vector256.Load(target + i) + Vector256.Load(source + i)).Store(target + i);
        for (; i < count; i++) target[i] += source[i];
    }

    public static void Clear(int* histogram, int count) => new Span<int>(histogram, count).Clear();
}

internal sealed unsafe class FloorHistogramSuite : FloorSuite
{
    private const int Count = FloorShape.Splats;
    private const int Cells = FloorShape.Cells;

    private readonly int* uniformRandom;
    private readonly int* uniformMorton;
    private readonly int* clumpRandom;
    private readonly int* clumpMorton;
    private readonly int* histogram;
    private readonly int* odd;
    private readonly int* merged;
    private readonly FloorCase[] cases;

    public FloorHistogramSuite()
    {
        var rng = new FloorRandom(0x52);
        Generate(0, FloorShape.Size, ref rng, out uniformRandom, out uniformMorton);
        Generate(448, 128, ref rng, out clumpRandom, out clumpMorton);
        histogram = Arena.Alloc<int>(Cells);
        odd = Arena.Alloc<int>(Cells);
        merged = Arena.Alloc<int>(Cells);
        cases =
        [
            Accumulating("P2", "random-uniform", uniformRandom, true),
            Accumulating("P2", "morton-uniform", uniformMorton, false),
            Accumulating("P2", "morton-uniform-prefetch", uniformMorton, true),
            new FloorCase("P2", "clear-4MB", Cells,
                () => FloorHistogramKernel.Clear(histogram, Cells),
                () =>
                {
                    FloorHistogramKernel.Clear(histogram, Cells);
                    return FloorHash.Of(histogram, Cells);
                }),
            Accumulating("P3", "baseline-morton-clump", clumpMorton, false),
            Interleaved("P3", "interleaved2-morton-uniform", uniformMorton, false),
            Interleaved("P3", "interleaved2-morton-uniform-prefetch", uniformMorton, true),
            Interleaved("P3", "interleaved2-morton-clump", clumpMorton, false),
            Coalesced("P3", "coalesce-morton-uniform", uniformMorton, false),
            Coalesced("P3", "coalesce-morton-clump", clumpMorton, false),
            new FloorCase("P3", "merge-2x4MB", Cells,
                () => FloorHistogramKernel.Merge(histogram, odd, Cells),
                () =>
                {
                    FloorHistogramKernel.Clear(histogram, Cells);
                    FloorHistogramKernel.Clear(odd, Cells);
                    odd[7] = 3;
                    FloorHistogramKernel.Merge(histogram, odd, Cells);
                    return FloorHash.Of(histogram, Cells);
                }),
        ];
    }

    public override string Id => "P2";

    public override IReadOnlyList<FloorCase> Cases => cases;

    public override void Receipt(FloorReceipts receipts)
    {
        var sets = new (string Name, nint Data)[]
        {
            ("random-uniform", (nint)uniformRandom), ("morton-uniform", (nint)uniformMorton),
            ("random-clump", (nint)clumpRandom), ("morton-clump", (nint)clumpMorton),
        };
        foreach (var (name, address) in sets)
        {
            var data = (int*)address;
            var expected = Reference(data);
            var prefetching = name.StartsWith("random", StringComparison.Ordinal);
            FloorHistogramKernel.Clear(histogram, Cells);
            FloorHistogramKernel.Accumulate(data, Count, histogram, prefetching);
            receipts.Check($"floor-p2-histogram-{name}-matches-reference", Equal(histogram, expected));

            FloorHistogramKernel.Clear(histogram, Cells);
            FloorHistogramKernel.Accumulate(data, Count, histogram, !prefetching);
            receipts.Check($"floor-p2-histogram-{name}-other-prefetch-matches-reference", Equal(histogram, expected));

            FloorHistogramKernel.Clear(histogram, Cells);
            FloorHistogramKernel.Clear(odd, Cells);
            FloorHistogramKernel.AccumulateInterleaved(data, Count, histogram, odd, prefetching);
            FloorHistogramKernel.Merge(histogram, odd, Cells);
            receipts.Check($"floor-p3-interleaved-{name}-matches-reference", Equal(histogram, expected));

            FloorHistogramKernel.Clear(histogram, Cells);
            FloorHistogramKernel.Clear(odd, Cells);
            FloorHistogramKernel.AccumulateInterleaved(data, Count, histogram, odd, !prefetching);
            FloorHistogramKernel.Merge(histogram, odd, Cells);
            receipts.Check($"floor-p3-interleaved-{name}-other-prefetch-matches-reference", Equal(histogram, expected));

            FloorHistogramKernel.Clear(histogram, Cells);
            FloorHistogramKernel.AccumulateCoalesced(data, Count, histogram, prefetching);
            receipts.Check($"floor-p3-coalesced-{name}-matches-reference", Equal(histogram, expected));
        }

        var shortRun = new int[] { 5, 5, 5, 9, 9, 5, 5, 7, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        var runExpected = new int[16];
        for (var i = 0; i < 8; i++) runExpected[shortRun[i]]++;
        var tail = new int[16];
        fixed (int* run = shortRun)
        fixed (int* h = tail)
        {
            FloorHistogramKernel.AccumulateCoalesced(run, 8, h, true);
        }

        receipts.Check("floor-p3-coalesced-runs-and-tail-exact", tail.AsSpan().SequenceEqual(runExpected));
        var oddTail = new int[16];
        var oddTailB = new int[16];
        fixed (int* run = shortRun)
        fixed (int* a = oddTail)
        fixed (int* b = oddTailB)
        {
            FloorHistogramKernel.AccumulateInterleaved(run, 7, a, b, true);
        }

        var combined = new int[16];
        for (var i = 0; i < 16; i++) combined[i] = oddTail[i] + oddTailB[i];
        var sevenExpected = new int[16];
        for (var i = 0; i < 7; i++) sevenExpected[shortRun[i]]++;
        receipts.Check("floor-p3-interleaved-odd-count-exact", combined.AsSpan().SequenceEqual(sevenExpected));
        FloorHistogramKernel.Clear(histogram, Cells);
        FloorHistogramKernel.Clear(odd, Cells);
    }

    private FloorCase Accumulating(string probe, string variant, int* data, bool prefetch)
        => new(probe, variant, Count,
            () => FloorHistogramKernel.Accumulate(data, Count, histogram, prefetch),
            () =>
            {
                FloorHistogramKernel.Clear(histogram, Cells);
                FloorHistogramKernel.Accumulate(data, Count, histogram, prefetch);
                return FloorHash.Of(histogram, Cells);
            });

    private FloorCase Interleaved(string probe, string variant, int* data, bool prefetch)
        => new(probe, variant, Count,
            () => FloorHistogramKernel.AccumulateInterleaved(data, Count, histogram, odd, prefetch),
            () =>
            {
                FloorHistogramKernel.Clear(histogram, Cells);
                FloorHistogramKernel.Clear(odd, Cells);
                FloorHistogramKernel.AccumulateInterleaved(data, Count, histogram, odd, prefetch);
                new Span<int>(histogram, Cells).CopyTo(new Span<int>(merged, Cells));
                FloorHistogramKernel.Merge(merged, odd, Cells);
                return FloorHash.Of(merged, Cells);
            });

    private FloorCase Coalesced(string probe, string variant, int* data, bool prefetch)
        => new(probe, variant, Count,
            () => FloorHistogramKernel.AccumulateCoalesced(data, Count, histogram, prefetch),
            () =>
            {
                FloorHistogramKernel.Clear(histogram, Cells);
                FloorHistogramKernel.AccumulateCoalesced(data, Count, histogram, prefetch);
                return FloorHash.Of(histogram, Cells);
            });

    private static int[] Reference(int* data)
    {
        var expected = new int[Cells];
        for (var i = 0; i < Count; i++) expected[data[i]]++;
        return expected;
    }

    private static bool Equal(int* actual, int[] expected)
        => new ReadOnlySpan<int>(actual, Cells).SequenceEqual(expected);

    private void Generate(int origin, int span, ref FloorRandom rng, out int* random, out int* morton)
    {
        var cells = new int[Count];
        var keys = new uint[Count];
        random = Arena.Alloc<int>(Count);
        for (var i = 0; i < Count; i++)
        {
            var cx = origin + rng.Below(span);
            var cy = origin + rng.Below(span);
            cells[i] = FloorShape.Paged(cx, cy, FloorShape.TilesPerSide);
            keys[i] = FloorShape.Morton(cx, cy);
            random[i] = cells[i];
        }

        Array.Sort(keys, cells);
        morton = Arena.Alloc<int>(Count);
        for (var i = 0; i < Count; i++) morton[i] = cells[i];
    }
}
