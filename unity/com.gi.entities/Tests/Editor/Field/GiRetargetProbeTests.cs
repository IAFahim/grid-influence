using System.Linq;
using System.Reflection;
using System.Runtime.Versioning;
using NUnit.Framework;
using GiWorld = Gi.World;

namespace Gi.Entities.Editor.Tests
{
    public sealed class GiRetargetProbeTests
    {
        [Test]
        public void TheEditorLoadsTheCoreclrVariant()
        {
            var location = typeof(GiWorld).Assembly.Location.Replace('\\', '/');
            Assert.That(location, Does.Contain("/Plugins/Gi/coreclr/Gi.dll"), location);
        }

        [Test]
        public void NoReferenceEscapesToFramework10()
        {
            var modern = typeof(GiWorld).Assembly.GetReferencedAssemblies().Where(r => r.Version.Major >= 10).Select(r => r.FullName).ToArray();
            Assert.That(modern, Is.Empty);
        }

        [Test]
        public void TheRetargetedAssemblyClaimsNetstandard21()
        {
            var framework = typeof(GiWorld).Assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName;
            Assert.That(framework, Is.EqualTo(".NETStandard,Version=v2.1"));
        }

        [Test]
        public void TheVectorPathIsLive()
        {
            var vector = typeof(object).Assembly.GetType("System.Runtime.Intrinsics.Vector128");
            var flag = vector?.GetProperty("IsHardwareAccelerated", BindingFlags.Public | BindingFlags.Static);
            Assert.That(flag?.GetValue(null), Is.EqualTo(true));
        }
    }
}
