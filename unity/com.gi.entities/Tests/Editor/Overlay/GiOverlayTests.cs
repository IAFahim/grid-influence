using System.Collections.Generic;
using System.IO;
using Gi.Entities.Presentation;
using Gi.Entities.Tests;
using Gi.Entities.Tests.Support;
using NUnit.Framework;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
using GiWorld = Gi.World;

namespace Gi.Entities.Editor.Tests
{
    public sealed unsafe class GiOverlayTests
    {
        private const string Folder = "Library/GiOverlayTests";

        [SetUp]
        public void SetUp()
        {
            GiRuntime.CompleteAll();
            GiOrphans.Reclaim(GiRuntime.Stamps);
            if (Directory.Exists(Folder)) Directory.Delete(Folder, true);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(Folder)) Directory.Delete(Folder, true);
        }

        private static quaternion Rotation(float heading)
        {
            return quaternion.RotateY(math.PI / 2f - heading);
        }

        private static Entity Emitter(GiEcsFixture fixture, byte stamp, float x, float z, float heading, float scale)
        {
            var entity = fixture.CreateEmitter(0, stamp, 8, x, z);
            fixture.Manager.AddComponent<GiOriented>(entity);
            fixture.Manager.AddComponent<GiScaled>(entity);
            fixture.Manager.SetComponentData(entity, new LocalTransform { Position = new float3(x, 0f, z), Rotation = Rotation(heading), Scale = scale });
            return entity;
        }

        private static void NonzeroBounds(GiEcsFixture fixture, int grid, out float minX, out float minY, out float maxX, out float maxY)
        {
            fixture.Complete();
            var frame = fixture.Frame;
            frame.Tables.TryGrid(grid, out var gridId);
            frame.Tables.TryLayer(new GiLayerKey(0), out var layer);
            global::Gi.Grid.TryGetSpec(frame.World, gridId, out var power, out var gx, out var gy, out var size);
            var cells = 1 << power;
            var values = new short[cells * cells];
            fixed (short* p = values) GiWorld.QueryRegion(frame.World, gridId, layer, 0, 0, cells, cells, p);
            int x0 = cells, y0 = cells, x1 = -1, y1 = -1;
            for (var y = 0; y < cells; y++)
            for (var x = 0; x < cells; x++)
            {
                if (values[y * cells + x] == 0) continue;

                x0 = Mathf.Min(x0, x);
                x1 = Mathf.Max(x1, x);
                y0 = Mathf.Min(y0, y);
                y1 = Mathf.Max(y1, y);
            }

            var perUnit = cells / size;
            minX = gx + x0 / perUnit;
            maxX = gx + (x1 + 1) / perUnit;
            minY = gy + y0 / perUnit;
            maxY = gy + (y1 + 1) / perUnit;
        }

        [Test]
        public void EachDrawnFootprintContainsWhatQueryRegionReportsForThatEmitterAlone()
        {
            var spec = GiScenarioProfile.Standard().ToSpec();
            var rng = new GiRandom(21);
            for (var round = 0; round < 12; round++)
            {
                using var fixture = new GiEcsFixture("OverlayFootprint" + round);
                fixture.SetStandardProfile();
                var stamp = (byte)(round % 6);
                var emitter = Emitter(fixture, stamp, 24f + rng.Next(16), 24f + rng.Next(16), rng.Next(16) * 0.3927f + 0.000024f, 0.75f + rng.Next(5) * 0.25f);
                fixture.Tick(3);
                fixture.Complete();

                var draws = GiOverlayModel.Footprints(fixture.World, spec, new[] { emitter });
                Assert.That(draws.Count, Is.EqualTo(1), $"round {round}");
                NonzeroBounds(fixture, 1, out var minX, out var minY, out var maxX, out var maxY);
                float fx0, fy0, fx1, fy1;
                if (draws[0].Round)
                {
                    fx0 = draws[0].Center.x - draws[0].Radius;
                    fx1 = draws[0].Center.x + draws[0].Radius;
                    fy0 = draws[0].Center.y - draws[0].Radius;
                    fy1 = draws[0].Center.y + draws[0].Radius;
                }
                else
                {
                    fx0 = fy0 = float.MaxValue;
                    fx1 = fy1 = float.MinValue;
                    foreach (var corner in draws[0].Corners)
                    {
                        fx0 = Mathf.Min(fx0, corner.x);
                        fx1 = Mathf.Max(fx1, corner.x);
                        fy0 = Mathf.Min(fy0, corner.y);
                        fy1 = Mathf.Max(fy1, corner.y);
                    }
                }

                const float tolerance = 0.51f;
                Assert.That(minX, Is.GreaterThanOrEqualTo(fx0 - tolerance), $"round {round} min x");
                Assert.That(maxX, Is.LessThanOrEqualTo(fx1 + tolerance), $"round {round} max x");
                Assert.That(minY, Is.GreaterThanOrEqualTo(fy0 - tolerance), $"round {round} min y");
                Assert.That(maxY, Is.LessThanOrEqualTo(fy1 + tolerance), $"round {round} max y");
            }
        }

