using UnityEngine;

public sealed class GpuDrivenShowcaseRuntimePanel : MonoBehaviour
{
    [SerializeField] private GpuDrivenShowcaseController controller;
    [SerializeField] private bool visible = true;
    [SerializeField] private KeyCode toggleKey = KeyCode.F1;
    [SerializeField] private Rect windowRect = new Rect(16, 16, 380, 455);
    [SerializeField] private Rect hizPreviewRect = new Rect(364, 16, 256, 256);
    [SerializeField] private Shader hizDebugShader;
    [SerializeField] private int hizDebugMip;
    [SerializeField] private bool hizDebugLinearize = true;

    private Material hizDebugMaterial;
    private DepthTextureGenerator cachedDepthGenerator;
    private static readonly int MipId = Shader.PropertyToID("_Mip");
    private static readonly int LinearizeId = Shader.PropertyToID("_Linearize");

    private void Awake()
    {
        if (controller == null)
        {
            controller = GpuDrivenShowcaseController.Instance;
        }

        if (hizDebugShader == null)
        {
            hizDebugShader = Shader.Find("GPU Driven/Hi-Z Debug View");
        }
    }

    private void Update()
    {
        if (Input.GetKeyDown(toggleKey))
        {
            visible = !visible;
        }
    }

    private void OnGUI()
    {
        if (!visible)
        {
            return;
        }

        if (controller == null)
        {
            controller = GpuDrivenShowcaseController.Instance;
        }

        windowRect = GUILayout.Window(GetInstanceID(), windowRect, DrawWindow, "GPU Driven Showcase");
        DrawHizPreview();
    }

    private void DrawWindow(int id)
    {
        if (controller == null)
        {
            GUILayout.Label("Controller not found.");
            GUILayout.Label("Add GpuDrivenShowcaseController to the scene.");
            GUI.DragWindow();
            return;
        }

        DrawModeButtons();
        GUILayout.Space(8);
        DrawDebugButtons();
        GUILayout.Space(8);
        DrawTerrainOptions();
        GUILayout.Space(8);
        DrawStats();
        GUILayout.Space(8);
        DrawHelp();
        GUI.DragWindow();
    }

    private void DrawModeButtons()
    {
        GUILayout.Label("Culling Mode");
        GUILayout.BeginHorizontal();
        DrawModeButton(GpuDrivenShowcaseCullingMode.None, "1 None");
        DrawModeButton(GpuDrivenShowcaseCullingMode.Frustum, "2 Frustum");
        DrawModeButton(GpuDrivenShowcaseCullingMode.FrustumAndHiZ, "3 Hi-Z");
        GUILayout.EndHorizontal();
    }

    private void DrawModeButton(GpuDrivenShowcaseCullingMode mode, string label)
    {
        bool active = controller.CullingMode == mode;
        bool previousEnabled = GUI.enabled;
        GUI.enabled = !active;
        if (GUILayout.Button(active ? "[" + label + "]" : label, GUILayout.Height(28)))
        {
            controller.SetCullingMode(mode);
        }
        GUI.enabled = previousEnabled;
    }

    private void DrawDebugButtons()
    {
        GUILayout.Label("Debug View");
        GUILayout.BeginHorizontal();
        DrawDebugButton(GpuDrivenShowcaseDebugView.None, "Off");
        DrawDebugButton(GpuDrivenShowcaseDebugView.Lod, "4 LOD");
        DrawDebugButton(GpuDrivenShowcaseDebugView.HiZ, "5 Hi-Z");
        DrawDebugButton(GpuDrivenShowcaseDebugView.Bounds, "Bounds");
        GUILayout.EndHorizontal();
    }

    private void DrawDebugButton(GpuDrivenShowcaseDebugView view, string label)
    {
        bool active = controller.DebugView == view;
        bool previousEnabled = GUI.enabled;
        GUI.enabled = !active;
        if (GUILayout.Button(active ? "[" + label + "]" : label, GUILayout.Height(26)))
        {
            controller.SetDebugView(view);
        }
        GUI.enabled = previousEnabled;
    }

    private void DrawTerrainOptions()
    {
        bool terrainColorDebug = GUILayout.Toggle(controller.TerrainColorDebug, "Terrain LOD Color Debug (F6)");
        if (terrainColorDebug != controller.TerrainColorDebug)
        {
            controller.SetTerrainColorDebug(terrainColorDebug);
        }
    }

