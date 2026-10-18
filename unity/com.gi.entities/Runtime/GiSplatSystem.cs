using BovineLabs.Core;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Transforms;

namespace Gi.Entities
{
    [WorldSystemFilter(Worlds.SimulationEditor)]
    [UpdateInGroup(typeof(GiSystemGroup))]
    [UpdateAfter(typeof(GiSyncSystem))]
    [UpdateBefore(typeof(GiProcessSystem))]
    public partial struct GiSplatSystem : ISystem
    {
        private EntityQuery _splats;
        private GiSplatView _view;
        private ComponentLookup<GiFrame> _frames;
        private ComponentLookup<GiStats> _stats;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GiFrame>();
            _splats = new EntityQueryBuilder(Allocator.Temp).WithAll<GiSplat>().Build(ref state);
            _view = new GiSplatView
            {
                Splats = state.GetComponentTypeHandle<GiSplat>(true),
                Transforms = state.GetComponentTypeHandle<LocalTransform>(true),
                Matrices = state.GetComponentTypeHandle<LocalToWorld>(true),
                Parents = state.GetComponentTypeHandle<Parent>(true),
                Faults = state.GetComponentTypeHandle<GiFault>(),
            };
            _frames = state.GetComponentLookup<GiFrame>();
            _stats = state.GetComponentLookup<GiStats>();
        }

        public void OnUpdate(ref SystemState state)
        {
            _stats.Update(ref state);
            var frameEntity = SystemAPI.GetSingletonEntity<GiFrame>();
            if (_splats.IsEmpty)
            {
                state.Dependency = new GiSplatIdleJob { Frame = frameEntity, Stats = _stats }.Schedule(state.Dependency);
                return;
            }

            _view.Splats.Update(ref state);
            _view.Transforms.Update(ref state);
            _view.Matrices.Update(ref state);
            _view.Parents.Update(ref state);
            _view.Faults.Update(ref state);
            _frames.Update(ref state);

            var allocator = state.WorldUpdateAllocator;
            var chunkCount = _splats.CalculateChunkCountWithoutFiltering();
            var context = new NativeReference<GiReconcileContext>(allocator);
            var counts = CollectionHelper.CreateNativeArray<int>(chunkCount * GiSplatPass.Layers, allocator);
            var offsets = CollectionHelper.CreateNativeArray<int>(chunkCount * GiSplatPass.Layers, allocator);
            var meta = CollectionHelper.CreateNativeArray<int>(2 * GiSplatPass.Layers, allocator);
            var xs = new NativeList<float>(allocator);
            var ys = new NativeList<float>(allocator);
            var stamps = new NativeList<byte>(allocator);
            var gains = new NativeList<sbyte>(allocator);

            var handle = new GiSplatCountJob { View = _view, Frame = frameEntity, Frames = _frames, Counts = counts }.ScheduleParallel(_splats, state.Dependency);
            handle = new GiSplatPrefixJob
            {
                Frame = frameEntity,
                Frames = _frames,
                Context = context,
                Counts = counts,
                Offsets = offsets,
                Meta = meta,
                Xs = xs,
                Ys = ys,
                Stamps = stamps,
                Gains = gains,
            }.Schedule(handle);
            handle = new GiSplatWriteJob
            {
                View = _view,
                Context = context,
                Offsets = offsets,
                Xs = xs.AsDeferredJobArray(),
                Ys = ys.AsDeferredJobArray(),
                Stamps = stamps.AsDeferredJobArray(),
                Gains = gains.AsDeferredJobArray(),
            }.ScheduleParallel(_splats, handle);

            handle = new GiSplatApplyJob
            {
                Frame = frameEntity,
                Frames = _frames,
                Stats = _stats,
                Meta = meta,
                Xs = xs,
                Ys = ys,
                Stamps = stamps,
                Gains = gains,
            }.Schedule(GiRuntime.BeginWrite(handle));
            GiRuntime.EndWrite(handle);

            state.Dependency = handle;
        }
    }
}
