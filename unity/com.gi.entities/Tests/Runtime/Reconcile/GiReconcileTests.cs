using System.Collections.Generic;
using Gi.Entities.Tests.Support;
using NUnit.Framework;
using BovineLabs.Core.SingletonCollection;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using GiWorld = Gi.World;

namespace Gi.Entities.Tests
{
    public sealed class GiReconcileTests
    {
        private static readonly GiScenarioOptions Options = GiScenarioOptions.Standard.WithoutTransforms().WithoutSplats();

        [SetUp]
        public void SetUp()
        {
            GiRuntime.CompleteAll();
            GiOrphans.Reclaim(GiRuntime.Stamps);
        }

        [Test]
        public void EcsMatchesTheReferenceOverFiftySeeds()
        {
            for (ulong seed = 1; seed <= 50; seed++)
            {
                var scenario = GiScenarioGenerator.Create(seed, Options);
                var expected = GiReferenceExecutor.Run(scenario);
                using var executor = new GiEcsExecutor(scenario);
                var actual = executor.Run();
                Assert.That(expected.Compare(actual), Is.EqualTo(string.Empty), $"seed {seed}");
            }
        }

        [Test]
        public void EcsMatchesTheReferenceWithTheOperationOrderShuffled()
        {
            for (ulong seed = 60; seed <= 80; seed++)
            {
                var scenario = GiScenarioGenerator.Create(seed, Options);
                var expected = GiReferenceExecutor.Run(scenario);
                using var executor = new GiEcsExecutor(scenario.WithOpOrderShuffled(seed));
                Assert.That(expected.Compare(executor.Run()), Is.EqualTo(string.Empty), $"seed {seed}");
            }
        }

        private static ulong EmptyHash(GiEcsFixture fixture, int layer)
        {
            fixture.Complete();
            var frame = fixture.Frame;
            frame.Tables.TryLayer(new GiLayerKey((byte)layer), out var id);
            return GiFieldHash.OfLayer(frame.World, id);
        }

        [Test]
        public void EmittersOnTransientOrDerivedLayersFaultWithoutThrowing()
        {
            using var fixture = new GiEcsFixture();
            fixture.SetStandardProfile();
            var transient = fixture.CreateEmitter(2, 0, 4, 20f, 20f);
            var derived = fixture.CreateEmitter(3, 0, 4, 30f, 30f);
            var good = fixture.CreateEmitter(0, 0, 4, 40f, 40f);

            Assert.DoesNotThrow(() => fixture.Tick(3));
            fixture.Complete();
            foreach (var bad in new[] { transient, derived })
            {
                Assert.That(fixture.Manager.GetComponentData<GiSource>(bad).Placed, Is.False);
                Assert.That(fixture.Manager.IsComponentEnabled<GiFault>(bad), Is.True);
                Assert.That(fixture.Manager.GetComponentData<GiFault>(bad).Code, Is.Not.EqualTo(GiFaultCode.None));
            }

            Assert.That(fixture.Manager.GetComponentData<GiSource>(good).Placed, Is.True);
            Assert.That(fixture.Manager.IsComponentEnabled<GiFault>(good), Is.False);
        }

        [Test]
        public void AnUnknownLayerKeyFaultsAndNeverPlaces()
        {
            using var fixture = new GiEcsFixture();
            fixture.SetStandardProfile();
            var entity = fixture.CreateEmitter(0, 0, 4, 20f, 20f);
            fixture.Tick();
            fixture.Complete();
            Assert.That(fixture.Manager.GetComponentData<GiSource>(entity).Placed, Is.True);

            fixture.Manager.SetComponentData(entity, new GiEmitter { Layer = new GiLayerKey(200), Stamp = new GiStampKey(0), Gain = 4 });
            fixture.Tick();
            fixture.Complete();
            Assert.That(fixture.Manager.GetComponentData<GiSource>(entity).Placed, Is.False);
            Assert.That(fixture.Manager.GetComponentData<GiFault>(entity).Code, Is.EqualTo(GiFaultCode.InvalidLayer));
            Assert.That(fixture.Manager.IsComponentEnabled<GiFault>(entity), Is.True);
        }

