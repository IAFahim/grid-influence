internal static partial class Verification
{
    private sealed record Mix(byte Id, int Op, byte A, byte B, int Wa, int Wb, int Shift, short Low, short High);

    private static short OracleMix(Mix m, int a, int b) => m.Op switch
    {
        0 => (short)Math.Clamp(OracleShift((long)m.Wa * a + (long)m.Wb * b, m.Shift), short.MinValue, short.MaxValue),
        1 => (short)Math.Min(a, b),
        2 => (short)Math.Max(a, b),
        _ => b >= m.Low && b <= m.High ? (short)a : (short)0,
    };

    private static long OracleShift(long v, int shift)
    {
        if (shift == 0) return v;
        var half = 1L << (shift - 1);
        return v >= 0 ? (v + half) >> shift : -((-v + half) >> shift);
    }

    private static Mix AddMix(byte w, List<Mix> mixes, int op, byte a, byte b, int wa = 0, int wb = 0, int shift = 0,
        short low = 0, short high = 0)
    {
        var id = op switch
        {
            0 when a == b && wb == 0 => Gi.Layer.Sum(w, a, wa, shift),
            0 => Gi.Layer.Sum(w, a, wa, b, wb, shift),
            1 => Gi.Layer.Min(w, a, b),
            2 => Gi.Layer.Max(w, a, b),
            _ => Gi.Layer.Mask(w, a, b, low, high),
        };
        var mix = new Mix(id, op, a, b, wa, wb, shift, low, high);
        mixes.Add(mix);
        return mix;
    }

    private static byte[] MixStamps()
    {
        var samples = new sbyte[9 * 7];
        for (var i = 0; i < samples.Length; i++) samples[i] = (sbyte)(i * 37 % 97 - 40);
        return
        [
            Gi.Stamp.Box(12, 12, 90),
            Gi.Stamp.Box(5, 9, -60),
            Gi.Stamp.Tent(14, 10, 80),
            Gi.Stamp.Bell(18, 18, 100),
            Gi.Stamp.New(samples, 9, 7),
            Gi.Stamp.Bell(3, 3, -70),
        ];
    }

    private static bool DerivedLayersMatchCellFormulas()
    {
        var w = Gi.World.New();
        (int Power, float X, float Y, float Size)[] specs = [(8, 0f, 0f, 256f), (7, 0f, 0f, 256f), (9, 64f, 32f, 128f), (5, 0f, 0f, 256f)];
        foreach (var s in specs) Gi.Grid.New(w, s.Power, s.X, s.Y, s.Size);
        var food = Gi.Layer.New(w);
        var threat = Gi.Layer.New(w);
        var herd = Gi.Layer.New(w);
        var mixes = new List<Mix>();
        var d0 = AddMix(w, mixes, 0, food, threat, 1, -2);
        var d1 = AddMix(w, mixes, 0, threat, threat, -1);
        AddMix(w, mixes, 0, food, herd, 3, 5, 2);
        AddMix(w, mixes, 1, food, herd);
        AddMix(w, mixes, 2, threat, herd);
        var d5 = AddMix(w, mixes, 3, food, threat, low: short.MinValue, high: 40);
        var d6 = AddMix(w, mixes, 2, d0.Id, d5.Id);
        AddMix(w, mixes, 0, d6.Id, d1.Id, 2, 1, 1);
        AddMix(w, mixes, 0, food, threat, short.MaxValue, short.MaxValue);
        AddMix(w, mixes, 3, herd, food, low: 100, high: 2000);
        AddMix(w, mixes, 0, herd, herd, -short.MaxValue, 0, 15);

        var stamps = MixStamps();
        byte[] bases = [food, threat, herd];
        var rng = new Random(707);
        var ids = new List<int>();
        for (var i = 0; i < 40; i++) ids.Add(Gi.World.Place(w, food, 90.5f, 70.25f, stamps[0], 16));
        for (var round = 0; round < 5; round++)
        {
            for (var k = 0; k < 140; k++)
            {
                var op = ids.Count < 30 ? 0 : rng.Next(4);
                if (op == 0)
                {
                    ids.Add(Gi.World.Place(w, bases[rng.Next(bases.Length)], (float)(rng.NextDouble() * 280 - 12),
                        (float)(rng.NextDouble() * 280 - 12), stamps[rng.Next(stamps.Length)], rng.Next(-16, 17)));
                    continue;
                }

                var index = rng.Next(ids.Count);
                if (op == 1) Gi.World.Move(w, ids[index], (float)(rng.NextDouble() * 280 - 12), (float)(rng.NextDouble() * 280 - 12));
                else if (op == 2) Gi.World.SetGain(w, ids[index], rng.Next(-16, 17));
                else
                {
                    Gi.World.Remove(w, ids[index]);
                    ids.RemoveAt(index);
                }
            }

            Gi.World.Process(w);
            for (var gi = 0; gi < specs.Length; gi++)
            {
                var side = 1 << specs[gi].Power;
                var layers = new short[mixes[^1].Id + 1][];
                foreach (var layer in bases) layers[layer] = Read(w, (byte)gi, layer, side);
                var changed = new HashSet<int>[layers.Length];
                foreach (var layer in bases) changed[layer] = Changed(w, (byte)gi, layer, side);
                foreach (var m in mixes)
                {
                    var expected = new short[side * side];
                    for (var c = 0; c < expected.Length; c++) expected[c] = OracleMix(m, layers[m.A][c], layers[m.B][c]);
                    var actual = Read(w, (byte)gi, m.Id, side);
                    for (var c = 0; c < expected.Length; c++)
                        if (actual[c] != expected[c]) return MixFail(m, gi, c, actual[c], expected[c]);
                    layers[m.Id] = expected;

                    var full = 0L;
                    var top = short.MinValue;
                    foreach (var v in expected)
                    {
                        full += v;
                        if (v > top) top = v;
                    }

                    if (Gi.World.Query(w, (byte)gi, m.Id, 0, 0, side, side) != full) return MixFail(m, gi, -1, 0, 0);
                    var best = Gi.World.QueryMax(w, (byte)gi, m.Id, out var bx, out var by);
                    if (best != top || Gi.World.Query(w, (byte)gi, m.Id, bx, by) != top) return MixFail(m, gi, -2, best, top);

                    var rx = rng.Next(side);
                    var ry = rng.Next(side);
                    var rw = rng.Next(1, side - rx + 1);
                    var rh = rng.Next(1, side - ry + 1);
                    var rectSum = 0L;
                    var rectTop = short.MinValue;
                    for (var y = ry; y < ry + rh; y++)
                    for (var x = rx; x < rx + rw; x++)
                    {
                        rectSum += expected[y * side + x];
                        if (expected[y * side + x] > rectTop) rectTop = expected[y * side + x];
                    }

                    if (Gi.World.Query(w, (byte)gi, m.Id, rx, ry, rw, rh) != rectSum) return MixFail(m, gi, -3, 0, 0);
                    var rectBest = Gi.World.QueryMax(w, (byte)gi, m.Id, rx, ry, rw, rh, out var qx, out var qy);
                    if (rectBest != rectTop || qx < rx || qy < ry || qx >= rx + rw || qy >= ry + rh ||
                        Gi.World.Query(w, (byte)gi, m.Id, qx, qy) != rectTop) return MixFail(m, gi, -4, rectBest, rectTop);

                    var union = new HashSet<int>(changed[m.A]);
                    union.UnionWith(changed[m.B]);
                    changed[m.Id] = Changed(w, (byte)gi, m.Id, side);
                    if (!changed[m.Id].SetEquals(union)) return MixFail(m, gi, -5, changed[m.Id].Count, union.Count);
                }
            }
        }

        return true;
    }

    private static unsafe short[] Read(byte w, byte g, byte l, int side)
    {
        var cells = new short[side * side];
        fixed (short* p = cells) Gi.World.QueryRegion(w, g, l, 0, 0, side, side, p);
        return cells;
    }

    private static unsafe HashSet<int> Changed(byte w, byte g, byte l, int side)
    {
        var tiles = new int[(side >> 5) * (side >> 5)];
        int count;
        fixed (int* p = tiles) count = Gi.World.ChangedTiles(w, g, l, p);
        return [.. tiles.AsSpan(0, count).ToArray()];
    }

    private static bool MixFail(Mix m, int grid, int cell, int actual, int expected)
    {
        Console.WriteLine($"  derived layer {m.Id} (op {m.Op}) grid {grid} check {cell}: {actual} vs {expected}");
        return false;
    }

    private static bool DerivedExcludeMatchesRemoval()
    {
        var w = Gi.World.New();
        Gi.Grid.New(w, 9, 0f, 0f, 256f);
        Gi.Grid.New(w, 8, 0f, 0f, 256f);
        Gi.Grid.New(w, 6, 0f, 0f, 256f);
        Gi.Grid.New(w, 12, 64f, 48f, 160f);
        var l = Gi.Layer.New(w);
        var other = Gi.Layer.New(w);
        var mixes = new List<Mix>();
        var sum = AddMix(w, mixes, 0, l, other, 2, -1);
        AddMix(w, mixes, 2, l, other);
        AddMix(w, mixes, 3, l, other, low: -100, high: 100);
        AddMix(w, mixes, 1, sum.Id, l);
        AddMix(w, mixes, 0, other, 3, 1);
        var stamps = MixStamps();
        var rng = new Random(909);
        var ids = new List<int>();
        for (var i = 0; i < 240; i++)
            ids.Add(Gi.World.Place(w, i % 3 == 0 ? other : l, (float)(rng.NextDouble() * 270 - 7),
                (float)(rng.NextDouble() * 270 - 7), stamps[rng.Next(stamps.Length)], rng.Next(-16, 17)));
        var piles = new List<int>();
        for (var i = 0; i < 50; i++) piles.Add(Gi.World.Place(w, i % 2 == 0 ? l : other, 100.3f, 77.7f, stamps[i % 4], 16));
        ids.AddRange(piles);
        Gi.World.Process(w);

        var points = new (float x, float y, float r)[12];
        var excluded = new short[mixes.Count, points.Length];
        var excludedArea = new long[mixes.Count, points.Length];
        var excludedGx = new float[mixes.Count, points.Length];
        var excludedGy = new float[mixes.Count, points.Length];
        var before = new long[mixes.Count, points.Length];
        for (var trial = 0; trial < 60; trial++)
        {
            var id = trial < 10 ? piles[trial * 5] : ids[rng.Next(ids.Count)];
            (float x, float y) anchor = trial < 10 ? (100.3f, 77.7f) : ((float)(rng.NextDouble() * 256), (float)(rng.NextDouble() * 256));
            for (var p = 0; p < points.Length; p++)
                points[p] = (anchor.x + (float)(rng.NextDouble() * 24 - 12), anchor.y + (float)(rng.NextDouble() * 24 - 12),
                    (float)(rng.NextDouble() * (p % 2 == 0 ? 6 : 30)));
            for (var m = 0; m < mixes.Count; m++)
            for (var p = 0; p < points.Length; p++)
            {
                var layer = mixes[m].Id;
                Gi.World.TrySenseArea(w, layer, points[p].x, points[p].y, points[p].r, out before[m, p]);
                Gi.World.TrySense(w, layer, points[p].x, points[p].y, id, out excluded[m, p]);
                Gi.World.TrySenseArea(w, layer, points[p].x, points[p].y, points[p].r, id, out excludedArea[m, p]);
                Gi.World.TrySenseGradient(w, layer, points[p].x, points[p].y, id, out excludedGx[m, p], out excludedGy[m, p]);
            }

            Gi.World.Record(w);
            Gi.World.Remove(w, id);
            Gi.World.Process(w);
            for (var m = 0; m < mixes.Count; m++)
            for (var p = 0; p < points.Length; p++)
            {
                var layer = mixes[m].Id;
                Gi.World.TrySense(w, layer, points[p].x, points[p].y, out var removed);
                Gi.World.TrySenseArea(w, layer, points[p].x, points[p].y, points[p].r, out var removedArea);
                Gi.World.TrySenseGradient(w, layer, points[p].x, points[p].y, out var gx, out var gy);
                if (removed != excluded[m, p] || removedArea != excludedArea[m, p] || gx != excludedGx[m, p] || gy != excludedGy[m, p])
                    return MixFail(mixes[m], trial, p, removed, excluded[m, p]);
            }

            Gi.World.Rewind(w);
            Gi.World.Process(w);
            for (var m = 0; m < mixes.Count; m++)
            for (var p = 0; p < points.Length; p++)
            {
                Gi.World.TrySenseArea(w, mixes[m].Id, points[p].x, points[p].y, points[p].r, out var restored);
                if (restored != before[m, p]) return MixFail(mixes[m], trial, -p, 0, 0);
            }
        }

        return true;
    }

    private static unsafe bool DerivedLayersFollowSourcesRewindAndClear()
    {
        var w = Gi.World.New();
        var g = Gi.Grid.New(w, 8, 0f, 0f, 256f);
        var a = Gi.Layer.New(w);
        var b = Gi.Layer.New(w);
        var d = Gi.Layer.Sum(w, a, 1, b, 1);
        var box = Gi.Stamp.Box(9, 9, 50);
        var tent = Gi.Stamp.Tent(15, 15, 70);
        if (Gi.World.Place(w, d, 40f, 40f, box, 4) != -1) return false;

        var rejects = 0;
        void Expect(Action create)
        {
            try { create(); }
            catch (ArgumentOutOfRangeException) { rejects++; }
        }

        Expect(() => Gi.Layer.Sum(w, a, 1, 30, 1));
        Expect(() => Gi.Layer.Sum(w, a, 40000));
        Expect(() => Gi.Layer.Sum(w, a, 1, 16));
        Expect(() => Gi.Layer.Mask(w, a, b, 10, -10));
        if (rejects != 4) return false;

        var ids = new int[60];
        for (var i = 0; i < ids.Length; i++)
            ids[i] = Gi.World.Place(w, i % 2 == 0 ? a : b, 20f + i * 3.7f, 30f + i * 2.9f, i % 3 == 0 ? tent : box, 3 + i % 5);
        Gi.World.Process(w);
        var baseline = Read(w, g, d, 256);
        Gi.World.Record(w);
        for (var i = 0; i < ids.Length; i += 3) Gi.World.Move(w, ids[i], 200f - i, 190f - i);
        for (var i = 1; i < ids.Length; i += 4) Gi.World.Remove(w, ids[i]);
        Gi.World.Place(w, a, 128f, 128f, tent, 9);
        Gi.World.Process(w);
        if (Read(w, g, d, 256).AsSpan().SequenceEqual(baseline)) return false;
        Gi.World.Rewind(w);
        Gi.World.Process(w);
        if (!Read(w, g, d, 256).AsSpan().SequenceEqual(baseline)) return false;

        Gi.World.Clear(w);
        if (Gi.World.Query(w, g, d, 0, 0, 256, 256) != 0 || Gi.World.QueryMax(w, g, d, out _, out _) != 0 ||
            Gi.World.ChangedTiles(w, g, d, null) != 0) return false;
        Gi.World.Place(w, a, 64f, 64f, box, 2);
        Gi.World.Place(w, b, 66f, 64f, box, 3);
        Gi.World.Process(w);
        var sums = Read(w, g, d, 256);
        var ra = Read(w, g, a, 256);
        var rb = Read(w, g, b, 256);
        for (var c = 0; c < sums.Length; c++)
            if (sums[c] != Math.Clamp(ra[c] + rb[c], short.MinValue, short.MaxValue)) return false;
        if (Gi.World.Query(w, g, d, 64, 64) != 250) return false;

        for (var i = 0; i < 40; i++) Gi.World.Place(w, i % 2 == 0 ? a : b, 10f + i * 6.1f, 200f - i * 4.3f, i % 3 == 0 ? tent : box, 2 + i % 7);
        Gi.World.Process(w);
        var late = Gi.Layer.Max(w, a, b);
        var later = Gi.Layer.Sum(w, d, 2, late, -1);
        Gi.World.Process(w);
        ra = Read(w, g, a, 256);
        rb = Read(w, g, b, 256);
        var max = Read(w, g, late, 256);
        var mixed = Read(w, g, later, 256);
        for (var c = 0; c < max.Length; c++)
        {
            var sum = Math.Clamp(ra[c] + rb[c], short.MinValue, short.MaxValue);
            if (max[c] != Math.Max(ra[c], rb[c]) || mixed[c] != Math.Clamp(2 * sum - max[c], short.MinValue, short.MaxValue)) return false;
        }

        return Gi.World.Query(w, g, late, 0, 0, 256, 256) == max.Sum(v => (long)v);
    }

    private static bool WarmDerivedProcessAllocationFree()
    {
        var w = Gi.World.New();
        Gi.Grid.New(w, 9, 0f, 0f, 512f);
        Gi.Grid.New(w, 6, 0f, 0f, 512f);
        var a = Gi.Layer.New(w);
        var b = Gi.Layer.New(w);
        var sum = Gi.Layer.Sum(w, a, 1, b, -2);
        Gi.Layer.Max(w, sum, Gi.Layer.Mask(w, a, b, -50, 50));
        var stamps = MixStamps();
        var ids = new int[300];
        for (var i = 0; i < ids.Length; i++)
            ids[i] = Gi.World.Place(w, i % 2 == 0 ? a : b, i * 1.7f % 500f, i * 3.1f % 500f, stamps[i % stamps.Length], 5);
        Gi.World.Process(w);
        void Frame(int f)
        {
            for (var i = 0; i < ids.Length; i += 2) Gi.World.Move(w, ids[i], (i * 1.7f + f * 0.37f) % 500f, (i * 3.1f + f * 0.23f) % 500f);
            Gi.World.Process(w);
        }

        for (var f = 0; f < 64; f++) Frame(f);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var f = 64; f < 192; f++) Frame(f);
        var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"  warm process with 3 derived layers over 128 frames: {bytes} B");
        return bytes == 0;
    }
}
