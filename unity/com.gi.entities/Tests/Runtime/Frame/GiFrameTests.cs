using System;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using Gi.Entities.Tests.Support;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using GiStamp = Gi.Stamp;
using GiWorld = Gi.World;

namespace Gi.Entities.Tests
{
    public sealed class GiFrameTests
    {
        private static int DefinedStamps()
        {
            var count = 0;
            for (var stamp = 1; stamp < 256; stamp++)
                if (GiStamp.TryGetSpec((byte)stamp, out _))
                    count++;
            return count;
        }

        [SetUp]
        public void SetUp()
        {
            GiRuntime.CompleteAll();
            GiOrphans.Reclaim(GiRuntime.Stamps);
        }

        [Test]
        public void AProfileBuildsOneWorldAndEachUpdateProcessesOnce()
        {
            using var fixture = new GiEcsFixture();
            Assert.That(fixture.Frame.Alive, Is.False);

            fixture.SetStandardProfile();
            fixture.Tick();
            var frame = fixture.Frame;
            Assert.That(frame.Alive, Is.True);
            Assert.That(frame.Generation, Is.EqualTo(1u));
            Assert.That(frame.Tick, Is.EqualTo(1));
            Assert.That(GiOrphans.LiveWorlds(), Is.EqualTo(1));
            Assert.That(frame.Tables.LayerCount, Is.EqualTo(GiScenarioProfile.Standard().Layers.Length));
            Assert.That(frame.ProfileHash, Is.EqualTo(GiScenarioProfile.Standard().ToSpec().Hash));

            fixture.Tick(4);
            Assert.That(fixture.Frame.Tick, Is.EqualTo(5));
            Assert.That(GiWorld.Tick(fixture.Frame.World), Is.EqualTo(5));
            Assert.That(fixture.Frame.Generation, Is.EqualTo(1u));
        }

        [Test]
        public void AChangedProfileRebuildsAndForgetsEverySourceId()
        {
            using var fixture = new GiEcsFixture();
            fixture.SetStandardProfile();
            fixture.Tick();
            var first = fixture.Frame;

            var source = fixture.Manager.CreateEntity(typeof(GiSource));
            fixture.Manager.SetComponentData(source, new GiSource { Id = new GiWorld.SourceId(3u, 7u), Gain = 4 });
            Assert.That(fixture.Manager.GetComponentData<GiSource>(source).Placed, Is.True);

            var changed = GiScenarioProfile.Standard().ToSpec();
            changed.Grids[0].Power = 6;
            changed.Grids[0].Size = 128f;
            fixture.SetProfile(changed);
            fixture.Tick();

            var second = fixture.Frame;
            Assert.That(second.Generation, Is.EqualTo(2u));
            Assert.That(second.ProfileHash, Is.Not.EqualTo(first.ProfileHash));
            Assert.That(GiOrphans.LiveWorlds(), Is.EqualTo(1));
            Assert.That(fixture.Manager.GetComponentData<GiSource>(source).Placed, Is.False);
            Assert.That(fixture.Manager.GetComponentData<GiSource>(source).Gain, Is.EqualTo((sbyte)4));
            Assert.That(DefinedStamps(), Is.EqualTo(GiScenarioProfile.Standard().Stamps.Length));
        }

        [Test]
        public void RemovingTheProfileFreesTheWorldAndItsStamps()
        {
            using var fixture = new GiEcsFixture();
            fixture.SetStandardProfile();
            fixture.Tick();
            Assert.That(GiOrphans.LiveWorlds(), Is.EqualTo(1));

            fixture.ClearProfile();
            fixture.Tick();
            Assert.That(fixture.Frame.Alive, Is.False);
            Assert.That(GiOrphans.LiveWorlds(), Is.EqualTo(0));
            Assert.That(DefinedStamps(), Is.EqualTo(0));
            Assert.That(GiRuntime.Stamps.LiveCount, Is.EqualTo(0));
        }

        [Test]
        public void ARejectedProfileRaisesAFaultAndLeavesNoWorld()
        {
            using var fixture = new GiEcsFixture();
            var spec = GiScenarioProfile.Standard().ToSpec();
            spec.Layers = new GiLayerSpec[33];
            fixture.SetProfile(spec);
            LogAssert.Expect(LogType.Error, new Regex("Gi profile rejected: TooManyLayers"));

            Assert.DoesNotThrow(() => fixture.Tick(3));
            var frame = fixture.Frame;
            Assert.That(frame.Alive, Is.False);
            Assert.That(frame.Faults.Has(GiFaultKind.Profile), Is.True);
            Assert.That(GiOrphans.LiveWorlds(), Is.EqualTo(0));
            Assert.That(DefinedStamps(), Is.EqualTo(0));

            fixture.SetStandardProfile();
            fixture.Tick();
            frame = fixture.Frame;
            Assert.That(frame.Alive, Is.True);
            Assert.That(frame.Faults.Has(GiFaultKind.Profile), Is.False);
        }

        [Test]
        public void ACorruptedProfileHashIsRejected()
        {
            using var fixture = new GiEcsFixture();
            var spec = GiScenarioProfile.Standard().ToSpec();
            fixture.SetProfile(spec);
            ref var blob = ref fixture.Manager.GetComponentData<GiProfileReference>(
                fixture.Manager.CreateEntityQuery(ComponentType.ReadOnly<GiProfileReference>()).GetSingletonEntity()).Value.Value;
            blob.Hash ^= 1ul;
            LogAssert.Expect(LogType.Error, new Regex("Gi profile rejected: hash mismatch"));

            fixture.Tick();
            Assert.That(fixture.Frame.Alive, Is.False);
            Assert.That(fixture.Frame.Faults.Has(GiFaultKind.Profile), Is.True);
        }

