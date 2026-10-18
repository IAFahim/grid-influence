using Gi.Entities.Tests.Support;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using GiWorld = Gi.World;

namespace Gi.Entities.Tests
{
    public sealed class GiSyncTests
    {
        private static readonly GiScenarioOptions Options = GiScenarioOptions.Standard.WithoutSplats();

        [SetUp]
        public void SetUp()
        {
            GiRuntime.CompleteAll();
            GiOrphans.Reclaim(GiRuntime.Stamps);
        }

        [Test]
        public void EcsWithMovesTurnsAndScalesMatchesTheReferenceOverFiftySeeds()
        {
            for (ulong seed = 1; seed <= 50; seed++)
            {
                var scenario = GiScenarioGenerator.Create(seed, Options);
                var expected = GiReferenceExecutor.Run(scenario);
                using var executor = new GiEcsExecutor(scenario);
                Assert.That(expected.Compare(executor.Run()), Is.EqualTo(string.Empty), $"seed {seed}");
            }
        }

        [Test]
        public void EcsWithTransformsMatchesWhenTheOperationOrderIsShuffled()
        {
            for (ulong seed = 100; seed <= 115; seed++)
            {
                var scenario = GiScenarioGenerator.Create(seed, Options);
                var expected = GiReferenceExecutor.Run(scenario);
                using var executor = new GiEcsExecutor(scenario.WithOpOrderShuffled(seed));
                Assert.That(expected.Compare(executor.Run()), Is.EqualTo(string.Empty), $"seed {seed}");
            }
        }

        [Test]
        public void OnlyTheChunksThatChangedAreSynced()
        {
            using var fixture = new GiEcsFixture();
            fixture.SetStandardProfile();
            var entities = new Entity[400];
            for (var i = 0; i < entities.Length; i++) entities[i] = fixture.CreateEmitter(0, 0, 4, 10f + i % 50, 10f + i / 50);
            fixture.Tick(3);
            fixture.Complete();

            Assert.That(SyncedRows(fixture), Is.EqualTo(0), "nothing moved");

            var moved = entities[0];
            var transform = fixture.Manager.GetComponentData<LocalTransform>(moved);
            transform.Position = new float3(15f, 0f, 15f);
            fixture.Manager.SetComponentData(moved, transform);
            fixture.Tick();
            fixture.Complete();

            var chunk = fixture.Manager.GetChunk(moved);
            Assert.That(SyncedRows(fixture), Is.EqualTo(chunk.Count));
            Assert.That(chunk.Count, Is.LessThan(entities.Length));
            var id = fixture.Manager.GetComponentData<GiSource>(moved).Id;
            Assert.That(GiWorld.TryGetSourceSpec(fixture.Frame.World, id, out var spec), Is.True);
            Assert.That(spec.X, Is.EqualTo(15f));
            Assert.That(spec.Y, Is.EqualTo(15f));

            fixture.Tick();
            fixture.Complete();
            Assert.That(SyncedRows(fixture), Is.EqualTo(0));
        }

        [Test]
        public void ChangeFiltersWorkPerChunk()
        {
            using var fixture = new GiEcsFixture();
            fixture.SetStandardProfile();
            var a = fixture.CreateEmitter(0, 0, 4, 1f, 1f);
            var b = fixture.CreateEmitter(0, 0, 4, 2f, 2f);
            fixture.Manager.AddComponent<GiOriented>(b);
            fixture.Tick();
            fixture.Complete();

            var since = fixture.Manager.GlobalSystemVersion;
            fixture.Tick();
            fixture.Complete();
            fixture.Manager.SetComponentData(a, LocalTransform.FromPosition(5f, 0f, 5f));
            var handle = fixture.Manager.GetComponentTypeHandle<LocalTransform>(true);
            Assert.That(fixture.Manager.GetChunk(a).DidChange(ref handle, since), Is.True);
            Assert.That(fixture.Manager.GetChunk(b).DidChange(ref handle, since), Is.False);

            fixture.Tick();
            fixture.Complete();
            Assert.That(fixture.Manager.GetChunk(a).DidChange(ref handle, fixture.Manager.GlobalSystemVersion), Is.False);
        }

