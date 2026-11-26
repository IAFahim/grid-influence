using System;
using Gi.Entities.Tests.Support;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace Gi.Entities.Tests
{
    public sealed class GiWorldSnapshotTests
    {
        [SetUp]
        public void SetUp()
        {
            GiRuntime.CompleteAll();
            GiOrphans.Reclaim(GiRuntime.Stamps);
        }

        private static ulong[] LayerHashes(GiEcsFixture fixture, int layerCount)
        {
            fixture.Complete();
            var frame = fixture.Frame;
            var hashes = new ulong[layerCount];
            for (var l = 0; l < layerCount; l++)
            {
                frame.Tables.TryLayer(new GiLayerKey((byte)l), out var layerId);
                hashes[l] = GiFieldHash.OfLayer(frame.World, layerId);
            }

            return hashes;
        }

        private static void StepDeterministic(GiEcsFixture fixture, Entity[] emitters, Entity splat, Entity pulseQueryEntity, int tick)
        {
            var m = fixture.Manager;
            for (var i = 0; i < emitters.Length; i++)
            {
                var x = 16f + ((i * 5 + tick * 0.4f) % 32f);
                var z = 14f + ((i * 7 + tick * 0.25f) % 32f);
                var heading = (tick + i) * 0.1f;
                var scale = 0.8f + ((tick + i) % 5) * 0.15f;
                m.SetComponentData(emitters[i], new LocalTransform
                {
                    Position = new float3(x, 0f, z),
                    Rotation = quaternion.RotateY(math.PI / 2f - heading),
                    Scale = scale,
                });
                var em = m.GetComponentData<GiEmitter>(emitters[i]);
                em.Gain = (sbyte)(2 + ((tick + i) % 6));
                m.SetComponentData(emitters[i], em);
                m.SetComponentEnabled<GiEmitter>(emitters[i], (tick + i) % 7 != 0);
            }

            m.SetComponentData(splat, LocalTransform.FromPosition(20f + (tick % 12), 0f, 22f + (tick % 9)));

            if (tick % 3 == 0)
            {
                var writer = m.GetComponentData<GiPulseSingleton>(pulseQueryEntity).CreateWriter();
                writer.Enqueue(new GiPulse
                {
                    Layer = new GiLayerKey(0),
                    Stamp = new GiStampKey((byte)(tick % 6)),
                    X = 24f + (tick % 10),
                    Y = 26f + (tick % 8),
                    Scale = 1.1f,
                    Gain = 5,
                    Ticks = 4,
                    FadeOut = true,
                });
            }

            fixture.Tick();
        }

        [Test]
        public void CaptureRestoreAndContinueMatchesAnUninterruptedRunBitForBit()
        {
            using var uninterrupted = new GiEcsFixture("SnapshotUninterrupted");
            using var rewound = new GiEcsFixture("SnapshotRewound");
            var spec = GiScenarioProfile.Standard().ToSpec();
            uninterrupted.SetProfile(spec);
            rewound.SetProfile(spec);

            var count = 8;
            var uEmitters = new Entity[count];
            var rEmitters = new Entity[count];
            for (var i = 0; i < count; i++)
            {
                uEmitters[i] = uninterrupted.CreateEmitter((byte)(i % 2), (byte)(i % 6), 4, 16f + i * 2f, 16f + i, (ushort)(i % 3 + 1));
                uninterrupted.Manager.AddComponent<GiOriented>(uEmitters[i]);
                uninterrupted.Manager.AddComponent<GiScaled>(uEmitters[i]);

                rEmitters[i] = rewound.CreateEmitter((byte)(i % 2), (byte)(i % 6), 4, 16f + i * 2f, 16f + i, (ushort)(i % 3 + 1));
                rewound.Manager.AddComponent<GiOriented>(rEmitters[i]);
                rewound.Manager.AddComponent<GiScaled>(rEmitters[i]);
            }

            var uSplat = uninterrupted.CreateSplat(2, 1, 3, 20f, 22f);
            var rSplat = rewound.CreateSplat(2, 1, 3, 20f, 22f);

            using var uPulseQ = new EntityQueryBuilder(Allocator.Temp).WithAllRW<GiPulseSingleton>().WithOptions(EntityQueryOptions.IncludeSystems).Build(uninterrupted.Manager);
            using var rPulseQ = new EntityQueryBuilder(Allocator.Temp).WithAllRW<GiPulseSingleton>().WithOptions(EntityQueryOptions.IncludeSystems).Build(rewound.Manager);
            var uPulseEntity = uPulseQ.GetSingletonEntity();
            var rPulseEntity = rPulseQ.GetSingletonEntity();

            const int split = 15;
            const int end = 35;
            for (var t = 0; t < split; t++)
            {
                StepDeterministic(uninterrupted, uEmitters, uSplat, uPulseEntity, t);
                StepDeterministic(rewound, rEmitters, rSplat, rPulseEntity, t);
            }

            using var snapshot = new NativeList<byte>(1 << 16, Allocator.Persistent);
            Assert.That(GiWorldSnapshot.Capture(rewound.Manager, snapshot), Is.True);

            var savedTransforms = new LocalTransform[count];
            var savedEmitters = new GiEmitter[count];
            var savedEnabled = new bool[count];
            for (var i = 0; i < count; i++)
            {
                savedTransforms[i] = rewound.Manager.GetComponentData<LocalTransform>(rEmitters[i]);
                savedEmitters[i] = rewound.Manager.GetComponentData<GiEmitter>(rEmitters[i]);
                savedEnabled[i] = rewound.Manager.IsComponentEnabled<GiEmitter>(rEmitters[i]);
            }
            var savedSplatTransform = rewound.Manager.GetComponentData<LocalTransform>(rSplat);

            for (var t = split; t < split + 10; t++)
                StepDeterministic(rewound, rEmitters, rSplat, rPulseEntity, t + 100);

            for (var i = 0; i < count; i++)
            {
                rewound.Manager.SetComponentData(rEmitters[i], savedTransforms[i]);
                rewound.Manager.SetComponentData(rEmitters[i], savedEmitters[i]);
                rewound.Manager.SetComponentEnabled<GiEmitter>(rEmitters[i], savedEnabled[i]);
            }
            rewound.Manager.SetComponentData(rSplat, savedSplatTransform);

            Assert.That(GiWorldSnapshot.Restore(rewound.Manager, snapshot), Is.True);
            Assert.That(LayerHashes(rewound, spec.Layers.Length), Is.EqualTo(LayerHashes(uninterrupted, spec.Layers.Length)));
            Assert.That(rewound.Frame.Tick, Is.EqualTo(uninterrupted.Frame.Tick));

            for (var t = split; t < end; t++)
            {
                StepDeterministic(uninterrupted, uEmitters, uSplat, uPulseEntity, t);
                StepDeterministic(rewound, rEmitters, rSplat, rPulseEntity, t);
                Assert.That(LayerHashes(rewound, spec.Layers.Length), Is.EqualTo(LayerHashes(uninterrupted, spec.Layers.Length)), "tick " + t);
            }
        }

        [Test]
        public void WarmCaptureAndRestoreAllocateNothing()
        {
            using var fixture = new GiEcsFixture("SnapshotAlloc");
            fixture.SetStandardProfile();
            for (var i = 0; i < 64; i++)
                fixture.CreateEmitter((byte)(i % 2), (byte)(i % 6), 4, 10f + i % 40, 12f + i / 40);
            fixture.Tick(4);

            using var snapshot = new NativeList<byte>(1 << 16, Allocator.Persistent);
            Assert.That(GiWorldSnapshot.Capture(fixture.Manager, snapshot), Is.True);
            Assert.That(GiWorldSnapshot.Restore(fixture.Manager, snapshot), Is.True);

            var run = 0;
            for (var i = 0; i < 12; i++)
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                GiWorldSnapshot.Capture(fixture.Manager, snapshot);
                GiWorldSnapshot.Restore(fixture.Manager, snapshot);
                var delta = GC.GetAllocatedBytesForCurrentThread() - before;
                run = delta == 0L ? run + 1 : 0;
                if (run >= 3) break;
            }

            Assert.That(run, Is.GreaterThanOrEqualTo(3));
        }
    }
}
