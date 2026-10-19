using System.Collections.Generic;
using Gi.Entities.Tests.Support;
using Xunit;
using GiWorld = Gi.World;

namespace Gi.Entities.Columns.Tests;

public sealed class RestoreTests
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

    private static void Restore(GiScenarioWorld source, GiScenarioWorld target, IEnumerable<GiWorld.SourceId> ids, GiRestoreDriver driver,
        Dictionary<GiWorld.SourceId, GiWorld.SourceId> map)
    {
        var tables = GiColumnsExecutor.TablesOf(source, Profile());
        var states = new List<GiSourceState>();
        foreach (var id in ids)
        {
            Assert.True(GiSourceCapture.TryCapture(source.World, tables, id, out var state));
            states.Add(state);
        }

        var targetTables = GiColumnsExecutor.TablesOf(target, Profile());
        GiRestore.Sources(target.World, targetTables, states.ToArray(), map, driver);
    }

    [Fact]
    public void AHeadingAndScaleSurviveCaptureAndRestoreExactlyOverManyValues()
    {
        var rng = new GiRandom(3);
        using var original = GiScenarioWorld.Create(Profile());
        using var copy = GiScenarioWorld.Create(Profile());
        var ids = new List<GiWorld.SourceId>();
        for (var i = 0; i < 300; i++)
        {
            var id = GiWorld.Place(original.World, original.Layers[0], 8 + rng.Next(48) + rng.Next(8) * 0.125f, 8 + rng.Next(48) + rng.Next(8) * 0.125f,
                original.Stamps[rng.Next(6)], (sbyte)(1 + rng.Next(9)));
            GiWorld.Turn(original.World, id, rng.Next(100000) * 0.0001f);
            GiWorld.Scale(original.World, id, 0.5f + rng.Next(40) * 0.1f);
            ids.Add(id);
        }

        GiWorld.Process(original.World);
        Restore(original, copy, ids, new GiRestoreDriver(), new Dictionary<GiWorld.SourceId, GiWorld.SourceId>());
        Assert.Equal(Hash(original), Hash(copy));
    }

    [Fact]
    public void AnExpiryInFlightRestoresToTheSameRemovalTick()
    {
        using var original = GiScenarioWorld.Create(Profile());
        using var copy = GiScenarioWorld.Create(Profile());
        var ids = new List<GiWorld.SourceId>();
        for (var i = 0; i < 6; i++)
        {
            var id = GiWorld.Place(original.World, original.Layers[i % 2], 12 + i * 7, 20f, original.Stamps[i % 3], 5);
            GiWorld.Expire(original.World, id, 4 + i);
            ids.Add(id);
        }

        GiWorld.Process(original.World);
        GiWorld.Process(original.World);
        Restore(original, copy, ids, new GiRestoreDriver(), new Dictionary<GiWorld.SourceId, GiWorld.SourceId>());
        Assert.Equal(Hash(original), Hash(copy));
        for (var t = 0; t < 12; t++)
        {
            GiWorld.Process(original.World);
            GiWorld.Process(copy.World);
            Assert.Equal(Hash(original), Hash(copy));
        }
    }

    [Fact]
    public void AFadeInFlightRestoresToTheSameStaircaseAtEveryElapsedTick()
    {
        for (var elapsed = 0; elapsed <= 9; elapsed++)
        {
            using var original = GiScenarioWorld.Create(Profile());
            using var copy = GiScenarioWorld.Create(Profile());
            var ids = new List<GiWorld.SourceId>();
            var plans = new[] { (From: (sbyte)10, To: (sbyte)-7, Ticks: 9), (From: (sbyte)3, To: (sbyte)12, Ticks: 7), (From: (sbyte)-4, To: (sbyte)5, Ticks: 11) };
            for (var i = 0; i < plans.Length; i++)
            {
                var id = GiWorld.Place(original.World, original.Layers[i % 2], 14 + i * 11, 30f, original.Stamps[i], plans[i].From);
                GiWorld.Process(original.World);
                GiWorld.Fade(original.World, id, plans[i].To, plans[i].Ticks);
                ids.Add(id);
            }

            for (var t = 0; t < elapsed; t++) GiWorld.Process(original.World);

            var driver = new GiRestoreDriver();
                Restore(original, copy, ids, driver, new Dictionary<GiWorld.SourceId, GiWorld.SourceId>());
            Assert.Equal(Hash(original), Hash(copy));
            for (var t = 0; t < 14; t++)
            {
                GiWorld.Process(original.World);
                driver.Step(copy.World);
                GiWorld.Process(copy.World);
                Assert.Equal(Hash(original), Hash(copy));
            }
        }
    }

    [Fact]
    public void TheEngineFadeGainEqualsTheRestoreStaircaseOverTheWholeGainRange()
    {
        foreach (var ticks in new[] { 1, 2, 3, 5, 8, 13, 64 })
        {
            using var setup = GiScenarioWorld.Create(Profile());
            var world = setup.World;
            var ids = new List<GiWorld.SourceId>();
            var plans = new (sbyte From, sbyte To)[33 * 33];
            var index = 0;
            for (var from = -16; from <= 16; from++)
            for (var to = -16; to <= 16; to++)
            {
                var row = index / 33;
                var column = index % 33;
                ids.Add(GiWorld.Place(world, setup.Layers[0], 4f + column * 1.75f, 4f + row * 1.75f, setup.Stamps[0], (sbyte)from));
                plans[index] = ((sbyte)from, (sbyte)to);
                index++;
            }

            for (var i = 0; i < ids.Count; i++)
                Assert.Equal(GiWorld.WriteStatus.Applied, GiWorld.Fade(world, ids[i], plans[i].To, ticks));

            for (var elapsed = 0; elapsed <= ticks; elapsed++)
            {
                for (var i = 0; i < ids.Count; i++)
                {
                    Assert.True(GiWorld.TryGetSourceSpec(world, ids[i], out var spec));
                    var staircase = GiRestoreDriver.Staircase(plans[i].From, plans[i].To, ticks, elapsed);
                    Assert.True(staircase == spec.Gain,
                        $"ticks {ticks} from {plans[i].From} to {plans[i].To} elapsed {elapsed} engine {spec.Gain} staircase {staircase}");
                }

                if (elapsed < ticks) GiWorld.Process(world);
            }
        }
    }

    [Fact]
    public void TheStaircaseMatchesTheDocumentedRounding()
    {
        Assert.Equal((sbyte)0, GiRestoreDriver.Staircase(0, 0, 5, 3));
        Assert.Equal((sbyte)10, GiRestoreDriver.Staircase(0, 10, 4, 4));
        Assert.Equal((sbyte)-3, GiRestoreDriver.Staircase(0, -10, 6, 2));
        Assert.Equal((sbyte)3, GiRestoreDriver.Staircase(0, 10, 6, 2));
        Assert.Equal((sbyte)7, GiRestoreDriver.Staircase(5, 9, 4, 2));
        Assert.Equal((sbyte)9, GiRestoreDriver.Staircase(5, 9, 0, 2));
    }

    [Fact]
    public void ACancelledEntryNeverWritesAgain()
    {
        using var world = GiScenarioWorld.Create(Profile());
        var id = GiWorld.Place(world.World, world.Layers[0], 20f, 20f, world.Stamps[0], 2);
        GiWorld.Process(world.World);
        var driver = new GiRestoreDriver();
        driver.Add(id, 2, 10, 4, 1);
        Assert.Equal(1, driver.Count);
        driver.Cancel(id);
        Assert.Equal(0, driver.Count);
        driver.Step(world.World);
        GiWorld.Process(world.World);
        Assert.True(GiWorld.TryGetSourceSpec(world.World, id, out var spec));
        Assert.Equal((sbyte)2, spec.Gain);
    }
}
