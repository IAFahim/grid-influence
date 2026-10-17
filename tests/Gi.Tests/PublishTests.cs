using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using Xunit;

namespace Gi.Tests;

public enum PublishPath
{
    Dispatch,
    Scalar,
    Vector128,
    Vector256,
}

public sealed unsafe class PublishTests
{
    private const int Size = TileBake.TileSize;
    private const int Cells = Size * Size;

    private static readonly int[] EdgeInts =
    {
        int.MinValue, int.MaxValue, 1 << 29, -(1 << 29), (1 << 29) - 1, -(1 << 29) - 1,
        1 << 30, -(1 << 30), 0, 1, -1, 17,
    };

    private static readonly long[] EdgeLongs =
    {
        long.MinValue, long.MaxValue, 0, 1, -1, 1L << 40, -(1L << 40), 2L << 40,
        (1L << 51) - 1, -(1L << 51), 1L << 62, -(1L << 62),
    };

    public enum Regime
    {
        Small,
        NearBound,
        FullInt32,
        MixedEdges,
    }

    public static TheoryData<Regime, int, bool, bool, PublishPath> RandomPlanes()
    {
        var data = new TheoryData<Regime, int, bool, bool, PublishPath>();
        foreach (Regime regime in Enum.GetValues<Regime>())
        foreach (var seed in new[] { 1, 2, 3 })
        foreach (var tent in new[] { false, true })
        foreach (var bell in new[] { false, true })
        foreach (PublishPath path in Enum.GetValues<PublishPath>())
            data.Add(regime, seed, tent, bell, path);
        return data;
    }

    public static TheoryData<PublishPath> Paths()
    {
        var data = new TheoryData<PublishPath>();
        data.Add(PublishPath.Dispatch);
        data.Add(PublishPath.Scalar);
        data.Add(PublishPath.Vector128);
        data.Add(PublishPath.Vector256);
        return data;
    }

    private static bool Supported(PublishPath path) => path switch
    {
        PublishPath.Vector128 => Sse2.IsSupported || AdvSimd.IsSupported,
        PublishPath.Vector256 => Avx2.IsSupported,
        _ => true,
    };

    private sealed class Model
    {
        public int[] I = new int[Cells];
        public long[]? T;
        public long[]? B;
        public short[] Page = new short[Cells];
        public long Sum;
        public short Max;
        public short ArgMaxX;
        public short ArgMaxY;
        public int Epoch;
        public int[] RowSums = new int[Size];
        public short[] RowMaxima = new short[Size];
    }

    private static (bool Changed, bool Any) Apply(Model m, int x0, int y0, int x1, int y1, int tick)
    {
        if ((x1 - x0) * (y1 - y0) >= Size * Size / 2)
        {
            x0 = 0;
            y0 = 0;
            x1 = Size;
            y1 = Size;
        }

        var changed = false;
        unchecked
        {
            for (var y = y0; y < y1; y++)
            for (var x = x0; x < x1; x++)
            {
                var i = y * Size + x;
                var total = (long)m.I[i]
                    + (m.T != null ? TileBake.RoundQ40(m.T[i]) : 0)
                    + (m.B != null ? TileBake.RoundQ40(m.B[i]) : 0);
                var cell = (short)Math.Clamp(total, short.MinValue, short.MaxValue);
                if (cell != m.Page[i]) changed = true;
                m.Page[i] = cell;
            }
        }

        var any = false;
        for (var i = 0; i < Cells; i++) any |= m.Page[i] != 0;

        for (var y = y0; y < y1; y++)
        {
            var sum = 0;
            var max = short.MinValue;
            for (var x = 0; x < Size; x++)
            {
                var cell = m.Page[y * Size + x];
                sum += cell;
                if (cell > max) max = cell;
            }

            m.RowSums[y] = sum;
            m.RowMaxima[y] = max;
        }

        m.Sum = 0;
        for (var y = 0; y < Size; y++) m.Sum += m.RowSums[y];

        m.Max = m.Page[0];
        m.ArgMaxX = 0;
        m.ArgMaxY = 0;
        for (var i = 0; i < Cells; i++)
            if (m.Page[i] > m.Max)
            {
                m.Max = m.Page[i];
                m.ArgMaxX = (short)(i % Size);
                m.ArgMaxY = (short)(i / Size);
            }

        if (changed) m.Epoch = tick;
        return (changed, any);
    }

    private sealed class Tile
    {
        public PlaneStore Store;
        public TilePlanes Plane;

        public static Tile Of(Model m)
        {
            var tile = new Tile();
            tile.Plane = tile.Store.Take();
            tile.Sync(m);
            return tile;
        }

