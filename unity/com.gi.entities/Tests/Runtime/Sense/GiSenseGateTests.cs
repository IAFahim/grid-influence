using System;
using Gi.Entities.Tests.Support;
using NUnit.Framework;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using GiWorld = Gi.World;

namespace Gi.Entities.Tests
{
    public sealed class GiSenseGateTests
    {
        private const float Reach = 14f;
        private const short Threshold = 3;
        private const int Probes = 240;

        [SetUp]
        public void SetUp()
        {
            GiRuntime.CompleteAll();
            GiOrphans.Reclaim(GiRuntime.Stamps);
        }

        private static void Scene(GiEcsFixture fixture, int probes, ulong seed, out Entity[] entities, out Entity[] emitters,
            out float[] xs, out float[] zs)
        {
            var rng = new GiRandom(seed);
            emitters = new Entity[24];
            for (var i = 0; i < emitters.Length; i++)
                emitters[i] = fixture.CreateEmitter((byte)(i % 2), (byte)rng.Next(6), (sbyte)(2 + rng.Next(10)), 6 + rng.Next(52), 6 + rng.Next(52));

            var archetype = fixture.Manager.CreateArchetype(typeof(LocalTransform), typeof(ProbeSample), typeof(ProbeCovered),
                typeof(ProbeGated), typeof(ProbeNote), typeof(KindArea), typeof(KindMax), typeof(KindNearest),
                typeof(KindFound), typeof(KindWake), typeof(KindReach));
            entities = new Entity[probes];
            xs = new float[probes];
            zs = new float[probes];
            for (var i = 0; i < probes; i++)
            {
                xs[i] = 4 + rng.Next(56) + rng.Next(8) * 0.125f;
                zs[i] = 4 + rng.Next(56) + rng.Next(8) * 0.125f;
                entities[i] = fixture.Manager.CreateEntity(archetype);
                fixture.Manager.SetComponentData(entities[i], LocalTransform.FromPosition(xs[i], 0f, zs[i]));
                fixture.Manager.SetComponentData(entities[i], new ProbeNote { Mark = i });
                fixture.Manager.SetComponentData(entities[i], new KindReach { Reach = 6f + rng.Next(14), Threshold = (short)(1 + rng.Next(6)) });
                fixture.Manager.SetComponentEnabled<ProbeCovered>(entities[i], false);
                fixture.Manager.SetComponentEnabled<KindFound>(entities[i], false);
                fixture.Manager.SetComponentEnabled<KindWake>(entities[i], false);
            }
        }

        private static float3 Position(GiEcsFixture fixture, Entity entity)
        {
            return fixture.Manager.GetComponentData<LocalTransform>(entity).Position;
        }

