using System;
using Gi.Entities.Tests.Support;
using NUnit.Framework;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.TestTools;
using GiWorld = Gi.World;

namespace Gi.Entities.Tests
{
    public sealed class GiSenseKindsTests
    {
        private const float Reach = 14f;
        private const short Threshold = 3;
        private const int Probes = 240;

        internal static readonly GiBindingSpec Area = new GiBindingSpec
        {
            Kind = GiBindingKind.Area,
            Layer = 3,
            Reach = Reach,
            Total = GiBindingFields.Of<KindArea>("Total"),
        };

        internal static readonly GiBindingSpec Max = new GiBindingSpec
        {
            Kind = GiBindingKind.Max,
            Layer = 4,
            Reach = Reach,
            Value = GiBindingFields.Of<KindMax>("Value"),
            Position = GiBindingFields.Of<KindMax>("Position"),
        };

        internal static readonly GiBindingSpec Nearest = new GiBindingSpec
        {
            Kind = GiBindingKind.Nearest,
            Layer = 1,
            Reach = Reach,
            Threshold = Threshold,
            Value = GiBindingFields.Of<KindNearest>("Value"),
            Position = GiBindingFields.Of<KindNearest>("Position"),
            Coverage = GiBindingFields.Tag<KindFound>(),
        };

        internal static readonly GiBindingSpec Changed = new GiBindingSpec
        {
            Kind = GiBindingKind.Changed,
            Layer = 1,
            Reach = 4f,
            Since = 3,
            Coverage = GiBindingFields.Tag<KindWake>(),
        };

        [SetUp]
        public void SetUp()
        {
            GiRuntime.CompleteAll();
            GiOrphans.Reclaim(GiRuntime.Stamps);
        }

        private static Entity[] Scene(GiEcsFixture fixture, int probes, ulong seed, bool emitters, out float[] xs, out float[] zs)
        {
            var rng = new GiRandom(seed);
            for (var i = 0; i < 24; i++)
                fixture.CreateEmitter((byte)(i % 2), (byte)rng.Next(6), (sbyte)(2 + rng.Next(10)), 6 + rng.Next(52), 6 + rng.Next(52));

            var archetype = emitters
                ? fixture.Manager.CreateArchetype(typeof(LocalTransform), typeof(GiEmitter), typeof(GiFault), typeof(KindValue),
                    typeof(KindArea), typeof(KindMax), typeof(KindNearest), typeof(KindFound), typeof(KindWake), typeof(KindReach))
                : fixture.Manager.CreateArchetype(typeof(LocalTransform), typeof(KindValue), typeof(KindArea), typeof(KindMax),
                    typeof(KindNearest), typeof(KindFound), typeof(KindWake), typeof(KindReach));
            var entities = new Entity[probes];
            xs = new float[probes];
            zs = new float[probes];
            for (var i = 0; i < probes; i++)
            {
                xs[i] = 4 + rng.Next(56) + rng.Next(8) * 0.125f;
                zs[i] = 4 + rng.Next(56) + rng.Next(8) * 0.125f;
                entities[i] = fixture.Manager.CreateEntity(archetype);
                fixture.Manager.SetComponentData(entities[i], LocalTransform.FromPosition(xs[i], 0f, zs[i]));
                fixture.Manager.SetComponentData(entities[i], new KindReach { Reach = 6f + rng.Next(14), Threshold = (short)(1 + rng.Next(6)) });
                fixture.Manager.SetComponentEnabled<KindFound>(entities[i], false);
                fixture.Manager.SetComponentEnabled<KindWake>(entities[i], false);
                if (emitters)
                {
                    fixture.Manager.SetComponentData(entities[i], new GiEmitter { Layer = new GiLayerKey(0), Stamp = new GiStampKey(0), Gain = 6 });
                    fixture.Manager.SetComponentEnabled<GiFault>(entities[i], false);
                }
            }

            return entities;
        }

        private static bool Same(float expected, float actual)
        {
            return BitConverter.SingleToInt32Bits(expected) == BitConverter.SingleToInt32Bits(actual);
        }

