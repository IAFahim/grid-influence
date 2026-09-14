using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.InteropServices;
using System.Text;

namespace GridInfluence.Io;

public enum PnmMagic : byte
{
    GrayBinary = 5,
    RgbBinary = 6
}

public readonly struct PnmHeader
{
    public required PnmMagic Magic { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int MaxVal { get; init; }
    public required int DataOffset { get; init; }

    public int Channels => Magic == PnmMagic.RgbBinary ? 3 : 1;
    public int BytesPerSample => MaxVal > 255 ? 2 : 1;
    public long SampleCount => (long)Width * Height * Channels;
    public long DataLength => SampleCount * BytesPerSample;
}

public sealed unsafe class WeightMap : IDisposable
{
    private int* _samples;

    public int Width { get; }
    public int Height { get; }
    public int MaxVal { get; }
    public int Bias { get; }

    public WeightMap(int width, int height, int maxVal, int bias)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);
        Width = width;
        Height = height;
        MaxVal = maxVal;
        Bias = bias;
        _samples = (int*)NativeMemory.AlignedAlloc((nuint)Math.Max(1L, (long)width * height) * sizeof(int), 64);
    }

    public Span<int> Samples => new(_samples, Width * Height);

    public int* SamplePointer => _samples;

    public int this[int x, int y] => _samples[y * Width + x];

    public void Dispose()
    {
        if (_samples == null) return;

        NativeMemory.AlignedFree(_samples);
        _samples = null;
    }
}

public static class Pnm
{
    public static PnmHeader ParseHeader(ReadOnlySpan<byte> bytes)
    {
        var magic = bytes.Length >= 2 && bytes[0] == (byte)'P'
            ? bytes[1] - (byte)'0'
            : throw new InvalidDataException("Not a PNM file: missing P# magic.");
        if (magic is not ((int)PnmMagic.GrayBinary or (int)PnmMagic.RgbBinary))
            throw new InvalidDataException($"Unsupported PNM subtype P{magic}: only binary P5 and P6 are handled.");

        var offset = 2;
        var width = ReadInt(bytes, ref offset);
        var height = ReadInt(bytes, ref offset);
        var maxVal = ReadInt(bytes, ref offset);
        if (width <= 0 || height <= 0) throw new InvalidDataException($"PNM dimensions must be positive: {width}x{height}.");
        if (maxVal is <= 0 or > 65535) throw new InvalidDataException($"PNM maxval must be in 1..65535: {maxVal}.");
        if (offset >= bytes.Length || !IsWhitespace(bytes[offset]))
            throw new InvalidDataException("Malformed PNM header: expected a single whitespace before raster data.");
        offset++;

        return new PnmHeader
        {
            Magic = (PnmMagic)magic,
            Width = width,
            Height = height,
            MaxVal = maxVal,
            DataOffset = offset
        };
    }

