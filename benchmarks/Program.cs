using BenchmarkDotNet.Running;

namespace Benchmarks;

internal static class Program
{
    public static int Main(string[] arguments)
    {
        if (arguments.Length > 0 && arguments[0] == "--verify") return Verification.Run();

        if (arguments.Length > 1 && arguments[0] == "--pmu") return Pmu.Run(arguments[1]);

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(arguments);
        return 0;
    }
}