        public void Sync(Model m)
        {
            var cells = Store.Cells(Plane);
            fixed (int* source = m.I)
                Buffer.MemoryCopy(source, cells.I, PlaneStore.Int32Unit, PlaneStore.Int32Unit);
            if (m.T != null)
            {
                var tent = Store.EnsureT(ref Plane);
                fixed (long* source = m.T)
                    Buffer.MemoryCopy(source, tent, PlaneStore.Int64Unit, PlaneStore.Int64Unit);
            }

            if (m.B != null)
            {
                var bell = Store.EnsureB(ref Plane);
                fixed (long* source = m.B)
                    Buffer.MemoryCopy(source, bell, PlaneStore.Int64Unit, PlaneStore.Int64Unit);
            }
        }

        public void Release()
        {
            Store.Give(Plane);
            Store.Reset();
        }
    }

    private static ResolveOutcome Invoke(PublishPath path, Tile tile, int x0, int y0, int x1, int y1, int tick)
    {
        fixed (TilePlanes* planes = &tile.Plane)
        fixed (PlaneStore* store = &tile.Store)
        {
            return path switch
            {
#if !GI_NETSTANDARD
                PublishPath.Vector256 => TileBake.Publish256(store->Cells(*planes), x0, y0, x1, y1, tick),
                PublishPath.Vector128 => TileBake.Publish128(store->Cells(*planes), x0, y0, x1, y1, tick),
#endif
                PublishPath.Scalar => TileBake.PublishScalar(store->Cells(*planes), x0, y0, x1, y1, tick),
                _ => TileBake.Publish(store, planes, x0, y0, x1, y1, tick),
            };
        }
    }

    private static void AssertMatches(Tile tile, Model m, ResolveOutcome outcome, (bool Changed, bool Any) expected)
    {
        var cells = tile.Store.Cells(tile.Plane);
        for (var i = 0; i < Cells; i++) Assert.Equal(m.Page[i], cells.Page[i]);

        var summary = cells.Summary;
        Assert.Equal(m.Sum, summary->Sum);
        Assert.Equal(m.Max, summary->Max);
        Assert.Equal(m.ArgMaxX, summary->ArgMaxX);
        Assert.Equal(m.ArgMaxY, summary->ArgMaxY);
        Assert.Equal(m.Epoch, summary->Epoch);
        for (var y = 0; y < Size; y++)
        {
            Assert.Equal(m.RowSums[y], summary->RowSums[y]);
            Assert.Equal(m.RowMaxima[y], summary->RowMaxima[y]);
        }

        Assert.Equal(expected.Changed, (outcome & ResolveOutcome.Changed) != 0);
        Assert.Equal(expected.Any, (outcome & ResolveOutcome.Empty) == 0);
    }

    private static void Seed(Random random, Regime regime, Model m, bool tent, bool bell)
    {
        if (tent) m.T = new long[Cells];
        if (bell) m.B = new long[Cells];
        unchecked
        {
            for (var i = 0; i < Cells; i++)
            {
                m.I[i] = NextInt(random, regime);
                if (tent) m.T![i] = NextLong(random, regime);
                if (bell) m.B![i] = NextLong(random, regime);
            }
        }
    }

    private static int NextInt(Random random, Regime regime) => regime switch
    {
        Regime.Small => random.Next(-1000, 1001),
        Regime.NearBound => random.Next(-(1 << 29), (1 << 29) + 1),
        Regime.FullInt32 => random.Next(int.MinValue, int.MaxValue),
        _ => EdgeInts[random.Next(EdgeInts.Length)],
    };

    private static long NextLong(Random random, Regime regime) => regime switch
    {
        Regime.Small => random.NextInt64(-(1L << 40), (1L << 40) + 1),
        Regime.NearBound => random.NextInt64(-(1L << 51), (1L << 51) + 1),
        Regime.FullInt32 => random.NextInt64(),
        _ => EdgeLongs[random.Next(EdgeLongs.Length)],
    };

