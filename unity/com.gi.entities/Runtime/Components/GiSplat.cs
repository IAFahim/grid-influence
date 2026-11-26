#if GI_SAVANNA
using BovineLabs.Savanna;
#endif
using Unity.Entities;

namespace Gi.Entities
{
#if GI_SAVANNA
    [Save]
#endif
    public struct GiSplat : IComponentData, IEnableableComponent
    {
        public GiLayerKey Layer;
        public GiStampKey Stamp;
        public sbyte Gain;
    }
}
