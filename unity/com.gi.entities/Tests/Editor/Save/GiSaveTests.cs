using BovineLabs.Core.Collections;
using BovineLabs.Core.SingletonCollection;
using System.Linq;
using System.Reflection;
using BovineLabs.Savanna;
using Gi.Entities.Tests;
using Gi.Entities.Tests.Support;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using GiWorld = Gi.World;

namespace Gi.Entities.Editor.Tests
{
    public sealed class GiSaveTests
    {
        [SetUp]
        public void SetUp()
        {
            GiRuntime.CompleteAll();
            GiOrphans.Reclaim(GiRuntime.Stamps);
        }

        private static int LiveSources(GiEcsFixture fixture)
        {
            fixture.Complete();
            GiWorld.Memory(fixture.Frame.World, out _, out _, out _, out _, out var live);
            return live;
        }

        [Test]
        public void SavingLoadingAndContinuingEqualsAnUninterruptedRun()
        {
            var fadesSaved = 0;
            var pulsesSaved = 0;
            var rampsSaved = 0;
            foreach (var seed in new ulong[] { 3, 8, 12, 17 })
            foreach (var split in new[] { 9, 14, 21, 29, 37, 46, 55, 67, 80 })
            {
                var scenario = GiScenarioGenerator.Create(seed, new GiScenarioOptions(130, 50, 120, 4, true, true, true, true, 3));
                using var uninterrupted = new GiEcsExecutor(scenario, "SaveUninterrupted");
                for (var t = 0; t < split; t++) uninterrupted.Step(t);

                using var loaded = new GiEcsExecutor(scenario, "SaveLoaded");
                loaded.AdoptFrom(uninterrupted);
                var holder = loaded.Fixture.Manager.GetBuffer<GiSavedPulse>(GiSave.Holder(loaded.Fixture.Manager));
                pulsesSaved += holder.Length;
                using (var schedules = loaded.Fixture.Manager.CreateEntityQuery(ComponentType.ReadOnly<GiSavedSource>()))
                {
                    using var saved = schedules.ToComponentDataArray<GiSavedSource>(Allocator.Temp);
                    rampsSaved += saved.Length;
                    foreach (var schedule in saved)
                        if (schedule.FadeTicks > 0)
                            fadesSaved++;
                }

                var aliveBefore = LiveSources(uninterrupted.Fixture);
                for (var t = split; t < scenario.Ticks.Length; t++)
                {
                    var expected = uninterrupted.Step(t);
                    var actual = loaded.Step(t);
                    Assert.That(actual, Is.EqualTo(expected), "seed " + seed + " split " + split + " tick " + t);
                    if (t == split) Assert.That(LiveSources(loaded.Fixture), Is.EqualTo(LiveSources(uninterrupted.Fixture)), "seed " + seed + " split " + split);
                }

                Assert.That(aliveBefore, Is.GreaterThan(0));
            }

            Assert.That(pulsesSaved, Is.GreaterThan(5), "pulses in flight when saved");
            Assert.That(rampsSaved, Is.GreaterThan(10), "ramped emitters when saved");
            Assert.That(fadesSaved, Is.GreaterThan(3), "ramps in flight when saved");
        }

        [Test]
        public void PulsesComeBackWithTheirRemainingTicks()
        {
            var scenario = GiScenarioGenerator.Create(5, new GiScenarioOptions(60, 20, 40, 2, true, false, true, true, 2));
            using var live = new GiEcsExecutor(scenario, "PulseLive");
            for (var t = 0; t < 30; t++) live.Step(t);

            using var loaded = new GiEcsExecutor(scenario, "PulseLoaded");
            loaded.AdoptFrom(live);
            var saved = loaded.Fixture.Manager.GetBuffer<GiSavedPulse>(GiSave.Holder(loaded.Fixture.Manager)).ToNativeArray(Allocator.Temp);
            Assert.That(saved.Length, Is.GreaterThan(0));
            loaded.Fixture.Frame1();
            var mirror = GiSave.Entries(loaded.Fixture.Manager, loaded.Fixture.FrameEntity);
            Assert.That(mirror.Length, Is.GreaterThanOrEqualTo(saved.Length));
            var left = new System.Collections.Generic.List<int>();
            foreach (var entry in mirror)
            {
                GiWorld.TryGetSchedule(loaded.Fixture.Frame.World, entry.Id, out _, out _, out _, out _, out _, out var remaining);
                left.Add(remaining + 1);
            }

            foreach (var pulse in saved) Assert.That(left, Does.Contain(pulse.ExpireTicksLeft));
            saved.Dispose();
            for (var t = 0; t < 14; t++) loaded.Fixture.Frame1();
            Assert.That(GiSave.Entries(loaded.Fixture.Manager, loaded.Fixture.FrameEntity).Length, Is.LessThan(mirror.Length + 1));
        }

