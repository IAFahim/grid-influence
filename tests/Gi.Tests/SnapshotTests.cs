using Xunit;

namespace Gi.Tests;

public unsafe class SnapshotTests
{
    [Theory]
    [InlineData(41)]
    [InlineData(42)]
    [InlineData(43)]
    [InlineData(44)]
    [InlineData(45)]
    [InlineData(46)]
    public void SnapshotRestoreResumesLikeATwin(int seed)
    {
        var a = World.New();
        var b = World.New();
        var stamps = new List<byte>
        {
            Stamp.Box(3, 3, 30), Stamp.Box(6, 2, -25), Stamp.Tent(6, 6, 40), Stamp.Disk(4, 50), Stamp.Bell(5, 5, 20)
        };
        try
        {
            Grid.New(a, 6, 0f, 0f, 64f);
            Grid.New(a, 8, -32f, -32f, 256f);
            var exact = Layer.New(a, Layer.Precision.Exact);
            var cell = Layer.New(a, Layer.Precision.Cell);
            var transient = Layer.New(a, Layer.Residency.Transient, Layer.Precision.Cell);
            var sum = Layer.Sum(a, exact, 1, cell, 1);
            Layer.Mask(a, sum, cell, -20, 20);

            var ids = new List<World.SourceId>();
            var setup = new Random(seed);
            for (var i = 0; i < 300; i++)
            {
                var layer = setup.Next(2) == 0 ? exact : cell;
                var x = (float)(setup.NextDouble() * 128.0 - 32.0);
                var y = (float)(setup.NextDouble() * 128.0 - 32.0);
                var id = World.Place(a, layer, x, y, stamps[setup.Next(stamps.Count)], (sbyte)setup.Next(1, 7));
                ids.Add(id);
                if (setup.Next(3) == 0)
                {
                    World.Fade(a, id, (sbyte)setup.Next(-6, 7), setup.Next(1, 16));
                    World.Expire(a, id, setup.Next(0, 30));
                }

                if (setup.Next(4) == 0) World.Remove(a, id);
            }

            for (var frame = 0; frame < 5; frame++)
            {
                foreach (var id in ids)
                {
                    World.Move(a, id, (float)(setup.NextDouble() * 128.0 - 32.0),
                        (float)(setup.NextDouble() * 128.0 - 32.0));
                    World.Turn(a, id, (float)(setup.NextDouble() * 6.0));
                }

                World.Splat(a, transient, new[] { (float)(setup.NextDouble() * 64.0) },
                    new[] { (float)(setup.NextDouble() * 64.0) },
                    stamps[0], 5);
                World.Process(a);
            }

            var size = World.SnapshotSize(a);
            Assert.True(size > 0);
            var buffer = new byte[size];
            Assert.True(World.TrySnapshot(a, buffer, out var written));
            Assert.Equal(size, written);
            Assert.False(World.TrySnapshot(a, buffer.AsSpan(0, size - 1), out _));
            Assert.True(World.TryRestore(b, buffer));

            for (byte g = 0; g < 2; g++)
            for (byte l = 0; l < 5; l++)
                Assert.Equal(World.LayerHash(a, g, l), World.LayerHash(b, g, l));

            foreach (var id in ids)
            {
                Assert.Equal(
                    World.TryGetSchedule(a, id, out var scheduleA, out _, out _, out _, out _, out _),
                    World.TryGetSchedule(b, id, out var scheduleB, out _, out _, out _, out _, out _));
                Assert.Equal(scheduleA, scheduleB);
            }

            var run = new Random(seed * 7919);
            for (var frame = 0; frame < 12; frame++)
            {
                for (var op = 0; op < 80; op++)
                {
                    var id = ids[run.Next(ids.Count)];
                    var x = (float)(run.NextDouble() * 128.0 - 32.0);
                    var y = (float)(run.NextDouble() * 128.0 - 32.0);
                    var gain = (sbyte)run.Next(-6, 7);
                    var angle = (float)(run.NextDouble() * 6.0);
                    var scale = (float)(run.NextDouble() * 2.0 + 0.5);
                    var ticks = run.Next(0, 10);
                    switch (run.Next(7))
                    {
                        case 0:
                            World.Move(a, id, x, y);
                            World.Move(b, id, x, y);
                            break;
                        case 1:
                            World.SetGain(a, id, gain);
                            World.SetGain(b, id, gain);
                            break;
                        case 2:
                            World.Turn(a, id, angle);
                            World.Turn(b, id, angle);
                            break;
                        case 3:
                            World.Scale(a, id, scale);
                            World.Scale(b, id, scale);
                            break;
                        case 4:
                            World.Fade(a, id, gain, ticks);
                            World.Fade(b, id, gain, ticks);
                            break;
                        case 5:
                            World.Expire(a, id, ticks);
                            World.Expire(b, id, ticks);
                            break;
                        default:
                            World.Remove(a, id);
                            World.Remove(b, id);
                            break;
                    }
                }

                var sx = (float)(run.NextDouble() * 64.0);
                var sy = (float)(run.NextDouble() * 64.0);
                World.Splat(a, transient, new[] { sx }, new[] { sy }, stamps[1], 4);
                World.Splat(b, transient, new[] { sx }, new[] { sy }, stamps[1], 4);
                World.Process(a);
                World.Process(b);
                CompareHashes(a, b);
            }

            for (var frame = 0; frame < 3; frame++)
            {
                World.Process(a);
                World.Process(b);
                CompareHashes(a, b);
            }

            CompareFields(a, b);
            foreach (var id in ids)
            {
                var foundA = World.TryGetSourceSpec(a, id, out var specA);
                var foundB = World.TryGetSourceSpec(b, id, out var specB);
                Assert.Equal(foundA, foundB);
                if (foundA) Assert.Equal(specA, specB);
                Assert.Equal(
                    World.TryGetSchedule(a, id, out var scheduleA, out _, out _, out _, out _, out _),
                    World.TryGetSchedule(b, id, out var scheduleB, out _, out _, out _, out _, out _));
                Assert.Equal(scheduleA, scheduleB);
            }
        }
        finally
        {
            World.Free(a);
            World.Free(b);
            foreach (var s in stamps) Stamp.Free(s);
        }
    }

