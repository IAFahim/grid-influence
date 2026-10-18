using System;
using System.Threading.Tasks;
using Gi.Entities.Tests.Support;
using Xunit;
using GiWorld = Gi.World;

namespace Gi.Entities.Columns.Tests;

public sealed unsafe class BindingTests
{
    private static GiBindingField Field(ulong hash, int offset, int size)
    {
        return new GiBindingField { TypeHash = hash, Offset = (ushort)offset, Size = (ushort)size };
    }

    private static GiBindingSpec Value(ulong type = 1, int offset = 0)
    {
        return new GiBindingSpec { Kind = GiBindingKind.Value, Layer = 0, Value = Field(type, offset, 2) };
    }

    private static GiBindingStatus[] Check(GiBindingSupport support, params GiBindingSpec[] bindings)
    {
        var statuses = new GiBindingStatus[bindings.Length];
        GiBindingTable.Validate(bindings, 4, support, statuses);
        return statuses;
    }

    [Fact]
    public void AValidBindingPasses()
    {
        Assert.Equal(new[] { GiBindingStatus.Valid }, Check(GiBindingSupport.All, Value()));
        var both = new GiBindingSpec
        {
            Kind = GiBindingKind.ValueGradient, Layer = 3, Value = Field(7, 0, 2), Gradient = Field(7, 8, 8), Coverage = Field(9, 0, 0),
        };
        Assert.Equal(new[] { GiBindingStatus.Valid }, Check(GiBindingSupport.All, both));
    }

    [Fact]
    public void WrongSizesMissingOutputsAndUnexpectedFieldsAreRefused()
    {
        var wrongValue = Value();
        wrongValue.Value.Size = 4;
        Assert.Equal(GiBindingStatus.WrongFieldSize, Check(GiBindingSupport.All, wrongValue)[0]);

        var wrongGradient = new GiBindingSpec { Kind = GiBindingKind.Gradient, Gradient = Field(2, 0, 4) };
        Assert.Equal(GiBindingStatus.WrongFieldSize, Check(GiBindingSupport.All, wrongGradient)[0]);

        var missing = new GiBindingSpec { Kind = GiBindingKind.ValueGradient, Value = Field(1, 0, 2) };
        Assert.Equal(GiBindingStatus.MissingOutput, Check(GiBindingSupport.All, missing)[0]);

        var unexpected = Value();
        unexpected.Gradient = Field(1, 8, 8);
        Assert.Equal(GiBindingStatus.UnexpectedField, Check(GiBindingSupport.All, unexpected)[0]);

        var layer = Value();
        layer.Layer = 4;
        Assert.Equal(GiBindingStatus.LayerOutOfRange, Check(GiBindingSupport.All, layer)[0]);
    }

    [Fact]
    public void ASecondWriterToTheSameFieldIsRefusedAndOverlapsCount()
    {
        var statuses = Check(GiBindingSupport.All, Value(5), Value(5));
        Assert.Equal(GiBindingStatus.Valid, statuses[0]);
        Assert.Equal(GiBindingStatus.DuplicateWriter, statuses[1]);

        var overlapping = Check(GiBindingSupport.All, Value(5), Value(5, 1));
        Assert.Equal(GiBindingStatus.DuplicateWriter, overlapping[1]);

        var adjacent = Check(GiBindingSupport.All, Value(5), Value(5, 2));
        Assert.Equal(new[] { GiBindingStatus.Valid, GiBindingStatus.Valid }, adjacent);

        var otherType = Check(GiBindingSupport.All, Value(5), Value(6));
        Assert.Equal(new[] { GiBindingStatus.Valid, GiBindingStatus.Valid }, otherType);

        var tags = Check(GiBindingSupport.All,
            new GiBindingSpec { Kind = GiBindingKind.Changed, Reach = 8f, Coverage = Field(8, 0, 0) },
            new GiBindingSpec { Kind = GiBindingKind.Changed, Reach = 8f, Coverage = Field(8, 0, 0) });
        Assert.Equal(GiBindingStatus.DuplicateWriter, tags[1]);
    }

