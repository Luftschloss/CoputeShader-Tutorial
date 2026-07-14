using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
#if UNITY_EDITOR
using UnityEditor;
#endif

public sealed class GpuDrivenFoliageRenderer : MonoBehaviour, IGpuDrivenShowcaseModule
{
    private static readonly int MatrixBufferId = Shader.PropertyToID("_GpuDrivenFoliageMatrices");
    private static readonly int BaseMapId = Shader.PropertyToID("_BaseMap");
    private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    private static readonly int CutoffId = Shader.PropertyToID("_Cutoff");
    private static readonly int BillboardId = Shader.PropertyToID("_GpuDrivenFoliageBillboard");
    private static readonly int AllMatricesId = Shader.PropertyToID("_AllMatrices");
    private static readonly int VisibleMatricesId = Shader.PropertyToID("_VisibleMatrices");
    private static readonly int VPMatrixId = Shader.PropertyToID("_VPMatrix");
    private static readonly int HiZMapId = Shader.PropertyToID("_HiZMap");
    private static readonly int BoundsCenterId = Shader.PropertyToID("_BoundsCenter");
    private static readonly int BoundsExtentsId = Shader.PropertyToID("_BoundsExtents");
    private static readonly int DepthTextureSizeId = Shader.PropertyToID("_DepthTextureSize");
    private static readonly int InstanceCountId = Shader.PropertyToID("_InstanceCount");
    private static readonly int UseHiZId = Shader.PropertyToID("_UseHiZ");
    private static readonly int UseReversedZId = Shader.PropertyToID("_UseReversedZ");
    private static readonly int IsOpenGLId = Shader.PropertyToID("_IsOpenGL");
    private static readonly int FrustumPaddingId = Shader.PropertyToID("_FrustumPadding");
    private static readonly int HiZDepthBiasId = Shader.PropertyToID("_HiZDepthBias");
    private static readonly int DebugColorModeId = Shader.PropertyToID("_GpuDrivenFoliageDebugColorMode");
    private static readonly int DebugColorId = Shader.PropertyToID("_GpuDrivenFoliageDebugColor");

    [Header("Data")]
    [SerializeField] private GpuDrivenFoliageData foliageData;
    [SerializeField] private ComputeShader cullingCompute;
    [SerializeField] private Shader foliageShader;
    [SerializeField] private DepthTextureGenerator depthTextureGenerator;

    [Header("Culling")]
    [SerializeField] private GpuDrivenShowcaseCullingMode cullingMode = GpuDrivenShowcaseCullingMode.FrustumAndHiZ;
    [SerializeField, Range(0.0f, 0.2f)] private float frustumPadding = 0.02f;
    [SerializeField, Range(0.0f, 0.05f)] private float hizDepthBias = 0.001f;

    [Header("Material Defaults")]
    [SerializeField] private Color fallbackBaseColor = Color.white;
    [SerializeField, Range(0.0f, 1.0f)] private float alphaCutoff = 0.35f;
    [SerializeField] private List<FoliageMaterialOverride> materialOverrides = new List<FoliageMaterialOverride>();

    [Header("Debug")]
    [SerializeField, Min(0)] private int debugMaxDrawnInstances = 1024;
    [SerializeField] private bool debugDrawWorldBounds = true;

    private readonly List<PrototypeRuntime> runtimes = new List<PrototypeRuntime>();
    private readonly uint[] args = new uint[5];
    private MaterialPropertyBlock propertyBlock;
    private Camera mainCamera;
    private int cullKernel = -1;
    private float nextStatsReadbackTime;
    private int lastVisibleInstanceCount;
    private bool lastHizActive;
    private bool debugStatsEnabled;
    private bool sceneWireDebugEnabled;
    private bool debugColorModeEnabled;
    private bool debugVisibleReadbackActive;

    public string DisplayName => "GPU Foliage";
    public GpuDrivenFoliageData FoliageData => foliageData;

