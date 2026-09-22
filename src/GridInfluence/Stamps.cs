using System.Runtime.InteropServices;

namespace GridInfluence;

internal enum StampKind : byte
{
    ConstantRectangle,
    Raster,
}

internal unsafe struct StampVariant
{
    public StampKind Kind;
    public int Width;
    public int Height;
    public int Pitch;
    public int OriginQ8X;
    public int OriginQ8Y;
    public sbyte Constant;
    public sbyte* Data;
}

internal static unsafe class StampCatalog
{
    internal const int MaxStamps = 256;
    internal const int MaxAxis = 256;

    private static readonly StampVariant* Variants =
        (StampVariant*)NativeMemory.AllocZeroed((nuint)(MaxStamps * sizeof(StampVariant)));

    private static int _count = 1;

    internal static int Count => _count;

    internal static StampVariant* Get(byte id) => Variants + id;

    public static byte Bake(sbyte* data, int width, int height)
    {
        Validate(width, height);

        var uniform = true;
        var first = data[0];
        for (var i = 1; i < width * height; i++)
        {
            if (data[i] != first) { uniform = false; break; }
        }

        if (uniform) return Box(width, height, first);

        var id = Reserve();
        var v = Variants + id;
        var pitch = width + 2;
        var basePtr = (sbyte*)NativeMemory.AllocZeroed((nuint)(pitch * (height + 2)));
        for (var y = 0; y < height; y++)
        {
            Buffer.MemoryCopy(
                data + (long)y * width,
                basePtr + (long)(y + 1) * pitch + 1,
                pitch * (height + 2) - ((long)(y + 1) * pitch + 1),
                width);
        }

        v->Kind = StampKind.Raster;
        v->Width = width;
        v->Height = height;
        v->Pitch = pitch;
        v->OriginQ8X = -(width * 128);
        v->OriginQ8Y = -(height * 128);
        v->Data = basePtr + pitch + 1;
        return (byte)id;
    }

    public static byte Box(int width, int height, sbyte value)
    {
        Validate(width, height);

        var id = Reserve();
        var v = Variants + id;
        v->Kind = StampKind.ConstantRectangle;
        v->Width = width;
        v->Height = height;
        v->OriginQ8X = -(width * 128);
        v->OriginQ8Y = -(height * 128);
        v->Constant = value;
        return (byte)id;
    }

    private static void Validate(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(width, MaxAxis);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(height, MaxAxis);
    }

    private static int Reserve()
    {
        if (_count >= MaxStamps) throw new InvalidOperationException("Stamp catalog is full.");
        return _count++;
    }
}

public static unsafe class Stamp
{
    public static byte New(sbyte* data, int width, int height)
        => StampCatalog.Bake(data, width, height);

    public static byte New(ReadOnlySpan<sbyte> data, int width, int height)
    {
        if (data.Length < width * height) throw new ArgumentException("Sample span is smaller than width*height.", nameof(data));
        fixed (sbyte* p = data) return StampCatalog.Bake(p, width, height);
    }

    public static byte Box(int width, int height, sbyte value)
        => StampCatalog.Box(width, height, value);
}
