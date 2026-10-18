using System;
using System.Collections.Generic;
using Gi.Entities.Authoring;
using UnityEditor;
using UnityEngine.UIElements;

namespace Gi.Entities.Editor
{
    [CustomPropertyDrawer(typeof(GiFieldRef))]
    public sealed class GiFieldRefDrawer : PropertyDrawer
    {
        private const string None = "(none)";
        private const string Whole = "(component only)";

        public override VisualElement CreatePropertyGUI(SerializedProperty property)
        {
            var typeProperty = property.FindPropertyRelative("Type");
            var fieldProperty = property.FindPropertyRelative("Field");
            var root = new VisualElement { style = { flexDirection = FlexDirection.Row } };
            var label = new Label(property.displayName) { style = { minWidth = 120 } };
            var types = GiFieldCatalog.ComponentTypes();
            var typeNames = new List<string> { None };
            foreach (var type in types) typeNames.Add(type.FullName);

            var current = Array.Find(types.ToArray(), type => type.AssemblyQualifiedName == typeProperty.stringValue);
            var typeDropdown = new DropdownField(typeNames, current == null ? 0 : typeNames.IndexOf(current.FullName));
            var fieldDropdown = new DropdownField();

            void FillFields(Type type)
            {
                var choices = new List<string> { Whole };
                if (type != null)
                    foreach (var field in GiFieldCatalog.Fields(type))
                        choices.Add(field.ToString());
                fieldDropdown.choices = choices;
                var selected = 0;
                if (type != null && !string.IsNullOrEmpty(fieldProperty.stringValue))
                {
                    var index = 1;
                    foreach (var field in GiFieldCatalog.Fields(type))
                    {
                        if (field.Name == fieldProperty.stringValue) selected = index;
                        index++;
                    }
                }

                fieldDropdown.SetValueWithoutNotify(choices[selected]);
                fieldDropdown.SetEnabled(type != null);
            }

            FillFields(current);
            typeDropdown.RegisterValueChangedCallback(change =>
            {
                var type = change.newValue == None ? null : types.Find(candidate => candidate.FullName == change.newValue);
                typeProperty.stringValue = type == null ? string.Empty : type.AssemblyQualifiedName;
                fieldProperty.stringValue = string.Empty;
                property.serializedObject.ApplyModifiedProperties();
                FillFields(type);
            });
            fieldDropdown.RegisterValueChangedCallback(change =>
            {
                var type = types.Find(candidate => candidate.AssemblyQualifiedName == typeProperty.stringValue);
                fieldProperty.stringValue = string.Empty;
                if (type != null)
                    foreach (var field in GiFieldCatalog.Fields(type))
                        if (field.ToString() == change.newValue)
                            fieldProperty.stringValue = field.Name;
                property.serializedObject.ApplyModifiedProperties();
            });

            root.Add(label);
            root.Add(typeDropdown);
            root.Add(fieldDropdown);
            return root;
        }
    }
}
