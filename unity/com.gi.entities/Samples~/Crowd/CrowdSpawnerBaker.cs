using System.Collections.Generic;
using Unity.Entities;
using UnityEngine;

namespace Gi.Entities.Samples.Crowd
{
    public sealed class CrowdSpawnerBaker : Baker<CrowdSpawnerAuthoring>
    {
        public override void Bake(CrowdSpawnerAuthoring authoring)
        {
            DependsOn(authoring.Profile);
            DependsOn(authoring.Layer);
            DependsOn(authoring.Stamp);

            var errors = new List<string>();
            if (authoring.Profile == null) errors.Add("No profile is assigned.");
            var layer = authoring.Profile == null ? -1 : authoring.Profile.LayerIndex(authoring.Layer);
            var stamp = authoring.Profile == null ? -1 : authoring.Profile.StampIndex(authoring.Stamp);
            if (layer < 0) errors.Add(authoring.Layer == null ? "No layer is assigned." : "The layer is not in the profile's stack.");
            if (stamp < 0) errors.Add(authoring.Stamp == null ? "No stamp is assigned." : "The stamp is not in the profile's stamp list.");
            if (errors.Count > 0)
            {
                foreach (var error in errors) Debug.LogError(error, authoring.gameObject);
                return;
            }

            var entity = GetEntity(TransformUsageFlags.None);
            AddComponent(entity, new CrowdSpawner
            {
                Layer = new GiLayerKey((byte)layer),
                Stamp = new GiStampKey((byte)stamp),
                Count = authoring.Count,
                Seed = authoring.Seed,
                Radius = authoring.Radius,
                Gain = (sbyte)authoring.Gain,
                Speed = authoring.Speed,
            });
        }
    }

    public struct CrowdSpawner : IComponentData
    {
        public GiLayerKey Layer;
        public GiStampKey Stamp;
        public int Count;
        public uint Seed;
        public float Radius;
        public sbyte Gain;
        public float Speed;
    }
}
