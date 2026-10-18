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

namespace Gi.Entities.Tests
{
    public static class GiRogueGate
    {
        public static int Armed;
        public static int Baseline;
        public static int Tag;
    }

    [WorldSystemFilter(Worlds.SimulationEditor)]
    [UpdateInGroup(typeof(GiSystemGroup))]
    [UpdateAfter(typeof(GiBootstrapSystem))]
    [UpdateBefore(typeof(GiReconcileSystem))]
    public partial struct GiRogueSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            if (Volatile.Read(ref GiRogueGate.Armed) == 0) return;
            if (!SystemAPI.TryGetSingleton<GiFrame>(out var frame) || !frame.Alive) return;

            state.Dependency = new HoldJob { World = frame.World, Tag = GiRogueGate.Tag, Baseline = GiRogueGate.Baseline }.Schedule(state.Dependency);
        }

        private struct HoldJob : IJob
        {
            public byte World;
            public int Tag;
            public int Baseline;

            public void Execute()
            {
                GiGuard.Begin(World, Tag, true);
                var timer = Stopwatch.StartNew();
                while (GiGuard.Violations == Baseline && timer.ElapsedMilliseconds < 5000) Thread.SpinWait(200);
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
            var rogue = fixture.World.GetOrCreateSystem<GiRogueSystem>();
            fixture.Group.AddSystemToUpdateList(rogue);
            fixture.Group.SortSystems();

            var before = GiGuard.Violations;
            GiRogueGate.Tag = GiGuard.Register("GiRogueSystem");
            GiRogueGate.Baseline = before;
            Volatile.Write(ref GiRogueGate.Armed, 1);
            fixture.Tick();
            Volatile.Write(ref GiRogueGate.Armed, 0);
            fixture.Complete();

            Assert.That(GiGuard.Violations, Is.GreaterThan(before), "the guard saw no overlap");

            LogAssert.Expect(LogType.Error, new Regex("(?=.*GiRogueSystem)(?=.*GiProcessSystem)Gi guard"));
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
