using Gi.Entities.Tests.Support;
using Xunit;

namespace Gi.Entities.Columns.Tests;

public sealed unsafe class GiContractTests : IDisposable
{
    private readonly List<byte> _worlds = [];
    private readonly List<byte> _stamps = [];

    public void Dispose()
    {
        foreach (var world in _worlds) World.Free(world);
        foreach (var stamp in _stamps) Stamp.Free(stamp);
    }

    private byte NewWorld()
    {
        var world = World.New();
        _worlds.Add(world);
        return world;
    }

    private byte NewBox(int width, int height, sbyte value)
    {
        var stamp = Stamp.Box((ushort)width, (ushort)height, value);
        _stamps.Add(stamp);
        return stamp;
    }

    private byte NewTent(int width, int height, sbyte value)
    {
        var stamp = Stamp.Tent((ushort)width, (ushort)height, value);
        _stamps.Add(stamp);
        return stamp;
    }

    private byte NewCone(int radius, sbyte value, ushort arc)
    {
        var stamp = Stamp.Cone((ushort)radius, value, arc);
        _stamps.Add(stamp);
        return stamp;
    }

    [Fact]
    public void C01_PlaceApplied_NeverReturnsDefaultSourceId()
    {
        var world = NewWorld();
        Grid.New(world, 7, 0f, 0f, 128f);
        var first = Layer.New(world);
        var second = Layer.New(world);
        var stamp = NewBox(4, 4, 50);

        for (var i = 0; i < 100; i++)
        {
            var id = World.Place(world, i % 2 == 0 ? first : second, 10f + i * 0.5f, 20f + i * 0.25f, stamp,
                (sbyte)(i % 33 - 16), out var status);
            Assert.Equal(World.WriteStatus.Applied, status);
            Assert.NotEqual(default(World.SourceId), id);
            Assert.NotEqual(0u, id.Generation);
        }

        var commands = new World.SourceCommand[100];
        var results = new World.SourceResult[100];
        for (var i = 0; i < commands.Length; i++)
            commands[i] = World.SourceCommand.Place(first, 10f + i, 30f + i, stamp, 5);
        Assert.Equal(World.BatchStatus.Applied, World.ApplyBatch(world, commands, results));
        foreach (var result in results)
        {
            Assert.Equal(World.SourceResultStatus.Applied, result.Status);
            Assert.NotEqual(default(World.SourceId), result.SourceId);
            Assert.NotEqual(0u, result.SourceId.Generation);
        }
    }

    [Fact]
    public void C02_TransformRangeInvalid_IsAnOutputMaskOfRejectedRows()
    {
        var world = NewWorld();
        Grid.New(world, 7, 0f, 0f, 128f);
        var layer = Layer.New(world);
        var stamp = NewBox(2, 2, 40);
        var alive = World.Place(world, layer, 20f, 20f, stamp, 4);
        var stale = World.Place(world, layer, 30f, 30f, stamp, 4);
        World.Remove(world, stale);
        var held = World.Place(world, layer, 40f, 40f, stamp, 4);
        World.Process(world);

        var sources = stackalloc World.SourceId[3];
        var xs = stackalloc float[3];
        var ys = stackalloc float[3];
        var mask = stackalloc ulong[2];
        sources[0] = stale;
        sources[1] = held;
        sources[2] = alive;
        xs[0] = 1f;
        ys[0] = 1f;
        xs[1] = float.NaN;
        ys[1] = 2f;
        xs[2] = 50f;
        ys[2] = 50f;
        mask[0] = ulong.MaxValue;
        mask[1] = ulong.MaxValue;

        var ranges = stackalloc World.TransformRange[1];
        ranges[0] = new World.TransformRange
        {
            Sources = sources, Xs = xs, Ys = ys, Invalid = mask, Count = 3, InvalidWords = 2,
        };
        var token = World.Capture(world);
        var hash = GiFieldHash.OfLayer(world, layer);
        var revision = World.Revision(world);

        Assert.Equal(World.BatchStatus.Applied, World.SyncTransforms(token, new ReadOnlySpan<World.TransformRange>(ranges, 1)));
        Assert.Equal(3ul, mask[0]);
        Assert.Equal(ulong.MaxValue, mask[1]);
        Assert.True(World.TryGetSourceSpec(world, alive, out var spec));
        Assert.Equal(50f, spec.X);
        Assert.Equal(50f, spec.Y);
        Assert.True(World.TryGetSourceSpec(world, held, out var heldSpec));
        Assert.Equal(40f, heldSpec.X);
        Assert.Equal(40f, heldSpec.Y);

        ranges[0].InvalidWords = 0;
        Assert.Equal(World.BatchStatus.InvalidRange, World.SyncTransforms(token, new ReadOnlySpan<World.TransformRange>(ranges, 1)));
        Assert.Equal(revision, World.Revision(world));
        Assert.Equal(hash, GiFieldHash.OfLayer(world, layer));
    }

    [Fact]
    public void C03_ApplyBatchAcceptsTwoToTheTwentyFourthCommandsAndRefusesOneMore()
    {
        var world = NewWorld();
        Grid.New(world, 7, 0f, 0f, 128f);
        var layer = Layer.New(world);
        var stamp = NewBox(1, 1, 30);

        var commands = new World.SourceCommand[4096];
        var results = new World.SourceResult[4096];
        for (var i = 0; i < commands.Length; i++)
            commands[i] = World.SourceCommand.Place(layer, 10f + (i & 63), 10f + (i >> 6), stamp, 3);
        var token = World.Capture(world);
        Assert.Equal(World.BatchStatus.Applied, World.ApplyBatch(token, commands, results));
        Assert.All(results, result => Assert.Equal(World.SourceResultStatus.Applied, result.Status));

        Assert.Equal(World.BatchStatus.InsufficientResults,
            World.ApplyBatch(token, commands, results.AsSpan(0, commands.Length - 1)));

        var huge = GC.AllocateUninitializedArray<World.SourceCommand>((1 << 24) + 1);
        var revision = World.Revision(world);
        var hash = GiFieldHash.OfLayer(world, layer);
        Assert.Equal(World.BatchStatus.TooManyCommands,
            World.ApplyBatch(token, huge, Span<World.SourceResult>.Empty));
        Assert.Equal(World.BatchStatus.InsufficientResults,
            World.ApplyBatch(token, new ReadOnlySpan<World.SourceCommand>(huge, 0, 1 << 24), Span<World.SourceResult>.Empty));
        Assert.Equal(revision, World.Revision(world));
        Assert.Equal(hash, GiFieldHash.OfLayer(world, layer));
    }

    [Fact]
    public void C04_FieldValuesIgnoreCreationCommandSplatAndSplitOrder_OverSeeds()
    {
        for (uint seed = 0; seed < 200; seed++) C04Seed(seed);
    }

