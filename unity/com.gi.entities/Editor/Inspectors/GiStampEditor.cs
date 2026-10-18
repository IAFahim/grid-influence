using System.Collections.Generic;
using Gi.Entities.Authoring;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine.UIElements;

namespace Gi.Entities.Editor
{
    [CustomEditor(typeof(GiStampAsset))]
    public sealed class GiStampEditor : UnityEditor.Editor
    {
        public static List<GiProfileIssue> Check(GiStampAsset stamp)
        {
            var issues = new List<GiProfileIssue>();
            var cells = new List<sbyte>();
            GiProfileCheck.CheckStamp(stamp.ToSpec(cells), 0, cells.Count, issues);
            return issues;
        }

        public override VisualElement CreateInspectorGUI()
        {
            var stamp = (GiStampAsset)target;
            var root = new VisualElement();
            var issues = new GiIssueBox();
            InspectorElement.FillDefaultInspector(root, serializedObject, this);
            root.Add(issues);

            void Refresh()
            {
                issues.Show(Check(stamp));
            }

            root.TrackSerializedObjectValue(serializedObject, _ => Refresh());
            Refresh();
            return root;
        }
    }
}
