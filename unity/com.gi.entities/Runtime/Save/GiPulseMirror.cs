using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using GiWorld = Gi.World;

namespace Gi.Entities
{
    public struct GiPulseEntry
    {
        public GiWorld.SourceId Id;
        public int EndTick;
    }

    public unsafe struct GiPulseMirror : IComponentData
    {
        public const int PruneAbove = 64;

        public UnsafeList<GiPulseEntry>* Entries;

        public void Place(GiWorld.SourceId id)
        {
            Entries->Add(new GiPulseEntry { Id = id, EndTick = int.MaxValue });
        }

        public void Expire(GiWorld.SourceId id, int endTick)
        {
            for (var i = Entries->Length - 1; i >= 0; i--)
            {
                if (!Entries->ElementAt(i).Id.Equals(id)) continue;

                Entries->ElementAt(i).EndTick = endTick;
                return;
            }
        }

        public void Prune(int tick)
        {
            if (Entries->Length <= PruneAbove) return;

            for (var i = Entries->Length - 1; i >= 0; i--)
                if (Entries->ElementAt(i).EndTick <= tick)
                    Entries->RemoveAtSwapBack(i);
        }
    }
}
