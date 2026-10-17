using System.Diagnostics;

internal static partial class Verification
{
    private static readonly byte CrowdBox4 = Gi.Stamp.Box(4, 4, 60);
    private static readonly byte CrowdBox8 = Gi.Stamp.Box(8, 8, 60);
    private static readonly byte CrowdBox9 = Gi.Stamp.Box(9, 9, 55);
    private static readonly byte CrowdBox12 = Gi.Stamp.Box(12, 12, 60);
    private static readonly byte CrowdBox16 = Gi.Stamp.Box(16, 16, 60);
    private static readonly byte CrowdSenseBox = Gi.Stamp.Box(12, 12, 90);
    private static readonly byte CrowdSenseTent = Gi.Stamp.Tent(14, 10, 80);
    private static readonly byte CrowdSenseBell = Gi.Stamp.Bell(18, 18, 100);

    private static (byte world, byte grid, byte layer, Gi.World.SourceId[] ids, float[] xs, float[] ys) Crowd(int count, int width, byte stamp, int power, int seed)
    {
        var w = Gi.World.New();
        var g = Gi.Grid.New(w, power, 0f, 0f, 1 << power);
        var l = Gi.Layer.New(w);
        var rng = new Random(seed);
        var ids = new Gi.World.SourceId[count];
        var xs = new float[count];
        var ys = new float[count];
        var span = (1 << power) - 2 * width - 8;
        for (var i = 0; i < count; i++)
        {
            xs[i] = (float)(rng.NextDouble() * span + width + 4);
            ys[i] = (float)(rng.NextDouble() * span + width + 4);
            ids[i] = Gi.World.Place(w, l, xs[i], ys[i], stamp, 8);
        }

        return (w, g, l, ids, xs, ys);
    }

    private static unsafe bool CrowdedMatchesStepped(int count, int width, byte stamp, int power, int frames, int batch)
    {
        var (crowdedWorld, cg, cl, crowdedIds, crowdedXs, crowdedYs) = Crowd(count, width, stamp, power, 91);
        var (steppedWorld, _, _, steppedIds, steppedXs, steppedYs) = Crowd(count, width, stamp, power, 91);
        var cells = 1 << power;
        var field = new short[2 * cells * cells];
        for (var frame = 0; frame < frames; frame++)
        {
            for (var i = 0; i < count; i++)
            {
                crowdedXs[i] = (crowdedXs[i] * 1.00013f + i * 0.371f + frame * 1.137f) % (cells - 2 * width - 8) + width + 4;
                crowdedYs[i] = (crowdedYs[i] * 1.00029f + i * 0.113f + frame * 2.071f) % (cells - 2 * width - 8) + width + 4;
                steppedXs[i] = crowdedXs[i];
                steppedYs[i] = crowdedYs[i];
            }

            for (var i = 0; i < count; i++) Gi.World.Move(crowdedWorld, crowdedIds[i], crowdedXs[i], crowdedYs[i]);
            Gi.World.Process(crowdedWorld);
            for (var start = 0; start < count; start += batch)
            {
                var end = Math.Min(start + batch, count);
                for (var i = start; i < end; i++) Gi.World.Move(steppedWorld, steppedIds[i], steppedXs[i], steppedYs[i]);
                Gi.World.Process(steppedWorld);
            }

            fixed (short* p = field)
            {
                Gi.World.QueryRegion(crowdedWorld, cg, cl, 0, 0, cells, cells, p);
                var q = p + cells * cells;
                Gi.World.QueryRegion(steppedWorld, cg, cl, 0, 0, cells, cells, q);
                for (var i = 0; i < cells * cells; i++)
                    if (p[i] != q[i]) return false;
            }
        }

        return true;
    }

    private static unsafe bool CrowdedIdleFramesReportNothing()
    {
        var (w, g, l, ids, xs, ys) = Crowd(200, 9, CrowdBox9, 8, 93);
        Gi.World.Process(w);
        if (Gi.World.ChangedTiles(w, g, l, null) == 0) return false;
        for (var i = 0; i < ids.Length; i++)
        {
            var leadX = (int)MathF.Floor(xs[i] * 256f);
            var leadY = (int)MathF.Floor(ys[i] * 256f);
            Gi.World.Move(w, ids[i], (leadX + 0.5f) / 256f, (leadY + 0.5f) / 256f);
        }

        Gi.World.Process(w);
        return Gi.World.ChangedTiles(w, g, l, null) == 0;
    }

