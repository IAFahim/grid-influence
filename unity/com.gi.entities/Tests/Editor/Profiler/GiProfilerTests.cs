using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Unity.Profiling;

namespace Gi.Entities.Editor.Tests
{
    public sealed class GiProfilerTests
    {
        private static readonly Regex GiName = new Regex(@"^gi(\.[a-z0-9]+(-[a-z0-9]+)*)+$");
        private static readonly Regex ValidConfigName = new Regex("^[a-z_+-][a-z0-9_+.-]*$");

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
            var names = GiConfigVarManager.FindAll().Select(found => found.ConfigVar.Name).Where(name => name.StartsWith("gi.")).ToList();
            foreach (var name in new[] { "gi.sense.batch", "gi.mirror.budget", "gi.runtime.keep-orphans", "gi.guard", "gi.draw.heatmap" })
                Assert.That(names, Does.Contain(name));
            foreach (var name in names)
            {
                Assert.That(GiName.IsMatch(name), Is.True, name);
                Assert.That(ValidConfigName.IsMatch(name), Is.True, name);
            }

            Assert.That(names, Is.Unique);
        }

        [Test]
        public void ACommandLineOverrideTakesEffect()
        {
            var original = GiSenseConfig.Batch.Data;
            var originalBudget = Presentation.GiMirrorConfig.Budget.Data;
            try
            {
                GiConfigVarManager.Initialize(new[] { "-gi.sense.batch", "192", "-gi.mirror.budget", "7" });
                Assert.That(GiSenseConfig.Batch.Data, Is.EqualTo(192));
                Assert.That(Presentation.GiMirrorConfig.Budget.Data, Is.EqualTo(7));
            }
            finally
            {
                GiConfigVarManager.Initialize(Array.Empty<string>());
                GiSenseConfig.Batch.Data = original;
                Presentation.GiMirrorConfig.Budget.Data = originalBudget;
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
