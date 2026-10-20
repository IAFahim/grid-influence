using System.Runtime.CompilerServices;

namespace Gi;

public static unsafe partial class World
{
    public static FieldView View(byte world, byte grid, byte layer)
    {
        var w = GetContext(world);
        if (w == null || grid >= w->GridCount || layer >= w->LayerCount || w->Grids[grid].ScaleQ8 == 0) return default;

        var g = w->Grids + grid;
        return new FieldView(g, g->Layers + layer);
    }

    public ref struct FieldView
    {
        private readonly LayerData* _layer;
        private readonly int _tilesPerSide;
        private readonly int _scaleQ8;
        private readonly float _originX;
        private readonly float _originY;
        private int _tile;
        private short* _page;

        internal FieldView(GridCtx* g, LayerData* layer)
        {
            _layer = layer;
            Size = g->Size;
            _tilesPerSide = g->TilesPerSide;
            _scaleQ8 = g->ScaleQ8;
            _originX = g->OriginX;
            _originY = g->OriginY;
            _tile = -1;
            _page = null;
        }

        public int Size { get; }

        public short this[int x, int y]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                if ((uint)x >= (uint)Size || (uint)y >= (uint)Size) return 0;

                var tile = (y >> TileBake.TileBits) * _tilesPerSide + (x >> TileBake.TileBits);
                if (tile != _tile)
                {
                    _tile = tile;
                    _page = _layer->PageOf(tile);
                }

                return _page == null
                    ? (short)0
                    : _page[(y & (TileBake.TileSize - 1)) * TileBake.TileSize + (x & (TileBake.TileSize - 1))];
            }
        }

        public bool TryCell(float x, float y, out int cx, out int cy)
        {
            cx = 0;
            cy = 0;
            if (Size == 0) return false;

            var qx = CellQ8(x, _originX, _scaleQ8);
            var qy = CellQ8(y, _originY, _scaleQ8);
            if (qx < 0 || qy < 0 || qx >= (long)Size << 8 || qy >= (long)Size << 8) return false;

            cx = (int)(qx >> 8);
            cy = (int)(qy >> 8);
            return true;
        }

        public short At(float x, float y)
        {
            var qx = CellQ8(x, _originX, _scaleQ8);
            var qy = CellQ8(y, _originY, _scaleQ8);
            return this[(int)(qx >> 8), (int)(qy >> 8)];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Gradient(int x, int y, out int gx, out int gy)
        {
            var lx = x & (TileBake.TileSize - 1);
            var ly = y & (TileBake.TileSize - 1);
            if ((uint)x < (uint)Size && (uint)y < (uint)Size &&
                (uint)(lx - 1) < TileBake.TileSize - 2 && (uint)(ly - 1) < TileBake.TileSize - 2)
            {
                var tile = (y >> TileBake.TileBits) * _tilesPerSide + (x >> TileBake.TileBits);
                if (tile != _tile)
                {
                    _tile = tile;
                    _page = _layer->PageOf(tile);
                }

                if (_page == null)
                {
                    gx = 0;
                    gy = 0;
                    return;
                }

                var at = _page + ly * TileBake.TileSize + lx;
                gx = at[1] - at[-1];
                gy = at[TileBake.TileSize] - at[-TileBake.TileSize];
                return;
            }

            gx = this[x + 1, y] - this[x - 1, y];
            gy = this[x, y + 1] - this[x, y - 1];
        }
    }
}
