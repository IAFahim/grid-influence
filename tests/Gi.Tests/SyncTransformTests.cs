using Xunit;

namespace Gi.Tests;

public sealed class SyncTransformTests : IDisposable
{
    private readonly byte _grid;
    private readonly byte _layer;
    private readonly byte _stamp;
    private readonly byte _world = World.New();

    public SyncTransformTests()
    {
        _grid = Grid.New(_world, 6, 0f, 0f, 64f);
        _layer = Layer.New(_world);
        _stamp = Stamp.Box(2, 2, 1);
    }

    public void Dispose()
    {
        World.Free(_world);
        Assert.True(Stamp.Free(_stamp));
    }

    private static unsafe void AssertIdentical(byte a, byte gridA, byte layerA, byte b, byte gridB, byte layerB)
    {
        Assert.Equal(World.Tick(a), World.Tick(b));
        Assert.Equal(World.Query(a, gridA, layerA, 0, 0, 64, 64), World.Query(b, gridB, layerB, 0, 0, 64, 64));
        Assert.Equal(
            World.QueryMax(a, gridA, layerA, out var ax, out var ay),
            World.QueryMax(b, gridB, layerB, out var bx, out var by));
        Assert.Equal((ax, ay), (bx, by));
        var mine = stackalloc short[64 * 64];
        var theirs = stackalloc short[64 * 64];
        World.QueryRegion(a, gridA, layerA, 0, 0, 64, 64, mine);
        World.QueryRegion(b, gridB, layerB, 0, 0, 64, 64, theirs);
        Assert.True(new Span<short>(mine, 64 * 64).SequenceEqual(new Span<short>(theirs, 64 * 64)));
        Assert.Equal(
            World.ChangedTiles(a, gridA, layerA, null),
            World.ChangedTiles(b, gridB, layerB, null));
    }

    [Fact]
    public unsafe void PositionOnly_MatchesWorldSyncIncludingSignedZeroRejectedNaNAndDuplicates()
    {
        var twin = World.New();
        var twinGrid = Grid.New(twin, 6, 0f, 0f, 64f);
        var twinLayer = Layer.New(twin);
        try
        {
            const int count = 200;
            var mine = new World.SourceId[count];
            var theirs = new World.SourceId[count];
            for (var index = 0; index < count; index++)
            {
                var x = 2f + index % 60;
                var row = index / 60 % 60;
                var y = 2f + row;
                mine[index] = World.Place(_world, _layer, x, y, _stamp, 2);
                theirs[index] = World.Place(twin, twinLayer, x, y, _stamp, 2);
            }

            var zeroed = World.Place(_world, _layer, 0f, 0f, _stamp, 2);
            var zeroedTwin = World.Place(twin, twinLayer, 0f, 0f, _stamp, 2);
            World.Process(_world);
            World.Process(twin);

            var stale = World.Place(_world, _layer, 5f, 5f, _stamp, 2);
            var staleTwin = World.Place(twin, twinLayer, 5f, 5f, _stamp, 2);
            World.Remove(_world, stale);
            World.Remove(twin, staleTwin);

            var ids = new World.SourceId[count];
            var twinIds = new World.SourceId[count];
            var xs = new float[count];
            var ys = new float[count];
            for (var index = 0; index < count; index++)
            {
                var invalid = index % 37 == 0;
                ids[index] = invalid ? stale : mine[index];
                twinIds[index] = invalid ? staleTwin : theirs[index];
                xs[index] = 1f + index % 61;
                var row = index / 61 % 60;
                ys[index] = 1f + row;
            }

            ids[3] = mine[42];
            twinIds[3] = theirs[42];
            xs[3] = 33.5f;
            ys[3] = 33.5f;
            ids[17] = zeroed;
            twinIds[17] = zeroedTwin;
            xs[17] = -0.0f;
            ys[17] = 0f;
            ids[23] = mine[23];
            twinIds[23] = theirs[23];
            ys[23] = float.NaN;

            var words = (count + 63) >> 6;
            var mask = new ulong[words + 1];
            var expected = new ulong[words];
            for (var index = 0; index < count; index++)
                if (twinIds[index] == staleTwin || !float.IsFinite(xs[index]) || !float.IsFinite(ys[index]))
                    expected[index >> 6] |= 1ul << (index & 63);

            fixed (World.SourceId* idPointer = ids)
            fixed (World.SourceId* twinIdPointer = twinIds)
            fixed (float* xPointer = xs)
            fixed (float* yPointer = ys)
            fixed (ulong* maskPointer = mask)
            {
                var range = stackalloc World.TransformRange[1];
                range[0] = new World.TransformRange
                {
                    Sources = idPointer, Xs = xPointer, Ys = yPointer,
                    Invalid = maskPointer, InvalidWords = words, Count = count
                };
                mask[words] = 0xAAAAAAAAAAAAAAAAUL;
                var token = World.Capture(_world);
                Assert.Equal(
                    World.BatchStatus.Applied,
                    World.SyncTransforms(token, new ReadOnlySpan<World.TransformRange>(range, 1)));
                World.Sync(twin, twinIdPointer, xPointer, yPointer, count);
                World.Process(_world);
                World.Process(twin);
                AssertIdentical(_world, _grid, _layer, twin, twinGrid, twinLayer);
                Assert.True(mask.AsSpan(0, words).SequenceEqual(expected));
                Assert.Equal(0xAAAAAAAAAAAAAAAAUL, mask[words]);

                mask.AsSpan(0, words).Clear();
                Assert.Equal(
                    World.BatchStatus.Applied,
                    World.SyncTransforms(token, new ReadOnlySpan<World.TransformRange>(range, 1)));
                World.Sync(twin, twinIdPointer, xPointer, yPointer, count);
                World.Process(_world);
                World.Process(twin);
                AssertIdentical(_world, _grid, _layer, twin, twinGrid, twinLayer);
                Assert.True(mask.AsSpan(0, words).SequenceEqual(expected));
            }
        }
        finally
        {
            World.Free(twin);
        }
    }

