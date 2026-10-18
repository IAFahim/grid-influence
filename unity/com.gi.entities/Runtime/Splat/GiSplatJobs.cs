using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;

namespace Gi.Entities
{
    [BurstCompile]
    internal unsafe struct GiSplatCountJob : IJobChunk
    {
        public GiSplatView View;
        [ReadOnly] public NativeReference<GiReconcileContext> Context;
        [NativeDisableParallelForRestriction] public NativeArray<int> Counts;

        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
        {
            View.Visit(chunk, Context.Value, (int*)Counts.GetUnsafePtr() + unfilteredChunkIndex * GiSplatPass.Layers, null, null, null,
                null, null);
        }
    }

    [BurstCompile]
    internal struct GiSplatPrefixJob : IJob
    {
        [ReadOnly] public NativeArray<int> Counts;
        public NativeArray<int> Offsets;
        public NativeArray<int> Meta;
        public NativeList<float> Xs;
        public NativeList<float> Ys;
        public NativeList<byte> Stamps;
        public NativeList<sbyte> Gains;

        public void Execute()
        {
            var chunks = Counts.Length / GiSplatPass.Layers;
            var total = 0;
            for (var k = 0; k < GiSplatPass.Layers; k++)
            {
                var count = 0;
                Meta[k] = total;
                for (var c = 0; c < chunks; c++)
                {
                    Offsets[c * GiSplatPass.Layers + k] = total + count;
                    count += Counts[c * GiSplatPass.Layers + k];
                }

                Meta[GiSplatPass.Layers + k] = count;
                total += count;
            }

            Xs.ResizeUninitialized(total);
            Ys.ResizeUninitialized(total);
            Stamps.ResizeUninitialized(total);
            Gains.ResizeUninitialized(total);
        }
    }

    [BurstCompile]
    internal unsafe struct GiSplatWriteJob : IJobChunk
    {
        public GiSplatView View;
        [ReadOnly] public NativeReference<GiReconcileContext> Context;
        [ReadOnly] public NativeArray<int> Offsets;
        [NativeDisableParallelForRestriction] public NativeArray<float> Xs;
        [NativeDisableParallelForRestriction] public NativeArray<float> Ys;
        [NativeDisableParallelForRestriction] public NativeArray<byte> Stamps;
        [NativeDisableParallelForRestriction] public NativeArray<sbyte> Gains;

        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
        {
            var cursor = stackalloc int[GiSplatPass.Layers];
            for (var k = 0; k < GiSplatPass.Layers; k++) cursor[k] = Offsets[unfilteredChunkIndex * GiSplatPass.Layers + k];

            View.Visit(chunk, Context.Value, null, cursor, (float*)Xs.GetUnsafePtr(), (float*)Ys.GetUnsafePtr(),
                (byte*)Stamps.GetUnsafePtr(), (sbyte*)Gains.GetUnsafePtr());
        }
    }

    internal unsafe struct GiSplatApplyJob : IJob
    {
        public Entity Frame;
        public ComponentLookup<GiFrame> Frames;
        public ComponentLookup<GiStats> Stats;
        [ReadOnly] public NativeArray<int> Meta;
        public NativeList<float> Xs;
        public NativeList<float> Ys;
        public NativeList<byte> Stamps;
        public NativeList<sbyte> Gains;

        public void Execute()
        {
            var frame = Frames[Frame];
            var applied = 0;
            if (frame.Alive && Xs.Length > 0)
            {
                var meta = (int*)Meta.GetUnsafeReadOnlyPtr();
                GiGuard.Begin(frame.World, GiGuard.Splat, true);
                var scope = GiMarkers.Splat.Auto();
                applied = GiSplatPass.Apply(frame.World, frame.Tables, meta, meta + GiSplatPass.Layers, (float*)Xs.GetUnsafePtr(),
                    (float*)Ys.GetUnsafePtr(), (byte*)Stamps.GetUnsafePtr(), (sbyte*)Gains.GetUnsafePtr());
                scope.Dispose();
                GiGuard.End(frame.World, true);
            }

            var stats = Stats[Frame];
            stats.Splats = applied;
            Stats[Frame] = stats;
        }
    }
}
