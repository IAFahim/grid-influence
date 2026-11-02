using System.Diagnostics;
using System.Globalization;
using Gi;

internal static unsafe class J50Spike
{
    private sealed record Config(string Frame, int Entities, int Power, ulong Seed, int Warmup, int Measure, int Passes, int[] Workers);

    public static int Run(string[] args)
    {
        var entities = 200_000;
        var warmup = 10;
        var measure = 30;
        var passes = 3;
        var seed = 7UL;
        var workers = new[] { 0, 1, 4, 8 };
        for (var i = 1; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length) return Usage("missing value after " + args[i]);
            var value = args[i + 1];
            switch (args[i])
            {
                case "--entities" when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var v) && v >= 1000:
                    entities = v;
                    break;
                case "--warmup" when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var v) && v >= 0:
                    warmup = v;
                    break;
                case "--measure" when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var v) && v >= 1:
                    measure = v;
                    break;
                case "--passes" when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var v) && v >= 1:
                    passes = v;
                    break;
                case "--seed" when ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var s):
                    seed = s;
                    break;
                case "--workers":
                    workers = value.Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(p => int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out var w) ? w : -1)
                        .ToArray();
                    if (workers.Length == 0 || workers.Any(w => w is < 0 or > 64)) return Usage("bad --workers " + value);
                    break;
                default:
                    return Usage("bad option " + args[i] + " " + value);
            }
        }

        var failures = 0;
        failures |= RunFrame(new Config("C", entities, 10, seed, warmup, measure, passes, workers), () => new FrameCrowd(entities, 10, seed));
        failures |= RunFrame(new Config("W", entities, 11, seed, warmup, measure, passes, workers), () => new FrameWorld(entities, 11, seed));
        Console.Out.WriteLine("J50 verdict=" + (failures == 0 ? "PASS" : "FAIL"));
        return failures;
    }

    private static int Usage(string error)
    {
        Console.Error.WriteLine(error);
        Console.Error.WriteLine("usage: --j50 [--entities N] [--warmup N] [--measure N] [--passes N] [--seed N] [--workers 0,1,4,8]");
        return 2;
    }

    private static int RunFrame(Config cfg, Func<FrameScenario> create)
    {
        var failures = 0;
        string reference = "";
        var medians = new List<string>();
        var marks = stackalloc long[16];
        foreach (var w in cfg.Workers)
        {
            var passMedians = new List<double>();
            var passTotals = new List<double>();
            string hash = "";
            string sig = "";
            var ok = true;
            for (var p = 0; p < cfg.Passes; p++)
            {
                using var scenario = create();
                scenario.Setup();
                var width = scenario.Stages.Length + 1;
                var process = Array.IndexOf(scenario.Stages, "process");
                World.ForcedWorkers = w;
                for (var f = 0; f < cfg.Warmup; f++) FrameDriver.Execute(scenario, f, marks, out _);
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                var proc = new long[cfg.Measure];
                var total = new long[cfg.Measure];
                var checksum = FrameHash.Seed;
                var allocatedBefore = GC.GetTotalAllocatedBytes(false);
                World.ParallelBakedTiles = 0;
                for (var k = 0; k < cfg.Measure; k++)
                {
                    ok &= FrameDriver.Execute(scenario, cfg.Warmup + k, marks, out var frameHash);
                    checksum = FrameHash.Mix(checksum, frameHash);
                    proc[k] = marks[process + 1] - marks[process];
                    total[k] = marks[width - 1] - marks[0];
                }

                var allocated = GC.GetTotalAllocatedBytes(false) - allocatedBefore;
                var tiles = World.ParallelBakedTiles / Math.Max(cfg.Measure, 1);
                var verified = scenario.VerifyState();
                var state = scenario.FinalState();
                var layers = string.Join("+", scenario.LayerIds.Select(l => World.LayerHash(scenario.World, scenario.Grid, l).ToString("x16", CultureInfo.InvariantCulture)));
                hash = checksum.ToString("x16", CultureInfo.InvariantCulture)
                    + "/final" + state.ToString("x16", CultureInfo.InvariantCulture)
                    + "/layers" + layers;
                sig = FormattableString.Invariant($"complete={ok} verified={verified} {hash}");
                Array.Sort(proc);
                Array.Sort(total);
                var toMs = 1e3 / Stopwatch.Frequency;
                passMedians.Add(FrameStats.Rank(proc, 50) * toMs);
                passTotals.Add(FrameStats.Rank(total, 50) * toMs);
                Console.Out.WriteLine(FormattableString.Invariant(
                    $"J50 frame={cfg.Frame} workers={w} pass={p} process_ms={FrameStats.Rank(proc, 50) * toMs:F3} total_ms={FrameStats.Rank(total, 50) * toMs:F3} tiles_per_frame={tiles} alloc_bytes={allocated} sig={sig}"));
                if (w == 0 && p == 0) reference = sig;
                else if (sig != reference) ok = false;
            }

            passMedians.Sort();
            passTotals.Sort();
            var mid = passMedians[passMedians.Count / 2];
            var midTotal = passTotals[passTotals.Count / 2];
            medians.Add(FormattableString.Invariant($"J50 median frame={cfg.Frame} workers={w} process_ms={mid:F3} total_ms={midTotal:F3}"));
            if (w != 0 && sig != reference)
            {
                Console.Out.WriteLine(FormattableString.Invariant($"J50 MISMATCH frame={cfg.Frame} workers={w} sig={sig} reference={reference}"));
                failures++;
            }

            if (!ok) failures++;
            World.ForcedWorkers = 0;
        }

        foreach (var line in medians) Console.Out.WriteLine(line);
        Console.Out.WriteLine(FormattableString.Invariant($"J50 frame={cfg.Frame} byte_equal={(failures == 0 ? "yes" : "no")}"));
        return failures == 0 ? 0 : 1;
    }
}