    private void OnEnable()
    {
        mainCamera = Camera.main;
        if (depthTextureGenerator == null && mainCamera != null)
        {
            depthTextureGenerator = mainCamera.GetComponent<DepthTextureGenerator>();
        }

        if (foliageShader == null)
        {
            foliageShader = Shader.Find("GPU Driven/Foliage Indirect");
        }

        SyncMaterialOverridesWithData();

        if (Application.isPlaying)
        {
            Rebuild();
        }
    }

#if UNITY_EDITOR
    private void Reset()
    {
        AutoAssignEditorDefaults();
    }

    private void OnValidate()
    {
        AutoAssignEditorDefaults();
    }

    private void AutoAssignEditorDefaults()
    {
        if (cullingCompute == null)
        {
            cullingCompute = AssetDatabase.LoadAssetAtPath<ComputeShader>(
                "Assets/GPUDrivenShowcase/Shaders/GpuDrivenFoliageCulling.compute");
        }

        if (foliageShader == null)
        {
            foliageShader = Shader.Find("GPU Driven/Foliage Indirect");
        }

        if (depthTextureGenerator == null && Camera.main != null)
        {
            depthTextureGenerator = Camera.main.GetComponent<DepthTextureGenerator>();
        }

        SyncMaterialOverridesWithData();
    }
#endif

    private void Update()
    {
        if (mainCamera == null)
        {
            mainCamera = Camera.main;
        }

        if (mainCamera == null || foliageData == null || runtimes.Count == 0)
        {
            return;
        }

        bool useCulling = cullingMode != GpuDrivenShowcaseCullingMode.None;
        bool cullingActive = useCulling && cullingCompute != null && cullKernel >= 0;
        bool useHiZ = cullingMode.UsesHiZ() &&
                      depthTextureGenerator != null &&
                      depthTextureGenerator.DepthTexture != null;
        lastHizActive = useHiZ;
        if (useHiZ && depthTextureGenerator != null)
        {
            depthTextureGenerator.useHiz = true;
        }

        if (cullingActive)
        {
            DispatchCulling(useHiZ);
        }
        else
        {
            DrawAllInstances();
        }

        DrawVisibleInstances(cullingActive);
        ReadbackDebugDataIfNeeded(cullingActive && debugStatsEnabled);
    }

    public void Rebuild()
    {
        ReleaseRuntimeResources();
        lastVisibleInstanceCount = foliageData != null ? foliageData.InstanceCount : 0;

        if (foliageData == null || foliageData.InstanceCount == 0 || foliageData.PrototypeCount == 0)
        {
            return;
        }

        if (propertyBlock == null)
        {
            propertyBlock = new MaterialPropertyBlock();
        }

        cullKernel = -1;
        if (cullingCompute != null)
        {
            cullKernel = cullingCompute.FindKernel("CullFoliage");
        }

        List<Matrix4x4>[] groupedMatrices = new List<Matrix4x4>[foliageData.PrototypeCount];
        for (int i = 0; i < groupedMatrices.Length; i++)
        {
            groupedMatrices[i] = new List<Matrix4x4>();
        }

        IReadOnlyList<GpuDrivenFoliageInstance> instances = foliageData.Instances;
        for (int i = 0; i < instances.Count; i++)
        {
            int prototypeIndex = instances[i].prototypeIndex;
            if (prototypeIndex >= 0 && prototypeIndex < groupedMatrices.Length)
            {
                groupedMatrices[prototypeIndex].Add(instances[i].LocalToWorld);
            }
        }

        IReadOnlyList<GpuDrivenFoliagePrototype> prototypes = foliageData.Prototypes;
        for (int i = 0; i < prototypes.Count; i++)
        {
            GpuDrivenFoliagePrototype prototype = prototypes[i];
            List<Matrix4x4> matrices = groupedMatrices[i];
            if (prototype.mesh == null || prototype.subMeshIndex < 0 ||
                prototype.subMeshIndex >= prototype.mesh.subMeshCount || matrices.Count == 0)
            {
                continue;
            }

            PrototypeRuntime runtime = new PrototypeRuntime(i, prototype, matrices.Count);
            runtime.material = CreateRuntimeMaterial(prototype, ResolveMaterialOverride(i));
            runtime.allMatricesBuffer.SetData(matrices);
            ResetArgs(runtime, matrices.Count);
            runtimes.Add(runtime);
        }
    }

