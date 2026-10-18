using System;

namespace Gi.Entities
{
    public sealed class GiMirrorPlanner
    {
        private readonly GiPageTable _pages;
        private readonly byte[] _queued;
        private readonly int[] _queue;
        private int _head;
        private int _count;

        public GiMirrorPlanner(int tileCount)
        {
            _pages = new GiPageTable(tileCount);
            _queued = new byte[tileCount];
            _queue = new int[tileCount];
        }

        public int TileCount => _pages.TileCount;

        public int Pending => _count;

        public int LiveTiles => _pages.LiveTiles;

        public int SlotOf(int tile) => _pages.SlotOf(tile);

        public int Capacity { get; private set; }

        public void Enqueue(int tile)
        {
            if ((uint)tile >= (uint)_queued.Length || _queued[tile] != 0) return;

            _queued[tile] = 1;
            var tail = _head + _count;
            if (tail >= _queue.Length) tail -= _queue.Length;
            _queue[tail] = tile;
            _count++;
        }

        public int Take(int budget, int[] tiles)
        {
            var take = GiPageTable.Plan(_count, budget);
            if (take > tiles.Length) take = tiles.Length;
            for (var i = 0; i < take; i++)
            {
                var tile = _queue[_head];
                _head++;
                if (_head == _queue.Length) _head = 0;
                _queued[tile] = 0;
                tiles[i] = tile;
            }

            _count -= take;
            return take;
        }

        public int Commit(int tile, bool empty)
        {
            if (empty)
            {
                _pages.Release(tile);
                return -1;
            }

            var slot = _pages.Acquire(tile);
            if (slot >= Capacity) Capacity = slot + 1;
            return slot;
        }

        public static int GrownCapacity(int current, int required)
        {
            if (required <= current) return current;
            var grown = current < 16 ? 16 : current;
            while (grown < required) grown = grown > int.MaxValue / 2 ? required : grown * 2;
            return grown;
        }

        public static long UploadBytes(int tiles)
        {
            return (long)tiles * (TileInts * sizeof(int) + sizeof(int));
        }

        public const int TileCells = 32;
        public const int TileInts = TileCells * TileCells / 2;

        public static int TilesPerSide(int power)
        {
            if (power < 5) throw new ArgumentOutOfRangeException(nameof(power));
            return 1 << (power - 5);
        }
    }
}
