using System.Runtime.CompilerServices;

[module: SkipLocalsInit]

namespace Gi;

internal static unsafe class Runtime
{
    internal static WorldCtx* Worlds;
    internal static StampVariant* Stamps;
    internal static byte* ZeroPage;
    internal static int* WorldRevisions;

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void Ensure()
    {
        if (Worlds != null) return;

        Worlds = (WorldCtx*)NativeHeap.AllocZeroed((nuint)(World.MaxWorlds * sizeof(WorldCtx)));
        Stamps = (StampVariant*)NativeHeap.AllocZeroed((nuint)(StampCatalog.MaxStamps * sizeof(StampVariant)));
        ZeroPage = (byte*)NativeHeap.AlignedAlloc(World.PageBytes);
        new Span<byte>(ZeroPage, World.PageBytes).Clear();
        WorldRevisions = (int*)NativeHeap.AllocZeroed((nuint)World.MaxWorlds * sizeof(int));
    }
}
