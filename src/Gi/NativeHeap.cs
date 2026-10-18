using System.Runtime.InteropServices;
using System.Threading;

namespace Gi;

internal static unsafe class NativeHeap
{
    internal const nuint DefaultAlignment = 0;
    internal const nuint VectorAlignment = 64;

    internal static int Calls;
    internal static delegate* unmanaged[Cdecl]<nuint, nuint, void*> HookAllocate;
    internal static delegate* unmanaged[Cdecl]<void*, void> HookFree;

#if NET
    public static void* AllocZeroed(nuint bytes)
    {
        Interlocked.Increment(ref Calls);
        var hook = HookAllocate;
        if (hook == null) return NativeMemory.AllocZeroed(bytes);
        var pointer = hook(bytes, DefaultAlignment);
        new Span<byte>(pointer, (int)bytes).Clear();
        return pointer;
    }

    public static void Free(void* pointer)
    {
        Interlocked.Increment(ref Calls);
        var hook = HookFree;
        if (hook == null) NativeMemory.Free(pointer);
        else hook(pointer);
    }

    public static void* AlignedAlloc(nuint bytes)
    {
        Interlocked.Increment(ref Calls);
        var hook = HookAllocate;
        return hook == null ? NativeMemory.AlignedAlloc(bytes, VectorAlignment) : hook(bytes, VectorAlignment);
    }

    public static void AlignedFree(void* pointer)
    {
        Interlocked.Increment(ref Calls);
        var hook = HookFree;
        if (hook == null) NativeMemory.AlignedFree(pointer);
        else hook(pointer);
    }
#else
    public static void* AllocZeroed(nuint bytes)
    {
        Interlocked.Increment(ref Calls);
        var hook = HookAllocate;
        if (hook == null)
        {
            var pointer = (void*)Marshal.AllocHGlobal((nint)bytes);
            new Span<byte>(pointer, (int)bytes).Clear();
            return pointer;
        }

        var hooked = hook(bytes, DefaultAlignment);
        new Span<byte>(hooked, (int)bytes).Clear();
        return hooked;
    }

    public static void Free(void* pointer)
    {
        Interlocked.Increment(ref Calls);
        var hook = HookFree;
        if (hook == null) Marshal.FreeHGlobal((nint)pointer);
        else hook(pointer);
    }

    public static void* AlignedAlloc(nuint bytes)
    {
        Interlocked.Increment(ref Calls);
        var hook = HookAllocate;
        if (hook == null) return (void*)Marshal.AllocHGlobal((nint)bytes);
        return hook(bytes, VectorAlignment);
    }

    public static void AlignedFree(void* pointer)
    {
        Interlocked.Increment(ref Calls);
        var hook = HookFree;
        if (hook == null) Marshal.FreeHGlobal((nint)pointer);
        else hook(pointer);
    }
#endif
}
