using Unity.Entities;
using UnityEngine;

namespace Gi.Entities.Samples.Territory
{
    public sealed class TerritorySpawnerAuthoring : MonoBehaviour
    {
        public int Count = 200;
        public uint Seed = 11;
        public float Radius = 28f;
    }

    public sealed class TerritorySpawnerBaker : Baker<TerritorySpawnerAuthoring>
    {
        public override void Bake(TerritorySpawnerAuthoring authoring)
        {
            var entity = GetEntity(TransformUsageFlags.None);
            AddComponent(entity, new TerritorySpawner
            {
                Count = authoring.Count,
                Seed = authoring.Seed,
                Radius = authoring.Radius,
            });
        }
    }
}