        [Test]
        public void AreaMaxNearestAndChangedMatchTheScalarCalls()
        {
            using var fixture = new GiEcsFixture();
            fixture.SetProfileWith(Area, Max, Nearest, Changed);
            var entities = Scene(fixture, Probes, 7, false, out var xs, out var zs);
            fixture.Tick(3);
            fixture.Complete();

            var frame = fixture.Frame;
            frame.Tables.TryLayer(new GiLayerKey(3), out var areaLayer);
            frame.Tables.TryLayer(new GiLayerKey(4), out var maxLayer);
            frame.Tables.TryLayer(new GiLayerKey(1), out var cellLayer);
            var found = 0;
            var woke = 0;
            for (var i = 0; i < entities.Length; i++)
            {
                var areaOk = GiWorld.TrySenseArea(frame.World, areaLayer, xs[i], zs[i], Reach, out var total);
                Assert.That(fixture.Manager.GetComponentData<KindArea>(entities[i]).Total, Is.EqualTo(total), $"area {i}");

                var maxOk = GiWorld.TrySenseMax(frame.World, maxLayer, xs[i], zs[i], Reach, out var best, out var bx, out var by);
                var max = fixture.Manager.GetComponentData<KindMax>(entities[i]);
                Assert.That(max.Value, Is.EqualTo(best), $"max {i}");
                if (maxOk)
                {
                    Assert.That(Same(bx, max.Position.x), Is.True, $"max x {i}");
                    Assert.That(Same(by, max.Position.y), Is.True, $"max y {i}");
                }

                GiWorld.TrySenseNearest(frame.World, cellLayer, xs[i], zs[i], Reach, Threshold, out var near, out var nx, out var ny);
                var nearOk = near != short.MinValue;
                var nearest = fixture.Manager.GetComponentData<KindNearest>(entities[i]);
                Assert.That(nearest.Value, Is.EqualTo(near), $"nearest {i}");
                Assert.That(fixture.Manager.IsComponentEnabled<KindFound>(entities[i]), Is.EqualTo(nearOk), $"found {i}");
                if (nearOk)
                {
                    found++;
                    Assert.That(Same(nx, nearest.Position.x), Is.True, $"nearest x {i}");
                    Assert.That(Same(ny, nearest.Position.y), Is.True, $"nearest y {i}");
                }

                var changed = GiWorld.Changed(frame.World, cellLayer, xs[i], zs[i], 4f, frame.Tick - 3);
                Assert.That(fixture.Manager.IsComponentEnabled<KindWake>(entities[i]), Is.EqualTo(changed), $"wake {i}");
                if (changed) woke++;
                if (areaOk) Assert.That(total, Is.Not.EqualTo(long.MinValue));
            }

            Assert.That(found, Is.GreaterThan(5));
            Assert.That(woke, Is.GreaterThan(5));
            Assert.That(woke, Is.LessThan(entities.Length));
            Assert.That(fixture.Manager.GetComponentData<GiStats>(fixture.FrameEntity).Sensed, Is.EqualTo(4 * entities.Length));
        }