        private static void CheckAll(GiEcsFixture fixture, Entity[] entities, byte derived, byte cell, byte areaLayer, byte maxLayer)
        {
            var frame = fixture.Frame;
            for (var i = 0; i < entities.Length; i++)
            {
                var at = Position(fixture, entities[i]);
                var sample = fixture.Manager.GetComponentData<ProbeSample>(entities[i]);
                var gradientOk = GiWorld.TrySenseGradient(frame.World, derived, at.x, at.z, out var gx, out var gy);
                Assert.That(fixture.Manager.IsComponentEnabled<ProbeCovered>(entities[i]), Is.EqualTo(gradientOk), $"coverage {i}");
                Assert.That(BitConverter.SingleToInt32Bits(sample.Gradient.x), Is.EqualTo(BitConverter.SingleToInt32Bits(gx)), $"gx {i}");
                Assert.That(BitConverter.SingleToInt32Bits(sample.Gradient.y), Is.EqualTo(BitConverter.SingleToInt32Bits(gy)), $"gy {i}");
                if (gradientOk)
                {
                    GiWorld.TrySense(frame.World, derived, at.x, at.z, out var expected);
                    Assert.That(sample.Value, Is.EqualTo(expected), $"value {i}");
                }

                GiWorld.TrySense(frame.World, cell, at.x, at.z, out var cellValue);
                Assert.That(fixture.Manager.GetComponentData<ProbeGated>(entities[i]).Value, Is.EqualTo(cellValue), $"gated {i}");

                var areaOk = GiWorld.TrySenseArea(frame.World, areaLayer, at.x, at.z, Reach, out var total);
                Assert.That(fixture.Manager.GetComponentData<KindArea>(entities[i]).Total, Is.EqualTo(total), $"area {i}");
                if (areaOk) Assert.That(total, Is.Not.EqualTo(long.MinValue));

                var maxOk = GiWorld.TrySenseMax(frame.World, maxLayer, at.x, at.z, Reach, out var best, out var bx, out var by);
                var max = fixture.Manager.GetComponentData<KindMax>(entities[i]);
                Assert.That(max.Value, Is.EqualTo(best), $"max {i}");
                if (maxOk)
                {
                    Assert.That(BitConverter.SingleToInt32Bits(bx), Is.EqualTo(BitConverter.SingleToInt32Bits(max.Position.x)), $"max x {i}");
                    Assert.That(BitConverter.SingleToInt32Bits(by), Is.EqualTo(BitConverter.SingleToInt32Bits(max.Position.y)), $"max y {i}");
                }

                GiWorld.TrySenseNearest(frame.World, cell, at.x, at.z, Reach, Threshold, out var near, out var nx, out var ny);
                var nearOk = near != short.MinValue;
                var nearest = fixture.Manager.GetComponentData<KindNearest>(entities[i]);
                Assert.That(nearest.Value, Is.EqualTo(near), $"nearest {i}");
                Assert.That(fixture.Manager.IsComponentEnabled<KindFound>(entities[i]), Is.EqualTo(nearOk), $"found {i}");
                if (nearOk)
                {
                    Assert.That(BitConverter.SingleToInt32Bits(nx), Is.EqualTo(BitConverter.SingleToInt32Bits(nearest.Position.x)), $"nearest x {i}");
                    Assert.That(BitConverter.SingleToInt32Bits(ny), Is.EqualTo(BitConverter.SingleToInt32Bits(nearest.Position.y)), $"nearest y {i}");
                }

                var changed = GiWorld.Changed(frame.World, cell, at.x, at.z, 4f, frame.Tick - 3);
                Assert.That(fixture.Manager.IsComponentEnabled<KindWake>(entities[i]), Is.EqualTo(changed), $"wake {i}");
            }
        }

        [Test]
        public void GatedOutputsMatchTheScalarCallsAcrossMutationsAndQuietTicks()
        {
            foreach (var seed in new ulong[] { 7, 21 })
            {
                using var fixture = new GiEcsFixture("Gate" + seed);
                fixture.SetProfileWith(GiSenseTests.Sampled, GiSenseTests.Gated, GiSenseKindsTests.Area,
                    GiSenseKindsTests.Max, GiSenseKindsTests.Nearest, GiSenseKindsTests.Changed);
                Scene(fixture, Probes, seed, out var entities, out var emitters, out _, out _);
                fixture.Tick(3);
                fixture.Complete();

                var frame = fixture.Frame;
                frame.Tables.TryLayer(new GiLayerKey(3), out var derived);
                frame.Tables.TryLayer(new GiLayerKey(1), out var cell);
                frame.Tables.TryLayer(new GiLayerKey(4), out var maxLayer);
                CheckAll(fixture, entities, derived, cell, derived, maxLayer);
                Assert.That(fixture.Manager.GetComponentData<GiStats>(fixture.FrameEntity).Sensed, Is.EqualTo(6 * entities.Length));

                fixture.Tick(2);
                fixture.Complete();
                frame = fixture.Frame;
                frame.Tables.TryLayer(new GiLayerKey(3), out derived);
                frame.Tables.TryLayer(new GiLayerKey(1), out cell);
                frame.Tables.TryLayer(new GiLayerKey(4), out maxLayer);
                CheckAll(fixture, entities, derived, cell, derived, maxLayer);

                fixture.Manager.SetComponentData(entities[5], LocalTransform.FromPosition(50f, 0f, 50f));
                fixture.Manager.SetComponentData(entities[9], LocalTransform.FromPosition(8f, 0f, 55f));
                fixture.Manager.SetComponentData(entities[9], new KindReach { Reach = 20f, Threshold = (short)5 });
                fixture.Manager.SetComponentData(emitters[3], new GiEmitter
                {
                    Layer = new GiLayerKey(0),
                    Stamp = new GiStampKey(0),
                    Gain = (sbyte)12,
                });
                fixture.Manager.DestroyEntity(emitters[7]);
                fixture.Tick(1);
                fixture.Complete();
                frame = fixture.Frame;
                frame.Tables.TryLayer(new GiLayerKey(3), out derived);
                frame.Tables.TryLayer(new GiLayerKey(1), out cell);
                frame.Tables.TryLayer(new GiLayerKey(4), out maxLayer);
                CheckAll(fixture, entities, derived, cell, derived, maxLayer);

                fixture.Tick(2);
                fixture.Complete();
                frame = fixture.Frame;
                frame.Tables.TryLayer(new GiLayerKey(3), out derived);
                frame.Tables.TryLayer(new GiLayerKey(1), out cell);
                frame.Tables.TryLayer(new GiLayerKey(4), out maxLayer);
                CheckAll(fixture, entities, derived, cell, derived, maxLayer);
            }
        }

