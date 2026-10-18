using System;
using System.Linq;
using Gi.Entities.Netcode;
using Gi.Entities.Tests.Support;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;
using Unity.NetCode.Tests;
using Unity.Transforms;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.TestTools;

namespace Gi.Entities.Netcode.Tests
{
    public sealed class TestEmitterAuthoring : MonoBehaviour
    {
    }

    public sealed class TestEmitterBaker : Baker<TestEmitterAuthoring>
    {
        public override void Bake(TestEmitterAuthoring authoring)
        {
            var entity = GetEntity(TransformUsageFlags.Dynamic);
            AddComponent(entity, new GiEmitter { Layer = new GiLayerKey(0), Stamp = new GiStampKey(0), Gain = 1 });
            AddComponent(entity, default(GiFault));
            SetComponentEnabled<GiFault>(entity, false);
        }
    }

    public sealed class GiNetcodeTests
    {
        private static readonly Type[] Core =
        {
            typeof(GiSystemGroup), typeof(GiBootstrapSystem), typeof(GiReconcileSystem), typeof(GiSyncSystem), typeof(GiSplatSystem), typeof(GiSenseSystem),
            typeof(GiProcessSystem), typeof(GiSaveRestoreSystem), typeof(GiRestoreStepSystem), typeof(GiRecorderSystem),
            typeof(GiGhostServerSystem), typeof(GiGhostClientSystem), typeof(GiProfileHandshakeServerSystem), typeof(GiProfileHandshakeClientSystem),
            typeof(GiLockstepSystem), typeof(GiLockstepServerSystem), typeof(GiLockstepClientSystem),
        };

        private static readonly Type[] GiSystems = Systems();

        private static Type[] Systems()
        {
            var types = new System.Collections.Generic.List<Type>(Core);
            foreach (var type in typeof(GiGhostEmitter).Assembly.GetTypes())
                if (type.Name.EndsWith("RpcCommandRequestSystem", StringComparison.Ordinal) && !types.Contains(type))
                    types.Add(type);
            return types.ToArray();
        }

        [SetUp]
        public void SetUp()
        {
            GiRuntime.CompleteAll();
            GiOrphans.Reclaim(GiRuntime.Stamps);
        }

        private static BlobAssetReference<GiProfileBlob> Profile(Unity.Entities.World world, GiProfileSpec spec)
        {
            var blob = GiProfileBlobBuilder.Create(spec, Allocator.Persistent);
            var entity = world.EntityManager.CreateEntity(typeof(GiProfileReference));
            world.EntityManager.SetComponentData(entity, new GiProfileReference { Value = blob });
            return blob;
        }

        private static GameObject Prefab()
        {
            var go = new GameObject("GiNetEmitter");
            go.AddComponent<GhostAuthoringComponent>();
            go.AddComponent<TestEmitterAuthoring>();
            go.AddComponent<GiGhostEmitterAuthoring>();
            return go;
        }

        private static Entity Frame(Unity.Entities.World world)
        {
            using var query = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GiFrame>());
            return query.GetSingletonEntity();
        }

        private static int Sources(Unity.Entities.World world)
        {
            world.EntityManager.CompleteAllTrackedJobs();
            GiRuntime.CompleteAll();
            var frame = world.EntityManager.GetComponentData<GiFrame>(Frame(world));
            Assert.That(frame.Alive, Is.True, world.Name);
            Gi.World.Memory(frame.World, out _, out _, out _, out _, out var live);
            return live;
        }

        private static ulong Hash(Unity.Entities.World world, int layerKey)
        {
            world.EntityManager.CompleteAllTrackedJobs();
            GiRuntime.CompleteAll();
            var frame = world.EntityManager.GetComponentData<GiFrame>(Frame(world));
            Assert.That(frame.Alive, Is.True, world.Name);
            Assert.That(frame.Tables.TryLayer(new GiLayerKey((byte)layerKey), out var layer), Is.True);
            return GiFieldHash.OfLayer(frame.World, layer);
        }

        [Test]
        public void TheServerAndClientFieldsAreEqualForStaticSources()
        {
            using var testWorld = new NetCodeTestWorld();
            try
            {
                TheServerAndClientFieldsAreEqualForStaticSourcesBody(testWorld);
            }
            catch (Exception exception)
            {
                Debug.Log("BODY " + exception);
                throw;
            }
        }

