using System.Globalization;
using UnityEngine.UIElements;

namespace Gi.Entities.Editor
{
    public sealed class GiSourceInspector : VisualElement
    {
        public const string NothingSelected = "Select an entity to see its Gi state.";

        public void Show(Presentation.GiEntityReport report)
        {
            Clear();
            foreach (var line in Lines(report)) Add(new Label(line));
        }

        public static string[] Lines(Presentation.GiEntityReport report)
        {
            if (report == null) return new[] { NothingSelected };
            if (!report.Exists) return new[] { "The entity no longer exists." };

            var lines = new System.Collections.Generic.List<string>();
            if (report.HasEmitter)
            {
                lines.Add("Desired: layer " + report.DesiredLayer.ToString(CultureInfo.InvariantCulture) + ", stamp " +
                          report.DesiredStamp.ToString(CultureInfo.InvariantCulture) + ", gain " + report.DesiredGain.ToString(CultureInfo.InvariantCulture) +
                          (report.Enabled ? "" : ", emitter disabled"));
            }

            if (report.HasSource)
            {
                lines.Add(report.Placed
                    ? "Applied: " + report.SourceId + ", layer " + report.AppliedLayer.ToString(CultureInfo.InvariantCulture) + ", stamp " +
                      report.AppliedStamp.ToString(CultureInfo.InvariantCulture) + ", gain " + report.AppliedGain.ToString(CultureInfo.InvariantCulture)
                    : "Applied: not placed");
                if (report.Drift) lines.Add("Desired and applied state differ; the next reconcile applies the change.");
            }
            else if (report.HasEmitter)
            {
                lines.Add("Applied: no source yet.");
            }

            if (report.Live)
            {
                lines.Add("Gi source: (" + GiExplainText.Number(report.Spec.X) + ", " + GiExplainText.Number(report.Spec.Y) + "), heading " +
                          GiExplainText.Number(report.Spec.HeadingRadians) + ", scale " + GiExplainText.Number(report.Spec.Scale) + ", gain " +
                          report.Spec.Gain.ToString(CultureInfo.InvariantCulture) + ", layer " + report.Spec.Layer.ToString(CultureInfo.InvariantCulture) +
                          ", stamp " + report.Spec.Stamp.ToString(CultureInfo.InvariantCulture));
            }
            else if (report.HasSource && report.Placed)
            {
                lines.Add("Gi source: not found in the world.");
            }

            lines.Add(report.HasFault ? "Fault: " + report.Fault : "Fault: none");
            if (!report.HasPosition) return lines.ToArray();

            lines.Add("Field at (" + GiExplainText.Number(report.X) + ", " + GiExplainText.Number(report.Y) + "):");
            foreach (var reading in report.Layers)
            {
                lines.Add("  layer " + reading.LayerKey.ToString(CultureInfo.InvariantCulture) + (reading.Ok
                    ? ": " + reading.Value.ToString(CultureInfo.InvariantCulture) + ", gradient (" + GiExplainText.Number(reading.GradientX) + ", " +
                      GiExplainText.Number(reading.GradientY) + ")"
                    : ": not readable"));
            }

            return lines.ToArray();
        }
    }
}
