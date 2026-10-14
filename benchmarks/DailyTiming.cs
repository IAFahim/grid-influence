using System.Diagnostics;

internal static partial class Verification
{
    private const int DailyMeasuredTicks = 600;
    private const int DailyWarmTicks = 300;

    public static void Daily()
    {
        RunDaily(4, 2, out _, out _, out _, out _, out _, out _, out _, out _);
        var checksum = RunDaily(DailyMeasuredTicks, DailyWarmTicks,
            out var meanTick, out var worstTick, out var mutateUs, out var processUs, out var queryUs,
            out var allocated, out var argmaxOk, out var decisions);
        var twinChecksum = RunDaily(DailyMeasuredTicks, DailyWarmTicks,
            out _, out _, out _, out _, out _, out _, out _, out _);

        Console.WriteLine($"{(argmaxOk ? "PASS" : "FAIL")} daily-argmax-matches-region-scan (derived safety over 512^2)");
        Console.WriteLine($"{(checksum == twinChecksum ? "PASS" : "FAIL")} daily-replay-is-deterministic (checksum {checksum})");
        Console.WriteLine($"{(allocated == 0 ? "PASS" : "FAIL")} daily-tick-loop-allocates-0-bytes ({DailyMeasuredTicks} ticks)");
        Console.WriteLine($"day-to-day frame, 512^2 world at 60 Hz: mean {meanTick:F1} us, worst {worstTick:F1} us " +
            $"({meanTick / 16666.7 * 100:F2}% of the frame budget)");
        Console.WriteLine($"  mutations (20 turn+move cones, 150 sheep moves, 4 fading crumbs, churn): {mutateUs:F1} us");
        Console.WriteLine($"  Process (food, threat, herd, scent + derived safety): {processUs:F1} us");
        Console.WriteLine($"  agent reads ({DailyMeasuredTicks * 200 - decisions} slept, {decisions} re-decided): {queryUs:F1} us");
    }

