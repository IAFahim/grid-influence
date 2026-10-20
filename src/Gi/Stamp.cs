namespace Gi;

internal enum StampKind : byte
{
    ConstantRectangle,
    Raster,
    Tent,
    Bell,
    Disk,
    Cone,
    Dome
}

internal unsafe struct StampVariant
{
    public byte Live;
    public StampKind Kind;
    public int Width;
    public int Height;
    public int Pitch;
    public int OriginQ8X;
    public int OriginQ8Y;
    public sbyte Constant;
    public sbyte* Data;
    public sbyte* Mips;
    public int MipCount;
    public int Arc;
    public int ArcCos;
    public int ArcSin;
}

internal static unsafe class StampCatalog
{
    internal const int MaxStamps = ValidationRules.MaxStamps;
    private const int FullArc = ValidationRules.FullArc;

    internal static int Count { get; private set; } = 1;

    internal static StampVariant* Get(byte id)
    {
        return Runtime.Stamps + id;
    }

    internal static bool Valid(byte id)
    {
        return id != 0 && id < Count && Runtime.Stamps[id].Live != 0;
    }

    internal static ulong Fingerprint(byte id)
    {
        var v = Runtime.Stamps + id;
        var hash = TileHash.Offset;
        hash = Mix(hash, (byte)v->Kind);
        hash = Mix(hash, v->Width);
        hash = Mix(hash, v->Height);
        hash = Mix(hash, v->Pitch);
        hash = Mix(hash, v->OriginQ8X);
        hash = Mix(hash, v->OriginQ8Y);
        hash = Mix(hash, v->Constant);
        hash = Mix(hash, v->Arc);
        if (v->Data == null) return hash;

        var bordered = v->Data - v->Pitch - 1;
        var end = bordered + (long)v->Pitch * (v->Height + 2);
        for (var p = bordered; p < end; p++) hash = (hash ^ (ulong)*p) * TileHash.Prime;
        return hash;
    }

    private static ulong Mix(ulong hash, int value)
    {
        hash = (hash ^ (ulong)(value & 0xFF)) * TileHash.Prime;
        hash = (hash ^ (ulong)((value >> 8) & 0xFF)) * TileHash.Prime;
        hash = (hash ^ (ulong)((value >> 16) & 0xFF)) * TileHash.Prime;
        hash = (hash ^ (ulong)((value >> 24) & 0xFF)) * TileHash.Prime;
        return hash;
    }

    public static bool Free(byte id)
    {
        if (!Valid(id) || World.UsesStamp(id)) return false;

        var v = Runtime.Stamps + id;
        if (v->Data != null) NativeHeap.Free(v->Data - v->Pitch - 1);
        if (v->Mips != null) NativeHeap.Free(v->Mips);
        *v = default;
        World.FlushKernels();
        return true;
    }

    internal static void Release()
    {
        for (var id = 1; id < Count; id++)
        {
            var v = Runtime.Stamps + id;
            if (v->Data != null) NativeHeap.Free(v->Data - v->Pitch - 1);
            if (v->Mips != null) NativeHeap.Free(v->Mips);
            *v = default;
        }

        Count = 1;
    }

    public static byte Bake(sbyte* data, int width, int height)
    {
        Validate(width, height);
        Runtime.Ensure();

        var uniform = true;
        var first = data[0];
        for (var i = 1; i < width * height; i++)
            if (data[i] != first)
            {
                uniform = false;
                break;
            }

        if (uniform) return Box(width, height, first);

        var id = Reserve();
        var v = Runtime.Stamps + id;
        v->Kind = StampKind.Raster;
        StoreRaster(v, data, width, height);
        return (byte)id;
    }

    private static void StoreRaster(StampVariant* v, sbyte* data, int width, int height)
    {
        var pitch = width + 2;
        var basePtr = (sbyte*)NativeHeap.AllocZeroed((nuint)(pitch * (height + 2)));
        for (var y = 0; y < height; y++)
            Buffer.MemoryCopy(
                data + (long)y * width,
                basePtr + (long)(y + 1) * pitch + 1,
                pitch * (height + 2) - ((long)(y + 1) * pitch + 1),
                width);

        v->Width = width;
        v->Height = height;
        v->Pitch = pitch;
        v->OriginQ8X = -(width * 128);
        v->OriginQ8Y = -(height * 128);
        v->Data = basePtr + pitch + 1;
        BakeMips(v, basePtr);
    }

