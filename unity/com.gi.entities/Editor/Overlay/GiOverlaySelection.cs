using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using UnityEditor;
using UnityEngine;
using EcsWorld = Unity.Entities.World;

namespace Gi.Entities.Editor
{
    public static class GiOverlaySelection
    {
        public static List<Entity> Entities(EcsWorld world)
        {
            var result = Entities(world, Selection.gameObjects);
            if (GiEntitySelection.TryGetSelected(out var selectedWorld, out var entity) && selectedWorld == world && !result.Contains(entity)) result.Add(entity);
            return result;
        }

        public static List<Entity> Entities(EcsWorld world, IReadOnlyList<GameObject> objects)
        {
            var result = new List<Entity>();
            if (world == null || !world.IsCreated || objects.Count == 0) return result;

            using var query = world.EntityManager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<EntityGuid>() },
                Options = EntityQueryOptions.IncludePrefab | EntityQueryOptions.IncludeDisabledEntities,
            });
            if (query.IsEmptyIgnoreFilter) return result;

            using var entities = query.ToEntityArray(Allocator.Temp);
            using var guids = query.ToComponentDataArray<EntityGuid>(Allocator.Temp);
            foreach (var go in objects)
            {
                if (go == null) continue;

                var id = go.GetEntityId();
                for (var i = 0; i < guids.Length; i++)
                    if (guids[i].OriginatingEntityId == id && guids[i].Serial == 0u && !result.Contains(entities[i]))
                        result.Add(entities[i]);
            }

            return result;
        }
    }
}
