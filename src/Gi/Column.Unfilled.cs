namespace Gi;

public static unsafe partial class World
{
    private static void ClearRows<T>(T* rows, int count) where T : unmanaged
    {
        if (rows != null && count > 0) new Span<T>(rows, count).Clear();
    }

    private static void ClearBits(ulong* words, int count)
    {
        if (words != null && count > 0) new Span<ulong>(words, (int)(((uint)count + 63) >> 6)).Clear();
    }

    private static bool Unfilled(short* values, ulong* whole, int count)
    {
        ClearRows(values, count);
        ClearBits(whole, count);
        return false;
    }

    private static bool UnfilledGradient(float* gradientXs, float* gradientYs, ulong* whole, int count)
    {
        ClearRows(gradientXs, count);
        ClearRows(gradientYs, count);
        ClearBits(whole, count);
        return false;
    }

    private static bool UnfilledArea(long* totals, ulong* whole, int count)
    {
        ClearRows(totals, count);
        ClearBits(whole, count);
        return false;
    }

    private static bool UnfilledMax(short* values, float* bestXs, float* bestYs, ulong* whole, int count)
    {
        ClearRows(values, count);
        ClearRows(bestXs, count);
        ClearRows(bestYs, count);
        ClearBits(whole, count);
        return false;
    }

    private static bool UnfilledNearest(short* values, float* nearestXs, float* nearestYs, ulong* found, int count)
    {
        if (values != null && count > 0) new Span<short>(values, count).Fill(short.MinValue);
        ClearRows(nearestXs, count);
        ClearRows(nearestYs, count);
        ClearBits(found, count);
        return false;
    }
}
