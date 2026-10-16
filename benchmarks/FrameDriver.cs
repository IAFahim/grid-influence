using System.Diagnostics;

internal sealed class FrameResult
{
    public required string Name { get; init; }
    public required string Variant { get; init; }
    public required string Layout { get; init; }
    public required int Entities { get; init; }
    public required int Movers { get; init; }
    public required int GridCells { get; init; }
    public required int Warmup { get; init; }
    public required int Measure { get; init; }
    public required double P50Ns { get; init; }
    public required double P99Ns { get; init; }
    public required double MinNs { get; init; }
    public required double MaxNs { get; init; }
    public required double MeanNs { get; init; }
    public required double? TargetMs { get; init; }
    public required string[] StageNames { get; init; }
    public required double[] StageP50Ns { get; init; }
    public required double[] StageMeanNs { get; init; }
    public required int ResortInterval { get; init; }
    public required long[] ResortTicks { get; init; }
    public required long AllocatedBytes { get; init; }
    public required int Collections { get; init; }
    public required double CrossingCell { get; init; }
    public required double CrossingQ8 { get; init; }
    public required int CpuStart { get; init; }
    public required int CpuEnd { get; init; }
    public required double SetupSeconds { get; init; }
    public required double RssSetupMb { get; init; }
    public required double RssPeakMb { get; init; }
    public required ulong Checksum { get; init; }
    public required ulong FinalState { get; init; }
    public required bool ReadsComplete { get; init; }

    public bool Ok => ReadsComplete && AllocatedBytes == 0 && Checksum != 0;

    public string ToJson(string precision)
    {
        var entities = (double)Entities;
        var resortNs = ResortInterval > 0 && ResortTicks.Length > 0 ? MedianTicks() * 1e9 / Stopwatch.Frequency : 0.0;
        var amortizedNs = ResortInterval > 0 ? resortNs / ResortInterval : 0.0;
        var json = new FrameJson().BeginObject()
            .Text("frame", Name)
            .Text("variant", Variant)
            .Text("precision", precision)
            .Text("layout", Layout)
            .Integer("entities", Entities)
            .Integer("movers", Movers)
            .Integer("grid_cells", GridCells)
            .Integer("warmup_frames", Warmup)
            .Integer("measure_frames", Measure)
            .Number("p50_ms", P50Ns / 1e6, 6)
            .Number("p99_ms", P99Ns / 1e6, 6)
            .Number("min_ms", MinNs / 1e6, 6)
            .Number("max_ms", MaxNs / 1e6, 6)
            .Number("mean_ms", MeanNs / 1e6, 6)
            .Number("p50_ns", P50Ns, 1)
            .Number("p99_ns", P99Ns, 1)
            .Number("mean_ns", MeanNs, 1)
            .Number("p50_ns_per_entity", P50Ns / entities, 3)
            .Number("p99_ns_per_entity", P99Ns / entities, 3)
            .Number("mean_ns_per_entity", MeanNs / entities, 3);
        if (TargetMs.HasValue) json.Number("target_ms", TargetMs.Value, 4);
        else json.Null("target_ms");
        json.BeginArray("stages");
        for (var s = 0; s < StageNames.Length; s++)
            json.BeginObject()
                .Text("name", StageNames[s])
                .Number("p50_ns", StageP50Ns[s], 1)
                .Number("mean_ns", StageMeanNs[s], 1)
                .Number("p50_ns_per_entity", StageP50Ns[s] / entities, 3)
                .Number("mean_ns_per_entity", StageMeanNs[s] / entities, 3)
                .Number("mean_share", MeanNs > 0 ? StageMeanNs[s] / MeanNs : 0, 4)
                .EndObject();
        json.EndArray();
        if (ResortInterval > 0)
            json.BeginObject("resort")
                .Integer("interval_frames", ResortInterval)
                .Integer("events", ResortTicks.Length)
                .Number("median_ms", resortNs / 1e6, 6)
                .Number("amortized_ns_per_entity", amortizedNs / entities, 3)
                .Number("p50_ms_with_resort", (P50Ns + amortizedNs) / 1e6, 6)
                .Number("p99_ms_with_resort", (P99Ns + amortizedNs) / 1e6, 6)
                .Number("p50_ns_per_entity_with_resort", (P50Ns + amortizedNs) / entities, 3)
                .EndObject();
        else json.Null("resort");
        return json.Integer("allocated_bytes_total", AllocatedBytes)
            .Integer("bytes_per_frame", AllocatedBytes / Math.Max(Measure, 1))
            .Integer("gc_collections", Collections)
            .Number("crossing_cell", CrossingCell, 4)
            .Number("crossing_q8", CrossingQ8, 4)
            .Integer("cpu_start", CpuStart)
            .Integer("cpu_end", CpuEnd)
            .Number("setup_seconds", SetupSeconds, 2)
            .Number("rss_after_setup_mb", RssSetupMb, 1)
            .Number("rss_peak_mb", RssPeakMb, 1)
            .Flag("reads_complete", ReadsComplete)
            .Text("checksum", Checksum.ToString("x16", System.Globalization.CultureInfo.InvariantCulture))
            .Text("final_state", FinalState.ToString("x16", System.Globalization.CultureInfo.InvariantCulture))
            .Flag("ok", Ok)
            .EndObject()
            .ToString();
    }

