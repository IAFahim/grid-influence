using System;
using System.Collections.Generic;
using Unity.Entities;
using GiWorld = Gi.World;

namespace Gi.Entities.Presentation
{
    public struct GiExplainRequest
    {
        public int Id;
        public byte Layer;
        public float X;
        public float Y;
        public int Capacity;
    }

    public sealed class GiExplainResult
    {
        public int Id;
        public int Tick;
        public byte Layer;
        public float X;
        public float Y;
        public bool Valid;
        public GiExplanation Explanation;
        public GiContribution[] Contributions = Array.Empty<GiContribution>();
        public Entity[] Entities = Array.Empty<Entity>();
        public GiRecipeStep[] Steps = Array.Empty<GiRecipeStep>();
    }

    public struct GiEntityRequest
    {
        public int Id;
        public Entity Entity;
    }

    public struct GiEntityLayerReading
    {
        public int LayerKey;
        public bool Ok;
        public short Value;
        public float GradientX;
        public float GradientY;
    }

    public sealed class GiEntityReport
    {
        public int Id;
        public int Tick;
        public Entity Entity;
        public bool Exists;
        public bool HasEmitter;
        public bool Enabled;
        public byte DesiredLayer;
        public byte DesiredStamp;
        public sbyte DesiredGain;
        public bool HasSource;
        public bool Placed;
        public GiWorld.SourceId SourceId;
        public byte AppliedLayer;
        public byte AppliedStamp;
        public sbyte AppliedGain;
        public bool Live;
        public GiWorld.SourceSpec Spec;
        public bool HasFault;
        public GiFaultCode Fault;
        public bool HasPosition;
        public float X;
        public float Y;
        public GiEntityLayerReading[] Layers = Array.Empty<GiEntityLayerReading>();

        public bool Drift => HasEmitter && HasSource && Placed && (DesiredLayer != AppliedLayer || DesiredStamp != AppliedStamp || DesiredGain != AppliedGain);
    }

    public static partial class GiInspectorBoard
    {
        private const int ExplainLimit = 64;

        private static readonly List<(string World, GiExplainRequest Request)> ExplainRequests = new List<(string, GiExplainRequest)>();
        private static readonly Dictionary<int, GiExplainResult> ExplainResults = new Dictionary<int, GiExplainResult>();
        private static readonly Queue<int> ExplainOrder = new Queue<int>();
        private static readonly List<(string World, GiEntityRequest Request)> EntityRequests = new List<(string, GiEntityRequest)>();
        private static readonly Dictionary<int, GiEntityReport> EntityResults = new Dictionary<int, GiEntityReport>();
        private static readonly Queue<int> EntityOrder = new Queue<int>();

        public static int RequestExplain(string ecsWorld, GiExplainRequest request)
        {
            request.Id = ++_nextId;
            ExplainRequests.Add((ecsWorld, request));
            return request.Id;
        }

        public static bool TryTakeExplain(int id, out GiExplainResult result)
        {
            if (!ExplainResults.TryGetValue(id, out result)) return false;

            ExplainResults.Remove(id);
            return true;
        }

        public static int RequestEntity(string ecsWorld, Entity entity)
        {
            var request = new GiEntityRequest { Id = ++_nextId, Entity = entity };
            EntityRequests.Add((ecsWorld, request));
            return request.Id;
        }

        public static bool TryTakeEntity(int id, out GiEntityReport report)
        {
            if (!EntityResults.TryGetValue(id, out report)) return false;

            EntityResults.Remove(id);
            return true;
        }

        internal static bool TryDequeueExplain(string ecsWorld, out GiExplainRequest request)
        {
            for (var i = 0; i < ExplainRequests.Count; i++)
            {
                if (ExplainRequests[i].World != ecsWorld) continue;

                request = ExplainRequests[i].Request;
                ExplainRequests.RemoveAt(i);
                return true;
            }

            request = default;
            return false;
        }

        internal static bool TryDequeueEntity(string ecsWorld, out GiEntityRequest request)
        {
            for (var i = 0; i < EntityRequests.Count; i++)
            {
                if (EntityRequests[i].World != ecsWorld) continue;

                request = EntityRequests[i].Request;
                EntityRequests.RemoveAt(i);
                return true;
            }

            request = default;
            return false;
        }

        internal static void StoreExplain(GiExplainResult result)
        {
            ExplainResults[result.Id] = result;
            ExplainOrder.Enqueue(result.Id);
            while (ExplainOrder.Count > ExplainLimit) ExplainResults.Remove(ExplainOrder.Dequeue());
        }

        internal static void StoreEntity(GiEntityReport report)
        {
            EntityResults[report.Id] = report;
            EntityOrder.Enqueue(report.Id);
            while (EntityOrder.Count > ExplainLimit) EntityResults.Remove(EntityOrder.Dequeue());
        }

        private static void RetireRequests(string ecsWorld)
        {
            for (var i = ExplainRequests.Count - 1; i >= 0; i--)
                if (ExplainRequests[i].World == ecsWorld)
                    ExplainRequests.RemoveAt(i);

            for (var i = EntityRequests.Count - 1; i >= 0; i--)
                if (EntityRequests[i].World == ecsWorld)
                    EntityRequests.RemoveAt(i);
        }
    }
}
