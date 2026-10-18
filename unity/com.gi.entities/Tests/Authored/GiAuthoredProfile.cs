using System.Collections.Generic;
using Gi.Entities.Authoring;
using Gi.Entities.Tests.Support;
using UnityEngine;

namespace Gi.Entities.Tests.Authored
{
    public static class GiAuthoredProfile
    {
        public static GiProfile FromScenario(GiScenarioProfile scenario, List<Object> made = null)
        {
            var profile = ScriptableObject.CreateInstance<GiProfile>();
            made?.Add(profile);
            foreach (var grid in scenario.Grids)
                profile.Grids.Add(new GiGridEntry { Power = grid.Power, Origin = new Vector2(grid.X, grid.Y), Size = grid.Size });

            foreach (var layer in scenario.Layers)
            {
                var asset = ScriptableObject.CreateInstance<GiLayerAsset>();
                made?.Add(asset);
                asset.Owner = profile;
                asset.Kind = (GiLayerKind)(byte)layer.Kind;
                asset.Precision = GiLayerPrecision.Custom;
                asset.PhaseBits = (byte)layer.PhaseBits;
                asset.TurnBits = (byte)layer.TurnBits;
                asset.First = layer.First < 0 ? null : profile.Layers[layer.First];
                asset.Second = layer.Second < 0 ? null : profile.Layers[layer.Second];
                asset.FirstWeight = layer.FirstWeight;
                asset.SecondWeight = layer.SecondWeight;
                asset.Shift = layer.Shift;
                asset.MaskLow = (short)layer.MaskLow;
                asset.MaskHigh = (short)layer.MaskHigh;
                profile.Layers.Add(asset);
            }

            foreach (var stamp in scenario.Stamps)
            {
                var asset = ScriptableObject.CreateInstance<GiStampAsset>();
                made?.Add(asset);
                asset.Shape = (GiStampShape)(byte)stamp.Shape;
                asset.Width = (ushort)stamp.Width;
                asset.Height = (ushort)stamp.Height;
                asset.Radius = (ushort)stamp.Radius;
                asset.Arc = (ushort)stamp.Arc;
                asset.Value = (sbyte)stamp.Value;
                asset.Cells = stamp.Cells;
                profile.Stamps.Add(asset);
            }

            return profile;
        }
    }
}
