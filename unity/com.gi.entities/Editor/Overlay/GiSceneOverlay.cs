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

        private static readonly Vector3[] Quad = new Vector3[4];

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

                var world = GiOverlayWorld(info);
                var frame = FrameOf(world);
                var axes = frame?.Plane.Axes ?? GiPlaneAxes.Xz;

                if (settings.GridBounds) DrawGrids(info, axes);
                if (settings.Heatmap) DrawHeatmap(frame, grid, settings, axes);
                if (settings.Footprints) DrawFootprints(world, axes, settings);
                if (settings.ReachDiscs) DrawReaches(world, axes);
            }
        }

        private static Vector3 Embed(Vector2 plane, GiPlaneAxes axes)
        {
            return axes == GiPlaneAxes.Xz ? new Vector3(plane.x, 0f, plane.y) : new Vector3(plane.x, plane.y, 0f);
        }

        private static void DrawGrids(GiWorldInfo info, GiPlaneAxes axes)
        {
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

        private static void DrawHeatmap(GiFrame? frame, GiGridInfo grid, GiOverlaySettings settings, GiPlaneAxes axes)
        {
            if (frame == null || !frame.Value.Alive || !frame.Value.Tables.TryLayer(new GiLayerKey((byte)settings.Layer), out var layer)) return;

            var step = grid.Size / Lattice;
            var half = step * 0.5f;
            for (var row = 0; row < Lattice; row++)
            for (var column = 0; column < Lattice; column++)
            {
                var x = grid.X + (column + 0.5f) * step;
                var y = grid.Y + (row + 0.5f) * step;
                global::Gi.World.TrySense(frame.Value.World, layer, x, y, out var value);
                if (value == 0) continue;

                var t = Mathf.Clamp(value / settings.ValueScale, -1f, 1f);
                var color = t >= 0f ? Color.Lerp(Color.white, new Color(0.90f, 0.45f, 0.05f), t) : Color.Lerp(Color.white, new Color(0.05f, 0.35f, 0.75f), -t);
                color.a = 0.55f;
                Quad[0] = Embed(new Vector2(x - half, y - half), axes);
                Quad[1] = Embed(new Vector2(x + half, y - half), axes);
                Quad[2] = Embed(new Vector2(x + half, y + half), axes);
                Quad[3] = Embed(new Vector2(x - half, y + half), axes);
                Handles.DrawSolidRectangleWithOutline(Quad, color, Color.clear);
            }
        }

        private static GiFrame? FrameOf(Unity.Entities.World world)
        {
            if (world == null) return null;

            using var query = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<GiFrame>());
            if (query.IsEmptyIgnoreFilter) return null;

            return world.EntityManager.GetComponentData<GiFrame>(query.GetSingletonEntity());
        }

        private static void DrawReaches(Unity.Entities.World world, GiPlaneAxes axes)
        {
            if (world == null) return;

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

        private static void DrawFootprints(Unity.Entities.World world, GiPlaneAxes axes, GiOverlaySettings settings)
        {
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

        private static Unity.Entities.World GiOverlayWorld(GiWorldInfo info)
        {
            foreach (var world in Unity.Entities.World.All)
                if (world.Name == info.EcsWorld)
                    return world;
            return null;
        }
    }
}
