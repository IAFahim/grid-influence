using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace GridInfluence;

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeBuffer<T> : IDisposable where T : unmanaged
{
    private T* _pointer;
    private int _length;
    private int _capacity;

    public int Length => _length;

    public Span<T> Span => _pointer == null ? default : new(_pointer, _length);

    public T* Pointer => _pointer;

    public void SetLength(int value) => _length = value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Resize(int length)
    {
        if (length <= _capacity)
        {
            _length = length;
            return;
        }

        var capacity = (nuint)Math.Max(length, Math.Max(16, _capacity * 2)) * (nuint)sizeof(T);
        var pointer = (T*)NativeMemory.AlignedAlloc(capacity, 64);
        if (_pointer != null)
        {
            Buffer.MemoryCopy(_pointer, pointer, (long)capacity, (long)_length * sizeof(T));
            NativeMemory.AlignedFree(_pointer);
        }

        _pointer = pointer;
        _capacity = (int)(capacity / (nuint)sizeof(T));
        _length = length;
    }

    public void Dispose()
    {
        if (_pointer == null) return;

        NativeMemory.AlignedFree(_pointer);
        _pointer = null;
        _length = 0;
        _capacity = 0;
    }
}
