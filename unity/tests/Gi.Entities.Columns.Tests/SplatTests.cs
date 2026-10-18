using System;
using Gi.Entities.Tests.Support;
using Xunit;
using GiWorld = Gi.World;

namespace Gi.Entities.Columns.Tests;

public sealed unsafe class SplatTests
{
    [Fact]
    public void ColumnsPipelineWithTheSplatPassMatchesTheReferenceOverTwoHundredSeeds()
    {
        for (ulong seed = 1; seed <= 200; seed++)
        {
            var scenario = GiScenarioGenerator.Create(seed, GiScenarioOptions.Standard);
            var expected = GiReferenceExecutor.Run(scenario);
            var actual = GiColumnsExecutor.Run(scenario);
            Assert.True(expected.Compare(actual).Length == 0, $"seed {seed}: {expected.Compare(actual)}");
        }
    }

    [Fact]
    public void TheUniformOverloadAndThePerEntryOverloadMakeTheSameField()
    {
        using var uniform = GiScenarioWorld.Create(GiScenarioProfile.Standard());
        using var mixed = GiScenarioWorld.Create(GiScenarioProfile.Standard());
        var tables = GiColumnsExecutor.TablesOf(uniform, GiScenarioProfile.Standard());
        var rng = new GiRandom(5);
        var xs = new float[64];
        var ys = new float[64];
        for (var i = 0; i < xs.Length; i++)
        {
            xs[i] = 4 + rng.Next(56);
            ys[i] = 4 + rng.Next(56);
        }

        var stamps = new byte[64];
        var gains = new sbyte[64];
        Array.Fill(stamps, uniform.Stamps[4]);
        Array.Fill(gains, (sbyte)3);
        var starts = new int[GiSplatPass.Layers];
        var counts = new int[GiSplatPass.Layers];
        counts[2] = 64;

        fixed (float* x = xs, y = ys)
        fixed (byte* s = stamps)
        fixed (sbyte* g = gains)
        fixed (int* st = starts, ct = counts)
        {
            Assert.True(GiSplatPass.Uniform(s, g, 64));
            Assert.Equal(64, GiSplatPass.Apply(uniform.World, tables, st, ct, x, y, s, g));
        }

        var mixedStamps = new byte[64];
        for (var i = 0; i < mixedStamps.Length; i++) mixedStamps[i] = mixed.Stamps[4];
        Assert.Equal(64, GiWorld.Splat(mixed.World, mixed.Layers[2], xs, ys, mixedStamps, gains));
        GiWorld.Process(uniform.World);
        GiWorld.Process(mixed.World);
        Assert.Equal(GiFieldHash.OfLayer(mixed.World, mixed.Layers[2]), GiFieldHash.OfLayer(uniform.World, uniform.Layers[2]));
    }

    [Fact]
    public void UniformIsDetectedOnlyWhenEveryStampAndGainAgree()
    {
        var stamps = new byte[] { 3, 3, 3, 3 };
        var gains = new sbyte[] { 2, 2, 2, 2 };
        fixed (byte* s = stamps)
        fixed (sbyte* g = gains)
        {
            Assert.True(GiSplatPass.Uniform(s, g, 4));
            Assert.True(GiSplatPass.Uniform(s, g, 1));
            gains[3] = 1;
            Assert.False(GiSplatPass.Uniform(s, g, 4));
            gains[3] = 2;
            stamps[2] = 4;
            Assert.False(GiSplatPass.Uniform(s, g, 4));
        }
    }

    [Fact]
    public void CheckClassifiesLayersByKeyAndResidency()
    {
        using var setup = GiScenarioWorld.Create(GiScenarioProfile.Standard());
        var tables = GiColumnsExecutor.TablesOf(setup, GiScenarioProfile.Standard());
        Assert.Equal(GiFaultCode.None, GiSplatPass.Check(tables, new GiLayerKey(2), new GiStampKey(0), out var stamp));
        Assert.Equal(setup.Stamps[0], stamp);
        Assert.Equal(GiFaultCode.None, GiSplatPass.Check(tables, new GiLayerKey(6), new GiStampKey(1), out _));
        Assert.Equal(GiFaultCode.WrongResidency, GiSplatPass.Check(tables, new GiLayerKey(0), new GiStampKey(0), out _));
        Assert.Equal(GiFaultCode.WrongResidency, GiSplatPass.Check(tables, new GiLayerKey(3), new GiStampKey(0), out _));
        Assert.Equal(GiFaultCode.InvalidLayer, GiSplatPass.Check(tables, new GiLayerKey(40), new GiStampKey(0), out _));
        Assert.Equal(GiFaultCode.InvalidStamp, GiSplatPass.Check(tables, new GiLayerKey(2), new GiStampKey(99), out _));
        Assert.True(tables.IsTransient(new GiLayerKey(2)));
        Assert.False(tables.IsTransient(new GiLayerKey(1)));
        Assert.False(tables.IsTransient(new GiLayerKey(200)));
    }

    [Fact]
    public void ABuiltWorldRecordsWhichLayersAreTransient()
    {
        var spec = GiScenarioProfile.Standard().ToSpec();
        var registry = new GiStampRegistry();
        Assert.Equal(GiBuildStatus.Built,
            GiWorldBuilder.TryBuild(spec.Grids, spec.Layers, spec.Stamps, spec.Cells, registry, out var world, out var tables));
        Assert.Equal((1u << 2) | (1u << 6), tables.TransientMask);
        GiWorldBuilder.Release(world, tables, registry);
    }
}
