using System.Diagnostics;

internal static partial class Verification
{
    private static double Best(int reps, Action run)
    {
        run();
        var best = double.MaxValue;
        for (var r = 0; r < reps; r++)
        {
            var t = Stopwatch.GetTimestamp();
            run();
            best = Math.Min(best, Stopwatch.GetElapsedTime(t).TotalMicroseconds);
        }

        return best;
    }

    private static unsafe void FeatureTiming()
    {
        var w = Gi.World.New();
        var g = Gi.Grid.New(w, 10, 0f, 0f, 1024f);
        var food = Gi.Layer.New(w);
        var threat = Gi.Layer.New(w);
        var rng = new Random(9);
        var foodBell = Gi.Stamp.Bell(24, 24, 40);
        var threatBell = Gi.Stamp.Bell(48, 48, 50);
        var foodIds = new int[3000];
        for (var i = 0; i < foodIds.Length; i++)
            foodIds[i] = Gi.World.Place(w, food, (float)rng.NextDouble() * 1024f, (float)rng.NextDouble() * 1024f, foodBell, 4);
        for (var i = 0; i < 300; i++)
            Gi.World.Place(w, threat, (float)rng.NextDouble() * 1024f, (float)rng.NextDouble() * 1024f, threatBell, 4);
        Gi.World.Process(w);
        var best = Gi.Layer.Sum(w, food, 1, threat, -2);
        Gi.World.Process(w);

        var frame = 0;
        void Churn()
        {
            for (var i = 0; i < 60; i++)
                Gi.World.Move(w, foodIds[(frame * 60 + i) % foodIds.Length], (frame * 37 + i * 11) % 1000f + 12f, (frame * 13 + i * 29) % 1000f + 12f);
            frame++;
            Gi.World.Process(w);
        }

        var derived = Best(30, Churn);
        var a = new short[1024 * 1024];
        var b = new short[1024 * 1024];
        var naiveBest = 0;
        var naive = Best(5, () =>
        {
            fixed (short* pa = a)
            fixed (short* pb = b)
            {
                Gi.World.QueryRegion(w, g, food, 0, 0, 1024, 1024, pa);
                Gi.World.QueryRegion(w, g, threat, 0, 0, 1024, 1024, pb);
            }

            naiveBest = int.MinValue;
            for (var i = 0; i < a.Length; i++) naiveBest = Math.Max(naiveBest, Math.Clamp(a[i] - 2 * b[i], short.MinValue, short.MaxValue));
        });
        short top = 0;
        var query = Best(20, () =>
        {
            for (var i = 0; i < 1000; i++) top = Gi.World.QueryMax(w, g, best, out _, out _);
        }) / 1000;
        if (top != naiveBest) throw new InvalidOperationException($"derived QueryMax {top} != scan {naiveBest}");
        Console.WriteLine($"best cell of food - 2*threat (1024^2): QueryRegion x2 + scan {naive:F0} us vs derived QueryMax {query * 1000:F0} ns ({naiveBest}/{top})");
        var pw = Gi.World.New();
        Gi.Grid.New(pw, 10, 0f, 0f, 1024f);
        var pf = Gi.Layer.New(pw);
        var pt = Gi.Layer.New(pw);
        var plainIds = new int[3000];
        var prng = new Random(9);
        for (var i = 0; i < plainIds.Length; i++)
            plainIds[i] = Gi.World.Place(pw, pf, (float)prng.NextDouble() * 1024f, (float)prng.NextDouble() * 1024f, foodBell, 4);
        for (var i = 0; i < 300; i++)
            Gi.World.Place(pw, pt, (float)prng.NextDouble() * 1024f, (float)prng.NextDouble() * 1024f, threatBell, 4);
        Gi.World.Process(pw);
        var plainFrame = 0;
        var plain = Best(30, () =>
        {
            for (var i = 0; i < 60; i++)
                Gi.World.Move(pw, plainIds[(plainFrame * 60 + i) % plainIds.Length], (plainFrame * 37 + i * 11) % 1000f + 12f, (plainFrame * 13 + i * 29) % 1000f + 12f);
            plainFrame++;
            Gi.World.Process(pw);
        });
        Console.WriteLine($"derived Sum upkeep on a 60-move frame: {plain:F0} us without, {derived:F0} us with");

        var mw = Gi.World.New();
        Gi.Grid.New(mw, 10, 0f, 0f, 1024f);
        var ml = Gi.Layer.New(mw);
        var cone = Gi.Stamp.Cone(16, 90, 90);
        var dome = Gi.Stamp.Dome(8, 60);
        var tent = Gi.Stamp.Tent(16, 16, 60);
        var cones = new int[200];
        var domes = new int[200];
        var tents = new int[200];
        for (var i = 0; i < 200; i++)
        {
            cones[i] = Gi.World.Place(mw, ml, (i * 41.3f) % 1000f + 12f, (i * 29.7f) % 1000f + 12f, cone, 6);
            domes[i] = Gi.World.Place(mw, ml, (i * 17.9f) % 1000f + 12f, (i * 53.1f) % 1000f + 12f, dome, 6);
            tents[i] = Gi.World.Place(mw, ml, (i * 23.3f) % 1000f + 12f, (i * 31.9f) % 1000f + 12f, tent, 6);
        }

        Gi.World.Process(mw);
        var turn = 0;
        var turning = Best(20, () =>
        {
            turn++;
            for (var i = 0; i < 200; i++) Gi.World.Turn(mw, cones[i], turn * 0.05f + i);
            Gi.World.Process(mw);
        });
        var moving = Best(20, () =>
        {
            turn++;
            for (var i = 0; i < 200; i++) Gi.World.Move(mw, domes[i], (i * 17.9f + turn * 0.3f) % 1000f + 12f, (i * 53.1f) % 1000f + 12f);
            Gi.World.Process(mw);
        });
        var scaling = Best(20, () =>
        {
            turn++;
            for (var i = 0; i < 200; i++) Gi.World.Scale(mw, tents[i], 1f + (turn + i) % 8 / 8f);
            Gi.World.Process(mw);
        });
        Console.WriteLine($"turn 200 vision cones (r=16, 90°) + Process: {turning:F0} us | move 200 domes (r=8): {moving:F0} us | scale 200 tents: {scaling:F0} us");

        var fw = Gi.World.New();
        Gi.Grid.New(fw, 10, 0f, 0f, 1024f);
        var fl = Gi.Layer.New(fw);
        var crumb = Gi.Stamp.Tent(6, 6, 40);
        var trail = new int[2000];
        for (var i = 0; i < trail.Length; i++)
        {
            trail[i] = Gi.World.Place(fw, fl, (i * 7.3f) % 1000f + 12f, (i * 3.1f) % 1000f + 12f, crumb, 16);
            Gi.World.Fade(fw, trail[i], 0, 600 + i % 300);
        }

        Gi.World.Process(fw);
        var fading = Best(50, () => Gi.World.Process(fw));
        var steps = 0;
        var fadeTotal = Stopwatch.GetTimestamp();
        for (var t = 0; t < 300; t++)
        {
            Gi.World.Process(fw);
            steps++;
        }

        var perTick = Stopwatch.GetElapsedTime(fadeTotal).TotalMicroseconds / steps;
        Console.WriteLine($"2000 sources fading over 600-900 ticks: best tick {fading:F1} us, mean tick {perTick:F1} us (dense decay of 1024^2 is ~330 us)");

        var points = new (float x, float y)[1000];
        for (var i = 0; i < points.Length; i++) points[i] = ((float)(rng.NextDouble() * 1000 + 12), (float)(rng.NextDouble() * 1000 + 12));
        long acc = 0;
        var nearest = Best(20, () =>
        {
            foreach (var (x, y) in points)
            {
                Gi.World.TrySenseNearest(w, food, x, y, 32f, 100, out var v, out _, out _);
                acc += v;
            }
        }) / points.Length;
        var changed = Best(20, () =>
        {
            foreach (var (x, y) in points)
                if (Gi.World.Changed(w, food, x, y, 16f, Gi.World.Tick(w) - 1)) acc++;
        }) / points.Length;
        Console.WriteLine($"TrySenseNearest r=32: {nearest * 1000:F0} ns | Changed r=16: {changed * 1000:F0} ns ({acc & 1})");
    }
}
