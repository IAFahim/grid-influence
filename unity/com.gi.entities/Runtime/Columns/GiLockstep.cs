using System;

namespace Gi.Entities
{
    public enum GiLockstepVerdict : byte
    {
        Unknown,
        Match,
        Mismatch,
    }

    public sealed class GiLockstep
    {
        public const int Window = 256;

        private readonly int _layers;
        private readonly int[] _ticks = new int[Window];
        private readonly ulong[] _hashes;

        public GiLockstep(int layers)
        {
            _layers = layers;
            _hashes = new ulong[Window * Math.Max(1, layers)];
            Array.Fill(_ticks, -1);
        }

        public int LayerCount => _layers;

        public int Latest { get; private set; } = -1;

        public int Checks { get; private set; }

        public int Mismatches { get; private set; }

        public int FirstMismatchTick { get; private set; } = -1;

        public void Record(int tick, ReadOnlySpan<ulong> hashes)
        {
            var slot = tick & (Window - 1);
            _ticks[slot] = tick;
            for (var l = 0; l < _layers && l < hashes.Length; l++) _hashes[slot * _layers + l] = hashes[l];
            if (tick > Latest) Latest = tick;
        }

        public bool TryGet(int tick, int layer, out ulong hash)
        {
            hash = 0ul;
            if ((uint)layer >= (uint)_layers) return false;

            var slot = tick & (Window - 1);
            if (_ticks[slot] != tick) return false;

            hash = _hashes[slot * _layers + layer];
            return true;
        }

        public GiLockstepVerdict Check(int tick, int layer, ulong remote)
        {
            if (!TryGet(tick, layer, out var local)) return GiLockstepVerdict.Unknown;

            Checks++;
            if (local == remote) return GiLockstepVerdict.Match;

            Mismatches++;
            if (FirstMismatchTick < 0 || tick < FirstMismatchTick) FirstMismatchTick = tick;
            return GiLockstepVerdict.Mismatch;
        }

        public bool Expired(int tick)
        {
            return Latest >= 0 && tick <= Latest - Window;
        }

        public bool Ahead(int tick)
        {
            return tick > Latest;
        }
    }
}
