using System;

namespace Gi.Entities
{
    public struct GiContentHash
    {
        private const ulong Offset = 14695981039346656037ul;
        private const ulong Prime = 1099511628211ul;

        private ulong _state;

        public static GiContentHash Start()
        {
            return new GiContentHash { _state = Offset };
        }

        public void Add(byte value)
        {
            unchecked
            {
                _state ^= value;
                _state *= Prime;
            }
        }

        public void Add(int value)
        {
            Add((uint)value);
        }

        public void Add(uint value)
        {
            Add((byte)value);
            Add((byte)(value >> 8));
            Add((byte)(value >> 16));
            Add((byte)(value >> 24));
        }

        public void Add(ulong value)
        {
            Add((uint)value);
            Add((uint)(value >> 32));
        }

        public void Add(float value)
        {
            Add(BitConverter.SingleToInt32Bits(value));
        }

        public void Add(ReadOnlySpan<sbyte> values)
        {
            Add(values.Length);
            for (var i = 0; i < values.Length; i++) Add((byte)values[i]);
        }

        public ulong Finish()
        {
            unchecked
            {
                var h = _state;
                h ^= h >> 33;
                h *= 0xff51afd7ed558ccdul;
                h ^= h >> 33;
                h *= 0xc4ceb9fe1a85ec53ul;
                h ^= h >> 33;
                return h;
            }
        }
    }
}
