using Unity.Entities;
using Unity.NetCode;

namespace Gi.Entities.Netcode
{
    public struct GiGhostEmitter : IComponentData
    {
        [GhostField] public byte Layer;
        [GhostField] public byte Stamp;
        [GhostField] public sbyte Gain;
        [GhostField] public bool Enabled;
    }

    public struct GiGhostSplat : IComponentData
    {
        [GhostField] public byte Layer;
        [GhostField] public byte Stamp;
        [GhostField] public sbyte Gain;
        [GhostField] public bool Enabled;
    }

    public struct GiProfileHashRpc : IRpcCommand
    {
        public ulong Hash;
    }

    public struct GiProfileSent : IComponentData
    {
    }
}