    private static unsafe long RunDaily(int measuredTicks, int warmTicks,
        out double meanTickUs, out double worstTickUs, out double mutateUs, out double processUs, out double queryUs,
        out long allocated, out bool argmaxMatchesScan, out int decisions)
    {
        var w = Gi.World.New();
        var fine = Gi.Grid.New(w, 9, 0f, 0f, 512f);
        var coarse = Gi.Grid.New(w, 7, 0f, 0f, 512f);
        var food = Gi.Layer.New(w);
        var threat = Gi.Layer.New(w);
        var herd = Gi.Layer.New(w);
        var scent = Gi.Layer.New(w);
        var safety = Gi.Layer.Sum(w, food, 1, threat, -2);
        var bush = Gi.Stamp.Bell(16, 16, 40);
        var cone = Gi.Stamp.Cone(24, 70, 110);
        var aura = Gi.Stamp.Dome(6, 30);
        var crumb = Gi.Stamp.Tent(5, 5, 50);
        var rng = new Random(73021);

        const int bushes = 240, wolves = 20, sheep = 200;
        var foodIds = new int[bushes];
        var foodX = new float[bushes];
        var foodY = new float[bushes];
        for (var i = 0; i < bushes; i++)
        {
            foodX[i] = (float)(rng.NextDouble() * 496 + 8);
            foodY[i] = (float)(rng.NextDouble() * 496 + 8);
            foodIds[i] = Gi.World.Place(w, food, foodX[i], foodY[i], bush, 6);
        }

        var wolfIds = new int[wolves];
        var wolfX = new float[wolves];
        var wolfY = new float[wolves];
        var wolfHeading = new float[wolves];
        for (var i = 0; i < wolves; i++)
        {
            wolfX[i] = (float)(rng.NextDouble() * 496 + 8);
            wolfY[i] = (float)(rng.NextDouble() * 496 + 8);
            wolfHeading[i] = (float)(rng.NextDouble() * MathF.Tau);
            wolfIds[i] = Gi.World.Place(w, threat, wolfX[i], wolfY[i], cone, 10);
        }

        var sheepIds = new int[sheep];
        var sheepX = new float[sheep];
        var sheepY = new float[sheep];
        for (var i = 0; i < sheep; i++)
        {
            sheepX[i] = (float)(rng.NextDouble() * 496 + 8);
            sheepY[i] = (float)(rng.NextDouble() * 496 + 8);
            sheepIds[i] = Gi.World.Place(w, herd, sheepX[i], sheepY[i], aura, 4);
        }

        Gi.World.Process(w);

        var minimap = new short[128 * 128];
        long acc = 0;
        decisions = 0;
        var worst = 0.0;
        long mutateTicks = 0, processTicks = 0, queryTicks = 0;

        fixed (short* page = minimap)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var t = 0; t < warmTicks + measuredTicks; t++)
            {
                var measured = t >= warmTicks;
                var m0 = Stopwatch.GetTimestamp();

                for (var i = 0; i < wolves; i++)
                {
                    wolfHeading[i] += (float)((rng.NextDouble() - 0.5) * 0.3);
                    wolfX[i] += MathF.Cos(wolfHeading[i]) * 1.5f;
                    wolfY[i] += MathF.Sin(wolfHeading[i]) * 1.5f;
                    if (wolfX[i] < 20f || wolfX[i] > 492f)
                    {
                        wolfHeading[i] = MathF.PI - wolfHeading[i];
                        wolfX[i] = Math.Clamp(wolfX[i], 20f, 492f);
                    }

                    if (wolfY[i] < 20f || wolfY[i] > 492f)
                    {
                        wolfHeading[i] = -wolfHeading[i];
                        wolfY[i] = Math.Clamp(wolfY[i], 20f, 492f);
                    }

                    Gi.World.Turn(w, wolfIds[i], wolfHeading[i]);
                    Gi.World.Move(w, wolfIds[i], wolfX[i], wolfY[i]);
                }

                for (var s = 0; s < 150; s++)
                {
                    var a = (t * 37 + s * 13) % sheep;
                    sheepX[a] = Math.Clamp(sheepX[a] + (float)((rng.NextDouble() - 0.5) * 2.4), 8f, 504f);
                    sheepY[a] = Math.Clamp(sheepY[a] + (float)((rng.NextDouble() - 0.5) * 2.4), 8f, 504f);
                    Gi.World.Move(w, sheepIds[a], sheepX[a], sheepY[a]);
                }

                if (t % 8 == 0)
                    for (var j = 0; j < 2; j++)
                    {
                        var k = (t / 8 * 3 + j) % sheep;
                        Gi.World.Remove(w, sheepIds[k]);
                        sheepX[k] = (float)(rng.NextDouble() * 496 + 8);
                        sheepY[k] = (float)(rng.NextDouble() * 496 + 8);
                        sheepIds[k] = Gi.World.Place(w, herd, sheepX[k], sheepY[k], aura, 4);
                    }

                for (var j = 0; j < 2; j++)
                {
                    var b = (t * 11 + j) % bushes;
                    foodX[b] = (float)(rng.NextDouble() * 496 + 8);
                    foodY[b] = (float)(rng.NextDouble() * 496 + 8);
                    Gi.World.Move(w, foodIds[b], foodX[b], foodY[b]);
                }

                for (var c = 0; c < 4; c++)
                {
                    var id = Gi.World.Place(w, scent, (float)(rng.NextDouble() * 496 + 8), (float)(rng.NextDouble() * 496 + 8), crumb, 16);
                    Gi.World.Fade(w, id, 0, 240);
                    Gi.World.Expire(w, id, 240);
                }

                var m1 = Stopwatch.GetTimestamp();
                Gi.World.Process(w);
                var m2 = Stopwatch.GetTimestamp();

                var since = Math.Max(0, Gi.World.Tick(w) - 6);
                for (var a = 0; a < sheep; a++)
                    if (Gi.World.Changed(w, threat, sheepX[a], sheepY[a], 20f, since))
                    {
                        if (measured) decisions++;
                        Gi.World.TrySenseNearest(w, food, sheepX[a], sheepY[a], 40f, 60, out var meal, out _, out _);
                        acc += meal;
                    }

                for (var i = 0; i < wolves; i++)
                {
                    Gi.World.TrySenseNearest(w, herd, wolfX[i], wolfY[i], 48f, 30, out var prey, out _, out _);
                    acc += prey;
                }

                for (var b = 0; b < 60; b++)
                {
                    var a = (t * 29 + b * 7) % sheep;
                    Gi.World.TrySenseGradient(w, safety, sheepX[a], sheepY[a], out var gx, out var gy);
                    acc += (int)gx + (int)gy;
                }

                if (t % 4 == 0)
                {
                    Gi.World.QueryRegion(w, coarse, safety, 0, 0, 128, 128, page);
                    acc += page[t % minimap.Length];
                }

                if (t % 10 == 0) acc += Gi.World.QueryMax(w, fine, safety, out _, out _);
                var m3 = Stopwatch.GetTimestamp();

                if (!measured) continue;
                mutateTicks += m1 - m0;
                processTicks += m2 - m1;
                queryTicks += m3 - m2;
                worst = Math.Max(worst, Stopwatch.GetElapsedTime(m0).TotalMicroseconds);
            }

            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        }

        acc += Gi.World.Query(w, fine, food, 0, 0, 512, 512);
        acc += Gi.World.Query(w, fine, threat, 0, 0, 512, 512);
        acc += Gi.World.Query(w, fine, herd, 0, 0, 512, 512);
        acc += Gi.World.Query(w, fine, scent, 0, 0, 512, 512);
        acc += Gi.World.Query(w, coarse, safety, 0, 0, 128, 128);

        var field = new short[512 * 512];
        fixed (short* p = field)
        {
            Gi.World.QueryRegion(w, fine, safety, 0, 0, 512, 512, p);
            var scan = short.MinValue;
            for (var i = 0; i < field.Length; i++)
                if (field[i] > scan) scan = field[i];
            var top = Gi.World.QueryMax(w, fine, safety, out var bx, out var by);
            argmaxMatchesScan = top == scan && Gi.World.Query(w, fine, safety, bx, by) == scan;
        }

        var freq = Stopwatch.Frequency;
        mutateUs = mutateTicks * 1e6 / freq / measuredTicks;
        processUs = processTicks * 1e6 / freq / measuredTicks;
        queryUs = queryTicks * 1e6 / freq / measuredTicks;
        meanTickUs = mutateUs + processUs + queryUs;
        worstTickUs = worst;
        return acc;
    }
}
