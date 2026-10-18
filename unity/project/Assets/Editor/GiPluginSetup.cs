using System;
using UnityEditor;

namespace Gi.Host
{
    public static class GiPluginSetup
    {
        private const string Root = "Packages/com.gi.entities/Plugins/Gi/";

        public static void Apply()
        {
            var variant = Environment.GetEnvironmentVariable("GI_PLUGIN_VARIANT");
            var constraint = Environment.GetEnvironmentVariable("GI_PLUGIN_CONSTRAINT");
            foreach (var name in new[] { "Gi.dll", "Gi.Validation.dll" })
            {
                var importer = (PluginImporter)AssetImporter.GetAtPath(Root + variant + "/" + name);
                importer.SetCompatibleWithAnyPlatform(true);
                importer.SetCompatibleWithEditor(true);
                importer.DefineConstraints = new[] { constraint };
                importer.SaveAndReimport();
            }
        }
    }
}
