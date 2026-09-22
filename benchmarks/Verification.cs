using System.Diagnostics;
using GridInfluence;
using GridInfluence.Io;

namespace Benchmarks;

internal static class Verification
{
    public static int Run()
    {
        var failures = 0;
        void Check(string name, bool ok)
        {
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}");
            if (!ok) failures++;
        }

        Check("pipeline-matches-naive-decay", MatchesNaive(decay: true));
        Check("pipeline-matches-naive-nodecay", MatchesNaive(decay: false));
        Check("pipeline-parallel-matches-naive-decay", MatchesNaive(decay: true, parallelism: Environment.ProcessorCount));
        Check("pipeline-parallel-matches-naive-nodecay", MatchesNaive(decay: false, parallelism: Environment.ProcessorCount));
        Check("pnm-signed-roundtrip", PnmRoundTrip());
        Check("region-write-read-roundtrip", RegionRoundTrip());
        Check("warm-tick-allocates-0-bytes", TickAllocationFree());
        Check("warm-parallel-tick-allocates-0-bytes", ParallelTickAllocationFree());
        Check("warm-query-allocates-0-bytes", QueryAllocationFree());
        Check("budget-drops-deterministic", BudgetDrops());
        Check("world-process-matches-oracle", WorldMatchesOracle());
        Check("world-process-allocates-0-bytes", WorldProcessAllocationFree());
        Check("world-query-allocates-0-bytes", WorldQueryAllocationFree());
        Check("world-process-deterministic", WorldDeterministic());
        Check("world-fade-decays", WorldFadeDecays());

