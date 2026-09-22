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
        Check("world-apply-matches-oracle", WorldMatchesOracle());
        Check("world-apply-allocates-0-bytes", WorldApplyAllocationFree());
        Check("world-query-allocates-0-bytes", WorldQueryAllocationFree());
        Check("world-apply-deterministic", WorldDeterministic());
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
        var oversized = new Stamp(InfluenceShape.Disc(Int2.Zero, 600_000, 10), Int2.Zero);
        var stats = field.Tick([oversized], 1);
        return stats.StampsDroppedSpanBudget == 1 && field.ActiveSlotCount == 0;
    }

    private unsafe struct WorldFixture
    {
        public Float2* Pos;
        public float* Bounds;
        public byte* Stamps;
        public byte* Fades;
        public int Count;
    }

    private static unsafe WorldFixture WorldData(int n, float extent, int seed)
    {
        var f = new WorldFixture
        {
            Pos = (Float2*)System.Runtime.InteropServices.NativeMemory.AlignedAlloc((nuint)(n * sizeof(Float2)), 64),
            Bounds = (float*)System.Runtime.InteropServices.NativeMemory.AlignedAlloc((nuint)(n * sizeof(float)), 64),
            Stamps = (byte*)System.Runtime.InteropServices.NativeMemory.Alloc((nuint)n),
            Fades = (byte*)System.Runtime.InteropServices.NativeMemory.Alloc((nuint)n),
            Count = n,
        };
        var rng = seed;
        for (var i = 0; i < n; i++)
        {
            rng = rng * 1664525 + 1013904223;
            f.Pos[i] = new Float2((rng % (uint)(extent * 2)) - extent * 0.9f, ((rng >> 8) % (uint)extent) * 0.95f);
            f.Bounds[i] = 8f;
            f.Stamps[i] = 0;
            f.Fades[i] = 0;
        }
        return f;
    }

    private static unsafe bool WorldMatchesOracle()
    {
        var w = World.New();
        var g0 = World.Grid(w, 8, -256f, 0f, 512f);
        var g1 = World.Grid(w, 6, 256f, 0f, 512f);
        var l = World.Layer(w);
        var s = Stamps.Box(100);
        var d = WorldData(500, 512f, 7);
        for (var i = 0; i < d.Count; i++) d.Stamps[i] = s;
        World.Queue(w, l, d.Pos, d.Bounds, d.Stamps, d.Fades, d.Count);
        World.Apply(w);
        var grids = new[] { (g: g0, ox: -256f, size: 512f, cells: 256), (g: g1, ox: 256f, size: 512f, cells: 64) };
        for (var i = 0; i < 60; i++)
        {
            var wx = d.Pos[i].X; var wy = d.Pos[i].Y;
            foreach (var (g, ox, size, cells) in grids)
            {
                var qx = (int)((wx - ox) * cells / size);
                var qy = (int)(wy * cells / size);
                if (qx < 0 || qx >= cells || qy < 0 || qy >= cells) continue;
                var expected = 0L;
                for (var j = 0; j < d.Count; j++)
                {
                    var mcx = (int)((d.Pos[j].X - ox) * cells / size);
                    var mcy = (int)(d.Pos[j].Y * cells / size);
                    var mr = (int)(d.Bounds[j] * cells / size);
                    if (Math.Abs(qx - mcx) <= mr && Math.Abs(qy - mcy) <= mr) expected += 100;
                }
                if (World.Cell(w, g, l, qx, qy) != Math.Clamp(expected, short.MinValue, short.MaxValue))
                    return false;
            }
        }
        return true;
    }

    private static unsafe bool WorldApplyAllocationFree()
    {
        var w = World.New();
        World.Grid(w, 8, 0f, 0f, 256f);
        World.Grid(w, 8, 256f, 0f, 256f);
        var l = World.Layer(w);
        var s = Stamps.Box(100);
        var d = WorldData(1000, 256f, 3);
        for (var i = 0; i < d.Count; i++) d.Stamps[i] = s;
        World.Queue(w, l, d.Pos, d.Bounds, d.Stamps, d.Fades, d.Count);
        for (var i = 0; i < 8; i++) World.Apply(w);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 64; i++) World.Apply(w);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"  world apply allocation over 64 applies: {allocated} B");
        return allocated == 0;
    }

    private static unsafe bool WorldQueryAllocationFree()
    {
        var w = World.New();
        var g = World.Grid(w, 8, 0f, 0f, 256f);
        var l = World.Layer(w);
        var s = Stamps.Box(100);
        var d = WorldData(200, 256f, 5);
        for (var i = 0; i < d.Count; i++) d.Stamps[i] = s;
        World.Queue(w, l, d.Pos, d.Bounds, d.Stamps, d.Fades, d.Count);
        World.Apply(w);
        World.Cell(w, g, l, 10, 10);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        long acc = 0;
        for (var i = 0; i < 50_000; i++) acc += World.Cell(w, g, l, i % 250, (i * 7) % 250);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        _ = acc;
        Console.WriteLine($"  world query allocation over 50k cells: {allocated} B");
        return allocated == 0;
    }

    private static unsafe bool WorldDeterministic()
    {
        var w = World.New();
        var g = World.Grid(w, 8, 0f, 0f, 256f);
        var l = World.Layer(w);
        var s = Stamps.Box(100);
        var d = WorldData(300, 256f, 9);
        for (var i = 0; i < d.Count; i++) d.Stamps[i] = s;
        World.Queue(w, l, d.Pos, d.Bounds, d.Stamps, d.Fades, d.Count);
        World.Apply(w);
        var a = World.Cell(w, g, l, 100, 100);
        var b = World.Cell(w, g, l, 37, 200);
        World.Apply(w);
        return World.Cell(w, g, l, 100, 100) == a && World.Cell(w, g, l, 37, 200) == b;
    }

    private static unsafe bool WorldFadeDecays()
    {
        var w = World.New();
        var g = World.Grid(w, 8, 0f, 0f, 256f);
        var l = World.Layer(w);
        var s = Stamps.Box(100);
        var f = Fade.Stamp(50);
        var pos = (Float2*)System.Runtime.InteropServices.NativeMemory.AlignedAlloc((nuint)sizeof(Float2), 64);
        var bounds = (float*)System.Runtime.InteropServices.NativeMemory.AlignedAlloc((nuint)sizeof(float), 64);
        var stamps = (byte*)System.Runtime.InteropServices.NativeMemory.Alloc(1);
        var fades = (byte*)System.Runtime.InteropServices.NativeMemory.Alloc(1);
        pos[0] = new Float2(128f, 128f); bounds[0] = 8f; stamps[0] = s; fades[0] = f;
        World.Queue(w, l, pos, bounds, stamps, fades, 1);
        World.Apply(w);
        var first = World.Cell(w, g, l, 128, 128);
        World.Apply(w);
        var second = World.Cell(w, g, l, 128, 128);
        return first == 100 && second < first;
    }
}
