using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Transforms;
using GiWorld = Gi.World;

namespace Gi.Entities.Presentation
{
    public partial struct GiInspectorSystem
    {
        private void ServeExplain(ref SystemState state, string name, in GiFrame frame)
        {
            while (GiInspectorBoard.TryDequeueExplain(name, out var request)) GiInspectorBoard.StoreExplain(Explain(request, frame));
            while (GiInspectorBoard.TryDequeueEntity(name, out var request)) GiInspectorBoard.StoreEntity(Report(ref state, request, frame));
        }

        private GiExplainResult Explain(in GiExplainRequest request, in GiFrame frame)
        {
            var result = new GiExplainResult { Id = request.Id, Tick = frame.Tick, Layer = request.Layer, X = request.X, Y = request.Y };
            if (!frame.Tables.TryLayer(new GiLayerKey(request.Layer), out var layer)) return result;

            GiRuntime.CompleteAll();
            using var components = _sources.ToComponentDataArray<GiSource>(Allocator.Temp);
            using var entities = _sources.ToEntityArray(Allocator.Temp);
            var owners = new Dictionary<GiWorld.SourceId, Entity>(components.Length);
            var ids = new List<GiWorld.SourceId>(components.Length);
            for (var i = 0; i < components.Length; i++)
            {
                if (!components[i].Placed) continue;

                owners[components[i].Id] = entities[i];
                ids.Add(components[i].Id);
            }

            var output = new GiContribution[Math.Max(1, request.Capacity)];
            result.Explanation = GiExplain.Cell(frame.World, layer, request.X, request.Y, ids.ToArray(), output);
            result.Valid = result.Explanation.Ok;
            result.Contributions = new GiContribution[result.Explanation.Count];
            result.Entities = new Entity[result.Explanation.Count];
            for (var i = 0; i < result.Explanation.Count; i++)
            {
                result.Contributions[i] = output[i];
                result.Entities[i] = owners.TryGetValue(output[i].Source, out var owner) ? owner : Entity.Null;
            }

            if (!result.Valid || _profiles.IsEmptyIgnoreFilter) return result;

            var blob = _profiles.GetSingleton<GiProfileReference>().Value;
            if (!blob.IsCreated) return result;

            ref var profile = ref blob.Value;
            var specs = new GiLayerSpec[profile.Layers.Length];
            var mapped = new byte[specs.Length];
            for (var i = 0; i < specs.Length; i++)
            {
                specs[i] = profile.Layers[i];
                frame.Tables.TryLayer(new GiLayerKey((byte)i), out mapped[i]);
            }

            var steps = new GiRecipeStep[GiRecipe.MaxLayers];
            var count = GiRecipe.Trace(frame.World, specs, mapped, request.Layer, request.X, request.Y, steps);
            result.Steps = new GiRecipeStep[count];
            Array.Copy(steps, result.Steps, count);
            return result;
        }

        private GiEntityReport Report(ref SystemState state, in GiEntityRequest request, in GiFrame frame)
        {
            var report = new GiEntityReport { Id = request.Id, Tick = frame.Tick, Entity = request.Entity };
            var manager = state.EntityManager;
            if (request.Entity == Entity.Null || !manager.Exists(request.Entity)) return report;

            GiRuntime.CompleteAll();
            report.Exists = true;
            var entity = request.Entity;
            report.HasEmitter = manager.HasComponent<GiEmitter>(entity);
            if (report.HasEmitter)
            {
                var emitter = manager.GetComponentData<GiEmitter>(entity);
                report.Enabled = manager.IsComponentEnabled<GiEmitter>(entity);
                report.DesiredLayer = emitter.Layer.Index;
                report.DesiredStamp = emitter.Stamp.Index;
                report.DesiredGain = emitter.Gain;
            }

            report.HasSource = manager.HasComponent<GiSource>(entity);
            if (report.HasSource)
            {
                var source = manager.GetComponentData<GiSource>(entity);
                report.Placed = source.Placed;
                report.SourceId = source.Id;
                report.AppliedLayer = source.Layer.Index;
                report.AppliedStamp = source.Stamp.Index;
                report.AppliedGain = source.Gain;
                if (source.Placed) report.Live = GiWorld.TryGetSourceSpec(frame.World, source.Id, out report.Spec);
            }

            if (manager.HasComponent<GiFault>(entity) && manager.IsComponentEnabled<GiFault>(entity))
            {
                report.HasFault = true;
                report.Fault = manager.GetComponentData<GiFault>(entity).Code;
            }

            if (manager.HasComponent<LocalToWorld>(entity))
            {
                var matrix = manager.GetComponentData<LocalToWorld>(entity).Value;
                frame.Plane.Project(matrix.c3.x, matrix.c3.y, matrix.c3.z, out report.X, out report.Y);
                report.HasPosition = true;
            }
            else if (manager.HasComponent<LocalTransform>(entity))
            {
                var position = manager.GetComponentData<LocalTransform>(entity).Position;
                frame.Plane.Project(position.x, position.y, position.z, out report.X, out report.Y);
                report.HasPosition = true;
            }
            else if (report.Live)
            {
                report.X = report.Spec.X;
                report.Y = report.Spec.Y;
                report.HasPosition = true;
            }

            if (!report.HasPosition) return report;

            var readings = new GiEntityLayerReading[frame.Tables.LayerCount];
            for (var i = 0; i < readings.Length; i++)
            {
                readings[i].LayerKey = i;
                if (!frame.Tables.TryLayer(new GiLayerKey((byte)i), out var layer)) continue;

                var probe = new GiProbeRequest { Kind = GiProbeKind.Value, World = frame.World, Layer = layer, X = report.X, Y = report.Y };
                var value = GiProbe.Run(probe);
                probe.Kind = GiProbeKind.Gradient;
                var gradient = GiProbe.Run(probe);
                readings[i].Ok = value.Ok;
                readings[i].Value = value.Value;
                readings[i].GradientX = gradient.GradientX;
                readings[i].GradientY = gradient.GradientY;
            }

            report.Layers = readings;
            return report;
        }
    }
}
