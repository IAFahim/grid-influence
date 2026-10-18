using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;

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
        [ReadOnly] public NativeReference<GiReconcileContext> Context;
        [NativeDisableParallelForRestriction] public NativeArray<int> Counts;

        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
        {
            if (!Context.Value.Alive || !View.Eligible(chunk)) return;

            var rows = View.Rows(chunk, useEnabledMask, chunkEnabledMask);
            var count = 0;
            while (rows.NextEntityIndex(out _)) count++;
            Counts[unfilteredChunkIndex] = count;
        }
    }

    [BurstCompile]
    internal struct GiSensePrefixJob : IJob
    {
        public Entity Stats;
        public ComponentLookup<GiStats> Lookup;
        public byte NeedsValue;
        public byte NeedsGradient;
        public GiLayerKey Layer;
        [ReadOnly] public NativeReference<GiReconcileContext> Context;
        [ReadOnly] public NativeArray<int> Counts;
        public NativeArray<int> Offsets;
        public NativeArray<int> Rows;
        public NativeList<float> Xs;
        public NativeList<float> Ys;
        public NativeList<short> Values;
        public NativeList<float> GradientXs;
        public NativeList<float> GradientYs;
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

            var context = Context.Value;
            var valid = context.Alive && context.Tables.TryLayer(Layer, out _);
            Rows[0] = valid ? total : 0;
            Rows[1] = valid ? 1 : 0;
            Xs.ResizeUninitialized(total);
            Ys.ResizeUninitialized(total);
            Values.ResizeUninitialized(NeedsValue != 0 ? total : 0);
            GradientXs.ResizeUninitialized(NeedsGradient != 0 ? total : 0);
            GradientYs.ResizeUninitialized(NeedsGradient != 0 ? total : 0);
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
        [ReadOnly] public NativeReference<GiReconcileContext> Context;
        [ReadOnly] public NativeArray<int> Offsets;
        [NativeDisableParallelForRestriction] public NativeArray<float> Xs;
        [NativeDisableParallelForRestriction] public NativeArray<float> Ys;

        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
        {
            var context = Context.Value;
            if (!context.Alive || !View.Eligible(chunk)) return;

            var useMatrix = View.UsesMatrix(chunk);
            var transforms = !useMatrix ? chunk.GetNativeArray(ref View.Transforms) : default;
            var matrices = useMatrix && chunk.Has(ref View.Matrices) ? chunk.GetNativeArray(ref View.Matrices) : default;
            var rows = View.Rows(chunk, useEnabledMask, chunkEnabledMask);
            var row = Offsets[unfilteredChunkIndex];
            while (rows.NextEntityIndex(out var i))
            {
                GiPose.Read(context.Plane, useMatrix, transforms, matrices, i, out var x, out var y, out _, out _);
                Xs[row] = x;
                Ys[row] = y;
                row++;
            }
        }
    }


    internal unsafe struct GiSenseReadJob : IJobParallelForDefer
    {
        public GiBindingKind Kind;
        public GiLayerKey Layer;
        public int Batch;
        [ReadOnly] public NativeList<int> Batches;
        [ReadOnly] public NativeReference<GiReconcileContext> Context;
        [ReadOnly] public NativeArray<int> Rows;
        [ReadOnly] public NativeArray<float> Xs;
        [ReadOnly] public NativeArray<float> Ys;
        [NativeDisableParallelForRestriction] public NativeArray<short> Values;
        [NativeDisableParallelForRestriction] public NativeArray<float> GradientXs;
        [NativeDisableParallelForRestriction] public NativeArray<float> GradientYs;
        [NativeDisableParallelForRestriction] public NativeArray<ulong> Whole;

        public void Execute(int batchIndex)
        {
            var context = Context.Value;
            if (Rows[1] == 0 || Rows[0] == 0 || !context.Tables.TryLayer(Layer, out var layer)) return;

            GiSensePass.ReadBatch(Kind, context.World, layer, (float*)Xs.GetUnsafeReadOnlyPtr(), (float*)Ys.GetUnsafeReadOnlyPtr(), Rows[0],
                Batch, batchIndex, Values.Length == 0 ? null : (short*)Values.GetUnsafePtr(),
                GradientXs.Length == 0 ? null : (float*)GradientXs.GetUnsafePtr(),
                GradientYs.Length == 0 ? null : (float*)GradientYs.GetUnsafePtr(), (ulong*)Whole.GetUnsafePtr());
        }
    }

    [BurstCompile]
    internal unsafe struct GiSenseScatterJob : IJobChunk
    {
        public GiSenseView View;
        public DynamicComponentTypeHandle Handle;
        public int TypeSize;
        public GiSenseField Value;
        public GiSenseField Gradient;
        public GiSenseField Coverage;
        [ReadOnly] public NativeArray<int> Rows;
        [ReadOnly] public NativeArray<int> Offsets;
        [ReadOnly] public NativeList<short> Values;
        [ReadOnly] public NativeList<float> GradientXs;
        [ReadOnly] public NativeList<float> GradientYs;
        [ReadOnly] public NativeList<ulong> Whole;

        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
        {
            if (Rows[1] == 0 || !View.Eligible(chunk) || !chunk.Has(ref Handle)) return;

            byte* data = null;
            if (TypeSize > 0) data = (byte*)chunk.GetDynamicComponentDataArrayReinterpret<byte>(ref Handle, TypeSize).GetUnsafePtr();

            var whole = (ulong*)Whole.GetUnsafeReadOnlyPtr();
            var rows = View.Rows(chunk, useEnabledMask, chunkEnabledMask);
            var row = Offsets[unfilteredChunkIndex];
            while (rows.NextEntityIndex(out var i))
            {
                var covered = (whole[row >> 6] >> (row & 63) & 1ul) != 0ul;
                if (Value.Active) *(short*)(data + i * TypeSize + Value.Offset) = Values[row];
                if (Gradient.Active)
                {
                    var target = (float*)(data + i * TypeSize + Gradient.Offset);
                    target[0] = GradientXs[row];
                    target[1] = GradientYs[row];
                }

                if (Coverage.Active) chunk.SetComponentEnabled(ref Handle, i, covered);
                row++;
            }
        }
    }
}
