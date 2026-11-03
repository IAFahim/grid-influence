using UnityEditor;

namespace Gi.Host
{
    public static class GiPluginSetup
    {
        private const string Root = "Packages/com.gi.entities/Plugins/Gi/";

        [InitializeOnLoadMethod]
        public static void EnsureImporters()
        {
            Configure("coreclr", "ENABLE_CORECLR");
            Configure("portable", "!ENABLE_CORECLR");
        }

        public static void Apply()
        {
            EnsureImporters();
        }

        private static void Configure(string variant, string constraint)
        {
            foreach (var name in new[] { "Gi.dll", "Gi.Validation.dll" })
            {
                var importer = AssetImporter.GetAtPath(Root + variant + "/" + name) as PluginImporter;
                if (importer == null) continue;
                var changed = false;
                if (!importer.GetCompatibleWithAnyPlatform())
                {
                    importer.SetCompatibleWithAnyPlatform(true);
                    changed = true;
                }

                if (!importer.GetCompatibleWithEditor())
                {
                    importer.SetCompatibleWithEditor(true);
                    changed = true;
                }

                var constraints = importer.DefineConstraints ?? new string[0];
                if (constraints.Length != 1 || constraints[0] != constraint)
                {
                    importer.DefineConstraints = new[] { constraint };
                    changed = true;
                }

                var serialized = new SerializedObject(importer);
                var validate = serialized.FindProperty("m_ValidateReferences");
                if (validate != null && validate.boolValue)
                {
                    validate.boolValue = false;
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                    changed = true;
                }

                if (changed) importer.SaveAndReimport();
            }
        }
    }
}
