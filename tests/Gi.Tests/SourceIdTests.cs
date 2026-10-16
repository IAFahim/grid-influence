using System.Globalization;
using Xunit;

namespace Gi.Tests;

public sealed class SourceIdTests : IDisposable
{
    private readonly byte _world = World.New();
    private readonly byte _layer;
    private readonly byte _stamp = Stamp.Box(2, 2, 3);

    public SourceIdTests()
    {
        Grid.New(_world, 6, 0f, 0f, 64f);
        _layer = Layer.New(_world);
    }

    public void Dispose()
    {
        World.Free(_world);
        Assert.True(Stamp.Free(_stamp));
    }

    [Fact]
    public unsafe void MillionKillSpawnCycles_OnTenSlots_NeverAliasAnId()
    {
        const int slots = 10;
        const int cycles = 1_000_000;
        var live = new World.SourceId[slots];
        var lastGeneration = new uint[slots];
        var issued = new HashSet<World.SourceId>(cycles + slots);
        for (var i = 0; i < slots; i++)
        {
            live[i] = World.Place(_world, _layer, 10f + i, 10f, _stamp, 1);
            Assert.True(issued.Add(live[i]));
            lastGeneration[live[i].Slot] = live[i].Generation;
        }

        for (var cycle = 0; cycle < cycles; cycle++)
        {
            var seat = cycle % slots;
            var dying = live[seat];
            World.Remove(_world, dying);
            Assert.False(World.TryGetSourceSpec(_world, dying, out _),
                $"cycle {cycle.ToString(CultureInfo.InvariantCulture)}: stale id {dying} addresses a live source");

            var spawned = World.Place(_world, _layer, 10f + seat, 12f, _stamp, 1);
            Assert.NotEqual(default, spawned);
            Assert.Equal(dying.Slot, spawned.Slot);
            Assert.True(spawned.Generation > lastGeneration[spawned.Slot],
                $"cycle {cycle.ToString(CultureInfo.InvariantCulture)}: slot {spawned.Slot.ToString(CultureInfo.InvariantCulture)} " +
                $"regenerated generation {spawned.Generation.ToString(CultureInfo.InvariantCulture)}");
            if (!issued.Add(spawned))
                Assert.Fail($"id {spawned} reissued at cycle {cycle.ToString(CultureInfo.InvariantCulture)}");

            lastGeneration[spawned.Slot] = spawned.Generation;
            live[seat] = spawned;
        }

        for (var i = 0; i < slots; i++) Assert.True(World.TryGetSourceSpec(_world, live[i], out _));
        Assert.Equal(slots + cycles, issued.Count);
    }

    [Fact]
    public unsafe void ASlotWhoseGenerationReachesMaxValue_IsRetiredAndNeverReissued()
    {
        var a = World.Place(_world, _layer, 10f, 10f, _stamp, 1);
        var s = &World.GetContext(_world)->Sources;
        s->Gen[(int)a.Slot] = uint.MaxValue - 1;
        var aged = new World.SourceId(a.Slot, uint.MaxValue - 1);

        World.Remove(_world, aged);
        var nearDeath = World.Place(_world, _layer, 12f, 12f, _stamp, 1);
        Assert.Equal(a.Slot, nearDeath.Slot);
        Assert.Equal(uint.MaxValue, nearDeath.Generation);

        World.Remove(_world, nearDeath);
        var elsewhere = World.Place(_world, _layer, 14f, 14f, _stamp, 1);
        Assert.NotEqual(a.Slot, elsewhere.Slot);
        Assert.False(World.TryGetSourceSpec(_world, a, out _));
        Assert.False(World.TryGetSourceSpec(_world, nearDeath, out _));

        for (var reuse = 0; reuse < 64; reuse++)
        {
            World.Remove(_world, elsewhere);
            elsewhere = World.Place(_world, _layer, 14f, 14f, _stamp, 1);
            Assert.NotEqual(a.Slot, elsewhere.Slot);
        }

        World.Record(_world);
        World.Remove(_world, elsewhere);
        World.Rewind(_world);
        var afterRewind = World.Place(_world, _layer, 16f, 16f, _stamp, 1);
        Assert.NotEqual(a.Slot, afterRewind.Slot);
    }

    [Fact]
    public unsafe void ExpiredRetiredSlots_DoNotReenterTheRing()
    {
        var a = World.Place(_world, _layer, 10f, 10f, _stamp, 1);
        World.Expire(_world, a, 1);
        World.Process(_world);
        var s = &World.GetContext(_world)->Sources;
        var retired = World.Place(_world, _layer, 12f, 12f, _stamp, 1);
        s->Gen[(int)retired.Slot] = uint.MaxValue - 1;
        World.Expire(_world, new World.SourceId(retired.Slot, uint.MaxValue - 1), 1);
        World.Process(_world);

        var final = World.Place(_world, _layer, 14f, 14f, _stamp, 1);
        Assert.Equal(retired.Slot, final.Slot);
        Assert.Equal(uint.MaxValue, final.Generation);
        World.Remove(_world, final);

        var fresh = World.Place(_world, _layer, 16f, 16f, _stamp, 1);
        Assert.NotEqual(final.Slot, fresh.Slot);
        Assert.False(World.TryGetSourceSpec(_world, retired, out _));
        Assert.False(World.TryGetSourceSpec(_world, final, out _));
    }
}