    [Fact]
    public unsafe void SmallUnevenRanges_MatchOrderedMoveTurnScale()
    {
        var twin = World.New();
        var twinGrid = Grid.New(twin, 6, 0f, 0f, 64f);
        var twinLayer = Layer.New(twin);
        try
        {
            const int firstCount = 187;
            const int secondCount = 113;
            const int count = firstCount + secondCount;
            var mine = new World.SourceId[count];
            for (var index = 0; index < count; index++)
            {
                var x = 2f + index % 60;
                var row = index / 60 % 60;
                var y = 2f + row;
                mine[index] = World.Place(_world, _layer, x, y, _stamp, 3);
                World.Place(twin, twinLayer, x, y, _stamp, 3);
            }

            var stale = World.Place(_world, _layer, 5f, 5f, _stamp, 3);
            World.Remove(_world, stale);
            World.Process(_world);
            World.Process(twin);

            var ids = new World.SourceId[count];
            var xs = new float[count];
            var ys = new float[count];
            var headings = new float[count];
            var scales = new float[count];
            for (var index = 0; index < count; index++)
            {
                ids[index] = index % 29 == 0 ? stale : mine[index];
                xs[index] = index % 11 == 0 ? 2f + index % 60 : 1f + index % 61;
                var row = index / 60 % 60;
                var column = index / 61 % 60;
                ys[index] = index % 13 == 0 ? 2f + row : 1f + column;
                headings[index] = index % 7 == 0 ? float.NaN : 0.25f * (index % 4);
                scales[index] = index % 17 == 0 ? 0.001f : 1f + index % 5 * 0.25f;
            }

            ids[5] = mine[100];
            xs[5] = 45.5f;
            ys[5] = 45.5f;
            headings[5] = 1.5f;
            scales[5] = 2f;

            var firstWords = (firstCount + 63) >> 6;
            var words = firstWords + ((secondCount + 63) >> 6);
            var mask = new ulong[words + 2];
            var expected = new ulong[words];
            for (var index = 0; index < count; index++)
            {
                if (ids[index] != stale) continue;
                var local = index < firstCount ? index : index - firstCount;
                var word = index < firstCount ? local >> 6 : firstWords + (local >> 6);
                expected[word] |= 1ul << (local & 63);
            }

            fixed (World.SourceId* idPointer = ids)
            fixed (float* xPointer = xs)
            fixed (float* yPointer = ys)
            fixed (float* headingPointer = headings)
            fixed (float* scalePointer = scales)
            fixed (ulong* maskPointer = mask)
            {
                var range = stackalloc World.TransformRange[2];
                range[0] = new World.TransformRange
                {
                    Sources = idPointer, Xs = xPointer, Ys = yPointer,
                    Headings = headingPointer, Scales = scalePointer,
                    Invalid = maskPointer, InvalidWords = firstWords, Count = firstCount
                };
                range[1] = new World.TransformRange
                {
                    Sources = idPointer + firstCount, Xs = xPointer + firstCount, Ys = yPointer + firstCount,
                    Headings = headingPointer + firstCount, Scales = scalePointer + firstCount,
                    Invalid = maskPointer + firstWords, InvalidWords = (secondCount + 63) >> 6,
                    Count = secondCount
                };
                mask[words] = 0xFEDCBA0987654321UL;
                mask[words + 1] = 0x1234567890ABCDEFUL;
                var token = World.Capture(_world);
                Assert.Equal(
                    World.BatchStatus.Applied,
                    World.SyncTransforms(token, new ReadOnlySpan<World.TransformRange>(range, 2)));
                for (var index = 0; index < count; index++)
                {
                    if (ids[index] == stale) continue;
                    World.Move(twin, ids[index], xs[index], ys[index]);
                    if (float.IsFinite(headings[index])) World.Turn(twin, ids[index], headings[index]);
                    if (float.IsFinite(scales[index])) World.Scale(twin, ids[index], scales[index]);
                }

                World.Process(_world);
                World.Process(twin);
                AssertIdentical(_world, _grid, _layer, twin, twinGrid, twinLayer);
                Assert.True(mask.AsSpan(0, words).SequenceEqual(expected));
                Assert.Equal(0xFEDCBA0987654321UL, mask[words]);
                Assert.Equal(0x1234567890ABCDEFUL, mask[words + 1]);
            }
        }
        finally
        {
            World.Free(twin);
        }
    }

