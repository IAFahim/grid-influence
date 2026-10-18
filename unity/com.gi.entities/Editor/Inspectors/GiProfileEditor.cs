using Gi.Entities.Authoring;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine.UIElements;

namespace Gi.Entities.Editor
{
    [CustomEditor(typeof(GiProfile))]
    public sealed class GiProfileEditor : UnityEditor.Editor
    {
        public override VisualElement CreateInspectorGUI()
        {
            var profile = (GiProfile)target;
            var root = new VisualElement();
            var stack = new VisualElement();
            var issues = new GiIssueBox();

            root.Add(new PropertyField(serializedObject.FindProperty("Axes")));
            root.Add(new PropertyField(serializedObject.FindProperty("Origin")));
            root.Add(new PropertyField(serializedObject.FindProperty("Grids")));
            root.Add(stack);
            root.Add(AddRow());
            root.Add(new PropertyField(serializedObject.FindProperty("Stamps")));
            root.Add(new PropertyField(serializedObject.FindProperty("Bindings")));
            root.Add(issues);
            root.Bind(serializedObject);

            void Refresh()
            {
                RebuildStack(stack);
                issues.Show(profile.Check());
            }

            root.TrackSerializedObjectValue(serializedObject, _ => Refresh());
            Refresh();
            return root;
        }

        private VisualElement AddRow()
        {
            var row = new VisualElement { style = { flexDirection = FlexDirection.Row } };
            foreach (GiLayerKind kind in System.Enum.GetValues(typeof(GiLayerKind)))
            {
                var captured = kind;
                row.Add(new Button(() => GiLayerStack.Add(serializedObject, captured)) { text = "Add " + kind });
            }

            return row;
        }

        private void RebuildStack(VisualElement stack)
        {
            stack.Clear();
            var profile = (GiProfile)target;
            stack.Add(new Label("Layer stack (a derived layer reads only layers above it)"));
            for (var i = 0; i < profile.Layers.Count; i++)
            {
                var layer = profile.Layers[i];
                if (layer == null) continue;

                var index = i;
                var row = new Foldout { text = index + ": " + layer.name + " (" + layer.Kind + ")", value = false };
                var buttons = new VisualElement { style = { flexDirection = FlexDirection.Row } };
                buttons.Add(new Button(() => GiLayerStack.Move(serializedObject, index, index - 1)) { text = "Up" });
                buttons.Add(new Button(() => GiLayerStack.Move(serializedObject, index, index + 1)) { text = "Down" });
                buttons.Add(new Button(() => GiLayerStack.Remove(serializedObject, index)) { text = "Remove" });
                row.Add(buttons);
                row.Add(new InspectorElement(layer));
                stack.Add(row);
            }
        }
    }
}
