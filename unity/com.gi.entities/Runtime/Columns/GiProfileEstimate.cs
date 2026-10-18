namespace Gi.Entities
{
    public static class GiProfileEstimate
    {
        public const long FixedBytes = 262144;
        public const long TileBytes = 24576;

        public static long Tiles(int power)
        {
            var side = 1L << (power - 5);
            return side * side;
        }

        public static long Layer(long liveTiles)
        {
            return FixedBytes + TileBytes * liveTiles;
        }

        public static long Worst(GiProfileSpec spec)
        {
            var total = 0L;
            foreach (var grid in spec.Grids)
            {
                if (grid.Power < 5 || grid.Power > 14) continue;
                total += spec.Layers.Length * Layer(Tiles(grid.Power));
            }

            return total;
        }

        public static string Format(long bytes)
        {
            if (bytes < 1024L) return bytes + " B";
            if (bytes < 1024L * 1024L) return (bytes / 1024L) + " KiB";
            if (bytes < 1024L * 1024L * 1024L) return (bytes / (1024L * 1024L)) + " MiB";
            return (bytes / (1024L * 1024L * 1024L)) + " GiB";
        }
    }
}
