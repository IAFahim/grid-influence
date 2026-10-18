using System;
using System.Collections.Generic;
using GiWorld = Gi.World;

namespace Gi.Entities.Tests.Support
{
    public static class GiColumnsExecutor
    {
        public static GiScenarioHashes Run(GiScenario scenario)
        {
            return Run(scenario, GiApplyPass.BatchLimit);
        }

        public static GiScenarioHashes Run(GiScenario scenario, int batchLimit)
        {
            using (var setup = GiScenarioWorld.Create(scenario.Profile))
            {
                var tables = TablesOf(setup);
                var token = GiWorld.Capture(setup.World);
                var desired = new GiDesired[scenario.EntityCount];
                var exists = new bool[scenario.EntityCount];
                var enabled = new bool[scenario.EntityCount];
                var applied = new GiApplied[scenario.EntityCount];
                var perTick = new ulong[scenario.Ticks.Length][];
                for (var t = 0; t < scenario.Ticks.Length; t++)
                {
                    var pulses = new List<GiPulse>();
                    var splats = new List<GiScenarioOp>();
                    foreach (var op in scenario.Ticks[t].Ops) Fold(op, desired, exists, enabled, pulses, splats);
                    for (var e = 0; e < desired.Length; e++) desired[e].Want = exists[e] && enabled[e];

                    var commands = new List<GiCommand>();
                    for (var e = 0; e < desired.Length; e++)
                        AppendPlan(commands, GiDiff.Plan(desired[e], applied[e], tables, e, commands.Count));

                    pulses.Sort(new GiPulseOrder());
                    foreach (var pulse in pulses) AppendPlan(commands, GiPulseExpand.Plan(pulse, tables, commands.Count));

                    var results = new GiCommandResult[commands.Count];
                    var status = GiApplyPass.Apply(ref token, setup.World, commands.ToArray(), results,
                        new GiWorld.SourceCommand[commands.Count], new GiWorld.SourceResult[commands.Count], batchLimit);
                    if (status != GiApplyStatus.Applied) throw new InvalidOperationException("apply " + status);

                    for (var i = 0; i < commands.Count; i++)
                    {
                        var owner = commands[i].Owner;
                        if (owner < 0) continue;
                        var fault = GiWriteBack.Fold(commands[i], results[i], ref applied[owner]);
                        if (fault != GiFaultCode.None) throw new InvalidOperationException(commands[i].Kind + " " + fault);
                    }

                    EmitSplats(setup, scenario.Profile, splats);
                    GiWorld.Process(setup.World);
                    var hashes = new ulong[setup.Layers.Length];
                    for (var l = 0; l < hashes.Length; l++) hashes[l] = GiFieldHash.OfLayer(setup.World, setup.Layers[l]);
                    perTick[t] = hashes;
                }

                return new GiScenarioHashes(perTick);
            }
        }

        public static GiTables TablesOf(GiScenarioWorld setup)
        {
            var tables = default(GiTables);
            foreach (var grid in setup.Grids) tables.AddGrid(grid);
            foreach (var layer in setup.Layers) tables.AddLayer(layer);
            foreach (var stamp in setup.Stamps) tables.AddStamp(stamp);
            return tables;
        }

        private static void AppendPlan(List<GiCommand> commands, GiPlan plan)
        {
            for (var i = 0; i < plan.Count; i++) commands.Add(plan[i]);
        }

        private static void Fold(GiScenarioOp op, GiDesired[] desired, bool[] exists, bool[] enabled, List<GiPulse> pulses,
            List<GiScenarioOp> splats)
        {
            switch (op.Kind)
            {
                case GiScenarioOpKind.Spawn:
                    exists[op.Entity] = true;
                    enabled[op.Entity] = (op.Flags & GiScenarioFlags.Disabled) == 0;
                    desired[op.Entity] = new GiDesired
                    {
                        Layer = new GiLayerKey((byte)op.Layer),
                        Stamp = new GiStampKey((byte)op.Stamp),
                        Gain = (sbyte)op.Gain,
                        Ramp = (ushort)op.Ticks,
                        Oriented = (op.Flags & GiScenarioFlags.Oriented) != 0,
                        Scaled = (op.Flags & GiScenarioFlags.Scaled) != 0,
                        X = op.X,
                        Y = op.Y,
                        Heading = op.Heading,
                        Scale = op.Scale,
                    };
                    break;
                case GiScenarioOpKind.Despawn:
                    exists[op.Entity] = false;
                    break;
                case GiScenarioOpKind.Disable:
                    enabled[op.Entity] = false;
                    break;
                case GiScenarioOpKind.Enable:
                    enabled[op.Entity] = true;
                    break;
                case GiScenarioOpKind.Move:
                    desired[op.Entity].X = op.X;
                    desired[op.Entity].Y = op.Y;
                    break;
                case GiScenarioOpKind.Turn:
                    desired[op.Entity].Heading = op.Heading;
                    break;
                case GiScenarioOpKind.Scale:
                    desired[op.Entity].Scale = op.Scale;
                    break;
                case GiScenarioOpKind.SetGain:
                    desired[op.Entity].Gain = (sbyte)op.Gain;
                    break;
                case GiScenarioOpKind.Retarget:
                    desired[op.Entity].Layer = new GiLayerKey((byte)op.Layer);
                    desired[op.Entity].Stamp = new GiStampKey((byte)op.Stamp);
                    break;
                case GiScenarioOpKind.Pulse:
                    pulses.Add(new GiPulse
                    {
                        Layer = new GiLayerKey((byte)op.Layer),
                        Stamp = new GiStampKey((byte)op.Stamp),
                        X = op.X,
                        Y = op.Y,
                        Heading = op.Heading,
                        Scale = op.Scale,
                        Gain = (sbyte)op.Gain,
                        Ticks = (ushort)op.Ticks,
                        FadeOut = (op.Flags & GiScenarioFlags.FadeOut) != 0,
                    });
                    break;
                case GiScenarioOpKind.Splat:
                    splats.Add(op);
                    break;
            }
        }

        private static void EmitSplats(GiScenarioWorld setup, GiScenarioProfile profile, List<GiScenarioOp> splats)
        {
            for (var layer = 0; layer < profile.Layers.Length; layer++)
            {
                var xs = new List<float>();
                var ys = new List<float>();
                var stamps = new List<byte>();
                var gains = new List<sbyte>();
                foreach (var splat in splats)
                {
                    if (splat.Layer != layer) continue;
                    xs.Add(splat.X);
                    ys.Add(splat.Y);
                    stamps.Add(setup.Stamps[splat.Stamp]);
                    gains.Add((sbyte)splat.Gain);
                }

                if (xs.Count == 0) continue;
                GiWorld.Splat(setup.World, setup.Layers[layer], xs.ToArray(), ys.ToArray(), stamps.ToArray(), gains.ToArray());
            }
        }
    }
}
