using System.Collections.Generic;

namespace Gi.Entities.Tests.Support
{
    public static class GiScenarioSpecs
    {
        public static GiProfileSpec ToSpec(this GiScenarioProfile profile)
        {
            return profile.ToSpec(new GiPlaneSpec { Axes = GiPlaneAxes.Xz });
        }

        public static GiProfileSpec ToSpec(this GiScenarioProfile profile, GiPlaneSpec plane)
        {
            var grids = new GiGridSpec[profile.Grids.Length];
            for (var i = 0; i < grids.Length; i++)
            {
                var grid = profile.Grids[i];
                grids[i] = new GiGridSpec { Power = grid.Power, X = grid.X, Y = grid.Y, Size = grid.Size };
            }

            var layers = new GiLayerSpec[profile.Layers.Length];
            for (var i = 0; i < layers.Length; i++) layers[i] = Layer(profile.Layers[i]);

            var cells = new List<sbyte>();
            var stamps = new GiStampSpec[profile.Stamps.Length];
            for (var i = 0; i < stamps.Length; i++) stamps[i] = Stamp(profile.Stamps[i], cells);

            return new GiProfileSpec
            {
                Plane = plane,
                Grids = grids,
                Layers = layers,
                Stamps = stamps,
                Cells = cells.ToArray(),
            };
        }

        private static GiLayerSpec Layer(GiScenarioLayer layer)
        {
            return new GiLayerSpec
            {
                Kind = (GiLayerKind)(byte)layer.Kind,
                PhaseBits = (byte)layer.PhaseBits,
                TurnBits = (byte)layer.TurnBits,
                First = layer.First < 0 ? GiLayerSpec.NoLayer : (byte)layer.First,
                Second = layer.Second < 0 ? GiLayerSpec.NoLayer : (byte)layer.Second,
                FirstWeight = layer.FirstWeight,
                SecondWeight = layer.SecondWeight,
                Shift = layer.Shift,
                MaskLow = (short)layer.MaskLow,
                MaskHigh = (short)layer.MaskHigh,
            };
        }

        private static GiStampSpec Stamp(GiScenarioStamp stamp, List<sbyte> cells)
        {
            var spec = new GiStampSpec
            {
                Shape = (GiStampShape)(byte)stamp.Shape,
                Width = (ushort)stamp.Width,
                Height = (ushort)stamp.Height,
                Radius = (ushort)stamp.Radius,
                Arc = (ushort)stamp.Arc,
                Value = (sbyte)stamp.Value,
            };
            if (stamp.Shape != GiScenarioShape.Raster) return spec;

            spec.CellOffset = cells.Count;
            spec.CellCount = stamp.Cells.Length;
            cells.AddRange(stamp.Cells);
            return spec;
        }
    }
}
