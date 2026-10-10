using System.Diagnostics;

internal static partial class Verification
{
    private sealed record GridSpec(byte Id, int Power, float X, float Y, float Size)
    {
        public int Cells => 1 << Power;
        public int ScaleQ8 => (int)(Cells / Size * 256f);
        public long Q8(float value, float origin) => (int)MathF.Floor((value - origin) * ScaleQ8);
    }

    private sealed class SenseWorld
    {
        public byte World;
        public byte Layer;
        public GridSpec[] Grids = [];
    }

    private static SenseWorld? _layered;

    private static SenseWorld Layered()
    {
        if (_layered != null) return _layered;

        var w = Gi.World.New();
        GridSpec[] specs =
        [
            new(0, 6, 0f, 0f, 1024f),
            new(1, 8, 0f, 0f, 256f),
            new(2, 8, 128.5f, 64.25f, 256f),
            new(3, 9, 300f, 300f, 256f),
        ];
        foreach (var s in specs) Gi.Grid.New(w, s.Power, s.X, s.Y, s.Size);
        var l = Gi.Layer.New(w);
        var stamps = SenseStamps();
        var rng = new Random(404);
        for (var i = 0; i < 700; i++)
            Gi.World.Place(w, l, (float)(rng.NextDouble() * 1040 - 8), (float)(rng.NextDouble() * 1040 - 8),
                stamps[rng.Next(stamps.Length)], rng.Next(-16, 17));
        for (var i = 0; i < 40; i++) Gi.World.Place(w, l, 140f, 140f, stamps[0], 16);
        Gi.World.Process(w);
        _layered = new SenseWorld { World = w, Layer = l, Grids = specs };
        return _layered;
    }

    private static byte[] SenseStamps()
    {
        var samples = new sbyte[7 * 5];
        for (var i = 0; i < samples.Length; i++) samples[i] = (sbyte)(i * 23 % 61 - 20);
        return
        [
            Gi.Stamp.Box(12, 12, 90),
            Gi.Stamp.Box(5, 9, -60),
            Gi.Stamp.Tent(14, 10, 80),
            Gi.Stamp.Bell(18, 18, 100),
            Gi.Stamp.New(samples, 7, 5),
        ];
    }

    private static int OraclePick(GridSpec[] grids, float x, float y, float reach, int margin, bool disk, out bool complete)
    {
        var pick = -1;
        complete = false;
        var scale = 0;
        foreach (var g in grids)
        {
            if (g.ScaleQ8 == 0) continue;
            var qx = g.Q8(x, g.X);
            var qy = g.Q8(y, g.Y);
            if (qx < 0 || qy < 0 || (qx >> 8) >= g.Cells || (qy >> 8) >= g.Cells) continue;

            bool whole;
            if (disk)
            {
                var (x0, y0, x1, y1) = OracleDiskBox(g, x, y, reach);
                whole = x0 >= 0 && y0 >= 0 && x1 < g.Cells && y1 < g.Cells;
            }
            else
            {
                var cx = qx >> 8;
                var cy = qy >> 8;
                whole = cx - margin >= 0 && cy - margin >= 0 && cx + margin < g.Cells && cy + margin < g.Cells;
            }

            if (pick >= 0 && !(whole && !complete) && !(whole == complete && g.ScaleQ8 > scale)) continue;
            pick = g.Id;
            complete = whole;
            scale = g.ScaleQ8;
        }

        return pick;
    }

    private static long OracleReach(GridSpec g, float reach)
        => reach > 0f ? (long)MathF.Floor(reach * g.ScaleQ8) : 0;

    private static (long x0, long y0, long x1, long y1) OracleDiskBox(GridSpec g, float x, float y, float reach)
    {
        var qx = g.Q8(x, g.X);
        var qy = g.Q8(y, g.Y);
        var r = OracleReach(g, reach);
        var cx = qx >> 8;
        var cy = qy >> 8;
        return (Math.Min((long)Math.Ceiling((qx - r - 128) / 256.0), cx), Math.Min((long)Math.Ceiling((qy - r - 128) / 256.0), cy),
            Math.Max((long)Math.Floor((qx + r - 128) / 256.0), cx), Math.Max((long)Math.Floor((qy + r - 128) / 256.0), cy));
    }

