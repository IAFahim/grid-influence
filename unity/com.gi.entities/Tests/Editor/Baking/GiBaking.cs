using System;
using System.Reflection;
using Unity.Entities;
using UnityEngine;
using Unity.Scripting.LifecycleManagement;
using EcsWorld = Unity.Entities.World;

namespace Gi.Entities.Editor.Tests
{
    [NoAutoStaticsCleanup]
    internal static class GiBaking
    {
        public static EcsWorld Bake(params GameObject[] roots)
        {
            var hybrid = typeof(BakingSystem).Assembly;
            var utility = hybrid.GetType("Unity.Entities.BakingUtility", true);
            var settingsType = hybrid.GetType("Unity.Entities.BakingSettings", true);
            var flagsType = utility.GetNestedType("BakingFlags", BindingFlags.Public | BindingFlags.NonPublic);
            var flags = Enum.ToObject(flagsType, 1u << 2);
            var world = new EcsWorld("GiBake", WorldFlags.Game);
            var store = new BlobAssetStore(128);
            var settings = Activator.CreateInstance(settingsType, flags, store);
            var bake = utility.GetMethod("BakeGameObjects", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            bake.Invoke(null, new object[] { world, roots, settings });
            Stores.Add(world, store);
            return world;
        }

        public static void Dispose(EcsWorld world)
        {
            if (Stores.TryGetValue(world, out var store)) store.Dispose();
            Stores.Remove(world);
            world.Dispose();
        }

        private static readonly System.Collections.Generic.Dictionary<EcsWorld, BlobAssetStore> Stores =
            new System.Collections.Generic.Dictionary<EcsWorld, BlobAssetStore>();
    }
}
