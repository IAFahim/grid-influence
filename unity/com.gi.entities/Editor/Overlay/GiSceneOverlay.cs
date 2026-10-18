using System.Collections.Generic;
using Gi.Entities.Authoring;
using Gi.Entities.Presentation;
using Unity.Entities;
using UnityEditor;
using UnityEngine;
using Unity.Scripting.LifecycleManagement;

namespace Gi.Entities.Editor
{
    [InitializeOnLoad]
    [NoAutoStaticsCleanup]
    public static class GiSceneOverlay
    {
        private const int Lattice = 48;

        private static GiOverlaySettings _settings;

        static GiSceneOverlay()
        {
            SceneView.duringSceneGui += Draw;
        }

        public static GiOverlaySettings Settings => _settings ??= GiOverlaySettingsStore.Load();

        public static void Save()
        {
            GiOverlaySettingsStore.Save(Settings);
        }

        [MenuItem("Gi/Toggle Scene Overlay Reach Discs")]
        private static void ToggleReach()
        {
            Settings.ReachDiscs = !Settings.ReachDiscs;
            Save();
            SceneView.RepaintAll();
        }

        [MenuItem("Gi/Toggle Scene Overlay All Footprints")]
        private static void ToggleAllFootprints()
        {
            Settings.AllFootprints = !Settings.AllFootprints;
            Save();
            SceneView.RepaintAll();
        }

        [MenuItem("Gi/Toggle Scene Overlay Heatmap")]
        private static void ToggleHeatmap()
        {
            Settings.Heatmap = !Settings.Heatmap;
            Save();
            SceneView.RepaintAll();
        }

        private static void Draw(SceneView view)
        {
            var settings = Settings;
            foreach (var info in GiInspectorBoard.All)
            {
                if (!info.Alive) continue;

                var grid = settings.Grid < info.Grids.Length ? info.Grids[settings.Grid] : null;
                if (grid == null) continue;

                if (settings.GridBounds) DrawGrids(info);
                if (settings.Heatmap) DrawHeatmap(info, grid, settings);
                if (settings.Footprints) DrawFootprints(info, settings);
                if (settings.ReachDiscs) DrawReaches(info);
            }
        }

        private static Vector3 Embed(Vector2 plane, GiPlaneAxes axes)
        {
            return axes == GiPlaneAxes.Xz ? new Vector3(plane.x, 0f, plane.y) : new Vector3(plane.x, plane.y, 0f);
        }

        private static void DrawGrids(GiWorldInfo info)
        {
            var axes = Axes(info);
            Handles.color = new Color(0.1f, 0.5f, 0.8f, 0.8f);
            foreach (var grid in info.Grids)
            {
                var a = Embed(new Vector2(grid.X, grid.Y), axes);
                var b = Embed(new Vector2(grid.X + grid.Size, grid.Y), axes);
                var c = Embed(new Vector2(grid.X + grid.Size, grid.Y + grid.Size), axes);
                var d = Embed(new Vector2(grid.X, grid.Y + grid.Size), axes);
                Handles.DrawPolyLine(a, b, c, d, a);
            }
        }

        private static void DrawHeatmap(GiWorldInfo info, GiGridInfo grid, GiOverlaySettings settings)
        {
            var axes = Axes(info);
            var step = grid.Size / Lattice;
            for (var row = 0; row < Lattice; row++)
            for (var column = 0; column < Lattice; column++)
            {
                var x = grid.X + (column + 0.5f) * step;
                var y = grid.Y + (row + 0.5f) * step;
                var value = Sample(info, settings.Layer, x, y);
                if (value == 0) continue;

                var t = Mathf.Clamp(value / settings.ValueScale, -1f, 1f);
                var color = t >= 0f ? Color.Lerp(Color.white, new Color(0.90f, 0.45f, 0.05f), t) : Color.Lerp(Color.white, new Color(0.05f, 0.35f, 0.75f), -t);
                color.a = 0.55f;
                var half = step * 0.5f;
                var verts = new[]
                {
                    Embed(new Vector2(x - half, y - half), axes), Embed(new Vector2(x + half, y - half), axes),
                    Embed(new Vector2(x + half, y + half), axes), Embed(new Vector2(x - half, y + half), axes),
                };
                Handles.DrawSolidRectangleWithOutline(verts, color, Color.clear);
            }
        }

