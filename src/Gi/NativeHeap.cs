using System.Runtime.InteropServices;

namespace Gi;

internal static unsafe class NativeHeap
{
    internal static int Calls;

#if NET
    public static void* AllocZeroed(nuint bytes)
    {
        Calls++;
        return NativeMemory.AllocZeroed(bytes);
    }

    public static void Free(void* pointer)
    {
        Calls++;
        NativeMemory.Free(pointer);
    }

    public static void* AlignedAlloc(nuint bytes)
    {
        Calls++;
        return NativeMemory.AlignedAlloc(bytes, 64);
    }

    public static void AlignedFree(void* pointer)
    {
        Calls++;
        NativeMemory.AlignedFree(pointer);
    }
#else
    public static void* AllocZeroed(nuint bytes)
    {
        Calls++;
        var pointer = (void*)Marshal.AllocHGlobal((nint)bytes);
        new Span<byte>(pointer, (int)bytes).Clear();
        return pointer;
    }

    public static void Free(void* pointer)
    {
        Calls++;
        Marshal.FreeHGlobal((nint)pointer);
    }

    public static void* AlignedAlloc(nuint bytes)
    {
        Calls++;
        return (void*)Marshal.AllocHGlobal((nint)bytes);
    }

    public static void AlignedFree(void* pointer)
    {
        Calls++;
        Marshal.FreeHGlobal((nint)pointer);
    }
#endif
}
