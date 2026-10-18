using Unity.Entities;
using Unity.Mathematics;

namespace Gi.Entities.Samples.Crowd
{
    public struct CrowdSteering : IComponentData
    {
        public float2 Gradient;
    }
}
