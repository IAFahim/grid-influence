using System;
using System.Collections.Generic;
using System.Linq;
using Gi.Entities.Tests.Support;
using Gi.Validation;
using Xunit;

namespace Gi.Entities.Columns.Tests;

public sealed class ProfileCheckTests
{
    private static GiProfileSpec Standard() => GiScenarioProfile.Standard().ToSpec();

    private static GiProfileIssue Only(GiProfileSpec spec)
    {
        var issues = GiProfileCheck.Check(spec);
        Assert.Single(issues);
        return issues[0];
    }

    private static void Grow<T>(ref T[] array, int length)
    {
        var grown = new T[length];
        for (var i = 0; i < length; i++) grown[i] = i < array.Length ? array[i] : array[0];
        array = grown;
    }

    [Fact]
    public void TheStandardProfileHasNoIssues()
    {
        Assert.Empty(GiProfileCheck.Check(Standard()));
    }

    [Fact]
    public void AnEmptyProfileNamesWhatIsMissing()
    {
        var issues = GiProfileCheck.Check(new GiProfileSpec());
        Assert.Equal(new[] { GiProfileCheck.NoGrids, GiProfileCheck.NoLayers }, issues.Select(i => i.Message).ToArray());
        Assert.All(issues, issue => Assert.Equal(GiProfileSection.Profile, issue.Section));
    }

    [Fact]
    public void GridRulesUseTheValidationMessageWord_ForWord()
    {
        var spec = Standard();
        spec.Grids[1].Power = 4;
        var issue = Only(spec);
        GiValidation.TryGrid(4, spec.Grids[1].X, spec.Grids[1].Y, spec.Grids[1].Size, out var expected);
        Assert.Equal(GiProfileSection.Grid, issue.Section);
        Assert.Equal(1, issue.Index);
        Assert.Equal(expected, issue.Message);

        spec = Standard();
        spec.Grids[0].Size = -1f;
        GiValidation.TryGrid(spec.Grids[0].Power, spec.Grids[0].X, spec.Grids[0].Y, -1f, out expected);
        Assert.Equal(expected, Only(spec).Message);
    }

    [Fact]
    public void CountRulesUseTheValidationMessages()
    {
        var spec = Standard();
        Grow(ref spec.Layers, 33);
        GiValidation.TryLayerCount(33, out var layers);
        Assert.Equal(layers, Only(spec).Message);

        spec = Standard();
        Grow(ref spec.Stamps, 256);
        GiValidation.TryStampCount(256, out var stamps);
        Assert.Equal(stamps, Only(spec).Message);

        spec = Standard();
        Grow(ref spec.Grids, 33);
        var issue = Only(spec);
        Assert.Equal(GiProfileCheck.TooManyGrids, issue.Message);
    }

    [Fact]
    public void StampRulesUseTheValidationMessages()
    {
        var spec = Standard();
        spec.Stamps[0].Width = 0;
        GiValidation.TryRectangle(0, spec.Stamps[0].Height, spec.Stamps[0].Value, out var rectangle);
        var issue = Only(spec);
        Assert.Equal((GiProfileSection.Stamp, 0, rectangle), (issue.Section, issue.Index, issue.Message));

        spec = Standard();
        var disk = Array.FindIndex(spec.Stamps, s => s.Shape == GiStampShape.Disk);
        spec.Stamps[disk].Radius = 0;
        GiValidation.TryRound(0, spec.Stamps[disk].Arc, spec.Stamps[disk].Value, out var round);
        issue = Only(spec);
        Assert.Equal((GiProfileSection.Stamp, disk, round), (issue.Section, issue.Index, issue.Message));

        spec = Standard();
        var raster = Array.FindIndex(spec.Stamps, s => s.Shape == GiStampShape.Raster);
        spec.Stamps[raster].CellCount--;
        issue = Only(spec);
        Assert.Equal((GiProfileSection.Stamp, raster, GiProfileCheck.RasterCells), (issue.Section, issue.Index, issue.Message));

        spec = Standard();
        spec.Stamps[raster].Width = 0;
        GiValidation.TryRectangle(0, spec.Stamps[raster].Height, 0, out var rasterRectangle);
        Assert.Equal(rasterRectangle, Only(spec).Message);
    }

