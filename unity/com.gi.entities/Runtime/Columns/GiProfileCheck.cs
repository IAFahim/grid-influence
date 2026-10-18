using System;
using System.Collections.Generic;
using System.Globalization;
using Gi.Validation;
using GiLayer = Gi.Layer;

namespace Gi.Entities
{
    public enum GiProfileSection : byte
    {
        Profile,
        Grid,
        Layer,
        Stamp,
        Binding,
    }

    public readonly struct GiProfileIssue
    {
        public readonly GiProfileSection Section;
        public readonly int Index;
        public readonly string Message;

        public GiProfileIssue(GiProfileSection section, int index, string message)
        {
            Section = section;
            Index = index;
            Message = message;
        }

        public override string ToString()
        {
            return Section == GiProfileSection.Profile
                ? Message
                : Section + " " + Index.ToString(CultureInfo.InvariantCulture) + ": " + Message;
        }
    }

    public static class GiProfileCheck
    {
        public const string NoGrids = "A profile needs at least one grid.";
        public const string NoLayers = "A profile needs at least one layer.";
        public const string TooManyGrids = "A world holds at most 32 grids.";
        public const string ForwardReference = "A derived layer reads only layers above it in the stack.";
        public const string EmptyMask = "The mask range is empty: the low bound is above the high bound.";
        public const string RasterCells = "A raster stamp needs exactly width times height cells inside the profile's cell data.";
        public const string UnknownShape = "The stamp shape is not one this version of Gi supports.";
        public const string UnknownLayerKind = "The layer kind is not one this version of Gi supports.";

        private static string _precision;

        public static string PrecisionMessage
        {
            get
            {
                if (_precision == null) _precision = BuildPrecisionMessage();
                return _precision;
            }
        }

        public static List<GiProfileIssue> Check(GiProfileSpec spec)
        {
            var issues = new List<GiProfileIssue>();
            Check(spec, issues);
            return issues;
        }

        public static void Check(GiProfileSpec spec, List<GiProfileIssue> issues)
        {
            if (spec.Grids.Length == 0) issues.Add(new GiProfileIssue(GiProfileSection.Profile, 0, NoGrids));
            if (spec.Layers.Length == 0) issues.Add(new GiProfileIssue(GiProfileSection.Profile, 0, NoLayers));
            if (spec.Grids.Length > GiTables.MaxGrids) issues.Add(new GiProfileIssue(GiProfileSection.Profile, 0, TooManyGrids));
            if (!GiValidation.TryLayerCount(spec.Layers.Length, out var layerCount))
                issues.Add(new GiProfileIssue(GiProfileSection.Profile, 0, layerCount));
            if (!GiValidation.TryStampCount(spec.Stamps.Length, out var stampCount))
                issues.Add(new GiProfileIssue(GiProfileSection.Profile, 0, stampCount));

            for (var i = 0; i < spec.Grids.Length; i++)
            {
                var grid = spec.Grids[i];
                if (!GiValidation.TryGrid(grid.Power, grid.X, grid.Y, grid.Size, out var error))
                    issues.Add(new GiProfileIssue(GiProfileSection.Grid, i, error));
            }

            for (var i = 0; i < spec.Layers.Length; i++) CheckLayer(spec.Layers[i], i, issues);
            for (var i = 0; i < spec.Stamps.Length; i++) CheckStamp(spec.Stamps[i], i, spec.Cells.Length, issues);
            CheckBindings(spec, issues);
        }

        public static bool Has(List<GiProfileIssue> issues, GiProfileSection section, int index)
        {
            foreach (var issue in issues)
                if (issue.Section == section && issue.Index == index)
                    return true;
            return false;
        }

