using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using GiWorld = Gi.World;

namespace Gi.Entities
{
    public unsafe partial struct GiSenseSystem
    {
        private JobHandle Schedule(ref SystemState state, GiSenseBinding binding, Entity frame, NativeReference<GiReconcileContext> context,
            int batch, Allocator allocator, JobHandle handle)
        {
            var spec = binding.Spec;
            for (var t = 0; t < binding.Types.Length; t++) binding.Types[t].Handle.Update(ref state);
            var chunkCount = binding.Query.CalculateChunkCountWithoutFiltering();
            var counts = CollectionHelper.CreateNativeArray<int>(chunkCount, allocator);
            var moved = CollectionHelper.CreateNativeArray<int>(chunkCount, allocator);
            var offsets = CollectionHelper.CreateNativeArray<int>(chunkCount, allocator);
            var rows = CollectionHelper.CreateNativeArray<int>(2, allocator);
            var xs = new NativeList<float>(allocator);
            var ys = new NativeList<float>(allocator);
            var reaches = new NativeList<float>(allocator);
            var thresholds = new NativeList<short>(allocator);
            var excludes = new NativeList<GiWorld.SourceId>(allocator);
            var values = new NativeList<short>(allocator);
            var gradientXs = new NativeList<float>(allocator);
            var gradientYs = new NativeList<float>(allocator);
            var totals = new NativeList<long>(allocator);
            var whole = new NativeList<ulong>(allocator);
            var batches = new NativeList<int>(allocator);
            var inputs = GateInputs(binding, allocator);

            handle = new GiSenseCountJob
            {
                View = _view,
                Context = context,
                Period = binding.Period,
                LastVersion = state.LastSystemVersion,
                WatchSources = (byte)((spec.Flags & GiBindingSpec.ExcludeSelf) != 0 ? 1 : 0),
                Inputs = inputs,
                Counts = counts,
                Moved = moved,
            }.ScheduleParallel(binding.Query, handle);
            handle = new GiSenseGateJob
            {
                Kind = spec.Kind,
                Layer = new GiLayerKey(spec.Layer),
                Reach = spec.Reach,
                Since = spec.Since,
                Needs = (byte)binding.Needs,
                Eligible = (byte)(binding.GateEligible ? 1 : 0),
                Context = context,
                Counts = counts,
                Moved = moved,
                Grids = binding.GateGrids,
                PrevXs = binding.PrevXs,
                PrevYs = binding.PrevYs,
                PrevReach = binding.PrevReach,
                Scratch = binding.GateScratch,
                Gate = binding.GateState,
            }.Schedule(GiRuntime.BeginRead(handle));
            handle = new GiSensePrefixJob
            {
                Layer = new GiLayerKey(spec.Layer),
                Context = context,
                Stats = frame,
                Lookup = _stats,
                Needs = (byte)binding.Needs,
                Gate = binding.GateState,
                Counts = counts,
                Offsets = offsets,
                Rows = rows,
                Xs = xs,
                Ys = ys,
                Reaches = reaches,
                Thresholds = thresholds,
                Excludes = excludes,
                Values = values,
                GradientXs = gradientXs,
                GradientYs = gradientYs,
                Totals = totals,
                Whole = whole,
                Batches = batches,
                PrevXs = binding.PrevXs,
                PrevYs = binding.PrevYs,
                PrevReach = binding.PrevReach,
                Batch = batch,
            }.Schedule(handle);
            handle = new GiSenseWriteJob
            {
                View = _view,
                Context = context,
                Gate = binding.GateState,
                Period = binding.Period,
                Reach = spec.Reach,
                Threshold = spec.Threshold,
                Offsets = offsets,
                Xs = xs.AsDeferredJobArray(),
                Ys = ys.AsDeferredJobArray(),
                Reaches = reaches.AsDeferredJobArray(),
                Thresholds = thresholds.AsDeferredJobArray(),
                Excludes = excludes.AsDeferredJobArray(),
                PrevXs = binding.PrevXs.AsDeferredJobArray(),
                PrevYs = binding.PrevYs.AsDeferredJobArray(),
                PrevReach = binding.PrevReach.AsDeferredJobArray(),
            }.ScheduleParallel(binding.Query, handle);

            for (var t = 0; t < binding.Types.Length; t++)
            {
                var type = binding.Types[t];
                if (!type.HasInput) continue;

                type.Handle.Update(ref state);
                if (type.Reach.Active)
                    handle = Gather(binding, type, type.Reach, false, context, offsets, reaches, thresholds, handle);
                if (type.Threshold.Active)
                    handle = Gather(binding, type, type.Threshold, true, context, offsets, reaches, thresholds, handle);
            }

            handle = new GiSenseReadJob
            {
                Kind = spec.Kind,
                Layer = new GiLayerKey(spec.Layer),
                Batch = batch,
                Reach = spec.Reach,
                Since = spec.Since,
                Batches = batches,
                Context = context,
                Gate = binding.GateState,
                Rows = rows,
                Xs = xs.AsDeferredJobArray(),
                Ys = ys.AsDeferredJobArray(),
                Reaches = reaches.AsDeferredJobArray(),
                Thresholds = thresholds.AsDeferredJobArray(),
                Excludes = excludes.AsDeferredJobArray(),
                Values = values.AsDeferredJobArray(),
                GradientXs = gradientXs.AsDeferredJobArray(),
                GradientYs = gradientYs.AsDeferredJobArray(),
                Totals = totals.AsDeferredJobArray(),
                Whole = whole.AsDeferredJobArray(),
            }.Schedule(batches, 1, GiRuntime.BeginRead(handle));
            GiRuntime.EndRead(handle);

            for (var t = 0; t < binding.Types.Length; t++)
            {
                var type = binding.Types[t];
                if (!type.HasOutput && !type.Coverage.Active) continue;

                type.Handle.Update(ref state);
                handle = new GiSenseScatterJob
                {
                    View = _view,
                    Handle = type.Handle,
                    TypeSize = type.Size,
                    Period = binding.Period,
                    Value = type.Value,
                    Gradient = type.Gradient,
                    Total = type.Total,
                    Position = type.Position,
                    Coverage = type.Coverage,
                    Context = context,
                    Gate = binding.GateState,
                    Rows = rows,
                    Offsets = offsets,
                    Values = values,
                    GradientXs = gradientXs,
                    GradientYs = gradientYs,
                    Totals = totals,
                    Whole = whole,
                }.ScheduleParallel(binding.Query, handle);
            }

            return handle;
        }