    [Theory]
    [InlineData(2_000_000, 0, 2)]
    [InlineData(2_000_001, 0, 2)]
    [InlineData(2_100_000, 1, 2)]
    [InlineData(2_100_001, 1, 2)]
    [InlineData(2_200_000, 2, 2)]
    [InlineData(2_200_001, 2, 2)]
    [InlineData(3_000_000, 0, 3)]
    [InlineData(3_000_001, 0, 3)]
    [InlineData(3_100_000, 1, 3)]
    [InlineData(3_100_001, 1, 3)]
    [InlineData(3_200_000, 2, 3)]
    [InlineData(3_200_001, 2, 3)]
    public void SnapshotRestoreMatchesTheCorpusLineForLine(int seed, int mode, int profile)
    {
        var precision = (PrecisionMode)mode;
        var residency = (StreamProfile)profile;
        var setup = StreamGenerator.Generate(seed, precision, residency);
        var name = precision.ToString().ToLowerInvariant();
        var modeText = residency == StreamProfile.Transient ? "transient-" + name : name;

        var baseline = new StringWriter();
        CorpusRunner.Run(setup, modeText, baseline);

        var swapped = new StringWriter();
        var buffer = new byte[1 << 20];
        var done = false;
        CorpusRunner.Run(setup, modeText, swapped, w =>
        {
            if (done || !World.TrySnapshot(w, buffer, out var written)) return w;
            done = true;
            var twin = World.New();
            Assert.True(World.TryRestore(twin, buffer.AsSpan(0, written)));
            World.Free(w);
            return twin;
        });
        Assert.True(done);
        Assert.Equal(baseline.ToString(), swapped.ToString());
    }

    [Fact]
    public void SnapshotRoundTripsAnEmptyWorld()
    {
        var a = World.New();
        var b = World.New();
        try
        {
            var size = World.SnapshotSize(a);
            Assert.True(size > 0);
            var buffer = new byte[size];
            Assert.True(World.TrySnapshot(a, buffer, out var written));
            Assert.Equal(size, written);
            Assert.True(World.TryRestore(b, buffer));
            Assert.Equal(0, World.Tick(b));
            Assert.Equal(0, Layer.Count(b));
            Assert.Equal(0, Grid.Count(b));
        }
        finally
        {
            World.Free(a);
            World.Free(b);
        }
    }

