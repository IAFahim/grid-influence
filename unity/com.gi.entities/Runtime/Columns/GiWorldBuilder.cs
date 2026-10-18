using System;
using Gi.Validation;
using GiGrid = Gi.Grid;
using GiLayer = Gi.Layer;
using GiWorld = Gi.World;

namespace Gi.Entities
{
    public enum GiBuildStatus : byte
    {
        Built,
        NoGrids,
        NoLayers,
        TooManyGrids,
        TooManyLayers,
        TooManyStamps,
        InvalidGrid,
        InvalidLayer,
        InvalidStamp,
        WorldLimit,
        StampLimit,
        Unexpected,
    }

    public static class GiWorldBuilder
    {
        public static GiBuildStatus Validate(ReadOnlySpan<GiGridSpec> grids, ReadOnlySpan<GiLayerSpec> layers,
            ReadOnlySpan<GiStampSpec> stamps, ReadOnlySpan<sbyte> cells)
        {
            if (grids.Length == 0) return GiBuildStatus.NoGrids;
            if (layers.Length == 0) return GiBuildStatus.NoLayers;
            if (grids.Length > GiTables.MaxGrids) return GiBuildStatus.TooManyGrids;
            if (!GiValidation.TryLayerCount(layers.Length, out _)) return GiBuildStatus.TooManyLayers;
            if (!GiValidation.TryStampCount(stamps.Length, out _)) return GiBuildStatus.TooManyStamps;

            foreach (var grid in grids)
                if (!GiValidation.TryGrid(grid.Power, grid.X, grid.Y, grid.Size, out _))
                    return GiBuildStatus.InvalidGrid;

            for (var i = 0; i < layers.Length; i++)
                if (!LayerValid(layers[i], i))
                    return GiBuildStatus.InvalidLayer;

            foreach (var stamp in stamps)
                if (!StampValid(stamp, cells))
                    return GiBuildStatus.InvalidStamp;

            return GiBuildStatus.Built;
        }

        public static GiBuildStatus TryBuild(ReadOnlySpan<GiGridSpec> grids, ReadOnlySpan<GiLayerSpec> layers,
            ReadOnlySpan<GiStampSpec> stamps, ReadOnlySpan<sbyte> cells, GiStampRegistry registry, out byte world,
            out GiTables tables)
        {
            world = 0;
            tables = default;
            var status = Validate(grids, layers, stamps, cells);
            if (status != GiBuildStatus.Built) return status;

            byte created = 0;
            var haveWorld = false;
            var acquired = new byte[stamps.Length];
            var acquiredCount = 0;
            try
            {
                created = GiWorld.New();
                haveWorld = true;
                var built = default(GiTables);
                foreach (var grid in grids) built.AddGrid(GiGrid.New(created, grid.Power, grid.X, grid.Y, grid.Size));
                for (var i = 0; i < layers.Length; i++) built.AddLayer(NewLayer(created, layers[i], built), layers[i].Kind == GiLayerKind.Transient);
                foreach (var stamp in stamps)
                {
                    if (!registry.TryAcquire(stamp, cells, out var id))
                    {
                        Discard(created, acquired, acquiredCount, registry);
                        return GiBuildStatus.StampLimit;
                    }

                    acquired[acquiredCount++] = id;
                    built.AddStamp(id);
                }

                world = created;
                tables = built;
                return GiBuildStatus.Built;
            }
            catch (InvalidOperationException)
            {
                if (haveWorld) Discard(created, acquired, acquiredCount, registry);
                return haveWorld ? GiBuildStatus.Unexpected : GiBuildStatus.WorldLimit;
            }
            catch (ArgumentException)
            {
                Discard(created, acquired, acquiredCount, registry);
                return GiBuildStatus.Unexpected;
            }
        }

        public static void Release(byte world, in GiTables tables, GiStampRegistry registry)
        {
            GiWorld.Free(world);
            for (var i = 0; i < tables.StampCount; i++)
                if (tables.TryStamp(new GiStampKey((byte)i), out var stamp))
                    registry.Release(stamp);
        }

