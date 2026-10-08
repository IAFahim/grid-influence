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

    public static void BuildWeb()
    {
        try
        {
            PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Disabled;

            if (!File.Exists(ScenePath)) GiDemoMenu.CreateDemoScene();

            var output = "Build/WebGL";
            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i < args.Length - 1; i++)
                if (args[i] == "-buildOutput") output = args[i + 1];

            var projectRoot = Directory.GetParent(Application.dataPath).FullName;
            var location = Path.IsPathRooted(output) ? output : Path.Combine(projectRoot, output);
            Directory.CreateDirectory(location);

            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = location,
                target = BuildTarget.WebGL,
                options = BuildOptions.None
            });

            var summary = report.summary;
            Debug.Log("GiDemo WebGL build " + summary.result + " size " + summary.totalSize + " errors " + summary.totalErrors);
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
