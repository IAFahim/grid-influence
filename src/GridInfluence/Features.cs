namespace GridInfluence;

public static class Territory
{
    public static int Controller(FieldReader field, Int2 cell)
    {
        var value = field.ReadCell(cell);
        return value == 0 ? 0 : value > 0 ? 1 : -1;
    }

    public static bool IsFrontline(FieldReader field, Int2 cell, int band)
        => Math.Abs(field.ReadCell(cell)) <= band;
}

public static class Vision
{
    public static bool IsSeen(FieldReader field, Int2 cell) => field.ReadCell(cell) > 0;

    public static bool InShadow(FieldReader field, Int2 cell) => field.ReadCell(cell) == 0;
}

public static class Capture
{
    public static int Score(FieldReader field, Int2 min, Int2 size)
    {
        var total = 0;
        for (var y = 0; y < size.Y; y++)
        for (var x = 0; x < size.X; x++)
            total += field.ReadCell(new Int2(min.X + x, min.Y + y));

        return total;
    }
}

public static class FlowSteering
{
    public static Int2 Direction(FieldReader field, Int2 cell) => -field.Gradient(cell);
}

public static class Placement
{
    public static bool IsValid(FieldReader field, Int2 min, Int2 size, int maxTolerance)
    {
        for (var y = 0; y < size.Y; y++)
        for (var x = 0; x < size.X; x++)
        {
            if (field.ReadCell(new Int2(min.X + x, min.Y + y)) > maxTolerance) return false;
        }

        return true;
    }
}