        [Test]
        public void ReachAndThresholdFieldsAreReadPerEntity()
        {
            var perEntityArea = Area;
            perEntityArea.Reach = 0f;
            perEntityArea.ReachField = GiBindingFields.Of<KindReach>("Reach");
            var perEntityNearest = Nearest;
            perEntityNearest.Reach = 0f;
            perEntityNearest.ReachField = GiBindingFields.Of<KindReach>("Reach");
            perEntityNearest.ThresholdField = GiBindingFields.Of<KindReach>("Threshold");

            using var fixture = new GiEcsFixture();
            fixture.SetProfileWith(perEntityArea, perEntityNearest);
            var entities = Scene(fixture, Probes, 11, false, out var xs, out var zs);
            fixture.Tick(3);
            fixture.Complete();

            var frame = fixture.Frame;
            frame.Tables.TryLayer(new GiLayerKey(3), out var areaLayer);
            frame.Tables.TryLayer(new GiLayerKey(1), out var cellLayer);
            var differing = 0;
            for (var i = 0; i < entities.Length; i++)
            {
                var field = fixture.Manager.GetComponentData<KindReach>(entities[i]);
                GiWorld.TrySenseArea(frame.World, areaLayer, xs[i], zs[i], field.Reach, out var total);
                Assert.That(fixture.Manager.GetComponentData<KindArea>(entities[i]).Total, Is.EqualTo(total), $"area {i}");

                GiWorld.TrySenseNearest(frame.World, cellLayer, xs[i], zs[i], field.Reach, field.Threshold, out var near, out _, out _);
                Assert.That(fixture.Manager.GetComponentData<KindNearest>(entities[i]).Value, Is.EqualTo(near), $"nearest {i}");
                Assert.That(fixture.Manager.IsComponentEnabled<KindFound>(entities[i]), Is.EqualTo(near != short.MinValue), $"found {i}");

                GiWorld.TrySenseArea(frame.World, areaLayer, xs[i], zs[i], Reach, out var constant);
                if (constant != total) differing++;
            }

            Assert.That(differing, Is.GreaterThan(20), "per-entity reach must matter");
        }

        [Test]
        public void ExcludeSelfMatchesTrySenseWithTheEntitysOwnSource()
        {
            var excluding = new GiBindingSpec
            {
                Kind = GiBindingKind.Value,
                Layer = 0,
                Flags = GiBindingSpec.ExcludeSelf,
                Value = GiBindingFields.Of<KindValue>("Value"),
            };
            var excludingArea = Area;
            excludingArea.Layer = 0;
            excludingArea.Flags = GiBindingSpec.ExcludeSelf;

            using var fixture = new GiEcsFixture();
            fixture.SetProfileWith(excluding, excludingArea);
            var entities = Scene(fixture, 120, 13, true, out var xs, out var zs);
            fixture.Tick(4);
            fixture.Complete();

            var frame = fixture.Frame;
            frame.Tables.TryLayer(new GiLayerKey(0), out var layer);
            var different = 0;
            for (var i = 0; i < entities.Length; i++)
            {
                var id = fixture.Manager.GetComponentData<GiSource>(entities[i]).Id;
                Assert.That(id.Generation, Is.Not.EqualTo(0), $"entity {i} has no source");

                GiWorld.TrySense(frame.World, layer, xs[i], zs[i], id, out var excluded);
                Assert.That(fixture.Manager.GetComponentData<KindValue>(entities[i]).Value, Is.EqualTo(excluded), $"value {i}");

                GiWorld.TrySenseArea(frame.World, layer, xs[i], zs[i], Reach, id, out var excludedTotal);
                Assert.That(fixture.Manager.GetComponentData<KindArea>(entities[i]).Total, Is.EqualTo(excludedTotal), $"area {i}");

                GiWorld.TrySense(frame.World, layer, xs[i], zs[i], out var included);
                if (included != excluded) different++;
            }

            Assert.That(different, Is.GreaterThan(30), "excluding the own source must change the value");
        }

        [Test]
        public void ExcludeSelfOnATransientLayerIsRejectedAtBootstrap()
        {
            var rejected = new GiBindingSpec
            {
                Kind = GiBindingKind.Value,
                Layer = 2,
                Flags = GiBindingSpec.ExcludeSelf,
                Value = GiBindingFields.Of<KindValue>("Value"),
            };
            using var fixture = new GiEcsFixture();
            fixture.SetProfileWith(rejected, Area);
            var entities = Scene(fixture, 40, 5, false, out _, out _);
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("Gi binding 0 rejected: .*transient"));
            fixture.Tick(3);
            fixture.Complete();

            Assert.That(fixture.Frame.Faults.Has(GiFaultKind.Profile), Is.True);
            var ran = false;
            foreach (var entity in entities)
                if (fixture.Manager.GetComponentData<KindArea>(entity).Total != 0)
                    ran = true;
            Assert.That(ran, Is.True, "the valid binding after the rejected one still ran");
        }