    [Theory]
    [MemberData(nameof(RandomPlanes))]
    public void FullTileThenConfinedRect_MatchesResolveAndModel(Regime regime, int seed, bool tent, bool bell, PublishPath path)
    {
        if (!Supported(path)) return;

        var random = new Random((int)regime * 7919 + seed * 104729);
        var m = new Model();
        Seed(random, regime, m, tent, bell);
        var tile = Tile.Of(m);

        var expected = Apply(m, 0, 0, Size, Size, 1);
        var outcome = Invoke(path, tile, 0, 0, Size, Size, 1);
        AssertMatches(tile, m, outcome, expected);

        var x0 = random.Next(Size);
        var y0 = random.Next(Size);
        var x1 = Math.Min(Size, x0 + random.Next(1, Size + 1));
        var y1 = Math.Min(Size, y0 + random.Next(1, Size + 1));
        unchecked
        {
            for (var y = 0; y < Size; y++)
            for (var x = 0; x < Size; x++)
            {
                if (x < x0 || x >= x1 || y < y0 || y >= y1) continue;
                m.I[y * Size + x] = NextInt(random, regime);
                if (tent) m.T![y * Size + x] = NextLong(random, regime);
                if (bell) m.B![y * Size + x] = NextLong(random, regime);
            }
        }

        tile.Sync(m);
        expected = Apply(m, x0, y0, x1, y1, 2);
        outcome = Invoke(path, tile, x0, y0, x1, y1, 2);
        AssertMatches(tile, m, outcome, expected);

        tile.Release();
    }

    [Theory]
    [MemberData(nameof(RandomPlanes))]
    public void RandomRectSequences_MatchModel_AcrossPaths(Regime regime, int seed, bool tent, bool bell, PublishPath path)
    {
        if (!Supported(path)) return;

        var random = new Random((int)regime * 104729 + seed * 7919);
        var m = new Model();
        Seed(random, regime, m, tent, bell);
        var tile = Tile.Of(m);

        var reference = new Model();
        Seed(new Random((int)regime * 104729 + seed * 7919), regime, reference, tent, bell);
        var referenceTile = Tile.Of(reference);
        for (var step = 1; step <= 8; step++)
        {
            var x0 = random.Next(Size);
            var y0 = random.Next(Size);
            var x1 = Math.Min(Size, x0 + random.Next(1, Size + 1));
            var y1 = Math.Min(Size, y0 + random.Next(1, Size + 1));
            for (var c = 0; c < 4; c++)
            {
                var cx = random.Next(Size);
                var cy = random.Next(Size);
                var di = NextInt(random, regime);
                var dt = tent && random.Next(2) == 0 ? NextLong(random, regime) : 0;
                var db = bell && random.Next(2) == 0 ? NextLong(random, regime) : 0;
                m.I[cy * Size + cx] = di;
                if (tent) m.T![cy * Size + cx] = dt;
                if (bell) m.B![cy * Size + cx] = db;
                reference.I[cy * Size + cx] = di;
                if (tent) reference.T![cy * Size + cx] = dt;
                if (bell) reference.B![cy * Size + cx] = db;
            }

            tile.Sync(m);
            referenceTile.Sync(reference);
            var expected = Apply(m, x0, y0, x1, y1, step);
            var outcome = Invoke(path, tile, x0, y0, x1, y1, step);
            AssertMatches(tile, m, outcome, expected);

            var referencePath = path == PublishPath.Scalar && Supported(PublishPath.Vector256)
                ? PublishPath.Vector256
                : PublishPath.Scalar;
            var referenceOutcome = Invoke(referencePath, referenceTile, x0, y0, x1, y1, step);
            Assert.Equal(outcome, referenceOutcome);
            Apply(reference, x0, y0, x1, y1, step);
        }

        AssertTilesEqual(tile, referenceTile);
        tile.Release();
        referenceTile.Release();
    }