        private JobHandle Gather(GiSenseBinding binding, GiSenseType type, GiSenseField field, bool threshold,
            NativeReference<GiReconcileContext> context, NativeArray<int> offsets, NativeList<float> reaches,
            NativeList<short> thresholds, JobHandle handle)
        {
            return new GiSenseGatherJob
            {
                View = _view,
                Handle = type.Handle,
                TypeSize = type.Size,
                Offset = field.Offset,
                Threshold = (byte)(threshold ? 1 : 0),
                Period = binding.Period,
                Context = context,
                Gate = binding.GateState,
                Offsets = offsets,
                Reaches = reaches.AsDeferredJobArray(),
                Thresholds = thresholds.AsDeferredJobArray(),
                PrevReach = binding.PrevReach.AsDeferredJobArray(),
            }.ScheduleParallel(binding.Query, handle);
        }

        private NativeArray<DynamicComponentTypeHandle> GateInputs(GiSenseBinding binding, Allocator allocator)
        {
            var count = 0;
            for (var t = 0; t < binding.Types.Length; t++)
            {
                var type = binding.Types[t];
                if (type.HasInput && !type.HasOutput && !type.Coverage.Active) count++;
            }

            var inputs = new NativeArray<DynamicComponentTypeHandle>(count, allocator);
            var cursor = 0;
            for (var t = 0; t < binding.Types.Length; t++)
            {
                var type = binding.Types[t];
                if (type.HasInput && !type.HasOutput && !type.Coverage.Active) inputs[cursor++] = type.Handle;
            }

            return inputs;
        }