    private void DrawStats()
    {
        GpuDrivenShowcaseStats stats = controller.Stats;
        GUILayout.Label("Stats");
        GUILayout.Label("Mode: " + controller.CullingMode.ToDisplayName());
        GUILayout.Label("Debug: " + controller.DebugView.ToDisplayName());
        GUILayout.Label("Terrain Color Debug: " + (stats.terrainColorDebugEnabled ? "On" : "Off"));
        GUILayout.Label("Terrain Shadow: cast " + (stats.terrainShadowCasterEnabled ? "On" : "Off")
            + " / receive " + (stats.terrainShadowReceiverEnabled ? "On" : "Off"));
        GUILayout.Label("Modules: " + controller.ModuleCount);
        GUILayout.Label("CPU Frame: " + stats.cpuFrameMs.ToString("0.00") + " ms");
        GUILayout.Label("Hi-Z: " + (stats.hizEnabled ? "On" : "Off"));
        if (!string.IsNullOrEmpty(stats.depthTextureDescription))
        {
            GUILayout.Label("DepthTexture: " + stats.depthTextureDescription);
        }
        GUILayout.Label("Terrain Patches: " + stats.terrainVisiblePatchCount + " / " + stats.terrainPatchCount);
        if (stats.hizEnabled)
        {
            GUILayout.Label("Terrain Depth: " + (stats.terrainDepthOccluderEnabled ? "On" : "Off")
                + " / draws " + stats.hizTerrainDepthDrawCount);
            GUILayout.Label("Terrain Culling: dispatched " + stats.terrainCullingDispatchedPatchCount
                + " / frustum " + stats.terrainFrustumVisiblePatchCount
                + " / outside " + stats.terrainFrustumRejectedPatchCount);
            GUILayout.Label("Hi-Z Terrain: rejected " + stats.terrainHiZRejectedPatchCount
                + " / tested " + stats.terrainHiZTestedPatchCount
                + " / skipped " + stats.terrainHiZSkippedPatchCount);
        }
        GUILayout.Label("Foliage Instances: " + stats.foliageVisibleInstanceCount + " / " + stats.foliageInstanceCount);
        GUILayout.Label("Status: " + stats.status);
    }

    private void DrawHelp()
    {
        GUILayout.Label("Hotkeys");
        GUILayout.Label("1 None | 2 Frustum | 3 Hi-Z | 4 LOD | 5 Hi-Z View");
        GUILayout.Label("F1 Panel | F5 Rebind | F6 Terrain Color | RMB + WASDQE Fly");
    }

    private void DrawHizPreview()
    {
        if (controller == null || controller.DebugView != GpuDrivenShowcaseDebugView.HiZ)
        {
            return;
        }

        RenderTexture hizTexture = GetHizTexture();
        if (hizTexture == null)
        {
            return;
        }

        Material material = GetHizDebugMaterial();
        if (material != null)
        {
            int mip = GetClampedHizMip(hizTexture);
            material.SetFloat(MipId, mip);
            material.SetFloat(LinearizeId, hizDebugLinearize ? 1.0f : 0.0f);
            Graphics.DrawTexture(hizPreviewRect, hizTexture, material);
        }
        else
        {
            GUI.DrawTexture(hizPreviewRect, hizTexture, ScaleMode.ScaleToFit, false);
        }

        GUI.Box(hizPreviewRect, GUIContent.none);
        GUI.Label(new Rect(hizPreviewRect.x + 8.0f, hizPreviewRect.y + 6.0f, hizPreviewRect.width - 16.0f, 20.0f),
            "Hi-Z mip " + GetClampedHizMip(hizTexture));
    }

    private int GetClampedHizMip(RenderTexture hizTexture)
    {
        int maxMip = Mathf.Max(0, (int)Mathf.Log(hizTexture.width, 2) - 1);
        return Mathf.Clamp(hizDebugMip, 0, maxMip);
    }

    private RenderTexture GetHizTexture()
    {
        if (cachedDepthGenerator == null)
        {
            Camera mainCamera = Camera.main;
            if (mainCamera != null)
            {
                cachedDepthGenerator = mainCamera.GetComponent<DepthTextureGenerator>();
            }

            if (cachedDepthGenerator == null)
            {
                cachedDepthGenerator = FindObjectOfType<DepthTextureGenerator>();
            }
        }

        return cachedDepthGenerator != null ? cachedDepthGenerator.DepthTexture : null;
    }

    private Material GetHizDebugMaterial()
    {
        if (hizDebugMaterial == null && hizDebugShader != null)
        {
            hizDebugMaterial = new Material(hizDebugShader)
            {
                hideFlags = HideFlags.HideAndDontSave
            };
        }

        return hizDebugMaterial;
    }

    private void OnDestroy()
    {
        if (hizDebugMaterial != null)
        {
            Destroy(hizDebugMaterial);
            hizDebugMaterial = null;
        }
    }
}