    public void SetFoliageData(GpuDrivenFoliageData data)
    {
        foliageData = data;
        SyncMaterialOverridesWithData();
        if (Application.isPlaying && isActiveAndEnabled)
        {
            Rebuild();
        }
    }

    public void SetCullingMode(GpuDrivenShowcaseCullingMode mode)
    {
        cullingMode = mode;
    }

    public void SetDebugView(GpuDrivenShowcaseDebugView view)
    {
        sceneWireDebugEnabled = view == GpuDrivenShowcaseDebugView.SceneWire;
        debugStatsEnabled = sceneWireDebugEnabled;
        if (!debugStatsEnabled)
        {
            ClearDebugVisibleReadback();
        }
    }

    public void SetDebugColorMode(bool enabled)
    {
        debugColorModeEnabled = enabled;
    }

    public void CollectStats(ref GpuDrivenShowcaseStats showcaseStats)
    {
        showcaseStats.foliageInstanceCount += foliageData != null ? foliageData.InstanceCount : 0;
        showcaseStats.foliageVisibleInstanceCount += lastVisibleInstanceCount;
        showcaseStats.hizEnabled |= lastHizActive;
    }

    private void DispatchCulling(bool useHiZ)
    {
        Matrix4x4 vpMatrix = GL.GetGPUProjectionMatrix(mainCamera.projectionMatrix, false) * mainCamera.worldToCameraMatrix;
        cullingCompute.SetMatrix(VPMatrixId, vpMatrix);
        cullingCompute.SetInt(UseHiZId, useHiZ ? 1 : 0);
        cullingCompute.SetInt(UseReversedZId, SystemInfo.usesReversedZBuffer ? 1 : 0);
        cullingCompute.SetInt(IsOpenGLId, IsOpenGLClipSpace() ? 1 : 0);
        cullingCompute.SetFloat(FrustumPaddingId, frustumPadding);
        cullingCompute.SetFloat(HiZDepthBiasId, hizDepthBias);

        if (depthTextureGenerator != null)
        {
            RenderTexture depthTexture = depthTextureGenerator.DepthTexture;
            if (depthTexture != null)
            {
                // z 使用 DepthTextureGenerator 的有效 mip 数，foliage compute 会用它限制最高采样 mip。
                cullingCompute.SetVector(DepthTextureSizeId, new Vector4(
                    depthTexture.width,
                    depthTexture.height,
                    depthTextureGenerator.DepthTextureMipCount,
                    0.0f));
                cullingCompute.SetTexture(cullKernel, HiZMapId, depthTexture);
            }
            else
            {
                cullingCompute.SetVector(DepthTextureSizeId, Vector4.zero);
                // 禁用 Hi-Z 时仍绑定有效纹理，避免 compute kernel 校验纹理槽失败。
                cullingCompute.SetTexture(cullKernel, HiZMapId, Texture2D.blackTexture);
            }
        }
        else
        {
            cullingCompute.SetVector(DepthTextureSizeId, Vector4.zero);
            // 禁用 Hi-Z 时仍绑定有效纹理，避免 compute kernel 校验纹理槽失败。
            cullingCompute.SetTexture(cullKernel, HiZMapId, Texture2D.blackTexture);
        }

        for (int i = 0; i < runtimes.Count; i++)
        {
            PrototypeRuntime runtime = runtimes[i];
            runtime.visibleMatricesBuffer.SetCounterValue(0);
            cullingCompute.SetInt(InstanceCountId, runtime.instanceCount);
            cullingCompute.SetVector(BoundsCenterId, runtime.prototype.localBounds.center);
            cullingCompute.SetVector(BoundsExtentsId, runtime.prototype.localBounds.extents);
            cullingCompute.SetBuffer(cullKernel, AllMatricesId, runtime.allMatricesBuffer);
            cullingCompute.SetBuffer(cullKernel, VisibleMatricesId, runtime.visibleMatricesBuffer);
            cullingCompute.Dispatch(cullKernel, Mathf.CeilToInt(runtime.instanceCount / 64.0f), 1, 1);
            ComputeBuffer.CopyCount(runtime.visibleMatricesBuffer, runtime.argsBuffer, sizeof(uint));
        }
    }

