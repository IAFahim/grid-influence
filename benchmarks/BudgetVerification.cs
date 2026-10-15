using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Gi;

internal static unsafe class BudgetVerification
{
    private static long _receipt;

    public static int Run(string[] arguments)
    {
        var count = arguments.Length > 1 ? int.Parse(arguments[1], CultureInfo.InvariantCulture) : 1_000_000;
        var frames = arguments.Length > 2 ? int.Parse(arguments[2], CultureInfo.InvariantCulture) : 30;
        var selected = arguments.Length > 3 ? arguments[3] : "canonical";
        if (count <= 0 || frames <= 0) return 2;
        ResolvePool.SerialOnly = true;
        var results = new List<Result>();
        foreach (var kind in new[] { "idle", "canonical", "area", "tent16", "bell16", "tent64", "bell64", "dense", "sparse", "exclude", "region", "nested", "derived", "churn", "schedules" })
        {
            if (selected != "all" && selected != kind) continue;
            results.Add(Measure(kind, count, frames));
        }

        var report = new
        {
            runtime = RuntimeInformation.FrameworkDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            intrinsics = Environment.GetEnvironmentVariable("DOTNET_EnableHWIntrinsic") != "0",
            serial = true,
            seed = 402026,
            results,
            receipt = _receipt
        };
        Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        return results.Count != 0 && results.All(result => result.AllocatedBytes == 0 && result.Receipt != 0) ? 0 : 1;
    }

