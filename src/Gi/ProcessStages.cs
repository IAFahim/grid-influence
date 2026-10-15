using System.Diagnostics;

namespace Gi;

internal static class ProcessStages
{
    internal static bool Measuring;
    internal static long PrepareTicks;
    internal static long ResolveTicks;

    internal static void Reset()
    {
        PrepareTicks = 0;
        ResolveTicks = 0;
    }

    internal static long Mark() => Measuring ? Stopwatch.GetTimestamp() : 0L;

    internal static void Measure(ref long stage, long stamp)
    {
        if (Measuring) stage += Stopwatch.GetTimestamp() - stamp;
    }
}