    private static IEnumerable<(int cx, int cy)> OracleDiskCells(GridSpec g, float x, float y, float reach)
    {
        var qx = g.Q8(x, g.X);
        var qy = g.Q8(y, g.Y);
        var r = OracleReach(g, reach);
        var (x0, y0, x1, y1) = OracleDiskBox(g, x, y, reach);
        for (var cy = Math.Max(y0, 0); cy <= Math.Min(y1, g.Cells - 1); cy++)
        for (var cx = Math.Max(x0, 0); cx <= Math.Min(x1, g.Cells - 1); cx++)
        {
            var dx = cx * 256 + 128 - qx;
            var dy = cy * 256 + 128 - qy;
            var center = cx == qx >> 8 && cy == qy >> 8;
            if (center || dx * dx + dy * dy <= r * r) yield return ((int)cx, (int)cy);
        }
    }

    private static long OracleWorldArea(long cells, int scaleQ8)
    {
        var den = (long)scaleQ8 * scaleQ8;
        var magnitude = (Math.Abs(cells) * 65536 + den / 2) / den;
        return cells < 0 ? -magnitude : magnitude;
    }

    private static (float x, float y, float reach)[] SenseProbes(int seed, int count)
    {
        var rng = new Random(seed);
        var probes = new (float, float, float)[count];
        float[] edges = [0f, 255.9f, 256f, 128f, 384f, 300f, 556f, 1023.9f, 1024f];
        for (var i = 0; i < count; i++)
        {
            var x = i % 4 == 0 ? edges[rng.Next(edges.Length)] + (float)(rng.NextDouble() * 6 - 3) : (float)(rng.NextDouble() * 1100 - 40);
            var y = i % 5 == 0 ? edges[rng.Next(edges.Length)] + (float)(rng.NextDouble() * 6 - 3) : (float)(rng.NextDouble() * 1100 - 40);
            probes[i] = (x, y, i % 7 == 0 ? 0f : (float)(rng.NextDouble() * (i % 3 == 0 ? 90 : 18)));
        }

        return probes;
    }

    private static bool SensePicksFinestAndReportsGaps()
    {
        var s = Layered();
        foreach (var (x, y, _) in SenseProbes(11, 4000))
        {
            var expected = OraclePick(s.Grids, x, y, 0f, 0, false, out var complete);
            var ok = Gi.World.TrySense(s.World, s.Layer, x, y, out var value);
            if (Gi.World.Covers(s.World, x, y) != expected >= 0) return false;
            if (expected < 0)
            {
                if (ok || value != 0) return false;
                continue;
            }

            var g = s.Grids[expected];
            var cell = Gi.World.Query(s.World, g.Id, s.Layer, (int)(g.Q8(x, g.X) >> 8), (int)(g.Q8(y, g.Y) >> 8));
            if (ok != complete || value != cell) return false;
        }

        return !Gi.World.TrySense(s.World, s.Layer, 1100f, 50f, out var gap) && gap == 0 &&
            !Gi.World.TrySenseArea(s.World, s.Layer, -50f, 50f, 10f, out var gapArea) && gapArea == 0;
    }

    private static bool SenseAreaMatchesDiskScan()
    {
        var s = Layered();
        foreach (var (x, y, reach) in SenseProbes(12, 1500))
        {
            var expected = OraclePick(s.Grids, x, y, reach, 0, true, out var complete);
            var ok = Gi.World.TrySenseArea(s.World, s.Layer, x, y, reach, out var total);
            if (expected < 0)
            {
                if (ok || total != 0) return false;
                continue;
            }

            var g = s.Grids[expected];
            var cells = 0L;
            foreach (var (cx, cy) in OracleDiskCells(g, x, y, reach)) cells += Gi.World.Query(s.World, g.Id, s.Layer, cx, cy);
            if (ok != complete || total != OracleWorldArea(cells, g.ScaleQ8)) return false;
        }

        return Gi.World.TrySenseArea(s.World, s.Layer, 250f, 130f, 20f, out var seam) &&
            OraclePick(s.Grids, 250f, 130f, 20f, 0, true, out _) == 2 &&
            Gi.World.TrySenseArea(s.World, s.Layer, 60f, 260f, 20f, out _) &&
            OraclePick(s.Grids, 60f, 260f, 20f, 0, true, out _) == 0 && seam != long.MinValue;
    }

