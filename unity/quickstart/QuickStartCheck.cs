using System;
using Gi.Entities;
using Gi.Entities.Authoring;
using Unity.Entities;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using EcsWorld = Unity.Entities.World;

public static class QuickStartCheck
{
    public static void Run()
    {
        try
        {
            Execute();
            Debug.Log("QUICKSTART OK");
            EditorApplication.Exit(0);
        }
        catch (Exception exception)
        {
            Debug.LogError("QUICKSTART FAILED " + exception);
            EditorApplication.Exit(1);
        }
    }

    private static void Bake(EcsWorld world, BlobAssetStore store, params GameObject[] roots)
    {
        var hybrid = typeof(BakingSystem).Assembly;
        var utility = hybrid.GetType("Unity.Entities.BakingUtility", true);
        var settingsType = hybrid.GetType("Unity.Entities.BakingSettings", true);
        var flagsType = utility.GetNestedType("BakingFlags", BindingFlags.Public | BindingFlags.NonPublic);
        var settings = Activator.CreateInstance(settingsType, Enum.ToObject(flagsType, 1u << 2), store);
        utility.GetMethod("BakeGameObjects", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public).Invoke(null, new object[] { world, roots, settings });
    }

    private static void Execute()
    {
        var layer = ScriptableObject.CreateInstance<GiLayerAsset>();
        layer.Kind = GiLayerKind.Persistent;
        var stamp = ScriptableObject.CreateInstance<GiStampAsset>();
        stamp.Shape = GiStampShape.Box;
        stamp.Value = 40;
        var profile = ScriptableObject.CreateInstance<GiProfile>();
        profile.Grids.Add(new GiGridEntry { Power = 6, Origin = Vector2.zero, Size = 64f });
        profile.Layers.Add(layer);
        profile.Stamps.Add(stamp);
        layer.Owner = profile;
        System.IO.Directory.CreateDirectory("Assets/Gi");
        AssetDatabase.CreateAsset(profile, "Assets/Gi/Profile.asset");
        AssetDatabase.AddObjectToAsset(layer, profile);
        AssetDatabase.AddObjectToAsset(stamp, profile);
        AssetDatabase.SaveAssets();

        var profileObject = new GameObject("Profile");
        profileObject.AddComponent<GiProfileAuthoring>().Profile = profile;
        var emitterObject = new GameObject("Emitter");
        emitterObject.transform.position = new Vector3(30f, 0f, 30f);
        var emitter = emitterObject.AddComponent<GiEmitterAuthoring>();
        emitter.Profile = profile;
        emitter.Layer = layer;
        emitter.Stamp = stamp;
        emitter.Gain = 5;
        emitter.Moves = false;

        using var store = new BlobAssetStore(128);
        using var baking = new EcsWorld("QuickStartBaking", WorldFlags.Game);
        Bake(baking, store, profileObject, emitterObject);

        var world = DefaultWorldInitialization.Initialize("QuickStartRun", false);
        try
        {
            world.EntityManager.MoveEntitiesFrom(baking.EntityManager);
            var time = 0.0;
            for (var i = 0; i < 90; i++)
            {
                time += 1.0 / 60.0;
                world.SetTime(new Unity.Core.TimeData(time, 1f / 60f));
                world.Update();
            }

            using var frames = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GiFrame>());
            GiRuntime.CompleteAll();
            world.EntityManager.CompleteAllTrackedJobs();
            var frame = world.EntityManager.GetComponentData<GiFrame>(frames.GetSingletonEntity());
            if (!frame.Alive) throw new InvalidOperationException("the Gi world is not alive");

            Gi.World.Memory(frame.World, out _, out _, out _, out _, out var sources);
            if (sources != 1) throw new InvalidOperationException("expected one source, found " + sources);

            frame.Tables.TryLayer(new GiLayerKey(0), out var layerId);
            Gi.World.TrySense(frame.World, layerId, 30.5f, 30.5f, out var value);
            if (value <= 0) throw new InvalidOperationException("the field is empty at the emitter: " + value);

            Debug.Log("QUICKSTART tick " + frame.Tick + " value " + value);
        }
        finally
        {
            world.Dispose();
        }
    }
}
