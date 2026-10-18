using GiWorld = Gi.World;

namespace Gi.Entities
{
    public static unsafe partial class GiSensePass
    {
        public const int WordRows = 64;

        public const int DefaultBatch = 4096;

        public static int Batch(int configured)
        {
            if (configured <= 0) return DefaultBatch;
            return configured < WordRows ? WordRows : configured & ~(WordRows - 1);
        }

        public static GiWorld.ReadStatus Read(GiBindingKind kind, byte world, byte layer, float* xs, float* ys, int count,
            short* values, float* gradientXs, float* gradientYs, ulong* whole)
        {
            return Read(kind, world, layer, xs, ys, null, count, values, gradientXs, gradientYs, whole);
        }

        public static GiWorld.ReadStatus Read(GiBindingKind kind, byte world, byte layer, float* xs, float* ys,
            GiWorld.SourceId* excludes, int count, short* values, float* gradientXs, float* gradientYs, ulong* whole)
        {
            switch (kind)
            {
                case GiBindingKind.Value:
                    return excludes == null
                        ? GiWorld.SenseColumn(world, layer, xs, ys, count, values, whole)
                        : GiWorld.SenseColumn(world, layer, xs, ys, excludes, count, values, whole);
                case GiBindingKind.Gradient:
                    return GiWorld.SenseGradientColumn(world, layer, xs, ys, excludes, count, gradientXs, gradientYs, whole);
                default:
                    return GiWorld.SenseGradientColumn(world, layer, xs, ys, excludes, count, values, gradientXs, gradientYs, whole);
            }
        }

        public static void ReadBatch(GiBindingKind kind, byte world, byte layer, float* xs, float* ys, int rows, int batch,
            int batchIndex, short* values, float* gradientXs, float* gradientYs, ulong* whole)
        {
            ReadBatch(kind, world, layer, xs, ys, null, rows, batch, batchIndex, values, gradientXs, gradientYs, whole);
        }

        public static void ReadBatch(GiBindingKind kind, byte world, byte layer, float* xs, float* ys, GiWorld.SourceId* excludes,
            int rows, int batch, int batchIndex, short* values, float* gradientXs, float* gradientYs, ulong* whole)
        {
            var start = batchIndex * batch;
            var count = rows - start < batch ? rows - start : batch;
            if (count <= 0) return;

            Read(kind, world, layer, xs + start, ys + start, excludes == null ? null : excludes + start, count,
                values == null ? null : values + start, gradientXs == null ? null : gradientXs + start,
                gradientYs == null ? null : gradientYs + start, whole + (start >> 6));
        }

        public static int Batches(int rows, int batch)
        {
            return (rows + batch - 1) / batch;
        }

        public static int Words(int rows)
        {
            return (rows + 63) >> 6;
        }
    }
}
