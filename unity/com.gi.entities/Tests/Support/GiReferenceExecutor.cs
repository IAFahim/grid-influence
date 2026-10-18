using System;
using System.Collections.Generic;
using System.Globalization;
using GiWorld = Gi.World;

namespace Gi.Entities.Tests.Support
{
    public static class GiReferenceExecutor
    {
        private sealed class Entity
        {
            public bool Exists;
            public bool Enabled;
            public int Layer;
            public int Stamp;
            public int Gain;
            public int Ramp;
            public int XQuarters;
            public int YQuarters;
            public int HeadingSteps;
            public int ScaleQuarters;
            public bool Oriented;
            public bool Scaled;

            public bool Placed;
            public GiWorld.SourceId Source;
            public int PlacedLayer;
            public int PlacedStamp;
            public int PlacedGain;
            public int PlacedXQuarters;
            public int PlacedYQuarters;
            public int PlacedHeadingSteps;
            public int PlacedScaleQuarters;
        }

        public static GiScenarioHashes Run(GiScenario scenario)
        {
            using (var setup = GiScenarioWorld.Create(scenario.Profile))
            {
                var entities = new Entity[scenario.EntityCount];
                for (var e = 0; e < entities.Length; e++) entities[e] = new Entity();
                var perTick = new ulong[scenario.Ticks.Length][];
                for (var t = 0; t < scenario.Ticks.Length; t++)
                {
                    var pulses = new List<GiScenarioOp>();
                    var splats = new List<GiScenarioOp>();
                    foreach (var op in scenario.Ticks[t].Ops) Apply(op, entities, pulses, splats);

                    foreach (var entity in entities) Reconcile(setup, entity);

                    foreach (var pulse in pulses) EmitPulse(setup, pulse);
                    EmitSplats(setup, scenario.Profile, splats);
                    GiWorld.Process(setup.World);

                    var hashes = new ulong[setup.Layers.Length];
                    for (var l = 0; l < hashes.Length; l++) hashes[l] = GiFieldHash.OfLayer(setup.World, setup.Layers[l]);
                    perTick[t] = hashes;
                }

                return new GiScenarioHashes(perTick);
            }
        }

        private static void Apply(GiScenarioOp op, Entity[] entities, List<GiScenarioOp> pulses, List<GiScenarioOp> splats)
        {
            switch (op.Kind)
            {
                case GiScenarioOpKind.Spawn:
                    var spawned = entities[op.Entity];
                    spawned.Exists = true;
                    spawned.Enabled = (op.Flags & GiScenarioFlags.Disabled) == 0;
                    spawned.Layer = op.Layer;
                    spawned.Stamp = op.Stamp;
                    spawned.Gain = op.Gain;
                    spawned.Ramp = op.Ticks;
                    spawned.XQuarters = op.XQuarters;
                    spawned.YQuarters = op.YQuarters;
                    spawned.HeadingSteps = op.HeadingSteps;
                    spawned.ScaleQuarters = op.ScaleQuarters;
                    spawned.Oriented = (op.Flags & GiScenarioFlags.Oriented) != 0;
                    spawned.Scaled = (op.Flags & GiScenarioFlags.Scaled) != 0;
                    break;
                case GiScenarioOpKind.Despawn:
                    entities[op.Entity].Exists = false;
                    break;
                case GiScenarioOpKind.Disable:
                    entities[op.Entity].Enabled = false;
                    break;
                case GiScenarioOpKind.Enable:
                    entities[op.Entity].Enabled = true;
                    break;
                case GiScenarioOpKind.Move:
                    entities[op.Entity].XQuarters = op.XQuarters;
                    entities[op.Entity].YQuarters = op.YQuarters;
                    break;
                case GiScenarioOpKind.Turn:
                    entities[op.Entity].HeadingSteps = op.HeadingSteps;
                    break;
                case GiScenarioOpKind.Scale:
                    entities[op.Entity].ScaleQuarters = op.ScaleQuarters;
                    break;
                case GiScenarioOpKind.SetGain:
                    entities[op.Entity].Gain = op.Gain;
                    break;
                case GiScenarioOpKind.Retarget:
                    entities[op.Entity].Layer = op.Layer;
                    entities[op.Entity].Stamp = op.Stamp;
                    break;
                case GiScenarioOpKind.Pulse:
                    pulses.Add(op);
                    break;
                case GiScenarioOpKind.Splat:
                    splats.Add(op);
                    break;
            }
        }

