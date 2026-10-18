using System.Globalization;
using System.Text;

namespace Gi.Entities.Tests.Support
{
    public sealed class GiScenarioTick
    {
        public readonly GiScenarioOp[] Ops;

        public GiScenarioTick(GiScenarioOp[] ops)
        {
            Ops = ops;
        }
    }

    public sealed class GiScenario
    {
        public readonly GiScenarioProfile Profile;
        public readonly GiScenarioTick[] Ticks;
        public readonly int EntityCount;

        public GiScenario(GiScenarioProfile profile, GiScenarioTick[] ticks)
        {
            Profile = profile;
            Ticks = ticks;
            var entities = 0;
            foreach (var tick in ticks)
            foreach (var op in tick.Ops)
                if (op.Kind == GiScenarioOpKind.Spawn && op.Entity >= entities)
                    entities = op.Entity + 1;
            EntityCount = entities;
        }

        public GiScenario WithOpOrderShuffled(ulong seed)
        {
            var rng = new GiRandom(seed, 7ul);
            var ticks = new GiScenarioTick[Ticks.Length];
            for (var i = 0; i < ticks.Length; i++)
            {
                var ops = (GiScenarioOp[])Ticks[i].Ops.Clone();
                rng.Shuffle(ops);
                ticks[i] = new GiScenarioTick(ops);
            }

            return new GiScenario(Profile, ticks);
        }

        public string ToText()
        {
            var text = new StringBuilder();
            text.Append("scenario v1\n");
            foreach (var grid in Profile.Grids)
                text.Append("grid power=").Append(Number(grid.Power)).Append(" x=").Append(Number(grid.X))
                    .Append(" y=").Append(Number(grid.Y)).Append(" size=").Append(Number(grid.Size)).Append('\n');

            for (var i = 0; i < Profile.Layers.Length; i++) AppendLayer(text, i, Profile.Layers[i]);
            for (var i = 0; i < Profile.Stamps.Length; i++) AppendStamp(text, i, Profile.Stamps[i]);
            for (var t = 0; t < Ticks.Length; t++)
            {
                text.Append("tick ").Append(Number(t)).Append('\n');
                foreach (var op in Ticks[t].Ops) AppendOp(text, op);
            }

            return text.ToString();
        }

        public static string Quarters(int quarters)
        {
            var magnitude = quarters < 0 ? -quarters : quarters;
            var fraction = (magnitude & 3) switch
            {
                0 => "00",
                1 => "25",
                2 => "50",
                _ => "75",
            };
            return (quarters < 0 ? "-" : "") + Number(magnitude >> 2) + "." + fraction;
        }

        private static string Number(int value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        private static void AppendLayer(StringBuilder text, int index, GiScenarioLayer layer)
        {
            text.Append("layer ").Append(Number(index)).Append(' ').Append(layer.Kind.ToString().ToLowerInvariant());
            if (layer.IsBase)
            {
                text.Append(" phase=").Append(Number(layer.PhaseBits)).Append(" turn=").Append(Number(layer.TurnBits));
            }
            else
            {
                text.Append(" first=").Append(Number(layer.First)).Append(" second=").Append(Number(layer.Second));
                if (layer.Kind == GiScenarioLayerKind.Sum)
                    text.Append(" weights=").Append(Number(layer.FirstWeight)).Append(',')
                        .Append(Number(layer.SecondWeight)).Append(" shift=").Append(Number(layer.Shift));
                if (layer.Kind == GiScenarioLayerKind.Mask)
                    text.Append(" range=").Append(Number(layer.MaskLow)).Append(',').Append(Number(layer.MaskHigh));
            }

            text.Append('\n');
        }

        private static void AppendStamp(StringBuilder text, int index, GiScenarioStamp stamp)
        {
            text.Append("stamp ").Append(Number(index)).Append(' ').Append(stamp.Shape.ToString().ToLowerInvariant());
            switch (stamp.Shape)
            {
                case GiScenarioShape.Disk:
                case GiScenarioShape.Cone:
                case GiScenarioShape.Dome:
                    text.Append(" radius=").Append(Number(stamp.Radius)).Append(" value=").Append(Number(stamp.Value))
                        .Append(" arc=").Append(Number(stamp.Arc));
                    break;
                case GiScenarioShape.Raster:
                    text.Append(" size=").Append(Number(stamp.Width)).Append('x').Append(Number(stamp.Height))
                        .Append(" cells=");
                    for (var i = 0; i < stamp.Cells.Length; i++)
                    {
                        if (i > 0) text.Append(',');
                        text.Append(Number(stamp.Cells[i]));
                    }

                    break;
                default:
                    text.Append(" size=").Append(Number(stamp.Width)).Append('x').Append(Number(stamp.Height))
                        .Append(" value=").Append(Number(stamp.Value));
                    break;
            }

            text.Append('\n');
        }

        private static void AppendOp(StringBuilder text, GiScenarioOp op)
        {
            text.Append("  ").Append(op.Kind.ToString().ToLowerInvariant());
            if (op.Entity >= 0) text.Append(" e=").Append(Number(op.Entity));
            switch (op.Kind)
            {
                case GiScenarioOpKind.Spawn:
                    text.Append(" layer=").Append(Number(op.Layer)).Append(" stamp=").Append(Number(op.Stamp))
                        .Append(" gain=").Append(Number(op.Gain)).Append(" at=").Append(Quarters(op.XQuarters))
                        .Append(',').Append(Quarters(op.YQuarters)).Append(" heading=").Append(Number(op.HeadingSteps))
                        .Append(" scale=").Append(Quarters(op.ScaleQuarters)).Append(" ramp=").Append(Number(op.Ticks))
                        .Append(" flags=").Append(op.Flags.ToString().Replace(", ", "|"));
                    break;
                case GiScenarioOpKind.Move:
                    text.Append(" to=").Append(Quarters(op.XQuarters)).Append(',').Append(Quarters(op.YQuarters));
                    break;
                case GiScenarioOpKind.Turn:
                    text.Append(" heading=").Append(Number(op.HeadingSteps));
                    break;
                case GiScenarioOpKind.Scale:
                    text.Append(" scale=").Append(Quarters(op.ScaleQuarters));
                    break;
                case GiScenarioOpKind.SetGain:
                    text.Append(" gain=").Append(Number(op.Gain));
                    break;
                case GiScenarioOpKind.Retarget:
                    text.Append(" layer=").Append(Number(op.Layer)).Append(" stamp=").Append(Number(op.Stamp));
                    break;
                case GiScenarioOpKind.Pulse:
                    text.Append(" layer=").Append(Number(op.Layer)).Append(" stamp=").Append(Number(op.Stamp))
                        .Append(" gain=").Append(Number(op.Gain)).Append(" at=").Append(Quarters(op.XQuarters))
                        .Append(',').Append(Quarters(op.YQuarters)).Append(" heading=").Append(Number(op.HeadingSteps))
                        .Append(" scale=").Append(Quarters(op.ScaleQuarters)).Append(" ticks=").Append(Number(op.Ticks))
                        .Append(" flags=").Append(op.Flags.ToString().Replace(", ", "|"));
                    break;
                case GiScenarioOpKind.Splat:
                    text.Append(" layer=").Append(Number(op.Layer)).Append(" stamp=").Append(Number(op.Stamp))
                        .Append(" gain=").Append(Number(op.Gain)).Append(" at=").Append(Quarters(op.XQuarters))
                        .Append(',').Append(Quarters(op.YQuarters));
                    break;
            }

            text.Append('\n');
        }
    }
}
