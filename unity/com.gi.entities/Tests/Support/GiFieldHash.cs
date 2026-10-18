namespace Gi.Entities.Tests.Support
{
    public static unsafe class GiFieldHash
    {
        private const ulong Offset = 14695981039346656037ul;
        private const ulong Prime = 1099511628211ul;

        public static ulong OfWorld(byte world)
        {
            var hash = Offset;
            var layers = Layer.Count(world);
            for (byte layer = 0; layer < layers; layer++) hash = MixValue(hash, OfLayer(world, layer));
            return hash;
        }

        public static ulong OfLayer(byte world, byte layer)
        {
            var hash = Offset;
            var grids = Grid.Count(world);
            for (byte grid = 0; grid < grids; grid++)
            {
                if (!Grid.TryGetSpec(world, grid, out var power, out _, out _, out _)) continue;

                var size = 1 << power;
                var cells = new short[size * size];
                fixed (short* p = cells)
                {
                    World.QueryRegion(world, grid, layer, 0, 0, size, size, p);
                    for (var i = 0; i < cells.Length; i++)
                    {
                        hash = Mix(hash, (byte)(cells[i] & 0xFF));
                        hash = Mix(hash, (byte)((cells[i] >> 8) & 0xFF));
                    }
                }
            }

            return hash;
        }

        private static ulong Mix(ulong hash, byte value)
        {
            unchecked
            {
                hash ^= value;
                hash *= Prime;
                return hash;
            }
        }

        private static ulong MixValue(ulong hash, ulong value)
        {
            unchecked
            {
                for (var i = 0; i < 8; i++)
                {
                    hash = Mix(hash, (byte)(value & 0xFF));
                    value >>= 8;
                }

                return hash;
            }
        }
    }
}
