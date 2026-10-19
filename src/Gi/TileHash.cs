namespace Gi;

internal static unsafe class TileHash
{
    internal const ulong Offset = 0xcbf29ce484222325ul;
    internal const ulong Prime = 0x100000001b3ul;

    internal static ulong Of(int tile, short* page)
    {
        var words = (ulong*)page;
        var hash = (Offset ^ (ulong)tile) * Prime;
        for (var i = 0; i < World.PageBytes / sizeof(ulong); i++) hash = (hash ^ words[i]) * Prime;
        return hash;
    }
}