    private static bool SenseAreaConservesAcrossGrids()
    {
        var fine = Gi.World.New();
        Gi.Grid.New(fine, 9, 0f, 0f, 512f);
        var lf = Gi.Layer.New(fine);
        var coarse = Gi.World.New();
        Gi.Grid.New(coarse, 7, 0f, 0f, 512f);
        var lc = Gi.Layer.New(coarse);
        var box = Gi.Stamp.Box(16, 16, 70);
        var rng = new Random(77);
        for (var i = 0; i < 60; i++)
        {
            var x = 200 + rng.Next(29) * 4;
            var y = 200 + rng.Next(29) * 4;
            var gain = 1 + rng.Next(16);
            Gi.World.Place(fine, lf, x, y, box, gain);
            Gi.World.Place(coarse, lc, x, y, box, gain);
        }

        Gi.World.Process(fine);
        Gi.World.Process(coarse);
        var a = Gi.World.TrySenseArea(fine, lf, 256f, 256f, 120f, out var fineTotal);
        var b = Gi.World.TrySenseArea(coarse, lc, 256f, 256f, 120f, out var coarseTotal);
        var raw = Gi.World.Query(fine, 0, lf, 0, 0, 512, 512);
        return a && b && fineTotal == coarseTotal && fineTotal == raw && fineTotal > 0;
    }

    private static bool SenseMaxMatchesDiskScan()
    {
        var s = Layered();
        foreach (var (x, y, reach) in SenseProbes(13, 1500))
        {
            var expected = OraclePick(s.Grids, x, y, reach, 0, true, out var complete);
            var ok = Gi.World.TrySenseMax(s.World, s.Layer, x, y, reach, out var value, out var bx, out var by);
            if (expected < 0)
            {
                if (ok || value != 0 || bx != x || by != y) return false;
                continue;
            }

            var g = s.Grids[expected];
            var best = short.MinValue;
            var bestCx = -1;
            var bestCy = -1;
            foreach (var (cx, cy) in OracleDiskCells(g, x, y, reach))
            {
                var v = Gi.World.Query(s.World, g.Id, s.Layer, cx, cy);
                if (bestCx >= 0 && (v < best || (v == best && (cy > bestCy || (cy == bestCy && cx > bestCx))))) continue;
                best = v;
                bestCx = cx;
                bestCy = cy;
            }

            var ex = g.X + ((bestCx << 8) + 128) / (float)g.ScaleQ8;
            var ey = g.Y + ((bestCy << 8) + 128) / (float)g.ScaleQ8;
            if (ok != complete || value != best || bx != ex || by != ey) return false;
        }

        return true;
    }

    private static bool SenseGradientPerWorldUnit()
    {
        var s = Layered();
        foreach (var (x, y, _) in SenseProbes(14, 3000))
        {
            var expected = OraclePick(s.Grids, x, y, 0f, 1, false, out var complete);
            var ok = Gi.World.TrySenseGradient(s.World, s.Layer, x, y, out var gx, out var gy);
            if (expected < 0)
            {
                if (ok || gx != 0f || gy != 0f) return false;
                continue;
            }

            var g = s.Grids[expected];
            var cx = (int)(g.Q8(x, g.X) >> 8);
            var cy = (int)(g.Q8(y, g.Y) >> 8);
            var perUnit = g.ScaleQ8 / 512f;
            var ex = (Gi.World.Query(s.World, g.Id, s.Layer, cx + 1, cy) - Gi.World.Query(s.World, g.Id, s.Layer, cx - 1, cy)) * perUnit;
            var ey = (Gi.World.Query(s.World, g.Id, s.Layer, cx, cy + 1) - Gi.World.Query(s.World, g.Id, s.Layer, cx, cy - 1)) * perUnit;
            if (ok != complete || gx != ex || gy != ey) return false;
        }

        return OraclePick(s.Grids, 255.5f, 30f, 0f, 1, false, out var edge) == 0 && edge;
    }

