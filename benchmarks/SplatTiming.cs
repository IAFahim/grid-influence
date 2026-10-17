using System.Diagnostics;
using Gi;

internal static unsafe class SplatTiming
{
    public static int Run()
    {
        var count = int.Parse(Environment.GetEnvironmentVariable("GI_SPLATS") ?? "1000000");
        var world = World.New();
        Grid.New(world, 10, 0f, 0f, 1024f);
        var phase = int.Parse(Environment.GetEnvironmentVariable("GI_PHASE") ?? "0", System.Globalization.CultureInfo.InvariantCulture);
        var layer = Layer.New(world, Layer.Residency.Transient, new Layer.Precision((byte)phase, 0));
        var box = Stamp.Box(4, 4, 8);
        var cost = Environment.GetEnvironmentVariable("GI_COST");
        if (cost != null) World.ConfigureConvolveCost(world, layer, long.Parse(cost, System.Globalization.CultureInfo.InvariantCulture));
        var xs = new float[count];
        var ys = new float[count];
        var rng = new Random(3);
        var keys = new ulong[count];
        for (var i = 0; i < count; i++)
        {
            var x = (float)(rng.NextDouble() * 1000.0 + 12.0);
            var y = (float)(rng.NextDouble() * 1000.0 + 12.0);
            keys[i] = Morton((uint)x, (uint)y) << 32 | (uint)i;
            xs[i] = x;
            ys[i] = y;
        }

        Array.Sort(keys);
        var sx = new float[count];
        var sy = new float[count];
        for (var i = 0; i < count; i++)
        {
            var source = (int)(keys[i] & 0xFFFFFFFF);
            sx[i] = xs[source];
            sy[i] = ys[source];
        }

        var drift = float.Parse(Environment.GetEnvironmentVariable("GI_DRIFT") ?? "0", System.Globalization.CultureInfo.InvariantCulture);
        if (drift != 0f)
        {
            var wobble = new Random(11);
            for (var i = 0; i < count; i++)
            {
                sx[i] = Math.Clamp(sx[i] + (float)(wobble.NextDouble() * 2.0 - 1.0) * drift, 12f, 1010f);
                sy[i] = Math.Clamp(sy[i] + (float)(wobble.NextDouble() * 2.0 - 1.0) * drift, 12f, 1010f);
            }
        }

        double bestSplat = double.MaxValue;
        double bestProcess = double.MaxValue;
        for (var pass = 0; pass < int.Parse(Environment.GetEnvironmentVariable("GI_PASSES") ?? "14"); pass++)
        {
            var started = Stopwatch.GetTimestamp();
            var applied = World.Splat(world, layer, sx, sy, box, 1);
            var middle = Stopwatch.GetTimestamp();
            World.Process(world);
            var finished = Stopwatch.GetTimestamp();
            if (applied != count) return 1;
            if (pass < 3) continue;
            bestSplat = Math.Min(bestSplat, Stopwatch.GetElapsedTime(started, middle).TotalMilliseconds);
            bestProcess = Math.Min(bestProcess, Stopwatch.GetElapsedTime(middle, finished).TotalMilliseconds);
        }

        var values = new short[count];
        var gx = new float[count];
        var gy = new float[count];
        var whole = new ulong[(count + 63) >> 6];
        var bestRead = double.MaxValue;
        var bestShuffled = double.MaxValue;
        var shuffledX = (float[])xs.Clone();
        var shuffledY = (float[])ys.Clone();
        fixed (short* v = values)
        fixed (float* px = sx, py = sy, qx = shuffledX, qy = shuffledY, rx = gx, ry = gy)
        fixed (ulong* wh = whole)
        {
            for (var pass = 0; pass < 14; pass++)
            {
                var started = Stopwatch.GetTimestamp();
                World.SenseGradientColumn(world, layer, px, py, null, count, v, rx, ry, wh);
                var middle = Stopwatch.GetTimestamp();
                World.SenseGradientColumn(world, layer, qx, qy, null, count, v, rx, ry, wh);
                var finished = Stopwatch.GetTimestamp();
                if (pass < 3) continue;
                bestRead = Math.Min(bestRead, Stopwatch.GetElapsedTime(started, middle).TotalMilliseconds);
                bestShuffled = Math.Min(bestShuffled, Stopwatch.GetElapsedTime(middle, finished).TotalMilliseconds);
            }
        }

        Console.WriteLine($"1M fused reads: morton {bestRead:F2} ms ({bestRead * 1e6 / count:F2} ns each), random {bestShuffled:F2} ms ({bestShuffled * 1e6 / count:F2} ns each)");
        Console.WriteLine($"1M Morton splats: splat {bestSplat:F2} ms ({bestSplat * 1e6 / count:F2} ns each), process {bestProcess:F2} ms ({bestProcess * 1e6 / count:F2} ns/agent)");
        World.Free(world);
        Stamp.Free(box);
        return 0;
    }

    private static ulong Morton(uint x, uint y)
    {
        ulong result = 0;
        for (var bit = 0; bit < 16; bit++)
            result |= ((ulong)(x >> bit & 1) << (2 * bit)) | ((ulong)(y >> bit & 1) << (2 * bit + 1));
        return result;
    }
}
