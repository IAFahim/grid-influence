using Gi.Validation;
using Xunit;

namespace Gi.Tests;

public sealed unsafe class FiniteInputTests : IDisposable
{
    private const int GridPower = 6;
    private const float GridSize = 64f;
    private const int Cells = 64;

    private static readonly float[] NonFinite =
        { float.NaN, float.PositiveInfinity, float.NegativeInfinity };

    private static readonly float[] Unreachable =
        { 3e9f, -3e9f, 1e20f, -1e20f, 4194304f, -4194304f, 4194305f, float.MaxValue, float.MinValue };

    private readonly byte _world = World.New();
    private readonly byte _grid;
    private readonly byte _layer;
    private readonly byte _derived;
    private readonly byte _box = Stamp.Box(8, 8, 100);
    private readonly World.SourceId _source;

    public FiniteInputTests()
    {
        _grid = Grid.New(_world, GridPower, 0f, 0f, GridSize);
        _layer = Layer.New(_world);
        _derived = Layer.Sum(_world, _layer, 1, _layer, 1);
        _source = World.Place(_world, _layer, 2f, 2f, _box, 3);
        World.Process(_world);
        Assert.True(_source != default);
        Assert.NotEqual(0, World.Query(_world, _grid, _layer, 0, 0));
    }

    public void Dispose()
    {
        World.Free(_world);
        Assert.True(Stamp.Free(_box));
    }

    public static TheoryData<float, float> OutsidePositions()
    {
        var data = new TheoryData<float, float>();
        foreach (var bad in NonFinite)
        {
            data.Add(bad, 2f);
            data.Add(2f, bad);
            data.Add(bad, bad);
        }

        foreach (var far in Unreachable)
        {
            data.Add(far, 2f);
            data.Add(2f, far);
            data.Add(far, far);
        }

        data.Add(float.NaN, float.PositiveInfinity);
        data.Add(3e9f, float.NaN);
        return data;
    }

    [Theory]
    [MemberData(nameof(OutsidePositions))]
    public void Reads_TreatNonFiniteAndUnreachablePositionsAsOutside(float x, float y)
    {
        Assert.Equal(0, World.QueryAt(_world, _grid, _layer, x, y));
        World.QueryGradient(_world, _grid, _layer, x, y, out var gx, out var gy);
        Assert.Equal((0, 0), (gx, gy));
        Assert.False(World.Covers(_world, x, y));

        Assert.False(World.TrySense(_world, _layer, x, y, out var value));
        Assert.Equal(0, value);
        Assert.False(World.TrySense(_world, _derived, x, y, out value));
        Assert.Equal(0, value);
        Assert.False(World.TrySense(_world, _layer, x, y, _source, out value));
        Assert.Equal(0, value);
        Assert.False(World.TrySenseGradient(_world, _layer, x, y, out var fx, out var fy));
        Assert.Equal((0f, 0f), (fx, fy));
        Assert.False(World.TrySenseGradient(_world, _layer, x, y, _source, out fx, out fy));
        Assert.Equal((0f, 0f), (fx, fy));
        Assert.False(World.TrySenseArea(_world, _layer, x, y, 8f, out var total));
        Assert.Equal(0, total);
        Assert.False(World.TrySenseArea(_world, _layer, x, y, 8f, _source, out total));
        Assert.Equal(0, total);
        Assert.False(World.TrySenseMax(_world, _layer, x, y, 8f, out value, out _, out _));
        Assert.Equal(0, value);
        Assert.False(World.TrySenseNearest(_world, _layer, x, y, 8f, 1, out value, out _, out _));
        Assert.Equal(short.MinValue, value);
        Assert.False(World.Changed(_world, _layer, x, y, 8f, 0));
    }