    private static bool SenseExcludeMatchesRemoval()
    {
        var w = Gi.World.New();
        Gi.Grid.New(w, 9, 0f, 0f, 256f);
        Gi.Grid.New(w, 8, 0f, 0f, 256f);
        Gi.Grid.New(w, 6, 0f, 0f, 256f);
        var l = Gi.Layer.New(w);
        var other = Gi.Layer.New(w);
        var stamps = SenseStamps();
        var rng = new Random(505);
        var ids = new List<int>();
        for (var i = 0; i < 260; i++)
            ids.Add(Gi.World.Place(w, i % 9 == 0 ? other : l, (float)(rng.NextDouble() * 270 - 7),
                (float)(rng.NextDouble() * 270 - 7), stamps[rng.Next(stamps.Length)], rng.Next(-16, 17)));
        var piles = new List<int>();
        for (var i = 0; i < 60; i++) piles.Add(Gi.World.Place(w, l, 100.3f, 77.7f, stamps[i % 2 == 0 ? 0 : 3], 16));
        for (var i = 0; i < 40; i++) piles.Add(Gi.World.Place(w, l, 180.6f, 160.2f, stamps[1], 16));
        ids.AddRange(piles);
        Gi.World.Process(w);

        var points = new (float x, float y, float r)[24];
        var before = new short[points.Length];
        var beforeArea = new long[points.Length];
        var excluded = new short[points.Length];
        var excludedArea = new long[points.Length];
        var excludedGx = new float[points.Length];
        var excludedGy = new float[points.Length];
        for (var trial = 0; trial < 120; trial++)
        {
            var id = trial < 16 ? piles[trial < 8 ? trial * 3 : 60 + (trial - 8) * 3] : ids[rng.Next(ids.Count)];
            var anchor = trial < 8 ? (x: 100.3f, y: 77.7f) : (x: 180.6f, y: 160.2f);
            if (trial >= 16)
                anchor = ((float)(rng.NextDouble() * 256), (float)(rng.NextDouble() * 256));
            for (var p = 0; p < points.Length; p++)
            {
                points[p] = (anchor.x + (float)(rng.NextDouble() * 24 - 12), anchor.y + (float)(rng.NextDouble() * 24 - 12),
                    (float)(rng.NextDouble() * (p % 2 == 0 ? 6 : 40)));
                Gi.World.TrySense(w, l, points[p].x, points[p].y, out before[p]);
                Gi.World.TrySenseArea(w, l, points[p].x, points[p].y, points[p].r, out beforeArea[p]);
                Gi.World.TrySense(w, l, points[p].x, points[p].y, id, out excluded[p]);
                Gi.World.TrySenseArea(w, l, points[p].x, points[p].y, points[p].r, id, out excludedArea[p]);
                Gi.World.TrySenseGradient(w, l, points[p].x, points[p].y, id, out excludedGx[p], out excludedGy[p]);
            }

            Gi.World.Record(w);
            Gi.World.Remove(w, id);
            Gi.World.Process(w);
            for (var p = 0; p < points.Length; p++)
            {
                Gi.World.TrySense(w, l, points[p].x, points[p].y, out var removed);
                Gi.World.TrySenseArea(w, l, points[p].x, points[p].y, points[p].r, out var removedArea);
                Gi.World.TrySenseGradient(w, l, points[p].x, points[p].y, out var removedGx, out var removedGy);
                if (removed != excluded[p] || removedArea != excludedArea[p] ||
                    removedGx != excludedGx[p] || removedGy != excludedGy[p]) return false;
            }

            Gi.World.Rewind(w);
            Gi.World.Process(w);
            for (var p = 0; p < points.Length; p++)
            {
                Gi.World.TrySense(w, l, points[p].x, points[p].y, out var restored);
                Gi.World.TrySenseArea(w, l, points[p].x, points[p].y, points[p].r, out var restoredArea);
                if (restored != before[p] || restoredArea != beforeArea[p]) return false;
            }
        }

        return true;
    }

    private static bool SenseExcludeReadsAppliedState()
    {
        var w = Gi.World.New();
        Gi.Grid.New(w, 8, 0f, 0f, 256f);
        var l = Gi.Layer.New(w);
        var box = Gi.Stamp.Box(10, 10, 80);
        var a = Gi.World.Place(w, l, 100f, 100f, box, 8);
        var b = Gi.World.Place(w, l, 104f, 100f, box, 5);
        Gi.World.Process(w);

        Gi.World.TrySense(w, l, 102f, 100f, a, out var settled);
        Gi.World.TrySenseArea(w, l, 102f, 100f, 9f, a, out var settledArea);
        Gi.World.Move(w, a, 180f, 30f);
        Gi.World.TrySense(w, l, 102f, 100f, a, out var pendingMove);
        Gi.World.TrySenseArea(w, l, 102f, 100f, 9f, a, out var pendingMoveArea);

        var fresh = Gi.World.Place(w, l, 102f, 100f, box, 9);
        Gi.World.TrySense(w, l, 102f, 100f, out var plain);
        Gi.World.TrySense(w, l, 102f, 100f, fresh, out var pendingPlace);

        Gi.World.Process(w);
        Gi.World.Remove(w, b);
        Gi.World.Process(w);
        Gi.World.TrySense(w, l, 102f, 100f, out var afterRemove);
        Gi.World.TrySense(w, l, 102f, 100f, b, out var stale);
        Gi.World.TrySense(w, l, 102f, 100f, 12345, out var bogus);

        return settled == 400 && settledArea > 0 && pendingMove == settled && pendingMoveArea == settledArea &&
            pendingPlace == plain && stale == afterRemove && bogus == afterRemove;
    }

