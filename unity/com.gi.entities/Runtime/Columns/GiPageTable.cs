namespace Gi.Entities
{
    public sealed class GiPageTable
    {
        private readonly int[] _slots;
        private readonly int[] _free;
        private int _freeCount;
        private int _highWater;

        public GiPageTable(int tileCount)
        {
            if (tileCount <= 0) throw new System.ArgumentOutOfRangeException(nameof(tileCount));
            _slots = new int[tileCount];
            for (var i = 0; i < _slots.Length; i++) _slots[i] = -1;
            _free = new int[tileCount];
        }

        public int TileCount => _slots.Length;

        public int LiveTiles => _highWater - _freeCount;

        public int SlotOf(int tile) => (uint)tile < (uint)_slots.Length ? _slots[tile] : -1;

        public int Acquire(int tile)
        {
            if ((uint)tile >= (uint)_slots.Length) return -1;
            var slot = _slots[tile];
            if (slot >= 0) return slot;
            slot = _freeCount > 0 ? _free[--_freeCount] : _highWater++;
            _slots[tile] = slot;
            return slot;
        }

        public bool Release(int tile)
        {
            if ((uint)tile >= (uint)_slots.Length) return false;
            var slot = _slots[tile];
            if (slot < 0) return false;
            _slots[tile] = -1;
            _free[_freeCount++] = slot;
            return true;
        }

        public static int Plan(int pendingTiles, int budgetTiles)
        {
            if (pendingTiles <= 0 || budgetTiles <= 0) return 0;
            return pendingTiles < budgetTiles ? pendingTiles : budgetTiles;
        }
    }
}