    private void DrawAllInstances()
    {
        lastVisibleInstanceCount = 0;
        for (int i = 0; i < runtimes.Count; i++)
        {
            ResetArgs(runtimes[i], runtimes[i].instanceCount);
            lastVisibleInstanceCount += runtimes[i].instanceCount;
        }
    }

    private void DrawVisibleInstances(bool useVisibleBuffer)
    {
        Bounds drawBounds = foliageData != null ? foliageData.WorldBounds : new Bounds(transform.position, Vector3.one);
        if (drawBounds.size == Vector3.zero)
        {
            drawBounds.size = Vector3.one;
        }

        for (int i = 0; i < runtimes.Count; i++)
        {
            PrototypeRuntime runtime = runtimes[i];
            if (runtime.material == null)
            {
                continue;
            }

            propertyBlock.Clear();
            propertyBlock.SetBuffer(MatrixBufferId, useVisibleBuffer ? runtime.visibleMatricesBuffer : runtime.allMatricesBuffer);
            propertyBlock.SetFloat(DebugColorModeId, debugColorModeEnabled ? 1.0f : 0.0f);
            propertyBlock.SetColor(DebugColorId, GetDebugColor(runtime.prototypeIndex));
            Graphics.DrawMeshInstancedIndirect(
                runtime.prototype.mesh,
                runtime.prototype.subMeshIndex,
                runtime.material,
                drawBounds,
                runtime.argsBuffer,
                0,
                propertyBlock,
                runtime.prototype.shadowCastingMode,
                runtime.prototype.receiveShadows,
                gameObject.layer,
                mainCamera);
        }
    }

    private void ReadbackDebugDataIfNeeded(bool useCulling)
    {
        if (!useCulling)
        {
            ClearDebugVisibleReadback();
            return;
        }

        if (Time.unscaledTime < nextStatsReadbackTime)
        {
            return;
        }

        nextStatsReadbackTime = Time.unscaledTime + 0.25f;
        lastVisibleInstanceCount = 0;
        debugVisibleReadbackActive = true;
        int perRuntimeBudget = GetPerRuntimeDebugBudget();
        for (int i = 0; i < runtimes.Count; i++)
        {
            PrototypeRuntime runtime = runtimes[i];
            runtime.argsBuffer.GetData(args);
            int visibleCount = (int)args[1];
            lastVisibleInstanceCount += visibleCount;

            int readCount = Mathf.Min(visibleCount, perRuntimeBudget);
            runtime.debugVisibleCount = readCount;
            if (readCount <= 0)
            {
                continue;
            }

            runtime.EnsureDebugVisibleCapacity(readCount);
            runtime.visibleMatricesBuffer.GetData(runtime.debugVisibleMatrices, 0, 0, readCount);
        }
    }

    private int GetPerRuntimeDebugBudget()
    {
        if (debugMaxDrawnInstances <= 0 || runtimes.Count == 0)
        {
            return 0;
        }

        return Mathf.Max(1, Mathf.CeilToInt(debugMaxDrawnInstances / (float)runtimes.Count));
    }

    private void ClearDebugVisibleReadback()
    {
        debugVisibleReadbackActive = false;
        for (int i = 0; i < runtimes.Count; i++)
        {
            runtimes[i].debugVisibleCount = 0;
        }
    }

