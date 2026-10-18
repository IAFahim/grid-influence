using BovineLabs.Core;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Transforms;
using GiWorld = Gi.World;

namespace Gi.Entities
{
    [WorldSystemFilter(Worlds.SimulationEditor)]
    [UpdateInGroup(typeof(GiSystemGroup))]
    [UpdateAfter(typeof(GiReconcileSystem))]
    [UpdateBefore(typeof(GiProcessSystem))]
    public partial struct GiSyncSystem : ISystem
    {
        private EntityQuery _placed;
        private GiSyncView _view;
        private ComponentTypeHandle<GiFault> _faults;
        private ComponentLookup<GiFrame> _frames;
        private ComponentLookup<GiStats> _stats;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GiFrame>();
            _placed = new EntityQueryBuilder(Allocator.Temp).WithAll<GiSource, GiEmitter>().Build(ref state);
            _view = new GiSyncView
            {
                Sources = state.GetComponentTypeHandle<GiSource>(true),
                Oriented = state.GetComponentTypeHandle<GiOriented>(true),
                Scaled = state.GetComponentTypeHandle<GiScaled>(true),
                Transforms = state.GetComponentTypeHandle<LocalTransform>(true),
                Matrices = state.GetComponentTypeHandle<LocalToWorld>(true),
                Parents = state.GetComponentTypeHandle<Parent>(true),
            };
            _faults = state.GetComponentTypeHandle<GiFault>();
            _frames = state.GetComponentLookup<GiFrame>();
            _stats = state.GetComponentLookup<GiStats>();
        }

        public void OnUpdate(ref SystemState state)
        {
            _view.Sources.Update(ref state);
            _view.Oriented.Update(ref state);
            _view.Scaled.Update(ref state);
            _view.Transforms.Update(ref state);
            _view.Matrices.Update(ref state);
            _view.Parents.Update(ref state);
            _faults.Update(ref state);
            _frames.Update(ref state);
            _stats.Update(ref state);

            var allocator = state.WorldUpdateAllocator;
            var frameEntity = SystemAPI.GetSingletonEntity<GiFrame>();
            var chunkCount = _placed.CalculateChunkCountWithoutFiltering();
            var context = new NativeReference<GiReconcileContext>(allocator);
            var counts = CollectionHelper.CreateNativeArray<int>(chunkCount * GiSyncPass.Categories, allocator);
            var offsets = CollectionHelper.CreateNativeArray<int>(chunkCount * GiSyncPass.Categories, allocator);
            var meta = CollectionHelper.CreateNativeArray<int>(3 * GiSyncPass.Categories, allocator);
            var sources = new NativeList<GiWorld.SourceId>(allocator);
            var xs = new NativeList<float>(allocator);
            var ys = new NativeList<float>(allocator);
            var headings = new NativeList<float>(allocator);
            var scales = new NativeList<float>(allocator);
            var invalid = new NativeList<ulong>(allocator);

            var handle = new GiSyncCountJob { View = _view, LastVersion = state.LastSystemVersion, Frame = frameEntity, Frames = _frames, Counts = counts }
                .ScheduleParallel(_placed, state.Dependency);
            handle = new GiSyncPrefixJob
            {
                Frame = frameEntity,
                Frames = _frames,
                Context = context,
                Counts = counts,
                Offsets = offsets,
                Meta = meta,
                Sources = sources,
                Xs = xs,
                Ys = ys,
                Headings = headings,
                Scales = scales,
                Invalid = invalid,
            }.Schedule(handle);
            handle = new GiSyncWriteJob
            {
                View = _view,
                LastVersion = state.LastSystemVersion,
                Context = context,
                Offsets = offsets,
                Sources = sources.AsDeferredJobArray(),
                Xs = xs.AsDeferredJobArray(),
                Ys = ys.AsDeferredJobArray(),
                Headings = headings.AsDeferredJobArray(),
                Scales = scales.AsDeferredJobArray(),
            }.ScheduleParallel(_placed, handle);

            handle = new GiSyncApplyJob
            {
                Frame = frameEntity,
                Frames = _frames,
                Stats = _stats,
                StatsEntity = frameEntity,
                Meta = meta,
                Sources = sources,
                Xs = xs,
                Ys = ys,
                Headings = headings,
                Scales = scales,
                Invalid = invalid,
            }.Schedule(GiRuntime.BeginWrite(handle));
            GiRuntime.EndWrite(handle);

            handle = new GiSyncScatterJob
            {
                View = _view,
                LastVersion = state.LastSystemVersion,
                Faults = _faults,
                Context = context,
                Offsets = offsets,
                Meta = meta,
                Xs = xs,
                Ys = ys,
                Invalid = invalid,
            }.ScheduleParallel(_placed, handle);

            state.Dependency = handle;
        }
    }
}
