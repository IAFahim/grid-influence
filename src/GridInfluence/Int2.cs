using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace GridInfluence;

[StructLayout(LayoutKind.Sequential)]
public readonly struct Int2 : IEquatable<Int2>
{
    public readonly int X;
    public readonly int Y;

    public Int2(int x, int y)
    {
        X = x;
        Y = y;
    }

    public static Int2 Zero => default;

    public int this[int index] => index == 0 ? X : Y;

    public static Int2 Min(Int2 left, Int2 right) => new(Math.Min(left.X, right.X), Math.Min(left.Y, right.Y));

    public static Int2 Max(Int2 left, Int2 right) => new(Math.Max(left.X, right.X), Math.Max(left.Y, right.Y));

    public static Int2 operator +(Int2 left, Int2 right) => new(left.X + right.X, left.Y + right.Y);

    public static Int2 operator -(Int2 left, Int2 right) => new(left.X - right.X, left.Y - right.Y);

    public static Int2 operator -(Int2 value) => new(-value.X, -value.Y);

    public static Int2 operator *(Int2 value, int scalar) => new(value.X * scalar, value.Y * scalar);

    public static bool operator ==(Int2 left, Int2 right) => left.X == right.X && left.Y == right.Y;

    public static bool operator !=(Int2 left, Int2 right) => left.X != right.X || left.Y != right.Y;

    public static Int2 Clamp(Int2 value, Int2 min, Int2 max)
        => new(Math.Clamp(value.X, min.X, max.X), Math.Clamp(value.Y, min.Y, max.Y));

    public bool Equals(Int2 other) => X == other.X && Y == other.Y;

    public override bool Equals(object? obj) => obj is Int2 other && Equals(other);

    public override int GetHashCode() => unchecked((int)((uint)X * 0x9E3779B1u ^ (uint)Y));

    public override string ToString() => $"({X}, {Y})";

    public void Deconstruct(out int x, out int y)
    {
        x = X;
        y = Y;
    }
}
