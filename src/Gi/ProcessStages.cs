using System.Runtime.CompilerServices;
using System.Diagnostics;

namespace Gi;

internal struct StageTicks
{
    public long Prepare;
    public long Apply;
}

internal static unsafe class ProcessStages
{
    internal static bool Measuring;

    internal static long PrepareTicks => Total(0);

    internal static long ApplyTicks => Total(1);

    internal static long ResolveTicks => 0L;

    [MethodImpl(Jit.Hot)]
    internal static void Reset()
    {
        if (Runtime.Worlds == null) return;

        for (var i = 0; i < World.MaxWorlds; i++) Runtime.Worlds[i].Stages = default;
    }

    [MethodImpl(Jit.Hot)]
    internal static long Mark()
    {
        return Measuring ? Stopwatch.GetTimestamp() : 0L;
    }

    [MethodImpl(Jit.Hot)]
    internal static void Measure(ref long stage, long stamp)
    {
        if (Measuring) stage += Stopwatch.GetTimestamp() - stamp;
    }

    [MethodImpl(Jit.Hot)]
    private static long Total(int stage)
    {
        if (Runtime.Worlds == null) return 0;

        var total = 0L;
        for (var i = 0; i < World.MaxWorlds; i++)
        {
            var ticks = Runtime.Worlds[i].Stages;
            total += stage == 0 ? ticks.Prepare : ticks.Apply;
        }

        return total;
    }
}