    private Material CreateRuntimeMaterial(GpuDrivenFoliagePrototype prototype, Material overrideMaterial)
    {
        Shader shader = foliageShader != null ? foliageShader : Shader.Find("GPU Driven/Foliage Indirect");
        Material source = overrideMaterial != null ? overrideMaterial : prototype.sourceMaterial;
        if (shader == null)
        {
            return source;
        }

        if (source != null && source.shader == shader)
        {
            return source;
        }

        Material material = new Material(shader)
        {
            hideFlags = HideFlags.HideAndDontSave
        };

        Texture baseMap = prototype.baseMap;
        Color baseColor = prototype.baseColor.a > 0.0f ? prototype.baseColor : fallbackBaseColor;
        if (source != null)
        {
            if (baseMap == null && source.HasProperty(BaseMapId))
            {
                baseMap = source.GetTexture(BaseMapId);
            }
            else if (baseMap == null && source.HasProperty("_MainTex"))
            {
                baseMap = source.GetTexture("_MainTex");
            }

            if (prototype.baseColor.a <= 0.0f && source.HasProperty(BaseColorId))
            {
                baseColor = source.GetColor(BaseColorId);
            }
            else if (prototype.baseColor.a <= 0.0f && source.HasProperty("_Color"))
            {
                baseColor = source.GetColor("_Color");
            }
        }

        if (baseMap != null)
        {
            material.SetTexture(BaseMapId, baseMap);
        }
        material.SetColor(BaseColorId, baseColor);
        material.SetFloat(CutoffId, prototype.alphaCutoff > 0.0f ? prototype.alphaCutoff : alphaCutoff);
        if (material.HasProperty(BillboardId))
        {
            material.SetFloat(BillboardId, prototype.billboard ? 1.0f : 0.0f);
        }
        return material;
    }

    private Material ResolveMaterialOverride(int prototypeIndex)
    {
        if (materialOverrides == null)
        {
            return null;
        }

        for (int i = 0; i < materialOverrides.Count; i++)
        {
            FoliageMaterialOverride materialOverride = materialOverrides[i];
            if (materialOverride != null &&
                materialOverride.prototypeIndex == prototypeIndex &&
                materialOverride.material != null)
            {
                return materialOverride.material;
            }
        }

        return null;
    }

    private void SyncMaterialOverridesWithData()
    {
        if (foliageData == null || foliageData.PrototypeCount <= 0)
        {
            return;
        }

        if (materialOverrides == null)
        {
            materialOverrides = new List<FoliageMaterialOverride>();
        }

        Material[] preservedMaterials = new Material[foliageData.PrototypeCount];
        for (int i = 0; i < materialOverrides.Count; i++)
        {
            FoliageMaterialOverride materialOverride = materialOverrides[i];
            if (materialOverride == null ||
                materialOverride.prototypeIndex < 0 ||
                materialOverride.prototypeIndex >= preservedMaterials.Length ||
                !ShouldPreserveMaterialOverride(materialOverride.material))
            {
                continue;
            }

            preservedMaterials[materialOverride.prototypeIndex] = materialOverride.material;
        }

        materialOverrides.Clear();
        IReadOnlyList<GpuDrivenFoliagePrototype> prototypes = foliageData.Prototypes;
        for (int i = 0; i < foliageData.PrototypeCount; i++)
        {
            Material material = preservedMaterials[i] != null
                ? preservedMaterials[i]
                : prototypes[i].sourceMaterial;
            materialOverrides.Add(new FoliageMaterialOverride
            {
                prototypeIndex = i,
                material = material
            });
        }
    }

    private bool ShouldPreserveMaterialOverride(Material material)
    {
        if (material == null)
        {
            return false;
        }

        Shader shader = foliageShader != null ? foliageShader : Shader.Find("GPU Driven/Foliage Indirect");
        return shader != null && material.shader == shader;
    }

    private void ResetArgs(PrototypeRuntime runtime, int instanceCount)
    {
        args[0] = runtime.prototype.mesh.GetIndexCount(runtime.prototype.subMeshIndex);
        args[1] = (uint)instanceCount;
        args[2] = runtime.prototype.mesh.GetIndexStart(runtime.prototype.subMeshIndex);
        args[3] = runtime.prototype.mesh.GetBaseVertex(runtime.prototype.subMeshIndex);
        args[4] = 0;
        runtime.argsBuffer.SetData(args);
    }

    private void OnDisable()
    {
        ReleaseRuntimeResources();
    }

    private void ReleaseRuntimeResources()
    {
        for (int i = 0; i < runtimes.Count; i++)
        {
            runtimes[i].Release();
        }
        runtimes.Clear();
    }

    private static bool IsOpenGLClipSpace()
    {
        GraphicsDeviceType deviceType = SystemInfo.graphicsDeviceType;
        return deviceType == GraphicsDeviceType.OpenGLCore ||
               deviceType == GraphicsDeviceType.OpenGLES2 ||
               deviceType == GraphicsDeviceType.OpenGLES3;
    }

