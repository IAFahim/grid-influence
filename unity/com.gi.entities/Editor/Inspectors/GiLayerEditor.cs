using System;
using System.Collections.Generic;
using Gi.Entities.Authoring;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine.UIElements;

namespace Gi.Entities.Editor
{
    [CustomEditor(typeof(GiLayerAsset))]
    public sealed class GiLayerEditor : UnityEditor.Editor
    {
        public override VisualElement CreateInspectorGUI()
        {
            var layer = (GiLayerAsset)target;
            var root = new VisualElement();
            var baseSection = new VisualElement();
            var derivedSection = new VisualElement();
            var issues = new GiIssueBox();

            root.Add(new PropertyField(serializedObject.FindProperty("Kind")));
            baseSection.Add(new PropertyField(serializedObject.FindProperty("Precision")));
            baseSection.Add(new PropertyField(serializedObject.FindProperty("PhaseBits")));
            baseSection.Add(new PropertyField(serializedObject.FindProperty("TurnBits")));
            derivedSection.Add(InputPopup("First input", false));
            derivedSection.Add(InputPopup("Second input", true));
            derivedSection.Add(new PropertyField(serializedObject.FindProperty("FirstWeight")));
            derivedSection.Add(new PropertyField(serializedObject.FindProperty("SecondWeight")));
            derivedSection.Add(new PropertyField(serializedObject.FindProperty("Shift")));
            derivedSection.Add(new PropertyField(serializedObject.FindProperty("MaskLow")));
            derivedSection.Add(new PropertyField(serializedObject.FindProperty("MaskHigh")));
            root.Add(baseSection);
            root.Add(derivedSection);
            root.Add(issues);
            root.Bind(serializedObject);

            void Refresh()
            {
                baseSection.style.display = layer.IsBase ? DisplayStyle.Flex : DisplayStyle.None;
                derivedSection.style.display = layer.IsBase ? DisplayStyle.None : DisplayStyle.Flex;
                issues.Clear();
                if (layer.Owner == null) return;

                var index = layer.Owner.LayerIndex(layer);
                issues.Show(GiIssueBox.For(layer.Owner.Check(), GiProfileSection.Layer, index));
            }

            root.TrackSerializedObjectValue(serializedObject, _ => Refresh());
            Refresh();
            return root;
        }

        private VisualElement InputPopup(string label, bool second)
        {
            var layer = (GiLayerAsset)target;
            var choices = new List<GiLayerAsset> { null };
            choices.AddRange(GiLayerStack.Candidates(layer));
            var current = second ? layer.Second : layer.First;
            if (!choices.Contains(current)) choices.Add(current);

            Func<GiLayerAsset, string> name = value => value == null ? "(none)" : value.name;
            var popup = new PopupField<GiLayerAsset>(label, choices, choices.IndexOf(current), name, name);
            popup.RegisterValueChangedCallback(change =>
            {
                if (!GiLayerStack.SetInput(serializedObject, second, change.newValue)) popup.SetValueWithoutNotify(change.previousValue);
            });
            return popup;
        }
    }
}
