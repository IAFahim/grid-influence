using System.Collections.Generic;
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
    public sealed class GiSensorBakingTests
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

        private GiSensorAuthoring Sensor(string name, GiProfile profile, int layer)
        {
            var authoring = new GameObject(name).AddComponent<GiSensorAuthoring>();
            _made.Add(authoring.gameObject);
            authoring.Profile = profile;
            authoring.Layer = profile.Layers[layer];
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
        public void ASensorBakesEveryEnabledBindingKindWithItsReachThresholdAndExcludeSelf()
        {
            var profile = Profile();
            var authoring = Sensor("Sensor", profile, 0);
            authoring.Value = true;
            authoring.ValueExcludeSelf = true;
            authoring.Gradient = true;
            authoring.GradientExcludeSelf = true;
            authoring.Area = true;
            authoring.AreaReach = 12.5f;
            authoring.AreaExcludeSelf = true;
            authoring.Max = true;
            authoring.MaxReach = 7.25f;
            authoring.Nearest = true;
            authoring.NearestReach = 21.5f;
            authoring.NearestThreshold = 40;

            var world = GiBaking.Bake(authoring.gameObject);
            try
            {
                var entities = All<GiSensorValue>(world);
                Assert.That(entities.Count, Is.EqualTo(1));
                var manager = world.EntityManager;
                var entity = entities[0];
                Assert.That(manager.GetComponentData<GiSensorValue>(entity).ExcludeSelf, Is.True);
                Assert.That(manager.GetComponentData<GiSensorGradient>(entity).ExcludeSelf, Is.True);
                Assert.That(manager.GetComponentData<GiSensorArea>(entity).Reach, Is.EqualTo(12.5f));
                Assert.That(manager.GetComponentData<GiSensorArea>(entity).ExcludeSelf, Is.True);
                var max = manager.GetComponentData<GiSensorMax>(entity);
                Assert.That(max.Reach, Is.EqualTo(7.25f));
                var nearest = manager.GetComponentData<GiSensorNearest>(entity);
                Assert.That(nearest.Reach, Is.EqualTo(21.5f));
                Assert.That(nearest.Threshold, Is.EqualTo((short)40));
                Assert.That(manager.HasComponent<GiSensorFound>(entity), Is.True);
                Assert.That(manager.IsComponentEnabled<GiSensorFound>(entity), Is.False);
                Assert.That(manager.HasComponent<LocalTransform>(entity), Is.True);
            }
            finally
            {
                GiBaking.Dispose(world);
            }
        }

        [Test]
        public void ASensorWithNoKindsEnabledBakesNoSensorComponents()
        {
            var profile = Profile();
            var authoring = Sensor("Empty", profile, 0);

            var world = GiBaking.Bake(authoring.gameObject);
            try
            {
                Assert.That(All<GiSensorValue>(world), Is.Empty);
                Assert.That(All<GiSensorGradient>(world), Is.Empty);
                Assert.That(All<GiSensorArea>(world), Is.Empty);
                Assert.That(All<GiSensorMax>(world), Is.Empty);
                Assert.That(All<GiSensorNearest>(world), Is.Empty);
                Assert.That(All<GiSensorFound>(world), Is.Empty);
            }
            finally
            {
                GiBaking.Dispose(world);
            }
        }

        [Test]
        public void AStaticSensorBakesWithoutALocalTransform()
        {
            var profile = Profile();
            var authoring = Sensor("Static", profile, 0);
            authoring.Moves = false;
            authoring.Value = true;

            var world = GiBaking.Bake(authoring.gameObject);
            try
            {
                var entities = All<GiSensorValue>(world);
                Assert.That(entities.Count, Is.EqualTo(1));
                Assert.That(world.EntityManager.HasComponent<LocalTransform>(entities[0]), Is.False);
                Assert.That(world.EntityManager.HasComponent<LocalToWorld>(entities[0]), Is.True);
            }
            finally
            {
                GiBaking.Dispose(world);
            }
        }

        [Test]
        public void SensorBakerErrorsNameTheAuthoringObject()
        {
            var profile = Profile();
            var noProfile = Sensor("No Profile", profile, 0);
            noProfile.Profile = null;
            noProfile.Value = true;
            var noLayer = Sensor("No Layer", profile, 0);
            noLayer.Layer = null;
            var foreign = Sensor("Foreign Layer", profile, 0);
            var other = GiAuthoredProfile.FromScenario(GiScenarioProfile.Standard(), _made);
            foreign.Layer = other.Layers[0];
            var noReach = Sensor("No Reach", profile, 0);
            noReach.Area = true;
            noReach.AreaReach = 0f;
            var onTransient = Sensor("On Transient", profile, 2);
            onTransient.Gradient = true;
            onTransient.GradientExcludeSelf = true;

            LogAssert.Expect(LogType.Error, new Regex("Gi sensor 'No Profile': No profile is assigned"));
            LogAssert.Expect(LogType.Error, new Regex("Gi sensor 'No Layer': No layer is assigned"));
            LogAssert.Expect(LogType.Error, new Regex("Gi sensor 'Foreign Layer': The layer is not in the profile's stack"));
            LogAssert.Expect(LogType.Error, new Regex("Gi sensor 'No Reach': The area sensor needs a positive reach"));
            LogAssert.Expect(LogType.Error,
                new Regex($"Gi sensor 'On Transient': {Regex.Escape(GiBindingTable.Describe(GiBindingStatus.ExcludeOnTransient))}"));

            var world = GiBaking.Bake(noProfile.gameObject, noLayer.gameObject, foreign.gameObject, noReach.gameObject,
                onTransient.gameObject);
            try
            {
                Assert.That(All<GiSensorValue>(world), Is.Empty);
                Assert.That(All<GiSensorGradient>(world), Is.Empty);
                Assert.That(All<GiSensorArea>(world), Is.Empty);
                Assert.That(All<GiSensorNearest>(world), Is.Empty);
                Assert.That(All<GiSensorFound>(world), Is.Empty);
            }
            finally
            {
                GiBaking.Dispose(world);
            }
        }
    }
}
