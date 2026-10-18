using System;
using System.Collections.Generic;
using Gi.Entities.Tests.Support;
using Xunit;
using GiGrid = Gi.Grid;
using GiLayer = Gi.Layer;
using GiStamp = Gi.Stamp;
using GiWorld = Gi.World;

namespace Gi.Entities.Columns.Tests;

public sealed class FrameColumnsTests
{
    private static GiProfileSpec Standard() => GiScenarioProfile.Standard().ToSpec();

    private static GiBuildStatus Build(GiProfileSpec spec, GiStampRegistry registry, out byte world, out GiTables tables) =>
        GiWorldBuilder.TryBuild(spec.Grids, spec.Layers, spec.Stamps, spec.Cells, registry, out world, out tables);

    private static int DefinedStamps()
    {
        var count = 0;
        for (var stamp = 1; stamp < 256; stamp++)
            if (GiStamp.TryGetSpec((byte)stamp, out _))
                count++;
        return count;
    }

    [Fact]
    public void ScenarioEnumsMapOneToOneOntoTheProductionEnums()
    {
        foreach (var name in Enum.GetNames<GiScenarioLayerKind>())
            Assert.Equal(name, ((GiLayerKind)(byte)Enum.Parse<GiScenarioLayerKind>(name)).ToString());
        foreach (var name in Enum.GetNames<GiScenarioShape>())
            Assert.Equal(name, ((GiStampShape)(byte)Enum.Parse<GiScenarioShape>(name)).ToString());
    }

    [Fact]
    public void KeysCompareByIndex()
    {
        Assert.Equal(new GiLayerKey(3), new GiLayerKey(3));
        Assert.NotEqual(new GiLayerKey(3), new GiLayerKey(4));
        Assert.Equal(new GiStampKey(9), new GiStampKey(9));
        Assert.NotEqual(new GiStampKey(9), new GiStampKey(10));
        Assert.Equal(7, new GiLayerKey(7).GetHashCode());
    }

    [Fact]
    public void TablesAreTotalOverTheirKeys()
    {
        var tables = default(GiTables);
        tables.AddGrid(4);
        tables.AddLayer(10);
        tables.AddLayer(11);
        tables.AddStamp(200);

        Assert.True(tables.TryGrid(0, out var grid));
        Assert.Equal(4, grid);
        Assert.False(tables.TryGrid(1, out _));
        Assert.False(tables.TryGrid(-1, out _));
        Assert.True(tables.TryLayer(new GiLayerKey(1), out var layer));
        Assert.Equal(11, layer);
        Assert.False(tables.TryLayer(new GiLayerKey(2), out _));
        Assert.True(tables.TryStamp(new GiStampKey(0), out var stamp));
        Assert.Equal(200, stamp);
        Assert.False(tables.TryStamp(new GiStampKey(255), out _));
    }

    [Fact]
    public void FaultsRaiseOnceAndRememberTheFirstTick()
    {
        var faults = default(GiFaults);
        Assert.False(faults.Has(GiFaultKind.Profile));
        Assert.Equal(-1, faults.FirstTick(GiFaultKind.Profile));

        Assert.True(faults.Raise(GiFaultKind.Profile, 12));
        Assert.False(faults.Raise(GiFaultKind.Profile, 99));
        Assert.True(faults.Raise(GiFaultKind.Reclaimed, 5));
        Assert.Equal(12, faults.FirstTick(GiFaultKind.Profile));
        Assert.Equal(5, faults.FirstTick(GiFaultKind.Reclaimed));
        Assert.True(faults.Has(GiFaultKind.Reclaimed));

        faults.Clear(GiFaultKind.Profile);
        Assert.False(faults.Has(GiFaultKind.Profile));
        Assert.True(faults.Has(GiFaultKind.Reclaimed));
        Assert.True(faults.Raise(GiFaultKind.Profile, 40));
        Assert.Equal(40, faults.FirstTick(GiFaultKind.Profile));
    }

    [Fact]
    public void ContentHashIsPinnedAndOrderSensitive()
    {
        var a = GiContentHash.Start();
        a.Add((byte)1);
        a.Add(2);
        a.Add(3ul);
        a.Add(1.5f);
        var b = GiContentHash.Start();
        b.Add(2);
        b.Add((byte)1);
        b.Add(3ul);
        b.Add(1.5f);

        Assert.Equal(a.Finish(), a.Finish());
        Assert.NotEqual(a.Finish(), b.Finish());
        Assert.Equal(0xF4FA0F1DD8A27625ul, a.Finish());
    }

