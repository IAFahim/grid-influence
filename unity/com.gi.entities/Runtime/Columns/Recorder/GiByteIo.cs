using System;

namespace Gi.Entities
{
    public sealed class GiByteWriter
    {
        private byte[] _buffer;
        private int _length;

        public GiByteWriter(int capacity = 256)
        {
            _buffer = new byte[Math.Max(16, capacity)];
        }

        public int Length => _length;

        public byte[] Buffer => _buffer;

        public ReadOnlySpan<byte> Span => new ReadOnlySpan<byte>(_buffer, 0, _length);

        public void Clear()
        {
            _length = 0;
        }

        public void Byte(byte value)
        {
            Reserve(1);
            _buffer[_length++] = value;
        }

        public void SByte(sbyte value)
        {
            Byte(unchecked((byte)value));
        }

        public void Bool(bool value)
        {
            Byte(value ? (byte)1 : (byte)0);
        }

        public void Varint(ulong value)
        {
            Reserve(10);
            while (value >= 0x80ul)
            {
                _buffer[_length++] = (byte)(value | 0x80ul);
                value >>= 7;
            }

            _buffer[_length++] = (byte)value;
        }

        public void Zigzag(long value)
        {
            Varint((ulong)((value << 1) ^ (value >> 63)));
        }

        public void Int(int value)
        {
            Zigzag(value);
        }

        public void Float(float value)
        {
            var bits = BitConverter.SingleToInt32Bits(value);
            Reserve(4);
            _buffer[_length++] = (byte)bits;
            _buffer[_length++] = (byte)(bits >> 8);
            _buffer[_length++] = (byte)(bits >> 16);
            _buffer[_length++] = (byte)(bits >> 24);
        }

        public void Fixed64(ulong value)
        {
            Reserve(8);
            for (var i = 0; i < 8; i++) _buffer[_length++] = (byte)(value >> (8 * i));
        }

        public void Bytes(ReadOnlySpan<byte> value)
        {
            Varint((ulong)value.Length);
            Reserve(value.Length);
            value.CopyTo(new Span<byte>(_buffer, _length, value.Length));
            _length += value.Length;
        }

        private void Reserve(int extra)
        {
            if (_length + extra <= _buffer.Length) return;

            var size = _buffer.Length;
            while (size < _length + extra) size *= 2;
            Array.Resize(ref _buffer, size);
        }
    }

    public sealed class GiByteReader
    {
        private readonly byte[] _data;
        private readonly int _end;
        private int _position;

        public GiByteReader(byte[] data, int offset, int length)
        {
            _data = data;
            _position = offset;
            _end = offset + length;
        }

        public bool Failed { get; private set; }

        public bool AtEnd => _position >= _end;

        public byte Byte()
        {
            if (_position >= _end)
            {
                Failed = true;
                return 0;
            }

            return _data[_position++];
        }

        public sbyte SByte()
        {
            return unchecked((sbyte)Byte());
        }

        public bool Bool()
        {
            return Byte() != 0;
        }

        public ulong Varint()
        {
            ulong value = 0;
            for (var shift = 0; shift < 70; shift += 7)
            {
                var b = Byte();
                value |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return value;
                if (Failed) return 0;
            }

            Failed = true;
            return 0;
        }

        public long Zigzag()
        {
            var raw = Varint();
            return (long)(raw >> 1) ^ -(long)(raw & 1ul);
        }

        public int Int()
        {
            return (int)Zigzag();
        }

        public int Count(int limit)
        {
            var value = Varint();
            if (value > (ulong)limit)
            {
                Failed = true;
                return 0;
            }

            return (int)value;
        }

        public float Float()
        {
            if (_position + 4 > _end)
            {
                Failed = true;
                _position = _end;
                return 0f;
            }

            var bits = _data[_position] | (_data[_position + 1] << 8) | (_data[_position + 2] << 16) | (_data[_position + 3] << 24);
            _position += 4;
            return BitConverter.Int32BitsToSingle(bits);
        }

        public ulong Fixed64()
        {
            if (_position + 8 > _end)
            {
                Failed = true;
                _position = _end;
                return 0ul;
            }

            ulong value = 0;
            for (var i = 0; i < 8; i++) value |= (ulong)_data[_position + i] << (8 * i);
            _position += 8;
            return value;
        }

        public byte[] Bytes(int limit)
        {
            var length = Count(limit);
            if (Failed || _position + length > _end)
            {
                Failed = true;
                return Array.Empty<byte>();
            }

            var copy = new byte[length];
            Array.Copy(_data, _position, copy, 0, length);
            _position += length;
            return copy;
        }
    }
}
