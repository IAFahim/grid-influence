using Unity.Entities;
using Unity.Mathematics;

namespace Gi.Entities.Tests
{
    public struct KindArea : IComponentData
    {
        public long Total;
    }

    public struct KindMax : IComponentData
    {
        public short Value;
        public float2 Position;
    }

    public struct KindNearest : IComponentData
    {
        public short Value;
        public float2 Position;
    }

    public struct KindFound : IComponentData, IEnableableComponent
    {
    }

    public struct KindWake : IComponentData, IEnableableComponent
    {
    }

    public struct KindReach : IComponentData
    {
        public float Reach;
        public short Threshold;
    }

    public struct KindValue : IComponentData
    {
        public short Value;
    }
}
