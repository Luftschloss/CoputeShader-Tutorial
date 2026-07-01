using System.Collections.Generic;
using UnityEngine;

public sealed class GpuDrivenShowcaseController : MonoBehaviour
{
    public static GpuDrivenShowcaseController Instance { get; private set; }

    [Header("Runtime State")]
    [SerializeField] private GpuDrivenShowcaseCullingMode cullingMode = GpuDrivenShowcaseCullingMode.FrustumAndHiZ;
    [SerializeField] private GpuDrivenShowcaseDebugView debugView = GpuDrivenShowcaseDebugView.None;
    [SerializeField] private bool terrainColorDebug;

    [Header("Input")]
    [SerializeField] private bool enableHotkeys = true;
    [SerializeField] private KeyCode noCullingKey = KeyCode.Alpha1;
    [SerializeField] private KeyCode frustumKey = KeyCode.Alpha2;
    [SerializeField] private KeyCode hizKey = KeyCode.Alpha3;
    [SerializeField] private KeyCode lodDebugKey = KeyCode.Alpha4;
    [SerializeField] private KeyCode hizDebugKey = KeyCode.Alpha5;
    [SerializeField] private KeyCode terrainColorDebugKey = KeyCode.F6;
    [SerializeField] private KeyCode refreshModulesKey = KeyCode.F5;

    [Header("Modules")]
    [SerializeField] private bool autoFindModules = true;
    [SerializeField] private MonoBehaviour[] explicitModules;

    private readonly List<IGpuDrivenShowcaseModule> modules = new List<IGpuDrivenShowcaseModule>();
    private readonly HashSet<Object> boundTargets = new HashSet<Object>();
    private GpuDrivenShowcaseStats stats;

    public GpuDrivenShowcaseCullingMode CullingMode => cullingMode;
    public GpuDrivenShowcaseDebugView DebugView => debugView;
    public bool TerrainColorDebug => terrainColorDebug;
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

    public void SetTerrainColorDebug(bool enabled)
    {
        if (terrainColorDebug == enabled)
        {
            return;
        }

        terrainColorDebug = enabled;
        ApplyTerrainColorDebugToModules();
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
            modules[i].SetTerrainColorDebug(terrainColorDebug);
        }
    }

    private void ApplyTerrainColorDebugToModules()
    {
        for (int i = 0; i < modules.Count; i++)
        {
            modules[i].SetTerrainColorDebug(terrainColorDebug);
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

        if (Input.GetKeyDown(lodDebugKey))
        {
            SetDebugView(debugView == GpuDrivenShowcaseDebugView.Lod
                ? GpuDrivenShowcaseDebugView.None
                : GpuDrivenShowcaseDebugView.Lod);
        }
        else if (Input.GetKeyDown(hizDebugKey))
        {
            SetDebugView(debugView == GpuDrivenShowcaseDebugView.HiZ
                ? GpuDrivenShowcaseDebugView.None
                : GpuDrivenShowcaseDebugView.HiZ);
        }

        if (Input.GetKeyDown(terrainColorDebugKey))
        {
            SetTerrainColorDebug(!terrainColorDebug);
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
            hizTerrainDepthDrawCount = GpuDrivenHizFeature.LastTerrainDepthDrawCount,
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

        public void SetTerrainColorDebug(bool enabled)
        {
            if (target != null)
            {
                target.SetTerrainColorDebug(enabled);
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

        public void SetTerrainColorDebug(bool enabled)
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
