using System.Globalization;
using System.Text;

namespace Gi.Entities
{
    public static class GiExplainText
    {
        public static string Number(float value)
        {
            return value.ToString("0.##", CultureInfo.InvariantCulture);
        }

        public static string Summary(in GiExplanation explanation)
        {
            if (!explanation.Ok) return "outside the grid";

            var text = new StringBuilder();
            text.Append("value ").Append(explanation.Total.ToString(CultureInfo.InvariantCulture));
            text.Append(" = ").Append(explanation.Sum.ToString(CultureInfo.InvariantCulture)).Append(" from ")
                .Append(explanation.Contributors.ToString(CultureInfo.InvariantCulture)).Append(explanation.Contributors == 1 ? " source" : " sources");
            if (explanation.Rest != 0)
                text.Append(", ").Append(explanation.Rest.ToString("+0;-0", CultureInfo.InvariantCulture)).Append(" unattributed");
            if (explanation.Saturated) text.Append(", saturated");
            return text.ToString();
        }

        public static string Contribution(in GiContribution contribution)
        {
            return contribution.Value.ToString("+0;-0", CultureInfo.InvariantCulture)
                   + "  stamp " + contribution.Stamp.ToString(CultureInfo.InvariantCulture)
                   + " gain " + contribution.Gain.ToString(CultureInfo.InvariantCulture)
                   + " at (" + Number(contribution.X) + ", " + Number(contribution.Y) + ")";
        }

        public static string Step(in GiRecipeStep step)
        {
            var text = new StringBuilder();
            text.Append("layer ").Append(step.Layer.ToString(CultureInfo.InvariantCulture)).Append(" = ").Append(step.Kind).Append(" of layer ")
                .Append(step.First.ToString(CultureInfo.InvariantCulture)).Append(" (").Append(step.A.ToString(CultureInfo.InvariantCulture)).Append(")");
            if (step.Second >= 0)
                text.Append(" and layer ").Append(step.Second.ToString(CultureInfo.InvariantCulture)).Append(" (")
                    .Append(step.B.ToString(CultureInfo.InvariantCulture)).Append(")");
            switch (step.Kind)
            {
                case GiLayerKind.Sum:
                    text.Append(": ").Append(step.FirstWeight.ToString(CultureInfo.InvariantCulture)).Append("*").Append(step.A.ToString(CultureInfo.InvariantCulture))
                        .Append(" + ").Append(step.SecondWeight.ToString(CultureInfo.InvariantCulture)).Append("*").Append(step.B.ToString(CultureInfo.InvariantCulture))
                        .Append(" = ").Append(step.Raw.ToString(CultureInfo.InvariantCulture));
                    if (step.Shift != 0) text.Append(", shifted right by ").Append(step.Shift.ToString(CultureInfo.InvariantCulture));
                    break;
                case GiLayerKind.Mask:
                    text.Append(": layer ").Append(step.First.ToString(CultureInfo.InvariantCulture)).Append(" passes where ")
                        .Append(step.Low.ToString(CultureInfo.InvariantCulture)).Append(" <= ").Append(step.B.ToString(CultureInfo.InvariantCulture))
                        .Append(" <= ").Append(step.High.ToString(CultureInfo.InvariantCulture));
                    break;
            }

            text.Append(" -> ").Append(step.Value.ToString(CultureInfo.InvariantCulture));
            if (!step.Matches) text.Append(" (world reads ").Append(step.Actual.ToString(CultureInfo.InvariantCulture)).Append(")");
            return text.ToString();
        }
    }
}
