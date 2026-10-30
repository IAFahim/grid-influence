using System.Runtime.CompilerServices;

namespace Gi;

internal static unsafe partial class TileBake
{
    private const long HalfQ40 = 1L << 39;
    private const long FractionMask = (1L << 40) - 1;

    [MethodImpl(Jit.Inline)]
    internal static long RoundWide(long high, long low)
    {
        var whole = high + (low >> 40);
        var fraction = low & FractionMask;
        return whole + (whole >= 0 ? fraction >= HalfQ40 ? 1 : 0 : fraction > HalfQ40 ? 1 : 0);
    }

    [MethodImpl(Jit.Hot)]
    private static ResolveOutcome PublishWide(PlaneStore* store, TilePlanes* tile, int x0, int y0, int x1, int y1,
        int tick)
    {
        var t = store->Cells(*tile);
        if ((x1 - x0) * (y1 - y0) >= PublishWholeTile)
        {
            x0 = 0;
            y0 = 0;
            x1 = TileSize;
            y1 = TileSize;
        }

        var summary = t.Summary;
        var carry = summary->CarryI == 0 ? null : store->Wide(summary->CarryI);
        var highT = summary->HighT == 0 ? null : store->Wide(summary->HighT);
        var lowT = summary->LowT == 0 ? null : store->Wide(summary->LowT);
        var highB = summary->HighB == 0 ? null : store->Wide(summary->HighB);
        var lowB = summary->LowB == 0 ? null : store->Wide(summary->LowB);
        var changed = false;
        for (var y = y0; y < y1; y++)
        for (var x = x0; x < x1; x++)
        {
            var i = y * TileSize + x;
            var total = t.I[i] + (carry == null ? 0 : carry[i]);
            if (t.T != null) total += RoundWide(highT == null ? 0 : highT[i], (lowT == null ? 0 : lowT[i]) + t.T[i]);
            if (t.B != null) total += RoundWide(highB == null ? 0 : highB[i], (lowB == null ? 0 : lowB[i]) + t.B[i]);
            var cell = (short)Math.Clamp(total, short.MinValue, short.MaxValue);
            if (cell != t.Page[i]) changed = true;
            t.Page[i] = cell;
        }

        UpdateSummaries(t, x0, y0, x1, y1);
        if (changed) t.Summary->Epoch = tick;
        return Outcome(Occupied(t.Summary), changed);
    }
}
