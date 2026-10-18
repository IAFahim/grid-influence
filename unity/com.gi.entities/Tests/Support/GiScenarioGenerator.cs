using System.Collections.Generic;

namespace Gi.Entities.Tests.Support
{
    public readonly struct GiScenarioOptions
    {
        public readonly int Ticks;
        public readonly int InitialEntities;
        public readonly int MaxEntities;
        public readonly int SplatsPerTick;
        public readonly bool Pulses;
        public readonly bool Ramps;
        public readonly bool Transforms;

        public GiScenarioOptions(int ticks, int initialEntities, int maxEntities, int splatsPerTick, bool pulses, bool ramps,
            bool transforms = true)
        {
            Transforms = transforms;
            Ticks = ticks;
            InitialEntities = initialEntities;
            MaxEntities = maxEntities;
            SplatsPerTick = splatsPerTick;
            Pulses = pulses;
            Ramps = ramps;
        }

        public static GiScenarioOptions Standard => new GiScenarioOptions(8, 24, 64, 8, true, true);

        public GiScenarioOptions WithoutTransforms()
        {
            return new GiScenarioOptions(Ticks, InitialEntities, MaxEntities, SplatsPerTick, Pulses, Ramps, false);
        }

        public GiScenarioOptions WithoutSplats()
        {
            return new GiScenarioOptions(Ticks, InitialEntities, MaxEntities, 0, Pulses, Ramps, Transforms);
        }
    }

    public static class GiScenarioGenerator
    {
        private const int MinQuarters = 8 * 4;
        private const int MaxQuarters = 56 * 4;
        private static readonly int[] ScaleChoices = { 2, 3, 4, 5, 6, 8 };
        private static readonly int[] SplatStamps = { 0, 4 };

        private sealed class EntityState
        {
            public bool Alive = true;
            public bool Enabled;
            public int Layer;
            public int Stamp;
            public int Gain;
            public int XQuarters;
            public int YQuarters;
            public int HeadingSteps;
            public int ScaleQuarters;
            public bool Oriented;
            public bool Scaled;
        }

        public static GiScenario Create(ulong seed, GiScenarioOptions options)
        {
            var profile = GiScenarioProfile.Standard();
            var rng = new GiRandom(seed);
            var persistent = LayersOf(profile, GiScenarioLayerKind.Persistent);
            var transient = LayersOf(profile, GiScenarioLayerKind.Transient);
            var entities = new List<EntityState>();
            var ticks = new GiScenarioTick[options.Ticks];
            for (var t = 0; t < ticks.Length; t++)
            {
                var ops = new List<GiScenarioOp>();
                if (t > 0) MutateEntities(ref rng, profile, persistent, entities, ops, options.Transforms);

                var spawns = t == 0 ? options.InitialEntities : rng.Next(3);
                for (var i = 0; i < spawns && entities.Count < options.MaxEntities; i++)
                    ops.Add(Spawn(ref rng, profile, persistent, options, entities));

                if (options.Pulses && rng.Chance(1, 2))
                {
                    var pulses = rng.Next(3);
                    for (var i = 0; i < pulses; i++) ops.Add(Pulse(ref rng, profile, persistent));
                }

                for (var i = 0; i < options.SplatsPerTick; i++)
                    ops.Add(GiScenarioOp.Splat(transient[rng.Next(transient.Length)],
                        SplatStamps[rng.Next(SplatStamps.Length)], rng.Range(-4, 5),
                        rng.Range(4 * 4, 60 * 4), rng.Range(4 * 4, 60 * 4)));

                ticks[t] = new GiScenarioTick(ops.ToArray());
            }

            return new GiScenario(profile, ticks);
        }

        private static int[] LayersOf(GiScenarioProfile profile, GiScenarioLayerKind kind)
        {
            var layers = new List<int>();
            for (var i = 0; i < profile.Layers.Length; i++)
                if (profile.Layers[i].Kind == kind)
                    layers.Add(i);
            return layers.ToArray();
        }

        private static int NonZeroGain(ref GiRandom rng)
        {
            var gain = rng.Range(-16, 17);
            return gain == 0 ? 4 : gain;
        }

