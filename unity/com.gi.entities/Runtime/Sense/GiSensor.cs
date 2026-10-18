using Unity.Entities;
using Unity.Mathematics;

namespace Gi.Entities
{
    public struct GiSensorValue : IComponentData
    {
        public short Value;
        public bool ExcludeSelf;
    }

    public struct GiSensorGradient : IComponentData
    {
        public float2 Gradient;
        public bool ExcludeSelf;
    }

    public struct GiSensorArea : IComponentData
    {
        public long Total;
        public float Reach;
        public bool ExcludeSelf;
    }

    public struct GiSensorMax : IComponentData
    {
        public short Value;
        public float2 Position;
        public float Reach;
    }

    public struct GiSensorNearest : IComponentData
    {
        public short Value;
        public float2 Position;
        public float Reach;
        public short Threshold;
    }

    public struct GiSensorFound : IComponentData, IEnableableComponent
    {
    }
}
