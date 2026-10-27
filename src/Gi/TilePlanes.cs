using System.Runtime.CompilerServices;
namespace Gi;

internal unsafe struct TileSummary
{
    public const int Bytes = 256;

    public long Sum;
    public short Max;
    public short ArgMaxX;
    public short ArgMaxY;
    public int Epoch;
    public fixed int RowSums[TileBake.TileSize];
    public fixed short RowMaxima[TileBake.TileSize];
    public int CarryI;
    public int HighT;
    public int LowT;
    public int HighB;
    public int LowB;
}

internal struct TilePlanes
{
    public int Page;
    public int I;
    public int T;
    public int B;
    public int Summary;
    public int BoundI;
    public byte Stale;
    public long BoundT;
    public long BoundB;
}

internal unsafe struct PlaneCells
{
    public short* Page;
    public int* I;
    public long* T;
    public long* B;
    public TileSummary* Summary;
}

internal unsafe struct PlaneStore
{
    public const int PageUnit = TileBake.TileSize * TileBake.TileSize * sizeof(short);
    public const int Int32Unit = TileBake.TileSize * TileBake.TileSize * sizeof(int);
    public const int Int64Unit = TileBake.TileSize * TileBake.TileSize * sizeof(long);

    public FixedSlab Pages;
    public FixedSlab Int32Planes;
    public FixedSlab Int64Planes;
    public FixedSlab Summaries;
    public long LimitI;
    public long LimitQ40;

    public readonly long Bytes => Pages.Bytes + Int32Planes.Bytes + Int64Planes.Bytes + Summaries.Bytes;

    [MethodImpl(Jit.Hot)]
    public TilePlanes Take()
    {
        var pageIndex = Pages.Take(PageUnit);
        var page = (short*)Pages.Unit(pageIndex);
        new Span<short>(page, TileBake.TileSize * TileBake.TileSize).Clear();

        var int32Index = Int32Planes.Take(Int32Unit);
        var i = (int*)Int32Planes.Unit(int32Index);
        new Span<int>(i, TileBake.TileSize * TileBake.TileSize).Clear();

        var summaryIndex = Summaries.Take(TileSummary.Bytes);
        var summary = (TileSummary*)Summaries.Unit(summaryIndex);
        new Span<byte>(summary, TileSummary.Bytes).Clear();

        return new TilePlanes
        {
            Page = pageIndex,
            I = int32Index,
            T = -1,
            B = -1,
            Summary = summaryIndex
        };
    }

    [MethodImpl(Jit.Hot)]
    public TilePlanes TakeDerived()
    {
        var pageIndex = Pages.Take(PageUnit);
        var page = (short*)Pages.Unit(pageIndex);
        new Span<short>(page, TileBake.TileSize * TileBake.TileSize).Clear();

        var summaryIndex = Summaries.Take(TileSummary.Bytes);
        var summary = (TileSummary*)Summaries.Unit(summaryIndex);
        new Span<byte>(summary, TileSummary.Bytes).Clear();

        return new TilePlanes
        {
            Page = pageIndex,
            I = -1,
            T = -1,
            B = -1,
            Summary = summaryIndex
        };
    }

    [MethodImpl(Jit.Hot)]
    public long* EnsureT(ref TilePlanes tile)
    {
        if (tile.T >= 0) return (long*)Int64Planes.Unit(tile.T);
        tile.T = TakeInt64();
        return (long*)Int64Planes.Unit(tile.T);
    }

    [MethodImpl(Jit.Hot)]
    public long* EnsureB(ref TilePlanes tile)
    {
        if (tile.B >= 0) return (long*)Int64Planes.Unit(tile.B);
        tile.B = TakeInt64();
        return (long*)Int64Planes.Unit(tile.B);
    }

    [MethodImpl(Jit.Hot)]
    public readonly bool PlanesZero(in TilePlanes tile)
    {
        var summary = (TileSummary*)Summaries.Unit(tile.Summary);
        if ((summary->CarryI | summary->HighT | summary->HighB) != 0) return TotalsZero(tile, summary);

        const int count = TileBake.TileSize * TileBake.TileSize;
        if (tile.I >= 0 && !TileBake.AllZero((int*)Int32Planes.Unit(tile.I), count)) return false;
        if (tile.T >= 0 && !TileBake.AllZero((long*)Int64Planes.Unit(tile.T), count)) return false;
        return tile.B < 0 || TileBake.AllZero((long*)Int64Planes.Unit(tile.B), count);
    }

