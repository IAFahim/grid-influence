using System.Collections.Generic;
using NUnit.Framework;
using Unity.Entities;
using Gi.Entities.Tests.Support;
using GiWorld = Gi.World;

namespace Gi.Entities.Tests
{
    public sealed class GiTouchedListTests
    {
        [SetUp]
        public void SetUp()
        {
            GiRuntime.CompleteAll();
            GiOrphans.Reclaim(GiRuntime.Stamps);
        }

        [Test]
        public void NewEmitterPlacesWithinOneTick()
        {
            using var fixture = new GiEcsFixture();
            fixture.SetStandardProfile();
            var entity = fixture.CreateEmitter(0, 0, 6, 30f, 30f);
            fixture.Tick();
            fixture.Complete();
            var source = fixture.Manager.GetComponentData<GiSource>(entity);
            Assert.That(source.Placed, Is.True);
            Assert.That(GiWorld.TryGetSourceSpec(fixture.Frame.World, source.Id, out _), Is.True);
        }

        [Test]
        public void EnableBitToggleRemovesAndReplacesAcrossTicks()
        {
            using var fixture = new GiEcsFixture();
            fixture.SetStandardProfile();
            var entity = fixture.CreateEmitter(0, 0, 6, 30f, 30f);
            fixture.Tick(2);
            fixture.Complete();
            var id = fixture.Manager.GetComponentData<GiSource>(entity).Id;
            Assert.That(GiWorld.TryGetSourceSpec(fixture.Frame.World, id, out _), Is.True);

            fixture.Manager.SetComponentEnabled<GiEmitter>(entity, false);
            fixture.Tick();
            fixture.Complete();
            Assert.That(fixture.Manager.GetComponentData<GiSource>(entity).Placed, Is.False);
            Assert.That(GiWorld.TryGetSourceSpec(fixture.Frame.World, id, out _), Is.False);

            fixture.Manager.SetComponentEnabled<GiEmitter>(entity, true);
            fixture.Tick(2);
            fixture.Complete();
            var placed = fixture.Manager.GetComponentData<GiSource>(entity);
            Assert.That(placed.Placed, Is.True);
            Assert.That(GiWorld.TryGetSourceSpec(fixture.Frame.World, placed.Id, out _), Is.True);
        }

        [Test]
        public void SettledTicksLeaveNoDirtyEnabled()
        {
            using var fixture = new GiEcsFixture();
            fixture.SetStandardProfile();
            for (var i = 0; i < 50; i++) fixture.CreateEmitter((byte)(i % 2), (byte)(i % 6), 4, 10f + i, 10f);
            fixture.Tick(3);
            fixture.Complete();
            using var enabled = fixture.Manager.CreateEntityQuery(ComponentType.ReadOnly<GiDirty>());
            Assert.That(enabled.CalculateEntityCount(), Is.EqualTo(0));
        }

        [Test]
        public void ChurnSettlesBackToNoDirtyEnabled()
        {
            using var fixture = new GiEcsFixture();
            fixture.SetStandardProfile();
            var entities = new List<Entity>();
            for (var i = 0; i < 50; i++) entities.Add(fixture.CreateEmitter((byte)(i % 2), (byte)(i % 6), 4, 10f + i, 10f));
            fixture.Tick(3);
            fixture.Complete();
            for (var i = 0; i < 25; i++) fixture.Manager.DestroyEntity(entities[i]);
            fixture.Tick(3);
            fixture.Complete();
            using var enabled = fixture.Manager.CreateEntityQuery(ComponentType.ReadOnly<GiDirty>());
            Assert.That(enabled.CalculateEntityCount(), Is.EqualTo(0));
            for (var i = 25; i < 50; i++)
                Assert.That(fixture.Manager.GetComponentData<GiSource>(entities[i]).Placed, Is.True);
        }

        [Test]
        public void AForcedFullVisitReproducesTheTouchedWorldContent()
        {
            using var fixture = new GiEcsFixture();
            fixture.SetStandardProfile();
            var entities = new List<Entity>();
            for (var i = 0; i < 50; i++) entities.Add(fixture.CreateEmitter((byte)(i % 2), (byte)(i % 6), 4, 10f + i, 10f));
            fixture.Tick(3);
            fixture.Complete();
            var touched = ContentHash(fixture, entities);
            fixture.SetStandardProfile();
            fixture.Tick(3);
            fixture.Complete();
            Assert.That(ContentHash(fixture, entities), Is.EqualTo(touched));
            foreach (var entity in entities)
                Assert.That(fixture.Manager.GetComponentData<GiSource>(entity).Placed, Is.True);
        }

        private static ulong ContentHash(GiEcsFixture fixture, List<Entity> entities)
        {
            var hash = GiContentHash.Start();
            foreach (var entity in entities)
            {
                var source = fixture.Manager.GetComponentData<GiSource>(entity);
                hash.Add(source.Placed ? 1 : 0);
                hash.Add(source.Layer.Index);
                hash.Add(source.Stamp.Index);
                hash.Add(source.Gain);
                if (source.Placed && GiWorld.TryGetSourceSpec(fixture.Frame.World, source.Id, out var spec))
                {
                    hash.Add(spec.X);
                    hash.Add(spec.Y);
                    hash.Add(spec.HeadingRadians);
                    hash.Add(spec.Scale);
                    hash.Add(spec.Gain);
                    hash.Add(spec.Layer);
                    hash.Add(spec.Stamp);
                }
            }
            return hash.Finish();
        }
    }
}
