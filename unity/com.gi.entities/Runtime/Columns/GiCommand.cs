using GiWorld = Gi.World;

namespace Gi.Entities
{
    public enum GiCommandKind : byte
    {
        Place,
        Remove,
        SetGain,
        Fade,
        Expire,
    }

    public struct GiCommand
    {
        public const int Existing = -1;
        public const int NoOwner = -1;

        public GiCommandKind Kind;
        public GiLayerKey LayerKey;
        public GiStampKey StampKey;
        public byte Layer;
        public byte Stamp;
        public sbyte Gain;
        public sbyte TargetGain;
        public ushort Ticks;
        public int Target;
        public int Owner;
        public GiWorld.SourceId Source;
        public float X;
        public float Y;
        public float Heading;
        public float Scale;
    }

    public struct GiCommandResult
    {
        public GiFaultCode Code;
        public GiWorld.SourceId Source;
    }

    public enum GiApplyStatus : byte
    {
        Applied,
        Stale,
        Adapter,
    }
}