    [Fact]
    public void ProfileHashChangesWithEveryFieldAndIgnoresNothing()
    {
        var baseline = Standard().Hash;
        Assert.Equal(baseline, Standard().Hash);

        var mutations = new List<Action<GiProfileSpec>>
        {
            spec => spec.Plane.Axes = GiPlaneAxes.Xy,
            spec => spec.Plane.OriginX = 1f,
            spec => spec.Grids[0].Power++,
            spec => spec.Grids[1].Size += 1f,
            spec => spec.Layers[0].PhaseBits++,
            spec => spec.Layers[3].Shift++,
            spec => spec.Layers[3].SecondWeight++,
            spec => spec.Layers[6].Kind = GiLayerKind.Persistent,
            spec => spec.Stamps[0].Value++,
            spec => spec.Stamps[2].Arc++,
            spec => spec.Stamps[3].CellCount--,
            spec => spec.Cells[5]++,
            spec => spec.Bindings = new[] { new GiBindingSpec { Kind = GiBindingKind.Area } },
        };
        var seen = new HashSet<ulong> { baseline };
        foreach (var mutate in mutations)
        {
            var spec = Standard();
            mutate(spec);
            Assert.True(seen.Add(spec.Hash));
        }
    }

    [Fact]
    public void BindingFieldsAreHashed()
    {
        var spec = Standard();
        spec.Bindings = new[] { new GiBindingSpec { Kind = GiBindingKind.Value, Layer = 1 } };
        var before = spec.Hash;
        spec.Bindings[0].Value = new GiBindingField { TypeHash = 5, Offset = 2, Size = 2 };
        Assert.NotEqual(before, spec.Hash);
        before = spec.Hash;
        spec.Bindings[0].Flags = GiBindingSpec.ExcludeSelf;
        Assert.NotEqual(before, spec.Hash);
    }

    [Fact]
    public void RegistrySharesEqualStampsAndFreesAfterTheLastRelease()
    {
        var registry = new GiStampRegistry();
        var spec = Standard();
        var before = DefinedStamps();

        Assert.True(registry.TryAcquire(spec.Stamps[0], spec.Cells, out var first));
        Assert.True(registry.TryAcquire(spec.Stamps[0], spec.Cells, out var second));
        Assert.True(registry.TryAcquire(spec.Stamps[1], spec.Cells, out var other));
        Assert.Equal(first, second);
        Assert.NotEqual(first, other);
        Assert.Equal(2, registry.LiveCount);
        Assert.Equal(before + 2, DefinedStamps());

        registry.Release(first);
        Assert.Equal(2, registry.LiveCount);
        registry.Release(second);
        Assert.Equal(1, registry.LiveCount);
        Assert.Equal(before + 1, DefinedStamps());
        registry.Release(other);
        Assert.Equal(0, registry.LiveCount);
        Assert.Equal(before, DefinedStamps());
        registry.Release(other);
        Assert.Equal(before, DefinedStamps());
    }

    [Fact]
    public void RegistryRefusesRatherThanThrowsWhenTheCatalogIsFull()
    {
        var registry = new GiStampRegistry();
        var held = new List<byte>();
        try
        {
            var spec = new GiStampSpec { Shape = GiStampShape.Box, Width = 1, Height = 1 };
            var added = 0;
            for (var value = -128; value < 128 && added < 300; value++)
            {
                spec.Value = (sbyte)value;
                if (!registry.TryAcquire(spec, ReadOnlySpan<sbyte>.Empty, out var id)) break;
                held.Add(id);
                added++;
            }

            for (var width = 2; width < 40; width++)
            {
                spec.Width = (ushort)width;
                spec.Value = 5;
                if (registry.TryAcquire(spec, ReadOnlySpan<sbyte>.Empty, out var id)) held.Add(id);
                else break;
            }

            Assert.False(registry.TryAcquire(new GiStampSpec { Shape = GiStampShape.Disk, Radius = 3, Arc = 360, Value = 77 },
                ReadOnlySpan<sbyte>.Empty, out _));
        }
        finally
        {
            foreach (var id in held) registry.Release(id);
        }

        Assert.Equal(0, registry.LiveCount);
    }

