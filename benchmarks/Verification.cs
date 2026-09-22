using System.Diagnostics;

internal static class Verification
{
    public static int Run()
    {
        var failures = 0;
        void Check(string name, bool ok)
        {
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}");
            if (!ok) failures++;
        }

        Check("process-matches-oracle", ProcessMatchesOracle());
        Check("process-deterministic", ProcessDeterministic());
        Check("remove-restores-baseline", RemoveRestoresBaseline());
        Check("warm-process-allocates-0-bytes", WarmProcessAllocationFree());
        Check("warm-query-allocates-0-bytes", WarmQueryAllocationFree());
        Check("saturated-sum-clamps", SaturatedSumClamps());

        Console.WriteLine(failures == 0 ? "verification: all receipts green" : $"verification: {failures} failures");
        return failures == 0 ? 0 : 1;
    }

    private static int RoundQ16(int value)
        => value < 0 ? -((-value + 32768) >> 16) : (value + 32768) >> 16;

    private static int[] OracleBox(int cells, int px, int py, int fx, int fy, int w, int h, int value, int gain)
    {
        var field = new int[cells * cells];
        Span<(int s, int e, int w)> xb = stackalloc (int, int, int)[3];
        Span<(int s, int e, int w)> yb = stackalloc (int, int, int)[3];
        var nx = Bands(w, fx, xb);
        var ny = Bands(h, fy, yb);
        for (var y = 0; y < ny; y++)
        for (var x = 0; x < nx; x++)
        {
            var v = RoundQ16(value * xb[x].w * yb[y].w) * gain;
            if (v == 0) continue;
            var x0 = Math.Max(px + xb[x].s, 0);
            var y0 = Math.Max(py + yb[y].s, 0);
            var x1 = Math.Min(px + xb[x].e, cells);
            var y1 = Math.Min(py + yb[y].e, cells);
            for (var cy = y0; cy < y1; cy++)
            for (var cx = x0; cx < x1; cx++)
                field[cy * cells + cx] += v;
        }

        return field;
    }

    private static int Bands(int length, int phase, Span<(int s, int e, int w)> bands)
    {
        if (phase == 0) { bands[0] = (0, length, 256); return 1; }
        bands[0] = (0, 1, 256 - phase);
        if (length == 1) { bands[1] = (1, 2, phase); return 2; }
        bands[1] = (1, length, 256);
        bands[2] = (length, length + 1, phase);
        return 3;
    }

    private static bool ProcessMatchesOracle()
    {
        var w = GridInfluence.World.New();
        var g = GridInfluence.Grid.New(w, 8, 0f, 0f, 256f);
        var l = GridInfluence.Layer.New(w);
        var box = GridInfluence.Stamp.Box(16, 16, 100);
        var rng = new Random(9);
        var field = new int[256 * 256];
        for (var i = 0; i < 300; i++)
        {
            var x = (float)(rng.NextDouble() * 250);
            var y = (float)(rng.NextDouble() * 250);
            GridInfluence.World.Place(w, l, x, y, box, 8);
            var qx = (int)MathF.Floor(x * 256f) - 16 * 128;
            var qy = (int)MathF.Floor(y * 256f) - 16 * 128;
            var piece = OracleBox(256, qx >> 8, qy >> 8, qx & 255, qy & 255, 16, 16, 100, 8);
            for (var c = 0; c < field.Length; c++) field[c] += piece[c];
        }

        GridInfluence.World.Process(w);
        for (var i = 0; i < 2000; i++)
        {
            var cx = rng.Next(256);
            var cy = rng.Next(256);
            var expected = Math.Clamp(field[cy * 256 + cx], short.MinValue, short.MaxValue);
            if (GridInfluence.World.Query(w, g, l, cx, cy) != expected) return false;
        }

        return true;
    }

    private static bool ProcessDeterministic()
    {
        var a = GridInfluence.World.New();
        var ga = GridInfluence.Grid.New(a, 7, 0f, 0f, 128f);
        var la = GridInfluence.Layer.New(a);
        var b = GridInfluence.World.New();
        var gb = GridInfluence.Grid.New(b, 7, 0f, 0f, 128f);
        var lb = GridInfluence.Layer.New(b);
        var stamp = GridInfluence.Stamp.Box(10, 6, 55);
        var rng = new Random(13);
        for (var i = 0; i < 200; i++)
        {
            var x = (float)(rng.NextDouble() * 128);
            var y = (float)(rng.NextDouble() * 128);
            GridInfluence.World.Place(a, la, x, y, stamp, 9);
            GridInfluence.World.Place(b, lb, x, y, stamp, 9);
        }

        GridInfluence.World.Process(a);
        GridInfluence.World.Process(b);
        for (var i = 0; i < 500; i++)
        {
            var cx = rng.Next(128);
            var cy = rng.Next(128);
            if (GridInfluence.World.Query(a, ga, la, cx, cy) != GridInfluence.World.Query(b, gb, lb, cx, cy))
                return false;
        }

        return true;
    }

    private static bool RemoveRestoresBaseline()
    {
        var w = GridInfluence.World.New();
        var g = GridInfluence.Grid.New(w, 6, 0f, 0f, 64f);
        var l = GridInfluence.Layer.New(w);
        var stamp = GridInfluence.Stamp.Box(8, 8, 50);
        var s = GridInfluence.World.Place(w, l, 20f, 20f, stamp, 6);
        GridInfluence.World.Process(w);
        if (GridInfluence.World.Query(w, g, l, 20, 20) != 300) return false;
        GridInfluence.World.Remove(w, s);
        GridInfluence.World.Process(w);
        return GridInfluence.World.Query(w, g, l, 20, 20) == 0;
    }

    private static bool WarmProcessAllocationFree()
    {
        var w = GridInfluence.World.New();
        GridInfluence.Grid.New(w, 8, 0f, 0f, 256f);
        var l = GridInfluence.Layer.New(w);
        var stamp = GridInfluence.Stamp.Box(12, 12, 40);
        var rng = new Random(5);
        for (var i = 0; i < 400; i++)
            GridInfluence.World.Place(w, l, (float)(rng.NextDouble() * 256), (float)(rng.NextDouble() * 256), stamp, 8);
        GridInfluence.World.Process(w);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 64; i++) GridInfluence.World.Process(w);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"  warm process (unchanged) over 64 runs: {allocated} B");
        if (allocated != 0) return false;

        var s = GridInfluence.World.Place(w, l, 100.25f, 100.5f, stamp, 8);
        GridInfluence.World.Process(w);
        GridInfluence.World.Remove(w, s);
        GridInfluence.World.Process(w);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 64; i++)
        {
            var id = GridInfluence.World.Place(w, l, 100.25f, 100.5f, stamp, 8);
            GridInfluence.World.Process(w);
            GridInfluence.World.Remove(w, id);
            GridInfluence.World.Process(w);
        }
        allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"  warm process (place/remove churn) over 64 pairs: {allocated} B");
        return allocated == 0;
    }

    private static bool WarmQueryAllocationFree()
    {
        var w = GridInfluence.World.New();
        var g = GridInfluence.Grid.New(w, 8, 0f, 0f, 256f);
        var l = GridInfluence.Layer.New(w);
        var stamp = GridInfluence.Stamp.Box(12, 12, 40);
        var rng = new Random(5);
        for (var i = 0; i < 200; i++)
            GridInfluence.World.Place(w, l, (float)(rng.NextDouble() * 256), (float)(rng.NextDouble() * 256), stamp, 8);
        GridInfluence.World.Process(w);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        long acc = 0;
        for (var i = 0; i < 200_000; i++) acc += GridInfluence.World.Query(w, g, l, i % 251, (i * 7) % 251);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        _ = acc;
        Console.WriteLine($"  warm query over 200k cells: {allocated} B");
        return allocated == 0;
    }

    private static bool SaturatedSumClamps()
    {
        var w = GridInfluence.World.New();
        var g = GridInfluence.Grid.New(w, 6, 0f, 0f, 64f);
        var l = GridInfluence.Layer.New(w);
        var stamp = GridInfluence.Stamp.Box(4, 4, 127);
        for (var i = 0; i < 120; i++) GridInfluence.World.Place(w, l, 32f, 32f, stamp, 16);
        GridInfluence.World.Process(w);
        return GridInfluence.World.Query(w, g, l, 32, 32) == 32767;
    }

    public static void Timing()
    {
        var w = GridInfluence.World.New();
        var g = GridInfluence.Grid.New(w, 10, 0f, 0f, 1024f);
        var l = GridInfluence.Layer.New(w);
        var stamp = GridInfluence.Stamp.Box(16, 16, 60);
        var rng = new Random(17);
        for (var i = 0; i < 4000; i++)
            GridInfluence.World.Place(w, l, (float)(rng.NextDouble() * 1024), (float)(rng.NextDouble() * 1024), stamp, 8);
        GridInfluence.World.Process(w);

        var best = double.MaxValue;
        for (var r = 0; r < 20; r++)
        {
            var t = Stopwatch.GetTimestamp();
            GridInfluence.World.Process(w);
            var el = Stopwatch.GetElapsedTime(t).TotalMicroseconds;
            if (el < best) best = el;
        }
        Console.WriteLine($"unchanged process (4000 sources, 1024-grid): {best:F0} us");

        best = double.MaxValue;
        for (var r = 0; r < 20; r++)
        {
            var id = GridInfluence.World.Place(w, l, r, r, stamp, 8);
            var t = Stopwatch.GetTimestamp();
            GridInfluence.World.Process(w);
            var el = Stopwatch.GetElapsedTime(t).TotalMicroseconds;
            GridInfluence.World.Remove(w, id);
            if (el < best) best = el;
        }
        Console.WriteLine($"incremental process (1 added source): {best:F0} us");

        best = double.MaxValue;
        long acc = 0;
        for (var r = 0; r < 20; r++)
        {
            var t = Stopwatch.GetTimestamp();
            for (var i = 0; i < 10_000; i++) acc += GridInfluence.World.Query(w, g, l, i % 1021, (i * 3) % 1021);
            var el = Stopwatch.GetElapsedTime(t).TotalMicroseconds;
            if (el < best) best = el;
        }
        Console.WriteLine($"query: {best / 10:F2} us per 1k cells ({acc})");
    }
}