        [Test]
        public void TheSavedComponentsCarryTheSaveAttributeAndTheCleanupSourceDoesNot()
        {
            foreach (var type in new[]
                     {
                         typeof(GiEmitter), typeof(GiSplat), typeof(GiGainRamp), typeof(GiOriented), typeof(GiScaled), typeof(GiSavedSource),
                         typeof(GiSavedPulse), typeof(GiSaveHolder),
                     })
                Assert.That(type.GetCustomAttribute<SaveAttribute>(), Is.Not.Null, type.Name);
            Assert.That(typeof(GiSource).GetCustomAttribute<SaveAttribute>(), Is.Null);
            Assert.That(typeof(GiFault).GetCustomAttribute<SaveAttribute>(), Is.Null);
        }

        [Test]
        public void PrepareAndReleaseLeaveTheLiveRunUntouched()
        {
            var scenario = GiScenarioGenerator.Create(6, new GiScenarioOptions(50, 30, 60, 2, true, true, true, true, 2));
            using var reference = new GiEcsExecutor(scenario, "PrepareReference");
            using var prepared = new GiEcsExecutor(scenario, "PrepareLive");
            for (var t = 0; t < 25; t++)
            {
                reference.Step(t);
                prepared.Step(t);
            }

            Assert.That(GiSave.Prepare(prepared.Fixture.Manager), Is.GreaterThan(0));
            for (var t = 25; t < 40; t++)
                Assert.That(prepared.Step(t), Is.EqualTo(reference.Step(t)), "tick " + t);

            GiSave.Release(prepared.Fixture.Manager);
            using (var query = prepared.Fixture.Manager.CreateEntityQuery(ComponentType.ReadOnly<GiSavedSource>()))
                Assert.That(query.IsEmptyIgnoreFilter, Is.True);
            Assert.That(prepared.Fixture.Manager.GetBuffer<GiSavedPulse>(GiSave.Holder(prepared.Fixture.Manager)).Length, Is.EqualTo(0));
            for (var t = 40; t < 50; t++)
                Assert.That(prepared.Step(t), Is.EqualTo(reference.Step(t)), "tick " + t);
        }

        [Test]
        public void TheMirrorStaysSmallUnderConstantPulsing()
        {
            using var fixture = new GiEcsFixture("PulseMirror");
            fixture.SetStandardProfile();
            fixture.Frame1();
            var singleton = fixture.Manager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadWrite<GiPulseSingleton>() },
                Options = EntityQueryOptions.IncludeSystems,
            }).GetSingleton<GiPulseSingleton>();
            var peak = 0;
            for (var t = 0; t < 400; t++)
            {
                var writer = singleton.CreateThreadStream();
                for (var i = 0; i < 6; i++)
                    writer.Write(new GiPulse
                    {
                        Layer = new GiLayerKey(0),
                        Stamp = new GiStampKey((byte)(i % 3)),
                        X = 10 + (t + i * 7) % 40,
                        Y = 12 + (t * 3 + i) % 40,
                        Scale = 1f,
                        Gain = 3,
                        Ticks = (ushort)(2 + i),
                    });
                fixture.Frame1();
                peak = System.Math.Max(peak, GiSave.Entries(fixture.Manager, fixture.FrameEntity).Length);
            }

            Assert.That(peak, Is.LessThan(2 * GiPulseMirror.PruneAbove + 12));
        }
    }
}
