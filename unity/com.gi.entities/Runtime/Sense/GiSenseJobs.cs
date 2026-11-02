using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using GiWorld = Gi.World;

namespace Gi.Entities
{
    [BurstCompile]
    internal struct GiSenseBeginJob : IJob
    {
        public Entity Stats;
        public ComponentLookup<GiStats> Lookup;
        public Entity Frame;
        [ReadOnly] public ComponentLookup<GiFrame> Frames;
        public NativeReference<GiReconcileContext> Context;

        public void Execute()
        {
            Context.Value = GiReconcileContext.Of(Frames[Frame]);
            var stats = Lookup[Stats];
            stats.Sensed = 0;
            Lookup[Stats] = stats;
        }
    }

    [BurstCompile]
    internal struct GiSenseCountJob : IJobChunk
    {
        public GiSenseView View;
        public int Period;
        public uint LastVersion;
        public byte WatchSources;
        [ReadOnly] public NativeReference<GiReconcileContext> Context;
        [ReadOnly] public NativeArray<DynamicComponentTypeHandle> Inputs;
        [NativeDisableParallelForRestriction] public NativeArray<int> Counts;
        [NativeDisableParallelForRestriction] public NativeArray<int> Moved;

        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
        {
            Moved[unfilteredChunkIndex] = 0;
            var context = Context.Value;
            if (!context.Alive || !View.Eligible(chunk)) return;

            var useMatrix = View.UsesMatrix(chunk);
            if (useMatrix ? !chunk.Has(ref View.Matrices) || chunk.DidChange(ref View.Matrices, LastVersion) :
                chunk.DidChange(ref View.Transforms, LastVersion))
            {
                Moved[unfilteredChunkIndex] = 1;
            }
            else if (WatchSources != 0 && (!chunk.Has(ref View.Sources) || chunk.DidChange(ref View.Sources, LastVersion)))
            {
                Moved[unfilteredChunkIndex] = 1;
            }
            else if (chunk.DidOrderChange(LastVersion))
            {
                Moved[unfilteredChunkIndex] = 1;
            }
            else
            {
                for (var i = 0; i < Inputs.Length; i++)
                {
                    var handle = Inputs[i];
                    if (chunk.DidChange(ref handle, LastVersion))
                    {
                        Moved[unfilteredChunkIndex] = 1;
                        break;
                    }
                }
            }

            var entities = View.EntitiesOf(chunk);
            var rows = View.Rows(chunk, useEnabledMask, chunkEnabledMask);
            var count = 0;
            while (rows.NextEntityIndex(out var i))
                if (GiSensePass.Due(entities[i].Index, Period, context.Tick))
                    count++;
            Counts[unfilteredChunkIndex] = count;
        }
    }

    [BurstCompile]
    internal struct GiSensePrefixJob : IJob
    {
        public Entity Stats;
        public ComponentLookup<GiStats> Lookup;
        public byte Needs;
        public GiLayerKey Layer;
        public NativeReference<GiSenseGateState> Gate;
        [ReadOnly] public NativeReference<GiReconcileContext> Context;
        [ReadOnly] public NativeArray<int> Counts;
        public NativeArray<int> Offsets;
        public NativeArray<int> Rows;
        public NativeList<float> Xs;
        public NativeList<float> Ys;
        public NativeList<float> Reaches;
        public NativeList<short> Thresholds;
        public NativeList<GiWorld.SourceId> Excludes;
        public NativeList<short> Values;
        public NativeList<float> GradientXs;
        public NativeList<float> GradientYs;
        public NativeList<long> Totals;
        public NativeList<ulong> Whole;
        public NativeList<float> PrevXs;
        public NativeList<float> PrevYs;
        public NativeList<float> PrevReach;

        public void Execute()
        {
            var total = 0;
            for (var c = 0; c < Counts.Length; c++)
            {
                Offsets[c] = total;
                total += Counts[c];
            }

            var needs = (GiSenseNeeds)Needs;
            var context = Context.Value;
            var valid = context.Alive && context.Tables.TryLayer(Layer, out _);
            var skip = Gate.Value.Skip != 0;
            Rows[0] = valid && !skip ? total : 0;
            Rows[1] = valid ? 1 : 0;
            Xs.ResizeUninitialized(skip ? 0 : total);
            Ys.ResizeUninitialized(skip ? 0 : total);
            Reaches.ResizeUninitialized(skip || (needs & GiSenseNeeds.Reach) == 0 ? 0 : total);
            Thresholds.ResizeUninitialized(skip || (needs & GiSenseNeeds.Threshold) == 0 ? 0 : total);
            Excludes.ResizeUninitialized(skip || (needs & GiSenseNeeds.Exclude) == 0 ? 0 : total);
            Values.ResizeUninitialized(skip || (needs & GiSenseNeeds.Value) == 0 ? 0 : total);
            GradientXs.ResizeUninitialized(skip || (needs & GiSenseNeeds.Vector) == 0 ? 0 : total);
            GradientYs.ResizeUninitialized(skip || (needs & GiSenseNeeds.Vector) == 0 ? 0 : total);
            Totals.ResizeUninitialized(skip || (needs & GiSenseNeeds.Total) == 0 ? 0 : total);
            Whole.ResizeUninitialized(skip ? 0 : GiSensePass.Words(total));
            PrevXs.ResizeUninitialized(total);
            PrevYs.ResizeUninitialized(total);
            PrevReach.ResizeUninitialized((needs & GiSenseNeeds.Reach) != 0 ? total : 0);

            var stats = Lookup[Stats];
            stats.Sensed += valid ? total : 0;
            Lookup[Stats] = stats;
        }
    }

