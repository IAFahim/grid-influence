using System.IO;
using System.Text;

namespace Gi.Entities.Editor
{
    public static class GiOverlaySettingsStore
    {
        public const string DefaultPath = "UserSettings/Gi/Overlay.json";

        public static GiOverlaySettings Load(string path = DefaultPath)
        {
            return File.Exists(path) ? GiOverlaySettings.FromJson(File.ReadAllText(path)) : new GiOverlaySettings();
        }

        public static void Save(GiOverlaySettings settings, string path = DefaultPath)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            var json = settings.ToJson();
            if (File.Exists(path) && File.ReadAllText(path) == json) return;

            File.WriteAllText(path, json, new UTF8Encoding(false));
        }
    }
}