        Console.WriteLine(failures == 0 ? "verification: all receipts green" : $"verification: {failures} failures");
        return failures == 0 ? 0 : 1;
    }

    private static bool MatchesNaive(bool decay, int parallelism = 0)
    {
        const int extent = 512;
        var stamps = Fixtures.BuildStamps(512, extent);
        using var pipeline = new PipelineField(5, parallelism: parallelism);
        var naive = new NaiveField(extent, decay ? Fixtures.DecayPerMille : 0, Fixtures.SpreadDenominator);

        for (var tick = 0; tick < 30; tick++)
        {
            if (decay) pipeline.Tick(stamps);
            else pipeline.TickNoDecay(stamps);
            naive.Tick(stamps);
        }

        var reader = pipeline.Front.AsReader();
        for (var y = 0; y < extent; y++)
        for (var x = 0; x < extent; x++)
        {
            if (reader.ReadCell(new Int2(x, y)) != naive[x, y]) return false;
        }

        return true;
    }

    private static bool PnmRoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tlinfluence-verify-{Guid.NewGuid():N}.pgm");
        try
        {
            var weights = new int[64 * 32];
            for (var i = 0; i < weights.Length; i++) weights[i] = (i % 11 - 5) * 5000;

            Pnm.SaveGraySigned(path, weights, 64, 32, 65535);
            using var map = Pnm.LoadWeights(path);
            for (var i = 0; i < weights.Length; i++)
            {
                if (weights[i] != map.Samples[i]) return false;
            }

            return true;
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static bool RegionRoundTrip()
    {
        using var field = new Field(GridSpec.FromPowerOfTwo(3, uint.MaxValue));
        var weights = new int[11 * 7];
        for (var i = 0; i < weights.Length; i++) weights[i] = i * 13 - 70;

        field.WriteRegion(new Int2(5, -4), new Int2(11, 7), weights);
        var exported = new int[11 * 7];
        field.ReadRegion(new Int2(5, -4), new Int2(11, 7), exported);
        for (var i = 0; i < weights.Length; i++)
        {
            if (weights[i] != exported[i]) return false;
        }

        return true;
    }

    private static bool TickAllocationFree()
    {
        const int extent = 512;
        var stamps = Fixtures.BuildStamps(128, extent);
        using var pipeline = new PipelineField(5);
        for (var tick = 0; tick < 8; tick++) pipeline.Tick(stamps);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var tick = 0; tick < 64; tick++) pipeline.Tick(stamps);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Console.WriteLine($"  warm tick allocation over 64 ticks: {allocated} B");
        return allocated == 0;
    }

    private static bool ParallelTickAllocationFree()
    {
        const int extent = 1024;
        var stamps = Fixtures.BuildStamps(512, extent);
        using var pipeline = new PipelineField(5, parallelism: Environment.ProcessorCount);
        for (var tick = 0; tick < 8; tick++) pipeline.Tick(stamps);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var tick = 0; tick < 64; tick++) pipeline.Tick(stamps);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Console.WriteLine($"  warm parallel tick allocation over 64 ticks: {allocated} B");
        return allocated == 0;
    }

    private static bool QueryAllocationFree()
    {
        const int extent = 512;
        var stamps = Fixtures.BuildStamps(64, extent);
        using var pipeline = new PipelineField(5);
        for (var tick = 0; tick < 4; tick++) pipeline.Tick(stamps);

        var reader = pipeline.Front.AsReader();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        long acc = 0;
        for (var i = 0; i < 200_000; i++) acc += reader.Gradient(new Int2(i % 511, (i * 7) % 511)).X;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        _ = acc;

        Console.WriteLine($"  warm query allocation over 200k gradients: {allocated} B");
        return allocated == 0;
    }

    private static bool BudgetDrops()
    {
        using var field = new Field(GridSpec.FromPowerOfTwo(3, uint.MaxValue));
        var oversized = new FieldStamp(InfluenceShape.Disc(Int2.Zero, 600_000, 10), Int2.Zero);
        var stats = field.Tick([oversized], 1);
        return stats.StampsDroppedSpanBudget == 1 && field.ActiveSlotCount == 0;
    }

    private static (float x, float y)[] WorldData(int n, float extent, int seed)
    {
        var p = new (float, float)[n];
        var rng = seed;
        for (var i = 0; i < n; i++)
        {
            rng = rng * 1664525 + 1013904223;
            p[i] = ((rng % (uint)(extent * 2)) - extent * 0.9f, ((rng >> 8) % (uint)extent) * 0.95f);
        }
        return p;
    }

    private static bool WorldMatchesOracle()
    {
        var w = World.New();
        var g0 = Grid.New(w, 8, -256f, 0f, 512f);
        var g1 = Grid.New(w, 6, 256f, 0f, 512f);
        var l = Layer.New(w);
        var s = Stamp.Box(100);
        var d = WorldData(500, 512f, 7);
        foreach (var (x, y) in d) World.Place(w, l, x, y, 8f, s);
        World.Process(w);
        var grids = new[] { (g: g0, ox: -256f, size: 512f, cells: 256), (g: g1, ox: 256f, size: 512f, cells: 64) };
        for (var i = 0; i < 60; i++)
        {
            var (wx, wy) = d[i];
            foreach (var (g, ox, size, cells) in grids)
            {
                var qx = (int)((wx - ox) * cells / size);
                var qy = (int)(wy * cells / size);
                if (qx < 0 || qx >= cells || qy < 0 || qy >= cells) continue;
                var expected = 0L;
                for (var j = 0; j < d.Length; j++)
                {
                    var mcx = (int)((d[j].x - ox) * cells / size);
                    var mcy = (int)(d[j].y * cells / size);
                    var mr = (int)(8f * cells / size);
                    if (Math.Abs(qx - mcx) <= mr && Math.Abs(qy - mcy) <= mr) expected += 100;
                }
                if (World.Query(w, g, l, qx, qy) != Math.Clamp(expected, short.MinValue, short.MaxValue))
                    return false;
            }
        }
        return true;
    }

    private static bool WorldProcessAllocationFree()
    {
        var w = World.New();
        Grid.New(w, 8, 0f, 0f, 256f);
        Grid.New(w, 8, 256f, 0f, 256f);
        var l = Layer.New(w);
        var s = Stamp.Box(100);
        var d = WorldData(1000, 256f, 3);
        foreach (var (x, y) in d) World.Place(w, l, x, y, 8f, s);
        for (var i = 0; i < 8; i++) World.Process(w);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 64; i++) World.Process(w);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"  world process allocation over 64 runs: {allocated} B");
        return allocated == 0;
    }

    private static bool WorldQueryAllocationFree()
    {
        var w = World.New();
        var g = Grid.New(w, 8, 0f, 0f, 256f);
        var l = Layer.New(w);
        var s = Stamp.Box(100);
        var d = WorldData(200, 256f, 5);
        foreach (var (x, y) in d) World.Place(w, l, x, y, 8f, s);
        World.Process(w);
        World.Query(w, g, l, 10, 10);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        long acc = 0;
        for (var i = 0; i < 50_000; i++) acc += World.Query(w, g, l, i % 250, (i * 7) % 250);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        _ = acc;
        Console.WriteLine($"  world query allocation over 50k cells: {allocated} B");
        return allocated == 0;
    }

    private static bool WorldDeterministic()
    {
        var w = World.New();
        var g = Grid.New(w, 8, 0f, 0f, 256f);
        var l = Layer.New(w);
        var s = Stamp.Box(100);
        var d = WorldData(300, 256f, 9);
        foreach (var (x, y) in d) World.Place(w, l, x, y, 8f, s);
        World.Process(w);
        var a = World.Query(w, g, l, 100, 100);
        var b = World.Query(w, g, l, 37, 200);
        World.Process(w);
        return World.Query(w, g, l, 100, 100) == a && World.Query(w, g, l, 37, 200) == b;
    }

    private static bool WorldFadeDecays()
    {
        var w = World.New();
        var g = Grid.New(w, 8, 0f, 0f, 256f);
        var l = Layer.New(w);
        var s = Stamp.Box(100);
        var f = Fade.Stamp(50);
        World.Place(w, l, 128f, 128f, 8f, s, f);
        World.Process(w);
        var first = World.Query(w, g, l, 128, 128);
        World.Process(w);
        var second = World.Query(w, g, l, 128, 128);
        return first == 100 && second < first;
    }
}
