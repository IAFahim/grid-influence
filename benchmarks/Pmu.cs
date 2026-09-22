using System.Diagnostics;
using GridInfluence;
using GridInfluence.Io;

namespace Benchmarks;

internal static class Pmu
{
    private const long WarmupTicks = 64;
    private const long MeasuredTicks = 2048;
    private const long QueryWarmup = 100_000;
    private const long QueryMeasured = 4_000_000;

    internal static int Run(string scenario)
    {
        return scenario switch
        {
            "field-tick" => MeasureTicks(scenario, 256, 1024),
            "field-tick-wide" => MeasureTicks(scenario, 256, 2048),
            "naive-tick" => MeasureNaive(scenario, 256, 1024),
            "naive-tick-wide" => MeasureNaive(scenario, 256, 2048),
            "query" => MeasureQuery(scenario),
            "ppm-decode" => MeasureDecode(scenario),
            "ppm-encode" => MeasureEncode(scenario),
            "world-apply" => MeasureWorldApply(scenario, 100_000, 1),
            "world-apply-multi" => MeasureWorldApply(scenario, 100_000, 32),
            "world-query" => MeasureWorldQuery(scenario),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
    }

    private static int MeasureTicks(string scenario, int stampCount, int extent)
    {
        var stamps = Fixtures.BuildStamps(stampCount, extent);
        using var pipeline = new PipelineField(5);
        for (var tick = 0; tick < WarmupTicks; tick++) pipeline.Tick(stamps);

        Console.WriteLine($"ready {MeasuredTicks}");
        if (Console.ReadLine() != "go") throw new InvalidOperationException();

        long receipt = 0;
        for (var tick = 0; tick < MeasuredTicks; tick++)
        {
            var stats = pipeline.Tick(stamps);
            receipt ^= stats.ActiveSlots;
        }

        Console.WriteLine($"{receipt} {MeasuredTicks}");
        _ = Console.ReadLine();
        return 0;
    }

    private static int MeasureNaive(string scenario, int stampCount, int extent)
    {
        var stamps = Fixtures.BuildStamps(stampCount, extent);
        var naive = new NaiveField(extent, Fixtures.DecayPerMille, Fixtures.SpreadDenominator);
        for (var tick = 0; tick < WarmupTicks; tick++) naive.Tick(stamps);

        Console.WriteLine($"ready {MeasuredTicks}");
        if (Console.ReadLine() != "go") throw new InvalidOperationException();

        long receipt = 0;
        for (var tick = 0; tick < MeasuredTicks; tick++)
        {
            naive.Tick(stamps);
            receipt ^= naive[0, 0];
        }

        Console.WriteLine($"{receipt} {MeasuredTicks}");
        _ = Console.ReadLine();
        return 0;
    }

    private static int MeasureQuery(string scenario)
    {
        const int extent = 1024;
        var stamps = Fixtures.BuildStamps(256, extent);
        using var pipeline = new PipelineField(5);
        for (var tick = 0; tick < 8; tick++) pipeline.Tick(stamps);

        var reader = pipeline.Front.AsReader();
        for (var i = 0; i < QueryWarmup; i++) _ = reader.Gradient(new Int2((i * 13) % 1022 + 1, (i * 7) % 1022 + 1)).X;

        Console.WriteLine($"ready {QueryMeasured}");
        if (Console.ReadLine() != "go") throw new InvalidOperationException();

        long receipt = 0;
        for (var i = 0; i < QueryMeasured; i++) receipt ^= receiptOp(i, reader);

        Console.WriteLine($"{receipt} {QueryMeasured}");
        _ = Console.ReadLine();
        return 0;

        static long receiptOp(int i, FieldReader reader)
            => reader.Gradient(new Int2((i * 13) % 1022 + 1, (i * 7) % 1022 + 1)).X;
    }

    private static int MeasureDecode(string scenario)
    {
        const int size = 1024;
        var samples = new int[size * size];
        var state = 0xABCDEF01u;
        for (var i = 0; i < samples.Length; i++)
        {
            state = state * 1664525u + 1013904223u;
            samples[i] = (int)(state % 65535u) - 32768;
        }

        var path = Path.Combine(Path.GetTempPath(), "tlinfluence-pmu.pgm");
        Pnm.SaveGraySigned(path, samples, size, size, 65535);
        var bytes = File.ReadAllBytes(path);
        File.Delete(path);
        var header = Pnm.ParseHeader(bytes);
        var map = new WeightMap(size, size, 65535, 32768);

        for (var i = 0; i < WarmupTicks; i++) Decode(header, bytes, map);

        Console.WriteLine($"ready {MeasuredTicks}");
        if (Console.ReadLine() != "go") throw new InvalidOperationException();

        long receipt = 0;
        for (var i = 0; i < MeasuredTicks; i++) receipt ^= Decode(header, bytes, map);

        Console.WriteLine($"{receipt} {MeasuredTicks}");
        _ = Console.ReadLine();
        map.Dispose();
        return 0;

        static long Decode(PnmHeader header, byte[] bytes, WeightMap map)
        {
            unsafe
            {
                fixed (byte* source = bytes)
                {
                    Pnm.DecodeGray(header, new ReadOnlySpan<byte>(source, bytes.Length), map.SamplePointer, map.Bias);
                }
            }

            return map.Samples[0];
        }
    }

    private static int MeasureEncode(string scenario)
    {
        const int size = 1024;
        var samples = new int[size * size];
        var state = 0xABCDEF01u;
        for (var i = 0; i < samples.Length; i++)
        {
            state = state * 1664525u + 1013904223u;
            samples[i] = (int)(state % 65535u) - 32768;
        }

        using var stream = new MemoryStream(size * size * 2 + 64);
        for (var i = 0; i < WarmupTicks; i++) Encode(stream, samples, size);

        Console.WriteLine($"ready {MeasuredTicks}");
        if (Console.ReadLine() != "go") throw new InvalidOperationException();

        long receipt = 0;
        for (var i = 0; i < MeasuredTicks; i++) receipt ^= Encode(stream, samples, size);

        Console.WriteLine($"{receipt} {MeasuredTicks}");
        _ = Console.ReadLine();
        return 0;

        static long Encode(MemoryStream stream, int[] samples, int size)
        {
            stream.Seek(0, SeekOrigin.Begin);
            Pnm.SaveGray(samples, size, size, stream, 65535);
            return stream.Length;
        }
    }

    private static unsafe int MeasureWorldApply(string scenario, int items, int grids)
    {
        var w = World.New();
        for (var i = 0; i < grids; i++) World.Grid(w, 8, i * 256f, 0f, 256f);
        var l = World.Layer(w);
        var s = Stamps.Box(100);
        var pos = (Float2*)System.Runtime.InteropServices.NativeMemory.AlignedAlloc((nuint)(items * sizeof(Float2)), 64);
        var bounds = (float*)System.Runtime.InteropServices.NativeMemory.AlignedAlloc((nuint)(items * sizeof(float)), 64);
        var stamps = (byte*)System.Runtime.InteropServices.NativeMemory.Alloc((nuint)items);
        var fades = (byte*)System.Runtime.InteropServices.NativeMemory.Alloc((nuint)items);
        var rng = 11;
        for (var i = 0; i < items; i++)
        {
            rng = rng * 1664525 + 1013904223;
            pos[i] = new Float2(rng % (uint)(220 * grids) + 18f, (rng >> 8) % 220 + 18f);
            bounds[i] = 8f; stamps[i] = s; fades[i] = 0;
        }
        World.Queue(w, l, pos, bounds, stamps, fades, items);
        for (var i = 0; i < WarmupTicks; i++) World.Apply(w);

        Console.WriteLine($"ready {MeasuredTicks}");
        if (Console.ReadLine() != "go") throw new InvalidOperationException();

        long receipt = 0;
        for (var i = 0; i < MeasuredTicks; i++)
        {
            World.Apply(w);
            receipt ^= World.Cell(w, 0, l, 128, 128);
        }

        Console.WriteLine($"{receipt} {MeasuredTicks}");
        _ = Console.ReadLine();
        return 0;
    }

    private static unsafe int MeasureWorldQuery(string scenario)
    {
        var w = World.New();
        var g = World.Grid(w, 8, 0f, 0f, 256f);
        var l = World.Layer(w);
        var s = Stamps.Box(100);
        var pos = (Float2*)System.Runtime.InteropServices.NativeMemory.AlignedAlloc((nuint)(2000 * sizeof(Float2)), 64);
        var bounds = (float*)System.Runtime.InteropServices.NativeMemory.AlignedAlloc((nuint)(2000 * sizeof(float)), 64);
        var stamps = (byte*)System.Runtime.InteropServices.NativeMemory.Alloc(2000);
        var fades = (byte*)System.Runtime.InteropServices.NativeMemory.Alloc(2000);
        for (var i = 0; i < 2000; i++)
        {
            pos[i] = new Float2(i % 240 + 8f, i * 7 % 240 + 8f);
            bounds[i] = 8f; stamps[i] = s; fades[i] = 0;
        }
        World.Queue(w, l, pos, bounds, stamps, fades, 2000);
        World.Apply(w);
        for (var i = 0; i < QueryWarmup; i++) _ = World.Cell(w, g, l, i % 256, (i * 3) % 256);

        Console.WriteLine($"ready {QueryMeasured}");
        if (Console.ReadLine() != "go") throw new InvalidOperationException();

        long receipt = 0;
        for (var i = 0; i < QueryMeasured; i++) receipt += World.Cell(w, g, l, i % 256, (i * 3) % 256);

        Console.WriteLine($"{receipt} {QueryMeasured}");
        _ = Console.ReadLine();
        return 0;
    }
}
