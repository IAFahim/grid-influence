using System.Runtime.InteropServices;
using BovineLabs.Core;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Transforms;

namespace Gi.Entities
{
    [WorldSystemFilter(Worlds.SimulationEditor)]
    [UpdateInGroup(typeof(GiSystemGroup))]
    [UpdateAfter(typeof(GiProcessSystem))]
    public unsafe partial struct GiSenseSystem : ISystem
    {
        private EntityQuery _profiles;
        private EntityQuery _frames;
        private GCHandle _bindings;
        private GiSenseView _view;
        private ComponentLookup<GiFrame> _frameData;
        private ComponentLookup<GiStats> _stats;
        private ulong _builtHash;

        private GiSenseBinding[] Table => _bindings.IsAllocated ? (GiSenseBinding[])_bindings.Target : null;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<GiFrame>();
            _profiles = state.GetEntityQuery(ComponentType.ReadOnly<GiProfileReference>());
            _frames = state.GetEntityQuery(ComponentType.ReadOnly<GiFrame>());
            _view = new GiSenseView
            {
                Transforms = state.GetComponentTypeHandle<LocalTransform>(true),
                Matrices = state.GetComponentTypeHandle<LocalToWorld>(true),
                Parents = state.GetComponentTypeHandle<Parent>(true),
                Sources = state.GetComponentTypeHandle<GiSource>(true),
                Entities = state.GetEntityTypeHandle(),
            };
            _frameData = state.GetComponentLookup<GiFrame>();
            _stats = state.GetComponentLookup<GiStats>();
        }

        public void OnDestroy(ref SystemState state)
        {
            state.Dependency.Complete();
            Dispose();
        }

        public void OnUpdate(ref SystemState state)
        {
            if (_profiles.IsEmptyIgnoreFilter)
            {
                if (_builtHash != 0ul) Dispose();
                return;
            }

            var reference = _profiles.GetSingleton<GiProfileReference>();
            if (!reference.Value.IsCreated) return;

            if (reference.Value.Value.Hash != _builtHash) Rebuild(ref state, ref reference.Value.Value);
            var table = Table;
            if (table == null || table.Length == 0) return;

            _view.Transforms.Update(ref state);
            _view.Matrices.Update(ref state);
            _view.Parents.Update(ref state);
            _view.Sources.Update(ref state);
            _view.Entities.Update(ref state);
            _frameData.Update(ref state);
            _stats.Update(ref state);

            var frame = SystemAPI.GetSingletonEntity<GiFrame>();
            var allocator = state.WorldUpdateAllocator;
            var batch = GiSensePass.Batch(GiSenseConfig.Batch.Data);
            var context = new NativeReference<GiReconcileContext>(allocator);
            var handle = new GiSenseBeginJob { Stats = frame, Lookup = _stats, Frame = frame, Frames = _frameData, Context = context }.Schedule(state.Dependency);

            for (var b = 0; b < table.Length; b++)
                handle = Schedule(ref state, table[b], frame, context, batch, allocator, handle);

            state.Dependency = handle;
        }

        private void Rebuild(ref SystemState state, ref GiProfileBlob blob)
        {
            Dispose();
            _builtHash = blob.Hash;
            var list = new System.Collections.Generic.List<GiSenseBinding>();
            var specs = new System.ReadOnlySpan<GiBindingSpec>(blob.Bindings.GetUnsafePtr(), blob.Bindings.Length);
            var layers = new System.ReadOnlySpan<GiLayerSpec>(blob.Layers.GetUnsafePtr(), blob.Layers.Length);
            var statuses = new GiBindingStatus[specs.Length];
            GiBindingTable.Validate(specs, layers, GiBindingSupport.All, statuses);
            for (var i = 0; i < specs.Length; i++)
            {
                if (statuses[i] != GiBindingStatus.Valid)
                {
                    Report(ref state, i, GiBindingTable.Describe(statuses[i]));
                    continue;
                }

                var binding = Resolve(ref state, i, specs[i]);
                if (binding != null) list.Add(binding);
            }

            if (list.Count > 0) _bindings = GCHandle.Alloc(list.ToArray());
        }

        private void Report(ref SystemState state, int index, string reason)
        {
            BLGlobalLogger.LogErrorString("Gi binding " + index + " rejected: " + reason);
            var frame = _frames.GetSingletonEntity();
            var data = state.EntityManager.GetComponentData<GiFrame>(frame);
            data.Faults.Raise(GiFaultKind.Profile, data.Tick);
            state.EntityManager.SetComponentData(frame, data);
        }

        private void Dispose()
        {
            var table = Table;
            if (table != null)
                foreach (var binding in table) binding.Query.Dispose();
            if (_bindings.IsAllocated) _bindings.Free();
            _bindings = default;
            _builtHash = 0ul;
        }
    }
}
