internal static partial class Verification
{
    private static int SpecFadeGain(int from, int to, int elapsed, int ticks)
    {
        if (elapsed >= ticks) return to;
        var delta = to - from;
        var step = (int)(((long)Math.Abs(delta) * elapsed + ticks / 2) / ticks);
        return from + (delta < 0 ? -step : step);
    }

    private static unsafe bool FadeStepsMatchSchedule()
    {
        var w = Gi.World.New();
        var g = Gi.Grid.New(w, 6, 0f, 0f, 64f);
        var l = Gi.Layer.New(w);
        var unit = Gi.Stamp.Box(1, 1, 1);
        var cases = new List<(int Id, int Cx, int Cy, int From, int To, int Ticks)>();
        foreach (var from in new[] { -16, -3, 0, 5, 16 })
        foreach (var to in new[] { -16, 0, 7, 16 })
        foreach (var ticks in new[] { 1, 2, 3, 7, 16, 33, 100 })
        {
            var n = cases.Count;
            var cx = n % 64;
            var cy = n / 64 * 2;
            cases.Add((Gi.World.Place(w, l, cx + 0.5f, cy + 0.5f, unit, from), cx, cy, from, to, ticks));
        }

        Gi.World.Process(w);
        var start = Gi.World.Tick(w);
        foreach (var c in cases) Gi.World.Fade(w, c.Id, c.To, c.Ticks);
        for (var k = 1; k <= 104; k++)
        {
            Gi.World.Process(w);
            if (Gi.World.Tick(w) != start + k) return false;
            foreach (var c in cases)
            {
                var expected = SpecFadeGain(c.From, c.To, k, c.Ticks);
                if (Gi.World.Query(w, g, l, c.Cx, c.Cy) != expected)
                {
                    Console.WriteLine($"  fade {c.From}->{c.To} over {c.Ticks}: tick {k} read {Gi.World.Query(w, g, l, c.Cx, c.Cy)}, expected {expected}");
                    return false;
                }
            }
        }

        Gi.World.Process(w);
        return Gi.World.ChangedTiles(w, g, l, null) == 0;
    }

    private static bool ExpireRemovesOnSchedule()
    {
        var w = Gi.World.New();
        var g = Gi.Grid.New(w, 6, 0f, 0f, 64f);
        var l = Gi.Layer.New(w);
        var unit = Gi.Stamp.Box(1, 1, 1);
        int At(int cx) => Gi.World.Query(w, g, l, cx, 10);
        var a = Gi.World.Place(w, l, 1.5f, 10.5f, unit, 5);
        var b = Gi.World.Place(w, l, 3.5f, 10.5f, unit, 5);
        var c = Gi.World.Place(w, l, 5.5f, 10.5f, unit, 5);
        var d = Gi.World.Place(w, l, 7.5f, 10.5f, unit, 4);
        var e = Gi.World.Place(w, l, 9.5f, 10.5f, unit, 5);
        var h = Gi.World.Place(w, l, 13.5f, 10.5f, unit, 8);
        Gi.World.Expire(w, a, 3);
        Gi.World.Expire(w, b, 2);
        Gi.World.Expire(w, b, 5);
        Gi.World.Expire(w, c, 2);
        Gi.World.Expire(w, c, 0);
        Gi.World.Fade(w, d, 0, 4);
        Gi.World.Expire(w, d, 4);
        Gi.World.Expire(w, e, 3);
        Gi.World.Fade(w, h, 0, 10);
        Gi.World.Expire(w, h, 6);
        int[] expectA = [5, 5, 0, 0, 0, 0, 0, 0];
        int[] expectB = [5, 5, 5, 5, 0, 0, 0, 0];
        int[] expectD = [3, 2, 1, 0, 0, 0, 0, 0];
        for (var tick = 1; tick <= 8; tick++)
        {
            Gi.World.Process(w);
            if (At(1) != expectA[tick - 1] || At(3) != expectB[tick - 1] || At(5) != 5 || At(7) != expectD[tick - 1]) return false;
            if (tick == 1)
            {
                Gi.World.Remove(w, e);
                var f = Gi.World.Place(w, l, 11.5f, 10.5f, unit, 6);
                if ((f & 0xFFFFFF) != (e & 0xFFFFFF)) return false;
            }

            if (tick == 2) Gi.World.SetGain(w, h, 9);
            if (tick >= 2 && At(11) != 6) return false;
            if (tick is 3 or 4 or 5 && At(13) != 9) return false;
            if (tick >= 6 && At(13) != 0) return false;
        }

        Gi.World.Move(w, a, 30.5f, 30.5f);
        Gi.World.SetGain(w, d, 12);
        Gi.World.Process(w);
        return Gi.World.Query(w, g, l, 30, 30) == 0 && At(7) == 0 && At(9) == 0;
    }

