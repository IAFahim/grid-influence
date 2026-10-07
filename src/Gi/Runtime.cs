using System.Runtime.CompilerServices;

namespace Gi;

internal static unsafe class Runtime
{
    internal static WorldCtx* Worlds;
    internal static StampVariant* Stamps;
    internal static byte* ZeroDense;

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void Ensure()
    {
        if (Worlds != null) return;

        Worlds = (WorldCtx*)NativeHeap.AllocZeroed((nuint)(World.MaxWorlds * sizeof(WorldCtx)));
        Stamps = (StampVariant*)NativeHeap.AllocZeroed((nuint)(StampCatalog.MaxStamps * sizeof(StampVariant)));
        ZeroDense = (byte*)NativeHeap.AlignedAlloc((nuint)World.DenseBytes);
        new Span<byte>(ZeroDense, World.DenseBytes).Clear();
    }
}
