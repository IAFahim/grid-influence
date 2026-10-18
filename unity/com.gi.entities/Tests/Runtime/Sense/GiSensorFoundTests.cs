using System;
using Gi.Entities.Tests.Support;
using NUnit.Framework;
using Unity.Entities;
using Unity.Transforms;
using GiWorld = Gi.World;

namespace Gi.Entities.Tests
{
    public sealed class GiSensorFoundTests
    {
        internal static readonly GiBindingSpec Binding = new GiBindingSpec
        {
            Kind = GiBindingKind.Nearest,
            Layer = 1,
            ReachField = GiBindingFields.Of<GiSensorNearest>("Reach"),
            ThresholdField = GiBindingFields.Of<GiSensorNearest>("Threshold"),
            Value = GiBindingFields.Of<GiSensorNearest>("Value"),
            Position = GiBindingFields.Of<GiSensorNearest>("Position"),
            Coverage = GiBindingFields.Tag<GiSensorFound>(),
        };

        [SetUp]
        public void SetUp()
        {
            GiRuntime.CompleteAll();
            GiOrphans.Reclaim(GiRuntime.Stamps);
        }

        private Entity Sensor(GiEcsFixture fixture, float x, float z, float reach, short threshold)
        {
            var entity = fixture.Manager.CreateEntity(typeof(LocalTransform), typeof(GiSensorNearest), typeof(GiSensorFound));
            fixture.Manager.SetComponentData(entity, LocalTransform.FromPosition(x, 0f, z));
            fixture.Manager.SetComponentData(entity, new GiSensorNearest { Reach = reach, Threshold = threshold });
            fixture.Manager.SetComponentEnabled<GiSensorFound>(entity, false);
            return entity;
        }

        [Test]
        public void ANearestBindingThatStopsBeingFoundDisablesTheComponentOnTheNextTick()
        {
            using var fixture = new GiEcsFixture();
            fixture.SetProfileWith(Binding);
            var sensor = Sensor(fixture, 20f, 20f, 24f, 1);
            var emitter = fixture.CreateEmitter(1, 0, 8, 22f, 22f);
            fixture.Tick(2);
            fixture.Complete();

            var frame = fixture.Frame;
            frame.Tables.TryLayer(new GiLayerKey(1), out var layer);
            GiWorld.TrySenseNearest(frame.World, layer, 20f, 20f, 24f, 1, out var near, out var nx, out var ny);
            Assert.That(near, Is.Not.EqualTo(short.MinValue));
            Assert.That(fixture.Manager.IsComponentEnabled<GiSensorFound>(sensor), Is.True);
            var sensed = fixture.Manager.GetComponentData<GiSensorNearest>(sensor);
            Assert.That(sensed.Value, Is.EqualTo(near));
            Assert.That(BitConverter.SingleToInt32Bits(sensed.Position.x), Is.EqualTo(BitConverter.SingleToInt32Bits(nx)));
            Assert.That(BitConverter.SingleToInt32Bits(sensed.Position.y), Is.EqualTo(BitConverter.SingleToInt32Bits(ny)));

            fixture.Manager.DestroyEntity(emitter);
            fixture.Tick(1);
            fixture.Complete();

            GiWorld.TrySenseNearest(fixture.Frame.World, layer, 20f, 20f, 24f, 1, out near, out _, out _);
            Assert.That(near, Is.EqualTo(short.MinValue));
            Assert.That(fixture.Manager.IsComponentEnabled<GiSensorFound>(sensor), Is.False,
                "the found bit must disable on the tick after the source is gone");
            Assert.That(fixture.Manager.GetComponentData<GiSensorNearest>(sensor).Value, Is.EqualTo(short.MinValue));
        }

        [Test]
        public void ANearestSensorParksDisabledUntilTheSensePassFindsAHit()
        {
            using var fixture = new GiEcsFixture();
            fixture.SetProfileWith(Binding);
            var sensor = Sensor(fixture, 20f, 20f, 24f, 1);
            fixture.Tick(3);
            fixture.Complete();

            Assert.That(fixture.Manager.IsComponentEnabled<GiSensorFound>(sensor), Is.False);
            Assert.That(fixture.Manager.GetComponentData<GiSensorNearest>(sensor).Value, Is.EqualTo(short.MinValue));

            var emitter = fixture.CreateEmitter(1, 0, 8, 23f, 20f);
            fixture.Tick(1);
            fixture.Complete();

            Assert.That(fixture.Manager.IsComponentEnabled<GiSensorFound>(sensor), Is.True);
            var frame = fixture.Frame;
            frame.Tables.TryLayer(new GiLayerKey(1), out var layer);
            GiWorld.TrySenseNearest(frame.World, layer, 20f, 20f, 24f, 1, out var near, out _, out _);
            Assert.That(fixture.Manager.GetComponentData<GiSensorNearest>(sensor).Value, Is.EqualTo(near));
        }
    }
}
