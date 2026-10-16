using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using Xunit;

namespace Gi.Tests;

public enum ResolvePath
{
    Dispatch,
    Scalar,
    Vector128,
    Vector256,
}

public sealed unsafe class ResolveTests
{
    private const int Size = TileBake.TileSize;
    private const int Cells = Size * Size;
    private const int Pitch = TileBake.DiffPitch;
    private const int Rows = TileBake.DiffRows;

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

    public static TheoryData<ResolvePath> Paths()
    {
        var data = new TheoryData<ResolvePath>();
        data.Add(ResolvePath.Dispatch);
        data.Add(ResolvePath.Scalar);
        data.Add(ResolvePath.Vector128);
        data.Add(ResolvePath.Vector256);
        return data;
    }

    private static bool Supported(ResolvePath path) => path switch
    {
        ResolvePath.Vector128 => Sse2.IsSupported || AdvSimd.IsSupported,
        ResolvePath.Vector256 => Avx2.IsSupported,
        _ => true,
    };

    private static bool Invoke(ResolvePath path, int* difference, int* dense, int* previousRow,
        long* tent, long* bell, short* output, long* pageSum, short* pageMax) => path switch
    {
        ResolvePath.Vector128 => TileBake.Resolve128(difference, dense, previousRow, tent, bell, output, pageSum, pageMax),
        ResolvePath.Vector256 => TileBake.Resolve256(difference, dense, previousRow, tent, bell, output, pageSum, pageMax),
        ResolvePath.Scalar => TileBake.ResolveScalar(difference, dense, previousRow, tent, bell, output, pageSum, pageMax),
        _ => TileBake.Resolve(difference, dense, previousRow, tent, bell, output, pageSum, pageMax),
    };

    private sealed class Tile
    {
        public int[] Difference = new int[Pitch * Rows];
        public int[] Dense = new int[Cells];
        public int[] PreviousRow = new int[Size];
        public long[]? Tent;
        public long[]? Bell;
    }

    private sealed class Expected
    {
        public short[] Output = new short[Cells];
        public int[] PreviousRow = new int[Size];
        public long PageSum;
        public short PageMax = short.MinValue;
        public bool Any;
    }

    private static Expected Expect(Tile tile)
    {
        var expected = new Expected();
        tile.PreviousRow.CopyTo(expected.PreviousRow, 0);
        unchecked
        {
            for (var y = 0; y < Size; y++)
            {
                var carry = 0;
                for (var x = 0; x < Size; x++)
                {
                    carry += tile.Difference[y * Pitch + x];
                    var boxes = carry + expected.PreviousRow[x];
                    expected.PreviousRow[x] = boxes;
                    var tent = tile.Tent != null ? TileBake.RoundQ40(tile.Tent[y * Size + x]) : 0;
                    var bell = tile.Bell != null ? TileBake.RoundQ40(tile.Bell[y * Size + x]) : 0;
                    var total = (long)boxes + tile.Dense[y * Size + x] + tent + bell;
                    if (total != 0) expected.Any = true;
                    var cell = (short)Math.Clamp(total, short.MinValue, short.MaxValue);
                    expected.Output[y * Size + x] = cell;
                    if (cell > expected.PageMax) expected.PageMax = cell;
                    expected.PageSum += cell;
                }
            }
        }

        return expected;
    }

