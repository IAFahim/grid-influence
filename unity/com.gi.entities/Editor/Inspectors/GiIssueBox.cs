using System.Collections.Generic;
using UnityEngine.UIElements;

namespace Gi.Entities.Editor
{
    public sealed class GiIssueBox : VisualElement
    {
        public void Show(IEnumerable<GiProfileIssue> issues)
        {
            Clear();
            foreach (var issue in issues) Add(new HelpBox(issue.ToString(), HelpBoxMessageType.Error));
        }

        public static IEnumerable<GiProfileIssue> For(List<GiProfileIssue> issues, GiProfileSection section, int index)
        {
            foreach (var issue in issues)
                if (issue.Section == section && issue.Index == index)
                    yield return issue;
        }
    }
}
