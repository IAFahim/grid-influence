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
        public Entity Frame;
        [ReadOnly] public ComponentLookup<GiFrame> Frames;
        [NativeDisableParallelForRestriction] public NativeArray<int> Counts;

        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
        {
            View.Visit(chunk, GiReconcileContext.Of(Frames[Frame]), (int*)Counts.GetUnsafePtr() + unfilteredChunkIndex * GiSplatPass.Layers, null, null, null,
                null, null);
        }
    }

    [BurstCompile]
    internal struct GiSplatPrefixJob : IJob
    {
        public Entity Frame;
        [ReadOnly] public ComponentLookup<GiFrame> Frames;
        public NativeReference<GiReconcileContext> Context;
        [ReadOnly] public NativeArray<int> Counts;
        public NativeArray<int> Offsets;
        public NativeArray<int> Meta;
        public NativeList<float> Xs;
        public NativeList<float> Ys;
        public NativeList<byte> Stamps;
        public NativeList<sbyte> Gains;

        public void Execute()
        {
            Context.Value = GiReconcileContext.Of(Frames[Frame]);
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

    [BurstCompile]
    internal struct GiSplatIdleJob : IJob
    {
        public Entity Frame;
        public ComponentLookup<GiStats> Stats;

        public void Execute()
        {
            var stats = Stats[Frame];
            stats.Splats = 0;
            Stats[Frame] = stats;
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
                var xs = (float*)Xs.GetUnsafePtr();
                var ys = (float*)Ys.GetUnsafePtr();
                var stamps = (byte*)Stamps.GetUnsafePtr();
                var gains = (sbyte*)Gains.GetUnsafePtr();
                GiGuard.Begin(frame.World, GiGuard.Splat, true);
                var scope = GiMarkers.Splat.Auto();
                if (GiRecorderGate.Enabled) GiRecorderGate.Splat(frame.World, meta, meta + GiSplatPass.Layers, xs, ys, stamps, gains);
                applied = GiSplatPass.Apply(frame.World, frame.Tables, meta, meta + GiSplatPass.Layers, xs, ys, stamps, gains);
                scope.Dispose();
                GiGuard.End(frame.World, true);
            }

            var stats = Stats[Frame];
            stats.Splats = applied;
            Stats[Frame] = stats;
        }
    }
}