    private static short[] Check(ResolvePath path, Tile tile, out bool any)
    {
        var expected = Expect(tile);
        var output = new short[Cells];
        var previousRow = (int[])tile.PreviousRow.Clone();
        long pageSum = long.MinValue;
        short pageMax = 0;
        if (Supported(path))
        {
            fixed (int* difference = tile.Difference)
            fixed (int* dense = tile.Dense)
            fixed (int* prev = previousRow)
            fixed (long* tent = tile.Tent)
            fixed (long* bell = tile.Bell)
            fixed (short* page = output)
            {
                any = Invoke(path, difference, dense, prev, tent, bell, page, &pageSum, &pageMax);
            }

            Assert.Equal(expected.Output, output);
            Assert.Equal(expected.PreviousRow, previousRow);
            Assert.Equal(expected.PageSum, pageSum);
            Assert.Equal(expected.PageMax, pageMax);
            Assert.Equal(expected.Any, any);
        }
        else
        {
            any = false;
        }

        return output;
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public void BoxesAndDenseBeyondInt32_SaturatePositive(ResolvePath path)
    {
        if (!Supported(path)) return;
        var tile = new Tile { Difference = new int[Pitch * Rows] };
        tile.Difference[0] = 1_500_000_000;
        tile.Dense[0] = 1_500_000_000;
        var output = Check(path, tile, out _);
        Assert.Equal((short)32767, output[0]);
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public void BoxesAndDenseBeyondInt32_SaturateNegative(ResolvePath path)
    {
        if (!Supported(path)) return;
        var tile = new Tile();
        tile.Difference[0] = -1_500_000_000;
        tile.Dense[0] = -1_500_000_000;
        var output = Check(path, tile, out _);
        Assert.Equal((short)-32768, output[0]);
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public void Int32MaxBoxesAndDensePlusTent_ReportsAny(ResolvePath path)
    {
        if (!Supported(path)) return;
        var tile = new Tile { Tent = new long[Cells] };
        tile.Difference[0] = int.MaxValue;
        tile.Dense[0] = int.MaxValue;
        tile.Tent[0] = 2L << 40;
        var output = Check(path, tile, out var any);
        Assert.True(any);
        Assert.Equal((short)32767, output[0]);
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public void EdgeValueGrid_MatchesLongReference(ResolvePath path)
    {
        var tile = new Tile { Tent = new long[Cells], Bell = new long[Cells] };
        for (var i = 0; i < tile.Difference.Length; i++)
            tile.Difference[i] = EdgeInts[i % EdgeInts.Length];
        for (var i = 0; i < Cells; i++)
        {
            tile.Dense[i] = EdgeInts[(i * 7 + 3) % EdgeInts.Length];
            tile.Tent[i] = EdgeLongs[i % EdgeLongs.Length];
            tile.Bell[i] = EdgeLongs[(i * 5 + 7) % EdgeLongs.Length];
        }

        for (var i = 0; i < Size; i++)
            tile.PreviousRow[i] = EdgeInts[(i * 5 + 1) % EdgeInts.Length];
        Check(path, tile, out _);
    }

    public enum Regime
    {
        Small,
        NearBound,
        FullInt32,
        MixedEdges,
    }

    public static TheoryData<Regime, int, bool, bool, ResolvePath> RandomPlanes()
    {
        var data = new TheoryData<Regime, int, bool, bool, ResolvePath>();
        foreach (Regime regime in Enum.GetValues<Regime>())
        foreach (var seed in new[] { 1, 2, 3, 4, 5 })
        foreach (var tent in new[] { false, true })
        foreach (var bell in new[] { false, true })
        foreach (ResolvePath path in Enum.GetValues<ResolvePath>())
            data.Add(regime, seed, tent, bell, path);
        return data;
    }

    [Theory]
    [MemberData(nameof(RandomPlanes))]
    public void RandomPlanes_MatchLongReference(Regime regime, int seed, bool withTent, bool withBell, ResolvePath path)
    {
        var random = new Random((int)regime * 7919 + seed * 104729);
        var tile = new Tile
        {
            Tent = withTent ? new long[Cells] : null,
            Bell = withBell ? new long[Cells] : null,
        };
        unchecked
        {
            for (var i = 0; i < tile.Difference.Length; i++)
                tile.Difference[i] = NextInt(random, regime);
            for (var i = 0; i < Cells; i++)
            {
                tile.Dense[i] = NextInt(random, regime);
                if (withTent) tile.Tent![i] = NextLong(random, regime);
                if (withBell) tile.Bell![i] = NextLong(random, regime);
            }

            for (var i = 0; i < Size; i++)
                tile.PreviousRow[i] = NextInt(random, regime);
        }

        Check(path, tile, out _);
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
}

public sealed class PlaneLimitTests
{
    private const float X = 128.5f;
    private const float Y = 64.5f;
    private const int CellX = 128;
    private const int CellY = 64;

    [Fact]
    public void BoxPlane_IsExactBelow2To20MaxAmplitudeSources_OnOneCell()
    {
        var world = World.New();
        var grid = Grid.New(world, 8, 0f, 0f, 256f);
        var layer = Layer.New(world);
        var stamp = Stamp.Box(1, 1, -128);
        for (var i = 0; i < (1 << 20) - 1; i++)
            World.Place(world, layer, X, Y, stamp, -16);
        World.Process(world);
        Assert.Equal((short)32767, World.Query(world, grid, layer, CellX, CellY));
        World.Free(world);
        Assert.True(Stamp.Free(stamp));
    }

    [Fact]
    public void BoxPlane_WrapsAt2To20MaxAmplitudeSources_OnOneCell()
    {
        var world = World.New();
        var grid = Grid.New(world, 8, 0f, 0f, 256f);
        var layer = Layer.New(world);
        var stamp = Stamp.Box(1, 1, -128);
        for (var i = 0; i < 1 << 20; i++)
            World.Place(world, layer, X, Y, stamp, -16);
        World.Process(world);
        Assert.Equal((short)-32768, World.Query(world, grid, layer, CellX, CellY));
        World.Free(world);
        Assert.True(Stamp.Free(stamp));
    }

    [Fact]
    public void TentPlane_IsExactBelow4096MaxAmplitudeSources_AtOnePeak()
    {
        var world = World.New();
        var grid = Grid.New(world, 8, 0f, 0f, 256f);
        var layer = Layer.New(world);
        var stamp = Stamp.Tent(1, 1, -128);
        for (var i = 0; i < 4095; i++)
            World.Place(world, layer, X, Y, stamp, -16);
        World.Process(world);
        Assert.Equal((short)32767, World.Query(world, grid, layer, CellX, CellY));
        World.Free(world);
        Assert.True(Stamp.Free(stamp));
    }

    [Fact]
    public void TentPlane_WrapsAt4096MaxAmplitudeSources_AtOnePeak()
    {
        var world = World.New();
        var grid = Grid.New(world, 8, 0f, 0f, 256f);
        var layer = Layer.New(world);
        var stamp = Stamp.Tent(1, 1, -128);
        for (var i = 0; i < 4096; i++)
            World.Place(world, layer, X, Y, stamp, -16);
        World.Process(world);
        Assert.True(World.Query(world, grid, layer, CellX, CellY) < 0);
        World.Free(world);
        Assert.True(Stamp.Free(stamp));
    }

    [Fact]
    public void BellPlane_IsExactBelow4096MaxAmplitudeSources_AtOnePeak()
    {
        var world = World.New();
        var grid = Grid.New(world, 8, 0f, 0f, 256f);
        var layer = Layer.New(world);
        var stamp = Stamp.Bell(1, 1, -128);
        for (var i = 0; i < 4095; i++)
            World.Place(world, layer, X, Y, stamp, -16);
        World.Process(world);
        Assert.Equal((short)32767, World.Query(world, grid, layer, CellX, CellY));
        World.Free(world);
        Assert.True(Stamp.Free(stamp));
    }

    [Fact]
    public void BellPlane_WrapsAt4096MaxAmplitudeSources_AtOnePeak()
    {
        var world = World.New();
        var grid = Grid.New(world, 8, 0f, 0f, 256f);
        var layer = Layer.New(world);
        var stamp = Stamp.Bell(1, 1, -128);
        for (var i = 0; i < 4096; i++)
            World.Place(world, layer, X, Y, stamp, -16);
        World.Process(world);
        Assert.True(World.Query(world, grid, layer, CellX, CellY) < 0);
        World.Free(world);
        Assert.True(Stamp.Free(stamp));
    }

    [Fact]
    public void ExcludedSaturatedCell_MatchesTheFieldWithoutTheSource_WhenBoxesAndDenseOverflowInt32AsASum()
    {
        const int each = 550_000;
        const float px = 128f;
        const float py = 64f;
        var world = World.New();
        var twin = World.New();
        var grids = new byte[2];
        var layers = new byte[2];
        var index = 0;
        foreach (var candidate in new[] { world, twin })
        {
            grids[index] = Grid.New(candidate, 8, 0f, 0f, 256f);
            layers[index] = Layer.New(candidate);
            index++;
        }

        var box = Stamp.Box(1, 1, 127);
        var raster = Stamp.New(new sbyte[] { 127 }, 1, 1);
        var excluded = -1;
        for (var i = 0; i < each; i++)
        {
            var id = World.Place(world, layers[0], px, py, box, 16);
            if (i == 0) excluded = id;
            World.Place(world, layers[0], px, py, raster, 16);
            if (i > 0) World.Place(twin, layers[1], px, py, box, 16);
            World.Place(twin, layers[1], px, py, raster, 16);
        }

        World.Process(world);
        World.Process(twin);
        Assert.Equal((short)32767, World.Query(world, grids[0], layers[0], CellX, CellY));
        Assert.Equal((short)32767, World.Query(twin, grids[1], layers[1], CellX, CellY));

        Assert.True(World.TrySense(world, layers[0], px, py, excluded, out var excludedValue));
        Assert.True(World.TrySense(twin, layers[1], px, py, out var removedValue));
        Assert.Equal(removedValue, excludedValue);

        Assert.True(World.TrySenseArea(world, layers[0], px, py, 4f, excluded, out var excludedArea));
        Assert.True(World.TrySenseArea(twin, layers[1], px, py, 4f, out var removedArea));
        Assert.Equal(removedArea, excludedArea);

        World.Free(world);
        World.Free(twin);
        Assert.True(Stamp.Free(box));
        Assert.True(Stamp.Free(raster));
    }
}
