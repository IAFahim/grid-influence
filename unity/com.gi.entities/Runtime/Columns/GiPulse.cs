using System.Collections.Generic;

namespace Gi.Entities
{
    public struct GiPulse
    {
        public GiLayerKey Layer;
        public GiStampKey Stamp;
        public float X;
        public float Y;
        public float Heading;
        public float Scale;
        public sbyte Gain;
        public ushort Ticks;
        public bool FadeOut;
    }

    public struct GiPulseOrder : IComparer<GiPulse>
    {
        public int Compare(GiPulse a, GiPulse b)
        {
            var c = a.Layer.Index.CompareTo(b.Layer.Index);
            if (c != 0) return c;
            c = a.Stamp.Index.CompareTo(b.Stamp.Index);
            if (c != 0) return c;
            c = Bits(a.X).CompareTo(Bits(b.X));
            if (c != 0) return c;
            c = Bits(a.Y).CompareTo(Bits(b.Y));
            if (c != 0) return c;
            c = Bits(a.Heading).CompareTo(Bits(b.Heading));
            if (c != 0) return c;
            c = Bits(a.Scale).CompareTo(Bits(b.Scale));
            if (c != 0) return c;
            c = a.Gain.CompareTo(b.Gain);
            if (c != 0) return c;
            c = a.Ticks.CompareTo(b.Ticks);
            return c != 0 ? c : a.FadeOut.CompareTo(b.FadeOut);
        }

        private static uint Bits(float value)
        {
            return unchecked((uint)System.BitConverter.SingleToInt32Bits(value));
        }
    }
}
