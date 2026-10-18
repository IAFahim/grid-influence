using System;

namespace Gi.Entities.Tests.Support
{
    public enum GiScenarioOpKind : byte
    {
        Spawn,
        Despawn,
        Disable,
        Enable,
        Move,
        Turn,
        Scale,
        SetGain,
        Retarget,
        Pulse,
        Splat,
    }

    [Flags]
    public enum GiScenarioFlags : byte
    {
        None = 0,
        Oriented = 1,
        Scaled = 2,
        Disabled = 4,
        FadeOut = 8,
    }

    public readonly struct GiScenarioOp : IEquatable<GiScenarioOp>
    {
        public const float HeadingStepRadians = 0.39269908169872414f;

        public readonly GiScenarioOpKind Kind;
        public readonly int Entity;
        public readonly int Layer;
        public readonly int Stamp;
        public readonly int Gain;
        public readonly int XQuarters;
        public readonly int YQuarters;
        public readonly int HeadingSteps;
        public readonly int ScaleQuarters;
        public readonly int Ticks;
        public readonly GiScenarioFlags Flags;

        private GiScenarioOp(GiScenarioOpKind kind, int entity, int layer, int stamp, int gain, int xQuarters,
            int yQuarters, int headingSteps, int scaleQuarters, int ticks, GiScenarioFlags flags)
        {
            Kind = kind;
            Entity = entity;
            Layer = layer;
            Stamp = stamp;
            Gain = gain;
            XQuarters = xQuarters;
            YQuarters = yQuarters;
            HeadingSteps = headingSteps;
            ScaleQuarters = scaleQuarters;
            Ticks = ticks;
            Flags = flags;
        }

        public float X => XQuarters * 0.25f;

        public float Y => YQuarters * 0.25f;

        public float Heading => HeadingSteps * HeadingStepRadians;

        public float Scale => ScaleQuarters * 0.25f;

        public bool Equals(GiScenarioOp other)
        {
            return Kind == other.Kind && Entity == other.Entity && Layer == other.Layer && Stamp == other.Stamp &&
                   Gain == other.Gain && XQuarters == other.XQuarters && YQuarters == other.YQuarters &&
                   HeadingSteps == other.HeadingSteps && ScaleQuarters == other.ScaleQuarters &&
                   Ticks == other.Ticks && Flags == other.Flags;
        }

        public override bool Equals(object obj)
        {
            return obj is GiScenarioOp other && Equals(other);
        }

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(Kind);
            hash.Add(Entity);
            hash.Add(Layer);
            hash.Add(Stamp);
            hash.Add(Gain);
            hash.Add(XQuarters);
            hash.Add(YQuarters);
            hash.Add(HeadingSteps);
            hash.Add(ScaleQuarters);
            hash.Add(Ticks);
            hash.Add(Flags);
            return hash.ToHashCode();
        }

        public static GiScenarioOp Spawn(int entity, int layer, int stamp, int gain, int xQuarters, int yQuarters,
            int headingSteps, int scaleQuarters, int rampTicks, GiScenarioFlags flags)
        {
            return new GiScenarioOp(GiScenarioOpKind.Spawn, entity, layer, stamp, gain, xQuarters, yQuarters,
                headingSteps, scaleQuarters, rampTicks, flags);
        }

        public static GiScenarioOp Despawn(int entity)
        {
            return new GiScenarioOp(GiScenarioOpKind.Despawn, entity, 0, 0, 0, 0, 0, 0, 4, 0, GiScenarioFlags.None);
        }

        public static GiScenarioOp Disable(int entity)
        {
            return new GiScenarioOp(GiScenarioOpKind.Disable, entity, 0, 0, 0, 0, 0, 0, 4, 0, GiScenarioFlags.None);
        }

        public static GiScenarioOp Enable(int entity)
        {
            return new GiScenarioOp(GiScenarioOpKind.Enable, entity, 0, 0, 0, 0, 0, 0, 4, 0, GiScenarioFlags.None);
        }

        public static GiScenarioOp Move(int entity, int xQuarters, int yQuarters)
        {
            return new GiScenarioOp(GiScenarioOpKind.Move, entity, 0, 0, 0, xQuarters, yQuarters, 0, 4, 0,
                GiScenarioFlags.None);
        }

        public static GiScenarioOp Turn(int entity, int headingSteps)
        {
            return new GiScenarioOp(GiScenarioOpKind.Turn, entity, 0, 0, 0, 0, 0, headingSteps, 4, 0,
                GiScenarioFlags.None);
        }

        public static GiScenarioOp ScaleTo(int entity, int scaleQuarters)
        {
            return new GiScenarioOp(GiScenarioOpKind.Scale, entity, 0, 0, 0, 0, 0, 0, scaleQuarters, 0,
                GiScenarioFlags.None);
        }

        public static GiScenarioOp SetGain(int entity, int gain)
        {
            return new GiScenarioOp(GiScenarioOpKind.SetGain, entity, 0, 0, gain, 0, 0, 0, 4, 0, GiScenarioFlags.None);
        }

        public static GiScenarioOp Retarget(int entity, int layer, int stamp)
        {
            return new GiScenarioOp(GiScenarioOpKind.Retarget, entity, layer, stamp, 0, 0, 0, 0, 4, 0,
                GiScenarioFlags.None);
        }

        public static GiScenarioOp Pulse(int layer, int stamp, int gain, int xQuarters, int yQuarters,
            int headingSteps, int scaleQuarters, int ticks, bool fadeOut)
        {
            return new GiScenarioOp(GiScenarioOpKind.Pulse, -1, layer, stamp, gain, xQuarters, yQuarters,
                headingSteps, scaleQuarters, ticks, fadeOut ? GiScenarioFlags.FadeOut : GiScenarioFlags.None);
        }

        public static GiScenarioOp Splat(int layer, int stamp, int gain, int xQuarters, int yQuarters)
        {
            return new GiScenarioOp(GiScenarioOpKind.Splat, -1, layer, stamp, gain, xQuarters, yQuarters, 0, 4, 0,
                GiScenarioFlags.None);
        }
    }
}