        public static System.Collections.Generic.IEnumerable<object[]> Facings()
        {
            yield return new object[] { GiPlaneAxes.Xz, 1, 0, -1, 0 };
            yield return new object[] { GiPlaneAxes.Xz, 0, 1, 0, -1 };
            yield return new object[] { GiPlaneAxes.Xy, 1, 0, -1, 0 };
            yield return new object[] { GiPlaneAxes.Xy, 0, 1, 0, -1 };
        }

        [TestCaseSource(nameof(Facings))]
        public void ACone_FollowsTheEntityAsItTurns(GiPlaneAxes axes, int dx, int dy, int ex, int ey)
        {
            using var fixture = new GiEcsFixture();
            var spec = GiScenarioProfile.Standard().ToSpec(new GiPlaneSpec { Axes = axes });
            fixture.SetProfile(spec);
            var entity = fixture.CreateEmitter(1, 2, 8, 32.5f, 32.5f);
            fixture.Manager.AddComponent<GiOriented>(entity);
            Point(fixture, entity, axes, dx, dy);
            fixture.Tick(2);

            AssertAhead(fixture, axes, dx, dy);
            Point(fixture, entity, axes, ex, ey);
            fixture.Tick();
            AssertAhead(fixture, axes, ex, ey);
            Point(fixture, entity, axes, dx, dy);
            fixture.Tick();
            AssertAhead(fixture, axes, dx, dy);
        }

        [Test]
        public void ANonFinitePositionIsRejectedPerRowAndRecovers()
        {
            using var fixture = new GiEcsFixture();
            fixture.SetStandardProfile();
            var healthy = fixture.CreateEmitter(0, 0, 4, 20f, 20f);
            var broken = fixture.CreateEmitter(0, 0, 4, 30f, 30f);
            fixture.Tick(2);
            fixture.Complete();

            fixture.Manager.SetComponentData(broken, LocalTransform.FromPosition(float.NaN, 0f, 5f));
            fixture.Manager.SetComponentData(healthy, LocalTransform.FromPosition(25f, 0f, 25f));
            fixture.Tick();
            fixture.Complete();
            Assert.That(fixture.Manager.IsComponentEnabled<GiFault>(broken), Is.True);
            Assert.That(fixture.Manager.GetComponentData<GiFault>(broken).Code, Is.EqualTo(GiFaultCode.NonFinite));
            Assert.That(fixture.Manager.IsComponentEnabled<GiFault>(healthy), Is.False);
            GiWorld.TryGetSourceSpec(fixture.Frame.World, fixture.Manager.GetComponentData<GiSource>(broken).Id, out var kept);
            Assert.That(kept.X, Is.EqualTo(30f));
            GiWorld.TryGetSourceSpec(fixture.Frame.World, fixture.Manager.GetComponentData<GiSource>(healthy).Id, out var moved);
            Assert.That(moved.X, Is.EqualTo(25f));

            fixture.Manager.SetComponentData(broken, LocalTransform.FromPosition(31f, 0f, 31f));
            fixture.Tick();
            fixture.Complete();
            Assert.That(fixture.Manager.IsComponentEnabled<GiFault>(broken), Is.False);
            GiWorld.TryGetSourceSpec(fixture.Frame.World, fixture.Manager.GetComponentData<GiSource>(broken).Id, out kept);
            Assert.That(kept.X, Is.EqualTo(31f));
        }