    [Fact]
    public void RestoredFadesResumeTheSameStaircase()
    {
        var a = World.New();
        var b = World.New();
        var stamp = Stamp.Box(4, 4, 25);
        try
        {
            Grid.New(a, 5, 0f, 0f, 32f);
            var layer = Layer.New(a);
            var id = World.Place(a, layer, 16f, 16f, stamp, 1);
            World.Fade(a, id, 10, 13);
            for (var frame = 0; frame < 5; frame++) World.Process(a);

            var buffer = Snapshot(a);
            Assert.True(World.TryRestore(b, buffer));
            for (var frame = 0; frame < 15; frame++)
            {
                World.Process(a);
                World.Process(b);
                var foundA = World.TryGetSourceSpec(a, id, out var specA);
                var foundB = World.TryGetSourceSpec(b, id, out var specB);
                Assert.Equal(foundA, foundB);
                Assert.Equal(specA.Gain, specB.Gain);
                Assert.Equal(
                    World.TryGetSchedule(a, id, out _, out _, out _, out _, out var leftA, out _),
                    World.TryGetSchedule(b, id, out _, out _, out _, out _, out var leftB, out _));
                Assert.Equal(leftA, leftB);
            }
        }
        finally
        {
            World.Free(a);
            World.Free(b);
            Stamp.Free(stamp);
        }
    }

    [Fact]
    public void RestoredExpiriesFireOnTheSameTick()
    {
        var a = World.New();
        var b = World.New();
        var stamp = Stamp.Box(4, 4, 25);
        try
        {
            Grid.New(a, 5, 0f, 0f, 32f);
            var layer = Layer.New(a);
            var id = World.Place(a, layer, 16f, 16f, stamp, 5);
            World.Expire(a, id, 8);
            for (var frame = 0; frame < 3; frame++) World.Process(a);

            var buffer = Snapshot(a);
            Assert.True(World.TryRestore(b, buffer));
            for (var frame = 0; frame < 8; frame++)
            {
                World.Process(a);
                World.Process(b);
                Assert.Equal(World.TryGetSourceSpec(a, id, out _), World.TryGetSourceSpec(b, id, out _));
            }

            Assert.False(World.TryGetSourceSpec(a, id, out _));
            Assert.False(World.TryGetSourceSpec(b, id, out _));
        }
        finally
        {
            World.Free(a);
            World.Free(b);
            Stamp.Free(stamp);
        }
    }

    [Fact]
    public void RestoredWorldKeepsChangeEpochsAcrossTheBoundary()
    {
        var a = World.New();
        var b = World.New();
        var stamp = Stamp.Box(4, 4, 25);
        try
        {
            Grid.New(a, 5, 0f, 0f, 32f);
            var layer = Layer.New(a);
            var first = World.Place(a, layer, 10f, 10f, stamp, 5);
            var second = World.Place(a, layer, 22f, 22f, stamp, 5);
            World.Process(a);
            World.Move(a, second, 24f, 24f);
            World.Process(a);
            World.Move(a, first, 12f, 12f);
            World.Process(a);

            var buffer = Snapshot(a);
            Assert.True(World.TryRestore(b, buffer));
            var tilesA = new int[64];
            var tilesB = new int[64];
            foreach (var since in new[] { 1, 2, 3 })
                fixed (int* pA = tilesA)
                fixed (int* pB = tilesB)
                {
                    var countA = World.ChangedTiles(a, 0, 0, since, pA);
                    var countB = World.ChangedTiles(b, 0, 0, since, pB);
                    Assert.Equal(countA, countB);
                    for (var i = 0; i < countA; i++) Assert.Equal(tilesA[i], tilesB[i]);
                }

            fixed (int* pA = tilesA)
            fixed (int* pB = tilesB)
            {
                var countA = World.ChangedTiles(a, 0, 0, pA);
                var countB = World.ChangedTiles(b, 0, 0, pB);
                Assert.Equal(countA, countB);
                for (var i = 0; i < countA; i++) Assert.Equal(tilesA[i], tilesB[i]);
            }
        }
        finally
        {
            World.Free(a);
            World.Free(b);
            Stamp.Free(stamp);
        }
    }

    [Fact]
    public void RestoredWorldKeepsAllocatedZeroEpochs()
    {
        var a = World.New();
        var b = World.New();
        var zero = Stamp.Box(2, 2, 0);
        try
        {
            Grid.New(a, 5, 0f, 0f, 32f);
            var layer = Layer.New(a, Layer.Residency.Transient, Layer.Precision.Cell);
            var idle = Layer.New(a);
            Assert.Equal(1, World.Splat(a, layer, new[] { 16f }, new[] { 16f }, zero, 5));
            World.Process(a);

            var buffer = Snapshot(a);
            Assert.True(World.TryRestore(b, buffer));

            var tiles = new int[4];
            fixed (int* p = tiles)
            {
                var countA = World.ChangedTiles(a, 0, layer, -1, p);
                var countB = World.ChangedTiles(b, 0, layer, -1, p);
                Assert.Equal(countA, countB);
                Assert.Equal(1, countB);
                Assert.Equal(0, World.ChangedTiles(a, 0, idle, -1, p));
                Assert.Equal(0, World.ChangedTiles(b, 0, idle, -1, p));
            }

            Assert.Equal(World.Changed(a, layer, 16f, 16f, 4f, -1), World.Changed(b, layer, 16f, 16f, 4f, -1));
            Assert.True(World.Changed(b, layer, 16f, 16f, 4f, -1));
            Assert.Equal(World.Changed(a, layer, 16f, 16f, 4f, 0), World.Changed(b, layer, 16f, 16f, 4f, 0));
            Assert.False(World.Changed(b, layer, 16f, 16f, 4f, 0));
        }
        finally
        {
            World.Free(a);
            World.Free(b);
            Stamp.Free(zero);
        }
    }

