using Unity.Entities;

namespace Gi.Entities
{
    public struct GiEmitter : IComponentData, IEnableableComponent
    {
        public GiLayerKey Layer;
        public GiStampKey Stamp;
        public sbyte Gain;
    }

    public struct GiGainRamp : IComponentData
    {
        public ushort Ticks;
    }

    public struct GiOriented : IComponentData
    {
    }

    public struct GiScaled : IComponentData
    {
    }
}
