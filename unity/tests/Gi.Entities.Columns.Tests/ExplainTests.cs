using System;
using Gi.Entities.Tests.Support;
using Xunit;
using GiGrid = Gi.Grid;
using GiLayer = Gi.Layer;
using GiStamp = Gi.Stamp;
using GiWorld = Gi.World;

namespace Gi.Entities.Columns.Tests;

public sealed class ExplainTests
{

    private static readonly int[] ScatterLayers = new[] { 0, 1, 2, 3, 4, 5 };
    private static readonly int[] ExpectedLayers = new[] { 2, 3, 4, 5 };
    private static readonly int[] IntegerStamps = { 0, 2, 3, 4 };

    private static GiWorld.SourceId[] Scatter(GiScenarioWorld setup, GiRandom rng, int count, int[] stamps, int layers)
    {
        var ids = new GiWorld.SourceId[count];
        for (var i = 0; i < count; i++)
        {
            ids[i] = GiWorld.Place(setup.World, setup.Layers[rng.Next(layers)], 8 + rng.Next(48) + rng.Next(8) * 0.125f,
                8 + rng.Next(48) + rng.Next(8) * 0.125f, setup.Stamps[stamps[rng.Next(stamps.Length)]], (sbyte)(1 + rng.Next(6)));
        }

        GiWorld.Process(setup.World);
        GiWorld.Process(setup.World);
        return ids;
    }

    private static void NearSource(GiScenarioWorld setup, GiWorld.SourceId[] ids, GiRandom rng, out float x, out float y)
    {
        GiWorld.TryGetSourceSpec(setup.World, ids[rng.Next(ids.Length)], out var spec);
        x = spec.X + rng.Next(5) * 0.5f - 1f;
        y = spec.Y + rng.Next(5) * 0.5f - 1f;
    }

    [Fact]
    public void IntegerStampContributionsSumToTheValueOverOneHundredSeeds()
    {
        var cells = 0;
        for (ulong seed = 1; seed <= 100; seed++)
        {
            using var setup = GiScenarioWorld.Create(GiScenarioProfile.Standard());
            var rng = new GiRandom(seed);
            var ids = Scatter(setup, rng, 12 + rng.Next(14), IntegerStamps, 2);
            var output = new GiContribution[ids.Length];
            for (var probe = 0; probe < 16; probe++)
            {
                var layer = rng.Next(2);
                NearSource(setup, ids, rng, out var x, out var y);
                var result = GiExplain.Cell(setup.World, setup.Layers[layer], x, y, ids, output);
                Assert.True(result.Ok);
                Assert.False(result.Saturated);
                Assert.Equal(result.Total, result.Sum);
                Assert.Equal(0, result.Rest);
                if (result.Contributors > 0) cells++;
            }
        }

        Assert.True(cells > 600, "cells with contributors " + cells);
    }

    [Fact]
    public void RoundedStampContributionsLeaveAResidualBoundedByTheContributors()
    {
        var residual = 0;
        var contributed = 0;
        for (ulong seed = 1; seed <= 100; seed++)
        {
            using var setup = GiScenarioWorld.Create(GiScenarioProfile.Standard());
            var rng = new GiRandom(seed);
            var ids = Scatter(setup, rng, 12 + rng.Next(14), ScatterLayers, 2);
            var output = new GiContribution[ids.Length];
            for (var probe = 0; probe < 16; probe++)
            {
                var layer = rng.Next(2);
                NearSource(setup, ids, rng, out var x, out var y);
                var result = GiExplain.Cell(setup.World, setup.Layers[layer], x, y, ids, output);
                Assert.False(result.Saturated);
                Assert.True(Math.Abs(result.Rest) <= result.Contributors, "rest " + result.Rest + " of " + result.Contributors);
                if (result.Contributors > 0) contributed++;
                if (result.Rest != 0) residual++;
            }
        }

        Assert.True(contributed > 600, "cells with contributors " + contributed);
        Assert.True(residual < contributed);
    }

