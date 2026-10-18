using System.Collections.Generic;
using Gi.Entities.Tests.Support;
using Xunit;
using GiWorld = Gi.World;

namespace Gi.Entities.Columns.Tests;

public sealed class SaveTests
{
    private static GiScenarioProfile Profile()
    {
        return GiScenarioProfile.Standard();
    }

    private static ulong[] Hash(GiScenarioWorld world)
    {
        var hashes = new ulong[world.Layers.Length];
        for (var l = 0; l < hashes.Length; l++) hashes[l] = GiFieldHash.OfLayer(world.World, world.Layers[l]);
        return hashes;
    }

    private static List<GiSourceState> Capture(GiScenarioWorld world, List<GiWorld.SourceId> ids)
    {
        var tables = GiColumnsExecutor.TablesOf(world, Profile());
        var states = new List<GiSourceState>();
        foreach (var id in ids)
            if (GiSourceCapture.TryCapture(world.World, tables, id, out var state))
                states.Add(state);
        return states;
    }

    private static void Pulses(GiScenarioWorld world, List<GiWorld.SourceId> ids, GiRandom rng, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var id = GiWorld.Place(world.World, world.Layers[rng.Next(2)], 10 + rng.Next(44) + rng.Next(8) * 0.125f, 10 + rng.Next(44) + rng.Next(8) * 0.125f,
                world.Stamps[rng.Next(6)], (sbyte)(2 + rng.Next(12)));
            GiWorld.Turn(world.World, id, rng.Next(100000) * 0.0001f);
            GiWorld.Scale(world.World, id, 0.5f + rng.Next(30) * 0.1f);
            var ticks = 3 + rng.Next(10);
            if (rng.Chance(1, 2)) GiWorld.Fade(world.World, id, 0, ticks);
            GiWorld.Expire(world.World, id, ticks);
            ids.Add(id);
        }
    }

    [Fact]
    public void PulsesComeBackWithTheirRemainingTicksAndContinueExactlyOverFortySeeds()
    {
        for (ulong seed = 1; seed <= 40; seed++)
        {
            var rng = new GiRandom(seed);
            using var original = GiScenarioWorld.Create(Profile());
            using var copy = GiScenarioWorld.Create(Profile());
            var ids = new List<GiWorld.SourceId>();
            Pulses(original, ids, rng, 12);
            var elapsed = rng.Next(8);
            for (var t = 0; t < elapsed; t++)
            {
                GiWorld.Process(original.World);
                if (t % 3 == 0) Pulses(original, ids, rng, 2);
            }

            var states = Capture(original, ids);
            var driver = new GiRestoreDriver();
            var tables = GiColumnsExecutor.TablesOf(copy, Profile());
            var restored = new List<(GiSourceState State, GiWorld.SourceId Id)>();
            foreach (var state in states)
            {
                Assert.True(GiRestore.Place(copy.World, tables, state, driver, out var id));
                restored.Add((state, id));
            }

            foreach (var (state, id) in restored)
            {
                Assert.True(GiWorld.TryGetSourceSpec(copy.World, id, out var spec));
                Assert.Equal(state.Gain, spec.Gain);
                if (!state.Expiring) continue;

                Assert.True(GiWorld.TryGetSchedule(copy.World, id, out _, out _, out _, out _, out _, out var left));
                Assert.Equal(state.ExpireTicksLeft, left);
            }

            for (var t = 0; t < 16; t++)
            {
                GiWorld.Process(original.World);
                driver.Step(copy.World);
                GiWorld.Process(copy.World);
                Assert.Equal(Hash(original), Hash(copy));
            }
        }
    }

    [Fact]
    public void ARampInFlightKeepsItsStaircaseAndRestartingItWouldNot()
    {
        var differs = 0;
        for (var elapsed = 1; elapsed <= 6; elapsed++)
        {
            using var original = GiScenarioWorld.Create(Profile());
            using var copy = GiScenarioWorld.Create(Profile());
            using var restarted = GiScenarioWorld.Create(Profile());
            var id = GiWorld.Place(original.World, original.Layers[0], 30f, 30f, original.Stamps[0], 0);
            GiWorld.Fade(original.World, id, 12, 8);
            for (var t = 0; t < elapsed; t++) GiWorld.Process(original.World);

            var state = Capture(original, new List<GiWorld.SourceId> { id })[0];
            var fresh = GiWorld.Place(copy.World, copy.Layers[0], 30f, 30f, copy.Stamps[0], 0);
            GiWorld.Fade(copy.World, fresh, 12, 8);
            var driver = new GiRestoreDriver();
            GiRestore.Schedule(copy.World, fresh, state, driver);

            var again = GiWorld.Place(restarted.World, restarted.Layers[0], 30f, 30f, restarted.Stamps[0], 0);
            GiWorld.Fade(restarted.World, again, 12, 8);

            for (var t = 0; t < 10; t++)
            {
                GiWorld.Process(original.World);
                driver.Step(copy.World);
                GiWorld.Process(copy.World);
                GiWorld.Process(restarted.World);
                Assert.Equal(Hash(original), Hash(copy));
                if (!System.Linq.Enumerable.SequenceEqual(Hash(original), Hash(restarted))) differs++;
            }
        }

        Assert.True(differs > 10);
    }

    [Fact]
    public void AForeignWriteCancelsTheRedriveOfThatSource()
    {
        using var world = GiScenarioWorld.Create(Profile());
        var id = GiWorld.Place(world.World, world.Layers[0], 30f, 30f, world.Stamps[0], 4);
        var other = GiWorld.Place(world.World, world.Layers[0], 40f, 30f, world.Stamps[0], 4);
        GiWorld.Process(world.World);
        var driver = new GiRestoreDriver();
        driver.Add(id, 4, 12, 8, 8);
        driver.Add(other, 4, 12, 8, 8);
        driver.Step(world.World);
        GiWorld.Process(world.World);
        GiWorld.TryGetSourceSpec(world.World, other, out var stepped);
        Assert.Equal(GiRestoreDriver.Staircase(4, 12, 8, 1), stepped.Gain);

        GiWorld.SetGain(world.World, id, 9);
        driver.Step(world.World);
        GiWorld.Process(world.World);
        Assert.Equal(1, driver.Count);
        GiWorld.TryGetSourceSpec(world.World, id, out var kept);
        Assert.Equal((sbyte)9, kept.Gain);

        GiWorld.Fade(world.World, other, 0, 5);
        driver.Step(world.World);
        Assert.Equal(0, driver.Count);
        GiWorld.Remove(world.World, other);
        driver.Add(other, 1, 2, 3, 3);
        driver.Step(world.World);
        Assert.Equal(0, driver.Count);
    }

    [Fact]
    public void AnOwnerWhoseAppliedGainChangedLosesItsEntryAndOthersKeepTheirs()
    {
        using var world = GiScenarioWorld.Create(Profile());
        var first = GiWorld.Place(world.World, world.Layers[0], 20f, 20f, world.Stamps[0], 2);
        var second = GiWorld.Place(world.World, world.Layers[0], 40f, 20f, world.Stamps[0], 2);
        var pulse = GiWorld.Place(world.World, world.Layers[0], 20f, 40f, world.Stamps[0], 2);
        GiWorld.Process(world.World);
        var driver = new GiRestoreDriver();
        driver.Add(first, 2, 10, 8, 8, 11, 10);
        driver.Add(second, 2, 10, 8, 8, 12, 10);
        driver.Add(pulse, 2, 10, 8, 8);
        Assert.Equal(3, driver.Count);

        driver.CancelWhere((tag, applied) => tag == 11 && applied == 10);
        Assert.Equal(2, driver.Count);
        driver.CancelWhere((_, _) => false);
        Assert.Equal(2, driver.Count);
        driver.CancelWhere((_, _) => true);
        Assert.Equal(1, driver.Count);
        driver.Step(world.World);
        GiWorld.Process(world.World);
        GiWorld.TryGetSourceSpec(world.World, pulse, out var stepped);
        Assert.Equal(GiRestoreDriver.Staircase(2, 10, 8, 1), stepped.Gain);
    }
}
