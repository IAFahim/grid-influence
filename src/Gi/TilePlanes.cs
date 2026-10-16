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
}

internal unsafe struct TilePlanes
{
    public int Page;
    public int I;
    public int T;
    public int B;
    public int Summary;
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
    public FixedSlab Int32s;
    public FixedSlab Int64s;
    public FixedSlab Summaries;

    public readonly long Bytes => Pages.Bytes + Int32s.Bytes + Int64s.Bytes + Summaries.Bytes;

    public TilePlanes Take()
    {
        var pageIndex = Pages.Take(PageUnit);
        var page = (short*)Pages.Unit(pageIndex);
        new Span<short>(page, TileBake.TileSize * TileBake.TileSize).Clear();

        var int32Index = Int32s.Take(Int32Unit);
        var i = (int*)Int32s.Unit(int32Index);
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
            Summary = summaryIndex,
        };
    }

    public long* EnsureT(ref TilePlanes tile)
    {
        if (tile.T >= 0) return (long*)Int64s.Unit(tile.T);
        tile.T = TakeInt64();
        return (long*)Int64s.Unit(tile.T);
    }

    public long* EnsureB(ref TilePlanes tile)
    {
        if (tile.B >= 0) return (long*)Int64s.Unit(tile.B);
        tile.B = TakeInt64();
        return (long*)Int64s.Unit(tile.B);
    }

    public readonly PlaneCells Cells(in TilePlanes tile) => new()
    {
        Page = (short*)Pages.Unit(tile.Page),
        I = (int*)Int32s.Unit(tile.I),
        T = tile.T < 0 ? null : (long*)Int64s.Unit(tile.T),
        B = tile.B < 0 ? null : (long*)Int64s.Unit(tile.B),
        Summary = (TileSummary*)Summaries.Unit(tile.Summary),
    };

    public void Give(in TilePlanes tile)
    {
        if (tile.T >= 0) Int64s.Give(tile.T);
        if (tile.B >= 0) Int64s.Give(tile.B);
        Int32s.Give(tile.I);
        Summaries.Give(tile.Summary);
        Pages.Give(tile.Page);
    }

    public void Reset()
    {
        Pages.Reset();
        Int32s.Reset();
        Int64s.Reset();
        Summaries.Reset();
    }

    private int TakeInt64()
    {
        var index = Int64s.Take(Int64Unit);
        new Span<long>(Int64s.Unit(index), TileBake.TileSize * TileBake.TileSize).Clear();
        return index;
    }
}