    [Fact]
    public unsafe void LargeRanges_MatchOrderedCallsWithPendingMergeAndStaleRows()
    {
        var twin = World.New();
        var twinGrid = Grid.New(twin, 6, 0f, 0f, 64f);
        var twinLayer = Layer.New(twin);
        try
        {
            const int live = 9000;
            const int staleCount = 10;
            var mine = new World.SourceId[live];
            var theirs = new World.SourceId[live];
            for (var index = 0; index < live; index++)
            {
                var x = 1f + index % 61;
                var row = index / 61 % 60;
                var y = 1f + row;
                mine[index] = World.Place(_world, _layer, x, y, _stamp, 1);
                theirs[index] = World.Place(twin, twinLayer, x, y, _stamp, 1);
            }

            var stale = new World.SourceId[staleCount];
            for (var index = 0; index < staleCount; index++)
            {
                stale[index] = World.Place(_world, _layer, 10f, 10f, _stamp, 1);
                World.Remove(_world, stale[index]);
            }

            World.Process(_world);
            World.Process(twin);

            const int count = live + staleCount;
            const int firstCount = 5000;
            var ids = new World.SourceId[count];
            var twinIds = new World.SourceId[count];
            var ax = new float[count];
            var ay = new float[count];
            var ah = new float[count];
            var aScale = new float[count];
            var bx = new float[count];
            var by = new float[count];
            var bh = new float[count];
            var bScale = new float[count];
            int[] staleRows = { 0, 63, 64, 127, 128, 1000, 4095, 4096, 8191, 8192 };
            var staleAt = 0;
            for (var index = 0; index < count; index++)
            {
                var isStale = staleAt < staleRows.Length && staleRows[staleAt] == index;
                if (isStale) staleAt++;
                var liveIndex = index - staleAt;
                var x = 1f + index % 61;
                var row = index / 61 % 60;
                var y = 1f + row;
                bx[index] = x;
                by[index] = y;
                if (isStale)
                {
                    ids[index] = stale[staleAt - 1];
                    twinIds[index] = default;
                    continue;
                }

                ids[index] = mine[liveIndex];
                twinIds[index] = theirs[liveIndex];
                ax[index] = Math.Min(x + 0.5f, 61.5f);
                ay[index] = Math.Min(y + 0.5f, 60.5f);
                ah[index] = 0.25f * (index % 4);
                aScale[index] = 1f + index % 5 * 0.25f;
            }

            var firstWords = (firstCount + 63) >> 6;
            var words = firstWords + ((count - firstCount + 63) >> 6);
            var mask = new ulong[words];
            var expected = new ulong[words];
            foreach (var row in staleRows)
            {
                var local = row < firstCount ? row : row - firstCount;
                var word = row < firstCount ? local >> 6 : firstWords + (local >> 6);
                expected[word] |= 1ul << (local & 63);
            }

            fixed (World.SourceId* idPointer = ids)
            fixed (float* axPointer = ax)
            fixed (float* ayPointer = ay)
            fixed (float* ahPointer = ah)
            fixed (float* aScalePointer = aScale)
            fixed (float* bxPointer = bx)
            fixed (float* byPointer = by)
            fixed (float* bhPointer = bh)
            fixed (float* bScalePointer = bScale)
            fixed (ulong* maskPointer = mask)
            {
                var range = stackalloc World.TransformRange[2];
                range[0] = new World.TransformRange
                {
                    Sources = idPointer, Xs = axPointer, Ys = ayPointer,
                    Headings = ahPointer, Scales = aScalePointer,
                    Invalid = maskPointer, InvalidWords = firstWords, Count = firstCount
                };
                range[1] = new World.TransformRange
                {
                    Sources = idPointer + firstCount, Xs = axPointer + firstCount, Ys = ayPointer + firstCount,
                    Headings = ahPointer + firstCount, Scales = aScalePointer + firstCount,
                    Invalid = maskPointer + firstWords, InvalidWords = (count - firstCount + 63) >> 6,
                    Count = count - firstCount
                };
                var token = World.Capture(_world);
                for (var frame = 0; frame < 3; frame++)
                {
                    mask.AsSpan().Clear();
                    Assert.Equal(
                        World.BatchStatus.Applied,
                        World.SyncTransforms(token, new ReadOnlySpan<World.TransformRange>(range, 2)));
                    Assert.True(new Span<ulong>(mask).SequenceEqual(expected));

                    mask.AsSpan().Clear();
                    range[0].Xs = bxPointer;
                    range[0].Ys = byPointer;
                    range[0].Headings = bhPointer;
                    range[0].Scales = bScalePointer;
                    range[1].Xs = bxPointer + firstCount;
                    range[1].Ys = byPointer + firstCount;
                    range[1].Headings = bhPointer + firstCount;
                    range[1].Scales = bScalePointer + firstCount;
                    Assert.Equal(
                        World.BatchStatus.Applied,
                        World.SyncTransforms(token, new ReadOnlySpan<World.TransformRange>(range, 2)));
                    Assert.True(new Span<ulong>(mask).SequenceEqual(expected));
                    range[0].Xs = axPointer;
                    range[0].Ys = ayPointer;
                    range[0].Headings = ahPointer;
                    range[0].Scales = aScalePointer;
                    range[1].Xs = axPointer + firstCount;
                    range[1].Ys = ayPointer + firstCount;
                    range[1].Headings = ahPointer + firstCount;
                    range[1].Scales = aScalePointer + firstCount;

                    for (var index = 0; index < count; index++)
                    {
                        if (twinIds[index] == default) continue;
                        World.Move(twin, twinIds[index], ax[index], ay[index]);
                        World.Turn(twin, twinIds[index], ah[index]);
                        World.Scale(twin, twinIds[index], aScale[index]);
                    }

                    for (var index = 0; index < count; index++)
                    {
                        if (twinIds[index] == default) continue;
                        World.Move(twin, twinIds[index], bx[index], by[index]);
                        World.Turn(twin, twinIds[index], bh[index]);
                        World.Scale(twin, twinIds[index], bScale[index]);
                    }

                    World.Process(_world);
                    World.Process(twin);
                    AssertIdentical(_world, _grid, _layer, twin, twinGrid, twinLayer);
                }
            }
        }
        finally
        {
            World.Free(twin);
        }
    }

