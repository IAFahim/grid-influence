using System.Reflection;
using System.Runtime.Versioning;
using Gi.Entities.Tests.Support;
using NUnit.Framework;
using GiWorld = Gi.World;

namespace Gi.Entities.Tests
{
    public sealed class GiVariantParityTests
    {
        [SetUp]
        public void SetUp()
        {
            GiRuntime.CompleteAll();
            GiOrphans.Reclaim(GiRuntime.Stamps);
        }

        [Test]
        public void VariantIdentityIsLogged()
        {
            var assembly = typeof(GiWorld).Assembly;
            var location = assembly.Location.Replace('\\', '/');
            var framework = assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName ?? "none";
            var vector = typeof(object).Assembly.GetType("System.Runtime.Intrinsics.Vector128");
            var flag = vector?.GetProperty("IsHardwareAccelerated", BindingFlags.Public | BindingFlags.Static);
            TestContext.Out.WriteLine("parity| location=" + location);
            TestContext.Out.WriteLine("parity| framework=" + framework);
#if ENABLE_CORECLR
            TestContext.Out.WriteLine("parity| ENABLE_CORECLR=yes");
#else
            TestContext.Out.WriteLine("parity| ENABLE_CORECLR=no");
#endif
            TestContext.Out.WriteLine("parity| Vector128.IsHardwareAccelerated=" + flag?.GetValue(null));
            TestContext.Out.WriteLine("parity| gi=" + assembly.GetName().Version);
            Assert.That(location, Does.Contain("/Plugins/Gi/"));
        }

        [Test]
        public void DeterminismSpotCheckMatchesOracleAndLogsDigest()
        {
            var combined = 14695981039346656037ul;
            for (ulong seed = 1; seed <= 3; seed++)
            {
                var scenario = GiScenarioGenerator.Create(seed, GiScenarioOptions.Standard);
                var expected = GiReferenceExecutor.Run(scenario);
                using var plain = new GiEcsExecutor(scenario, "Plain");
                using var shuffled = new GiEcsExecutor(scenario.WithOpOrderShuffled(seed * 31), "Shuffled");
                var a = plain.Run();
                var b = shuffled.Run();
                Assert.That(a.Compare(b), Is.EqualTo(string.Empty), "seed " + seed + " plain vs shuffled");
                Assert.That(expected.Compare(a), Is.EqualTo(string.Empty), "seed " + seed + " oracle vs plain");
                combined = Mix(combined, a.Digest());
                TestContext.Out.WriteLine("parity| seed=" + seed + " digest=" + a.Digest().ToString("x16", System.Globalization.CultureInfo.InvariantCulture));
            }

            TestContext.Out.WriteLine("parity| combined=" + combined.ToString("x16", System.Globalization.CultureInfo.InvariantCulture));
        }

        private static ulong Mix(ulong hash, ulong value)
        {
            unchecked
            {
                for (var i = 0; i < 8; i++)
                {
                    hash ^= (byte)(value & 0xFF);
                    hash *= 1099511628211ul;
                    value >>= 8;
                }

                return hash;
            }
        }
    }
}
