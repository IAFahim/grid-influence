#if GI_SAVANNA
using BovineLabs.Savanna;
#endif
using Unity.Entities;

namespace Gi.Entities
{
#if GI_SAVANNA
    [Save]
#endif
    public struct GiEmitter : IComponentData, IEnableableComponent
    {
        public GiLayerKey Layer;
        public GiStampKey Stamp;
        public sbyte Gain;
    }

#if GI_SAVANNA
    [Save]
#endif
    public struct GiGainRamp : IComponentData
    {
        public ushort Ticks;
    }

#if GI_SAVANNA
    [Save]
#endif
    public struct GiOriented : IComponentData
    {
    }

#if GI_SAVANNA
    [Save]
#endif
    public struct GiScaled : IComponentData
    {
    }
}
