using System;
using Gi.Entities.Tests.Support;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.TestTools;
using GiWorld = Gi.World;

namespace Gi.Entities.Tests
{
    public sealed class GiSenseTests
    {
        internal static readonly GiBindingSpec Sampled = new GiBindingSpec
        {
            Kind = GiBindingKind.ValueGradient,
            Layer = 3,
            Value = GiBindingFields.Of<ProbeSample>("Value"),
            Gradient = GiBindingFields.Of<ProbeSample>("Gradient"),
            Coverage = GiBindingFields.Tag<ProbeCovered>(),
        };

        internal static readonly GiBindingSpec Gated = new GiBindingSpec
        {
            Kind = GiBindingKind.Value,
            Layer = 1,
            Value = GiBindingFields.Of<ProbeGated>("Value"),
        };

        [SetUp]
        public void SetUp()
        {
            GiRuntime.CompleteAll();
            GiOrphans.Reclaim(GiRuntime.Stamps);
        }

        private static void Scene(GiEcsFixture fixture, int probes, ulong seed, out Entity[] entities, out float[] xs, out float[] zs)
        {
            var rng = new GiRandom(seed);
            for (var i = 0; i < 24; i++)
                fixture.CreateEmitter((byte)(i % 2), (byte)rng.Next(6), (sbyte)(2 + rng.Next(10)), 6 + rng.Next(52), 6 + rng.Next(52));

            var archetype = fixture.Manager.CreateArchetype(typeof(LocalTransform), typeof(ProbeSample), typeof(ProbeCovered),
                typeof(ProbeGated), typeof(ProbeNote));
            entities = new Entity[probes];
            xs = new float[probes];
            zs = new float[probes];
            for (var i = 0; i < probes; i++)
            {
                xs[i] = -8 + rng.Next(80) + rng.Next(8) * 0.125f;
                zs[i] = -8 + rng.Next(80) + rng.Next(8) * 0.125f;
                entities[i] = fixture.Manager.CreateEntity(archetype);
                fixture.Manager.SetComponentData(entities[i], LocalTransform.FromPosition(xs[i], 0f, zs[i]));
                fixture.Manager.SetComponentData(entities[i], new ProbeNote { Mark = i });
                fixture.Manager.SetComponentEnabled<ProbeCovered>(entities[i], false);
            }
        }

        [Test]
        public void EveryEntityReceivesWhatTheScalarCallsReturn()
        {
            using var fixture = new GiEcsFixture();
            fixture.SetProfileWith(Sampled, Gated);
            Scene(fixture, 300, 3, out var entities, out var xs, out var zs);
            fixture.Tick(3);
            fixture.Complete();

            var frame = fixture.Frame;
            frame.Tables.TryLayer(new GiLayerKey(3), out var derived);
            frame.Tables.TryLayer(new GiLayerKey(1), out var cell);
            var covered = 0;
            for (var i = 0; i < entities.Length; i++)
            {
                var sample = fixture.Manager.GetComponentData<ProbeSample>(entities[i]);
                var gradientOk = GiWorld.TrySenseGradient(frame.World, derived, xs[i], zs[i], out var gx, out var gy);
                Assert.That(fixture.Manager.IsComponentEnabled<ProbeCovered>(entities[i]), Is.EqualTo(gradientOk), $"coverage {i}");
                Assert.That(BitConverter.SingleToInt32Bits(sample.Gradient.x), Is.EqualTo(BitConverter.SingleToInt32Bits(gx)), $"gx {i}");
                Assert.That(BitConverter.SingleToInt32Bits(sample.Gradient.y), Is.EqualTo(BitConverter.SingleToInt32Bits(gy)), $"gy {i}");
                if (gradientOk)
                {
                    covered++;
                    GiWorld.TrySense(frame.World, derived, xs[i], zs[i], out var expected);
                    Assert.That(sample.Value, Is.EqualTo(expected), $"value {i}");
                }

                GiWorld.TrySense(frame.World, cell, xs[i], zs[i], out var cellValue);
                Assert.That(fixture.Manager.GetComponentData<ProbeGated>(entities[i]).Value, Is.EqualTo(cellValue), $"gated {i}");
            }

            Assert.That(covered, Is.GreaterThan(100));
            Assert.That(covered, Is.LessThan(entities.Length));
            Assert.That(fixture.Manager.GetComponentData<GiStats>(fixture.FrameEntity).Sensed, Is.EqualTo(2 * entities.Length));
        }

