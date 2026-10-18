using System.Linq;
using System.Reflection;
using Gi.Entities.Tests;
using NUnit.Framework;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Transforms;
using UnityEngine;

namespace Gi.Entities.Editor.Tests
{
    public struct OverlayReach : IComponentData
    {
        public float Reach;
    }

    public struct OverlayArea : IComponentData
    {
        public long Total;
    }

    public struct OverlayNearest : IComponentData
    {
        public short Value;
        public Unity.Mathematics.float2 Position;
    }

    public struct OverlayValue : IComponentData
    {
        public short Value;
    }

    public sealed class GiOverlayReachTests
    {
        [SetUp]
        public void SetUp()
        {
            GiRuntime.CompleteAll();
            GiOrphans.Reclaim(GiRuntime.Stamps);
        }

        private static GiBindingField Field<T>(string name)
            where T : unmanaged, IComponentData
        {
            var info = typeof(T).GetField(name, BindingFlags.Public | BindingFlags.Instance);
            return new GiBindingField
            {
                TypeHash = TypeManager.GetTypeInfo(TypeManager.GetTypeIndex<T>()).StableTypeHash,
                Offset = (ushort)UnsafeUtility.GetFieldOffset(info),
                Size = (ushort)UnsafeUtility.SizeOf(info.FieldType),
            };
        }

        private static GiEcsFixture Fixture(string name, out Entity both, out Entity plain, out Entity other)
        {
            var fixture = new GiEcsFixture(name);
            fixture.SetProfileWith(
                new GiBindingSpec { Kind = GiBindingKind.Area, Layer = 3, Reach = 12f, Total = Field<OverlayArea>("Total") },
                new GiBindingSpec { Kind = GiBindingKind.Nearest, Layer = 1, ReachField = Field<OverlayReach>("Reach"), Threshold = 2, Value = Field<OverlayNearest>("Value"), Position = Field<OverlayNearest>("Position") },
                new GiBindingSpec { Kind = GiBindingKind.Value, Layer = 1, Value = Field<OverlayValue>("Value") });
            both = fixture.Manager.CreateEntity(typeof(LocalTransform), typeof(OverlayArea), typeof(OverlayNearest), typeof(OverlayReach));
            fixture.Manager.SetComponentData(both, LocalTransform.FromPosition(20f, 0f, 30f));
            fixture.Manager.SetComponentData(both, new OverlayReach { Reach = 7.5f });
            plain = fixture.Manager.CreateEntity(typeof(LocalTransform), typeof(OverlayArea));
            fixture.Manager.SetComponentData(plain, LocalTransform.FromPosition(40f, 0f, 12f));
            other = fixture.Manager.CreateEntity(typeof(LocalTransform));
            fixture.Frame1();
            return fixture;
        }

        [Test]
        public void ReachDiscsFollowTheBindingsAnEntityHasAndReadTheirRadiusFromTheEntity()
        {
            using var fixture = Fixture("OverlayReach", out var both, out var plain, out var other);
            var draws = GiOverlayModel.Reaches(fixture.World, new[] { both, plain, other });

            Assert.That(draws.Count, Is.EqualTo(3));
            var area = draws.Where(d => d.Kind == GiBindingKind.Area).ToList();
            Assert.That(area.Select(d => d.Entity), Is.EquivalentTo(new[] { both, plain }));
            Assert.That(area.Select(d => d.Radius), Has.All.EqualTo(12f));
            Assert.That(area.Single(d => d.Entity == both).Center, Is.EqualTo(new Vector2(20f, 30f)));
            var nearest = draws.Single(d => d.Kind == GiBindingKind.Nearest);
            Assert.That(nearest.Entity, Is.EqualTo(both));
            Assert.That(nearest.Radius, Is.EqualTo(7.5f));
            Assert.That(nearest.Binding, Is.EqualTo(1));
            Assert.That(draws.Any(d => d.Entity == other), Is.False);

            fixture.Manager.SetComponentData(both, new OverlayReach { Reach = 0f });
            Assert.That(GiOverlayModel.Reaches(fixture.World, new[] { both }).Select(d => d.Kind), Is.EqualTo(new[] { GiBindingKind.Area }));
            fixture.Manager.SetComponentData(both, new OverlayReach { Reach = float.PositiveInfinity });
            Assert.That(GiOverlayModel.Reaches(fixture.World, new[] { both }).Select(d => d.Kind), Is.EqualTo(new[] { GiBindingKind.Area }));
            Assert.That(GiOverlayModel.Reaches(fixture.World, new[] { Entity.Null }), Is.Empty);
        }

        [Test]
        public void ASelectedGameObjectIsMappedToItsEntityThroughTheEntityGuid()
        {
            using var fixture = Fixture("OverlaySelection", out var both, out _, out var other);
            var first = new GameObject("GiOverlayFirst");
            var second = new GameObject("GiOverlaySecond");
            var unmapped = new GameObject("GiOverlayUnmapped");
            try
            {
                fixture.Manager.AddComponentData(both, new EntityGuid(first.GetEntityId(), default, 0u, 0u));
                fixture.Manager.AddComponentData(other, new EntityGuid(second.GetEntityId(), default, 0u, 0u));
                var extra = fixture.Manager.CreateEntity(typeof(EntityGuid));
                fixture.Manager.SetComponentData(extra, new EntityGuid(first.GetEntityId(), default, 0u, 1u));

                Assert.That(GiOverlaySelection.Entities(fixture.World, new[] { first }), Is.EqualTo(new[] { both }));
                Assert.That(GiOverlaySelection.Entities(fixture.World, new[] { first, second, unmapped }), Is.EquivalentTo(new[] { both, other }));
                Assert.That(GiOverlaySelection.Entities(fixture.World, new[] { unmapped }), Is.Empty);
                Assert.That(GiOverlaySelection.Entities(fixture.World, new GameObject[0]), Is.Empty);
                Assert.That(GiOverlaySelection.Entities(null, new[] { first }), Is.Empty);
            }
            finally
            {
                Object.DestroyImmediate(first);
                Object.DestroyImmediate(second);
                Object.DestroyImmediate(unmapped);
            }
        }

        [Test]
        public void TheEntityHierarchySelectionJoinsTheGameObjectSelection()
        {
            using var fixture = Fixture("OverlayProxy", out var both, out var plain, out _);
            Assert.That(GiEntitySelection.Select(fixture.World, plain), Is.True);
            try
            {
                Assert.That(GiOverlaySelection.Entities(fixture.World), Is.EquivalentTo(new[] { plain }));
                Assert.That(GiOverlaySelection.Entities(fixture.World).Contains(both), Is.False);
            }
            finally
            {
                UnityEditor.Selection.activeObject = null;
            }
        }
    }
}
