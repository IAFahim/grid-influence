using System.Reflection;
using Xunit;

namespace Gi.Tests;

public sealed class StaticInventoryTests
{
    [Fact]
    public void MutableStaticsAreExactlyTheElevenDocumented()
    {
        var assembly = typeof(World).Assembly;
        var actual = new List<string>();
        foreach (var type in assembly.GetTypes())
        {
            foreach (var field in type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (field.IsLiteral || field.IsInitOnly)
                    continue;
                var name = field.Name;
                if (name.StartsWith('<') && name.EndsWith(">k__BackingField", StringComparison.Ordinal))
                    name = name.Substring(1, name.IndexOf('>') - 1);
                actual.Add(field.DeclaringType!.Name + "." + name);
            }
        }

        actual.Sort(StringComparer.Ordinal);
        string[] expected =
        [
            "NativeHeap.Calls",
            "NativeHeap.HookAllocate",
            "NativeHeap.HookFree",
            "ProcessStages.Measuring",
            "Runtime.Stamps",
            "Runtime.WorldRevisions",
            "Runtime.Worlds",
            "Runtime.ZeroDense",
            "Runtime._revisionFloor",
            "StampCatalog.Count",
            "World._worldCount",
        ];
        Assert.Equal(expected, actual);
    }
}