        [Test]
        public void TheBatchSizeNeverChangesAnOutput()
        {
            var snapshots = new System.Collections.Generic.List<(short[] Values, float[] Gradients, bool[] Covered)>();
            foreach (var batch in new[] { 64, 192, 4096, 1 << 20 })
            {
                GiSenseConfig.Batch.Data = batch;
                try
                {
                    using var fixture = new GiEcsFixture("Batch" + batch);
                    fixture.SetProfileWith(Sampled, Gated);
                    Scene(fixture, 700, 8, out var entities, out _, out _);
                    fixture.Tick(3);
                    fixture.Complete();
                    var values = new short[entities.Length];
                    var gradients = new float[entities.Length * 2];
                    var covered = new bool[entities.Length];
                    for (var i = 0; i < entities.Length; i++)
                    {
                        var sample = fixture.Manager.GetComponentData<ProbeSample>(entities[i]);
                        values[i] = sample.Value;
                        gradients[2 * i] = sample.Gradient.x;
                        gradients[2 * i + 1] = sample.Gradient.y;
                        covered[i] = fixture.Manager.IsComponentEnabled<ProbeCovered>(entities[i]);
                    }

                    snapshots.Add((values, gradients, covered));
                }
                finally
                {
                    GiSenseConfig.Batch.Data = 0;
                }
            }

            for (var s = 1; s < snapshots.Count; s++)
            {
                Assert.That(snapshots[s].Values, Is.EqualTo(snapshots[0].Values));
                Assert.That(snapshots[s].Gradients, Is.EqualTo(snapshots[0].Gradients));
                Assert.That(snapshots[s].Covered, Is.EqualTo(snapshots[0].Covered));
            }
        }

        [Test]
        public void OnlyEnabledEntitiesInAPartlyEnabledChunkAreWritten()
        {
            using var fixture = new GiEcsFixture();
            fixture.SetProfileWith(Gated);
            Scene(fixture, 200, 5, out var entities, out _, out _);
            for (var i = 0; i < entities.Length; i++)
            {
                fixture.Manager.SetComponentData(entities[i], new ProbeGated { Value = -1234 });
                if (i % 2 == 1) fixture.Manager.SetComponentEnabled<ProbeGated>(entities[i], false);
            }

            fixture.Tick(3);
            fixture.Complete();
            var frame = fixture.Frame;
            frame.Tables.TryLayer(new GiLayerKey(1), out var cell);
            var written = 0;
            for (var i = 0; i < entities.Length; i++)
            {
                var value = fixture.Manager.GetComponentData<ProbeGated>(entities[i]).Value;
                if (i % 2 == 1)
                {
                    Assert.That(value, Is.EqualTo((short)-1234), $"disabled {i} must be untouched");
                    Assert.That(fixture.Manager.GetComponentData<ProbeNote>(entities[i]).Mark, Is.EqualTo(i));
                    continue;
                }

                var position = fixture.Manager.GetComponentData<LocalTransform>(entities[i]).Position;
                GiWorld.TrySense(frame.World, cell, position.x, position.z, out var expected);
                Assert.That(value, Is.EqualTo(expected), $"enabled {i}");
                written++;
            }

            Assert.That(written, Is.EqualTo(100));
            Assert.That(fixture.Manager.GetComponentData<GiStats>(fixture.FrameEntity).Sensed, Is.EqualTo(100));
            for (var i = 0; i < entities.Length; i++) Assert.That(fixture.Manager.GetComponentData<ProbeNote>(entities[i]).Mark, Is.EqualTo(i));
        }

        [Test]
        public void ARejectedBindingRaisesAProfileFaultAndTheOthersStillRun()
        {
            using var fixture = new GiEcsFixture();
            var wrong = Gated;
            wrong.Value.Size = 4;
            fixture.SetProfileWith(Sampled, wrong, Gated);
            Scene(fixture, 50, 2, out var entities, out _, out _);
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("Gi binding 1 rejected"));
            fixture.Tick(3);
            fixture.Complete();

            Assert.That(fixture.Frame.Faults.Has(GiFaultKind.Profile), Is.True);
            var covered = 0;
            foreach (var entity in entities)
                if (fixture.Manager.IsComponentEnabled<ProbeCovered>(entity))
                    covered++;
            Assert.That(covered, Is.GreaterThan(10));
            var gatedRan = false;
            foreach (var entity in entities)
                if (fixture.Manager.GetComponentData<ProbeGated>(entity).Value != 0)
                    gatedRan = true;
            Assert.That(gatedRan, Is.True, "the valid binding after the rejected one still ran");
        }

        [Test]
        public void SensingManyEntitiesAllocatesNothingPerTick()
        {
            using var fixture = new GiEcsFixture();
            fixture.SetProfileWith(Sampled, Gated);
            Scene(fixture, 1000, 4, out _, out _, out _);
            fixture.Tick(100);

            Assert.That(GiAllocation.SteadyZero(() => fixture.Tick(50), 14, 3), Is.True);
            Assert.That(fixture.Manager.GetComponentData<GiStats>(fixture.FrameEntity).Sensed, Is.EqualTo(2000));
        }

        [Test]
        public void WithoutAProfileNothingIsSensedAndNothingThrows()
        {
            using var fixture = new GiEcsFixture();
            Assert.DoesNotThrow(() => fixture.Tick(3));
            fixture.SetProfileWith(Sampled);
            Scene(fixture, 10, 1, out _, out _, out _);
            fixture.Tick(2);
            fixture.ClearProfile();
            Assert.DoesNotThrow(() => fixture.Tick(3));
        }
    }
}
