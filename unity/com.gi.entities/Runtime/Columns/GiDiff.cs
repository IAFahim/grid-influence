using GiWorld = Gi.World;

namespace Gi.Entities
{
    public struct GiDesired
    {
        public bool Want;
        public GiLayerKey Layer;
        public GiStampKey Stamp;
        public sbyte Gain;
        public ushort Ramp;
        public bool Oriented;
        public bool Scaled;
        public float X;
        public float Y;
        public float Heading;
        public float Scale;
    }

    public struct GiApplied
    {
        public bool Placed;
        public GiLayerKey Layer;
        public GiStampKey Stamp;
        public sbyte Gain;
        public GiWorld.SourceId Source;
    }

    public struct GiPlan
    {
        public const int MaxCommands = 3;

        public int Count;
        public GiFaultCode Fault;
        public GiCommand First;
        public GiCommand Second;
        public GiCommand Third;

        public GiCommand this[int index] => index == 0 ? First : index == 1 ? Second : Third;
    }

    public static class GiDiff
    {
        public static GiPlan Plan(in GiDesired desired, in GiApplied applied, in GiTables tables, int owner, int firstIndex)
        {
            var plan = default(GiPlan);
            var want = desired.Want;
            byte layer = 0;
            byte stamp = 0;
            if (want)
            {
                if (!tables.TryLayer(desired.Layer, out layer))
                {
                    plan.Fault = GiFaultCode.InvalidLayer;
                    want = false;
                }
                else if (!tables.TryStamp(desired.Stamp, out stamp))
                {
                    plan.Fault = GiFaultCode.InvalidStamp;
                    want = false;
                }
            }

            if (!want)
            {
                if (applied.Placed) Add(ref plan, Remove(applied, owner));
                return plan;
            }

            var placed = applied.Placed;
            if (placed && (!applied.Layer.Equals(desired.Layer) || !applied.Stamp.Equals(desired.Stamp)))
            {
                Add(ref plan, Remove(applied, owner));
                placed = false;
            }

            if (!placed)
            {
                var ramp = desired.Ramp > 0;
                var index = firstIndex + plan.Count;
                Add(ref plan, new GiCommand
                {
                    Kind = GiCommandKind.Place,
                    LayerKey = desired.Layer,
                    StampKey = desired.Stamp,
                    Layer = layer,
                    Stamp = stamp,
                    Gain = ramp ? (sbyte)0 : desired.Gain,
                    TargetGain = desired.Gain,
                    Target = GiCommand.Existing,
                    Owner = owner,
                    X = desired.X,
                    Y = desired.Y,
                    Heading = desired.Oriented ? desired.Heading : 0f,
                    Scale = desired.Scaled ? desired.Scale : 1f,
                });
                if (ramp)
                    Add(ref plan, new GiCommand
                    {
                        Kind = GiCommandKind.Fade,
                        LayerKey = desired.Layer,
                        StampKey = desired.Stamp,
                        Gain = desired.Gain,
                        TargetGain = desired.Gain,
                        Ticks = desired.Ramp,
                        Target = index,
                        Owner = owner,
                    });
                return plan;
            }

            if (applied.Gain != desired.Gain)
                Add(ref plan, new GiCommand
                {
                    Kind = desired.Ramp > 0 ? GiCommandKind.Fade : GiCommandKind.SetGain,
                    LayerKey = desired.Layer,
                    StampKey = desired.Stamp,
                    Gain = desired.Gain,
                    TargetGain = desired.Gain,
                    Ticks = desired.Ramp,
                    Target = GiCommand.Existing,
                    Owner = owner,
                    Source = applied.Source,
                });

            return plan;
        }

        private static GiCommand Remove(in GiApplied applied, int owner)
        {
            return new GiCommand
            {
                Kind = GiCommandKind.Remove,
                LayerKey = applied.Layer,
                StampKey = applied.Stamp,
                Target = GiCommand.Existing,
                Owner = owner,
                Source = applied.Source,
            };
        }

        private static void Add(ref GiPlan plan, in GiCommand command)
        {
            if (plan.Count == 0) plan.First = command;
            else if (plan.Count == 1) plan.Second = command;
            else plan.Third = command;
            plan.Count++;
        }
    }
}
