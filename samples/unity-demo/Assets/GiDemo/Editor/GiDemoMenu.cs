using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEditor;

namespace GiDemo.Editor
{

public static class GiDemoMenu
{
    [MenuItem("Tools/Gi/Create Demo Scene")]
    public static void CreateDemoScene()
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var host = new GameObject("GiEcosystemDemo");
        host.AddComponent<GiEcosystemDemo>();
        EditorSceneManager.SaveScene(scene, "Assets/GiDemo/GiDemoScene.unity");
        Selection.activeGameObject = host;
        Debug.Log("Gi demo scene saved to Assets/GiDemo/GiDemoScene.unity — press Play");
    }
}
}
