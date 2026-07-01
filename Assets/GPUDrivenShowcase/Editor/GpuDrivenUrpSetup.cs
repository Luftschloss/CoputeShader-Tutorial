using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public static class GpuDrivenUrpSetup
{
    private const string SettingsDirectory = "Assets/GPUDrivenShowcase/Settings";
    private const string PipelineAssetPath = SettingsDirectory + "/GPUDriven_URP.asset";
    private const string RendererDataPath = SettingsDirectory + "/GPUDriven_UniversalRenderer.asset";

    [MenuItem("GPU Driven Showcase/Setup URP Pipeline")]
    public static void SetupUrpPipeline()
    {
        EnsureSettingsDirectory();

        UniversalRendererData rendererData = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RendererDataPath);
        if (rendererData == null)
        {
            rendererData = CreateRendererDataAsset(RendererDataPath);
        }

        EnsureHizFeature(rendererData);

        UniversalRenderPipelineAsset pipelineAsset = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(PipelineAssetPath);
        if (pipelineAsset == null)
        {
            pipelineAsset = UniversalRenderPipelineAsset.Create(rendererData);
            AssetDatabase.CreateAsset(pipelineAsset, PipelineAssetPath);
        }

        pipelineAsset.supportsCameraDepthTexture = true;
        rendererData.copyDepthMode = CopyDepthMode.AfterOpaques;

        GraphicsSettings.renderPipelineAsset = pipelineAsset;
        QualitySettings.renderPipeline = pipelineAsset;

        EditorUtility.SetDirty(rendererData);
        EditorUtility.SetDirty(pipelineAsset);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Debug.Log("GPU Driven URP pipeline configured. Add/verify the GPU Driven Hi-Z feature on the renderer if Unity reports missing renderer features after script reload.");
    }

    private static void EnsureSettingsDirectory()
    {
        if (!Directory.Exists(SettingsDirectory))
        {
            Directory.CreateDirectory(SettingsDirectory);
        }
    }

    private static void EnsureHizFeature(UniversalRendererData rendererData)
    {
        if (rendererData == null)
        {
            return;
        }

        foreach (ScriptableRendererFeature feature in rendererData.rendererFeatures)
        {
            if (feature is GpuDrivenHizFeature existingHizFeature)
            {
                ConfigureHizFeature(existingHizFeature);
                return;
            }
        }

        GpuDrivenHizFeature createdHizFeature = ScriptableObject.CreateInstance<GpuDrivenHizFeature>();
        createdHizFeature.name = "GPU Driven Hi-Z";
        ConfigureHizFeature(createdHizFeature);
        AssetDatabase.AddObjectToAsset(createdHizFeature, rendererData);

        rendererData.rendererFeatures.Add(createdHizFeature);
        AddRendererFeatureMapEntry(rendererData, createdHizFeature);
    }

    private static void ConfigureHizFeature(GpuDrivenHizFeature hizFeature)
    {
        SerializedObject serializedFeature = new SerializedObject(hizFeature);
        SerializedProperty mipmapShader = serializedFeature.FindProperty("mipmapShader");
        SerializedProperty depthCopyShader = serializedFeature.FindProperty("depthCopyShader");
        SerializedProperty terrainDepthShader = serializedFeature.FindProperty("terrainDepthShader");
        SerializedProperty passEvent = serializedFeature.FindProperty("passEvent");

        if (mipmapShader != null)
        {
            mipmapShader.objectReferenceValue = Shader.Find("ComputeShader/DepthTextureMipmapCalculator");
        }

        if (depthCopyShader != null)
        {
            depthCopyShader.objectReferenceValue = Shader.Find("GPU Driven/URP Depth To RFloat");
        }

        if (terrainDepthShader != null)
        {
            terrainDepthShader.objectReferenceValue = Shader.Find("GPU Driven/GPUTerrain Hi-Z Depth");
        }

        if (passEvent != null)
        {
            passEvent.intValue = (int)RenderPassEvent.AfterRenderingOpaques;
        }

        serializedFeature.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(hizFeature);
    }

    private static UniversalRendererData CreateRendererDataAsset(string path)
    {
        MethodInfo createRendererAsset = typeof(UniversalRenderPipelineAsset).GetMethod(
            "CreateRendererAsset",
            BindingFlags.Static | BindingFlags.NonPublic,
            null,
            new[] { typeof(string), typeof(RendererType), typeof(bool), typeof(string) },
            null);

        if (createRendererAsset != null)
        {
            object result = createRendererAsset.Invoke(null, new object[] { path, RendererType.UniversalRenderer, false, "Renderer" });
            if (result is UniversalRendererData created)
            {
                return created;
            }
        }

        UniversalRendererData fallback = ScriptableObject.CreateInstance<UniversalRendererData>();
        AssetDatabase.CreateAsset(fallback, path);
        return fallback;
    }

    private static void AddRendererFeatureMapEntry(ScriptableRendererData rendererData, ScriptableRendererFeature feature)
    {
        FieldInfo mapField = typeof(ScriptableRendererData).GetField("m_RendererFeatureMap", BindingFlags.Instance | BindingFlags.NonPublic);
        if (mapField == null)
        {
            return;
        }

        if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feature, out string _, out long localId))
        {
            return;
        }

        if (mapField.GetValue(rendererData) is System.Collections.IList map)
        {
            map.Add(localId);
        }
    }
}