    internal struct GiSenseFusedType
    {
        public DynamicComponentTypeHandle Handle;
        public int Size;
        public GiSenseField Value;
        public GiSenseField Gradient;
        public GiSenseField Total;
        public GiSenseField Position;
        public GiSenseField Coverage;
        public GiSenseField Reach;
        public GiSenseField Threshold;
    }

    [BurstCompile]
    internal unsafe struct GiSenseGatherJob : IJobChunk
    {
        public GiSenseView View;
        public int Period;
        public float Reach;
        public short Threshold;
        [ReadOnly] public NativeReference<GiReconcileContext> Context;
        [ReadOnly] public NativeReference<GiSenseGateState> Gate;
        [ReadOnly] public NativeArray<int> Offsets;
        [ReadOnly] public NativeArray<GiSenseFusedType> Types;
        [NativeDisableParallelForRestriction] public NativeArray<float> Xs;
        [NativeDisableParallelForRestriction] public NativeArray<float> Ys;
        [NativeDisableParallelForRestriction] public NativeArray<float> Reaches;
        [NativeDisableParallelForRestriction] public NativeArray<short> Thresholds;
        [NativeDisableParallelForRestriction] public NativeArray<GiWorld.SourceId> Excludes;
        [NativeDisableParallelForRestriction] public NativeArray<float> PrevXs;
        [NativeDisableParallelForRestriction] public NativeArray<float> PrevYs;
        [NativeDisableParallelForRestriction] public NativeArray<float> PrevReach;

        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
        {
            if (Gate.Value.Skip != 0) return;
            var context = Context.Value;
            if (!context.Alive || !View.Eligible(chunk)) return;

            var count = Types.Length;
            var present = stackalloc byte[count];
            var inputs = stackalloc byte*[count];
            for (var t = 0; t < count; t++)
            {
                present[t] = 0;
                inputs[t] = null;
                var type = Types[t];
                var handle = type.Handle;
                if ((type.Reach.Active || type.Threshold.Active) && type.Size != 0 && chunk.Has(ref handle))
                {
                    present[t] = 1;
                    inputs[t] = (byte*)chunk.GetDynamicComponentDataArrayReinterpret<byte>(ref handle, type.Size).GetUnsafeReadOnlyPtr();
                }
            }

            var useMatrix = View.UsesMatrix(chunk);
            var transforms = !useMatrix ? chunk.GetNativeArray(ref View.Transforms) : default;
            var matrices = useMatrix && chunk.Has(ref View.Matrices) ? chunk.GetNativeArray(ref View.Matrices) : default;
            var sources = Excludes.Length != 0 && chunk.Has(ref View.Sources) ? chunk.GetNativeArray(ref View.Sources) : default;
            var entities = View.EntitiesOf(chunk);
            var rows = View.Rows(chunk, useEnabledMask, chunkEnabledMask);
            var row = Offsets[unfilteredChunkIndex];
            while (rows.NextEntityIndex(out var i))
            {
                if (!GiSensePass.Due(entities[i].Index, Period, context.Tick)) continue;

                GiPose.Read(context.Plane, useMatrix, transforms, matrices, i, out var x, out var y, out _, out _);
                Xs[row] = x;
                Ys[row] = y;
                PrevXs[row] = x;
                PrevYs[row] = y;
                if (Reaches.Length != 0)
                {
                    Reaches[row] = Reach;
                    PrevReach[row] = Reach;
                }
                if (Thresholds.Length != 0) Thresholds[row] = Threshold;
                if (Excludes.Length != 0) Excludes[row] = sources.IsCreated ? sources[i].Id : default;
                for (var t = 0; t < count; t++)
                {
                    if (present[t] == 0) continue;
                    var type = Types[t];
                    if (type.Reach.Active)
                    {
                        Reaches[row] = *(float*)(inputs[t] + i * type.Size + type.Reach.Offset);
                        PrevReach[row] = Reaches[row];
                    }
                    if (type.Threshold.Active) Thresholds[row] = *(short*)(inputs[t] + i * type.Size + type.Threshold.Offset);
                }
                row++;
            }
        }
    }

