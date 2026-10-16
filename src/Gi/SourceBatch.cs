namespace Gi;

internal enum SourceCommandKind : byte
{
    Place = 1,
    Update,
    UpdateTransform,
    Move,
    Turn,
    Scale,
    SetGain,
    Remove,
    Fade,
    Expire,
}

public static partial class World
{
    public enum BatchStatus : byte
    {
        Applied,
        InvalidWorld,
        InsufficientResults,
        InvalidCommand,
        InvalidReference,
        OverlappingBuffers,
        TooManyCommands,
        StaleRevision,
        InvalidRange,
    }

    public enum SourceResultStatus : byte
    {
        Applied,
        InvalidSource,
        InvalidLayer,
        InvalidStamp,
        SourceLimitReached,
    }

    public readonly struct SourceTarget
    {
        internal readonly SourceId Source;
        internal readonly int Command;
        internal readonly bool Pending;

        private SourceTarget(SourceId source, int command, bool pending)
        {
            Source = source;
            Command = command;
            Pending = pending;
        }

        public static SourceTarget Existing(SourceId sourceId) => new(sourceId, 0, false);

        public static SourceTarget CreatedBy(int commandIndex) => new(default, commandIndex, true);

        public static implicit operator SourceTarget(SourceId sourceId) => Existing(sourceId);
    }

    public readonly struct SourceCommand
    {
        internal readonly SourceTarget Target;
        internal readonly float PositionX;
        internal readonly float PositionY;
        internal readonly float HeadingRadians;
        internal readonly float ScaleValue;
        internal readonly int Ticks;
        internal readonly SourceCommandKind Kind;
        internal readonly sbyte Gain;
        internal readonly byte Layer;
        internal readonly byte Stamp;

        private SourceCommand(
            SourceCommandKind kind, SourceTarget target = default,
            float positionX = 0f, float positionY = 0f, float headingRadians = 0f,
            float scale = 1f, int ticks = 0, sbyte gain = 0, byte layer = 0, byte stamp = 0)
        {
            Target = target;
            PositionX = positionX;
            PositionY = positionY;
            HeadingRadians = headingRadians;
            ScaleValue = scale;
            Ticks = ticks;
            Kind = kind;
            Gain = gain;
            Layer = layer;
            Stamp = stamp;
        }

        public static SourceCommand Place(
            byte layer, float positionX, float positionY, byte stamp, sbyte gain,
            float headingRadians = 0f, float scale = 1f)
            => new(SourceCommandKind.Place, positionX: positionX, positionY: positionY,
                headingRadians: headingRadians, scale: scale, gain: gain, layer: layer, stamp: stamp);

        public static SourceCommand Update(
            SourceTarget source, float positionX, float positionY, float headingRadians, float scale, sbyte gain)
            => new(SourceCommandKind.Update, source, positionX, positionY, headingRadians, scale, gain: gain);

        public static SourceCommand UpdateTransform(
            SourceTarget source, float positionX, float positionY, float headingRadians, float scale)
            => new(SourceCommandKind.UpdateTransform, source, positionX, positionY, headingRadians, scale);

        public static SourceCommand Move(SourceTarget source, float positionX, float positionY)
            => new(SourceCommandKind.Move, source, positionX, positionY);

        public static SourceCommand Turn(SourceTarget source, float headingRadians)
            => new(SourceCommandKind.Turn, source, headingRadians: headingRadians);

        public static SourceCommand Scale(SourceTarget source, float scale)
            => new(SourceCommandKind.Scale, source, scale: scale);

        public static SourceCommand SetGain(SourceTarget source, sbyte gain)
            => new(SourceCommandKind.SetGain, source, gain: gain);

        public static SourceCommand Remove(SourceTarget source) => new(SourceCommandKind.Remove, source);

        public static SourceCommand Fade(SourceTarget source, sbyte gain, int ticks)
            => new(SourceCommandKind.Fade, source, ticks: ticks, gain: gain);

        public static SourceCommand Expire(SourceTarget source, int ticks)
            => new(SourceCommandKind.Expire, source, ticks: ticks);
    }

    public readonly struct SourceResult
    {
        public readonly SourceId SourceId;
        public readonly SourceResultStatus Status;

        internal SourceResult(SourceId sourceId, SourceResultStatus status)
        {
            SourceId = sourceId;
            Status = status;
        }
    }
}

internal struct SourceBatchLink
{
    public int Entry;
    public int Next;
}

internal struct SourceBatchState
{
    public float PositionX;
    public float PositionY;
    public ushort Angle;
    public ushort Scale;
    public byte Stamp;
    public byte Layer;
    public byte Gain;
    public byte Alive;
    public uint Generation;
    public byte Timed;
    public sbyte FadeFrom;
    public sbyte FadeTo;
    public int FadeStart;
    public int FadeTicks;
    public int ExpireAt;
}

internal struct SourceBatchEntry
{
    public SourceBatchState State;
    public SourceBatchState PendingState;
    public ScheduleRecord ScheduleBefore;
    public int Slot;
    public int FirstCommand;
    public int LastCommand;
    public int FirstDepositCommand;
    public int FirstScheduleCommand;
    public uint PlannedGeneration;
    public byte PlannedAlive;
}
