using System;
using GiWorld = Gi.World;

namespace Gi.Entities
{
    public static unsafe partial class GiSensePass
    {
        public const int GateScratchLimit = 4096;

        private const float GateMaxCellQ8 = 1073741824f;

        public static void GateBounds(float* xs, float* ys, float* reaches, float constantReach, int rows,
            out float minX, out float minY, out float maxX, out float maxY)
        {
            minX = 0f;
            minY = 0f;
            maxX = 0f;
            maxY = 0f;
            for (var i = 0; i < rows; i++)
            {
                var reach = reaches != null ? reaches[i] : constantReach;
                if (!(reach >= 0f)) reach = 0f;
                var loX = xs[i] - reach;
                var hiX = xs[i] + reach;
                var loY = ys[i] - reach;
                var hiY = ys[i] + reach;
                if (i == 0)
                {
                    minX = loX;
                    minY = loY;
                    maxX = hiX;
                    maxY = hiY;
                    continue;
                }

                if (loX < minX) minX = loX;
                if (loY < minY) minY = loY;
                if (hiX > maxX) maxX = hiX;
                if (hiY > maxY) maxY = hiY;
            }
        }

        public static bool GateTiles(in GiGridSpec grid, float minX, float minY, float maxX, float maxY,
            out int tx0, out int ty0, out int tx1, out int ty1)
        {
            tx0 = 0;
            ty0 = 0;
            tx1 = -1;
            ty1 = -1;
            if (!GiGridCoverage.TryScaleQ8(grid, out var scale)) return false;

            var side = GiMirrorPlanner.TilesPerSide(grid.Power);
            var ax = GateCell(minX, grid.X, scale) >> 5;
            var ay = GateCell(minY, grid.Y, scale) >> 5;
            var bx = GateCell(maxX, grid.X, scale) >> 5;
            var by = GateCell(maxY, grid.Y, scale) >> 5;
            if (bx < 0 || by < 0 || ax >= side || ay >= side) return true;
            tx0 = ax < 0 ? 0 : ax;
            ty0 = ay < 0 ? 0 : ay;
            tx1 = bx >= side ? side - 1 : bx;
            ty1 = by >= side ? side - 1 : by;
            return true;
        }

        public static bool GateClosed(byte world, byte layer, GiGridSpec* grids, byte* gridIds, int gridCount,
            int since, float minX, float minY, float maxX, float maxY, int* scratch, int scratchCapacity)
        {
            for (var gi = 0; gi < gridCount; gi++)
            {
                if (!GateTiles(grids[gi], minX, minY, maxX, maxY, out var tx0, out var ty0, out var tx1, out var ty1))
                    return false;
                if (tx1 < tx0 || ty1 < ty0) continue;

                var count = GiWorld.ChangedTiles(world, gridIds[gi], layer, since, null);
                if (count <= 0) continue;
                if (scratch == null || count > scratchCapacity) return false;

                var side = GiMirrorPlanner.TilesPerSide(grids[gi].Power);
                var found = GiWorld.ChangedTiles(world, gridIds[gi], layer, since, scratch);
                if (found > count) found = count;
                for (var i = 0; i < found; i++)
                {
                    var tile = scratch[i];
                    if ((uint)tile >= (uint)(side * side)) continue;
                    var tx = tile % side;
                    if (tx < tx0 || tx > tx1) continue;
                    var ty = tile / side;
                    if (ty >= ty0 && ty <= ty1) return false;
                }
            }

            return true;
        }

        private static int GateCell(float value, float origin, int scaleQ8)
        {
            var q = (value - origin) * scaleQ8;
            long fixedQ8;
            if (Math.Abs(q) <= GateMaxCellQ8) fixedQ8 = (long)Math.Floor(q);
            else fixedQ8 = q > 0f ? (long)GateMaxCellQ8 : -(long)GateMaxCellQ8;
            return (int)(fixedQ8 >> 8);
        }
    }
}
