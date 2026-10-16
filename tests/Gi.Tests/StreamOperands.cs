namespace Gi.Tests;

internal static class StreamOperands
{
    public const ulong AuxSalt = 0xD1B54A32D192ED03ul;

    public static float CoordNear(ref SplitMix64 rng, float origin, float size)
    {
        if (rng.Chance(4)) return float.NaN;
        if (rng.Chance(10)) return origin + size * (1f + rng.NextUnit());
        return origin + rng.NextUnit() * size;
    }

    public static void Rect(ref SplitMix64 rng, int size, int width, out int x, out int y, out int w, out int h)
    {
        x = rng.NextInt(-4, size + 4);
        y = rng.NextInt(-4, size + 4);
        w = Math.Max(1, width);
        h = 1 + rng.NextInt(0, size + 2);
    }

    public static void ColumnPositions(ref SplitMix64 rng, in GridSpec target, float[] xs, float[] ys, int count)
    {
        for (var i = 0; i < count; i++)
        {
            xs[i] = CoordNear(ref rng, target.X, target.Size);
            ys[i] = CoordNear(ref rng, target.Y, target.Size);
        }
    }
}

internal struct CorpusHash
{
    public const ulong Offset = 14695981039346656037ul;
    private const ulong Prime = 1099511628211ul;
    private ulong _hash;

    public CorpusHash(ulong seed) => _hash = seed;

    public ulong Value => _hash;

    public void Mix(byte value) => _hash = (_hash ^ value) * Prime;

    public void Mix(bool value) => Mix(value ? (byte)1 : (byte)0);

    public void Mix(short value)
    {
        Mix((byte)(value & 0xFF));
        Mix((byte)((value >> 8) & 0xFF));
    }

    public void Mix(int value)
    {
        Mix((byte)(value & 0xFF));
        Mix((byte)((value >> 8) & 0xFF));
        Mix((byte)((value >> 16) & 0xFF));
        Mix((byte)((value >> 24) & 0xFF));
    }

    public void Mix(long value)
    {
        Mix((int)(value & 0xFFFFFFFF));
        Mix((int)((ulong)value >> 32));
    }

    public void Mix(float value) => Mix(BitConverter.SingleToInt32Bits(value));

    public void Mix(short[] values, int count)
    {
        Mix(count);
        for (var i = 0; i < count; i++) Mix(values[i]);
    }

    public void Mix(float[] values, int count)
    {
        Mix(count);
        for (var i = 0; i < count; i++) Mix(values[i]);
    }

    public void Mix(int[] values, int count)
    {
        Mix(count);
        for (var i = 0; i < count; i++) Mix(values[i]);
    }

    public void Mix(ulong[] values, int count)
    {
        Mix(count);
        for (var i = 0; i < count; i++) Mix(unchecked((long)values[i]));
    }
}
