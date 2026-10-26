using System.Runtime.CompilerServices;
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
    Expire
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
        InvalidRange
    }

    public enum SourceResultStatus : byte
    {
        Applied,
        InvalidSource,
        InvalidLayer,
        InvalidStamp,
        SourceLimitReached
    }

    public readonly struct SourceTarget
    {
        internal readonly SourceId Source;
        internal readonly int Command;
        internal readonly bool Pending;

        [MethodImpl(Jit.Hot)]
        private SourceTarget(SourceId source, int command, bool pending)
        {
            Source = source;
            Command = command;
            Pending = pending;
        }

        [MethodImpl(Jit.Hot)]
        public static SourceTarget Existing(SourceId sourceId)
        {
            return new SourceTarget(sourceId, 0, false);
        }

        [MethodImpl(Jit.Hot)]
        public static SourceTarget CreatedBy(int commandIndex)
        {
            return new SourceTarget(default, commandIndex, true);
        }

        [MethodImpl(Jit.Hot)]
        public static implicit operator SourceTarget(SourceId sourceId)
        {
            return Existing(sourceId);
        }
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

        [MethodImpl(Jit.Hot)]
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

        [MethodImpl(Jit.Hot)]
        public static SourceCommand Place(
            byte layer, float positionX, float positionY, byte stamp, sbyte gain,
            float headingRadians = 0f, float scale = 1f)
        {
            return new SourceCommand(SourceCommandKind.Place, positionX: positionX, positionY: positionY,
                headingRadians: headingRadians, scale: scale, gain: gain, layer: layer, stamp: stamp);
        }

        [MethodImpl(Jit.Hot)]
        public static SourceCommand Update(
            SourceTarget source, float positionX, float positionY, float headingRadians, float scale, sbyte gain)
        {
            return new SourceCommand(SourceCommandKind.Update, source, positionX, positionY, headingRadians, scale,
                gain: gain);
        }

        [MethodImpl(Jit.Hot)]
        public static SourceCommand UpdateTransform(
            SourceTarget source, float positionX, float positionY, float headingRadians, float scale)
        {
            return new SourceCommand(SourceCommandKind.UpdateTransform, source, positionX, positionY, headingRadians,
                scale);
        }

        [MethodImpl(Jit.Hot)]
        public static SourceCommand Move(SourceTarget source, float positionX, float positionY)
        {
            return new SourceCommand(SourceCommandKind.Move, source, positionX, positionY);
        }

        [MethodImpl(Jit.Hot)]
        public static SourceCommand Turn(SourceTarget source, float headingRadians)
        {
            return new SourceCommand(SourceCommandKind.Turn, source, headingRadians: headingRadians);
        }

        [MethodImpl(Jit.Hot)]
        public static SourceCommand Scale(SourceTarget source, float scale)
        {
            return new SourceCommand(SourceCommandKind.Scale, source, scale: scale);
        }

        [MethodImpl(Jit.Hot)]
        public static SourceCommand SetGain(SourceTarget source, sbyte gain)
        {
            return new SourceCommand(SourceCommandKind.SetGain, source, gain: gain);
        }

        [MethodImpl(Jit.Hot)]
        public static SourceCommand Remove(SourceTarget source)
        {
            return new SourceCommand(SourceCommandKind.Remove, source);
        }

        [MethodImpl(Jit.Hot)]
        public static SourceCommand Fade(SourceTarget source, sbyte gain, int ticks)
        {
            return new SourceCommand(SourceCommandKind.Fade, source, ticks: ticks, gain: gain);
        }

        [MethodImpl(Jit.Hot)]
        public static SourceCommand Expire(SourceTarget source, int ticks)
        {
            return new SourceCommand(SourceCommandKind.Expire, source, ticks: ticks);
        }
    }

    public readonly struct SourceResult
    {
        public readonly SourceId SourceId;
        public readonly SourceResultStatus Status;

        [MethodImpl(Jit.Hot)]
        internal SourceResult(SourceId sourceId, SourceResultStatus status)
        {
            SourceId = sourceId;
            Status = status;
        }
    }
}
