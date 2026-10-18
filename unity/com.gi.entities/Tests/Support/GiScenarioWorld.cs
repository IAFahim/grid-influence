using System;
using GiGrid = Gi.Grid;
using GiLayer = Gi.Layer;
using GiStamp = Gi.Stamp;
using GiWorld = Gi.World;

namespace Gi.Entities.Tests.Support
{
    public sealed class GiScenarioWorld : IDisposable
    {
        public readonly byte World;
        public readonly byte[] Grids;
        public readonly byte[] Layers;
        public readonly byte[] Stamps;
        private bool _disposed;

        private GiScenarioWorld(byte world, byte[] grids, byte[] layers, byte[] stamps)
        {
            World = world;
            Grids = grids;
            Layers = layers;
            Stamps = stamps;
        }

        public static GiScenarioWorld Create(GiScenarioProfile profile)
        {
            var world = GiWorld.New();
            var grids = new byte[profile.Grids.Length];
            for (var i = 0; i < grids.Length; i++)
            {
                var grid = profile.Grids[i];
                grids[i] = GiGrid.New(world, grid.Power, grid.X, grid.Y, grid.Size);
            }

            var layers = new byte[profile.Layers.Length];
            for (var i = 0; i < layers.Length; i++) layers[i] = NewLayer(world, profile.Layers[i], layers);

            var stamps = new byte[profile.Stamps.Length];
            for (var i = 0; i < stamps.Length; i++) stamps[i] = NewStamp(profile.Stamps[i]);
            return new GiScenarioWorld(world, grids, layers, stamps);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            GiWorld.Free(World);
            foreach (var stamp in Stamps) GiStamp.Free(stamp);
        }

        private static byte NewLayer(byte world, GiScenarioLayer layer, byte[] created)
        {
            switch (layer.Kind)
            {
                case GiScenarioLayerKind.Persistent:
                case GiScenarioLayerKind.Transient:
                    if (!GiLayer.Precision.TryCreate(layer.PhaseBits, layer.TurnBits, out var precision))
                        throw new ArgumentOutOfRangeException(nameof(layer));
                    var residency = layer.Kind == GiScenarioLayerKind.Persistent
                        ? GiLayer.Residency.Persistent
                        : GiLayer.Residency.Transient;
                    return GiLayer.New(world, residency, precision);
                case GiScenarioLayerKind.Sum:
                    return GiLayer.Sum(world, created[layer.First], layer.FirstWeight, created[layer.Second],
                        layer.SecondWeight, layer.Shift);
                case GiScenarioLayerKind.Min:
                    return GiLayer.Min(world, created[layer.First], created[layer.Second]);
                case GiScenarioLayerKind.Max:
                    return GiLayer.Max(world, created[layer.First], created[layer.Second]);
                default:
                    return GiLayer.Mask(world, created[layer.First], created[layer.Second], (short)layer.MaskLow,
                        (short)layer.MaskHigh);
            }
        }

        private static byte NewStamp(GiScenarioStamp stamp)
        {
            switch (stamp.Shape)
            {
                case GiScenarioShape.Box:
                    return GiStamp.Box((ushort)stamp.Width, (ushort)stamp.Height, (sbyte)stamp.Value);
                case GiScenarioShape.Tent:
                    return GiStamp.Tent((ushort)stamp.Width, (ushort)stamp.Height, (sbyte)stamp.Value);
                case GiScenarioShape.Bell:
                    return GiStamp.Bell((ushort)stamp.Width, (ushort)stamp.Height, (sbyte)stamp.Value);
                case GiScenarioShape.Disk:
                    return GiStamp.Disk((ushort)stamp.Radius, (sbyte)stamp.Value, (ushort)stamp.Arc);
                case GiScenarioShape.Cone:
                    return GiStamp.Cone((ushort)stamp.Radius, (sbyte)stamp.Value, (ushort)stamp.Arc);
                case GiScenarioShape.Dome:
                    return GiStamp.Dome((ushort)stamp.Radius, (sbyte)stamp.Value, (ushort)stamp.Arc);
                default:
                    return GiStamp.New(stamp.Cells, (ushort)stamp.Width, (ushort)stamp.Height);
            }
        }
    }
}
