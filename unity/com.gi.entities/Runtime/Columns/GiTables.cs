namespace Gi.Entities
{
    public unsafe struct GiTables
    {
        public const int MaxGrids = 32;
        public const int MaxLayers = 32;
        public const int MaxStamps = 255;

        public byte GridCount;
        public byte LayerCount;
        public byte StampCount;
        private fixed byte _grids[MaxGrids];
        private fixed byte _layers[MaxLayers];
        private fixed byte _stamps[MaxStamps];

        public readonly bool TryGrid(int index, out byte grid)
        {
            grid = 0;
            if ((uint)index >= GridCount) return false;
            grid = _grids[index];
            return true;
        }

        public readonly bool TryLayer(GiLayerKey key, out byte layer)
        {
            layer = 0;
            if (key.Index >= LayerCount) return false;
            layer = _layers[key.Index];
            return true;
        }

        public readonly bool TryStamp(GiStampKey key, out byte stamp)
        {
            stamp = 0;
            if (key.Index >= StampCount) return false;
            stamp = _stamps[key.Index];
            return true;
        }

        public void AddGrid(byte grid)
        {
            _grids[GridCount++] = grid;
        }

        public void AddLayer(byte layer)
        {
            _layers[LayerCount++] = layer;
        }

        public void AddStamp(byte stamp)
        {
            _stamps[StampCount++] = stamp;
        }
    }
}
