using Gi.Entities.Tests.Support;
using NUnit.Framework;
using Unity.Entities;
using Unity.Transforms;

namespace Gi.Entities.Tests
{
    public sealed class GiAllocationTests
    {
        [SetUp]
        public void SetUp()
        {
            GiRuntime.CompleteAll();
            GiOrphans.Reclaim(GiRuntime.Stamps);
        }

        [Test]
        public void AHundredSteadyTicksWithEverySystemActiveAllocateNothing()
        {
            using var fixture = new GiEcsFixture();
            fixture.SetProfileWith(GiSenseTests.Sampled, GiSenseTests.Gated, GiSenseKindsTests.Area, GiSenseKindsTests.Max,
                GiSenseKindsTests.Nearest, GiSenseKindsTests.Changed);

            var emitters = new Entity[400];
            for (var i = 0; i < emitters.Length; i++)
                emitters[i] = fixture.CreateEmitter((byte)(i % 2), (byte)(i % 6), 4, 8f + i % 48, 8f + i / 48, (ushort)(i % 5 == 0 ? 3 : 0));
            for (var i = 0; i < 300; i++)
                fixture.CreateSplat((byte)(i % 2 == 0 ? 2 : 6), (byte)(i % 5 == 0 ? 4 : 0), (sbyte)(1 + i % 3), 6f + i % 50, 6f + i / 50);

            var archetype = fixture.Manager.CreateArchetype(typeof(LocalTransform), typeof(ProbeSample), typeof(ProbeCovered),
                typeof(ProbeGated), typeof(KindArea), typeof(KindMax), typeof(KindNearest), typeof(KindFound), typeof(KindWake));
            for (var i = 0; i < 600; i++)
            {
                var probe = fixture.Manager.CreateEntity(archetype);
                fixture.Manager.SetComponentData(probe, LocalTransform.FromPosition(4f + i % 56, 0f, 4f + i / 56));
                fixture.Manager.SetComponentEnabled<ProbeCovered>(probe, false);
                fixture.Manager.SetComponentEnabled<KindFound>(probe, false);
                fixture.Manager.SetComponentEnabled<KindWake>(probe, false);
            }

            fixture.Tick(100);

            var phase = 0f;
            void Step()
            {
                for (var t = 0; t < 20; t++)
                {
                    phase += 0.25f;
                    for (var i = 0; i < emitters.Length; i += 9)
                        fixture.Manager.SetComponentData(emitters[i], LocalTransform.FromPosition(8f + i % 48 + phase % 3f, 0f, 8f + i / 48));
                    fixture.Tick();
                }
            }

            Assert.That(GiAllocation.Available, Is.True);
            Assert.That(GiAllocation.SteadyZero(Step, 16, 5), Is.True);

            var stats = fixture.Manager.GetComponentData<GiStats>(fixture.FrameEntity);
            Assert.That(stats.Splats, Is.EqualTo(300));
            Assert.That(stats.Sensed, Is.EqualTo(6 * 600));
            Assert.That(stats.SyncRows, Is.GreaterThan(0));
            Assert.That(fixture.Frame.Faults.Raised, Is.EqualTo(0));
        }
    }
}