    [Fact]
    public unsafe void LargeTransformRangesApplyCleanly()
    {
        var world = World.New();
        var grid = Grid.New(world, 6, 0f, 0f, 64f);
        var layer = Layer.New(world);
        try
        {
            const int count = 9000;
            var ids = new World.SourceId[count];
            var xs = new float[count];
            var ys = new float[count];
            var headings = new float[count];
            var scales = new float[count];
            for (var index = 0; index < count; index++)
            {
                var x = 1f + index % 61;
                var row = index / 61 % 60;
                var y = 1f + row;
                ids[index] = World.Place(world, layer, x, y, _stamp, 1);
                xs[index] = Math.Min(x + 0.5f, 61.5f);
                ys[index] = Math.Min(y + 0.5f, 60.5f);
                headings[index] = 0.25f * (index % 4);
                scales[index] = 1f + index % 5 * 0.25f;
            }

            World.Process(world);
            fixed (World.SourceId* idPointer = ids)
            fixed (float* xPointer = xs)
            fixed (float* yPointer = ys)
            fixed (float* headingPointer = headings)
            fixed (float* scalePointer = scales)
            {
                var range = stackalloc World.TransformRange[1];
                range[0] = new World.TransformRange
                {
                    Sources = idPointer, Xs = xPointer, Ys = yPointer,
                    Headings = headingPointer, Scales = scalePointer, Count = count
                };
                Assert.Equal(
                    World.BatchStatus.Applied,
                    World.SyncTransforms(World.Capture(world), new ReadOnlySpan<World.TransformRange>(range, 1)));
                World.Process(world);
            }

            Assert.True(World.Query(world, grid, layer, 0, 0, 64, 64) > 0);
        }
        finally
        {
            World.Free(world);
        }
    }

    [Fact]
    public unsafe void WarmLargeSyncAndProcess_AllocateNothing()
    {
        var world = World.New();
        var grid = Grid.New(world, 6, 0f, 0f, 64f);
        var layer = Layer.New(world);
        try
        {
            const int count = 9000;
            var ids = new World.SourceId[count];
            var ax = new float[count];
            var ay = new float[count];
            var ah = new float[count];
            var aScale = new float[count];
            var bx = new float[count];
            var by = new float[count];
            for (var index = 0; index < count; index++)
            {
                var x = 1f + index % 61;
                var row = index / 61 % 60;
                var y = 1f + row;
                ids[index] = World.Place(world, layer, x, y, _stamp, 1);
                bx[index] = x;
                by[index] = y;
                ax[index] = Math.Min(x + 0.5f, 61.5f);
                ay[index] = Math.Min(y + 0.5f, 60.5f);
                ah[index] = 0.25f * (index % 4);
                aScale[index] = 1f + index % 5 * 0.25f;
            }

            World.Process(world);
            long allocated;
            long checksum = 0;
            fixed (World.SourceId* idPointer = ids)
            fixed (float* axPointer = ax)
            fixed (float* ayPointer = ay)
            fixed (float* ahPointer = ah)
            fixed (float* aScalePointer = aScale)
            fixed (float* bxPointer = bx)
            fixed (float* byPointer = by)
            {
                var range = stackalloc World.TransformRange[1];
                range[0] = new World.TransformRange
                {
                    Sources = idPointer, Xs = axPointer, Ys = ayPointer,
                    Headings = ahPointer, Scales = aScalePointer, Count = count
                };
                var token = World.Capture(world);
                var cells = stackalloc short[64 * 64];
                var before = 0L;
                for (var frame = 0; frame < 12; frame++)
                {
                    if (frame == 6) before = GC.GetAllocatedBytesForCurrentThread();
                    if ((frame & 1) == 1)
                    {
                        range[0].Xs = bxPointer;
                        range[0].Ys = byPointer;
                    }
                    else
                    {
                        range[0].Xs = axPointer;
                        range[0].Ys = ayPointer;
                    }

                    World.SyncTransforms(token, new ReadOnlySpan<World.TransformRange>(range, 1));
                    World.Process(world);
                    World.QueryRegion(world, grid, layer, 0, 0, 64, 64, cells);
                    for (var cell = 0; cell < 64 * 64; cell++) checksum += cells[cell];
                }

                allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            }

            Assert.Equal(0L, allocated);
            Assert.NotEqual(0L, checksum);
        }
        finally
        {
            World.Free(world);
        }
    }

