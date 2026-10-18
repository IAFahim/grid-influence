using Unity.Entities;
using Unity.Mathematics;

namespace Gi.Entities.Samples.Crowd
{
    public struct CrowdAgent : IComponentData
    {
        public uint Rng;
        public float Speed;
    }
}