    [Fact]
    public void ExtendedKindsNeedAReachAndTheirInputFieldsHaveFixedSizes()
    {
        var area = new GiBindingSpec { Kind = GiBindingKind.Area, Total = Field(2, 0, 8) };
        Assert.Equal(GiBindingStatus.InvalidReach, Check(GiBindingSupport.All, area)[0]);

        area.Reach = float.NaN;
        Assert.Equal(GiBindingStatus.InvalidReach, Check(GiBindingSupport.All, area)[0]);

        area.Reach = 6f;
        Assert.Equal(GiBindingStatus.Valid, Check(GiBindingSupport.All, area)[0]);

        area.Reach = 0f;
        area.ReachField = Field(3, 0, 4);
        Assert.Equal(GiBindingStatus.Valid, Check(GiBindingSupport.All, area)[0]);

        area.ReachField = Field(3, 0, 8);
        Assert.Equal(GiBindingStatus.WrongFieldSize, Check(GiBindingSupport.All, area)[0]);

        var value = Value();
        value.ReachField = Field(3, 0, 4);
        Assert.Equal(GiBindingStatus.UnexpectedField, Check(GiBindingSupport.All, value)[0]);

        var nearest = new GiBindingSpec
        {
            Kind = GiBindingKind.Nearest, Reach = 5f, Value = Field(1, 0, 2), Position = Field(1, 8, 8), ThresholdField = Field(4, 0, 2),
        };
        Assert.Equal(GiBindingStatus.Valid, Check(GiBindingSupport.All, nearest)[0]);

        nearest.ThresholdField = Field(4, 0, 4);
        Assert.Equal(GiBindingStatus.WrongFieldSize, Check(GiBindingSupport.All, nearest)[0]);

        area.ReachField = Field(3, 0, 4);
        area.ThresholdField = Field(4, 0, 2);
        Assert.Equal(GiBindingStatus.UnexpectedField, Check(GiBindingSupport.All, area)[0]);
    }

    [Fact]
    public void ChangedTakesOneConstantReachForTheWholeBinding()
    {
        var changed = new GiBindingSpec { Kind = GiBindingKind.Changed, Reach = 8f, Coverage = Field(8, 0, 0) };
        Assert.Equal(GiBindingStatus.Valid, Check(GiBindingSupport.All, changed)[0]);

        changed.ReachField = Field(3, 0, 4);
        Assert.Equal(GiBindingStatus.UnexpectedField, Check(GiBindingSupport.All, changed)[0]);
    }

    [Fact]
    public void EachKindNamesTheBuffersItNeeds()
    {
        GiSenseNeeds Of(GiBindingKind kind, byte flags = 0)
        {
            return GiBindingTable.NeedsOf(new GiBindingSpec { Kind = kind, Flags = flags });
        }

        Assert.Equal(GiSenseNeeds.Value, Of(GiBindingKind.Value));
        Assert.Equal(GiSenseNeeds.Vector, Of(GiBindingKind.Gradient));
        Assert.Equal(GiSenseNeeds.Value | GiSenseNeeds.Vector | GiSenseNeeds.Exclude, Of(GiBindingKind.ValueGradient, GiBindingSpec.ExcludeSelf));
        Assert.Equal(GiSenseNeeds.Total | GiSenseNeeds.Reach, Of(GiBindingKind.Area));
        Assert.Equal(GiSenseNeeds.Total | GiSenseNeeds.Reach | GiSenseNeeds.Exclude, Of(GiBindingKind.Area, GiBindingSpec.ExcludeSelf));
        Assert.Equal(GiSenseNeeds.Value | GiSenseNeeds.Vector | GiSenseNeeds.Reach, Of(GiBindingKind.Max));
        Assert.Equal(GiSenseNeeds.Value | GiSenseNeeds.Vector | GiSenseNeeds.Reach | GiSenseNeeds.Threshold, Of(GiBindingKind.Nearest));
        Assert.Equal(GiSenseNeeds.None, Of(GiBindingKind.Changed));
        Assert.True(GiBindingTable.ReachPerRow(GiBindingKind.Area));
        Assert.False(GiBindingTable.ReachPerRow(GiBindingKind.Changed));
    }