    [Fact]
    public unsafe void PendingEditsBeforeSync_MergeIntoOneDeposit()
    {
        var twin = World.New();
        var twinGrid = Grid.New(twin, 6, 0f, 0f, 64f);
        var twinLayer = Layer.New(twin);
        try
        {
            const int count = 24;
            var mine = new World.SourceId[count];
            var theirs = new World.SourceId[count];
            for (var index = 0; index < count; index++)
            {
                var x = 2f + index % 20;
                var row = index / 20 % 20;
                var y = 2f + row;
                mine[index] = World.Place(_world, _layer, x, y, _stamp, 2);
                theirs[index] = World.Place(twin, twinLayer, x, y, _stamp, 2);
            }

            World.Process(_world);
            World.Process(twin);

            var xs = new float[count];
            var ys = new float[count];
            for (var index = 0; index < count; index++)
            {
                xs[index] = 1f + index % 30;
                var row = index / 30 % 30;
                ys[index] = 1f + row;
                var offset = index / 10 % 10;
                World.Move(_world, mine[index], 40f + index % 10, 40f + offset);
                World.Move(twin, theirs[index], 40f + index % 10, 40f + offset);
            }

            fixed (World.SourceId* idPointer = mine)
            fixed (float* xPointer = xs)
            fixed (float* yPointer = ys)
            {
                var range = stackalloc World.TransformRange[1];
                range[0] = new World.TransformRange
                    { Sources = idPointer, Xs = xPointer, Ys = yPointer, Count = count };
                Assert.Equal(
                    World.BatchStatus.Applied,
                    World.SyncTransforms(World.Capture(_world), new ReadOnlySpan<World.TransformRange>(range, 1)));
            }

            for (var index = 0; index < count; index++)
                World.Move(twin, theirs[index], xs[index], ys[index]);
            World.Process(_world);
            World.Process(twin);
            AssertIdentical(_world, _grid, _layer, twin, twinGrid, twinLayer);
        }
        finally
        {
            World.Free(twin);
        }
    }

    [Fact]
    public unsafe void FadesAndExpirySurviveSyncAndLandOnSchedule()
    {
        var twin = World.New();
        var twinGrid = Grid.New(twin, 6, 0f, 0f, 64f);
        var twinLayer = Layer.New(twin);
        try
        {
            const int count = 40;
            var mine = new World.SourceId[count];
            var theirs = new World.SourceId[count];
            for (var index = 0; index < count; index++)
            {
                var x = 2f + index % 20;
                var row = index / 20 % 20;
                var y = 2f + row;
                mine[index] = World.Place(_world, _layer, x, y, _stamp, 4);
                theirs[index] = World.Place(twin, twinLayer, x, y, _stamp, 4);
            }

            var commands = new World.SourceCommand[count * 2];
            var results = new World.SourceResult[commands.Length];
            for (var index = 0; index < count; index++)
            {
                commands[index] = World.SourceCommand.Fade(mine[index], 0, 4);
                commands[count + index] = World.SourceCommand.Expire(mine[index], 6);
            }

            Assert.Equal(World.BatchStatus.Applied, World.ApplyBatch(_world, commands, results));
            for (var index = 0; index < count; index++)
            {
                commands[index] = World.SourceCommand.Fade(theirs[index], 0, 4);
                commands[count + index] = World.SourceCommand.Expire(theirs[index], 6);
            }

            Assert.Equal(World.BatchStatus.Applied, World.ApplyBatch(twin, commands, results));

            var xs = new float[count];
            var ys = new float[count];
            var headings = new float[count];
            for (var index = 0; index < count; index++)
            {
                xs[index] = 1f + index % 30;
                var row = index / 30 % 30;
                ys[index] = 1f + row;
                headings[index] = 0.5f;
            }

            fixed (World.SourceId* idPointer = mine)
            fixed (float* xPointer = xs)
            fixed (float* yPointer = ys)
            fixed (float* headingPointer = headings)
            {
                var range = stackalloc World.TransformRange[1];
                range[0] = new World.TransformRange
                {
                    Sources = idPointer, Xs = xPointer, Ys = yPointer,
                    Headings = headingPointer, Count = count
                };
                var token = World.Capture(_world);
                for (var frame = 0; frame < 7; frame++)
                {
                    Assert.Equal(
                        World.BatchStatus.Applied,
                        World.SyncTransforms(token, new ReadOnlySpan<World.TransformRange>(range, 1)));
                    for (var index = 0; index < count; index++)
                    {
                        World.Move(twin, theirs[index], xs[index], ys[index]);
                        World.Turn(twin, theirs[index], headings[index]);
                    }

                    World.Process(_world);
                    World.Process(twin);
                    AssertIdentical(_world, _grid, _layer, twin, twinGrid, twinLayer);
                }
            }

            Assert.Equal(0L, World.Query(_world, _grid, _layer, 0, 0, 64, 64));
        }
        finally
        {
            World.Free(twin);
        }
    }

