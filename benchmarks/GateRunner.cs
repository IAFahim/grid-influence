using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

internal static class GateRunner
{
    private const double DefaultTolerance = 0.05;

    private sealed record Child(string Label, string[] Args, Dictionary<string, string> Environment);

    public static int Run(string[] args)
    {
        var update = args.Contains("--update");
        var baselinePath = ValueOf(args, "--baseline") ?? Path.Combine(AppContext.BaseDirectory, "gate-baseline.json");
        var tolerance = double.TryParse(ValueOf(args, "--tolerance"), NumberStyles.Float, CultureInfo.InvariantCulture, out var t)
            ? t : DefaultTolerance;
        var times = new SortedDictionary<string, double>(StringComparer.Ordinal);
        var checksums = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var allocations = new SortedDictionary<string, long>(StringComparer.Ordinal);
        var host = "";
        var children = Children().ToArray();
        var owner = new Dictionary<string, int>();
        for (var index = 0; index < children.Length; index++)
        {
            host = Measure(children[index], times, checksums, allocations, owner, index, false) ?? host;
        }

        if (update)
        {
            File.WriteAllText(baselinePath, Serialize(host, times, checksums), new UTF8Encoding(false));
            Console.WriteLine($"gate: baseline written to {baselinePath} for host '{host}' ({times.Count} rows)");
            return allocations.Values.Any(v => v != 0) ? 1 : 0;
        }

        return Compare(baselinePath, host, tolerance, times, checksums, allocations, children, owner);
    }

    private static string? Measure(
        Child child, SortedDictionary<string, double> times, SortedDictionary<string, string> checksums,
        SortedDictionary<string, long> allocations, Dictionary<string, int> owner, int index, bool keepBest)
    {
        Console.Error.WriteLine("gate: " + child.Label + (keepBest ? " (again)" : ""));
        var output = Spawn(child);
        var fresh = new SortedDictionary<string, double>(StringComparer.Ordinal);
        string? host = null;
        if (child.Args[0] == "--frames") host = ParseFrames(output, child.Label, fresh, checksums, allocations);
        else ParsePrices(output, fresh);
        foreach (var (name, value) in fresh)
        {
            owner[name] = index;
            times[name] = keepBest && times.TryGetValue(name, out var previous) ? Math.Min(previous, value) : value;
        }

        return host;
    }

    private static double Slack(string name) => name.EndsWith(".ms", StringComparison.Ordinal) ? 0.002
        : name.EndsWith(".us", StringComparison.Ordinal) ? 0.2 : 3.0;

    private static IEnumerable<Child> Children()
    {
        var none = new Dictionary<string, string>();
        yield return new("B1 exact", ["--frames", "B1", "--precision", "exact"], none);
        yield return new("B1 cell", ["--frames", "B1", "--precision", "cell"], none);
        yield return new("B2 cell", ["--frames", "B2", "--precision", "cell"], none);
        yield return new("B4 cell", ["--frames", "B4", "--precision", "cell"], none);
        yield return new("C cell", ["--frames", "C", "--precision", "cell", "--layer", "transient", "--reads", "fused"], none);
        yield return new("W cell", ["--frames", "W", "--precision", "cell"], none);
        yield return new("W exact", ["--frames", "W", "--precision", "exact"], none);
        var tiered = new Dictionary<string, string> { ["DOTNET_TieredCompilation"] = "0", ["GI_PASSES"] = "10" };
        foreach (var mode in new[] { "--area", "--region", "--splat", "--order", "--changed", "--kinds", "--view" })
            yield return new("price " + mode, [mode], tiered);
    }

    private static string Spawn(Child child)
    {
        var dll = Assembly.GetExecutingAssembly().Location;
        var info = new ProcessStartInfo(Environment.ProcessPath!)
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        info.ArgumentList.Add(dll);
        foreach (var argument in child.Args) info.ArgumentList.Add(argument);
        foreach (var (key, value) in child.Environment) info.Environment[key] = value;
        using var process = Process.Start(info)!;
        var text = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return text;
    }

