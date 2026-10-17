using Xunit;
using static Gi.World;

namespace Gi.Tests;

public sealed unsafe class DepositRecordTests : IDisposable
{
    private const int Layers = 3;
    private const int SeedCount = 120;
    private readonly byte _world = World.New();
    private readonly byte _stamp = Stamp.Box(1, 1, 3);

    public DepositRecordTests()
    {
        Grid.New(_world, 8, 0f, 0f, 256f);
        for (var layer = 0; layer < Layers; layer++) Assert.Equal(layer, Layer.New(_world));
    }

    public void Dispose()
    {
        World.Free(_world);
        Assert.True(Stamp.Free(_stamp));
    }

    public static IEnumerable<object[]> Seeds() => Enumerable.Range(1, SeedCount).Select(seed => new object[] { seed });

    private int[] Recount()
    {
        var counts = new int[Layers];
        var s = &World.GetContext(_world)->Sources;
        for (var i = 0; i < s->Count; i++)
            if (s->Alive[i] != 0 && s->Gain[i] != 0) counts[s->Layer[i]]++;
        return counts;
    }

    private int[] Maintained()
    {
        var counts = new int[Layers];
        var w = World.GetContext(_world);
        var s = &w->Sources;
        var records = w->Grids->Records.Pointer;
        if (records == null) return counts;
        for (var i = 0; i < s->Count; i++)
            if (records[i].Alive != 0 && records[i].Gain != 0) counts[records[i].Layer]++;
        return counts;
    }

    private static bool TargetEquals(SourceColumns* s, WorldCtx* w, DepositRecord* r, int slot)
    {
        if (s->Alive[slot] == 0)
            return r->Alive == 0 && r->Gain == 0 && r->Stamp == 0 && r->Layer == 0 &&
                r->Angle == 0 && r->Scale == 0 && r->Ax == 0 && r->Ay == 0;

        var layer = s->Layer[slot];
        if (r->Alive == 0 || r->Layer != layer || r->Stamp != s->Stamp[slot] ||
            r->Gain != (sbyte)s->Gain[slot] ||
            r->Angle != (ushort)(s->Angle[slot] & w->Recipes[layer].TurnMask) ||
            r->Scale != s->Scale[slot])
            return false;

        var g = w->Grids;
        var mask = w->Recipes[layer].AnchorMask;
        return r->Ax == TileBake.AnchorQ8(s->X[slot], g->OriginX, g->ScaleQ8, mask) &&
            r->Ay == TileBake.AnchorQ8(s->Y[slot], g->OriginY, g->ScaleQ8, mask);
    }

    private void AssertInvariant(string step)
    {
        var w = World.GetContext(_world);
        var s = &w->Sources;
        var records = w->Grids->Records.Pointer;
        var marked = new HashSet<int>();
        for (var i = 0; i < w->Changed.Length; i++)
        {
            var slot = w->Changed.Pointer[i];
            Assert.True(s->Mark[slot] == w->MarkEpoch, $"{step}: changed slot {slot} unmarked");
            Assert.True(marked.Add(slot), $"{step}: changed slot {slot} duplicated");
        }

        if (records == null) return;
        for (var i = 0; i < s->Count; i++)
            if (s->Mark[i] != w->MarkEpoch)
                Assert.True(TargetEquals(s, w, records + i, i), $"{step}: unswept slot {i} record diverged");
    }

    private void AssertExact(string step)
    {
        AssertInvariant(step);
        Assert.True(
            Recount().AsSpan().SequenceEqual(Maintained()),
            $"{step}: maintained [{string.Join(",", Maintained())}] recount [{string.Join(",", Recount())}]");
    }

    private World.SourceId PlaceAt(byte layer, sbyte gain, float x = 10.5f, float y = 10.5f)
        => World.Place(_world, layer, x, y, _stamp, gain);

