using System;
using System.Collections.Generic;
using GiWorld = Gi.World;

namespace Gi.Entities.Tests.Support
{
    public static unsafe class GiColumnsExecutor
    {
        public static GiScenarioHashes Run(GiScenario scenario)
        {
            return Run(scenario, GiApplyPass.BatchLimit);
        }

        public static GiScenarioHashes Run(GiScenario scenario, int batchLimit)
        {
            using (var session = new GiColumnsSession(scenario, batchLimit))
            {
                var perTick = new ulong[scenario.Ticks.Length][];
                for (var t = 0; t < scenario.Ticks.Length; t++) perTick[t] = session.Step(t);
                return new GiScenarioHashes(perTick);
            }
        }

        internal static int Sync(byte world, ref GiWorld.Token token, GiDesired[] desired, GiApplied[] applied, GiRecorder recorder)
        {
            var counts = new int[GiSyncPass.Categories];
            for (var e = 0; e < desired.Length; e++)
                if (applied[e].Placed && desired[e].Want)
                    counts[GiSyncPass.Category(desired[e].Oriented, desired[e].Scaled)]++;

            var starts = new int[GiSyncPass.Categories];
            var wordStarts = new int[GiSyncPass.Categories];
            var total = 0;
            var words = 0;
            for (var k = 0; k < GiSyncPass.Categories; k++)
            {
                starts[k] = total;
                wordStarts[k] = words;
                total += counts[k];
                words += GiSyncPass.Words(counts[k]);
            }

            if (total == 0) return 0;

            var sources = new GiWorld.SourceId[total];
            var xs = new float[total];
            var ys = new float[total];
            var headings = new float[total];
            var scales = new float[total];
            var invalid = new ulong[words];
            var cursor = (int[])starts.Clone();
            for (var e = 0; e < desired.Length; e++)
            {
                if (!applied[e].Placed || !desired[e].Want) continue;

                var row = cursor[GiSyncPass.Category(desired[e].Oriented, desired[e].Scaled)]++;
                sources[row] = applied[e].Source;
                xs[row] = desired[e].X;
                ys[row] = desired[e].Y;
                headings[row] = desired[e].Heading;
                scales[row] = desired[e].Scale;
            }

            fixed (GiWorld.SourceId* sourcePointer = sources)
            fixed (float* xPointer = xs, yPointer = ys, headingPointer = headings, scalePointer = scales)
            fixed (ulong* invalidPointer = invalid)
            fixed (int* startPointer = starts, countPointer = counts, wordPointer = wordStarts)
            {
                recorder?.OnSync(sourcePointer, xPointer, yPointer, headingPointer, scalePointer, startPointer, countPointer);
                var status = GiSyncPass.Apply(ref token, world, sourcePointer, xPointer, yPointer, headingPointer, scalePointer,
                    invalidPointer, startPointer, countPointer, wordPointer);
                if (status != GiApplyStatus.Applied) throw new InvalidOperationException("sync " + status);

                var rejected = 0;
                for (var k = 0; k < GiSyncPass.Categories; k++)
                for (var i = 0; i < counts[k]; i++)
                    if (GiSyncPass.Rejected(invalidPointer, wordStarts[k], i))
                        rejected++;
                return rejected;
            }
        }

        public static GiTables TablesOf(GiScenarioWorld setup, GiScenarioProfile profile)
        {
            var tables = default(GiTables);
            foreach (var grid in setup.Grids) tables.AddGrid(grid);
            for (var l = 0; l < setup.Layers.Length; l++) tables.AddLayer(setup.Layers[l], profile.Layers[l].Kind == GiScenarioLayerKind.Transient);
            foreach (var stamp in setup.Stamps) tables.AddStamp(stamp);
            return tables;
        }

        internal static void AppendPlan(List<GiCommand> commands, GiPlan plan)
        {
            for (var i = 0; i < plan.Count; i++) commands.Add(plan[i]);
        }

        internal static void Fold(GiScenarioOp op, GiDesired[] desired, bool[] exists, bool[] enabled, List<GiPulse> pulses,
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

        internal static void EmitSplats(GiScenarioWorld setup, GiTables tables, List<GiScenarioOp> splats, GiRecorder recorder)
        {
            var counts = new int[GiSplatPass.Layers];
            var valid = new List<GiScenarioOp>();
            foreach (var splat in splats)
            {
                if (GiSplatPass.Check(tables, new GiLayerKey((byte)splat.Layer), new GiStampKey((byte)splat.Stamp), out _) != GiFaultCode.None)
                    throw new InvalidOperationException("splat layer " + splat.Layer);
                valid.Add(splat);
                counts[splat.Layer]++;
            }

            var starts = new int[GiSplatPass.Layers];
            var total = 0;
            for (var k = 0; k < GiSplatPass.Layers; k++)
            {
                starts[k] = total;
                total += counts[k];
            }

            if (total == 0) return;

            var xs = new float[total];
            var ys = new float[total];
            var stamps = new byte[total];
            var gains = new sbyte[total];
            var cursor = (int[])starts.Clone();
            foreach (var splat in valid)
            {
                var row = cursor[splat.Layer]++;
                xs[row] = splat.X;
                ys[row] = splat.Y;
                stamps[row] = setup.Stamps[splat.Stamp];
                gains[row] = (sbyte)splat.Gain;
            }

            fixed (float* x = xs, y = ys)
            fixed (byte* st = stamps)
            fixed (sbyte* g = gains)
            fixed (int* s = starts, c = counts)
            {
                recorder?.OnSplat(s, c, x, y, st, g);
                var applied = GiSplatPass.Apply(setup.World, tables, s, c, x, y, st, g);
                if (applied != total) throw new InvalidOperationException("splat accepted " + applied + " of " + total);
            }
        }
    }
}
