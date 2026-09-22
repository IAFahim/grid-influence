using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace GridInfluence;

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct CoordMap : IDisposable
{
    private const byte Empty = 0;
    private const byte Live = 1;
    private const byte Tombstone = 2;

    private ulong* _keys;
    private int* _values;
    private byte* _used;
    private int _mask;
    private int _count;
    private int _tombstones;

    public int Count => _count;

    public static CoordMap Create(int minCapacity)
    {
        var map = default(CoordMap);
        map.GrowTo(Math.Max(16, minCapacity));
        return map;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong KeyOf(Int2 coord) => (uint)coord.X | ((ulong)(uint)coord.Y << 32);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int HashOf(ulong key)
    {
        key ^= key >> 33;
        key *= 0xFF51AFD7ED558CCDul;
        key ^= key >> 33;
        key *= 0xC4CEB9FE1A85EC53ul;
        key ^= key >> 33;
        return (int)key;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool TryGetValue(Int2 coord, out int value)
    {
        var index = IndexOf(KeyOf(coord));
        if (index >= 0)
        {
            value = _values[index];
            return true;
        }

        value = 0;
        return false;
    }

    public void Add(Int2 coord, int value)
    {
        if ((_count + _tombstones + 1) * 4 >= (_mask + 1) * 3) GrowTo((_count + _tombstones + 1) * 2);

        var key = KeyOf(coord);
        var index = HashOf(key) & _mask;
        var tombstone = -1;
        while (_used[index] != Empty)
        {
            if (_used[index] == Live && _keys[index] == key)
            {
                _values[index] = value;
                return;
            }

            if (_used[index] == Tombstone && tombstone < 0) tombstone = index;

            index = (index + 1) & _mask;
        }

        if (tombstone >= 0)
        {
            index = tombstone;
            _tombstones--;
        }

        _keys[index] = key;
        _values[index] = value;
        _used[index] = Live;
        _count++;
    }

    public bool Remove(Int2 coord)
    {
        var index = IndexOf(KeyOf(coord));
        if (index < 0) return false;

        _used[index] = Tombstone;
        _count--;
        _tombstones++;
        return true;
    }

    private readonly int IndexOf(ulong key)
    {
        var index = HashOf(key) & _mask;
        while (_used[index] != Empty)
        {
            if (_used[index] == Live && _keys[index] == key) return index;

            index = (index + 1) & _mask;
        }

        return -1;
    }

    private void GrowTo(int minCapacity)
    {
        var capacity = 16;
        while (capacity * 4 < minCapacity * 3) capacity <<= 1;

        var oldKeys = _keys;
        var oldValues = _values;
        var oldUsed = _used;
        var oldMask = _mask;

        _keys = (ulong*)NativeMemory.AlignedAlloc((nuint)capacity * sizeof(ulong), 64);
        _values = (int*)NativeMemory.AlignedAlloc((nuint)capacity * sizeof(int), 64);
        _used = (byte*)NativeMemory.AlignedAlloc((nuint)capacity, 64);
        _mask = capacity - 1;
        _count = 0;
        _tombstones = 0;
        new Span<byte>(_used, capacity).Clear();

        for (var i = 0; i <= oldMask; i++)
        {
            if (oldUsed != null && oldUsed[i] == Live)
            {
                AddUnchecked(oldKeys[i], oldValues[i]);
            }
        }

        if (oldKeys != null)
        {
            NativeMemory.AlignedFree(oldKeys);
            NativeMemory.AlignedFree(oldValues);
            NativeMemory.AlignedFree(oldUsed);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AddUnchecked(ulong key, int value)
    {
        var index = HashOf(key) & _mask;
        while (_used[index] == Live) index = (index + 1) & _mask;

        _keys[index] = key;
        _values[index] = value;
        _used[index] = Live;
        _count++;
    }

    public void Rehash()
    {
        if (_tombstones * 8 < _mask + 1) return;
        var oldKeys = _keys;
        var oldValues = _values;
        var oldUsed = _used;
        var oldMask = _mask;
        _keys = (ulong*)NativeMemory.AlignedAlloc((nuint)(oldMask + 1) * sizeof(ulong), 64);
        _values = (int*)NativeMemory.AlignedAlloc((nuint)(oldMask + 1) * sizeof(int), 64);
        _used = (byte*)NativeMemory.AlignedAlloc((nuint)(oldMask + 1), 64);
        _count = 0;
        _tombstones = 0;
        new Span<byte>(_used, oldMask + 1).Clear();
        for (var i = 0; i <= oldMask; i++)
        {
            if (oldUsed != null && oldUsed[i] == Live) AddUnchecked(oldKeys[i], oldValues[i]);
        }
        NativeMemory.AlignedFree(oldKeys);
        NativeMemory.AlignedFree(oldValues);
        NativeMemory.AlignedFree(oldUsed);
    }

    public void Dispose()
    {
        if (_keys == null) return;

        NativeMemory.AlignedFree(_keys);
        NativeMemory.AlignedFree(_values);
        NativeMemory.AlignedFree(_used);
        _keys = null;
        _values = null;
        _used = null;
        _mask = 0;
        _count = 0;
        _tombstones = 0;
    }
}