    [Fact]
    public unsafe void RecordRewindRestoresPreSyncState_AfterLargeSync()
    {
        var world = World.New();
        var grid = Grid.New(world, 6, 0f, 0f, 64f);
        var layer = Layer.New(world);
        try
        {
            const int count = 9000;
            var ids = new World.SourceId[count];
            var xs = new float[count];
            var ys = new float[count];
            for (var index = 0; index < count; index++)
            {
                var x = 1f + index % 61;
                var row = index / 61 % 60;
                var y = 1f + row;
                ids[index] = World.Place(world, layer, x, y, _stamp, 1);
                xs[index] = Math.Min(x + 0.5f, 61.5f);
                ys[index] = Math.Min(y + 0.5f, 60.5f);
            }

            World.Process(world);
            var snapshot = new short[64 * 64];
            fixed (short* snapshotPointer = snapshot)
            {
                World.QueryRegion(world, grid, layer, 0, 0, 64, 64, snapshotPointer);
            }

            World.Record(world);
            fixed (World.SourceId* idPointer = ids)
            fixed (float* xPointer = xs)
            fixed (float* yPointer = ys)
            {
                var range = stackalloc World.TransformRange[1];
                range[0] = new World.TransformRange
                    { Sources = idPointer, Xs = xPointer, Ys = yPointer, Count = count };
                Assert.Equal(
                    World.BatchStatus.Applied,
                    World.SyncTransforms(World.Capture(world), new ReadOnlySpan<World.TransformRange>(range, 1)));
            }

            World.Process(world);
            World.Rewind(world);
            World.Process(world);
            var restored = new short[64 * 64];
            fixed (short* restoredPointer = restored)
            {
                World.QueryRegion(world, grid, layer, 0, 0, 64, 64, restoredPointer);
            }

            Assert.True(snapshot.AsSpan().SequenceEqual(restored));
        }
        finally
        {
            Assert.True(World.Free(world));
        }
    }

    [Fact]
    public unsafe void MasksCoverWordBoundariesAndTailBitsStayZero()
    {
        const int firstCount = 63;
        const int secondCount = 64;
        const int thirdCount = 65;
        var mine = new World.SourceId[firstCount + secondCount + thirdCount];
        for (var index = 0; index < mine.Length; index++)
        {
            var lane = index / 60 % 60;
            mine[index] = World.Place(_world, _layer, 2f + index % 60, 2f + lane, _stamp, 1);
        }

        var stale = World.Place(_world, _layer, 5f, 5f, _stamp, 1);
        World.Remove(_world, stale);
        World.Process(_world);

        var ids = new World.SourceId[mine.Length];
        var xs = new float[mine.Length];
        var ys = new float[mine.Length];
        for (var index = 0; index < mine.Length; index++)
        {
            ids[index] = mine[index];
            xs[index] = 3f + index % 50;
            var lane = index / 50 % 50;
            ys[index] = 3f + lane;
        }

        ids[0] = stale;
        ids[62] = stale;
        ids[63] = stale;
        ids[63 + 63] = stale;
        ids[63 + 64 + 64] = stale;

        fixed (World.SourceId* idPointer = ids)
        fixed (float* xPointer = xs)
        fixed (float* yPointer = ys)
        {
            var masks = stackalloc ulong[6];
            new Span<ulong>(masks, 6).Fill(0xAAAAAAAAAAAAAAAAUL);
            var range = stackalloc World.TransformRange[3];
            range[0] = new World.TransformRange
            {
                Sources = idPointer, Xs = xPointer, Ys = yPointer,
                Invalid = masks, InvalidWords = 2, Count = firstCount
            };
            range[1] = new World.TransformRange
            {
                Sources = idPointer + firstCount, Xs = xPointer + firstCount, Ys = yPointer + firstCount,
                Invalid = masks + 2, InvalidWords = 1, Count = secondCount
            };
            range[2] = new World.TransformRange
            {
                Sources = idPointer + firstCount + secondCount,
                Xs = xPointer + firstCount + secondCount, Ys = yPointer + firstCount + secondCount,
                Invalid = masks + 3, InvalidWords = 2, Count = thirdCount
            };
            Assert.Equal(
                World.BatchStatus.Applied,
                World.SyncTransforms(World.Capture(_world), new ReadOnlySpan<World.TransformRange>(range, 3)));

            Assert.Equal((1ul << 0) | (1ul << 62), masks[0]);
            Assert.Equal(0xAAAAAAAAAAAAAAAAUL, masks[1]);
            Assert.Equal((1ul << 0) | (1ul << 63), masks[2]);
            Assert.Equal(0UL, masks[3]);
            Assert.Equal(1ul << 0, masks[4]);
            Assert.Equal(0xAAAAAAAAAAAAAAAAUL, masks[5]);
        }

        World.Process(_world);
        Assert.True(World.Query(_world, _grid, _layer, 0, 0, 64, 64) > 0);
    }

