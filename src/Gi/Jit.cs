using System.Runtime.CompilerServices;

namespace Gi;

internal static class Jit
{
    internal const MethodImplOptions Hot = (MethodImplOptions)512;
    internal const MethodImplOptions Inline = MethodImplOptions.AggressiveInlining | Hot;
}
