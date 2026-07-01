#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

[InitializeOnLoad]
public static class GpuDrivenShowcaseSceneViewOverlay
{
    private static readonly Rect PreviewRect = new Rect(16, 16, 256, 256);
    private static readonly int MipId = Shader.PropertyToID("_Mip");
    private static readonly int LinearizeId = Shader.PropertyToID("_Linearize");
    private static Material hizDebugMaterial;

    static GpuDrivenShowcaseSceneViewOverlay()
    {
        SceneView.duringSceneGui += OnSceneGui;
    }

    private static void OnSceneGui(SceneView sceneView)
    {
        GpuDrivenShowcaseController controller = GetShowcaseController();
        if (!IsHiZDebugEnabled(controller))
        {
            return;
        }

        RenderTexture hizTexture = GetHizTexture();
        if (hizTexture == null)
        {
            return;
        }

        Handles.BeginGUI();

        Material material = GetHizDebugMaterial();
        if (material != null)
        {
            material.SetFloat(MipId, 0.0f);
            material.SetFloat(LinearizeId, 1.0f);
            Graphics.DrawTexture(PreviewRect, hizTexture, material);
        }
        else
        {
            GUI.DrawTexture(PreviewRect, hizTexture, ScaleMode.ScaleToFit, false);
        }

        GUI.Box(PreviewRect, GUIContent.none);
        GUI.Label(new Rect(PreviewRect.x + 8.0f, PreviewRect.y + 6.0f, PreviewRect.width - 16.0f, 18.0f), "SceneView Hi-Z");

        if (controller != null)
        {
            GpuDrivenShowcaseStats stats = controller.Stats;
            GUI.Label(new Rect(PreviewRect.x + 8.0f, PreviewRect.y + 24.0f, PreviewRect.width - 16.0f, 18.0f),
                "Depth " + stats.depthTextureDescription);
            GUI.Label(new Rect(PreviewRect.x + 8.0f, PreviewRect.y + 42.0f, PreviewRect.width - 16.0f, 18.0f),
                "Terrain depth draws " + stats.hizTerrainDepthDrawCount);
        }

        Handles.EndGUI();
    }

    private static bool IsHiZDebugEnabled(GpuDrivenShowcaseController controller)
    {
        return controller != null && controller.DebugView == GpuDrivenShowcaseDebugView.HiZ;
    }

    private static GpuDrivenShowcaseController GetShowcaseController()
    {
        if (GpuDrivenShowcaseController.Instance != null)
        {
            return GpuDrivenShowcaseController.Instance;
        }

        return Object.FindObjectOfType<GpuDrivenShowcaseController>(true);
    }

    private static RenderTexture GetHizTexture()
    {
        Camera mainCamera = Camera.main;
        if (mainCamera != null)
        {
            DepthTextureGenerator generator = mainCamera.GetComponent<DepthTextureGenerator>();
            if (generator != null)
            {
                return generator.DepthTexture;
            }
        }

        DepthTextureGenerator fallback = Object.FindObjectOfType<DepthTextureGenerator>(true);
        return fallback != null ? fallback.DepthTexture : null;
    }

    private static Material GetHizDebugMaterial()
    {
        if (hizDebugMaterial == null)
        {
            Shader shader = Shader.Find("GPU Driven/Hi-Z Debug View");
            if (shader == null)
            {
                return null;
            }

            hizDebugMaterial = new Material(shader)
            {
                hideFlags = HideFlags.HideAndDontSave
            };
        }

        return hizDebugMaterial;
    }
}
#endif
