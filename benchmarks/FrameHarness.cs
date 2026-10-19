using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

internal static class ReadMode
{
    public static bool Fused;
    public static bool View;
    public static bool Transient;
}

internal sealed class FrameOptions
{
    public bool Cell;
    public int Entities = 1_000_000;
    public bool Fused;
    public int JitMilliseconds = 1500;
    public int Measure = 300;
    public ulong Seed = 402026;
    public string Selection = "ALL";
    public bool Transient;
    public bool View;
    public int Warmup = 30;
}

internal sealed record FramePlan(
    string Name,
    Func<FrameOptions, FrameScenario> Create,
    Func<bool, FrameScenario> CreateSmall);

internal static class FrameHarness
{
    private const int SmallEntities = 20_000;
    private const int SmallPower = 8;

    private static readonly FramePlan[] Catalog =
    [
        new("B1", o => new FrameLegacy(o.Entities, 12, o.Seed, o.Cell),
            cell => new FrameLegacy(SmallEntities, SmallPower, 1, cell)),
        new("B2", o => new FrameSparse(o.Entities, 12, o.Seed, 1, o.Cell),
            cell => new FrameSparse(SmallEntities, SmallPower, 1, 1, cell)),
        new("B2", o => new FrameSparse(o.Entities, 12, o.Seed, 200, o.Cell),
            cell => new FrameSparse(SmallEntities, SmallPower, 1, 200, cell)),
        new("B4", o => new FrameIdle(o.Entities, 12, o.Seed, o.Cell),
            cell => new FrameIdle(SmallEntities, SmallPower, 1, cell)),
        new("C", o => new FrameCrowd(o.Entities, 10, o.Seed, o.Cell),
            cell => new FrameCrowd(SmallEntities, SmallPower, 1, cell)),
        new("W", o => new FrameWorld(o.Entities, 11, o.Seed, o.Cell),
            cell => new FrameWorld(SmallEntities, SmallPower, 1, cell))
    ];

    public static int Run(string[] args)
    {
        if (!TryParse(args, out var options, out var error))
        {
            Console.Error.WriteLine(error);
            return 2;
        }

        if (options.Selection == "VERIFY") return FrameReceipts.Run(Console.Out) == 0 ? 0 : 1;

        var plans = Catalog.Where(p => options.Selection == "ALL" || options.Selection == p.Name).ToArray();
        if (plans.Length == 0)
        {
            Console.Error.WriteLine("unknown frame '" + options.Selection +
                                    "'; expected B1, B2, B4, C, W, all or verify.");
            return 2;
        }

        var receipts = new StringWriter(CultureInfo.InvariantCulture);
        var failures = FrameReceipts.Run(receipts);
        if (failures != 0)
        {
            Console.Error.Write(receipts.ToString());
            Console.Error.WriteLine("frame receipts failed; no timing was taken.");
            return 1;
        }

        ReadMode.Fused = options.Fused;
        ReadMode.View = options.View;
        ReadMode.Transient = options.Transient;
        var precision = options.Cell ? "cell" : "exact";
        Console.Out.Write(Header(options, precision,
            receipts.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length));
        Console.Out.Write('\n');
        Console.Out.Flush();

        var ok = true;
        foreach (var plan in plans)
        {
            Console.Error.WriteLine("frames: warming code paths for " + plan.Name);
            FrameDriver.WarmCode(() => plan.CreateSmall(options.Cell), options.JitMilliseconds);
            GC.Collect();
            using var scenario = plan.Create(options);
            Console.Error.WriteLine("frames: measuring " + plan.Name + " " + scenario.Variant);
            var result = FrameDriver.Measure(scenario, options);
            Console.Out.Write(result.ToJson(precision));
            Console.Out.Write('\n');
            Console.Out.Flush();
            ok &= result.Ok;
        }

        return ok ? 0 : 1;
    }

    private static string Header(FrameOptions options, string precision, int receiptCount)
    {
        return new FrameJson().BeginObject()
            .BeginObject("header")
            .Text("tool", "gi-frames")
            .Integer("version", 1)
            .Text("engine",
                "engine-v2: change-rate Process, transient layers with Splat; frame C runs as a transient layer unless --layer persistent")
            .Text("cpu_model", FrameHost.CpuModel())
            .Integer("cpu_id", Thread.GetCurrentProcessorId())
            .Integer("affinity_cpus", Environment.ProcessorCount)
            .Text("runtime", RuntimeInformation.FrameworkDescription)
            .Text("architecture", RuntimeInformation.ProcessArchitecture.ToString())
            .Flag("hw_intrinsics", Vector128.IsHardwareAccelerated)
            .Flag("avx2", Avx2.IsSupported)
            .Flag("serial_only", true)
            .Text("precision", precision)
            .Integer("seed", (long)options.Seed)
            .Integer("warmup_frames", options.Warmup)
            .Integer("measure_frames", options.Measure)
            .Text("percentile", "nearest-rank")
            .Number("timestamp_overhead_ns", FrameStats.TimestampOverheadNs(), 2)
            .Text("receipts", "green")
            .Integer("receipt_lines", receiptCount)
            .EndObject()
            .EndObject()
            .ToString();
    }

    private static bool TryParse(string[] args, out FrameOptions options, out string error)
    {
        options = new FrameOptions();
        error = "";
        if (args.Length < 2)
        {
            error =
                "usage: --frames <B1|B2|B4|C|W|all|verify> [--precision exact|cell] [--measure N] [--warmup N] [--entities N] [--seed N] [--jit-ms N]";
            return false;
        }

        options.Selection = args[1].ToUpperInvariant();
        for (var i = 2; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length)
            {
                error = "missing value after " + args[i];
                return false;
            }

            var value = args[i + 1];
            switch (args[i])
            {
                case "--precision":
                    if (value == "cell")
                    {
                        options.Cell = true;
                    }
                    else if (value != "exact")
                    {
                        error = "--precision expects exact or cell";
                        return false;
                    }

                    break;
                case "--measure" when TryInt(value, 1, out var measure):
                    options.Measure = measure;
                    break;
                case "--warmup" when TryInt(value, 0, out var warmup):
                    options.Warmup = warmup;
                    break;
                case "--entities" when TryInt(value, 1000, out var entities):
                    options.Entities = entities;
                    break;
                case "--jit-ms" when TryInt(value, 0, out var jit):
                    options.JitMilliseconds = jit;
                    break;
                case "--seed" when ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seed):
                    options.Seed = seed;
                    break;
                case "--layer" when value is "persistent" or "transient":
                    options.Transient = value == "transient";
                    break;
                case "--reads" when value is "fused" or "separate" or "view":
                    options.Fused = value == "fused";
                    options.View = value == "view";
                    break;
                default:
                    error = "bad option " + args[i] + " " + value;
                    return false;
            }
        }

        return true;
    }

    private static bool TryInt(string text, int minimum, out int value)
    {
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= minimum;
    }
}
