using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Gi.Entities.Authoring;
using Gi.Entities.Tests.Authored;
using Gi.Entities.Tests.Support;
using NUnit.Framework;
using Unity.Entities;
using Unity.Transforms;
using UnityEditor;
using UnityEngine;
using EcsWorld = Unity.Entities.World;
using UnityEngine.TestTools;

namespace Gi.Entities.Editor.Tests
{
    public sealed class GiBakingTests
    {
        private readonly List<Object> _made = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var made in _made)
                if (made != null)
                    Object.DestroyImmediate(made);
            _made.Clear();
        }

        private GiProfile Profile()
        {
            return GiAuthoredProfile.FromScenario(GiScenarioProfile.Standard(), _made);
        }

        private GameObject Make(string name)
        {
            var go = new GameObject(name);
            _made.Add(go);
            return go;
        }

        private GiEmitterAuthoring Emitter(string name, GiProfile profile, int layer, int stamp, int gain)
        {
            var authoring = Make(name).AddComponent<GiEmitterAuthoring>();
            authoring.Profile = profile;
            authoring.Layer = profile.Layers[layer];
            authoring.Stamp = profile.Stamps[stamp];
            authoring.Gain = gain;
            return authoring;
        }

        private static List<Entity> All<T>(EcsWorld world)
        {
            using var query = world.EntityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<T>() },
                Options = EntityQueryOptions.IncludePrefab | EntityQueryOptions.IncludeDisabledEntities,
            });
            return new List<Entity>(query.ToEntityArray(Unity.Collections.Allocator.Temp));
        }

        [Test]
        public void BakingTwiceGivesIdenticalBlobBytesAndAnIdenticalHash()
        {
            var profile = Profile();
            var authoring = Make("Profile").AddComponent<GiProfileAuthoring>();
            authoring.Profile = profile;

            var first = GiBaking.Bake(authoring.gameObject);
            var second = GiBaking.Bake(authoring.gameObject);
            try
            {
                var a = first.EntityManager.GetComponentData<GiProfileReference>(All<GiProfileReference>(first)[0]).Value;
                var b = second.EntityManager.GetComponentData<GiProfileReference>(All<GiProfileReference>(second)[0]).Value;
                Assert.That(a.Value.Hash, Is.EqualTo(b.Value.Hash));
                Assert.That(a.Value.Hash, Is.EqualTo(profile.ToSpec().Hash));
                Assert.That(Bytes(ref a.Value), Is.EqualTo(Bytes(ref b.Value)));
            }
            finally
            {
                GiBaking.Dispose(first);
                GiBaking.Dispose(second);
            }
        }

        [Test]
        public void ABrokenProfileLogsEveryIssueNamingTheAuthoringObjectAndBakesNothing()
        {
            var profile = Profile();
            profile.Grids[0] = new GiGridEntry { Power = 3, Origin = Vector2.zero, Size = 64f };
            var authoring = Make("Broken Field").AddComponent<GiProfileAuthoring>();
            authoring.Profile = profile;
            LogAssert.Expect(LogType.Error, new Regex(@"Gi profile 'Broken Field': Grid 0: Grid power must be"));

            var world = GiBaking.Bake(authoring.gameObject);
            try
            {
                Assert.That(All<GiProfileReference>(world), Is.Empty);
            }
            finally
            {
                GiBaking.Dispose(world);
            }
        }

        [Test]
        public void AnEmitterBakesThroughTheBuilderAndAMovingOneKeepsItsTransform()
        {
            var profile = Profile();
            var authoring = Emitter("Mover", profile, 0, 2, 7);
            authoring.Oriented = true;
            authoring.Scaled = true;
            authoring.RampTicks = 3;

            var world = GiBaking.Bake(authoring.gameObject);
            try
            {
                var entities = All<GiEmitter>(world);
                Assert.That(entities.Count, Is.EqualTo(1));
                var manager = world.EntityManager;
                var emitter = manager.GetComponentData<GiEmitter>(entities[0]);
                Assert.That(emitter.Layer, Is.EqualTo(new GiLayerKey(0)));
                Assert.That(emitter.Stamp, Is.EqualTo(new GiStampKey(2)));
                Assert.That(emitter.Gain, Is.EqualTo((sbyte)7));
                Assert.That(manager.GetComponentData<GiGainRamp>(entities[0]).Ticks, Is.EqualTo((ushort)3));
                Assert.That(manager.HasComponent<GiOriented>(entities[0]), Is.True);
                Assert.That(manager.HasComponent<GiScaled>(entities[0]), Is.True);
                Assert.That(manager.HasComponent<GiFault>(entities[0]), Is.True);
                Assert.That(manager.IsComponentEnabled<GiFault>(entities[0]), Is.False);
                Assert.That(manager.HasComponent<LocalTransform>(entities[0]), Is.True);
            }
            finally
            {
                GiBaking.Dispose(world);
            }
        }

        [Test]
        public void AStaticEmitterBakesWithoutALocalTransform()
        {
            var profile = Profile();
            var authoring = Emitter("Static", profile, 0, 0, 4);
            authoring.Moves = false;

            var world = GiBaking.Bake(authoring.gameObject);
            try
            {
                var entities = All<GiEmitter>(world);
                Assert.That(entities.Count, Is.EqualTo(1));
                Assert.That(world.EntityManager.HasComponent<LocalTransform>(entities[0]), Is.False);
                Assert.That(world.EntityManager.HasComponent<LocalToWorld>(entities[0]), Is.True);
                Assert.That(world.EntityManager.HasComponent<GiGainRamp>(entities[0]), Is.False);
            }
            finally
            {
                GiBaking.Dispose(world);
            }
        }

        [Test]
        public void ASplatBakesWithItsTransientLayer()
        {
            var profile = Profile();
            var authoring = Make("Splat").AddComponent<GiSplatAuthoring>();
            authoring.Profile = profile;
            authoring.Layer = profile.Layers[2];
            authoring.Stamp = profile.Stamps[1];
            authoring.Gain = -3;

            var world = GiBaking.Bake(authoring.gameObject);
            try
            {
                var entities = All<GiSplat>(world);
                Assert.That(entities.Count, Is.EqualTo(1));
                var splat = world.EntityManager.GetComponentData<GiSplat>(entities[0]);
                Assert.That(splat.Layer, Is.EqualTo(new GiLayerKey(2)));
                Assert.That(splat.Stamp, Is.EqualTo(new GiStampKey(1)));
                Assert.That(splat.Gain, Is.EqualTo((sbyte)-3));
            }
            finally
            {
                GiBaking.Dispose(world);
            }
        }

        [Test]
        public void EveryBakerErrorNamesItsAuthoringObject()
        {
            var profile = Profile();
            var onTransient = Emitter("On Transient", profile, 2, 0, 4);
            var strong = Emitter("Too Strong", profile, 0, 0, 17);
            var noStamp = Emitter("No Stamp", profile, 0, 0, 4);
            noStamp.Stamp = null;
            var noProfile = Emitter("No Profile", profile, 0, 0, 4);
            noProfile.Profile = null;
            var splat = Make("Wrong Splat").AddComponent<GiSplatAuthoring>();
            splat.Profile = profile;
            splat.Layer = profile.Layers[0];
            splat.Stamp = profile.Stamps[0];

            LogAssert.Expect(LogType.Error, new Regex($"Gi emitter 'On Transient': {Regex.Escape(GiSourceCheck.EmitterResidency)}"));
            LogAssert.Expect(LogType.Error, new Regex("Gi emitter 'Too Strong': Influence stamp gain must be between -16 and 16"));
            LogAssert.Expect(LogType.Error, new Regex("Gi emitter 'No Stamp': No stamp is assigned"));
            LogAssert.Expect(LogType.Error, new Regex("Gi emitter 'No Profile': No profile is assigned"));
            LogAssert.Expect(LogType.Error, new Regex($"Gi splat 'Wrong Splat': {Regex.Escape(GiSourceCheck.SplatResidency)}"));

            var world = GiBaking.Bake(onTransient.gameObject, strong.gameObject, noStamp.gameObject, noProfile.gameObject, splat.gameObject);
            try
            {
                Assert.That(All<GiEmitter>(world), Is.Empty);
                Assert.That(All<GiSplat>(world), Is.Empty);
            }
            finally
            {
                GiBaking.Dispose(world);
            }
        }

        private static byte[] Bytes(ref GiProfileBlob blob)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write(blob.Hash);
            writer.Write(blob.Plane.OriginX);
            writer.Write(blob.Plane.OriginY);
            writer.Write((byte)blob.Plane.Axes);
            for (var i = 0; i < blob.Grids.Length; i++)
            {
                writer.Write(blob.Grids[i].Power);
                writer.Write(blob.Grids[i].X);
                writer.Write(blob.Grids[i].Y);
                writer.Write(blob.Grids[i].Size);
            }

            for (var i = 0; i < blob.Layers.Length; i++)
            {
                var layer = blob.Layers[i];
                writer.Write((byte)layer.Kind);
                writer.Write(layer.PhaseBits);
                writer.Write(layer.TurnBits);
                writer.Write(layer.First);
                writer.Write(layer.Second);
                writer.Write(layer.FirstWeight);
                writer.Write(layer.SecondWeight);
                writer.Write(layer.Shift);
                writer.Write(layer.MaskLow);
                writer.Write(layer.MaskHigh);
            }

            for (var i = 0; i < blob.Stamps.Length; i++)
            {
                var stamp = blob.Stamps[i];
                writer.Write((byte)stamp.Shape);
                writer.Write(stamp.Width);
                writer.Write(stamp.Height);
                writer.Write(stamp.Radius);
                writer.Write(stamp.Arc);
                writer.Write(stamp.Value);
                writer.Write(stamp.CellOffset);
                writer.Write(stamp.CellCount);
            }

            for (var i = 0; i < blob.Cells.Length; i++) writer.Write(blob.Cells[i]);
            writer.Flush();
            return stream.ToArray();
        }
    }
}