        [Test]
        public void ABindingWithoutAReachIsRejectedAtBootstrap()
        {
            var noReach = Area;
            noReach.Reach = 0f;
            using var fixture = new GiEcsFixture();
            fixture.SetProfileWith(noReach);
            Scene(fixture, 10, 5, false, out _, out _);
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("Gi binding 0 rejected: .*reach"));
            fixture.Tick(2);
            fixture.Complete();
            Assert.That(fixture.Frame.Faults.Has(GiFaultKind.Profile), Is.True);
        }

        [Test]
        public void WithAPeriodOfNEachEntityIsSensedExactlyOnceEveryNTicks()
        {
            const int period = 3;
            var periodic = new GiBindingSpec
            {
                Kind = GiBindingKind.Value,
                Layer = 1,
                Period = period,
                Value = GiBindingFields.Of<KindValue>("Value"),
            };
            using var fixture = new GiEcsFixture();
            fixture.SetProfileWith(periodic);
            var entities = Scene(fixture, 150, 17, false, out _, out _);
            fixture.Tick(3);
            fixture.Complete();

            var timesWritten = new int[entities.Length];
            for (var round = 0; round < 2 * period; round++)
            {
                foreach (var entity in entities) fixture.Manager.SetComponentData(entity, new KindValue { Value = -1234 });
                fixture.Tick();
                fixture.Complete();

                var tick = fixture.Frame.Tick;
                var sensed = 0;
                for (var i = 0; i < entities.Length; i++)
                {
                    var written = fixture.Manager.GetComponentData<KindValue>(entities[i]).Value != -1234;
                    var due = entities[i].Index % period == tick % period;
                    Assert.That(written, Is.EqualTo(due), $"entity {i} index {entities[i].Index} tick {tick}");
                    if (written)
                    {
                        timesWritten[i]++;
                        sensed++;
                    }
                }

                Assert.That(fixture.Manager.GetComponentData<GiStats>(fixture.FrameEntity).Sensed, Is.EqualTo(sensed));
            }

            foreach (var count in timesWritten) Assert.That(count, Is.EqualTo(2));
        }

        [Test]
        public void SensingEveryKindAllocatesNothingPerTick()
        {
            using var fixture = new GiEcsFixture();
            fixture.SetProfileWith(Area, Max, Nearest, Changed);
            Scene(fixture, 1000, 4, false, out _, out _);
            fixture.Tick(100);

            Assert.That(GiAllocation.SteadyZero(() => fixture.Tick(50), 14, 3), Is.True);
            Assert.That(fixture.Manager.GetComponentData<GiStats>(fixture.FrameEntity).Sensed, Is.EqualTo(4000));
        }

        [Test]
        public void TheBatchSizeNeverChangesAnExtendedOutput()
        {
            var snapshots = new System.Collections.Generic.List<long[]>();
            foreach (var batch in new[] { 64, 192, 4096 })
            {
                GiSenseConfig.Batch.Data = batch;
                try
                {
                    using var fixture = new GiEcsFixture("KindBatch" + batch);
                    fixture.SetProfileWith(Area, Max, Nearest, Changed);
                    var entities = Scene(fixture, 700, 9, false, out _, out _);
                    fixture.Tick(3);
                    fixture.Complete();
                    var digest = new long[entities.Length * 4];
                    for (var i = 0; i < entities.Length; i++)
                    {
                        digest[4 * i] = fixture.Manager.GetComponentData<KindArea>(entities[i]).Total;
                        digest[4 * i + 1] = fixture.Manager.GetComponentData<KindMax>(entities[i]).Value;
                        digest[4 * i + 2] = fixture.Manager.GetComponentData<KindNearest>(entities[i]).Value;
                        digest[4 * i + 3] = (fixture.Manager.IsComponentEnabled<KindFound>(entities[i]) ? 1 : 0) |
                                            (fixture.Manager.IsComponentEnabled<KindWake>(entities[i]) ? 2 : 0);
                    }

                    snapshots.Add(digest);
                }
                finally
                {
                    GiSenseConfig.Batch.Data = 0;
                }
            }

            for (var s = 1; s < snapshots.Count; s++) Assert.That(snapshots[s], Is.EqualTo(snapshots[0]));
        }
    }
}