    private static unsafe bool SenseColumnMatchesTrySense()
    {
        var w = Gi.World.New();
        Gi.Grid.New(w, 6, 0f, 0f, 1024f);
        Gi.Grid.New(w, 8, 0f, 0f, 256f);
        Gi.Grid.New(w, 8, 128.5f, 64.25f, 256f);
        var l = Gi.Layer.New(w);
        var stamps = new[] { CrowdSenseBox, CrowdSenseTent, CrowdSenseBell };
        var rng = new Random(95);
        for (var i = 0; i < 400; i++)
            Gi.World.Place(w, l, (float)(rng.NextDouble() * 1040 - 8), (float)(rng.NextDouble() * 1040 - 8),
                stamps[rng.Next(stamps.Length)], (sbyte)rng.Next(-16, 17));
        Gi.World.Process(w);

        const int count = 9_000;
        var xs = new float[count];
        var ys = new float[count];
        for (var i = 0; i < count; i++)
        {
            xs[i] = (float)(rng.NextDouble() * 1100 - 30);
            ys[i] = (float)(rng.NextDouble() * 1100 - 30);
        }

        var values = new short[count];
        var whole = new ulong[(count + 63) >> 6];
        bool allWhole;
        fixed (float* xp = xs, yp = ys)
        fixed (short* vp = values)
        fixed (ulong* hp = whole)
        {
            allWhole = (Gi.World.SenseColumn(w, l, xp, yp, count, vp, hp) == Gi.World.ReadStatus.Complete);
        }

        var complete = 0;
        for (var i = 0; i < count; i++)
        {
            var expected = Gi.World.TrySense(w, l, xs[i], ys[i], out var value);
            if (values[i] != value) return false;
            if ((((whole[i >> 6] >> (i & 63)) & 1) == 1) != expected) return false;
            if (expected) complete++;
        }

        return allWhole == (complete == count);
    }

