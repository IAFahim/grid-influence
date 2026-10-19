using System.Diagnostics;
using System.Globalization;
using System.Text;
using Gi.Tests;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

var minutes = 1.0;
var seed = 7_000_000;
var output = "fuzz-failures";
for (var i = 0; i < args.Length; i++)
    if (args[i] == "--fuzz" && i + 1 < args.Length)
    {
        minutes = double.Parse(args[++i], CultureInfo.InvariantCulture);
    }
    else if (args[i] == "--seed" && i + 1 < args.Length)
    {
        seed = int.Parse(args[++i], CultureInfo.InvariantCulture);
    }
    else if (args[i] == "--out" && i + 1 < args.Length)
    {
        output = args[++i];
    }
    else
    {
        Console.Error.WriteLine("usage: --fuzz <minutes> [--seed N] [--out directory]");
        return 2;
    }

var profiles = new[] { StreamProfile.Gate, StreamProfile.Heavy, StreamProfile.Kinds, StreamProfile.Transient };
var clock = Stopwatch.StartNew();
var streams = 0;
var failures = 0;
while (clock.Elapsed.TotalMinutes < minutes)
{
    var profile = profiles[streams % profiles.Length];
    var mode = profile == StreamProfile.Heavy
        ? (PrecisionMode)(3 + streams / profiles.Length % 2)
        : (PrecisionMode)(streams / profiles.Length % 5);
    var current = seed + streams;
    var setup = StreamGenerator.Generate(current, mode, profile);
    var mismatch = StreamRunner.TryRun(setup, setup.Commands);
    streams++;
    if (mismatch == null) continue;

    failures++;
    var minimized = StreamMinimizer.Minimize(setup, setup.Commands, null);
    Directory.CreateDirectory(output);
    var path = Path.Combine(output, $"fuzz-{current}.cs");
    File.WriteAllText(path, StreamMinimizer.PrintAsCSharpTest(setup, minimized), new UTF8Encoding(false));
    Console.WriteLine(
        $"FAIL seed {current} {profile} {mode}: {mismatch.Message} -> {minimized.Count} commands in {path}");
}

Console.WriteLine(
    $"fuzz: {streams} streams in {clock.Elapsed.TotalMinutes.ToString("F1", CultureInfo.InvariantCulture)} min, {failures} failures");
return failures == 0 ? 0 : 1;