    [Theory]
    [MemberData(nameof(OutsidePositions))]
    public void ColumnReads_RejectNonFiniteAndUnreachablePositionsRowByRow(float x, float y)
    {
        const int count = 3;
        var xs = stackalloc float[count] { 3f, x, 3f };
        var ys = stackalloc float[count] { 3f, y, 3f };
        var excludes = stackalloc World.SourceId[count] { default, _source, _source };
        var values = stackalloc short[count] { -1, -1, -1 };
        var fxs = stackalloc float[count];
        var fys = stackalloc float[count];
        var totals = stackalloc long[count];
        var reaches = stackalloc float[count] { 1f, 1f, 1f };
        var thresholds = stackalloc short[count] { 1, 1, 1 };
        var bestXs = stackalloc float[count];
        var bestYs = stackalloc float[count];
        var whole = stackalloc ulong[1] { ulong.MaxValue };
        var words = stackalloc ulong[1] { ulong.MaxValue };

        Assert.False((World.SenseColumn(_world, _layer, xs, ys, count, values, whole) == World.ReadStatus.Complete));
        Assert.Equal(5ul, whole[0]);
        Assert.Equal(0, values[1]);

        values[1] = -1;
        whole[0] = ulong.MaxValue;
        Assert.False((World.SenseColumn(_world, _layer, xs, ys, excludes, count, values, whole) == World.ReadStatus.Complete));
        Assert.Equal(5ul, whole[0]);
        Assert.Equal(0, values[1]);

        whole[0] = ulong.MaxValue;
        Assert.False((World.SenseGradientColumn(_world, _layer, xs, ys, excludes, count, fxs, fys, whole) == World.ReadStatus.Complete));
        Assert.Equal(5ul, whole[0]);
        Assert.Equal((0f, 0f), (fxs[1], fys[1]));

        whole[0] = ulong.MaxValue;
        Assert.False((World.SenseAreaColumn(_world, _layer, xs, ys, reaches, excludes, count, totals, whole) == World.ReadStatus.Complete));
        Assert.Equal(5ul, whole[0]);
        Assert.Equal(0, totals[1]);

        whole[0] = ulong.MaxValue;
        Assert.False((World.SenseMaxColumn(_world, _layer, xs, ys, reaches, count, values, bestXs, bestYs, whole) == World.ReadStatus.Complete));
        Assert.Equal(5ul, whole[0]);

        whole[0] = ulong.MaxValue;
        Assert.False((World.SenseNearestColumn(
            _world, _layer, xs, ys, reaches, thresholds, count, values, bestXs, bestYs, whole) == World.ReadStatus.Complete));
        Assert.Equal(5ul, whole[0]);
        Assert.Equal(short.MinValue, values[1]);

        Assert.Equal(2, World.ChangedMask(_world, _layer, xs, ys, 1f, 0, count, words));
        Assert.Equal(5ul, words[0]);
    }

    [Fact]
    public void LargeColumnReads_MatchScalarReadsWithNonFiniteRowsOnPooledAndSerialPaths()
    {
        const int count = 8192 + 64;
        var xs = new float[count];
        var ys = new float[count];
        var random = new Random(4242);
        for (var i = 0; i < count; i++)
        {
            xs[i] = (float)(random.NextDouble() * 70 - 3);
            ys[i] = (float)(random.NextDouble() * 70 - 3);
            if (i % 61 == 0) xs[i] = NonFinite[i / 61 % NonFinite.Length];
            if (i % 67 == 0) ys[i] = NonFinite[i / 67 % NonFinite.Length];
            if (i % 71 == 0) xs[i] = Unreachable[i / 71 % Unreachable.Length];
        }

        var expectedValues = new short[count];
        var expectedWhole = new ulong[(count + 63) >> 6];
        var expectedChanged = new ulong[(count + 63) >> 6];
        var expectedHits = 0;
        for (var i = 0; i < count; i++)
        {
            if (World.TrySense(_world, _layer, xs[i], ys[i], out expectedValues[i]))
                expectedWhole[i >> 6] |= 1ul << (i & 63);
            if (!World.Changed(_world, _layer, xs[i], ys[i], 3f, 0)) continue;
            expectedChanged[i >> 6] |= 1ul << (i & 63);
            expectedHits++;
        }

        {
            var values = new short[count];
            var whole = new ulong[expectedWhole.Length];
            var changed = new ulong[expectedChanged.Length];
            fixed (float* xPointer = xs)
            fixed (float* yPointer = ys)
            fixed (short* valuePointer = values)
            fixed (ulong* wholePointer = whole)
            fixed (ulong* changedPointer = changed)
            {
                World.SenseColumn(_world, _layer, xPointer, yPointer, count, valuePointer, wholePointer);
                Assert.Equal(expectedHits, World.ChangedMask(
                    _world, _layer, xPointer, yPointer, 3f, 0, count, changedPointer));
            }

            Assert.True(expectedValues.AsSpan().SequenceEqual(values));
            Assert.True(expectedWhole.AsSpan().SequenceEqual(whole));
            Assert.True(expectedChanged.AsSpan().SequenceEqual(changed));
        }
    }