        [Test]
        public void ARoundEmitterDrawsADiscAndAStaticOneUsesItsLocalToWorld()
        {
            var spec = GiScenarioProfile.Standard().ToSpec();
            using var fixture = new GiEcsFixture("OverlayRound");
            fixture.SetStandardProfile();
            var round = Emitter(fixture, 4, 30f, 30f, 0f, 1.5f);
            var statics = fixture.Manager.CreateEntity(typeof(GiEmitter), typeof(LocalToWorld));
            fixture.Manager.SetComponentData(statics, new GiEmitter { Layer = new GiLayerKey(0), Stamp = new GiStampKey(0), Gain = 4 });
            fixture.Manager.SetComponentData(statics, new LocalToWorld { Value = float4x4.Translate(new float3(12f, 0f, 14f)) });
            fixture.Tick(2);

            var draws = GiOverlayModel.Footprints(fixture.World, spec, new[] { round, statics });
            Assert.That(draws.Count, Is.EqualTo(2));
            Assert.That(draws[0].Round, Is.True);
            Assert.That(draws[0].Radius, Is.EqualTo(6f).Within(1e-4f));
            Assert.That(draws[0].Center.x, Is.EqualTo(30f).Within(1e-4f));
            Assert.That(draws[1].Round, Is.False);
            Assert.That(draws[1].Center.x, Is.EqualTo(12f).Within(1e-4f));
            Assert.That(draws[1].Center.y, Is.EqualTo(14f).Within(1e-4f));
            Assert.That(draws[1].Corners.Length, Is.EqualTo(4));

            Assert.That(GiOverlayModel.Footprints(fixture.World, spec, new[] { Entity.Null }), Is.Empty);
        }

        [Test]
        public void DraggingAnEmitterChangesTheOverlayOnTheNextTick()
        {
            Assume.That(SystemInfo.supportsComputeShaders, "needs compute shaders");
            using var fixture = new GiEcsFixture("OverlayDrag");
            fixture.SetStandardProfile();
            fixture.AddInspector();
            var emitter = Emitter(fixture, 1, 14f, 14f, 0f, 1f);
            using var model = new GiFieldModel();
            model.Select("OverlayDrag", 1, 0);
            for (var i = 0; i < 4; i++) fixture.Frame1();

            var mirror = model.View.Mirror;
            Assert.That(mirror, Is.Not.Null);
            var uploadedBefore = mirror.UploadedTiles;
            var before = Sample(fixture, 14.5f, 14.5f);
            var farBefore = Sample(fixture, 46.5f, 46.5f);
            Assert.That(before, Is.Not.EqualTo(0));
            Assert.That(farBefore, Is.EqualTo(0));

            fixture.Manager.SetComponentData(emitter, new LocalTransform { Position = new float3(46f, 0f, 46f), Rotation = quaternion.identity, Scale = 1f });
            fixture.Frame1();
            Assert.That(mirror.UploadedTiles, Is.GreaterThan(uploadedBefore), "the mirror uploaded the tiles the drag changed");
            Assert.That(Sample(fixture, 14.5f, 14.5f), Is.EqualTo(0));
            Assert.That(Sample(fixture, 46.5f, 46.5f), Is.Not.EqualTo(0));
            Assert.That(model.View.Texture, Is.Not.Null);
            Assert.That(model.View.Dirty, Is.False, "the heatmap was redrawn in the same frame");
        }

        private static short Sample(GiEcsFixture fixture, float x, float y)
        {
            fixture.Complete();
            var frame = fixture.Frame;
            frame.Tables.TryLayer(new GiLayerKey(0), out var layer);
            GiWorld.TrySense(frame.World, layer, x, y, out var value);
            return value;
        }

        [Test]
        public void OverlayTogglesPersistInADeterministicFile()
        {
            var path = Folder + "/Gi/Overlay.json";
            Assert.That(GiOverlaySettingsStore.Load(path).ToJson(), Is.EqualTo(new GiOverlaySettings().ToJson()));

            var settings = new GiOverlaySettings { Heatmap = false, ReachDiscs = true, Layer = 3, Grid = 1, ValueScale = 33.25f };
            GiOverlaySettingsStore.Save(settings, path);
            var first = File.ReadAllBytes(path);
            var stamp = File.GetLastWriteTimeUtc(path);

            GiOverlaySettingsStore.Save(settings, path);
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(first));
            Assert.That(File.GetLastWriteTimeUtc(path), Is.EqualTo(stamp), "saving unchanged settings does not touch the file");

            var loaded = GiOverlaySettingsStore.Load(path);
            Assert.That((loaded.Heatmap, loaded.ReachDiscs, loaded.Layer, loaded.Grid, loaded.ValueScale), Is.EqualTo((false, true, 3, 1, 33.25f)));
            GiOverlaySettingsStore.Save(loaded, path);
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(first));
            Assert.That(System.Text.Encoding.UTF8.GetString(first), Does.StartWith("{\n  \"footprints\""));
            Assert.That(first.Length, Is.GreaterThan(0));
            Assert.That(first[0], Is.EqualTo((byte)'{'));
        }
    }
}
