using System.Threading;

namespace Gi.Entities
{
    public struct GiGuardReport
    {
        public byte World;
        public int Intruder;
        public int Holder;
        public bool IntruderWrites;
        public int Count;
    }

    public unsafe struct GiGuardTable
    {
        public const int Worlds = 256;
        public const int WriterBit = 1 << 20;

        private const int Free = 0;
        private const int Filling = 1;
        private const int Ready = 2;

        private fixed int _state[Worlds];
        private fixed int _holder[Worlds];
        private int _total;
        private int _slot;
        private int _world;
        private int _intruder;
        private int _holderTag;
        private int _writes;

        public void Reset()
        {
            for (var i = 0; i < Worlds; i++)
            {
                _state[i] = 0;
                _holder[i] = 0;
            }

            _total = 0;
            _slot = Free;
            _world = 0;
            _intruder = 0;
            _holderTag = 0;
            _writes = 0;
        }

        public bool Begin(byte world, int tag, bool write)
        {
            var conflict = write
                ? Interlocked.Add(ref _state[world], WriterBit) != WriterBit
                : Interlocked.Increment(ref _state[world]) >= WriterBit;
            if (conflict)
            {
                Report(world, tag, Volatile.Read(ref _holder[world]), write);
                return false;
            }

            Volatile.Write(ref _holder[world], tag);
            return true;
        }

        public void End(byte world, bool write)
        {
            if (write) Interlocked.Add(ref _state[world], -WriterBit);
            else Interlocked.Decrement(ref _state[world]);
        }

        public bool Idle(byte world)
        {
            return Volatile.Read(ref _state[world]) == 0;
        }

        public int Total()
        {
            return Volatile.Read(ref _total);
        }

        public bool Take(out GiGuardReport report)
        {
            if (Volatile.Read(ref _slot) != Ready)
            {
                report = default;
                return false;
            }

            report = new GiGuardReport
            {
                World = (byte)_world,
                Intruder = _intruder,
                Holder = _holderTag,
                IntruderWrites = _writes != 0,
                Count = Volatile.Read(ref _total),
            };
            Volatile.Write(ref _slot, Free);
            return true;
        }

        private void Report(byte world, int intruder, int holder, bool write)
        {
            Interlocked.Increment(ref _total);
            if (Interlocked.CompareExchange(ref _slot, Filling, Free) != Free) return;

            _world = world;
            _intruder = intruder;
            _holderTag = holder;
            _writes = write ? 1 : 0;
            Volatile.Write(ref _slot, Ready);
        }
    }
}
