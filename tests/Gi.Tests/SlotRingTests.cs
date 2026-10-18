using Xunit;

namespace Gi.Tests;

public sealed class SlotRingTests : IDisposable
{
    private readonly byte _world = World.New();
    private readonly byte _mirror = World.New();
    private readonly byte _layer;
    private readonly byte _stamp = Stamp.Box(2, 2, 3);

    public SlotRingTests()
    {
        foreach (var world in new[] { _world, _mirror })
        {
            Grid.New(world, 6, 0f, 0f, 64f);
            Assert.Equal(0, Layer.New(world));
        }

        _layer = 0;
    }

    public void Dispose()
    {
        World.Free(_world);
        World.Free(_mirror);
        Assert.True(Stamp.Free(_stamp));
    }

    private static uint SlotOf(World.SourceId id) => id.Slot;

    private World.SourceId Place(byte world) => World.Place(world, _layer, 10f, 10f, _stamp, 1);

    private World.SourceId Place() => Place(_world);

    private World.SourceId[] PlaceMany(int count)
    {
        var ids = new World.SourceId[count];
        for (var i = 0; i < count; i++) ids[i] = Place();
        return ids;
    }

    [Fact]
    public void FreedSlots_AreReusedInTheOrderTheyWereFreed()
    {
        var ids = PlaceMany(8);
        World.Remove(_world, ids[3]);
        World.Remove(_world, ids[1]);
        World.Remove(_world, ids[5]);

        var first = SlotOf(Place());
        var second = SlotOf(Place());
        var third = SlotOf(Place());
        var fresh = SlotOf(Place());

        Assert.Equal([3u, 1u, 5u, 8u], [first, second, third, fresh]);
    }

    [Fact]
    public void RemoveThenPlace_DoesNotHandBackTheSlotJustFreed_WhileOtherSlotsAreFree()
    {
        var ids = PlaceMany(4);
        World.Remove(_world, ids[0]);
        World.Remove(_world, ids[1]);
        World.Remove(_world, ids[2]);

        var reused = Place();

        Assert.Equal(0u, SlotOf(reused));
        Assert.NotEqual(ids[0], reused);
        Assert.False(World.TryGetSourceSpec(_world, ids[2], out _));
    }

    [Fact]
    public void RemoveThenPlace_ReusesTheOnlyFreeSlot()
    {
        var ids = PlaceMany(3);
        World.Remove(_world, ids[1]);

        var reused = Place();

        Assert.Equal(1u, SlotOf(reused));
        Assert.NotEqual(ids[1], reused);
        Assert.False(World.TryGetSourceSpec(_world, ids[1], out _));
        Assert.True(World.TryGetSourceSpec(_world, reused, out _));
    }

    [Fact]
    public void ExpiredSlots_JoinTheRingInExpiryOrder()
    {
        var a = Place();
        var b = Place();
        var c = Place();
        var survivor = Place();
        World.Process(_world);
        World.Expire(_world, c, 1);
        World.Expire(_world, a, 2);
        World.Expire(_world, b, 1);

        World.Process(_world);
        Assert.False(World.TryGetSourceSpec(_world, b, out _));
        Assert.False(World.TryGetSourceSpec(_world, c, out _));
        Assert.True(World.TryGetSourceSpec(_world, a, out _));
        World.Process(_world);
        Assert.False(World.TryGetSourceSpec(_world, a, out _));
        Assert.True(World.TryGetSourceSpec(_world, survivor, out _));

        var first = SlotOf(Place());
        var second = SlotOf(Place());
        var third = SlotOf(Place());
        var fresh = SlotOf(Place());

        Assert.Equal([SlotOf(b), SlotOf(c), SlotOf(a), 4u], [first, second, third, fresh]);
    }

    [Fact]
    public void Rewind_RebuildsTheRingInAscendingSlotOrder()
    {
        var ids = PlaceMany(6);
        World.Process(_world);
        World.Remove(_world, ids[4]);
        World.Remove(_world, ids[2]);
        World.Remove(_world, ids[5]);
        World.Process(_world);

        World.Record(_world);
        Assert.Equal(4u, SlotOf(Place()));
        Assert.Equal(2u, SlotOf(Place()));
        World.Remove(_world, ids[0]);
        World.Process(_world);
        World.Rewind(_world);
        World.Process(_world);

        var first = SlotOf(Place());
        var second = SlotOf(Place());
        var third = SlotOf(Place());
        var fresh = SlotOf(Place());

        Assert.Equal([2u, 4u, 5u, 6u], [first, second, third, fresh]);
        Assert.True(World.TryGetSourceSpec(_world, ids[0], out _));
    }

    [Fact]
    public void Clear_EmptiesTheRingAndKeepsPreClearIdsStale()
    {
        var ids = PlaceMany(5);
        World.Remove(_world, ids[1]);
        World.Remove(_world, ids[3]);

        World.Clear(_world);

        var first = Place();
        var second = Place();
        var third = Place();
        Assert.Equal([0u, 1u, 2u], [SlotOf(first), SlotOf(second), SlotOf(third)]);
        foreach (var id in ids) Assert.False(World.TryGetSourceSpec(_world, id, out _));
        Assert.True(World.TryGetSourceSpec(_world, first, out _));
    }