    private static bool RewindResumesSchedules()
    {
        byte Build(out List<int> placed)
        {
            var w = Gi.World.New();
            Gi.Grid.New(w, 7, 0f, 0f, 128f);
            Gi.Grid.New(w, 6, 0f, 0f, 128f);
            var l = Gi.Layer.New(w);
            Gi.Layer.Max(w, l, l);
            var rng = new Random(2121);
            byte[] stamps = [Gi.Stamp.Tent(9, 9, 40), Gi.Stamp.Box(5, 5, 30), Gi.Stamp.Dome(6, 50)];
            placed = [];
            for (var i = 0; i < 60; i++)
            {
                var id = Gi.World.Place(w, l, (float)(rng.NextDouble() * 128), (float)(rng.NextDouble() * 128), stamps[i % 3], rng.Next(-16, 17));
                if (i % 2 == 0) Gi.World.Fade(w, id, rng.Next(-16, 17), rng.Next(1, 40));
                if (i % 3 == 0) Gi.World.Expire(w, id, rng.Next(1, 30));
                placed.Add(id);
            }

            for (var t = 0; t < 3; t++) Gi.World.Process(w);
            return w;
        }

        var a = Build(out var ids);
        var b = Build(out _);
        Gi.World.Record(a);
        var window = new Random(77);
        for (var t = 0; t < 5; t++)
        {
            var id = ids[window.Next(ids.Count)];
            Gi.World.Fade(a, id, window.Next(-16, 17), window.Next(1, 10));
            Gi.World.Expire(a, ids[window.Next(ids.Count)], window.Next(1, 4));
            Gi.World.Move(a, ids[window.Next(ids.Count)], (float)(window.NextDouble() * 128), (float)(window.NextDouble() * 128));
            Gi.World.SetGain(a, ids[window.Next(ids.Count)], window.Next(-16, 17));
            Gi.World.Process(a);
        }

        Gi.World.Rewind(a);
        for (var t = 0; t < 45; t++)
        {
            Gi.World.Process(a);
            Gi.World.Process(b);
            for (byte gi = 0; gi < 2; gi++)
            for (byte layer = 0; layer < 2; layer++)
            {
                var side = gi == 0 ? 128 : 64;
                if (!Read(a, gi, layer, side).AsSpan().SequenceEqual(Read(b, gi, layer, side)))
                {
                    Console.WriteLine($"  rewound world diverged from its twin {t} ticks after rewind (grid {gi}, layer {layer})");
                    return false;
                }
            }
        }

        return Gi.World.Tick(a) == Gi.World.Tick(b) + 5;
    }

