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
    private static readonly int AllMatricesId = Shader.PropertyToID("_AllMatrices");
    private static readonly int VisibleMatricesId = Shader.PropertyToID("_VisibleMatrices");
    private static readonly int StatsBufferId = Shader.PropertyToID("_Stats");
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

    private readonly List<PrototypeRuntime> runtimes = new List<PrototypeRuntime>();
    private readonly uint[] args = new uint[5];
    private readonly uint[] stats = new uint[StatsCount];
    private readonly uint[] statsReset = new uint[StatsCount];
    private ComputeBuffer statsBuffer;
    private MaterialPropertyBlock propertyBlock;
    private Camera mainCamera;
    private int cullKernel = -1;
    private float nextStatsReadbackTime;
    private int lastVisibleInstanceCount;
    private bool lastHizActive;

    private const int StatsCount = 6;

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
        bool useHiZ = cullingMode.UsesHiZ() &&
                      depthTextureGenerator != null &&
                      depthTextureGenerator.DepthTexture != null;
        lastHizActive = useHiZ;
        if (useHiZ && depthTextureGenerator != null)
        {
            depthTextureGenerator.useHiz = true;
        }

        if (useCulling && cullingCompute != null && cullKernel >= 0)
        {
            DispatchCulling(useHiZ);
        }
        else
        {
            DrawAllInstances();
        }

        DrawVisibleInstances(useCulling && cullingCompute != null && cullKernel >= 0);
        ReadbackStatsIfNeeded(useCulling);
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

            PrototypeRuntime runtime = new PrototypeRuntime(prototype, matrices.Count);
            runtime.material = CreateRuntimeMaterial(prototype);
            runtime.allMatricesBuffer.SetData(matrices);
            ResetArgs(runtime, matrices.Count);
            runtimes.Add(runtime);
        }

        if (runtimes.Count > 0)
        {
            statsBuffer = new ComputeBuffer(StatsCount, sizeof(uint));
        }
    }

    public void SetFoliageData(GpuDrivenFoliageData data)
    {
        foliageData = data;
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
    }

    public void SetTerrainColorDebug(bool enabled)
    {
    }

    public void CollectStats(ref GpuDrivenShowcaseStats showcaseStats)
    {
        showcaseStats.foliageInstanceCount += foliageData != null ? foliageData.InstanceCount : 0;
        showcaseStats.foliageVisibleInstanceCount += lastVisibleInstanceCount;
        showcaseStats.hizEnabled |= lastHizActive;
    }

    private void DispatchCulling(bool useHiZ)
    {
        if (statsBuffer != null)
        {
            statsBuffer.SetData(statsReset);
        }

        Matrix4x4 vpMatrix = GL.GetGPUProjectionMatrix(mainCamera.projectionMatrix, false) * mainCamera.worldToCameraMatrix;
        cullingCompute.SetMatrix(VPMatrixId, vpMatrix);
        cullingCompute.SetInt(UseHiZId, useHiZ ? 1 : 0);
        cullingCompute.SetInt(UseReversedZId, SystemInfo.usesReversedZBuffer ? 1 : 0);
        cullingCompute.SetInt(IsOpenGLId, IsOpenGLClipSpace() ? 1 : 0);
        cullingCompute.SetFloat(FrustumPaddingId, frustumPadding);
        cullingCompute.SetFloat(HiZDepthBiasId, hizDepthBias);
        cullingCompute.SetBuffer(cullKernel, StatsBufferId, statsBuffer);

        if (depthTextureGenerator != null)
        {
            cullingCompute.SetInt(DepthTextureSizeId, depthTextureGenerator.DepthTextureSize);
            if (depthTextureGenerator.DepthTexture != null)
            {
                cullingCompute.SetTexture(cullKernel, HiZMapId, depthTextureGenerator.DepthTexture);
            }
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
        ClearStats();
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

    private void ReadbackStatsIfNeeded(bool useCulling)
    {
        if (!useCulling || Time.unscaledTime < nextStatsReadbackTime)
        {
            return;
        }

        nextStatsReadbackTime = Time.unscaledTime + 0.25f;
        lastVisibleInstanceCount = 0;
        for (int i = 0; i < runtimes.Count; i++)
        {
            runtimes[i].argsBuffer.GetData(args);
            lastVisibleInstanceCount += (int)args[1];
        }

        if (statsBuffer != null)
        {
            statsBuffer.GetData(stats);
        }
    }

    private Material CreateRuntimeMaterial(GpuDrivenFoliagePrototype prototype)
    {
        Shader shader = foliageShader != null ? foliageShader : Shader.Find("GPU Driven/Foliage Indirect");
        if (shader == null)
        {
            return prototype.sourceMaterial;
        }

        Material material = new Material(shader)
        {
            hideFlags = HideFlags.HideAndDontSave
        };

        Material source = prototype.sourceMaterial;
        Texture baseMap = null;
        Color baseColor = fallbackBaseColor;
        if (source != null)
        {
            if (source.HasProperty(BaseMapId))
            {
                baseMap = source.GetTexture(BaseMapId);
            }
            else if (source.HasProperty("_MainTex"))
            {
                baseMap = source.GetTexture("_MainTex");
            }

            if (source.HasProperty(BaseColorId))
            {
                baseColor = source.GetColor(BaseColorId);
            }
            else if (source.HasProperty("_Color"))
            {
                baseColor = source.GetColor("_Color");
            }
        }

        if (baseMap != null)
        {
            material.SetTexture(BaseMapId, baseMap);
        }
        material.SetColor(BaseColorId, baseColor);
        material.SetFloat(CutoffId, alphaCutoff);
        return material;
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

    private void ClearStats()
    {
        for (int i = 0; i < stats.Length; i++)
        {
            stats[i] = 0;
        }
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
        statsBuffer?.Release();
        statsBuffer = null;
    }

    private static bool IsOpenGLClipSpace()
    {
        GraphicsDeviceType deviceType = SystemInfo.graphicsDeviceType;
        return deviceType == GraphicsDeviceType.OpenGLCore ||
               deviceType == GraphicsDeviceType.OpenGLES2 ||
               deviceType == GraphicsDeviceType.OpenGLES3;
    }

    private sealed class PrototypeRuntime
    {
        public readonly GpuDrivenFoliagePrototype prototype;
        public readonly int instanceCount;
        public readonly ComputeBuffer allMatricesBuffer;
        public readonly ComputeBuffer visibleMatricesBuffer;
        public readonly ComputeBuffer argsBuffer;
        public Material material;

        public PrototypeRuntime(GpuDrivenFoliagePrototype prototype, int instanceCount)
        {
            this.prototype = prototype;
            this.instanceCount = instanceCount;
            allMatricesBuffer = new ComputeBuffer(instanceCount, sizeof(float) * 16);
            visibleMatricesBuffer = new ComputeBuffer(instanceCount, sizeof(float) * 16, ComputeBufferType.Append);
            argsBuffer = new ComputeBuffer(1, sizeof(uint) * 5, ComputeBufferType.IndirectArguments);
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
                    Object.Destroy(material);
                }
                else
                {
                    Object.DestroyImmediate(material);
                }
            }
        }
    }
}