    [Fact]
    public void BuilderMakesTheSameFieldAsTheReferenceConstruction()
    {
        var profile = GiScenarioProfile.Standard();
        var registry = new GiStampRegistry();
        Assert.Equal(GiBuildStatus.Built, Build(profile.ToSpec(), registry, out var world, out var tables));
        using var reference = GiScenarioWorld.Create(profile);

        Assert.Equal(profile.Grids.Length, tables.GridCount);
        Assert.Equal(profile.Layers.Length, tables.LayerCount);
        Assert.Equal(profile.Stamps.Length, tables.StampCount);
        Assert.Equal(profile.Layers.Length, GiLayer.Count(world));
        Assert.Equal(profile.Grids.Length, GiGrid.Count(world));

        for (var i = 0; i < 12; i++)
        {
            var x = 10f + i * 3.25f;
            var y = 12f + i * 2.5f;
            var stamp = i % profile.Stamps.Length;
            var layer = (i % 3) switch { 0 => 0, 1 => 1, _ => 5 };
            tables.TryLayer(new GiLayerKey((byte)layer), out var layerId);
            tables.TryStamp(new GiStampKey((byte)stamp), out var stampId);
            GiWorld.Place(world, layerId, x, y, stampId, (sbyte)(4 + i % 5));
            GiWorld.Place(reference.World, reference.Layers[layer], x, y, reference.Stamps[stamp], (sbyte)(4 + i % 5));
        }

        GiWorld.Process(world);
        GiWorld.Process(reference.World);
        for (var l = 0; l < profile.Layers.Length; l++)
        {
            tables.TryLayer(new GiLayerKey((byte)l), out var layerId);
            Assert.Equal(GiFieldHash.OfLayer(reference.World, reference.Layers[l]), GiFieldHash.OfLayer(world, layerId));
        }

        GiWorldBuilder.Release(world, tables, registry);
        Assert.Equal(0, registry.LiveCount);
    }

    [Fact]
    public void BuildAndFreeOneThousandTimesLeavesNothingBehind()
    {
        var registry = new GiStampRegistry();
        var spec = Standard();
        var worlds = GiOrphans.LiveWorlds();
        var stamps = DefinedStamps();
        for (var i = 0; i < 1000; i++)
        {
            Assert.Equal(GiBuildStatus.Built, Build(spec, registry, out var world, out var tables));
            GiWorldBuilder.Release(world, tables, registry);
        }

        Assert.Equal(worlds, GiOrphans.LiveWorlds());
        Assert.Equal(stamps, DefinedStamps());
        Assert.Equal(0, registry.LiveCount);
        Assert.Equal(0, registry.DeferredCount);
    }

    [Fact]
    public void TwoWorldsOnOneProfileShareStampsUntilTheLastIsFreed()
    {
        var registry = new GiStampRegistry();
        var spec = Standard();
        var stamps = DefinedStamps();
        Assert.Equal(GiBuildStatus.Built, Build(spec, registry, out var first, out var firstTables));
        Assert.Equal(GiBuildStatus.Built, Build(spec, registry, out var second, out var secondTables));
        Assert.Equal(stamps + spec.Stamps.Length, DefinedStamps());
        for (var i = 0; i < spec.Stamps.Length; i++)
        {
            firstTables.TryStamp(new GiStampKey((byte)i), out var a);
            secondTables.TryStamp(new GiStampKey((byte)i), out var b);
            Assert.Equal(a, b);
        }

        GiWorldBuilder.Release(first, firstTables, registry);
        Assert.Equal(stamps + spec.Stamps.Length, DefinedStamps());
        GiWorldBuilder.Release(second, secondTables, registry);
        Assert.Equal(stamps, DefinedStamps());
    }

