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
        Check("pnm-signed-roundtrip", PnmRoundTrip());
        Check("region-write-read-roundtrip", RegionRoundTrip());
        Check("warm-tick-allocates-0-bytes", TickAllocationFree());
        Check("warm-query-allocates-0-bytes", QueryAllocationFree());
        Check("budget-drops-deterministic", BudgetDrops());

        Console.WriteLine(failures == 0 ? "verification: all receipts green" : $"verification: {failures} failures");
        return failures == 0 ? 0 : 1;
    }

    private static bool MatchesNaive(bool decay)
    {
        const int extent = 128;
        var stamps = Fixtures.BuildStamps(64, extent);
        using var pipeline = new PipelineField(4);
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
        using var field = new InfluenceField(GridSpec.FromPowerOfTwo(3, uint.MaxValue));
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
        using var field = new InfluenceField(GridSpec.FromPowerOfTwo(3, uint.MaxValue));
        var oversized = new Stamp(InfluenceShape.Disc(Int2.Zero, 600_000, 10), Int2.Zero);
        var stats = field.Tick([oversized], 1);
        return stats.StampsDroppedSpanBudget == 1 && field.ActiveSlotCount == 0;
    }
}
