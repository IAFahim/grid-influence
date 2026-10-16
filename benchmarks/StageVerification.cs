using System.Diagnostics;
using Gi;

internal static partial class Verification
{
    private static bool MixedStampProcessAllocatesZeroBytes()
    {
        var world = World.New();
        Grid.New(world, 10, 0f, 0f, 1024f);
        Grid.New(world, 8, 0f, 0f, 1024f);
        Layer.New(world);
        Layer.New(world);
        Layer.Sum(world, 0, 2, 1, -1);

        var stamps = new[]
        {
            Stamp.Box(8, 8, 50), Stamp.Tent(16, 16, 90), Stamp.Bell(16, 16, 80),
            Stamp.Disk(8, 90), Stamp.Cone(16, 90, 90),
        };
        var rng = new Random(35);
        const int count = 4000;
        var ids = new Gi.World.SourceId[count];
        for (var i = 0; i < count; i++)
        {
            var layer = (byte)(i % 2);
            var x = (float)(rng.NextDouble() * 1000);
            var y = (float)(rng.NextDouble() * 1000);
            ids[i] = World.Place(world, layer, x, y, stamps[i % stamps.Length], (sbyte)(1 + i % 8));
        }

        var matches = true;
        for (var frame = 0; frame < 12 && matches; frame++)
        {
            for (var i = 0; i < count; i += 20)
            {
                var x = (float)(rng.NextDouble() * 1000);
                var y = (float)(rng.NextDouble() * 1000);
                World.Move(world, ids[i], x, y);
                World.Turn(world, ids[i], (float)(rng.NextDouble() * Math.PI * 2));
            }

            if (frame == 3)
                for (var i = 0; i < count; i += 13)
                    World.Remove(world, ids[i]);

            World.Process(world);
        }

        var allocated = 0L;
        if (matches)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var frame = 0; frame < 6; frame++)
            {
                for (var i = 0; i < count; i += 20)
                {
                    World.Move(world, ids[i], (float)(rng.NextDouble() * 1000), (float)(rng.NextDouble() * 1000));
                    World.Turn(world, ids[i], (float)(rng.NextDouble() * Math.PI * 2));
                }

                World.Process(world);
            }

            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        }

        World.Free(world);
        foreach (var stamp in stamps) matches &= Stamp.Free(stamp);
        return matches && allocated == 0;
    }

    private static void MeasureStages(int count, byte stamp, int movers, int frames)
    {
        var world = World.New();
        Grid.New(world, 10, 0f, 0f, 1024f);
        Layer.New(world);
        var ids = new Gi.World.SourceId[count];
        var rng = new Random(77);
        for (var i = 0; i < count; i++)
            ids[i] = World.Place(world, 0, (float)(rng.NextDouble() * 1000), (float)(rng.NextDouble() * 1000), stamp, 4);
        World.Process(world);

        for (var frame = 0; frame < 8; frame++)
        {
            for (var i = 0; i < movers; i++)
                World.Move(world, ids[i * 4 % count], (float)(rng.NextDouble() * 1000), (float)(rng.NextDouble() * 1000));
            World.Process(world);
        }

        ProcessStages.Reset();
        ProcessStages.Measuring = true;
        for (var frame = 0; frame < frames; frame++)
        {
            for (var i = 0; i < movers; i++)
                World.Move(world, ids[i * 4 % count], (float)(rng.NextDouble() * 1000), (float)(rng.NextDouble() * 1000));
            World.Process(world);
        }

        ProcessStages.Measuring = false;
        var micros = 1e6 / Stopwatch.Frequency / frames;
        Console.WriteLine($"prep {ProcessStages.PrepareTicks * micros:F1} us, " +
            $"apply {ProcessStages.ApplyTicks * micros:F1} us, resolve {ProcessStages.ResolveTicks * micros:F1} us");
        World.Free(world);
        Stamp.Free(stamp);
    }

    public static int StageTiming()
    {
        Console.WriteLine("stage costs per Process (i9-class, min over reps is not applied — mean over frames):");
        Console.WriteLine("4,000 boxes 8x8, 200 moves/frame, 1024^2:");
        MeasureStages(4000, Stamp.Box(8, 8, 50), 200, 200);
        Console.WriteLine("4,000 bells 16x16, 200 moves/frame, 1024^2:");
        MeasureStages(4000, Stamp.Bell(16, 16, 80), 200, 200);
        Console.WriteLine("20,000 boxes 8x8, 2,000 moves/frame, 1024^2:");
        MeasureStages(20000, Stamp.Box(8, 8, 50), 2000, 100);
        return 0;
    }
}
