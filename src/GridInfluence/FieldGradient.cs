using System.Numerics;

namespace GridInfluence;

public static class FieldGradient
{
    public static Int2 Ascent(FieldReader field, Int2 cell) => field.Gradient(cell);

    public static Int2 Descent(FieldReader field, Int2 cell) => -field.Gradient(cell);

    public static Int2 Step(Int2 gradient)
        => new(Math.Clamp(gradient.X, -1, 1), Math.Clamp(gradient.Y, -1, 1));

    public static Vector2 Normalized(Int2 gradient)
    {
        var x = (float)gradient.X;
        var y = (float)gradient.Y;
        var lengthSquared = x * x + y * y;
        return lengthSquared > 0f
            ? new Vector2(x, y) * (1f / MathF.Sqrt(lengthSquared))
            : Vector2.Zero;
    }
}
