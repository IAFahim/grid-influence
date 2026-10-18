using System.Runtime.InteropServices;

namespace System.Runtime.InteropServices
{
    internal static unsafe class NativeMemory
    {
        public static void* AlignedAlloc(nuint byteCount, nuint alignment)
        {
            var raw = Marshal.AllocHGlobal((nint)(byteCount + alignment + (nuint)sizeof(nint)));
            var aligned = (nint)(((nuint)(nint)raw + (nuint)sizeof(nint) + alignment - 1) & ~(alignment - 1));
            ((nint*)aligned)[-1] = raw;
            return (void*)aligned;
        }

        public static void AlignedFree(void* pointer)
        {
            if (pointer != null) Marshal.FreeHGlobal(((nint*)pointer)[-1]);
        }

        public static void Clear(void* pointer, nuint byteCount)
        {
            new Span<byte>(pointer, checked((int)byteCount)).Clear();
        }
    }
}

namespace System.Numerics
{
    internal static class BitOperations
    {
        public static ulong RotateLeft(ulong value, int offset)
        {
            return (value << offset) | (value >> (64 - offset));
        }
    }
}

namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit
    {
    }

    [AttributeUsage(AttributeTargets.All, AllowMultiple = true, Inherited = false)]
    internal sealed class RequiredMemberAttribute : Attribute
    {
    }

    [AttributeUsage(AttributeTargets.All, AllowMultiple = true, Inherited = false)]
    internal sealed class CompilerFeatureRequiredAttribute : Attribute
    {
        public CompilerFeatureRequiredAttribute(string featureName)
        {
        }
    }
}

namespace System.Diagnostics.CodeAnalysis
{
    [AttributeUsage(AttributeTargets.Constructor, AllowMultiple = false, Inherited = false)]
    internal sealed class SetsRequiredMembersAttribute : Attribute
    {
    }
}

internal static class GiStopwatch
{
    public static TimeSpan GetElapsedTime(long start)
    {
        return GetElapsedTime(start, System.Diagnostics.Stopwatch.GetTimestamp());
    }

    public static TimeSpan GetElapsedTime(long start, long end)
    {
        return TimeSpan.FromSeconds((end - start) / (double)System.Diagnostics.Stopwatch.Frequency);
    }
}
