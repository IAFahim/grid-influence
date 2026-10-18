using System;

namespace Gi.Entities
{
    public enum GiPlaneAxes : byte
    {
        Xz,
        Xy,
    }

    public enum GiLayerKind : byte
    {
        Persistent,
        Transient,
        Sum,
        Min,
        Max,
        Mask,
    }

    public enum GiStampShape : byte
    {
        Box,
        Tent,
        Bell,
        Disk,
        Cone,
        Dome,
        Raster,
    }

    public enum GiBindingKind : byte
    {
        Value,
        Gradient,
        ValueGradient,
        Area,
        Max,
        Nearest,
        Changed,
    }

    public struct GiPlaneSpec
    {
        public GiPlaneAxes Axes;
        public float OriginX;
        public float OriginY;
    }

    public struct GiGridSpec
    {
        public int Power;
        public float X;
        public float Y;
        public float Size;
    }

    public struct GiLayerSpec
    {
        public const byte NoLayer = 255;

        public GiLayerKind Kind;
        public byte PhaseBits;
        public byte TurnBits;
        public byte First;
        public byte Second;
        public int FirstWeight;
        public int SecondWeight;
        public int Shift;
        public short MaskLow;
        public short MaskHigh;
    }

    public struct GiStampSpec
    {
        public GiStampShape Shape;
        public ushort Width;
        public ushort Height;
        public ushort Radius;
        public ushort Arc;
        public sbyte Value;
        public int CellOffset;
        public int CellCount;
    }

    public struct GiBindingField
    {
        public ulong TypeHash;
        public ushort Offset;
        public ushort Size;
    }

    public struct GiBindingSpec
    {
        public const byte ExcludeSelf = 1;

        public GiBindingKind Kind;
        public byte Layer;
        public byte Flags;
        public ushort Period;
        public ushort Since;
        public float Reach;
        public short Threshold;
        public GiBindingField Value;
        public GiBindingField Gradient;
        public GiBindingField Total;
        public GiBindingField Position;
        public GiBindingField Coverage;
        public GiBindingField ReachField;
        public GiBindingField ThresholdField;
    }

    public static class GiProfileHash
    {
        public static ulong Compute(GiPlaneSpec plane, ReadOnlySpan<GiGridSpec> grids, ReadOnlySpan<GiLayerSpec> layers,
            ReadOnlySpan<GiStampSpec> stamps, ReadOnlySpan<sbyte> cells, ReadOnlySpan<GiBindingSpec> bindings)
        {
            var hash = GiContentHash.Start();
            hash.Add((byte)plane.Axes);
            hash.Add(plane.OriginX);
            hash.Add(plane.OriginY);

            hash.Add(grids.Length);
            foreach (var grid in grids)
            {
                hash.Add(grid.Power);
                hash.Add(grid.X);
                hash.Add(grid.Y);
                hash.Add(grid.Size);
            }

            hash.Add(layers.Length);
            foreach (var layer in layers)
            {
                hash.Add((byte)layer.Kind);
                hash.Add(layer.PhaseBits);
                hash.Add(layer.TurnBits);
                hash.Add(layer.First);
                hash.Add(layer.Second);
                hash.Add(layer.FirstWeight);
                hash.Add(layer.SecondWeight);
                hash.Add(layer.Shift);
                hash.Add(layer.MaskLow);
                hash.Add(layer.MaskHigh);
            }

            hash.Add(stamps.Length);
            foreach (var stamp in stamps) hash.Add(StampHash(stamp, cells));

            hash.Add(bindings.Length);
            foreach (var binding in bindings) AddBinding(ref hash, binding);

            return hash.Finish();
        }

        public static ulong StampHash(GiStampSpec stamp, ReadOnlySpan<sbyte> cells)
        {
            var hash = GiContentHash.Start();
            hash.Add((byte)stamp.Shape);
            hash.Add(stamp.Width);
            hash.Add(stamp.Height);
            hash.Add(stamp.Radius);
            hash.Add(stamp.Arc);
            hash.Add((byte)stamp.Value);
            hash.Add(stamp.CellCount > 0 ? cells.Slice(stamp.CellOffset, stamp.CellCount) : ReadOnlySpan<sbyte>.Empty);
            return hash.Finish();
        }

        private static void AddBinding(ref GiContentHash hash, GiBindingSpec binding)
        {
            hash.Add((byte)binding.Kind);
            hash.Add(binding.Layer);
            hash.Add(binding.Flags);
            hash.Add(binding.Period);
            hash.Add(binding.Since);
            hash.Add(binding.Reach);
            hash.Add(binding.Threshold);
            AddField(ref hash, binding.Value);
            AddField(ref hash, binding.Gradient);
            AddField(ref hash, binding.Total);
            AddField(ref hash, binding.Position);
            AddField(ref hash, binding.Coverage);
            AddField(ref hash, binding.ReachField);
            AddField(ref hash, binding.ThresholdField);
        }

        private static void AddField(ref GiContentHash hash, GiBindingField field)
        {
            hash.Add(field.TypeHash);
            hash.Add(field.Offset);
            hash.Add(field.Size);
        }
    }

    public sealed class GiProfileSpec
    {
        public GiPlaneSpec Plane;
        public GiGridSpec[] Grids = Array.Empty<GiGridSpec>();
        public GiLayerSpec[] Layers = Array.Empty<GiLayerSpec>();
        public GiStampSpec[] Stamps = Array.Empty<GiStampSpec>();
        public sbyte[] Cells = Array.Empty<sbyte>();
        public GiBindingSpec[] Bindings = Array.Empty<GiBindingSpec>();

        public ulong Hash => GiProfileHash.Compute(Plane, Grids, Layers, Stamps, Cells, Bindings);
    }
}