    [Fact]
    public unsafe void MalformedAndAliasedDescriptors_RejectBeforeAnyWrite()
    {
        var source = World.Place(_world, _layer, 5f, 5f, _stamp, 1);
        var token = World.Capture(_world);

        var ids = stackalloc World.SourceId[64];
        for (var index = 0; index < 64; index++) ids[index] = source;
        var columns = stackalloc float[128];
        for (var index = 0; index < 128; index++) columns[index] = 7.5f;
        var mask = stackalloc ulong[1];

        var valid = stackalloc World.TransformRange[1];
        valid[0] = new World.TransformRange
        {
            Sources = ids, Xs = columns, Ys = columns + 64,
            Invalid = mask, InvalidWords = 1, Count = 64
        };
        *mask = 0xAAAAAAAAAAAAAAAAUL;
        Assert.Equal(World.BatchStatus.Applied,
            World.SyncTransforms(token, new ReadOnlySpan<World.TransformRange>(valid, 1)));
        Assert.Equal(0UL, *mask);
        World.Process(_world);
        var before = World.Query(_world, _grid, _layer, 0, 0, 64, 64);

        var aliased = stackalloc float[130];
        for (var index = 0; index < 130; index++) aliased[index] = 7.5f;
        var aliasedRange = stackalloc World.TransformRange[1];
        aliasedRange[0] = new World.TransformRange
        {
            Sources = ids, Xs = aliased, Ys = aliased + 66,
            Invalid = (ulong*)(aliased + 100), InvalidWords = 1, Count = 64
        };
        *mask = 0xAAAAAAAAAAAAAAAAUL;
        Assert.Equal(
            World.BatchStatus.OverlappingBuffers,
            World.SyncTransforms(token, new ReadOnlySpan<World.TransformRange>(aliasedRange, 1)));

        var shared = stackalloc World.TransformRange[2];
        shared[0] = new World.TransformRange
        {
            Sources = ids, Xs = columns, Ys = columns + 64,
            Invalid = mask, InvalidWords = 1, Count = 64
        };
        shared[1] = shared[0];
        shared[1].Count = 1;
        Assert.Equal(
            World.BatchStatus.OverlappingBuffers,
            World.SyncTransforms(token, new ReadOnlySpan<World.TransformRange>(shared, 2)));

        var negative = stackalloc World.TransformRange[1];
        negative[0] = new World.TransformRange { Sources = ids, Xs = columns, Ys = columns + 64, Count = -1 };
        Assert.Equal(World.BatchStatus.InvalidRange,
            World.SyncTransforms(token, new ReadOnlySpan<World.TransformRange>(negative, 1)));

        var missing = stackalloc World.TransformRange[1];
        missing[0] = new World.TransformRange { Sources = null, Xs = columns, Ys = columns + 64, Count = 3 };
        Assert.Equal(World.BatchStatus.InvalidRange,
            World.SyncTransforms(token, new ReadOnlySpan<World.TransformRange>(missing, 1)));

        var noY = stackalloc World.TransformRange[1];
        noY[0] = new World.TransformRange { Sources = ids, Xs = columns, Ys = null, Count = 3 };
        Assert.Equal(World.BatchStatus.InvalidRange,
            World.SyncTransforms(token, new ReadOnlySpan<World.TransformRange>(noY, 1)));

        var shortMask = stackalloc World.TransformRange[1];
        shortMask[0] = new World.TransformRange
        {
            Sources = ids, Xs = columns, Ys = columns + 64,
            Invalid = mask, InvalidWords = 0, Count = 64
        };
        Assert.Equal(World.BatchStatus.InvalidRange,
            World.SyncTransforms(token, new ReadOnlySpan<World.TransformRange>(shortMask, 1)));

        var huge = stackalloc World.TransformRange[1];
        huge[0] = new World.TransformRange
        {
            Sources = ids, Xs = columns, Ys = columns + 64, Count = World.MaxSourceSlots + 1
        };
        Assert.Equal(World.BatchStatus.TooManyCommands,
            World.SyncTransforms(token, new ReadOnlySpan<World.TransformRange>(huge, 1)));

        Assert.Equal(0xAAAAAAAAAAAAAAAAUL, *mask);
        World.Process(_world);
        Assert.Equal(before, World.Query(_world, _grid, _layer, 0, 0, 64, 64));
    }

