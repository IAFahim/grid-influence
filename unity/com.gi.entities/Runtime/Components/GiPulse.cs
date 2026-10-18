using Unity.Mathematics;

namespace Gi.Entities
{
    public struct GiPulse
    {
        public GiLayerKey Layer;
        public GiStampKey Stamp;
        public float2 Position;
        public float Heading;
        public float Scale;
        public sbyte Gain;
        public ushort Ticks;
        public bool FadeOut;
    }
}