    private void OnDrawGizmos()
    {
        if (!sceneWireDebugEnabled || foliageData == null)
        {
            return;
        }

        Bounds worldBounds = foliageData.WorldBounds;
        if (debugDrawWorldBounds)
        {
            Gizmos.color = new Color(0.1f, 0.9f, 0.3f, 0.85f);
            Gizmos.DrawWireCube(worldBounds.center, worldBounds.size);
        }

#if UNITY_EDITOR
        DrawDebugLabel(worldBounds);
#endif

        if (debugMaxDrawnInstances <= 0 || foliageData.InstanceCount == 0 || foliageData.PrototypeCount == 0)
        {
            return;
        }

        bool drawRuntimeVisible = Application.isPlaying && debugVisibleReadbackActive && runtimes.Count > 0;
        DrawBakedInstanceSample(drawRuntimeVisible);
        if (drawRuntimeVisible)
        {
            DrawRuntimeVisibleGizmos();
        }
    }

    private void DrawBakedInstanceSample(bool dimmed)
    {
        IReadOnlyList<GpuDrivenFoliageInstance> instances = foliageData.Instances;
        IReadOnlyList<GpuDrivenFoliagePrototype> prototypes = foliageData.Prototypes;
        int prototypeCount = prototypes.Count;
        int perPrototypeBudget = GetPerPrototypeDebugBudget(prototypeCount);
        int[] prototypeInstanceCounts = new int[prototypeCount];
        for (int i = 0; i < instances.Count; i++)
        {
            int prototypeIndex = instances[i].prototypeIndex;
            if (prototypeIndex >= 0 && prototypeIndex < prototypeCount)
            {
                prototypeInstanceCounts[prototypeIndex]++;
            }
        }

        int[] prototypeStrides = new int[prototypeCount];
        int[] prototypeSeenCounts = new int[prototypeCount];
        int[] prototypeDrawnCounts = new int[prototypeCount];
        for (int i = 0; i < prototypeCount; i++)
        {
            prototypeStrides[i] = Mathf.Max(1, Mathf.CeilToInt(prototypeInstanceCounts[i] / (float)perPrototypeBudget));
        }

        for (int i = 0; i < instances.Count; i++)
        {
            GpuDrivenFoliageInstance instance = instances[i];
            int prototypeIndex = instance.prototypeIndex;
            if (prototypeIndex < 0 || prototypeIndex >= prototypeCount)
            {
                continue;
            }

            int seenIndex = prototypeSeenCounts[prototypeIndex]++;
            if (prototypeDrawnCounts[prototypeIndex] >= perPrototypeBudget ||
                seenIndex % prototypeStrides[prototypeIndex] != 0)
            {
                continue;
            }

            Matrix4x4 matrix = instance.LocalToWorld;
            Bounds bounds = TransformBounds(prototypes[prototypeIndex].localBounds, matrix);
            Color color = GetDebugColor(prototypeIndex);
            if (dimmed)
            {
                color.a = 0.25f;
            }
            Gizmos.color = color;
            Gizmos.DrawWireCube(bounds.center, bounds.size);
            prototypeDrawnCounts[prototypeIndex]++;
        }
    }

    private int GetPerPrototypeDebugBudget(int prototypeCount)
    {
        if (debugMaxDrawnInstances <= 0 || prototypeCount <= 0)
        {
            return 0;
        }

        return Mathf.Max(1, Mathf.CeilToInt(debugMaxDrawnInstances / (float)prototypeCount));
    }