        private static GiScenarioOp Spawn(ref GiRandom rng, GiScenarioProfile profile, int[] persistent,
            GiScenarioOptions options, List<EntityState> entities)
        {
            var state = new EntityState
            {
                Layer = persistent[rng.Next(persistent.Length)],
                Stamp = rng.Next(profile.Stamps.Length),
                Gain = NonZeroGain(ref rng),
                XQuarters = rng.Range(MinQuarters, MaxQuarters),
                YQuarters = rng.Range(MinQuarters, MaxQuarters),
                HeadingSteps = rng.Next(16),
                ScaleQuarters = ScaleChoices[rng.Next(ScaleChoices.Length)],
                Oriented = rng.Chance(1, 2),
                Scaled = rng.Chance(1, 3),
                Enabled = !rng.Chance(1, 10),
            };
            var ramp = options.Ramps && rng.Chance(3, 10) ? rng.Range(2, 5) : 0;
            var flags = GiScenarioFlags.None;
            if (state.Oriented) flags |= GiScenarioFlags.Oriented;
            if (state.Scaled) flags |= GiScenarioFlags.Scaled;
            if (!state.Enabled) flags |= GiScenarioFlags.Disabled;
            entities.Add(state);
            return GiScenarioOp.Spawn(entities.Count - 1, state.Layer, state.Stamp, state.Gain, state.XQuarters,
                state.YQuarters, state.HeadingSteps, state.ScaleQuarters, ramp, flags);
        }

        private static GiScenarioOp Pulse(ref GiRandom rng, GiScenarioProfile profile, int[] persistent)
        {
            return GiScenarioOp.Pulse(persistent[rng.Next(persistent.Length)], rng.Next(profile.Stamps.Length),
                NonZeroGain(ref rng), rng.Range(MinQuarters, MaxQuarters), rng.Range(MinQuarters, MaxQuarters),
                rng.Next(16), ScaleChoices[rng.Next(ScaleChoices.Length)], rng.Range(2, 6), rng.Chance(1, 2));
        }

        private static void MutateEntities(ref GiRandom rng, GiScenarioProfile profile, int[] persistent,
            List<EntityState> entities, List<GiScenarioOp> ops, bool transforms)
        {
            for (var e = 0; e < entities.Count; e++)
            {
                var state = entities[e];
                if (!state.Alive) continue;
                if (rng.Chance(4, 100))
                {
                    state.Alive = false;
                    ops.Add(GiScenarioOp.Despawn(e));
                    continue;
                }

                if (state.Enabled && rng.Chance(3, 100))
                {
                    state.Enabled = false;
                    ops.Add(GiScenarioOp.Disable(e));
                }
                else if (!state.Enabled && rng.Chance(30, 100))
                {
                    state.Enabled = true;
                    ops.Add(GiScenarioOp.Enable(e));
                }

                if (transforms && rng.Chance(25, 100))
                {
                    state.XQuarters = Moved(ref rng, state.XQuarters);
                    state.YQuarters = Moved(ref rng, state.YQuarters);
                    ops.Add(GiScenarioOp.Move(e, state.XQuarters, state.YQuarters));
                }

                if (transforms && state.Oriented && rng.Chance(10, 100))
                {
                    state.HeadingSteps = rng.Next(16);
                    ops.Add(GiScenarioOp.Turn(e, state.HeadingSteps));
                }

                if (transforms && state.Scaled && rng.Chance(8, 100))
                {
                    state.ScaleQuarters = ScaleChoices[rng.Next(ScaleChoices.Length)];
                    ops.Add(GiScenarioOp.ScaleTo(e, state.ScaleQuarters));
                }

                if (rng.Chance(10, 100))
                {
                    var gain = NonZeroGain(ref rng);
                    if (gain != state.Gain)
                    {
                        state.Gain = gain;
                        ops.Add(GiScenarioOp.SetGain(e, gain));
                    }
                }

                if (rng.Chance(5, 100))
                {
                    var layer = persistent[rng.Next(persistent.Length)];
                    var stamp = rng.Next(profile.Stamps.Length);
                    if (layer != state.Layer || stamp != state.Stamp)
                    {
                        state.Layer = layer;
                        state.Stamp = stamp;
                        ops.Add(GiScenarioOp.Retarget(e, layer, stamp));
                    }
                }
            }
        }

        private static int Moved(ref GiRandom rng, int quarters)
        {
            var next = rng.Chance(1, 10) ? rng.Range(MinQuarters, MaxQuarters) : quarters + rng.Range(-12, 13);
            if (next < MinQuarters) return MinQuarters;
            return next > MaxQuarters ? MaxQuarters : next;
        }
    }
}
