using BovineLabs.Savanna;
using Unity.Entities;

namespace Gi.Entities
{
    [Save]
    public struct GiEmitter : IComponentData, IEnableableComponent
    {
        public GiLayerKey Layer;
        public GiStampKey Stamp;
        public sbyte Gain;
    }

    [Save]
    public struct GiGainRamp : IComponentData
    {
        public ushort Ticks;
    }

    [Save]
    public struct GiOriented : IComponentData
    {
    }

    [Save]
    public struct GiScaled : IComponentData
    {
    }
}