    private static bool WarmFadeProcessAllocationFree()
    {
        var w = Gi.World.New();
        Gi.Grid.New(w, 9, 0f, 0f, 512f);
        var l = Gi.Layer.New(w);
        var tent = Gi.Stamp.Tent(12, 12, 50);
        var ids = new int[400];
        var until = new int[400];
        for (var i = 0; i < ids.Length; i++) ids[i] = Gi.World.Place(w, l, i * 1.3f % 500f, i * 2.9f % 500f, tent, 0);
        void Frame(int f)
        {
            for (var i = 0; i < ids.Length; i++)
            {
                if (f < until[i]) continue;
                if (i % 5 == 0)
                {
                    Gi.World.Remove(w, ids[i]);
                    ids[i] = Gi.World.Place(w, l, (i * 1.3f + f) % 500f, i * 2.9f % 500f, tent, 16);
                    Gi.World.Fade(w, ids[i], 0, 30);
                    Gi.World.Expire(w, ids[i], 30);
                    until[i] = f + 31;
                    continue;
                }

                Gi.World.Fade(w, ids[i], f % 2 == 0 ? 16 : -16, 12 + i % 9);
                until[i] = f + 12 + i % 9;
            }

            Gi.World.Process(w);
        }

        for (var f = 0; f < 200; f++) Frame(f);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var f = 200; f < 400; f++) Frame(f);
        var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"  warm fade/expire/replace process over 200 frames: {bytes} B");
        return bytes == 0;
    }

    private static unsafe bool ChangedSinceMatchesEpochs()
    {
        var w = Gi.World.New();
        GridSpec[] grids = [new(0, 7, 0f, 0f, 128f), new(1, 6, 0f, 0f, 128f), new(2, 7, 40f, 30f, 64f)];
        foreach (var g in grids) Gi.Grid.New(w, g.Power, g.X, g.Y, g.Size);
        var l = Gi.Layer.New(w);
        var mix = Gi.Layer.Sum(w, l, -1);
        byte[] layers = [l, mix];
        var last = new int[grids.Length, layers.Length, 64];
        var rng = new Random(3131);
        byte[] stamps = [Gi.Stamp.Box(6, 6, 40), Gi.Stamp.Cone(5, 60, 120), Gi.Stamp.Tent(8, 8, 30)];
        var ids = new List<int>();
        var tiles = stackalloc int[64];
        for (var tick = 1; tick <= 60; tick++)
        {
            for (var k = 0; k < 4; k++)
            {
                var op = ids.Count < 5 ? 0 : rng.Next(6);
                var px = (float)(rng.NextDouble() * 136 - 4);
                var py = (float)(rng.NextDouble() * 136 - 4);
                if (op == 0) ids.Add(Gi.World.Place(w, l, px, py, stamps[rng.Next(3)], rng.Next(1, 17)));
                else if (op == 1) Gi.World.Move(w, ids[rng.Next(ids.Count)], px, py);
                else if (op == 2) Gi.World.Fade(w, ids[rng.Next(ids.Count)], rng.Next(-16, 17), rng.Next(1, 12));
                else if (op == 3) Gi.World.Expire(w, ids[rng.Next(ids.Count)], rng.Next(1, 8));
                else if (op == 4) Gi.World.Turn(w, ids[rng.Next(ids.Count)], (float)(rng.NextDouble() * 7));
            }

            Gi.World.Process(w);
            if (Gi.World.Tick(w) != tick) return false;
            for (var gi = 0; gi < grids.Length; gi++)
            for (var li = 0; li < layers.Length; li++)
            {
                var count = Gi.World.ChangedTiles(w, grids[gi].Id, layers[li], tiles);
                for (var i = 0; i < count; i++) last[gi, li, tiles[i]] = tick;
            }

            for (var probe = 0; probe < 40; probe++)
            {
                var x = (float)(rng.NextDouble() * 140 - 6);
                var y = (float)(rng.NextDouble() * 140 - 6);
                var reach = probe % 5 == 0 ? 0f : (float)(rng.NextDouble() * 40);
                var since = tick - rng.Next(0, 12);
                var li = probe % 2;
                var pick = OraclePick(grids, x, y, reach, 0, true, out _);
                var expected = false;
                if (pick >= 0)
                    foreach (var (cx, cy) in OracleDiskCells(grids[pick], x, y, reach))
                        expected |= last[pick, li, (cy >> 5) * (grids[pick].Cells >> 5) + (cx >> 5)] - since > 0;
                if (Gi.World.Changed(w, layers[li], x, y, reach, since) != expected)
                {
                    Console.WriteLine($"  changed probe ({x},{y},{reach}) since {since} on layer {layers[li]}: expected {expected}");
                    return false;
                }
            }

            for (var gi = 0; gi < grids.Length; gi++)
            for (var li = 0; li < layers.Length; li++)
            {
                var since = tick - rng.Next(0, 10);
                var count = Gi.World.ChangedTiles(w, grids[gi].Id, layers[li], since, tiles);
                var expected = 0;
                for (var t = 0; t < (grids[gi].Cells >> 5) * (grids[gi].Cells >> 5); t++)
                    if (last[gi, li, t] - since > 0 && (count <= expected || tiles[expected++] != t)) return false;
                if (count != expected) return false;
            }
        }

        var now = Gi.World.Tick(w);
        Gi.World.Clear(w);
        return Gi.World.Changed(w, l, 64f, 64f, 90f, now) && !Gi.World.Changed(w, l, 64f, 64f, 90f, now + 1);
    }

    private static bool SenseNearestMatchesScan()
    {
        var s = Layered();
        short[] thresholds = [short.MinValue, -200, -1, 0, 1, 40, 500, 3000, short.MaxValue];
        var t = 0;
        foreach (var (x, y, reach) in SenseProbes(31, 1200))
        {
            var threshold = thresholds[t++ % thresholds.Length];
            var ok = Gi.World.TrySenseNearest(s.World, s.Layer, x, y, reach, threshold, out var value, out var nx, out var ny);
            var expected = OraclePick(s.Grids, x, y, reach, 0, true, out var complete);
            if (expected < 0)
            {
                if (ok || value != short.MinValue || nx != x || ny != y) return false;
                continue;
            }

            var g = s.Grids[expected];
            var qx = g.Q8(x, g.X);
            var qy = g.Q8(y, g.Y);
            var best = long.MaxValue;
            (int cx, int cy) found = (-1, -1);
            foreach (var (cx, cy) in OracleDiskCells(g, x, y, reach))
            {
                if (Gi.World.Query(s.World, g.Id, s.Layer, cx, cy) < threshold) continue;
                var dx = cx * 256L + 128 - qx;
                var dy = cy * 256L + 128 - qy;
                var d = dx * dx + dy * dy;
                if (d > best || (d == best && (cy > found.cy || (cy == found.cy && cx > found.cx)))) continue;
                best = d;
                found = (cx, cy);
            }

            if (ok != complete) return false;
            if (found.cx < 0)
            {
                if (value != short.MinValue || nx != x || ny != y) return false;
                continue;
            }

            var ex = g.X + ((found.cx << 8) + 128) / (float)g.ScaleQ8;
            var ey = g.Y + ((found.cy << 8) + 128) / (float)g.ScaleQ8;
            if (value != Gi.World.Query(s.World, g.Id, s.Layer, found.cx, found.cy) || nx != ex || ny != ey)
            {
                Console.WriteLine($"  nearest ({x},{y},{reach}) >= {threshold}: got {value} at ({nx},{ny}), expected ({ex},{ey})");
                return false;
            }
        }

        return true;
    }
}