    private static string? ParseFrames(
        string output, string label, SortedDictionary<string, double> times,
        SortedDictionary<string, string> checksums, SortedDictionary<string, long> allocations)
    {
        string? host = null;
        foreach (var line in output.Split('\n'))
        {
            if (line.StartsWith("{\"header\"", StringComparison.Ordinal))
            {
                using var header = JsonDocument.Parse(line);
                host = header.RootElement.GetProperty("header").GetProperty("cpu_model").GetString();
                continue;
            }

            if (!line.StartsWith("{\"frame\"", StringComparison.Ordinal)) continue;

            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var name = "frame." + label.Replace(' ', '.') + (root.GetProperty("variant").GetString() is { Length: > 0 } v ? "." + v : "");
            times[name + ".p50_ms"] = root.GetProperty("p50_ms").GetDouble();
            checksums[name] = root.GetProperty("checksum").GetString()!;
            allocations[name] = root.GetProperty("bytes_per_frame").GetInt64();
        }

        return host;
    }

    private static readonly Regex Area = new(@"^area r=\s*(\d+):\s+([\d.]+) ns each", RegexOptions.Multiline);
    private static readonly Regex MaxNearest = new(@"^\s+(max|near)\s+r=\s*(\d+):\s+([\d.]+) ns each", RegexOptions.Multiline);
    private static readonly Regex Exclusion = new(
        @"^exclusion r=\s*(\d+): area\s+([\d.]+) ->\s+([\d.]+) ns; point ([\d.]+) -> ([\d.]+); gradient ([\d.]+) -> ([\d.]+)",
        RegexOptions.Multiline);
    private static readonly Regex Region = new(@"^region\s+(\d+)x\d+\s*: QueryRegion\s+(\d+) ns.*Query\(rect\)\s+(\d+) ns", RegexOptions.Multiline);
    private static readonly Regex Splat = new(@"^1M Morton splats: splat ([\d.]+) ms.*process ([\d.]+) ms", RegexOptions.Multiline);
    private static readonly Regex Fused = new(@"^1M fused reads: morton ([\d.]+) ms.*random ([\d.]+) ms", RegexOptions.Multiline);
    private static readonly Regex Order = new(@"^1M spatial keys ([\d.]+) ms, radix sort ([\d.]+) ms", RegexOptions.Multiline);
    private static readonly Regex Changed = new(@"ChangedTiles\(since\) on [\d,]+ tiles: ([\d.]+) µs; Changed\(disk r=64\): (\d+) ns", RegexOptions.Multiline);
    private static readonly Regex Kind = new(@"^(\S.*?)\s{2,}([\d.]+)\s+[\d.]+\s+([\d.]+)\s+[\d.]+\s*$", RegexOptions.Multiline);
    private static readonly Regex View = new(@"^view coherent cell read: ([\d.]+) ns", RegexOptions.Multiline);

    private static void ParsePrices(string output, SortedDictionary<string, double> times)
    {
        foreach (Match m in Area.Matches(output)) times[$"price.area.r{m.Groups[1].Value}.ns"] = Number(m, 2);
        foreach (Match m in MaxNearest.Matches(output)) times[$"price.{m.Groups[1].Value}.r{m.Groups[2].Value}.ns"] = Number(m, 3);
        foreach (Match m in Exclusion.Matches(output))
        {
            var r = m.Groups[1].Value;
            times[$"price.excluded.area.r{r}.ns"] = Number(m, 3);
            times[$"price.excluded.point.r{r}.ns"] = Number(m, 5);
            times[$"price.excluded.gradient.r{r}.ns"] = Number(m, 7);
        }

        foreach (Match m in Region.Matches(output))
        {
            times[$"price.region.{m.Groups[1].Value}.copy.ns"] = Number(m, 2);
            times[$"price.region.{m.Groups[1].Value}.sum.ns"] = Number(m, 3);
        }

        foreach (Match m in Splat.Matches(output))
        {
            times["price.splat.1m.ms"] = Number(m, 1);
            times["price.splat.process.ms"] = Number(m, 2);
        }

        foreach (Match m in Fused.Matches(output))
        {
            times["price.reads.morton.ms"] = Number(m, 1);
            times["price.reads.random.ms"] = Number(m, 2);
        }

        foreach (Match m in Order.Matches(output))
        {
            times["price.order.keys.ms"] = Number(m, 1);
            times["price.order.sort.ms"] = Number(m, 2);
        }

        foreach (Match m in Changed.Matches(output))
        {
            times["price.changed.tiles.us"] = Number(m, 1);
            times["price.changed.disk.ns"] = Number(m, 2);
        }

        foreach (Match m in View.Matches(output)) times["price.view.cell.ns"] = Number(m, 1);
        foreach (Match m in Kind.Matches(output))
        {
            var name = m.Groups[1].Value.Trim();
            if (name == "kind") continue;

            times[$"price.kind.{name.Replace(' ', '_')}.cached.ns"] = Number(m, 2);
            times[$"price.kind.{name.Replace(' ', '_')}.oneshot.ns"] = Number(m, 3);
        }
    }

