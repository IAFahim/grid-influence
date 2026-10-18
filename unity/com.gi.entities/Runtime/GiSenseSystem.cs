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
    public unsafe partial class GiSenseSystem : SystemBase
    {
        private EntityQuery _profiles;
        private GiSenseBinding[] _bindings = System.Array.Empty<GiSenseBinding>();
        private GiSenseView _view;
        private ComponentLookup<GiFrame> _frames;
        private ComponentLookup<GiStats> _stats;
        private ulong _builtHash;

        protected override void OnCreate()
        {
            RequireForUpdate<GiFrame>();
            _profiles = GetEntityQuery(ComponentType.ReadOnly<GiProfileReference>());
            _view = new GiSenseView
            {
                Transforms = GetComponentTypeHandle<LocalTransform>(true),
                Matrices = GetComponentTypeHandle<LocalToWorld>(true),
                Parents = GetComponentTypeHandle<Parent>(true),
                Sources = GetComponentTypeHandle<GiSource>(true),
                Entities = GetEntityTypeHandle(),
            };
            _frames = GetComponentLookup<GiFrame>();
            _stats = GetComponentLookup<GiStats>();
        }

        protected override void OnDestroy()
        {
            Dependency.Complete();
            Dispose();
        }

        protected override void OnUpdate()
        {
            if (_profiles.IsEmptyIgnoreFilter)
            {
                if (_builtHash != 0ul) Dispose();
                return;
            }

            var reference = _profiles.GetSingleton<GiProfileReference>();
            if (!reference.Value.IsCreated) return;

            if (reference.Value.Value.Hash != _builtHash) Rebuild(ref reference.Value.Value);
            if (_bindings.Length == 0) return;

            _view.Transforms.Update(this);
            _view.Matrices.Update(this);
            _view.Parents.Update(this);
            _view.Sources.Update(this);
            _view.Entities.Update(this);
            _frames.Update(this);
            _stats.Update(this);

            var frame = SystemAPI.GetSingletonEntity<GiFrame>();
            var allocator = World.UpdateAllocator.ToAllocator;
            var batch = GiSensePass.Batch(GiSenseConfig.Batch.Data);
            var context = new NativeReference<GiReconcileContext>(allocator);
            var handle = new GiSenseBeginJob { Stats = frame, Lookup = _stats, Frame = frame, Frames = _frames, Context = context }.Schedule(Dependency);

            for (var b = 0; b < _bindings.Length; b++)
                handle = Schedule(_bindings[b], frame, context, batch, allocator, handle);

            Dependency = handle;
        }

        private void Rebuild(ref GiProfileBlob blob)
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
                    Report(i, GiBindingTable.Describe(statuses[i]));
                    continue;
                }

                var binding = Resolve(i, specs[i]);
                if (binding != null) list.Add(binding);
            }

            _bindings = list.ToArray();
        }

        private void Report(int index, string reason)
        {
            BLGlobalLogger.LogErrorString("Gi binding " + index + " rejected: " + reason);
            var frame = SystemAPI.GetSingletonEntity<GiFrame>();
            var data = EntityManager.GetComponentData<GiFrame>(frame);
            data.Faults.Raise(GiFaultKind.Profile, data.Tick);
            EntityManager.SetComponentData(frame, data);
        }

        private void Dispose()
        {
            foreach (var binding in _bindings) binding.Query.Dispose();
            _bindings = System.Array.Empty<GiSenseBinding>();
            _builtHash = 0ul;
        }
    }
}