    [Fact]
    public void Sync_IgnoresNonFinitePositionsAndAcceptsUnreachableFinitePositions()
    {
        var far = Place(14f, 14f);
        var other = Place(40f, 40f);
        World.Process(_world);
        var baseline = Snapshot();
        World.TryGetSourceSpec(_world, other, out var before);

        foreach (var bad in NonFinite)
        {
            SyncOne(other, bad, 40f);
            SyncOne(other, 40f, bad);
            SyncOne(other, bad, bad);
            Assert.Equal(World.BatchStatus.Applied, SyncOneWithToken(other, bad, 41f));
            World.Process(_world);
            Assert.True(baseline.AsSpan().SequenceEqual(Snapshot()));
            Assert.True(World.TryGetSourceSpec(_world, other, out var after));
            Assert.Equal((before.X, before.Y), (after.X, after.Y));
        }

        foreach (var unreachable in Unreachable)
        {
            SyncOne(other, unreachable, 40f);
            SyncOne(far, 14f, unreachable);
            World.Process(_world);
            Assert.True(World.TryGetSourceSpec(_world, other, out var spec));
            Assert.Equal(unreachable, spec.X);
            Assert.Equal(0, World.Query(_world, _grid, _layer, 34, 34, 12, 12));
            Assert.Equal(0, World.Query(_world, _grid, _layer, 8, 8, 12, 12));
            SyncOne(other, 40f, 40f);
            SyncOne(far, 14f, 14f);
            World.Process(_world);
            Assert.True(baseline.AsSpan().SequenceEqual(Snapshot()));
        }
    }

    [Fact]
    public void Sync_RejectedPositionsLeaveTheChangedListAndEpochsUntouched()
    {
        var other = Place(40f, 40f);
        World.Process(_world);
        var tick = World.Tick(_world);
        foreach (var bad in NonFinite) SyncOne(other, bad, bad);
        World.Process(_world);
        Assert.Equal(0, World.ChangedTiles(_world, _grid, _layer, null));
        Assert.Equal(0, World.ChangedTiles(_world, _grid, _layer, tick, null));
    }

