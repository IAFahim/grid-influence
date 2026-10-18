using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using NUnit.Framework;
using Unity.Jobs.LowLevel.Unsafe;
using UnityEngine.Assemblies;
using GiWorld = Gi.World;

namespace Gi.Entities.Tests
{
    public sealed class GiRuntimeProbeTests
    {
        private static readonly string[] Accelerated =
        {
            "System.Runtime.Intrinsics.Vector128",
            "System.Runtime.Intrinsics.Vector256",
            "System.Runtime.Intrinsics.Vector512",
        };

        private static readonly string[] Instructions =
        {
            "System.Runtime.Intrinsics.X86.Sse41",
            "System.Runtime.Intrinsics.X86.Avx2",
            "System.Runtime.Intrinsics.X86.Avx512F",
            "System.Runtime.Intrinsics.X86.Bmi2",
            "System.Runtime.Intrinsics.X86.Popcnt",
            "System.Runtime.Intrinsics.Arm.AdvSimd",
        };

        [Test]
        public void RuntimeFacts()
        {
            var report = new StringBuilder();
            Fact(report, "framework", RuntimeInformation.FrameworkDescription);
            Fact(report, "environment version", Environment.Version.ToString());
            Fact(report, "core library", typeof(object).Assembly.GetName().Name);
            Fact(report, "mono runtime", Type.GetType("Mono.Runtime") != null ? "present" : "absent");
            Fact(report, "os", RuntimeInformation.OSDescription);
            Fact(report, "process architecture", RuntimeInformation.ProcessArchitecture.ToString());
            Fact(report, "define ENABLE_CORECLR", Defined("ENABLE_CORECLR"));
            Fact(report, "define ENABLE_IL2CPP", Defined("ENABLE_IL2CPP"));
            Fact(report, "define ENABLE_MONO", Defined("ENABLE_MONO"));
            Fact(report, "define UNITY_EDITOR", Defined("UNITY_EDITOR"));

            var gi = typeof(GiWorld).Assembly;
            Fact(report, "gi target framework", gi.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName ?? "none");
            Fact(report, "gi version", gi.GetName().Version?.ToString() ?? "none");

            foreach (var name in Accelerated) Fact(report, name + ".IsHardwareAccelerated", StaticBool(name, "IsHardwareAccelerated"));
            foreach (var name in Instructions) Fact(report, name + ".IsSupported", StaticBool(name, "IsSupported"));

            Fact(report, "job workers", JobsUtility.JobWorkerCount.ToString());
            Fact(report, "DOTNET_TieredCompilation", Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ?? "unset");
            Fact(report, "DOTNET_TieredPGO", Environment.GetEnvironmentVariable("DOTNET_TieredPGO") ?? "unset");
            Fact(report, "DOTNET_EnableHWIntrinsic", Environment.GetEnvironmentVariable("DOTNET_EnableHWIntrinsic") ?? "unset");
            Fact(report, "gc server", System.Runtime.GCSettings.IsServerGC.ToString());
            Fact(report, "gc latency", System.Runtime.GCSettings.LatencyMode.ToString());

            TestContext.Out.WriteLine(report.ToString());
            Assert.That(RuntimeInformation.FrameworkDescription, Is.Not.Empty);
        }

        private static void Fact(StringBuilder report, string name, string value)
        {
            report.Append("probe| ").Append(name).Append(" = ").Append(value).Append('\n');
        }

        private static string Defined(string symbol)
        {
            switch (symbol)
            {
                case "ENABLE_CORECLR":
#if ENABLE_CORECLR
                    return "yes";
#else
                    return "no";
#endif
                case "ENABLE_IL2CPP":
#if ENABLE_IL2CPP
                    return "yes";
#else
                    return "no";
#endif
                case "ENABLE_MONO":
#if ENABLE_MONO
                    return "yes";
#else
                    return "no";
#endif
                default:
#if UNITY_EDITOR
                    return "yes";
#else
                    return "no";
#endif
            }
        }

        private static string StaticBool(string typeName, string property)
        {
            var type = Type.GetType(typeName + ", System.Runtime.Intrinsics") ?? Type.GetType(typeName) ?? FindLoaded(typeName);
            if (type == null) return "type missing";
            var info = type.GetProperty(property, BindingFlags.Public | BindingFlags.Static);
            return info == null ? "property missing" : info.GetValue(null).ToString();
        }

        private static Type FindLoaded(string typeName)
        {
            foreach (var assembly in CurrentAssemblies.GetLoadedAssemblies())
            {
                var type = assembly.GetType(typeName);
                if (type != null) return type;
            }

            return null;
        }
    }
}