    [Fact]
    public void ImmediateFade_KeepsTheRecordExact()
    {
        var id = PlaceAt(0, 5);
        AssertInvariant("place");
        World.Fade(_world, id, 0, 0);
        AssertInvariant("fade to zero");
        World.Fade(_world, id, 4, -1);
        AssertInvariant("fade from zero");
        World.Fade(_world, id, 4, 3);
        AssertInvariant("fade to the current gain");
        World.Fade(_world, id, -2, 0);
        AssertInvariant("fade across zero");
        World.Process(_world);
        AssertExact("process");
        Assert.Equal([1, 0, 0], Maintained());
    }

    [Fact]
    public void FadeSteps_CountTheSourceUntilItsGainReachesZero()
    {
        var id = PlaceAt(1, 4);
        World.Process(_world);
        World.Fade(_world, id, 0, 4);
        var seen = new List<int>();
        for (var tick = 0; tick < 6; tick++)
        {
            World.Process(_world);
            AssertExact($"fade tick {tick}");
            seen.Add(Maintained()[1]);
        }

        Assert.Equal([1, 1, 1, 0, 0, 0], seen);
        World.Fade(_world, id, 3, 3);
        for (var tick = 0; tick < 4; tick++)
        {
            World.Process(_world);
            AssertExact($"fade up tick {tick}");
        }

        Assert.Equal([0, 1, 0], Maintained());
    }

    [Fact]
    public void Expiry_UncountsTheSourceOnItsTickAndKeepsZeroGainSourcesOut()
    {
        var live = PlaceAt(0, 2);
        var silent = PlaceAt(0, 0);
        var other = PlaceAt(2, -3);
        World.Expire(_world, live, 2);
        World.Expire(_world, silent, 1);
        World.Expire(_world, other, 3);
        AssertInvariant("scheduled");
        Assert.Equal([1, 0, 1], Recount());
        for (var tick = 0; tick < 4; tick++)
        {
            World.Process(_world);
            AssertExact($"expiry tick {tick}");
        }

        Assert.Equal([0, 0, 0], Maintained());
    }

    [Fact]
    public void ExpiryOfAFadingSource_UncountsItOnce()
    {
        var id = PlaceAt(0, 6);
        World.Fade(_world, id, 0, 6);
        World.Expire(_world, id, 3);
        for (var tick = 0; tick < 5; tick++)
        {
            World.Process(_world);
            AssertExact($"tick {tick}");
        }

        Assert.Equal([0, 0, 0], Maintained());
    }

    [Fact]
    public void BatchFold_KeepsTheRecordExactAcrossPlaceRemoveFadeAndGain()
    {
        var first = PlaceAt(0, 3);
        var second = PlaceAt(1, 0);
        var third = PlaceAt(2, -2);
        World.Process(_world);
        SourceCommand[] commands =
        [
            SourceCommand.Place(1, 20.5f, 20.5f, _stamp, 4),
            SourceCommand.Place(2, 30.5f, 30.5f, _stamp, 0),
            SourceCommand.SetGain(SourceTarget.CreatedBy(1), 5),
            SourceCommand.Fade(first, 0, 0),
            SourceCommand.SetGain(second, 7),
            SourceCommand.Remove(third),
            SourceCommand.Fade(SourceTarget.CreatedBy(0), 0, 0),
            SourceCommand.Fade(SourceTarget.CreatedBy(0), 2, 3),
            SourceCommand.Expire(second, 1),
        ];
        var results = new SourceResult[commands.Length];
        Assert.Equal(BatchStatus.Applied, World.ApplyBatch(_world, commands, results));
        AssertInvariant("batch");
        World.Process(_world);
        AssertExact("process");
        World.Process(_world);
        AssertExact("expiry");
        Assert.Equal([0, 1, 1], Maintained());
    }