        [Test]
        public void QuietTicksMakeNoStructuralChange()
        {
            using var fixture = new GiEcsFixture();
            fixture.SetStandardProfile();
            for (var i = 0; i < 100; i++) fixture.CreateEmitter(0, (byte)(i % 6), 4, 10f + i % 40, 10f + i / 40);
            fixture.Tick(2);
            fixture.Complete();

            var before = fixture.Manager.GetComponentOrderVersion<GiSource>();
            fixture.Tick(5);
            fixture.Complete();
            Assert.That(fixture.Manager.GetComponentOrderVersion<GiSource>(), Is.EqualTo(before));

            fixture.CreateEmitter(0, 0, 4, 50f, 50f);
            fixture.Tick();
            fixture.Complete();
            Assert.That(fixture.Manager.GetComponentOrderVersion<GiSource>(), Is.Not.EqualTo(before));
        }

        [Test]
        public void SteadyTicksWithPlacedEmittersAllocateNothing()
        {
            using var fixture = new GiEcsFixture();
            fixture.SetStandardProfile();
            for (var i = 0; i < 500; i++) fixture.CreateEmitter((byte)(i % 2), (byte)(i % 6), 4, 10f + i % 45, 10f + i / 45);
            fixture.Tick(200);

            Assert.That(GiAllocation.SteadyZero(() => fixture.Tick(50), 14, 3), Is.True);
        }

        [Test]
        public void ADisabledTagRemovesAndRemovingItPlacesAgain()
        {
            using var fixture = new GiEcsFixture();
            fixture.SetStandardProfile();
            var entity = fixture.CreateEmitter(0, 0, 6, 30f, 30f);
            fixture.Tick();
            fixture.Complete();
            var id = fixture.Manager.GetComponentData<GiSource>(entity).Id;
            Assert.That(GiWorld.TryGetSourceSpec(fixture.Frame.World, id, out _), Is.True);

            fixture.Manager.AddComponent<Disabled>(entity);
            fixture.Tick();
            fixture.Complete();
            Assert.That(fixture.Manager.GetComponentData<GiSource>(entity).Placed, Is.False);
            Assert.That(GiWorld.TryGetSourceSpec(fixture.Frame.World, id, out _), Is.False);

            fixture.Manager.RemoveComponent<Disabled>(entity);
            fixture.Tick();
            fixture.Complete();
            Assert.That(fixture.Manager.GetComponentData<GiSource>(entity).Placed, Is.True);
        }

        [Test]
        public void PrefabsAreNeverPlaced()
        {
            using var fixture = new GiEcsFixture();
            fixture.SetStandardProfile();
            var prefab = fixture.CreateEmitter(0, 0, 6, 30f, 30f);
            fixture.Manager.AddComponent<Prefab>(prefab);
            fixture.Tick(3);
            fixture.Complete();
            Assert.That(fixture.Manager.HasComponent<GiSource>(prefab), Is.False);

            var instance = fixture.Manager.Instantiate(prefab);
            fixture.Tick(2);
            fixture.Complete();
            Assert.That(fixture.Manager.GetComponentData<GiSource>(instance).Placed, Is.True);
        }

        [Test]
        public void ADestroyedEmitterIsRemovedFromGiThenItsCleanupGoesAway()
        {
            using var fixture = new GiEcsFixture();
            fixture.SetStandardProfile();
            var entity = fixture.CreateEmitter(0, 0, 6, 30f, 30f);
            fixture.Tick();
            fixture.Complete();
            var id = fixture.Manager.GetComponentData<GiSource>(entity).Id;
            var empty = EmptyHash(fixture, 1);

            fixture.Manager.DestroyEntity(entity);
            Assert.That(fixture.Manager.Exists(entity), Is.True);
            Assert.That(fixture.Manager.HasComponent<GiSource>(entity), Is.True);
            Assert.That(fixture.Manager.HasComponent<GiEmitter>(entity), Is.False);

            fixture.Tick();
            fixture.Complete();
            Assert.That(GiWorld.TryGetSourceSpec(fixture.Frame.World, id, out _), Is.False);
            Assert.That(fixture.Manager.Exists(entity), Is.True);

            fixture.Tick();
            fixture.Complete();
            Assert.That(fixture.Manager.Exists(entity), Is.False);
            Assert.That(EmptyHash(fixture, 1), Is.EqualTo(empty));
        }

