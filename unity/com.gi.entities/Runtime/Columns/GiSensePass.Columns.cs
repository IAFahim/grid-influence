using GiWorld = Gi.World;

namespace Gi.Entities
{
    public unsafe struct GiSenseColumns
    {
        public float* Xs;
        public float* Ys;
        public float* Reaches;
        public short* Thresholds;
        public GiWorld.SourceId* Excludes;
        public short* Values;
        public float* VectorXs;
        public float* VectorYs;
        public long* Totals;
        public ulong* Whole;
        public int Rows;
    }

    public static unsafe partial class GiSensePass
    {
        public static bool Due(int entityIndex, int period, int tick)
        {
            return period <= 1 || entityIndex % period == tick % period;
        }

        public static int Periods(int configured)
        {
            return configured < 1 ? 1 : configured;
        }

        public static int SinceTick(int tick, int since)
        {
            return tick - (since < 1 ? 1 : since);
        }

        public static void ReadColumns(GiBindingKind kind, byte world, byte layer, in GiSenseColumns columns, float reach,
            int sinceTick, int batch, int batchIndex)
        {
            switch (kind)
            {
                case GiBindingKind.Value:
                case GiBindingKind.Gradient:
                case GiBindingKind.ValueGradient:
                    ReadBatch(kind, world, layer, columns.Xs, columns.Ys, columns.Excludes, columns.Rows, batch, batchIndex,
                        columns.Values, columns.VectorXs, columns.VectorYs, columns.Whole);
                    break;
                case GiBindingKind.Area:
                    ReadAreaBatch(world, layer, columns.Xs, columns.Ys, columns.Reaches, columns.Excludes, columns.Rows, batch,
                        batchIndex, columns.Totals, columns.Whole);
                    break;
                case GiBindingKind.Max:
                    ReadMaxBatch(world, layer, columns.Xs, columns.Ys, columns.Reaches, columns.Rows, batch, batchIndex,
                        columns.Values, columns.VectorXs, columns.VectorYs, columns.Whole);
                    break;
                case GiBindingKind.Nearest:
                    ReadNearestBatch(world, layer, columns.Xs, columns.Ys, columns.Reaches, columns.Thresholds, columns.Rows,
                        batch, batchIndex, columns.Values, columns.VectorXs, columns.VectorYs, columns.Whole);
                    break;
                default:
                    ReadChangedBatch(world, layer, columns.Xs, columns.Ys, reach, sinceTick, columns.Rows, batch, batchIndex,
                        columns.Whole);
                    break;
            }
        }
    }
}
