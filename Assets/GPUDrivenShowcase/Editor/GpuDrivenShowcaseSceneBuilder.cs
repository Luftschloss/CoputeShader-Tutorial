using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class GpuDrivenShowcaseSceneBuilder
{
    private const string SceneDirectory = "Assets/GPUDrivenShowcase/Scenes";
    private const string ScenePath = SceneDirectory + "/GPUDriven_Showcase.unity";

    [MenuItem("GPU Driven Showcase/Add Interaction Layer To Current Scene")]
    public static void AddInteractionLayerToCurrentScene()
    {
        EnsureCamera();
        EnsureDirectionalLight();
        EnsureController();
        EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
    }

    [MenuItem("GPU Driven Showcase/Create Empty Showcase Scene")]
    public static void CreateEmptyShowcaseScene()
    {
        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        scene.name = "GPUDriven_Showcase";

        EnsureCamera();
        EnsureDirectionalLight();
        EnsureController();
        SaveScene();
        AddSceneToBuildSettings();
    }

    [MenuItem("GPU Driven Showcase/Build Default Scene Asset")]
    public static void BuildDefaultSceneAsset()
    {
        CreateEmptyShowcaseScene();
    }

    private static void EnsureCamera()
    {
        Camera camera = Camera.main;
        if (camera == null)
        {
            GameObject cameraObject = new GameObject("Main Camera");
            cameraObject.tag = "MainCamera";
            cameraObject.transform.SetPositionAndRotation(new Vector3(0.0f, 35.0f, -55.0f), Quaternion.Euler(28.0f, 0.0f, 0.0f));
            camera = cameraObject.AddComponent<Camera>();
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 2500.0f;
            camera.clearFlags = CameraClearFlags.Skybox;
        }

        if (camera.GetComponent<GpuDrivenFreeCamera>() == null)
        {
            camera.gameObject.AddComponent<GpuDrivenFreeCamera>();
        }
    }

    private static void EnsureDirectionalLight()
    {
        Light[] lights = Object.FindObjectsOfType<Light>(true);
        for (int i = 0; i < lights.Length; i++)
        {
            if (lights[i].type == LightType.Directional)
            {
                return;
            }
        }

        GameObject lightObject = new GameObject("Directional Light");
        lightObject.transform.rotation = Quaternion.Euler(50.0f, -35.0f, 0.0f);
        Light light = lightObject.AddComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 1.0f;
    }

    private static void EnsureController()
    {
        GpuDrivenShowcaseController controller = Object.FindObjectOfType<GpuDrivenShowcaseController>(true);
        if (controller == null)
        {
            GameObject controllerObject = new GameObject("GPU Driven Showcase");
            controller = controllerObject.AddComponent<GpuDrivenShowcaseController>();
            controllerObject.AddComponent<GpuDrivenShowcaseRuntimePanel>();
        }
        else if (controller.GetComponent<GpuDrivenShowcaseRuntimePanel>() == null)
        {
            controller.gameObject.AddComponent<GpuDrivenShowcaseRuntimePanel>();
        }
    }

    private static void SaveScene()
    {
        if (!Directory.Exists(SceneDirectory))
        {
            Directory.CreateDirectory(SceneDirectory);
        }

        EditorSceneManager.SaveScene(SceneManager.GetActiveScene(), ScenePath);
        AssetDatabase.Refresh();
    }

    private static void AddSceneToBuildSettings()
    {
        EditorBuildSettingsScene[] scenes = EditorBuildSettings.scenes;
        for (int i = 0; i < scenes.Length; i++)
        {
            if (scenes[i].path == ScenePath)
            {
                scenes[i].enabled = true;
                EditorBuildSettings.scenes = scenes;
                return;
            }
        }

        EditorBuildSettingsScene[] newScenes = new EditorBuildSettingsScene[scenes.Length + 1];
        for (int i = 0; i < scenes.Length; i++)
        {
            newScenes[i] = scenes[i];
        }

        newScenes[newScenes.Length - 1] = new EditorBuildSettingsScene(ScenePath, true);
        EditorBuildSettings.scenes = newScenes;
    }
}
