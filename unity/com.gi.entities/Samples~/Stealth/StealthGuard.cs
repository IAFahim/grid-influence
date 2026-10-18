using Unity.Entities;
using Unity.Mathematics;

namespace Gi.Entities.Samples.Stealth
{
    public struct StealthGuard : IComponentData
    {
        public short Loudness;
        public float2 Where;
        public float Hearing;
        public float SweepRate;
        public float SweepPhase;
    }

    public struct StealthAlert : IComponentData, IEnableableComponent
    {
    }
}
