using System.Numerics;
using Gi;

internal static partial class Verification
{
    private static bool KernelsShareBoxUnitsAndCentre()
    {
        var w = World.New();
        var g = Grid.New(w, 9, 0f, 0f, 512f);
        var l = Layer.New(w);
        ushort[] widths = [1, 3, 5, 9, 15, 23, 33, 63, 129, 183, 231, 255];
        foreach (var width in widths)
        foreach (var value in new sbyte[] { 90, 1 })
        {
            var stamps = new[]
            {
                Stamp.Box(width, width, value),
                Stamp.Tent(width, width, value),
                Stamp.Bell(width, width, value)
            };
            foreach (var stamp in stamps)
            foreach (var gain in value == 1 ? new sbyte[] { 1 } : new sbyte[] { 16, -16 })
            {
                var id = World.Place(w, l, 256.5f, 256.5f, stamp, gain);
                World.Process(w);
                var expected = value * gain;
                var centre = World.Query(w, g, l, 256, 256);
                if (Math.Abs(centre - expected) > Math.Max(1, Math.Abs(expected) / 200))
                    return Fail(width, expected, stamp, centre);

                var support = 0;
                for (var d = 0; d <= width; d++)
                {
                    var right = World.Query(w, g, l, 256 + d, 256);
                    var left = World.Query(w, g, l, 256 - d, 256);
                    var down = World.Query(w, g, l, 256, 256 + d);
                    if (right != left || right != down) return Fail(width, expected, stamp, d);
                    if (Math.Abs(right) > Math.Abs(centre)) return Fail(width, expected, stamp, right);
                    if (right != 0) support = d;
                }

                if (value != 1 && support != width / 2) return Fail(width, expected, stamp, support);
                World.Remove(w, id);
                World.Process(w);
            }
        }

        return true;
    }

    private static bool KernelsMoveSmoothly()
    {
        var w = World.New();
        var g = Grid.New(w, 8, 0f, 0f, 256f);
        var l = Layer.New(w);
        foreach (var width in new ushort[] { 1, 2, 3, 8, 16, 33, 64 })
        foreach (var kind in new[] { 0, 1, 2 })
        {
            var stamp = kind == 0 ? Stamp.Box(width, width, 100) :
                kind == 1 ? Stamp.Tent(width, width, 100) : Stamp.Bell(width, width, 100);
            var halfCells = Math.Max(1.0, width / 2.0);
            var slope = kind == 0 ? 100.0 : kind == 1 ? 100.0 / halfCells : 200.0 / halfCells;
            var bound = (int)Math.Ceiling(slope / 16) + 1;
            var id = World.Place(w, l, 120f, 128.5f, stamp, 1);
            World.Process(w);
            var previous = new short[160];
            for (var c = 0; c < previous.Length; c++) previous[c] = World.Query(w, g, l, 48 + c, 128);
            for (var step = 1; step <= 32; step++)
            {
                World.Move(w, id, 120f + step / 16f, 128.5f);
                World.Process(w);
                for (var c = 0; c < previous.Length; c++)
                {
                    var v = World.Query(w, g, l, 48 + c, 128);
                    if (Math.Abs(v - previous[c]) > bound) return Fail(width, kind, stamp, v - previous[c]);
                    previous[c] = v;
                }
            }

            World.Remove(w, id);
            World.Process(w);
        }

        return true;
    }

    private static bool KernelsHoldStrengthAtEveryScale()
    {
        var strengthStamps = new List<(int Width, byte Stamp)>();
        foreach (var width in new ushort[] { 1, 16, 64, 128, 255 })
        {
            strengthStamps.Add((width, Stamp.Tent(width, width, 90)));
            strengthStamps.Add((width, Stamp.Bell(width, width, 90)));
        }

        foreach (var (power, size) in new[] { (6, 1024f), (9, 512f), (11, 512f), (12, 256f), (13, 256f) })
        {
            var w = World.New();
            var g = Grid.New(w, power, 0f, 0f, size);
            var l = Layer.New(w);
            var scale = (1 << power) / size;
            var x = (MathF.Floor(128f * scale) + 0.5f) / scale;
            foreach (var (width, stamp) in strengthStamps)
            {
                if (width * scale > 512f) continue;

                var id = World.Place(w, l, x, x, stamp, 16);
                World.Process(w);
                var centre = World.QueryAt(w, g, l, x, x);
                if (Math.Abs(centre - 1440) > 1440 * 3 / 200) return Fail(width, (int)scale, stamp, centre);
                World.Remove(w, id);
                World.Process(w);
            }
        }

        return true;
    }

