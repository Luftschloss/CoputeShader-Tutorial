using System.Collections.Generic;
using UnityEngine;

public sealed class GpuDrivenShowcaseController : MonoBehaviour
{
    public static GpuDrivenShowcaseController Instance { get; private set; }

    [Header("Runtime State")]
    [SerializeField] private GpuDrivenShowcaseCullingMode cullingMode = GpuDrivenShowcaseCullingMode.FrustumAndHiZ;
    [SerializeField] private GpuDrivenShowcaseDebugView debugView = GpuDrivenShowcaseDebugView.None;
    [SerializeField] private bool debugColorMode;

    [Header("Input")]
    [SerializeField] private bool enableHotkeys = true;
    [SerializeField] private KeyCode noCullingKey = KeyCode.Alpha1;
    [SerializeField] private KeyCode frustumKey = KeyCode.Alpha2;
    [SerializeField] private KeyCode hizKey = KeyCode.Alpha3;
    [SerializeField] private KeyCode debugViewKey = KeyCode.Alpha4;
    [SerializeField] private KeyCode debugColorKey = KeyCode.Alpha5;
    [SerializeField] private KeyCode refreshModulesKey = KeyCode.F5;

    [Header("Modules")]
    [SerializeField] private bool autoFindModules = true;
    [SerializeField] private MonoBehaviour[] explicitModules;

    private readonly List<IGpuDrivenShowcaseModule> modules = new List<IGpuDrivenShowcaseModule>();
    private readonly HashSet<Object> boundTargets = new HashSet<Object>();
    private GpuDrivenShowcaseStats stats;

