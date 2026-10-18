using System;

namespace Gi.Entities
{
    public sealed class GiHistory
    {
        private readonly short[] _values;
        private readonly int[] _ticks;
        private int _start;
        private int _count;

        public GiHistory(int capacity)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            _values = new short[capacity];
            _ticks = new int[capacity];
        }

        public int Capacity => _values.Length;

        public int Count => _count;

        public int LastTick => _count == 0 ? -1 : _ticks[(_start + _count - 1) % _values.Length];

        public short this[int index] => _values[(_start + Check(index)) % _values.Length];

        public int TickAt(int index) => _ticks[(_start + Check(index)) % _values.Length];

        public bool Push(int tick, short value)
        {
            if (_count > 0 && tick <= LastTick) return false;

            if (_count == _values.Length)
            {
                _start = (_start + 1) % _values.Length;
                _count--;
            }

            var slot = (_start + _count) % _values.Length;
            _values[slot] = value;
            _ticks[slot] = tick;
            _count++;
            return true;
        }

        public void Clear()
        {
            _start = 0;
            _count = 0;
        }

        public void Range(out short low, out short high)
        {
            low = 0;
            high = 0;
            for (var i = 0; i < _count; i++)
            {
                var value = this[i];
                if (i == 0 || value < low) low = value;
                if (i == 0 || value > high) high = value;
            }
        }

        private int Check(int index)
        {
            if ((uint)index >= (uint)_count) throw new ArgumentOutOfRangeException(nameof(index));
            return index;
        }
    }
}