        [Test]
        public void ARebuildPlacesEverySourceAgainInTheNewWorld()
        {
            using var fixture = new GiEcsFixture();
            fixture.SetStandardProfile();
            var entities = new List<Entity>();
            for (var i = 0; i < 20; i++) entities.Add(fixture.CreateEmitter(0, (byte)(i % 6), 5, 12f + i * 2f, 20f));
            fixture.Tick(2);
            fixture.Complete();
            var first = fixture.Frame.World;

            var changed = GiScenarioProfile.Standard().ToSpec();
            changed.Grids[1].Power = 8;
            fixture.SetProfile(changed);
            fixture.Tick(2);
            fixture.Complete();

            var frame = fixture.Frame;
            Assert.That(frame.Generation, Is.EqualTo(2u));
            Assert.That(GiOrphans.LiveWorlds(), Is.EqualTo(1));
            foreach (var entity in entities)
            {
                var source = fixture.Manager.GetComponentData<GiSource>(entity);
                Assert.That(source.Placed, Is.True);
                Assert.That(GiWorld.TryGetSourceSpec(frame.World, source.Id, out _), Is.True);
            }

            using var reference = new GiEcsFixture("Fresh");
            reference.SetProfile(changed);
            for (var i = 0; i < 20; i++) reference.CreateEmitter(0, (byte)(i % 6), 5, 12f + i * 2f, 20f);
            reference.Tick(2);
            for (var layer = 0; layer < 6; layer++) Assert.That(EmptyHash(fixture, layer), Is.EqualTo(EmptyHash(reference, layer)));
            Assert.That(first, Is.GreaterThanOrEqualTo((byte)0));
        }