    private static bool Fail(int width, int value, byte stamp, int detail)
    {
        Console.WriteLine($"  kernel mismatch: width {width}, value {value}, stamp {stamp}, detail {detail}");
        return false;
    }

    private static int OracleRoundQ40(long value)
    {
        return (int)((value + 549755813888L + (value >> 63)) >> 40);
    }

    private static long OracleKernel(bool bell, float wx, float wy, int width, int scaleQ8, int cx, int cy,
        long valueGain)
    {
        var extent = width * scaleQ8;
        var leadX = (int)MathF.Floor(wx * scaleQ8) + (((long)-(width * 128) * scaleQ8) >> 8);
        var leadY = (int)MathF.Floor(wy * scaleQ8) + (((long)-(width * 128) * scaleQ8) >> 8);
        var half = Math.Max(256L, extent >> 1);
        var bits = 64 - BitOperations.LeadingZeroCount((ulong)half);
        var shift = Math.Clamp(bits - (bell ? 8 : 15), 0, 7);
        var h = half >> shift;

        long Weight(long lead, int cell)
        {
            var d = ((cell * 256L + 128) >> shift) - ((lead + (extent >> 1)) >> shift);
            return Math.Max(0, bell ? h * h - d * d : h - Math.Abs(d));
        }

        var peak = bell ? h * h : h;
        var normalizer = ((1L << 40) + peak * peak / 2) / (peak * peak);
        return valueGain * normalizer * Weight(leadX, cx) * Weight(leadY, cy);
    }
}

internal static partial class Verification
{
    private static bool SubCellKernelsReachTheirSupport()
    {
        foreach (var (power, size) in new[] { (7, 256f), (6, 256f), (5, 256f) })
        {
            var w = World.New();
            var g = Grid.New(w, power, 0f, 0f, size);
            var l = Layer.New(w);
            var scaleQ8 = (int)((1 << power) / size * 256f);
            foreach (var bell in new[] { false, true })
            foreach (var width in new ushort[] { 1, 2, 3 })
            {
                var stamp = bell ? Stamp.Bell(width, width, 100) : Stamp.Tent(width, width, 100);
                for (var phase = 0; phase < 256; phase += 17)
                {
                    var cell = (1 << power) / 2 + (phase & 1);
                    var wx = (cell * 256 + phase) / (float)scaleQ8 + width / 2f;
                    var wy = (8 * 256 + 128) / (float)scaleQ8;
                    var id = World.Place(w, l, wx, wy, stamp, 16);
                    World.Process(w);
                    for (var cy = 4; cy <= 12; cy++)
                    for (var cx = cell - 4; cx <= cell + 4; cx++)
                    {
                        var expected =
                            Math.Clamp(OracleRoundQ40(OracleKernel(bell, wx, wy, width, scaleQ8, cx, cy, 1600)),
                                short.MinValue, short.MaxValue);
                        if (World.Query(w, g, l, cx, cy) != expected) return Fail(width, phase, stamp, cx);
                    }

                    var probes = new short[9];
                    for (var i = 0; i < 9; i++)
                        World.TrySense(w, l, (cell - 4 + i + 0.5f) * 256f / scaleQ8, wy, id, out probes[i]);
                    World.Remove(w, id);
                    World.Process(w);
                    for (var i = 0; i < 9; i++)
                    {
                        World.TrySense(w, l, (cell - 4 + i + 0.5f) * 256f / scaleQ8, wy, out var removed);
                        if (removed != probes[i]) return Fail(width, phase, stamp, -1 - i);
                    }
                }

                if (!Stamp.Free(stamp)) return false;
            }
        }

        return true;
    }
}
