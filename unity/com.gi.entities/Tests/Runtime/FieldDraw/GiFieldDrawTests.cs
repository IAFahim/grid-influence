using System.Collections;
using Gi.Entities.Presentation;
using Gi.Entities.Tests.Support;
using NUnit.Framework;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.TestTools;
using GiGrid = Gi.Grid;
using GiWorld = Gi.World;

namespace Gi.Entities.Tests
{
    public sealed class GiFieldDrawTests
    {
        [SetUp]
        public void SetUp()
        {
            GiRuntime.CompleteAll();
            GiOrphans.Reclaim(GiRuntime.Stamps);
        }

        private static GiFieldDrawStats Stats(GiEcsFixture fixture)
        {
            return fixture.Manager.GetComponentData<GiFieldDrawStats>(fixture.FrameEntity);
        }

        private static GiEcsFixture Fixture(string name, out SystemHandle draw)
        {
            var fixture = new GiEcsFixture(name);
            fixture.SetProfileWith(GiSenseTests.Sampled, GiSenseTests.Gated, GiSenseKindsTests.Max, GiSenseKindsTests.Nearest);
            draw = fixture.AddFieldDraw();
            for (var i = 0; i < 24; i++)
                fixture.CreateEmitter((byte)(i % 2), (byte)(i % 6), (sbyte)(3 + i % 5), 8f + (i * 7) % 48, 8f + (i * 11) % 48);

            var archetype = fixture.Manager.CreateArchetype(typeof(Unity.Transforms.LocalTransform), typeof(ProbeSample), typeof(KindMax), typeof(KindNearest));
            for (var i = 0; i < 40; i++)
            {
                var probe = fixture.Manager.CreateEntity(archetype);
                fixture.Manager.SetComponentData(probe, Unity.Transforms.LocalTransform.FromPosition(6f + i % 50, 0f, 6f + i / 50));
            }

            return fixture;
        }

        [Test]
        public void OneHundredSteadyFramesWithTheDrawActiveAllocateNothing()
        {
            using var fixture = Fixture("FieldDrawAlloc", out _);
            fixture.Frame1();
            fixture.Frame1();

            var emitters = fixture.Manager.CreateEntityQuery(typeof(GiEmitter), typeof(Unity.Transforms.LocalTransform)).ToEntityArray(Unity.Collections.Allocator.Persistent);
            var phase = 0f;
            void Step()
            {
                for (var t = 0; t < 20; t++)
                {
                    phase += 0.25f;
                    for (var i = 0; i < emitters.Length; i += 9)
                        fixture.Manager.SetComponentData(emitters[i],
                            Unity.Transforms.LocalTransform.FromPosition(8f + i % 48 + phase % 3f, 0f, 8f + i / 48));
                    fixture.Frame1();
                }
            }

            try
            {
                Assume.That(GiAllocation.Available, Is.True);
                Assert.That(GiAllocation.SteadyZero(Step, 16, 5), Is.True);
            }
            finally
            {
                emitters.Dispose();
            }

            fixture.Complete();
            var stats = Stats(fixture);
            Assert.That(stats.MirrorTiles, Is.GreaterThan(0));
            Assert.That(stats.Drawn, Is.EqualTo(1));
            Assert.That(fixture.Frame.Faults.Raised, Is.EqualTo(0));
        }