        private static void CheckLayer(in GiLayerSpec layer, int index, List<GiProfileIssue> issues)
        {
            switch (layer.Kind)
            {
                case GiLayerKind.Persistent:
                case GiLayerKind.Transient:
                    if (!GiLayer.Precision.TryCreate(layer.PhaseBits, layer.TurnBits, out _))
                        issues.Add(new GiProfileIssue(GiProfileSection.Layer, index, PrecisionMessage));
                    break;
                case GiLayerKind.Sum:
                    if (layer.First >= index || !InputAbove(layer.Second, index))
                        issues.Add(new GiProfileIssue(GiProfileSection.Layer, index, ForwardReference));
                    if (!GiValidation.TryDerivedLayer(layer.FirstWeight, layer.Second == GiLayerSpec.NoLayer ? 0 : layer.SecondWeight, layer.Shift,
                            out var error))
                        issues.Add(new GiProfileIssue(GiProfileSection.Layer, index, error));
                    break;
                case GiLayerKind.Min:
                case GiLayerKind.Max:
                    if (layer.First >= index || layer.Second >= index)
                        issues.Add(new GiProfileIssue(GiProfileSection.Layer, index, ForwardReference));
                    break;
                case GiLayerKind.Mask:
                    if (layer.First >= index || layer.Second >= index)
                        issues.Add(new GiProfileIssue(GiProfileSection.Layer, index, ForwardReference));
                    if (layer.MaskLow > layer.MaskHigh) issues.Add(new GiProfileIssue(GiProfileSection.Layer, index, EmptyMask));
                    break;
                default:
                    issues.Add(new GiProfileIssue(GiProfileSection.Layer, index, UnknownLayerKind));
                    break;
            }
        }

        private static bool InputAbove(byte input, int index)
        {
            return input == GiLayerSpec.NoLayer || input < index;
        }

        public static void CheckStamp(in GiStampSpec stamp, int index, int cellCount, List<GiProfileIssue> issues)
        {
            string error;
            switch (stamp.Shape)
            {
                case GiStampShape.Box:
                case GiStampShape.Tent:
                case GiStampShape.Bell:
                    if (!GiValidation.TryRectangle(stamp.Width, stamp.Height, stamp.Value, out error))
                        issues.Add(new GiProfileIssue(GiProfileSection.Stamp, index, error));
                    break;
                case GiStampShape.Disk:
                case GiStampShape.Cone:
                case GiStampShape.Dome:
                    if (!GiValidation.TryRound(stamp.Radius, stamp.Arc, stamp.Value, out error))
                        issues.Add(new GiProfileIssue(GiProfileSection.Stamp, index, error));
                    break;
                case GiStampShape.Raster:
                    if (!GiValidation.TryRectangle(stamp.Width, stamp.Height, 0, out error))
                        issues.Add(new GiProfileIssue(GiProfileSection.Stamp, index, error));
                    else if (stamp.CellOffset < 0 || stamp.CellCount != stamp.Width * stamp.Height ||
                             (long)stamp.CellOffset + stamp.CellCount > cellCount)
                        issues.Add(new GiProfileIssue(GiProfileSection.Stamp, index, RasterCells));
                    break;
                default:
                    issues.Add(new GiProfileIssue(GiProfileSection.Stamp, index, UnknownShape));
                    break;
            }
        }

        private static void CheckBindings(GiProfileSpec spec, List<GiProfileIssue> issues)
        {
            if (spec.Bindings.Length == 0) return;

            var statuses = new GiBindingStatus[spec.Bindings.Length];
            GiBindingTable.Validate(spec.Bindings, spec.Layers, GiBindingSupport.All, statuses);
            for (var i = 0; i < statuses.Length; i++)
                if (statuses[i] != GiBindingStatus.Valid)
                    issues.Add(new GiProfileIssue(GiProfileSection.Binding, i, GiBindingTable.Describe(statuses[i])));
        }

        private static string BuildPrecisionMessage()
        {
            var phaseLow = int.MaxValue;
            var phaseHigh = int.MinValue;
            var turnLow = int.MaxValue;
            var turnHigh = int.MinValue;
            for (var bits = 0; bits <= 32; bits++)
            {
                if (GiLayer.Precision.TryCreate(bits, 16, out _))
                {
                    phaseLow = Math.Min(phaseLow, bits);
                    phaseHigh = Math.Max(phaseHigh, bits);
                }

                if (GiLayer.Precision.TryCreate(8, bits, out _))
                {
                    turnLow = Math.Min(turnLow, bits);
                    turnHigh = Math.Max(turnHigh, bits);
                }
            }

            return "Layer phase bits must be " + phaseLow.ToString(CultureInfo.InvariantCulture) + " through " +
                   phaseHigh.ToString(CultureInfo.InvariantCulture) + " and turn bits " + turnLow.ToString(CultureInfo.InvariantCulture) +
                   " through " + turnHigh.ToString(CultureInfo.InvariantCulture) + ".";
        }
    }
}