    [Theory]
    [InlineData(300)]
    [InlineData(8192 + 600)]
    public void SyncTransforms_RejectsWholeRowsWithNonFinitePositionsOnSmallAndLargePaths(int count)
    {
        var world = World.New();
        var grid = Grid.New(world, GridPower, 0f, 0f, GridSize);
        var layer = Layer.New(world);
        var twin = World.New();
        var twinGrid = Grid.New(twin, GridPower, 0f, 0f, GridSize);
        var twinLayer = Layer.New(twin);
        var stamp = Stamp.Box(2, 2, 1);
        try
        {
            var mine = new World.SourceId[count];
            var theirs = new World.SourceId[count];
            for (var i = 0; i < count; i++)
            {
                var row = i / 61;
                var x = 1f + i % 61;
                var y = 1f + row % 60;
                mine[i] = World.Place(world, layer, x, y, stamp, 1);
                theirs[i] = World.Place(twin, twinLayer, x, y, stamp, 1);
            }

            World.Process(world);
            World.Process(twin);

            var xs = new float[count];
            var ys = new float[count];
            var headings = new float[count];
            var scales = new float[count];
            var expected = new ulong[(count + 63) >> 6];
            for (var i = 0; i < count; i++)
            {
                var row = i / 61;
                xs[i] = Math.Min(1f + i % 61 + 0.5f, 61.5f);
                ys[i] = Math.Min(1f + row % 60 + 0.5f, 60.5f);
                headings[i] = 0.25f * (i % 4);
                scales[i] = 1f + i % 5 * 0.25f;
                var rejected = false;
                if (i % 13 == 0) xs[i] = Unreachable[i / 13 % Unreachable.Length];
                if (i % 17 == 0) ys[i] = Unreachable[i / 17 % Unreachable.Length];
                if (i % 7 == 0)
                {
                    xs[i] = NonFinite[i / 7 % NonFinite.Length];
                    rejected = true;
                }

                if (i % 11 == 0)
                {
                    ys[i] = NonFinite[i / 11 % NonFinite.Length];
                    rejected = true;
                }

                if (rejected) expected[i >> 6] |= 1ul << (i & 63);
            }

            var duplicate = count - 1;
            mine[duplicate - 1] = mine[0];
            mine[duplicate] = mine[0];
            theirs[duplicate - 1] = theirs[0];
            theirs[duplicate] = theirs[0];
            xs[duplicate - 1] = 30.5f;
            ys[duplicate - 1] = 30.5f;
            xs[duplicate] = float.NaN;
            ys[duplicate] = 31.5f;
            expected[(duplicate - 1) >> 6] &= ~(1ul << ((duplicate - 1) & 63));
            expected[duplicate >> 6] |= 1ul << (duplicate & 63);

            for (var i = 0; i < count; i++)
            {
                if (!float.IsFinite(xs[i]) || !float.IsFinite(ys[i])) continue;
                World.Move(twin, theirs[i], xs[i], ys[i]);
                World.Turn(twin, theirs[i], headings[i]);
                World.Scale(twin, theirs[i], scales[i]);
            }

            var mask = new ulong[expected.Length + 1];
            mask[expected.Length] = 0xA5A5A5A5A5A5A5A5UL;
            fixed (World.SourceId* idPointer = mine)
            fixed (float* xPointer = xs)
            fixed (float* yPointer = ys)
            fixed (float* headingPointer = headings)
            fixed (float* scalePointer = scales)
            fixed (ulong* maskPointer = mask)
            {
                var range = stackalloc World.TransformRange[1];
                range[0] = new World.TransformRange
                {
                    Sources = idPointer, Xs = xPointer, Ys = yPointer,
                    Headings = headingPointer, Scales = scalePointer,
                    Invalid = maskPointer, InvalidWords = expected.Length, Count = count
                };
                var span = new ReadOnlySpan<World.TransformRange>(range, 1);
                Assert.Equal(World.BatchStatus.Applied, World.SyncTransforms(World.Capture(world), span));
                Assert.True(expected.AsSpan().SequenceEqual(mask.AsSpan(0, expected.Length)));
                Assert.Equal(0xA5A5A5A5A5A5A5A5UL, mask[expected.Length]);
                World.Process(world);
                World.Process(twin);
                Assert.True(
                    Region(world, grid, layer).AsSpan().SequenceEqual(Region(twin, twinGrid, twinLayer)));

                range[0].Invalid = null;
                range[0].InvalidWords = 0;
                Assert.Equal(World.BatchStatus.Applied, World.SyncTransforms(World.Capture(world), span));
                World.Process(world);
                Assert.True(
                    Region(world, grid, layer).AsSpan().SequenceEqual(Region(twin, twinGrid, twinLayer)));
            }
        }
        finally
        {
            World.Free(world);
            World.Free(twin);
            Assert.True(Stamp.Free(stamp));
        }
    }

