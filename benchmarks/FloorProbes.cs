using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Text;

internal static class FloorProbes
{
    private sealed record Registration(string[] Probes, Func<FloorSuite> Make);

    private sealed record LedgerRow(string Label, string[] Cases, double Low, double High, bool PerOp);

    private static readonly Registration[] Suites =
    [
        new(["P1"], () => new FloorQuantizeSuite()),
        new(["P2", "P3"], () => new FloorHistogramSuite()),
        new(["P4"], () => new FloorConvolveSuite()),
        new(["P5"], () => new FloorPublishSuite()),
        new(["P6"], () => new FloorReadsSuite()),
        new(["P7"], () => new FloorDisksSuite()),
        new(["P8"], () => new FloorSweepSuite()),
    ];

    private static readonly LedgerRow[] Ledger =
    [
        new("quantize 1M (ms)", ["P1/quantize-1M"], 0.05, 0.1, false),
        new("histogram 1M, coherent (ms)", ["P2/morton-uniform"], 0.3, 0.5, false),
        new("histogram 1M, coherent, best P3 (ms)",
            ["P3/interleaved2-morton-uniform", "P3/coalesce-morton-uniform"], 0.3, 0.5, false),
        new("histogram 1M, random (ms)", ["P2/random-uniform"], 1.5, 3, false),
        new("convolve 1024^2 box (ms)", ["P4/box4x4-int32"], 0.1, 0.2, false),
        new("convolve 1024^2 tent int64 (ms)", ["P4/tent6-int64"], 0.3, 0.6, false),
        new("publish 1,024 tiles (ms)", ["P5/publish-1024-tiles"], 0.15, 0.25, false),
        new("1M value+gradient reads, coherent 32 MB (ms)", ["P6/coherent-32MB"], 0.2, 0.4, false),
        new("1M value+gradient reads, random 32 MB (ms)", ["P6/random-32MB"], 3, 6, false),
        new("disk sum r=16 (ns)", ["P7/r16-1000-disks"], 50, 70, true),
        new("disk sum r=32 (ns)", ["P7/r32-1000-disks"], 100, 130, true),
        new("anchor sweep over 1M slots, 10% moved (ms)", ["P8/anchor-sweep-moved-10pct"], 0.2, 0.4, false),
    ];

