using BovineLabs.Savanna;
using Unity.Entities;

namespace Gi.Entities
{
    [Save]
    public struct GiSplat : IComponentData, IEnableableComponent
    {
        public GiLayerKey Layer;
        public GiStampKey Stamp;
        public sbyte Gain;
    }
}
