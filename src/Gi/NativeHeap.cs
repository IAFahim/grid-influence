using System.Runtime.InteropServices;

namespace Gi;

internal static unsafe class NativeHeap
{
#if NET
    public static void* AllocZeroed(nuint bytes) => NativeMemory.AllocZeroed(bytes);

    public static void* AlignedAlloc(nuint bytes) => NativeMemory.AlignedAlloc(bytes, 64);

    public static void AlignedFree(void* pointer) => NativeMemory.AlignedFree(pointer);
#else
    public static void* AllocZeroed(nuint bytes)
    {
        var pointer = (void*)Marshal.AllocHGlobal((nint)bytes);
        new Span<byte>(pointer, (int)bytes).Clear();
        return pointer;
    }

    public static void* AlignedAlloc(nuint bytes) => (void*)Marshal.AllocHGlobal((nint)bytes);

    public static void AlignedFree(void* pointer) => Marshal.FreeHGlobal((nint)pointer);
#endif
}
