using System;
using System.Collections.Generic;

namespace Gi.Entities.Presentation
{
    public sealed class GiGridInfo
    {
        public int Index;
        public byte GridId;
        public int Power;
        public float X;
        public float Y;
        public float Size;

        public int Cells => 1 << Power;

        public float CellsPerUnit => Cells / Size;
    }

    public sealed class GiWorldInfo
    {
        public string EcsWorld;
        public byte GiWorld;
        public bool Alive;
        public uint Generation;
        public int Tick;
        public GiGridInfo[] Grids = Array.Empty<GiGridInfo>();
        public int LayerCount;
    }

    public static partial class GiInspectorBoard
    {
        private const int ResultLimit = 256;

        private static readonly List<GiWorldInfo> Worlds = new List<GiWorldInfo>();
        private static readonly List<GiView> Views = new List<GiView>();
        private static readonly List<(string World, GiProbeRequest Request)> Requests = new List<(string, GiProbeRequest)>();
        private static readonly Dictionary<int, GiProbeResult> Results = new Dictionary<int, GiProbeResult>();
        private static readonly Queue<int> ResultOrder = new Queue<int>();
        private static int _nextId;

        public static event Action WorldsChanged;

        public static IReadOnlyList<GiWorldInfo> All => Worlds;

        public static int OpenViews => Views.Count;

        public static GiWorldInfo Find(string ecsWorld)
        {
            foreach (var world in Worlds)
                if (world.EcsWorld == ecsWorld)
                    return world;
            return null;
        }

        public static GiView Open(string ecsWorld, int grid, int layerKey)
        {
            var view = new GiView(ecsWorld, grid, layerKey);
            Views.Add(view);
            return view;
        }

        public static void Close(GiView view)
        {
            view.Dispose();
            Views.Remove(view);
        }

        public static int Request(string ecsWorld, GiProbeRequest request)
        {
            request.Id = ++_nextId;
            Requests.Add((ecsWorld, request));
            return request.Id;
        }

        public static bool TryTake(int id, out GiProbeResult result)
        {
            if (!Results.TryGetValue(id, out result)) return false;

            Results.Remove(id);
            return true;
        }

        internal static int ViewCount => Views.Count;

        internal static GiView ViewAt(int index) => Views[index];

        internal static int RequestCount => Requests.Count;

        internal static bool TryDequeue(string ecsWorld, out GiProbeRequest request)
        {
            for (var i = 0; i < Requests.Count; i++)
            {
                if (Requests[i].World != ecsWorld) continue;

                request = Requests[i].Request;
                Requests.RemoveAt(i);
                return true;
            }

            request = default;
            return false;
        }

        internal static void Store(in GiProbeResult result)
        {
            Results[result.Id] = result;
            ResultOrder.Enqueue(result.Id);
            while (ResultOrder.Count > ResultLimit) Results.Remove(ResultOrder.Dequeue());
        }

        internal static void Publish(string ecsWorld, GiWorldInfo info)
        {
            for (var i = 0; i < Worlds.Count; i++)
            {
                if (Worlds[i].EcsWorld != ecsWorld) continue;

                Worlds[i] = info;
                WorldsChanged?.Invoke();
                return;
            }

            Worlds.Add(info);
            WorldsChanged?.Invoke();
        }

        internal static void Retire(string ecsWorld)
        {
            for (var i = Views.Count - 1; i >= 0; i--)
            {
                if (Views[i].EcsWorld != ecsWorld) continue;

                Views[i].Dispose();
                Views.RemoveAt(i);
            }

            for (var i = Requests.Count - 1; i >= 0; i--)
                if (Requests[i].World == ecsWorld)
                    Requests.RemoveAt(i);

            RetireRequests(ecsWorld);

            for (var i = Worlds.Count - 1; i >= 0; i--)
            {
                if (Worlds[i].EcsWorld != ecsWorld) continue;

                Worlds.RemoveAt(i);
                WorldsChanged?.Invoke();
            }
        }
    }
}
