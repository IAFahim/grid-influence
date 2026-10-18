using System;

namespace Gi.Entities
{
    [Flags]
    public enum GiFaultKind : ushort
    {
        None = 0,
        Profile = 1,
        StaleToken = 2,
        Adapter = 4,
        Capacity = 8,
        Reclaimed = 16,
    }

    public unsafe struct GiFaults
    {
        public const int KindCount = 5;

        public ushort Raised;
        private fixed int _firstTick[KindCount];

        public bool Has(GiFaultKind kind)
        {
            return (Raised & (ushort)kind) != 0;
        }

        public bool Raise(GiFaultKind kind, int tick)
        {
            if (Has(kind)) return false;
            Raised |= (ushort)kind;
            _firstTick[Slot(kind)] = tick;
            return true;
        }

        public void Clear(GiFaultKind kind)
        {
            Raised = (ushort)(Raised & ~(ushort)kind);
        }

        public int FirstTick(GiFaultKind kind)
        {
            return Has(kind) ? _firstTick[Slot(kind)] : -1;
        }

        private static int Slot(GiFaultKind kind)
        {
            var bits = (uint)kind;
            var slot = 0;
            while ((bits >>= 1) != 0) slot++;
            return slot < KindCount ? slot : KindCount - 1;
        }
    }
}
