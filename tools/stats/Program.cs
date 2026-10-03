using System.Globalization;
using Gi;
using Gi.Stats;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;

if (!Options.TryParse(args, out var options, out var error))
{
    Report.WriteError(options, error);
    return 2;
}
if (options.Help)
{
    Console.WriteLine("""
        Gi stats — inspect a deterministic workload, without reflection.

        dotnet run --project tools/stats -c Release -- <command> [options]

        Commands:
          stats       Internal source, tile, queue, and stamp statistics (default)
          memory      Native memory breakdown, GC heap, and process memory
          profile     Verify first, then measure idle process, query, move + process
          verify      Correctness, native memory, and zero-allocation receipts

        Options:
          --format human|json    Output format (default: compact JSON; --json also accepted)
          --power <5..14>         Grid power; cells per side = 2^power (default: 8)
          --layers <1..32>        Independent layers (default: 3)
          --sources <0..1000000>  Persistent placements (default: 4000)
          --stamp box|raster     Stamp path (default: box)
          --stamp-size <1..256>  Stamp width and height (default: 16; raster >= 2)
          --iterations <n>       Frames per profile phase (default: 1000)
          --queries <n>          Cell queries per frame (default: 10000)
          --moves <n>            Sources moved per frame, capped to sources (default: 200)
          -h, --help             Show this help

        CPU counters: bash tools/stats/perf.sh stat [profile options]
        CPU samples:  bash tools/stats/perf.sh record [profile options]
        """);
    return 0;
}

var receipts = options.Command is "verify" or "profile" ? Receipts.Run() : [];
var success = true;
foreach (var receipt in receipts) success &= receipt.Passed;
if (!success || options.Command == "verify")
{
    Report.Write(options, success, receipts);
    return success ? 0 : 1;
}

var scene = Scene.Create(options);
try
{
    var measurements = options.Command == "profile" ? Profiler.Run(scene, options) : [];
    foreach (var measurement in measurements) success &= measurement.AllocatedBytes == 0;
    var snapshot = Inspection.Read(scene.World);
    Report.Write(options, success, receipts, snapshot, scene.Sum(), measurements);
    return success ? 0 : 1;
}
finally
{
    World.Clear(scene.World);
}
