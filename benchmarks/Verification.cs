using System.Diagnostics;
using Gi;

internal static partial class Verification
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
        Check("region-sum-matches-cell-scans", RegionSumMatchesCellScans());
        Check("query-at-matches-deposits", QueryAtMatchesDeposits());
        Check("query-max-matches-full-scan", QueryMaxMatchesFullScan());
        Check("query-max-region-matches-scan", QueryMaxRegionMatchesScan());
        Check("gradient-matches-central-differences", GradientMatchesCentralDifferences());
        Check("changed-tiles-match-drain", ChangedTilesMatchDrain());
        Check("deferred-window-matches-stepped-processing", DeferredWindowMatchesSteppedProcessing());
        Check("tent-matches-impulse-oracle", TentMatchesImpulseOracle());
        Check("bell-matches-paraboloid-oracle", BellMatchesParaboloidOracle());
        Check("source-slots-reuse-and-stale-handles-inert", SourceSlotsReuseAndStaleInert());
        Check("fifo-free-ring-delays-slot-reuse", FifoFreeRingDelaysSlotReuse());
        Check("rewind-restores-recorded-state", RewindRestoresRecordedState());
        Check("signed-gain-exact", SignedGainExact());
        Check("multi-layer-churn-matches-scans", MultiLayerChurnMatchesScans());
        Check("saturated-sum-clamps", SaturatedSumClamps());
        Check("cross-grid-sums-conserve-world-integral", CrossGridSumsConserve());
        Check("kernels-share-box-units-and-centre", KernelsShareBoxUnitsAndCentre());
        Check("kernels-move-smoothly", KernelsMoveSmoothly());
        Check("kernels-hold-strength-at-every-scale", KernelsHoldStrengthAtEveryScale());
        Check("sub-cell-kernels-reach-their-support", SubCellKernelsReachTheirSupport());
        Check("sense-picks-finest-covering-grid-and-reports-gaps", SensePicksFinestAndReportsGaps());
        Check("sense-area-matches-disk-scan", SenseAreaMatchesDiskScan());
        Check("sense-area-conserves-across-grids", SenseAreaConservesAcrossGrids());
        Check("sense-max-matches-disk-scan", SenseMaxMatchesDiskScan());
        Check("sense-gradient-per-world-unit", SenseGradientPerWorldUnit());
        Check("sense-exclude-matches-removal", SenseExcludeMatchesRemoval());
        Check("sense-exclude-reads-applied-state", SenseExcludeReadsAppliedState());
        Check("warm-sense-allocates-0-bytes", WarmSenseAllocationFree());
        Check("derived-layers-match-cell-formulas", DerivedLayersMatchCellFormulas());
        Check("derived-exclude-matches-removal", DerivedExcludeMatchesRemoval());
        Check("derived-layers-follow-sources-rewind-and-clear", DerivedLayersFollowSourcesRewindAndClear());
        Check("warm-derived-process-allocates-0-bytes", WarmDerivedProcessAllocationFree());
        Check("turned-and-round-stamps-match-oracle", TurnedAndRoundStampsMatchOracle());
        Check("round-kernels-share-box-units-and-centre", RoundKernelsShareBoxUnitsAndCentre());
        Check("stamps-turn-and-scale-smoothly", StampsTurnAndScaleSmoothly());
        Check("turn-and-scale-round-trip-exactly", TurnAndScaleRoundTripExactly());
        Check("stamps-wider-than-grid-cover-it", StampsWiderThanGridCoverIt());
        Check("turned-exclude-matches-removal", TurnedExcludeMatchesRemoval());
        Check("warm-turned-process-allocates-0-bytes", WarmTurnedProcessAllocationFree());
        Check("fade-steps-match-schedule", FadeStepsMatchSchedule());
        Check("expire-removes-on-schedule", ExpireRemovesOnSchedule());
        Check("rewind-resumes-schedules", RewindResumesSchedules());
        Check("warm-fade-process-allocates-0-bytes", WarmFadeProcessAllocationFree());
        Check("changed-since-matches-epochs", ChangedSinceMatchesEpochs());
        Check("sense-nearest-matches-scan", SenseNearestMatchesScan());
        Check("crowded-rebuild-matches-stepped-incremental", CrowdedMatchesStepped(3000, 8, CrowdBox8, 10, 4, 500));
        Check("crowded-small-boxes-match-stepped-incremental", CrowdedMatchesStepped(300, 4, CrowdBox4, 9, 4, 20));
        Check("crowded-unchanged-frames-report-no-changed-tiles", CrowdedIdleFramesReportNothing());
        Check("sense-column-matches-try-sense", SenseColumnMatchesTrySense());
        Check("sense-columns-match-scalar", SenseColumnsMatchScalar());
        Check("changed-mask-matches-changed", ChangedMaskMatchesChanged());
        Check("sync-matches-move", SyncMatchesMove());
        Check("warm-crowded-process-allocates-0-bytes", WarmCrowdAllocationFree());
        Check("warm-column-batches-allocate-0-bytes", WarmColumnAllocationFree());
        Check("source-batches-match-serial-and-allocate-0-bytes", SourceBatchesMatchSerialAndAllocateNothing());
        Check("source-batches-retract-across-layers", SourceBatchesRetractAcrossLayers());
        Check("transform-updates-match-ordered-calls", TransformUpdatesMatchOrderedCallsAndAllocateNothing());
        Check("sync-transforms-match-ordered-calls", SyncTransformsMatchOrderedCallsAndAllocateNothing());
        Check("mixed-stamp-process-allocates-zero-bytes", MixedStampProcessAllocatesZeroBytes());
        Check("precision-cell-skips-subcell-moves-and-matches-exclusion",
            PrecisionCellSkipsSubcellMovesAndMatchesExclusion());

        Console.WriteLine(failures == 0 ? "verification: all receipts green" : $"verification: {failures} failures");
        return failures == 0 ? 0 : 1;
    }

    private static int RoundQ16(int value)
    {
        return value < 0 ? -((-value + 32768) >> 16) : (value + 32768) >> 16;
    }

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
        if (phase == 0)
        {
            bands[0] = (0, length, 256);
            return 1;
        }

        bands[0] = (0, 1, 256 - phase);
        if (length == 1)
        {
            bands[1] = (1, 2, phase);
            return 2;
        }

        bands[1] = (1, length, 256);
        bands[2] = (length, length + 1, phase);
        return 3;
    }

    private static bool ProcessMatchesOracle()
    {
        var w = World.New();
        var g = Grid.New(w, 8, 0f, 0f, 256f);
        var l = Layer.New(w);
        var box = Stamp.Box(16, 16, 100);
        var rng = new Random(9);
        var field = new int[256 * 256];
        for (var i = 0; i < 300; i++)
        {
            var x = (float)(rng.NextDouble() * 250);
            var y = (float)(rng.NextDouble() * 250);
            World.Place(w, l, x, y, box, 8);
            var qx = (int)MathF.Floor(x * 256f) - 16 * 128;
            var qy = (int)MathF.Floor(y * 256f) - 16 * 128;
            var piece = OracleBox(256, qx >> 8, qy >> 8, qx & 255, qy & 255, 16, 16, 100, 8);
            for (var c = 0; c < field.Length; c++) field[c] += piece[c];
        }

        World.Process(w);
        for (var i = 0; i < 2000; i++)
        {
            var cx = rng.Next(256);
            var cy = rng.Next(256);
            var expected = Math.Clamp(field[cy * 256 + cx], short.MinValue, short.MaxValue);
            if (World.Query(w, g, l, cx, cy) != expected) return false;
        }

        return true;
    }

    private static bool ProcessDeterministic()
    {
        var a = World.New();
        var ga = Grid.New(a, 7, 0f, 0f, 128f);
        var la = Layer.New(a);
        var b = World.New();
        var gb = Grid.New(b, 7, 0f, 0f, 128f);
        var lb = Layer.New(b);
        var stamp = Stamp.Box(10, 6, 55);
        var rng = new Random(13);
        for (var i = 0; i < 200; i++)
        {
            var x = (float)(rng.NextDouble() * 128);
            var y = (float)(rng.NextDouble() * 128);
            World.Place(a, la, x, y, stamp, 9);
            World.Place(b, lb, x, y, stamp, 9);
        }

        World.Process(a);
        World.Process(b);
        for (var i = 0; i < 500; i++)
        {
            var cx = rng.Next(128);
            var cy = rng.Next(128);
            if (World.Query(a, ga, la, cx, cy) != World.Query(b, gb, lb, cx, cy))
                return false;
        }

        return true;
    }

    private static bool RemoveRestoresBaseline()
    {
        var w = World.New();
        var g = Grid.New(w, 6, 0f, 0f, 64f);
        var l = Layer.New(w);
        var stamp = Stamp.Box(8, 8, 50);
        var s = World.Place(w, l, 20f, 20f, stamp, 6);
        World.Process(w);
        if (World.Query(w, g, l, 20, 20) != 300) return false;
        World.Remove(w, s);
        World.Process(w);
        return World.Query(w, g, l, 20, 20) == 0;
    }

    private static bool WarmProcessAllocationFree()
    {
        var w = World.New();
        Grid.New(w, 8, 0f, 0f, 256f);
        var l = Layer.New(w);
        var stamp = Stamp.Box(12, 12, 40);
        var rng = new Random(5);
        for (var i = 0; i < 400; i++)
            World.Place(w, l, (float)(rng.NextDouble() * 256), (float)(rng.NextDouble() * 256), stamp, 8);
        World.Process(w);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 64; i++) World.Process(w);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"  warm process (unchanged) over 64 runs: {allocated} B");
        if (allocated != 0) return false;

        var s = World.Place(w, l, 100.25f, 100.5f, stamp, 8);
        World.Process(w);
        World.Remove(w, s);
        World.Process(w);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 64; i++)
        {
            var id = World.Place(w, l, 100.25f, 100.5f, stamp, 8);
            World.Process(w);
            World.Remove(w, id);
            World.Process(w);
        }

        allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"  warm process (place/remove churn) over 64 pairs: {allocated} B");
        return allocated == 0;
    }

    private static bool WarmQueryAllocationFree()
    {
        var w = World.New();
        var g = Grid.New(w, 8, 0f, 0f, 256f);
        var l = Layer.New(w);
        var stamp = Stamp.Box(12, 12, 40);
        var rng = new Random(5);
        for (var i = 0; i < 200; i++)
            World.Place(w, l, (float)(rng.NextDouble() * 256), (float)(rng.NextDouble() * 256), stamp, 8);
        World.Process(w);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        long acc = 0;
        for (var i = 0; i < 200_000; i++) acc += World.Query(w, g, l, i % 251, i * 7 % 251);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        _ = acc;
        Console.WriteLine($"  warm query over 200k cells: {allocated} B");
        return allocated == 0;
    }

    private static unsafe bool QueryRegionMatchesCells()
    {
        var w = World.New();
        var g = Grid.New(w, 8, 0f, 0f, 256f);
        var l = Layer.New(w);
        var box = Stamp.Box(14, 14, 80);
        var samples = new sbyte[6 * 9];
        for (var i = 0; i < samples.Length; i++) samples[i] = (sbyte)(i * 17 % 40 - 20);
        var raster = Stamp.New(samples, 6, 9);
        var rng = new Random(23);
        for (var i = 0; i < 300; i++)
        {
            var x = (float)(rng.NextDouble() * 256);
            var y = (float)(rng.NextDouble() * 256);
            World.Place(w, l, x, y, rng.Next(2) == 0 ? box : raster, (sbyte)(4 + rng.Next(13)));
        }

        World.Process(w);

        var buffer = new short[256 * 256];
        fixed (short* p = buffer)
        {
            World.QueryRegion(w, g, l, 0, 0, 256, 256, p);
            for (var cy = 0; cy < 256; cy++)
            for (var cx = 0; cx < 256; cx++)
                if (p[cy * 256 + cx] != World.Query(w, g, l, cx, cy))
                    return false;

            World.QueryRegion(w, g, l, 30, 62, 91, 133, p);
            for (var row = 0; row < 133; row++)
            for (var col = 0; col < 91; col++)
                if (p[row * 91 + col] != World.Query(w, g, l, 30 + col, 62 + row))
                    return false;

            World.QueryRegion(w, g, l, -7, 250, 20, 12, p);
            for (var row = 0; row < 12; row++)
            for (var col = 0; col < 20; col++)
                if (p[row * 20 + col] != World.Query(w, g, l, -7 + col, 250 + row))
                    return false;
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        fixed (short* p = buffer)
        {
            for (var i = 0; i < 100; i++) World.QueryRegion(w, g, l, 0, 0, 256, 256, p);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"  warm query-region over 100 full grids: {allocated} B");
        return allocated == 0;
    }

    private static bool SaturatedSumClamps()
    {
        var w = World.New();
        var g = Grid.New(w, 6, 0f, 0f, 64f);
        var l = Layer.New(w);
        var stamp = Stamp.Box(4, 4, 127);
        for (var i = 0; i < 120; i++) World.Place(w, l, 32f, 32f, stamp, 16);
        World.Process(w);
        return World.Query(w, g, l, 32, 32) == 32767;
    }

    private static bool CrossGridSumsConserve()
    {
        var w = World.New();
        var gFull = Grid.New(w, 9, 0f, 0f, 256f);
        var gFine = Grid.New(w, 8, 0f, 0f, 256f);
        var gHalf = Grid.New(w, 7, 0f, 0f, 256f);
        var gQuarter = Grid.New(w, 6, 0f, 0f, 256f);
        var l = Layer.New(w);
        var box = Stamp.Box(16, 16, 90);
        var rng = new Random(53);
        for (var i = 0; i < 40; i++)
            World.Place(w, l, (rng.Next(54) + 4) * 4, (rng.Next(54) + 4) * 4, box, (sbyte)(4 + i % 5));
        World.Process(w);

        var full = World.Query(w, gFull, l, 0, 0, 512, 512);
        var fine = World.Query(w, gFine, l, 0, 0, 256, 256);
        var half = World.Query(w, gHalf, l, 0, 0, 128, 128);
        var quarter = World.Query(w, gQuarter, l, 0, 0, 64, 64);
        return full == 4 * fine && fine == 4 * half && half == 4 * quarter && fine > 0;
    }

    private static bool PageSumMatchesScan()
    {
        for (var world = 0; world < 3; world++)
        {
            var w = World.New();
            var g = Grid.New(w, 8, 0f, 0f, 256f);
            var l = Layer.New(w);
            var box = Stamp.Box(14, 14, 80);
            var samples = new sbyte[6 * 9];
            for (var i = 0; i < samples.Length; i++) samples[i] = (sbyte)(i * 17 % 40 - 20);
            var raster = Stamp.New(samples, 6, 9);
            var rng = new Random(41 + world);
            var sources = new World.SourceId[90];
            for (var churn = 0; churn < 5; churn++)
            {
                for (var i = 0; i < sources.Length; i++)
                {
                    var x = (float)(rng.NextDouble() * 256);
                    var y = (float)(rng.NextDouble() * 256);
                    if (churn > 0 && rng.Next(3) == 0)
                    {
                        World.Move(w, sources[rng.Next(churn * sources.Length / 5)], x, y);
                        continue;
                    }

                    sources[i] = World.Place(w, l, x, y, rng.Next(2) == 0 ? box : raster, (sbyte)(1 + rng.Next(16)));
                }

                if (churn > 0)
                    for (var i = 0; i < 20; i++)
                        World.Remove(w, sources[rng.Next(sources.Length)]);

                World.Process(w);
                var sum = World.Query(w, g, l, 0, 0, 256, 256);
                var scan = 0L;
                for (var cy = 0; cy < 256; cy++)
                for (var cx = 0; cx < 256; cx++)
                    scan += World.Query(w, g, l, cx, cy);
                if (sum != scan) return false;

                var partial = World.Query(w, g, l, 1, 1, 254, 254);
                var partialScan = 0L;
                for (var cy = 1; cy < 255; cy++)
                for (var cx = 1; cx < 255; cx++)
                    partialScan += World.Query(w, g, l, cx, cy);
                if (partial != partialScan) return false;

                var strip = World.Query(w, g, l, 30, 0, 190, 256);
                var stripScan = 0L;
                for (var cy = 0; cy < 256; cy++)
                for (var cx = 30; cx < 220; cx++)
                    stripScan += World.Query(w, g, l, cx, cy);
                if (strip != stripScan) return false;
            }
        }

        return true;
    }

    private static bool RegionSumMatchesCellScans()
    {
        var w = World.New();
        var g = Grid.New(w, 9, 0f, 0f, 512f);
        var l = Layer.New(w);
        var box = Stamp.Box(14, 14, 80);
        var samples = new sbyte[6 * 9];
        for (var i = 0; i < samples.Length; i++) samples[i] = (sbyte)(i * 17 % 40 - 20);
        var raster = Stamp.New(samples, 6, 9);
        var rng = new Random(77);
        for (var i = 0; i < 400; i++)
            World.Place(w, l, (float)(rng.NextDouble() * 512), (float)(rng.NextDouble() * 512),
                rng.Next(2) == 0 ? box : raster, (sbyte)(4 + rng.Next(13)));
        World.Process(w);

        (int x, int y, int width, int height)[] regions =
        {
            (0, 0, 512, 512),
            (1, 1, 510, 510),
            (0, 0, 511, 512),
            (17, 0, 478, 512),
            (63, 129, 130, 127),
            (33, 65, 1, 1),
            (5, 500, 200, 40),
            (-10, -10, 540, 540),
            (502, 502, 20, 20)
        };

        foreach (var (x, y, width, height) in regions)
        {
            var scan = 0L;
            for (var row = 0; row < height; row++)
            for (var col = 0; col < width; col++)
            {
                var cy = y + row;
                var cx = x + col;
                if ((uint)cx < 512 && (uint)cy < 512) scan += World.Query(w, g, l, cx, cy);
            }

            if (World.Query(w, g, l, x, y, width, height) != scan) return false;
        }

        return true;
    }

    private static bool QueryAtMatchesDeposits()
    {
        var w = World.New();
        var g = Grid.New(w, 14, 0f, 0f, 10000f);
        var l = Layer.New(w);
        var stamp = Stamp.Box(4, 4, 60);
        var scale = 16384f / 10000f;
        var scaleQ8 = (int)(scale * 256f);
        var rng = new Random(97);
        var xs = new float[200];
        var ys = new float[200];
        var diverged = 0;
        for (var i = 0; i < 200; i++)
        {
            xs[i] = (float)(rng.NextDouble() * 9800 + 100);
            ys[i] = (float)(rng.NextDouble() * 9800 + 100);
            World.Place(w, l, xs[i], ys[i], stamp, 6);
            var cx = (int)MathF.Floor(xs[i] * scaleQ8) >> 8;
            var cy = (int)MathF.Floor(ys[i] * scaleQ8) >> 8;
            var floatCx = (int)MathF.Floor(xs[i] * scale);
            var floatCy = (int)MathF.Floor(ys[i] * scale);
            if (cx != floatCx || cy != floatCy) diverged++;
        }

        World.Process(w);
        if (diverged == 0) return false;
        for (var i = 0; i < 200; i++)
        {
            var cx = (int)MathF.Floor(xs[i] * scaleQ8) >> 8;
            var cy = (int)MathF.Floor(ys[i] * scaleQ8) >> 8;
            if (World.Query(w, g, l, cx, cy) != World.QueryAt(w, g, l, xs[i], ys[i])) return false;
        }

        return true;
    }

    private static bool QueryMaxMatchesFullScan()
    {
        var w = World.New();
        var g = Grid.New(w, 10, 0f, 0f, 1024f);
        var busy = Layer.New(w);
        var quiet = Layer.New(w);
        var stamp = Stamp.Box(20, 20, 25);
        var rng = new Random(211);
        var field = new short[1024 * 1024];

        short ScanMax(byte layer)
        {
            unsafe
            {
                fixed (short* p = field)
                {
                    World.QueryRegion(w, g, layer, 0, 0, 1024, 1024, p);
                }
            }

            var max = short.MinValue;
            for (var i = 0; i < field.Length; i++)
                if (field[i] > max)
                    max = field[i];
            return max;
        }

        bool LayerOk(byte layer)
        {
            var expected = ScanMax(layer);
            var first = World.QueryMax(w, g, layer, out var x, out var y);
            var repeat = World.QueryMax(w, g, layer, out var x2, out var y2);
            return first == expected && first == World.Query(w, g, layer, x, y) &&
                   repeat == first && x == x2 && y == y2;
        }

        if (World.QueryMax(w, g, busy, out var ex, out var ey) != 0 || ex != 0 || ey != 0) return false;

        var ids = new World.SourceId[140];
        for (var round = 0; round < 3; round++)
        {
            for (var i = 0; i < ids.Length; i++)
            {
                var layer = (i & 3) == 0 && round == 0 ? quiet : busy;
                if (round > 0 && rng.Next(5) == 0)
                {
                    World.Remove(w, ids[i]);
                    ids[i] = World.Place(w, layer,
                        (float)(rng.NextDouble() * 1000 + 12), (float)(rng.NextDouble() * 1000 + 12),
                        stamp, (sbyte)rng.Next(-8, 17));
                }
                else if (round > 0)
                {
                    World.Move(w, ids[i],
                        (float)(rng.NextDouble() * 1000 + 12), (float)(rng.NextDouble() * 1000 + 12));
                }
                else
                {
                    ids[i] = World.Place(w, layer,
                        (float)(rng.NextDouble() * 1000 + 12), (float)(rng.NextDouble() * 1000 + 12),
                        stamp, (sbyte)rng.Next(-8, 17));
                }
            }

            World.Process(w);
            if (!LayerOk(busy) || !LayerOk(quiet)) return false;
        }

        var cover = Layer.New(w);
        for (var cy = 0; cy < 64; cy++)
        for (var cx = 0; cx < 64; cx++)
            World.Place(w, cover, 8f + cx * 16, 8f + cy * 16, stamp, -4);
        World.Process(w);
        if (!LayerOk(cover)) return false;
        if (World.QueryMax(w, g, cover, out _, out _) >= 0) return false;

        for (var i = 0; i < 90; i++) World.Place(w, cover, 512f, 512f, stamp, 16);
        World.Process(w);
        if (!LayerOk(cover)) return false;
        return World.QueryMax(w, g, cover, out _, out _) == short.MaxValue;
    }

    private static unsafe bool QueryMaxRegionMatchesScan()
    {
        var w = World.New();
        var g = Grid.New(w, 8, 0f, 0f, 256f);
        var busy = Layer.New(w);
        var stamp = Stamp.Box(20, 20, 25);
        var rng = new Random(307);
        var field = new short[256 * 256];

        int ScanMax(int x0, int y0, int x1, int y1)
        {
            var best = short.MinValue;
            for (var cy = y0; cy < y1; cy++)
            for (var cx = x0; cx < x1; cx++)
                if (field[cy * 256 + cx] > best)
                    best = field[cy * 256 + cx];
            return best;
        }

        bool RegionOk(int x, int y, int rw, int rh)
        {
            var expected = ScanMax(x, y, x + rw, y + rh);
            var first = World.QueryMax(w, g, busy, x, y, rw, rh, out var bx, out var by);
            var repeat = World.QueryMax(w, g, busy, x, y, rw, rh, out var rx, out var ry);
            if (first != expected || repeat != first || bx != rx || by != ry) return false;
            if (bx < x || bx >= x + rw || by < y || by >= y + rh) return false;
            return World.Query(w, g, busy, bx, by) == first;
        }

        if (World.QueryMax(w, g, busy, 0, 0, 256, 256, out var ex, out var ey) != 0 ||
            ex != 0 || ey != 0) return false;
        if (World.QueryMax(w, g, busy, 400, 400, 10, 10, out _, out _) != 0) return false;
        if (World.QueryMax(w, g, busy, 5, 5, 0, 8, out _, out _) != 0) return false;

        var ids = new World.SourceId[120];
        for (var round = 0; round < 3; round++)
        {
            for (var i = 0; i < ids.Length; i++)
                if (round > 0 && rng.Next(5) == 0)
                {
                    World.Remove(w, ids[i]);
                    ids[i] = World.Place(w, busy,
                        (float)(rng.NextDouble() * 240 + 8), (float)(rng.NextDouble() * 240 + 8),
                        stamp, (sbyte)rng.Next(-8, 17));
                }
                else if (round > 0)
                {
                    World.Move(w, ids[i],
                        (float)(rng.NextDouble() * 240 + 8), (float)(rng.NextDouble() * 240 + 8));
                }
                else
                {
                    ids[i] = World.Place(w, busy,
                        (float)(rng.NextDouble() * 240 + 8), (float)(rng.NextDouble() * 240 + 8),
                        stamp, (sbyte)rng.Next(-8, 17));
                }

            World.Process(w);
            fixed (short* p = field)
            {
                World.QueryRegion(w, g, busy, 0, 0, 256, 256, p);
            }

            for (var q = 0; q < 40; q++)
            {
                var rw = rng.Next(1, 80);
                var rh = rng.Next(1, 80);
                if (!RegionOk(rng.Next(257 - rw), rng.Next(257 - rh), rw, rh)) return false;
            }

            if (!RegionOk(0, 0, 256, 256)) return false;
            if (!RegionOk(31, 31, 2, 2)) return false;
            if (!RegionOk(30, 30, 5, 5)) return false;
        }

        var first = World.QueryMax(w, g, busy, 64, 64, 64, 64, out var fx, out var fy);
        var again = World.QueryMax(w, g, busy, 64, 64, 64, 64, out var ax, out var ay);
        return first == again && fx == ax && fy == ay;
    }

    private static bool GradientMatchesCentralDifferences()
    {
        var w = World.New();
        var g = Grid.New(w, 9, 0f, 0f, 512f);
        var g2 = Grid.New(w, 7, 128f, 128f, 64f);
        var l = Layer.New(w);
        var stamp = Stamp.Box(14, 14, 35);
        var rng = new Random(307);
        var ids = new World.SourceId[60];
        for (var round = 0; round < 3; round++)
        {
            for (var i = 0; i < ids.Length; i++)
                if (round > 0 && rng.Next(6) == 0)
                {
                    World.Remove(w, ids[i]);
                    ids[i] = World.Place(w, l,
                        (float)(rng.NextDouble() * 500 + 6), (float)(rng.NextDouble() * 500 + 6),
                        stamp, (sbyte)rng.Next(-8, 17));
                }
                else if (round > 0)
                {
                    World.Move(w, ids[i],
                        (float)(rng.NextDouble() * 500 + 6), (float)(rng.NextDouble() * 500 + 6));
                }
                else
                {
                    ids[i] = World.Place(w, l,
                        (float)(rng.NextDouble() * 500 + 6), (float)(rng.NextDouble() * 500 + 6),
                        stamp, (sbyte)rng.Next(-8, 17));
                }

            World.Process(w);
            for (var i = 0; i < 200; i++)
            {
                var x = (float)(rng.NextDouble() * 520 - 4);
                var y = (float)(rng.NextDouble() * 520 - 4);
                foreach (var grid in new[] { g, g2 })
                {
                    var fine = grid == g;
                    var scale = fine ? 256f : 512f;
                    var origin = fine ? 0f : 128f;
                    World.QueryGradient(w, grid, l, x, y, out var gx, out var gy);
                    var cx = (int)MathF.Floor((x - origin) * scale) >> 8;
                    var cy = (int)MathF.Floor((y - origin) * scale) >> 8;
                    if (gx != World.Query(w, grid, l, cx + 1, cy) - World.Query(w, grid, l, cx - 1, cy)) return false;
                    if (gy != World.Query(w, grid, l, cx, cy + 1) - World.Query(w, grid, l, cx, cy - 1)) return false;
                }
            }
        }

        return true;
    }

    private static unsafe bool ChangedTilesMatchDrain()
    {
        var w = World.New();
        var g = Grid.New(w, 6, 0f, 0f, 64f);
        var red = Layer.New(w);
        var blue = Layer.New(w);
        var stamp = Stamp.Box(16, 16, 50);
        var expected = new HashSet<int>();
        var buffer = stackalloc int[8];

        void Touch(float x, float y, int width)
        {
            var cellX = (int)x;
            var cellY = (int)y;
            var half = width >> 1;
            for (var ty = (cellY - half) >> 5; ty <= (cellY + half - 1) >> 5; ty++)
            for (var tx = (cellX - half) >> 5; tx <= (cellX + half - 1) >> 5; tx++)
                expected.Add(ty * 2 + tx);
        }

        bool DrainMatches(byte layer, HashSet<int> want)
        {
            var count = World.ChangedTiles(w, g, layer, null);
            if (count != want.Count) return false;
            if (World.ChangedTiles(w, g, layer, buffer) != count) return false;
            var seen = new HashSet<int>();
            for (var i = 0; i < count; i++)
                if (!seen.Add(buffer[i]) || !want.Contains(buffer[i]))
                    return false;
            return true;
        }

        if (World.ChangedTiles(w, g, red, null) != 0) return false;
        var a = World.Place(w, red, 32f, 32f, stamp, 4);
        Touch(32, 32, 16);
        World.Process(w);
        if (!DrainMatches(red, expected) || World.ChangedTiles(w, g, blue, null) != 0) return false;

        expected.Clear();
        World.Process(w);
        if (!DrainMatches(red, expected)) return false;

        Touch(32, 32, 16);
        Touch(40, 40, 16);
        World.Move(w, a, 40f, 40f);
        World.Process(w);
        if (!DrainMatches(red, expected) || World.ChangedTiles(w, g, blue, null) != 0) return false;

        expected.Clear();
        Touch(40, 40, 16);
        var b = World.Place(w, blue, 8f, 8f, stamp, 2);
        World.Remove(w, a);
        World.Process(w);
        if (!DrainMatches(red, expected)) return false;
        if (World.ChangedTiles(w, g, blue, buffer) != 1 || buffer[0] != 0) return false;

        expected.Clear();
        World.Remove(w, b);
        World.Process(w);
        if (!DrainMatches(red, expected)) return false;
        if (World.ChangedTiles(w, g, blue, buffer) != 1 || buffer[0] != 0) return false;
        if (World.Query(w, g, red, 0, 0, 64, 64) != 0 || World.Query(w, g, blue, 0, 0, 64, 64) != 0) return false;

        World.Clear(w);
        return World.ChangedTiles(w, g, red, null) == 0 && World.ChangedTiles(w, g, blue, null) == 0;
    }

    private static unsafe bool DeferredWindowMatchesSteppedProcessing()
    {
        var rng = new Random(409);
        var stamp = Stamp.Box(12, 8, 45);
        for (var round = 0; round < 3; round++)
        {
            var batched = World.New();
            var stepped = World.New();
            var gb = Grid.New(batched, 8, 0f, 0f, 256f);
            var gs = Grid.New(stepped, 8, 0f, 0f, 256f);
            var lb = Layer.New(batched);
            var ls = Layer.New(stepped);
            var ids = new World.SourceId[24];
            var live = new bool[24];
            for (var step = 0; step < 90; step++)
            {
                var k = rng.Next(ids.Length);
                var x = (float)(rng.NextDouble() * 240 + 8);
                var y = (float)(rng.NextDouble() * 240 + 8);
                var gain = (sbyte)rng.Next(-16, 17);
                switch (rng.Next(4))
                {
                    case 0:
                        ids[k] = World.Place(batched, lb, x, y, stamp, gain);
                        World.Place(stepped, ls, x, y, stamp, gain);
                        live[k] = true;
                        break;
                    case 1:
                        if (!live[k]) break;
                        World.Move(batched, ids[k], x, y);
                        World.Move(stepped, ids[k], x, y);
                        break;
                    case 2:
                        if (!live[k]) break;
                        World.SetGain(batched, ids[k], gain);
                        World.SetGain(stepped, ids[k], gain);
                        break;
                    default:
                        if (!live[k]) break;
                        World.Remove(batched, ids[k]);
                        World.Remove(stepped, ids[k]);
                        live[k] = false;
                        break;
                }

                World.Process(stepped);
            }

            World.Process(batched);
            var fieldA = new short[256 * 256];
            var fieldB = new short[256 * 256];
            fixed (short* pa = fieldA, pb = fieldB)
            {
                World.QueryRegion(batched, gb, lb, 0, 0, 256, 256, pa);
                World.QueryRegion(stepped, gs, ls, 0, 0, 256, 256, pb);
            }

            for (var i = 0; i < fieldA.Length; i++)
                if (fieldA[i] != fieldB[i])
                    return false;
        }

        var collapse = World.New();
        var gc = Grid.New(collapse, 8, 0f, 0f, 256f);
        var lc = Layer.New(collapse);
        var stampC = Stamp.Box(12, 12, 40);
        var placed = new World.SourceId[200];
        for (var i = 0; i < placed.Length; i++)
            placed[i] = World.Place(collapse, lc, i * 13.7f % 240f + 8f, i * 7.3f % 240f + 8f, stampC, 7);
        for (var i = 0; i < placed.Length; i++) World.Remove(collapse, placed[i]);
        World.Process(collapse);
        return World.Query(collapse, gc, lc, 0, 0, 256, 256) == 0 &&
               World.QueryMax(collapse, gc, lc, out _, out _) == 0 &&
               World.ChangedTiles(collapse, gc, lc, null) == 0;
    }

    private static unsafe bool TentMatchesImpulseOracle()
    {
        var w = World.New();
        var fine = Grid.New(w, 8, 0f, 0f, 256f);
        var coarse = Grid.New(w, 7, 0f, 0f, 256f);
        var l = Layer.New(w);
        var rng = new Random(509);
        var widths = new ushort[] { 1, 2, 3, 5, 8, 13, 24, 40 };
        var tents = new byte[widths.Length];
        for (var i = 0; i < widths.Length; i++) tents[i] = Stamp.Tent(widths[i], widths[i], 40);
        const int count = 90;
        var ids = new World.SourceId[count];
        var sx = new float[count];
        var sy = new float[count];
        var ssize = new int[count];
        var sgain = new int[count];
        var slive = new bool[count];
        var fineField = new int[256 * 256];
        var coarseField = new int[128 * 128];

        int CellValue(int cx, int cy, int scaleQ8)
        {
            var sum = 0L;
            for (var i = 0; i < count; i++)
                if (slive[i])
                    sum += OracleKernel(false, sx[i], sy[i], ssize[i], scaleQ8, cx, cy, 40L * sgain[i]);
            return OracleRoundQ40(sum);
        }

        void Rebuild(int[] target, int size, int scaleQ8)
        {
            for (var cy = 0; cy < size; cy++)
            for (var cx = 0; cx < size; cx++)
                target[cy * size + cx] = CellValue(cx, cy, scaleQ8);
        }

        bool Matches(byte grid, int size, int[] oracle)
        {
            var scan = new short[size * size];
            fixed (short* p = scan)
            {
                World.QueryRegion(w, grid, l, 0, 0, size, size, p);
                for (var i = 0; i < oracle.Length; i++)
                    if (p[i] != (short)Math.Clamp(oracle[i], short.MinValue, short.MaxValue))
                    {
                        Console.WriteLine(
                            $"  first diff grid{size} ({i % size},{i / size}): engine {p[i]} oracle {oracle[i]}");
                        return false;
                    }
            }

            return true;
        }

        for (var round = 0; round < 3; round++)
        {
            for (var i = 0; i < count; i++)
            {
                if (round > 0 && rng.Next(5) == 0)
                {
                    World.Remove(w, ids[i]);
                    slive[i] = false;
                }

                if (!slive[i])
                {
                    var k = rng.Next(widths.Length);
                    ssize[i] = widths[k];
                    sx[i] = (float)(rng.NextDouble() * 248 + 4);
                    sy[i] = (float)(rng.NextDouble() * 248 + 4);
                    sgain[i] = rng.Next(-8, 15);
                    ids[i] = World.Place(w, l, sx[i], sy[i], tents[k], (sbyte)sgain[i]);
                    slive[i] = true;
                }
                else if (round > 0)
                {
                    sx[i] = (float)(rng.NextDouble() * 248 + 4);
                    sy[i] = (float)(rng.NextDouble() * 248 + 4);
                    World.Move(w, ids[i], sx[i], sy[i]);
                }
            }

            World.Process(w);
            Rebuild(fineField, 256, 256);
            Rebuild(coarseField, 128, 128);
            if (!Matches(fine, 256, fineField) || !Matches(coarse, 128, coarseField)) return false;
        }

        return true;
    }

    private static unsafe bool BellMatchesParaboloidOracle()
    {
        var w = World.New();
        var fine = Grid.New(w, 8, 0f, 0f, 256f);
        var coarse = Grid.New(w, 7, 0f, 0f, 256f);
        var l = Layer.New(w);
        var rng = new Random(613);
        var widths = new ushort[] { 2, 3, 5, 8, 13, 24, 40 };
        var bells = new byte[widths.Length];
        for (var i = 0; i < widths.Length; i++) bells[i] = Stamp.Bell(widths[i], widths[i], 40);
        const int count = 90;
        var ids = new World.SourceId[count];
        var sx = new float[count];
        var sy = new float[count];
        var ssize = new int[count];
        var sgain = new int[count];
        var slive = new bool[count];
        var fineField = new int[256 * 256];
        var coarseField = new int[128 * 128];

        int CellValue(int cx, int cy, int scaleQ8)
        {
            var sum = 0L;
            for (var i = 0; i < count; i++)
                if (slive[i])
                    sum += OracleKernel(true, sx[i], sy[i], ssize[i], scaleQ8, cx, cy, 40L * sgain[i]);
            return OracleRoundQ40(sum);
        }

        void Rebuild(int[] target, int size, int scaleQ8)
        {
            for (var cy = 0; cy < size; cy++)
            for (var cx = 0; cx < size; cx++)
                target[cy * size + cx] = CellValue(cx, cy, scaleQ8);
        }

        bool Matches(byte grid, int size, int[] oracle)
        {
            var scan = new short[size * size];
            fixed (short* p = scan)
            {
                World.QueryRegion(w, grid, l, 0, 0, size, size, p);
                for (var i = 0; i < oracle.Length; i++)
                    if (p[i] != (short)Math.Clamp(oracle[i], short.MinValue, short.MaxValue))
                    {
                        Console.WriteLine(
                            $"  first diff grid{size} ({i % size},{i / size}): engine {p[i]} oracle {oracle[i]}");
                        return false;
                    }
            }

            return true;
        }

        for (var round = 0; round < 3; round++)
        {
            for (var i = 0; i < count; i++)
            {
                if (round > 0 && rng.Next(5) == 0)
                {
                    World.Remove(w, ids[i]);
                    slive[i] = false;
                }

                if (!slive[i])
                {
                    var k = rng.Next(widths.Length);
                    ssize[i] = widths[k];
                    sx[i] = (float)(rng.NextDouble() * 248 + 4);
                    sy[i] = (float)(rng.NextDouble() * 248 + 4);
                    sgain[i] = rng.Next(-8, 15);
                    ids[i] = World.Place(w, l, sx[i], sy[i], bells[k], (sbyte)sgain[i]);
                    slive[i] = true;
                }
                else if (round > 0)
                {
                    sx[i] = (float)(rng.NextDouble() * 248 + 4);
                    sy[i] = (float)(rng.NextDouble() * 248 + 4);
                    World.Move(w, ids[i], sx[i], sy[i]);
                }
            }

            World.Process(w);
            Rebuild(fineField, 256, 256);
            Rebuild(coarseField, 128, 128);
            if (!Matches(fine, 256, fineField) || !Matches(coarse, 128, coarseField)) return false;
        }

        return true;
    }

    private static bool SourceSlotsReuseAndStaleInert()
    {
        var w = World.New();
        var g = Grid.New(w, 6, 0f, 0f, 64f);
        var l = Layer.New(w);
        var stamp = Stamp.Box(4, 4, 40);
        var survivor = World.Place(w, l, 12f, 12f, stamp, 5);
        var first = default(World.SourceId);
        for (var i = 0; i < 2000; i++)
        {
            var id = World.Place(w, l, 40f, 40f, stamp, 3);
            if (i == 0) first = id;
            World.Remove(w, id);
        }

        var reused = World.Place(w, l, 40f, 40f, stamp, 3);
        if (reused == first) return false;
        World.Process(w);
        if (World.Query(w, g, l, 0, 0, 64, 64) != 5 * 40 * 16 + 3 * 40 * 16) return false;

        World.Move(w, first, 8f, 8f);
        World.SetGain(w, first, 1);
        World.Remove(w, first);
        World.Process(w);
        if (World.Query(w, g, l, 0, 0, 64, 64) != 5 * 40 * 16 + 3 * 40 * 16) return false;

        World.Move(w, survivor, 20f, 20f);
        World.Process(w);
        if (World.Query(w, g, l, 20, 20) != 5 * 40) return false;
        return World.Query(w, g, l, 12, 12) == 0;
    }

    private static bool FifoFreeRingDelaysSlotReuse()
    {
        const int freeSlots = 64;
        const int liveSlots = 16;
        const int cycles = 127 * freeSlots;
        var w = World.New();
        try
        {
            var g = Grid.New(w, 6, 0f, 0f, 64f);
            var l = Layer.New(w);
            var stamp = Stamp.Box(1, 1, 10);
            var initial = new World.SourceId[freeSlots + liveSlots];
            for (var i = 0; i < initial.Length; i++)
                initial[i] = World.Place(w, l, 8.5f, 8.5f, stamp, 1);
            for (var i = 0; i < freeSlots; i++)
                World.Remove(w, initial[i]);
            var stale = initial[0];
            var active = new Queue<World.SourceId>(initial.Skip(freeSlots));
            for (var step = 0; step < cycles; step++)
            {
                var victim = active.Dequeue();
                World.Remove(w, victim);
                var placed = World.Place(w, l, 16.5f, 16.5f, stamp, 2);
                if (placed.Slot != (uint)(step % (freeSlots + liveSlots))) return false;
                if (placed == stale || World.TryGetSourceSpec(w, stale, out _)) return false;
                active.Enqueue(placed);
            }

            World.Move(w, stale, 32.5f, 32.5f);
            World.SetGain(w, stale, 16);
            World.Process(w);
            return World.Query(w, g, l, 32, 32) == 0 && World.Query(w, g, l, 16, 16) == liveSlots * 20;
        }
        finally
        {
            World.Free(w);
        }
    }

    private static unsafe bool PrecisionCellSkipsSubcellMovesAndMatchesExclusion()
    {
        var w = World.New();
        var snapped = World.New();
        try
        {
            var g = Grid.New(w, 7, 0f, 0f, 128f);
            var gs = Grid.New(snapped, 7, 0f, 0f, 128f);
            var cellLayer = Layer.New(w, Layer.Precision.Cell);
            var exactLayer = Layer.New(snapped);
            var derived = Layer.Sum(w, cellLayer, 2);
            var stamps = new[]
            {
                Stamp.Box(6, 4, 70),
                Stamp.Tent(9, 7, 90),
                Stamp.Bell(11, 11, 100),
                Stamp.Cone(5, 80, 180)
            };
            var rng = new Random(1010);
            var ids = new World.SourceId[40];
            var xs = new float[ids.Length];
            var ys = new float[ids.Length];
            for (var i = 0; i < ids.Length; i++)
            {
                xs[i] = 16f + rng.Next(96) + 0.125f + (float)(rng.NextDouble() * 0.5);
                ys[i] = 16f + rng.Next(96) + 0.125f + (float)(rng.NextDouble() * 0.5);
                var stamp = stamps[i % stamps.Length];
                var gain = (sbyte)(i % 2 == 0 ? 7 : -5);
                ids[i] = World.Place(w, cellLayer, xs[i], ys[i], stamp, gain);
                var sId = World.Place(snapped, exactLayer, MathF.Floor(xs[i]), MathF.Floor(ys[i]), stamp, gain);
                var angle = (float)(i * 0.31);
                World.Turn(w, ids[i], angle);
                World.Turn(snapped, sId, angle);
            }

            World.Process(w);
            World.Process(snapped);
            if (!Read(w, g, cellLayer, 128).AsSpan().SequenceEqual(Read(snapped, gs, exactLayer, 128))) return false;

            var tick = World.Tick(w);
            for (var i = 0; i < ids.Length; i++)
            {
                xs[i] = MathF.Floor(xs[i]) + 0.875f;
                ys[i] = MathF.Floor(ys[i]) + 0.75f;
                World.Move(w, ids[i], xs[i], ys[i]);
            }

            fixed (World.SourceId* idPtr = ids)
            fixed (float* xPtr = xs, yPtr = ys)
            {
                World.Sync(w, idPtr, xPtr, yPtr, ids.Length);
            }

            World.Process(w);
            if (World.ChangedTiles(w, g, cellLayer, null) != 0) return false;
            if (World.ChangedTiles(w, g, cellLayer, tick, null) != 0) return false;
            if (!Read(w, g, cellLayer, 128).AsSpan().SequenceEqual(Read(snapped, gs, exactLayer, 128))) return false;

            for (var i = 0; i < 12; i++)
            {
                var id = ids[i];
                var px = xs[i];
                var py = ys[i];
                if (!World.TrySense(w, cellLayer, px, py, id, out var exVal)) return false;
                if (!World.TrySenseArea(w, cellLayer, px, py, 10f, id, out var exArea)) return false;
                if (!World.TrySenseGradient(w, cellLayer, px, py, id, out var exGx, out var exGy)) return false;
                if (!World.TrySense(w, derived, px, py, id, out var exDer)) return false;

                World.Record(w);
                World.Remove(w, id);
                World.Process(w);
                World.TrySense(w, cellLayer, px, py, out var remVal);
                World.TrySenseArea(w, cellLayer, px, py, 10f, out var remArea);
                World.TrySenseGradient(w, cellLayer, px, py, out var remGx, out var remGy);
                World.TrySense(w, derived, px, py, out var remDer);
                World.Rewind(w);
                World.Process(w);
                if (exVal != remVal || exArea != remArea || exGx != remGx || exGy != remGy || exDer != remDer)
                    return false;
            }

            return true;
        }
        finally
        {
            World.Free(w);
            World.Free(snapped);
        }
    }

    private static bool RewindRestoresRecordedState()
    {
        var w = World.New();
        var g = Grid.New(w, 6, 0f, 0f, 64f);
        var l = Layer.New(w);
        var box = Stamp.Box(6, 6, 50);
        var tent = Stamp.Tent(9, 9, 40);
        var rng = new Random(99);
        var kept = new World.SourceId[24];
        for (var i = 0; i < kept.Length; i++)
            kept[i] = World.Place(w, l, rng.Next(4, 56), rng.Next(4, 56),
                (i & 1) == 0 ? box : tent, (sbyte)(1 + rng.Next(14)));
        World.Process(w);
        var baseline = World.Query(w, g, l, 0, 0, 64, 64);

        World.Record(w);
        var doomed = kept[3];
        World.Remove(w, doomed);
        var occupant = World.Place(w, l, 30f, 30f, box, 9);
        World.Process(w);
        for (var i = 0; i < kept.Length; i += 2)
            if (kept[i] != doomed)
                World.Move(w, kept[i], rng.Next(4, 56), rng.Next(4, 56));
        for (var i = 1; i < kept.Length; i += 3)
            World.SetGain(w, kept[i], -4);
        var extra = World.Place(w, l, 8f, 52f, tent, 7);
        World.Process(w);
        World.Move(w, kept[0], 2f, 2f);
        if (World.Query(w, g, l, 0, 0, 64, 64) == baseline) return false;

        World.Rewind(w);
        World.Process(w);
        if (World.Query(w, g, l, 0, 0, 64, 64) != baseline) return false;

        World.Move(w, occupant, 4f, 4f);
        World.Move(w, extra, 4f, 4f);
        World.Remove(w, occupant);
        World.Process(w);
        if (World.Query(w, g, l, 0, 0, 64, 64) != baseline) return false;

        World.Move(w, doomed, 10f, 10f);
        World.Process(w);
        if (World.Query(w, g, l, 12, 12) == 0) return false;

        World.Rewind(w);
        World.Process(w);
        return World.Query(w, g, l, 12, 12) != 0;
    }

    private static bool SignedGainExact()
    {
        var w = World.New();
        var g = Grid.New(w, 6, 0f, 0f, 64f);
        var l = Layer.New(w);
        var stamp = Stamp.Box(8, 8, 50);
        var plus = World.Place(w, l, 32f, 32f, stamp, 8);
        var minus = World.Place(w, l, 32f, 32f, stamp, -3);
        World.Process(w);
        if (World.Query(w, g, l, 32, 32) != 8 * 50 - 3 * 50) return false;
        if (World.Query(w, g, l, 0, 0, 64, 64) != (8 * 50 - 3 * 50) * 64) return false;

        World.SetGain(w, plus, -8);
        World.SetGain(w, minus, 3);
        World.Process(w);
        if (World.Query(w, g, l, 32, 32) != -8 * 50 + 3 * 50) return false;

        World.SetGain(w, plus, 20);
        World.SetGain(w, minus, -20);
        World.Process(w);
        if (World.Query(w, g, l, 32, 32) != 16 * 50 - 16 * 50) return false;

        World.Remove(w, plus);
        World.Remove(w, minus);
        World.Process(w);
        return World.Query(w, g, l, 0, 0, 64, 64) == 0;
    }

    private static bool MultiLayerChurnMatchesScans()
    {
        var w = World.New();
        var g = Grid.New(w, 8, 0f, 0f, 256f);
        var stamp = Stamp.Box(10, 10, 70);
        var rng = new Random(101);
        const int layers = 16;
        var layerIds = new byte[layers];
        for (var l = 0; l < layers; l++) layerIds[l] = Layer.New(w);
        var sources = new World.SourceId[layers][];

        for (var churn = 0; churn < 3; churn++)
        {
            for (var l = 0; l < layers; l++)
            {
                sources[l] = new World.SourceId[30];
                for (var i = 0; i < 30; i++)
                    sources[l][i] = World.Place(w, layerIds[l],
                        (float)(rng.NextDouble() * 256), (float)(rng.NextDouble() * 256), stamp,
                        (sbyte)(1 + rng.Next(16)));
            }

            if (churn > 0)
                for (var l = 0; l < layers; l++)
                for (var i = 0; i < 10; i++)
                    World.Remove(w, sources[l][rng.Next(30)]);

            World.Process(w);
            for (var l = 0; l < layers; l++)
            {
                var sum = World.Query(w, g, layerIds[l], 0, 0, 256, 256);
                var scan = 0L;
                for (var cy = 0; cy < 256; cy++)
                for (var cx = 0; cx < 256; cx++)
                    scan += World.Query(w, g, layerIds[l], cx, cy);
                if (sum != scan) return false;

                var partial = World.Query(w, g, layerIds[l], 1, 1, 254, 254);
                var partialScan = 0L;
                for (var cy = 1; cy < 255; cy++)
                for (var cx = 1; cx < 255; cx++)
                    partialScan += World.Query(w, g, layerIds[l], cx, cy);
                if (partial != partialScan) return false;

                for (var row = 0; row < 4; row++)
                {
                    var y = rng.Next(240);
                    var x = rng.Next(240);
                    var region = World.Query(w, g, layerIds[l], x, y, 16, 16);
                    var rectScan = 0L;
                    for (var cy = y; cy < y + 16; cy++)
                    for (var cx = x; cx < x + 16; cx++)
                        rectScan += World.Query(w, g, layerIds[l], cx, cy);
                    if (region != rectScan) return false;
                }
            }
        }

        return true;
    }

    public static void Compare()
    {
        const int cells = 1024;
        const int sources = 4000;
        var w = World.New();
        var g = Grid.New(w, 10, 0f, 0f, 1024f);
        var l = Layer.New(w);
        var stamp = Stamp.Box(16, 16, 60);
        var naive = new NaiveGrid(cells);
        var rng = new Random(31);
        var ids = new World.SourceId[sources];
        for (var i = 0; i < ids.Length; i++)
        {
            var x = 8 + rng.Next(cells - 16);
            var y = 8 + rng.Next(cells - 16);
            ids[i] = World.Place(w, l, x, y, stamp, 8);
            naive.Add(x, y, 16, 16, 60, 8);
        }

        World.Process(w);
        naive.Rebuild();

        var matches = naive.Sum() == World.Query(w, g, l, 0, 0, cells, cells);
        for (var i = 0; i < 2000 && matches; i++)
        {
            var cx = rng.Next(cells);
            var cy = rng.Next(cells);
            matches = naive.Query(cx, cy) == World.Query(w, g, l, cx, cy);
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
            World.Process(w);
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
            for (var i = 0; i < movers.Length; i++) World.Move(w, ids[movers[i]], x[i], y[i]);
            World.Process(w);
            el = Stopwatch.GetElapsedTime(t).TotalMicroseconds;
            if (r >= 0 && el < giBest) giBest = el;
        }

        matches = naive.Sum() == World.Query(w, g, l, 0, 0, cells, cells);
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
            acc += World.Query(w, g, l, 0, 0, cells, cells);
            var el = Stopwatch.GetElapsedTime(t).TotalMicroseconds;
            if (el < best) best = el;
        }

        Console.WriteLine($"Gi full-grid sum: {best:F1} us (checksum {acc})");
    }

    public static void PlaceProfile()
    {
        var w = World.New();
        var g = Grid.New(w, 10, 0f, 0f, 1024f);
        var l = Layer.New(w);
        var stamp = Stamp.Box(16, 16, 60);
        var rng = new Random(17);
        for (var i = 0; i < 4000; i++)
            World.Place(w, l, (float)(rng.NextDouble() * 1024), (float)(rng.NextDouble() * 1024), stamp, 8);
        World.Process(w);

        var placed = new World.SourceId[200];
        var rounds = int.Parse(Environment.GetEnvironmentVariable("PLACE_ROUNDS") ?? "4000");
        var acc = 0L;
        long placeTicks = 0, processTicks = 0, removeTicks = 0;
        for (var r = 0; r < rounds; r++)
        {
            var t = Stopwatch.GetTimestamp();
            for (var i = 0; i < placed.Length; i++)
                placed[i] = World.Place(w, l,
                    (i * 41.3f + (r + 1) * 17.9f) % 1000f + 12f,
                    (i * 29.7f + (r + 1) * 23.1f) % 1000f + 12f, stamp, 8);
            var t2 = Stopwatch.GetTimestamp();
            placeTicks += t2 - t;
            World.Process(w);
            var t3 = Stopwatch.GetTimestamp();
            processTicks += t3 - t2;
            for (var i = 0; i < placed.Length; i++) World.Remove(w, placed[i]);
            var t4 = Stopwatch.GetTimestamp();
            removeTicks += t4 - t3;
            World.Process(w);
            processTicks += Stopwatch.GetTimestamp() - t4;
            acc += World.Query(w, g, l, 0, 0, 64, 64);
        }

        var freq = Stopwatch.Frequency;
        Console.WriteLine($"place-profile {rounds} rounds ({acc}) " +
                          $"place={placeTicks * 1e6 / freq / rounds:F1}us process={processTicks * 1e6 / freq / rounds:F1}us remove={removeTicks * 1e6 / freq / rounds:F1}us");
    }

    public static void Timing()
    {
        var w = World.New();
        var g = Grid.New(w, 10, 0f, 0f, 1024f);
        var l = Layer.New(w);
        var stamp = Stamp.Box(16, 16, 60);
        var rng = new Random(17);
        var ids = new World.SourceId[4000];
        for (var i = 0; i < 4000; i++)
            ids[i] = World.Place(w, l, (float)(rng.NextDouble() * 1024), (float)(rng.NextDouble() * 1024), stamp, 8);
        World.Process(w);

        var best = double.MaxValue;
        for (var r = 0; r < 20; r++)
        {
            var t = Stopwatch.GetTimestamp();
            World.Process(w);
            var el = Stopwatch.GetElapsedTime(t).TotalMicroseconds;
            if (el < best) best = el;
        }

        Console.WriteLine($"unchanged process (4000 sources, 1024-grid): {best:F0} us");

        best = double.MaxValue;
        for (var r = 0; r < 20; r++)
        {
            var id = World.Place(w, l, r, r, stamp, 8);
            var t = Stopwatch.GetTimestamp();
            World.Process(w);
            var el = Stopwatch.GetElapsedTime(t).TotalMicroseconds;
            World.Remove(w, id);
            if (el < best) best = el;
        }

        Console.WriteLine($"incremental process (1 added source): {best:F0} us");

        best = double.MaxValue;
        long acc = 0;
        for (var r = 0; r < 20; r++)
        {
            var t = Stopwatch.GetTimestamp();
            for (var i = 0; i < 10_000; i++) acc += World.Query(w, g, l, i % 1021, i * 3 % 1021);
            var el = Stopwatch.GetElapsedTime(t).TotalMicroseconds;
            if (el < best) best = el;
        }

        Console.WriteLine($"query: {best / 10:F2} us per 1k cells ({acc})");

        var movers = new World.SourceId[200];
        for (var i = 0; i < movers.Length; i++) movers[i] = ids[i * 19 % 4000];
        best = double.MaxValue;
        for (var r = -1; r < 20; r++)
        {
            var t = Stopwatch.GetTimestamp();
            for (var i = 0; i < movers.Length; i++)
                World.Move(w, movers[i],
                    (movers[i].Slot * 5.13f + (r + 1) * 37.7f) % 1024f,
                    (movers[i].Slot * 7.29f + (r + 1) * 11.3f) % 1024f);
            World.Process(w);
            var el = Stopwatch.GetElapsedTime(t).TotalMicroseconds;
            if (r >= 0 && el < best) best = el;
        }

        Console.WriteLine($"move-200 churn process: {best:F0} us");

        var placed = new World.SourceId[200];
        best = double.MaxValue;
        for (var r = -1; r < 20; r++)
        {
            var t = Stopwatch.GetTimestamp();
            for (var i = 0; i < placed.Length; i++)
                placed[i] = World.Place(w, l,
                    (i * 41.3f + (r + 1) * 17.9f) % 1000f + 12f,
                    (i * 29.7f + (r + 1) * 23.1f) % 1000f + 12f, stamp, 8);
            World.Process(w);
            var el = Stopwatch.GetElapsedTime(t).TotalMicroseconds;
            if (r >= 0 && el < best) best = el;
            for (var i = 0; i < placed.Length; i++) World.Remove(w, placed[i]);
            World.Process(w);
        }

        Console.WriteLine($"place-200 churn process: {best:F0} us");

        best = double.MaxValue;
        long rewindAcc = 0;
        for (var r = -1; r < 20; r++)
        {
            World.Record(w);
            for (var i = 0; i < placed.Length; i++)
                placed[i] = World.Place(w, l,
                    (i * 41.3f + (r + 1) * 17.9f) % 1000f + 12f,
                    (i * 29.7f + (r + 1) * 23.1f) % 1000f + 12f, stamp, 8);
            World.Process(w);
            var t = Stopwatch.GetTimestamp();
            World.Rewind(w);
            World.Process(w);
            var el = Stopwatch.GetElapsedTime(t).TotalMicroseconds;
            rewindAcc += World.Query(w, g, l, 0, 0, 64, 64);
            if (r >= 0 && el < best) best = el;
        }

        Console.WriteLine($"rewind+process of 200-place window: {best:F0} us ({rewindAcc})");

        var collapseIds = new World.SourceId[200];
        best = double.MaxValue;
        for (var r = -1; r < 20; r++)
        {
            var t = Stopwatch.GetTimestamp();
            for (var i = 0; i < collapseIds.Length; i++)
                collapseIds[i] = World.Place(w, l,
                    (i * 41.3f + (r + 1) * 17.9f) % 1000f + 12f,
                    (i * 29.7f + (r + 1) * 23.1f) % 1000f + 12f, stamp, 8);
            for (var i = 0; i < collapseIds.Length; i++) World.Remove(w, collapseIds[i]);
            World.Process(w);
            var el = Stopwatch.GetElapsedTime(t).TotalMicroseconds;
            if (r >= 0 && el < best) best = el;
        }

        Console.WriteLine($"place+remove-200 collapse process: {best:F1} us");

        var tentStamp = Stamp.Tent(16, 16, 60);
        var tentIds = new World.SourceId[200];
        best = double.MaxValue;
        for (var r = -1; r < 20; r++)
        {
            var t = Stopwatch.GetTimestamp();
            for (var i = 0; i < tentIds.Length; i++)
                tentIds[i] = World.Place(w, l,
                    (i * 43.1f + (r + 1) * 19.3f) % 1000f + 12f,
                    (i * 27.9f + (r + 1) * 21.7f) % 1000f + 12f, tentStamp, 8);
            World.Process(w);
            var el = Stopwatch.GetElapsedTime(t).TotalMicroseconds;
            if (r >= 0 && el < best) best = el;
            for (var i = 0; i < tentIds.Length; i++) World.Remove(w, tentIds[i]);
            World.Process(w);
        }

        Console.WriteLine($"tent-200 churn process (16x16): {best:F0} us");

        var bellStamp = Stamp.Bell(16, 16, 60);
        var bellIds = new World.SourceId[200];
        best = double.MaxValue;
        for (var r = -1; r < 20; r++)
        {
            var t = Stopwatch.GetTimestamp();
            for (var i = 0; i < bellIds.Length; i++)
                bellIds[i] = World.Place(w, l,
                    (i * 43.1f + (r + 1) * 19.3f) % 1000f + 12f,
                    (i * 27.9f + (r + 1) * 21.7f) % 1000f + 12f, bellStamp, 8);
            World.Process(w);
            var el = Stopwatch.GetElapsedTime(t).TotalMicroseconds;
            if (r >= 0 && el < best) best = el;
            for (var i = 0; i < bellIds.Length; i++) World.Remove(w, bellIds[i]);
            World.Process(w);
        }

        Console.WriteLine($"bell-200 churn process (16x16): {best:F0} us");

        best = double.MaxValue;
        long total = 0;
        for (var r = 0; r < 20; r++)
        {
            var t = Stopwatch.GetTimestamp();
            total += World.Query(w, g, l, 0, 0, 1024, 1024);
            var el = Stopwatch.GetElapsedTime(t).TotalMicroseconds;
            if (el < best) best = el;
        }

        Console.WriteLine($"full-grid sum (1024-grid): {best:F1} us ({total})");

        best = double.MaxValue;
        total = 0;
        for (var r = 0; r < 20; r++)
        {
            var t = Stopwatch.GetTimestamp();
            total += World.Query(w, g, l, 1, 1, 1022, 1022);
            var el = Stopwatch.GetElapsedTime(t).TotalMicroseconds;
            if (el < best) best = el;
        }

        Console.WriteLine($"partial-region sum (1022x1022 of 1024): {best:F1} us ({total})");

        best = double.MaxValue;
        short bestCell = 0;
        for (var r = 0; r < 20; r++)
        {
            var t = Stopwatch.GetTimestamp();
            bestCell = World.QueryMax(w, g, l, out _, out _);
            var el = Stopwatch.GetElapsedTime(t).TotalMicroseconds;
            if (el < best) best = el;
        }

        Console.WriteLine($"best-cell query (argmax over 1024x1024): {best:F2} us ({bestCell})");

        best = double.MaxValue;
        short bestRegion = 0;
        for (var r = 0; r < 20; r++)
        {
            var t = Stopwatch.GetTimestamp();
            for (var i = 0; i < 1_000; i++)
                bestRegion = World.QueryMax(w, g, l,
                    i * 61 % 900, i * 37 % 900, 128, 128, out _, out _);
            var el = Stopwatch.GetElapsedTime(t).TotalMicroseconds;
            if (el < best) best = el;
        }

        Console.WriteLine($"best-cell-in-128x128-region query: {best / 10:F2} us per 1k ({bestRegion})");

        best = double.MaxValue;
        long gradientAcc = 0;
        for (var r = 0; r < 20; r++)
        {
            var t = Stopwatch.GetTimestamp();
            for (var i = 0; i < 1_000; i++)
            {
                World.QueryGradient(w, g, l, i * 13.37f % 1024f, i * 7.77f % 1024f, out var gx, out var gy);
                gradientAcc += gx + gy;
            }

            var el = Stopwatch.GetElapsedTime(t).TotalMicroseconds;
            if (el < best) best = el;
        }

        Console.WriteLine($"gradient query: {best / 10:F2} us per 1k points ({gradientAcc})");

        best = double.MaxValue;
        short naiveCell = 0;
        for (var r = 0; r < 3; r++)
        {
            var t = Stopwatch.GetTimestamp();
            naiveCell = short.MinValue;
            for (var y = 0; y < 1024; y++)
            for (var x = 0; x < 1024; x++)
            {
                var v = World.Query(w, g, l, x, y);
                if (v > naiveCell) naiveCell = v;
            }

            var el = Stopwatch.GetElapsedTime(t).TotalMicroseconds;
            if (el < best) best = el;
        }

        Console.WriteLine($"naive best-cell (1M Query calls): {best:F0} us ({naiveCell})");

        var mw = World.New();
        Grid.New(mw, 8, 0f, 0f, 256f);
        var mStamp = Stamp.Box(10, 10, 70);
        var mLayers = new byte[16];
        var mIds = new World.SourceId[16][];
        for (var li = 0; li < 16; li++)
        {
            mLayers[li] = Layer.New(mw);
            mIds[li] = new World.SourceId[25];
            for (var i = 0; i < 25; i++)
                mIds[li][i] = World.Place(mw, mLayers[li], (float)(rng.NextDouble() * 256),
                    (float)(rng.NextDouble() * 256), mStamp, 8);
        }

        World.Process(mw);
        best = double.MaxValue;
        for (var r = -1; r < 20; r++)
        {
            var t = Stopwatch.GetTimestamp();
            for (var li = 0; li < 16; li++)
            for (var i = 0; i < 25; i++)
                World.Move(mw, mIds[li][i],
                    (mIds[li][i].Slot * 3.13f + (r + 1) * 17.7f) % 256f,
                    (mIds[li][i].Slot * 5.29f + (r + 1) * 13.3f) % 256f);
            World.Process(mw);
            var el = Stopwatch.GetElapsedTime(t).TotalMicroseconds;
            if (r >= 0 && el < best) best = el;
        }

        Console.WriteLine($"move-400 across 16 layers process (~25 dirty each): {best:F0} us");

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
                    World.QueryRegion(w, g, l, 100, 76, 256, 256, p);
                    var el = Stopwatch.GetElapsedTime(t).TotalMicroseconds;
                    if (el < best) best = el;
                    checksum += p[0] + p[65535];
                }
            }
        }

        Console.WriteLine($"query-region 256x256: {best:F1} us ({checksum})");
        SenseTiming(w, l, default);
        FeatureTiming();
    }
}
