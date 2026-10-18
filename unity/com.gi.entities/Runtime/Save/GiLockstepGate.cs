namespace Gi.Entities
{
    public sealed class GiLockstepTrack
    {
        public readonly GiLockstep Lockstep;
        public readonly GiTileHashes Hashes;
        public readonly ulong[] Layers;

        public GiLockstepTrack(byte world, in GiTables tables)
        {
            var grids = new byte[tables.GridCount];
            for (var g = 0; g < grids.Length; g++) tables.TryGrid(g, out grids[g]);
            var layers = new byte[tables.LayerCount];
            for (var l = 0; l < layers.Length; l++) tables.TryLayer(new GiLayerKey((byte)l), out layers[l]);
            Hashes = new GiTileHashes(world, grids, layers);
            Lockstep = new GiLockstep(layers.Length);
            Layers = new ulong[layers.Length];
        }
    }

    public static class GiLockstepGate
    {
        private static readonly GiLockstepTrack[] Tracks = new GiLockstepTrack[256];
        private static readonly uint[] Generations = new uint[256];

        public static GiLockstepTrack Find(byte world)
        {
            return Tracks[world];
        }

        public static GiLockstepTrack Track(byte world, uint generation, in GiTables tables)
        {
            if (Tracks[world] != null && Generations[world] == generation) return Tracks[world];

            Generations[world] = generation;
            return Tracks[world] = new GiLockstepTrack(world, tables);
        }

        public static void Forget(byte world)
        {
            Tracks[world] = null;
            Generations[world] = 0u;
        }
    }
}
