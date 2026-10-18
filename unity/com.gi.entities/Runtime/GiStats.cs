using Unity.Entities;

namespace Gi.Entities
{
    public struct GiStats : IComponentData
    {
        public int Commands;
        public int Pulses;
        public int SyncRows;
        public int Splats;
        public int Sensed;
        public int ChangedTiles;
    }
}