    [Fact]
    public void InvalidProfilesAreRefusedWithoutThrowingOrLeaking()
    {
        var registry = new GiStampRegistry();
        var worlds = GiOrphans.LiveWorlds();
        var stamps = DefinedStamps();

        var tooManyLayers = Standard();
        tooManyLayers.Layers = new GiLayerSpec[33];
        Assert.Equal(GiBuildStatus.TooManyLayers, Build(tooManyLayers, registry, out _, out _));

        var forwardReference = Standard();
        forwardReference.Layers[3].First = 5;
        Assert.Equal(GiBuildStatus.InvalidLayer, Build(forwardReference, registry, out _, out _));

        var badMask = Standard();
        badMask.Layers[3] = new GiLayerSpec { Kind = GiLayerKind.Mask, First = 0, Second = 1, MaskLow = 5, MaskHigh = -5 };
        Assert.Equal(GiBuildStatus.InvalidLayer, Build(badMask, registry, out _, out _));

        var badPrecision = Standard();
        badPrecision.Layers[0].PhaseBits = 9;
        Assert.Equal(GiBuildStatus.InvalidLayer, Build(badPrecision, registry, out _, out _));

        var badPower = Standard();
        badPower.Grids[0].Power = 4;
        Assert.Equal(GiBuildStatus.InvalidGrid, Build(badPower, registry, out _, out _));

        var badSize = Standard();
        badSize.Grids[0].Size = float.NaN;
        Assert.Equal(GiBuildStatus.InvalidGrid, Build(badSize, registry, out _, out _));

        var badRadius = Standard();
        badRadius.Stamps[2].Radius = 0;
        Assert.Equal(GiBuildStatus.InvalidStamp, Build(badRadius, registry, out _, out _));

        var badRaster = Standard();
        badRaster.Stamps[3].CellCount = 3;
        Assert.Equal(GiBuildStatus.InvalidStamp, Build(badRaster, registry, out _, out _));

        var noGrids = Standard();
        noGrids.Grids = Array.Empty<GiGridSpec>();
        Assert.Equal(GiBuildStatus.NoGrids, Build(noGrids, registry, out _, out _));

        var noLayers = Standard();
        noLayers.Layers = Array.Empty<GiLayerSpec>();
        Assert.Equal(GiBuildStatus.NoLayers, Build(noLayers, registry, out _, out _));

        var tooManyGrids = Standard();
        tooManyGrids.Grids = new GiGridSpec[33];
        Assert.Equal(GiBuildStatus.TooManyGrids, Build(tooManyGrids, registry, out _, out _));

        var tooManyStamps = Standard();
        tooManyStamps.Stamps = new GiStampSpec[256];
        Assert.Equal(GiBuildStatus.TooManyStamps, Build(tooManyStamps, registry, out _, out _));

        Assert.Equal(worlds, GiOrphans.LiveWorlds());
        Assert.Equal(stamps, DefinedStamps());
        Assert.Equal(0, registry.LiveCount);
    }

    [Fact]
    public void TheWorldLimitIsAStatusNotAnException()
    {
        var registry = new GiStampRegistry();
        var held = new List<byte>();
        try
        {
            while (GiOrphans.LiveWorlds() < 255) held.Add(GiWorld.New());
            Assert.Equal(GiBuildStatus.WorldLimit, Build(Standard(), registry, out _, out _));
        }
        finally
        {
            foreach (var world in held) GiWorld.Free(world);
        }

        Assert.Equal(0, registry.LiveCount);
    }

    [Fact]
    public void ReclaimFreesWorldsAndStampsNobodyOwns()
    {
        var registry = new GiStampRegistry();
        Assert.Equal(GiBuildStatus.Built, Build(Standard(), registry, out _, out _));
        var strayWorld = GiWorld.New();
        var strayStamp = GiStamp.Box(2, 2, 10);
        GiWorld.Place(strayWorld, GiLayer.New(strayWorld), 1f, 1f, strayStamp, 1, out _);

        var report = GiOrphans.Reclaim(registry);
        Assert.True(report.Found);
        Assert.True(report.Worlds >= 2);
        Assert.True(report.Stamps >= Standard().Stamps.Length + 1);
        Assert.True(report.Torn);
        Assert.Equal(0, GiOrphans.LiveWorlds());
        Assert.Equal(0, DefinedStamps());
        Assert.Equal(0, registry.LiveCount);

        Assert.False(GiOrphans.Reclaim(registry).Found);
    }

