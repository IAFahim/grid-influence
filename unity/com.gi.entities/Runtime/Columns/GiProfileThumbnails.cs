using GiGrid = Gi.Grid;
using GiWorld = Gi.World;

namespace Gi.Entities
{
    public static class GiProfileThumbnails
    {
        public const int Side = 32;
        public const int Window = 256;

        public static unsafe bool TryRender(GiProfileSpec spec, out short[][] layers, out string error)
        {
            layers = null;
            error = null;
            var registry = new GiStampRegistry();
            var status = GiWorldBuilder.TryBuild(spec.Grids, spec.Layers, spec.Stamps, spec.Cells, registry, out var world, out var tables);
            if (status != GiBuildStatus.Built)
            {
                error = "The profile does not build: " + status + ".";
                return false;
            }

            try
            {
                var grid = Finest(spec);
                tables.TryGrid(grid, out var gridId);
                GiGrid.TryGetSpec(world, gridId, out var power, out var gx, out var gy, out var size);
                if (spec.Stamps.Length > 0 && tables.TryStamp(new GiStampKey(0), out var stamp))
                {
                    var centreX = gx + size * 0.5f;
                    var centreY = gy + size * 0.5f;
                    for (var l = 0; l < spec.Layers.Length; l++)
                    {
                        if (spec.Layers[l].Kind != GiLayerKind.Persistent) continue;

                        tables.TryLayer(new GiLayerKey((byte)l), out var layer);
                        GiWorld.Place(world, layer, centreX, centreY, stamp, 8);
                        GiWorld.Place(world, layer, centreX + size * 0.25f, centreY + size * 0.125f, stamp, 6);
                        GiWorld.Place(world, layer, centreX - size * 0.125f, centreY - size * 0.25f, stamp, 4);
                    }

                    GiWorld.Process(world);
                }

                var cells = 1 << power;
                var read = cells < Window ? cells : Window;
                var origin = (cells - read) / 2;
                layers = new short[spec.Layers.Length][];
                var buffer = new short[read * read];
                for (var l = 0; l < layers.Length; l++)
                {
                    tables.TryLayer(new GiLayerKey((byte)l), out var layer);
                    fixed (short* destination = buffer) GiWorld.QueryRegion(world, gridId, layer, origin, origin, read, read, destination);
                    layers[l] = Pool(buffer, read);
                }

                return true;
            }
            finally
            {
                GiWorldBuilder.Release(world, tables, registry);
            }
        }

        private static int Finest(GiProfileSpec spec)
        {
            var best = 0;
            var bestScale = 0;
            for (var i = 0; i < spec.Grids.Length; i++)
            {
                if (!GiGridCoverage.TryScaleQ8(spec.Grids[i], out var scale) || scale <= bestScale) continue;

                best = i;
                bestScale = scale;
            }

            return best;
        }

        private static short[] Pool(short[] cells, int read)
        {
            var thumb = new short[Side * Side];
            var step = read / (float)Side;
            for (var ty = 0; ty < Side; ty++)
            for (var tx = 0; tx < Side; tx++)
            {
                var x0 = (int)(tx * step);
                var y0 = (int)(ty * step);
                var x1 = System.Math.Max(x0 + 1, (int)((tx + 1) * step));
                var y1 = System.Math.Max(y0 + 1, (int)((ty + 1) * step));
                var best = 0;
                for (var y = y0; y < y1 && y < read; y++)
                for (var x = x0; x < x1 && x < read; x++)
                {
                    var value = cells[y * read + x];
                    if (System.Math.Abs(value) > System.Math.Abs(best)) best = value;
                }

                thumb[ty * Side + tx] = (short)best;
            }

            return thumb;
        }
    }
}
