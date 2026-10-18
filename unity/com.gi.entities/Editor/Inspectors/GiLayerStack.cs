using System.Collections.Generic;
using Gi.Entities.Authoring;
using UnityEditor;
using UnityEngine;

namespace Gi.Entities.Editor
{
    public static class GiLayerStack
    {
        private const string LayersProperty = "Layers";

        public static GiLayerAsset Add(SerializedObject profile, GiLayerKind kind)
        {
            var owner = (GiProfile)profile.targetObject;
            Undo.IncrementCurrentGroup();
            var group = Undo.GetCurrentGroup();
            var layer = ScriptableObject.CreateInstance<GiLayerAsset>();
            layer.Owner = owner;
            layer.Kind = kind;
            layer.name = NameFor(owner, kind);
            if (!string.IsNullOrEmpty(AssetDatabase.GetAssetPath(owner))) AssetDatabase.AddObjectToAsset(layer, owner);
            Undo.RegisterCreatedObjectUndo(layer, "Add Gi layer");

            profile.Update();
            var list = profile.FindProperty(LayersProperty);
            var index = list.arraySize;
            list.InsertArrayElementAtIndex(index);
            list.GetArrayElementAtIndex(index).objectReferenceValue = layer;
            profile.ApplyModifiedProperties();
            Undo.SetCurrentGroupName("Add Gi layer");
            Undo.CollapseUndoOperations(group);
            return layer;
        }

        public static bool Move(SerializedObject profile, int from, int to)
        {
            var owner = (GiProfile)profile.targetObject;
            if (from == to || from < 0 || to < 0 || from >= owner.Layers.Count || to >= owner.Layers.Count) return false;

            var order = new List<GiLayerAsset>(owner.Layers);
            var moved = order[from];
            order.RemoveAt(from);
            order.Insert(to, moved);
            if (HasForwardReference(order)) return false;

            profile.Update();
            profile.FindProperty(LayersProperty).MoveArrayElement(from, to);
            profile.ApplyModifiedProperties();
            return true;
        }

        public static bool Remove(SerializedObject profile, int index)
        {
            var owner = (GiProfile)profile.targetObject;
            if (index < 0 || index >= owner.Layers.Count) return false;

            Undo.IncrementCurrentGroup();
            var group = Undo.GetCurrentGroup();
            var layer = owner.Layers[index];
            foreach (var other in owner.Layers)
            {
                if (other == null || other == layer) continue;
                var edit = new SerializedObject(other);
                var changed = false;
                foreach (var name in new[] { "First", "Second" })
                {
                    var property = edit.FindProperty(name);
                    if (property.objectReferenceValue != layer) continue;

                    property.objectReferenceValue = null;
                    changed = true;
                }

                if (changed) edit.ApplyModifiedProperties();
            }

            profile.Update();
            var list = profile.FindProperty(LayersProperty);
            var element = list.GetArrayElementAtIndex(index);
            if (element.objectReferenceValue != null) element.objectReferenceValue = null;
            list.DeleteArrayElementAtIndex(index);
            profile.ApplyModifiedProperties();
            if (layer != null) Undo.DestroyObjectImmediate(layer);
            Undo.SetCurrentGroupName("Remove Gi layer");
            Undo.CollapseUndoOperations(group);
            return true;
        }

        public static bool SetInput(SerializedObject layer, bool second, GiLayerAsset candidate)
        {
            var target = (GiLayerAsset)layer.targetObject;
            if (candidate != null && !Candidates(target).Contains(candidate)) return false;

            layer.Update();
            layer.FindProperty(second ? "Second" : "First").objectReferenceValue = candidate;
            layer.ApplyModifiedProperties();
            return true;
        }

        public static List<GiLayerAsset> Candidates(GiLayerAsset layer)
        {
            var above = new List<GiLayerAsset>();
            if (layer.Owner == null) return above;

            foreach (var candidate in layer.Owner.Layers)
            {
                if (candidate == layer) break;
                if (candidate != null) above.Add(candidate);
            }

            return above;
        }

        public static bool HasForwardReference(List<GiLayerAsset> order)
        {
            for (var i = 0; i < order.Count; i++)
            {
                var layer = order[i];
                if (layer == null || layer.IsBase) continue;
                if (IsAtOrBelow(order, layer.First, i) || IsAtOrBelow(order, layer.Second, i)) return true;
            }

            return false;
        }

        private static bool IsAtOrBelow(List<GiLayerAsset> order, GiLayerAsset input, int index)
        {
            if (input == null) return false;
            var position = order.IndexOf(input);
            return position >= index;
        }

        private static string NameFor(GiProfile owner, GiLayerKind kind)
        {
            var count = 1;
            foreach (var layer in owner.Layers)
                if (layer != null && layer.Kind == kind)
                    count++;
            return kind + " " + count;
        }
    }
}
