using BovineLabs.Core;
using Unity.Entities;
using UnityEngine;
using GiGrid = Gi.Grid;

namespace Gi.Entities.Presentation
{
    [WorldSystemFilter(Worlds.SimulationEditor)]
    [UpdateInGroup(typeof(PresentationSystemGroup))]
    public partial class GiInspectorSystem : SystemBase
    {
        private GiWorldInfo _info;
        private EntityQuery _sources;
        private EntityQuery _profiles;

        protected override void OnCreate()
        {
            RequireForUpdate<GiFrame>();
            _sources = GetEntityQuery(ComponentType.ReadOnly<GiSource>());
            _profiles = GetEntityQuery(ComponentType.ReadOnly<GiProfileReference>());
        }

        protected override void OnDestroy()
        {
            GiInspectorBoard.Retire(World.Name);
        }

        protected override void OnUpdate()
        {
            var entity = SystemAPI.GetSingletonEntity<GiFrame>();
            var frame = EntityManager.GetComponentData<GiFrame>(entity);
            var name = World.Name;
            Publish(name, frame);
            if (!frame.Alive)
            {
                DetachViews(name);
                return;
            }

            while (GiInspectorBoard.TryDequeue(name, out var request)) Serve(request, frame);
            ServeExplain(name, frame);
            UpdateViews(name, frame);
        }

        private void Publish(string name, in GiFrame frame)
        {
            if (_info != null && _info.Alive == frame.Alive && _info.Generation == frame.Generation && _info.GiWorld == frame.World)
            {
                _info.Tick = frame.Tick;
                _info.Faults = frame.Faults.Raised;
                _info.DesyncTick = frame.Faults.FirstTick(GiFaultKind.Desync);
                return;
            }

            var info = new GiWorldInfo
            {
                EcsWorld = name,
                GiWorld = frame.World,
                Alive = frame.Alive,
                Generation = frame.Generation,
                Tick = frame.Tick,
                Faults = frame.Faults.Raised,
                DesyncTick = frame.Faults.FirstTick(GiFaultKind.Desync),
                LayerCount = frame.Alive ? frame.Tables.LayerCount : 0,
            };
            if (frame.Alive)
            {
                info.Grids = new GiGridInfo[frame.Tables.GridCount];
                for (var i = 0; i < info.Grids.Length; i++)
                {
                    frame.Tables.TryGrid(i, out var gridId);
                    GiGrid.TryGetSpec(frame.World, gridId, out var power, out var x, out var y, out var size);
                    info.Grids[i] = new GiGridInfo { Index = i, GridId = gridId, Power = power, X = x, Y = y, Size = size };
                }
            }

            _info = info;
            GiInspectorBoard.Publish(name, info);
        }

        private static void Serve(GiProbeRequest request, in GiFrame frame)
        {
            if (!frame.Tables.TryLayer(new GiLayerKey(request.Layer), out var layer))
            {
                GiInspectorBoard.Store(new GiProbeResult { Id = request.Id, Kind = request.Kind, Tick = frame.Tick });
                return;
            }

            request.World = frame.World;
            request.Layer = layer;
            GiInspectorBoard.Store(GiProbe.Run(request));
        }

        private static void DetachViews(string name)
        {
            for (var i = 0; i < GiInspectorBoard.ViewCount; i++)
            {
                var view = GiInspectorBoard.ViewAt(i);
                if (view.EcsWorld == name) view.Detach();
            }
        }

        private static void UpdateViews(string name, in GiFrame frame)
        {
            for (var i = 0; i < GiInspectorBoard.ViewCount; i++)
            {
                var view = GiInspectorBoard.ViewAt(i);
                if (view.EcsWorld != name || view.Disposed) continue;

                if (view.Mirror != null && view.Generation != frame.Generation) view.Detach();
                if (view.Mirror == null && !Attach(view, frame)) continue;

                var uploaded = view.Mirror.Update(GiMirrorConfig.Budget.Data);
                if (uploaded > 0 || view.Dirty)
                {
                    view.Mirror.Draw(view.Texture, view.Region, view.ValueScale);
                    view.Dirty = false;
                }
            }
        }

        private static bool Attach(GiView view, in GiFrame frame)
        {
            if (!SystemInfo.supportsComputeShaders) return false;
            if (!frame.Tables.TryGrid(view.Grid, out var gridId) || !frame.Tables.TryLayer(new GiLayerKey((byte)view.LayerKey), out var layer)) return false;
            if (!GiGrid.TryGetSpec(frame.World, gridId, out var power, out _, out _, out _)) return false;

            view.Attach(new GiMirror(frame.World, gridId, layer, power), frame.Generation);
            return true;
        }
    }
}
