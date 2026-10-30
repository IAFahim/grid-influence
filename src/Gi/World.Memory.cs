using System.Runtime.CompilerServices;
namespace Gi;

public static unsafe partial class World
{
    [MethodImpl(Jit.Hot)]
    public static void Memory(
        byte world, out long storeBytes, out int liveTiles, out int pages, out int kernelEntries, out int liveSources)
    {
        storeBytes = 0;
        liveTiles = 0;
        pages = 0;
        kernelEntries = 0;
        liveSources = 0;
        var w = GetContext(world);
        if (w == null) return;

        for (var gi = 0; gi < w->GridCount; gi++)
        {
            var g = w->Grids + gi;
            for (var li = 0; li < w->LayerCount; li++)
            {
                var ld = g->Layers + li;
                var store = &ld->Store;
                storeBytes += store->Bytes;
                liveTiles += ld->LivePages;
                pages += store->Pages.LiveCount + store->Int32Planes.LiveCount +
                         store->Int64Planes.LiveCount + store->Summaries.LiveCount;
            }
        }

        kernelEntries = w->Kernels.LiveEntries;
        var s = &w->Sources;
        for (var i = 0; i < s->Count; i++)
            if (s->Alive[i] != 0)
                liveSources++;
    }
}