    [Theory]
    [InlineData(GiPlaneAxes.Xz)]
    [InlineData(GiPlaneAxes.Xy)]
    public void ProjectionAndEmbeddingRoundTrip(GiPlaneAxes axes)
    {
        var plane = new GiPlane(axes, 10f, 20f);
        plane.Project(12f, 7f, 31f, out var px, out var py);
        Assert.Equal(2f, px);
        Assert.Equal(axes == GiPlaneAxes.Xz ? 11f : -13f, py);

        plane.EmbedPoint(px, py, out var x, out var y, out var z);
        Assert.Equal(12f, x);
        Assert.Equal(axes == GiPlaneAxes.Xz ? 0f : 7f, y);
        Assert.Equal(axes == GiPlaneAxes.Xz ? 31f : 0f, z);

        plane.EmbedVector(3f, -4f, out x, out y, out z);
        Assert.Equal(3f, x);
        Assert.Equal(axes == GiPlaneAxes.Xz ? 0f : -4f, y);
        Assert.Equal(axes == GiPlaneAxes.Xz ? -4f : 0f, z);
    }

    [Fact]
    public void NonFiniteProjectionPassesThroughUnchanged()
    {
        var plane = new GiPlane(GiPlaneAxes.Xz, 0f, 0f);
        plane.Project(float.NaN, 1f, float.PositiveInfinity, out var px, out var py);
        Assert.True(float.IsNaN(px));
        Assert.True(float.IsPositiveInfinity(py));

        var registry = new GiStampRegistry();
        Assert.Equal(GiBuildStatus.Built, Build(Standard(), registry, out var world, out var tables));
        tables.TryLayer(new GiLayerKey(0), out var layer);
        tables.TryStamp(new GiStampKey(0), out var stamp);
        GiWorld.Place(world, layer, px, py, stamp, 4, out var status);
        Assert.Equal(GiWorld.WriteStatus.NonFinite, status);
        GiWorldBuilder.Release(world, tables, registry);
    }

    public static IEnumerable<object[]> Directions()
    {
        yield return new object[] { GiPlaneAxes.Xz, 1, 0 };
        yield return new object[] { GiPlaneAxes.Xz, -1, 0 };
        yield return new object[] { GiPlaneAxes.Xz, 0, 1 };
        yield return new object[] { GiPlaneAxes.Xz, 0, -1 };
        yield return new object[] { GiPlaneAxes.Xy, 1, 0 };
        yield return new object[] { GiPlaneAxes.Xy, -1, 0 };
        yield return new object[] { GiPlaneAxes.Xy, 0, 1 };
        yield return new object[] { GiPlaneAxes.Xy, 0, -1 };
    }

    [Theory]
    [MemberData(nameof(Directions))]
    public void ACone_PointsAlongTheEntitiesFacing(GiPlaneAxes axes, int dx, int dy)
    {
        var angle = MathF.Atan2(dy, dx);
        float qx = 0f, qy = 0f, qz = 0f, qw;
        if (axes == GiPlaneAxes.Xy)
        {
            qz = MathF.Sin(angle / 2f);
            qw = MathF.Cos(angle / 2f);
        }
        else
        {
            var yaw = MathF.PI / 2f - angle;
            qy = MathF.Sin(yaw / 2f);
            qw = MathF.Cos(yaw / 2f);
        }

        var plane = new GiPlane(axes, 0f, 0f);
        var heading = plane.Heading(qx, qy, qz, qw);

        var registry = new GiStampRegistry();
        var spec = Standard();
        Assert.Equal(GiBuildStatus.Built, Build(spec, registry, out var world, out var tables));
        tables.TryLayer(new GiLayerKey(1), out var layer);
        tables.TryStamp(new GiStampKey(2), out var cone);
        tables.TryGrid(1, out var grid);
        var id = GiWorld.Place(world, layer, 32.5f, 32.5f, cone, 8, out var status);
        Assert.Equal(GiWorld.WriteStatus.Applied, status);
        Assert.Equal(GiWorld.WriteStatus.Applied, GiWorld.Turn(world, id, heading));
        GiWorld.Process(world);

        var best = GiWorld.QueryMax(world, grid, layer, out var bx, out var by);
        Assert.True(best > 0);
        var cell = (int)(32.5f * (1 << 7) / 64f);
        Assert.True((bx - cell) * dx + (by - cell) * dy > 0, $"{axes} {dx},{dy}: peak ({bx},{by}) around {cell}");
        GiWorldBuilder.Release(world, tables, registry);
    }
}
