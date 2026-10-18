using System;

namespace Gi.Entities.Tests.Support
{
    public struct GiRandom
    {
        private const ulong Multiplier = 6364136223846793005ul;

        private ulong _state;
        private readonly ulong _increment;

        public GiRandom(ulong seed, ulong sequence = 54ul)
        {
            _state = 0ul;
            _increment = (sequence << 1) | 1ul;
            NextUInt();
            _state += seed;
            NextUInt();
        }

        public uint NextUInt()
        {
            unchecked
            {
                var previous = _state;
                _state = previous * Multiplier + _increment;
                var shifted = (uint)(((previous >> 18) ^ previous) >> 27);
                var rotation = (int)(previous >> 59);
                return (shifted >> rotation) | (shifted << (-rotation & 31));
            }
        }

        public int Next(int bound)
        {
            if (bound <= 0) throw new ArgumentOutOfRangeException(nameof(bound));
            var limit = (uint)bound;
            var threshold = unchecked(0u - limit) % limit;
            while (true)
            {
                var value = NextUInt();
                if (value >= threshold) return (int)(value % limit);
            }
        }

        public int Range(int minInclusive, int maxExclusive)
        {
            return minInclusive + Next(maxExclusive - minInclusive);
        }

        public bool Chance(int numerator, int denominator)
        {
            return Next(denominator) < numerator;
        }

        public float Quarter(int minInclusive, int maxExclusive)
        {
            return Range(minInclusive * 4, maxExclusive * 4) * 0.25f;
        }

        public void Shuffle<T>(T[] values)
        {
            Shuffle(values, values.Length);
        }

        public void Shuffle<T>(T[] values, int count)
        {
            for (var i = count - 1; i > 0; i--)
            {
                var j = Next(i + 1);
                (values[i], values[j]) = (values[j], values[i]);
            }
        }
    }
}
