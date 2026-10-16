using System.Runtime.CompilerServices;

namespace Gi;

internal unsafe struct FreeRing
{
    private NativeBuffer<int> _slots;
    private int _head;
    private int _count;

    public int Count => _count;

    public int Size => _slots.Length;

    public readonly int Capacity => _slots.Capacity;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryPop(out int slot)
    {
        if (_count == 0)
        {
            slot = -1;
            return false;
        }

        slot = _slots.Pointer[_head];
        var next = _head + 1;
        _head = next == _slots.Length ? 0 : next;
        _count--;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Push(int slot)
    {
        var tail = _head + _count;
        if (tail >= _slots.Length) tail -= _slots.Length;
        _slots.Pointer[tail] = slot;
        _count++;
    }

    public void Clear()
    {
        _head = 0;
        _count = 0;
    }

    public void Reserve(int size)
    {
        var current = _slots.Length;
        if (size <= current) return;

        var grown = default(NativeBuffer<int>);
        grown.Resize(size);
        if (_count > 0)
        {
            var leading = Math.Min(_count, current - _head);
            new Span<int>(_slots.Pointer + _head, leading).CopyTo(new Span<int>(grown.Pointer, leading));
            new Span<int>(_slots.Pointer, _count - leading).CopyTo(new Span<int>(grown.Pointer + leading, _count - leading));
        }

        _slots.Free();
        _slots = grown;
        _head = 0;
    }

    public void Release()
    {
        _slots.Free();
        _head = 0;
        _count = 0;
    }
}
