using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Gi;

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeBuffer<T> where T : unmanaged
{
    public int Length { get; private set; }

    public int Capacity { get; private set; }

    public Span<T> Span => Pointer == null ? default : new Span<T>(Pointer, Length);

    public T* Pointer { get; private set; }

    [MethodImpl(Jit.Inline)]
    public void Resize(int length)
    {
        if (length <= Capacity)
        {
            Length = length;
            return;
        }

        var oldLength = Length;
        Grow(length);
        new Span<byte>(Pointer + oldLength, (int)((nuint)(Capacity - oldLength) * (nuint)sizeof(T))).Clear();
    }

    [MethodImpl(Jit.Hot)]
    public void ResizeUninitialized(int length)
    {
        if (length <= Capacity)
        {
            Length = length;
            return;
        }

        Grow(length);
    }

    [MethodImpl(Jit.Hot)]
    private void Grow(int length)
    {
        var capacity = (nuint)Math.Max(length, Math.Max(16, Capacity * 2)) * (nuint)sizeof(T);
        var pointer = (T*)NativeHeap.AlignedAlloc(capacity);
        if (Pointer != null)
        {
            Buffer.MemoryCopy(Pointer, pointer, (long)capacity, (long)Length * sizeof(T));
            NativeHeap.AlignedFree(Pointer);
        }

        Pointer = pointer;
        Capacity = (int)(capacity / (nuint)sizeof(T));
        Length = length;
    }

    [MethodImpl(Jit.Hot)]
    public void Free()
    {
        if (Pointer == null) return;

        NativeHeap.AlignedFree(Pointer);
        Pointer = null;
        Length = 0;
        Capacity = 0;
    }
}
