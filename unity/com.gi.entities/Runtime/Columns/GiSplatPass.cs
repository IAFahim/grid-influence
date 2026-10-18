using GiWorld = Gi.World;

namespace Gi.Entities
{
    public static unsafe class GiSplatPass
    {
        public const int Layers = 32;

        public static int Apply(byte world, GiTables tables, int* layerStart, int* layerCount, float* xs, float* ys, byte* stamps,
            sbyte* gains)
        {
            var applied = 0;
            for (var key = 0; key < Layers; key++)
            {
                var count = layerCount[key];
                if (count == 0 || !tables.TryLayer(new GiLayerKey((byte)key), out var layer)) continue;

                var start = layerStart[key];
                var columnsX = new System.ReadOnlySpan<float>(xs + start, count);
                var columnsY = new System.ReadOnlySpan<float>(ys + start, count);
                if (Uniform(stamps + start, gains + start, count))
                    applied += GiWorld.Splat(world, layer, columnsX, columnsY, stamps[start], gains[start]);
                else
                    applied += GiWorld.Splat(world, layer, columnsX, columnsY,
                        new System.ReadOnlySpan<byte>(stamps + start, count), new System.ReadOnlySpan<sbyte>(gains + start, count));
            }

            return applied;
        }

        public static bool Uniform(byte* stamps, sbyte* gains, int count)
        {
            for (var i = 1; i < count; i++)
                if (stamps[i] != stamps[0] || gains[i] != gains[0])
                    return false;
            return true;
        }

        public static GiFaultCode Check(in GiTables tables, GiLayerKey layer, GiStampKey stamp, out byte stampId)
        {
            stampId = 0;
            if (!tables.TryLayer(layer, out _)) return GiFaultCode.InvalidLayer;
            if (!tables.IsTransient(layer)) return GiFaultCode.WrongResidency;
            return tables.TryStamp(stamp, out stampId) ? GiFaultCode.None : GiFaultCode.InvalidStamp;
        }
    }
}
