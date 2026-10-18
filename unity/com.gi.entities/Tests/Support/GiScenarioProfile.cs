using System;

namespace Gi.Entities.Tests.Support
{
    public enum GiScenarioShape : byte
    {
        Box,
        Tent,
        Bell,
        Disk,
        Cone,
        Dome,
        Raster,
    }

    public enum GiScenarioLayerKind : byte
    {
        Persistent,
        Transient,
        Sum,
        Min,
        Max,
        Mask,
    }

    public readonly struct GiScenarioGrid
    {
        public readonly int Power;
        public readonly int X;
        public readonly int Y;
        public readonly int Size;

        public GiScenarioGrid(int power, int x, int y, int size)
        {
            Power = power;
            X = x;
            Y = y;
            Size = size;
        }
    }

    public readonly struct GiScenarioLayer
    {
        public readonly GiScenarioLayerKind Kind;
        public readonly int PhaseBits;
        public readonly int TurnBits;
        public readonly int First;
        public readonly int Second;
        public readonly int FirstWeight;
        public readonly int SecondWeight;
        public readonly int Shift;
        public readonly int MaskLow;
        public readonly int MaskHigh;

        private GiScenarioLayer(GiScenarioLayerKind kind, int phaseBits, int turnBits, int first, int second,
            int firstWeight, int secondWeight, int shift, int maskLow, int maskHigh)
        {
            Kind = kind;
            PhaseBits = phaseBits;
            TurnBits = turnBits;
            First = first;
            Second = second;
            FirstWeight = firstWeight;
            SecondWeight = secondWeight;
            Shift = shift;
            MaskLow = maskLow;
            MaskHigh = maskHigh;
        }

        public bool IsBase => Kind == GiScenarioLayerKind.Persistent || Kind == GiScenarioLayerKind.Transient;

        public static GiScenarioLayer Persistent(int phaseBits, int turnBits)
        {
            return new GiScenarioLayer(GiScenarioLayerKind.Persistent, phaseBits, turnBits, -1, -1, 0, 0, 0, 0, 0);
        }

        public static GiScenarioLayer Transient(int phaseBits, int turnBits)
        {
            return new GiScenarioLayer(GiScenarioLayerKind.Transient, phaseBits, turnBits, -1, -1, 0, 0, 0, 0, 0);
        }

        public static GiScenarioLayer Sum(int first, int firstWeight, int second, int secondWeight, int shift)
        {
            return new GiScenarioLayer(GiScenarioLayerKind.Sum, 0, 0, first, second, firstWeight, secondWeight, shift, 0, 0);
        }

        public static GiScenarioLayer Min(int first, int second)
        {
            return new GiScenarioLayer(GiScenarioLayerKind.Min, 0, 0, first, second, 0, 0, 0, 0, 0);
        }

        public static GiScenarioLayer Max(int first, int second)
        {
            return new GiScenarioLayer(GiScenarioLayerKind.Max, 0, 0, first, second, 0, 0, 0, 0, 0);
        }

        public static GiScenarioLayer Mask(int first, int second, int low, int high)
        {
            return new GiScenarioLayer(GiScenarioLayerKind.Mask, 0, 0, first, second, 0, 0, 0, low, high);
        }
    }

    public readonly struct GiScenarioStamp
    {
        public readonly GiScenarioShape Shape;
        public readonly int Width;
        public readonly int Height;
        public readonly int Radius;
        public readonly int Arc;
        public readonly int Value;
        public readonly sbyte[] Cells;

        private GiScenarioStamp(GiScenarioShape shape, int width, int height, int radius, int arc, int value, sbyte[] cells)
        {
            Shape = shape;
            Width = width;
            Height = height;
            Radius = radius;
            Arc = arc;
            Value = value;
            Cells = cells;
        }

        public static GiScenarioStamp Rectangle(GiScenarioShape shape, int width, int height, int value)
        {
            return new GiScenarioStamp(shape, width, height, 0, 0, value, Array.Empty<sbyte>());
        }

        public static GiScenarioStamp Round(GiScenarioShape shape, int radius, int value, int arc)
        {
            return new GiScenarioStamp(shape, 0, 0, radius, arc, value, Array.Empty<sbyte>());
        }

        public static GiScenarioStamp Raster(int width, int height, sbyte[] cells)
        {
            return new GiScenarioStamp(GiScenarioShape.Raster, width, height, 0, 0, 0, cells);
        }
    }

    public sealed class GiScenarioProfile
    {
        public readonly GiScenarioGrid[] Grids;
        public readonly GiScenarioLayer[] Layers;
        public readonly GiScenarioStamp[] Stamps;

        public GiScenarioProfile(GiScenarioGrid[] grids, GiScenarioLayer[] layers, GiScenarioStamp[] stamps)
        {
            Grids = grids;
            Layers = layers;
            Stamps = stamps;
        }

        public static GiScenarioProfile Standard()
        {
            var raster = new sbyte[16];
            for (var i = 0; i < raster.Length; i++) raster[i] = (sbyte)((i * 37 + 11) % 101 - 50);

            return new GiScenarioProfile(
                new[]
                {
                    new GiScenarioGrid(6, 0, 0, 64),
                    new GiScenarioGrid(7, 0, 0, 64),
                },
                new[]
                {
                    GiScenarioLayer.Persistent(8, 16),
                    GiScenarioLayer.Persistent(0, 16),
                    GiScenarioLayer.Transient(0, 16),
                    GiScenarioLayer.Sum(0, 1, 2, 1, 1),
                    GiScenarioLayer.Max(0, 1),
                    GiScenarioLayer.Persistent(1, 16),
                    GiScenarioLayer.Transient(1, 16),
                },
                new[]
                {
                    GiScenarioStamp.Rectangle(GiScenarioShape.Box, 3, 3, 50),
                    GiScenarioStamp.Rectangle(GiScenarioShape.Tent, 5, 5, 40),
                    GiScenarioStamp.Round(GiScenarioShape.Cone, 6, 30, 60),
                    GiScenarioStamp.Raster(4, 4, raster),
                    GiScenarioStamp.Round(GiScenarioShape.Disk, 4, 20, 360),
                    GiScenarioStamp.Rectangle(GiScenarioShape.Bell, 7, 7, 30),
                });
        }
    }
}
