using UnityEngine;

public sealed class GpuDrivenShowcaseRuntimePanel : MonoBehaviour
{
    [SerializeField] private GpuDrivenShowcaseController controller;
    [SerializeField] private bool visible = true;
    [SerializeField] private bool expanded = true;
    [SerializeField] private KeyCode toggleKey = KeyCode.F1;
    [SerializeField] private Rect windowRect = new Rect(16, 16, 360, 260);
    [SerializeField] private Rect collapsedRect = new Rect(16, 16, 176, 30);

    private void Awake()
    {
        if (controller == null)
        {
            controller = GpuDrivenShowcaseController.Instance;
        }
    }

    private void Update()
    {
        if (Input.GetKeyDown(toggleKey))
        {
            expanded = !expanded;
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

        if (!expanded)
        {
            collapsedRect.x = windowRect.x;
            collapsedRect.y = windowRect.y;
            if (GUI.Button(collapsedRect, "GPU Driven Showcase >"))
            {
                expanded = true;
            }
            return;
        }

        windowRect = GUILayout.Window(GetInstanceID(), windowRect, DrawWindow, "GPU Driven Showcase");
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

        GUILayout.BeginHorizontal();
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("Collapse", GUILayout.Width(84), GUILayout.Height(22)))
        {
            expanded = false;
        }
        GUILayout.EndHorizontal();

        DrawModeButtons();
        GUILayout.Space(8);
        DrawDebugButtons();
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
        DrawDebugButton(GpuDrivenShowcaseDebugView.SceneWire, "4 Scene Wire");
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

    private void DrawStats()
    {
        GpuDrivenShowcaseStats stats = controller.Stats;
        bool debugStats = controller.DebugView == GpuDrivenShowcaseDebugView.SceneWire;
        GUILayout.Label("Stats");
        GUILayout.Label("Mode: " + controller.CullingMode.ToDisplayName());
        GUILayout.Label("Debug: " + controller.DebugView.ToDisplayName());
        GUILayout.Label("CPU Frame: " + stats.cpuFrameMs.ToString("0.00") + " ms");
        GUILayout.Label("Hi-Z: " + (stats.hizEnabled ? "On" : "Off"));
        GUILayout.Label("Terrain Patches: " + stats.terrainPatchCount);
        GUILayout.Label("Foliage Instances: " + stats.foliageInstanceCount);

        if (!debugStats)
        {
            GUILayout.Label("Status: " + stats.status);
            return;
        }

        GUILayout.Label("Visible Patches: " + stats.terrainVisiblePatchCount + " / " + stats.terrainPatchCount);
        GUILayout.Label("Visible Foliage: " + stats.foliageVisibleInstanceCount + " / " + stats.foliageInstanceCount);
        if (stats.hizEnabled)
        {
            GUILayout.Label("Terrain Culling: frustum " + stats.terrainFrustumVisiblePatchCount
                + " / outside " + stats.terrainFrustumRejectedPatchCount);
            GUILayout.Label("Terrain Hi-Z: rejected " + stats.terrainHiZRejectedPatchCount
                + " / tested " + stats.terrainHiZTestedPatchCount);
        }
        GUILayout.Label("Status: " + stats.status);
    }

    private void DrawHelp()
    {
        GUILayout.Label("Hotkeys");
        GUILayout.Label("1 None | 2 Frustum | 3 Hi-Z | 4 Scene Wire");
        GUILayout.Label("F1 Panel | F5 Rebind | RMB + WASDQE Fly");
    }
}
