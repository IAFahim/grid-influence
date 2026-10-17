using Gi.Entities.Tests.Support;
using Xunit;

namespace Gi.Entities.Columns.Tests;

public sealed unsafe class GiContractTests : IDisposable
{
    private readonly List<byte> _worlds = [];
    private readonly List<byte> _stamps = [];

    public void Dispose()
    {
        foreach (var world in _worlds) Gi.World.Free(world);
        foreach (var stamp in _stamps) Gi.Stamp.Free(stamp);
    }

    private byte NewWorld()
    {
        var world = Gi.World.New();
        _worlds.Add(world);
        return world;
    }

    private byte NewBox(int width, int height, sbyte value)
    {
        var stamp = Gi.Stamp.Box((ushort)width, (ushort)height, value);
        _stamps.Add(stamp);
        return stamp;
    }

    private byte NewTent(int width, int height, sbyte value)
    {
        var stamp = Gi.Stamp.Tent((ushort)width, (ushort)height, value);
        _stamps.Add(stamp);
        return stamp;
    }

    private byte NewCone(int radius, sbyte value, ushort arc)
    {
        var stamp = Gi.Stamp.Cone((ushort)radius, value, arc);
        _stamps.Add(stamp);
        return stamp;
    }

    private byte NewRaster(in Rng rng, int width, int height)
    {
        var cells = new sbyte[width * height];
        for (var i = 0; i < cells.Length; i++) cells[i] = (sbyte)(rng.Next(101) - 50);
        var stamp = Gi.Stamp.New(cells, (ushort)width, (ushort)height);
        _stamps.Add(stamp);
        return stamp;
    }

    [Fact]
    public void C01_PlaceApplied_NeverReturnsDefaultSourceId()
    {
        var world = NewWorld();
        Gi.Grid.New(world, 7, 0f, 0f, 128f);
        var first = Gi.Layer.New(world);
        var second = Gi.Layer.New(world);
        var stamp = NewBox(4, 4, 50);

        for (var i = 0; i < 100; i++)
        {
            var id = Gi.World.Place(world, i % 2 == 0 ? first : second, 10f + i * 0.5f, 20f + i * 0.25f, stamp,
                (sbyte)(i % 33 - 16), out var status);
            Assert.Equal(Gi.World.WriteStatus.Applied, status);
            Assert.NotEqual(default(Gi.World.SourceId), id);
            Assert.NotEqual(0u, id.Generation);
        }

        var commands = new Gi.World.SourceCommand[100];
        var results = new Gi.World.SourceResult[100];
        for (var i = 0; i < commands.Length; i++)
            commands[i] = Gi.World.SourceCommand.Place(first, 10f + i, 30f + i, stamp, 5);
        Assert.Equal(Gi.World.BatchStatus.Applied, Gi.World.ApplyBatch(world, commands, results));
        foreach (var result in results)
        {
            Assert.Equal(Gi.World.SourceResultStatus.Applied, result.Status);
            Assert.NotEqual(default(Gi.World.SourceId), result.SourceId);
            Assert.NotEqual(0u, result.SourceId.Generation);
        }
    }

    [Fact]
    public unsafe void C02_TransformRangeInvalid_IsAnOutputMaskOfRejectedRows()
    {
        var world = NewWorld();
        var grid = Gi.Grid.New(world, 7, 0f, 0f, 128f);
        var layer = Gi.Layer.New(world);
        var stamp = NewBox(2, 2, 40);
        var alive = Gi.World.Place(world, layer, 20f, 20f, stamp, 4);
        var stale = Gi.World.Place(world, layer, 30f, 30f, stamp, 4);
        Gi.World.Remove(world, stale);
        var held = Gi.World.Place(world, layer, 40f, 40f, stamp, 4);
        Gi.World.Process(world);

        var sources = stackalloc Gi.World.SourceId[3];
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

        var ranges = stackalloc Gi.World.TransformRange[1];
        ranges[0] = new Gi.World.TransformRange
        {
            Sources = sources, Xs = xs, Ys = ys, Invalid = mask, Count = 3, InvalidWords = 2,
        };
        var token = Gi.World.Capture(world);
        var hash = GiFieldHash.OfLayer(world, layer);
        var revision = Gi.World.Revision(world);

        Assert.Equal(Gi.World.BatchStatus.Applied, Gi.World.SyncTransforms(token, new ReadOnlySpan<Gi.World.TransformRange>(ranges, 1)));
        Assert.Equal(3ul, mask[0]);
        Assert.Equal(ulong.MaxValue, mask[1]);
        Assert.True(Gi.World.TryGetSourceSpec(world, alive, out var spec));
        Assert.Equal(50f, spec.X);
        Assert.Equal(50f, spec.Y);
        Assert.True(Gi.World.TryGetSourceSpec(world, held, out var heldSpec));
        Assert.Equal(40f, heldSpec.X);
        Assert.Equal(40f, heldSpec.Y);

        ranges[0].InvalidWords = 0;
        Assert.Equal(Gi.World.BatchStatus.InvalidRange, Gi.World.SyncTransforms(token, new ReadOnlySpan<Gi.World.TransformRange>(ranges, 1)));
        Assert.Equal(revision, Gi.World.Revision(world));
        Assert.Equal(hash, GiFieldHash.OfLayer(world, layer));
    }

