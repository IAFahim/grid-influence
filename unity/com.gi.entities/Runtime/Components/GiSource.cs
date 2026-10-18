using Unity.Entities;
using GiWorld = Gi.World;

namespace Gi.Entities
{
    public struct GiSource : ICleanupComponentData
    {
        public GiWorld.SourceId Id;
        public GiLayerKey Layer;
        public GiStampKey Stamp;
        public sbyte Gain;

        public bool Placed => Id.Generation != 0u;
    }
}
