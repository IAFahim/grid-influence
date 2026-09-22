using GridInfluence;

namespace GridInfluence.Io;

public readonly struct CellWeight
{
    public readonly Int2 Offset;
    public readonly int Weight;

    public CellWeight(Int2 offset, int weight)
    {
        Offset = offset;
        Weight = weight;
    }
}

public static class PaintedCanvas
{
    public static FieldStamp[] ToStamps(ReadOnlySpan<CellWeight> cells, Int2 origin)
    {
        var sorted = cells.ToArray();
        Array.Sort(sorted, static (a, b) =>
        {
            var c = a.Offset.Y.CompareTo(b.Offset.Y);
            return c != 0 ? c : a.Offset.X.CompareTo(b.Offset.X);
        });

        var stamps = new List<FieldStamp>(sorted.Length);
        var index = 0;
        while (index < sorted.Length)
        {
            var y = sorted[index].Offset.Y;
            var weight = sorted[index].Weight;
            if (weight == 0)
            {
                index++;
                continue;
            }

            var x0 = sorted[index].Offset.X;
            var x1 = x0;
            while (index + 1 < sorted.Length
                   && sorted[index + 1].Offset.Y == y
                   && sorted[index + 1].Offset.X == x1 + 1
                   && sorted[index + 1].Weight == weight)
            {
                x1 = sorted[++index].Offset.X;
            }

            stamps.Add(new FieldStamp(
                InfluenceShape.SolidRect(new Int2(x0, y), new Int2(x1 - x0 + 1, 1), (sbyte)Math.Clamp(weight, sbyte.MinValue, sbyte.MaxValue)),
                origin));
            index++;
        }

        return stamps.ToArray();
    }
}