    private static unsafe bool SenseColumnsMatchScalar()
    {
        var s = Layered();
        const int count = 256;
        var rng = new Random(195);
        var xs = new float[count];
        var ys = new float[count];
        var reaches = new float[count];
        var excludes = new Gi.World.SourceId[count];
        var thresholds = new short[count];
        for (var i = 0; i < count; i++)
        {
            xs[i] = (float)(rng.NextDouble() * 1100 - 30);
            ys[i] = (float)(rng.NextDouble() * 1100 - 30);
            reaches[i] = 4f + i % 24;
            excludes[i] = i % 3 == 0 ? default : new Gi.World.SourceId((uint)(1 + i % 16), 0);
            thresholds[i] = (short)(20 * (i % 5));
        }

        var pointBatch = new short[count];
        var pointWhole = new ulong[(count + 63) >> 6];
        var gxBatch = new float[count];
        var gyBatch = new float[count];
        var gradWhole = new ulong[(count + 63) >> 6];
        var areaBatch = new long[count];
        var areaWhole = new ulong[(count + 63) >> 6];
        var maxBatch = new short[count];
        var maxBx = new float[count];
        var maxBy = new float[count];
        var maxWhole = new ulong[(count + 63) >> 6];
        var nearBatch = new short[count];
        var nearNx = new float[count];
        var nearNy = new float[count];
        var nearFound = new ulong[(count + 63) >> 6];

        bool pointOk, gradOk, areaOk, maxOk, nearOk;
        fixed (float* xp = xs, yp = ys, rp = reaches, gxp = gxBatch, gyp = gyBatch, mbx = maxBx, mby = maxBy, nnx = nearNx, nny = nearNy)
        fixed (Gi.World.SourceId* ep = excludes)
        fixed (short* tp = thresholds, pvp = pointBatch, mvp = maxBatch, nvp = nearBatch)
        fixed (long* avp = areaBatch)
        fixed (ulong* pwp = pointWhole, gwp = gradWhole, awp = areaWhole, mwp = maxWhole, nfp = nearFound)
        {
            pointOk = (Gi.World.SenseColumn(s.World, s.Layer, xp, yp, ep, count, pvp, pwp) == Gi.World.ReadStatus.Complete);
            gradOk = (Gi.World.SenseGradientColumn(s.World, s.Layer, xp, yp, ep, count, gxp, gyp, gwp) == Gi.World.ReadStatus.Complete);
            areaOk = (Gi.World.SenseAreaColumn(s.World, s.Layer, xp, yp, rp, ep, count, avp, awp) == Gi.World.ReadStatus.Complete);
            maxOk = (Gi.World.SenseMaxColumn(s.World, s.Layer, xp, yp, rp, count, mvp, mbx, mby, mwp) == Gi.World.ReadStatus.Complete);
            nearOk = (Gi.World.SenseNearestColumn(s.World, s.Layer, xp, yp, rp, tp, count, nvp, nnx, nny, nfp) == Gi.World.ReadStatus.Complete);
        }

        var expectedPointAll = true;
        var expectedGradAll = true;
        var expectedAreaAll = true;
        var expectedMaxAll = true;
        var expectedNearAll = true;
        for (var i = 0; i < count; i++)
        {
            var pComplete = excludes[i].Generation != 0
                ? Gi.World.TrySense(s.World, s.Layer, xs[i], ys[i], excludes[i], out var pVal)
                : Gi.World.TrySense(s.World, s.Layer, xs[i], ys[i], out pVal);
            expectedPointAll &= pComplete;
            if (pointBatch[i] != pVal || (((pointWhole[i >> 6] >> (i & 63)) & 1) == 1) != pComplete) return false;

            var gComplete = excludes[i].Generation != 0
                ? Gi.World.TrySenseGradient(s.World, s.Layer, xs[i], ys[i], excludes[i], out var gx, out var gy)
                : Gi.World.TrySenseGradient(s.World, s.Layer, xs[i], ys[i], out gx, out gy);
            expectedGradAll &= gComplete;
            if (gxBatch[i] != gx || gyBatch[i] != gy || (((gradWhole[i >> 6] >> (i & 63)) & 1) == 1) != gComplete) return false;

            var aComplete = excludes[i].Generation != 0
                ? Gi.World.TrySenseArea(s.World, s.Layer, xs[i], ys[i], reaches[i], excludes[i], out var area)
                : Gi.World.TrySenseArea(s.World, s.Layer, xs[i], ys[i], reaches[i], out area);
            expectedAreaAll &= aComplete;
            if (areaBatch[i] != area || (((areaWhole[i >> 6] >> (i & 63)) & 1) == 1) != aComplete) return false;

            var mComplete = Gi.World.TrySenseMax(s.World, s.Layer, xs[i], ys[i], reaches[i], out var mVal, out var bx, out var by);
            expectedMaxAll &= mComplete;
            if (maxBatch[i] != mVal || maxBx[i] != bx || maxBy[i] != by || (((maxWhole[i >> 6] >> (i & 63)) & 1) == 1) != mComplete) return false;

            var nComplete = Gi.World.TrySenseNearest(s.World, s.Layer, xs[i], ys[i], reaches[i], thresholds[i], out var nVal, out var nx, out var ny);
            expectedNearAll &= nComplete;
            if (nearBatch[i] != nVal || nearNx[i] != nx || nearNy[i] != ny || (((nearFound[i >> 6] >> (i & 63)) & 1) == 1) != (nVal != short.MinValue)) return false;
        }

        if (pointOk != expectedPointAll || gradOk != expectedGradAll || areaOk != expectedAreaAll ||
            maxOk != expectedMaxAll || nearOk != expectedNearAll)
            return false;

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        fixed (float* xp = xs, yp = ys, rp = reaches, gxp = gxBatch, gyp = gyBatch, mbx = maxBx, mby = maxBy, nnx = nearNx, nny = nearNy)
        fixed (Gi.World.SourceId* ep = excludes)
        fixed (short* tp = thresholds, pvp = pointBatch, mvp = maxBatch, nvp = nearBatch)
        fixed (long* avp = areaBatch)
        fixed (ulong* pwp = pointWhole, gwp = gradWhole, awp = areaWhole, mwp = maxWhole, nfp = nearFound)
        {
            for (var r = 0; r < 8; r++)
            {
                Gi.World.SenseColumn(s.World, s.Layer, xp, yp, ep, count, pvp, pwp);
                Gi.World.SenseGradientColumn(s.World, s.Layer, xp, yp, ep, count, gxp, gyp, gwp);
                Gi.World.SenseAreaColumn(s.World, s.Layer, xp, yp, rp, ep, count, avp, awp);
                Gi.World.SenseMaxColumn(s.World, s.Layer, xp, yp, rp, count, mvp, mbx, mby, mwp);
                Gi.World.SenseNearestColumn(s.World, s.Layer, xp, yp, rp, tp, count, nvp, nnx, nny, nfp);
            }
        }

        return GC.GetAllocatedBytesForCurrentThread() - before == 0;
    }

