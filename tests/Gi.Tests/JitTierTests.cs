using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;

namespace Gi.Tests;

public class JitTierTests
{
    private const BindingFlags Declared = BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic |
                                          BindingFlags.Static | BindingFlags.Instance;

    [Fact]
    public void EveryEngineMethodSkipsTieredCompilation()
    {
        var missing = new List<string>();
        foreach (var type in typeof(World).Assembly.GetTypes())
        {
            if (type.IsInterface || type.IsEnum || IsGenerated(type)) continue;
            if (type.FullName != null && type.FullName.StartsWith("System.", StringComparison.Ordinal)) continue;

            foreach (var method in type.GetMethods(Declared))
            {
                if (method.IsAbstract || IsGenerated(method) || IsAccessor(method)) continue;
                if (!Optimized(method.MethodImplementationFlags)) missing.Add(type.FullName + "." + method.Name);
            }

            foreach (var constructor in type.GetConstructors(Declared))
            {
                if (constructor.IsStatic || IsGenerated(constructor)) continue;
                if (!Optimized(constructor.MethodImplementationFlags)) missing.Add(type.FullName + "..ctor");
            }
        }

        missing.Sort(StringComparer.Ordinal);
        Assert.True(missing.Count == 0, "methods compiled through the tiers:\n" + string.Join("\n", missing));
    }

    private static bool Optimized(MethodImplAttributes flags)
    {
        return flags.HasFlag(MethodImplAttributes.AggressiveOptimization);
    }

    private static bool IsAccessor(MethodInfo method)
    {
        return method.IsSpecialName && (method.Name.StartsWith("get_", StringComparison.Ordinal) ||
                                        method.Name.StartsWith("set_", StringComparison.Ordinal));
    }

    private static bool IsGenerated(MemberInfo member)
    {
        return member.Name.Contains('<') || member.IsDefined(typeof(CompilerGeneratedAttribute), false);
    }
}
