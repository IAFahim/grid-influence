using System;

namespace Gi.Entities
{
    public readonly struct GiLayerKey : IEquatable<GiLayerKey>
    {
        public readonly byte Index;

        public GiLayerKey(byte index)
        {
            Index = index;
        }

        public bool Equals(GiLayerKey other)
        {
            return Index == other.Index;
        }

        public override bool Equals(object obj)
        {
            return obj is GiLayerKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            return Index;
        }
    }

    public readonly struct GiStampKey : IEquatable<GiStampKey>
    {
        public readonly byte Index;

        public GiStampKey(byte index)
        {
            Index = index;
        }

        public bool Equals(GiStampKey other)
        {
            return Index == other.Index;
        }

        public override bool Equals(object obj)
        {
            return obj is GiStampKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            return Index;
        }
    }
}
