using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Gi;

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeBuffer<T> where T : unmanaged
{
    private T* _pointer;
    private int _length;
    private int _capacity;

    public int Length => _length;

    public readonly int Capacity => _capacity;

    public Span<T> Span => _pointer == null ? default : new(_pointer, _length);

    public T* Pointer => _pointer;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Resize(int length)
    {
        if (length <= _capacity)
        {
            _length = length;
            return;
        }

        var capacity = (nuint)Math.Max(length, Math.Max(16, _capacity * 2)) * (nuint)sizeof(T);
        var pointer = (T*)NativeHeap.AlignedAlloc(capacity);
        if (_pointer != null)
        {
            Buffer.MemoryCopy(_pointer, pointer, (long)capacity, (long)_length * sizeof(T));
            NativeHeap.AlignedFree(_pointer);
        }

        _pointer = pointer;
        _capacity = (int)(capacity / (nuint)sizeof(T));
        _length = length;
    }

    public void Ensure(int capacity)
    {
        if (capacity <= _capacity) return;
        var length = _length;
        Resize(capacity);
        _length = length;
    }
}
