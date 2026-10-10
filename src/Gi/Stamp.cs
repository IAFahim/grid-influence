namespace Gi;

internal enum StampKind : byte
{
    ConstantRectangle,
    Raster,
    Tent,
    Bell,
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
    public sbyte* Mips;
    public int MipCount;
}

internal static unsafe class StampCatalog
{
    internal const int MaxStamps = 256;
    private const int MaxAxis = 256;

    private static int _count = 1;

    internal static int Count => _count;

    internal static StampVariant* Get(byte id) => Runtime.Stamps + id;

    public static byte Bake(sbyte* data, int width, int height)
    {
        Validate(width, height);
        Runtime.Ensure();

        var uniform = true;
        var first = data[0];
        for (var i = 1; i < width * height; i++)
        {
            if (data[i] != first) { uniform = false; break; }
        }

        if (uniform) return Box(width, height, first);

        var id = Reserve();
        var v = Runtime.Stamps + id;
        var pitch = width + 2;
        var basePtr = (sbyte*)NativeHeap.AllocZeroed((nuint)(pitch * (height + 2)));
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
        BakeMips(v, basePtr);
        return (byte)id;
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
        => (sbyte)(sum < 0 ? -((-sum + (n >> 1)) / n) : (sum + (n >> 1)) / n);

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
        v->Width = width;
        v->Height = height;
        v->OriginQ8X = -(width * 128);
        v->OriginQ8Y = -(height * 128);
        v->Constant = value;
        return (byte)id;
    }

    private static void Validate(int width, int height)
    {
        if (width < 1 || width > MaxAxis) throw new ArgumentOutOfRangeException(nameof(width));
        if (height < 1 || height > MaxAxis) throw new ArgumentOutOfRangeException(nameof(height));
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

    public static byte Tent(int width, int height, sbyte value)
        => StampCatalog.Tent(width, height, value);

    public static byte Bell(int width, int height, sbyte value)
        => StampCatalog.Bell(width, height, value);
}