    private void C04Seed(uint seed)
    {
        var rng = new Rng(seed * 2 + 1);
        var stamps = new byte[4];
        var a = World.New();
        var b = World.New();
        try
        {
            foreach (var world in new[] { a, b })
            {
                Grid.New(world, 6, 0f, 0f, 64f);
                Grid.New(world, 7, 0f, 0f, 64f);
                Layer.New(world);
                Layer.New(world, Layer.Precision.Cell);
                Layer.New(world, Layer.Residency.Transient, Layer.Precision.Cell);
                Layer.Sum(world, 0, 1, 2, 1, 1);
            }

            stamps[0] = Stamp.Box(3, 3, 50);
            stamps[1] = Stamp.Tent(5, 5, 40);
            stamps[2] = Stamp.Cone(6, 30, 60);
            var cells = new sbyte[16];
            for (var i = 0; i < cells.Length; i++) cells[i] = (sbyte)(rng.Next(101) - 50);
            stamps[3] = Stamp.New(cells, 4, 4);

            const int initial = 24;
            var layers = new byte[64];
            var xs = new float[64];
            var ys = new float[64];
            var stampIndex = new int[64];
            var gains = new sbyte[64];
            var headings = new float[64];
            var count = initial;
            for (var i = 0; i < initial; i++)
            {
                layers[i] = (byte)rng.Next(2);
                xs[i] = 8f + rng.Next(48);
                ys[i] = 8f + rng.Next(48);
                stampIndex[i] = rng.Next(4);
                gains[i] = (sbyte)(rng.Next(33) - 16);
                if (gains[i] == 0) gains[i] = 4;
                headings[i] = rng.Next(2) == 0 ? 0f : MathF.PI / 2f;
            }

            var order = new int[initial];
            for (var i = 0; i < initial; i++) order[i] = i;
            rng.Shuffle(order);

            var idsA = new World.SourceId[64];
            var idsB = new World.SourceId[64];
            for (var i = 0; i < initial; i++)
            {
                idsA[i] = World.Place(a, layers[i], xs[i], ys[i], stamps[stampIndex[i]], gains[i]);
                World.Turn(a, idsA[i], headings[i]);
            }

            for (var i = 0; i < initial; i++)
            {
                var j = order[i];
                idsB[j] = World.Place(b, layers[j], xs[j], ys[j], stamps[stampIndex[j]], gains[j]);
                World.Turn(b, idsB[j], headings[j]);
            }

            var live = new bool[64];
            Array.Fill(live, true, 0, initial);
            var ops = new Op[40];
            var commands = new World.SourceCommand[48];
            var results = new World.SourceResult[48];
            var splatXs = new float[8];
            var splatYs = new float[8];
            var splatStamps = new byte[8];
            var splatGains = new sbyte[8];
            var splatOrder = new int[8];
            var groupOrder = new int[43];
            var slicePlaceIndex = new int[3];
            var slicePlaceAt = new int[3];
            var transientId = stamps[0];
            for (var tick = 0; tick < 8; tick++)
            {
                var opCount = 0;
                for (var i = 0; i < count; i++)
                {
                    if (!live[i] || rng.Next(10) >= 4) continue;
                    var op = new Op { Source = i, Kind = rng.Next(6) };
                    switch (op.Kind)
                    {
                        case 0:
                            op.F0 = 8f + rng.Next(48);
                            op.F1 = 8f + rng.Next(48);
                            break;
                        case 1:
                            op.F0 = rng.Next(4) * (MathF.PI / 2f);
                            break;
                        case 2:
                        case 3:
                            op.G = (sbyte)(rng.Next(33) - 16);
                            op.I0 = op.Kind == 3 ? 1 + rng.Next(3) : 0;
                            break;
                        case 4:
                            op.I0 = 1 + rng.Next(2);
                            break;
                    }

                    ops[opCount++] = op;
                }

                var placeCount = rng.Next(3);
                for (var i = 0; i < placeCount; i++)
                {
                    layers[count + i] = (byte)rng.Next(2);
                    xs[count + i] = 8f + rng.Next(48);
                    ys[count + i] = 8f + rng.Next(48);
                    stampIndex[count + i] = rng.Next(4);
                    gains[count + i] = (sbyte)(rng.Next(31) - 15);
                    headings[count + i] = rng.Next(2) == 0 ? 0f : MathF.PI / 2f;
                    live[count + i] = true;
                }

                var groups = opCount + placeCount;
                for (var i = 0; i < groups; i++) groupOrder[i] = i;
                rng.Shuffle(groupOrder, groups);

                var splatCount = 8;
                for (var i = 0; i < splatCount; i++)
                {
                    splatXs[i] = 4f + rng.Next(56);
                    splatYs[i] = 4f + rng.Next(56);
                    splatStamps[i] = transientId;
                    splatGains[i] = (sbyte)(rng.Next(9) - 4);
                }

                var ci = 0;
                for (var i = 0; i < opCount; i++)
                {
                    var op = ops[i];
                    var target = World.SourceTarget.Existing(idsA[op.Source]);
                    commands[ci++] = op.Kind switch
                    {
                        0 => World.SourceCommand.Move(target, op.F0, op.F1),
                        1 => World.SourceCommand.Turn(target, op.F0),
                        2 => World.SourceCommand.SetGain(target, op.G),
                        3 => World.SourceCommand.Fade(target, op.G, op.I0),
                        4 => World.SourceCommand.Expire(target, op.I0),
                        _ => World.SourceCommand.Remove(target),
                    };
                }

                for (var i = 0; i < placeCount; i++)
                {
                    var index = count + i;
                    commands[ci++] = World.SourceCommand.Place(layers[index], xs[index], ys[index],
                        stamps[stampIndex[index]], gains[index], headings[index]);
                }

                Assert.Equal(World.BatchStatus.Applied, World.ApplyBatch(a, commands.AsSpan(0, ci), results));
                for (var i = 0; i < placeCount; i++) idsA[count + i] = results[opCount + i].SourceId;

                var cuts = 1 + rng.Next(3);
                var start = 0;
                for (var cut = 0; cut < cuts && start < groups; cut++)
                {
                    var stop = cut == cuts - 1 ? groups : start + 1 + rng.Next(groups - start);
                    var cbi = 0;
                    var slicePlaces = 0;
                    for (var g = start; g < stop; g++)
                    {
                        var group = groupOrder[g];
                        if (group < opCount)
                        {
                            var op = ops[group];
                            var target = World.SourceTarget.Existing(idsB[op.Source]);
                            commands[cbi++] = op.Kind switch
                            {
                                0 => World.SourceCommand.Move(target, op.F0, op.F1),
                                1 => World.SourceCommand.Turn(target, op.F0),
                                2 => World.SourceCommand.SetGain(target, op.G),
                                3 => World.SourceCommand.Fade(target, op.G, op.I0),
                                4 => World.SourceCommand.Expire(target, op.I0),
                                _ => World.SourceCommand.Remove(target),
                            };
                        }
                        else
                        {
                            var index = count + group - opCount;
                            slicePlaceIndex[slicePlaces] = index;
                            slicePlaceAt[slicePlaces] = cbi;
                            slicePlaces++;
                            commands[cbi++] = World.SourceCommand.Place(layers[index], xs[index], ys[index],
                                stamps[stampIndex[index]], gains[index], headings[index]);
                        }
                    }

                    Assert.Equal(World.BatchStatus.Applied, World.ApplyBatch(b, commands.AsSpan(0, cbi), results));
                    for (var i = 0; i < slicePlaces; i++) idsB[slicePlaceIndex[i]] = results[slicePlaceAt[i]].SourceId;

                    start = stop;
                }

                for (var i = 0; i < splatCount; i++) splatOrder[i] = i;
                rng.Shuffle(splatOrder, splatCount);
                var bx = new float[8];
                var by = new float[8];
                var bs = new byte[8];
                var bg = new sbyte[8];
                for (var i = 0; i < splatCount; i++)
                {
                    bx[i] = splatXs[splatOrder[i]];
                    by[i] = splatYs[splatOrder[i]];
                    bs[i] = splatStamps[splatOrder[i]];
                    bg[i] = splatGains[splatOrder[i]];
                }

                World.Splat(a, 2, splatXs, splatYs, splatStamps, splatGains);
                World.Splat(b, 2, bx, by, bs, bg);

                World.Process(a);
                World.Process(b);

                for (byte layer = 0; layer < 4; layer++)
                    Assert.Equal(GiFieldHash.OfLayer(a, layer), GiFieldHash.OfLayer(b, layer));

                var total = count + placeCount;
                for (var i = 0; i < total; i++)
                {
                    var aliveA = World.TryGetSourceSpec(a, idsA[i], out _);
                    var aliveB = World.TryGetSourceSpec(b, idsB[i], out _);
                    Assert.Equal(aliveA, aliveB);
                    live[i] = aliveA;
                }

                count = total;
            }
        }
        finally
        {
            World.Free(a);
            World.Free(b);
            foreach (var stamp in stamps) Stamp.Free(stamp);
        }
    }

    private struct Op
    {
        public int Source;
        public int Kind;
        public float F0;
        public float F1;
        public int I0;
        public sbyte G;
    }

    [Fact]
    public void C05_UniformSplatEqualsPerEntrySplat()
    {
        var rng = new Rng(7);
        foreach (var precision in new[]
                 {
                     Layer.Precision.Exact, Layer.Precision.Half, Layer.Precision.Quarter, Layer.Precision.Cell,
                 })
            C05One(precision, rng);
    }

