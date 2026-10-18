using System.Collections.Generic;
using Gi.Entities.Authoring;
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
    public sealed unsafe class GiGizmosTests
    {
        private GiProfile _profile;
        private GiLayerAsset _layer;
        private GiStampAsset _stamp;
        private GameObject _emitter;

        [SetUp]
        public void SetUp()
        {
            _profile = ScriptableObject.CreateInstance<GiProfile>();
            _profile.name = "GizmoProfile";
            _profile.Grids = new List<GiGridEntry> { new GiGridEntry { Power = 6, Origin = Vector2.zero, Size = 64f } };
            _layer = ScriptableObject.CreateInstance<GiLayerAsset>();
            _layer.name = "signal";
            _stamp = ScriptableObject.CreateInstance<GiStampAsset>();
            _stamp.name = "mark";
            _profile.Layers = new List<GiLayerAsset> { _layer };
            _profile.Stamps = new List<GiStampAsset> { _stamp };
            _emitter = new GameObject("GizmoEmitter");
            _emitter.transform.position = new Vector3(8f, 0f, 8f);
            var authoring = _emitter.AddComponent<GiEmitterAuthoring>();
            authoring.Profile = _profile;
            authoring.Layer = _layer;
            authoring.Stamp = _stamp;
            authoring.Gain = 4;
        }

        [TearDown]
        public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(_emitter);
            UnityEngine.Object.DestroyImmediate(_stamp);
            UnityEngine.Object.DestroyImmediate(_layer);
            UnityEngine.Object.DestroyImmediate(_profile);
            GiGizmos.Invalidate();
            GiGizmos.RescanIfNeeded();
        }

        private static Entity Emitter(GiEcsFixture fixture, byte stamp, float x, float z, float heading, float scale)
        {
            var entity = fixture.CreateEmitter(0, stamp, 8, x, z);
            fixture.Manager.AddComponent<GiOriented>(entity);
            fixture.Manager.AddComponent<GiScaled>(entity);
            fixture.Manager.SetComponentData(entity, new LocalTransform { Position = new float3(x, 0f, z), Rotation = quaternion.RotateY(math.PI / 2f - heading), Scale = scale });
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

        private static GiGizmoDraw MarkOf(GameObject gameObject)
        {
            foreach (var mark in GiGizmos.All)
                if (mark.EntityId == gameObject.GetEntityId())
                    return mark;
            return default;
        }

        [Test]
        public void DrawnShapesReuseGiFootprintAndContainTheNonzeroBoundsOverTwelveRounds()
        {
            var spec = GiScenarioProfile.Standard().ToSpec();
            var rng = new GiRandom(21);
            for (var round = 0; round < 12; round++)
            {
                using var fixture = new GiEcsFixture("GizmoFootprint" + round);
                fixture.SetStandardProfile();
                var stamp = (byte)(round % 6);
                var emitter = Emitter(fixture, stamp, 24f + rng.Next(16), 24f + rng.Next(16), rng.Next(16) * 0.3927f + 0.000024f, 0.75f + rng.Next(5) * 0.25f);
                fixture.Tick(3);
                fixture.Complete();
                Assert.That(GiOverlayModel.Pose(fixture.Manager, emitter, fixture.Frame.Plane, out var position, out var heading, out var scale), Is.True, $"round {round}");

                var shape = spec.Stamps[stamp];
                var points = GiGizmoShape.Outline(shape, position.x, position.y, heading, scale, out var spokes);
                var lift = GiGizmoShape.Lift(shape, heading, scale);
                NonzeroBounds(fixture, 1, out var minX, out var minY, out var maxX, out var maxY);

                if (GiFootprint.IsRound(shape.Shape))
                {
                    Assert.That(spokes, Is.EqualTo(shape.Arc != 360), $"round {round} spokes");
                    Assert.That(lift, Is.EqualTo(0f), $"round {round} round shapes stay flat");
                    var middle = points[points.Length / 2];
                    var radius = shape.Radius * scale;
                    Assert.That(middle.x, Is.EqualTo(position.x + radius * Mathf.Cos(heading)).Within(1e-3f), $"round {round} arc follows the heading");
                    Assert.That(middle.y, Is.EqualTo(position.y + radius * Mathf.Sin(heading)).Within(1e-3f), $"round {round} arc follows the heading");
                }
                else
                {
                    Assert.That(spokes, Is.False, $"round {round}");
                    Assert.That(lift, Is.GreaterThan(0f), $"round {round} rect shapes lift a flat cuboid");
                    var corners = new float[8];
                    GiFootprint.Corners(shape, position.x, position.y, heading, scale, corners);
                    Assert.That(points.Length, Is.EqualTo(4), $"round {round}");
                    for (var i = 0; i < 4; i++)
                    {
                        Assert.That(points[i].x, Is.EqualTo(corners[2 * i]).Within(1e-5f), $"round {round} corner {i} reuses GiFootprint");
                        Assert.That(points[i].y, Is.EqualTo(corners[2 * i + 1]).Within(1e-5f), $"round {round} corner {i} reuses GiFootprint");
                    }
                }

                float fx0 = float.MaxValue, fy0 = float.MaxValue, fx1 = float.MinValue, fy1 = float.MinValue;
                foreach (var point in points)
                {
                    fx0 = Mathf.Min(fx0, point.x);
                    fx1 = Mathf.Max(fx1, point.x);
                    fy0 = Mathf.Min(fy0, point.y);
                    fy1 = Mathf.Max(fy1, point.y);
                }

                const float tolerance = 0.51f;
                Assert.That(minX, Is.GreaterThanOrEqualTo(fx0 - tolerance), $"round {round} min x");
                Assert.That(maxX, Is.LessThanOrEqualTo(fx1 + tolerance), $"round {round} max x");
                Assert.That(minY, Is.GreaterThanOrEqualTo(fy0 - tolerance), $"round {round} min y");
                Assert.That(maxY, Is.LessThanOrEqualTo(fy1 + tolerance), $"round {round} max y");
            }
        }

        [Test]
        public void AnEmitterDraggedOutsideEveryGridWarnsWithinTheFrameAndDraggingBackClearsIt()
        {
            GiGizmos.Invalidate();
            GiGizmos.RescanIfNeeded();
            var mark = MarkOf(_emitter);
            Assert.That(mark.EntityId, Is.EqualTo(_emitter.GetEntityId()));
            Assert.That(mark.OutsideGrids, Is.False);
            Assert.That(mark.Label, Is.EqualTo("layer signal reach 3x3 gain 4 coverage grids 0"));

            _emitter.transform.position = new Vector3(80f, 0f, 8f);
            Assert.That(GiGizmos.Touch(_emitter.transform), Is.True, "the change a drag publishes marks the cache dirty");
            GiGizmos.RescanIfNeeded();
            mark = MarkOf(_emitter);
            Assert.That(mark.OutsideGrids, Is.True, "the warning is present after the rescan the repaint performs");
            Assert.That(mark.Label, Does.EndWith("coverage no grid"));

            _emitter.transform.position = new Vector3(8f, 0f, 8f);
            Assert.That(GiGizmos.Touch(_emitter.transform), Is.True);
            GiGizmos.RescanIfNeeded();
            Assert.That(MarkOf(_emitter).OutsideGrids, Is.False, "dragging back clears the warning");
        }

        [Test]
        public void TouchingAnUnrelatedGameObjectTriggersNoRescan()
        {
            var bystander = new GameObject("GizmoBystander");
            try
            {
                GiGizmos.Invalidate();
                GiGizmos.RescanIfNeeded();
                var before = GiGizmos.Rescans;

                var moved = bystander.transform.position;
                moved.x += 1f;
                bystander.transform.position = moved;
                Assert.That(GiGizmos.Touch(bystander.transform), Is.False, "the filter the change stream feeds rejects unrelated objects");
                GiGizmos.RescanIfNeeded();
                Assert.That(GiGizmos.Rescans, Is.EqualTo(before), "touching an unrelated GameObject does not rescan");

                Assert.That(GiGizmos.Touch(_emitter), Is.True);
                GiGizmos.RescanIfNeeded();
                Assert.That(GiGizmos.Rescans, Is.EqualTo(before + 1));

                Assert.That(GiGizmos.Touch(_profile), Is.True, "editing the profile asset in use also refreshes");
                GiGizmos.RescanIfNeeded();
                Assert.That(GiGizmos.Rescans, Is.EqualTo(before + 2));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(bystander);
            }
        }
    }
}