        private static short Sample(GiWorldInfo info, int layerKey, float x, float y)
        {
            var world = GiOverlayWorld(info);
            if (world == null) return 0;

            using var query = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GiFrame>());
            if (query.IsEmptyIgnoreFilter) return 0;

            var frame = world.EntityManager.GetComponentData<GiFrame>(query.GetSingletonEntity());
            if (!frame.Alive || !frame.Tables.TryLayer(new GiLayerKey((byte)layerKey), out var layer)) return 0;

            global::Gi.World.TrySense(frame.World, layer, x, y, out var value);
            return value;
        }

        private static void DrawReaches(GiWorldInfo info)
        {
            var world = GiOverlayWorld(info);
            if (world == null) return;

            var axes = Axes(info);
            var normal = axes == GiPlaneAxes.Xz ? Vector3.up : Vector3.forward;
            foreach (var draw in GiOverlayModel.Reaches(world, GiOverlaySelection.Entities(world)))
            {
                Handles.color = ReachColor(draw.Kind);
                Handles.DrawWireDisc(Embed(draw.Center, axes), normal, draw.Radius);
            }
        }

        public static Color ReachColor(GiBindingKind kind)
        {
            switch (kind)
            {
                case GiBindingKind.Area: return new Color(0.0f, 0.62f, 0.45f, 0.9f);
                case GiBindingKind.Max: return new Color(0.94f, 0.89f, 0.26f, 0.9f);
                case GiBindingKind.Nearest: return new Color(0.80f, 0.47f, 0.65f, 0.9f);
                default: return new Color(0.34f, 0.71f, 0.91f, 0.9f);
            }
        }

        private static void DrawFootprints(GiWorldInfo info, GiOverlaySettings settings)
        {
            var world = GiOverlayWorld(info);
            if (world == null) return;

            var profile = FindProfile();
            if (profile == null) return;

            List<Entity> selected;
            if (settings.AllFootprints)
            {
                selected = new List<Entity>();
                using var query = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GiEmitter>());
                using var entities = query.ToEntityArray(Unity.Collections.Allocator.Temp);
                foreach (var entity in entities) selected.Add(entity);
            }
            else
            {
                selected = GiOverlaySelection.Entities(world);
            }

            var axes = Axes(info);
            Handles.color = new Color(0.9f, 0.2f, 0.2f, 0.9f);
            foreach (var draw in GiOverlayModel.Footprints(world, profile.ToSpec(), selected))
            {
                if (draw.Round) Handles.DrawWireDisc(Embed(draw.Center, axes), axes == GiPlaneAxes.Xz ? Vector3.up : Vector3.forward, draw.Radius);
                else Handles.DrawPolyLine(Embed(draw.Corners[0], axes), Embed(draw.Corners[1], axes), Embed(draw.Corners[2], axes), Embed(draw.Corners[3], axes), Embed(draw.Corners[0], axes));
            }
        }

        private static GiProfile FindProfile()
        {
            foreach (var authoring in Object.FindObjectsByType<GiProfileAuthoring>())
                if (authoring.Profile != null)
                    return authoring.Profile;
            return null;
        }

        private static GiPlaneAxes Axes(GiWorldInfo info)
        {
            var world = GiOverlayWorld(info);
            if (world == null) return GiPlaneAxes.Xz;

            using var query = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GiFrame>());
            return query.IsEmptyIgnoreFilter ? GiPlaneAxes.Xz : world.EntityManager.GetComponentData<GiFrame>(query.GetSingletonEntity()).Plane.Axes;
        }

        private static Unity.Entities.World GiOverlayWorld(GiWorldInfo info)
        {
            foreach (var world in Unity.Entities.World.All)
                if (world.Name == info.EcsWorld)
                    return world;
            return null;
        }
    }
}