    private static Result Measure(string kind, int count, int frames)
    {
        var power = kind == "dense" ? 10 : kind == "sparse" ? 14 : 12;
        var size = 1 << power;
        var width = (ushort)(kind.EndsWith("64", StringComparison.Ordinal) ? 64 : kind.EndsWith("16", StringComparison.Ordinal) ? 16 : 8);
        var world = World.New();
        var grid = Grid.New(world, power, 0, 0, size);
        if (kind == "nested")
        {
            Grid.New(world, power - 1, 0, 0, size);
            Grid.New(world, power - 2, 0, 0, size);
        }

        var layer = Layer.New(world);
        var readLayer = layer;
        if (kind == "derived")
        {
            var second = Layer.Sum(world, layer, 1, layer, 1);
            var third = Layer.Max(world, second, layer);
            readLayer = Layer.Sum(world, third, 1, layer, -1);
        }

        var stamp = kind.StartsWith("tent", StringComparison.Ordinal) ? Stamp.Tent(width, width, 16)
            : kind.StartsWith("bell", StringComparison.Ordinal) ? Stamp.Bell(width, width, 16)
            : Stamp.Box(width, width, 16);
        var ids = new int[count];
        var xs = new float[count];
        var ys = new float[count];
        var nextXs = new float[count];
        var nextYs = new float[count];
        var values = new short[count];
        var gradientXs = new float[count];
        var gradientYs = new float[count];
        var whole = new ulong[(count + 63) >> 6];
        var changed = new ulong[whole.Length];
        var times = new long[frames];
        var writeTimes = new long[frames];
        var processTimes = new long[frames];
        var readTimes = new long[frames];
        var region = new short[256 * 256];
        var random = new Random(402026);
        for (var i = 0; i < count; i++)
        {
            xs[i] = width + 65 + (float)random.NextDouble() * (size - 2 * width - 132);
            ys[i] = width + 65 + (float)random.NextDouble() * (size - 2 * width - 132);
            nextXs[i] = xs[i] + 0.5f;
            nextYs[i] = ys[i] + 0.25f;
            ids[i] = World.Place(world, layer, xs[i], ys[i], stamp, (sbyte)(i % 3 == 0 ? -1 : 1));
            if (ids[i] < 0) throw new InvalidOperationException("Budget setup failed.");
            if (kind == "schedules")
            {
                World.Fade(world, ids[i], 0, int.MaxValue / 4);
                World.Expire(world, ids[i], int.MaxValue / 2);
            }
        }

        World.Process(world);
        var receipt = 1L;
        fixed (int* source = ids)
        fixed (float* x = xs, y = ys, nx = nextXs, ny = nextYs, gx = gradientXs, gy = gradientYs)
        fixed (short* output = values, regionOutput = region)
        fixed (ulong* mask = whole, changes = changed)
        {
            for (var frame = -8; frame < frames; frame++)
            {
                var px = (frame & 1) == 0 ? x : nx;
                var py = (frame & 1) == 0 ? y : ny;
                var allocated = GC.GetTotalAllocatedBytes(true);
                var started = Stopwatch.GetTimestamp();
                if (kind == "churn")
                {
                    for (var i = 0; i < count; i++) World.Remove(world, source[i]);
                    for (var i = 0; i < count; i++)
                    {
                        source[i] = World.Place(world, layer, px[i], py[i], stamp, 1);
                        if (source[i] < 0) throw new InvalidOperationException("Budget churn failed.");
                    }
                }
                else if (kind != "idle") World.Sync(world, source, px, py, count);
                var ingested = Stopwatch.GetTimestamp();
                var since = World.Tick(world);
                World.Process(world);
                var processed = Stopwatch.GetTimestamp();
                var success = true;
                var total = 0L;
                if (kind != "idle")
                {
                    success &= World.SenseColumn(world, readLayer, px, py, count, output, mask);
                    success &= World.SenseGradientColumn(world, readLayer, px, py, null, count, gx, gy, mask);
                    total += World.ChangedMask(world, readLayer, px, py, 0, since, count, changes);
                    if (kind is "area" or "exclude")
                    {
                        var step = kind == "area" ? 10 : 1;
                        for (var i = 0; i < count; i += step)
                        {
                            long area;
                            if (kind == "exclude") success &= World.TrySenseArea(world, readLayer, px[i], py[i], 64, source[i], out area);
                            else success &= World.TrySenseArea(world, readLayer, px[i], py[i], 64, out area);
                            total += area;
                        }
                    }
                    if (kind == "region")
                    {
                        for (var i = 0; i < count; i++)
                        {
                            World.QueryRegion(world, grid, readLayer, (int)px[i] - 128, (int)py[i] - 128, 256, 256, regionOutput);
                            total += regionOutput[(i * 17) & (region.Length - 1)];
                        }
                    }
                }

                var finished = Stopwatch.GetTimestamp();
                var bytes = GC.GetTotalAllocatedBytes(true) - allocated;
                if (!success) throw new InvalidOperationException("Budget read failed.");
                for (var i = 0; i < count; i++) total = unchecked(total * 31 + output[i] + (int)gx[i] + (int)gy[i]);
                receipt = unchecked(receipt * 31 + total + World.Tick(world));
                if (frame < 0) continue;
                if (bytes != 0) throw new InvalidOperationException(FormattableString.Invariant($"Budget frame allocated {bytes} B."));
                times[frame] = finished - started;
                writeTimes[frame] = ingested - started;
                processTimes[frame] = processed - ingested;
                readTimes[frame] = finished - processed;
            }
        }

        _receipt = unchecked(_receipt * 31 + receipt);
        World.Free(world);
        Stamp.Free(stamp);
        Array.Sort(times);
        var scale = 1_000_000_000.0 / Stopwatch.Frequency;
        return new Result(kind, count, size, frames, times[frames / 2] * scale / count,
            times[Math.Min(frames - 1, (int)Math.Ceiling(frames * 0.99) - 1)] * scale / 1_000_000,
            writeTimes.Average() * scale / count, processTimes.Average() * scale / count,
            readTimes.Average() * scale / count, 0, receipt);
    }

    private sealed record Result(string Scenario, int Entities, int GridSize, int Frames, double MedianNsPerEntity,
        double P99FrameMs, double WriteNsPerEntity, double ProcessNsPerEntity, double ReadNsPerEntity,
        long AllocatedBytes, long Receipt);
}
