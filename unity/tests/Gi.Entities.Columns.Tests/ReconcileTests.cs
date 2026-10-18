using System.Collections.Generic;
using Gi.Entities.Tests.Support;
using Xunit;
using GiWorld = Gi.World;

namespace Gi.Entities.Columns.Tests;

public sealed class ReconcileTests
{
    private static readonly GiScenarioOptions Options = GiScenarioOptions.Standard.WithoutTransforms().WithoutSplats();

    [Fact]
    public void ColumnsPipelineMatchesTheReferenceOverTwoHundredSeeds()
    {
        for (ulong seed = 1; seed <= 200; seed++)
        {
            var scenario = GiScenarioGenerator.Create(seed, Options);
            var expected = GiReferenceExecutor.Run(scenario);
            var actual = GiColumnsExecutor.Run(scenario);
            Assert.True(expected.Compare(actual).Length == 0, $"seed {seed}: {expected.Compare(actual)}");
        }
    }

    [Fact]
    public void ColumnsPipelineMatchesTheReferenceWithSplatsToo()
    {
        var options = GiScenarioOptions.Standard.WithoutTransforms();
        for (ulong seed = 300; seed < 340; seed++)
        {
            var scenario = GiScenarioGenerator.Create(seed, options);
            Assert.Equal(string.Empty, GiReferenceExecutor.Run(scenario).Compare(GiColumnsExecutor.Run(scenario)));
        }
    }

    [Fact]
    public void SlicingTheBatchKeepsEveryHashAndReindexesCreatedBy()
    {
        foreach (var limit in new[] { 1, 2, 3, 5, 7, 16 })
        {
            for (ulong seed = 1; seed <= 25; seed++)
            {
                var scenario = GiScenarioGenerator.Create(seed, Options);
                var expected = GiReferenceExecutor.Run(scenario);
                var actual = GiColumnsExecutor.Run(scenario, limit);
                Assert.True(expected.Compare(actual).Length == 0, $"limit {limit} seed {seed}: {expected.Compare(actual)}");
            }
        }
    }

    [Fact]
    public void SlicesNeverSeparateACreatedByCommandFromItsTarget()
    {
        var commands = new GiCommand[12];
        for (var i = 0; i < commands.Length; i += 3)
        {
            commands[i] = new GiCommand { Kind = GiCommandKind.Place, Target = GiCommand.Existing };
            commands[i + 1] = new GiCommand { Kind = GiCommandKind.Fade, Target = i };
            commands[i + 2] = new GiCommand { Kind = GiCommandKind.Expire, Target = i };
        }

        foreach (var limit in new[] { 1, 2, 3, 4, 5, 6, 7, 11 })
        {
            var start = 0;
            while (start < commands.Length)
            {
                var stop = GiApplyPass.SliceEnd(commands, start, limit);
                Assert.True(stop > start);
                if (stop < commands.Length) Assert.True(commands[stop].Target < 0, $"limit {limit} cut at {stop}");
                if (limit >= 3) Assert.True(stop - start <= limit, $"limit {limit} slice {start}..{stop}");
                start = stop;
            }
        }
    }

    [Fact]
    public void PulsesAreSortedFieldWiseSoTheBatchIsIdenticalAcrossRuns()
    {
        var pulses = new List<GiPulse>();
        var rng = new GiRandom(11);
        for (var i = 0; i < 40; i++)
            pulses.Add(new GiPulse
            {
                Layer = new GiLayerKey((byte)rng.Next(3)),
                Stamp = new GiStampKey((byte)rng.Next(4)),
                X = rng.Next(50),
                Y = rng.Next(50),
                Heading = rng.Next(4),
                Scale = 1f,
                Gain = (sbyte)(rng.Next(9) - 4),
                Ticks = (ushort)(2 + rng.Next(3)),
                FadeOut = rng.Chance(1, 2),
            });

        var sortedA = new List<GiPulse>(pulses);
        sortedA.Sort(new GiPulseOrder());
        var shuffled = pulses.ToArray();
        rng.Shuffle(shuffled);
        var sortedB = new List<GiPulse>(shuffled);
        sortedB.Sort(new GiPulseOrder());
        for (var i = 0; i < sortedA.Count; i++) Assert.Equal(0, new GiPulseOrder().Compare(sortedA[i], sortedB[i]));
    }

