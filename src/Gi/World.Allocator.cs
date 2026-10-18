namespace Gi;

public static unsafe partial class World
{
    public static bool SetAllocator(
        delegate* unmanaged[Cdecl]<nuint, nuint, void*> allocate,
        delegate* unmanaged[Cdecl]<void*, void> free)
    {
        if ((allocate == null) != (free == null))
            throw new ArgumentException("Install both pointers or pass both null.");

        if (Runtime.Worlds != null) return false;

        NativeHeap.HookAllocate = allocate;
        NativeHeap.HookFree = free;
        return true;
    }
}