    [Fact]
    public void ExcludingOnATransientLayerIsRefusedButPersistentAndDerivedLayersAreFine()
    {
        var layers = new[]
        {
            new GiLayerSpec { Kind = GiLayerKind.Persistent },
            new GiLayerSpec { Kind = GiLayerKind.Transient },
            new GiLayerSpec { Kind = GiLayerKind.Sum },
        };
        var persistent = Value();
        persistent.Flags = GiBindingSpec.ExcludeSelf;
        var transient = Value(2);
        transient.Layer = 1;
        transient.Flags = GiBindingSpec.ExcludeSelf;
        var derived = Value(3);
        derived.Layer = 2;
        derived.Flags = GiBindingSpec.ExcludeSelf;
        var plain = Value(4);
        plain.Layer = 1;

        var bindings = new[] { persistent, transient, derived, plain };
        var statuses = new GiBindingStatus[bindings.Length];
        var invalid = GiBindingTable.Validate(bindings, layers, GiBindingSupport.All, statuses);

        Assert.Equal(1, invalid);
        Assert.Equal(
            new[] { GiBindingStatus.Valid, GiBindingStatus.ExcludeOnTransient, GiBindingStatus.Valid, GiBindingStatus.Valid },
            statuses);
        Assert.False(string.IsNullOrEmpty(GiBindingTable.Describe(GiBindingStatus.ExcludeOnTransient)));
        Assert.False(string.IsNullOrEmpty(GiBindingTable.Describe(GiBindingStatus.InvalidReach)));
    }

    [Fact]
    public void AnEntityIsDueExactlyOncePerPeriod()
    {
        for (var period = 1; period <= 9; period++)
        for (var index = 0; index < 40; index++)
        {
            var due = 0;
            for (var tick = 100; tick < 100 + period; tick++)
                if (GiSensePass.Due(index, period, tick))
                    due++;
            Assert.Equal(1, due);
        }

        Assert.True(GiSensePass.Due(5, 0, 7));
        Assert.Equal(1, GiSensePass.Periods(0));
        Assert.Equal(4, GiSensePass.Periods(4));
        Assert.Equal(9, GiSensePass.SinceTick(10, 0));
        Assert.Equal(7, GiSensePass.SinceTick(10, 3));
    }

    [Fact]
    public void ExclusionAndExtendedKindsFollowTheSupportFlags()
    {
        var exclude = Value();
        exclude.Flags = GiBindingSpec.ExcludeSelf;
        Assert.Equal(GiBindingStatus.Unsupported, Check(GiBindingSupport.Basic, exclude)[0]);
        Assert.Equal(GiBindingStatus.Valid, Check(GiBindingSupport.Basic | GiBindingSupport.Exclusion, exclude)[0]);

        var nearest = new GiBindingSpec { Kind = GiBindingKind.Nearest, Reach = 5f, Value = Field(1, 0, 2), Position = Field(1, 8, 8), Flags = GiBindingSpec.ExcludeSelf };
        Assert.Equal(GiBindingStatus.ExcludeUnavailable, Check(GiBindingSupport.All, nearest)[0]);
        nearest.Flags = 0;
        Assert.Equal(GiBindingStatus.Unsupported, Check(GiBindingSupport.Basic, nearest)[0]);
        Assert.Equal(GiBindingStatus.Valid, Check(GiBindingSupport.All, nearest)[0]);
    }

    [Fact]
    public void EveryStatusHasAStableMessage()
    {
        var seen = new System.Collections.Generic.HashSet<string>();
        foreach (var status in Enum.GetValues<GiBindingStatus>()) Assert.True(seen.Add(GiBindingTable.Describe(status)));
        Assert.Equal("Another binding writes the same output field.", GiBindingTable.Describe(GiBindingStatus.DuplicateWriter));
        Assert.Equal("An output field has the wrong size for what the binding writes into it.", GiBindingTable.Describe(GiBindingStatus.WrongFieldSize));
    }

