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

    public readonly struct GiReachDraw
    {
        public readonly Entity Entity;
        public readonly Vector2 Center;
        public readonly float Radius;
        public readonly GiBindingKind Kind;
        public readonly int Binding;

        public GiReachDraw(Entity entity, Vector2 center, float radius, GiBindingKind kind, int binding)
        {
            Entity = entity;
            Center = center;
            Radius = radius;
            Kind = kind;
            Binding = binding;
        }
    }

    public static class GiOverlayModel
    {
        public static List<GiReachDraw> Reaches(EcsWorld world, ICollection<Entity> selection)
        {
            var draws = new List<GiReachDraw>();
            var manager = world.EntityManager;
            using var frames = manager.CreateEntityQuery(ComponentType.ReadOnly<GiFrame>());
            using var profiles = manager.CreateEntityQuery(ComponentType.ReadOnly<GiProfileReference>());
            if (frames.IsEmptyIgnoreFilter || profiles.IsEmptyIgnoreFilter) return draws;

            var frame = manager.GetComponentData<GiFrame>(frames.GetSingletonEntity());
            var blob = profiles.GetSingleton<GiProfileReference>().Value;
            if (!frame.Alive || !blob.IsCreated) return draws;

            ref var profile = ref blob.Value;
            foreach (var entity in selection)
            {
                if (!manager.Exists(entity) || !Pose(manager, entity, frame.Plane, out var position, out _, out _)) continue;

                for (var i = 0; i < profile.Bindings.Length; i++)
                {
                    var binding = profile.Bindings[i];
                    if (binding.Kind != GiBindingKind.Area && binding.Kind != GiBindingKind.Max && binding.Kind != GiBindingKind.Nearest &&
                        binding.Kind != GiBindingKind.Changed)
                        continue;

                    if (!Applies(manager, entity, binding)) continue;

                    var radius = binding.Reach;
                    if (binding.ReachField.TypeHash != 0ul && !GiBindingRead.TryFloat(manager, entity, binding.ReachField, out radius)) continue;
                    if (!(radius > 0f) || float.IsInfinity(radius)) continue;

                    draws.Add(new GiReachDraw(entity, position, radius, binding.Kind, i));
                }
            }

            return draws;
        }

        public static bool Applies(EntityManager manager, Entity entity, in GiBindingSpec binding)
        {
            var any = false;
            foreach (var field in new[] { binding.Value, binding.Gradient, binding.Total, binding.Position, binding.Coverage })
            {
                if (field.TypeHash == 0ul) continue;

                any = true;
                if (GiBindingRead.Has(manager, entity, field)) return true;
            }

            return !any;
        }

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
