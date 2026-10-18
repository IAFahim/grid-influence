using Unity.Entities;
using UnityEngine;

namespace Gi.Entities.Samples.Read
{
    public sealed class ReadSpawnerAuthoring : MonoBehaviour
    {
        public int Count = 600;
        public uint Seed = 3;
        public float Radius = 28f;
        [Range(0f, 12f)] public float Speed = 4f;
    }

    public sealed class ReadSpawnerBaker : Baker<ReadSpawnerAuthoring>
    {
        public override void Bake(ReadSpawnerAuthoring authoring)
        {
            var entity = GetEntity(TransformUsageFlags.None);
            AddComponent(entity, new ReadSpawner
            {
                Count = authoring.Count,
                Seed = authoring.Seed,
                Radius = authoring.Radius,
                Speed = authoring.Speed,
            });
        }
    }
}