    [Fact]
    public void PulseOrderTreatsNaNAsABitPatternNotAnUnorderedValue()
    {
        var a = new GiPulse { X = float.NaN };
        var b = new GiPulse { X = float.NaN };
        Assert.Equal(0, new GiPulseOrder().Compare(a, b));
        Assert.NotEqual(0, new GiPulseOrder().Compare(new GiPulse { X = float.NaN }, new GiPulse { X = 1f }));
    }

    [Fact]
    public void DiffIsTotalAndIssuesTheExpectedCommands()
    {
        var tables = TablesOf(GiScenarioProfile.Standard());
        var none = default(GiApplied);
        var want = new GiDesired { Want = true, Layer = new GiLayerKey(0), Stamp = new GiStampKey(1), Gain = 5, X = 10f, Y = 12f };

        var place = GiDiff.Plan(want, none, tables, 7, 100);
        Assert.Equal(1, place.Count);
        Assert.Equal(GiCommandKind.Place, place[0].Kind);
        Assert.Equal(7, place[0].Owner);
        Assert.Equal(5, place[0].Gain);

        want.Ramp = 3;
        var ramp = GiDiff.Plan(want, none, tables, 7, 100);
        Assert.Equal(2, ramp.Count);
        Assert.Equal(0, ramp[0].Gain);
        Assert.Equal(5, ramp[0].TargetGain);
        Assert.Equal(GiCommandKind.Fade, ramp[1].Kind);
        Assert.Equal(100, ramp[1].Target);
        Assert.Equal(3, ramp[1].Ticks);

        var placed = new GiApplied { Placed = true, Layer = want.Layer, Stamp = want.Stamp, Gain = 5, Source = new GiWorld.SourceId(2, 3) };
        Assert.Equal(0, GiDiff.Plan(want, placed, tables, 0, 0).Count);

        want.Gain = 9;
        var fade = GiDiff.Plan(want, placed, tables, 0, 0);
        Assert.Equal(1, fade.Count);
        Assert.Equal(GiCommandKind.Fade, fade[0].Kind);
        Assert.Equal(new GiWorld.SourceId(2, 3), fade[0].Source);
        want.Ramp = 0;
        Assert.Equal(GiCommandKind.SetGain, GiDiff.Plan(want, placed, tables, 0, 0)[0].Kind);

        want.Stamp = new GiStampKey(2);
        var retarget = GiDiff.Plan(want, placed, tables, 0, 50);
        Assert.Equal(2, retarget.Count);
        Assert.Equal(GiCommandKind.Remove, retarget[0].Kind);
        Assert.Equal(GiCommandKind.Place, retarget[1].Kind);

        want.Want = false;
        var remove = GiDiff.Plan(want, placed, tables, 0, 0);
        Assert.Equal(1, remove.Count);
        Assert.Equal(GiCommandKind.Remove, remove[0].Kind);
        Assert.Equal(0, GiDiff.Plan(want, none, tables, 0, 0).Count);
    }

    [Fact]
    public void AnUnknownKeyBecomesAFaultAndRemovesWhatIsPlaced()
    {
        var tables = TablesOf(GiScenarioProfile.Standard());
        var placed = new GiApplied { Placed = true, Layer = new GiLayerKey(0), Stamp = new GiStampKey(0), Source = new GiWorld.SourceId(1, 1) };
        var badLayer = new GiDesired { Want = true, Layer = new GiLayerKey(200), Stamp = new GiStampKey(0) };
        var plan = GiDiff.Plan(badLayer, placed, tables, 0, 0);
        Assert.Equal(GiFaultCode.InvalidLayer, plan.Fault);
        Assert.Equal(1, plan.Count);
        Assert.Equal(GiCommandKind.Remove, plan[0].Kind);

        var badStamp = new GiDesired { Want = true, Layer = new GiLayerKey(0), Stamp = new GiStampKey(200) };
        plan = GiDiff.Plan(badStamp, default, tables, 0, 0);
        Assert.Equal(GiFaultCode.InvalidStamp, plan.Fault);
        Assert.Equal(0, plan.Count);
    }