    [Fact]
    public void RestoredFreeRingReusesLowestDeadSlotAndHonorsRetiredSlots()
    {
        var a = World.New();
        var b = World.New();
        var stamp = Stamp.Box(3, 3, 20);
        try
        {
            Grid.New(a, 5, 0f, 0f, 32f);
            var layer = Layer.New(a);
            var ids = new List<World.SourceId>();
            for (var i = 0; i < 6; i++) ids.Add(World.Place(a, layer, 4f + i * 4, 16f, stamp, 5));

            foreach (var index in new[] { 4, 1, 3, 2 }) World.Remove(a, ids[index]);
            World.Process(a);
            var columns = &World.GetContext(a)->Sources;
            columns->Gen[2] = uint.MaxValue;
            var expectedGeneration = columns->Gen[1] + 1;

            var buffer = Snapshot(a);
            Assert.True(World.TryRestore(b, buffer));
            var first = World.Place(b, layer, 8f, 8f, stamp, 5);
            Assert.Equal(1, (int)first.Slot);
            Assert.Equal(expectedGeneration, first.Generation);
            Assert.Equal(3, (int)World.Place(b, layer, 8f, 8f, stamp, 5).Slot);
            Assert.Equal(4, (int)World.Place(b, layer, 8f, 8f, stamp, 5).Slot);
            Assert.Equal(6, (int)World.Place(b, layer, 8f, 8f, stamp, 5).Slot);
        }
        finally
        {
            World.Free(a);
            World.Free(b);
            Stamp.Free(stamp);
        }
    }

    [Fact]
    public void SnapshotsRejectNonQuiescentWorlds()
    {
        var a = World.New();
        var stamp = Stamp.Box(3, 3, 20);
        try
        {
            Grid.New(a, 5, 0f, 0f, 32f);
            var layer = Layer.New(a);
            var id = World.Place(a, layer, 8f, 8f, stamp, 5);
            Assert.Equal(0, World.SnapshotSize(a));
            Assert.False(World.TrySnapshot(a, new byte[64], out var written));
            Assert.Equal(0, written);

            World.Process(a);
            Assert.True(World.SnapshotSize(a) > 0);

            World.Record(a);
            Assert.Equal(0, World.SnapshotSize(a));
            World.StopRecording(a);
            Assert.True(World.SnapshotSize(a) > 0);

            World.SetGain(a, id, 6);
            Assert.Equal(0, World.SnapshotSize(a));
            World.Process(a);
            Assert.True(World.SnapshotSize(a) > 0);
        }
        finally
        {
            World.Free(a);
            Stamp.Free(stamp);
        }
    }

