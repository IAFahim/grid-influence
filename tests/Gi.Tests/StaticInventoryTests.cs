using System.Reflection;
using Xunit;

namespace Gi.Tests;

public sealed class StaticInventoryTests
{
    [Fact]
    public void MutableStaticsAreExactlyOneRoot()
    {
        var assembly = typeof(World).Assembly;
        var actual = new List<string>();
        foreach (var type in assembly.GetTypes())
        {
            foreach (var field in type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (field.IsLiteral || field.IsInitOnly)
                    continue;
                actual.Add(field.DeclaringType!.Name + "." + field.Name);
            }
        }

        actual.Sort(StringComparer.Ordinal);
        string[] expected = ["Runtime.Root"];
        Assert.Equal(expected, actual);
    }
}