        private static void Discard(byte world, byte[] acquired, int acquiredCount, GiStampRegistry registry)
        {
            GiWorld.Free(world);
            for (var i = 0; i < acquiredCount; i++) registry.Release(acquired[i]);
        }

        private static bool LayerValid(GiLayerSpec layer, int index)
        {
            switch (layer.Kind)
            {
                case GiLayerKind.Persistent:
                case GiLayerKind.Transient:
                    return GiLayer.Precision.TryCreate(layer.PhaseBits, layer.TurnBits, out _);
                case GiLayerKind.Sum:
                    return layer.First < index && InputValid(layer.Second, index) &&
                           GiValidation.TryDerivedLayer(layer.FirstWeight, layer.Second == GiLayerSpec.NoLayer ? 0 : layer.SecondWeight, layer.Shift, out _);
                case GiLayerKind.Min:
                case GiLayerKind.Max:
                    return layer.First < index && layer.Second < index;
                case GiLayerKind.Mask:
                    return layer.First < index && layer.Second < index && layer.MaskLow <= layer.MaskHigh;
                default:
                    return false;
            }
        }

        private static bool InputValid(byte input, int index)
        {
            return input == GiLayerSpec.NoLayer || input < index;
        }

        private static bool StampValid(GiStampSpec stamp, ReadOnlySpan<sbyte> cells)
        {
            switch (stamp.Shape)
            {
                case GiStampShape.Box:
                case GiStampShape.Tent:
                case GiStampShape.Bell:
                    return GiValidation.TryRectangle(stamp.Width, stamp.Height, stamp.Value, out _);
                case GiStampShape.Disk:
                case GiStampShape.Cone:
                case GiStampShape.Dome:
                    return GiValidation.TryRound(stamp.Radius, stamp.Arc, stamp.Value, out _);
                case GiStampShape.Raster:
                    return GiValidation.TryRectangle(stamp.Width, stamp.Height, 0, out _) && stamp.CellOffset >= 0 &&
                           stamp.CellCount == stamp.Width * stamp.Height &&
                           (long)stamp.CellOffset + stamp.CellCount <= cells.Length;
                default:
                    return false;
            }
        }

        private static byte NewLayer(byte world, GiLayerSpec spec, in GiTables built)
        {
            switch (spec.Kind)
            {
                case GiLayerKind.Persistent:
                case GiLayerKind.Transient:
                    GiLayer.Precision.TryCreate(spec.PhaseBits, spec.TurnBits, out var precision);
                    return GiLayer.New(world,
                        spec.Kind == GiLayerKind.Persistent ? GiLayer.Residency.Persistent : GiLayer.Residency.Transient,
                        precision);
                case GiLayerKind.Sum:
                    built.TryLayer(new GiLayerKey(spec.First), out var first);
                    if (spec.Second == GiLayerSpec.NoLayer) return GiLayer.Sum(world, first, spec.FirstWeight, spec.Shift);
                    built.TryLayer(new GiLayerKey(spec.Second), out var second);
                    return GiLayer.Sum(world, first, spec.FirstWeight, second, spec.SecondWeight, spec.Shift);
                case GiLayerKind.Min:
                    built.TryLayer(new GiLayerKey(spec.First), out var minFirst);
                    built.TryLayer(new GiLayerKey(spec.Second), out var minSecond);
                    return GiLayer.Min(world, minFirst, minSecond);
                case GiLayerKind.Max:
                    built.TryLayer(new GiLayerKey(spec.First), out var maxFirst);
                    built.TryLayer(new GiLayerKey(spec.Second), out var maxSecond);
                    return GiLayer.Max(world, maxFirst, maxSecond);
                default:
                    built.TryLayer(new GiLayerKey(spec.First), out var maskFirst);
                    built.TryLayer(new GiLayerKey(spec.Second), out var maskSecond);
                    return GiLayer.Mask(world, maskFirst, maskSecond, spec.MaskLow, spec.MaskHigh);
            }
        }
    }
}
