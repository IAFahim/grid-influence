using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;
using BovineLabs.Core;
using Gi.Entities.Tests.Support;
using NUnit.Framework;
using Unity.Entities;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.TestTools;
using Unity.Scripting.LifecycleManagement;

namespace Gi.Entities.Tests
{
    [NoAutoStaticsCleanup]
    public static class GiRogueGate
    {
        public static int Armed;
        public static int Held;
        public static int Baseline;
        public static int HolderTag;
        public static int IntruderTag;
        public static byte World;
        public static JobHandle Holder;
        public static JobHandle Intruder;
    }

    [WorldSystemFilter(Worlds.SimulationEditor)]
    [UpdateInGroup(typeof(GiSystemGroup))]
    [UpdateAfter(typeof(GiBootstrapSystem))]
    [UpdateBefore(typeof(GiReconcileSystem))]
    public partial struct GiRogueHolderSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            if (Volatile.Read(ref GiRogueGate.Armed) == 0) return;

            GiRogueGate.Holder = new HoldJob { World = GiRogueGate.World, Tag = GiRogueGate.HolderTag, Baseline = GiRogueGate.Baseline }.Schedule();
        }

        private struct HoldJob : IJob
        {
            public byte World;
            public int Tag;
            public int Baseline;

            public void Execute()
            {
                GiGuard.Begin(World, Tag, true);
                Volatile.Write(ref GiRogueGate.Held, 1);
                var timer = Stopwatch.StartNew();
                while (GiGuard.Violations == Baseline && timer.ElapsedMilliseconds < 5000) Thread.SpinWait(200);
                GiGuard.End(World, true);
            }
        }
    }

    [WorldSystemFilter(Worlds.SimulationEditor)]
    [UpdateInGroup(typeof(GiSystemGroup))]
    [UpdateAfter(typeof(GiBootstrapSystem))]
    [UpdateBefore(typeof(GiReconcileSystem))]
    public partial struct GiRogueIntruderSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            if (Volatile.Read(ref GiRogueGate.Armed) == 0) return;

            GiRogueGate.Intruder = new IntrudeJob { World = GiRogueGate.World, Tag = GiRogueGate.IntruderTag }.Schedule();
        }

        private struct IntrudeJob : IJob
        {
            public byte World;
            public int Tag;

            public void Execute()
            {
                var timer = Stopwatch.StartNew();
                while (Volatile.Read(ref GiRogueGate.Held) == 0 && timer.ElapsedMilliseconds < 5000) Thread.SpinWait(200);
                GiGuard.Begin(World, Tag, true);
                GiGuard.End(World, true);
            }
        }
    }

    public sealed class GiConcurrencyTests
    {
        private static readonly GiScenarioOptions Long = new GiScenarioOptions(1000, 24, 64, 4, true, true);

        [SetUp]
        public void SetUp()
        {
            GiRuntime.CompleteAll();
            GiOrphans.Reclaim(GiRuntime.Stamps);
        }

        [Test]
        public void TwoEcsWorldsInOneProcessMatchTheOracleForAThousandTicksWithoutAFault()
        {
            var first = GiScenarioGenerator.Create(101, Long);
            var second = GiScenarioGenerator.Create(202, Long);
            var expectedFirst = GiReferenceExecutor.Run(first);
            var expectedSecond = GiReferenceExecutor.Run(second);
            var before = GiGuard.Violations;

            using var a = new GiEcsExecutor(first, "WorldA");
            using var b = new GiEcsExecutor(second, "WorldB");
            var perFirst = new ulong[Long.Ticks][];
            var perSecond = new ulong[Long.Ticks][];
            for (var t = 0; t < Long.Ticks; t++)
            {
                a.Queue(t);
                b.Queue(t);
                a.Fixture.Tick();
                b.Fixture.Tick();
                perFirst[t] = a.Hashes();
                perSecond[t] = b.Hashes();
            }

            Assert.That(expectedFirst.Compare(new GiScenarioHashes(perFirst)), Is.EqualTo(string.Empty));
            Assert.That(expectedSecond.Compare(new GiScenarioHashes(perSecond)), Is.EqualTo(string.Empty));
            Assert.That(a.Fixture.Frame.Faults.Raised, Is.EqualTo(0));
            Assert.That(b.Fixture.Frame.Faults.Raised, Is.EqualTo(0));
            Assert.That(a.Fixture.Frame.World, Is.Not.EqualTo(b.Fixture.Frame.World));
            Assert.That(GiGuard.Violations, Is.EqualTo(before));
        }

        [Test]
        public void AMisorderedSystemTripsTheGuardAndTheFaultNamesBothSystems()
        {
            using var fixture = new GiEcsFixture();
            fixture.SetStandardProfile();
            fixture.Tick();
            fixture.Group.AddSystemToUpdateList(fixture.World.GetOrCreateSystem<GiRogueHolderSystem>());
            fixture.Group.AddSystemToUpdateList(fixture.World.GetOrCreateSystem<GiRogueIntruderSystem>());
            fixture.Group.SortSystems();

            var before = GiGuard.Violations;
            GiRogueGate.HolderTag = GiGuard.Register("GiRogueHolderSystem");
            GiRogueGate.IntruderTag = GiGuard.Register("GiRogueIntruderSystem");
            GiRogueGate.Baseline = before;
            GiRogueGate.World = fixture.Frame.World;
            Volatile.Write(ref GiRogueGate.Held, 0);
            Volatile.Write(ref GiRogueGate.Armed, 1);
            fixture.Tick();
            Volatile.Write(ref GiRogueGate.Armed, 0);
            JobHandle.ScheduleBatchedJobs();
            GiRogueGate.Holder.Complete();
            GiRogueGate.Intruder.Complete();
            fixture.Complete();

            Assert.That(GiGuard.Violations, Is.GreaterThan(before), "the guard saw no overlap");

            LogAssert.Expect(LogType.Error, new Regex("(?=.*GiRogueHolderSystem)(?=.*GiRogueIntruderSystem)Gi guard"));
            fixture.Tick();
            fixture.Complete();
            Assert.That(fixture.Frame.Faults.Has(GiFaultKind.Overlap), Is.True);
        }

        [Test]
        public void OneFrameOfThreeFixedTicksEqualsThreeFramesOfOneTick()
        {
            var scenario = GiScenarioGenerator.Create(31, new GiScenarioOptions(6, 24, 64, 4, true, true));
            using var batched = new GiEcsExecutor(scenario, "Batched");
            using var stepped = new GiEcsExecutor(scenario, "Stepped");
            batched.Fixture.UseCatchUp(0.25f);
            stepped.Fixture.UseCatchUp(0.25f);
            batched.Fixture.Advance(0f);
            stepped.Fixture.Advance(0f);
            Assert.That(batched.Fixture.Frame.Tick, Is.EqualTo(1));
            Assert.That(stepped.Fixture.Frame.Tick, Is.EqualTo(1));

            for (var t = 0; t < scenario.Ticks.Length; t++)
            {
                batched.Queue(t);
                stepped.Queue(t);
                batched.Fixture.Advance(0.75f);
                for (var i = 0; i < 3; i++) stepped.Fixture.Advance(0.25f);

                var expectedTick = 1 + 3 * (t + 1);
                Assert.That(batched.Fixture.Frame.Tick, Is.EqualTo(expectedTick), $"batched tick after chunk {t}");
                Assert.That(stepped.Fixture.Frame.Tick, Is.EqualTo(expectedTick), $"stepped tick after chunk {t}");
                var a = batched.Hashes();
                var b = stepped.Hashes();
                Assert.That(a, Is.EqualTo(b), $"hashes after chunk {t}");
            }
        }
    }
}