        private GiSenseBinding Resolve(ref SystemState state, int index, in GiBindingSpec spec)
        {
            var types = new System.Collections.Generic.List<GiSenseType>();
            if (!Bind(ref state, index, types, spec.Value, GiSenseSlot.Value) || !Bind(ref state, index, types, spec.Gradient, GiSenseSlot.Gradient) ||
                !Bind(ref state, index, types, spec.Total, GiSenseSlot.Total) || !Bind(ref state, index, types, spec.Position, GiSenseSlot.Position) ||
                !Bind(ref state, index, types, spec.Coverage, GiSenseSlot.Coverage) || !Bind(ref state, index, types, spec.ReachField, GiSenseSlot.Reach) ||
                !Bind(ref state, index, types, spec.ThresholdField, GiSenseSlot.Threshold))
                return null;

            var all = new System.Collections.Generic.List<ComponentType>();
            var present = new System.Collections.Generic.List<ComponentType>();
            foreach (var type in types)
            {
                var readWrite = type.HasOutput || (type.Coverage.Active && type.HasInput);
                if (readWrite)
                {
                    all.Add(ComponentType.ReadWrite(type.Index));
                    type.Handle = state.EntityManager.GetDynamicComponentTypeHandle(ComponentType.ReadWrite(type.Index));
                }
                else if (type.Coverage.Active)
                {
                    present.Add(ComponentType.ReadWrite(type.Index));
                    type.Handle = state.EntityManager.GetDynamicComponentTypeHandle(ComponentType.ReadWrite(type.Index));
                }
                else
                {
                    all.Add(ComponentType.ReadOnly(type.Index));
                    type.Handle = state.EntityManager.GetDynamicComponentTypeHandle(ComponentType.ReadOnly(type.Index));
                }
            }

            if ((spec.Flags & GiBindingSpec.ExcludeSelf) != 0) all.Add(ComponentType.ReadOnly<GiSource>());

            var query = state.EntityManager.CreateEntityQuery(new EntityQueryDesc { All = all.ToArray(), Present = present.ToArray() });
            return new GiSenseBinding
            {
                Spec = spec,
                Needs = GiBindingTable.NeedsOf(spec),
                Period = GiSensePass.Periods(spec.Period),
                Query = query,
                Types = types.ToArray(),
            };
        }

        private void SetupGate(GiSenseBinding binding, BlobArray<GiGridSpec> grids)
        {
            binding.GateEligible = binding.Period == 1;
            foreach (var type in binding.Types)
                if (type.HasInput && (type.HasOutput || type.Coverage.Active))
                    binding.GateEligible = false;
            binding.GateGrids = new NativeArray<GiGridSpec>(grids.Length, Allocator.Persistent);
            var maxTiles = 0;
            for (var i = 0; i < grids.Length; i++)
            {
                var grid = grids[i];
                binding.GateGrids[i] = grid;
                if (grid.Power >= 5 && grid.Power <= 14)
                {
                    var tiles = 1 << ((grid.Power - 5) * 2);
                    if (tiles > maxTiles) maxTiles = tiles;
                }
            }

            if (maxTiles > GiSensePass.GateScratchLimit) maxTiles = GiSensePass.GateScratchLimit;
            binding.GateScratch = new NativeArray<int>(maxTiles, Allocator.Persistent);
            binding.PrevXs = new NativeList<float>(Allocator.Persistent);
            binding.PrevYs = new NativeList<float>(Allocator.Persistent);
            binding.PrevReach = new NativeList<float>(Allocator.Persistent);
            binding.GateState = new NativeReference<GiSenseGateState>(
                new GiSenseGateState { PrevRows = -1, PrevTick = -1, Skip = 0 }, Allocator.Persistent);
        }

        private bool Bind(ref SystemState state, int index, System.Collections.Generic.List<GiSenseType> types, in GiBindingField field, GiSenseSlot slot)
        {
            if (field.TypeHash == 0ul) return true;

            var typeIndex = TypeManager.GetTypeIndexFromStableTypeHash(field.TypeHash);
            if (typeIndex == TypeIndex.Null)
            {
                Report(ref state, index, "An output field names a component type that is not in this build.");
                return false;
            }

            var info = TypeManager.GetTypeInfo(typeIndex);
            var size = info.IsZeroSized ? 0 : info.TypeSize;
            if (field.Offset + field.Size > size)
            {
                Report(ref state, index, "An output field lies outside its component.");
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
                case GiSenseSlot.Total:
                    type.Total = target;
                    break;
                case GiSenseSlot.Position:
                    type.Position = target;
                    break;
                case GiSenseSlot.Coverage:
                    type.Coverage = target;
                    break;
                case GiSenseSlot.Reach:
                    type.Reach = target;
                    break;
                default:
                    type.Threshold = target;
                    break;
            }

            return true;
        }

        private enum GiSenseSlot : byte
        {
            Value,
            Gradient,
            Total,
            Position,
            Coverage,
            Reach,
            Threshold,
        }
    }
}