    internal struct GiSenseGateState
    {
        public int PrevRows;
        public int PrevTick;
        public int Skip;
    }

    internal unsafe struct GiSenseGateJob : IJob
    {
        public GiBindingKind Kind;
        public GiLayerKey Layer;
        public float Reach;
        public ushort Since;
        public byte Needs;
        public byte Eligible;
        [ReadOnly] public NativeReference<GiReconcileContext> Context;
        [ReadOnly] public NativeArray<int> Counts;
        [ReadOnly] public NativeArray<int> Moved;
        [ReadOnly] public NativeArray<GiGridSpec> Grids;
        [ReadOnly] public NativeList<float> PrevXs;
        [ReadOnly] public NativeList<float> PrevYs;
        [ReadOnly] public NativeList<float> PrevReach;
        [NativeDisableParallelForRestriction] public NativeArray<int> Scratch;
        public NativeReference<GiSenseGateState> Gate;

        public void Execute()
        {
            var context = Context.Value;
            var prior = Gate.Value;
            var total = 0;
            var moved = 0;
            for (var c = 0; c < Counts.Length; c++)
            {
                total += Counts[c];
                moved |= Moved[c];
            }

            var state = new GiSenseGateState { PrevRows = total, PrevTick = context.Tick, Skip = 0 };
            if (Eligible != 0 && context.Alive && prior.PrevTick >= 0 && total == prior.PrevRows && moved == 0 &&
                PrevXs.Length >= total && PrevYs.Length >= total &&
                context.Tables.TryLayer(Layer, out var layer))
            {
                GiGuard.Begin(context.World, GiGuard.Sense, false);
                var scope = GiMarkers.Sense.Auto();
                state.Skip = Decide(context, layer, prior, total) ? 1 : 0;
                scope.Dispose();
                GiGuard.End(context.World, false);
            }

            Gate.Value = state;
        }

        private readonly bool Decide(in GiReconcileContext context, byte layer, in GiSenseGateState prior, int total)
        {
            var grids = Grids;
            var gridCount = context.Tables.GridCount;
            if (grids.Length < gridCount) gridCount = grids.Length;
            if (gridCount <= 0 || Scratch.Length == 0) return false;

            var needsReach = ((GiSenseNeeds)Needs & GiSenseNeeds.Reach) != 0;
            if (needsReach && PrevReach.Length < total) return false;

            var since = Kind == GiBindingKind.Changed ? prior.PrevTick - (Since < 1 ? 1 : Since) : prior.PrevTick;
            float minX;
            float minY;
            float maxX;
            float maxY;
            GiSensePass.GateBounds(
                total == 0 ? null : (float*)PrevXs.GetUnsafeReadOnlyPtr(),
                total == 0 ? null : (float*)PrevYs.GetUnsafeReadOnlyPtr(),
                !needsReach || total == 0 ? null : (float*)PrevReach.GetUnsafeReadOnlyPtr(),
                needsReach ? 0f : Reach, total, out minX, out minY, out maxX, out maxY);

            var ids = stackalloc byte[GiTables.MaxGrids];
            var count = 0;
            for (var gi = 0; gi < gridCount; gi++)
            {
                if (!context.Tables.TryGrid(gi, out ids[count])) return false;
                count++;
            }

            return GiSensePass.GateClosed(context.World, layer, (GiGridSpec*)grids.GetUnsafeReadOnlyPtr(), ids,
                count, since, minX, minY, maxX, maxY, (int*)Scratch.GetUnsafePtr(), Scratch.Length);
        }
    }

    internal unsafe struct GiSenseReadJob : IJob
    {
        public GiBindingKind Kind;
        public GiLayerKey Layer;
        public float Reach;
        public int Since;
        [ReadOnly] public NativeReference<GiReconcileContext> Context;
        [ReadOnly] public NativeReference<GiSenseGateState> Gate;
        [ReadOnly] public NativeArray<int> Rows;
        [ReadOnly] public NativeArray<float> Xs;
        [ReadOnly] public NativeArray<float> Ys;
        [ReadOnly] public NativeArray<float> Reaches;
        [ReadOnly] public NativeArray<short> Thresholds;
        [ReadOnly] public NativeArray<GiWorld.SourceId> Excludes;
        [NativeDisableParallelForRestriction] public NativeArray<short> Values;
        [NativeDisableParallelForRestriction] public NativeArray<float> GradientXs;
        [NativeDisableParallelForRestriction] public NativeArray<float> GradientYs;
        [NativeDisableParallelForRestriction] public NativeArray<long> Totals;
        [NativeDisableParallelForRestriction] public NativeArray<ulong> Whole;