    private static double Number(Match match, int group) => double.Parse(match.Groups[group].Value, CultureInfo.InvariantCulture);

    private static string Serialize(string host, SortedDictionary<string, double> times, SortedDictionary<string, string> checksums)
    {
        var text = new StringBuilder();
        text.Append("{\n  \"host\": \"").Append(host).Append("\",\n  \"rows\": {\n");
        var first = true;
        foreach (var (name, value) in times)
        {
            if (!first) text.Append(",\n");
            first = false;
            text.Append("    \"").Append(name).Append("\": ").Append(value.ToString("R", CultureInfo.InvariantCulture));
        }

        text.Append("\n  },\n  \"checksums\": {\n");
        first = true;
        foreach (var (name, value) in checksums)
        {
            if (!first) text.Append(",\n");
            first = false;
            text.Append("    \"").Append(name).Append("\": \"").Append(value).Append('"');
        }

        text.Append("\n  }\n}\n");
        return text.ToString();
    }

    private static int Compare(
        string path, string host, double tolerance, SortedDictionary<string, double> times,
        SortedDictionary<string, string> checksums, SortedDictionary<string, long> allocations,
        Child[] children, Dictionary<string, int> owner)
    {
        var failures = 0;
        foreach (var (name, bytes) in allocations)
            if (bytes != 0)
            {
                Console.WriteLine($"FAIL {name}: {bytes} B per frame");
                failures++;
            }

        if (!File.Exists(path))
        {
            Console.WriteLine($"gate: no baseline at {path}; run --gate --update on the pinned host");
            return 1;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var baselineHost = document.RootElement.GetProperty("host").GetString();
        if (baselineHost != host)
        {
            Console.WriteLine($"gate: baseline host '{baselineHost}' differs from '{host}'; timings are not comparable");
            return 1;
        }

        var suspects = new SortedSet<int>();
        foreach (var row in document.RootElement.GetProperty("rows").EnumerateObject())
            if (times.TryGetValue(row.Name, out var first) && Regressed(row.Name, row.Value.GetDouble(), first, tolerance))
                suspects.Add(owner[row.Name]);

        foreach (var index in suspects) Measure(children[index], times, checksums, allocations, owner, index, true);

        foreach (var row in document.RootElement.GetProperty("rows").EnumerateObject())
        {
            if (!times.TryGetValue(row.Name, out var current))
            {
                Console.WriteLine($"FAIL {row.Name}: missing from this run");
                failures++;
                continue;
            }

            var baseline = row.Value.GetDouble();
            var delta = baseline == 0 ? 0 : current / baseline - 1;
            var regressed = Regressed(row.Name, baseline, current, tolerance);
            Console.WriteLine($"{(regressed ? "FAIL" : "ok  ")} {row.Name,-44} {baseline,12:F3} -> {current,12:F3} ({delta * 100,+6:F1}%)");
            if (regressed) failures++;
        }

        foreach (var row in document.RootElement.GetProperty("checksums").EnumerateObject())
            if (!checksums.TryGetValue(row.Name, out var current) || current != row.Value.GetString())
            {
                Console.WriteLine($"FAIL checksum {row.Name}: {row.Value.GetString()} -> {checksums.GetValueOrDefault(row.Name, "missing")}");
                failures++;
            }

        Console.WriteLine(failures == 0 ? "gate: pass" : $"gate: {failures} failures");
        return failures == 0 ? 0 : 1;
    }

    private static bool Regressed(string name, double baseline, double current, double tolerance)
        => current > baseline * (1 + (name.Contains("random", StringComparison.Ordinal) ? 2 * tolerance : tolerance)) &&
            current - baseline > Slack(name);

    private static string? ValueOf(string[] args, string name)
    {
        var at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }
}