    [Fact]
    public void LayerRulesUseTheValidationMessagesAndTheStackOrder()
    {
        var sum = Array.FindIndex(Standard().Layers, l => l.Kind == GiLayerKind.Sum);
        var spec = Standard();
        spec.Layers[sum].FirstWeight = 40000;
        GiValidation.TryDerivedLayer(40000, spec.Layers[sum].SecondWeight, spec.Layers[sum].Shift, out var weights);
        var issue = Only(spec);
        Assert.Equal((GiProfileSection.Layer, sum, weights), (issue.Section, issue.Index, issue.Message));

        spec = Standard();
        spec.Layers[sum].Shift = 16;
        GiValidation.TryDerivedLayer(spec.Layers[sum].FirstWeight, spec.Layers[sum].SecondWeight, 16, out var shift);
        Assert.Equal(shift, Only(spec).Message);

        spec = Standard();
        spec.Layers[sum].First = (byte)(sum + 1);
        issue = Only(spec);
        Assert.Equal((GiProfileSection.Layer, sum, GiProfileCheck.ForwardReference), (issue.Section, issue.Index, issue.Message));

        spec = Standard();
        spec.Layers[sum].Second = (byte)sum;
        Assert.Equal(GiProfileCheck.ForwardReference, Only(spec).Message);

        var max = Array.FindIndex(Standard().Layers, l => l.Kind == GiLayerKind.Max);
        spec = Standard();
        spec.Layers[max].First = (byte)(max + 2);
        Assert.Equal(GiProfileCheck.ForwardReference, Only(spec).Message);

        spec = Standard();
        spec.Layers[max].Kind = GiLayerKind.Mask;
        spec.Layers[max].MaskLow = 10;
        spec.Layers[max].MaskHigh = 5;
        Assert.Equal(GiProfileCheck.EmptyMask, Only(spec).Message);

        spec = Standard();
        spec.Layers[0].PhaseBits = 99;
        issue = Only(spec);
        Assert.Equal(GiProfileCheck.PrecisionMessage, issue.Message);
        Assert.Matches(@"^Layer phase bits must be \d+ through \d+ and turn bits \d+ through \d+\.$", issue.Message);
    }

    [Fact]
    public void BindingIssuesCarryTheBindingTableText()
    {
        var spec = Standard();
        spec.Bindings = new[] { new GiBindingSpec { Kind = GiBindingKind.Area, Layer = 3 } };
        var issue = Only(spec);
        Assert.Equal(GiProfileSection.Binding, issue.Section);
        Assert.Equal(GiBindingTable.Describe(GiBindingStatus.MissingOutput), issue.Message);

        spec.Bindings[0].Layer = 77;
        Assert.Equal(GiBindingTable.Describe(GiBindingStatus.LayerOutOfRange), Only(spec).Message);
    }

    [Fact]
    public void IssuesFormatWithTheirLocation()
    {
        Assert.Equal("Layer 3: x", new GiProfileIssue(GiProfileSection.Layer, 3, "x").ToString());
        Assert.Equal("y", new GiProfileIssue(GiProfileSection.Profile, 0, "y").ToString());
        var issues = new List<GiProfileIssue> { new GiProfileIssue(GiProfileSection.Stamp, 2, "z") };
        Assert.True(GiProfileCheck.Has(issues, GiProfileSection.Stamp, 2));
        Assert.False(GiProfileCheck.Has(issues, GiProfileSection.Stamp, 3));
    }

    [Fact]
    public void ACleanCheckIsTheSameAsTheWorldBuilderAcceptingTheProfile()
    {
        var rng = new GiRandom(7);
        var accepted = 0;
        var rejected = 0;
        for (var round = 0; round < 600; round++)
        {
            var spec = Standard();
            for (var edits = 1 + rng.Next(3); edits > 0; edits--) Mutate(spec, ref rng);

            var issues = GiProfileCheck.Check(spec).Where(i => i.Section != GiProfileSection.Binding).ToList();
            var built = GiWorldBuilder.Validate(spec.Grids, spec.Layers, spec.Stamps, spec.Cells) == GiBuildStatus.Built;
            Assert.True(built == (issues.Count == 0), $"round {round}: builder {built}, issues [{string.Join("; ", issues)}]");
            if (built) accepted++;
            else rejected++;
        }

        Assert.True(accepted > 50);
        Assert.True(rejected > 50);
    }

    private static void Mutate(GiProfileSpec spec, ref GiRandom rng)
    {
        switch (rng.Next(12))
        {
            case 0:
                spec.Grids[rng.Next(spec.Grids.Length)].Power = rng.Next(20);
                break;
            case 1:
                spec.Grids[rng.Next(spec.Grids.Length)].Size = rng.Next(3) == 0 ? -1f : 16f + rng.Next(80);
                break;
            case 2:
                spec.Layers[rng.Next(spec.Layers.Length)].PhaseBits = (byte)rng.Next(14);
                break;
            case 3:
                spec.Layers[rng.Next(spec.Layers.Length)].TurnBits = (byte)rng.Next(20);
                break;
            case 4:
                spec.Layers[rng.Next(spec.Layers.Length)].First = (byte)rng.Next(9);
                break;
            case 5:
                spec.Layers[rng.Next(spec.Layers.Length)].Second = (byte)(rng.Next(4) == 0 ? GiLayerSpec.NoLayer : rng.Next(9));
                break;
            case 6:
                spec.Layers[rng.Next(spec.Layers.Length)].FirstWeight = rng.Next(80000) - 40000;
                break;
            case 7:
                spec.Layers[rng.Next(spec.Layers.Length)].Shift = rng.Next(20);
                break;
            case 8:
                spec.Stamps[rng.Next(spec.Stamps.Length)].Width = (ushort)rng.Next(300);
                break;
            case 9:
                spec.Stamps[rng.Next(spec.Stamps.Length)].Radius = (ushort)rng.Next(140);
                break;
            case 10:
                spec.Stamps[rng.Next(spec.Stamps.Length)].Arc = (ushort)rng.Next(400);
                break;
            default:
                spec.Layers[rng.Next(spec.Layers.Length)].MaskLow = (short)(rng.Next(40) - 20);
                break;
        }
    }
}