    [Fact]
    public void BatchSizesRoundDownToWholeWords()
    {
        Assert.Equal(64, GiSensePass.Batch(1));
        Assert.Equal(64, GiSensePass.Batch(100));
        Assert.Equal(4096, GiSensePass.Batch(4096));
        Assert.Equal(4096, GiSensePass.Batch(4100));
        Assert.Equal(1, GiSensePass.Batches(64, 64));
        Assert.Equal(2, GiSensePass.Batches(65, 64));
        Assert.Equal(3, GiSensePass.Words(130));
    }

    [Fact]
    public void ParallelBatchesEqualSerialScalarReadsOverTwoHundredSeeds()
    {
        for (ulong seed = 1; seed <= 200; seed++)
        {
            using var setup = GiScenarioWorld.Create(GiScenarioProfile.Standard());
            var rng = new GiRandom(seed);
            for (var i = 0; i < 30; i++)
                GiWorld.Place(setup.World, setup.Layers[i % 2], 6 + rng.Next(52) + rng.Next(4) * 0.25f, 6 + rng.Next(52), setup.Stamps[rng.Next(6)],
                    (sbyte)(1 + rng.Next(12)));
            GiWorld.Process(setup.World);

            var rows = 200 + rng.Next(400);
            var xs = new float[rows];
            var ys = new float[rows];
            for (var i = 0; i < rows; i++)
            {
                xs[i] = -8 + rng.Next(80) + rng.Next(8) * 0.125f;
                ys[i] = -8 + rng.Next(80) + rng.Next(8) * 0.125f;
            }

            foreach (var kind in new[] { GiBindingKind.Value, GiBindingKind.Gradient, GiBindingKind.ValueGradient })
            foreach (var batch in new[] { 64, 192, 4096 })
            {
                var values = new short[rows];
                var gx = new float[rows];
                var gy = new float[rows];
                var whole = new ulong[GiSensePass.Words(rows)];
                var layer = setup.Layers[3];
                fixed (float* x = xs, y = ys, px = gx, py = gy)
                fixed (short* v = values)
                fixed (ulong* w = whole)
                {
                    var xp = x;
                    var yp = y;
                    var vp = kind == GiBindingKind.Gradient ? null : v;
                    var gxp = kind == GiBindingKind.Value ? null : px;
                    var gyp = kind == GiBindingKind.Value ? null : py;
                    var wp = w;
                    var xa = (nint)xp;
                    var ya = (nint)yp;
                    var va = (nint)vp;
                    var gxa = (nint)gxp;
                    var gya = (nint)gyp;
                    var wa = (nint)wp;
                    var world = setup.World;
                    Parallel.For(0, GiSensePass.Batches(rows, batch), b =>
                        GiSensePass.ReadBatch(kind, world, layer, (float*)xa, (float*)ya, rows, batch, b, (short*)va, (float*)gxa, (float*)gya,
                            (ulong*)wa));
                }

                for (var i = 0; i < rows; i++)
                {
                    var covered = (whole[i >> 6] >> (i & 63) & 1ul) != 0ul;
                    if (kind == GiBindingKind.Value)
                    {
                        var ok = GiWorld.TrySense(setup.World, layer, xs[i], ys[i], out var expected);
                        Assert.True(ok == covered, $"seed {seed} {kind} batch {batch} row {i} ({xs[i]},{ys[i]}) scalar={ok} bit={covered}");
                        Assert.Equal(expected, values[i]);
                        continue;
                    }

                    var gradientOk = GiWorld.TrySenseGradient(setup.World, layer, xs[i], ys[i], out var ex, out var ey);
                    Assert.True(gradientOk == covered, $"seed {seed} {kind} batch {batch} row {i} ({xs[i]},{ys[i]}) scalar={gradientOk} bit={covered}");
                    Assert.Equal(BitConverter.SingleToInt32Bits(ex), BitConverter.SingleToInt32Bits(gx[i]));
                    Assert.Equal(BitConverter.SingleToInt32Bits(ey), BitConverter.SingleToInt32Bits(gy[i]));
                    if (kind == GiBindingKind.ValueGradient && covered)
                    {
                        GiWorld.TrySense(setup.World, layer, xs[i], ys[i], out var expected);
                        Assert.Equal(expected, values[i]);
                    }
                }
            }
        }
    }
}
