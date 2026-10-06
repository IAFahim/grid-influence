using System.Diagnostics;

namespace Gi.Stats;

internal readonly record struct Measurement(string Name, long Operations, long ElapsedTicks, long AllocatedBytes, long Checksum)
{
    public double NanosecondsPerOperation => (double)ElapsedTicks * 1_000_000_000 / Stopwatch.Frequency / Operations;
}

internal static class Profiler
{
    public static Measurement[] Run(Scene scene, Options options)
    {
        for (var i = 0; i < 256; i++) World.Process(scene.World);
        Query(scene, options.Queries);
        for (var i = 0; i < 256; i++) Mutate(scene, i);
        var field = scene.Sum();

        var before = GC.GetAllocatedBytesForCurrentThread();
        var start = Stopwatch.GetTimestamp();
        for (var i = 0; i < options.Iterations; i++) World.Process(scene.World);
        var ticks = Stopwatch.GetTimestamp() - start;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        var output = scene.Sum();
        if (output != field) throw new InvalidOperationException("Idle process changed the field.");
        var idle = new Measurement("idle-process", options.Iterations, ticks, allocated, output);

        var expected = Query(scene, options.Queries);
        before = GC.GetAllocatedBytesForCurrentThread();
        start = Stopwatch.GetTimestamp();
        var checksum = 0L;
        for (var i = 0; i < options.Iterations; i++) checksum += Query(scene, options.Queries);
        ticks = Stopwatch.GetTimestamp() - start;
        allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        if (checksum != expected * options.Iterations) throw new InvalidOperationException("Query checksum changed.");
        var query = new Measurement("cell-query", (long)options.Iterations * options.Queries, ticks, allocated, checksum);

        var even = Mutate(scene, 0);
        var odd = Mutate(scene, 1);
        before = GC.GetAllocatedBytesForCurrentThread();
        start = Stopwatch.GetTimestamp();
        checksum = 0;
        for (var i = 0; i < options.Iterations; i++) checksum += Mutate(scene, i);
        ticks = Stopwatch.GetTimestamp() - start;
        allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        if (checksum != even * ((options.Iterations + 1L) / 2) + odd * (options.Iterations / 2L))
            throw new InvalidOperationException("Mutation checksum changed.");
        var mutation = new Measurement("move-and-process-frame", options.Iterations, ticks, allocated, checksum);
        return [idle, query, mutation];
    }

    private static long Query(Scene scene, int count)
    {
        var checksum = 0L;
        var mask = (uint)(scene.Cells - 1);
        for (var i = 0; i < count; i++)
            checksum += World.Query(scene.World, scene.Grid, (byte)(i % scene.Layers),
                (int)((uint)i & mask), (int)(unchecked((uint)i * 7u) & mask));
        return checksum;
    }

    private static long Mutate(Scene scene, int frame)
    {
        var mask = (uint)(scene.Cells - 1);
        var offset = (uint)(frame & 1) * 3u;
        for (var i = 0; i < scene.Moves; i++)
            World.Move(scene.World, i, ((unchecked((uint)i * 17u) + offset) & mask) + 0.25f,
                ((unchecked((uint)i * 31u) + offset) & mask) + 0.5f);
        World.Process(scene.World);
        return World.Query(scene.World, scene.Grid, 0, scene.Cells / 2, scene.Cells / 2);
    }
}
