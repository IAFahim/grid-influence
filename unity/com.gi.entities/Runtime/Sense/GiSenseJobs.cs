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

        public void Execute()
        {
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
        [ReadOnly] public NativeReference<GiReconcileContext> Context;
        [NativeDisableParallelForRestriction] public NativeArray<int> Counts;

        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
        {
            var context = Context.Value;
            if (!context.Alive || !View.Eligible(chunk)) return;

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
        public NativeList<int> Batches;
        public int Batch;

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
            Rows[0] = valid ? total : 0;
            Rows[1] = valid ? 1 : 0;
            Xs.ResizeUninitialized(total);
            Ys.ResizeUninitialized(total);
            Reaches.ResizeUninitialized((needs & GiSenseNeeds.Reach) != 0 ? total : 0);
            Thresholds.ResizeUninitialized((needs & GiSenseNeeds.Threshold) != 0 ? total : 0);
            Excludes.ResizeUninitialized((needs & GiSenseNeeds.Exclude) != 0 ? total : 0);
            Values.ResizeUninitialized((needs & GiSenseNeeds.Value) != 0 ? total : 0);
            GradientXs.ResizeUninitialized((needs & GiSenseNeeds.Vector) != 0 ? total : 0);
            GradientYs.ResizeUninitialized((needs & GiSenseNeeds.Vector) != 0 ? total : 0);
            Totals.ResizeUninitialized((needs & GiSenseNeeds.Total) != 0 ? total : 0);
            Whole.ResizeUninitialized(GiSensePass.Words(total));
            Batches.ResizeUninitialized(GiSensePass.Batches(total, Batch));

            var stats = Lookup[Stats];
            stats.Sensed += Rows[0];
            Lookup[Stats] = stats;
        }
    }

    [BurstCompile]
    internal unsafe struct GiSenseWriteJob : IJobChunk
    {
        public GiSenseView View;
        public int Period;
        public float Reach;
        public short Threshold;
        [ReadOnly] public NativeReference<GiReconcileContext> Context;
        [ReadOnly] public NativeArray<int> Offsets;
        [NativeDisableParallelForRestriction] public NativeArray<float> Xs;
        [NativeDisableParallelForRestriction] public NativeArray<float> Ys;
        [NativeDisableParallelForRestriction] public NativeArray<float> Reaches;
        [NativeDisableParallelForRestriction] public NativeArray<short> Thresholds;
        [NativeDisableParallelForRestriction] public NativeArray<GiWorld.SourceId> Excludes;

        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
        {
            var context = Context.Value;
            if (!context.Alive || !View.Eligible(chunk)) return;

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
                if (Reaches.Length != 0) Reaches[row] = Reach;
                if (Thresholds.Length != 0) Thresholds[row] = Threshold;
                if (Excludes.Length != 0) Excludes[row] = sources.IsCreated ? sources[i].Id : default;
                row++;
            }
        }
    }

    [BurstCompile]
    internal unsafe struct GiSenseGatherJob : IJobChunk
    {
        public GiSenseView View;
        public DynamicComponentTypeHandle Handle;
        public int TypeSize;
        public ushort Offset;
        public byte Threshold;
        public int Period;
        [ReadOnly] public NativeReference<GiReconcileContext> Context;
        [ReadOnly] public NativeArray<int> Offsets;
        [NativeDisableParallelForRestriction] public NativeArray<float> Reaches;
        [NativeDisableParallelForRestriction] public NativeArray<short> Thresholds;

        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
        {
            var context = Context.Value;
            if (!context.Alive || !View.Eligible(chunk) || !chunk.Has(ref Handle) || TypeSize == 0) return;

            var data = (byte*)chunk.GetDynamicComponentDataArrayReinterpret<byte>(ref Handle, TypeSize).GetUnsafeReadOnlyPtr();
            var entities = View.EntitiesOf(chunk);
            var rows = View.Rows(chunk, useEnabledMask, chunkEnabledMask);
            var row = Offsets[unfilteredChunkIndex];
            while (rows.NextEntityIndex(out var i))
            {
                if (!GiSensePass.Due(entities[i].Index, Period, context.Tick)) continue;

                if (Threshold != 0) Thresholds[row] = *(short*)(data + i * TypeSize + Offset);
                else Reaches[row] = *(float*)(data + i * TypeSize + Offset);
                row++;
            }
        }
    }

    internal unsafe struct GiSenseReadJob : IJobParallelForDefer
    {
        public GiBindingKind Kind;
        public GiLayerKey Layer;
        public int Batch;
        public float Reach;
        public int Since;
        [ReadOnly] public NativeList<int> Batches;
        [ReadOnly] public NativeReference<GiReconcileContext> Context;
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

        public void Execute(int batchIndex)
        {
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
            GiSensePass.ReadColumns(Kind, context.World, layer, columns, Reach, GiSensePass.SinceTick(context.Tick, Since), Batch, batchIndex);
            scope.Dispose();
            GiGuard.End(context.World, false);
        }
    }

    [BurstCompile]
    internal unsafe struct GiSenseScatterJob : IJobChunk
    {
        public GiSenseView View;
        public DynamicComponentTypeHandle Handle;
        public int TypeSize;
        public int Period;
        public GiSenseField Value;
        public GiSenseField Gradient;
        public GiSenseField Total;
        public GiSenseField Position;
        public GiSenseField Coverage;
        [ReadOnly] public NativeReference<GiReconcileContext> Context;
        [ReadOnly] public NativeArray<int> Rows;
        [ReadOnly] public NativeArray<int> Offsets;
        [ReadOnly] public NativeList<short> Values;
        [ReadOnly] public NativeList<float> GradientXs;
        [ReadOnly] public NativeList<float> GradientYs;
        [ReadOnly] public NativeList<long> Totals;
        [ReadOnly] public NativeList<ulong> Whole;

        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
        {
            if (Rows[1] == 0 || !View.Eligible(chunk) || !chunk.Has(ref Handle)) return;

            byte* data = null;
            if (TypeSize > 0) data = (byte*)chunk.GetDynamicComponentDataArrayReinterpret<byte>(ref Handle, TypeSize).GetUnsafePtr();

            var tick = Context.Value.Tick;
            var entities = View.EntitiesOf(chunk);
            var whole = (ulong*)Whole.GetUnsafeReadOnlyPtr();
            var rows = View.Rows(chunk, useEnabledMask, chunkEnabledMask);
            var row = Offsets[unfilteredChunkIndex];
            while (rows.NextEntityIndex(out var i))
            {
                if (!GiSensePass.Due(entities[i].Index, Period, tick)) continue;

                var covered = (whole[row >> 6] >> (row & 63) & 1ul) != 0ul;
                if (Value.Active) *(short*)(data + i * TypeSize + Value.Offset) = Values[row];
                if (Total.Active) *(long*)(data + i * TypeSize + Total.Offset) = Totals[row];
                if (Gradient.Active)
                {
                    var target = (float*)(data + i * TypeSize + Gradient.Offset);
                    target[0] = GradientXs[row];
                    target[1] = GradientYs[row];
                }

                if (Position.Active)
                {
                    var target = (float*)(data + i * TypeSize + Position.Offset);
                    target[0] = GradientXs[row];
                    target[1] = GradientYs[row];
                }

                if (Coverage.Active) chunk.SetComponentEnabled(ref Handle, i, covered);
                row++;
            }
        }
    }
}