    [MethodImpl(Jit.Hot)]
    private readonly bool TotalsZero(in TilePlanes tile, TileSummary* summary)
    {
        var i32 = tile.I < 0 ? null : (int*)Int32Planes.Unit(tile.I);
        var tent = tile.T < 0 ? null : (long*)Int64Planes.Unit(tile.T);
        var bell = tile.B < 0 ? null : (long*)Int64Planes.Unit(tile.B);
        var carry = summary->CarryI == 0 ? null : Wide(summary->CarryI);
        var highT = summary->HighT == 0 ? null : Wide(summary->HighT);
        var lowT = summary->LowT == 0 ? null : Wide(summary->LowT);
        var highB = summary->HighB == 0 ? null : Wide(summary->HighB);
        var lowB = summary->LowB == 0 ? null : Wide(summary->LowB);
        for (var i = 0; i < TileBake.TileSize * TileBake.TileSize; i++)
        {
            if ((i32 == null ? 0 : i32[i]) + (carry == null ? 0 : carry[i]) != 0) return false;
            if (!Q40Zero(highT == null ? 0 : highT[i],
                    (lowT == null ? 0 : lowT[i]) + (tent == null ? 0 : tent[i]))) return false;
            if (!Q40Zero(highB == null ? 0 : highB[i],
                    (lowB == null ? 0 : lowB[i]) + (bell == null ? 0 : bell[i]))) return false;
        }

        return true;
    }

    [MethodImpl(Jit.Hot)]
    private static bool Q40Zero(long high, long low)
    {
        return high + (low >> 40) == 0 && (low & ((1L << 40) - 1)) == 0;
    }

    [MethodImpl(Jit.Inline)]
    public readonly PlaneCells Cells(in TilePlanes tile)
    {
        return new PlaneCells
        {
            Page = (short*)Pages.Unit(tile.Page),
            I = tile.I < 0 ? null : (int*)Int32Planes.Unit(tile.I),
            T = tile.T < 0 ? null : (long*)Int64Planes.Unit(tile.T),
            B = tile.B < 0 ? null : (long*)Int64Planes.Unit(tile.B),
            Summary = (TileSummary*)Summaries.Unit(tile.Summary)
        };
    }

    [MethodImpl(Jit.Hot)]
    public void Give(in TilePlanes tile)
    {
        var summary = (TileSummary*)Summaries.Unit(tile.Summary);
        GiveWide(summary->CarryI);
        GiveWide(summary->HighT);
        GiveWide(summary->LowT);
        GiveWide(summary->HighB);
        GiveWide(summary->LowB);
        if (tile.T >= 0) Int64Planes.Give(tile.T);
        if (tile.B >= 0) Int64Planes.Give(tile.B);
        if (tile.I >= 0) Int32Planes.Give(tile.I);
        Summaries.Give(tile.Summary);
        Pages.Give(tile.Page);
    }

    [MethodImpl(Jit.Hot)]
    public void Reset()
    {
        Pages.Reset();
        Int32Planes.Reset();
        Int64Planes.Reset();
        Summaries.Reset();
    }

    [MethodImpl(Jit.Hot)]
    public int TakeWide()
    {
        return TakeInt64() + 1;
    }

    [MethodImpl(Jit.Hot)]
    public void ClearWide(TilePlanes* tile)
    {
        var summary = (TileSummary*)Summaries.Unit(tile->Summary);
        GiveWide(summary->CarryI);
        GiveWide(summary->HighT);
        GiveWide(summary->LowT);
        GiveWide(summary->HighB);
        GiveWide(summary->LowB);
        summary->CarryI = 0;
        summary->HighT = 0;
        summary->LowT = 0;
        summary->HighB = 0;
        summary->LowB = 0;
        tile->BoundI = 0;
        tile->BoundT = 0;
        tile->BoundB = 0;
    }

    [MethodImpl(Jit.Hot)]
    public readonly long* Wide(int index)
    {
        return (long*)Int64Planes.Unit(index - 1);
    }

    [MethodImpl(Jit.Hot)]
    private void GiveWide(int index)
    {
        if (index != 0) Int64Planes.Give(index - 1);
    }

    [MethodImpl(Jit.Hot)]
    private int TakeInt64()
    {
        var index = Int64Planes.Take(Int64Unit);
        new Span<long>(Int64Planes.Unit(index), TileBake.TileSize * TileBake.TileSize).Clear();
        return index;
    }
}