    private static unsafe bool ChangedMaskMatchesChanged()
    {
        var (w, _, l, ids, _, _) = Crowd(600, 12, CrowdBox12, 8, 97);
        Gi.World.Process(w);
        var since = Gi.World.Tick(w);
        var rng = new Random(99);
        for (var i = 0; i < ids.Length; i += 3)
            Gi.World.Move(w, ids[i], (float)(rng.NextDouble() * 240 + 8), (float)(rng.NextDouble() * 240 + 8));
        Gi.World.Process(w);

        const int count = 9_000;
        var agents = new float[count];
        var agentYs = new float[count];
        for (var i = 0; i < count; i++)
        {
            agents[i] = (float)(rng.NextDouble() * 300 - 20);
            agentYs[i] = (float)(rng.NextDouble() * 300 - 20);
        }

        var words = new ulong[(count + 63) >> 6];
        int hits;
        fixed (float* xp = agents, yp = agentYs)
        fixed (ulong* wp = words)
        {
            hits = Gi.World.ChangedMask(w, l, xp, yp, 24f, since, count, wp);
        }

        var expected = 0;
        for (var i = 0; i < count; i++)
        {
            var changed = Gi.World.Changed(w, l, agents[i], agentYs[i], 24f, since);
            if ((((words[i >> 6] >> (i & 63)) & 1) == 1) != changed) return false;
            if (changed) expected++;
        }

        return hits == expected;
    }

    private static unsafe bool SyncMatchesMove()
    {
        var (w, _, l, ids, xs, ys) = Crowd(500, 9, CrowdBox9, 8, 101);
        var oracle = Gi.World.New();
        var ol = Gi.Layer.New(oracle);
        Gi.Grid.New(oracle, 8, 0f, 0f, 256f);
        for (var i = 0; i < ids.Length; i++) Gi.World.Place(oracle, ol, xs[i], ys[i], CrowdBox9, 8);
        var rng = new Random(103);
        for (var i = 0; i < ids.Length; i++)
        {
            xs[i] = (float)(rng.NextDouble() * 240 + 8);
            ys[i] = (float)(rng.NextDouble() * 240 + 8);
        }

        for (var i = 0; i < ids.Length; i++) Gi.World.Move(oracle, ids[i], xs[i], ys[i]);
        fixed (Gi.World.SourceId* ip = ids)
        fixed (float* xp = xs, yp = ys)
        {
            Gi.World.Sync(w, ip, xp, yp, ids.Length);
        }

        Gi.World.Process(w);
        Gi.World.Process(oracle);
        return Gi.World.Query(w, 0, l, 0, 0, 256, 256) == Gi.World.Query(oracle, 0, ol, 0, 0, 256, 256);
    }

    private static bool WarmCrowdAllocationFree()
    {
        var (w, _, _, ids, xs, ys) = Crowd(20_000, 8, CrowdBox8, 10, 105);
        Gi.World.Process(w);
        for (var frame = 0; frame < 2; frame++)
        {
            for (var i = 0; i < ids.Length; i++)
                Gi.World.Move(w, ids[i], (xs[i] * 1.00013f + i * 0.371f) % 996f + 14f, (ys[i] * 1.00029f + i * 0.113f) % 996f + 14f);
            Gi.World.Process(w);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var frame = 0; frame < 64; frame++)
        {
            for (var i = 0; i < ids.Length; i++)
                Gi.World.Move(w, ids[i], (xs[i] * 1.00013f + i * 0.371f) % 996f + 14f, (ys[i] * 1.00029f + i * 0.113f) % 996f + 14f);
            Gi.World.Process(w);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"  warm crowded process (20k all moving) over 64 frames: {allocated} B");
        return allocated == 0;
    }

