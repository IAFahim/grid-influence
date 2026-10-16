namespace Gi;

internal static unsafe class PlaneFold
{
    internal const long DefaultLimitI = int.MaxValue;
    internal const long DefaultLimitQ40 = 1L << 62;
    private const long Mask40 = (1L << 40) - 1;
    private const int Cells = TileBake.TileSize * TileBake.TileSize;

    public static long LimitI(PlaneStore* store) => store->LimitI == 0 ? DefaultLimitI : store->LimitI;

    public static void ChargeI(PlaneStore* store, TilePlanes* set, long amount)
    {
        var next = set->BoundI + amount;
        if (next > LimitI(store))
        {
            FoldI(store, set);
            next = amount;
        }

        set->BoundI = (int)next;
    }

    public static void ChargeQ40(PlaneStore* store, TilePlanes* set, bool tent, long amount)
    {
        var next = (tent ? set->BoundT : set->BoundB) + amount;
        if (next > (store->LimitQ40 == 0 ? DefaultLimitQ40 : store->LimitQ40))
        {
            FoldQ40(store, set, tent);
            next = amount;
        }

        if (tent) set->BoundT = next;
        else set->BoundB = next;
    }

    public static void AddWideI(PlaneStore* store, TilePlanes* set, int* sums, int value)
    {
        var carry = EnsureCarry(store, set);
        for (var i = 0; i < Cells; i++) carry[i] += (long)sums[i] * value;
    }

    private static void FoldI(PlaneStore* store, TilePlanes* set)
    {
        var carry = EnsureCarry(store, set);
        var plane = (int*)store->Int32s.Unit(set->I);
        for (var i = 0; i < Cells; i++)
        {
            carry[i] += plane[i];
            plane[i] = 0;
        }

        set->BoundI = 0;
    }

    private static void FoldQ40(PlaneStore* store, TilePlanes* set, bool tent)
    {
        EnsureHighLow(store, set, tent);
        var summary = (TileSummary*)store->Summaries.Unit(set->Summary);
        var plane = (long*)store->Int64s.Unit(tent ? set->T : set->B);
        var high = store->Wide(tent ? summary->HighT : summary->HighB);
        var low = store->Wide(tent ? summary->LowT : summary->LowB);
        for (var i = 0; i < Cells; i++)
        {
            var value = plane[i];
            var whole = value >> 40;
            var fraction = low[i] + (value & Mask40);
            if (fraction > Mask40)
            {
                fraction -= Mask40 + 1;
                whole++;
            }

            low[i] = fraction;
            high[i] += whole;
            plane[i] = 0;
        }

        if (tent) set->BoundT = 0;
        else set->BoundB = 0;
    }

    private static long* EnsureCarry(PlaneStore* store, TilePlanes* set)
    {
        var summary = (TileSummary*)store->Summaries.Unit(set->Summary);
        if (summary->CarryI == 0)
        {
            var index = store->TakeWide();
            summary = (TileSummary*)store->Summaries.Unit(set->Summary);
            summary->CarryI = index;
        }

        return store->Wide(summary->CarryI);
    }

    private static void EnsureHighLow(PlaneStore* store, TilePlanes* set, bool tent)
    {
        var summary = (TileSummary*)store->Summaries.Unit(set->Summary);
        if ((tent ? summary->HighT : summary->HighB) != 0) return;

        var high = store->TakeWide();
        var low = store->TakeWide();
        summary = (TileSummary*)store->Summaries.Unit(set->Summary);
        if (tent)
        {
            summary->HighT = high;
            summary->LowT = low;
        }
        else
        {
            summary->HighB = high;
            summary->LowB = low;
        }
    }
}
