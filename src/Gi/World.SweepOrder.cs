using System.Runtime.CompilerServices;
#if NET
using System.Runtime.Intrinsics.X86;
#endif

namespace Gi;

internal struct SweepEntry
{
    public int Slot;
    public float X;
    public float Y;
    public uint Meta;
    public uint Shape;
}

public static unsafe partial class World
{
    private const int SortThreshold = 4096;
    private const int RecordAhead = 8;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static SweepEntry EntryOf(SourceColumns* s, int slot)
        => new()
        {
            Slot = slot,
            X = s->X[slot],
            Y = s->Y[slot],
            Meta = s->Layer[slot] | (uint)s->Stamp[slot] << 8 | (uint)s->Gain[slot] << 16 | (uint)s->Alive[slot] << 24,
            Shape = s->Angle[slot] | (uint)s->Scale[slot] << 16,
        };

    private static int SweepGrid(WorldCtx* w)
    {
        for (var gi = 0; gi < w->GridCount; gi++)
            if (w->Grids[gi].ScaleQ8 != 0) return gi;
        return -1;
    }

    private static int TileKey(WorldCtx* w, GridCtx* g, int gi, SourceColumns* s, int slot)
    {
        int ax;
        int ay;
        if (s->Alive[slot] != 0)
        {
            var mask = w->Recipes[s->Layer[slot]].AnchorMask;
            ax = TileBake.AnchorQ8(s->X[slot], g->OriginX, g->ScaleQ8, mask);
            ay = TileBake.AnchorQ8(s->Y[slot], g->OriginY, g->ScaleQ8, mask);
        }
        else
        {
            var rec = RecordOf(g, slot);
            ax = rec->Ax;
            ay = rec->Ay;
        }

        var cx = Math.Clamp(ax >> 8, 0, g->Size - 1);
        var cy = Math.Clamp(ay >> 8, 0, g->Size - 1);
        return (cy >> TileBake.TileBits) * g->TilesPerSide + (cx >> TileBake.TileBits);
    }

    private static bool OrderByTile(WorldCtx* w)
    {
        var count = w->Changed.Length;
        var gi = SweepGrid(w);
        if (count < SortThreshold || gi < 0) return false;

        var g = w->Grids + gi;
        if (count < g->TileCount >> 1) return false;

        var s = &w->Sources;
        var slots = w->Changed.Pointer;
        w->SweepKeys.ResizeUninitialized(count);
        w->SweepEntries.ResizeUninitialized(count);
        w->SweepCounts.Resize(g->TileCount + 1);
        var keys = w->SweepKeys.Pointer;
        var counts = w->SweepCounts.Pointer;
        new Span<int>(counts, g->TileCount + 1).Clear();
        for (var i = 0; i < count; i++)
        {
            var key = TileKey(w, g, gi, s, slots[i]);
            keys[i] = key;
            counts[key + 1]++;
        }

        for (var tile = 0; tile < g->TileCount; tile++) counts[tile + 1] += counts[tile];

        var entries = w->SweepEntries.Pointer;
        for (var i = 0; i < count; i++)
            entries[counts[keys[i]]++] = EntryOf(s, slots[i]);

        return true;
    }

#if NET
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
#endif
    private static void PrefetchRecord(WorldCtx* w, SweepEntry* entries, int index, int count)
    {
#if NET
        if (!Sse.IsSupported || index >= count) return;

        var slot = entries[index].Slot;
        for (var gi = 0; gi < w->GridCount; gi++)
            Sse.Prefetch0(RecordOf(w->Grids + gi, slot));
#endif
    }
}