    private static void AssertTilesEqual(Tile left, Tile right)
    {
        var a = left.Store.Cells(left.Plane);
        var b = right.Store.Cells(right.Plane);
        for (var i = 0; i < Cells; i++) Assert.Equal(a.Page[i], b.Page[i]);

        Assert.Equal(a.Summary->Sum, b.Summary->Sum);
        Assert.Equal(a.Summary->Max, b.Summary->Max);
        Assert.Equal(a.Summary->ArgMaxX, b.Summary->ArgMaxX);
        Assert.Equal(a.Summary->ArgMaxY, b.Summary->ArgMaxY);
        Assert.Equal(a.Summary->Epoch, b.Summary->Epoch);
        for (var y = 0; y < Size; y++)
        {
            Assert.Equal(a.Summary->RowSums[y], b.Summary->RowSums[y]);
            Assert.Equal(a.Summary->RowMaxima[y], b.Summary->RowMaxima[y]);
        }
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public void HalfTileRect_PublishesTheWholeTile(PublishPath path)
    {
        if (!Supported(path)) return;

        var m = new Model();
        m.I[10 * Size + 10] = 40000;
        var tile = Tile.Of(m);
        Apply(m, 0, 0, Size, Size, 1);
        Invoke(path, tile, 0, 0, Size, Size, 1);

        m.I[20 * Size + 20] = 70000;
        m.I[2 * Size + 2] = -50000;
        tile.Sync(m);
        var expected = Apply(m, 0, 0, Size / 2, Size, 2);
        var outcome = Invoke(path, tile, 0, 0, Size / 2, Size, 2);
        AssertMatches(tile, m, outcome, expected);
        Assert.Equal((short)32767, tile.Store.Cells(tile.Plane).Page[20 * Size + 20]);
        Assert.Equal((short)-32768, tile.Store.Cells(tile.Plane).Page[2 * Size + 2]);

        m.I[21 * Size + 21] = 90000;
        tile.Sync(m);
        expected = Apply(m, Size / 2, 0, Size, Size, 3);
        outcome = Invoke(path, tile, Size / 2, 0, Size, Size, 3);
        AssertMatches(tile, m, outcome, expected);

        tile.Release();
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public void NarrowRect_LeavesCellsOutsideTheRectangleStale(PublishPath path)
    {
        if (!Supported(path)) return;

        var m = new Model();
        m.I[5 * Size + 5] = 30000;
        var tile = Tile.Of(m);
        Apply(m, 0, 0, Size, Size, 1);
        Invoke(path, tile, 0, 0, Size, Size, 1);

        m.I[5 * Size + 5] = -30000;
        m.I[25 * Size + 25] = 60000;
        tile.Sync(m);
        var expected = Apply(m, 0, 0, 4, 4, 2);
        var outcome = Invoke(path, tile, 0, 0, 4, 4, 2);
        AssertMatches(tile, m, outcome, expected);

        var cells = tile.Store.Cells(tile.Plane);
        Assert.Equal((short)30000, cells.Page[5 * Size + 5]);
        Assert.Equal((short)0, cells.Page[25 * Size + 25]);

        tile.Release();
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public void RepublishWithoutChange_KeepsEpochAndReportsNoChange(PublishPath path)
    {
        if (!Supported(path)) return;

        var m = new Model();
        m.I[7 * Size + 7] = 12000;
        m.T = new long[Cells];
        m.T[8 * Size + 8] = 3L << 40;
        var tile = Tile.Of(m);

        var expected = Apply(m, 0, 0, Size, Size, 5);
        var outcome = Invoke(path, tile, 0, 0, Size, Size, 5);
        AssertMatches(tile, m, outcome, expected);
        Assert.Equal(5, tile.Store.Cells(tile.Plane).Summary->Epoch);

        outcome = Invoke(path, tile, 0, 0, Size, Size, 6);
        Assert.False((outcome & ResolveOutcome.Changed) != 0);
        Assert.Equal(5, tile.Store.Cells(tile.Plane).Summary->Epoch);

        m.I[7 * Size + 7] = 13000;
        tile.Sync(m);
        expected = Apply(m, 7, 7, 8, 8, 7);
        outcome = Invoke(path, tile, 7, 7, 8, 8, 7);
        AssertMatches(tile, m, outcome, expected);
        Assert.Equal(7, tile.Store.Cells(tile.Plane).Summary->Epoch);

        tile.Release();
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public void ArgMaxMaintenance_HandlesRaiseDecreaseAndTies(PublishPath path)
    {
        if (!Supported(path)) return;

        var m = new Model();
        m.I[10 * Size + 10] = 1000;
        m.I[12 * Size + 12] = 1000;
        var tile = Tile.Of(m);
        Apply(m, 0, 0, Size, Size, 1);
        Invoke(path, tile, 0, 0, Size, Size, 1);
        var summary = tile.Store.Cells(tile.Plane).Summary;
        Assert.Equal((short)1000, summary->Max);
        Assert.Equal((short)10, summary->ArgMaxX);
        Assert.Equal((short)10, summary->ArgMaxY);

        m.I[5 * Size + 5] = 1500;
        tile.Sync(m);
        var expected = Apply(m, 4, 4, 8, 8, 2);
        var outcome = Invoke(path, tile, 4, 4, 8, 8, 2);
        AssertMatches(tile, m, outcome, expected);

        m.I[5 * Size + 5] = 0;
        tile.Sync(m);
        expected = Apply(m, 4, 4, 8, 8, 3);
        outcome = Invoke(path, tile, 4, 4, 8, 8, 3);
        AssertMatches(tile, m, outcome, expected);

        m.I[10 * Size + 10] = 0;
        m.I[3 * Size + 3] = 1200;
        tile.Sync(m);
        expected = Apply(m, 0, 0, 12, 12, 4);
        outcome = Invoke(path, tile, 0, 0, 12, 12, 4);
        AssertMatches(tile, m, outcome, expected);

        m.I[2 * Size + 2] = 1200;
        tile.Sync(m);
        expected = Apply(m, 2, 2, 3, 3, 5);
        outcome = Invoke(path, tile, 2, 2, 3, 3, 5);
        AssertMatches(tile, m, outcome, expected);
        summary = tile.Store.Cells(tile.Plane).Summary;
        Assert.Equal((short)1200, summary->Max);
        Assert.Equal((short)2, summary->ArgMaxX);
        Assert.Equal((short)2, summary->ArgMaxY);

        m.I[30 * Size + 30] = 1200;
        tile.Sync(m);
        expected = Apply(m, 29, 29, 31, 31, 6);
        outcome = Invoke(path, tile, 29, 29, 31, 31, 6);
        AssertMatches(tile, m, outcome, expected);
        summary = tile.Store.Cells(tile.Plane).Summary;
        Assert.Equal((short)2, summary->ArgMaxX);
        Assert.Equal((short)2, summary->ArgMaxY);

        for (var i = 0; i < Cells; i++) m.I[i] = -10;
        tile.Sync(m);
        expected = Apply(m, 0, 0, Size, Size, 7);
        outcome = Invoke(path, tile, 0, 0, Size, Size, 7);
        AssertMatches(tile, m, outcome, expected);
        summary = tile.Store.Cells(tile.Plane).Summary;
        Assert.Equal((short)-10, summary->Max);
        Assert.Equal((short)0, summary->ArgMaxX);
        Assert.Equal((short)0, summary->ArgMaxY);

        tile.Release();
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public void EdgePlanes_MatchResolveAndModel(PublishPath path)
    {
        if (!Supported(path)) return;

        var m = new Model();
        m.T = new long[Cells];
        m.B = new long[Cells];
        for (var i = 0; i < Cells; i++)
        {
            m.I[i] = EdgeInts[(i * 7 + 3) % EdgeInts.Length];
            m.T[i] = EdgeLongs[i % EdgeLongs.Length];
            m.B[i] = EdgeLongs[(i * 5 + 7) % EdgeLongs.Length];
        }

        var tile = Tile.Of(m);
        var expected = Apply(m, 0, 0, Size, Size, 1);
        var outcome = Invoke(path, tile, 0, 0, Size, Size, 1);
        AssertMatches(tile, m, outcome, expected);

        for (var y = 17; y < 26; y++)
        for (var x = 3; x < 29; x++)
            m.I[y * Size + x] = EdgeInts[((y * Size + x) * 11 + 5) % EdgeInts.Length];
        tile.Sync(m);
        expected = Apply(m, 3, 17, 29, 26, 2);
        outcome = Invoke(path, tile, 3, 17, 29, 26, 2);
        AssertMatches(tile, m, outcome, expected);

        tile.Release();
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public void FreshTile_PublishesZeroPageWithZeroSummaries(PublishPath path)
    {
        if (!Supported(path)) return;

        var m = new Model();
        var tile = Tile.Of(m);
        var expected = Apply(m, 0, 0, Size, Size, 1);
        var outcome = Invoke(path, tile, 0, 0, Size, Size, 1);
        AssertMatches(tile, m, outcome, expected);

        var summary = tile.Store.Cells(tile.Plane).Summary;
        Assert.Equal(0L, summary->Sum);
        Assert.Equal((short)0, summary->Max);
        Assert.Equal((short)0, summary->ArgMaxX);
        Assert.Equal((short)0, summary->ArgMaxY);
        Assert.Equal(0, summary->Epoch);

        tile.Release();
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public void SmoothPlanes_AttachOnFirstUseAndRepublish(PublishPath path)
    {
        if (!Supported(path)) return;

        var m = new Model();
        m.I[6 * Size + 6] = 500;
        var tile = Tile.Of(m);
        var expected = Apply(m, 0, 0, Size, Size, 1);
        var outcome = Invoke(path, tile, 0, 0, Size, Size, 1);
        AssertMatches(tile, m, outcome, expected);

        m.T = new long[Cells];
        m.T[6 * Size + 6] = 2L << 40;
        tile.Sync(m);
        expected = Apply(m, 0, 0, Size, Size, 2);
        outcome = Invoke(path, tile, 0, 0, Size, Size, 2);
        AssertMatches(tile, m, outcome, expected);

        m.B = new long[Cells];
        m.B[6 * Size + 6] = -(3L << 40);
        tile.Sync(m);
        expected = Apply(m, 5, 5, 7, 7, 3);
        outcome = Invoke(path, tile, 5, 5, 7, 7, 3);
        AssertMatches(tile, m, outcome, expected);

        tile.Release();
    }
}
