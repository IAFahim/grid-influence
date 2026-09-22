using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace GridInfluence;

internal unsafe struct PageMap : IDisposable
{
    private const byte Empty = 0;
    internal const byte Live = 1;
    private const byte Tombstone = 2;

    private int* _keys;
    private byte** _blocks;
    private byte* _used;
    private int _mask;
    private int _count;
    private int _tombstones;

    public readonly int Count => _count;

    public readonly int SlotCount => _used == null ? 0 : _mask + 1;

    public readonly byte* Used => _used;

    public readonly int* Keys => _keys;

    public readonly byte** Blocks => _blocks;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int HashOf(int key)
    {
        var k = (ulong)(uint)key;
        k ^= k >> 33;
        k *= 0xFF51AFD7ED558CCDul;
        k ^= k >> 33;
        k *= 0xC4CEB9FE1A85EC53ul;
        k ^= k >> 33;
        return (int)k;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool TryGet(int tile, out byte* block)
    {
        var index = IndexOf(tile);
        if (index >= 0)
        {
            block = _blocks[index];
            return true;
        }

        block = null;
        return false;
    }

    public void Put(int tile, byte* block)
    {
        if (_mask == 0 || (_count + _tombstones + 1) * 4 >= (_mask + 1) * 3)
            GrowTo(Math.Max(16, (_count + _tombstones + 1) * 2));

        var index = HashOf(tile) & _mask;
        var tombstone = -1;
        while (_used[index] != Empty)
        {
            if (_used[index] == Live && _keys[index] == tile)
            {
                _blocks[index] = block;
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

        _keys[index] = tile;
        _blocks[index] = block;
        _used[index] = Live;
        _count++;
    }

    public void TombstoneAt(int index)
    {
        _used[index] = Tombstone;
        _count--;
        _tombstones++;
    }

    public bool Remove(int tile)
    {
        var index = IndexOf(tile);
        if (index < 0) return false;

        TombstoneAt(index);
        return true;
    }

    private readonly int IndexOf(int tile)
    {
        if (_mask == 0) return -1;

        var index = HashOf(tile) & _mask;
        while (_used[index] != Empty)
        {
            if (_used[index] == Live && _keys[index] == tile) return index;

            index = (index + 1) & _mask;
        }

        return -1;
    }

    public void Reset()
    {
        if (_used == null) return;

        NativeMemory.AlignedFree(_keys);
        NativeMemory.AlignedFree(_blocks);
        NativeMemory.AlignedFree(_used);
        _keys = null;
        _blocks = null;
        _used = null;
        _mask = 0;
        _count = 0;
        _tombstones = 0;
    }

    private void GrowTo(int minCapacity)
    {
        var capacity = 16;
        while (capacity * 4 < minCapacity * 3) capacity <<= 1;

        var oldKeys = _keys;
        var oldBlocks = _blocks;
        var oldUsed = _used;
        var oldMask = _mask;

        _keys = (int*)NativeMemory.AlignedAlloc((nuint)capacity * sizeof(int), 64);
        _blocks = (byte**)NativeMemory.AlignedAlloc((nuint)capacity * (nuint)sizeof(byte*), 64);
        _used = (byte*)NativeMemory.AlignedAlloc((nuint)capacity, 64);
        _mask = capacity - 1;
        _count = 0;
        _tombstones = 0;
        new Span<byte>(_used, capacity).Clear();

        for (var i = 0; i <= oldMask; i++)
        {
            if (oldUsed != null && oldUsed[i] == Live) AddUnchecked(oldKeys[i], oldBlocks[i]);
        }

        if (oldKeys != null)
        {
            NativeMemory.AlignedFree(oldKeys);
            NativeMemory.AlignedFree(oldBlocks);
            NativeMemory.AlignedFree(oldUsed);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AddUnchecked(int key, byte* block)
    {
        var index = HashOf(key) & _mask;
        while (_used[index] == Live) index = (index + 1) & _mask;

        _keys[index] = key;
        _blocks[index] = block;
        _used[index] = Live;
        _count++;
    }

    public void Dispose()
    {
        if (_used == null) return;

        for (var i = 0; i <= _mask; i++)
        {
            if (_used[i] == Live) NativeMemory.AlignedFree(_blocks[i]);
        }

        Reset();
    }
}
