using System.Runtime.CompilerServices;

namespace Gi;

internal unsafe struct FreeRing
{
    private NativeBuffer<int> _slots;
    private int _head;

    private int Count { get; set; }

    public readonly int Capacity => _slots.Capacity;

    [MethodImpl(Jit.Inline)]
    public bool TryPop(out int slot)
    {
        if (Count == 0)
        {
            slot = -1;
            return false;
        }

        slot = _slots.Pointer[_head];
        var next = _head + 1;
        _head = next == _slots.Length ? 0 : next;
        Count--;
        return true;
    }

    [MethodImpl(Jit.Inline)]
    public void Push(int slot)
    {
        var tail = _head + Count;
        if (tail >= _slots.Length) tail -= _slots.Length;
        _slots.Pointer[tail] = slot;
        Count++;
    }

    [MethodImpl(Jit.Hot)]
    public void Clear()
    {
        _head = 0;
        Count = 0;
    }

    [MethodImpl(Jit.Hot)]
    public void Reserve(int size)
    {
        var current = _slots.Length;
        if (size <= current) return;

        var grown = default(NativeBuffer<int>);
        grown.Resize(size);
        if (Count > 0)
        {
            var leading = Math.Min(Count, current - _head);
            new Span<int>(_slots.Pointer + _head, leading).CopyTo(new Span<int>(grown.Pointer, leading));
            new Span<int>(_slots.Pointer, Count - leading).CopyTo(new Span<int>(grown.Pointer + leading,
                Count - leading));
        }

        _slots.Free();
        _slots = grown;
        _head = 0;
    }

    [MethodImpl(Jit.Hot)]
    public void Release()
    {
        _slots.Free();
        _head = 0;
        Count = 0;
    }
}