    [Fact]
    public void C03_ApplyBatchAcceptsTwoToTheTwentyFourthCommandsAndRefusesOneMore()
    {
        var world = NewWorld();
        Gi.Grid.New(world, 7, 0f, 0f, 128f);
        var layer = Gi.Layer.New(world);
        var stamp = NewBox(1, 1, 30);

        var commands = new Gi.World.SourceCommand[4096];
        var results = new Gi.World.SourceResult[4096];
        for (var i = 0; i < commands.Length; i++)
            commands[i] = Gi.World.SourceCommand.Place(layer, 10f + (i & 63), 10f + (i >> 6), stamp, 3);
        var token = Gi.World.Capture(world);
        Assert.Equal(Gi.World.BatchStatus.Applied, Gi.World.ApplyBatch(token, commands, results));
        Assert.All(results, result => Assert.Equal(Gi.World.SourceResultStatus.Applied, result.Status));

        Assert.Equal(Gi.World.BatchStatus.InsufficientResults,
            Gi.World.ApplyBatch(token, commands, results.AsSpan(0, commands.Length - 1)));

        var huge = GC.AllocateUninitializedArray<Gi.World.SourceCommand>((1 << 24) + 1);
        var revision = Gi.World.Revision(world);
        var hash = GiFieldHash.OfLayer(world, layer);
        Assert.Equal(Gi.World.BatchStatus.TooManyCommands,
            Gi.World.ApplyBatch(token, huge, Span<Gi.World.SourceResult>.Empty));
        Assert.Equal(Gi.World.BatchStatus.InsufficientResults,
            Gi.World.ApplyBatch(token, new ReadOnlySpan<Gi.World.SourceCommand>(huge, 0, 1 << 24), Span<Gi.World.SourceResult>.Empty));
        Assert.Equal(revision, Gi.World.Revision(world));
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
        var a = Gi.World.New();
        var b = Gi.World.New();
        try
        {
            foreach (var world in new[] { a, b })
            {
                Gi.Grid.New(world, 6, 0f, 0f, 64f);
                Gi.Grid.New(world, 7, 0f, 0f, 64f);
                Gi.Layer.New(world);
                Gi.Layer.New(world, Gi.Layer.Precision.Cell);
                Gi.Layer.New(world, Gi.Layer.Residency.Transient, Gi.Layer.Precision.Cell);
                Gi.Layer.Sum(world, 0, 1, 2, 1, 1);
            }

            stamps[0] = Gi.Stamp.Box(3, 3, 50);
            stamps[1] = Gi.Stamp.Tent(5, 5, 40);
            stamps[2] = Gi.Stamp.Cone(6, 30, 60);
            var cells = new sbyte[16];
            for (var i = 0; i < cells.Length; i++) cells[i] = (sbyte)(rng.Next(101) - 50);
            stamps[3] = Gi.Stamp.New(cells, 4, 4);

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

            var idsA = new Gi.World.SourceId[64];
            var idsB = new Gi.World.SourceId[64];
            for (var i = 0; i < initial; i++)
            {
                idsA[i] = Gi.World.Place(a, layers[i], xs[i], ys[i], stamps[stampIndex[i]], gains[i]);
                Gi.World.Turn(a, idsA[i], headings[i]);
            }

            for (var i = 0; i < initial; i++)
            {
                var j = order[i];
                idsB[j] = Gi.World.Place(b, layers[j], xs[j], ys[j], stamps[stampIndex[j]], gains[j]);
                Gi.World.Turn(b, idsB[j], headings[j]);
            }

            var live = new bool[64];
            Array.Fill(live, true, 0, initial);
            var ops = new Op[40];
            var commands = new Gi.World.SourceCommand[48];
            var results = new Gi.World.SourceResult[48];
            var splatXs = new float[8];
            var splatYs = new float[8];
            var splatStamps = new byte[8];
            var splatGains = new sbyte[8];
            var splatOrder = new int[8];
            var groupOrder = new int[43];
            var slicePlaceIndex = new int[3];
            var slicePlaceAt = new int[3];
            var transientId = stamps[0];
            var groups = 0;

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

                groups = opCount + placeCount;
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
                    var target = Gi.World.SourceTarget.Existing(idsA[op.Source]);
                    commands[ci++] = op.Kind switch
                    {
                        0 => Gi.World.SourceCommand.Move(target, op.F0, op.F1),
                        1 => Gi.World.SourceCommand.Turn(target, op.F0),
                        2 => Gi.World.SourceCommand.SetGain(target, op.G),
                        3 => Gi.World.SourceCommand.Fade(target, op.G, op.I0),
                        4 => Gi.World.SourceCommand.Expire(target, op.I0),
                        _ => Gi.World.SourceCommand.Remove(target),
                    };
                }

                for (var i = 0; i < placeCount; i++)
                {
                    var index = count + i;
                    commands[ci++] = Gi.World.SourceCommand.Place(layers[index], xs[index], ys[index],
                        stamps[stampIndex[index]], gains[index], headings[index]);
                }

                Assert.Equal(Gi.World.BatchStatus.Applied, Gi.World.ApplyBatch(a, commands.AsSpan(0, ci), results));
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
                            var target = Gi.World.SourceTarget.Existing(idsB[op.Source]);
                            commands[cbi++] = op.Kind switch
                            {
                                0 => Gi.World.SourceCommand.Move(target, op.F0, op.F1),
                                1 => Gi.World.SourceCommand.Turn(target, op.F0),
                                2 => Gi.World.SourceCommand.SetGain(target, op.G),
                                3 => Gi.World.SourceCommand.Fade(target, op.G, op.I0),
                                4 => Gi.World.SourceCommand.Expire(target, op.I0),
                                _ => Gi.World.SourceCommand.Remove(target),
                            };
                        }
                        else
                        {
                            var index = count + group - opCount;
                            slicePlaceIndex[slicePlaces] = index;
                            slicePlaceAt[slicePlaces] = cbi;
                            slicePlaces++;
                            commands[cbi++] = Gi.World.SourceCommand.Place(layers[index], xs[index], ys[index],
                                stamps[stampIndex[index]], gains[index], headings[index]);
                        }
                    }

                    Assert.Equal(Gi.World.BatchStatus.Applied, Gi.World.ApplyBatch(b, commands.AsSpan(0, cbi), results));
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

                Gi.World.Splat(a, 2, splatXs, splatYs, splatStamps, splatGains);
                Gi.World.Splat(b, 2, bx, by, bs, bg);

                Gi.World.Process(a);
                Gi.World.Process(b);

                for (byte layer = 0; layer < 4; layer++)
                    Assert.Equal(GiFieldHash.OfLayer(a, layer), GiFieldHash.OfLayer(b, layer));

                var total = count + placeCount;
                for (var i = 0; i < total; i++)
                {
                    var aliveA = Gi.World.TryGetSourceSpec(a, idsA[i], out _);
                    var aliveB = Gi.World.TryGetSourceSpec(b, idsB[i], out _);
                    Assert.Equal(aliveA, aliveB);
                    live[i] = aliveA;
                }

                count = total;
            }
        }
        finally
        {
            Gi.World.Free(a);
            Gi.World.Free(b);
            foreach (var stamp in stamps) Gi.Stamp.Free(stamp);
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
                     Gi.Layer.Precision.Exact, Gi.Layer.Precision.Half, Gi.Layer.Precision.Quarter, Gi.Layer.Precision.Cell,
                 })
            C05One(precision, rng);
    }

    private unsafe void C05One(Gi.Layer.Precision precision, Rng rng)
    {
        var a = Gi.World.New();
        var b = Gi.World.New();
        var stamp = Gi.Stamp.Cone(5, 30, 90);
        try
        {
            foreach (var world in new[] { a, b })
            {
                Gi.Grid.New(world, 7, 0f, 0f, 128f);
                Gi.Layer.New(world, Gi.Layer.Residency.Transient, precision);
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

                    Gi.World.Splat(a, 0, xs, ys, stamp, gain);
                    Gi.World.Splat(b, 0, xs, ys, stampIds, gains);
                    Gi.World.Process(a);
                    Gi.World.Process(b);
                    Assert.Equal(GiFieldHash.OfLayer(a, 0), GiFieldHash.OfLayer(b, 0));
                }
            }
        }
        finally
        {
            Gi.World.Free(a);
            Gi.World.Free(b);
            Gi.Stamp.Free(stamp);
        }
    }

    [Fact]
    public void C06_ProcessAdvancesTickByExactlyOneAndNothingElseMovesIt()
    {
        var world = NewWorld();
        Gi.Grid.New(world, 7, 0f, 0f, 128f);
        var layer = Gi.Layer.New(world);
        var transient = Gi.Layer.New(world, Gi.Layer.Residency.Transient, Gi.Layer.Precision.Cell);
        var stamp = NewBox(4, 4, 50);
        Assert.Equal(0, Gi.World.Tick(world));

        var id = Gi.World.Place(world, layer, 64f, 64f, stamp, 4);
        Gi.World.Move(world, id, 65f, 64f);
        Gi.World.Turn(world, id, MathF.PI / 4f);
        Gi.World.Scale(world, id, 1.5f);
        Gi.World.SetGain(world, id, 5);
        Gi.World.Fade(world, id, 1, 4);
        Gi.World.Expire(world, id, 100);
        Assert.Equal(0, Gi.World.Tick(world));

        var commands = new[] { Gi.World.SourceCommand.Move(Gi.World.SourceTarget.Existing(id), 66f, 64f) };
        var results = new Gi.World.SourceResult[1];
        var token = Gi.World.Capture(world);
        Assert.Equal(Gi.World.BatchStatus.Applied, Gi.World.ApplyBatch(token, commands, results));
        var sources = stackalloc Gi.World.SourceId[1];
        var xs = stackalloc float[1];
        var ys = stackalloc float[1];
        sources[0] = id;
        xs[0] = 67f;
        ys[0] = 64f;
        Assert.Equal(Gi.World.BatchStatus.Applied, Gi.World.Sync(token, sources, xs, ys, 1));
        var ranges = stackalloc Gi.World.TransformRange[1];
        ranges[0] = new Gi.World.TransformRange { Sources = sources, Xs = xs, Ys = ys, Count = 1 };
        Assert.Equal(Gi.World.BatchStatus.Applied, Gi.World.SyncTransforms(token, new ReadOnlySpan<Gi.World.TransformRange>(ranges, 1)));
        Gi.World.Splat(world, transient, new[] { 64f }, new[] { 64f }, stamp, 2);
        Assert.Equal(0, Gi.World.Tick(world));

        for (var i = 1; i <= 4; i++)
        {
            Gi.World.Process(world);
            Assert.Equal(i, Gi.World.Tick(world));
        }

        Gi.World.ProcessAll();
        Assert.Equal(5, Gi.World.Tick(world));
        Assert.Equal(Gi.World.BatchStatus.Applied, Gi.World.Process(token));
        Assert.Equal(6, Gi.World.Tick(world));
    }

    [Fact]
    public unsafe void C07_OnlyLifecycleCallsBumpRevision()
    {
        var world = NewWorld();
        var grid = Gi.Grid.New(world, 7, 0f, 0f, 128f);
        var layer = Gi.Layer.New(world);
        var transient = Gi.Layer.New(world, Gi.Layer.Residency.Transient, Gi.Layer.Precision.Cell);
        var stamp = NewBox(4, 4, 50);
        var id = Gi.World.Place(world, layer, 64f, 64f, stamp, 4);

        var token = Gi.World.Capture(world);
        var revision = Gi.World.Revision(world);

        Gi.World.Move(world, id, 65f, 64f);
        Gi.World.Turn(world, id, 1f);
        Gi.World.Scale(world, id, 1.5f);
        Gi.World.SetGain(world, id, 6);
        Gi.World.Fade(world, id, 2, 5);
        Gi.World.Expire(world, id, 50);
        var commands = new[] { Gi.World.SourceCommand.Move(Gi.World.SourceTarget.Existing(id), 66f, 64f) };
        var results = new Gi.World.SourceResult[1];
        Assert.Equal(Gi.World.BatchStatus.Applied, Gi.World.ApplyBatch(token, commands, results));
        var sources = stackalloc Gi.World.SourceId[1];
        var xs = stackalloc float[1];
        var ys = stackalloc float[1];
        sources[0] = id;
        xs[0] = 67f;
        ys[0] = 64f;
        Assert.Equal(Gi.World.BatchStatus.Applied, Gi.World.Sync(token, sources, xs, ys, 1));
        var ranges = stackalloc Gi.World.TransformRange[1];
        ranges[0] = new Gi.World.TransformRange { Sources = sources, Xs = xs, Ys = ys, Count = 1 };
        Assert.Equal(Gi.World.BatchStatus.Applied, Gi.World.SyncTransforms(token, new ReadOnlySpan<Gi.World.TransformRange>(ranges, 1)));
        Gi.World.Splat(world, transient, new[] { 64f }, new[] { 64f }, stamp, 2);
        Assert.Equal(Gi.World.BatchStatus.Applied, Gi.World.Process(token));
        Gi.World.ProcessAll();
        var scratch = stackalloc short[64];
        Gi.World.QueryRegion(world, grid, layer, 0, 0, 8, 8, scratch);

        Assert.Equal(revision, Gi.World.Revision(world));

        Gi.World.Clear(world);
        var cleared = Gi.World.Revision(world);
        Assert.NotEqual(revision, cleared);
        Assert.Equal(Gi.World.BatchStatus.StaleRevision, Gi.World.Process(token));

        var fresh = Gi.World.Capture(world);
        Assert.True(fresh.IsValid);
        Assert.Equal(Gi.World.BatchStatus.Applied, Gi.World.Process(fresh));
        Assert.Equal(cleared, Gi.World.Revision(world));

        var again = Gi.World.Place(world, layer, 64f, 64f, stamp, 4);
        Gi.World.Record(world);
        Gi.World.Move(world, again, 70f, 64f);
        Gi.World.Rewind(world);
        Assert.NotEqual(cleared, Gi.World.Revision(world));
    }

    [Fact]
    public unsafe void C08_PureReadsFromEightThreadsMatchSerial()
    {
        var world = NewWorld();
        var grid = Gi.Grid.New(world, 7, 0f, 0f, 128f);
        var coarse = Gi.Grid.New(world, 6, -32f, -32f, 96f);
        var layer = Gi.Layer.New(world);
        var transient = Gi.Layer.New(world, Gi.Layer.Residency.Transient, Gi.Layer.Precision.Cell);
        var derived = Gi.Layer.Sum(world, layer, 1, 1);
        var box = NewBox(4, 4, 50);
        var tent = NewTent(6, 6, 40);
        var cone = NewCone(8, 30, 120);

        for (var i = 0; i < 12; i++)
        {
            var stamp = i % 3 == 0 ? box : i % 3 == 1 ? tent : cone;
            var placed = Gi.World.Place(world, layer, 12f + i * 8.5f, 20f + (i % 5) * 17.25f, stamp, (sbyte)(i % 9 + 1));
            Gi.World.Turn(world, placed, (i % 4) * (MathF.PI / 8f));
            Gi.World.Process(world);
            Gi.World.Splat(world, transient, new[] { 10f + i * 9.125f }, new[] { 100f - i * 6.5f }, box, 3);
        }

        Gi.World.Process(world);
        var since = Gi.World.Tick(world) - 1;

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

    private static unsafe void RunReads(byte world, byte grid, byte layer, int since, float[] xs, float[] ys, ReadOutcomes o)
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
            Gi.World.SenseColumn(world, layer, px, py, 97, cv, whole);
            Gi.World.SenseGradientColumn(world, layer, px, py, null, 97, cv, cgx, cgy, whole);
            Gi.World.SenseAreaColumn(world, layer, px, py, pr, null, 97, ca, whole);
            Gi.World.SenseMaxColumn(world, layer, px, py, pr, 97, cmv, cmx, cmy, whole);
            Gi.World.SenseNearestColumn(world, layer, px, py, pr, th, 97, cnv, cnx, cny, found);
            Gi.World.ChangedMask(world, layer, px, py, 4f, since, 97, mask);
            o.TileCount = Gi.World.ChangedTiles(world, grid, layer, since, tiles);
            o.QueryMaxValue = Gi.World.QueryMax(world, grid, layer, out o.QueryMaxX, out o.QueryMaxY);
            Gi.World.QueryRegion(world, grid, layer, 0, 0, 128, 128, region);
            for (var i = 0; i < 97; i++)
            {
                o.Complete[i] = Gi.World.TrySense(world, layer, px[i], py[i], out o.Values[i]);
                Gi.World.TrySenseGradient(world, layer, px[i], py[i], out o.GradientX[i], out o.GradientY[i]);
                Gi.World.TrySenseArea(world, layer, px[i], py[i], 4f, out o.Areas[i]);
                Gi.World.TrySenseMax(world, layer, px[i], py[i], 4f, out o.MaxValues[i], out o.MaxX[i], out o.MaxY[i]);
                Gi.World.TrySenseNearest(world, layer, px[i], py[i], 4f, 10, out o.NearValues[i], out o.NearX[i], out o.NearY[i]);
                o.Covers[i] = Gi.World.Covers(world, px[i], py[i]);
                o.Changed[i] = Gi.World.Changed(world, layer, px[i], py[i], 4f, since);
                o.CellValues[i] = Gi.World.Query(world, grid, layer, (int)px[i], (int)py[i]);
                o.AtValues[i] = Gi.World.QueryAt(world, grid, layer, px[i], py[i]);
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
    public unsafe void C09_ExclusionReadsAreKernelCacheStateInvariant()
    {
        var world = NewWorld();
        var grid = Gi.Grid.New(world, 7, 0f, 0f, 128f);
        var layer = Gi.Layer.New(world);
        var stamps = new byte[17];
        var ids = new Gi.World.SourceId[17];
        for (var i = 0; i < stamps.Length; i++)
        {
            stamps[i] = Gi.Stamp.Box((ushort)(i + 1), (ushort)(i + 1), 60);
            _stamps.Add(stamps[i]);
            ids[i] = Gi.World.Place(world, layer, 16f + i * 6.5f, 64f, stamps[i], 4);
        }

        Gi.World.Process(world);

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
                    var value = Gi.World.TrySense(world, layer, 16f + i * 6.5f, 64f, ids[i], out _);
                    Assert.True(value);
                }
            }
            else
            {
                Gi.World.Process(world);
                for (var i = 8; i < 17; i++)
                    Assert.True(Gi.World.TrySense(world, layer, 16f + i * 6.5f, 64f, ids[i], out _));
            }

            var n = 0;
            for (var i = 0; i < 8; i++)
            for (var d = 0; d < 5; d++)
            {
                xs[n] = 16f + i * 6.5f + d * 0.25f;
                ys[n] = 64f + (d % 2 == 0 ? 0.5f : -0.5f);
                Assert.True(Gi.World.TrySense(world, layer, xs[n], ys[n], ids[i], out measured[pass][n]));
                Assert.True(Gi.World.TrySenseGradient(world, layer, xs[n], ys[n], ids[i], out var gx, out var gy));
                Assert.True(Gi.World.TrySenseArea(world, layer, xs[n], ys[n], 3f, ids[i], out _));
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
        var grid = Gi.Grid.New(world, 7, 0f, 0f, 128f);
        var first = Gi.Layer.New(world);
        var second = Gi.Layer.New(world);
        var unrelated = Gi.Layer.New(world);
        var sum = Gi.Layer.Sum(world, first, 2, second, 3, 1);
        var min = Gi.Layer.Min(world, first, second);
        var box = NewBox(3, 3, 40);
        var other = NewBox(2, 2, 60);
        var onFirst = Gi.World.Place(world, first, 40.5f, 40.5f, box, 6);
        var onSecond = Gi.World.Place(world, second, 41.5f, 41.5f, other, -5);
        var onUnrelated = Gi.World.Place(world, unrelated, 42.5f, 42.5f, box, 8);
        Gi.World.Process(world);

        for (var dx = -2; dx <= 2; dx++)
        for (var dy = -2; dy <= 2; dy++)
        {
            var x = 41f + dx * 0.75f;
            var y = 41f + dy * 0.75f;
            Assert.True(Gi.World.TrySense(world, first, x, y, onFirst, out var firstExcluded));
            Assert.True(Gi.World.TrySense(world, second, x, y, out var secondValue));
            Assert.True(Gi.World.TrySense(world, sum, x, y, out var plainSum));
            Assert.True(Gi.World.TrySense(world, sum, x, y, onFirst, out var sumExcluded));
            Assert.True(Gi.World.TrySense(world, sum, x, y, onUnrelated, out var sumUnrelated));
            Assert.True(Gi.World.TrySense(world, min, x, y, onFirst, out var minExcluded));

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
    public unsafe void C11_WholeAndFoundBitsAreEntryIndexedAndTailBitsAreCleared()
    {
        var world = NewWorld();
        var grid = Gi.Grid.New(world, 7, 0f, 0f, 128f);
        var layer = Gi.Layer.New(world);
        var stamp = NewBox(8, 8, 40);
        Gi.World.Place(world, layer, 64f, 64f, stamp, 5);
        Gi.World.Process(world);

        var xs = new float[70];
        var ys = new float[70];
        for (var i = 0; i < 70; i++)
        {
            xs[i] = 40f + (i % 10) * 2.5f;
            ys[i] = 40f + (i / 10) * 2.5f;
        }

        var whole = new ulong[4];
        var values = new short[70];
        whole.AsSpan().Fill(0xAAAAAAAAAAAAAAAAul);
        fixed (float* px = xs, py = ys)
        fixed (short* pv = values)
        fixed (ulong* pw = whole)
        {
            Gi.World.SenseColumn(world, layer, px, py, 70, pv, pw);
            Assert.Equal(ulong.MaxValue, whole[0]);
            Assert.Equal(63ul, whole[1]);
            Assert.Equal(0xAAAAAAAAAAAAAAAAul, whole[2]);

            Gi.World.SenseColumn(world, layer, px, py, 64, pv, pw);
            Assert.Equal(ulong.MaxValue, whole[0]);
            Assert.Equal(63ul, whole[1]);

            whole[1] = 0xAAAAAAAAAAAAAAAAul;
            var reaches = new float[70];
            reaches.AsSpan().Fill(30f);
            var thresholds = new short[70];
            thresholds.AsSpan().Fill((short)10);
            var nearValues = new short[70];
            var nearX = new float[70];
            var nearY = new float[70];
            var found = new ulong[4];
            found.AsSpan().Fill(0xAAAAAAAAAAAAAAAAul);
            fixed (float* pr = reaches, pnx = nearX, pny = nearY)
            fixed (short* pnv = nearValues, pth = thresholds)
            fixed (ulong* pf = found)
            {
                Gi.World.SenseNearestColumn(world, layer, px, py, pr, pth, 70, pnv, pnx, pny, pf);
                Assert.Equal(ulong.MaxValue, pf[0]);
                Assert.Equal(63ul, pf[1]);
                Assert.Equal(0xAAAAAAAAAAAAAAAAul, pf[2]);
            }

            var mask = new ulong[4];
            mask.AsSpan().Fill(0xAAAAAAAAAAAAAAAAul);
            fixed (ulong* pm = mask)
            {
                Gi.World.ChangedMask(world, layer, px, py, 4f, Gi.World.Tick(world) - 1, 70, pm);
                Assert.Equal(0ul, pm[1] & ~((1ul << 6) - 1));
                Assert.Equal(0xAAAAAAAAAAAAAAAAul, mask[2]);
            }
        }
    }

    [Fact]
    public unsafe void C12_NearestColumnWritesMinValueAndThePointItselfWhenNothingIsFound()
    {
        var world = NewWorld();
        var grid = Gi.Grid.New(world, 7, 0f, 0f, 128f);
        var layer = Gi.Layer.New(world);
        var stamp = NewBox(2, 2, 10);
        Gi.World.Place(world, layer, 64f, 64f, stamp, 1);
        Gi.World.Process(world);

        var xs = new float[] { 64f, 64.5f, 400f };
        var ys = new float[] { 64.5f, 64f, 400f };
        var reaches = new float[] { 2f, 2f, 2f };
        var thresholds = new short[] { 100, 100, 100 };
        var values = new short[3];
        var nearX = new float[3];
        var nearY = new float[3];
        var found = new ulong[1];

        fixed (float* px = xs, py = ys, pr = reaches, pnx = nearX, pny = nearY)
        fixed (short* pv = values, pth = thresholds)
        fixed (ulong* pf = found)
        {
            var status = Gi.World.SenseNearestColumn(world, layer, px, py, pr, pth, 3, pv, pnx, pny, pf);
            Assert.Equal(Gi.World.ReadStatus.Partial, status);
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
            var status = Gi.World.SenseNearestColumn(200, layer, px, py, pr, pth, 3, pv, pnx, pny, pf);
            Assert.Equal(Gi.World.ReadStatus.InvalidLayer, status);
            Assert.All(values, v => Assert.Equal(short.MinValue, v));
            Assert.Equal(0ul, found[0]);
            Assert.All(nearX, x => Assert.Equal(0f, x));
            Assert.All(nearY, y => Assert.Equal(0f, y));
        }
    }

    [Fact]
    public unsafe void C13_ChangedTilesSinceListsEachChangedTileOnceAscendingAfterItsTick()
    {
        var world = NewWorld();
        var grid = Gi.Grid.New(world, 6, 0f, 0f, 64f);
        var layer = Gi.Layer.New(world);
        var stamp = NewBox(40, 40, 5);
        var id = Gi.World.Place(world, layer, 32f, 32f, stamp, 4);
        Gi.World.Process(world);
        Assert.Equal(1, Gi.World.Tick(world));

        var tiles = new int[4];
        int count;
        fixed (int* p = tiles) count = Gi.World.ChangedTiles(world, grid, layer, 0, p);
        Assert.Equal(4, count);
        Assert.Equal(new[] { 0, 1, 2, 3 }, tiles.AsSpan(0, count).ToArray());
        Assert.True(Ascending(tiles, count));
        fixed (int* p = tiles) Assert.Equal(0, Gi.World.ChangedTiles(world, grid, layer, 1, p));
        fixed (int* p = tiles) Assert.Equal(count, Gi.World.ChangedTiles(world, grid, layer, 0, null));

        Gi.World.Remove(world, id);
        Gi.World.Process(world);
        Assert.Equal(2, Gi.World.Tick(world));
        fixed (int* p = tiles) count = Gi.World.ChangedTiles(world, grid, layer, 1, p);
        Assert.Equal(4, count);
        Assert.Equal(new[] { 0, 1, 2, 3 }, tiles.AsSpan(0, count).ToArray());
        fixed (int* p = tiles) Assert.Equal(0, Gi.World.ChangedTiles(world, grid, layer, 2, p));

        var corner = Gi.World.Place(world, layer, 10f, 52f, stamp, 4);
        Gi.World.Process(world);
        Assert.Equal(3, Gi.World.Tick(world));
        fixed (int* p = tiles) count = Gi.World.ChangedTiles(world, grid, layer, 2, p);
        Assert.Equal(1, count);
        Assert.Equal(2, tiles[0]);
        Assert.True(Ascending(tiles, count));
        fixed (int* p = tiles) Assert.Equal(0, Gi.World.ChangedTiles(world, grid, layer, 3, p));
    }

    private static bool Ascending(int[] values, int count)
    {
        for (var i = 1; i < count; i++)
            if (values[i - 1] >= values[i])
                return false;
        return true;
    }

    [Fact]
    public unsafe void C14_QueryRegionIsRowMajorAndWritesZeroOutsideTheGrid()
    {
        var world = NewWorld();
        var grid = Gi.Grid.New(world, 7, 0f, 0f, 128f);
        var layer = Gi.Layer.New(world);
        var stamp = NewBox(5, 5, 70);
        Gi.World.Place(world, layer, 64.5f, 64.5f, stamp, 4);
        Gi.World.Process(world);

        const int width = 20;
        const int height = 15;
        var region = new short[width * height];
        fixed (short* p = region)
        {
            Gi.World.QueryRegion(world, grid, layer, -4, -3, width, height, p);
            for (var row = 0; row < height; row++)
            for (var column = 0; column < width; column++)
            {
                var cx = -4 + column;
                var cy = -3 + row;
                var expected = cx < 0 || cy < 0 || cx >= 128 || cy >= 128 ? (short)0 : Gi.World.Query(world, grid, layer, cx, cy);
                Assert.Equal(expected, region[row * width + column]);
            }

            var full = new short[128 * 128];
            fixed (short* q = full)
            {
                Gi.World.QueryRegion(world, grid, layer, 0, 0, 128, 128, q);
                for (var cy = 0; cy < 128; cy++)
                for (var cx = 0; cx < 128; cx++)
                    Assert.Equal(Gi.World.Query(world, grid, layer, cx, cy), full[cy * 128 + cx]);
            }
        }
    }

    [Fact]
    public void C15_AGridAddedAfterItsSourcesMissesThemUntilTheyChange()
    {
        var world = NewWorld();
        var coarse = Gi.Grid.New(world, 6, 0f, 0f, 64f);
        var layer = Gi.Layer.New(world);
        var stamp = NewBox(2, 2, 50);
        var id = Gi.World.Place(world, layer, 20f, 20f, stamp, 4);
        Gi.World.Process(world);
        Assert.True(Gi.World.Query(world, coarse, layer, 20, 20) > 0);

        var fine = Gi.Grid.New(world, 7, 0f, 0f, 64f);
        Gi.World.Process(world);
        Assert.Equal(0, Gi.World.Query(world, fine, layer, 40, 40));
        Assert.Equal(0L, Gi.World.Query(world, fine, layer, 0, 0, 128, 128));

        Gi.World.SetGain(world, id, 6);
        Gi.World.Process(world);
        Assert.True(Gi.World.Query(world, fine, layer, 40, 40) > 0);
        Assert.True(Gi.World.Query(world, coarse, layer, 20, 20) > 0);
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
            Gi.Grid.New(world, 6, 0f, 0f, 64f);
            Gi.Layer.New(world);
        }

        var inFirst = Gi.World.Place(first, 0, 20f, 20f, stamp, 4);
        var inSecond = Gi.World.Place(second, 0, 20f, 20f, stamp, 4);
        Assert.False(Gi.Stamp.Free(stamp));

        Gi.World.Free(first);
        _worlds.Remove(first);
        Assert.False(Gi.Stamp.Free(stamp));

        Gi.World.Free(second);
        _worlds.Remove(second);
        Assert.True(Gi.Stamp.Free(stamp));
        _stamps.Remove(stamp);

        var reissued = NewBox(3, 3, 40);
        var applied = Gi.World.Place(third, 0, 20f, 20f, reissued, 4);
        Gi.World.Process(third);
        Gi.World.Remove(third, applied);
        Assert.False(Gi.Stamp.Free(reissued));
        Gi.World.Process(third);
        Assert.True(Gi.Stamp.Free(reissued));
        _stamps.Remove(reissued);

        var neverAppliedStamp = NewBox(3, 3, 40);
        var fourth = NewWorld();
        Gi.Grid.New(fourth, 6, 0f, 0f, 64f);
        Gi.Layer.New(fourth);
        var neverApplied = Gi.World.Place(fourth, 0, 24f, 24f, neverAppliedStamp, 4);
        Gi.World.Remove(fourth, neverApplied);
        Assert.True(Gi.Stamp.Free(neverAppliedStamp));
        _stamps.Remove(neverAppliedStamp);
        Gi.World.Process(fourth);
    }

    [Fact]
    public void C17_BatchPlaceFadeExpireRemovesAfterExactlyTicksProcesses()
    {
        var world = NewWorld();
        var grid = Gi.Grid.New(world, 7, 0f, 0f, 128f);
        var layer = Gi.Layer.New(world);
        var stamp = NewBox(3, 3, 60);

        var commands = new Gi.World.SourceCommand[3];
        commands[0] = Gi.World.SourceCommand.Place(layer, 64f, 64f, stamp, 8);
        commands[1] = Gi.World.SourceCommand.Fade(Gi.World.SourceTarget.CreatedBy(0), 0, 5);
        commands[2] = Gi.World.SourceCommand.Expire(Gi.World.SourceTarget.CreatedBy(0), 5);
        var results = new Gi.World.SourceResult[3];
        Assert.Equal(Gi.World.BatchStatus.Applied, Gi.World.ApplyBatch(world, commands, results));
        var id = results[0].SourceId;

        for (var tick = 1; tick <= 4; tick++)
        {
            Gi.World.Process(world);
            Assert.True(Gi.World.TryGetSourceSpec(world, id, out var spec));
            var expected = (sbyte)(8 - (8 * tick + 2) / 5);
            Assert.Equal(expected, spec.Gain);
            Assert.True(Gi.World.Query(world, grid, layer, 64, 64) > 0);
        }

        Gi.World.Process(world);
        Assert.False(Gi.World.TryGetSourceSpec(world, id, out _));
        Assert.Equal(0, Gi.World.Query(world, grid, layer, 64, 64));
        Assert.Equal(5, Gi.World.Tick(world));
    }

    [Fact]
    public void C18_HandlesAndSourceIdsReuseWithFreshGenerations()
    {
        var first = NewWorld();
        var second = NewWorld();
        Gi.Grid.New(first, 6, 0f, 0f, 64f);
        Gi.Layer.New(first);
        var stamp = NewBox(3, 3, 40);

        var beforeFree = Gi.World.Capture(first);
        Gi.World.Free(first);
        _worlds.Remove(first);
        Assert.True(beforeFree.IsValid);
        Assert.Equal(Gi.World.BatchStatus.InvalidWorld, Gi.World.Process(beforeFree));
        Assert.False(Gi.World.Capture(first).IsValid);

        var recycled = Gi.World.New();
        _worlds.Add(recycled);
        Assert.Equal(first, recycled);
        Gi.Grid.New(recycled, 6, 0f, 0f, 64f);
        Gi.Layer.New(recycled);

        var a = Gi.World.Place(recycled, 0, 20f, 20f, stamp, 4);
        var b = Gi.World.Place(recycled, 0, 30f, 30f, stamp, 4);
        Gi.World.Remove(recycled, a);
        var c = Gi.World.Place(recycled, 0, 40f, 40f, stamp, 4);
        Assert.NotEqual(default(Gi.World.SourceId), a);
        Assert.Equal(a.Slot, c.Slot);
        Assert.True(c.Generation > a.Generation);
        Assert.Equal(Gi.World.WriteStatus.StaleSource, Gi.World.Move(recycled, a, 50f, 50f));

        var d = Gi.World.Place(recycled, 0, 10f, 10f, stamp, 4);
        Gi.World.Clear(recycled);
        var e = Gi.World.Place(recycled, 0, 12f, 12f, stamp, 4);
        Assert.True(e.Generation > d.Generation);
        Assert.Equal(Gi.World.WriteStatus.StaleSource, Gi.World.Move(recycled, d, 50f, 50f));
    }

    [Fact]
    public unsafe void C19_AdapterWarmPathAllocatesZeroBytesOverAThousandTicks()
    {
        var world = NewWorld();
        var grid = Gi.Grid.New(world, 8, 0f, 0f, 256f);
        var layer = Gi.Layer.New(world);
        var transient = Gi.Layer.New(world, Gi.Layer.Residency.Transient, Gi.Layer.Precision.Cell);
        var stamp = NewBox(3, 3, 40);
        var cone = NewCone(4, 30, 90);

        const int sources = 64;
        const int probes = 128;
        const int splats = 32;
        var commands = new Gi.World.SourceCommand[sources];
        var results = new Gi.World.SourceResult[sources];
        var ids = new Gi.World.SourceId[sources];
        for (var i = 0; i < sources; i++)
        {
            commands[i] = Gi.World.SourceCommand.Place(layer, 16f + i * 3.5f, 16f + (i * 7) % 200, i % 2 == 0 ? stamp : cone, 4);
        }

        Assert.Equal(Gi.World.BatchStatus.Applied, Gi.World.ApplyBatch(world, commands, results));
        for (var i = 0; i < sources; i++) ids[i] = results[i].SourceId;
        var token = Gi.World.Capture(world);
        Gi.World.Process(world);

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
        var syncIds = new Gi.World.SourceId[sources];
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
                commands[i] = Gi.World.SourceCommand.Update(
                    Gi.World.SourceTarget.Existing(ids[i]), x, y, (tick % 7) * 0.5f, 1f, (sbyte)((tick + i) % 9 - 4));
                syncIds[i] = ids[i];
                syncXs[i] = 16f + (i * 11 + tick * 2) % 220;
                syncYs[i] = 16f + (i * 3 + tick) % 220;
                headings[i] = (tick % 5) * 0.25f;
                scales[i] = 1f;
            }

            statuses += (int)Gi.World.ApplyBatch(token, commands, results);
            fixed (Gi.World.SourceId* si = syncIds)
            fixed (float* sx = syncXs, sy = syncYs, sh = headings, ss = scales)
            fixed (ulong* iv = invalid)
            {
                statuses += (int)Gi.World.Sync(token, si, sx, sy, sources);
                var ranges = stackalloc Gi.World.TransformRange[1];
                ranges[0] = new Gi.World.TransformRange
                {
                    Sources = si, Xs = sx, Ys = sy, Headings = sh, Scales = ss, Invalid = iv, Count = sources, InvalidWords = 1,
                };
                statuses += (int)Gi.World.SyncTransforms(token, new ReadOnlySpan<Gi.World.TransformRange>(ranges, 1));
            }

            for (var i = 0; i < splats; i++)
            {
                splatXs[i] = 8f + (i * 19 + tick * 7) % 240;
                splatYs[i] = 8f + (i * 23 + tick * 11) % 240;
                splatStamps[i] = stamp;
                splatGains[i] = (sbyte)(i % 5 - 2);
            }

            Gi.World.Splat(world, transient, splatXs, splatYs, splatStamps, splatGains);
            statuses += (int)Gi.World.Process(token);

            fixed (float* px = probeXs, py = probeYs, pr = reaches, pgx = gradientX, pgy = gradientY,
                   pbx = bestX, pby = bestY, pnx = nearX, pny = nearY)
            fixed (short* pv = values, pmv = maxValues, pnv = nearValues, pth = thresholds)
            fixed (long* pt = totals)
            fixed (ulong* pw = words, pf = found, pm = mask)
            fixed (int* tp = tiles)
            fixed (short* rg = region)
            {
                Gi.World.SenseColumn(world, layer, px, py, probes, pv, pw);
                Gi.World.SenseGradientColumn(world, layer, px, py, null, probes, pv, pgx, pgy, pw);
                Gi.World.SenseAreaColumn(world, layer, px, py, pr, null, probes, pt, pw);
                Gi.World.SenseMaxColumn(world, layer, px, py, pr, probes, pmv, pbx, pby, pw);
                Gi.World.SenseNearestColumn(world, layer, px, py, pr, pth, probes, pnv, pnx, pny, pf);
                Gi.World.ChangedMask(world, layer, px, py, 4f, Gi.World.Tick(world) - 1, probes, pm);
                Gi.World.ChangedTiles(world, grid, layer, Gi.World.Tick(world) - 1, tp);
                Gi.World.QueryRegion(world, grid, layer, 32, 32, 32, 32, rg);
                for (var i = 0; i < probes; i++)
                {
                    sink += Gi.World.TrySense(world, layer, px[i], py[i], out pv[i]) ? 1 : 0;
                    sink += Gi.World.QueryAt(world, grid, layer, px[i], py[i]);
                    sink += Gi.World.Covers(world, px[i], py[i]) ? 1 : 0;
                }

                for (var i = 0; i < 8; i++)
                    if (Gi.World.TryGetSourceSpec(world, ids[i], out var spec))
                        sink += spec.Gain;
            }

            sink += Gi.World.Tick(world) + Gi.World.Revision(world);
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
    public unsafe void C20_ExceptionsEscapeOnlyAtConstruction()
    {
        var world = NewWorld();
        var grid = Gi.Grid.New(world, 6, 0f, 0f, 64f);
        var layer = Gi.Layer.New(world);
        var transient = Gi.Layer.New(world, Gi.Layer.Residency.Transient, Gi.Layer.Precision.Cell);
        var stamp = NewBox(3, 3, 40);
        var id = Gi.World.Place(world, layer, 20f, 20f, stamp, 4);
        Gi.World.Process(world);
        var token = Gi.World.Capture(world);
        Exception? thrown = null;
        try
        {
            Assert.Equal((short)0, Gi.World.Query(world, 200, layer, 1, 1));
            Assert.Equal((short)0, Gi.World.Query(world, grid, 200, 1, 1));
            Assert.Equal(0L, Gi.World.Query(world, grid, layer, 1, 1, -2, 3));
            Assert.Equal((short)0, Gi.World.QueryAt(world, grid, layer, float.NaN, float.PositiveInfinity));
            Assert.Equal((short)0, Gi.World.QueryMax(world, 200, layer, out _, out _));
            Gi.World.QueryGradient(world, grid, layer, float.NaN, 0f, out var gx, out var gy);
            Assert.Equal(0, gx);
            Assert.Equal(0, gy);
            fixed (short* region = new short[4])
            {
                Gi.World.QueryRegion(world, grid, layer, -10, -10, 2, 2, region);
                Gi.World.QueryRegion(200, grid, layer, 0, 0, 2, 2, region);
            }

            Assert.Equal(default(Gi.World.SourceId), Gi.World.Place(world, 33, 1f, 1f, stamp, 1, out var invalidLayer));
            Assert.Equal(Gi.World.WriteStatus.InvalidLayer, invalidLayer);
            Assert.Equal(default(Gi.World.SourceId), Gi.World.Place(world, layer, float.NaN, 1f, stamp, 1, out var nonFinite));
            Assert.Equal(Gi.World.WriteStatus.NonFinite, nonFinite);
            Assert.Equal(default(Gi.World.SourceId), Gi.World.Place(world, layer, 1f, 1f, 255, 1, out var invalidStamp));
            Assert.Equal(Gi.World.WriteStatus.InvalidStamp, invalidStamp);
            Assert.Equal(Gi.World.WriteStatus.StaleSource, Gi.World.Move(world, default, 1f, 1f));
            Assert.Equal(Gi.World.WriteStatus.NonFinite, Gi.World.Move(world, id, float.NaN, 1f));
            Assert.Equal(Gi.World.WriteStatus.StaleSource, Gi.World.SetGain(world, default, 1));
            Assert.Equal(Gi.World.WriteStatus.NonFinite, Gi.World.Turn(world, id, float.PositiveInfinity));
            Assert.Equal(Gi.World.WriteStatus.NonFinite, Gi.World.Scale(world, id, float.NegativeInfinity));
            Assert.Equal(Gi.World.WriteStatus.StaleSource, Gi.World.Remove(world, default));
            Assert.Equal(Gi.World.WriteStatus.StaleSource, Gi.World.Fade(world, default, 1, 1));
            Assert.Equal(Gi.World.WriteStatus.StaleSource, Gi.World.Expire(world, default, 1));
            Assert.Equal(0, Gi.World.Tick(200));
            Assert.Equal(0, Gi.World.Revision(200));
            Gi.World.Process(200);
            Gi.World.ProcessAll();
            Gi.World.Clear(200);
            Gi.World.Record(200);
            Gi.World.Rewind(200);
            Gi.World.StopRecording(200);
            Assert.False(Gi.World.Capture(200).IsValid);
            Assert.Equal(Gi.World.BatchStatus.InvalidWorld, Gi.World.ApplyBatch(200, [], []));
            Assert.Equal(Gi.World.BatchStatus.Applied, Gi.World.ApplyBatch(token, [], []));
            var shortResults = new Gi.World.SourceResult[0];
            var one = new[] { Gi.World.SourceCommand.SetGain(Gi.World.SourceTarget.Existing(id), 4) };
            Assert.Equal(Gi.World.BatchStatus.InsufficientResults, Gi.World.ApplyBatch(token, one, shortResults));
            Gi.World.Sync(200, null, null, null, 0);
            var badRange = new Gi.World.TransformRange { Count = -1 };
            Assert.Equal(Gi.World.BatchStatus.InvalidRange,
                Gi.World.SyncTransforms(token, new[] { badRange }));
            Assert.Equal(Gi.World.ReadStatus.InvalidLayer, Gi.World.SenseColumn(world, 33, null, null, null, 0, null, null));
            Assert.Equal(0, Gi.World.ChangedMask(world, layer, null, null, float.NaN, 0, 0, null));
            fixed (int* tiles = new int[1])
                Assert.Equal(0, Gi.World.ChangedTiles(world, grid, 33, 0, tiles));
            Assert.False(Gi.World.Covers(world, float.NaN, 0f));
            Assert.False(Gi.World.TrySense(200, layer, 1f, 1f, out _));
            Assert.False(Gi.World.TrySenseNearest(world, layer, float.NaN, 1f, 1f, 1, out _, out _, out _));
            Assert.Equal(0, Gi.World.Splat(world, layer, new[] { 1f }, new[] { 1f }, stamp, 1));
            Assert.Equal(0, Gi.World.Splat(200, transient, new[] { 1f }, new[] { 1f }, stamp, 1));
            Assert.False(Gi.Stamp.TryGetSpec(0, out _));
            Assert.False(Gi.World.TryGetSourceSpec(world, default, out _));
            Assert.False(Gi.Layer.TryGetPrecision(world, 33, out _));
            Assert.False(Gi.Grid.TryGetSpec(world, 33, out _, out _, out _, out _));
            Gi.World.CopyWorldIds(new byte[4]);
        }
        catch (Exception exception)
        {
            thrown = exception;
        }

        Assert.Null(thrown);

        Assert.Throws<ArgumentOutOfRangeException>(() => Gi.Grid.New(world, 4, 0f, 0f, 64f));
        Assert.Throws<ArgumentOutOfRangeException>(() => Gi.Grid.New(world, 15, 0f, 0f, 64f));
        Assert.Throws<ArgumentOutOfRangeException>(() => Gi.Grid.New(world, 6, float.NaN, 0f, 64f));
        Assert.Throws<ArgumentOutOfRangeException>(() => Gi.Grid.New(world, 6, 0f, 0f, 0f));
        Assert.Throws<ArgumentOutOfRangeException>(() => Gi.Grid.New(world, 5, 0f, 0f, 0.001f));
        Assert.Throws<ArgumentOutOfRangeException>(() => Gi.Stamp.Box(0, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Gi.Stamp.Box(257, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Gi.Stamp.Disk(0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Gi.Stamp.Disk(129, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Gi.Stamp.Cone(4, 1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Gi.Stamp.Cone(4, 1, 361));
        Assert.Throws<ArgumentException>(() => Gi.Stamp.New(new sbyte[3], 2, 2));

        Span<byte> live = stackalloc byte[256];
        var liveCount = Gi.World.CopyWorldIds(live);
        var created = new List<byte>();
        try
        {
            while (liveCount + created.Count < 255) created.Add(Gi.World.New());
            Assert.Throws<InvalidOperationException>(() => Gi.World.New());

            var full = created[0];
            for (var i = 0; i < 32; i++) Gi.Grid.New(full, 5, 0f, 0f, 1f);
            var gridCount = Gi.Grid.Count(full);
            Assert.Throws<InvalidOperationException>(() => Gi.Grid.New(full, 5, 0f, 0f, 1f));
            Assert.Equal(gridCount, Gi.Grid.Count(full));

            for (var i = 0; i < 32; i++) Gi.Layer.New(full);
            var layerCount = Gi.Layer.Count(full);
            Assert.Throws<InvalidOperationException>(() => Gi.Layer.New(full));
            Assert.Equal(layerCount, Gi.Layer.Count(full));
        }
        finally
        {
            created.Reverse();
            foreach (var created2 in created) Gi.World.Free(created2);
        }
    }

    [Fact]
    public void C21_BatchPlaceHeadingMatchesTheScalarTurnConvention()
    {
        var turned = NewWorld();
        var batched = NewWorld();
        var grid = Gi.Grid.New(turned, 7, 0f, 0f, 128f);
        Gi.Grid.New(batched, 7, 0f, 0f, 128f);
        var layerTurned = Gi.Layer.New(turned);
        var layerBatched = Gi.Layer.New(batched);
        var cone = NewCone(12, 80, 60);

        var commands = new Gi.World.SourceCommand[1];
        commands[0] = Gi.World.SourceCommand.Place(layerBatched, 64.5f, 64.5f, cone, 4, MathF.PI / 2f);
        var results = new Gi.World.SourceResult[1];
        Assert.Equal(Gi.World.BatchStatus.Applied, Gi.World.ApplyBatch(batched, commands, results));

        var id = Gi.World.Place(turned, layerTurned, 64.5f, 64.5f, cone, 4);
        Gi.World.Turn(turned, id, MathF.PI / 2f);

        Gi.World.Process(turned);
        Gi.World.Process(batched);
        Assert.Equal(GiFieldHash.OfLayer(turned, layerTurned), GiFieldHash.OfLayer(batched, layerBatched));

        commands[0] = Gi.World.SourceCommand.Place(layerBatched, 30.5f, 30.5f, cone, 4);
        Assert.Equal(Gi.World.BatchStatus.Applied, Gi.World.ApplyBatch(batched, commands, results));
        Gi.World.Process(batched);

        Assert.True(Gi.World.Query(batched, grid, layerBatched, 64, 70) > 0);
        Assert.Equal(0, Gi.World.Query(batched, grid, layerBatched, 64, 58));
        Assert.Equal(0, Gi.World.Query(batched, grid, layerBatched, 70, 64));

        Assert.True(Gi.World.Query(batched, grid, layerBatched, 36, 30) > 0);
        Assert.Equal(0, Gi.World.Query(batched, grid, layerBatched, 24, 30));
        Assert.Equal(0, Gi.World.Query(batched, grid, layerBatched, 30, 36));
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