    private static int ReadInt(ReadOnlySpan<byte> bytes, ref int offset)
    {
        SkipWhitespaceAndComments(bytes, ref offset);

        var value = 0;
        var digits = 0;
        while (offset < bytes.Length && bytes[offset] is >= (byte)'0' and <= (byte)'9')
        {
            value = value * 10 + (bytes[offset] - (byte)'0');
            offset++;
            digits++;
        }

        if (digits == 0) throw new InvalidDataException("Malformed PNM header: expected an integer.");

        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsWhitespace(byte b)
        => b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';

    private static void SkipWhitespaceAndComments(ReadOnlySpan<byte> bytes, ref int offset)
    {
        while (offset < bytes.Length)
        {
            var b = bytes[offset];
            if (b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            {
                offset++;
            }
            else if (b == (byte)'#')
            {
                while (offset < bytes.Length && bytes[offset] != (byte)'\n') offset++;
            }
            else
            {
                return;
            }
        }
    }

    public static unsafe WeightMap LoadWeights(string path, bool signed = true)
    {
        var bytes = File.ReadAllBytes(path);
        var header = ParseHeader(bytes);
        if (header.Magic != PnmMagic.GrayBinary)
            throw new InvalidDataException($"{path}: weight maps must be binary gray (P5), not P{((byte)header.Magic - (byte)'0')}.");

        var map = new WeightMap(header.Width, header.Height, header.MaxVal, signed ? SignedBias(header.MaxVal) : 0);
        DecodeGray(header, bytes, map.SamplePointer, map.Bias);
        return map;
    }

    public static int SignedBias(int maxVal) => (maxVal + 1) / 2;

    public static unsafe void DecodeGray(PnmHeader header, ReadOnlySpan<byte> bytes, int* destination, int bias)
    {
        if (header.Magic != PnmMagic.GrayBinary) throw new InvalidDataException("DecodeGray requires a P5 header.");

        fixed (byte* source = bytes)
        {
            var input = source + header.DataOffset;
            var count = header.Width * header.Height;
            if (header.BytesPerSample == 1) DecodeGray8(input, destination, count, bias);
            else DecodeGray16(input, destination, count, bias);
        }
    }

    private static unsafe void DecodeGray8(byte* input, int* destination, int count, int bias)
    {
        if (Vector128.IsHardwareAccelerated && count >= Vector128<byte>.Count)
        {
            var biasVector = Vector128.Create(-bias);
            var byteLanes = Vector128<byte>.Count;
            var intLanes = Vector128<int>.Count;
            var position = 0;
            for (; position <= count - byteLanes; position += byteLanes)
            {
                var loaded = Vector128.Load(input + position);
                var (low16, high16) = Vector128.Widen(loaded);
                StoreBias(low16, destination + position, biasVector);
                StoreBias(high16, destination + position + 2 * intLanes, biasVector);
            }

            for (; position < count; position++) destination[position] = input[position] - bias;
        }
        else
        {
            for (var i = 0; i < count; i++) destination[i] = input[i] - bias;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void StoreBias(Vector128<ushort> values, int* destination, Vector128<int> biasVector)
    {
        var (low32, high32) = Vector128.Widen(values);
        (low32.AsInt32() + biasVector).Store(destination);
        (high32.AsInt32() + biasVector).Store(destination + Vector128<int>.Count);
    }

    private static unsafe void DecodeGray16(byte* input, int* destination, int count, int bias)
    {
        for (var i = 0; i < count; i++)
            destination[i] = BinaryPrimitives.ReadUInt16BigEndian(new ReadOnlySpan<byte>(input + 2 * i, 2)) - bias;
    }

    public static void SaveGray(string path, ReadOnlySpan<int> samples, int width, int height, int maxVal = 65535)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(samples.Length, width * height);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxVal, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxVal, 65535);

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        var header = Encoding.ASCII.GetBytes($"P5\n{width} {height}\n{maxVal}\n");
        stream.Write(header);
        var payload = (long)width * height * (maxVal > 255 ? 2 : 1);
        var buffer = new byte[payload];
        unsafe
        {
            fixed (byte* target = buffer)
            fixed (int* source = samples)
            {
                if (maxVal > 255) EncodeGray16(source, target, width * height, maxVal);
                else EncodeGray8(source, target, width * height, maxVal);
            }
        }

        stream.Write(buffer);
    }

    public static void SaveGraySigned(string path, ReadOnlySpan<int> samples, int width, int height, int maxVal = 65535)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(samples.Length, width * height);

        var bias = SignedBias(maxVal);
        var biased = new int[samples.Length];
        for (var i = 0; i < samples.Length; i++) biased[i] = samples[i] + bias;

        SaveGray(path, biased, width, height, maxVal);
    }

    public static void SaveGray(ReadOnlySpan<int> samples, int width, int height, Stream stream, int maxVal = 65535)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(samples.Length, width * height);

        var header = Encoding.ASCII.GetBytes($"P5\n{width} {height}\n{maxVal}\n");
        stream.Write(header);
        var buffer = new byte[width * height * (maxVal > 255 ? 2 : 1)];
        unsafe
        {
            fixed (byte* target = buffer)
            fixed (int* source = samples)
            {
                if (maxVal > 255) EncodeGray16(source, target, width * height, maxVal);
                else EncodeGray8(source, target, width * height, maxVal);
            }
        }

        stream.Write(buffer);
    }

    private static unsafe void EncodeGray16(int* source, byte* target, int count, int maxVal)
    {
        for (var i = 0; i < count; i++)
        {
            var value = Math.Clamp(source[i], 0, maxVal);
            BinaryPrimitives.WriteUInt16BigEndian(new Span<byte>(target + 2 * i, 2), (ushort)value);
        }
    }

    private static unsafe void EncodeGray8(int* source, byte* target, int count, int maxVal)
    {
        for (var i = 0; i < count; i++) target[i] = (byte)Math.Clamp(source[i], 0, maxVal);
    }

    public static void SaveColorRgb(string path, ReadOnlySpan<int> samples, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(samples.Length, width * height);

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        var header = Encoding.ASCII.GetBytes($"P6\n{width} {height}\n255\n");
        stream.Write(header);
        var buffer = new byte[width * height * 3];
        for (var i = 0; i < samples.Length; i++)
        {
            var value = samples[i];
            var magnitude = Math.Min(Math.Abs(value), 255);
            var offset = 3 * i;
            if (value >= 0)
            {
                buffer[offset] = 0;
                buffer[offset + 1] = (byte)magnitude;
                buffer[offset + 2] = (byte)(magnitude >> 1);
            }
            else
            {
                buffer[offset] = (byte)magnitude;
                buffer[offset + 1] = 0;
                buffer[offset + 2] = (byte)(magnitude >> 1);
            }
        }

        stream.Write(buffer);
    }
}