using System.Diagnostics;
using Gi;

internal static partial class Verification
{
    public static int SweepProfile()
    {
        ProfileCase("B1-like: 200k boxes 8x8, all move 0.5 cells", 200_000, Stamp.Box(8, 8, 50), false);
        ProfileCase("random: 200k boxes 8x8, all move random", 200_000, Stamp.Box(8, 8, 50), true);
        return 0;
    }

    private static void ProfileCase(string name, int count, byte stamp, bool random)
    {
        var world = World.New();
        Grid.New(world, 12, 0f, 0f, 4096f);
        Layer.New(world);
        var ids = new World.SourceId[count];
        var rng = new Random(11);
        for (var i = 0; i < count; i++)
            ids[i] = World.Place(world, 0,
                (float)(rng.NextDouble() * 4000), (float)(rng.NextDouble() * 4000), stamp, 4);
        World.Process(world);

        var dx = 0.5f;
        for (var f = 0; f < 5; f++)
        {
            for (var i = 0; i < count; i++)
            {
                var row = i / 500;
                World.Move(world, ids[i], 50f + i % 4000 + f * dx, 50f + row + f * dx * 0.5f);
            }

            World.Process(world);
        }

        ProcessStages.Reset();
        ProcessStages.Measuring = true;
        var frames = 20;
        var sw = Stopwatch.StartNew();
        for (var f = 0; f < frames; f++)
        {
            for (var i = 0; i < count; i++)
            {
                if (random)
                    World.Move(world, ids[i], (float)(rng.NextDouble() * 4000), (float)(rng.NextDouble() * 4000));
                else
                {
                    var row = i / 500;
                    World.Move(world, ids[i], 50f + i % 4000 + (f + 5) * dx, 50f + row + (f + 5) * dx * 0.5f);
                }
            }

            World.Process(world);
        }

        ProcessStages.Measuring = false;
        sw.Stop();
        var micros = 1e6 / Stopwatch.Frequency / frames;
        Console.WriteLine($"{name}: total {sw.ElapsedMilliseconds / (double)frames:F1} ms/frame " +
            $"prep {ProcessStages.PrepareTicks * micros:F1} us apply {ProcessStages.ApplyTicks * micros:F1} us " +
            $"({(ProcessStages.PrepareTicks + ProcessStages.ApplyTicks) * micros / count:F2} us/source)");
        World.Free(world);
        Stamp.Free(stamp);
    }
}