    [Fact]
    public void SnapshotsRejectInFlightTransientSplats()
    {
        var a = World.New();
        var b = World.New();
        var stamp = Stamp.Box(2, 2, 40);
        try
        {
            Grid.New(a, 5, 0f, 0f, 32f);
            var cell = Layer.New(a, Layer.Residency.Transient, Layer.Precision.Cell);
            var exact = Layer.New(a, Layer.Residency.Transient, Layer.Precision.Exact);
            World.Process(a);
            Assert.True(World.SnapshotSize(a) > 0);

            World.Splat(a, cell, new[] { 16f }, new[] { 16f }, stamp, 5);
            Assert.Equal(0, World.SnapshotSize(a));
            Assert.False(World.TrySnapshot(a, new byte[64], out var written));
            Assert.Equal(0, written);

            World.Splat(a, cell, new[] { 500f }, new[] { 500f }, stamp, 5);
            World.Splat(a, cell, new[] { 16f }, new[] { 16f }, stamp, 0);
            Assert.Equal(0, World.SnapshotSize(a));
            World.Process(a);
            Assert.True(World.SnapshotSize(a) > 0);

            World.Splat(a, exact, new[] { 16.3f }, new[] { 16.7f }, stamp, 5);
            Assert.Equal(0, World.SnapshotSize(a));
            World.Process(a);

            var size = World.SnapshotSize(a);
            var buffer = new byte[size];
            Assert.True(World.TrySnapshot(a, buffer, out _));
            Assert.True(World.TryRestore(b, buffer));

            World.Splat(a, exact, new[] { 16.9f }, new[] { 16.2f }, stamp, 3);
            World.Splat(b, exact, new[] { 16.9f }, new[] { 16.2f }, stamp, 3);
            World.Splat(a, cell, new[] { 8f }, new[] { 8f }, stamp, 4);
            World.Splat(b, cell, new[] { 8f }, new[] { 8f }, stamp, 4);
            World.Process(a);
            World.Process(b);
            Assert.Equal(World.LayerHash(a, 0, cell), World.LayerHash(b, 0, cell));
            Assert.Equal(World.LayerHash(a, 0, exact), World.LayerHash(b, 0, exact));
        }
        finally
        {
            World.Free(a);
            World.Free(b);
            Stamp.Free(stamp);
        }
    }

    [Fact]
    public void SnapshotsRejectDeadHandlesAndShortBuffers()
    {
        Assert.Equal(0, World.SnapshotSize(200));
        Assert.False(World.TrySnapshot(200, new byte[64], out _));
        Assert.False(World.TryRestore(200, new byte[64]));

        var a = World.New();
        var stamp = Stamp.Box(3, 3, 20);
        try
        {
            Grid.New(a, 5, 0f, 0f, 32f);
            var layer = Layer.New(a);
            World.Place(a, layer, 8f, 8f, stamp, 5);
            World.Process(a);
            var size = World.SnapshotSize(a);
            var buffer = new byte[size];
            Assert.True(World.TrySnapshot(a, buffer, out _));
            Assert.False(World.TrySnapshot(a, buffer.AsSpan(0, size - 1), out _));
        }
        finally
        {
            World.Free(a);
            Stamp.Free(stamp);
        }
    }

    [Fact]
    public void RestoreRejectsMalformedBuffers()
    {
        var buffer = SmallWorldSnapshot(out var a, out var stamp);
        var target = World.New();
        try
        {
            Assert.True(World.TryRestore(target, buffer));
            World.Free(target);
            target = World.New();
            Rejects(buffer, target, data => data[0] ^= 0xFF);
            Rejects(buffer, target, data => data[4] ^= 0xFF);
            Rejects(buffer, target, data => data[6] = 40);
            Rejects(buffer, target, data => data[7] = 40);
            Rejects(buffer, target, data => data[^1] ^= 0xFF);

            var truncated = buffer.AsSpan(0, buffer.Length - 1).ToArray();
            Assert.False(World.TryRestore(target, truncated));
            var extended = buffer.Concat(new byte[] { 0 }).ToArray();
            Assert.False(World.TryRestore(target, extended));
            Assert.False(World.TryRestore(target, ReadOnlySpan<byte>.Empty));
        }
        finally
        {
            World.Free(target);
            World.Free(a);
            Stamp.Free(stamp);
        }
    }

    [Fact]
    public void RestoreRejectsStampDriftAndAcceptsIdenticalRebakedStamps()
    {
        var buffer = SmallWorldSnapshot(out var a, out var stamp);
        var world = World.New();
        try
        {
            Assert.True(World.TryRestore(world, buffer));
            World.Free(world);
            World.Free(a);
            a = 0;
            Assert.True(Stamp.Free(stamp));

            var drift = Stamp.Box(9, 9, 9);
            Assert.Equal(stamp, drift);
            world = World.New();
            Assert.False(World.TryRestore(world, buffer));
            World.Free(world);
            Stamp.Free(drift);

            var rebaked = Stamp.Box(3, 3, 20);
            Assert.Equal(stamp, rebaked);
            world = World.New();
            Assert.True(World.TryRestore(world, buffer));
            Stamp.Free(rebaked);
        }
        finally
        {
            World.Free(world);
            if (a != 0) World.Free(a);
            if (Stamp.TryGetSpec(stamp, out _)) Stamp.Free(stamp);
        }
    }

