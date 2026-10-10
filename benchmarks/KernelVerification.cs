internal static partial class Verification
{
    private static bool KernelsShareBoxUnitsAndCentre()
    {
        var w = Gi.World.New();
        var g = Gi.Grid.New(w, 9, 0f, 0f, 512f);
        var l = Gi.Layer.New(w);
        int[] widths = [1, 3, 5, 7, 9, 11, 15, 21, 23, 33, 45, 63, 101, 129, 151, 181, 183, 201, 231, 255];
        foreach (var width in widths)
        foreach (var value in new sbyte[] { 90, -90, 1 })
        {
            var stamps = new[]
            {
                Gi.Stamp.Box(width, width, value),
                Gi.Stamp.Tent(width, width, value),
                Gi.Stamp.Bell(width, width, value),
            };
            foreach (var stamp in stamps)
            {
                var id = Gi.World.Place(w, l, 256.5f, 256.5f, stamp, 16);
                Gi.World.Process(w);
                var expected = value * 16;
                var centre = Gi.World.Query(w, g, l, 256, 256);
                if (Math.Abs(centre - expected) > Math.Max(1, Math.Abs(expected) / 200)) return Fail(width, value, stamp, centre);

                var support = 0;
                for (var d = 0; d <= width; d++)
                {
                    var right = Gi.World.Query(w, g, l, 256 + d, 256);
                    var left = Gi.World.Query(w, g, l, 256 - d, 256);
                    var down = Gi.World.Query(w, g, l, 256, 256 + d);
                    if (right != left || right != down) return Fail(width, value, stamp, d);
                    if (Math.Abs(right) > Math.Abs(centre)) return Fail(width, value, stamp, right);
                    if (right != 0) support = d;
                }

                if (value != 1 && support != width / 2) return Fail(width, value, stamp, support);
                Gi.World.Remove(w, id);
                Gi.World.Process(w);
            }
        }

        return true;
    }

    private static bool Fail(int width, int value, byte stamp, int detail)
    {
        Console.WriteLine($"  kernel mismatch: width {width}, value {value}, stamp {stamp}, detail {detail}");
        return false;
    }
}
