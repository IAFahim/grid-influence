using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Transforms;
using UnityEngine;
using EcsWorld = Unity.Entities.World;

namespace Gi.Entities.Editor
{
    public readonly struct GiFootprintDraw
    {
        public readonly Entity Entity;
        public readonly bool Round;
        public readonly Vector2 Center;
        public readonly float Radius;
        public readonly Vector2[] Corners;

        public GiFootprintDraw(Entity entity, bool round, Vector2 center, float radius, Vector2[] corners)
        {
            Entity = entity;
            Round = round;
            Center = center;
            Radius = radius;
            Corners = corners;
        }
    }

    public static class GiOverlayModel
    {
        public static List<GiFootprintDraw> Footprints(EcsWorld world, GiProfileSpec profile, ICollection<Entity> selection)
        {
            var draws = new List<GiFootprintDraw>();
            var manager = world.EntityManager;
            using var frames = manager.CreateEntityQuery(ComponentType.ReadOnly<GiFrame>());
            if (frames.IsEmptyIgnoreFilter) return draws;

            var frame = manager.GetComponentData<GiFrame>(frames.GetSingletonEntity());
            if (!frame.Alive) return draws;

            var corners = new float[8];
            foreach (var entity in selection)
            {
                if (!manager.Exists(entity) || !manager.HasComponent<GiEmitter>(entity)) continue;

                var emitter = manager.GetComponentData<GiEmitter>(entity);
                if (emitter.Stamp.Index >= profile.Stamps.Length) continue;
                if (!Pose(manager, entity, frame.Plane, out var position, out var heading, out var scale)) continue;

                var spec = profile.Stamps[emitter.Stamp.Index];
                if (!manager.HasComponent<GiOriented>(entity)) heading = 0f;
                if (!manager.HasComponent<GiScaled>(entity)) scale = 1f;
                if (GiFootprint.IsRound(spec.Shape))
                {
                    draws.Add(new GiFootprintDraw(entity, true, position, spec.Radius * scale, null));
                    continue;
                }

                GiFootprint.Corners(spec, position.x, position.y, heading, scale, corners);
                draws.Add(new GiFootprintDraw(entity, false, position, 0f,
                    new[] { new Vector2(corners[0], corners[1]), new Vector2(corners[2], corners[3]), new Vector2(corners[4], corners[5]), new Vector2(corners[6], corners[7]) }));
            }

            return draws;
        }

        public static bool Pose(EntityManager manager, Entity entity, in GiPlane plane, out Vector2 position, out float heading, out float scale)
        {
            position = default;
            heading = 0f;
            scale = 1f;
            if (manager.HasComponent<LocalTransform>(entity) && !manager.HasComponent<Parent>(entity))
            {
                var transform = manager.GetComponentData<LocalTransform>(entity);
                plane.Project(transform.Position.x, transform.Position.y, transform.Position.z, out var x, out var y);
                position = new Vector2(x, y);
                var q = transform.Rotation.value;
                heading = plane.Heading(q.x, q.y, q.z, q.w);
                scale = transform.Scale;
                return true;
            }

            if (!manager.HasComponent<LocalToWorld>(entity)) return false;

            var matrix = manager.GetComponentData<LocalToWorld>(entity);
            plane.Project(matrix.Value.c3.x, matrix.Value.c3.y, matrix.Value.c3.z, out var px, out var py);
            position = new Vector2(px, py);
            var rotation = matrix.Rotation.value;
            heading = plane.Heading(rotation.x, rotation.y, rotation.z, rotation.w);
            scale = Unity.Mathematics.math.length(matrix.Value.c0.xyz);
            return true;
        }
    }
}