    [Fact]
    public unsafe void StaleAndInvalidTokens_RejectWithoutTouchingMasksOrWorld()
    {
        var world = World.New();
        Grid.New(world, 6, 0f, 0f, 64f);
        var layer = Layer.New(world);
        var stamp = Stamp.Box(2, 2, 1);
        try
        {
            var source = World.Place(world, layer, 5f, 5f, stamp, 1);
            World.Process(world);
            var token = World.Capture(world);

            var ids = stackalloc World.SourceId[1];
            var xs = stackalloc float[1];
            var ys = stackalloc float[1];
            var mask = stackalloc ulong[1];
            ids[0] = source;
            xs[0] = 10.5f;
            ys[0] = 10.5f;
            var range = stackalloc World.TransformRange[1];
            range[0] = new World.TransformRange
            {
                Sources = ids, Xs = xs, Ys = ys,
                Invalid = mask, InvalidWords = 1, Count = 1
            };

            *mask = 0xAAAAAAAAAAAAAAAAUL;
            Assert.Equal(
                World.BatchStatus.Applied,
                World.SyncTransforms(token, new ReadOnlySpan<World.TransformRange>(range, 1)));
            World.Process(world);
            Assert.Equal(0UL, *mask);

            *mask = 0xAAAAAAAAAAAAAAAAUL;
            World.Clear(world);
            Assert.Equal(
                World.BatchStatus.StaleRevision,
                World.SyncTransforms(token, new ReadOnlySpan<World.TransformRange>(range, 1)));
            Assert.Equal(0xAAAAAAAAAAAAAAAAUL, *mask);

            var fresh = World.Capture(world);
            ids[0] = World.Place(world, layer, 5f, 5f, stamp, 1);
            xs[0] = 12.5f;
            Assert.Equal(
                World.BatchStatus.Applied,
                World.SyncTransforms(fresh, new ReadOnlySpan<World.TransformRange>(range, 1)));

            World.Record(world);
            World.Process(world);
            World.Rewind(world);
            *mask = 0xAAAAAAAAAAAAAAAAUL;
            Assert.Equal(
                World.BatchStatus.StaleRevision,
                World.SyncTransforms(fresh, new ReadOnlySpan<World.TransformRange>(range, 1)));
            Assert.Equal(0xAAAAAAAAAAAAAAAAUL, *mask);

            var reused = World.Capture(world);
            Assert.Equal(
                World.BatchStatus.Applied,
                World.SyncTransforms(reused, new ReadOnlySpan<World.TransformRange>(range, 1)));

            Assert.True(World.Free(world));
            *mask = 0xAAAAAAAAAAAAAAAAUL;
            Assert.Equal(
                World.BatchStatus.InvalidWorld,
                World.SyncTransforms(reused, new ReadOnlySpan<World.TransformRange>(range, 1)));
            Assert.Equal(
                World.BatchStatus.InvalidWorld,
                World.SyncTransforms(World.Capture(world), new ReadOnlySpan<World.TransformRange>(range, 1)));
            Assert.Equal(0xAAAAAAAAAAAAAAAAUL, *mask);
        }
        finally
        {
            World.Free(world);
            Stamp.Free(stamp);
        }
    }

    [Fact]
    public unsafe void EmptyRangeSets_AppliedWithoutWork()
    {
        World.Place(_world, _layer, 5f, 5f, _stamp, 1);
        World.Process(_world);
        var token = World.Capture(_world);
        Assert.Equal(World.BatchStatus.Applied, World.SyncTransforms(token, ReadOnlySpan<World.TransformRange>.Empty));

        var mask = stackalloc ulong[1];
        *mask = 0x1234567890ABCDEFUL;
        var empty = stackalloc World.TransformRange[1];
        empty[0] = new World.TransformRange { Invalid = mask, InvalidWords = 1, Count = 0 };
        Assert.Equal(
            World.BatchStatus.Applied,
            World.SyncTransforms(token, new ReadOnlySpan<World.TransformRange>(empty, 1)));
        Assert.Equal(0x1234567890ABCDEFUL, *mask);
    }

    [Fact]
    public unsafe void IndependentWorlds_RemainIsolated()
    {
        var other = World.New();
        var otherGrid = Grid.New(other, 6, 0f, 0f, 64f);
        var otherLayer = Layer.New(other);
        try
        {
            const int count = 16;
            var mine = new World.SourceId[count];
            for (var index = 0; index < count; index++)
            {
                mine[index] = World.Place(_world, _layer, 2f + index, 2f, _stamp, 2);
                World.Place(other, otherLayer, 2f + index, 2f, _stamp, 2);
            }

            World.Process(_world);
            World.Process(other);
            var snapshot = new short[64 * 64];
            var mineBefore = new short[64 * 64];
            fixed (short* snapshotPointer = snapshot)
            fixed (short* mineBeforePointer = mineBefore)
            {
                World.QueryRegion(other, otherGrid, otherLayer, 0, 0, 64, 64, snapshotPointer);
                World.QueryRegion(_world, _grid, _layer, 0, 0, 64, 64, mineBeforePointer);
            }

            var xs = new float[count];
            var ys = new float[count];
            for (var index = 0; index < count; index++)
            {
                xs[index] = 30f + index;
                ys[index] = 30f;
            }

            fixed (World.SourceId* idPointer = mine)
            fixed (float* xPointer = xs)
            fixed (float* yPointer = ys)
            {
                var range = stackalloc World.TransformRange[1];
                range[0] = new World.TransformRange
                    { Sources = idPointer, Xs = xPointer, Ys = yPointer, Count = count };
                Assert.Equal(
                    World.BatchStatus.Applied,
                    World.SyncTransforms(World.Capture(_world), new ReadOnlySpan<World.TransformRange>(range, 1)));
            }

            World.Process(_world);
            var after = new short[64 * 64];
            var mineAfter = new short[64 * 64];
            fixed (short* afterPointer = after)
            fixed (short* mineAfterPointer = mineAfter)
            {
                World.QueryRegion(other, otherGrid, otherLayer, 0, 0, 64, 64, afterPointer);
                World.QueryRegion(_world, _grid, _layer, 0, 0, 64, 64, mineAfterPointer);
            }

            Assert.True(snapshot.AsSpan().SequenceEqual(after));
            Assert.False(mineBefore.AsSpan().SequenceEqual(mineAfter));
        }
        finally
        {
            World.Free(other);
        }
    }
}