    private static bool WarmSenseAllocationFree()
    {
        var s = Layered();
        long acc = 0;
        for (var i = 0; i < 100; i++)
        {
            Gi.World.TrySense(s.World, s.Layer, i * 9.7f, i * 7.3f, out var v);
            Gi.World.TrySenseArea(s.World, s.Layer, i * 9.7f, i * 7.3f, 25f, out var t);
            acc += v + t;
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var start = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 20_000; i++)
        {
            var x = i % 1031 * 0.997f;
            var y = i % 977 * 1.031f;
            Gi.World.TrySense(s.World, s.Layer, x, y, out var v);
            Gi.World.TrySense(s.World, s.Layer, x, y, 5, out var e);
            Gi.World.TrySenseArea(s.World, s.Layer, x, y, 12f, out var t);
            Gi.World.TrySenseArea(s.World, s.Layer, x, y, 12f, 5, out var te);
            Gi.World.TrySenseMax(s.World, s.Layer, x, y, 12f, out var m, out _, out _);
            Gi.World.TrySenseGradient(s.World, s.Layer, x, y, out var gx, out _);
            Gi.World.TrySenseGradient(s.World, s.Layer, x, y, 5, out var ex, out _);
            acc += v + e + t + te + m + (long)gx + (long)ex;
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - start;
        Console.WriteLine($"  warm sense over 20k points x 7 queries: {allocated} B ({acc})");
        return allocated == 0;
    }

    private static void SenseTiming(byte w, byte l, int source)
    {
        var points = new (float x, float y)[1000];
        var rng = new Random(91);
        for (var i = 0; i < points.Length; i++) points[i] = ((float)(rng.NextDouble() * 1000 + 12), (float)(rng.NextDouble() * 1000 + 12));
        Gi.World.TrySense(w, l, 0f, 0f, source, out var sx);
        long acc = sx;

        double PerPoint(Func<float, float, long> sense)
        {
            var best = double.MaxValue;
            for (var r = 0; r < 20; r++)
            {
                var t = Stopwatch.GetTimestamp();
                foreach (var (x, y) in points) acc += sense(x, y);
                best = Math.Min(best, Stopwatch.GetElapsedTime(t).TotalMicroseconds * 1000 / points.Length);
            }

            return best;
        }

        var raw = PerPoint((x, y) => Gi.World.QueryAt(w, 0, l, x, y));
        var point = PerPoint((x, y) => { Gi.World.TrySense(w, l, x, y, out var v); return v; });
        var exclude = PerPoint((x, y) => { Gi.World.TrySense(w, l, x, y, source, out var v); return v; });
        var near = PerPoint((x, y) => { Gi.World.TrySenseArea(w, l, x, y, 8f, out var v); return v; });
        var far = PerPoint((x, y) => { Gi.World.TrySenseArea(w, l, x, y, 64f, out var v); return v; });
        var max = PerPoint((x, y) => { Gi.World.TrySenseMax(w, l, x, y, 32f, out var v, out _, out _); return v; });
        var gradient = PerPoint((x, y) => { Gi.World.TrySenseGradient(w, l, x, y, out var gx, out _); return (long)gx; });
        var me = Gi.World.Place(w, l, 512.3f, 511.6f, Gi.Stamp.Bell(24, 24, 90), 12);
        Gi.World.Process(w);
        var selfPoint = PerPoint((_, _) => { Gi.World.TrySense(w, l, 512.3f, 511.6f, me, out var v); return v; });
        var selfArea = PerPoint((_, _) => { Gi.World.TrySenseArea(w, l, 512.3f, 511.6f, 24f, me, out var v); return v; });
        Gi.World.Remove(w, me);
        Gi.World.Process(w);
        Console.WriteLine($"sense point: {point:F1} ns (QueryAt in same harness {raw:F1} ns) | exclude-self point (off footprint): {exclude:F1} ns");
        Console.WriteLine($"sense area r=8: {near:F0} ns | r=64: {far:F0} ns | max r=32: {max:F0} ns | gradient: {gradient:F1} ns");
        Console.WriteLine($"exclude-self at own position (24x24 bell): point {selfPoint:F0} ns | area r=24 {selfArea:F0} ns ({acc})");
    }
}