    private void C05One(Layer.Precision precision, Rng rng)
    {
        var a = World.New();
        var b = World.New();
        var stamp = Stamp.Cone(5, 30, 90);
        try
        {
            foreach (var world in new[] { a, b })
            {
                Grid.New(world, 7, 0f, 0f, 128f);
                Layer.New(world, Layer.Residency.Transient, precision);
            }

            const int count = 97;
            var xs = new float[count];
            var ys = new float[count];
            var stampIds = new byte[count];
            var gains = new sbyte[count];
            foreach (var gain in new sbyte[] { 3, -3 })
            {
                for (var round = 0; round < 4; round++)
                {
                    for (var i = 0; i < count; i++)
                    {
                        xs[i] = rng.NextFloat() * 140f - 6f;
                        ys[i] = rng.NextFloat() * 140f - 6f;
                        stampIds[i] = stamp;
                        gains[i] = gain;
                    }

                    World.Splat(a, 0, xs, ys, stamp, gain);
                    World.Splat(b, 0, xs, ys, stampIds, gains);
                    World.Process(a);
                    World.Process(b);
                    Assert.Equal(GiFieldHash.OfLayer(a, 0), GiFieldHash.OfLayer(b, 0));
                }
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
    public void C06_ProcessAdvancesTickByExactlyOneAndNothingElseMovesIt()
    {
        var world = NewWorld();
        Grid.New(world, 7, 0f, 0f, 128f);
        var layer = Layer.New(world);
        var transient = Layer.New(world, Layer.Residency.Transient, Layer.Precision.Cell);
        var stamp = NewBox(4, 4, 50);
        Assert.Equal(0, World.Tick(world));

        var id = World.Place(world, layer, 64f, 64f, stamp, 4);
        World.Move(world, id, 65f, 64f);
        World.Turn(world, id, MathF.PI / 4f);
        World.Scale(world, id, 1.5f);
        World.SetGain(world, id, 5);
        World.Fade(world, id, 1, 4);
        World.Expire(world, id, 100);
        Assert.Equal(0, World.Tick(world));

        var commands = new[] { World.SourceCommand.Move(World.SourceTarget.Existing(id), 66f, 64f) };
        var results = new World.SourceResult[1];
        var token = World.Capture(world);
        Assert.Equal(World.BatchStatus.Applied, World.ApplyBatch(token, commands, results));
        var sources = stackalloc World.SourceId[1];
        var xs = stackalloc float[1];
        var ys = stackalloc float[1];
        sources[0] = id;
        xs[0] = 67f;
        ys[0] = 64f;
        Assert.Equal(World.BatchStatus.Applied, World.Sync(token, sources, xs, ys, 1));
        var ranges = stackalloc World.TransformRange[1];
        ranges[0] = new World.TransformRange { Sources = sources, Xs = xs, Ys = ys, Count = 1 };
        Assert.Equal(World.BatchStatus.Applied, World.SyncTransforms(token, new ReadOnlySpan<World.TransformRange>(ranges, 1)));
        World.Splat(world, transient, new[] { 64f }, new[] { 64f }, stamp, 2);
        Assert.Equal(0, World.Tick(world));

        for (var i = 1; i <= 4; i++)
        {
            World.Process(world);
            Assert.Equal(i, World.Tick(world));
        }

        World.ProcessAll();
        Assert.Equal(5, World.Tick(world));
        Assert.Equal(World.BatchStatus.Applied, World.Process(token));
        Assert.Equal(6, World.Tick(world));
    }

    [Fact]
    public void C07_OnlyLifecycleCallsBumpRevision()
    {
        var world = NewWorld();
        var grid = Grid.New(world, 7, 0f, 0f, 128f);
        var layer = Layer.New(world);
        var transient = Layer.New(world, Layer.Residency.Transient, Layer.Precision.Cell);
        var stamp = NewBox(4, 4, 50);
        var id = World.Place(world, layer, 64f, 64f, stamp, 4);

        var token = World.Capture(world);
        var revision = World.Revision(world);

        World.Move(world, id, 65f, 64f);
        World.Turn(world, id, 1f);
        World.Scale(world, id, 1.5f);
        World.SetGain(world, id, 6);
        World.Fade(world, id, 2, 5);
        World.Expire(world, id, 50);
        var commands = new[] { World.SourceCommand.Move(World.SourceTarget.Existing(id), 66f, 64f) };
        var results = new World.SourceResult[1];
        Assert.Equal(World.BatchStatus.Applied, World.ApplyBatch(token, commands, results));
        var sources = stackalloc World.SourceId[1];
        var xs = stackalloc float[1];
        var ys = stackalloc float[1];
        sources[0] = id;
        xs[0] = 67f;
        ys[0] = 64f;
        Assert.Equal(World.BatchStatus.Applied, World.Sync(token, sources, xs, ys, 1));
        var ranges = stackalloc World.TransformRange[1];
        ranges[0] = new World.TransformRange { Sources = sources, Xs = xs, Ys = ys, Count = 1 };
        Assert.Equal(World.BatchStatus.Applied, World.SyncTransforms(token, new ReadOnlySpan<World.TransformRange>(ranges, 1)));
        World.Splat(world, transient, new[] { 64f }, new[] { 64f }, stamp, 2);
        Assert.Equal(World.BatchStatus.Applied, World.Process(token));
        World.ProcessAll();
        var scratch = stackalloc short[64];
        World.QueryRegion(world, grid, layer, 0, 0, 8, 8, scratch);

        Assert.Equal(revision, World.Revision(world));

        World.Clear(world);
        var cleared = World.Revision(world);
        Assert.NotEqual(revision, cleared);
        Assert.Equal(World.BatchStatus.StaleRevision, World.Process(token));

        var fresh = World.Capture(world);
        Assert.True(fresh.IsValid);
        Assert.Equal(World.BatchStatus.Applied, World.Process(fresh));
        Assert.Equal(cleared, World.Revision(world));

        var again = World.Place(world, layer, 64f, 64f, stamp, 4);
        World.Record(world);
        World.Move(world, again, 70f, 64f);
        World.Rewind(world);
        Assert.NotEqual(cleared, World.Revision(world));
    }

    [Fact]
    public void C08_PureReadsFromEightThreadsMatchSerial()
    {
        var world = NewWorld();
        var grid = Grid.New(world, 7, 0f, 0f, 128f);
        Grid.New(world, 6, -32f, -32f, 96f);
        var layer = Layer.New(world);
        var transient = Layer.New(world, Layer.Residency.Transient, Layer.Precision.Cell);
        var derived = Layer.Sum(world, layer, 1, 1);
        var box = NewBox(4, 4, 50);
        var tent = NewTent(6, 6, 40);
        var cone = NewCone(8, 30, 120);

        for (var i = 0; i < 12; i++)
        {
            var stamp = i % 3 == 0 ? box : i % 3 == 1 ? tent : cone;
            var placed = World.Place(world, layer, 12f + i * 8.5f, 20f + (i % 5) * 17.25f, stamp, (sbyte)(i % 9 + 1));
            World.Turn(world, placed, (i % 4) * (MathF.PI / 8f));
            World.Process(world);
            World.Splat(world, transient, new[] { 10f + i * 9.125f }, new[] { 100f - i * 6.5f }, box, 3);
        }

        World.Process(world);
        var since = World.Tick(world) - 1;

        var xs = new float[97];
        var ys = new float[97];
        var rng = new Rng(11);
        for (var i = 0; i < 97; i++)
        {
            xs[i] = rng.NextFloat() * 170f - 21f;
            ys[i] = rng.NextFloat() * 170f - 21f;
        }

        var outcomes = new ReadOutcomes[2][];
        for (var layerIndex = 0; layerIndex < 2; layerIndex++)
        {
            outcomes[layerIndex] = new ReadOutcomes[9];
            for (var i = 0; i < 9; i++) outcomes[layerIndex][i] = new ReadOutcomes();
            RunReads(world, grid, layerIndex == 0 ? layer : derived, since, xs, ys, outcomes[layerIndex][0]);
        }

        var barrier = new Barrier(8);
        var threads = new Thread[8];
        for (var t = 0; t < threads.Length; t++)
        {
            var index = t;
            threads[t] = new Thread(() =>
            {
                barrier.SignalAndWait();
                for (var layerIndex = 0; layerIndex < 2; layerIndex++)
                    RunReads(world, grid, layerIndex == 0 ? layer : derived, since, xs, ys, outcomes[layerIndex][index + 1]);
            });
        }

        foreach (var thread in threads) thread.Start();
        foreach (var thread in threads) thread.Join();

        for (var layerIndex = 0; layerIndex < 2; layerIndex++)
            for (var t = 1; t <= 8; t++)
                CompareOutcomes(outcomes[layerIndex][0], outcomes[layerIndex][t]);
    }

    private sealed class ReadOutcomes
    {
        public const int Count = 97;

        public readonly short[] Values = new short[Count];
        public readonly float[] GradientX = new float[Count];
        public readonly float[] GradientY = new float[Count];
        public readonly long[] Areas = new long[Count];
        public readonly short[] MaxValues = new short[Count];
        public readonly float[] MaxX = new float[Count];
        public readonly float[] MaxY = new float[Count];
        public readonly short[] NearValues = new short[Count];
        public readonly float[] NearX = new float[Count];
        public readonly float[] NearY = new float[Count];
        public readonly bool[] Complete = new bool[Count];
        public readonly bool[] Covers = new bool[Count];
        public readonly bool[] Changed = new bool[Count];
        public readonly short[] CellValues = new short[Count];
        public readonly short[] AtValues = new short[Count];
        public readonly short[] ColumnValues = new short[Count];
        public readonly float[] ColumnGradientX = new float[Count];
        public readonly float[] ColumnGradientY = new float[Count];
        public readonly long[] ColumnAreas = new long[Count];
        public readonly short[] ColumnMaxValues = new short[Count];
        public readonly float[] ColumnMaxX = new float[Count];
        public readonly float[] ColumnMaxY = new float[Count];
        public readonly short[] ColumnNearValues = new short[Count];
        public readonly float[] ColumnNearX = new float[Count];
        public readonly float[] ColumnNearY = new float[Count];
        public readonly ulong[] Whole = new ulong[2];
        public readonly ulong[] Found = new ulong[2];
        public readonly ulong[] Mask = new ulong[2];
        public readonly int[] Tiles = new int[64];
        public int TileCount;
        public short QueryMaxValue;
        public int QueryMaxX;
        public int QueryMaxY;
        public readonly short[] Region = new short[128 * 128];
    }

    private static void RunReads(byte world, byte grid, byte layer, int since, float[] xs, float[] ys, ReadOutcomes o)
    {
        var reaches = new float[97];
        var thresholds = new short[97];
        for (var i = 0; i < 97; i++)
        {
            reaches[i] = 4f;
            thresholds[i] = 10;
        }

        fixed (float* px = xs, py = ys, pr = reaches, cgx = o.ColumnGradientX, cgy = o.ColumnGradientY,
               cmx = o.ColumnMaxX, cmy = o.ColumnMaxY, cnx = o.ColumnNearX, cny = o.ColumnNearY)
        fixed (short* cv = o.ColumnValues, cmv = o.ColumnMaxValues, cnv = o.ColumnNearValues, th = thresholds)
        fixed (long* ca = o.ColumnAreas)
        fixed (ulong* whole = o.Whole, found = o.Found, mask = o.Mask)
        fixed (int* tiles = o.Tiles)
        fixed (short* region = o.Region)
        {
            o.Whole.AsSpan().Fill(0xAAAAAAAAAAAAAAAAul);
            o.Found.AsSpan().Fill(0xAAAAAAAAAAAAAAAAul);
            o.Mask.AsSpan().Fill(0xAAAAAAAAAAAAAAAAul);
            World.SenseColumn(world, layer, px, py, 97, cv, whole);
            World.SenseGradientColumn(world, layer, px, py, null, 97, cv, cgx, cgy, whole);
            World.SenseAreaColumn(world, layer, px, py, pr, null, 97, ca, whole);
            World.SenseMaxColumn(world, layer, px, py, pr, 97, cmv, cmx, cmy, whole);
            World.SenseNearestColumn(world, layer, px, py, pr, th, 97, cnv, cnx, cny, found);
            World.ChangedMask(world, layer, px, py, 4f, since, 97, mask);
            o.TileCount = World.ChangedTiles(world, grid, layer, since, tiles);
            o.QueryMaxValue = World.QueryMax(world, grid, layer, out o.QueryMaxX, out o.QueryMaxY);
            World.QueryRegion(world, grid, layer, 0, 0, 128, 128, region);
            for (var i = 0; i < 97; i++)
            {
                o.Complete[i] = World.TrySense(world, layer, px[i], py[i], out o.Values[i]);
                World.TrySenseGradient(world, layer, px[i], py[i], out o.GradientX[i], out o.GradientY[i]);
                World.TrySenseArea(world, layer, px[i], py[i], 4f, out o.Areas[i]);
                World.TrySenseMax(world, layer, px[i], py[i], 4f, out o.MaxValues[i], out o.MaxX[i], out o.MaxY[i]);
                World.TrySenseNearest(world, layer, px[i], py[i], 4f, 10, out o.NearValues[i], out o.NearX[i], out o.NearY[i]);
                o.Covers[i] = World.Covers(world, px[i], py[i]);
                o.Changed[i] = World.Changed(world, layer, px[i], py[i], 4f, since);
                o.CellValues[i] = World.Query(world, grid, layer, (int)px[i], (int)py[i]);
                o.AtValues[i] = World.QueryAt(world, grid, layer, px[i], py[i]);
            }
        }
    }

    private static void CompareOutcomes(ReadOutcomes expected, ReadOutcomes actual)
    {
        Assert.Equal(expected.Values, actual.Values);
        Assert.Equal(expected.GradientX, actual.GradientX);
        Assert.Equal(expected.GradientY, actual.GradientY);
        Assert.Equal(expected.Areas, actual.Areas);
        Assert.Equal(expected.MaxValues, actual.MaxValues);
        Assert.Equal(expected.MaxX, actual.MaxX);
        Assert.Equal(expected.MaxY, actual.MaxY);
        Assert.Equal(expected.NearValues, actual.NearValues);
        Assert.Equal(expected.NearX, actual.NearX);
        Assert.Equal(expected.NearY, actual.NearY);
        Assert.Equal(expected.Complete, actual.Complete);
        Assert.Equal(expected.Covers, actual.Covers);
        Assert.Equal(expected.Changed, actual.Changed);
        Assert.Equal(expected.CellValues, actual.CellValues);
        Assert.Equal(expected.AtValues, actual.AtValues);
        Assert.Equal(expected.ColumnValues, actual.ColumnValues);
        Assert.Equal(expected.ColumnGradientX, actual.ColumnGradientX);
        Assert.Equal(expected.ColumnGradientY, actual.ColumnGradientY);
        Assert.Equal(expected.ColumnAreas, actual.ColumnAreas);
        Assert.Equal(expected.ColumnMaxValues, actual.ColumnMaxValues);
        Assert.Equal(expected.ColumnMaxX, actual.ColumnMaxX);
        Assert.Equal(expected.ColumnMaxY, actual.ColumnMaxY);
        Assert.Equal(expected.ColumnNearValues, actual.ColumnNearValues);
        Assert.Equal(expected.ColumnNearX, actual.ColumnNearX);
        Assert.Equal(expected.ColumnNearY, actual.ColumnNearY);
        Assert.Equal(expected.Whole, actual.Whole);
        Assert.Equal(expected.Found, actual.Found);
        Assert.Equal(expected.Mask, actual.Mask);
        Assert.Equal(expected.TileCount, actual.TileCount);
        Assert.Equal(expected.Tiles.AsSpan(0, expected.TileCount).ToArray(), actual.Tiles.AsSpan(0, actual.TileCount).ToArray());
        Assert.Equal(expected.QueryMaxValue, actual.QueryMaxValue);
        Assert.Equal(expected.QueryMaxX, actual.QueryMaxX);
        Assert.Equal(expected.QueryMaxY, actual.QueryMaxY);
        Assert.Equal(expected.Region, actual.Region);
    }

    [Fact]
    public void C09_ExclusionReadsAreKernelCacheStateInvariant()
    {
        var world = NewWorld();
        Grid.New(world, 7, 0f, 0f, 128f);
        var layer = Layer.New(world);
        var stamps = new byte[17];
        var ids = new World.SourceId[17];
        for (var i = 0; i < stamps.Length; i++)
        {
            stamps[i] = Stamp.Box((ushort)(i + 1), (ushort)(i + 1), 60);
            _stamps.Add(stamps[i]);
            ids[i] = World.Place(world, layer, 16f + i * 6.5f, 64f, stamps[i], 4);
        }

        World.Process(world);

        const int probes = 40;
        var xs = new float[probes];
        var ys = new float[probes];
        var measured = new short[8][];
        for (var i = 0; i < 8; i++) measured[i] = new short[probes];

        for (var pass = 0; pass < 3; pass++)
        {
            if (pass == 0)
            {
                for (var i = 8; i < 17; i++)
                {
                    var value = World.TrySense(world, layer, 16f + i * 6.5f, 64f, ids[i], out _);
                    Assert.True(value);
                }
            }
            else
            {
                World.Process(world);
                for (var i = 8; i < 17; i++)
                    Assert.True(World.TrySense(world, layer, 16f + i * 6.5f, 64f, ids[i], out _));
            }

            var n = 0;
            for (var i = 0; i < 8; i++)
            for (var d = 0; d < 5; d++)
            {
                xs[n] = 16f + i * 6.5f + d * 0.25f;
                ys[n] = 64f + (d % 2 == 0 ? 0.5f : -0.5f);
                Assert.True(World.TrySense(world, layer, xs[n], ys[n], ids[i], out measured[pass][n]));
                Assert.True(World.TrySenseGradient(world, layer, xs[n], ys[n], ids[i], out var gx, out var gy));
                Assert.True(World.TrySenseArea(world, layer, xs[n], ys[n], 3f, ids[i], out _));
                measured[pass][n] = (short)(measured[pass][n] ^ (short)(gx * 4096f) ^ (short)(gy * 4096f));
                n++;
            }

            Assert.Equal(probes, n);
        }

        Assert.Equal(measured[0], measured[1]);
        Assert.Equal(measured[0], measured[2]);
    }

    [Fact]
    public void C10_ExclusionOnDerivedLayerSubtractsThroughTheRecipe()
    {
        var world = NewWorld();
        Grid.New(world, 7, 0f, 0f, 128f);
        var first = Layer.New(world);
        var second = Layer.New(world);
        var unrelated = Layer.New(world);
        var sum = Layer.Sum(world, first, 2, second, 3, 1);
        var min = Layer.Min(world, first, second);
        var box = NewBox(3, 3, 40);
        var other = NewBox(2, 2, 60);
        var onFirst = World.Place(world, first, 40.5f, 40.5f, box, 6);
        World.Place(world, second, 41.5f, 41.5f, other, -5);
        var onUnrelated = World.Place(world, unrelated, 42.5f, 42.5f, box, 8);
        World.Process(world);

        for (var dx = -2; dx <= 2; dx++)
        for (var dy = -2; dy <= 2; dy++)
        {
            var x = 41f + dx * 0.75f;
            var y = 41f + dy * 0.75f;
            Assert.True(World.TrySense(world, first, x, y, onFirst, out var firstExcluded));
            Assert.True(World.TrySense(world, second, x, y, out var secondValue));
            Assert.True(World.TrySense(world, sum, x, y, out var plainSum));
            Assert.True(World.TrySense(world, sum, x, y, onFirst, out var sumExcluded));
            Assert.True(World.TrySense(world, sum, x, y, onUnrelated, out var sumUnrelated));
            Assert.True(World.TrySense(world, min, x, y, onFirst, out var minExcluded));

            Assert.Equal(SumShifted(2, firstExcluded, 3, secondValue, 1), sumExcluded);
            Assert.Equal(plainSum, sumUnrelated);
            Assert.Equal(Math.Min(firstExcluded, secondValue), minExcluded);
        }
    }

    private static short SumShifted(int weightA, short a, int weightB, short b, int shift)
    {
        var v = weightA * a + weightB * b;
        if (shift != 0) v = (v + (1 << (shift - 1)) + (v >> 31)) >> shift;
        return (short)Math.Clamp(v, short.MinValue, short.MaxValue);
    }

    [Fact]
    public void C11_WholeAndFoundBitsAreEntryIndexedAndTailBitsAreCleared()
    {
        var world = NewWorld();
        Grid.New(world, 7, 0f, 0f, 128f);
        var layer = Layer.New(world);
        var stamp = NewBox(8, 8, 40);
        World.Place(world, layer, 64f, 64f, stamp, 5);
        World.Process(world);

        var xs = new float[70];
        var ys = new float[70];
        for (var i = 0; i < 70; i++)
        {
            xs[i] = 40f + (i % 10) * 2.5f;
            var row = i / 10;
            ys[i] = 40f + row * 2.5f;
        }

        var whole = new ulong[4];
        var values = new short[70];
        whole.AsSpan().Fill(0xAAAAAAAAAAAAAAAAul);
        fixed (float* px = xs, py = ys)
        fixed (short* pv = values)
        fixed (ulong* pw = whole)
        {
            World.SenseColumn(world, layer, px, py, 70, pv, pw);
            Assert.Equal(ulong.MaxValue, whole[0]);
            Assert.Equal(63ul, whole[1]);
            Assert.Equal(0xAAAAAAAAAAAAAAAAul, whole[2]);

            World.SenseColumn(world, layer, px, py, 64, pv, pw);
            Assert.Equal(ulong.MaxValue, whole[0]);
            Assert.Equal(63ul, whole[1]);

            whole[1] = 0xAAAAAAAAAAAAAAAAul;
            var reaches = new float[70];
            reaches.AsSpan().Fill(30f);
            var thresholds = new short[70];
            thresholds.AsSpan().Fill(10);
            var nearValues = new short[70];
            var nearX = new float[70];
            var nearY = new float[70];
            var found = new ulong[4];
            found.AsSpan().Fill(0xAAAAAAAAAAAAAAAAul);
            fixed (float* pr = reaches, pnx = nearX, pny = nearY)
            fixed (short* pnv = nearValues, pth = thresholds)
            fixed (ulong* pf = found)
            {
                World.SenseNearestColumn(world, layer, px, py, pr, pth, 70, pnv, pnx, pny, pf);
                Assert.Equal(ulong.MaxValue, pf[0]);
                Assert.Equal(63ul, pf[1]);
                Assert.Equal(0xAAAAAAAAAAAAAAAAul, pf[2]);
            }

            var mask = new ulong[4];
            mask.AsSpan().Fill(0xAAAAAAAAAAAAAAAAul);
            fixed (ulong* pm = mask)
            {
                World.ChangedMask(world, layer, px, py, 4f, World.Tick(world) - 1, 70, pm);
                Assert.Equal(0ul, pm[1] & ~((1ul << 6) - 1));
                Assert.Equal(0xAAAAAAAAAAAAAAAAul, mask[2]);
            }
        }
    }

    [Fact]
    public void C12_NearestColumnWritesMinValueAndThePointItselfWhenNothingIsFound()
    {
        var world = NewWorld();
        Grid.New(world, 7, 0f, 0f, 128f);
        var layer = Layer.New(world);
        var stamp = NewBox(2, 2, 10);
        World.Place(world, layer, 64f, 64f, stamp, 1);
        World.Process(world);

        float[] xs = [64f, 64.5f, 400f];
        float[] ys = [64.5f, 64f, 400f];
        float[] reaches = [2f, 2f, 2f];
        short[] thresholds = [100, 100, 100];
        var values = new short[3];
        var nearX = new float[3];
        var nearY = new float[3];
        var found = new ulong[1];

        fixed (float* px = xs, py = ys, pr = reaches, pnx = nearX, pny = nearY)
        fixed (short* pv = values, pth = thresholds)
        fixed (ulong* pf = found)
        {
            var status = World.SenseNearestColumn(world, layer, px, py, pr, pth, 3, pv, pnx, pny, pf);
            Assert.Equal(World.ReadStatus.Partial, status);
            Assert.All(values, v => Assert.Equal(short.MinValue, v));
            Assert.Equal(0ul, found[0]);
            Assert.Equal(64f, pnx[0]);
            Assert.Equal(64.5f, pny[0]);
            Assert.Equal(64.5f, pnx[1]);
            Assert.Equal(64f, pny[1]);
            Assert.Equal(0f, pnx[2]);
            Assert.Equal(0f, pny[2]);
        }

        values.AsSpan().Fill(0);
        nearX.AsSpan().Fill(1f);
        nearY.AsSpan().Fill(1f);
        found[0] = ulong.MaxValue;
        fixed (float* px = xs, py = ys, pr = reaches, pnx = nearX, pny = nearY)
        fixed (short* pv = values, pth = thresholds)
        fixed (ulong* pf = found)
        {
            var status = World.SenseNearestColumn(200, layer, px, py, pr, pth, 3, pv, pnx, pny, pf);
            Assert.Equal(World.ReadStatus.InvalidLayer, status);
            Assert.All(values, v => Assert.Equal(short.MinValue, v));
            Assert.Equal(0ul, found[0]);
            Assert.All(nearX, x => Assert.Equal(0f, x));
            Assert.All(nearY, y => Assert.Equal(0f, y));
        }
    }

    [Fact]
    public void C13_ChangedTilesSinceListsEachChangedTileOnceAscendingAfterItsTick()
    {
        var world = NewWorld();
        var grid = Grid.New(world, 6, 0f, 0f, 64f);
        var layer = Layer.New(world);
        var stamp = NewBox(40, 40, 5);
        var id = World.Place(world, layer, 32f, 32f, stamp, 4);
        World.Process(world);
        Assert.Equal(1, World.Tick(world));

        var tiles = new int[4];
        int count;
        fixed (int* p = tiles) count = World.ChangedTiles(world, grid, layer, 0, p);
        Assert.Equal(4, count);
        Assert.Equal(new[] { 0, 1, 2, 3 }, tiles.AsSpan(0, count).ToArray());
        Assert.True(Ascending(tiles, count));
        fixed (int* p = tiles) Assert.Equal(0, World.ChangedTiles(world, grid, layer, 1, p));
        Assert.Equal(count, World.ChangedTiles(world, grid, layer, 0, null));

        World.Remove(world, id);
        World.Process(world);
        Assert.Equal(2, World.Tick(world));
        fixed (int* p = tiles) count = World.ChangedTiles(world, grid, layer, 1, p);
        Assert.Equal(4, count);
        Assert.Equal(new[] { 0, 1, 2, 3 }, tiles.AsSpan(0, count).ToArray());
        fixed (int* p = tiles) Assert.Equal(0, World.ChangedTiles(world, grid, layer, 2, p));

        World.Place(world, layer, 10f, 52f, stamp, 4);
        World.Process(world);
        Assert.Equal(3, World.Tick(world));
        fixed (int* p = tiles) count = World.ChangedTiles(world, grid, layer, 2, p);
        Assert.Equal(1, count);
        Assert.Equal(2, tiles[0]);
        Assert.True(Ascending(tiles, count));
        fixed (int* p = tiles) Assert.Equal(0, World.ChangedTiles(world, grid, layer, 3, p));
    }

    private static bool Ascending(int[] values, int count)
    {
        for (var i = 1; i < count; i++)
            if (values[i - 1] >= values[i])
                return false;
        return true;
    }

    [Fact]
    public void C14_QueryRegionIsRowMajorAndWritesZeroOutsideTheGrid()
    {
        var world = NewWorld();
        var grid = Grid.New(world, 7, 0f, 0f, 128f);
        var layer = Layer.New(world);
        var stamp = NewBox(5, 5, 70);
        World.Place(world, layer, 64.5f, 64.5f, stamp, 4);
        World.Process(world);

        const int width = 20;
        const int height = 15;
        var region = new short[width * height];
        fixed (short* p = region)
        {
            World.QueryRegion(world, grid, layer, -4, -3, width, height, p);
            for (var row = 0; row < height; row++)
            for (var column = 0; column < width; column++)
            {
                var cx = -4 + column;
                var cy = -3 + row;
                var expected = cx < 0 || cy < 0 || cx >= 128 || cy >= 128 ? (short)0 : World.Query(world, grid, layer, cx, cy);
                Assert.Equal(expected, region[row * width + column]);
            }

            var full = new short[128 * 128];
            fixed (short* q = full)
            {
                World.QueryRegion(world, grid, layer, 0, 0, 128, 128, q);
                for (var cy = 0; cy < 128; cy++)
                for (var cx = 0; cx < 128; cx++)
                    Assert.Equal(World.Query(world, grid, layer, cx, cy), full[cy * 128 + cx]);
            }
        }
    }

    [Fact]
    public void C15_AGridAddedAfterItsSourcesMissesThemUntilTheyChange()
    {
        var world = NewWorld();
        var coarse = Grid.New(world, 6, 0f, 0f, 64f);
        var layer = Layer.New(world);
        var stamp = NewBox(2, 2, 50);
        var id = World.Place(world, layer, 20f, 20f, stamp, 4);
        World.Process(world);
        Assert.True(World.Query(world, coarse, layer, 20, 20) > 0);

        var fine = Grid.New(world, 7, 0f, 0f, 64f);
        World.Process(world);
        Assert.Equal(0, World.Query(world, fine, layer, 40, 40));
        Assert.Equal(0L, World.Query(world, fine, layer, 0, 0, 128, 128));

        World.SetGain(world, id, 6);
        World.Process(world);
        Assert.True(World.Query(world, fine, layer, 40, 40) > 0);
        Assert.True(World.Query(world, coarse, layer, 20, 20) > 0);
    }

    [Fact]
    public void C16_StampFreeIsRefusedWhileAnyWorldOrPendingSnapshotUsesIt()
    {
        var stamp = NewBox(3, 3, 40);
        var first = NewWorld();
        var second = NewWorld();
        var third = NewWorld();
        foreach (var world in new[] { first, second, third })
        {
            Grid.New(world, 6, 0f, 0f, 64f);
            Layer.New(world);
        }

        World.Place(first, 0, 20f, 20f, stamp, 4);
        World.Place(second, 0, 20f, 20f, stamp, 4);
        Assert.False(Stamp.Free(stamp));

        World.Free(first);
        _worlds.Remove(first);
        Assert.False(Stamp.Free(stamp));

        World.Free(second);
        _worlds.Remove(second);
        Assert.True(Stamp.Free(stamp));
        _stamps.Remove(stamp);

        var reissued = NewBox(3, 3, 40);
        var applied = World.Place(third, 0, 20f, 20f, reissued, 4);
        World.Process(third);
        World.Remove(third, applied);
        Assert.False(Stamp.Free(reissued));
        World.Process(third);
        Assert.True(Stamp.Free(reissued));
        _stamps.Remove(reissued);

        var neverAppliedStamp = NewBox(3, 3, 40);
        var fourth = NewWorld();
        Grid.New(fourth, 6, 0f, 0f, 64f);
        Layer.New(fourth);
        var neverApplied = World.Place(fourth, 0, 24f, 24f, neverAppliedStamp, 4);
        World.Remove(fourth, neverApplied);
        Assert.True(Stamp.Free(neverAppliedStamp));
        _stamps.Remove(neverAppliedStamp);
        World.Process(fourth);
    }

    [Fact]
    public void C17_BatchPlaceFadeExpireRemovesAfterExactlyTicksProcesses()
    {
        var world = NewWorld();
        var grid = Grid.New(world, 7, 0f, 0f, 128f);
        var layer = Layer.New(world);
        var stamp = NewBox(3, 3, 60);

        var commands = new World.SourceCommand[3];
        commands[0] = World.SourceCommand.Place(layer, 64f, 64f, stamp, 8);
        commands[1] = World.SourceCommand.Fade(World.SourceTarget.CreatedBy(0), 0, 5);
        commands[2] = World.SourceCommand.Expire(World.SourceTarget.CreatedBy(0), 5);
        var results = new World.SourceResult[3];
        Assert.Equal(World.BatchStatus.Applied, World.ApplyBatch(world, commands, results));
        var id = results[0].SourceId;

        for (var tick = 1; tick <= 4; tick++)
        {
            World.Process(world);
            Assert.True(World.TryGetSourceSpec(world, id, out var spec));
            var expected = (sbyte)(8 - (8 * tick + 2) / 5);
            Assert.Equal(expected, spec.Gain);
            Assert.True(World.Query(world, grid, layer, 64, 64) > 0);
        }

        World.Process(world);
        Assert.False(World.TryGetSourceSpec(world, id, out _));
        Assert.Equal(0, World.Query(world, grid, layer, 64, 64));
        Assert.Equal(5, World.Tick(world));
    }

    [Fact]
    public void C18_HandlesAndSourceIdsReuseWithFreshGenerations()
    {
        var first = NewWorld();
        NewWorld();
        Grid.New(first, 6, 0f, 0f, 64f);
        Layer.New(first);
        var stamp = NewBox(3, 3, 40);

        var beforeFree = World.Capture(first);
        World.Free(first);
        _worlds.Remove(first);
        Assert.True(beforeFree.IsValid);
        Assert.Equal(World.BatchStatus.InvalidWorld, World.Process(beforeFree));
        Assert.False(World.Capture(first).IsValid);

        var recycled = World.New();
        _worlds.Add(recycled);
        Assert.Equal(first, recycled);
        Grid.New(recycled, 6, 0f, 0f, 64f);
        Layer.New(recycled);

        var a = World.Place(recycled, 0, 20f, 20f, stamp, 4);
        World.Place(recycled, 0, 30f, 30f, stamp, 4);
        World.Remove(recycled, a);
        var c = World.Place(recycled, 0, 40f, 40f, stamp, 4);
        Assert.NotEqual(default(World.SourceId), a);
        Assert.Equal(a.Slot, c.Slot);
        Assert.True(c.Generation > a.Generation);
        Assert.Equal(World.WriteStatus.StaleSource, World.Move(recycled, a, 50f, 50f));

        var d = World.Place(recycled, 0, 10f, 10f, stamp, 4);
        World.Clear(recycled);
        var e = World.Place(recycled, 0, 12f, 12f, stamp, 4);
        Assert.True(e.Generation > d.Generation);
        Assert.Equal(World.WriteStatus.StaleSource, World.Move(recycled, d, 50f, 50f));
    }

    [Fact]
    public void C19_AdapterWarmPathAllocatesZeroBytesOverAThousandTicks()
    {
        var world = NewWorld();
        var grid = Grid.New(world, 8, 0f, 0f, 256f);
        var layer = Layer.New(world);
        var transient = Layer.New(world, Layer.Residency.Transient, Layer.Precision.Cell);
        var stamp = NewBox(3, 3, 40);
        var cone = NewCone(4, 30, 90);

        const int sources = 64;
        const int probes = 128;
        const int splats = 32;
        var commands = new World.SourceCommand[sources];
        var results = new World.SourceResult[sources];
        var ids = new World.SourceId[sources];
        for (var i = 0; i < sources; i++)
        {
            commands[i] = World.SourceCommand.Place(layer, 16f + i * 3.5f, 16f + (i * 7) % 200, i % 2 == 0 ? stamp : cone, 4);
        }

        Assert.Equal(World.BatchStatus.Applied, World.ApplyBatch(world, commands, results));
        for (var i = 0; i < sources; i++) ids[i] = results[i].SourceId;
        var token = World.Capture(world);
        World.Process(world);

        var probeXs = new float[probes];
        var probeYs = new float[probes];
        var reaches = new float[probes];
        var thresholds = new short[probes];
        for (var i = 0; i < probes; i++)
        {
            probeXs[i] = 8f + (i * 17) % 240;
            probeYs[i] = 8f + (i * 29) % 240;
            reaches[i] = 4f;
            thresholds[i] = 10;
        }

        var values = new short[probes];
        var gradientX = new float[probes];
        var gradientY = new float[probes];
        var totals = new long[probes];
        var maxValues = new short[probes];
        var bestX = new float[probes];
        var bestY = new float[probes];
        var nearValues = new short[probes];
        var nearX = new float[probes];
        var nearY = new float[probes];
        var words = new ulong[2];
        var found = new ulong[2];
        var mask = new ulong[2];
        var tiles = new int[64];
        var region = new short[1024];
        var splatXs = new float[splats];
        var splatYs = new float[splats];
        var splatStamps = new byte[splats];
        var splatGains = new sbyte[splats];
        var syncIds = new World.SourceId[sources];
        var syncXs = new float[sources];
        var syncYs = new float[sources];
        var headings = new float[sources];
        var scales = new float[sources];
        var invalid = new ulong[1];
        var sink = 0L;
        var statuses = 0L;

        void Tick(int tick)
        {
            for (var i = 0; i < sources; i++)
            {
                var x = 16f + (i * 7 + tick * 3) % 220;
                var y = 16f + (i * 13 + tick * 5) % 220;
                commands[i] = World.SourceCommand.Update(
                    World.SourceTarget.Existing(ids[i]), x, y, (tick % 7) * 0.5f, 1f, (sbyte)((tick + i) % 9 - 4));
                syncIds[i] = ids[i];
                syncXs[i] = 16f + (i * 11 + tick * 2) % 220;
                syncYs[i] = 16f + (i * 3 + tick) % 220;
                headings[i] = (tick % 5) * 0.25f;
                scales[i] = 1f;
            }

            statuses += (int)World.ApplyBatch(token, commands, results);
            fixed (World.SourceId* si = syncIds)
            fixed (float* sx = syncXs, sy = syncYs, sh = headings, ss = scales)
            fixed (ulong* iv = invalid)
            {
                statuses += (int)World.Sync(token, si, sx, sy, sources);
                var ranges = stackalloc World.TransformRange[1];
                ranges[0] = new World.TransformRange
                {
                    Sources = si, Xs = sx, Ys = sy, Headings = sh, Scales = ss, Invalid = iv, Count = sources, InvalidWords = 1,
                };
                statuses += (int)World.SyncTransforms(token, new ReadOnlySpan<World.TransformRange>(ranges, 1));
            }

            for (var i = 0; i < splats; i++)
            {
                splatXs[i] = 8f + (i * 19 + tick * 7) % 240;
                splatYs[i] = 8f + (i * 23 + tick * 11) % 240;
                splatStamps[i] = stamp;
                splatGains[i] = (sbyte)(i % 5 - 2);
            }

            World.Splat(world, transient, splatXs, splatYs, splatStamps, splatGains);
            statuses += (int)World.Process(token);

            fixed (float* px = probeXs, py = probeYs, pr = reaches, pgx = gradientX, pgy = gradientY,
                   pbx = bestX, pby = bestY, pnx = nearX, pny = nearY)
            fixed (short* pv = values, pmv = maxValues, pnv = nearValues, pth = thresholds)
            fixed (long* pt = totals)
            fixed (ulong* pw = words, pf = found, pm = mask)
            fixed (int* tp = tiles)
            fixed (short* rg = region)
            {
                World.SenseColumn(world, layer, px, py, probes, pv, pw);
                World.SenseGradientColumn(world, layer, px, py, null, probes, pv, pgx, pgy, pw);
                World.SenseAreaColumn(world, layer, px, py, pr, null, probes, pt, pw);
                World.SenseMaxColumn(world, layer, px, py, pr, probes, pmv, pbx, pby, pw);
                World.SenseNearestColumn(world, layer, px, py, pr, pth, probes, pnv, pnx, pny, pf);
                World.ChangedMask(world, layer, px, py, 4f, World.Tick(world) - 1, probes, pm);
                World.ChangedTiles(world, grid, layer, World.Tick(world) - 1, tp);
                World.QueryRegion(world, grid, layer, 32, 32, 32, 32, rg);
                for (var i = 0; i < probes; i++)
                {
                    sink += World.TrySense(world, layer, px[i], py[i], out pv[i]) ? 1 : 0;
                    sink += World.QueryAt(world, grid, layer, px[i], py[i]);
                    sink += World.Covers(world, px[i], py[i]) ? 1 : 0;
                }

                for (var i = 0; i < 8; i++)
                    if (World.TryGetSourceSpec(world, ids[i], out var spec))
                        sink += spec.Gain;
            }

            sink += World.Tick(world) + World.Revision(world);
        }

        for (var tick = 0; tick < 100; tick++) Tick(tick);

        GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var tick = 100; tick < 1100; tick++) Tick(tick);
        var after = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0L, statuses);
        Assert.NotEqual(0L, sink);
        Assert.Equal(0L, after - before);
    }

    [Fact]
    public void C20_ExceptionsEscapeOnlyAtConstruction()
    {
        var world = NewWorld();
        var grid = Grid.New(world, 6, 0f, 0f, 64f);
        var layer = Layer.New(world);
        var transient = Layer.New(world, Layer.Residency.Transient, Layer.Precision.Cell);
        var stamp = NewBox(3, 3, 40);
        var id = World.Place(world, layer, 20f, 20f, stamp, 4);
        World.Process(world);
        var token = World.Capture(world);
        Exception? thrown = null;
        try
        {
            Assert.Equal((short)0, World.Query(world, 200, layer, 1, 1));
            Assert.Equal((short)0, World.Query(world, grid, 200, 1, 1));
            Assert.Equal(0L, World.Query(world, grid, layer, 1, 1, -2, 3));
            Assert.Equal((short)0, World.QueryAt(world, grid, layer, float.NaN, float.PositiveInfinity));
            Assert.Equal((short)0, World.QueryMax(world, 200, layer, out _, out _));
            World.QueryGradient(world, grid, layer, float.NaN, 0f, out var gx, out var gy);
            Assert.Equal(0, gx);
            Assert.Equal(0, gy);
            fixed (short* region = new short[4])
            {
                World.QueryRegion(world, grid, layer, -10, -10, 2, 2, region);
                World.QueryRegion(200, grid, layer, 0, 0, 2, 2, region);
            }

            Assert.Equal(default(World.SourceId), World.Place(world, 33, 1f, 1f, stamp, 1, out var invalidLayer));
            Assert.Equal(World.WriteStatus.InvalidLayer, invalidLayer);
            Assert.Equal(default(World.SourceId), World.Place(world, layer, float.NaN, 1f, stamp, 1, out var nonFinite));
            Assert.Equal(World.WriteStatus.NonFinite, nonFinite);
            Assert.Equal(default(World.SourceId), World.Place(world, layer, 1f, 1f, 255, 1, out var invalidStamp));
            Assert.Equal(World.WriteStatus.InvalidStamp, invalidStamp);
            Assert.Equal(World.WriteStatus.StaleSource, World.Move(world, default, 1f, 1f));
            Assert.Equal(World.WriteStatus.NonFinite, World.Move(world, id, float.NaN, 1f));
            Assert.Equal(World.WriteStatus.StaleSource, World.SetGain(world, default, 1));
            Assert.Equal(World.WriteStatus.NonFinite, World.Turn(world, id, float.PositiveInfinity));
            Assert.Equal(World.WriteStatus.NonFinite, World.Scale(world, id, float.NegativeInfinity));
            Assert.Equal(World.WriteStatus.StaleSource, World.Remove(world, default));
            Assert.Equal(World.WriteStatus.StaleSource, World.Fade(world, default, 1, 1));
            Assert.Equal(World.WriteStatus.StaleSource, World.Expire(world, default, 1));
            Assert.Equal(0, World.Tick(200));
            Assert.Equal(0, World.Revision(200));
            World.Process(200);
            World.ProcessAll();
            World.Clear(200);
            World.Record(200);
            World.Rewind(200);
            World.StopRecording(200);
            Assert.False(World.Capture(200).IsValid);
            Assert.Equal(World.BatchStatus.InvalidWorld, World.ApplyBatch(200, [], []));
            Assert.Equal(World.BatchStatus.Applied, World.ApplyBatch(token, [], []));
            var shortResults = new World.SourceResult[0];
            var one = new[] { World.SourceCommand.SetGain(World.SourceTarget.Existing(id), 4) };
            Assert.Equal(World.BatchStatus.InsufficientResults, World.ApplyBatch(token, one, shortResults));
            World.Sync(200, null, null, null, 0);
            var badRange = new World.TransformRange { Count = -1 };
            Assert.Equal(World.BatchStatus.InvalidRange,
                World.SyncTransforms(token, new[] { badRange }));
            Assert.Equal(World.ReadStatus.InvalidLayer, World.SenseColumn(world, 33, null, null, null, 0, null, null));
            Assert.Equal(0, World.ChangedMask(world, layer, null, null, float.NaN, 0, 0, null));
            fixed (int* tiles = new int[1])
                Assert.Equal(0, World.ChangedTiles(world, grid, 33, 0, tiles));
            Assert.False(World.Covers(world, float.NaN, 0f));
            Assert.False(World.TrySense(200, layer, 1f, 1f, out _));
            Assert.False(World.TrySenseNearest(world, layer, float.NaN, 1f, 1f, 1, out _, out _, out _));
            Assert.Equal(0, World.Splat(world, layer, new[] { 1f }, new[] { 1f }, stamp, 1));
            Assert.Equal(0, World.Splat(200, transient, new[] { 1f }, new[] { 1f }, stamp, 1));
            Assert.False(Stamp.TryGetSpec(0, out _));
            Assert.False(World.TryGetSourceSpec(world, default, out _));
            Assert.False(Layer.TryGetPrecision(world, 33, out _));
            Assert.False(Grid.TryGetSpec(world, 33, out _, out _, out _, out _));
            World.CopyWorldIds(new byte[4]);
        }
        catch (Exception exception)
        {
            thrown = exception;
        }

        Assert.Null(thrown);

        Assert.Throws<ArgumentOutOfRangeException>(() => Grid.New(world, 4, 0f, 0f, 64f));
        Assert.Throws<ArgumentOutOfRangeException>(() => Grid.New(world, 15, 0f, 0f, 64f));
        Assert.Throws<ArgumentOutOfRangeException>(() => Grid.New(world, 6, float.NaN, 0f, 64f));
        Assert.Throws<ArgumentOutOfRangeException>(() => Grid.New(world, 6, 0f, 0f, 0f));
        Assert.Throws<ArgumentOutOfRangeException>(() => Grid.New(world, 5, 0f, 0f, 0.001f));
        Assert.Throws<ArgumentOutOfRangeException>(() => Stamp.Box(0, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Stamp.Box(257, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Stamp.Disk(0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Stamp.Disk(129, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Stamp.Cone(4, 1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Stamp.Cone(4, 1, 361));
        Assert.Throws<ArgumentException>(() => Stamp.New(new sbyte[3], 2, 2));

        Span<byte> live = stackalloc byte[256];
        var liveCount = World.CopyWorldIds(live);
        var created = new List<byte>();
        try
        {
            while (liveCount + created.Count < 255) created.Add(World.New());
            Assert.Throws<InvalidOperationException>(() => World.New());

            var full = created[0];
            for (var i = 0; i < 32; i++) Grid.New(full, 5, 0f, 0f, 1f);
            var gridCount = Grid.Count(full);
            Assert.Throws<InvalidOperationException>(() => Grid.New(full, 5, 0f, 0f, 1f));
            Assert.Equal(gridCount, Grid.Count(full));

            for (var i = 0; i < 32; i++) Layer.New(full);
            var layerCount = Layer.Count(full);
            Assert.Throws<InvalidOperationException>(() => Layer.New(full));
            Assert.Equal(layerCount, Layer.Count(full));
        }
        finally
        {
            created.Reverse();
            foreach (var created2 in created) World.Free(created2);
        }
    }

    [Fact]
    public void C21_BatchPlaceHeadingMatchesTheScalarTurnConvention()
    {
        var turned = NewWorld();
        var batched = NewWorld();
        var grid = Grid.New(turned, 7, 0f, 0f, 128f);
        Grid.New(batched, 7, 0f, 0f, 128f);
        var layerTurned = Layer.New(turned);
        var layerBatched = Layer.New(batched);
        var cone = NewCone(12, 80, 60);

        var commands = new World.SourceCommand[1];
        commands[0] = World.SourceCommand.Place(layerBatched, 64.5f, 64.5f, cone, 4, MathF.PI / 2f);
        var results = new World.SourceResult[1];
        Assert.Equal(World.BatchStatus.Applied, World.ApplyBatch(batched, commands, results));

        var id = World.Place(turned, layerTurned, 64.5f, 64.5f, cone, 4);
        World.Turn(turned, id, MathF.PI / 2f);

        World.Process(turned);
        World.Process(batched);
        Assert.Equal(GiFieldHash.OfLayer(turned, layerTurned), GiFieldHash.OfLayer(batched, layerBatched));

        commands[0] = World.SourceCommand.Place(layerBatched, 30.5f, 30.5f, cone, 4);
        Assert.Equal(World.BatchStatus.Applied, World.ApplyBatch(batched, commands, results));
        World.Process(batched);

        Assert.True(World.Query(batched, grid, layerBatched, 64, 70) > 0);
        Assert.Equal(0, World.Query(batched, grid, layerBatched, 64, 58));
        Assert.Equal(0, World.Query(batched, grid, layerBatched, 70, 64));

        Assert.True(World.Query(batched, grid, layerBatched, 36, 30) > 0);
        Assert.Equal(0, World.Query(batched, grid, layerBatched, 24, 30));
        Assert.Equal(0, World.Query(batched, grid, layerBatched, 30, 36));
    }

    private struct Rng
    {
        private ulong _state;

        public Rng(ulong seed)
        {
            _state = seed * 6364136223846793005ul + 1442695040888963407ul;
        }

        public int Next(int bound)
        {
            _state ^= _state >> 12;
            _state ^= _state << 25;
            _state ^= _state >> 27;
            return (int)(_state * 2685821657736338717ul >> 33) % bound;
        }

        public float NextFloat() => Next(1 << 24) / (float)(1 << 24);

        public void Shuffle<T>(T[] items) => Shuffle(items, items.Length);

        public void Shuffle<T>(T[] items, int count)
        {
            for (var i = count - 1; i > 0; i--)
            {
                var j = Next(i + 1);
                (items[i], items[j]) = (items[j], items[i]);
            }
        }
    }
}