    private static void BakeMips(StampVariant* v, sbyte* basePtr)
    {
        var levels = stackalloc int[8];
        var count = 0;
        var w = v->Width;
        var h = v->Height;
        while (w > 1 || h > 1)
        {
            w = (w + 1) >> 1;
            h = (h + 1) >> 1;
            levels[count++] = (w + 2) * (h + 2);
        }

        v->MipCount = count;
        if (count == 0) return;

        var total = 0L;
        for (var i = 0; i < count; i++) total += levels[i];
        var mips = (sbyte*)NativeHeap.AllocZeroed((nuint)total);
        var cursor = mips;
        var source = basePtr;
        var sourceWidth = v->Width;
        var sourceHeight = v->Height;
        var sourcePitch = v->Pitch;
        for (var i = 0; i < count; i++)
        {
            var downWidth = (sourceWidth + 1) >> 1;
            var downHeight = (sourceHeight + 1) >> 1;
            var downPitch = downWidth + 2;
            for (var y = 0; y < downHeight; y++)
            for (var x = 0; x < downWidth; x++)
            {
                var sum = 0;
                for (var dy = 0; dy < 2; dy++)
                for (var dx = 0; dx < 2; dx++)
                {
                    var sx = x * 2 + dx;
                    var sy = y * 2 + dy;
                    if (sx < sourceWidth && sy < sourceHeight)
                        sum += source[(sy + 1) * sourcePitch + sx + 1];
                }

                cursor[(y + 1) * downPitch + x + 1] = Average(sum, 4);
            }

            source = cursor;
            sourceWidth = downWidth;
            sourceHeight = downHeight;
            sourcePitch = downPitch;
            cursor += downPitch * (downHeight + 2);
        }

        v->Mips = mips;
    }

    private static sbyte Average(int sum, int n)
    {
        return (sbyte)(sum < 0 ? -((-sum + (n >> 1)) / n) : (sum + (n >> 1)) / n);
    }

    public static byte Box(int width, int height, sbyte value)
    {
        Validate(width, height);
        Runtime.Ensure();

        var id = Reserve();
        var v = Runtime.Stamps + id;
        v->Kind = StampKind.ConstantRectangle;
        v->Width = width;
        v->Height = height;
        v->OriginQ8X = -(width * 128);
        v->OriginQ8Y = -(height * 128);
        v->Constant = value;
        return (byte)id;
    }

    public static byte Tent(int width, int height, sbyte value)
    {
        Validate(width, height);
        Runtime.Ensure();

        var id = Reserve();
        var v = Runtime.Stamps + id;
        v->Kind = StampKind.Tent;
        v->Width = width;
        v->Height = height;
        v->OriginQ8X = -(width * 128);
        v->OriginQ8Y = -(height * 128);
        v->Constant = value;
        return (byte)id;
    }

    public static byte Bell(int width, int height, sbyte value)
    {
        Validate(width, height);
        Runtime.Ensure();

        var id = Reserve();
        var v = Runtime.Stamps + id;
        v->Kind = StampKind.Bell;
        v->Constant = value;
        var samples = (sbyte*)NativeHeap.AlignedAlloc((nuint)(width * height));
        TileBake.BellSamples(samples, width, height, value);
        StoreRaster(v, samples, width, height);
        NativeHeap.AlignedFree(samples);
        return (byte)id;
    }

