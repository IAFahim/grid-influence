using System.Runtime.InteropServices;

namespace Gi;

internal static unsafe class NativeHeap
{
    internal static int Calls;

#if NET
    public static void* AllocZeroed(nuint bytes)
    {
        Interlocked.Increment(ref Calls);
        return NativeMemory.AllocZeroed(bytes);
    }

    public static void Free(void* pointer)
    {
        Interlocked.Increment(ref Calls);
        NativeMemory.Free(pointer);
    }

    public static void* AlignedAlloc(nuint bytes)
    {
        Interlocked.Increment(ref Calls);
        return NativeMemory.AlignedAlloc(bytes, 64);
    }

    public static void AlignedFree(void* pointer)
    {
        Interlocked.Increment(ref Calls);
        NativeMemory.AlignedFree(pointer);
    }
#else
    public static void* AllocZeroed(nuint bytes)
    {
        Interlocked.Increment(ref Calls);
        var pointer = (void*)Marshal.AllocHGlobal((nint)bytes);
        new Span<byte>(pointer, (int)bytes).Clear();
        return pointer;
    }

    public static void Free(void* pointer)
    {
        Interlocked.Increment(ref Calls);
        Marshal.FreeHGlobal((nint)pointer);
    }

    public static void* AlignedAlloc(nuint bytes)
    {
        Interlocked.Increment(ref Calls);
        return (void*)Marshal.AllocHGlobal((nint)bytes);
    }

    public static void AlignedFree(void* pointer)
    {
        Interlocked.Increment(ref Calls);
        Marshal.FreeHGlobal((nint)pointer);
    }
#endif
}
