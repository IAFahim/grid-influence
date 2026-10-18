using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using BovineLabs.Core.ConfigVars;
using BovineLabs.Core.Utility;
using NUnit.Framework;
using Unity.Profiling;

namespace Gi.Entities.Editor.Tests
{
    public sealed class GiProfilerTests
    {
        private static readonly Regex GiName = new Regex(@"^gi(\.[a-z0-9]+(-[a-z0-9]+)*)+$");
        private static readonly Regex BovineName = new Regex("^[a-z_+-][a-z0-9_+.-]*$");

        [Test]
        public void EveryCounterInGiCountersShowsInTheModule()
        {
            var fields = typeof(GiCounters).GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(field => field.FieldType.IsGenericType && field.FieldType.GetGenericTypeDefinition() == typeof(ProfilerCounterValue<>))
                .ToList();
            Assert.That(fields.Count, Is.EqualTo(GiCounters.Names.Length));
            Assert.That(GiProfilerModule.CounterNames(), Is.EqualTo(GiCounters.Names));
            Assert.That(GiCounters.Names, Is.Unique);
            foreach (var name in GiCounters.Names) Assert.That(name, Does.StartWith("Gi "));
        }

        [Test]
        public void EverySystemMarkerHasAUniqueGiName()
        {
            var markers = typeof(GiMarkers).GetFields(BindingFlags.Public | BindingFlags.Static).Count(field => field.FieldType == typeof(ProfilerMarker));
            Assert.That(markers, Is.EqualTo(GiMarkers.Names.Length));
            Assert.That(GiMarkers.Names, Is.Unique);
            foreach (var name in GiMarkers.Names) Assert.That(name, Does.StartWith("Gi."));
        }

        [Test]
        public void EveryGiConfigVarFollowsTheNamingPattern()
        {
            var names = ConfigVarManager.FindAllConfigVars().Select(found => found.ConfigVar.Name).Where(name => name.StartsWith("gi.")).ToList();
            foreach (var name in new[] { "gi.sense.batch", "gi.mirror.budget", "gi.runtime.keep-orphans", "gi.guard", "gi.draw.heatmap" })
                Assert.That(names, Does.Contain(name));
            foreach (var name in names)
            {
                Assert.That(GiName.IsMatch(name), Is.True, name);
                Assert.That(BovineName.IsMatch(name), Is.True, name);
            }

            Assert.That(names, Is.Unique);
        }

        [Test]
        public void ACommandLineOverrideTakesEffect()
        {
            var args = (List<string>)typeof(CommandLineArgs).GetField("Args", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            var manager = typeof(ConfigVarManager);
            var shutdown = manager.GetMethod("Shutdown", BindingFlags.NonPublic | BindingFlags.Static);
            var initialize = manager.GetMethod("Initialize", BindingFlags.NonPublic | BindingFlags.Static);
            var original = GiSenseConfig.Batch.Data;
            var originalBudget = Presentation.GiMirrorConfig.Budget.Data;
            try
            {
                shutdown.Invoke(null, null);
                args.Add("-gi.sense.batch");
                args.Add("192");
                args.Add("-gi.mirror.budget");
                args.Add("7");
                initialize.Invoke(null, null);
                Assert.That(GiSenseConfig.Batch.Data, Is.EqualTo(192));
                Assert.That(Presentation.GiMirrorConfig.Budget.Data, Is.EqualTo(7));
            }
            finally
            {
                args.RemoveRange(args.Count - 4, 4);
                GiSenseConfig.Batch.Data = original;
                Presentation.GiMirrorConfig.Budget.Data = originalBudget;
                shutdown.Invoke(null, null);
                initialize.Invoke(null, null);
            }

            Assert.That(GiSenseConfig.Batch.Data, Is.EqualTo(original));
        }

        [Test]
        public void TheGuardConfigVarSwitchesTheGuardOff()
        {
            var tag = GiGuard.Register("ConfigVarProbe");
            var original = GiDebugConfig.Guard.Data;
            try
            {
                GiDebugConfig.Guard.Data = false;
                var before = GiGuard.Violations;
                GiGuard.Begin(200, tag, true);
                GiGuard.Begin(200, tag, true);
                GiGuard.End(200, true);
                GiGuard.End(200, true);
                Assert.That(GiGuard.Violations, Is.EqualTo(before));

                GiDebugConfig.Guard.Data = true;
                GiGuard.Begin(201, tag, true);
                GiGuard.Begin(201, tag, true);
                GiGuard.End(201, true);
                GiGuard.End(201, true);
                Assert.That(GiGuard.Violations, Is.EqualTo(before + 1));
                GiGuard.TryTake(out _);
            }
            finally
            {
                GiDebugConfig.Guard.Data = original;
            }
        }
    }
}
