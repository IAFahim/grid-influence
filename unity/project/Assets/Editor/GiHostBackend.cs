using System;
using UnityEditor;
using UnityEditor.Build;

namespace Gi.Host
{
    public static class GiHostBackend
    {
        private const string Variable = "GI_SCRIPTING_BACKEND";

        [InitializeOnLoadMethod]
        private static void Select()
        {
            var requested = Environment.GetEnvironmentVariable(Variable);
            if (string.IsNullOrEmpty(requested)) return;

            ScriptingImplementation backend;
            switch (requested)
            {
                case "il2cpp":
                    backend = ScriptingImplementation.IL2CPP;
                    break;
                case "coreclr":
                    backend = ScriptingImplementation.CoreCLR;
                    break;
                default:
                    throw new ArgumentException(Variable + "=" + requested);
            }

            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, backend);
        }
    }
}
