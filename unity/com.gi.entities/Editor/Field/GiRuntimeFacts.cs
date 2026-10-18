using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Gi.Entities.Presentation;
using GiStamp = Gi.Stamp;
using GiWorld = Gi.World;

namespace Gi.Entities.Editor
{
    public static class GiRuntimeFacts
    {
        public const string NotAvailable = "n/a";

        public static List<KeyValuePair<string, string>> Collect(GiWorldInfo selected)
        {
            var assembly = typeof(GiWorld).Assembly;
            var framework = assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName ?? NotAvailable;
            var rows = new List<KeyValuePair<string, string>>
            {
                Row("Runtime", RuntimeInformation.FrameworkDescription),
                Row("Gi plugin", assembly.GetName().Name + " " + assembly.GetName().Version + " (" + framework + ")"),
                Row("Gi code path", framework.Contains("NETStandard") ? "scalar (no hardware intrinsics in this build)" : "hardware intrinsics where the CPU has them"),
                Row("Gi version", assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? NotAvailable),
                Row("CPU", Cpu()),
                Row("Live Gi worlds", GiOrphans.LiveWorlds().ToString(CultureInfo.InvariantCulture)),
                Row("Stamps used", DefinedStamps().ToString(CultureInfo.InvariantCulture) + " of 255"),
            };
            if (selected == null || !selected.Alive)
            {
                foreach (var name in new[] { "Faults", "Desync", "Store bytes", "Live tiles", "Pages", "Kernel cache entries", "Live sources" }) rows.Add(Row(name, NotAvailable));
                return rows;
            }

            rows.Add(Row("Faults", Faults(selected.Faults)));
            rows.Add(Row("Desync", selected.DesyncTick < 0 ? "none" : "first at tick " + selected.DesyncTick.ToString(CultureInfo.InvariantCulture)));
            GiWorld.Memory(selected.GiWorld, out var bytes, out var tiles, out var pages, out var kernels, out var sources);
            rows.Add(Row("Store bytes", bytes.ToString(CultureInfo.InvariantCulture)));
            rows.Add(Row("Live tiles", tiles.ToString(CultureInfo.InvariantCulture)));
            rows.Add(Row("Pages", pages.ToString(CultureInfo.InvariantCulture)));
            rows.Add(Row("Kernel cache entries", kernels.ToString(CultureInfo.InvariantCulture)));
            rows.Add(Row("Live sources", sources.ToString(CultureInfo.InvariantCulture)));
            return rows;
        }

        public static string Faults(ushort raised)
        {
            if (raised == 0) return "none";

            var names = new List<string>();
            foreach (GiFaultKind kind in System.Enum.GetValues(typeof(GiFaultKind)))
                if (kind != GiFaultKind.None && (raised & (ushort)kind) != 0)
                    names.Add(kind.ToString());
            return string.Join(", ", names);
        }

        public static int DefinedStamps()
        {
            var count = 0;
            for (var stamp = 1; stamp < 256; stamp++)
                if (GiStamp.TryGetSpec((byte)stamp, out _))
                    count++;
            return count;
        }

        private static string Cpu()
        {
            var features = new List<string>();
            foreach (var feature in new[]
                     {
                         ("System.Runtime.Intrinsics.X86.Avx2", "AVX2"), ("System.Runtime.Intrinsics.X86.Avx", "AVX"),
                         ("System.Runtime.Intrinsics.X86.Sse41", "SSE4.1"), ("System.Runtime.Intrinsics.X86.Sse2", "SSE2"),
                         ("System.Runtime.Intrinsics.Arm.AdvSimd", "NEON"),
                     })
                if (Supported(feature.Item1))
                    features.Add(feature.Item2);
            return RuntimeInformation.ProcessArchitecture + (features.Count == 0 ? " (no vector ISA reported)" : " " + string.Join(" ", features));
        }

        private static bool Supported(string typeName)
        {
            var type = typeof(object).Assembly.GetType(typeName);
            var property = type?.GetProperty("IsSupported", BindingFlags.Public | BindingFlags.Static);
            return property != null && property.GetValue(null) is bool supported && supported;
        }

        private static KeyValuePair<string, string> Row(string name, string value)
        {
            return new KeyValuePair<string, string>(name, value);
        }
    }
}
