using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Gi.Entities.Authoring;
using Unity.Scripting.LifecycleManagement;
using UnityEditor;
using UnityEngine;

namespace Gi.Entities.Editor
{
    public readonly struct GiGizmoDraw
    {
        public readonly EntityId EntityId;
        public readonly Vector3 Position;
        public readonly Vector3 Normal;
        public readonly Vector3[] Path;
        public readonly Vector3[] Top;
        public readonly float Lift;
        public readonly bool Spokes;
        public readonly bool OutsideGrids;
        public readonly string Label;

        public GiGizmoDraw(EntityId entityId, Vector3 position, Vector3 normal, Vector3[] path, Vector3[] top, float lift, bool spokes, bool outsideGrids, string label)
        {
            EntityId = entityId;
            Position = position;
            Normal = normal;
            Path = path;
            Top = top;
            Lift = lift;
            Spokes = spokes;
            OutsideGrids = outsideGrids;
            Label = label;
        }
    }

    public static class GiGizmoShape
    {
        private const int ArcSegments = 32;

        public static Vector2[] Outline(in GiStampSpec spec, float x, float y, float headingRadians, float scale, out bool spokes)
        {
            if (!GiFootprint.IsRound(spec.Shape))
            {
                spokes = false;
                var corners = new float[8];
                GiFootprint.Corners(spec, x, y, headingRadians, scale, corners);
                return new[]
                {
                    new Vector2(corners[0], corners[1]),
                    new Vector2(corners[2], corners[3]),
                    new Vector2(corners[4], corners[5]),
                    new Vector2(corners[6], corners[7]),
                };
            }

            var degrees = spec.Arc == 0 ? 360f : spec.Arc;
            spokes = degrees < 360f;
            var radius = spec.Radius * scale;
            var points = new Vector2[ArcSegments + 1 + (spokes ? 1 : 0)];
            var index = 0;
            if (spokes) points[index++] = new Vector2(x, y);
            var heading = headingRadians * Mathf.Rad2Deg;
            for (var i = 0; i <= ArcSegments; i++)
            {
                var radians = (heading + i * degrees / ArcSegments - degrees * 0.5f) * Mathf.Deg2Rad;
                points[index++] = new Vector2(x + radius * Mathf.Cos(radians), y + radius * Mathf.Sin(radians));
            }

            return points;
        }

        public static float Lift(in GiStampSpec spec, float headingRadians, float scale)
        {
            if (GiFootprint.IsRound(spec.Shape)) return 0f;

            GiFootprint.HalfExtents(spec, headingRadians, scale, out var halfWidth, out var halfHeight);
            return Mathf.Min(halfWidth, halfHeight) * 0.5f;
        }
    }

    [InitializeOnLoad]
    public static class GiGizmos
    {
        public const string OutsideGridsText = "outside every grid";

        private static readonly Color SelectedInk = new Color(0.92f, 0.45f, 0.16f, 0.95f);
        private static readonly Color DimmedInk = new Color(0.92f, 0.45f, 0.16f, 0.22f);
        private static readonly Color WarningInk = new Color(0.95f, 0.16f, 0.16f, 0.95f);

        [NoAutoStaticsCleanup]
        private static readonly List<GiGizmoDraw> Marks = new List<GiGizmoDraw>();

        [NoAutoStaticsCleanup]
        private static readonly HashSet<EntityId> Watched = new HashSet<EntityId>();

        [NoAutoStaticsCleanup]
        private static readonly List<sbyte> ScratchCells = new List<sbyte>();

        [NoAutoStaticsCleanup]
        private static readonly StringBuilder ScratchCoverage = new StringBuilder();

        [NoAutoStaticsCleanup]
        private static readonly StringBuilder ScratchText = new StringBuilder();

        private static bool _dirty = true;
        private static int _rescans;

        static GiGizmos()
        {
            SceneView.duringSceneGui += DrawSceneGui;
            EditorApplication.hierarchyChanged += Invalidate;
            Undo.undoRedoPerformed += Invalidate;
            ObjectChangeEvents.changesPublished += ChangesPublished;
        }

        public static int Rescans => _rescans;

        public static IReadOnlyList<GiGizmoDraw> All => Marks;

        public static void Invalidate()
        {
            if (_dirty) return;

            _dirty = true;
            SceneView.RepaintAll();
        }