        [Test]
        public void AClientWhoseProfileHashDiffersKeepsGiOffAndRaisesAFault()
        {
            using var testWorld = new NetCodeTestWorld();
            try
            {
                AClientWhoseProfileHashDiffersKeepsGiOffAndRaisesAFaultBody(testWorld);
            }
            catch (Exception exception)
            {
                Debug.Log("BODY " + exception);
                throw;
            }
        }

        [Test]
        public void ChangesAndDisablingOnTheServerReachTheClientField()
        {
            using var testWorld = new NetCodeTestWorld();
            try
            {
                ChangesAndDisablingOnTheServerReachTheClientFieldBody(testWorld);
            }
            catch (Exception exception)
            {
                Debug.Log("BODY " + exception);
                throw;
            }
        }

        private static void TheServerAndClientFieldsAreEqualForStaticSourcesBody(NetCodeTestWorld testWorld)
        {
            testWorld.Bootstrap(true, GiSystems);
            var go = Prefab();
            Assert.That(testWorld.CreateGhostCollection(go), Is.True);
            testWorld.CreateWorlds(true, 1);
            var spec = GiScenarioProfile.Standard().ToSpec();
            var serverBlob = Profile(testWorld.ServerWorld, spec);
            var clientBlob = Profile(testWorld.ClientWorlds[0], spec);
            try
            {
                testWorld.Connect(1f / 60f, 8);
                testWorld.GoInGame();
                var rng = new GiRandom(4);
                var server = testWorld.ServerWorld.EntityManager;
                for (var i = 0; i < 40; i++)
                {
                    var entity = testWorld.SpawnOnServer(0);
                    server.SetComponentData(entity, new GiEmitter
                    {
                        Layer = new GiLayerKey((byte)(i % 2)),
                        Stamp = new GiStampKey((byte)rng.Next(6)),
                        Gain = (sbyte)(1 + rng.Next(9)),
                    });
                    server.SetComponentData(entity, LocalTransform.FromPosition(10f + rng.Next(44), 0f, 10f + rng.Next(44)));
                }

                for (var i = 0; i < 90; i++) testWorld.Tick();

                var client = testWorld.ClientWorlds[0];
                Assert.That(Hash(client, 0), Is.EqualTo(Hash(testWorld.ServerWorld, 0)));
                Assert.That(Hash(client, 1), Is.EqualTo(Hash(testWorld.ServerWorld, 1)));
                Assert.That(Sources(testWorld.ServerWorld), Is.EqualTo(40));
                Assert.That(Sources(client), Is.EqualTo(40));
                using var emitters = client.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GiEmitter>());
                Assert.That(emitters.CalculateEntityCount(), Is.EqualTo(40));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
                serverBlob.Dispose();
                clientBlob.Dispose();
            }
        }

        private static void AClientWhoseProfileHashDiffersKeepsGiOffAndRaisesAFaultBody(NetCodeTestWorld testWorld)
        {
            testWorld.Bootstrap(true, GiSystems);
            var go = Prefab();
            Assert.That(testWorld.CreateGhostCollection(go), Is.True);
            testWorld.CreateWorlds(true, 2);
            var spec = GiScenarioProfile.Standard().ToSpec();
            var other = GiScenarioProfile.Standard().ToSpec();
            other.Plane = new GiPlaneSpec { Axes = GiPlaneAxes.Xz, OriginX = 5f };
            Assert.That(other.Hash, Is.Not.EqualTo(spec.Hash));
            var blobs = new[]
            {
                Profile(testWorld.ServerWorld, spec), Profile(testWorld.ClientWorlds[0], spec), Profile(testWorld.ClientWorlds[1], other),
            };
            LogAssert.Expect(LogType.Error, new Regex("Gi profile rejected: suppressed"));
            try
            {
                testWorld.Connect(1f / 60f, 8);
                testWorld.GoInGame();
                var server = testWorld.ServerWorld.EntityManager;
                for (var i = 0; i < 6; i++)
                {
                    var entity = testWorld.SpawnOnServer(0);
                    server.SetComponentData(entity, new GiEmitter { Layer = new GiLayerKey(0), Stamp = new GiStampKey(0), Gain = 5 });
                    server.SetComponentData(entity, LocalTransform.FromPosition(12f + i * 6f, 0f, 20f));
                }

                for (var i = 0; i < 60; i++) testWorld.Tick();

                using (var sent = testWorld.ServerWorld.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GiProfileSent>()))
                    Assert.That(sent.CalculateEntityCount(), Is.EqualTo(2));
                using (var suppress = testWorld.ClientWorlds[1].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GiSuppress>()))
                    Assert.That(suppress.CalculateEntityCount(), Is.EqualTo(1));
                using (var suppress = testWorld.ClientWorlds[0].EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GiSuppress>()))
                    Assert.That(suppress.CalculateEntityCount(), Is.EqualTo(0));

                var good = testWorld.ClientWorlds[0].EntityManager.GetComponentData<GiFrame>(Frame(testWorld.ClientWorlds[0]));
                var bad = testWorld.ClientWorlds[1].EntityManager.GetComponentData<GiFrame>(Frame(testWorld.ClientWorlds[1]));
                var host = server.GetComponentData<GiFrame>(Frame(testWorld.ServerWorld));
                Assert.That(host.Alive, Is.True);
                Assert.That(host.Faults.Has(GiFaultKind.Profile), Is.False);
                Assert.That(good.Alive, Is.True);
                Assert.That(good.Faults.Has(GiFaultKind.Profile), Is.False);
                Assert.That(bad.Alive, Is.False);
                Assert.That(bad.Faults.Has(GiFaultKind.Profile), Is.True);
                Assert.That(Hash(testWorld.ClientWorlds[0], 0), Is.EqualTo(Hash(testWorld.ServerWorld, 0)));
                Assert.That(Sources(testWorld.ServerWorld), Is.EqualTo(6));
                Assert.That(Sources(testWorld.ClientWorlds[0]), Is.EqualTo(6));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
                foreach (var blob in blobs) blob.Dispose();
            }
        }

        private static void ChangesAndDisablingOnTheServerReachTheClientFieldBody(NetCodeTestWorld testWorld)
        {
            testWorld.Bootstrap(true, GiSystems);
            var go = Prefab();
            Assert.That(testWorld.CreateGhostCollection(go), Is.True);
            testWorld.CreateWorlds(true, 1);
            var spec = GiScenarioProfile.Standard().ToSpec();
            var blobs = new[] { Profile(testWorld.ServerWorld, spec), Profile(testWorld.ClientWorlds[0], spec) };
            try
            {
                testWorld.Connect(1f / 60f, 8);
                testWorld.GoInGame();
                var server = testWorld.ServerWorld.EntityManager;
                var entities = new Entity[12];
                for (var i = 0; i < entities.Length; i++)
                {
                    entities[i] = testWorld.SpawnOnServer(0);
                    server.SetComponentData(entities[i], new GiEmitter { Layer = new GiLayerKey(0), Stamp = new GiStampKey((byte)(i % 3)), Gain = 4 });
                    server.SetComponentData(entities[i], LocalTransform.FromPosition(10f + i * 4f, 0f, 24f));
                }

                for (var i = 0; i < 60; i++) testWorld.Tick();
                var client = testWorld.ClientWorlds[0];
                var before = Hash(client, 0);
                Assert.That(Sources(client), Is.EqualTo(12));
                Assert.That(before, Is.EqualTo(Hash(testWorld.ServerWorld, 0)));

                for (var i = 0; i < entities.Length; i++)
                {
                    var emitter = server.GetComponentData<GiEmitter>(entities[i]);
                    emitter.Gain = (sbyte)(i % 2 == 0 ? -7 : 9);
                    server.SetComponentData(entities[i], emitter);
                    if (i % 4 == 3) server.SetComponentEnabled<GiEmitter>(entities[i], false);
                }

                for (var i = 0; i < 60; i++) testWorld.Tick();
                var after = Hash(client, 0);
                Assert.That(after, Is.Not.EqualTo(before));
                Assert.That(after, Is.EqualTo(Hash(testWorld.ServerWorld, 0)));
                Assert.That(Sources(client), Is.EqualTo(9));
                Assert.That(Sources(testWorld.ServerWorld), Is.EqualTo(9));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
                foreach (var blob in blobs) blob.Dispose();
            }
        }

        private static void LockstepBody(NetCodeTestWorld testWorld, bool inject)
        {
            const int interval = 10;
            var original = GiNetConfig.Check.Data;
            var spec = GiScenarioProfile.Standard().ToSpec();
            var blobs = new[] { Profile(testWorld.ServerWorld, spec), Profile(testWorld.ClientWorlds[0], spec) };
            try
            {
                testWorld.Connect(1f / 60f, 8);
                testWorld.GoInGame();
                var server = testWorld.ServerWorld.EntityManager;
                for (var i = 0; i < 20; i++)
                {
                    var entity = testWorld.SpawnOnServer(0);
                    server.SetComponentData(entity, new GiEmitter { Layer = new GiLayerKey((byte)(i % 2)), Stamp = new GiStampKey((byte)(i % 4)), Gain = 5 });
                    server.SetComponentData(entity, LocalTransform.FromPosition(10f + i * 2f, 0f, 14f + i * 2f));
                }

                var client = testWorld.ClientWorlds[0];
                for (var i = 0; i < 80; i++) testWorld.Tick();
                GiNetConfig.Check.Data = interval;
                for (var i = 0; i < 80; i++) testWorld.Tick();
                var frameEntity = Frame(client);
                var quiet = client.EntityManager.GetComponentData<GiFrame>(frameEntity);
                Assert.That(quiet.Alive && !quiet.Faults.Has(GiFaultKind.Desync), Is.True);
                var track = GiLockstepGate.Find(quiet.World);
                Assert.That(track, Is.Not.Null);
                Assert.That(track.Lockstep.Checks, Is.GreaterThan(5));
                Assert.That(track.Lockstep.Mismatches, Is.EqualTo(0));

                var injected = -1;
                if (inject)
                {
                    GiRuntime.CompleteAll();
                    client.EntityManager.CompleteAllTrackedJobs();
                    injected = quiet.Tick;
                    quiet.Tables.TryLayer(new GiLayerKey(0), out var layer);
                    quiet.Tables.TryStamp(new GiStampKey(0), out var stamp);
                    Assert.That(Gi.World.Place(quiet.World, layer, 30f, 30f, stamp, 9).Generation, Is.Not.EqualTo(0u));
                    LogAssert.Expect(LogType.Error, new Regex("Gi desync: layer"));
                }

                for (var i = 0; i < 60; i++) testWorld.Tick();

                var frame = client.EntityManager.GetComponentData<GiFrame>(frameEntity);
                if (!inject)
                {
                    Assert.That(frame.Faults.Has(GiFaultKind.Desync), Is.False);
                    Assert.That(track.Lockstep.Checks, Is.GreaterThan(10));
                    Assert.That(track.Lockstep.Mismatches, Is.EqualTo(0));
                    return;
                }

                Assert.That(frame.Faults.Has(GiFaultKind.Desync), Is.True);
                var first = frame.Faults.FirstTick(GiFaultKind.Desync);
                Assert.That(first, Is.GreaterThan(injected));
                Assert.That(first, Is.LessThanOrEqualTo(injected + interval));
                Assert.That(first % interval, Is.EqualTo(0));
                Assert.That(track.Lockstep.Mismatches, Is.GreaterThan(0));
                Assert.That(track.Lockstep.FirstMismatchTick, Is.EqualTo(first));
                var host = server.GetComponentData<GiFrame>(Frame(testWorld.ServerWorld));
                Assert.That(host.Faults.Has(GiFaultKind.Desync), Is.False);
            }
            finally
            {
                GiNetConfig.Check.Data = original;
                foreach (var blob in blobs) blob.Dispose();
            }
        }

        private static void RunLockstep(bool inject)
        {
            using var testWorld = new NetCodeTestWorld();
            testWorld.Bootstrap(true, GiSystems);
            var go = Prefab();
            try
            {
                Assert.That(testWorld.CreateGhostCollection(go), Is.True);
                testWorld.CreateWorlds(true, 1);
                LockstepBody(testWorld, inject);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void ADesyncInjectedOnPurposeIsReportedWithinTheCheckInterval()
        {
            RunLockstep(true);
        }

        [Test]
        public void AMatchingRunNeverRaisesADesync()
        {
            RunLockstep(false);
        }
    }
}