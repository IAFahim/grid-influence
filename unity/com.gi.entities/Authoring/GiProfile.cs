using System;
using System.Collections.Generic;
using UnityEngine;

namespace Gi.Entities.Authoring
{
    [Serializable]
    public struct GiGridEntry
    {
        public int Power;
        public Vector2 Origin;
        public float Size;
    }

    [Serializable]
    public struct GiBindingEntry
    {
        public GiBindingKind Kind;
        public GiLayerAsset Layer;
        public bool ExcludeSelf;
        public ushort Period;
        public ushort Since;
        public float Reach;
        public short Threshold;
        public GiFieldRef Value;
        public GiFieldRef Gradient;
        public GiFieldRef Total;
        public GiFieldRef Position;
        public GiFieldRef Coverage;
        public GiFieldRef ReachField;
        public GiFieldRef ThresholdField;
    }

    [CreateAssetMenu(menuName = "Gi/Profile", fileName = "GiProfile")]
    public sealed class GiProfile : ScriptableObject
    {
        public GiPlaneAxes Axes;
        public Vector2 Origin;
        public List<GiGridEntry> Grids = new List<GiGridEntry>();
        public List<GiLayerAsset> Layers = new List<GiLayerAsset>();
        public List<GiStampAsset> Stamps = new List<GiStampAsset>();
        public List<GiBindingEntry> Bindings = new List<GiBindingEntry>();

        public int LayerIndex(GiLayerAsset layer)
        {
            return layer == null ? -1 : Layers.IndexOf(layer);
        }

        public int StampIndex(GiStampAsset stamp)
        {
            return stamp == null ? -1 : Stamps.IndexOf(stamp);
        }

        public GiProfileSpec ToSpec(List<GiProfileIssue> authoring = null)
        {
            var spec = new GiProfileSpec { Plane = new GiPlaneSpec { Axes = Axes, OriginX = Origin.x, OriginY = Origin.y } };
            spec.Grids = new GiGridSpec[Grids.Count];
            for (var i = 0; i < Grids.Count; i++)
                spec.Grids[i] = new GiGridSpec { Power = Grids[i].Power, X = Grids[i].Origin.x, Y = Grids[i].Origin.y, Size = Grids[i].Size };

            spec.Layers = new GiLayerSpec[Layers.Count];
            for (var i = 0; i < Layers.Count; i++) spec.Layers[i] = LayerSpec(Layers[i], i, authoring);

            var cells = new List<sbyte>();
            spec.Stamps = new GiStampSpec[Stamps.Count];
            for (var i = 0; i < Stamps.Count; i++)
            {
                if (Stamps[i] == null)
                {
                    authoring?.Add(new GiProfileIssue(GiProfileSection.Stamp, i, "The stamp slot is empty."));
                    spec.Stamps[i] = new GiStampSpec { Shape = (GiStampShape)255 };
                    continue;
                }

                spec.Stamps[i] = Stamps[i].ToSpec(cells);
            }

            spec.Cells = cells.ToArray();
            spec.Bindings = new GiBindingSpec[Bindings.Count];
            for (var i = 0; i < Bindings.Count; i++) spec.Bindings[i] = BindingSpec(Bindings[i], i, authoring);
            return spec;
        }

        public List<GiProfileIssue> Check()
        {
            var authoring = new List<GiProfileIssue>();
            var spec = ToSpec(authoring);
            var issues = new List<GiProfileIssue>();
            GiProfileCheck.Check(spec, issues);
            issues.RemoveAll(issue => issue.Section == GiProfileSection.Layer && GiProfileCheck.Has(authoring, GiProfileSection.Layer, issue.Index));
            issues.AddRange(authoring);
            return issues;
        }

        private GiLayerSpec LayerSpec(GiLayerAsset layer, int index, List<GiProfileIssue> authoring)
        {
            if (layer == null)
            {
                authoring?.Add(new GiProfileIssue(GiProfileSection.Layer, index, "The layer slot is empty."));
                return new GiLayerSpec { Kind = (GiLayerKind)255, First = GiLayerSpec.NoLayer, Second = GiLayerSpec.NoLayer };
            }

            var spec = new GiLayerSpec { Kind = layer.Kind, First = GiLayerSpec.NoLayer, Second = GiLayerSpec.NoLayer };
            if (layer.IsBase)
            {
                layer.Bits(out var phaseBits, out var turnBits);
                spec.PhaseBits = phaseBits;
                spec.TurnBits = turnBits;
                return spec;
            }

            spec.FirstWeight = layer.FirstWeight;
            spec.SecondWeight = layer.SecondWeight;
            spec.Shift = layer.Shift;
            spec.MaskLow = layer.MaskLow;
            spec.MaskHigh = layer.MaskHigh;
            spec.First = Input(layer.First, index, "first", authoring, true);
            spec.Second = layer.Kind == GiLayerKind.Sum && layer.Second == null
                ? GiLayerSpec.NoLayer
                : Input(layer.Second, index, "second", authoring, false);
            return spec;
        }

        private byte Input(GiLayerAsset input, int index, string which, List<GiProfileIssue> authoring, bool required)
        {
            var found = LayerIndex(input);
            if (found >= 0) return (byte)found;

            authoring?.Add(new GiProfileIssue(GiProfileSection.Layer, index,
                input == null ? "A derived layer needs its " + which + " input layer chosen." : "The " + which + " input layer is not in this profile's stack."));
            return (byte)index;
        }

        private GiBindingSpec BindingSpec(in GiBindingEntry entry, int index, List<GiProfileIssue> authoring)
        {
            var layer = LayerIndex(entry.Layer);
            if (layer < 0) authoring?.Add(new GiProfileIssue(GiProfileSection.Binding, index, "The binding's layer is not in this profile's stack."));

            var spec = new GiBindingSpec
            {
                Kind = entry.Kind,
                Layer = (byte)(layer < 0 ? GiLayerSpec.NoLayer : layer),
                Flags = entry.ExcludeSelf ? GiBindingSpec.ExcludeSelf : (byte)0,
                Period = entry.Period,
                Since = entry.Since,
                Reach = entry.Reach,
                Threshold = entry.Threshold,
            };
            spec.Value = Field(entry.Value, index, authoring);
            spec.Gradient = Field(entry.Gradient, index, authoring);
            spec.Total = Field(entry.Total, index, authoring);
            spec.Position = Field(entry.Position, index, authoring);
            spec.Coverage = Field(entry.Coverage, index, authoring);
            spec.ReachField = Field(entry.ReachField, index, authoring);
            spec.ThresholdField = Field(entry.ThresholdField, index, authoring);
            return spec;
        }

        private static GiBindingField Field(in GiFieldRef reference, int index, List<GiProfileIssue> authoring)
        {
            if (reference.TryResolve(out var field, out var error)) return field;

            authoring?.Add(new GiProfileIssue(GiProfileSection.Binding, index, error));
            return default;
        }
    }
}
