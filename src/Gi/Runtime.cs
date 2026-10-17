using System.Runtime.CompilerServices;

[module: SkipLocalsInit]

namespace Gi;

internal static unsafe class Runtime
{
    internal static WorldCtx* Worlds;
    internal static StampVariant* Stamps;
    internal static byte* ZeroDense;
    internal static int* WorldRevisions;
    internal static int RevisionFloor;

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void Ensure()
    {
        if (Worlds != null) return;

        Worlds = (WorldCtx*)NativeHeap.AllocZeroed((nuint)(World.MaxWorlds * sizeof(WorldCtx)));
        Stamps = (StampVariant*)NativeHeap.AllocZeroed((nuint)(StampCatalog.MaxStamps * sizeof(StampVariant)));
        ZeroDense = (byte*)NativeHeap.AlignedAlloc(World.PageBytes);
        new Span<byte>(ZeroDense, World.PageBytes).Clear();
        WorldRevisions = (int*)NativeHeap.AllocZeroed((nuint)World.MaxWorlds * sizeof(int));
        new Span<int>(WorldRevisions, World.MaxWorlds).Fill(RevisionFloor);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void Release()
    {
        for (var i = 0; i < World.MaxWorlds; i++)
            if (WorldRevisions[i] > RevisionFloor) RevisionFloor = WorldRevisions[i];

        NativeHeap.AlignedFree(ZeroDense);
        NativeHeap.Free(Worlds);
        NativeHeap.Free(Stamps);
        NativeHeap.Free(WorldRevisions);
        Worlds = null;
        Stamps = null;
        ZeroDense = null;
        WorldRevisions = null;
        NativeHeap.Calls = 0;
    }
}
