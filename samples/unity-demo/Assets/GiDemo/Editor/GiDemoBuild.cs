using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace GiDemo.Editor
{

public static class GiDemoBuild
{
    private const string ScenePath = "Assets/GiDemo/GiDemoScene.unity";

    public static void BuildWeb() => Build(BuildTarget.WebGL, "Build/WebGL", "");

    public static void BuildLinux() => Build(BuildTarget.StandaloneLinux64, "Build/Linux64", "unity-demo");

    private static void Build(BuildTarget target, string defaultOutput, string executable)
    {
        try
        {
            if (target == BuildTarget.WebGL)
            {
                PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Disabled;
                PlayerSettings.WebGL.debugSymbolMode = WebGLDebugSymbolMode.External;
            }

            if (!File.Exists(ScenePath)) GiDemoMenu.CreateDemoScene();

            var output = defaultOutput;
            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i < args.Length - 1; i++)
                if (args[i] == "-buildOutput") output = args[i + 1];

            var projectRoot = Directory.GetParent(Application.dataPath).FullName;
            var location = Path.IsPathRooted(output) ? output : Path.Combine(projectRoot, output);
            Directory.CreateDirectory(location);

            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = executable.Length == 0 ? location : Path.Combine(location, executable),
                target = target,
                options = BuildOptions.None
            });

            var summary = report.summary;
            Debug.Log("GiDemo " + target + " build " + summary.result + " size " + summary.totalSize + " errors " + summary.totalErrors);
            if (summary.result != BuildResult.Succeeded || summary.totalErrors > 0)
                EditorApplication.Exit(1);
            else
                EditorApplication.Exit(0);
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            EditorApplication.Exit(1);
        }
    }
}
}