    [Fact]
    public void Deposits_AtUnreachableFinitePositionsLeaveNoResidueForEveryStampKind()
    {
        var parked = World.New();
        var parkedGrid = Grid.New(parked, GridPower, 0f, 0f, GridSize);
        var parkedLayer = Layer.New(parked);
        var direct = World.New();
        var directGrid = Grid.New(direct, GridPower, 0f, 0f, GridSize);
        var directLayer = Layer.New(direct);
        var stamps = new[]
        {
            Stamp.Box(5, 3, 40), Stamp.Tent(7, 5, 60), Stamp.Bell(9, 9, 70),
            Stamp.Disk(6, 50), Stamp.Cone(6, 50, 270), Stamp.Dome(6, 50),
        };
        try
        {
            var count = stamps.Length * Unreachable.Length;
            var ids = new World.SourceId[count];
            var index = 0;
            foreach (var stamp in stamps)
            foreach (var unreachable in Unreachable)
            {
                var id = World.Place(parked, parkedLayer, unreachable, index % 2 == 0 ? unreachable : 20f, stamp, (sbyte)(1 + index % 3));
                Assert.True(id != default);
                World.Turn(parked, id, 0.7f + index);
                World.Scale(parked, id, 0.5f + index % 4);
                ids[index++] = id;
            }

            World.Process(parked);
            Assert.Equal(0, World.Query(parked, parkedGrid, parkedLayer, 0, 0, Cells, Cells));
            Assert.Equal(0, World.ChangedTiles(parked, parkedGrid, parkedLayer, null));

            index = 0;
            foreach (var stamp in stamps)
            foreach (var _ in Unreachable)
            {
                var row = index / 9;
                var x = 12f + index % 9 * 5f;
                var y = 12f + row % 6 * 6f;
                World.Move(parked, ids[index], x, y);
                var id = World.Place(direct, directLayer, x, y, stamp, (sbyte)(1 + index % 3));
                World.Turn(direct, id, 0.7f + index);
                World.Scale(direct, id, 0.5f + index % 4);
                index++;
            }

            World.Process(parked);
            World.Process(direct);
            Assert.NotEqual(0, World.Query(direct, directGrid, directLayer, 0, 0, Cells, Cells));
            Assert.True(
                Region(parked, parkedGrid, parkedLayer).AsSpan().SequenceEqual(Region(direct, directGrid, directLayer)));
        }
        finally
        {
            World.Free(parked);
            World.Free(direct);
            foreach (var stamp in stamps) Assert.True(Stamp.Free(stamp));
        }
    }

    [Fact]
    public void RejectedRowsAndOutsideReads_AllocateNothingWhenWarm()
    {
        const int count = 8192 + 128;
        var ids = new World.SourceId[count];
        var xs = new float[count];
        var ys = new float[count];
        var values = new short[count];
        var whole = new ulong[(count + 63) >> 6];
        var mask = new ulong[(count + 63) >> 6];
        for (var i = 0; i < count; i++)
        {
            var row = i / 61;
            var queryRow = i / 60;
            ids[i] = Place(1f + i % 61, 1f + row % 60);
            xs[i] = i % 5 == 0 ? NonFinite[i / 5 % NonFinite.Length] : 2f + i % 60;
            ys[i] = i % 7 == 0 ? Unreachable[i / 7 % Unreachable.Length] : 2f + queryRow % 60;
        }

        World.Process(_world);
        var checksum = 0L;
        var before = 0L;
        fixed (World.SourceId* idPointer = ids)
        fixed (float* xPointer = xs)
        fixed (float* yPointer = ys)
        fixed (short* valuePointer = values)
        fixed (ulong* wholePointer = whole)
        fixed (ulong* maskPointer = mask)
        {
            var range = stackalloc World.TransformRange[1];
            range[0] = new World.TransformRange
            {
                Sources = idPointer, Xs = xPointer, Ys = yPointer,
                Invalid = maskPointer, InvalidWords = mask.Length, Count = count
            };
            var token = World.Capture(_world);
            for (var frame = 0; frame < 12; frame++)
            {
                if (frame == 6) before = GC.GetAllocatedBytesForCurrentThread();
                World.Sync(_world, idPointer, xPointer, yPointer, count);
                World.SyncTransforms(token, new ReadOnlySpan<World.TransformRange>(range, 1));
                World.Process(_world);
                World.SenseColumn(_world, _layer, xPointer, yPointer, count, valuePointer, wholePointer);
                checksum += World.QueryAt(_world, _grid, _layer, xs[frame], ys[frame]);
                checksum += (long)(mask[0] ^ whole[0]) + values[frame];
            }
        }

        Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.NotEqual(0L, checksum);
    }

