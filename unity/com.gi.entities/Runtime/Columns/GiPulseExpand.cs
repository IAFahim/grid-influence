namespace Gi.Entities
{
    public static class GiPulseExpand
    {
        public const int MaxCommands = 3;

        public static int Count(in GiPulse pulse)
        {
            return pulse.FadeOut ? 3 : 2;
        }

        public static GiPlan Plan(in GiPulse pulse, in GiTables tables, int firstIndex)
        {
            var plan = default(GiPlan);
            if (!tables.TryLayer(pulse.Layer, out var layer))
            {
                plan.Fault = GiFaultCode.InvalidLayer;
                return plan;
            }

            if (!tables.TryStamp(pulse.Stamp, out var stamp))
            {
                plan.Fault = GiFaultCode.InvalidStamp;
                return plan;
            }

            plan.First = new GiCommand
            {
                Kind = GiCommandKind.Place,
                LayerKey = pulse.Layer,
                StampKey = pulse.Stamp,
                Layer = layer,
                Stamp = stamp,
                Gain = pulse.Gain,
                TargetGain = pulse.Gain,
                Target = GiCommand.Existing,
                Owner = GiCommand.NoOwner,
                X = pulse.X,
                Y = pulse.Y,
                Heading = pulse.Heading,
                Scale = pulse.Scale,
            };
            plan.Count = 1;
            if (pulse.FadeOut)
            {
                plan.Second = new GiCommand
                {
                    Kind = GiCommandKind.Fade,
                    Gain = 0,
                    Ticks = pulse.Ticks,
                    Target = firstIndex,
                    Owner = GiCommand.NoOwner,
                };
                plan.Third = new GiCommand
                {
                    Kind = GiCommandKind.Expire,
                    Ticks = pulse.Ticks,
                    Target = firstIndex,
                    Owner = GiCommand.NoOwner,
                };
                plan.Count = 3;
                return plan;
            }

            plan.Second = new GiCommand
            {
                Kind = GiCommandKind.Expire,
                Ticks = pulse.Ticks,
                Target = firstIndex,
                Owner = GiCommand.NoOwner,
            };
            plan.Count = 2;
            return plan;
        }
    }
}
