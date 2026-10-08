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
        Check("query-region-matches-cells", QueryRegionMatchesCells());
        Check("page-sum-matches-scan", PageSumMatchesScan());
        Check("saturated-sum-clamps", SaturatedSumClamps());
        Check("cross-grid-sums-conserve-world-integral", CrossGridSumsConserve());

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
        var w = Gi.World.New();
        var g = Gi.Grid.New(w, 8, 0f, 0f, 256f);
        var l = Gi.Layer.New(w);
        var box = Gi.Stamp.Box(16, 16, 100);
        var rng = new Random(9);
        var field = new int[256 * 256];
        for (var i = 0; i < 300; i++)
        {
            var x = (float)(rng.NextDouble() * 250);
            var y = (float)(rng.NextDouble() * 250);
            Gi.World.Place(w, l, x, y, box, 8);
            var qx = (int)MathF.Floor(x * 256f) - 16 * 128;
            var qy = (int)MathF.Floor(y * 256f) - 16 * 128;
            var piece = OracleBox(256, qx >> 8, qy >> 8, qx & 255, qy & 255, 16, 16, 100, 8);
            for (var c = 0; c < field.Length; c++) field[c] += piece[c];
        }

        Gi.World.Process(w);
        for (var i = 0; i < 2000; i++)
        {
            var cx = rng.Next(256);
            var cy = rng.Next(256);
            var expected = Math.Clamp(field[cy * 256 + cx], short.MinValue, short.MaxValue);
            if (Gi.World.Query(w, g, l, cx, cy) != expected) return false;
        }

        return true;
    }

    private static bool ProcessDeterministic()
    {
        var a = Gi.World.New();
        var ga = Gi.Grid.New(a, 7, 0f, 0f, 128f);
        var la = Gi.Layer.New(a);
        var b = Gi.World.New();
        var gb = Gi.Grid.New(b, 7, 0f, 0f, 128f);
        var lb = Gi.Layer.New(b);
        var stamp = Gi.Stamp.Box(10, 6, 55);
        var rng = new Random(13);
        for (var i = 0; i < 200; i++)
        {
            var x = (float)(rng.NextDouble() * 128);
            var y = (float)(rng.NextDouble() * 128);
            Gi.World.Place(a, la, x, y, stamp, 9);
            Gi.World.Place(b, lb, x, y, stamp, 9);
        }

        Gi.World.Process(a);
        Gi.World.Process(b);
        for (var i = 0; i < 500; i++)
        {
            var cx = rng.Next(128);
            var cy = rng.Next(128);
            if (Gi.World.Query(a, ga, la, cx, cy) != Gi.World.Query(b, gb, lb, cx, cy))
                return false;
        }

        return true;
    }

    private static bool RemoveRestoresBaseline()
    {
        var w = Gi.World.New();
        var g = Gi.Grid.New(w, 6, 0f, 0f, 64f);
        var l = Gi.Layer.New(w);
        var stamp = Gi.Stamp.Box(8, 8, 50);
        var s = Gi.World.Place(w, l, 20f, 20f, stamp, 6);
        Gi.World.Process(w);
        if (Gi.World.Query(w, g, l, 20, 20) != 300) return false;
        Gi.World.Remove(w, s);
        Gi.World.Process(w);
        return Gi.World.Query(w, g, l, 20, 20) == 0;
    }

    private static bool WarmProcessAllocationFree()
    {
        var w = Gi.World.New();
        Gi.Grid.New(w, 8, 0f, 0f, 256f);
        var l = Gi.Layer.New(w);
        var stamp = Gi.Stamp.Box(12, 12, 40);
        var rng = new Random(5);
        for (var i = 0; i < 400; i++)
            Gi.World.Place(w, l, (float)(rng.NextDouble() * 256), (float)(rng.NextDouble() * 256), stamp, 8);
        Gi.World.Process(w);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 64; i++) Gi.World.Process(w);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"  warm process (unchanged) over 64 runs: {allocated} B");
        if (allocated != 0) return false;

        var s = Gi.World.Place(w, l, 100.25f, 100.5f, stamp, 8);
        Gi.World.Process(w);
        Gi.World.Remove(w, s);
        Gi.World.Process(w);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 64; i++)
        {
            var id = Gi.World.Place(w, l, 100.25f, 100.5f, stamp, 8);
            Gi.World.Process(w);
            Gi.World.Remove(w, id);
            Gi.World.Process(w);
        }
        allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"  warm process (place/remove churn) over 64 pairs: {allocated} B");
        return allocated == 0;
    }

    private static bool WarmQueryAllocationFree()
    {
        var w = Gi.World.New();
        var g = Gi.Grid.New(w, 8, 0f, 0f, 256f);
        var l = Gi.Layer.New(w);
        var stamp = Gi.Stamp.Box(12, 12, 40);
        var rng = new Random(5);
        for (var i = 0; i < 200; i++)
            Gi.World.Place(w, l, (float)(rng.NextDouble() * 256), (float)(rng.NextDouble() * 256), stamp, 8);
        Gi.World.Process(w);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        long acc = 0;
        for (var i = 0; i < 200_000; i++) acc += Gi.World.Query(w, g, l, i % 251, (i * 7) % 251);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        _ = acc;
        Console.WriteLine($"  warm query over 200k cells: {allocated} B");
        return allocated == 0;
    }

    private static unsafe bool QueryRegionMatchesCells()
    {
        var w = Gi.World.New();
        var g = Gi.Grid.New(w, 8, 0f, 0f, 256f);
        var l = Gi.Layer.New(w);
        var box = Gi.Stamp.Box(14, 14, 80);
        var samples = new sbyte[6 * 9];
        for (var i = 0; i < samples.Length; i++) samples[i] = (sbyte)(i * 17 % 40 - 20);
        var raster = Gi.Stamp.New(samples, 6, 9);
        var rng = new Random(23);
        for (var i = 0; i < 300; i++)
        {
            var x = (float)(rng.NextDouble() * 256);
            var y = (float)(rng.NextDouble() * 256);
            Gi.World.Place(w, l, x, y, rng.Next(2) == 0 ? box : raster, 4 + rng.Next(13));
        }

        Gi.World.Process(w);

        var buffer = new short[256 * 256];
        fixed (short* p = buffer)
        {
            Gi.World.QueryRegion(w, g, l, 0, 0, 256, 256, p);
            for (var cy = 0; cy < 256; cy++)
            for (var cx = 0; cx < 256; cx++)
                if (p[cy * 256 + cx] != Gi.World.Query(w, g, l, cx, cy)) return false;

            Gi.World.QueryRegion(w, g, l, 30, 62, 91, 133, p);
            for (var row = 0; row < 133; row++)
            for (var col = 0; col < 91; col++)
                if (p[row * 91 + col] != Gi.World.Query(w, g, l, 30 + col, 62 + row)) return false;

            Gi.World.QueryRegion(w, g, l, -7, 250, 20, 12, p);
            for (var row = 0; row < 12; row++)
            for (var col = 0; col < 20; col++)
                if (p[row * 20 + col] != Gi.World.Query(w, g, l, -7 + col, 250 + row)) return false;
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        fixed (short* p = buffer)
        {
            for (var i = 0; i < 100; i++) Gi.World.QueryRegion(w, g, l, 0, 0, 256, 256, p);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"  warm query-region over 100 full grids: {allocated} B");
        return allocated == 0;
    }

    private static bool SaturatedSumClamps()
    {
        var w = Gi.World.New();
        var g = Gi.Grid.New(w, 6, 0f, 0f, 64f);
        var l = Gi.Layer.New(w);
        var stamp = Gi.Stamp.Box(4, 4, 127);
        for (var i = 0; i < 120; i++) Gi.World.Place(w, l, 32f, 32f, stamp, 16);
        Gi.World.Process(w);
        return Gi.World.Query(w, g, l, 32, 32) == 32767;
    }

    private static bool CrossGridSumsConserve()
    {
        var w = Gi.World.New();
        var gFull = Gi.Grid.New(w, 9, 0f, 0f, 256f);
        var gFine = Gi.Grid.New(w, 8, 0f, 0f, 256f);
        var gHalf = Gi.Grid.New(w, 7, 0f, 0f, 256f);
        var gQuarter = Gi.Grid.New(w, 6, 0f, 0f, 256f);
        var l = Gi.Layer.New(w);
        var box = Gi.Stamp.Box(16, 16, 90);
        var rng = new Random(53);
        for (var i = 0; i < 40; i++)
            Gi.World.Place(w, l, (rng.Next(54) + 4) * 4, (rng.Next(54) + 4) * 4, box, 4 + i % 5);
        Gi.World.Process(w);

        var full = Gi.World.Query(w, gFull, l, 0, 0, 512, 512);
        var fine = Gi.World.Query(w, gFine, l, 0, 0, 256, 256);
        var half = Gi.World.Query(w, gHalf, l, 0, 0, 128, 128);
        var quarter = Gi.World.Query(w, gQuarter, l, 0, 0, 64, 64);
        return full == 4 * fine && fine == 4 * half && half == 4 * quarter && fine > 0;
    }

    private static bool PageSumMatchesScan()
    {
        for (var world = 0; world < 3; world++)
        {
            var w = Gi.World.New();
            var g = Gi.Grid.New(w, 8, 0f, 0f, 256f);
            var l = Gi.Layer.New(w);
            var box = Gi.Stamp.Box(14, 14, 80);
            var samples = new sbyte[6 * 9];
            for (var i = 0; i < samples.Length; i++) samples[i] = (sbyte)(i * 17 % 40 - 20);
            var raster = Gi.Stamp.New(samples, 6, 9);
            var rng = new Random(41 + world);
            var sources = new int[90];
            for (var churn = 0; churn < 5; churn++)
            {
                for (var i = 0; i < sources.Length; i++)
                {
                    var x = (float)(rng.NextDouble() * 256);
                    var y = (float)(rng.NextDouble() * 256);
                    if (churn > 0 && rng.Next(3) == 0)
                    {
                        Gi.World.Move(w, sources[rng.Next(churn * sources.Length / 5)], x, y);
                        continue;
                    }

                    sources[i] = Gi.World.Place(w, l, x, y, rng.Next(2) == 0 ? box : raster, 1 + rng.Next(16));
                }

                if (churn > 0)
                    for (var i = 0; i < 20; i++)
                        Gi.World.Remove(w, sources[rng.Next(sources.Length)]);

                Gi.World.Process(w);
                var sum = Gi.World.Query(w, g, l, 0, 0, 256, 256);
                var scan = 0L;
                for (var cy = 0; cy < 256; cy++)
                for (var cx = 0; cx < 256; cx++)
                    scan += Gi.World.Query(w, g, l, cx, cy);
                if (sum != scan) return false;
            }
        }

        return true;
    }

    public static void Compare()
    {
        const int cells = 1024;
        const int sources = 4000;
        var w = Gi.World.New();
        var g = Gi.Grid.New(w, 10, 0f, 0f, 1024f);
        var l = Gi.Layer.New(w);
        var stamp = Gi.Stamp.Box(16, 16, 60);
        var naive = new NaiveGrid(cells);
        var rng = new Random(31);
        var ids = new int[sources];
        for (var i = 0; i < ids.Length; i++)
        {
            var x = 8 + rng.Next(cells - 16);
            var y = 8 + rng.Next(cells - 16);
            ids[i] = Gi.World.Place(w, l, x, y, stamp, 8);
            naive.Add(x, y, 16, 16, 60, 8);
        }

        Gi.World.Process(w);
        naive.Rebuild();

        var matches = naive.Sum() == Gi.World.Query(w, g, l, 0, 0, cells, cells);
        for (var i = 0; i < 2000 && matches; i++)
        {
            var cx = rng.Next(cells);
            var cy = rng.Next(cells);
            matches = naive.Query(cx, cy) == Gi.World.Query(w, g, l, cx, cy);
        }

        Console.WriteLine($"{(matches ? "PASS" : "FAIL")} naive-grid-matches-gi (full sum + 2000 sampled cells)");

        var best = double.MaxValue;
        for (var r = 0; r < 20; r++)
        {
            var t = Stopwatch.GetTimestamp();
            naive.Rebuild();
            var el = Stopwatch.GetElapsedTime(t).TotalMicroseconds;
            if (el < best) best = el;
        }
        Console.WriteLine($"naive static frame (clear + {sources} sources): {best:F0} us");

        best = double.MaxValue;
        for (var r = 0; r < 20; r++)
        {
            var t = Stopwatch.GetTimestamp();
            Gi.World.Process(w);
            var el = Stopwatch.GetElapsedTime(t).TotalMicroseconds;
            if (el < best) best = el;
        }
        Console.WriteLine($"Gi unchanged process: {best:F0} us");

        var movers = new int[200];
        for (var i = 0; i < movers.Length; i++) movers[i] = i * 19 % sources;
        var naiveBest = double.MaxValue;
        var giBest = double.MaxValue;
        for (var r = -1; r < 20; r++)
        {
            var x = new int[200];
            var y = new int[200];
            for (var i = 0; i < movers.Length; i++)
            {
                x[i] = (movers[i] * 5 + (r + 1) * 37) % 1000 + 12;
                y[i] = (movers[i] * 7 + (r + 1) * 11) % 1000 + 12;
            }

            var t = Stopwatch.GetTimestamp();
            for (var i = 0; i < movers.Length; i++) naive.Move(movers[i], x[i], y[i]);
            naive.Rebuild();
            var el = Stopwatch.GetElapsedTime(t).TotalMicroseconds;
            if (r >= 0 && el < naiveBest) naiveBest = el;

            t = Stopwatch.GetTimestamp();
            for (var i = 0; i < movers.Length; i++) Gi.World.Move(w, ids[movers[i]], x[i], y[i]);
            Gi.World.Process(w);
            el = Stopwatch.GetElapsedTime(t).TotalMicroseconds;
            if (r >= 0 && el < giBest) giBest = el;
        }

        matches = naive.Sum() == Gi.World.Query(w, g, l, 0, 0, cells, cells);
        Console.WriteLine($"{(matches ? "PASS" : "FAIL")} naive-grid-matches-gi-after-churn");
        Console.WriteLine($"naive move-200 churn rebuild: {naiveBest:F0} us");
        Console.WriteLine($"Gi move-200 churn process: {giBest:F0} us");

        best = double.MaxValue;
        long acc = 0;
        for (var r = 0; r < 20; r++)
        {
            var t = Stopwatch.GetTimestamp();
            acc += naive.Sum();
            var el = Stopwatch.GetElapsedTime(t).TotalMicroseconds;
            if (el < best) best = el;
        }
        Console.WriteLine($"naive full-grid sum (1M cells): {best:F0} us");

        best = double.MaxValue;
        for (var r = 0; r < 20; r++)
        {
            var t = Stopwatch.GetTimestamp();
            acc += Gi.World.Query(w, g, l, 0, 0, cells, cells);
            var el = Stopwatch.GetElapsedTime(t).TotalMicroseconds;
            if (el < best) best = el;
        }
        Console.WriteLine($"Gi full-grid sum: {best:F1} us (checksum {acc})");
    }

    public static void Timing()
    {
        var w = Gi.World.New();
        var g = Gi.Grid.New(w, 10, 0f, 0f, 1024f);
        var l = Gi.Layer.New(w);
        var stamp = Gi.Stamp.Box(16, 16, 60);
        var rng = new Random(17);
        for (var i = 0; i < 4000; i++)
            Gi.World.Place(w, l, (float)(rng.NextDouble() * 1024), (float)(rng.NextDouble() * 1024), stamp, 8);
        Gi.World.Process(w);

        var best = double.MaxValue;
        for (var r = 0; r < 20; r++)
        {
            var t = Stopwatch.GetTimestamp();
            Gi.World.Process(w);
            var el = Stopwatch.GetElapsedTime(t).TotalMicroseconds;
            if (el < best) best = el;
        }
        Console.WriteLine($"unchanged process (4000 sources, 1024-grid): {best:F0} us");

        best = double.MaxValue;
        for (var r = 0; r < 20; r++)
        {
            var id = Gi.World.Place(w, l, r, r, stamp, 8);
            var t = Stopwatch.GetTimestamp();
            Gi.World.Process(w);
            var el = Stopwatch.GetElapsedTime(t).TotalMicroseconds;
            Gi.World.Remove(w, id);
            if (el < best) best = el;
        }
        Console.WriteLine($"incremental process (1 added source): {best:F0} us");

        best = double.MaxValue;
        long acc = 0;
        for (var r = 0; r < 20; r++)
        {
            var t = Stopwatch.GetTimestamp();
            for (var i = 0; i < 10_000; i++) acc += Gi.World.Query(w, g, l, i % 1021, (i * 3) % 1021);
            var el = Stopwatch.GetElapsedTime(t).TotalMicroseconds;
            if (el < best) best = el;
        }
        Console.WriteLine($"query: {best / 10:F2} us per 1k cells ({acc})");

        var movers = new int[200];
        for (var i = 0; i < movers.Length; i++) movers[i] = i * 19 % 4000;
        best = double.MaxValue;
        for (var r = -1; r < 20; r++)
        {
            var t = Stopwatch.GetTimestamp();
            for (var i = 0; i < movers.Length; i++)
                Gi.World.Move(w, movers[i],
                    (movers[i] * 5.13f + (r + 1) * 37.7f) % 1024f,
                    (movers[i] * 7.29f + (r + 1) * 11.3f) % 1024f);
            Gi.World.Process(w);
            var el = Stopwatch.GetElapsedTime(t).TotalMicroseconds;
            if (r >= 0 && el < best) best = el;
        }
        Console.WriteLine($"move-200 churn process: {best:F0} us");

        var placed = new int[200];
        best = double.MaxValue;
        for (var r = -1; r < 20; r++)
        {
            var t = Stopwatch.GetTimestamp();
            for (var i = 0; i < placed.Length; i++)
                placed[i] = Gi.World.Place(w, l,
                    (i * 41.3f + (r + 1) * 17.9f) % 1000f + 12f,
                    (i * 29.7f + (r + 1) * 23.1f) % 1000f + 12f, stamp, 8);
            Gi.World.Process(w);
            var el = Stopwatch.GetElapsedTime(t).TotalMicroseconds;
            if (r >= 0 && el < best) best = el;
            for (var i = 0; i < placed.Length; i++) Gi.World.Remove(w, placed[i]);
            Gi.World.Process(w);
        }
        Console.WriteLine($"place-200 churn process: {best:F0} us");

        best = double.MaxValue;
        long total = 0;
        for (var r = 0; r < 20; r++)
        {
            var t = Stopwatch.GetTimestamp();
            total += Gi.World.Query(w, g, l, 0, 0, 1024, 1024);
            var el = Stopwatch.GetElapsedTime(t).TotalMicroseconds;
            if (el < best) best = el;
        }
        Console.WriteLine($"full-grid sum (1024-grid): {best:F1} us ({total})");

        var region = new short[256 * 256];
        best = double.MaxValue;
        long checksum = 0;
        unsafe
        {
            fixed (short* p = region)
            {
                for (var r = 0; r < 20; r++)
                {
                    var t = Stopwatch.GetTimestamp();
                    Gi.World.QueryRegion(w, g, l, 100, 76, 256, 256, p);
                    var el = Stopwatch.GetElapsedTime(t).TotalMicroseconds;
                    if (el < best) best = el;
                    checksum += p[0] + p[65535];
                }
            }
        }
        Console.WriteLine($"query-region 256x256: {best:F1} us ({checksum})");
    }
}