    private static unsafe bool WarmColumnAllocationFree()
    {
        var (w, _, l, ids, _, _) = Crowd(4_000, 12, CrowdBox12, 10, 107);
        Gi.World.Process(w);
        var since = Gi.World.Tick(w);
        var rng = new Random(109);
        for (var i = 0; i < ids.Length; i += 4)
            Gi.World.Move(w, ids[i], (float)(rng.NextDouble() * 1024), (float)(rng.NextDouble() * 1024));
        Gi.World.Process(w);

        const int count = 10_000;
        var agents = new float[count];
        var agentYs = new float[count];
        for (var i = 0; i < count; i++)
        {
            agents[i] = (float)(rng.NextDouble() * 1100 - 30);
            agentYs[i] = (float)(rng.NextDouble() * 1100 - 30);
        }

        var values = new short[count];
        var words = new ulong[(count + 63) >> 6];
        long acc = 0;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        fixed (float* xp = agents, yp = agentYs)
        fixed (short* vp = values)
        fixed (ulong* wp = words)
        {
            for (var round = 0; round < 32; round++)
            {
                Gi.World.SenseColumn(w, l, xp, yp, count, vp, null);
                acc += Gi.World.ChangedMask(w, l, xp, yp, 24f, since, count, wp);
                acc += vp[round % count];
            }
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"  warm sense-column + changed-mask over 32 pairs (10k agents): {allocated} B ({acc})");
        return allocated == 0;
    }

    public static void CrowdTiming()
    {
        CrowdSweep(20_000, 8, CrowdBox8, 10, "20k boxes 8x8");
        CrowdSweep(100_000, 4, CrowdBox4, 10, "100k boxes 4x4");
        CrowdSweep(4_000, 16, CrowdBox16, 10, "4k boxes 16x16");
        ColumnTiming();
    }

    private static void CrowdSweep(int count, int width, byte stamp, int power, string label)
    {
        var (w, _, l, ids, xs, ys) = Crowd(count, width, stamp, power, 111);
        Gi.World.Process(w);
        var cells = 1 << power;
        var span = cells - 2 * width - 8;
        foreach (var fraction in new[] { 0.25, 0.5, 0.75, 1.0 })
        {
            var movers = (int)(count * fraction);
            var best = double.MaxValue;
            long checksum = 0;
            for (var r = -1; r < 10; r++)
            {
                for (var i = 0; i < movers; i++)
                    Gi.World.Move(w, ids[i], (xs[i] * 1.00013f + i * 0.371f + r * 1.7f) % span + width + 4,
                        (ys[i] * 1.00029f + i * 0.113f + r * 2.3f) % span + width + 4);
                var t = Stopwatch.GetTimestamp();
                Gi.World.Process(w);
                var el = Stopwatch.GetElapsedTime(t).TotalMicroseconds;
                checksum += Gi.World.Query(w, 0, l, 0, 0, 64, 64);
                if (r >= 0 && el < best) best = el;
            }

            Console.WriteLine($"{label} {fraction * 100:F0}% moving: {best:F0} us ({checksum})");
        }

        var naive = new NaiveGrid(cells);
        var rng = new Random(113);
        for (var i = 0; i < count; i++)
        {
            var x = rng.Next(span) + width + 4;
            var y = rng.Next(span) + width + 4;
            Gi.World.Move(w, ids[i], x, y);
            naive.Add(x, y, width, width, 60, 8);
        }

        Gi.World.Process(w);
        var bestN = double.MaxValue;
        for (var r = 0; r < 10; r++)
        {
            var t = Stopwatch.GetTimestamp();
            naive.Rebuild();
            var el = Stopwatch.GetElapsedTime(t).TotalMicroseconds;
            if (el < bestN) bestN = el;
        }

        var giSum = Gi.World.Query(w, 0, l, 0, 0, cells, cells);
        Console.WriteLine($"{label} naive full redraw (clear + {count} sources): {bestN:F0} us; sums match: {naive.Sum() == giSum}");
    }

