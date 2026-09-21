using System.Numerics;

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
    public static unsafe int Score(FieldReader field, Int2 min, Int2 size)
    {
        var total = 0;
        var max = min + size;
        var spec = field.Spec;
        var chunks = ChunkMath.ChunkRangeOf(new CellRect(min, max), spec.Log2);
        for (var cy = chunks.Min.Y; cy <= chunks.Max.Y; cy++)
        for (var cx = chunks.Min.X; cx <= chunks.Max.X; cx++)
        {
            var chunkCoord = new Int2(cx, cy);
            if (!field.TryGetChunk(chunkCoord, out var view)) continue;

            var chunkBase = ChunkMath.ChunkBaseOf(chunkCoord, spec.Log2);
            var lx0 = Math.Max(0, min.X - chunkBase.X);
            var ly0 = Math.Max(0, min.Y - chunkBase.Y);
            var lx1 = Math.Min(view.Size, max.X - chunkBase.X);
            var ly1 = Math.Min(view.Size, max.Y - chunkBase.Y);
            var data = view.Data;
            var stride = view.Stride;
            for (var ly = ly0; ly < ly1; ly++)
            {
                var row = data + (long)ly * stride + lx0;
                var width = lx1 - lx0;
                var x = 0;
                if (Vector.IsHardwareAccelerated && width >= Vector<int>.Count)
                {
                    var lanes = Vector<int>.Count;
                    var acc = Vector<int>.Zero;
                    for (; x <= width - lanes; x += lanes)
                        acc += new Vector<int>(new ReadOnlySpan<int>(row + x, lanes));
                    for (var lane = 0; lane < lanes; lane++) total += acc[lane];
                }
                for (; x < width; x++) total += row[x];
            }
        }
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
