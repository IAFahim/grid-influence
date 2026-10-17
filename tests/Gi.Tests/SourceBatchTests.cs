using System.Runtime.InteropServices;
using Xunit;

namespace Gi.Tests;

public sealed class SourceBatchTests : IDisposable
{
    private readonly byte _batchWorld = World.New();
    private readonly byte _serialWorld = World.New();
    private readonly byte[] _stamps;

    public SourceBatchTests()
    {
        foreach (var world in new[] { _batchWorld, _serialWorld })
        {
            Grid.New(world, 6, 0f, 0f, 64f);
            Grid.New(world, 5, 0f, 0f, 64f);
            var firstLayer = Layer.New(world);
            var secondLayer = Layer.New(world);
            Layer.Sum(world, firstLayer, 1, secondLayer, -1);
        }

        _stamps =
        [
            Stamp.Box(3, 3, 5), Stamp.Tent(5, 5, 20), Stamp.Bell(5, 5, 20),
            Stamp.Disk(3, 10), Stamp.Cone(3, 10, 90), Stamp.Dome(3, 10),
            Stamp.New(new sbyte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }, 3, 3)
        ];
    }

    public void Dispose()
    {
        World.Free(_batchWorld);
        World.Free(_serialWorld);
        foreach (var stamp in _stamps) Assert.True(Stamp.Free(stamp));
    }

    [Fact]
    public void MixedShapesAndOrderedPartialChanges_MatchSerialCalls()
    {
        var commands = new List<World.SourceCommand>();
        for (var index = 0; index < _stamps.Length; index++)
            commands.Add(World.SourceCommand.Place((byte)(index % 2), 10.5f + index * 5, 12.25f,
                _stamps[index], (sbyte)(index + 1), index * 0.3f, 1f + index * 0.125f));

        for (var index = 0; index < _stamps.Length; index++)
        {
            var target = World.SourceTarget.CreatedBy(index);
            commands.Add(World.SourceCommand.Move(target, 8.25f + index * 6, 30.75f));
            commands.Add(World.SourceCommand.Turn(target, -index * 0.25f));
            commands.Add(World.SourceCommand.Scale(target, 0.75f + index * 0.125f));
            commands.Add(World.SourceCommand.SetGain(target, (sbyte)(4 - index)));
            commands.Add(World.SourceCommand.Fade(target, 0, 6));
            commands.Add(World.SourceCommand.Expire(target, 9));
        }

        ApplyAndReplay(commands.ToArray());
        Assert.Equal(0, World.Tick(_batchWorld));
        AssertFieldsEqual();
        for (var tick = 0; tick < 10; tick++) ProcessAndCompare();
    }

    [Fact]
    public void PendingCreationRemovalAndReuse_PreserveGenerationsAndRejectStaleTickets()
    {
        World.SourceCommand[] commands =
        [
            World.SourceCommand.Place(0, 10.5f, 10.5f, _stamps[0], 2),
            World.SourceCommand.Remove(World.SourceTarget.CreatedBy(0)),
            World.SourceCommand.Place(1, 40.5f, 40.5f, _stamps[1], 3),
            World.SourceCommand.Move(World.SourceTarget.CreatedBy(0), 20.5f, 20.5f),
            World.SourceCommand.Update(World.SourceTarget.CreatedBy(2), 35.5f, 35.5f, 0.75f, 1.5f, 4),
            World.SourceCommand.Expire(World.SourceTarget.CreatedBy(0), 1)
        ];

        var results = ApplyAndReplay(commands);
        Assert.NotEqual(results[0].SourceId, results[2].SourceId);
        Assert.Equal(World.SourceResultStatus.InvalidSource, results[3].Status);
        Assert.Equal(World.SourceResultStatus.InvalidSource, results[5].Status);
        ProcessAndCompare();
        ProcessAndCompare();
    }

    [Fact]
    public void ExistingPendingOperationsAndSelfExclusion_MatchSerialCalls()
    {
        var source = PlacePair(0, 10.5f, 10.5f, _stamps[0], 4);
        PlacePair(0, 10.5f, 10.5f, _stamps[0], 2);
        ProcessAndCompare();
        World.Move(_batchWorld, source, 20.5f, 20.5f);
        World.Move(_serialWorld, source, 20.5f, 20.5f);
        ApplyAndReplay(
        [
            World.SourceCommand.Move(source, 40.5f, 40.5f),
            World.SourceCommand.SetGain(source, 8),
            World.SourceCommand.Turn(source, 0.5f),
            World.SourceCommand.Scale(source, 1.5f)
        ]);

        Assert.True(World.TrySense(_batchWorld, 0, 10.5f, 10.5f, source, out var actual));
        Assert.True(World.TrySense(_serialWorld, 0, 10.5f, 10.5f, source, out var expected));
        Assert.Equal(expected, actual);
        AssertFieldsEqual();
        ProcessAndCompare();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnchangedGainAndCompleteState_CancelAnEarlierFade(bool completeState)
    {
        var source = PlacePair(0, 10.5f, 10.5f, _stamps[0], 8);
        ProcessAndCompare();
        ApplyAndReplay(
        [
            World.SourceCommand.Fade(source, 0, 4),
            completeState
                ? World.SourceCommand.Update(source, 10.5f, 10.5f, 0f, 1f, 8)
                : World.SourceCommand.SetGain(source, 8)
        ]);

        for (var tick = 0; tick < 5; tick++) ProcessAndCompare();
        Assert.Equal(40, World.QueryAt(_batchWorld, 0, 0, 10.5f, 10.5f));
    }

    [Fact]
    public void InvalidAnglesAndScalesAndClampedGains_MatchSerialCalls()
    {
        var source = PlacePair(0, 10.5f, 10.5f, _stamps[2], 1);
        ProcessAndCompare();
        ApplyAndReplay(
        [
            World.SourceCommand.Update(source, 20.5f, 30.5f, float.NaN, float.PositiveInfinity, 100),
            World.SourceCommand.Turn(source, -MathF.PI / 2),
            World.SourceCommand.Scale(source, -2),
            World.SourceCommand.Scale(source, 1.25f),
            World.SourceCommand.Fade(source, -100, 0),
            World.SourceCommand.Expire(source, 0)
        ]);
        ProcessAndCompare();
    }

    [Fact]
    public void Rewind_RestoresMixedBatchChangesAndSchedules()
    {
        var source = PlacePair(0, 10.5f, 10.5f, _stamps[0], 8);
        World.Fade(_batchWorld, source, 0, 8);
        World.Fade(_serialWorld, source, 0, 8);
        ProcessAndCompare();
        World.Record(_batchWorld);
        World.Record(_serialWorld);
        ApplyAndReplay(
        [
            World.SourceCommand.Move(source, 40.5f, 40.5f),
            World.SourceCommand.SetGain(source, 2),
            World.SourceCommand.Fade(source, 10, 3),
            World.SourceCommand.Expire(source, 2),
            World.SourceCommand.Place(1, 30.5f, 30.5f, _stamps[3], 2),
            World.SourceCommand.Remove(World.SourceTarget.CreatedBy(4))
        ]);
        ProcessAndCompare();
        World.Rewind(_batchWorld);
        World.Rewind(_serialWorld);
        for (var tick = 0; tick < 4; tick++) ProcessAndCompare();
    }

    [Fact]
    public void ClearAndBulkGrowth_PreserveGenerationBytesOfRetainedCapacity()
    {
        var source = PlacePair(0, 10.5f, 10.5f, _stamps[0], 1);
        World.Remove(_batchWorld, source);
        World.Remove(_serialWorld, source);
        var recycled = PlacePair(0, 20.5f, 20.5f, _stamps[0], 1);
        World.Clear(_batchWorld);
        World.Clear(_serialWorld);
        var commands = new World.SourceCommand[130];
        for (var index = 0; index < commands.Length; index++)
        {
            var row = index / 60;
            commands[index] = World.SourceCommand.Place(0, index % 60 + 0.5f, row + 0.5f, _stamps[0], 1);
        }

        var results = ApplyAndReplay(commands);
        Assert.NotEqual(source, results[0].SourceId);
        Assert.NotEqual(recycled, results[0].SourceId);
        ProcessAndCompare();
    }

    [Fact]
    public void InvalidBatchStructure_IsRejectedBeforeMutatingSourcesOrResults()
    {
        var source = PlacePair(0, 10.5f, 10.5f, _stamps[0], 1);
        ProcessAndCompare();
        World.SourceCommand[] commands =
        [
            World.SourceCommand.Move(source, 40.5f, 40.5f),
            World.SourceCommand.Remove(World.SourceTarget.CreatedBy(2)),
            World.SourceCommand.Place(0, 20.5f, 20.5f, _stamps[0], 2)
        ];
        var results = Enumerable.Repeat(new World.SourceResult(new World.SourceId(123, 7), World.SourceResultStatus.InvalidStamp), commands.Length).ToArray();
        Assert.Equal(World.BatchStatus.InvalidReference, World.ApplyBatch(_batchWorld, commands, results));
        Assert.All(results, result => Assert.Equal(new World.SourceId(123, 7), result.SourceId));
        Assert.Equal(World.BatchStatus.InsufficientResults, World.ApplyBatch(_batchWorld, commands, results.AsSpan(0, 1)));
        Assert.Equal(World.BatchStatus.InvalidCommand, World.ApplyBatch(_batchWorld,
            new[] { World.SourceCommand.Move(source, 40.5f, 40.5f), default(World.SourceCommand) }, results));
        ProcessAndCompare();
    }

    [Fact]
    public void OverlappingInputAndResultMemory_IsRejected()
    {
        var commands = new[] { World.SourceCommand.Place(0, 10.5f, 10.5f, _stamps[0], 1) };
        var overlappingResults = MemoryMarshal.Cast<World.SourceCommand, World.SourceResult>(commands.AsSpan());
        Assert.Equal(World.BatchStatus.OverlappingBuffers, World.ApplyBatch(_batchWorld, commands, overlappingResults));
        ProcessAndCompare();
    }

    [Fact]
    public void LargeCreationAndUpdateBatches_MatchSerialCallsAndAllocateNothingWhenWarm()
    {
        const int count = 16384;
        var creations = new World.SourceCommand[count];
        for (var index = 0; index < count; index++)
        {
            var row = index / 60 % 60;
            creations[index] = World.SourceCommand.Place((byte)(index % 2), index % 60 + 0.5f,
                row + 0.5f, _stamps[index % _stamps.Length], (sbyte)(index % 4 + 1));
        }
        var creationResults = ApplyAndReplay(creations);
        Assert.Equal(count, creationResults.Select(result => result.SourceId).Distinct().Count());
        ProcessAndCompare();
        var firstUpdates = new World.SourceCommand[count];
        var secondUpdates = new World.SourceCommand[count];
        var results = new World.SourceResult[count];
        for (var index = 0; index < count; index++)
        {
            var source = creationResults[index].SourceId;
            var row = index / 60 % 60;
            firstUpdates[index] = World.SourceCommand.Update(source, index % 60 + 1.25f,
                row + 1.25f, index % 5 * 0.25f, 0.75f, (sbyte)(index % 4 + 1));
            secondUpdates[index] = World.SourceCommand.Update(source, index % 60 + 0.5f,
                row + 0.5f, 0f, 1f, (sbyte)(index % 4 + 1));
        }

        ApplyAndReplay(firstUpdates);
        ProcessAndCompare();
        ApplyAndReplay(secondUpdates);
        ProcessAndCompare();
        World.ApplyBatch(_batchWorld, firstUpdates, results);
        World.Process(_batchWorld);
        World.ApplyBatch(_batchWorld, secondUpdates, results);
        World.Process(_batchWorld);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var checksum = 0L;
        for (var tick = 0; tick < 8; tick++)
        {
            var status = World.ApplyBatch(_batchWorld, (tick & 1) == 0 ? firstUpdates : secondUpdates, results);
            if (status != World.BatchStatus.Applied) throw new InvalidOperationException();
            World.Process(_batchWorld);
            checksum += World.Query(_batchWorld, 0, 0, 0, 0, 64, 64);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.NotEqual(0, checksum);
    }

    [Fact]
    public void FailedCreationsAndStaleSources_ReportIndividualFailures()
    {
        var source = PlacePair(0, 10.5f, 10.5f, _stamps[0], 1);
        var commands = new[]
        {
            World.SourceCommand.Place(2, 10.5f, 10.5f, _stamps[0], 1),
            World.SourceCommand.Place(0, 20.5f, 20.5f, 0, 1),
            World.SourceCommand.Move(World.SourceTarget.CreatedBy(0), 30.5f, 30.5f),
            World.SourceCommand.Move(default, 30.5f, 30.5f),
            World.SourceCommand.Remove(source),
            World.SourceCommand.Move(source, 40.5f, 40.5f)
        };
        var results = ApplyAndReplay(commands);
        Assert.Equal(World.SourceResultStatus.InvalidLayer, results[0].Status);
        Assert.Equal(World.SourceResultStatus.InvalidStamp, results[1].Status);
        Assert.Equal(World.SourceResultStatus.InvalidSource, results[2].Status);
        Assert.Equal(World.SourceResultStatus.InvalidSource, results[3].Status);
        Assert.Equal(World.SourceResultStatus.Applied, results[4].Status);
        Assert.Equal(World.SourceResultStatus.InvalidSource, results[5].Status);
        ProcessAndCompare();
    }

    [Fact]
    public void RandomOrderedCommandsAcrossProcessingAndRewind_MatchSerialCalls()
    {
        var random = new Random(1734);
        var previousTargets = new List<World.SourceTarget>();
        for (var frame = 0; frame < 12; frame++)
        {
            if (frame == 3)
            {
                World.Record(_batchWorld);
                World.Record(_serialWorld);
            }

            var commands = new List<World.SourceCommand>();
            var targets = new List<World.SourceTarget>(previousTargets);
            for (var index = 0; index < 512; index++)
            {
                var kind = targets.Count == 0 ? 0 : random.Next(9);
                var positionX = 4f + random.NextSingle() * 54f;
                var positionY = 4f + random.NextSingle() * 54f;
                var gain = (sbyte)random.Next(-16, 17);
                if (kind == 0)
                {
                    targets.Add(World.SourceTarget.CreatedBy(commands.Count));
                    commands.Add(World.SourceCommand.Place((byte)random.Next(2), positionX, positionY,
                        _stamps[random.Next(_stamps.Length)], gain));
                    continue;
                }

                var target = targets[random.Next(targets.Count)];
                commands.Add(kind switch
                {
                    1 => World.SourceCommand.Move(target, positionX, positionY),
                    2 => World.SourceCommand.Turn(target, random.NextSingle() * 12f - 6f),
                    3 => World.SourceCommand.Scale(target, random.NextSingle() * 2f),
                    4 => World.SourceCommand.SetGain(target, gain),
                    5 => World.SourceCommand.Fade(target, gain, random.Next(-1, 9)),
                    6 => World.SourceCommand.Expire(target, random.Next(-1, 9)),
                    7 => World.SourceCommand.Remove(target),
                    _ => World.SourceCommand.Update(target, positionX, positionY,
                        random.NextSingle(), random.NextSingle() * 2f, gain)
                });
            }

            var results = ApplyAndReplay(commands.ToArray());
            previousTargets = results.Where(result => result.Status == World.SourceResultStatus.Applied)
                .Select(result => World.SourceTarget.Existing(result.SourceId)).TakeLast(128).ToList();
            ProcessAndCompare();
            if (frame == 7)
            {
                World.Rewind(_batchWorld);
                World.Rewind(_serialWorld);
                AssertSourceStatesEqual();
                AssertPendingEqual();
                ProcessAndCompare();
            }
        }
    }

    [Fact]
    public async Task DifferentWorlds_CanApplyLargeBatchesConcurrently()
    {
        const int count = 16384;
        var creations = new World.SourceCommand[count];
        for (var index = 0; index < count; index++)
        {
            var row = index / 60 % 60;
            creations[index] = World.SourceCommand.Place(0, index % 60 + 0.5f, row + 0.5f, _stamps[0], 1);
        }
        var firstResults = new World.SourceResult[count];
        var secondResults = new World.SourceResult[count];

        await Task.WhenAll(
            Task.Run(() => ApplyConcurrentFrames(_batchWorld, creations, firstResults)),
            Task.Run(() => ApplyConcurrentFrames(_serialWorld, creations, secondResults)));

        Assert.Equal(firstResults.Select(result => result.SourceId), secondResults.Select(result => result.SourceId));
        Assert.Equal(4, World.Tick(_batchWorld));
        Assert.Equal(4, World.Tick(_serialWorld));
        AssertFieldsEqual();
    }

    [Theory]
    [InlineData(64)]
    [InlineData(8192)]
    public void FullUpdatesWithLateDuplicatesAndPendingChanges_MatchSerialCalls(int count)
    {
        var creations = new World.SourceCommand[count];
        for (var index = 0; index < count; index++)
        {
            var row = index / 60 % 60;
            creations[index] = World.SourceCommand.Place(0, index % 60 + 0.5f,
                row + 0.5f, _stamps[0], 2);
        }
        var sources = ApplyAndReplay(creations);
        ProcessAndCompare();
        var firstSource = sources[0].SourceId;
        World.Move(_batchWorld, firstSource, 40.5f, 40.5f);
        World.Move(_serialWorld, firstSource, 40.5f, 40.5f);
        World.Fade(_batchWorld, firstSource, 0, 3);
        World.Fade(_serialWorld, firstSource, 0, 3);
        var commands = new World.SourceCommand[count + 1];
        for (var index = 0; index < count; index++)
        {
            var row = index / 60 % 60;
            commands[index] = World.SourceCommand.Update(sources[index].SourceId, index % 60 + 0.25f,
                row + 0.25f, 0.25f, 0.75f, 4);
        }
        commands[count] = World.SourceCommand.Update(firstSource, 20.5f, 20.5f, 0f, 1f, 2);
        ApplyAndReplay(commands);
        for (var frame = 0; frame < 4; frame++) ProcessAndCompare();
    }

    [Fact]
    public void FullUpdateWithoutDepositChanges_StillCancelsFading()
    {
        var source = PlacePair(0, 10.5f, 10.5f, _stamps[0], 8);
        ProcessAndCompare();
        World.Fade(_batchWorld, source, 0, 4);
        World.Fade(_serialWorld, source, 0, 4);
        World.Record(_batchWorld);
        World.Record(_serialWorld);
        ApplyAndReplay([World.SourceCommand.Update(source, 10.5f, 10.5f, 0f, 1f, 8)]);
        ProcessAndCompare();
        World.Rewind(_batchWorld);
        World.Rewind(_serialWorld);
        for (var frame = 0; frame < 4; frame++) ProcessAndCompare();
    }

    [Fact]
    public void EmptyAndInvalidWorldBatches_DoNotWriteResults()
    {
        Assert.Equal(World.BatchStatus.Applied, World.ApplyBatch(_batchWorld, [], []));
        var results = new[] { new World.SourceResult(new World.SourceId(123, 7), World.SourceResultStatus.InvalidStamp) };
        Assert.Equal(World.BatchStatus.InvalidWorld, World.ApplyBatch(byte.MaxValue,
            new[] { World.SourceCommand.Place(0, 10.5f, 10.5f, _stamps[0], 1) }, results));
        Assert.Equal(new World.SourceId(123, 7), results[0].SourceId);
        Assert.Equal(0, World.Tick(_batchWorld));
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(64, 128, 0)]
    [InlineData(0, 0, 1)]
    [InlineData(64, 128, 1)]
    public void ReusedSlotsChangingLayersBeforeProcess_RetractTheOriginalLayer(
        int remainingOriginalSources, int existingTargetSources, int stampIndex)
    {
        const int count = 64;
        var creations = new World.SourceCommand[count];
        for (var index = 0; index < count; index++)
            creations[index] = World.SourceCommand.Place(0, 10.5f, 10.5f, _stamps[stampIndex], 2);
        var sources = ApplyAndReplay(creations);
        for (var index = 0; index < remainingOriginalSources; index++)
            PlacePair(0, 10.5f, 10.5f, _stamps[stampIndex], 2);
        for (var index = 0; index < existingTargetSources; index++)
            PlacePair(1, 50.5f, 50.5f, _stamps[stampIndex], 2);
        ProcessAndCompare();
        var originalValue = World.QueryAt(_batchWorld, 0, 0, 10.5f, 10.5f);
        World.Record(_batchWorld);
        World.Record(_serialWorld);
        var commands = new World.SourceCommand[count * 2];
        for (var index = 0; index < count; index++)
        {
            commands[index] = World.SourceCommand.Remove(sources[index].SourceId);
            commands[count + index] = World.SourceCommand.Place(1, 40.5f, 40.5f, _stamps[stampIndex], 3);
        }

        ApplyAndReplay(commands);
        ProcessAndCompare();
        var expectedOriginalValue = originalValue * remainingOriginalSources / (count + remainingOriginalSources);
        Assert.Equal(expectedOriginalValue, World.QueryAt(_batchWorld, 0, 0, 10.5f, 10.5f));
        Assert.Equal(expectedOriginalValue, World.QueryAt(_serialWorld, 0, 0, 10.5f, 10.5f));
        Assert.True(World.QueryAt(_batchWorld, 0, 1, 40.5f, 40.5f) > 0);
        World.Rewind(_batchWorld);
        World.Rewind(_serialWorld);
        ProcessAndCompare();
        Assert.Equal(originalValue, World.QueryAt(_batchWorld, 0, 0, 10.5f, 10.5f));
        Assert.Equal(0, World.QueryAt(_batchWorld, 0, 1, 40.5f, 40.5f));
    }

    private static void ApplyConcurrentFrames(byte world, World.SourceCommand[] creations, World.SourceResult[] results)
    {
        Assert.Equal(World.BatchStatus.Applied, World.ApplyBatch(world, creations, results));
        World.Process(world);
        var updates = new World.SourceCommand[creations.Length];
        for (var frame = 0; frame < 3; frame++)
        {
            for (var index = 0; index < updates.Length; index++)
            {
                var row = index / 60 % 60;
                updates[index] = World.SourceCommand.Update(results[index].SourceId, index % 60 + 0.25f * frame,
                    row + 0.25f * frame, frame * 0.25f, 1f, 2);
            }
            Assert.Equal(World.BatchStatus.Applied, World.ApplyBatch(world, updates, results));
            World.Process(world);
        }
    }

    private World.SourceId PlacePair(byte layer, float positionX, float positionY, byte stamp, sbyte gain)
    {
        var source = World.Place(_batchWorld, layer, positionX, positionY, stamp, gain);
        Assert.Equal(source, World.Place(_serialWorld, layer, positionX, positionY, stamp, gain));
        return source;
    }

    private World.SourceResult[] ApplyAndReplay(World.SourceCommand[] commands)
    {
        var results = new World.SourceResult[commands.Length];
        Assert.Equal(World.BatchStatus.Applied, World.ApplyBatch(_batchWorld, commands, results));
        var serialResults = new World.SourceId[commands.Length];
        for (var index = 0; index < commands.Length; index++)
        {
            ref readonly var command = ref commands[index];
            var target = command.Target.Pending ? serialResults[command.Target.Command] : command.Target.Source;
            if (command.Kind == SourceCommandKind.Place)
            {
                var source = World.Place(_serialWorld, command.Layer, command.PositionX, command.PositionY, command.Stamp, command.Gain);
                serialResults[index] = source;
                Assert.Equal(source, results[index].SourceId);
                World.Turn(_serialWorld, source, command.HeadingRadians);
                World.Scale(_serialWorld, source, command.ScaleValue);
                continue;
            }

            serialResults[index] = target;
            switch (command.Kind)
            {
                case SourceCommandKind.Update:
                    World.Move(_serialWorld, target, command.PositionX, command.PositionY);
                    World.Turn(_serialWorld, target, command.HeadingRadians);
                    World.Scale(_serialWorld, target, command.ScaleValue);
                    World.SetGain(_serialWorld, target, command.Gain);
                    break;
                case SourceCommandKind.Move: World.Move(_serialWorld, target, command.PositionX, command.PositionY); break;
                case SourceCommandKind.Turn: World.Turn(_serialWorld, target, command.HeadingRadians); break;
                case SourceCommandKind.Scale: World.Scale(_serialWorld, target, command.ScaleValue); break;
                case SourceCommandKind.SetGain: World.SetGain(_serialWorld, target, command.Gain); break;
                case SourceCommandKind.Remove: World.Remove(_serialWorld, target); break;
                case SourceCommandKind.Fade: World.Fade(_serialWorld, target, command.Gain, command.Ticks); break;
                case SourceCommandKind.Expire: World.Expire(_serialWorld, target, command.Ticks); break;
            }
        }

        return results;
    }

    private unsafe void AssertPendingEqual()
    {
        var actual = World.GetContext(_batchWorld);
        var expected = World.GetContext(_serialWorld);
        Assert.Equal(expected->Changed.Length, actual->Changed.Length);
        for (var index = 0; index < actual->Changed.Length; index++)
            Assert.Equal(expected->Changed.Pointer[index], actual->Changed.Pointer[index]);

        Assert.Equal(expected->Pending.Length, actual->Pending.Length);
        for (var index = 0; index < actual->Pending.Length; index++)
        {
            var a = actual->Pending.Pointer[index];
            var e = expected->Pending.Pointer[index];
            Assert.True(
                a.Slot == e.Slot && a.X.Equals(e.X) && a.Y.Equals(e.Y) && a.Stamp == e.Stamp &&
                a.Layer == e.Layer && a.Gain == e.Gain && a.Alive == e.Alive &&
                a.Angle == e.Angle && a.Scale == e.Scale && a.Gen == e.Gen,
                $"pending {index}: slot {a.Slot}");
        }
    }

    private void ProcessAndCompare()
    {
        World.Process(_batchWorld);
        World.Process(_serialWorld);
        Assert.Equal(World.Tick(_serialWorld), World.Tick(_batchWorld));
        AssertSourceStatesEqual();
        AssertFieldsEqual();
    }

    private unsafe void AssertSourceStatesEqual()
    {
        var actual = &World.GetContext(_batchWorld)->Sources;
        var expected = &World.GetContext(_serialWorld)->Sources;
        Assert.Equal(expected->Count, actual->Count);
        for (var slot = 0; slot < actual->Count; slot++)
        {
            Assert.True(expected->Alive[slot] == actual->Alive[slot], $"alive {slot}");
            if (expected->Alive[slot] == 0) continue;
            var expectedState = (expected->X[slot], expected->Y[slot], expected->Gain[slot],
                expected->Stamp[slot], expected->Layer[slot], expected->Angle[slot], expected->Scale[slot],
                expected->Gen[slot], expected->Timed.Pointer[slot]);
            var actualState = (actual->X[slot], actual->Y[slot], actual->Gain[slot],
                actual->Stamp[slot], actual->Layer[slot], actual->Angle[slot], actual->Scale[slot],
                actual->Gen[slot], actual->Timed.Pointer[slot]);
            Assert.True(expectedState == actualState, $"slot {slot}: expected {expectedState}, actual {actualState}");
        }
    }

    private void AssertFieldsEqual()
    {
        for (byte grid = 0; grid < 2; grid++)
        for (byte layer = 0; layer < 3; layer++)
        {
            var size = grid == 0 ? 64 : 32;
            Assert.Equal(ReadGrid(_serialWorld, grid, layer, size), ReadGrid(_batchWorld, grid, layer, size));
        }
    }

    private static unsafe short[] ReadGrid(byte world, byte grid, byte layer, int size)
    {
        var cells = new short[size * size];
        fixed (short* destination = cells) World.QueryRegion(world, grid, layer, 0, 0, size, size, destination);
        return cells;
    }
}