        [Test]
        public void PerEntityReachOutputsMatchTheScalarCallsAcrossAReachChange()
        {
            var perEntityArea = GiSenseKindsTests.Area;
            perEntityArea.Reach = 0f;
            perEntityArea.ReachField = GiBindingFields.Of<KindReach>("Reach");
            var perEntityNearest = GiSenseKindsTests.Nearest;
            perEntityNearest.Reach = 0f;
            perEntityNearest.ReachField = GiBindingFields.Of<KindReach>("Reach");
            perEntityNearest.ThresholdField = GiBindingFields.Of<KindReach>("Threshold");

            using var fixture = new GiEcsFixture("GateReach");
            fixture.SetProfileWith(perEntityArea, perEntityNearest);
            Scene(fixture, Probes, 11, out var entities, out _, out _, out _);
            fixture.Tick(3);
            fixture.Complete();

            var frame = fixture.Frame;
            frame.Tables.TryLayer(new GiLayerKey(3), out var areaLayer);
            frame.Tables.TryLayer(new GiLayerKey(1), out var cellLayer);
            for (var i = 0; i < entities.Length; i++)
            {
                var at = Position(fixture, entities[i]);
                var field = fixture.Manager.GetComponentData<KindReach>(entities[i]);
                GiWorld.TrySenseArea(frame.World, areaLayer, at.x, at.z, field.Reach, out var total);
                Assert.That(fixture.Manager.GetComponentData<KindArea>(entities[i]).Total, Is.EqualTo(total), $"area {i}");

                GiWorld.TrySenseNearest(frame.World, cellLayer, at.x, at.z, field.Reach, field.Threshold, out var near, out _, out _);
                Assert.That(fixture.Manager.GetComponentData<KindNearest>(entities[i]).Value, Is.EqualTo(near), $"nearest {i}");
            }

            fixture.Manager.SetComponentData(entities[40], new KindReach { Reach = 24f, Threshold = (short)1 });
            fixture.Tick(1);
            fixture.Complete();

            frame = fixture.Frame;
            frame.Tables.TryLayer(new GiLayerKey(3), out areaLayer);
            frame.Tables.TryLayer(new GiLayerKey(1), out cellLayer);
            for (var i = 0; i < entities.Length; i++)
            {
                var at = Position(fixture, entities[i]);
                var field = fixture.Manager.GetComponentData<KindReach>(entities[i]);
                GiWorld.TrySenseArea(frame.World, areaLayer, at.x, at.z, field.Reach, out var total);
                Assert.That(fixture.Manager.GetComponentData<KindArea>(entities[i]).Total, Is.EqualTo(total), $"area after {i}");

                GiWorld.TrySenseNearest(frame.World, cellLayer, at.x, at.z, field.Reach, field.Threshold, out var near, out _, out _);
                Assert.That(fixture.Manager.GetComponentData<KindNearest>(entities[i]).Value, Is.EqualTo(near), $"nearest after {i}");
            }

            fixture.Tick(2);
            fixture.Complete();

            frame = fixture.Frame;
            frame.Tables.TryLayer(new GiLayerKey(3), out areaLayer);
            frame.Tables.TryLayer(new GiLayerKey(1), out cellLayer);
            for (var i = 0; i < entities.Length; i++)
            {
                var at = Position(fixture, entities[i]);
                var field = fixture.Manager.GetComponentData<KindReach>(entities[i]);
                GiWorld.TrySenseArea(frame.World, areaLayer, at.x, at.z, field.Reach, out var total);
                Assert.That(fixture.Manager.GetComponentData<KindArea>(entities[i]).Total, Is.EqualTo(total), $"area quiet {i}");

                GiWorld.TrySenseNearest(frame.World, cellLayer, at.x, at.z, field.Reach, field.Threshold, out var near, out _, out _);
                Assert.That(fixture.Manager.GetComponentData<KindNearest>(entities[i]).Value, Is.EqualTo(near), $"nearest quiet {i}");
            }
        }
    }
}