    [Fact]
    public void ASaturatedPileSetsTheFlagAndLeavesARest()
    {
        using var setup = GiScenarioWorld.Create(GiScenarioProfile.Standard());
        var ids = new GiWorld.SourceId[90];
        for (var i = 0; i < ids.Length; i++) ids[i] = GiWorld.Place(setup.World, setup.Layers[0], 32f, 32f, setup.Stamps[0], 12);
        GiWorld.Process(setup.World);
        GiWorld.Process(setup.World);

        var result = GiExplain.Cell(setup.World, setup.Layers[0], 32.5f, 32.5f, ids, new GiContribution[8]);
        Assert.True(result.Ok);
        Assert.True(result.Saturated);
        Assert.Equal(GiExplain.Clamp, result.Total);
        Assert.Equal(0, result.Count);
        Assert.Equal(GiExplain.Clamp, result.Rest);

        var quiet = GiExplain.Cell(setup.World, setup.Layers[0], 3.5f, 3.5f, ids, new GiContribution[8]);
        Assert.False(quiet.Saturated);
        Assert.Equal(0, quiet.Count);
        Assert.Equal(0, quiet.Contributors);
    }

    [Fact]
    public void APileJustOverTheClampAttributesLessThanTheTotalAndSetsTheFlag()
    {
        using var setup = GiScenarioWorld.Create(GiScenarioProfile.Standard());
        var ids = new GiWorld.SourceId[55];
        for (var i = 0; i < ids.Length; i++) ids[i] = GiWorld.Place(setup.World, setup.Layers[0], 32f, 32f, setup.Stamps[0], 12);
        GiWorld.Process(setup.World);
        GiWorld.Process(setup.World);

        var output = new GiContribution[8];
        var result = GiExplain.Cell(setup.World, setup.Layers[0], 32.5f, 32.5f, ids, output);
        Assert.True(result.Saturated);
        Assert.Equal(GiExplain.Clamp, result.Total);
        Assert.Equal(ids.Length, result.Contributors);
        Assert.True(result.Sum < 600L * ids.Length);
        Assert.NotEqual(0, result.Rest);
        Assert.Equal(8, result.Count);
        Assert.Equal(output[0].Value, output[7].Value);
    }

    [Fact]
    public void ASingleSourceOwnsTheWholeValue()
    {
        using var setup = GiScenarioWorld.Create(GiScenarioProfile.Standard());
        var id = GiWorld.Place(setup.World, setup.Layers[1], 20.5f, 20.5f, setup.Stamps[0], 5);
        GiWorld.Process(setup.World);
        GiWorld.Process(setup.World);

        var output = new GiContribution[4];
        var result = GiExplain.Cell(setup.World, setup.Layers[1], 20.5f, 20.5f, new[] { id }, output);
        Assert.Equal(1, result.Count);
        Assert.Equal(result.Total, output[0].Value);
        Assert.Equal(id, output[0].Source);
        Assert.Equal(setup.Layers[1], output[0].Layer);
        Assert.Equal(setup.Stamps[0], output[0].Stamp);
        Assert.Equal(5, output[0].Gain);
        Assert.NotEqual(0, result.Total);
    }

    [Fact]
    public void RemovedStaleAndForeignSourcesContributeNothing()
    {
        using var setup = GiScenarioWorld.Create(GiScenarioProfile.Standard());
        var kept = GiWorld.Place(setup.World, setup.Layers[0], 30.5f, 30.5f, setup.Stamps[0], 4);
        var gone = GiWorld.Place(setup.World, setup.Layers[0], 30.5f, 30.5f, setup.Stamps[0], 4);
        var other = GiWorld.Place(setup.World, setup.Layers[1], 30.5f, 30.5f, setup.Stamps[0], 4);
        GiWorld.Process(setup.World);
        GiWorld.Remove(setup.World, gone);
        GiWorld.Process(setup.World);

        var ids = new[] { kept, gone, other, default };
        var output = new GiContribution[8];
        var result = GiExplain.Cell(setup.World, setup.Layers[0], 30.5f, 30.5f, ids, output);
        Assert.Equal(1, result.Count);
        Assert.Equal(kept, output[0].Source);
        Assert.Equal(result.Total, result.Sum);
    }