        public void Execute()
        {
            if (Gate.Value.Skip != 0) return;
            var context = Context.Value;
            if (Rows[1] == 0 || Rows[0] == 0 || !context.Tables.TryLayer(Layer, out var layer)) return;

            var columns = new GiSenseColumns
            {
                Xs = (float*)Xs.GetUnsafeReadOnlyPtr(),
                Ys = (float*)Ys.GetUnsafeReadOnlyPtr(),
                Reaches = Reaches.Length == 0 ? null : (float*)Reaches.GetUnsafeReadOnlyPtr(),
                Thresholds = Thresholds.Length == 0 ? null : (short*)Thresholds.GetUnsafeReadOnlyPtr(),
                Excludes = Excludes.Length == 0 ? null : (GiWorld.SourceId*)Excludes.GetUnsafeReadOnlyPtr(),
                Values = Values.Length == 0 ? null : (short*)Values.GetUnsafePtr(),
                VectorXs = GradientXs.Length == 0 ? null : (float*)GradientXs.GetUnsafePtr(),
                VectorYs = GradientYs.Length == 0 ? null : (float*)GradientYs.GetUnsafePtr(),
                Totals = Totals.Length == 0 ? null : (long*)Totals.GetUnsafePtr(),
                Whole = (ulong*)Whole.GetUnsafePtr(),
                Rows = Rows[0],
            };
            GiGuard.Begin(context.World, GiGuard.Sense, false);
            var scope = GiMarkers.Sense.Auto();
            GiSensePass.ReadColumns(Kind, context.World, layer, columns, Reach, GiSensePass.SinceTick(context.Tick, Since), columns.Rows, 0);
            scope.Dispose();
            GiGuard.End(context.World, false);
        }
    }

    [BurstCompile]
    internal unsafe struct GiSenseScatterJob : IJobChunk
    {
        public GiSenseView View;
        public int Period;
        [ReadOnly] public NativeReference<GiReconcileContext> Context;
        [ReadOnly] public NativeReference<GiSenseGateState> Gate;
        [ReadOnly] public NativeArray<int> Rows;
        [ReadOnly] public NativeArray<int> Offsets;
        [ReadOnly] public NativeArray<GiSenseFusedType> Types;
        [ReadOnly] public NativeList<short> Values;
        [ReadOnly] public NativeList<float> GradientXs;
        [ReadOnly] public NativeList<float> GradientYs;
        [ReadOnly] public NativeList<long> Totals;
        [ReadOnly] public NativeList<ulong> Whole;

        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
        {
            if (Gate.Value.Skip != 0) return;
            if (Rows[1] == 0 || !View.Eligible(chunk)) return;

            var count = Types.Length;
            var present = stackalloc byte[count];
            var outputs = stackalloc byte*[count];
            for (var t = 0; t < count; t++)
            {
                present[t] = 0;
                outputs[t] = null;
                var type = Types[t];
                if (!type.Value.Active && !type.Gradient.Active && !type.Total.Active && !type.Position.Active && !type.Coverage.Active)
                    continue;
                var handle = type.Handle;
                if (!chunk.Has(ref handle)) continue;
                present[t] = 1;
                if (type.Size != 0)
                    outputs[t] = (byte*)chunk.GetDynamicComponentDataArrayReinterpret<byte>(ref handle, type.Size).GetUnsafePtr();
            }

            var tick = Context.Value.Tick;
            var entities = View.EntitiesOf(chunk);
            var whole = (ulong*)Whole.GetUnsafeReadOnlyPtr();
            var rows = View.Rows(chunk, useEnabledMask, chunkEnabledMask);
            var row = Offsets[unfilteredChunkIndex];
            while (rows.NextEntityIndex(out var i))
            {
                if (!GiSensePass.Due(entities[i].Index, Period, tick)) continue;

                var covered = (whole[row >> 6] >> (row & 63) & 1ul) != 0ul;
                for (var t = 0; t < count; t++)
                {
                    if (present[t] == 0) continue;
                    var type = Types[t];
                    var data = outputs[t];
                    if (type.Value.Active) *(short*)(data + i * type.Size + type.Value.Offset) = Values[row];
                    if (type.Total.Active) *(long*)(data + i * type.Size + type.Total.Offset) = Totals[row];
                    if (type.Gradient.Active)
                    {
                        var gradient = (float*)(data + i * type.Size + type.Gradient.Offset);
                        gradient[0] = GradientXs[row];
                        gradient[1] = GradientYs[row];
                    }

                    if (type.Position.Active)
                    {
                        var position = (float*)(data + i * type.Size + type.Position.Offset);
                        position[0] = GradientXs[row];
                        position[1] = GradientYs[row];
                    }

                    if (type.Coverage.Active)
                    {
                        var handle = type.Handle;
                        chunk.SetComponentEnabled(ref handle, i, covered);
                    }
                }
                row++;
            }
        }
    }
}
