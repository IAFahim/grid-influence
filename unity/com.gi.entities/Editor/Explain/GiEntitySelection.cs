using System;
using System.Reflection;
using Unity.Entities;
using UnityEditor;
using EcsWorld = Unity.Entities.World;

namespace Gi.Entities.Editor
{
    public static class GiEntitySelection
    {
        private static readonly Type ProxyType = Type.GetType("Unity.Entities.Editor.EntitySelectionProxy, Unity.Entities.Editor");

        private static readonly MethodInfo SelectMethod = ProxyType?.GetMethod("SelectEntity", BindingFlags.Public | BindingFlags.Static);

        private static readonly PropertyInfo WorldProperty = ProxyType?.GetProperty("World", BindingFlags.Public | BindingFlags.Instance);

        private static readonly PropertyInfo EntityProperty = ProxyType?.GetProperty("Entity", BindingFlags.Public | BindingFlags.Instance);

        public static bool Available => SelectMethod != null && WorldProperty != null && EntityProperty != null;

        public static EcsWorld Find(string name)
        {
            foreach (var world in EcsWorld.All)
                if (world.Name == name)
                    return world;
            return null;
        }

        public static bool Select(EcsWorld world, Entity entity)
        {
            if (!Available || world == null || !world.IsCreated || entity == Entity.Null || !world.EntityManager.Exists(entity)) return false;

            SelectMethod.Invoke(null, new object[] { world, entity });
            return TryGetSelected(out var selectedWorld, out var selected) && selectedWorld == world && selected == entity;
        }

        public static bool TryGetSelected(out EcsWorld world, out Entity entity)
        {
            world = null;
            entity = Entity.Null;
            var active = Selection.activeObject;
            if (!Available || active == null || active.GetType() != ProxyType) return false;

            world = (EcsWorld)WorldProperty.GetValue(active);
            entity = (Entity)EntityProperty.GetValue(active);
            return world != null && world.IsCreated;
        }
    }
}