    [Fact]
    public void ACapacityOfNKeepsTheNLargestInTheFullOrder()
    {
        for (ulong seed = 1; seed <= 40; seed++)
        {
            using var setup = GiScenarioWorld.Create(GiScenarioProfile.Standard());
            var rng = new GiRandom(seed);
            var ids = Scatter(setup, rng, 30, IntegerStamps, 1);
            var full = new GiContribution[ids.Length];
            var whole = GiExplain.Cell(setup.World, setup.Layers[0], 32.5f, 32.5f, ids, full);
            for (var i = 1; i < whole.Count; i++) Assert.True(GiExplain.Precedes(full[i - 1], full[i]));

            foreach (var capacity in new[] { 1, 2, 3, 5 })
            {
                var small = new GiContribution[capacity];
                var part = GiExplain.Cell(setup.World, setup.Layers[0], 32.5f, 32.5f, ids, small);
                Assert.Equal(whole.Contributors, part.Contributors);
                Assert.Equal(whole.Sum, part.Sum);
                Assert.Equal(Math.Min(capacity, whole.Count), part.Count);
                for (var i = 0; i < part.Count; i++) Assert.Equal(full[i].Source, small[i].Source);
            }
        }
    }

    [Fact]
    public void ACellOutsideEveryGridIsNotOkAndTheOrderIsDeterministic()
    {
        using var setup = GiScenarioWorld.Create(GiScenarioProfile.Standard());
        var id = GiWorld.Place(setup.World, setup.Layers[0], 10f, 10f, setup.Stamps[0], 3);
        GiWorld.Process(setup.World);
        Assert.False(GiExplain.Cell(setup.World, setup.Layers[0], -900f, 900f, new[] { id }, new GiContribution[2]).Ok);
        Assert.False(GiExplain.Cell(200, 0, 1f, 1f, new[] { id }, new GiContribution[2]).Ok);
        Assert.Equal(0, GiExplain.Cell(setup.World, setup.Layers[0], 10f, 10f, new[] { id }, Span<GiContribution>.Empty).Count);

        var a = new GiContribution { Source = new GiWorld.SourceId(1, 1), Value = -9 };
        var b = new GiContribution { Source = new GiWorld.SourceId(2, 1), Value = 9 };
        Assert.True(GiExplain.Precedes(a, b));
        Assert.False(GiExplain.Precedes(b, a));
        Assert.True(GiExplain.Precedes(b, new GiContribution { Source = new GiWorld.SourceId(3, 1), Value = 8 }));
    }

    private static GiScenarioProfile ChainProfile()
    {
        return new GiScenarioProfile(
            new[] { new GiScenarioGrid(6, 0, 0, 64) },
            new[]
            {
                GiScenarioLayer.Persistent(0, 16),
                GiScenarioLayer.Persistent(0, 16),
                GiScenarioLayer.Sum(0, 3, 1, -2, 1),
                GiScenarioLayer.Max(2, 0),
                GiScenarioLayer.Mask(3, 1, 1, 100),
                GiScenarioLayer.Min(4, 2),
            },
            GiScenarioProfile.Standard().Stamps);
    }