        public static bool Touch(Object changed)
        {
            if (changed == null) return false;
            if (TouchId(changed.GetEntityId())) return true;
            return changed is Component component && TouchId(component.gameObject.GetEntityId());
        }

        public static void RescanIfNeeded()
        {
            if (!_dirty) return;

            _dirty = false;
            _rescans++;
            Marks.Clear();
            Watched.Clear();
            foreach (var authoring in Object.FindObjectsByType<GiEmitterAuthoring>(FindObjectsSortMode.None)) Build(authoring);
            foreach (var authoring in Object.FindObjectsByType<GiSplatAuthoring>(FindObjectsSortMode.None)) Build(authoring);
        }

        private static void ChangesPublished(ref ObjectChangeEventStream stream)
        {
            for (var i = 0; i < stream.length; i++)
            {
                switch (stream.GetEventType(i))
                {
                    case ObjectChangeKind.ChangeScene:
                        Invalidate();
                        break;
                    case ObjectChangeKind.CreateGameObjectHierarchy:
                        stream.GetCreateGameObjectHierarchyEvent(i, out var created);
                        TouchId(created.entityId);
                        break;
                    case ObjectChangeKind.DestroyGameObjectHierarchy:
                        stream.GetDestroyGameObjectHierarchyEvent(i, out var destroyed);
                        TouchId(destroyed.entityId);
                        break;
                    case ObjectChangeKind.ChangeGameObjectStructureHierarchy:
                        stream.GetChangeGameObjectStructureHierarchyEvent(i, out var structureHierarchy);
                        TouchId(structureHierarchy.entityId);
                        break;
                    case ObjectChangeKind.ChangeGameObjectStructure:
                        stream.GetChangeGameObjectStructureEvent(i, out var structure);
                        TouchId(structure.entityId);
                        break;
                    case ObjectChangeKind.ChangeGameObjectParent:
                        stream.GetChangeGameObjectParentEvent(i, out var parent);
                        TouchId(parent.entityId);
                        break;
                    case ObjectChangeKind.ChangeGameObjectOrComponentProperties:
                        stream.GetChangeGameObjectOrComponentPropertiesEvent(i, out var properties);
                        TouchId(properties.entityId);
                        break;
                    case ObjectChangeKind.ChangeChildrenOrder:
                        stream.GetChangeChildrenOrderEvent(i, out var children);
                        TouchId(children.entityId);
                        break;
                    case ObjectChangeKind.ChangeRootOrder:
                        stream.GetChangeRootOrderEvent(i, out var root);
                        TouchId(root.entityId);
                        break;
                    case ObjectChangeKind.CreateAssetObject:
                        stream.GetCreateAssetObjectEvent(i, out var assetCreated);
                        TouchId(assetCreated.entityId);
                        break;
                    case ObjectChangeKind.DestroyAssetObject:
                        stream.GetDestroyAssetObjectEvent(i, out var assetDestroyed);
                        TouchId(assetDestroyed.entityId);
                        break;
                    case ObjectChangeKind.ChangeAssetObjectProperties:
                        stream.GetChangeAssetObjectPropertiesEvent(i, out var asset);
                        TouchId(asset.entityId);
                        break;
                    case ObjectChangeKind.UpdatePrefabInstances:
                        stream.GetUpdatePrefabInstancesEvent(i, out var instances);
                        if (instances.entityIds.IsCreated)
                            foreach (var id in instances.entityIds)
                                TouchId(id);
                        break;
                }
            }
        }

        private static bool TouchId(EntityId entityId)
        {
            if (!Watched.Contains(entityId)) return false;

            Invalidate();
            return true;
        }

        private static void Build(GiEmitterAuthoring authoring)
        {
            Build(authoring, authoring.Profile, authoring.Stamp, authoring.Layer, authoring.Gain, authoring.Oriented, authoring.Scaled);
        }

        private static void Build(GiSplatAuthoring authoring)
        {
            Build(authoring, authoring.Profile, authoring.Stamp, authoring.Layer, authoring.Gain, false, false);
        }

