using System.Collections.Generic;
using BovineLabs.Core.EntityCommands;
using Gi.Entities.Tests.Authored;
using Gi.Entities.Tests.Support;
using NUnit.Framework;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
using GiStamp = Gi.Stamp;
using GiWorld = Gi.World;

namespace Gi.Entities.Tests
{
    public sealed class GiBuilderTests
    {
        private readonly List<Object> _made = new List<Object>();

        [SetUp]
        public void SetUp()
        {
            GiRuntime.CompleteAll();
            GiOrphans.Reclaim(GiRuntime.Stamps);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var made in _made)
                if (made != null)
                    Object.DestroyImmediate(made);
            _made.Clear();
        }

        private static int DefinedStamps()
        {
            var count = 0;
            for (var stamp = 1; stamp < 256; stamp++)
                if (GiStamp.TryGetSpec((byte)stamp, out _))
                    count++;
            return count;
        }

        [Test]
        public void TheEmitterBuilderAddsExactlyTheComponentsItsSettingsAskFor()
        {
            using var fixture = new GiEcsFixture();
            var manager = fixture.Manager;
            var plain = manager.CreateEntity();
            var commands = new EntityManagerCommands(manager, plain);
            GiEmitterBuilder.Apply(ref commands, new GiEmitterSettings { Layer = new GiLayerKey(1), Stamp = new GiStampKey(3), Gain = -4 });
            Assert.That(manager.GetComponentData<GiEmitter>(plain).Gain, Is.EqualTo((sbyte)-4));
            Assert.That(manager.GetComponentData<GiEmitter>(plain).Layer, Is.EqualTo(new GiLayerKey(1)));
            Assert.That(manager.IsComponentEnabled<GiFault>(plain), Is.False);
            Assert.That(manager.HasComponent<GiGainRamp>(plain), Is.False);
            Assert.That(manager.HasComponent<GiOriented>(plain), Is.False);
            Assert.That(manager.HasComponent<GiScaled>(plain), Is.False);

            var full = manager.CreateEntity();
            commands = new EntityManagerCommands(manager, full);
            GiEmitterBuilder.Apply(ref commands,
                new GiEmitterSettings { Layer = new GiLayerKey(0), Stamp = new GiStampKey(0), Gain = 5, RampTicks = 4, Oriented = true, Scaled = true });
            Assert.That(manager.GetComponentData<GiGainRamp>(full).Ticks, Is.EqualTo((ushort)4));
            Assert.That(manager.HasComponent<GiOriented>(full), Is.True);
            Assert.That(manager.HasComponent<GiScaled>(full), Is.True);

            var splat = manager.CreateEntity();
            var splatCommands = new EntityManagerCommands(manager, splat);
            GiSplatBuilder.Apply(ref splatCommands, new GiSplatSettings { Layer = new GiLayerKey(2), Stamp = new GiStampKey(1), Gain = 2 });
            Assert.That(manager.GetComponentData<GiSplat>(splat).Layer, Is.EqualTo(new GiLayerKey(2)));
            Assert.That(manager.IsComponentEnabled<GiFault>(splat), Is.False);
        }

        [Test]
        public void AStaticEmitterWithOnlyALocalToWorldIsStillPlaced()
        {
            using var fixture = new GiEcsFixture();
            fixture.SetStandardProfile();
            var manager = fixture.Manager;
            var entity = manager.CreateEntity();
            var commands = new EntityManagerCommands(manager, entity);
            GiEmitterBuilder.Apply(ref commands, new GiEmitterSettings { Layer = new GiLayerKey(0), Stamp = new GiStampKey(0), Gain = 6 });
            manager.AddComponentData(entity, new LocalToWorld { Value = float4x4.Translate(new float3(20f, 0f, 22f)) });

            fixture.Tick(3);
            fixture.Complete();
            Assert.That(manager.HasComponent<LocalTransform>(entity), Is.False);
            var source = manager.GetComponentData<GiSource>(entity);
            Assert.That(source.Placed, Is.True);
            Assert.That(GiWorld.TryGetSourceSpec(fixture.Frame.World, source.Id, out var spec), Is.True);
            Assert.That(spec.X, Is.EqualTo(20f));
            Assert.That(spec.Y, Is.EqualTo(22f));
        }

        [Test]
        public void EditingALayerRebuildsTheWorldReplacesEverySourceAndLeaksNothing()
        {
            var profile = GiAuthoredProfile.FromScenario(GiScenarioProfile.Standard(), _made);
            using var fixture = new GiEcsFixture();
            fixture.SetProfile(profile.ToSpec());
            var emitters = new List<Entity>();
            for (var i = 0; i < 12; i++)
            {
                var entity = fixture.Manager.CreateEntity();
                var commands = new EntityManagerCommands(fixture.Manager, entity);
                GiEmitterBuilder.Apply(ref commands, new GiEmitterSettings { Layer = new GiLayerKey(0), Stamp = new GiStampKey((byte)(i % 6)), Gain = 5 });
                fixture.Manager.AddComponentData(entity, LocalTransform.FromPosition(10f + i * 3f, 0f, 20f));
                emitters.Add(entity);
            }

            fixture.Tick(3);
            fixture.Complete();
            var first = fixture.Frame;
            Assert.That(first.Generation, Is.EqualTo(1u));
            Assert.That(GiOrphans.LiveWorlds(), Is.EqualTo(1));
            var stamps = DefinedStamps();

            profile.Layers[3].Shift = 2;
            fixture.SetProfile(profile.ToSpec());
            fixture.Tick(3);
            fixture.Complete();
            var second = fixture.Frame;

            Assert.That(second.Generation, Is.EqualTo(2u));
            Assert.That(second.ProfileHash, Is.Not.EqualTo(first.ProfileHash));
            Assert.That(GiOrphans.LiveWorlds(), Is.EqualTo(1), "the old world was freed");
            Assert.That(DefinedStamps(), Is.EqualTo(stamps), "no stamp leaked or was lost");
            foreach (var entity in emitters)
            {
                var source = fixture.Manager.GetComponentData<GiSource>(entity);
                Assert.That(source.Placed, Is.True);
                Assert.That(GiWorld.TryGetSourceSpec(second.World, source.Id, out _), Is.True);
            }

            profile.Layers[3].Shift = 1;
            fixture.SetProfile(profile.ToSpec());
            fixture.Tick(3);
            fixture.Complete();
            Assert.That(fixture.Frame.ProfileHash, Is.EqualTo(first.ProfileHash), "undoing the edit returns the same content hash");
            Assert.That(DefinedStamps(), Is.EqualTo(stamps));
        }
    }
}