    private static GiLayerSpec[] Specs(GiScenarioProfile profile)
    {
        var specs = new GiLayerSpec[profile.Layers.Length];
        for (var i = 0; i < specs.Length; i++)
        {
            var layer = profile.Layers[i];
            specs[i] = new GiLayerSpec
            {
                Kind = (GiLayerKind)(byte)layer.Kind,
                First = (byte)(layer.First < 0 ? GiLayerSpec.NoLayer : layer.First),
                Second = (byte)(layer.Second < 0 ? GiLayerSpec.NoLayer : layer.Second),
                FirstWeight = layer.FirstWeight,
                SecondWeight = layer.SecondWeight,
                Shift = layer.Shift,
                MaskLow = (short)layer.MaskLow,
                MaskHigh = (short)layer.MaskHigh,
            };
        }

        return specs;
    }

    [Fact]
    public void ARecipeTraceWalksTheInputsOnceAndEveryStepMatchesTheWorldOverOneHundredSeeds()
    {
        var profile = ChainProfile();
        var specs = Specs(profile);
        var shown = 0;
        for (ulong seed = 1; seed <= 100; seed++)
        {
            using var setup = GiScenarioWorld.Create(profile);
            var rng = new GiRandom(seed);
            for (var i = 0; i < 20; i++)
                GiWorld.Place(setup.World, setup.Layers[rng.Next(2)], 8 + rng.Next(48) + rng.Next(8) * 0.125f, 8 + rng.Next(48) + rng.Next(8) * 0.125f,
                    setup.Stamps[rng.Next(6)], (sbyte)(1 + rng.Next(9)));
            GiWorld.Process(setup.World);
            GiWorld.Process(setup.World);

            var steps = new GiRecipeStep[GiRecipe.MaxLayers];
            for (var probe = 0; probe < 12; probe++)
            {
                var x = 6 + rng.Next(52) + rng.Next(8) * 0.125f;
                var y = 6 + rng.Next(52) + rng.Next(8) * 0.125f;
                Assert.Equal(0, GiRecipe.Trace(setup.World, specs, setup.Layers, 0, x, y, steps));

                var count = GiRecipe.Trace(setup.World, specs, setup.Layers, 5, x, y, steps);
                Assert.Equal(4, count);
                Assert.Equal(ExpectedLayers, new[] { steps[0].Layer, steps[1].Layer, steps[2].Layer, steps[3].Layer });
                for (var s = 0; s < count; s++)
                {
                    Assert.True(steps[s].Matches, "step " + steps[s].Layer + " " + steps[s].Value + " vs " + steps[s].Actual);
                    GiWorld.TrySense(setup.World, setup.Layers[steps[s].Layer], x, y, out var actual);
                    Assert.Equal(actual, steps[s].Actual);
                    GiWorld.TrySense(setup.World, setup.Layers[steps[s].First], x, y, out var a);
                    GiWorld.TrySense(setup.World, setup.Layers[steps[s].Second], x, y, out var b);
                    Assert.Equal((a, b), (steps[s].A, steps[s].B));
                }

                GiWorld.TrySense(setup.World, setup.Layers[5], x, y, out var final);
                Assert.Equal(final, steps[count - 1].Value);
                if (steps[0].Raw != 0) shown++;
            }
        }

        Assert.True(shown > 100);
    }

    [Fact]
    public void ARecipeStepForTheSumReportsTheWeightedRawAndTheRoundedShift()
    {
        var spec = new GiLayerSpec { Kind = GiLayerKind.Sum, FirstWeight = 3, SecondWeight = -2, Shift = 1 };
        Assert.Equal(3, GiRecipe.Combine(spec, 3, 2, out var raw));
        Assert.Equal(5, raw);
        Assert.Equal(-3, GiRecipe.Combine(spec, 1, 4, out raw));
        Assert.Equal(-5, raw);
        Assert.Equal(short.MaxValue, GiRecipe.Combine(new GiLayerSpec { Kind = GiLayerKind.Sum, FirstWeight = 1000, SecondWeight = 1000 }, 30000, 30000, out _));
        Assert.Equal(short.MinValue, GiRecipe.Combine(new GiLayerSpec { Kind = GiLayerKind.Sum, FirstWeight = -1000, SecondWeight = 1 }, 30000, 0, out _));
        Assert.Equal(-4, GiRecipe.Combine(new GiLayerSpec { Kind = GiLayerKind.Min }, -4, 9, out _));
        Assert.Equal(9, GiRecipe.Combine(new GiLayerSpec { Kind = GiLayerKind.Max }, -4, 9, out _));
        var mask = new GiLayerSpec { Kind = GiLayerKind.Mask, MaskLow = 2, MaskHigh = 5 };
        Assert.Equal(7, GiRecipe.Combine(mask, 7, 5, out _));
        Assert.Equal(0, GiRecipe.Combine(mask, 7, 6, out _));
        Assert.False(GiRecipe.IsDerived(GiLayerKind.Persistent));
        Assert.False(GiRecipe.IsDerived(GiLayerKind.Transient));
        Assert.True(GiRecipe.IsDerived(GiLayerKind.Mask));
    }

