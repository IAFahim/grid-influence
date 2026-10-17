using System.Diagnostics;
using System.Runtime.CompilerServices;
using Gi;

internal static class ViewTiming
{
    public static int Run()
    {
        var world = World.New();
        Grid.New(world, 10, 0f, 0f, 1024f);
        var layer = Layer.New(world, Layer.Precision.Cell);
        var box = Stamp.Box(4, 4, 7);
        var rng = new Random(7);
        for (var i = 0; i < 200_000; i++)
            World.Place(world, layer, (float)(rng.NextDouble() * 1000.0 + 12.0), (float)(rng.NextDouble() * 1000.0 + 12.0), box, 1);
        World.Process(world);

        var best = double.MaxValue;
        long checksum = 0;
        for (var pass = 0; pass < 12; pass++)
        {
            var started = Stopwatch.GetTimestamp();
            checksum += Sweep(world, layer);
            var elapsed = Stopwatch.GetElapsedTime(started).TotalNanoseconds / (1024.0 * 1024.0);
            if (pass > 1 && elapsed < best) best = elapsed;
        }

        var reference = 0L;
        for (var y = 0; y < 1024; y++)
        for (var x = 0; x < 1024; x++) reference += World.Query(world, 0, layer, x, y);
        var ok = reference * 12 == checksum;
        Console.WriteLine($"view coherent cell read: {best:F2} ns/cell over 1,048,576 cells, checksum {(ok ? "matches Query" : "MISMATCH")}");
        World.Free(world);
        Stamp.Free(box);
        return ok ? 0 : 1;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization | MethodImplOptions.NoInlining)]
    private static long Sweep(byte world, byte layer)
    {
        var view = World.View(world, 0, layer);
        long total = 0;
        for (var y = 0; y < 1024; y++)
        for (var x = 0; x < 1024; x++) total += view[x, y];
        return total;
    }
}