    public GpuDrivenShowcaseCullingMode CullingMode => cullingMode;
    public GpuDrivenShowcaseDebugView DebugView => debugView;
    public bool DebugColorMode => debugColorMode;
    public GpuDrivenShowcaseStats Stats => stats;
    public int ModuleCount => modules.Count;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }

        Instance = this;
        RefreshModules();
    }

    private void Start()
    {
        NormalizeDebugView();
        ApplyModeToModules();
    }

    private void Update()
    {
        if (enableHotkeys)
        {
            HandleHotkeys();
        }

        UpdateStats();
    }

    private void OnValidate()
    {
        NormalizeDebugView();
        if (Application.isPlaying)
        {
            ApplyModeToModules();
        }
    }

    public void SetCullingMode(GpuDrivenShowcaseCullingMode mode)
    {
        if (cullingMode == mode)
        {
            return;
        }

        cullingMode = mode;
        ApplyModeToModules();
    }

    public void SetDebugView(GpuDrivenShowcaseDebugView view)
    {
        if (debugView == view)
        {
            return;
        }

        debugView = view;
        ApplyModeToModules();
    }

    public void SetDebugColorMode(bool enabled)
    {
        if (debugColorMode == enabled)
        {
            return;
        }

        debugColorMode = enabled;
        ApplyModeToModules();
    }

    public void RefreshModules()
    {
        modules.Clear();
        boundTargets.Clear();

        if (explicitModules != null)
        {
            for (int i = 0; i < explicitModules.Length; i++)
            {
                TryAddModule(explicitModules[i]);
            }
        }

        if (autoFindModules)
        {
            MonoBehaviour[] behaviours = FindObjectsOfType<MonoBehaviour>(true);
            for (int i = 0; i < behaviours.Length; i++)
            {
                TryAddModule(behaviours[i]);
            }

            GPUTerrain[] terrains = FindObjectsOfType<GPUTerrain>(true);
            for (int i = 0; i < terrains.Length; i++)
            {
                if (boundTargets.Add(terrains[i]))
                {
                    modules.Add(new TerrainModuleHandle(terrains[i]));
                }
            }

            DrawGrass[] foliageRenderers = FindObjectsOfType<DrawGrass>(true);
            for (int i = 0; i < foliageRenderers.Length; i++)
            {
                if (boundTargets.Add(foliageRenderers[i]))
                {
                    modules.Add(new FoliageModuleHandle(foliageRenderers[i]));
                }
            }
        }

        ApplyModeToModules();
    }

    private void TryAddModule(MonoBehaviour behaviour)
    {
        if (behaviour == null || behaviour == this)
        {
            return;
        }

        IGpuDrivenShowcaseModule module = behaviour as IGpuDrivenShowcaseModule;
        if (module == null)
        {
            return;
        }

        if (boundTargets.Add(behaviour))
        {
            modules.Add(module);
        }
    }

    private void ApplyModeToModules()
    {
        for (int i = 0; i < modules.Count; i++)
        {
            modules[i].SetCullingMode(cullingMode);
            modules[i].SetDebugView(debugView);
            modules[i].SetDebugColorMode(debugColorMode);
        }
    }

    private void NormalizeDebugView()
    {
        if (debugView != GpuDrivenShowcaseDebugView.None &&
            debugView != GpuDrivenShowcaseDebugView.SceneWire)
        {
            debugView = GpuDrivenShowcaseDebugView.SceneWire;
        }
    }

    private void HandleHotkeys()
    {
        if (Input.GetKeyDown(noCullingKey))
        {
            SetCullingMode(GpuDrivenShowcaseCullingMode.None);
        }
        else if (Input.GetKeyDown(frustumKey))
        {
            SetCullingMode(GpuDrivenShowcaseCullingMode.Frustum);
        }
        else if (Input.GetKeyDown(hizKey))
        {
            SetCullingMode(GpuDrivenShowcaseCullingMode.FrustumAndHiZ);
        }

        if (Input.GetKeyDown(debugViewKey))
        {
            SetDebugView(debugView == GpuDrivenShowcaseDebugView.SceneWire
                ? GpuDrivenShowcaseDebugView.None
                : GpuDrivenShowcaseDebugView.SceneWire);
        }

        if (Input.GetKeyDown(debugColorKey))
        {
            SetDebugColorMode(!debugColorMode);
        }

        if (Input.GetKeyDown(refreshModulesKey))
        {
            RefreshModules();
        }
    }

    private void UpdateStats()
    {
        stats = new GpuDrivenShowcaseStats
        {
            cpuFrameMs = Time.unscaledDeltaTime * 1000.0f,
            status = modules.Count == 0 ? "No showcase modules bound" : "Running"
        };

        for (int i = 0; i < modules.Count; i++)
        {
            modules[i].CollectStats(ref stats);
        }
    }

    private sealed class TerrainModuleHandle : IGpuDrivenShowcaseModule
    {
        private readonly GPUTerrain target;

        public TerrainModuleHandle(GPUTerrain target)
        {
            this.target = target;
        }

        public string DisplayName => "GPU Terrain";

        public void SetCullingMode(GpuDrivenShowcaseCullingMode mode)
        {
            if (target != null)
            {
                target.SetShowcaseCullingMode(mode);
            }
        }

        public void SetDebugView(GpuDrivenShowcaseDebugView view)
        {
            if (target != null)
            {
                target.SetShowcaseDebugView(view);
            }
        }

        public void SetDebugColorMode(bool enabled)
        {
            if (target != null)
            {
                target.SetShowcaseDebugColorMode(enabled);
            }
        }

        public void CollectStats(ref GpuDrivenShowcaseStats stats)
        {
            if (target != null)
            {
                target.CollectShowcaseStats(ref stats);
            }
        }
    }

    private sealed class FoliageModuleHandle : IGpuDrivenShowcaseModule
    {
        private readonly DrawGrass target;

        public FoliageModuleHandle(DrawGrass target)
        {
            this.target = target;
        }

        public string DisplayName => "GPU Foliage";

        public void SetCullingMode(GpuDrivenShowcaseCullingMode mode)
        {
            if (target != null)
            {
                target.SetShowcaseCullingMode(mode);
            }
        }

        public void SetDebugView(GpuDrivenShowcaseDebugView view)
        {
            if (target != null)
            {
                target.SetShowcaseDebugView(view);
            }
        }

        public void SetDebugColorMode(bool enabled)
        {
        }

        public void CollectStats(ref GpuDrivenShowcaseStats stats)
        {
            if (target != null)
            {
                target.CollectShowcaseStats(ref stats);
            }
        }
    }
}
