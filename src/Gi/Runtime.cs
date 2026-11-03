using System.Runtime.CompilerServices;

[module: SkipLocalsInit]

namespace Gi;

internal unsafe struct GiRoot
{
    public WorldCtx* Worlds;
    public StampVariant* Stamps;
    public byte* ZeroDense;
    public int* WorldRevisions;
    public int RevisionFloor;
    public int Calls;
    public delegate* unmanaged[Cdecl]<nuint, nuint, void*> HookAllocate;
    public delegate* unmanaged[Cdecl]<void*, void> HookFree;
    public bool Measuring;
    public int StampCount;
    public int WorldCount;
}

internal static unsafe class Runtime
{
    internal static GiRoot Root;

    internal static WorldCtx* Worlds
    {
        get => Root.Worlds;
        set => Root.Worlds = value;
    }

    internal static StampVariant* Stamps
    {
        get => Root.Stamps;
        set => Root.Stamps = value;
    }

    internal static byte* ZeroDense
    {
        get => Root.ZeroDense;
        set => Root.ZeroDense = value;
    }

    internal static int* WorldRevisions
    {
        get => Root.WorldRevisions;
        set => Root.WorldRevisions = value;
    }

    [MethodImpl(MethodImplOptions.NoInlining | Jit.Hot)]
    internal static void Ensure()
    {
        if (Root.Worlds != null) return;

        Root.Worlds = (WorldCtx*)NativeHeap.AllocZeroed((nuint)(World.MaxWorlds * sizeof(WorldCtx)));
        Root.Stamps = (StampVariant*)NativeHeap.AllocZeroed((nuint)(StampCatalog.MaxStamps * sizeof(StampVariant)));
        Root.ZeroDense = (byte*)NativeHeap.AlignedAlloc(World.PageBytes);
        new Span<byte>(Root.ZeroDense, World.PageBytes).Clear();
        Root.WorldRevisions = (int*)NativeHeap.AllocZeroed((nuint)World.MaxWorlds * sizeof(int));
        new Span<int>(Root.WorldRevisions, World.MaxWorlds).Fill(Root.RevisionFloor);
        if (Root.StampCount == 0) Root.StampCount = 1;
    }

    [MethodImpl(MethodImplOptions.NoInlining | Jit.Hot)]
    internal static void Release()
    {
        for (var i = 0; i < World.MaxWorlds; i++)
            if (Root.WorldRevisions[i] > Root.RevisionFloor)
                Root.RevisionFloor = Root.WorldRevisions[i];

        NativeHeap.AlignedFree(Root.ZeroDense);
        NativeHeap.Free(Root.Worlds);
        NativeHeap.Free(Root.Stamps);
        NativeHeap.Free(Root.WorldRevisions);
        Root.Worlds = null;
        Root.Stamps = null;
        Root.ZeroDense = null;
        Root.WorldRevisions = null;
        Root.Calls = 0;
    }
}
