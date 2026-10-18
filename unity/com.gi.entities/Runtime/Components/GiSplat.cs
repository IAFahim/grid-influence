using Unity.Entities;

namespace Gi.Entities
{
    public struct GiSplat : IComponentData, IEnableableComponent
    {
        public GiLayerKey Layer;
        public GiStampKey Stamp;
        public sbyte Gain;
    }
}