    public static int Run(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        var wanted = new HashSet<string>();
        var maxIterations = FloorTimer.MaxIterations;
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == "--probes" && i + 1 < args.Length)
                foreach (var id in args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries)) wanted.Add(id.ToUpperInvariant());
            else if (args[i] == "--iters" && i + 1 < args.Length)
                maxIterations = Math.Clamp(int.Parse(args[++i], CultureInfo.InvariantCulture), 1, 100_000);
        }

        PrintEnvironment();
        var results = new List<FloorResult>();
        var failed = false;
        Console.WriteLine();
        foreach (var registration in Suites)
        {
            if (wanted.Count > 0 && !registration.Probes.Any(wanted.Contains)) continue;
            using var suite = registration.Make();
            var receipts = new FloorReceipts();
            suite.Receipt(receipts);
            foreach (var item in receipts.Items) Console.WriteLine(Describe(item));
            if (!receipts.AllPassed)
            {
                Console.WriteLine($"floor: receipts failed for {string.Join("+", registration.Probes)}; nothing timed for it");
                failed = true;
                continue;
            }

            Console.WriteLine(Row("probe", "variant", "ops", "ns_per_op", "mean_ms", "p50_ms", "p99_ms", "min_ms", "iters", "alloc_B", "checksum"));
            foreach (var floorCase in suite.Cases)
            {
                if (wanted.Count > 0 && !wanted.Contains(floorCase.Probe)) continue;
                var result = FloorTimer.Measure(floorCase, maxIterations);
                results.Add(result);
                Console.WriteLine(Row(floorCase.Probe, floorCase.Variant, floorCase.Ops.ToString(CultureInfo.InvariantCulture),
                    Fixed(result.NsPerOp, 4), Fixed(result.MeanMs, 4), Fixed(result.P50Ms, 4), Fixed(result.P99Ms, 4),
                    Fixed(result.MinMs, 4), result.Iterations.ToString(CultureInfo.InvariantCulture),
                    result.AllocatedBytes.ToString(CultureInfo.InvariantCulture),
                    result.Checksum.ToString("x16", CultureInfo.InvariantCulture)));
            }
        }

        var allocating = results.Count(r => r.AllocatedBytes != 0);
        Console.WriteLine();
        Console.WriteLine($"{(allocating == 0 ? "PASS" : "FAIL")} floor-timed-passes-allocate-0-bytes ({results.Count} cases)");
        PrintLedger(results);
        return failed || allocating != 0 ? 1 : 0;
    }

    public static int Receipts()
    {
        var failures = 0;
        foreach (var registration in Suites)
        {
            using var suite = registration.Make();
            var receipts = new FloorReceipts();
            suite.Receipt(receipts);
            foreach (var item in receipts.Items) Console.WriteLine(Describe(item));
            failures += receipts.Failures;
            GC.Collect();
        }

        Console.WriteLine(failures == 0 ? "floor probes: all receipts green" : $"floor probes: {failures} failures");
        return failures == 0 ? 0 : 1;
    }

    private static string Describe((string Name, bool Ok, string Note) item)
        => item.Note.Length > 0 ? $"SKIP {item.Name} ({item.Note})" : $"{(item.Ok ? "PASS" : "FAIL")} {item.Name}";

    private static void PrintLedger(List<FloorResult> results)
    {
        var byName = results.ToDictionary(r => $"{r.Case.Probe}/{r.Case.Variant}");
        Console.WriteLine();
        Console.WriteLine("ledger (roadmap section 2, P0-2 floors) against this run");
        Console.WriteLine(Row("row", "predicted", "p50", "p99", "verdict", "case"));
        foreach (var row in Ledger)
        {
            var found = row.Cases.Where(byName.ContainsKey).Select(c => byName[c]).ToList();
            if (found.Count == 0) continue;
            var best = found.MinBy(r => r.P50Ms)!;
            var p50 = row.PerOp ? best.NsPerOp : best.P50Ms;
            var p99 = row.PerOp ? best.P99Ms * 1e6 / best.Case.Ops : best.P99Ms;
            var verdict = p50 < row.Low ? "below" : p50 > row.High ? "above" : "within";
            Console.WriteLine(Row(row.Label, $"{Compact(row.Low)}-{Compact(row.High)}", Fixed(p50, 4), Fixed(p99, 4), verdict,
                $"{best.Case.Probe}/{best.Case.Variant}"));
        }
    }

    private static void PrintEnvironment()
    {
        var cpu = SchedGetCpu();
        Console.WriteLine("floor probes: no Gi calls, one thread, 1M splats, 1024^2 grid, 32x32 tiles, warm caches");
        Console.WriteLine($"cpu: {CpuModel()}");
        Console.WriteLine($"affinity: {StatusField("Cpus_allowed_list")}  running-on-cpu: {cpu}  max-mhz: {CpuFile(cpu, "cpufreq/cpuinfo_max_freq", 1000)}");
        Console.WriteLine($"cache: {Caches(cpu)}");
        Console.WriteLine($"runtime: {RuntimeInformation.FrameworkDescription}  os: {RuntimeInformation.OSDescription}");
        Console.WriteLine($"isa: path={(Avx2.IsSupported ? "avx2" : "scalar")} avx2={Avx2.IsSupported} avx512f={Avx512F.IsSupported} " +
            $"vector256={Vector256.IsHardwareAccelerated} vector512={Vector512.IsHardwareAccelerated} sse41={Sse41.IsSupported}");
        Console.WriteLine($"env: DOTNET_EnableHWIntrinsic={Env("DOTNET_EnableHWIntrinsic")} DOTNET_EnableAVX2={Env("DOTNET_EnableAVX2")} " +
            $"DOTNET_EnableAVX512F={Env("DOTNET_EnableAVX512F")}");
        Console.WriteLine($"timing: warm 12 passes, then {FloorTimer.MinIterations}-{FloorTimer.MaxIterations} measured passes " +
            $"(about {FloorTimer.BudgetMs / 1000:F0} s per case); p50 = lower median, p99 = ceil(0.99 n)-th smallest");
    }

    private static string Env(string name) => Environment.GetEnvironmentVariable(name) ?? "unset";

    private static string CpuModel()
    {
        try
        {
            foreach (var line in File.ReadLines("/proc/cpuinfo"))
                if (line.StartsWith("model name", StringComparison.Ordinal))
                    return line[(line.IndexOf(':') + 1)..].Trim();
        }
        catch (IOException)
        {
        }

        return "unknown";
    }

    private static string StatusField(string key)
    {
        try
        {
            foreach (var line in File.ReadLines("/proc/self/status"))
                if (line.StartsWith(key, StringComparison.Ordinal))
                    return line[(line.IndexOf(':') + 1)..].Trim();
        }
        catch (IOException)
        {
        }

        return "unknown";
    }

    private static string CpuFile(int cpu, string path, int divisor)
    {
        try
        {
            var text = File.ReadAllText($"/sys/devices/system/cpu/cpu{cpu}/{path}").Trim();
            return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? (value / divisor).ToString(CultureInfo.InvariantCulture)
                : text;
        }
        catch (IOException)
        {
            return "unknown";
        }
        catch (UnauthorizedAccessException)
        {
            return "unknown";
        }
    }

    private static string Caches(int cpu)
    {
        var builder = new StringBuilder();
        for (var index = 0; index < 5; index++)
        {
            var level = CpuFile(cpu, $"cache/index{index}/level", 1);
            var type = CpuFile(cpu, $"cache/index{index}/type", 1);
            var size = CpuFile(cpu, $"cache/index{index}/size", 1);
            if (level == "unknown" || type == "Instruction") continue;
            builder.Append($"L{level}{(type == "Data" ? "d" : "")}={size} ");
        }

        return builder.ToString().Trim();
    }

    [DllImport("libc", EntryPoint = "sched_getcpu")]
    private static extern int SchedGetCpuNative();

    private static int SchedGetCpu()
    {
        try
        {
            return SchedGetCpuNative();
        }
        catch (DllNotFoundException)
        {
            return -1;
        }
        catch (EntryPointNotFoundException)
        {
            return -1;
        }
    }

    private static string Fixed(double value, int digits) => value.ToString("F" + digits, CultureInfo.InvariantCulture);

    private static string Compact(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Row(params string[] cells)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < cells.Length; i++)
        {
            var width = cells.Length == 6
                ? i switch { 0 => 50, 1 => 10, 2 => 10, 3 => 10, 4 => 8, _ => 0 }
                : i switch { 0 => 5, 1 => 32, 2 => 9, 3 => 10, 4 => 9, 5 => 9, 6 => 9, 7 => 9, 8 => 6, 9 => 8, _ => 0 };
            builder.Append(cells[i].PadRight(width)).Append(' ');
        }

        return builder.ToString().TrimEnd();
    }
}