        private static void Reconcile(GiScenarioWorld setup, Entity entity)
        {
            var world = setup.World;
            if (!entity.Exists || !entity.Enabled)
            {
                if (entity.Placed)
                {
                    Expect(GiWorld.Remove(world, entity.Source), "remove");
                    entity.Placed = false;
                }

                return;
            }

            if (entity.Placed && (entity.PlacedLayer != entity.Layer || entity.PlacedStamp != entity.Stamp))
            {
                Expect(GiWorld.Remove(world, entity.Source), "retarget remove");
                entity.Placed = false;
            }

            if (!entity.Placed)
            {
                PlaceEntity(setup, entity);
                return;
            }

            if (entity.PlacedGain != entity.Gain)
            {
                Expect(
                    entity.Ramp > 0
                        ? GiWorld.Fade(world, entity.Source, (sbyte)entity.Gain, entity.Ramp)
                        : GiWorld.SetGain(world, entity.Source, (sbyte)entity.Gain),
                    "gain");
                entity.PlacedGain = entity.Gain;
            }

            if (entity.PlacedXQuarters != entity.XQuarters || entity.PlacedYQuarters != entity.YQuarters)
            {
                Expect(GiWorld.Move(world, entity.Source, entity.XQuarters * 0.25f, entity.YQuarters * 0.25f), "move");
                entity.PlacedXQuarters = entity.XQuarters;
                entity.PlacedYQuarters = entity.YQuarters;
            }

            if (entity.Oriented && entity.PlacedHeadingSteps != entity.HeadingSteps)
            {
                Expect(GiWorld.Turn(world, entity.Source, entity.HeadingSteps * GiScenarioOp.HeadingStepRadians), "turn");
                entity.PlacedHeadingSteps = entity.HeadingSteps;
            }

            if (entity.Scaled && entity.PlacedScaleQuarters != entity.ScaleQuarters)
            {
                Expect(GiWorld.Scale(world, entity.Source, entity.ScaleQuarters * 0.25f), "scale");
                entity.PlacedScaleQuarters = entity.ScaleQuarters;
            }
        }

        private static void PlaceEntity(GiScenarioWorld setup, Entity entity)
        {
            var world = setup.World;
            var start = entity.Ramp > 0 ? 0 : entity.Gain;
            entity.Source = GiWorld.Place(world, setup.Layers[entity.Layer], entity.XQuarters * 0.25f,
                entity.YQuarters * 0.25f, setup.Stamps[entity.Stamp], (sbyte)start, out var status);
            Expect(status, "place");
            if (entity.Oriented && entity.HeadingSteps != 0)
                Expect(GiWorld.Turn(world, entity.Source, entity.HeadingSteps * GiScenarioOp.HeadingStepRadians), "turn");
            if (entity.Scaled && entity.ScaleQuarters != 4)
                Expect(GiWorld.Scale(world, entity.Source, entity.ScaleQuarters * 0.25f), "scale");
            if (entity.Ramp > 0)
                Expect(GiWorld.Fade(world, entity.Source, (sbyte)entity.Gain, entity.Ramp), "fade in");

            entity.Placed = true;
            entity.PlacedLayer = entity.Layer;
            entity.PlacedStamp = entity.Stamp;
            entity.PlacedGain = entity.Gain;
            entity.PlacedXQuarters = entity.XQuarters;
            entity.PlacedYQuarters = entity.YQuarters;
            entity.PlacedHeadingSteps = entity.Oriented ? entity.HeadingSteps : 0;
            entity.PlacedScaleQuarters = entity.Scaled ? entity.ScaleQuarters : 4;
        }

        private static void EmitPulse(GiScenarioWorld setup, GiScenarioOp pulse)
        {
            var world = setup.World;
            var source = GiWorld.Place(world, setup.Layers[pulse.Layer], pulse.X, pulse.Y, setup.Stamps[pulse.Stamp],
                (sbyte)pulse.Gain, out var status);
            Expect(status, "pulse place");
            if (pulse.HeadingSteps != 0) Expect(GiWorld.Turn(world, source, pulse.Heading), "pulse turn");
            if (pulse.ScaleQuarters != 4) Expect(GiWorld.Scale(world, source, pulse.Scale), "pulse scale");
            if ((pulse.Flags & GiScenarioFlags.FadeOut) != 0) Expect(GiWorld.Fade(world, source, 0, pulse.Ticks), "pulse fade");
            Expect(GiWorld.Expire(world, source, pulse.Ticks), "pulse expire");
        }

        private static void EmitSplats(GiScenarioWorld setup, GiScenarioProfile profile, List<GiScenarioOp> splats)
        {
            for (var layer = 0; layer < profile.Layers.Length; layer++)
            {
                var count = 0;
                foreach (var splat in splats)
                    if (splat.Layer == layer)
                        count++;
                if (count == 0) continue;

                var xs = new float[count];
                var ys = new float[count];
                var stamps = new byte[count];
                var gains = new sbyte[count];
                var i = 0;
                foreach (var splat in splats)
                {
                    if (splat.Layer != layer) continue;
                    xs[i] = splat.X;
                    ys[i] = splat.Y;
                    stamps[i] = setup.Stamps[splat.Stamp];
                    gains[i] = (sbyte)splat.Gain;
                    i++;
                }

                var accepted = GiWorld.Splat(setup.World, setup.Layers[layer], xs, ys, stamps, gains);
                if (accepted != count)
                    throw new InvalidOperationException("splat accepted " + accepted.ToString(CultureInfo.InvariantCulture) +
                                                        " of " + count.ToString(CultureInfo.InvariantCulture));
            }
        }

        private static void Expect(GiWorld.WriteStatus status, string what)
        {
            if (status != GiWorld.WriteStatus.Applied)
                throw new InvalidOperationException(what + ": " + status);
        }
    }
}
