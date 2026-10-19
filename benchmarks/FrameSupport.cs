using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

internal sealed unsafe class FrameArena : IDisposable
{
    private readonly List<nint> _blocks = [];

    public void Dispose()
    {
        foreach (var block in _blocks) NativeMemory.AlignedFree((void*)block);
        _blocks.Clear();
    }

    public static long Padded(long count)
    {
        return Math.Max((count + 15) & ~15L, 16);
    }

    public T* Alloc<T>(long count) where T : unmanaged
    {
        var bytes = (nuint)(Padded(count) * sizeof(T));
        var block = NativeMemory.AlignedAlloc(bytes, 64);
        NativeMemory.Clear(block, bytes);
        _blocks.Add((nint)block);
        return (T*)block;
    }
}

internal struct FrameRandom(ulong seed)
{
    private ulong _state = seed;

    public ulong Next()
    {
        var z = _state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    public float Unit()
    {
        return (Next() >> 40) * (1f / 16777216f);
    }

    public float Signed()
    {
        return Unit() * 2f - 1f;
    }
}

internal static unsafe class FrameHash
{
    public const ulong Seed = 0xCBF29CE484222325UL;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Mix(ulong hash, ulong value)
    {
        return BitOperations.RotateLeft(hash ^ value, 29) * 0x9E3779B97F4A7C15UL + 0x165667B19E3779F9UL;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static ulong Block(ulong hash, void* data, long bytes)
    {
        var words = (ulong*)data;
        for (var i = 0L; i < bytes >> 3; i++) hash = Mix(hash, words[i]);
        return hash;
    }

    public static ulong Elements<T>(ulong hash, T* data, long count) where T : unmanaged
    {
        return Block(hash, data, FrameArena.Padded(count) * sizeof(T));
    }
}

internal static unsafe class FrameOrder
{
    private const int DigitBits = 11;
    private const int Digits = 1 << DigitBits;

    public static uint Morton(uint x, uint y)
    {
        return Spread(x) | (Spread(y) << 1);
    }

    private static uint Spread(uint value)
    {
        value &= 0xFFFF;
        value = (value | (value << 8)) & 0x00FF00FF;
        value = (value | (value << 4)) & 0x0F0F0F0F;
        value = (value | (value << 2)) & 0x33333333;
        value = (value | (value << 1)) & 0x55555555;
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static ulong* Sort(float* xs, float* ys, int count, int power, ulong* pairs, ulong* scratch, uint* counts)
    {
        var limit = (1 << power) - 1;
        for (var i = 0; i < count; i++)
        {
            var cx = (uint)Math.Clamp((int)xs[i], 0, limit);
            var cy = (uint)Math.Clamp((int)ys[i], 0, limit);
            pairs[i] = ((ulong)Morton(cx, cy) << 32) | (uint)i;
        }

        var from = pairs;
        var to = scratch;
        var passes = (2 * power + DigitBits - 1) / DigitBits;
        for (var pass = 0; pass < passes; pass++)
        {
            var shift = 32 + pass * DigitBits;
            new Span<uint>(counts, Digits).Clear();
            for (var i = 0; i < count; i++) counts[(int)((from[i] >> shift) & (Digits - 1))]++;
            var offset = 0u;
            for (var d = 0; d < Digits; d++)
            {
                var n = counts[d];
                counts[d] = offset;
                offset += n;
            }

            for (var i = 0; i < count; i++)
            {
                var item = from[i];
                to[counts[(int)((item >> shift) & (Digits - 1))]++] = item;
            }

            Swap(ref from, ref to);
        }

        return from;
    }

    public static void Swap<T>(ref T* left, ref T* right) where T : unmanaged
    {
        var held = left;
        left = right;
        right = held;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void Gather<T>(T* source, T* target, ulong* order, int count) where T : unmanaged
    {
        for (var i = 0; i < count; i++) target[i] = source[(uint)order[i]];
    }
}

internal static class FrameStats
{
    public static long Rank(long[] ascending, int percent)
    {
        var rank = (ascending.Length * percent + 99) / 100;
        return ascending[Math.Clamp(rank, 1, ascending.Length) - 1];
    }

    public static double TimestampOverheadNs()
    {
        const int rounds = 16;
        const int reads = 1 << 14;
        var toNs = 1e9 / Stopwatch.Frequency;
        var min = double.MaxValue;
        for (var r = 0; r < rounds; r++)
        {
            long last = 0;
            var start = Stopwatch.GetTimestamp();
            for (var i = 0; i < reads; i++) last = Stopwatch.GetTimestamp();
            var perRead = (double)(last - start) / reads * toNs;
            if (perRead < min) min = perRead;
        }

        return min;
    }
}

internal sealed class FrameJson
{
    private readonly StringBuilder _text = new();
    private bool _separate;

    public FrameJson BeginObject(string? key = null)
    {
        return Open(key, '{');
    }

    public FrameJson BeginArray(string key)
    {
        return Open(key, '[');
    }

    public FrameJson EndObject()
    {
        return Close('}');
    }

    public FrameJson EndArray()
    {
        return Close(']');
    }

    public FrameJson Text(string key, string value)
    {
        Prefix(key);
        Quote(value);
        return this;
    }

    public FrameJson Integer(string key, long value)
    {
        Prefix(key);
        _text.Append(value.ToString(CultureInfo.InvariantCulture));
        return this;
    }

    public FrameJson Number(string key, double value, int decimals)
    {
        Prefix(key);
        if (!double.IsFinite(value))
        {
            _text.Append("null");
            return this;
        }

        var rounded = Math.Round(value, decimals, MidpointRounding.AwayFromZero);
        if (rounded == 0) rounded = 0;
        _text.Append(rounded.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture),
            CultureInfo.InvariantCulture));
        return this;
    }

    public FrameJson Flag(string key, bool value)
    {
        Prefix(key);
        _text.Append(value ? "true" : "false");
        return this;
    }

    public FrameJson Null(string key)
    {
        Prefix(key);
        _text.Append("null");
        return this;
    }

    public override string ToString()
    {
        return _text.ToString();
    }

    private FrameJson Open(string? key, char bracket)
    {
        Prefix(key);
        _text.Append(bracket);
        _separate = false;
        return this;
    }

    private FrameJson Close(char bracket)
    {
        _text.Append(bracket);
        _separate = true;
        return this;
    }

    private void Prefix(string? key)
    {
        if (_separate) _text.Append(',');
        _separate = true;
        if (key == null) return;
        Quote(key);
        _text.Append(':');
    }

    private void Quote(string value)
    {
        _text.Append('"');
        foreach (var c in value)
            if (c == '"' || c == '\\') _text.Append('\\').Append(c);
            else if (c < 0x20 || c > 0x7E)
                _text.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
            else _text.Append(c);

        _text.Append('"');
    }
}

internal static class FrameHost
{
    public static string CpuModel()
    {
        try
        {
            foreach (var line in File.ReadLines("/proc/cpuinfo"))
                if (line.StartsWith("model name", StringComparison.Ordinal))
                    return line[(line.IndexOf(':') + 1)..].Trim();
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return RuntimeInformation.ProcessArchitecture.ToString();
    }

    public static double ResidentMegabytes(string field)
    {
        try
        {
            foreach (var line in File.ReadLines("/proc/self/status"))
            {
                if (!line.StartsWith(field + ":", StringComparison.Ordinal)) continue;
                var digits = new string(line.Where(char.IsDigit).ToArray());
                return long.Parse(digits, CultureInfo.InvariantCulture) / 1024.0;
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return double.NaN;
    }
}
