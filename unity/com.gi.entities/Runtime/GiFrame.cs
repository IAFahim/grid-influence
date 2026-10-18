using Unity.Entities;
using GiWorld = Gi.World;

namespace Gi.Entities
{
    public struct GiFrame : IComponentData
    {
        public byte World;
        public GiWorld.Token Token;
        public int Tick;
        public uint Generation;
        public ulong ProfileHash;
        public GiPlane Plane;
        public GiTables Tables;
        public GiFaults Faults;

        public bool Alive => Generation != 0u;
    }
}