    private static unsafe void ColumnTiming()
    {
        var (w, _, l, ids, _, _) = Crowd(4_000, 12, CrowdBox12, 10, 115);
        Gi.World.Process(w);
        var since = Gi.World.Tick(w);
        var rng = new Random(117);
        for (var i = 0; i < ids.Length; i += 2)
            Gi.World.Move(w, ids[i], (float)(rng.NextDouble() * 1024), (float)(rng.NextDouble() * 1024));
        Gi.World.Process(w);

        const int agents = 100_000;
        var ax = new float[agents];
        var ay = new float[agents];
        for (var i = 0; i < agents; i++)
        {
            ax[i] = (float)(rng.NextDouble() * 1100 - 30);
            ay[i] = (float)(rng.NextDouble() * 1100 - 30);
        }

        var values = new short[agents];
        var words = new ulong[(agents + 63) >> 6];
        var best = double.MaxValue;
        long acc = 0;
        fixed (float* xp = ax, yp = ay)
        fixed (short* vp = values)
        {
            for (var r = 0; r < 20; r++)
            {
                var t = Stopwatch.GetTimestamp();
                Gi.World.SenseColumn(w, l, xp, yp, agents, vp, null);
                var el = Stopwatch.GetElapsedTime(t).TotalMicroseconds;
                acc += vp[r];
                if (el < best) best = el;
            }
        }

        Console.WriteLine($"sense-column 100k agents: {best:F0} us ({best * 1000 / agents:F2} ns per agent) ({acc})");

        best = double.MaxValue;
        acc = 0;
        fixed (float* xp = ax, yp = ay)
        {
            for (var r = 0; r < 20; r++)
            {
                var t = Stopwatch.GetTimestamp();
                for (var i = 0; i < 100_000; i++) acc += Gi.World.TrySense(w, l, xp[i], yp[i], out var value) ? value : 0;
                var el = Stopwatch.GetElapsedTime(t).TotalMicroseconds;
                if (el < best) best = el;
            }
        }

        Console.WriteLine($"try-sense loop 100k agents: {best:F0} us ({best * 1000 / agents:F2} ns per agent) ({acc})");

        best = double.MaxValue;
        int hits = 0;
        fixed (float* xp = ax, yp = ay)
        fixed (ulong* wp = words)
        {
            for (var r = 0; r < 20; r++)
            {
                var t = Stopwatch.GetTimestamp();
                hits = Gi.World.ChangedMask(w, l, xp, yp, 24f, since, agents, wp);
                var el = Stopwatch.GetElapsedTime(t).TotalMicroseconds;
                if (el < best) best = el;
            }
        }

        Console.WriteLine($"changed-mask 100k agents (reach 24): {best:F0} us ({hits} changed)");

        best = double.MaxValue;
        fixed (float* xp = ax, yp = ay)
        {
            acc = 0;
            for (var r = 0; r < 20; r++)
            {
                var t = Stopwatch.GetTimestamp();
                for (var i = 0; i < 100_000; i++) acc += Gi.World.Changed(w, l, xp[i], yp[i], 24f, since) ? 1 : 0;
                var el = Stopwatch.GetElapsedTime(t).TotalMicroseconds;
                if (el < best) best = el;
            }
        }

        Console.WriteLine($"changed loop 100k agents: {best:F0} us ({acc})");

        var (sw, _, _, sids, sxs, sys) = Crowd(100_000, 8, CrowdBox8, 10, 119);
        Gi.World.Process(sw);
        best = double.MaxValue;
        fixed (Gi.World.SourceId* ip = sids)
        fixed (float* xp = sxs, yp = sys)
        {
            for (var r = -1; r < 10; r++)
            {
                var t = Stopwatch.GetTimestamp();
                for (var i = 0; i < sids.Length; i++)
                {
                    sxs[i] = (sxs[i] * 1.00013f + i * 0.371f + r * 1.9f) % 996f + 14f;
                    sys[i] = (sys[i] * 1.00029f + i * 0.113f + r * 2.1f) % 996f + 14f;
                }

                Gi.World.Sync(sw, ip, xp, yp, sids.Length);
                var el = Stopwatch.GetElapsedTime(t).TotalMicroseconds;
                if (r >= 0 && el < best) best = el;
                Gi.World.Process(sw);
            }
        }

        Console.WriteLine($"sync 100k positions (excludes process): {best:F0} us");

        best = double.MaxValue;
        for (var r = -1; r < 10; r++)
        {
            var t = Stopwatch.GetTimestamp();
            for (var i = 0; i < sids.Length; i++) Gi.World.Move(sw, sids[i], sxs[i], sys[i]);
            var el = Stopwatch.GetElapsedTime(t).TotalMicroseconds;
            if (r >= 0 && el < best) best = el;
        }

        Console.WriteLine($"move loop 100k sources: {best:F0} us");
    }
}
