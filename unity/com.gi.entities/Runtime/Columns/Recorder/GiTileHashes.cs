using System;
using GiGrid = Gi.Grid;
using GiWorld = Gi.World;

namespace Gi.Entities
{
    public sealed unsafe class GiTileHashes
    {
        public const int TileSide = 32;
        private const int TileCells = TileSide * TileSide;
        private const ulong Golden = 0x9E3779B97F4A7C15ul;

        private readonly byte _world;
        private readonly byte[] _grids;
        private readonly byte[] _layers;
        private readonly int[] _tilesPerSide;
        private readonly ulong[][] _tiles;
        private readonly ulong[] _accumulators;
        private readonly int[] _changed;
        private readonly short[] _page = new short[TileCells];
        private int _since;

        public GiTileHashes(byte world, ReadOnlySpan<byte> grids, ReadOnlySpan<byte> layers)
        {
            _world = world;
            _grids = grids.ToArray();
            _layers = layers.ToArray();
            _tilesPerSide = new int[_grids.Length];
            var largest = 1;
            for (var g = 0; g < _grids.Length; g++)
            {
                GiGrid.TryGetSpec(world, _grids[g], out var power, out _, out _, out _);
                _tilesPerSide[g] = Math.Max(1, (1 << power) / TileSide);
                largest = Math.Max(largest, _tilesPerSide[g] * _tilesPerSide[g]);
            }

            _changed = new int[largest];
            _tiles = new ulong[_grids.Length * _layers.Length][];
            _accumulators = new ulong[_tiles.Length];
            for (var g = 0; g < _grids.Length; g++)
            for (var l = 0; l < _layers.Length; l++)
                _tiles[g * _layers.Length + l] = new ulong[_tilesPerSide[g] * _tilesPerSide[g]];
        }

        public int LayerCount => _layers.Length;

        public int GridCount => _grids.Length;

        public int TilesPerSide(int grid)
        {
            return _tilesPerSide[grid];
        }

        public ulong Tile(int grid, int layer, int tile)
        {
            return _tiles[grid * _layers.Length + layer][tile];
        }

        public ulong Grid(int grid, int layer)
        {
            return _accumulators[grid * _layers.Length + layer];
        }

        public ulong Layer(int layer)
        {
            var hash = GiContentHash.Start();
            for (var g = 0; g < _grids.Length; g++) hash.Add(_accumulators[g * _layers.Length + layer]);
            return hash.Finish();
        }

        public void Layers(Span<ulong> destination)
        {
            for (var l = 0; l < _layers.Length && l < destination.Length; l++) destination[l] = Layer(l);
        }

        public void Update()
        {
            var tick = GiWorld.Tick(_world);
            for (var g = 0; g < _grids.Length; g++)
            for (var l = 0; l < _layers.Length; l++) Refresh(g, l);
            _since = tick;
        }

        public static ulong Full(byte world, ReadOnlySpan<byte> grids, byte layer)
        {
            var hash = GiContentHash.Start();
            var page = new short[TileCells];
            for (var g = 0; g < grids.Length; g++)
            {
                GiGrid.TryGetSpec(world, grids[g], out var power, out _, out _, out _);
                var side = Math.Max(1, (1 << power) / TileSide);
                var accumulator = 0ul;
                for (var tile = 0; tile < side * side; tile++) accumulator ^= Read(world, grids[g], layer, side, tile, page);
                hash.Add(accumulator);
            }

            return hash.Finish();
        }

        private void Refresh(int g, int l)
        {
            var count = GiWorld.ChangedTiles(_world, _grids[g], _layers[l], _since, null);
            if (count == 0) return;

            var tiles = _tiles[g * _layers.Length + l];
            var accumulator = _accumulators[g * _layers.Length + l];
            fixed (int* destination = _changed)
            {
                count = GiWorld.ChangedTiles(_world, _grids[g], _layers[l], _since, destination);
                for (var i = 0; i < count; i++)
                {
                    var tile = _changed[i];
                    var hash = Read(_world, _grids[g], _layers[l], _tilesPerSide[g], tile, _page);
                    accumulator ^= tiles[tile] ^ hash;
                    tiles[tile] = hash;
                }
            }

            _accumulators[g * _layers.Length + l] = accumulator;
        }

        private static ulong Read(byte world, byte grid, byte layer, int side, int tile, short[] page)
        {
            fixed (short* destination = page)
                GiWorld.QueryRegion(world, grid, layer, tile % side * TileSide, tile / side * TileSide, TileSide, TileSide, destination);

            var any = 0;
            var hash = 14695981039346656037ul;
            unchecked
            {
                for (var i = 0; i < TileCells; i++)
                {
                    var cell = (ushort)page[i];
                    any |= cell;
                    hash = (hash ^ cell) * 1099511628211ul;
                }

                if (any == 0) return 0ul;

                hash ^= (ulong)(tile + 1) * Golden;
                hash ^= hash >> 32;
                hash *= Golden;
                return hash ^ (hash >> 29);
            }
        }

        public static ulong TileHash(byte world, byte grid, byte layer, int tile)
        {
            GiGrid.TryGetSpec(world, grid, out var power, out _, out _, out _);
            return Read(world, grid, layer, Math.Max(1, (1 << power) / TileSide), tile, new short[TileCells]);
        }
    }
}