    [Fact]
    public void WriteBackFoldsResultsIntoTheAppliedState()
    {
        var applied = default(GiApplied);
        var place = new GiCommand { Kind = GiCommandKind.Place, LayerKey = new GiLayerKey(2), StampKey = new GiStampKey(3), TargetGain = 6 };
        var id = new GiWorld.SourceId(4, 9);
        Assert.Equal(GiFaultCode.None, GiWriteBack.Fold(place, new GiCommandResult { Source = id }, ref applied));
        Assert.True(applied.Placed);
        Assert.Equal(id, applied.Source);
        Assert.Equal(6, applied.Gain);
        Assert.Equal(new GiLayerKey(2), applied.Layer);

        Assert.Equal(GiFaultCode.None,
            GiWriteBack.Fold(new GiCommand { Kind = GiCommandKind.Fade, TargetGain = 1 }, default, ref applied));
        Assert.Equal(1, applied.Gain);

        var failedGain = new GiCommandResult { Code = GiFaultCode.InvalidSource };
        Assert.Equal(GiFaultCode.InvalidSource, GiWriteBack.Fold(new GiCommand { Kind = GiCommandKind.SetGain, TargetGain = 9 }, failedGain, ref applied));
        Assert.Equal(1, applied.Gain);

        Assert.Equal(GiFaultCode.None, GiWriteBack.Fold(new GiCommand { Kind = GiCommandKind.Remove }, failedGain, ref applied));
        Assert.False(applied.Placed);
        Assert.Equal(default, applied.Source);

        var failedPlace = new GiCommandResult { Code = GiFaultCode.SourceLimit };
        Assert.Equal(GiFaultCode.SourceLimit, GiWriteBack.Fold(place, failedPlace, ref applied));
        Assert.False(applied.Placed);
    }

    [Fact]
    public void TheApplyPassRetriesOnceAfterAStaleToken()
    {
        using var setup = GiScenarioWorld.Create(GiScenarioProfile.Standard());
        var tables = GiColumnsExecutor.TablesOf(setup);
        var token = GiWorld.Capture(setup.World);
        GiWorld.Clear(setup.World);

        var commands = new[]
        {
            new GiCommand
            {
                Kind = GiCommandKind.Place, Layer = setup.Layers[0], Stamp = setup.Stamps[0], Gain = 4, TargetGain = 4,
                Target = GiCommand.Existing, Owner = 0, X = 20f, Y = 20f, Scale = 1f,
            },
        };
        var results = new GiCommandResult[1];
        var status = GiApplyPass.Apply(ref token, setup.World, commands, results, new GiWorld.SourceCommand[1],
            new GiWorld.SourceResult[1], GiApplyPass.BatchLimit);
        Assert.Equal(GiApplyStatus.Applied, status);
        Assert.Equal(GiFaultCode.None, results[0].Code);
        Assert.NotEqual(default, results[0].Source);
        Assert.True(tables.StampCount > 0);
        Assert.Equal(GiWorld.Revision(setup.World), token.RevisionAtCapture);
    }

    private static GiTables TablesOf(GiScenarioProfile profile)
    {
        var tables = default(GiTables);
        tables.AddGrid(0);
        tables.AddGrid(1);
        for (var i = 0; i < profile.Layers.Length; i++) tables.AddLayer((byte)(10 + i));
        for (var i = 0; i < profile.Stamps.Length; i++) tables.AddStamp((byte)(20 + i));
        return tables;
    }
}
