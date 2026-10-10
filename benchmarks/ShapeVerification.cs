internal static partial class Verification
{
    private sealed record OracleSource(int Kind, float X, float Y, int Width, int Height, sbyte Value, int Gain,
        float Radians, int Scale, int ArcDegrees, sbyte[]? Samples)
    {
        public int Angle => SpecAngle(Radians);
        public int Arc => ArcDegrees >= 360 ? 0 : ArcDegrees * 32768 / 360;
    }

    private static int SpecAngle(float radians)
    {
        var turns = radians * (1.0 / (2 * Math.PI));
        turns -= Math.Floor(turns);
        return (int)(turns * 65536.0) & 0xFFFF;
    }

    private const int KindBox = 0;
    private const int KindRaster = 1;
    private const int KindTent = 2;
    private const int KindBell = 3;
    private const int KindDisk = 4;
    private const int KindCone = 5;
    private const int KindDome = 6;

    private static int OracleSin(int angle)
    {
        var a = angle & 0xFFFF;
        var quadrant = a >> 14;
        var t = a & 16383;
        if ((quadrant & 1) != 0) t = 16384 - t;
        var x2 = (t * t) >> 14;
        var s = (t * (25736 + ((x2 * (-10583 + ((x2 * 1231) >> 14))) >> 14))) >> 14;
        return quadrant >= 2 ? -s : s;
    }

    private static int OracleCos(int angle) => OracleSin(angle + 16384);

    private static long OracleRoot(long value)
    {
        long root = 0;
        for (var bit = 1L << 31; bit > 0; bit >>= 1)
            if ((root + bit) * (root + bit) <= value) root += bit;
        return root;
    }

    private static long OracleFloorDiv(long a, long b) => (long)Math.Floor((decimal)a / b);

    private static int OracleRoundShift(long value, int bits) => (int)((value + (1L << (bits - 1)) + (value >> 63)) >> bits);

    private static (long lead, long extent) OracleAxis(float position, float origin, int scaleQ8, int sampling, int length)
    {
        var originQ8 = -(length * 128);
        var lead = (int)MathF.Floor((position - origin) * scaleQ8) + ((long)originQ8 * sampling >> 8);
        var extent = Math.Min((long)length * sampling, 1L << 30);
        return (lead, extent);
    }

    private static void OracleDeposit(OracleSource s, GridSpec g, int[] dense, long[] tents, long[] bells)
    {
        var sampling = (int)Math.Min((long)g.ScaleQ8 * s.Scale >> 8, int.MaxValue);
        if (sampling == 0) return;
        var (leadX, extentX) = OracleAxis(s.X, g.X, g.ScaleQ8, sampling, s.Width);
        var (leadY, extentY) = OracleAxis(s.Y, g.Y, g.ScaleQ8, sampling, s.Height);
        var centreX = 2 * leadX + extentX;
        var centreY = 2 * leadY + extentY;
        var cos = (long)OracleCos(s.Angle);
        var sin = (long)OracleSin(s.Angle);
        var bell = s.Kind == KindBell;
        long halfU = 0, halfV = 0, curve = 0;
        int shiftU = 0, shiftV = 0;
        if (s.Kind is KindTent or KindBell)
        {
            var hu = Math.Max(256L, extentX >> 1);
            var hv = Math.Max(256L, extentY >> 1);
            shiftU = Math.Clamp(64 - System.Numerics.BitOperations.LeadingZeroCount((ulong)hu) - (bell ? 8 : 15), 0, 7);
            shiftV = Math.Clamp(64 - System.Numerics.BitOperations.LeadingZeroCount((ulong)hv) - (bell ? 8 : 15), 0, 7);
            halfU = hu >> shiftU;
            halfV = hv >> shiftV;
            var peak = (bell ? halfU * halfU : halfU) * (bell ? halfV * halfV : halfV);
            curve = ((1L << 40) + peak / 2) / peak;
        }

        var reach = s.Kind == KindDisk ? extentX : Math.Max(extentX, 512);
        var arcCos = OracleCos(s.Arc);
        var arcSin = OracleSin(s.Arc);
        for (var cy = 0; cy < g.Cells; cy++)
        for (var cx = 0; cx < g.Cells; cx++)
        {
            var dx = cx * 512L + 256 - centreX;
            var dy = cy * 512L + 256 - centreY;
            var u = (dx * cos + dy * sin) >> 14;
            var v = (dy * cos - dx * sin) >> 14;
            var i = cy * g.Cells + cx;
            switch (s.Kind)
            {
                case KindBox:
                {
                    var wu = (int)(Math.Clamp(Math.Min(extentX, u + 256) - Math.Max(-extentX, u - 256), 0, 512) >> 1);
                    var wv = (int)(Math.Clamp(Math.Min(extentY, v + 256) - Math.Max(-extentY, v - 256), 0, 512) >> 1);
                    dense[i] += RoundQ16(s.Value * wu * wv) * s.Gain;
                    break;
                }
                case KindRaster:
                {
                    var tx = OracleFloorDiv((u + extentX - 256) * 32768, sampling);
                    var ty = OracleFloorDiv((v + extentY - 256) * 32768, sampling);
                    var ix = (int)(tx >> 16);
                    var iy = (int)(ty >> 16);
                    if (ix < -1 || iy < -1 || ix >= s.Width || iy >= s.Height) break;
                    var fx = (int)((tx >> 8) & 255);
                    var fy = (int)((ty >> 8) & 255);
                    int Sample(int x, int y) => x < 0 || y < 0 || x >= s.Width || y >= s.Height ? 0 : s.Samples![y * s.Width + x];
                    var top = Sample(ix, iy) * (256 - fx) + Sample(ix + 1, iy) * fx;
                    var bottom = Sample(ix, iy + 1) * (256 - fx) + Sample(ix + 1, iy + 1) * fx;
                    dense[i] += RoundQ16(top * (256 - fy) + bottom * fy) * s.Gain;
                    break;
                }
                case KindTent or KindBell:
                {
                    var du = u >> (shiftU + 1);
                    var dv = v >> (shiftV + 1);
                    var wu = bell ? halfU * halfU - du * du : halfU - Math.Abs(du);
                    var wv = bell ? halfV * halfV - dv * dv : halfV - Math.Abs(dv);
                    if (wu <= 0 || wv <= 0) break;
                    var product = (long)s.Value * s.Gain * curve * wu * wv;
                    if (bell) bells[i] += product;
                    else tents[i] += product;
                    break;
                }
                default:
                {
                    var d2 = dx * dx + dy * dy;
                    long weight;
                    if (s.Kind == KindDisk)
                    {
                        var edge = reach - OracleRoot(d2) + 256;
                        if (edge <= 0) break;
                        weight = Math.Min(Math.Min(edge, 512), 2 * reach) << 7;
                    }
                    else
                    {
                        if (d2 >= reach * reach) break;
                        if (s.Kind == KindCone) weight = ((reach - OracleRoot(d2)) << 16) / reach;
                        else
                        {
                            var r2 = reach * reach;
                            weight = r2 < 1L << 47 ? ((r2 - d2) << 16) / r2 : (r2 - d2) / (r2 >> 16);
                        }
                    }

                    var cover = 256L;
                    if (s.Arc != 0)
                    {
                        var outside = (Math.Abs(v) * arcCos - u * arcSin) >> 14;
                        cover = Math.Clamp(256 - outside, 0, 512) >> 1;
                    }

                    dense[i] += OracleRoundShift(s.Value * weight * cover, 24) * s.Gain;
                    break;
                }
            }
        }
    }

    private static short[] OracleField(IEnumerable<OracleSource> sources, GridSpec g)
    {
        var n = g.Cells * g.Cells;
        var dense = new int[n];
        var tents = new long[n];
        var bells = new long[n];
        foreach (var s in sources) OracleDeposit(s, g, dense, tents, bells);
        var field = new short[n];
        for (var i = 0; i < n; i++)
            field[i] = (short)Math.Clamp(dense[i] + (long)OracleRoundQ40(tents[i]) + OracleRoundQ40(bells[i]), short.MinValue, short.MaxValue);
        return field;
    }

    private static byte ShapeStamp(OracleSource s) => s.Kind switch
    {
        KindBox => Gi.Stamp.Box(s.Width, s.Height, s.Value),
        KindRaster => Gi.Stamp.New(s.Samples!, s.Width, s.Height),
        KindTent => Gi.Stamp.Tent(s.Width, s.Height, s.Value),
        KindBell => Gi.Stamp.Bell(s.Width, s.Height, s.Value),
        KindDisk => Gi.Stamp.Disk(s.Width / 2, s.Value, s.ArcDegrees),
        KindCone => Gi.Stamp.Cone(s.Width / 2, s.Value, s.ArcDegrees),
        KindDome => Gi.Stamp.Dome(s.Width / 2, s.Value, s.ArcDegrees),
        _ => throw new ArgumentOutOfRangeException(nameof(s)),
    };

    private static float TurnedRadians(Random rng, int kind)
    {
        while (true)
        {
            var binary = rng.Next(4) == 0 ? rng.Next(4) * 16384 : rng.Next(65536);
            var radians = (float)(binary * (2 * Math.PI / 65536.0));
            if (kind >= KindDisk || SpecAngle(radians) != 0) return radians;
        }
    }

    private static OracleSource RandomShape(Random rng, int kind, float size)
    {
        var radius = rng.Next(1, 20);
        var width = kind >= KindDisk ? 2 * radius : rng.Next(1, 30);
        var height = kind >= KindDisk ? 2 * radius : rng.Next(1, 30);
        sbyte[]? samples = null;
        if (kind == KindRaster)
        {
            samples = new sbyte[width * height];
            for (var i = 0; i < samples.Length; i++) samples[i] = (sbyte)rng.Next(-90, 120);
        }

        return new OracleSource(kind, (float)(rng.NextDouble() * (size + 20) - 10), (float)(rng.NextDouble() * (size + 20) - 10),
            width, height, (sbyte)rng.Next(-100, 127), rng.Next(-16, 17), TurnedRadians(rng, kind),
            rng.Next(4) == 0 ? 256 : rng.Next(256, 640), rng.Next(3) == 0 ? 360 : rng.Next(10, 300), samples);
    }

    private static bool TurnedAndRoundStampsMatchOracle()
    {
        var w = Gi.World.New();
        GridSpec[] grids = [new(0, 7, 0f, 0f, 128f), new(1, 8, 0f, 0f, 128f), new(2, 6, 16f, 8f, 96f)];
        foreach (var g in grids) Gi.Grid.New(w, g.Power, g.X, g.Y, g.Size);
        var l = Gi.Layer.New(w);
        var rng = new Random(1212);
        var live = new List<(int Id, OracleSource Source)>();
        var palette = new List<(OracleSource Shape, byte Stamp)>();
        for (var i = 0; i < 21; i++)
        {
            var shape = RandomShape(rng, i % 7, 128f);
            palette.Add((shape, ShapeStamp(shape)));
        }

        OracleSource Pick()
        {
            var (shape, _) = palette[rng.Next(palette.Count)];
            return shape with
            {
                X = (float)(rng.NextDouble() * 148 - 10), Y = (float)(rng.NextDouble() * 148 - 10),
                Gain = rng.Next(-16, 17), Radians = TurnedRadians(rng, shape.Kind), Scale = rng.Next(4) == 0 ? 256 : rng.Next(256, 640),
            };
        }

        void Add(OracleSource s)
        {
            var id = Gi.World.Place(w, l, s.X, s.Y, palette.First(p => p.Shape.Kind == s.Kind && p.Shape.Width == s.Width &&
                p.Shape.Height == s.Height && p.Shape.Value == s.Value && p.Shape.ArcDegrees == s.ArcDegrees && p.Shape.Samples == s.Samples).Stamp, s.Gain);
            Gi.World.Turn(w, id, s.Radians);
            Gi.World.Scale(w, id, s.Scale / 256f);
            live.Add((id, s));
        }

        for (var i = 0; i < 30; i++) Add(Pick());
        for (var round = 0; round < 3; round++)
        {
            Gi.World.Process(w);
            foreach (var g in grids)
            {
                var expected = OracleField(live.Select(e => e.Source), g);
                var actual = Read(w, g.Id, l, g.Cells);
                for (var c = 0; c < expected.Length; c++)
                    if (actual[c] != expected[c])
                    {
                        Console.WriteLine($"  shape mismatch round {round} grid {g.Id} cell ({c % g.Cells},{c / g.Cells}): {actual[c]} vs {expected[c]}");
                        return false;
                    }
            }

            for (var k = 0; k < 14; k++)
            {
                var index = rng.Next(live.Count);
                var (id, s) = live[index];
                switch (rng.Next(5))
                {
                    case 0:
                        Gi.World.Remove(w, id);
                        live.RemoveAt(index);
                        Add(Pick());
                        break;
                    case 1:
                    {
                        var turned = s with { Radians = TurnedRadians(rng, s.Kind) };
                        Gi.World.Turn(w, id, turned.Radians);
                        live[index] = (id, turned);
                        break;
                    }
                    case 2:
                    {
                        var scaled = s with { Scale = rng.Next(256, 768) };
                        Gi.World.Scale(w, id, scaled.Scale / 256f);
                        live[index] = (id, scaled);
                        break;
                    }
                    case 3:
                    {
                        var moved = s with { X = (float)(rng.NextDouble() * 140 - 6), Y = (float)(rng.NextDouble() * 140 - 6) };
                        Gi.World.Move(w, id, moved.X, moved.Y);
                        live[index] = (id, moved);
                        break;
                    }
                    default:
                    {
                        var gained = s with { Gain = rng.Next(-16, 17) };
                        Gi.World.SetGain(w, id, gained.Gain);
                        live[index] = (id, gained);
                        break;
                    }
                }
            }
        }

        return ReleaseStamps(w, palette.Select(p => p.Stamp));
    }

    private static bool ReleaseStamps(byte w, IEnumerable<byte> stamps)
    {
        var held = stamps.Distinct().ToArray();
        Gi.World.Clear(w);
        foreach (var stamp in held)
            if (!Gi.Stamp.Free(stamp))
            {
                Console.WriteLine($"  stamp {stamp} did not free after Clear");
                return false;
            }

        return !Gi.Stamp.Free(held[0]);
    }

    private static bool RoundKernelsShareBoxUnitsAndCentre()
    {
        var w = Gi.World.New();
        var g = Gi.Grid.New(w, 9, 0f, 0f, 512f);
        var l = Gi.Layer.New(w);
        foreach (var radius in new[] { 1, 2, 3, 7, 16, 41, 90, 128 })
        foreach (var make in new Func<int, sbyte, byte>[] { (r, v) => Gi.Stamp.Disk(r, v), (r, v) => Gi.Stamp.Cone(r, v), (r, v) => Gi.Stamp.Dome(r, v) })
        {
            var stamp = make(radius, 90);
            if (!RoundKernelHoldsCentre(w, g, l, stamp, radius) || !Gi.Stamp.Free(stamp)) return false;
        }

        return Gi.World.Query(w, g, l, 0, 0, 512, 512) == 0;
    }

    private static bool RoundKernelHoldsCentre(byte w, byte g, byte l, byte stamp, int radius)
    {
        foreach (var gain in new[] { 16, -7 })
        {
            var id = Gi.World.Place(w, l, 256.5f, 256.5f, stamp, gain);
            Gi.World.Process(w);
            var centre = Gi.World.Query(w, g, l, 256, 256);
            if (centre != 90 * gain) return Fail(radius, gain, stamp, centre);
            var support = 0;
            for (var d = 0; d <= radius + 2; d++)
            {
                var right = Gi.World.Query(w, g, l, 256 + d, 256);
                if (right != Gi.World.Query(w, g, l, 256 - d, 256) || right != Gi.World.Query(w, g, l, 256, 256 + d) ||
                    right != Gi.World.Query(w, g, l, 256, 256 - d)) return Fail(radius, gain, stamp, d);
                if (Math.Abs(right) > Math.Abs(centre)) return Fail(radius, gain, stamp, right);
                if (d > 0 && Math.Abs(right) > Math.Abs(Gi.World.Query(w, g, l, 256 + d - 1, 256))) return Fail(radius, gain, stamp, -d);
                if (right != 0) support = d;
            }

            for (var d = 1; d < radius; d++)
                if (Gi.World.Query(w, g, l, 256 + d, 256 + d) != Gi.World.Query(w, g, l, 256 - d, 256 - d))
                    return Fail(radius, gain, stamp, -100 - d);
            if (support > radius) return Fail(radius, gain, stamp, support);
            Gi.World.Remove(w, id);
            Gi.World.Process(w);
        }

        return true;
    }

    private static bool StampsTurnAndScaleSmoothly()
    {
        var w = Gi.World.New();
        var g = Gi.Grid.New(w, 8, 0f, 0f, 256f);
        var l = Gi.Layer.New(w);
        var samples = new sbyte[20 * 12];
        for (var y = 0; y < 12; y++)
        for (var x = 0; x < 20; x++)
            samples[y * 20 + x] = (sbyte)(60 + 40 * Math.Sin(x * 0.4) * Math.Cos(y * 0.5));
        var stamps = new (byte Stamp, double Slope, double Radius)[]
        {
            (Gi.Stamp.Box(24, 10, 100), 100, 14),
            (Gi.Stamp.Tent(24, 16, 100), 100.0 / 8, 15),
            (Gi.Stamp.Bell(24, 16, 100), 200.0 / 8, 15),
            (Gi.Stamp.New(samples, 20, 12), 100, 12),
            (Gi.Stamp.Disk(12, 100, 90), 100, 13),
            (Gi.Stamp.Cone(14, 100, 120), 100, 15),
            (Gi.Stamp.Dome(14, 100, 60), 100, 15),
        };
        const int steps = 256;
        foreach (var (stamp, slope, radius) in stamps)
        {
            var id = Gi.World.Place(w, l, 128.3f, 127.6f, stamp, 1);
            Gi.World.Turn(w, id, 0.001f);
            Gi.World.Process(w);
            var previous = Read(w, g, l, 256);
            var turnBound = (int)Math.Ceiling(slope * radius * 1.5 * (2 * Math.PI / steps)) + 2;
            for (var step = 1; step <= steps; step++)
            {
                Gi.World.Turn(w, id, (float)(step * 2 * Math.PI / steps) + 0.001f);
                Gi.World.Process(w);
                var field = Read(w, g, l, 256);
                for (var c = 0; c < field.Length; c++)
                    if (Math.Abs(field[c] - previous[c]) > turnBound) return Fail(stamp, step, field[c] - previous[c], turnBound);
                previous = field;
            }

            const int scaleSteps = 64;
            var scaleBound = (int)Math.Ceiling(slope * radius * 2 * 1.5 / scaleSteps) + 2;
            for (var step = 1; step <= scaleSteps; step++)
            {
                Gi.World.Scale(w, id, 1f + (float)step / scaleSteps);
                Gi.World.Process(w);
                var field = Read(w, g, l, 256);
                for (var c = 0; c < field.Length; c++)
                    if (Math.Abs(field[c] - previous[c]) > scaleBound) return Fail(stamp, 1000 + step, field[c] - previous[c], scaleBound);
                previous = field;
            }

            Gi.World.Remove(w, id);
            Gi.World.Process(w);
        }

        return ReleaseStamps(w, stamps.Select(s => s.Stamp));
    }

    private static bool Fail(byte stamp, int step, int delta, int bound)
    {
        Console.WriteLine($"  stamp {stamp} step {step}: cell moved {delta} (bound {bound})");
        return false;
    }

    private static bool TurnAndScaleRoundTripExactly()
    {
        var w = Gi.World.New();
        var g = Gi.Grid.New(w, 8, 0f, 0f, 256f);
        var l = Gi.Layer.New(w);
        var samples = new sbyte[9 * 7];
        for (var i = 0; i < samples.Length; i++) samples[i] = (sbyte)(i * 13 % 70 - 20);
        byte[] stamps = [Gi.Stamp.Box(14, 9, 70), Gi.Stamp.Tent(17, 11, 80), Gi.Stamp.Bell(13, 19, 60), Gi.Stamp.New(samples, 9, 7)];
        var ids = new int[stamps.Length * 6];
        for (var i = 0; i < ids.Length; i++)
            ids[i] = Gi.World.Place(w, l, 20f + i * 9.3f, 30f + i * 7.1f, stamps[i % stamps.Length], 5 + i % 4);
        Gi.World.Process(w);
        var baseline = Read(w, g, l, 256);
        for (var i = 0; i < ids.Length; i++)
        {
            Gi.World.Turn(w, ids[i], 0.3f + i);
            Gi.World.Scale(w, ids[i], 0.5f + i * 0.1f);
        }

        Gi.World.Process(w);
        if (Read(w, g, l, 256).AsSpan().SequenceEqual(baseline)) return false;
        for (var i = 0; i < ids.Length; i++)
        {
            Gi.World.Turn(w, ids[i], (float)(2 * Math.PI * (i % 3)));
            Gi.World.Scale(w, ids[i], 1f);
        }

        Gi.World.Process(w);
        if (!Read(w, g, l, 256).AsSpan().SequenceEqual(baseline)) return false;

        byte[] small = [Gi.Stamp.Box(8, 8, 50), Gi.Stamp.Tent(8, 6, 50), Gi.Stamp.Disk(4, 50)];
        var box = Gi.World.Place(w, l, 100.25f, 100.75f, small[0], 3);
        var tent = Gi.World.Place(w, l, 150.5f, 60.5f, small[1], 3);
        var disk = Gi.World.Place(w, l, 60.5f, 160.5f, small[2], 3);
        stamps = [.. stamps, .. small];
        Gi.World.Scale(w, box, 2f);
        Gi.World.Scale(w, tent, 2f);
        Gi.World.Scale(w, disk, 2f);
        Gi.World.Process(w);
        var scaled = Read(w, g, l, 256);
        Gi.World.Remove(w, box);
        Gi.World.Remove(w, tent);
        Gi.World.Remove(w, disk);
        byte[] resized = [Gi.Stamp.Box(16, 16, 50), Gi.Stamp.Tent(16, 12, 50), Gi.Stamp.Disk(8, 50)];
        Gi.World.Place(w, l, 100.25f, 100.75f, resized[0], 3);
        Gi.World.Place(w, l, 150.5f, 60.5f, resized[1], 3);
        Gi.World.Place(w, l, 60.5f, 160.5f, resized[2], 3);
        Gi.World.Process(w);
        return Read(w, g, l, 256).AsSpan().SequenceEqual(scaled) && ReleaseStamps(w, stamps.Concat(resized));
    }

    private static bool StampsWiderThanGridCoverIt()
    {
        var w = Gi.World.New();
        var g = Gi.Grid.New(w, 5, 0f, 0f, 32f);
        var l = Gi.Layer.New(w);
        var wide = Gi.Stamp.Box(64, 64, 40);
        var id = Gi.World.Place(w, l, 4f, 28f, wide, 2);
        Gi.World.Process(w);
        if (Gi.World.Query(w, g, l, 0, 0, 32, 32) != 32 * 32 * 80) return false;
        Gi.World.Scale(w, id, 6f);
        Gi.World.Move(w, id, 100f, -100f);
        Gi.World.Process(w);
        if (Gi.World.Query(w, g, l, 0, 0, 32, 32) != 32 * 32 * 80) return false;
        Gi.World.Remove(w, id);
        var samples = new sbyte[48 * 48];
        Array.Fill(samples, (sbyte)30);
        samples[0] = 31;
        var sheet = Gi.Stamp.New(samples, 48, 48);
        var raster = Gi.World.Place(w, l, 16f, 16f, sheet, 1);
        Gi.World.Process(w);
        for (var y = 0; y < 32; y++)
        for (var x = 0; x < 32; x++)
            if (Gi.World.Query(w, g, l, x, y) != 30) return false;
        Gi.World.Remove(w, raster);
        Gi.World.Process(w);
        return Gi.World.Query(w, g, l, 0, 0, 32, 32) == 0 && ReleaseStamps(w, [wide, sheet]);
    }

    private static bool TurnedExcludeMatchesRemoval()
    {
        var w = Gi.World.New();
        Gi.Grid.New(w, 8, 0f, 0f, 256f);
        Gi.Grid.New(w, 7, 0f, 0f, 256f);
        Gi.Grid.New(w, 10, 64f, 64f, 128f);
        var l = Gi.Layer.New(w);
        var mix = Gi.Layer.Sum(w, l, 2);
        var rng = new Random(1313);
        var palette = new List<byte>();
        for (var i = 0; i < 14; i++) palette.Add(ShapeStamp(RandomShape(rng, i % 7, 256f)));
        var dome = Gi.Stamp.Dome(9, 120);
        palette.Add(dome);
        var ids = new List<int>();
        for (var i = 0; i < 160; i++)
        {
            var s = RandomShape(rng, i % 7, 256f);
            var id = Gi.World.Place(w, l, s.X, s.Y, palette[i % 14], s.Gain);
            if (i % 5 != 0) Gi.World.Turn(w, id, s.Radians);
            Gi.World.Scale(w, id, s.Scale / 256f);
            ids.Add(id);
        }

        var pile = new List<int>();
        for (var i = 0; i < 30; i++)
        {
            var id = Gi.World.Place(w, l, 120.3f, 90.6f, dome, 16);
            Gi.World.Turn(w, id, i * 0.2f);
            pile.Add(id);
        }

        ids.AddRange(pile);
        Gi.World.Process(w);
        byte[] layers = [l, mix];
        for (var trial = 0; trial < 50; trial++)
        {
            var id = trial < 6 ? pile[trial * 4] : ids[rng.Next(ids.Count)];
            var ax = trial < 6 ? 120.3f : (float)(rng.NextDouble() * 256);
            var ay = trial < 6 ? 90.6f : (float)(rng.NextDouble() * 256);
            var points = new (float x, float y, float r)[10];
            for (var p = 0; p < points.Length; p++)
                points[p] = (ax + (float)(rng.NextDouble() * 24 - 12), ay + (float)(rng.NextDouble() * 24 - 12), (float)(rng.NextDouble() * 20));
            var excluded = new (short v, long a, float gx, float gy)[layers.Length, points.Length];
            for (var k = 0; k < layers.Length; k++)
            for (var p = 0; p < points.Length; p++)
            {
                Gi.World.TrySense(w, layers[k], points[p].x, points[p].y, id, out var v);
                Gi.World.TrySenseArea(w, layers[k], points[p].x, points[p].y, points[p].r, id, out var a);
                Gi.World.TrySenseGradient(w, layers[k], points[p].x, points[p].y, id, out var gx, out var gy);
                excluded[k, p] = (v, a, gx, gy);
            }

            Gi.World.Record(w);
            Gi.World.Remove(w, id);
            Gi.World.Process(w);
            for (var k = 0; k < layers.Length; k++)
            for (var p = 0; p < points.Length; p++)
            {
                Gi.World.TrySense(w, layers[k], points[p].x, points[p].y, out var v);
                Gi.World.TrySenseArea(w, layers[k], points[p].x, points[p].y, points[p].r, out var a);
                Gi.World.TrySenseGradient(w, layers[k], points[p].x, points[p].y, out var gx, out var gy);
                if ((v, a, gx, gy) != excluded[k, p])
                {
                    Console.WriteLine($"  turned exclusion trial {trial} layer {layers[k]} point {p}: {(v, a, gx, gy)} vs {excluded[k, p]}");
                    return false;
                }
            }

            Gi.World.Rewind(w);
            Gi.World.Process(w);
        }

        return ReleaseStamps(w, palette);
    }

    private static bool WarmTurnedProcessAllocationFree()
    {
        var w = Gi.World.New();
        Gi.Grid.New(w, 9, 0f, 0f, 512f);
        var l = Gi.Layer.New(w);
        byte[] stamps = [Gi.Stamp.Box(12, 6, 50), Gi.Stamp.Tent(16, 10, 60), Gi.Stamp.Cone(10, 70, 90), Gi.Stamp.Dome(8, 60), Gi.Stamp.Disk(6, 40, 200)];
        var ids = new int[200];
        for (var i = 0; i < ids.Length; i++) ids[i] = Gi.World.Place(w, l, i * 2.3f % 500f, i * 4.7f % 500f, stamps[i % stamps.Length], 4);
        void Frame(int f)
        {
            for (var i = 0; i < ids.Length; i++)
            {
                Gi.World.Turn(w, ids[i], f * 0.05f + i);
                if (i % 3 == 0) Gi.World.Scale(w, ids[i], 1f + (f + i) % 8 / 8f);
                if (i % 2 == 0) Gi.World.Move(w, ids[i], (i * 2.3f + f * 0.4f) % 500f, i * 4.7f % 500f);
            }

            Gi.World.Process(w);
        }

        for (var f = 0; f < 32; f++) Frame(f);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var f = 32; f < 96; f++) Frame(f);
        var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"  warm turn/scale/move process over 64 frames: {bytes} B");
        return bytes == 0 && ReleaseStamps(w, stamps);
    }
}
