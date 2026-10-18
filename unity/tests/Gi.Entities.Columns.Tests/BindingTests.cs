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
        var statuses = Check(GiBindingSupport.All, Value(5, 0), Value(5, 0));
        Assert.Equal(GiBindingStatus.Valid, statuses[0]);
        Assert.Equal(GiBindingStatus.DuplicateWriter, statuses[1]);

        var overlapping = Check(GiBindingSupport.All, Value(5, 0), Value(5, 1));
        Assert.Equal(GiBindingStatus.DuplicateWriter, overlapping[1]);

        var adjacent = Check(GiBindingSupport.All, Value(5, 0), Value(5, 2));
        Assert.Equal(new[] { GiBindingStatus.Valid, GiBindingStatus.Valid }, adjacent);

        var otherType = Check(GiBindingSupport.All, Value(5, 0), Value(6, 0));
        Assert.Equal(new[] { GiBindingStatus.Valid, GiBindingStatus.Valid }, otherType);

        var tags = Check(GiBindingSupport.All,
            new GiBindingSpec { Kind = GiBindingKind.Changed, Coverage = Field(8, 0, 0) },
            new GiBindingSpec { Kind = GiBindingKind.Changed, Coverage = Field(8, 0, 0) });
        Assert.Equal(GiBindingStatus.DuplicateWriter, tags[1]);
    }

    [Fact]
    public void ExclusionAndExtendedKindsFollowTheSupportFlags()
    {
        var exclude = Value();
        exclude.Flags = GiBindingSpec.ExcludeSelf;
        Assert.Equal(GiBindingStatus.Unsupported, Check(GiBindingSupport.Basic, exclude)[0]);
        Assert.Equal(GiBindingStatus.Valid, Check(GiBindingSupport.Basic | GiBindingSupport.Exclusion, exclude)[0]);

        var nearest = new GiBindingSpec { Kind = GiBindingKind.Nearest, Value = Field(1, 0, 2), Position = Field(1, 8, 8), Flags = GiBindingSpec.ExcludeSelf };
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
