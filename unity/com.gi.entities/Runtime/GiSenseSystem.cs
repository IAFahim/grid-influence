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
            _frames.Update(this);
            _stats.Update(this);

            var frame = SystemAPI.GetSingletonEntity<GiFrame>();
            var allocator = World.UpdateAllocator.ToAllocator;
            var batch = GiSensePass.Batch(GiSenseConfig.Batch.Data);
            var context = new NativeReference<GiReconcileContext>(allocator);
            var handle = new GiPrepareJob { Frame = frame, Frames = _frames, Context = context }.Schedule(Dependency);
            handle = new GiSenseBeginJob { Stats = frame, Lookup = _stats }.Schedule(handle);

            for (var b = 0; b < _bindings.Length; b++)
                handle = Schedule(_bindings[b], frame, context, batch, allocator, handle);

            Dependency = handle;
        }

        private JobHandle Schedule(GiSenseBinding binding, Entity frame, NativeReference<GiReconcileContext> context, int batch,
            Allocator allocator, JobHandle handle)
        {
            var spec = binding.Spec;
            var chunkCount = binding.Query.CalculateChunkCountWithoutFiltering();
            var counts = CollectionHelper.CreateNativeArray<int>(chunkCount, allocator);
            var offsets = CollectionHelper.CreateNativeArray<int>(chunkCount, allocator);
            var rows = CollectionHelper.CreateNativeArray<int>(2, allocator);
            var xs = new NativeList<float>(allocator);
            var ys = new NativeList<float>(allocator);
            var values = new NativeList<short>(allocator);
            var gradientXs = new NativeList<float>(allocator);
            var gradientYs = new NativeList<float>(allocator);
            var whole = new NativeList<ulong>(allocator);
            var batches = new NativeList<int>(allocator);
            var wantsValue = spec.Kind != GiBindingKind.Gradient;
            var wantsGradient = spec.Kind != GiBindingKind.Value;

            handle = new GiSenseCountJob { View = _view, Context = context, Counts = counts }.ScheduleParallel(binding.Query, handle);
            handle = new GiSensePrefixJob
            {
                Layer = new GiLayerKey(spec.Layer),
                Context = context,
                Stats = frame,
                Lookup = _stats,
                NeedsValue = (byte)(wantsValue ? 1 : 0),
                NeedsGradient = (byte)(wantsGradient ? 1 : 0),
                Counts = counts,
                Offsets = offsets,
                Rows = rows,
                Xs = xs,
                Ys = ys,
                Values = values,
                GradientXs = gradientXs,
                GradientYs = gradientYs,
                Whole = whole,
                Batches = batches,
                Batch = batch,
            }.Schedule(handle);
            handle = new GiSenseWriteJob
            {
                View = _view,
                Context = context,
                Offsets = offsets,
                Xs = xs.AsDeferredJobArray(),
                Ys = ys.AsDeferredJobArray(),
            }.ScheduleParallel(binding.Query, handle);

            handle = new GiSenseReadJob
            {
                Kind = spec.Kind,
                Layer = new GiLayerKey(spec.Layer),
                Batch = batch,
                Batches = batches,
                Context = context,
                Rows = rows,
                Xs = xs.AsDeferredJobArray(),
                Ys = ys.AsDeferredJobArray(),
                Values = values.AsDeferredJobArray(),
                GradientXs = gradientXs.AsDeferredJobArray(),
                GradientYs = gradientYs.AsDeferredJobArray(),
                Whole = whole.AsDeferredJobArray(),
            }.Schedule(batches, 1, GiRuntime.BeginRead(handle));
            GiRuntime.EndRead(handle);

            for (var t = 0; t < binding.Types.Length; t++)
            {
                var type = binding.Types[t];
                type.Handle.Update(this);
                handle = new GiSenseScatterJob
                {
                    View = _view,
                    Handle = type.Handle,
                    TypeSize = type.Size,
                    Value = type.Value,
                    Gradient = type.Gradient,
                    Coverage = type.Coverage,
                    Rows = rows,
                    Offsets = offsets,
                    Values = values,
                    GradientXs = gradientXs,
                    GradientYs = gradientYs,
                    Whole = whole,
                }.ScheduleParallel(binding.Query, handle);
            }

            return handle;
        }

        private void Rebuild(ref GiProfileBlob blob)
        {
            Dispose();
            _builtHash = blob.Hash;
            var list = new System.Collections.Generic.List<GiSenseBinding>();
            var specs = new System.ReadOnlySpan<GiBindingSpec>(blob.Bindings.GetUnsafePtr(), blob.Bindings.Length);
            var statuses = new GiBindingStatus[specs.Length];
            GiBindingTable.Validate(specs, blob.Layers.Length, GiBindingSupport.Basic, statuses);
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

        private GiSenseBinding Resolve(int index, in GiBindingSpec spec)
        {
            var types = new System.Collections.Generic.List<GiSenseType>();
            var all = new System.Collections.Generic.List<ComponentType>();
            var present = new System.Collections.Generic.List<ComponentType>();
            if (!Bind(index, types, spec.Value, GiSenseSlot.Value) || !Bind(index, types, spec.Gradient, GiSenseSlot.Gradient) ||
                !Bind(index, types, spec.Coverage, GiSenseSlot.Coverage))
                return null;

            foreach (var type in types)
            {
                var kindIsCoverage = type.Coverage.Active && !type.Value.Active && !type.Gradient.Active;
                (kindIsCoverage ? present : all).Add(ComponentType.ReadWrite(type.Index));
                type.Handle = GetDynamicComponentTypeHandle(ComponentType.ReadWrite(type.Index));
            }

            var query = EntityManager.CreateEntityQuery(new EntityQueryDesc { All = all.ToArray(), Present = present.ToArray() });
            return new GiSenseBinding { Spec = spec, Query = query, Types = types.ToArray() };
        }

        private bool Bind(int index, System.Collections.Generic.List<GiSenseType> types, in GiBindingField field, GiSenseSlot slot)
        {
            if (field.TypeHash == 0ul) return true;

            var typeIndex = TypeManager.GetTypeIndexFromStableTypeHash(field.TypeHash);
            if (typeIndex == TypeIndex.Null)
            {
                Report(index, "An output field names a component type that is not in this build.");
                return false;
            }

            var info = TypeManager.GetTypeInfo(typeIndex);
            var size = info.IsZeroSized ? 0 : info.TypeSize;
            if (field.Offset + field.Size > size)
            {
                Report(index, "An output field lies outside its component.");
                return false;
            }

            GiSenseType type = null;
            foreach (var candidate in types)
                if (candidate.Index == typeIndex)
                    type = candidate;
            if (type == null)
            {
                type = new GiSenseType { Index = typeIndex, Size = size };
                types.Add(type);
            }

            var target = new GiSenseField { Active = true, Offset = field.Offset, Size = field.Size };
            switch (slot)
            {
                case GiSenseSlot.Value:
                    type.Value = target;
                    break;
                case GiSenseSlot.Gradient:
                    type.Gradient = target;
                    break;
                default:
                    type.Coverage = target;
                    break;
            }

            return true;
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

        private enum GiSenseSlot : byte
        {
            Value,
            Gradient,
            Coverage,
        }
    }
}