        [Test]
        public void AParentedEmitterFollowsTheMatrixOneFrameLate()
        {
            using var fixture = new GiEcsFixture();
            fixture.AddTransformSystems();
            fixture.SetStandardProfile();

            var parent = fixture.Manager.CreateEntity(typeof(LocalTransform), typeof(LocalToWorld));
            fixture.Manager.SetComponentData(parent, LocalTransform.FromPosition(10f, 0f, 10f));
            var child = fixture.CreateEmitter(0, 0, 4, 5f, 5f);
            fixture.Manager.AddComponent<LocalToWorld>(child);
            fixture.Manager.AddComponentData(child, new Parent { Value = parent });

            fixture.Frame1();
            fixture.Frame1();
            fixture.Frame1();
            fixture.Complete();
            var id = fixture.Manager.GetComponentData<GiSource>(child).Id;
            Assert.That(GiWorld.TryGetSourceSpec(fixture.Frame.World, id, out var spec), Is.True);
            Assert.That(spec.X, Is.EqualTo(15f));
            Assert.That(spec.Y, Is.EqualTo(15f));

            fixture.Manager.SetComponentData(parent, LocalTransform.FromPosition(30f, 0f, 30f));
            fixture.Frame1();
            fixture.Complete();
            GiWorld.TryGetSourceSpec(fixture.Frame.World, id, out spec);
            Assert.That(spec.X, Is.EqualTo(15f), "fixed step runs before the transform system");

            fixture.Frame1();
            fixture.Complete();
            GiWorld.TryGetSourceSpec(fixture.Frame.World, id, out spec);
            Assert.That(spec.X, Is.EqualTo(35f));
            Assert.That(spec.Y, Is.EqualTo(35f));
        }

        [Test]
        public void MovingEmittersAllocateNothingPerTick()
        {
            using var fixture = new GiEcsFixture();
            fixture.SetStandardProfile();
            var entities = new Entity[500];
            for (var i = 0; i < entities.Length; i++) entities[i] = fixture.CreateEmitter((byte)(i % 2), (byte)(i % 6), 4, 10f + i % 45, 10f + i / 45);
            fixture.Tick(100);

            var phase = 0f;
            void Step()
            {
                for (var t = 0; t < 40; t++)
                {
                    phase += 0.25f;
                    for (var i = 0; i < entities.Length; i += 7)
                        fixture.Manager.SetComponentData(entities[i], LocalTransform.FromPosition(10f + i % 45 + phase % 3f, 0f, 10f + i / 45));
                    fixture.Tick();
                }
            }

            Assert.That(GiAllocation.SteadyZero(Step, 14, 3), Is.True);
        }

        private static int SyncedRows(GiEcsFixture fixture)
        {
            fixture.Complete();
            return fixture.Manager.GetComponentData<GiStats>(fixture.FrameEntity).SyncRows;
        }

        private static void Point(GiEcsFixture fixture, Entity entity, GiPlaneAxes axes, int dx, int dy)
        {
            var transform = fixture.Manager.GetComponentData<LocalTransform>(entity);
            transform.Rotation = axes == GiPlaneAxes.Xz
                ? quaternion.RotateY(math.atan2(dx, dy))
                : quaternion.RotateZ(math.atan2(dy, dx));
            transform.Position = axes == GiPlaneAxes.Xz ? new float3(32.5f, 0f, 32.5f) : new float3(32.5f, 32.5f, 0f);
            fixture.Manager.SetComponentData(entity, transform);
        }

        private static void AssertAhead(GiEcsFixture fixture, GiPlaneAxes axes, int dx, int dy)
        {
            fixture.Complete();
            var frame = fixture.Frame;
            frame.Tables.TryGrid(1, out var grid);
            frame.Tables.TryLayer(new GiLayerKey(1), out var layer);
            var best = GiWorld.QueryMax(frame.World, grid, layer, out var bx, out var by);
            Assert.That(best, Is.GreaterThan(0));
            const int cell = 65;
            Assert.That((bx - cell) * dx + (by - cell) * dy, Is.GreaterThan(0), $"{axes} {dx},{dy}: peak ({bx},{by})");
        }
    }
}