        [UnityTest]
        public IEnumerator TheMarkerExistsAndUpdatedTilesFollowChangedTiles()
        {
            using var fixture = Fixture("FieldDrawMarker", out _);
            fixture.Frame1();
            fixture.Frame1();

            var wasEnabled = Profiler.enabled;
            Profiler.enabled = true;
            using var draw = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, GiFieldDrawMarkers.DrawName);
            using var mirror = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, GiFieldDrawMarkers.MirrorName);
            using var heat = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, GiFieldDrawMarkers.HeatName);
            try
            {
                var since = fixture.Frame.Tick;
                fixture.Frame1();
                fixture.Complete();
                yield return null;
                fixture.Frame1();
                fixture.Complete();
                yield return null;

                Assert.That(draw.Valid, Is.True);
                Assert.That(draw.LastValue, Is.GreaterThan(0));
                Assert.That(mirror.Valid, Is.True);
                Assert.That(mirror.LastValue, Is.GreaterThan(0));
                Assert.That(heat.Valid, Is.True);
                Assert.That(heat.LastValue, Is.GreaterThan(0));

                var stats = Stats(fixture);
                var frame = fixture.Frame;
                Assert.That(frame.Tables.TryLayer(new GiLayerKey(0), out var layer), Is.True);
                var expected = 0;
                for (var grid = 0; grid < frame.Tables.GridCount; grid++)
                {
                    if (!frame.Tables.TryGrid(grid, out var gridId)) continue;
                    expected += ChangedCount(fixture, gridId, layer, since);
                }

                Assert.That(stats.UpdatedTiles, Is.EqualTo(expected));
                Assert.That(stats.MirrorTiles, Is.GreaterThan(0));
                Assert.That(stats.VisibleTiles, Is.GreaterThan(0));
            }
            finally
            {
                Profiler.enabled = wasEnabled;
            }
        }

        [Test]
        public void TilesSourcesAndBindingsOutsideBothCamerasDrawNothing()
        {
            using var fixture = Fixture("FieldDrawCull", out _);
            fixture.Frame1();
            fixture.Frame1();
            fixture.Complete();

            var open = Stats(fixture);
            Assert.That(open.VisibleTiles, Is.GreaterThan(0));
            Assert.That(open.DrawnSources, Is.GreaterThan(0));
            Assert.That(open.DrawnBindings, Is.GreaterThan(0));

            var cameraObject = new GameObject("FieldDrawCamera");
            try
            {
                cameraObject.tag = "MainCamera";
                var camera = cameraObject.AddComponent<Camera>();
                cameraObject.transform.position = new Vector3(9000f, 900f, 9000f);
                cameraObject.transform.rotation = Quaternion.LookRotation(Vector3.forward);
                camera.nearClipPlane = 0.3f;
                camera.farClipPlane = 100f;

                fixture.Frame1();
                fixture.Frame1();
                fixture.Complete();

                var hidden = Stats(fixture);
                Assert.That(hidden.VisibleTiles, Is.EqualTo(0));
                Assert.That(hidden.DrawnSources, Is.EqualTo(0));
                Assert.That(hidden.DrawnBindings, Is.EqualTo(0));

                cameraObject.transform.position = new Vector3(32f, 40f, 32f);
                cameraObject.transform.rotation = Quaternion.LookRotation(new Vector3(-1f, -1f, -1f));
                camera.farClipPlane = 300f;

                fixture.Frame1();
                fixture.Frame1();
                fixture.Complete();

                var seen = Stats(fixture);
                Assert.That(seen.VisibleTiles, Is.GreaterThan(0));
                Assert.That(seen.DrawnSources, Is.GreaterThan(0));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(cameraObject);
            }
        }

        private static unsafe int ChangedCount(GiEcsFixture fixture, byte gridId, byte layer, int since)
        {
            var frame = fixture.Frame;
            return GiWorld.ChangedTiles(frame.World, gridId, layer, since, null);
        }

        [Test]
        public unsafe void TheDrawnHeatEqualsTheFieldWindowHeatmapOnTheSameLayerAndTick()
        {
            using var fixture = Fixture("FieldDrawHeat", out var draw);
            fixture.Frame1();
            fixture.Frame1();
            fixture.Complete();

            var frame = fixture.Frame;
            Assert.That(frame.Tables.TryLayer(new GiLayerKey(0), out var layer), Is.True);
            ref var system = ref fixture.World.Unmanaged.GetUnsafeSystemRef<GiFieldDrawSystem>(draw);
            var liveTotal = 0;

            for (var grid = 0; grid < frame.Tables.GridCount; grid++)
            {
                Assert.That(frame.Tables.TryGrid(grid, out var gridId), Is.True);
                Assert.That(GiGrid.TryGetSpec(frame.World, gridId, out var power, out _, out _, out _), Is.True);

                using var mirror = new GiMirror(frame.World, gridId, layer, power);
                mirror.Update(1 << 20);
                fixture.Frame1();
                fixture.Complete();
                mirror.Update(1 << 20);

                var tilesPerSide = mirror.TilesPerSide;
                var tileCells = stackalloc short[GiMirrorPlanner.TileCells * GiMirrorPlanner.TileCells];
                var expectedCells = stackalloc short[GiMirrorPlanner.TileCells * GiMirrorPlanner.TileCells];
                for (var tile = 0; tile < tilesPerSide * tilesPerSide; tile++)
                {
                    var has = system.TryReadTile(gridId, tile,
                        new System.Span<short>(tileCells, GiMirrorPlanner.TileCells * GiMirrorPlanner.TileCells));
                    var mirrorLive = mirror.IsLive(tile);
                    if (has) liveTotal++;
                    Assert.That(has, Is.EqualTo(mirrorLive), $"grid {grid} tile {tile % tilesPerSide},{tile / tilesPerSide} liveness");

                    if (!has) continue;

                    GiWorld.QueryRegion(frame.World, gridId, layer,
                        (tile % tilesPerSide) * GiMirrorPlanner.TileCells, (tile / tilesPerSide) * GiMirrorPlanner.TileCells,
                        GiMirrorPlanner.TileCells, GiMirrorPlanner.TileCells, expectedCells);
                    for (var cell = 0; cell < GiMirrorPlanner.TileCells * GiMirrorPlanner.TileCells; cell++)
                        Assert.That(tileCells[cell], Is.EqualTo(expectedCells[cell]),
                            $"grid {grid} tile {tile % tilesPerSide},{tile / tilesPerSide} cell {cell % GiMirrorPlanner.TileCells},{cell / GiMirrorPlanner.TileCells}");
                }
            }

            Assert.That(Stats(fixture).MirrorTiles, Is.EqualTo(liveTotal));
        }
    }
}
