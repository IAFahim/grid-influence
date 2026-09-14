using System.Numerics;

namespace GridInfluence;

public static class StampOrder
{
    private const int InsertionThreshold = 16;

    public static int Compare(in Stamp a, in Stamp b)
    {
        var c = CompareOrigin(in a, in b);
        if (c != 0) return c;

        c = CompareShapeIdentity(in a, in b);
        return c != 0 ? c : CompareShapeExtents(in a, in b);
    }

    public static void Sort(Span<Stamp> stamps)
    {
        if (stamps.Length < 2) return;

        var depth = 2 * (BitOperations.Log2((uint)stamps.Length) + 1);
        Introsort(stamps, depth);
    }

    private static void Introsort(Span<Stamp> stamps, int depth)
    {
        while (stamps.Length > InsertionThreshold)
        {
            if (depth == 0)
            {
                Heapsort(stamps);
                return;
            }

            depth--;
            var pivot = Partition(stamps);
            Introsort(stamps[(pivot + 1)..], depth);
            stamps = stamps[..pivot];
        }

        Insertion(stamps);
    }

    private static int Partition(Span<Stamp> stamps)
    {
        var length = stamps.Length;
        var mid = length >> 1;
        var last = length - 1;
        if (Compare(stamps[mid], stamps[0]) > 0) (stamps[0], stamps[mid]) = (stamps[mid], stamps[0]);
        if (Compare(stamps[last], stamps[0]) > 0) (stamps[0], stamps[last]) = (stamps[last], stamps[0]);
        if (Compare(stamps[last], stamps[mid]) < 0) (stamps[mid], stamps[last]) = (stamps[last], stamps[mid]);

        var pivot = mid;
        var lo = 0;
        var hi = last;
        while (lo <= hi)
        {
            while (Compare(stamps[lo], stamps[pivot]) < 0) lo++;
            while (Compare(stamps[hi], stamps[pivot]) > 0) hi--;
            if (lo > hi) break;

            (stamps[lo], stamps[hi]) = (stamps[hi], stamps[lo]);
            if (lo == pivot) pivot = hi;
            else if (hi == pivot) pivot = lo;
            lo++;
            hi--;
        }

        return hi;
    }

    private static void Insertion(Span<Stamp> stamps)
    {
        for (var i = 1; i < stamps.Length; i++)
        {
            var stamp = stamps[i];
            var j = i - 1;
            while (j >= 0 && Compare(stamps[j], stamp) > 0)
            {
                stamps[j + 1] = stamps[j];
                j--;
            }

            stamps[j + 1] = stamp;
        }
    }

    private static void Heapsort(Span<Stamp> stamps)
    {
        var length = stamps.Length;
        for (var i = length / 2 - 1; i >= 0; i--) SiftDown(stamps, i, length);

        for (var end = length - 1; end > 0; end--)
        {
            (stamps[0], stamps[end]) = (stamps[end], stamps[0]);
            SiftDown(stamps, 0, end);
        }
    }

    private static void SiftDown(Span<Stamp> stamps, int root, int end)
    {
        while (true)
        {
            var child = 2 * root + 1;
            if (child >= end) return;

            if (child + 1 < end && Compare(stamps[child], stamps[child + 1]) < 0) child++;

            if (Compare(stamps[root], stamps[child]) >= 0) return;

            (stamps[root], stamps[child]) = (stamps[child], stamps[root]);
            root = child;
        }
    }

    private static int CompareOrigin(in Stamp a, in Stamp b)
    {
        var c = a.Origin.X.CompareTo(b.Origin.X);
        return c != 0 ? c : a.Origin.Y.CompareTo(b.Origin.Y);
    }

    private static int CompareShapeIdentity(in Stamp a, in Stamp b)
    {
        var c = ((byte)a.Shape.Kind).CompareTo((byte)b.Shape.Kind);
        if (c != 0) return c;

        c = a.Shape.Weight.CompareTo(b.Shape.Weight);
        if (c != 0) return c;

        c = a.Shape.RectMin.X.CompareTo(b.Shape.RectMin.X);
        return c != 0 ? c : a.Shape.RectMin.Y.CompareTo(b.Shape.RectMin.Y);
    }

    private static int CompareShapeExtents(in Stamp a, in Stamp b)
    {
        var c = a.Shape.RectSize.X.CompareTo(b.Shape.RectSize.X);
        if (c != 0) return c;

        c = a.Shape.RectSize.Y.CompareTo(b.Shape.RectSize.Y);
        if (c != 0) return c;

        c = a.Shape.ShellThickness.CompareTo(b.Shape.ShellThickness);
        return c != 0 ? c : CompareShapeAngles(in a, in b);
    }

    private static int CompareShapeAngles(in Stamp a, in Stamp b)
    {
        var c = a.Shape.AnnulusInnerRadius.CompareTo(b.Shape.AnnulusInnerRadius);
        if (c != 0) return c;

        c = a.Shape.SectorDir1.X.CompareTo(b.Shape.SectorDir1.X);
        return c != 0 ? c : a.Shape.SectorDir1.Y.CompareTo(b.Shape.SectorDir1.Y);
    }
}
