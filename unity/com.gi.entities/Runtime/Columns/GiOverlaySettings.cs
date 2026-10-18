using System.Globalization;
using System.Text;

namespace Gi.Entities
{
    public sealed class GiOverlaySettings
    {
        public bool Heatmap = true;
        public bool Footprints = true;
        public bool AllFootprints;
        public bool ReachDiscs;
        public bool GridBounds = true;
        public int Grid;
        public int Layer;
        public float ValueScale = 64f;

        public string ToJson()
        {
            var text = new StringBuilder();
            text.Append("{\n");
            text.Append("  \"allFootprints\": ").Append(Bool(AllFootprints)).Append(",\n");
            text.Append("  \"footprints\": ").Append(Bool(Footprints)).Append(",\n");
            text.Append("  \"grid\": ").Append(Grid.ToString(CultureInfo.InvariantCulture)).Append(",\n");
            text.Append("  \"gridBounds\": ").Append(Bool(GridBounds)).Append(",\n");
            text.Append("  \"heatmap\": ").Append(Bool(Heatmap)).Append(",\n");
            text.Append("  \"layer\": ").Append(Layer.ToString(CultureInfo.InvariantCulture)).Append(",\n");
            text.Append("  \"reachDiscs\": ").Append(Bool(ReachDiscs)).Append(",\n");
            text.Append("  \"valueScale\": ").Append(ValueScale.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
            text.Append("}\n");
            return text.ToString();
        }

        public static GiOverlaySettings FromJson(string json)
        {
            var settings = new GiOverlaySettings();
            if (string.IsNullOrEmpty(json)) return settings;

            foreach (var raw in json.Split('\n'))
            {
                var line = raw.Trim().TrimEnd(',');
                var colon = line.IndexOf(':');
                if (colon < 2 || line[0] != '"') continue;

                var key = line.Substring(1, line.IndexOf('"', 1) - 1);
                var value = line.Substring(colon + 1).Trim();
                switch (key)
                {
                    case "allFootprints": settings.AllFootprints = value == "true"; break;
                    case "footprints": settings.Footprints = value == "true"; break;
                    case "gridBounds": settings.GridBounds = value == "true"; break;
                    case "heatmap": settings.Heatmap = value == "true"; break;
                    case "reachDiscs": settings.ReachDiscs = value == "true"; break;
                    case "grid": settings.Grid = Int(value, settings.Grid); break;
                    case "layer": settings.Layer = Int(value, settings.Layer); break;
                    case "valueScale":
                        if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var scale) && scale >= 1f && !float.IsInfinity(scale)) settings.ValueScale = scale;
                        break;
                }
            }

            return settings;
        }

        private static string Bool(bool value)
        {
            return value ? "true" : "false";
        }

        private static int Int(string value, int fallback)
        {
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0 ? parsed : fallback;
        }
    }
}