        private static void Build(Component source, GiProfile profile, GiStampAsset stamp, GiLayerAsset layer, int gain, bool oriented, bool scaled)
        {
            var gameObject = source.gameObject;
            Watched.Add(gameObject.GetEntityId());
            Watched.Add(source.transform.GetEntityId());
            Watched.Add(source.GetEntityId());
            if (profile != null) Watched.Add(profile.GetEntityId());
            if (profile == null || stamp == null || stamp.Shape < GiStampShape.Box || stamp.Shape > GiStampShape.Raster) return;

            var plane = new GiPlane(profile.Axes, profile.Origin.x, profile.Origin.y);
            var position = source.transform.position;
            plane.Project(position.x, position.y, position.z, out var x, out var y);
            var rotation = source.transform.rotation;
            var heading = oriented ? plane.Heading(rotation.x, rotation.y, rotation.z, rotation.w) : 0f;
            var uniformScale = scaled ? source.transform.lossyScale.x : 1f;

            ScratchCells.Clear();
            var spec = stamp.ToSpec(ScratchCells);
            var outline = GiGizmoShape.Outline(spec, x, y, heading, uniformScale, out var spokes);
            var lift = GiGizmoShape.Lift(spec, heading, uniformScale);

            var path = new Vector3[outline.Length + 1];
            for (var i = 0; i < outline.Length; i++)
            {
                plane.EmbedPoint(outline[i].x, outline[i].y, out var wx, out var wy, out var wz);
                path[i] = new Vector3(wx, wy, wz);
            }

            path[path.Length - 1] = path[0];

            var normal = profile.Axes == GiPlaneAxes.Xz ? Vector3.up : Vector3.forward;
            Vector3[] top = null;
            if (lift > 0f)
            {
                top = new Vector3[path.Length];
                for (var i = 0; i < path.Length; i++) top[i] = path[i] + normal * lift;
            }

            ScratchCoverage.Clear();
            for (var i = 0; i < profile.Grids.Count; i++)
            {
                if (!GiGridCoverage.Contains(GridSpec(profile.Grids[i]), x, y)) continue;

                if (ScratchCoverage.Length > 0) ScratchCoverage.Append(',');
                ScratchCoverage.Append(i.ToString(CultureInfo.InvariantCulture));
            }

            var covered = ScratchCoverage.Length > 0;
            ScratchText.Clear();
            ScratchText.Append("layer ").Append(layer != null ? layer.name : "none").Append(" reach ");
            if (GiFootprint.IsRound(spec.Shape))
            {
                ScratchText.Append((spec.Radius * uniformScale).ToString("0.##", CultureInfo.InvariantCulture));
                if (spec.Arc > 0 && spec.Arc < 360)
                    ScratchText.Append(" arc ").Append(spec.Arc.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                ScratchText.Append(spec.Width.ToString(CultureInfo.InvariantCulture)).Append('x')
                    .Append(spec.Height.ToString(CultureInfo.InvariantCulture));
            }

            ScratchText.Append(" gain ").Append(gain.ToString(CultureInfo.InvariantCulture));
            ScratchText.Append(" coverage ").Append(covered ? "grids " + ScratchCoverage : "no grid");

            Marks.Add(new GiGizmoDraw(gameObject.GetEntityId(), position, normal, path, top, lift, spokes, !covered, ScratchText.ToString()));
        }

        private static GiGridSpec GridSpec(GiGridEntry entry)
        {
            return new GiGridSpec { Power = entry.Power, X = entry.Origin.x, Y = entry.Origin.y, Size = entry.Size };
        }

        private static void DrawSceneGui(SceneView view)
        {
            RescanIfNeeded();
            foreach (var mark in Marks)
            {
                var selected = Selection.Contains(mark.EntityId);
                Handles.color = selected ? SelectedInk : DimmedInk;
                Handles.DrawPolyLine(mark.Path);
                if (mark.Top != null)
                {
                    Handles.DrawPolyLine(mark.Top);
                    for (var i = 0; i < mark.Top.Length - 1; i++) Handles.DrawLine(mark.Path[i], mark.Top[i]);
                }

                if (selected) Handles.Label(mark.Position + mark.Normal * (mark.Lift + 0.75f), mark.Label);
                if (mark.OutsideGrids)
                {
                    var size = HandleUtility.GetHandleSize(mark.Position) * 0.08f;
                    Handles.color = WarningInk;
                    Handles.DrawSolidDisc(mark.Position + mark.Normal * mark.Lift, mark.Normal, size);
                    Handles.Label(mark.Position + mark.Normal * (mark.Lift + 0.35f), OutsideGridsText);
                }
            }
        }
    }
}
