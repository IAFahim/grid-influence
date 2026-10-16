using System.Diagnostics;
using System.Runtime.InteropServices;

internal static class FloorShape
{
    public const int Splats = 1_000_000;
    public const int Size = 1024;
    public const int TileBits = 5;
    public const int TileSize = 1 << TileBits;
    public const int TilesPerSide = Size / TileSize;
    public const int Tiles = TilesPerSide * TilesPerSide;
    public const int TileCells = TileSize * TileSize;
    public const int Cells = Size * Size;

    public static int Paged(int cx, int cy, int tilesPerSide)
        => (((cy >> TileBits) * tilesPerSide + (cx >> TileBits)) * TileCells)
           + (cy & (TileSize - 1)) * TileSize + (cx & (TileSize - 1));

    public static uint Morton(int x, int y) => Spread((uint)x) | (Spread((uint)y) << 1);

    private static uint Spread(uint value)
    {
        value &= 0xFFFF;
        value = (value | (value << 8)) & 0x00FF00FF;
        value = (value | (value << 4)) & 0x0F0F0F0F;
        value = (value | (value << 2)) & 0x33333333;
        value = (value | (value << 1)) & 0x55555555;
        return value;
    }
}

internal struct FloorRandom
{
    private ulong state;

    public FloorRandom(ulong seed) => state = seed;

    public ulong Next()
    {
        state += 0x9E3779B97F4A7C15UL;
        var z = state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    public int Below(int bound) => (int)(((Next() >> 32) * (uint)bound) >> 32);

    public float Unit() => (Next() >> 40) * (1f / 16777216f);
}

internal static unsafe class FloorHash
{
    public const ulong Seed = 0xCBF29CE484222325UL;

    public static ulong Add(ulong hash, ulong word)
    {
        hash = (hash ^ word) * 0x9E3779B97F4A7C15UL;
        return hash ^ (hash >> 32);
    }

    public static ulong Add(ulong hash, void* data, long bytes)
    {
        var words = (ulong*)data;
        var count = bytes >> 3;
        for (var i = 0L; i < count; i++) hash = Add(hash, words[i]);
        var tail = (byte*)data + (count << 3);
        var rest = 0UL;
        for (var i = 0; i < (int)(bytes & 7); i++) rest |= (ulong)tail[i] << (i << 3);
        return (bytes & 7) == 0 ? hash : Add(hash, rest);
    }

    public static ulong Of<T>(T* data, long count) where T : unmanaged
        => Add(Seed, data, count * sizeof(T));
}

internal sealed unsafe class FloorArena : IDisposable
{
    private readonly List<nint> blocks = [];

    public T* Alloc<T>(long count) where T : unmanaged
    {
        var bytes = (nuint)((count * sizeof(T) + 127) & ~63L);
        var block = NativeMemory.AlignedAlloc(bytes, 64);
        NativeMemory.Clear(block, bytes);
        blocks.Add((nint)block);
        return (T*)block;
    }

    public void Dispose()
    {
        foreach (var block in blocks) NativeMemory.AlignedFree((void*)block);
        blocks.Clear();
    }
}

internal sealed class FloorCase(string probe, string variant, long ops, Action pass, Func<ulong> checksum)
{
    public string Probe { get; } = probe;
    public string Variant { get; } = variant;
    public long Ops { get; } = ops;
    public Action Pass { get; } = pass;
    public Func<ulong> Checksum { get; } = checksum;
}

internal sealed class FloorReceipts
{
    private readonly List<(string Name, bool Ok, string Note)> items = [];

    public bool AllPassed => items.All(i => i.Ok);

    public int Failures => items.Count(i => !i.Ok);

    public IReadOnlyList<(string Name, bool Ok, string Note)> Items => items;

    public void Check(string name, bool ok) => items.Add((name, ok, ""));

    public void Skip(string name, string reason) => items.Add((name, true, reason));
}

internal abstract class FloorSuite : IDisposable
{
    protected FloorArena Arena { get; } = new();

    public abstract string Id { get; }

    public abstract IReadOnlyList<FloorCase> Cases { get; }

    public abstract void Receipt(FloorReceipts receipts);

    public void Dispose() => Arena.Dispose();
}

internal sealed class FloorResult
{
    public required FloorCase Case { get; init; }
    public required int Iterations { get; init; }
    public required double MeanMs { get; init; }
    public required double P50Ms { get; init; }
    public required double P99Ms { get; init; }
    public required double MinMs { get; init; }
    public required long AllocatedBytes { get; init; }
    public required ulong Checksum { get; init; }

    public double NsPerOp => P50Ms * 1e6 / Case.Ops;
}

internal static class FloorTimer
{
    public const int MinIterations = 50;
    public const int MaxIterations = 300;
    public const double BudgetMs = 3000;

    public static FloorResult Measure(FloorCase floor, int maxIterations)
    {
        for (var i = 0; i < 6; i++) floor.Pass();
        var calibration = Stopwatch.GetTimestamp();
        for (var i = 0; i < 6; i++) floor.Pass();
        var perPass = Stopwatch.GetElapsedTime(calibration).TotalMilliseconds / 6;
        var iterations = (int)Math.Clamp(BudgetMs / Math.Max(perPass, 1e-6), Math.Min(MinIterations, maxIterations), maxIterations);
        var samples = new long[iterations];
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < iterations; i++)
        {
            var start = Stopwatch.GetTimestamp();
            floor.Pass();
            samples[i] = Stopwatch.GetTimestamp() - start;
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        var total = 0L;
        foreach (var s in samples) total += s;
        Array.Sort(samples);
        var toMs = 1000.0 / Stopwatch.Frequency;
        return new FloorResult
        {
            Case = floor,
            Iterations = iterations,
            MeanMs = total * toMs / iterations,
            P50Ms = samples[(iterations - 1) / 2] * toMs,
            P99Ms = samples[Math.Min(iterations - 1, (int)Math.Ceiling(0.99 * iterations) - 1)] * toMs,
            MinMs = samples[0] * toMs,
            AllocatedBytes = allocated,
            Checksum = floor.Checksum(),
        };
    }
}