        [Test]
        public void TwoEcsWorldsOnOneProfileShareStampsAndFreeThemTogether()
        {
            var spec = GiScenarioProfile.Standard().ToSpec();
            var expected = spec.Stamps.Length;
            var first = new GiEcsFixture("First");
            var second = new GiEcsFixture("Second");
            try
            {
                first.SetProfile(spec);
                second.SetProfile(spec);
                first.Tick();
                second.Tick();
                Assert.That(GiOrphans.LiveWorlds(), Is.EqualTo(2));
                Assert.That(DefinedStamps(), Is.EqualTo(expected));
                Assert.That(first.Frame.World, Is.Not.EqualTo(second.Frame.World));

                first.Dispose();
                Assert.That(GiOrphans.LiveWorlds(), Is.EqualTo(1));
                Assert.That(DefinedStamps(), Is.EqualTo(expected));
            }
            finally
            {
                first.Dispose();
                second.Dispose();
            }

            Assert.That(GiOrphans.LiveWorlds(), Is.EqualTo(0));
            Assert.That(DefinedStamps(), Is.EqualTo(0));
        }

        [Test]
        public void BuildingAndFreeingOneThousandEcsWorldsLeavesNothingBehind()
        {
            var spec = GiScenarioProfile.Standard().ToSpec();
            for (var i = 0; i < 1000; i++)
            {
                using var fixture = new GiEcsFixture("Cycle");
                fixture.SetProfile(spec);
                fixture.Tick();
            }

            Assert.That(GiOrphans.LiveWorlds(), Is.EqualTo(0));
            Assert.That(DefinedStamps(), Is.EqualTo(0));
            Assert.That(GiRuntime.Stamps.LiveCount, Is.EqualTo(0));
            Assert.That(GiRuntime.Stamps.DeferredCount, Is.EqualTo(0));
        }

        [Test]
        public void SteadyTicksAllocateNoManagedMemory()
        {
            using var fixture = new GiEcsFixture();
            fixture.SetStandardProfile();
            fixture.Tick(200);

            Assert.That(GiAllocation.Available, Is.True);
            Assert.That(GiAllocation.SteadyZero(() => fixture.Tick(100), 12, 3), Is.True);
        }

        [Test]
        public void AManagedJobRunsOnAWorkerThread()
        {
            var main = Thread.CurrentThread.ManagedThreadId;
            using var seen = new NativeReference<int>(Allocator.TempJob);
            var handle = new ThreadJob { Seen = seen }.Schedule();
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!handle.IsCompleted && DateTime.UtcNow < deadline) Thread.Sleep(1);
            handle.Complete();
            Assert.That(seen.Value, Is.Not.EqualTo(0));
            Assert.That(seen.Value, Is.Not.EqualTo(main));
        }

        [Test]
        public void ReclaimOnCodeLoadFreesOrphansUnlessKept()
        {
            var load = typeof(GiRuntime).GetMethod("Load", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(load, Is.Not.Null);

            var world = GiWorld.New();
            var stamp = GiStamp.Box(2, 2, 10);
            GiRuntimeConfig.KeepOrphans.Data = true;
            try
            {
                load.Invoke(null, null);
                Assert.That(GiOrphans.LiveWorlds(), Is.EqualTo(1));
                Assert.That(GiStamp.TryGetSpec(stamp, out _), Is.True);
            }
            finally
            {
                GiRuntimeConfig.KeepOrphans.Data = false;
            }

            load.Invoke(null, null);
            Assert.That(GiOrphans.LiveWorlds(), Is.EqualTo(0));
            Assert.That(GiStamp.TryGetSpec(stamp, out _), Is.False);
            Assert.That(GiWorld.Free(world), Is.False);
        }

        [Test]
        public unsafe void FencesOrderWritersBeforeReadersAndCompleteEverything()
        {
            using var log = new NativeArray<int>(8, Allocator.TempJob);
            var write = new StepJob { Log = log, Step = 1 }.Schedule(GiRuntime.BeginWrite(default));
            GiRuntime.EndWrite(write);
            var readA = new StepJob { Log = log, Step = 2 }.Schedule(GiRuntime.BeginRead(default));
            GiRuntime.EndRead(readA);
            var readB = new StepJob { Log = log, Step = 3 }.Schedule(GiRuntime.BeginRead(default));
            GiRuntime.EndRead(readB);
            var second = new StepJob { Log = log, Step = 4 }.Schedule(GiRuntime.BeginWrite(default));
            GiRuntime.EndWrite(second);

            GiRuntime.CompleteAll();
            Assert.That(log[0], Is.EqualTo(4));
            Assert.That(log[1], Is.EqualTo(1));
            Assert.That(log[4], Is.EqualTo(4));
            var readers = new[] { log[2], log[3] };
            Array.Sort(readers);
            Assert.That(readers, Is.EqualTo(new[] { 2, 3 }));
        }

        private struct ThreadJob : IJob
        {
            public NativeReference<int> Seen;

            public void Execute()
            {
                Seen.Value = Thread.CurrentThread.ManagedThreadId;
            }
        }

        private unsafe struct StepJob : IJob
        {
            [NativeDisableContainerSafetyRestriction]
            public NativeArray<int> Log;
            public int Step;

            public void Execute()
            {
                var counter = (int*)Log.GetUnsafePtr();
                var slot = Interlocked.Increment(ref counter[0]);
                counter[slot] = Step;
            }
        }
    }
}
