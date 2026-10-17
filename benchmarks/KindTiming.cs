using System.Diagnostics;
using Gi;

internal static class KindTiming
{
    private const int Sources = 50_000;
    private const int Frames = 24;

    private readonly record struct Kind(string Name, Func<byte> Make, int Cells, bool Turns);

    public static int Run()
    {
        var kinds = new[]
        {
            new Kind("box 8x8", () => Stamp.Box(8, 8, 3), 64, false),
            new Kind("tent 8x8", () => Stamp.Tent(8, 8, 3), 64, false),
            new Kind("bell 8x8", () => Stamp.Bell(8, 8, 3), 64, false),
            new Kind("disk r4", () => Stamp.Disk(4, 3), 81, false),
            new Kind("cone r4", () => Stamp.Cone(4, 3), 81, false),
            new Kind("dome r4", () => Stamp.Dome(4, 3), 81, false),
            new Kind("arc120 r4", () => Stamp.Disk(4, 3, 120), 81, true),
            new Kind("raster 8x8", () => RasterStamp(), 64, true),
        };

        Console.WriteLine($"{"kind",-12} {"cell-hit ns/src",16} {"ns/cell",9} {"exact-free-angle ns/src",25} {"ns/cell",9}");
        foreach (var kind in kinds)
        {
            var hit = Measure(kind, Layer.Precision.Cell, false);
            var free = Measure(kind, Layer.Precision.Exact, true);
            Console.WriteLine($"{kind.Name,-12} {hit,16:F1} {hit / kind.Cells,9:F2} {free,25:F1} {free / kind.Cells,9:F2}");
        }

        return 0;
    }

    private static byte RasterStamp()
    {
        var data = new sbyte[64];
        for (var i = 0; i < data.Length; i++) data[i] = (sbyte)(i % 9 - 3);
        return Stamp.New(data, 8, 8);
    }

    private static double Measure(Kind kind, Layer.Precision precision, bool freeAngle)
    {
        var world = World.New();
        Grid.New(world, 10, 0f, 0f, 1024f);
        var layer = Layer.New(world, precision);
        var stamp = kind.Make();
        var rng = new Random(11);
        var ids = new World.SourceId[Sources];
        var xs = new float[Sources];
        var ys = new float[Sources];
        for (var i = 0; i < Sources; i++)
        {
            xs[i] = 40f + (float)rng.NextDouble() * 940f;
            ys[i] = 40f + (float)rng.NextDouble() * 940f;
            ids[i] = World.Place(world, layer, xs[i], ys[i], stamp, 1);
        }

        World.Process(world);
        var samples = new double[Frames];
        for (var frame = 0; frame < Frames; frame++)
        {
            for (var i = 0; i < Sources; i++)
            {
                xs[i] += frame % 2 == 0 ? 1f : -1f;
                World.Move(world, ids[i], xs[i], ys[i]);
                if (freeAngle && kind.Turns) World.Turn(world, ids[i], (float)((i * 7 + frame * 13) % 6283) / 1000f);
            }

            var started = Stopwatch.GetTimestamp();
            World.Process(world);
            samples[frame] = Stopwatch.GetElapsedTime(started).TotalNanoseconds / Sources;
        }

        Array.Sort(samples);
        World.Free(world);
        Stamp.Free(stamp);
        return samples[Frames / 2];
    }
}