    [Fact]
    public void BatchAboveTheColumnThreshold_KeepsTheRecordExact()
    {
        const int count = 8192 + 1500;
        var create = new SourceCommand[count];
        for (var i = 0; i < count; i++)
        {
            var row = i / 250;
            create[i] = SourceCommand.Place((byte)(i % Layers), 1f + i % 250, 1f + row % 250, _stamp, (sbyte)(i % 5 - 2));
        }
        var created = new SourceResult[count];
        Assert.Equal(BatchStatus.Applied, World.ApplyBatch(_world, create, created));
        AssertInvariant("create");

        var update = new SourceCommand[count];
        for (var i = 0; i < count; i++)
        {
            var target = created[i].SourceId;
            var row = i / 200;
            update[i] = (i % 4) switch
            {
                0 => SourceCommand.SetGain(target, (sbyte)(i % 3)),
                1 => SourceCommand.Remove(target),
                2 => SourceCommand.Fade(target, (sbyte)(i % 7 - 3), 0),
                _ => SourceCommand.Update(target, 2f + i % 200, 2f + row % 200, 0f, 1f, (sbyte)(i % 4 - 1)),
            };
        }

        var updated = new SourceResult[count];
        Assert.Equal(BatchStatus.Applied, World.ApplyBatch(_world, update, updated));
        AssertInvariant("update");
        World.Process(_world);
        AssertExact("process");
    }

    [Fact]
    public void Rewind_RepublishesOnceAndMatchesTheStateBeforeRecording()
    {
        var a = PlaceAt(0, 3);
        var b = PlaceAt(1, 2);
        var c = PlaceAt(2, 0);
        World.Process(_world);
        var before = Recount();
        World.Record(_world);
        World.SetGain(_world, a, 0);
        World.Remove(_world, b);
        World.Fade(_world, c, 5, 0);
        PlaceAt(0, 1);
        World.Process(_world);
        AssertExact("recorded");
        World.Rewind(_world);
        AssertInvariant("rewound");
        Assert.Equal(before, Recount());
        World.Process(_world);
        AssertExact("process after rewind");
        Assert.Equal(before, Maintained());
    }

    [Fact]
    public void RewindWithoutProcess_RestoresPendingMutationsExactly()
    {
        var a = PlaceAt(0, 3);
        World.Process(_world);
        World.Record(_world);
        World.SetGain(_world, a, 0);
        PlaceAt(1, 4);
        World.Rewind(_world);
        AssertInvariant("rewound");
        Assert.Equal([1, 0, 0], Recount());
        World.Process(_world);
        AssertExact("process after rewind");
    }

    [Fact]
    public void Clear_ResetsTheRecord()
    {
        PlaceAt(0, 3);
        PlaceAt(2, 1);
        World.Clear(_world);
        AssertExact("cleared");
        Assert.Equal([0, 0, 0], Maintained());
    }