    [Fact]
    public void RestoreRejectsNonEmptyWorlds()
    {
        var buffer = SmallWorldSnapshot(out var a, out var stamp);
        try
        {
            var gridded = World.New();
            Grid.New(gridded, 5, 0f, 0f, 32f);
            Assert.False(World.TryRestore(gridded, buffer));
            World.Free(gridded);

            var layered = World.New();
            Layer.New(layered);
            Assert.False(World.TryRestore(layered, buffer));
            World.Free(layered);

            var restored = World.New();
            Assert.True(World.TryRestore(restored, buffer));
            Assert.False(World.TryRestore(restored, buffer));
            World.Free(restored);
        }
        finally
        {
            World.Free(a);
            Stamp.Free(stamp);
        }
    }

    [Fact]
    public void RestoreRejectsAliveColumnsAndRecordsOnDerivedLayers()
    {
        var stamp = Stamp.Box(3, 3, 40);
        var a = World.New();
        Grid.New(a, 5, 0f, 0f, 32f);
        var layer = Layer.New(a);
        Layer.Sum(a, layer, 1, layer, 1);
        World.Place(a, layer, 16f, 16f, stamp, 8);
        World.Process(a);
        var buffer = Snapshot(a);
        var b = World.New();
        try
        {
            Rejects(buffer, b, data => data[162] = 1);
            Rejects(buffer, b, data => data[132] = 1);
            Assert.True(World.TryRestore(b, buffer));
        }
        finally
        {
            World.Free(b);
            World.Free(a);
            Stamp.Free(stamp);
        }
    }

