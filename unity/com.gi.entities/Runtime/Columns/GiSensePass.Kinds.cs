using GiWorld = Gi.World;

namespace Gi.Entities
{
    public static unsafe partial class GiSensePass
    {
        public static GiWorld.ReadStatus ReadArea(byte world, byte layer, float* xs, float* ys, float* reaches,
            GiWorld.SourceId* excludes, int count, long* totals, ulong* whole)
        {
            return GiWorld.SenseAreaColumn(world, layer, xs, ys, reaches, excludes, count, totals, whole);
        }

        public static void ReadAreaBatch(byte world, byte layer, float* xs, float* ys, float* reaches,
            GiWorld.SourceId* excludes, int rows, int batch, int batchIndex, long* totals, ulong* whole)
        {
            var start = batchIndex * batch;
            var count = rows - start < batch ? rows - start : batch;
            if (count <= 0) return;

            ReadArea(world, layer, xs + start, ys + start, reaches + start, excludes == null ? null : excludes + start,
                count, totals + start, whole + (start >> 6));
        }

        public static GiWorld.ReadStatus ReadMax(byte world, byte layer, float* xs, float* ys, float* reaches, int count,
            short* values, float* bestXs, float* bestYs, ulong* whole)
        {
            return GiWorld.SenseMaxColumn(world, layer, xs, ys, reaches, count, values, bestXs, bestYs, whole);
        }

        public static void ReadMaxBatch(byte world, byte layer, float* xs, float* ys, float* reaches, int rows, int batch,
            int batchIndex, short* values, float* bestXs, float* bestYs, ulong* whole)
        {
            var start = batchIndex * batch;
            var count = rows - start < batch ? rows - start : batch;
            if (count <= 0) return;

            ReadMax(world, layer, xs + start, ys + start, reaches + start, count, values + start, bestXs + start,
                bestYs + start, whole + (start >> 6));
        }

        public static GiWorld.ReadStatus ReadNearest(byte world, byte layer, float* xs, float* ys, float* reaches,
            short* thresholds, int count, short* values, float* nearestXs, float* nearestYs, ulong* found)
        {
            return GiWorld.SenseNearestColumn(world, layer, xs, ys, reaches, thresholds, count, values, nearestXs, nearestYs,
                found);
        }

        public static void ReadNearestBatch(byte world, byte layer, float* xs, float* ys, float* reaches, short* thresholds,
            int rows, int batch, int batchIndex, short* values, float* nearestXs, float* nearestYs, ulong* found)
        {
            var start = batchIndex * batch;
            var count = rows - start < batch ? rows - start : batch;
            if (count <= 0) return;

            ReadNearest(world, layer, xs + start, ys + start, reaches + start, thresholds + start, count, values + start,
                nearestXs + start, nearestYs + start, found + (start >> 6));
        }

        public static int ReadChanged(byte world, byte layer, float* xs, float* ys, float reach, int since, int count,
            ulong* words)
        {
            return GiWorld.ChangedMask(world, layer, xs, ys, reach, since, count, words);
        }

        public static void ReadChangedBatch(byte world, byte layer, float* xs, float* ys, float reach, int since, int rows,
            int batch, int batchIndex, ulong* words)
        {
            var start = batchIndex * batch;
            var count = rows - start < batch ? rows - start : batch;
            if (count <= 0) return;

            ReadChanged(world, layer, xs + start, ys + start, reach, since, count, words + (start >> 6));
        }
    }
}