    public static byte Round(StampKind kind, int radius, sbyte value, int arc)
    {
        if (!ValidationRules.IsRadius(radius)) throw new ArgumentOutOfRangeException(nameof(radius));
        if (!ValidationRules.IsArc(arc)) throw new ArgumentOutOfRangeException(nameof(arc));
        Runtime.Ensure();

        var id = Reserve();
        var v = Runtime.Stamps + id;
        v->Kind = kind;
        v->Width = 2 * radius;
        v->Height = 2 * radius;
        v->OriginQ8X = -(radius * 256);
        v->OriginQ8Y = -(radius * 256);
        v->Constant = value;
        if (arc < FullArc)
        {
            v->Arc = arc * 32768 / FullArc;
            v->ArcCos = TileBake.Cos(v->Arc);
            v->ArcSin = TileBake.Sin(v->Arc);
        }

        return (byte)id;
    }

    private static void Validate(int width, int height)
    {
        if (!ValidationRules.IsAxis(width)) throw new ArgumentOutOfRangeException(nameof(width));
        if (!ValidationRules.IsAxis(height)) throw new ArgumentOutOfRangeException(nameof(height));
    }

    private static int Reserve()
    {
        var id = 1;
        while (id < Count && Runtime.Stamps[id].Live != 0) id++;
        if (id == Count)
        {
            if (Count >= MaxStamps) throw new InvalidOperationException("Stamp catalog is full.");
            Count++;
        }

        Runtime.Stamps[id] = new StampVariant { Live = 1 };
        return id;
    }
}

public static unsafe class Stamp
{
    public enum Shape : byte
    {
        Box,
        Raster,
        Tent,
        Bell,
        Disk,
        Cone,
        Dome
    }

    public static bool TryGetSpec(byte stamp, out Spec spec)
    {
        spec = default;
        if (!StampCatalog.Valid(stamp)) return false;

        var v = StampCatalog.Get(stamp);
        spec = new Spec(
            ShapeOf(v->Kind),
            v->Width,
            v->Height,
            v->Constant,
            v->Arc == 0
                ? ValidationRules.FullArc
                : (v->Arc * ValidationRules.FullArc + ValidationRules.FullArc - 1) / 32768);
        return true;
    }

    private static Shape ShapeOf(StampKind kind)
    {
        return kind switch
        {
            StampKind.ConstantRectangle => Shape.Box,
            StampKind.Raster => Shape.Raster,
            StampKind.Tent => Shape.Tent,
            StampKind.Bell => Shape.Bell,
            StampKind.Disk => Shape.Disk,
            StampKind.Cone => Shape.Cone,
            _ => Shape.Dome
        };
    }

    public static byte New(sbyte* data, ushort width, ushort height)
    {
        return StampCatalog.Bake(data, width, height);
    }

    public static byte New(ReadOnlySpan<sbyte> data, ushort width, ushort height)
    {
        if (data.Length < width * height)
            throw new ArgumentException("Sample span is smaller than width*height.", nameof(data));
        fixed (sbyte* p = data)
        {
            return StampCatalog.Bake(p, width, height);
        }
    }

    public static byte Box(ushort width, ushort height, sbyte value)
    {
        return StampCatalog.Box(width, height, value);
    }

    public static byte Tent(ushort width, ushort height, sbyte value)
    {
        return StampCatalog.Tent(width, height, value);
    }

    public static byte Bell(ushort width, ushort height, sbyte value)
    {
        return StampCatalog.Bell(width, height, value);
    }

    public static byte Disk(ushort radius, sbyte value, ushort arc = 360)
    {
        return StampCatalog.Round(StampKind.Disk, radius, value, arc);
    }

    public static byte Cone(ushort radius, sbyte value, ushort arc = 360)
    {
        return StampCatalog.Round(StampKind.Cone, radius, value, arc);
    }

    public static byte Dome(ushort radius, sbyte value, ushort arc = 360)
    {
        return StampCatalog.Round(StampKind.Dome, radius, value, arc);
    }

    public static bool Free(byte stamp)
    {
        return StampCatalog.Free(stamp);
    }

    public readonly struct Spec
    {
        public readonly Shape StampShape;
        public readonly int Width;
        public readonly int Height;
        public readonly sbyte Value;
        public readonly int ArcDegrees;

        internal Spec(Shape stampShape, int width, int height, sbyte value, int arcDegrees)
        {
            StampShape = stampShape;
            Width = width;
            Height = height;
            Value = value;
            ArcDegrees = arcDegrees;
        }
    }
}