    public static TheoryData<int, float, float, float, string?> GridCases() => new()
    {
        { 6, 0f, 0f, 64f, null },
        { 6, -1000f, 250.5f, 64f, null },
        { 6, 3e38f, -3e38f, 64f, null },
        { 5, 0f, 0f, 8192f, null },
        { 5, 0f, 0f, 8193f, "size" },
        { 14, 0f, 0f, 4f, null },
        { 14, 0f, 0f, 3.9999f, "size" },
        { 14, 0f, 0f, 16384f, null },
        { 4, 0f, 0f, 64f, "power" },
        { 15, 0f, 0f, 64f, "power" },
        { 6, float.NaN, 0f, 64f, "x" },
        { 6, float.PositiveInfinity, 0f, 64f, "x" },
        { 6, float.NegativeInfinity, 0f, 64f, "x" },
        { 6, 0f, float.NaN, 64f, "y" },
        { 6, 0f, float.PositiveInfinity, 64f, "y" },
        { 6, 0f, float.NegativeInfinity, 64f, "y" },
        { 6, 0f, 0f, float.NaN, "size" },
        { 6, 0f, 0f, float.PositiveInfinity, "size" },
        { 6, 0f, 0f, float.NegativeInfinity, "size" },
        { 6, 0f, 0f, 0f, "size" },
        { 6, 0f, 0f, -64f, "size" },
        { 6, 0f, 0f, 1e-30f, "size" },
        { 6, 0f, 0f, 1e-3f, "size" },
        { 6, 0f, 0f, 3e38f, "size" },
        { 6, 0f, 0f, float.Epsilon, "size" },
        { 6, 0f, 0f, float.MaxValue, "size" },
    };

    [Theory]
    [MemberData(nameof(GridCases))]
    public void AddGrid_AndValidationAgreeOnEveryRule(int power, float x, float y, float size, string? rejected)
    {
        var world = World.New();
        try
        {
            var before = Grid.Count(world);
            Assert.Equal(rejected == null, GiValidation.TryGrid(power, x, y, size, out var error));
            Assert.Equal(rejected == null, error == null);
            if (rejected == null)
            {
                var grid = Grid.New(world, power, x, y, size);
                Assert.Equal(before + 1, Grid.Count(world));
                Assert.True(Grid.TryGetSpec(world, grid, out var gridPower, out var gx, out var gy, out var gs));
                Assert.Equal((power, x, y, size), (gridPower, gx, gy, gs));
                var layer = Layer.New(world);
                World.Process(world);
                Assert.Equal(0, World.QueryAt(world, grid, layer, x, y));
                return;
            }

            var thrown = Assert.Throws<ArgumentOutOfRangeException>(() => Grid.New(world, power, x, y, size));
            Assert.Equal(rejected, thrown.ParamName);
            Assert.Equal(before, Grid.Count(world));
        }
        finally
        {
            World.Free(world);
        }
    }

    [Fact]
    public void Grid_TwoArgumentValidationUsesTheSameScaleRule()
    {
        Assert.True(GiValidation.TryGrid(6, 64f, out _));
        Assert.False(GiValidation.TryGrid(6, 1e-3f, out var error));
        Assert.NotNull(error);
        Assert.False(GiValidation.TryGrid(6, 3e38f, out _));
    }

    private World.SourceId Place(float x, float y) => World.Place(_world, _layer, x, y, _box, 2);

    private void SyncOne(World.SourceId id, float x, float y)
    {
        var ids = stackalloc World.SourceId[1] { id };
        var xs = stackalloc float[1] { x };
        var ys = stackalloc float[1] { y };
        World.Sync(_world, ids, xs, ys, 1);
    }

    private World.BatchStatus SyncOneWithToken(World.SourceId id, float x, float y)
    {
        var ids = stackalloc World.SourceId[1] { id };
        var xs = stackalloc float[1] { x };
        var ys = stackalloc float[1] { y };
        return World.Sync(World.Capture(_world), ids, xs, ys, 1);
    }

    private short[] Snapshot() => Region(_world, _grid, _layer);

    private static short[] Region(byte world, byte grid, byte layer)
    {
        var cells = new short[Cells * Cells];
        fixed (short* pointer = cells) World.QueryRegion(world, grid, layer, 0, 0, Cells, Cells, pointer);
        return cells;
    }
}