    private void DrawRuntimeVisibleGizmos()
    {
        for (int i = 0; i < runtimes.Count; i++)
        {
            PrototypeRuntime runtime = runtimes[i];
            if (runtime.debugVisibleCount <= 0 || runtime.debugVisibleMatrices == null)
            {
                continue;
            }

            Gizmos.color = GetDebugColor(runtime.prototypeIndex);
            for (int matrixIndex = 0; matrixIndex < runtime.debugVisibleCount; matrixIndex++)
            {
                Bounds bounds = TransformBounds(runtime.prototype.localBounds, runtime.debugVisibleMatrices[matrixIndex]);
                Gizmos.DrawWireCube(bounds.center, bounds.size);
            }
        }
    }

#if UNITY_EDITOR
    private void DrawDebugLabel(Bounds worldBounds)
    {
        Vector3 labelPosition = worldBounds.center + Vector3.up * Mathf.Max(2.0f, worldBounds.extents.y + 1.0f);
        string visibleText = debugVisibleReadbackActive
            ? lastVisibleInstanceCount + " / " + foliageData.InstanceCount
            : "sample only / " + foliageData.InstanceCount;
        Handles.Label(
            labelPosition,
            "GPU Foliage\nVisible: " + visibleText +
            "\nPrototype: " + foliageData.PrototypeCount +
            "\nHi-Z: " + (lastHizActive ? "On" : "Off"));
    }
#endif

    private static Bounds TransformBounds(Bounds localBounds, Matrix4x4 localToWorld)
    {
        Vector3 center = localToWorld.MultiplyPoint3x4(localBounds.center);
        Vector3 extents = localBounds.extents;
        Vector3 axisX = localToWorld.MultiplyVector(new Vector3(extents.x, 0.0f, 0.0f));
        Vector3 axisY = localToWorld.MultiplyVector(new Vector3(0.0f, extents.y, 0.0f));
        Vector3 axisZ = localToWorld.MultiplyVector(new Vector3(0.0f, 0.0f, extents.z));
        extents = new Vector3(
            Mathf.Abs(axisX.x) + Mathf.Abs(axisY.x) + Mathf.Abs(axisZ.x),
            Mathf.Abs(axisX.y) + Mathf.Abs(axisY.y) + Mathf.Abs(axisZ.y),
            Mathf.Abs(axisX.z) + Mathf.Abs(axisY.z) + Mathf.Abs(axisZ.z));
        return new Bounds(center, extents * 2.0f);
    }

    private static Color GetDebugColor(int prototypeIndex)
    {
        float hue = Mathf.Repeat(prototypeIndex * 0.6180339f, 1.0f);
        Color color = Color.HSVToRGB(hue, 0.75f, 1.0f);
        color.a = 0.85f;
        return color;
    }

#pragma warning disable 0649
    [Serializable]
    private sealed class FoliageMaterialOverride
    {
        public int prototypeIndex;
        public Material material;
    }
#pragma warning restore 0649

    private sealed class PrototypeRuntime
    {
        public readonly int prototypeIndex;
        public readonly GpuDrivenFoliagePrototype prototype;
        public readonly int instanceCount;
        public readonly ComputeBuffer allMatricesBuffer;
        public readonly ComputeBuffer visibleMatricesBuffer;
        public readonly ComputeBuffer argsBuffer;
        public Matrix4x4[] debugVisibleMatrices;
        public int debugVisibleCount;
        public Material material;

        public PrototypeRuntime(int prototypeIndex, GpuDrivenFoliagePrototype prototype, int instanceCount)
        {
            this.prototypeIndex = prototypeIndex;
            this.prototype = prototype;
            this.instanceCount = instanceCount;
            allMatricesBuffer = new ComputeBuffer(instanceCount, sizeof(float) * 16);
            visibleMatricesBuffer = new ComputeBuffer(instanceCount, sizeof(float) * 16, ComputeBufferType.Append);
            argsBuffer = new ComputeBuffer(1, sizeof(uint) * 5, ComputeBufferType.IndirectArguments);
        }

        public void EnsureDebugVisibleCapacity(int count)
        {
            if (debugVisibleMatrices == null || debugVisibleMatrices.Length < count)
            {
                debugVisibleMatrices = new Matrix4x4[count];
            }
        }

        public void Release()
        {
            allMatricesBuffer.Release();
            visibleMatricesBuffer.Release();
            argsBuffer.Release();
            if (material != null && material.hideFlags == HideFlags.HideAndDontSave)
            {
                if (Application.isPlaying)
                {
                    UnityEngine.Object.Destroy(material);
                }
                else
                {
                    UnityEngine.Object.DestroyImmediate(material);
                }
            }
        }
    }
}