        [Test]
        public void PulsesLastExactlyTheirTicksWithoutAnyEntity()
        {
            using var fixture = new GiEcsFixture();
            fixture.SetStandardProfile();
            fixture.Tick();
            var empty = EmptyHash(fixture, 0);
            using var pulses = fixture.Manager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadWrite<GiPulseSingleton>() },
                Options = EntityQueryOptions.IncludeSystems,
            });

            var writer = pulses.GetSingleton<GiPulseSingleton>().CreateThreadStream();
            writer.Write(new GiPulse { Layer = new GiLayerKey(0), Stamp = new GiStampKey(0), X = 30f, Y = 30f, Scale = 1f, Gain = 8, Ticks = 2 });
            fixture.Tick();
            Assert.That(EmptyHash(fixture, 0), Is.Not.EqualTo(empty));
            fixture.Tick();
            Assert.That(EmptyHash(fixture, 0), Is.EqualTo(empty));
            Assert.That(fixture.Manager.CreateEntityQuery(typeof(GiSource)).CalculateEntityCount(), Is.EqualTo(0));
        }

        [Test]
        public void PulseOrderOfEmissionNeverChangesTheField()
        {
            var pulses = new List<GiPulse>();
            var rng = new GiRandom(21);
            for (var i = 0; i < 30; i++)
                pulses.Add(new GiPulse
                {
                    Layer = new GiLayerKey((byte)(rng.Next(2) * 5)),
                    Stamp = new GiStampKey((byte)rng.Next(6)),
                    X = 10 + rng.Next(40),
                    Y = 10 + rng.Next(40),
                    Scale = 1f,
                    Gain = (sbyte)(1 + rng.Next(8)),
                    Ticks = (ushort)(2 + rng.Next(3)),
                    FadeOut = rng.Chance(1, 2),
                });

            var hashes = new List<ulong[]>();
            for (var run = 0; run < 3; run++)
            {
                var order = pulses.ToArray();
                if (run > 0) new GiRandom((ulong)run).Shuffle(order);
                using var fixture = new GiEcsFixture("Pulses" + run);
                fixture.SetStandardProfile();
                fixture.Tick();
                using var query = fixture.Manager.CreateEntityQuery(new EntityQueryDesc
                {
                    All = new[] { ComponentType.ReadWrite<GiPulseSingleton>() },
                    Options = EntityQueryOptions.IncludeSystems,
                });
                var writer = query.GetSingleton<GiPulseSingleton>().CreateThreadStream();
                foreach (var pulse in order) writer.Write(pulse);
                fixture.Tick();
                var layers = new ulong[6];
                for (var l = 0; l < 6; l++) layers[l] = EmptyHash(fixture, l);
                hashes.Add(layers);
            }

            Assert.That(hashes[1], Is.EqualTo(hashes[0]));
            Assert.That(hashes[2], Is.EqualTo(hashes[0]));
        }

        [Test]
        public void EnableableQueriesBehaveAsTheReconcilePassAssumes()
        {
            using var fixture = new GiEcsFixture();
            var enabled = fixture.CreateEmitter(0, 0, 1, 0f, 0f);
            var disabled = fixture.CreateEmitter(0, 0, 1, 0f, 0f);
            fixture.Manager.SetComponentEnabled<GiEmitter>(disabled, false);
            var absent = fixture.Manager.CreateEntity(typeof(LocalTransform));

            using var all = new EntityQueryBuilder(Allocator.Temp).WithAll<LocalTransform, GiEmitter>().Build(fixture.Manager);
            using var withDisabled = new EntityQueryBuilder(Allocator.Temp).WithAll<LocalTransform>().WithDisabled<GiEmitter>().Build(fixture.Manager);
            using var withAbsent = new EntityQueryBuilder(Allocator.Temp).WithAll<LocalTransform>().WithAbsent<GiEmitter>().Build(fixture.Manager);
            using var withNone = new EntityQueryBuilder(Allocator.Temp).WithAll<LocalTransform>().WithNone<GiEmitter>().Build(fixture.Manager);

            Assert.That(all.ToEntityArray(Allocator.Temp).ToArray(), Is.EquivalentTo(new[] { enabled }), "WithAll");
            Assert.That(withDisabled.ToEntityArray(Allocator.Temp).ToArray(), Is.EquivalentTo(new[] { disabled }), "WithDisabled");
            Assert.That(withAbsent.ToEntityArray(Allocator.Temp).ToArray(), Is.EquivalentTo(new[] { absent }), "WithAbsent");
            Assert.That(withNone.ToEntityArray(Allocator.Temp).ToArray(), Is.EquivalentTo(new[] { disabled, absent }), "WithNone");
        }

        [Test]
        public void BaseEntityIndexArrayCountsOnlyEnabledEntities()
        {
            using var fixture = new GiEcsFixture();
            var entities = new List<Entity>();
            for (var i = 0; i < 600; i++) entities.Add(fixture.CreateEmitter(0, 0, 1, i, 0f));
            for (var i = 0; i < 600; i += 3) fixture.Manager.SetComponentEnabled<GiEmitter>(entities[i], false);

            using var query = new EntityQueryBuilder(Allocator.Temp).WithAll<GiEmitter>().Build(fixture.Manager);
            var expected = query.CalculateEntityCount();
            Assert.That(expected, Is.EqualTo(400));

            var bases = query.CalculateBaseEntityIndexArrayAsync(Allocator.TempJob, default, out var handle);
            handle.Complete();
            var chunks = query.ToArchetypeChunkArray(Allocator.TempJob);
            Assert.That(bases.Length, Is.EqualTo(chunks.Length));
            var running = 0;
            var emitterHandle = fixture.Manager.GetComponentTypeHandle<GiEmitter>(true);
            for (var c = 0; c < chunks.Length; c++)
            {
                Assert.That(bases[c], Is.EqualTo(running));
                var mask = chunks[c].GetEnabledMask(ref emitterHandle);
                for (var i = 0; i < chunks[c].Count; i++)
                    if (mask[i])
                        running++;
            }

            Assert.That(running, Is.EqualTo(expected));
            chunks.Dispose();
            bases.Dispose();
        }
    }
}