    [Fact]
    public void Batch_PopsAndPushesFollowTheSameRingOrderAsSerialCalls_AcrossGrowthOfAWrappedRing()
    {
        var setupOne = Setup(_world);
        var setupTwo = Setup(_mirror);
        Assert.Equal(setupOne, setupTwo);

        var live = setupOne;
        var commands = new List<World.SourceCommand>();
        for (var i = 0; i < 100; i++)
        {
            commands.Add(World.SourceCommand.Place(_layer, 10f, 10f, _stamp, 1));
            if (i % 10 == 0 && i / 10 < live.Length)
                commands.Add(World.SourceCommand.Remove(live[i / 10]));
        }

        var results = new World.SourceResult[commands.Count];
        Assert.Equal(World.BatchStatus.Applied, World.ApplyBatch(_world, commands.ToArray(), results));
        for (var i = 0; i < commands.Count; i++)
        {
            var command = commands[i];
            var expected = command.Kind == SourceCommandKind.Place
                ? Place(_mirror)
                : Remove(_mirror, command.Target.Source);
            Assert.Equal(expected, results[i].SourceId);
        }

        for (var i = 0; i < 80; i++) Assert.Equal(Place(_mirror), Place(_world));
        World.Process(_world);
        World.Process(_mirror);
        Assert.Equal(World.Query(_mirror, 0, _layer, 0, 0, 64, 64), World.Query(_world, 0, _layer, 0, 0, 64, 64));

        World.SourceId[] Setup(byte world)
        {
            var all = new World.SourceId[64];
            for (var i = 0; i < all.Length; i++) all[i] = Place(world);
            foreach (var id in all) World.Remove(world, id);
            var replaced = new World.SourceId[30];
            for (var i = 0; i < replaced.Length; i++) replaced[i] = Place(world);
            for (var i = 0; i < 20; i++) World.Remove(world, replaced[i]);
            return replaced[20..];
        }

        World.SourceId Remove(byte world, World.SourceId id)
        {
            World.Remove(world, id);
            return id;
        }
    }

    [Fact]
    public void RingChurn_AllocatesNothingWhenWarm()
    {
        var live = new Queue<World.SourceId>(PlaceMany(300));
        for (var i = 0; i < 150; i++) World.Remove(_world, live.Dequeue());
        for (var i = 0; i < 400; i++)
        {
            World.Remove(_world, live.Dequeue());
            live.Enqueue(Place());
            live.Enqueue(Place());
            World.Process(_world);
        }

        var ids = live.ToArray();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var cursor = 0;
        for (var i = 0; i < 1000; i++)
        {
            World.Remove(_world, ids[cursor]);
            ids[cursor] = Place();
            cursor = (cursor + 1) % ids.Length;
            World.Process(_world);
        }

        Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void ShortLivedSpawns_NeverReissueAnIdWhileAThousandSlotsAreFree()
    {
        const int permanent = 1000;
        const int free = 1000;
        const int frames = 127 * free;
        var issued = new HashSet<World.SourceId>();
        var live = SeedPermanentSources(permanent, free, issued);
        var retired = new List<World.SourceId>(frames);
        var previous = default(World.SourceId);
        for (var frame = 0; frame < frames; frame++)
        {
            var spawned = Place();
            if (!issued.Add(spawned)) Assert.Fail($"id {spawned} reissued at frame {frame}");
            if (previous != default)
            {
                Assert.NotEqual(SlotOf(previous), SlotOf(spawned));
                World.Remove(_world, previous);
                retired.Add(previous);
            }

            previous = spawned;
            World.Process(_world);
        }

        foreach (var id in retired) Assert.False(World.TryGetSourceSpec(_world, id, out _));
        foreach (var id in live) Assert.True(World.TryGetSourceSpec(_world, id, out _));
        Assert.True(World.TryGetSourceSpec(_world, previous, out _));
    }

    [Fact]
    public void StaleIds_StayInertAgainstReusedSlots_OverTenThousandShortLivedSpawns()
    {
        const int permanent = 1000;
        const int lag = 256;
        var live = SeedPermanentSources(permanent, 1000, new HashSet<World.SourceId>());
        var retired = new List<World.SourceId>();
        var previous = default(World.SourceId);
        for (var frame = 0; frame < 10000; frame++)
        {
            var spawned = Place();
            if (previous != default)
            {
                World.Remove(_world, previous);
                retired.Add(previous);
            }

            previous = spawned;
            if (retired.Count > lag)
            {
                var stale = retired[^lag];
                World.Move(_world, stale, 50f, 50f);
                World.SetGain(_world, stale, 16);
                World.Fade(_world, stale, 0, 2);
                World.Expire(_world, stale, 1);
                World.Remove(_world, stale);
            }

            World.Process(_world);
        }

        Assert.Equal((permanent + 1) * 4 * 3, World.Query(_world, 0, _layer, 0, 0, 64, 64));
        Assert.Equal(0, World.Query(_world, 0, _layer, 48, 48, 6, 6));
        foreach (var id in retired) Assert.False(World.TryGetSourceSpec(_world, id, out _));
        foreach (var id in live) Assert.True(World.TryGetSourceSpec(_world, id, out _));
        Assert.True(World.TryGetSourceSpec(_world, previous, out _));
    }

    private World.SourceId[] SeedPermanentSources(int permanent, int free, HashSet<World.SourceId> issued)
    {
        var all = PlaceMany(permanent + free);
        foreach (var id in all) Assert.True(issued.Add(id));
        for (var i = 0; i < free; i++) World.Remove(_world, all[i]);
        World.Process(_world);
        return all[free..];
    }

    [Fact]
    public void ASlotReusedOneHundredTwentyEightTimes_NeverReissuesAnId()
    {
        var ids = new List<World.SourceId> { Place() };
        for (var reuse = 0; reuse < 128; reuse++)
        {
            World.Remove(_world, ids[^1]);
            ids.Add(Place());
        }

        Assert.Single(ids.Select(SlotOf).Distinct());
        Assert.Equal(129, ids.Distinct().Count());
    }
}