    private double MedianTicks()
    {
        var sorted = (long[])ResortTicks.Clone();
        Array.Sort(sorted);
        return sorted[sorted.Length / 2];
    }
}

internal static unsafe class FrameDriver
{
    public static FrameResult Measure(FrameScenario scenario, FrameOptions options)
    {
        var width = scenario.Stages.Length + 1;
        var started = Stopwatch.GetTimestamp();
        scenario.Setup();
        var setupSeconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
        var rssSetup = FrameHost.ResidentMegabytes("VmRSS");

        var marks = stackalloc long[width];
        var samples = new long[options.Measure * width];
        for (var frame = 0; frame < options.Warmup; frame++) Execute(scenario, frame, marks, out _);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        scenario.ResetCounters();
        var collections = GC.CollectionCount(0);
        var cpuStart = Thread.GetCurrentProcessorId();
        var allocated = 0L;
        var complete = true;
        var checksum = FrameHash.Seed;
        for (var k = 0; k < options.Measure; k++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            complete &= Execute(scenario, options.Warmup + k, marks, out var frameHash);
            checksum = FrameHash.Mix(checksum, frameHash);
            allocated += GC.GetAllocatedBytesForCurrentThread() - before;
            for (var m = 0; m < width; m++) samples[k * width + m] = marks[m];
        }

        var cpuEnd = Thread.GetCurrentProcessorId();
        var collected = GC.CollectionCount(0) - collections;
        var finalState = scenario.FinalState();
        return Summarize(scenario, options, samples, width, allocated, collected, complete, checksum, finalState,
            cpuStart, cpuEnd, setupSeconds, rssSetup);
    }

    public static void WarmCode(Func<FrameScenario> create, int milliseconds)
    {
        using var scenario = create();
        scenario.Setup();
        var marks = stackalloc long[scenario.Stages.Length + 1];
        var started = Stopwatch.GetTimestamp();
        for (var frame = 0; ; frame++)
        {
            Execute(scenario, frame, marks, out _);
            if (frame >= 16 && Stopwatch.GetElapsedTime(started).TotalMilliseconds >= milliseconds) break;
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    public static bool Execute(FrameScenario scenario, int frame, long* marks, out ulong frameHash)
    {
        scenario.Step(frame);
        var complete = scenario.Run(frame, marks);
        frameHash = scenario.Consume();
        return complete;
    }

    private static FrameResult Summarize(FrameScenario scenario, FrameOptions options, long[] samples, int width,
        long allocated, int collections, bool complete, ulong checksum, ulong finalState, int cpuStart, int cpuEnd,
        double setupSeconds, double rssSetup)
    {
        var stages = width - 1;
        var frames = options.Measure;
        var toNs = 1e9 / Stopwatch.Frequency;
        var total = new long[frames];
        var perStage = new long[stages][];
        for (var s = 0; s < stages; s++) perStage[s] = new long[frames];
        for (var k = 0; k < frames; k++)
        {
            total[k] = samples[k * width + stages] - samples[k * width];
            for (var s = 0; s < stages; s++) perStage[s][k] = samples[k * width + s + 1] - samples[k * width + s];
        }

        var stageP50 = new double[stages];
        var stageMean = new double[stages];
        for (var s = 0; s < stages; s++)
        {
            var sorted = (long[])perStage[s].Clone();
            Array.Sort(sorted);
            stageP50[s] = FrameStats.Rank(sorted, 50) * toNs;
            stageMean[s] = perStage[s].Average() * toNs;
        }

        var ordered = (long[])total.Clone();
        Array.Sort(ordered);
        return new FrameResult
        {
            Name = scenario.Name,
            Variant = scenario.Variant,
            Layout = scenario.Layout,
            Entities = scenario.Entities,
            Movers = scenario.Movers,
            GridCells = scenario.Size,
            Warmup = options.Warmup,
            Measure = frames,
            P50Ns = FrameStats.Rank(ordered, 50) * toNs,
            P99Ns = FrameStats.Rank(ordered, 99) * toNs,
            MinNs = ordered[0] * toNs,
            MaxNs = ordered[^1] * toNs,
            MeanNs = total.Average() * toNs,
            TargetMs = scenario.TargetMs,
            StageNames = scenario.Stages,
            StageP50Ns = stageP50,
            StageMeanNs = stageMean,
            ResortInterval = scenario.ResortInterval,
            ResortTicks = scenario.ResortTicks,
            AllocatedBytes = allocated,
            Collections = collections,
            CrossingCell = scenario.CrossingCell,
            CrossingQ8 = scenario.CrossingQ8,
            CpuStart = cpuStart,
            CpuEnd = cpuEnd,
            SetupSeconds = setupSeconds,
            RssSetupMb = rssSetup,
            RssPeakMb = FrameHost.ResidentMegabytes("VmHWM"),
            Checksum = checksum,
            FinalState = finalState,
            ReadsComplete = complete,
        };
    }
}
