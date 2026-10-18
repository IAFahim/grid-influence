using System;

namespace Gi.Entities
{
    public static class GiGridCoverage
    {
        private const float MaxCellQ8 = 1073741824f;

        public static bool TryScaleQ8(GiGridSpec grid, out int scaleQ8)
        {
            scaleQ8 = 0;
            if (grid.Power < 5 || grid.Power > 14 || !(grid.Size > 0f) || float.IsInfinity(grid.Size)) return false;

            var scaled = (1 << grid.Power) / grid.Size * 256f;
            if (!(scaled >= 1f && scaled < 1048577f)) return false;

            scaleQ8 = (int)scaled;
            return true;
        }

        public static bool Contains(GiGridSpec grid, float x, float y)
        {
            if (!TryScaleQ8(grid, out var scaleQ8)) return false;

            var qx = CellQ8(x, grid.X, scaleQ8);
            var qy = CellQ8(y, grid.Y, scaleQ8);
            var size = 1 << grid.Power;
            return qx >= 0 && qy >= 0 && (qx >> 8) < size && (qy >> 8) < size;
        }

        public static bool Covers(ReadOnlySpan<GiGridSpec> grids, float x, float y)
        {
            for (var i = 0; i < grids.Length; i++)
                if (Contains(grids[i], x, y))
                    return true;
            return false;
        }

        public static int Finest(ReadOnlySpan<GiGridSpec> grids, float x, float y)
        {
            var best = -1;
            var bestScale = 0;
            for (var i = 0; i < grids.Length; i++)
            {
                if (!Contains(grids[i], x, y) || !TryScaleQ8(grids[i], out var scale)) continue;
                if (best >= 0 && scale <= bestScale) continue;

                best = i;
                bestScale = scale;
            }

            return best;
        }

        public static void Map(ReadOnlySpan<GiGridSpec> grids, float x0, float y0, float x1, float y1, int width, int height, byte[] output)
        {
            if (output.Length < width * height) throw new ArgumentException("The map buffer is too small.", nameof(output));

            for (var row = 0; row < height; row++)
            for (var column = 0; column < width; column++)
            {
                var x = x0 + (x1 - x0) * (column + 0.5f) / width;
                var y = y0 + (y1 - y0) * (row + 0.5f) / height;
                output[row * width + column] = (byte)(Finest(grids, x, y) + 1);
            }
        }

        private static long CellQ8(float value, float origin, int scaleQ8)
        {
            var q = (value - origin) * scaleQ8;
            if (Math.Abs(q) <= MaxCellQ8) return (int)Math.Floor(q);

            return q > 0f ? (long)MaxCellQ8 : -(long)MaxCellQ8;
        }
    }
}