    [Fact]
    public void ATraceIgnoresBrokenLayerReferencesAndSmallOutputs()
    {
        var profile = ChainProfile();
        var specs = Specs(profile);
        using var setup = GiScenarioWorld.Create(profile);
        GiWorld.Place(setup.World, setup.Layers[0], 20f, 20f, setup.Stamps[0], 6);
        GiWorld.Process(setup.World);

        var steps = new GiRecipeStep[1];
        Assert.Equal(1, GiRecipe.Trace(setup.World, specs, setup.Layers, 5, 20.5f, 20.5f, steps));
        Assert.Equal(0, GiRecipe.Trace(setup.World, specs, setup.Layers, 5, 20.5f, 20.5f, Span<GiRecipeStep>.Empty));
        Assert.Equal(0, GiRecipe.Trace(setup.World, specs, setup.Layers, -1, 20.5f, 20.5f, steps));
        Assert.Equal(0, GiRecipe.Trace(setup.World, specs, setup.Layers, 99, 20.5f, 20.5f, steps));
        var broken = (GiLayerSpec[])specs.Clone();
        broken[2].First = GiLayerSpec.NoLayer;
        Assert.Equal(0, GiRecipe.Trace(setup.World, broken, setup.Layers, 2, 20.5f, 20.5f, steps));
        Assert.Equal(0, GiRecipe.Trace(setup.World, specs, new byte[2], 5, 1f, 1f, steps));
    }

    [Fact]
    public void AOneInputSumTracesWithoutASecondLayer()
    {
        var world = GiWorld.New();
        var stamp = GiStamp.Box(3, 3, 20);
        try
        {
            GiGrid.New(world, 6, 0f, 0f, 64f);
            var baseLayer = GiLayer.New(world, GiLayer.Residency.Persistent, GiLayer.Precision.Cell);
            var summed = GiLayer.Sum(world, baseLayer, 3, 1);
            GiWorld.Place(world, baseLayer, 20f, 20f, stamp, 5);
            GiWorld.Process(world);

            var specs = new[]
            {
                new GiLayerSpec { Kind = GiLayerKind.Persistent, First = GiLayerSpec.NoLayer, Second = GiLayerSpec.NoLayer },
                new GiLayerSpec { Kind = GiLayerKind.Sum, First = 0, Second = GiLayerSpec.NoLayer, FirstWeight = 3, Shift = 1 },
            };
            var steps = new GiRecipeStep[2];
            Assert.Equal(1, GiRecipe.Trace(world, specs, new[] { baseLayer, summed }, 1, 20.5f, 20.5f, steps));
            Assert.Equal(-1, steps[0].Second);
            Assert.True(steps[0].Matches);
            Assert.NotEqual(0, steps[0].Value);
            Assert.Equal(steps[0].A * 3, steps[0].Raw);
            Assert.DoesNotContain("and layer", GiExplainText.Step(steps[0]));
        }
        finally
        {
            GiWorld.Free(world);
            GiStamp.Free(stamp);
        }
    }
}
