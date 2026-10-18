using Unity.Entities;
using Unity.Mathematics;

namespace Gi.Entities.Samples.Read
{
    public struct ReadMote : IComponentData
    {
        public float Heading;
        public float Speed;
        public short Field;
    }

    public struct ReadSpawner : IComponentData
    {
        public int Count;
        public uint Seed;
        public float Radius;
        public float Speed;
    }
}
