using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Gi;

internal static unsafe class NativeHeap
{
    private const nuint DefaultAlignment = 0;
    private const nuint VectorAlignment = 64;

    internal static int Calls { get => Runtime.Root.Calls; set => Runtime.Root.Calls = value; }
    internal static delegate* unmanaged[Cdecl]<nuint, nuint, void*> HookAllocate { get => Runtime.Root.HookAllocate; set => Runtime.Root.HookAllocate = value; }
    internal static delegate* unmanaged[Cdecl]<void*, void> HookFree { get => Runtime.Root.HookFree; set => Runtime.Root.HookFree = value; }

#if NET
    [MethodImpl(Jit.Hot)]
    public static void* AllocZeroed(nuint bytes)
    {
        Interlocked.Increment(ref Runtime.Root.Calls);
        var hook = Runtime.Root.HookAllocate;
        if (hook == null) return NativeMemory.AllocZeroed(bytes);
        var pointer = hook(bytes, DefaultAlignment);
        new Span<byte>(pointer, (int)bytes).Clear();
        return pointer;
    }

    [MethodImpl(Jit.Hot)]
    public static void Free(void* pointer)
    {
        Interlocked.Increment(ref Runtime.Root.Calls);
        var hook = Runtime.Root.HookFree;
        if (hook == null) NativeMemory.Free(pointer);
        else hook(pointer);
    }

    [MethodImpl(Jit.Hot)]
    public static void* AlignedAlloc(nuint bytes)
    {
        Interlocked.Increment(ref Runtime.Root.Calls);
        var hook = Runtime.Root.HookAllocate;
        return hook == null ? NativeMemory.AlignedAlloc(bytes, VectorAlignment) : hook(bytes, VectorAlignment);
    }

    [MethodImpl(Jit.Hot)]
    public static void AlignedFree(void* pointer)
    {
        Interlocked.Increment(ref Runtime.Root.Calls);
        var hook = Runtime.Root.HookFree;
        if (hook == null) NativeMemory.AlignedFree(pointer);
        else hook(pointer);
    }
#else
    [MethodImpl(Jit.Hot)]
    public static void* AllocZeroed(nuint bytes)
    {
        Interlocked.Increment(ref Runtime.Root.Calls);
        var hook = Runtime.Root.HookAllocate;
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

    [MethodImpl(Jit.Hot)]
    public static void Free(void* pointer)
    {
        Interlocked.Increment(ref Runtime.Root.Calls);
        var hook = Runtime.Root.HookFree;
        if (hook == null) Marshal.FreeHGlobal((nint)pointer);
        else hook(pointer);
    }

    [MethodImpl(Jit.Hot)]
    public static void* AlignedAlloc(nuint bytes)
    {
        Interlocked.Increment(ref Runtime.Root.Calls);
        var hook = Runtime.Root.HookAllocate;
        if (hook != null) return hook(bytes, VectorAlignment);
        nuint slack = VectorAlignment - 1 + (nuint)sizeof(void*);
        var raw = (byte*)Marshal.AllocHGlobal((nint)(bytes + slack));
        var aligned = (byte*)(((nuint)raw + (nuint)sizeof(void*) + VectorAlignment - 1) & ~(VectorAlignment - 1));
        ((void**)aligned)[-1] = raw;
        return aligned;
    }

    [MethodImpl(Jit.Hot)]
    public static void AlignedFree(void* pointer)
    {
        Interlocked.Increment(ref Runtime.Root.Calls);
        var hook = Runtime.Root.HookFree;
        if (hook == null) Marshal.FreeHGlobal((nint)((void**)pointer)[-1]);
        else hook(pointer);
    }
#endif
}