    [Fact]
    public void RestoreRejectsMarkEpochZero()
    {
        var stamp = Stamp.Box(3, 3, 40);
        var a = World.New();
        Grid.New(a, 5, 0f, 0f, 32f);
        var layer = Layer.New(a);
        World.Place(a, layer, 16f, 16f, stamp, 8);
        World.Process(a);
        var buffer = Snapshot(a);
        var b = World.New();
        try
        {
            Rejects(buffer, b, data => System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(12, 4), 0));
            Assert.True(World.TryRestore(b, buffer));
        }
        finally
        {
            World.Free(b);
            World.Free(a);
            Stamp.Free(stamp);
        }
    }

    [Fact]
    public void RestorePinsFoldLimitsToTheEngineRange()
    {
        var stamp = Stamp.Box(3, 3, 40);
        var a = World.New();
        Grid.New(a, 5, 0f, 0f, 32f);
        var layer = Layer.New(a);
        World.Place(a, layer, 16f, 16f, stamp, 8);
        World.Process(a);
        var buffer = Snapshot(a);
        var b = World.New();
        try
        {
            Rejects(buffer, b,
                data => System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(16, 8), long.MaxValue));
            Rejects(buffer, b,
                data => System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(16, 8), -1));
            Rejects(buffer, b,
                data => System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(24, 8), (1L << 62) + 1));
            Assert.True(World.TryRestore(b, buffer));
        }
        finally
        {
            World.Free(b);
            World.Free(a);
            Stamp.Free(stamp);
        }
    }

    [Fact]
    public void RestoreRejectsAliveRecordsOnTransientLayers()
    {
        var stamp = Stamp.Box(3, 3, 40);
        var a = World.New();
        Grid.New(a, 5, 0f, 0f, 32f);
        var layer = Layer.New(a);
        var transient = Layer.New(a, Layer.Residency.Transient, Layer.Precision.Cell);
        World.Place(a, layer, 16f, 16f, stamp, 8);
        World.Process(a);
        var buffer = Snapshot(a);
        var b = World.New();
        try
        {
            Rejects(buffer, b, data => data[162] = transient);
            Assert.True(World.TryRestore(b, buffer));
        }
        finally
        {
            World.Free(b);
            World.Free(a);
            Stamp.Free(stamp);
        }
    }

    [Fact]
    public void RestoreRejectsImpossibleReadsMasks()
    {
        var stamp = Stamp.Box(3, 3, 40);
        var a = World.New();
        Grid.New(a, 5, 0f, 0f, 32f);
        var layer = Layer.New(a);
        Layer.Sum(a, layer, 2, layer, 0);
        World.Place(a, layer, 16f, 16f, stamp, 8);
        World.Process(a);
        var buffer = Snapshot(a);
        var b = World.New();
        try
        {
            Rejects(buffer, b, data => System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(92, 4), 0));
            Rejects(buffer, b,
                data => System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(92, 4), uint.MaxValue));
            Assert.True(World.TryRestore(b, buffer));
        }
        finally
        {
            World.Free(b);
            World.Free(a);
            Stamp.Free(stamp);
        }
    }

    [Fact]
    public void RestoreRejectsSparsePairsNamingLiveTiles()
    {
        var stamp = Stamp.Box(3, 3, 40);
        var a = World.New();
        Grid.New(a, 6, 0f, 0f, 64f);
        var layer = Layer.New(a);
        var gone = World.Place(a, layer, 16f, 16f, stamp, 8);
        World.Place(a, layer, 48f, 48f, stamp, 8);
        World.Process(a);
        World.Remove(a, gone);
        World.Process(a);
        var buffer = Snapshot(a);
        SectionLayout(buffer, 0, 0, out var firstTile, out var pairs);
        var b = World.New();
        try
        {
            Rejects(buffer, b, data =>
            {
                var liveTile = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(firstTile, 4));
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(pairs + 4, 4), liveTile);
            });
            Assert.True(World.TryRestore(b, buffer));
        }
        finally
        {
            World.Free(b);
            World.Free(a);
            Stamp.Free(stamp);
        }
    }
    [Fact]
    public void RestoreRejectsDerivedSummariesThatLieAboutTheirPage()
    {
        var stamp = Stamp.Box(3, 3, 40);
        var a = World.New();
        Grid.New(a, 5, 0f, 0f, 32f);
        var layer = Layer.New(a);
        var sum = Layer.Sum(a, layer, 2, layer, 0);
        World.Place(a, layer, 16f, 16f, stamp, 8);
        World.Process(a);
        var buffer = Snapshot(a);
        SectionLayout(buffer, 0, sum, out var firstTile, out _);
        var b = World.New();
        try
        {
            Rejects(buffer, b, data =>
                System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(firstTile + 38, 2), short.MaxValue));
            Assert.True(World.TryRestore(b, buffer));
        }
        finally
        {
            World.Free(b);
            World.Free(a);
            Stamp.Free(stamp);
        }
    }
    [Fact]
    public void RestoringAnySingleByteCorruptionNeverThrows()
    {
        var buffer = SmallWorldSnapshot(out var a, out var stamp);
        try
        {
            for (var i = 0; i < buffer.Length; i++)
            {
                buffer[i] ^= 0xA5;
                var world = World.New();
                if (World.TryRestore(world, buffer)) World.Process(world);
                World.Free(world);
                buffer[i] ^= 0xA5;
            }
        }
        finally
        {
            World.Free(a);
            Stamp.Free(stamp);
        }
    }

    [Fact]
    public void SnapshotReadsAndWritesAllocateNothingOnWarmPaths()
    {
        var a = World.New();
        var stamp = Stamp.Box(3, 3, 20);
        try
        {
            Grid.New(a, 5, 0f, 0f, 32f);
            var layer = Layer.New(a);
            for (var i = 0; i < 8; i++) World.Place(a, layer, 4f + i * 3, 16f, stamp, 5);
            World.Process(a);
            var buffer = new byte[World.SnapshotSize(a)];
            Assert.True(World.TrySnapshot(a, buffer, out _));

            var minimum = long.MaxValue;
            for (var i = 0; i < 50; i++)
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                Assert.True(World.SnapshotSize(a) > 0);
                Assert.True(World.TrySnapshot(a, buffer, out _));
                var delta = GC.GetAllocatedBytesForCurrentThread() - before;
                if (delta < minimum) minimum = delta;
            }

            Assert.Equal(0, minimum);
        }
        finally
        {
            World.Free(a);
            Stamp.Free(stamp);
        }
    }

    [Fact]
    public void RestoringAllocatesNoManagedBytes()
    {
        var buffer = SmallWorldSnapshot(out var a, out var stamp);
        var world = World.New();
        try
        {
            Assert.True(World.TryRestore(world, buffer));
            World.Free(world);
            world = World.New();
            Assert.True(World.TryRestore(world, buffer));
            World.Free(world);

            var minimum = long.MaxValue;
            for (var i = 0; i < 50; i++)
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                world = World.New();
                Assert.True(World.TryRestore(world, buffer));
                World.Free(world);
                var delta = GC.GetAllocatedBytesForCurrentThread() - before;
                if (delta < minimum) minimum = delta;
            }

            Assert.Equal(0, minimum);
        }
        finally
        {
            World.Free(world);
            World.Free(a);
            Stamp.Free(stamp);
        }
    }

    private static byte[] Snapshot(byte world)
    {
        var buffer = new byte[World.SnapshotSize(world)];
        Assert.True(World.TrySnapshot(world, buffer, out var written));
        return buffer.AsSpan(0, written).ToArray();
    }

    private static byte[] SmallWorldSnapshot(out byte world, out byte stamp)
    {
        world = World.New();
        stamp = Stamp.Box(3, 3, 20);
        Grid.New(world, 5, 0f, 0f, 32f);
        var layer = Layer.New(world);
        var fading = World.Place(world, layer, 8f, 8f, stamp, 5);
        World.Place(world, layer, 20f, 20f, stamp, 3);
        World.Fade(world, fading, 1, 6);
        World.Remove(world, World.Place(world, layer, 26f, 26f, stamp, 2));
        World.Process(world);
        World.Process(world);
        var buffer = Snapshot(world);
        World.Process(world);
        return buffer;
    }

    private static void SectionLayout(
        byte[] buffer, int targetGrid, int targetLayer, out int firstTile, out int pairs)
    {
        var summary = stackalloc byte[TileSummary.Bytes];
        var reader = new SnapshotReader(buffer);
        reader.U32(out _);
        reader.U16(out _);
        reader.Byte(out var layers);
        reader.Byte(out var grids);
        reader.Skip(24);
        reader.Skip(32 * layers);
        reader.Skip(13 * grids);
        reader.Byte(out var stamps);
        reader.Skip(9 * stamps);
        reader.I32(out var slots);
        reader.Skip(35 * slots);
        for (var gi = 0; gi < grids; gi++)
        {
            reader.I32(out var records);
            reader.Skip(16 * records);
        }

        firstTile = -1;
        pairs = -1;
        for (var gi = 0; gi < grids; gi++)
        for (var l = 0; l < layers; l++)
        {
            reader.Byte(out _);
            reader.I32(out var live);
            if (gi == targetGrid && l == targetLayer) firstTile = reader.Position;
            for (var i = 0; i < live; i++)
            {
                reader.U32(out _);
                reader.Byte(out var flags);
                reader.Byte(out _);
                reader.Skip(24);
                reader.Copy(summary, TileSummary.Bytes);
                var handles = (TileSummary*)summary;
                var planes = PlaneStore.PageUnit;
                if ((flags & 1) != 0) planes += PlaneStore.Int32Unit;
                if ((flags & 2) != 0) planes += PlaneStore.Int64Unit;
                if ((flags & 4) != 0) planes += PlaneStore.Int64Unit;
                if (handles->CarryI != 0) planes += PlaneStore.Int64Unit;
                if (handles->HighT != 0) planes += PlaneStore.Int64Unit;
                if (handles->LowT != 0) planes += PlaneStore.Int64Unit;
                if (handles->HighB != 0) planes += PlaneStore.Int64Unit;
                if (handles->LowB != 0) planes += PlaneStore.Int64Unit;
                reader.Skip(planes);
            }

            reader.I32(out var changed);
            reader.Skip(4 * changed);
            reader.I32(out var stale);
            reader.Skip(4 * stale);
            if (gi == targetGrid && l == targetLayer) pairs = reader.Position;
            reader.I32(out var pairCount);
            reader.Skip(8 * pairCount);
        }
    }

    private static void Rejects(byte[] buffer, byte target, Action<byte[]> corrupt)
    {
        var data = buffer.ToArray();
        corrupt(data);
        Assert.False(World.TryRestore(target, data));
    }

    private static void CompareHashes(byte a, byte b)
    {
        for (byte g = 0; g < 2; g++)
        for (byte l = 0; l < 5; l++)
            Assert.Equal(World.LayerHash(a, g, l), World.LayerHash(b, g, l));
    }

    private static void CompareFields(byte a, byte b)
    {
        var smallA = new short[64 * 64];
        var smallB = new short[64 * 64];
        var largeA = new short[256 * 256];
        var largeB = new short[256 * 256];
        int[] sizes = [64, 256];
        for (byte g = 0; g < 2; g++)
        for (byte l = 0; l < 5; l++)
        {
            var size = sizes[g];
            var pageA = g == 0 ? smallA : largeA;
            var pageB = g == 0 ? smallB : largeB;
            fixed (short* pA = pageA)
            fixed (short* pB = pageB)
            {
                World.QueryRegion(a, g, l, 0, 0, size, size, pA);
                World.QueryRegion(b, g, l, 0, 0, size, size, pB);
            }

            Assert.Equal(pageA, pageB);
            Assert.Equal(World.Query(a, g, l, 0, 0, size, size), World.Query(b, g, l, 0, 0, size, size));
            Assert.Equal(World.QueryMax(a, g, l, out var xA, out var yA),
                World.QueryMax(b, g, l, out var xB, out var yB));
            Assert.Equal((xA, yA), (xB, yB));
        }
    }


}
