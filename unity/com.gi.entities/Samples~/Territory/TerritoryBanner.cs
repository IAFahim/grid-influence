using Unity.Entities;

namespace Gi.Entities.Samples.Territory
{
    public struct TerritoryBanner : IComponentData
    {
        public short Strength;
    }

    public struct TerritoryHeld : IComponentData, IEnableableComponent
    {
    }

    public struct TerritorySpawner : IComponentData
    {
        public int Count;
        public uint Seed;
        public float Radius;
    }
}