    [Fact]
    public void Process_LeavesUnchangedSlotsOutOfTheSweep()
    {
        const int count = 200;
        var ids = new World.SourceId[count];
        for (var i = 0; i < count; i++)
        {
            ids[i] = PlaceAt((byte)(i % Layers), 2, 1f + i, 1f + i);
            World.Expire(_world, ids[i], 1000);
        }

        World.Process(_world);
        var w = World.GetContext(_world);
        var records = w->Grids->Records.Pointer;
        var snapshot = new DepositRecord[w->Sources.Count];
        for (var i = 0; i < snapshot.Length; i++) snapshot[i] = records[i];

        World.Process(_world);
        Assert.Equal(0, w->Changed.Length);
        for (var i = 0; i < snapshot.Length; i++)
            Assert.Equal(snapshot[i], records[i]);

        World.Move(_world, ids[0], 100.5f, 100.5f);
        Assert.Equal(1, w->Changed.Length);
        World.Process(_world);
        Assert.Equal(0, w->Changed.Length);
        Assert.NotEqual(snapshot[0], records[0]);
        for (var i = 1; i < snapshot.Length; i++)
            Assert.Equal(snapshot[i], records[i]);
        World.Process(_world);
        Assert.Equal(0, World.ChangedTiles(_world, 0, 0, null));
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void RandomStreams_KeepTheRecordEqualToABruteForceRecount(int seed)
    {
        var random = new Random(seed);
        var ids = new List<World.SourceId>();
        var recording = false;
        sbyte Gain() => (sbyte)random.Next(-3, 4);
        World.SourceId Target() => ids.Count == 0 ? default : ids[random.Next(ids.Count)];
        float Coordinate() => 2f + random.Next(240);

        for (var step = 0; step < 600; step++)
        {
            var action = random.Next(100);
            var processed = false;
            if (action < 14) ids.Add(PlaceAt((byte)random.Next(Layers), Gain(), Coordinate(), Coordinate()));
            else if (action < 20) World.Move(_world, Target(), Coordinate(), Coordinate());
            else if (action < 30) World.SetGain(_world, Target(), Gain());
            else if (action < 40) World.Fade(_world, Target(), Gain(), random.Next(-1, 7));
            else if (action < 47) World.Expire(_world, Target(), random.Next(0, 5));
            else if (action < 54) World.Remove(_world, Target());
            else if (action < 57) World.Turn(_world, Target(), random.Next(7));
            else if (action < 60) World.Scale(_world, Target(), 0.5f + random.Next(4));
            else if (action < 76)
            {
                World.Process(_world);
                processed = true;
            }
            else if (action < 90) ids.AddRange(RandomBatch(random, ids));
            else if (action < 94)
            {
                if (!recording) World.Record(_world);
                recording = true;
            }
            else if (action < 98)
            {
                World.Rewind(_world);
                recording = false;
            }
            else if (action < 99)
            {
                World.StopRecording(_world);
                recording = false;
            }
            else
            {
                World.Clear(_world);
                recording = false;
                processed = true;
            }

            if (processed) AssertExact($"seed {seed} step {step} action {action}");
            else AssertInvariant($"seed {seed} step {step} action {action}");
        }
    }

    private List<World.SourceId> RandomBatch(Random random, List<World.SourceId> ids)
    {
        var created = new List<World.SourceId>();
        var count = random.Next(1, 9);
        var commands = new SourceCommand[count];
        var places = new List<int>();
        for (var i = 0; i < count; i++)
        {
            SourceTarget target;
            var useCreated = places.Count > 0 && random.Next(3) == 0;
            if (useCreated) target = SourceTarget.CreatedBy(places[random.Next(places.Count)]);
            else target = ids.Count == 0 ? default(World.SourceId) : ids[random.Next(ids.Count)];
            var gain = (sbyte)random.Next(-3, 4);
            var x = 2f + random.Next(240);
            var y = 2f + random.Next(240);
            switch (random.Next(7))
            {
                case 0:
                    places.Add(i);
                    commands[i] = SourceCommand.Place((byte)random.Next(Layers), x, y, _stamp, gain);
                    break;
                case 1: commands[i] = SourceCommand.SetGain(target, gain); break;
                case 2: commands[i] = SourceCommand.Update(target, x, y, 0f, 1f, gain); break;
                case 3: commands[i] = SourceCommand.Remove(target); break;
                case 4: commands[i] = SourceCommand.Fade(target, gain, random.Next(-1, 6)); break;
                case 5: commands[i] = SourceCommand.Expire(target, random.Next(0, 4)); break;
                default: commands[i] = SourceCommand.UpdateTransform(target, x, y, 0.5f, 1f); break;
            }
        }

        var results = new SourceResult[count];
        Assert.Equal(BatchStatus.Applied, World.ApplyBatch(_world, commands, results));
        for (var i = 0; i < count; i++)
            if (commands[i].Kind == SourceCommandKind.Place && results[i].Status == SourceResultStatus.Applied)
                created.Add(results[i].SourceId);
        return created;
    }
}
