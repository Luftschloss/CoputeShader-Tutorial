using System;
using System.Runtime.InteropServices;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;

public class GPUTerrain : MonoBehaviour
{
    private const int HiZStatsCount = 6;
    private const int IndirectArgsCount = 5;
    private const int NodeInfoStride = 40;
    private const int TerrainLeafLookupInfoStride = 32;
    private const int DefaultTerrainLodConfigCount = 5;
    private const float DefaultMaxLodDistance = 1000.0f;
    private const int MaxTerrainLodDebugColorCount = 16;

    // 缓存 shader 属性 ID，供材质、compute 和 property block 绑定复用。
    private static readonly int TerrainDebugColorModeId = Shader.PropertyToID("_TerrainDebugColorMode");
    private static readonly int TerrainMaterialDebugModeId = Shader.PropertyToID("_TerrainMaterialDebugMode");
    private static readonly int TerrainLodDebugColorsId = Shader.PropertyToID("_TerrainLodDebugColors");
    private static readonly int TerrainLodDebugColorCountId = Shader.PropertyToID("_TerrainLodDebugColorCount");
    private static readonly int TerrainHeightmapTextureArrayId = Shader.PropertyToID("_TerrainHeightmapTextureArray");
    private static readonly int TerrainNormalmapTextureArrayId = Shader.PropertyToID("_TerrainNormalmapTextureArray");
    private static readonly int TerrainControlTextureArrayId = Shader.PropertyToID("_TerrainControlTextureArray");
    private static readonly int TerrainLayerDiffuseArrayId = Shader.PropertyToID("_TerrainLayerDiffuseArray");
    private static readonly int TerrainLayerNormalArrayId = Shader.PropertyToID("_TerrainLayerNormalArray");
    private static readonly int TerrainLayerMaskArrayId = Shader.PropertyToID("_TerrainLayerMaskArray");
    private static readonly int TerrainParamsId = Shader.PropertyToID("_TerrainParams");
    private static readonly int TerrainOriginSizesId = Shader.PropertyToID("_TerrainOriginSizes");
    private static readonly int TerrainLayerIndicesId = Shader.PropertyToID("_TerrainLayerIndices");
    private static readonly int TerrainLayerTileSizeOffsetsId = Shader.PropertyToID("_TerrainLayerTileSizeOffsets");
    private static readonly int TerrainLayerPbrParamsId = Shader.PropertyToID("_TerrainLayerPbrParams");
    private static readonly int TerrainCountId = Shader.PropertyToID("_TerrainCount");
    private static readonly int TerrainLayerCountId = Shader.PropertyToID("_TerrainLayerCount");
    private static readonly int TerrainHasLayerDataId = Shader.PropertyToID("_TerrainHasLayerData");
    private static readonly int HizDepthBiasId = Shader.PropertyToID("_HizDepthBias");
    private static readonly int HizCameraPositionWSId = Shader.PropertyToID("_HizCameraPositionWS");
    private static readonly int HizCameraMatrixVPId = Shader.PropertyToID("_HizCameraMatrixVP");
    private static readonly int HizMapId = Shader.PropertyToID("_HizMap");
    private static readonly int HizMapSizeId = Shader.PropertyToID("_HizMapSize");
    private static readonly int CollectStatsId = Shader.PropertyToID("_CollectStats");
    private static readonly int TerrainLeafLookupCountId = Shader.PropertyToID("_TerrainLeafLookupCount");
    private static readonly int TerrainLeafLookupCellCountId = Shader.PropertyToID("_TerrainLeafLookupCellCount");
    private const string ReverseZKeyword = "_REVERSE_Z";

    [Header("Baked Data")]
    // 运行时唯一消费的 Bake 地形数据源，包含节点、纹理和材质数据。
    [SerializeField] private GpuTerrainBakedData bakedData;
    // 归一化 patch 网格，每个选中的地形节点实例化绘制一次。
    [SerializeField] private Mesh instanceMesh;

    [Header("LOD")]
    // 每个 mip 的 CPU LOD 距离阈值和 shader 调试颜色，长度对齐 bakedData.LodCount。
    [SerializeField] private TerrainLodConfig[] lodConfigs = CreateDefaultLodConfigs(DefaultTerrainLodConfigCount);
    // 触发 active node 集合重建的最小相机 XZ 移动距离。
    [SerializeField, Range(0.0f, 5.0f)] private float lodRebuildDistanceThreshold = 0.5f;

    [Header("Hi-Z Terrain Culling")]
    // 预留的地形深度写入 Hi-Z 路径；当前 URP feature 尚未接入。
    [SerializeField] private bool writeTerrainDepthToHiZ;
    // Hi-Z 遮挡测试投影地形 bounds 时使用的深度偏移。
    [SerializeField, Range(0.0f, 100.0f)] private float hizDepthBias = 1.0f;
    // compute 视锥剔除使用的 clip-space 扩张量。
    [SerializeField, Range(0.0f, 0.2f)] private float frustumPadding = 0.03f;

    [Header("Shadow Map")]
    [SerializeField] private bool castShadowMap = true;
    [SerializeField] private bool receiveShadowMap = true;
    [SerializeField] private bool shadowMapUsesAllPatches;

    [Header("Rendering")]
    // 地形 frustum 和 Hi-Z 剔除使用的 compute shader。
    [SerializeField] private ComputeShader cullingComputeShader;
    // 运行时地形材质，消费 baked texture arrays 和 node buffer。
    [SerializeField] private Material mat;

    [Header("Debug")]
    // shader 侧地形材质可视化模式。
    [SerializeField] private TerrainMaterialDebugMode materialDebugMode = TerrainMaterialDebugMode.Lit;
    [SerializeField, Range(1, 30)] private int debugReadbackFrameInterval = 6;

    [Header("Editor")]
    [SerializeField] private bool drawInSceneView;

    // 相机持有的 Hi-Z 纹理提供者，供 terrain 和 foliage culling 共享。
    public DepthTextureGenerator depthTextureGenerator;
    // 开启 GPU stats readback 和 SceneView wire 调试。
    public bool DebugGizmos;

    // 从 baked TerrainTileInfo 和材质元数据派生出的每 terrain shader 上传数组。
    private readonly Vector4[] terrainParams = new Vector4[GpuTerrainBakedData.MaxTerrainCount];
    private readonly Vector4[] terrainOriginSizes = new Vector4[GpuTerrainBakedData.MaxTerrainCount];
    private readonly Vector4[] terrainLayerIndices = new Vector4[GpuTerrainBakedData.MaxTerrainCount];
    private readonly Vector4[] terrainLayerTileSizeOffsets = new Vector4[GpuTerrainBakedData.MaxTerrainLayerCount];
    private readonly Vector4[] terrainLayerPbrParams = new Vector4[GpuTerrainBakedData.MaxTerrainLayerCount];
    // 上传到地形 shader 的每 mip 调试颜色。
    private readonly Vector4[] terrainLodDebugColors = new Vector4[MaxTerrainLodDebugColorCount];
    // DrawMeshInstancedIndirect 参数的 CPU 侧暂存副本。
    private readonly uint[] args = new uint[IndirectArgsCount] { 0, 0, 0, 0, 0 };
    // compute Hi-Z / frustum 调试计数器的 CPU 侧暂存副本。
    private readonly uint[] hizStats = new uint[HiZStatsCount];

    // baked texture array 引用，直接绑定到全局和材质地形 shader 属性。
    private Texture2DArray heightMapArray;
    private Texture2DArray normalMapArray;
    private Texture2DArray controlMapArray;
    private Texture2DArray layerDiffuseArray;
    private Texture2DArray layerNormalArray;
    private Texture2DArray layerMaskArray;
    // 覆盖所有 baked terrain tile 的保守 indirect draw bounds。
    private Bounds nodeBounds = new Bounds(Vector3.zero, new Vector3(1000.0f, 1000.0f, 1000.0f));
    // active GpuTerrainNodeInfo 结构化 buffer，供 culling 和 vertex shader 使用。
    private ComputeBuffer allInstancePosBuffer;
    // compute culling 写入可见 active-node ID 的 append buffer。
    private ComputeBuffer visibleInstancePosIDBuffer;
    // 顺序 active-node ID buffer，用于 no-culling、shadow 和 depth-only 路径。
    private ComputeBuffer allInstancePosIDBuffer;
    // DrawMeshInstancedIndirect 使用的 indirect args buffer。
    private ComputeBuffer argsBuffer;
    // 地形 Hi-Z depth injection 路径使用的 indirect args buffer。
    private ComputeBuffer depthArgsBuffer;
    // 可选 Hi-Z / frustum 调试统计使用的 GPU counter buffer。
    private ComputeBuffer hizStatsBuffer;
    // SceneView 可见节点 wire 调试使用的可选 append/readback buffer。
    private ComputeBuffer visibleNodeInfoBuffer;
    private ComputeBuffer terrainLeafLookupInfoBuffer;
    private ComputeBuffer terrainLeafMipLookupBuffer;
    // active node 数据的持久上传内存，避免每帧分配。
    private NativeArray<GpuTerrainNodeInfo> activeNodeInfoUpload;
    // 顺序 active node ID 的持久上传内存。
    private NativeArray<uint> allNodeIdsUpload;
    // indirect args 更新使用的持久上传内存。
    private NativeArray<uint> argsUpload;
    // dispatch 前清空 Hi-Z stats 使用的持久上传内存。
    private NativeArray<uint> hizStatsResetUpload;
    private NativeArray<GpuTerrainLeafLookupInfo> terrainLeafLookupInfoUpload;
    // 可见节点调试绘制使用的 CPU readback 缓存。
    private GpuTerrainNodeInfo[] visibleNodeInfoArray;
    // 当前和上一帧 active node 在 bakedData.Nodes 中的索引，用于判断 LOD 集合是否变化。
    private int[] activeNodeIndices = Array.Empty<int>();
    private int[] previousActiveNodeIndices = Array.Empty<int>();
    // CPU traversal 和 GPU dispatch 输入共享的 active node 数量。
    private int activeNodeCount;
    private int previousActiveNodeCount;
    // 上一次从 GPU 读回的 debug 可见节点数量。
    private int debugVisibleNodeCount;
    // TerrainCulling.compute 的 kernel index 缓存。
    private int cullTerrainKernel = -1;
    private int clearTerrainLeafLookupKernel = -1;
    private int buildTerrainLeafLookupKernel = -1;
    private int buildTerrainNeighborsKernel = -1;
    // 运行时相机，用于 LOD 选择、视锥矩阵、绘制目标和 Hi-Z 查询。
    private new Camera camera;
    // Showcase 选择的 GPU culling 模式，影响 compute dispatch 和材质绑定。
    private GpuDrivenShowcaseCullingMode showcaseCullingMode = GpuDrivenShowcaseCullingMode.FrustumAndHiZ;
    // Showcase 调试覆盖开关，用于把地形材质输出切到 LOD color。
    private bool showcaseDebugColorMode;
    private TerrainMaterialDebugMode materialDebugModeBeforeShowcaseColor = TerrainMaterialDebugMode.Lit;
    // 上一帧实际 Hi-Z 可用状态，用于运行时 UI / status。
    private bool lastHizActive;
    // 强制 CPU LOD traversal，忽略相机移动阈值。
    private bool forceLodRebuild = true;
    // baked data 变化或 OnValidate 后触发 buffer 和 texture reference 重建。
    private bool terrainResourcesDirty;
    // 资源变化后触发 compute / material buffer 和静态 shader 状态重新绑定。
    private bool terrainGpuBindingsDirty = true;
    private bool terrainRenderPropertiesDirty = true;
    private bool materialStateDirty = true;
    // active set 或容量变化后重新上传顺序 active node ID。
    private bool nodeIdBufferDirty = true;
    private bool terrainLeafLookupInfoDirty = true;
    private int terrainLeafLookupCellCount;
    // 上一次 indirect draw 的可见 patch 数量，用于 status / debug UI。
    private uint lastVisiblePatchCount;
    // 上一次 active LOD rebuild 时的相机 XZ 位置。
    private Vector2 lastLodBuildCameraXZ;
    // 地形 Hi-Z depth draw 复用的 property block。
    private MaterialPropertyBlock hizDepthProperties;
    // 地形 shadow-only draw 复用的 property block。
    private MaterialPropertyBlock shadowProperties;
    private ComputeBuffer appliedVisibleIdBuffer;
    private bool appliedLodDebugColorMode;
    private int appliedMaterialDebugMode = -1;
    private bool appliedReceiveShadowMap;
    private bool hasAppliedReceiveShadowMap;
    private int nextDebugReadbackFrame;

    public void SetBakedData(GpuTerrainBakedData data)
    {
        bakedData = data;
        EnsureLodConfigDefaults();
        terrainResourcesDirty = true;
        forceLodRebuild = true;
        terrainGpuBindingsDirty = true;
        terrainRenderPropertiesDirty = true;
        materialStateDirty = true;
    }

    private void OnEnable()
    {
        EnsureLodConfigDefaults();
        camera = Camera.main;
        RebuildTerrainResources();
        ApplyMaterialState(showcaseCullingMode != GpuDrivenShowcaseCullingMode.None);
        BindTerrainRenderPropertiesIfDirty();
    }

    private void OnValidate()
    {
        EnsureLodConfigDefaults();
        terrainResourcesDirty = true;
        forceLodRebuild = true;
        terrainGpuBindingsDirty = true;
        terrainRenderPropertiesDirty = true;
        materialStateDirty = true;
    }

    private void LateUpdate()
    {
        if (camera == null)
        {
            camera = Camera.main;
        }

        if (camera == null)
        {
            return;
        }

        if (terrainResourcesDirty)
        {
            RebuildTerrainResources();
        }

        if (!HasRenderResources())
        {
            return;
        }

        BindTerrainRenderPropertiesIfDirty();
        if (ShouldRebuildVisibleTerrainNodes())
        {
            Profiler.BeginSample("Gpu Terrain LOD");
            RebuildVisibleTerrainNodes();
            Profiler.EndSample();
        }

        if (activeNodeCount == 0 || allInstancePosBuffer == null || visibleInstancePosIDBuffer == null || argsBuffer == null)
        {
            return;
        }

        bool useCulling = showcaseCullingMode != GpuDrivenShowcaseCullingMode.None;
        bool wantsHiZ = WantsHiZ();
        bool useHiz = BindHiZTextureIfReady(out Vector4 hizMapSize, out Matrix4x4 hizMatrixVP, out Vector3 hizCameraPositionWS);
        bool collectDebugStats = DebugGizmos;
        lastHizActive = useHiz;
        if (!useHiz || !collectDebugStats)
        {
            ClearHiZStats();
        }

        UpdateHiZGeneratorState(wantsHiZ);

        Matrix4x4 vp = GL.GetGPUProjectionMatrix(camera.projectionMatrix, false) * camera.worldToCameraMatrix;
        visibleInstancePosIDBuffer.SetCounterValue(0);
        if (collectDebugStats && visibleNodeInfoBuffer != null)
        {
            visibleNodeInfoBuffer.SetCounterValue(0);
        }

        cullingComputeShader.SetMatrix("_VPMatrix", vp);
        cullingComputeShader.SetMatrix(HizCameraMatrixVPId, hizMatrixVP);
        cullingComputeShader.SetVector(HizCameraPositionWSId, hizCameraPositionWS);
        cullingComputeShader.SetInt("_InstanceCount", activeNodeCount);
        if (useHiz)
        {
            cullingComputeShader.SetVector(HizMapSizeId, hizMapSize);
        }
        cullingComputeShader.SetBool("_UseHiZ", useHiz);
        cullingComputeShader.SetBool("_WriteDebugResult", collectDebugStats);
        cullingComputeShader.SetBool(CollectStatsId, collectDebugStats);
        ApplyMaterialState(useCulling);

        if (useCulling)
        {
            if (collectDebugStats && hizStatsBuffer != null)
            {
                EnsureHiZStatsResetCapacity();
                hizStatsBuffer.SetData(hizStatsResetUpload);
            }

            cullingComputeShader.Dispatch(cullTerrainKernel, Mathf.CeilToInt(activeNodeCount / 64.0f), 1, 1);
            ComputeBuffer.CopyCount(visibleInstancePosIDBuffer, argsBuffer, 4);
        }
        else
        {
            args[1] = (uint)activeNodeCount;
            UploadArgsBuffer(argsBuffer);
            lastVisiblePatchCount = args[1];
            ClearHiZStats();
        }

        ReadBackDebugStats(useCulling);
#if UNITY_EDITOR
        Camera drawCamera = drawInSceneView ? null : camera;
#else
        Camera drawCamera = camera;
#endif
        Graphics.DrawMeshInstancedIndirect(instanceMesh, 0, mat, nodeBounds, argsBuffer, 0, null,
            ShadowCastingMode.Off, receiveShadowMap, gameObject.layer, drawCamera);
        DrawShadowMap(drawCamera);
    }

    private void OnDisable()
    {
        ReleaseBuffers();
        ReleaseUploadArrays();
        ReleaseTerrainTextureArrays();
    }

    private void RebuildTerrainResources()
    {
        ReleaseBuffers();
        ReleaseTerrainTextureArrays();
        terrainGpuBindingsDirty = true;
        terrainRenderPropertiesDirty = true;
        materialStateDirty = true;
        nodeIdBufferDirty = true;
        activeNodeCount = 0;
        debugVisibleNodeCount = 0;
        lastVisiblePatchCount = 0;

        if (bakedData == null || !bakedData.IsValid)
        {
            terrainResourcesDirty = false;
            return;
        }

        UpdateNodeBounds();
        BindBakedTextureArrays();
        UpdateTerrainShaderArrays();
        terrainRenderPropertiesDirty = true;
        EnsureTerrainLeafLookupInfo();
        EnsureActiveNodeInfoCapacity(bakedData.NodeCount);
        EnsureActiveNodeIndexCapacity(bakedData.NodeCount);
        EnsureNodeIdCapacity(bakedData.NodeCount);
        if (camera != null)
        {
            RebuildVisibleTerrainNodes();
        }

        terrainResourcesDirty = false;
    }

    private bool HasRenderResources()
    {
        return bakedData != null && bakedData.IsValid &&
               heightMapArray != null && normalMapArray != null &&
               instanceMesh != null && mat != null && cullingComputeShader != null;
    }

    private void BindBakedTextureArrays()
    {
        heightMapArray = bakedData.HeightMapArray;
        normalMapArray = bakedData.NormalMapArray;
        controlMapArray = bakedData.ControlMapArray;
        layerDiffuseArray = bakedData.LayerDiffuseArray;
        layerNormalArray = bakedData.LayerNormalArray;
        layerMaskArray = bakedData.LayerMaskArray;
    }

    private void UpdateNodeBounds()
    {
        bool hasBounds = false;
        Bounds combinedBounds = default;
        GpuTerrainBakedData.TerrainTileInfo[] terrains = bakedData.Terrains;
        for (int i = 0; i < terrains.Length; i++)
        {
            Vector3 origin = terrains[i].worldOrigin;
            Vector3 size = terrains[i].size;
            Bounds terrainBounds = new Bounds(origin + new Vector3(size.x * 0.5f, size.y * 0.5f, size.z * 0.5f), size);
            if (!hasBounds)
            {
                combinedBounds = terrainBounds;
                hasBounds = true;
            }
            else
            {
                combinedBounds.Encapsulate(terrainBounds);
            }
        }

        nodeBounds = hasBounds ? combinedBounds : new Bounds(Vector3.zero, new Vector3(1000.0f, 1000.0f, 1000.0f));
    }

    private void UpdateTerrainShaderArrays()
    {
        for (int i = 0; i < terrainParams.Length; i++)
        {
            terrainParams[i] = Vector4.zero;
            terrainOriginSizes[i] = Vector4.zero;
            terrainLayerIndices[i] = Vector4.zero;
        }

        for (int i = 0; i < terrainLayerTileSizeOffsets.Length; i++)
        {
            terrainLayerTileSizeOffsets[i] = new Vector4(1.0f, 1.0f, 0.0f, 0.0f);
            terrainLayerPbrParams[i] = new Vector4(1.0f, 0.0f, 0.5f, 0.0f);
        }

        UpdateTerrainLodDebugColorArray();

        if (bakedData == null)
        {
            return;
        }

        GpuTerrainBakedData.TerrainTileInfo[] terrains = bakedData.Terrains;
        for (int i = 0; i < terrains.Length && i < GpuTerrainBakedData.MaxTerrainCount; i++)
        {
            terrainParams[i] = terrains[i].TerrainParams;
            terrainOriginSizes[i] = terrains[i].OriginSize;
        }

        Vector4[] bakedTerrainLayerIndices = bakedData.TerrainLayerIndices;
        for (int i = 0; i < bakedTerrainLayerIndices.Length && i < terrainLayerIndices.Length; i++)
        {
            terrainLayerIndices[i] = bakedTerrainLayerIndices[i];
        }

        Vector4[] bakedLayerTileSizeOffsets = bakedData.LayerTileSizeOffsets;
        for (int i = 0; i < bakedLayerTileSizeOffsets.Length && i < terrainLayerTileSizeOffsets.Length; i++)
        {
            terrainLayerTileSizeOffsets[i] = bakedLayerTileSizeOffsets[i];
        }

        Vector4[] bakedLayerPbrParams = bakedData.LayerPbrParams;
        for (int i = 0; i < bakedLayerPbrParams.Length && i < terrainLayerPbrParams.Length; i++)
        {
            terrainLayerPbrParams[i] = bakedLayerPbrParams[i];
        }
    }

    private void UpdateTerrainLodDebugColorArray()
    {
        int colorCount = GetTerrainLodDebugColorCount();
        for (int i = 0; i < terrainLodDebugColors.Length; i++)
        {
            Color color = GetLodDebugColor(i, colorCount);
            terrainLodDebugColors[i] = new Vector4(color.r, color.g, color.b, color.a);
        }
    }

    private int GetTerrainLodDebugColorCount()
    {
        if (lodConfigs != null && lodConfigs.Length > 0)
        {
            return Mathf.Clamp(lodConfigs.Length, 1, MaxTerrainLodDebugColorCount);
        }

        int lodCount = bakedData != null && bakedData.IsValid ? bakedData.LodCount : 0;
        return Mathf.Clamp(lodCount, 1, MaxTerrainLodDebugColorCount);
    }

    private Color GetLodDebugColor(int index, int colorCount)
    {
        if (lodConfigs != null && lodConfigs.Length > 0)
        {
            int sourceCount = Mathf.Min(lodConfigs.Length, MaxTerrainLodDebugColorCount);
            TerrainLodConfig config = lodConfigs[Mathf.Clamp(index, 0, sourceCount - 1)];
            return config != null ? config.debugColor : GetDefaultLodDebugColor(index, colorCount);
        }

        float t = colorCount <= 1 ? 0.0f : Mathf.Clamp01((float)index / (colorCount - 1));
        return Color.Lerp(Color.red, Color.blue, t);
    }

    private float GetLodDistance(int mip)
    {
        if (lodConfigs == null || mip < 0 || mip >= lodConfigs.Length)
        {
            return 0.0f;
        }

        TerrainLodConfig config = lodConfigs[mip];
        return config != null ? Mathf.Max(0.0f, config.distance) : 0.0f;
    }

    /// <summary>
    /// 初始化LOD（distance，debugColor）配置
    /// </summary>
    private void EnsureLodConfigDefaults()
    {
        int lodCount = GetExpectedLodConfigCount();
        if (lodConfigs == null || lodConfigs.Length != lodCount)
        {
            TerrainLodConfig[] defaultConfigs = CreateDefaultLodConfigs(lodCount);
            if (lodConfigs != null)
            {
                int copyCount = Mathf.Min(lodConfigs.Length, defaultConfigs.Length);
                for (int i = 0; i < copyCount; i++)
                {
                    if (lodConfigs[i] != null)
                    {
                        defaultConfigs[i].distance = Mathf.Max(0.0f, lodConfigs[i].distance);
                        defaultConfigs[i].debugColor = lodConfigs[i].debugColor;
                    }
                }
            }

            lodConfigs = defaultConfigs;
        }

        for (int i = 0; i < lodConfigs.Length; i++)
        {
            if (lodConfigs[i] == null)
            {
                lodConfigs[i] = new TerrainLodConfig(0.0f, GetDefaultLodDebugColor(i, lodConfigs.Length));
            }

            lodConfigs[i].distance = Mathf.Max(0.0f, lodConfigs[i].distance);
        }
    }

    private int GetExpectedLodConfigCount()
    {
        if (bakedData != null)
        {
            return Mathf.Max(1, bakedData.LodCount);
        }

        if (lodConfigs != null && lodConfigs.Length > 0)
        {
            return lodConfigs.Length;
        }

        return DefaultTerrainLodConfigCount;
    }

    private static TerrainLodConfig[] CreateDefaultLodConfigs(int lodCount)
    {
        lodCount = Mathf.Max(1, lodCount);
        TerrainLodConfig[] configs = new TerrainLodConfig[lodCount];
        for (int i = 0; i < configs.Length; i++)
        {
            configs[i] = new TerrainLodConfig(GetDefaultLodDistance(i, lodCount), GetDefaultLodDebugColor(i, lodCount));
        }

        return configs;
    }

    private static float GetDefaultLodDistance(int index, int count)
    {
        if (count <= 1)
        {
            return DefaultMaxLodDistance;
        }

        float t = Mathf.Clamp01((float)index / (count - 1));
        return t * DefaultMaxLodDistance;
    }

    private static Color GetDefaultLodDebugColor(int index, int count)
    {
        float t = count <= 1 ? 0.0f : Mathf.Clamp01((float)index / (count - 1));
        return Color.Lerp(Color.red, Color.blue, t);
    }

    private bool ShouldRebuildVisibleTerrainNodes()
    {
        if (forceLodRebuild || allInstancePosBuffer == null)
        {
            return true;
        }

        Vector2 cameraXZ = new Vector2(camera.transform.position.x, camera.transform.position.z);
        float threshold = GetEffectiveLodRebuildDistanceThreshold();
        float sqrThreshold = threshold * threshold;
        return (cameraXZ - lastLodBuildCameraXZ).sqrMagnitude >= sqrThreshold;
    }

    private float GetEffectiveLodRebuildDistanceThreshold()
    {
        float threshold = Mathf.Max(0.01f, lodRebuildDistanceThreshold);
        if (bakedData != null && bakedData.IsValid)
        {
            threshold = Mathf.Max(threshold, GetLeafPatchSize() * 0.5f);
        }

        return threshold;
    }

    private float GetLeafPatchSize()
    {
        if (bakedData == null || !bakedData.IsValid)
        {
            return Mathf.Max(1.0f, lodRebuildDistanceThreshold);
        }

        int subdivisionCount = Mathf.Max(0, bakedData.LodCount - 1);
        int subdivisionScale = 1 << Mathf.Min(subdivisionCount, 20);
        return Mathf.Max(0.001f, bakedData.PatchSize / subdivisionScale);
    }

    Vector2 cameraXZ;

    private void RebuildVisibleTerrainNodes()
    {
        activeNodeCount = 0;

        cameraXZ.x = camera.transform.position.x;
        cameraXZ.y = camera.transform.position.z;
        int[] roots = bakedData.RootNodeIndices;
        GpuTerrainBakedData.BakedNode[] nodes = bakedData.Nodes;
        Profiler.BeginSample("Gpu Terrain LOD Traverse");
        for (int i = 0; i < roots.Length; i++)
        {
            CollectActiveNodes(roots[i], cameraXZ, nodes);
        }
        Profiler.EndSample();

        bool canSkipUnchangedActiveSet = !forceLodRebuild && allInstancePosBuffer != null;
        if (canSkipUnchangedActiveSet && !DidActiveNodesChange())
        {
            lastLodBuildCameraXZ = cameraXZ;
            forceLodRebuild = false;
            return;
        }

        if (!canSkipUnchangedActiveSet)
        {
            StorePreviousActiveNodes();
        }

        Profiler.BeginSample("Gpu Terrain LOD Build Info");
        BuildActiveNodeInfo(nodes);
        Profiler.EndSample();

        Profiler.BeginSample("Gpu Terrain LOD Upload");
        UpdateComputeBuffer();
        Profiler.EndSample();
        lastLodBuildCameraXZ = cameraXZ;
        forceLodRebuild = false;
    }

    private void CollectActiveNodes(int nodeIndex, Vector2 cameraXZ, GpuTerrainBakedData.BakedNode[] nodes)
    {
        if (nodeIndex < 0 || nodeIndex >= nodes.Length)
        {
            return;
        }

        GpuTerrainBakedData.BakedNode node = nodes[nodeIndex];
        float lodDistanceValue = GetLodDistance(node.mip);
        float centerX = node.rect.x + node.rect.z * 0.5f;
        float centerZ = node.rect.y + node.rect.w * 0.5f;
        float dx = cameraXZ.x - centerX;
        float dz = cameraXZ.y - centerZ;
        bool emitNode = node.mip <= 0 || dx * dx + dz * dz >= lodDistanceValue * lodDistanceValue || node.childCount <= 0;
        if (emitNode)
        {
            EnsureActiveNodeIndexCapacity(activeNodeCount + 1);
            activeNodeIndices[activeNodeCount] = nodeIndex;
            activeNodeCount++;
            return;
        }

        for (int i = 0; i < node.childCount; i++)
        {
            CollectActiveNodes(node.GetChildIndex(i), cameraXZ, nodes);
        }
    }

    private void BuildActiveNodeInfo(GpuTerrainBakedData.BakedNode[] nodes)
    {
        EnsureActiveNodeInfoCapacity(activeNodeCount);
        for (int i = 0; i < activeNodeCount; i++)
        {
            int nodeIndex = activeNodeIndices[i];
            if (nodeIndex < 0 || nodeIndex >= nodes.Length)
            {
                activeNodeInfoUpload[i] = default;
                continue;
            }

            GpuTerrainBakedData.BakedNode node = nodes[nodeIndex];
            activeNodeInfoUpload[i] = new GpuTerrainNodeInfo(node.rect, node.heightMinMax, node.mip, node.terrainIndex);
        }
    }

    private void UpdateComputeBuffer()
    {
        if (activeNodeCount == 0 || instanceMesh == null || cullingComputeShader == null || mat == null ||
            heightMapArray == null || normalMapArray == null)
        {
            return;
        }

        int requiredCapacity = Mathf.Max(bakedData != null ? bakedData.NodeCount : activeNodeCount, activeNodeCount, 1);
        bool recreatedBuffers = false;
        recreatedBuffers |= RecreateBuffer(ref allInstancePosBuffer, requiredCapacity, NodeInfoStride, ComputeBufferType.Default);
        recreatedBuffers |= RecreateBuffer(ref visibleInstancePosIDBuffer, requiredCapacity, sizeof(uint), ComputeBufferType.Append);
        recreatedBuffers |= RecreateBuffer(ref allInstancePosIDBuffer, requiredCapacity, sizeof(uint), ComputeBufferType.Default);
        recreatedBuffers |= RecreateBuffer(ref visibleNodeInfoBuffer, requiredCapacity, NodeInfoStride, ComputeBufferType.Append);
        recreatedBuffers |= EnsureTerrainLeafLookupBuffers();

        EnsureNodeIdCapacity(requiredCapacity);
        if (nodeIdBufferDirty || recreatedBuffers)
        {
            allInstancePosIDBuffer.SetData(allNodeIdsUpload, 0, 0, requiredCapacity);
            nodeIdBufferDirty = false;
        }

        if (argsBuffer == null)
        {
            argsBuffer = new ComputeBuffer(1, IndirectArgsCount * sizeof(uint), ComputeBufferType.IndirectArguments);
            recreatedBuffers = true;
        }

        if (depthArgsBuffer == null)
        {
            depthArgsBuffer = new ComputeBuffer(1, IndirectArgsCount * sizeof(uint), ComputeBufferType.IndirectArguments);
            recreatedBuffers = true;
        }

        if (hizStatsBuffer == null || hizStatsBuffer.count != HiZStatsCount)
        {
            hizStatsBuffer?.Release();
            hizStatsBuffer = new ComputeBuffer(HiZStatsCount, sizeof(uint));
            recreatedBuffers = true;
        }

        allInstancePosBuffer.SetData(activeNodeInfoUpload, 0, 0, activeNodeCount);
        if (terrainLeafLookupInfoDirty && terrainLeafLookupInfoBuffer != null && terrainLeafLookupInfoUpload.IsCreated)
        {
            terrainLeafLookupInfoBuffer.SetData(terrainLeafLookupInfoUpload);
            terrainLeafLookupInfoDirty = false;
        }

        args[0] = instanceMesh.GetIndexCount(0);
        args[1] = (uint)activeNodeCount;
        args[2] = instanceMesh.GetIndexStart(0);
        args[3] = instanceMesh.GetBaseVertex(0);
        args[4] = 0;
        UploadArgsBuffer(argsBuffer);
        UploadArgsBuffer(depthArgsBuffer);

        if (recreatedBuffers)
        {
            terrainGpuBindingsDirty = true;
        }

        BindTerrainGpuResources();
        DispatchTerrainNeighborUpdate();
    }

    private void BindTerrainGpuResources()
    {
        if (!terrainGpuBindingsDirty)
        {
            return;
        }

        if (cullTerrainKernel < 0)
        {
            cullTerrainKernel = cullingComputeShader.FindKernel("CullTerrain");
        }

        if (clearTerrainLeafLookupKernel < 0)
        {
            clearTerrainLeafLookupKernel = cullingComputeShader.FindKernel("ClearTerrainLeafLookup");
        }

        if (buildTerrainLeafLookupKernel < 0)
        {
            buildTerrainLeafLookupKernel = cullingComputeShader.FindKernel("BuildTerrainLeafLookup");
        }

        if (buildTerrainNeighborsKernel < 0)
        {
            buildTerrainNeighborsKernel = cullingComputeShader.FindKernel("BuildTerrainNeighbors");
        }

        cullingComputeShader.SetBuffer(cullTerrainKernel, "_AllInstancesPosWSBuffer", allInstancePosBuffer);
        cullingComputeShader.SetBuffer(cullTerrainKernel, "_VisibleInstancesOnlyPosWSIDBuffer", visibleInstancePosIDBuffer);
        cullingComputeShader.SetBuffer(cullTerrainKernel, "result", visibleNodeInfoBuffer);
        cullingComputeShader.SetBuffer(cullTerrainKernel, "_HiZStatsBuffer", hizStatsBuffer);
        BindTerrainLodComputeBuffers(clearTerrainLeafLookupKernel);
        BindTerrainLodComputeBuffers(buildTerrainLeafLookupKernel);
        BindTerrainLodComputeBuffers(buildTerrainNeighborsKernel);
        cullingComputeShader.SetFloat("_FrustumPadding", frustumPadding);
        cullingComputeShader.SetFloat(HizDepthBiasId, hizDepthBias);
        ConfigureCullingShaderKeywords();
        cullingComputeShader.SetBool("isOpenGL", IsOpenGLClipSpace());
        cullingComputeShader.SetInt("_InstanceCount", activeNodeCount);
        cullingComputeShader.SetInt(TerrainLeafLookupCountId, terrainLeafLookupInfoUpload.IsCreated ? terrainLeafLookupInfoUpload.Length : 0);
        cullingComputeShader.SetInt(TerrainLeafLookupCellCountId, terrainLeafLookupCellCount);
        cullingComputeShader.SetBool("_UseHiZ", IsHiZReady());
        cullingComputeShader.SetBool("_WriteDebugResult", DebugGizmos);
        cullingComputeShader.SetBool(CollectStatsId, DebugGizmos);
        mat.SetBuffer("_AllInstancesTransformBuffer", allInstancePosBuffer);
        ApplyMaterialState(showcaseCullingMode != GpuDrivenShowcaseCullingMode.None);
        terrainGpuBindingsDirty = false;
    }

    private void BindTerrainLodComputeBuffers(int kernel)
    {
        if (kernel < 0)
        {
            return;
        }

        cullingComputeShader.SetBuffer(kernel, "_AllInstancesPosWSBuffer", allInstancePosBuffer);
        cullingComputeShader.SetBuffer(kernel, "_TerrainLeafLookupInfoBuffer", terrainLeafLookupInfoBuffer);
        cullingComputeShader.SetBuffer(kernel, "_TerrainLeafMipLookupBuffer", terrainLeafMipLookupBuffer);
    }

    private bool EnsureTerrainLeafLookupBuffers()
    {
        bool recreated = false;
        int lookupCount = terrainLeafLookupInfoUpload.IsCreated ? Mathf.Max(1, terrainLeafLookupInfoUpload.Length) : 1;
        int cellCount = Mathf.Max(1, terrainLeafLookupCellCount);

        recreated |= RecreateBuffer(
            ref terrainLeafLookupInfoBuffer,
            lookupCount,
            TerrainLeafLookupInfoStride,
            ComputeBufferType.Default);
        recreated |= RecreateBuffer(
            ref terrainLeafMipLookupBuffer,
            cellCount,
            sizeof(int),
            ComputeBufferType.Default);

        if (recreated)
        {
            terrainLeafLookupInfoDirty = true;
        }

        return recreated;
    }

    private void DispatchTerrainNeighborUpdate()
    {
        if (activeNodeCount <= 0 ||
            clearTerrainLeafLookupKernel < 0 ||
            buildTerrainLeafLookupKernel < 0 ||
            buildTerrainNeighborsKernel < 0 ||
            terrainLeafLookupInfoBuffer == null ||
            terrainLeafMipLookupBuffer == null)
        {
            return;
        }

        cullingComputeShader.SetInt("_InstanceCount", activeNodeCount);
        cullingComputeShader.SetInt(TerrainLeafLookupCountId, terrainLeafLookupInfoUpload.IsCreated ? terrainLeafLookupInfoUpload.Length : 0);
        cullingComputeShader.SetInt(TerrainLeafLookupCellCountId, terrainLeafLookupCellCount);

        Profiler.BeginSample("Gpu Terrain LOD GPU Lookup");
        cullingComputeShader.Dispatch(clearTerrainLeafLookupKernel, Mathf.CeilToInt(Mathf.Max(1, terrainLeafLookupCellCount) / 64.0f), 1, 1);
        cullingComputeShader.Dispatch(buildTerrainLeafLookupKernel, Mathf.CeilToInt(activeNodeCount / 64.0f), 1, 1);
        Profiler.EndSample();

        Profiler.BeginSample("Gpu Terrain LOD GPU Neighbors");
        cullingComputeShader.Dispatch(buildTerrainNeighborsKernel, Mathf.CeilToInt(activeNodeCount / 64.0f), 1, 1);
        Profiler.EndSample();
    }

    private void ConfigureCullingShaderKeywords()
    {
        if (cullingComputeShader == null)
        {
            return;
        }

        if (SystemInfo.usesReversedZBuffer)
        {
            cullingComputeShader.EnableKeyword(ReverseZKeyword);
        }
        else
        {
            cullingComputeShader.DisableKeyword(ReverseZKeyword);
        }
    }

    private static bool RecreateBuffer(ref ComputeBuffer buffer, int count, int stride, ComputeBufferType type)
    {
        if (buffer != null && buffer.count == count && buffer.stride == stride)
        {
            return false;
        }

        buffer?.Release();
        buffer = new ComputeBuffer(count, stride, type);
        return true;
    }

    private void EnsureActiveNodeInfoCapacity(int requiredCapacity)
    {
        if (requiredCapacity <= activeNodeInfoUpload.Length)
        {
            return;
        }

        int newCapacity = Mathf.NextPowerOfTwo(Mathf.Max(requiredCapacity, 64));
        ResizeNativeArray(ref activeNodeInfoUpload, newCapacity, true);
    }

    private void EnsureActiveNodeIndexCapacity(int requiredCapacity)
    {
        if (requiredCapacity <= activeNodeIndices.Length)
        {
            return;
        }

        int newCapacity = Mathf.NextPowerOfTwo(Mathf.Max(requiredCapacity, 64));
        Array.Resize(ref activeNodeIndices, newCapacity);
    }

    private void EnsurePreviousActiveNodeIndexCapacity(int requiredCapacity)
    {
        if (requiredCapacity <= previousActiveNodeIndices.Length)
        {
            return;
        }

        int newCapacity = Mathf.NextPowerOfTwo(Mathf.Max(requiredCapacity, 64));
        Array.Resize(ref previousActiveNodeIndices, newCapacity);
    }

    private bool DidActiveNodesChange()
    {
        bool changed = activeNodeCount != previousActiveNodeCount;
        if (!changed)
        {
            for (int i = 0; i < activeNodeCount; i++)
            {
                if (activeNodeIndices[i] != previousActiveNodeIndices[i])
                {
                    changed = true;
                    break;
                }
            }
        }

        if (!changed)
        {
            return false;
        }

        StorePreviousActiveNodes();
        return true;
    }

    private void StorePreviousActiveNodes()
    {
        EnsurePreviousActiveNodeIndexCapacity(activeNodeCount);
        for (int i = 0; i < activeNodeCount; i++)
        {
            previousActiveNodeIndices[i] = activeNodeIndices[i];
        }

        previousActiveNodeCount = activeNodeCount;
    }

    private void EnsureNodeIdCapacity(int requiredCapacity)
    {
        if (requiredCapacity <= allNodeIdsUpload.Length)
        {
            return;
        }

        int newCapacity = Mathf.NextPowerOfTwo(Mathf.Max(requiredCapacity, 64));
        int oldLength = allNodeIdsUpload.Length;
        ResizeNativeArray(ref allNodeIdsUpload, newCapacity, true);
        for (int i = oldLength; i < allNodeIdsUpload.Length; i++)
        {
            allNodeIdsUpload[i] = (uint)i;
        }

        nodeIdBufferDirty = true;
    }

    private void EnsureTerrainLeafLookupInfo()
    {
        terrainLeafLookupCellCount = 0;

        if (bakedData == null || !bakedData.IsValid)
        {
            if (terrainLeafLookupInfoUpload.IsCreated)
            {
                terrainLeafLookupInfoUpload.Dispose();
            }

            terrainLeafLookupInfoDirty = true;
            return;
        }

        GpuTerrainBakedData.TerrainTileInfo[] terrains = bakedData.Terrains;
        if (!terrainLeafLookupInfoUpload.IsCreated || terrainLeafLookupInfoUpload.Length != terrains.Length)
        {
            if (terrainLeafLookupInfoUpload.IsCreated)
            {
                terrainLeafLookupInfoUpload.Dispose();
            }

            terrainLeafLookupInfoUpload = new NativeArray<GpuTerrainLeafLookupInfo>(
                terrains.Length,
                Allocator.Persistent,
                NativeArrayOptions.UninitializedMemory);
        }

        float leafSize = GetLeafPatchSize();
        int cellOffset = 0;
        for (int i = 0; i < terrains.Length; i++)
        {
            Vector3 origin = terrains[i].worldOrigin;
            Vector3 size = terrains[i].size;
            int width = Mathf.Max(1, Mathf.CeilToInt(size.x / leafSize));
            int depth = Mathf.Max(1, Mathf.CeilToInt(size.z / leafSize));

            terrainLeafLookupInfoUpload[i] = new GpuTerrainLeafLookupInfo(
                new Vector4(origin.x, origin.z, leafSize, origin.x + size.x),
                new Vector4(width, depth, cellOffset, origin.z + size.z));

            cellOffset += width * depth;
        }

        terrainLeafLookupCellCount = Mathf.Max(1, cellOffset);
        terrainLeafLookupInfoDirty = true;
    }

    private void EnsureArgsUploadCapacity()
    {
        if (argsUpload.IsCreated)
        {
            return;
        }

        argsUpload = new NativeArray<uint>(IndirectArgsCount, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
    }

    private void EnsureHiZStatsResetCapacity()
    {
        if (hizStatsResetUpload.IsCreated)
        {
            return;
        }

        hizStatsResetUpload = new NativeArray<uint>(HiZStatsCount, Allocator.Persistent, NativeArrayOptions.ClearMemory);
    }

    private void UploadArgsBuffer(ComputeBuffer targetBuffer)
    {
        if (targetBuffer == null)
        {
            return;
        }

        EnsureArgsUploadCapacity();
        for (int i = 0; i < IndirectArgsCount; i++)
        {
            argsUpload[i] = args[i];
        }

        targetBuffer.SetData(argsUpload);
    }

    private static void ResizeNativeArray<T>(ref NativeArray<T> array, int newLength, bool copyExisting)
        where T : struct
    {
        NativeArray<T> newArray = new NativeArray<T>(newLength, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
        if (copyExisting && array.IsCreated)
        {
            NativeArray<T>.Copy(array, newArray, Mathf.Min(array.Length, newLength));
        }

        if (array.IsCreated)
        {
            array.Dispose();
        }

        array = newArray;
    }

    private void EnsureVisibleNodeInfoCapacity(int requiredCapacity)
    {
        if (requiredCapacity <= 0)
        {
            return;
        }

        if (visibleNodeInfoArray != null && requiredCapacity <= visibleNodeInfoArray.Length)
        {
            return;
        }

        int newCapacity = Mathf.NextPowerOfTwo(Mathf.Max(requiredCapacity, 64));
        Array.Resize(ref visibleNodeInfoArray, newCapacity);
    }

    private void BindTerrainRenderProperties()
    {
        if (heightMapArray == null || normalMapArray == null || bakedData == null)
        {
            return;
        }

        UpdateTerrainShaderArrays();
        Shader.SetGlobalTexture(TerrainHeightmapTextureArrayId, heightMapArray);
        Shader.SetGlobalTexture(TerrainNormalmapTextureArrayId, normalMapArray);
        SetGlobalTextureIfNotNull(TerrainControlTextureArrayId, controlMapArray);
        SetGlobalTextureIfNotNull(TerrainLayerDiffuseArrayId, layerDiffuseArray);
        SetGlobalTextureIfNotNull(TerrainLayerNormalArrayId, layerNormalArray);
        SetGlobalTextureIfNotNull(TerrainLayerMaskArrayId, layerMaskArray);
        Shader.SetGlobalVectorArray(TerrainParamsId, terrainParams);
        Shader.SetGlobalVectorArray(TerrainOriginSizesId, terrainOriginSizes);
        Shader.SetGlobalVectorArray(TerrainLayerIndicesId, terrainLayerIndices);
        Shader.SetGlobalVectorArray(TerrainLayerTileSizeOffsetsId, terrainLayerTileSizeOffsets);
        Shader.SetGlobalVectorArray(TerrainLayerPbrParamsId, terrainLayerPbrParams);
        Shader.SetGlobalInt(TerrainCountId, bakedData.TerrainCount);
        Shader.SetGlobalInt(TerrainLayerCountId, bakedData.LayerCount);
        Shader.SetGlobalFloat(TerrainHasLayerDataId, bakedData.HasLayerData ? 1.0f : 0.0f);
        Shader.SetGlobalVectorArray(TerrainLodDebugColorsId, terrainLodDebugColors);
        Shader.SetGlobalInt(TerrainLodDebugColorCountId, GetTerrainLodDebugColorCount());
        Shader.SetGlobalInt(TerrainMaterialDebugModeId, (int)materialDebugMode);

        if (mat != null)
        {
            BindTerrainRenderProperties(mat);
        }

        terrainRenderPropertiesDirty = false;
    }

    private void BindTerrainRenderPropertiesIfDirty()
    {
        if (!terrainRenderPropertiesDirty)
        {
            return;
        }

        BindTerrainRenderProperties();
    }

    private void BindTerrainRenderProperties(Material targetMaterial)
    {
        if (targetMaterial == null || heightMapArray == null || normalMapArray == null || bakedData == null)
        {
            return;
        }

        targetMaterial.SetTexture(TerrainHeightmapTextureArrayId, heightMapArray);
        targetMaterial.SetTexture(TerrainNormalmapTextureArrayId, normalMapArray);
        SetMaterialTextureIfNotNull(targetMaterial, TerrainControlTextureArrayId, controlMapArray);
        SetMaterialTextureIfNotNull(targetMaterial, TerrainLayerDiffuseArrayId, layerDiffuseArray);
        SetMaterialTextureIfNotNull(targetMaterial, TerrainLayerNormalArrayId, layerNormalArray);
        SetMaterialTextureIfNotNull(targetMaterial, TerrainLayerMaskArrayId, layerMaskArray);
        targetMaterial.SetVectorArray(TerrainParamsId, terrainParams);
        targetMaterial.SetVectorArray(TerrainOriginSizesId, terrainOriginSizes);
        targetMaterial.SetVectorArray(TerrainLayerIndicesId, terrainLayerIndices);
        targetMaterial.SetVectorArray(TerrainLayerTileSizeOffsetsId, terrainLayerTileSizeOffsets);
        targetMaterial.SetVectorArray(TerrainLayerPbrParamsId, terrainLayerPbrParams);
        targetMaterial.SetInt(TerrainCountId, bakedData.TerrainCount);
        targetMaterial.SetInt(TerrainLayerCountId, bakedData.LayerCount);
        targetMaterial.SetFloat(TerrainHasLayerDataId, bakedData.HasLayerData ? 1.0f : 0.0f);
        targetMaterial.SetVectorArray(TerrainLodDebugColorsId, terrainLodDebugColors);
        targetMaterial.SetInt(TerrainLodDebugColorCountId, GetTerrainLodDebugColorCount());
        targetMaterial.SetInt(TerrainMaterialDebugModeId, (int)materialDebugMode);
    }

    private void BindTerrainRenderProperties(MaterialPropertyBlock propertyBlock)
    {
        if (propertyBlock == null || heightMapArray == null || normalMapArray == null || bakedData == null)
        {
            return;
        }

        propertyBlock.SetTexture(TerrainHeightmapTextureArrayId, heightMapArray);
        propertyBlock.SetTexture(TerrainNormalmapTextureArrayId, normalMapArray);
        SetPropertyBlockTextureIfNotNull(propertyBlock, TerrainControlTextureArrayId, controlMapArray);
        SetPropertyBlockTextureIfNotNull(propertyBlock, TerrainLayerDiffuseArrayId, layerDiffuseArray);
        SetPropertyBlockTextureIfNotNull(propertyBlock, TerrainLayerNormalArrayId, layerNormalArray);
        SetPropertyBlockTextureIfNotNull(propertyBlock, TerrainLayerMaskArrayId, layerMaskArray);
        propertyBlock.SetVectorArray(TerrainParamsId, terrainParams);
        propertyBlock.SetVectorArray(TerrainOriginSizesId, terrainOriginSizes);
        propertyBlock.SetVectorArray(TerrainLayerIndicesId, terrainLayerIndices);
        propertyBlock.SetVectorArray(TerrainLayerTileSizeOffsetsId, terrainLayerTileSizeOffsets);
        propertyBlock.SetVectorArray(TerrainLayerPbrParamsId, terrainLayerPbrParams);
        propertyBlock.SetInt(TerrainCountId, bakedData.TerrainCount);
        propertyBlock.SetInt(TerrainLayerCountId, bakedData.LayerCount);
        propertyBlock.SetFloat(TerrainHasLayerDataId, bakedData.HasLayerData ? 1.0f : 0.0f);
        propertyBlock.SetVectorArray(TerrainLodDebugColorsId, terrainLodDebugColors);
        propertyBlock.SetInt(TerrainLodDebugColorCountId, GetTerrainLodDebugColorCount());
        propertyBlock.SetInt(TerrainMaterialDebugModeId, (int)materialDebugMode);
    }

    private static void SetGlobalTextureIfNotNull(int propertyId, Texture texture)
    {
        if (texture != null)
        {
            Shader.SetGlobalTexture(propertyId, texture);
        }
    }

    private static void SetMaterialTextureIfNotNull(Material targetMaterial, int propertyId, Texture texture)
    {
        if (texture != null)
        {
            targetMaterial.SetTexture(propertyId, texture);
        }
    }

    private static void SetPropertyBlockTextureIfNotNull(MaterialPropertyBlock propertyBlock, int propertyId, Texture texture)
    {
        if (texture != null)
        {
            propertyBlock.SetTexture(propertyId, texture);
        }
    }

    public bool DrawHiZDepth(CommandBuffer cmd, Material depthMaterial, int shaderPass)
    {
        if (!writeTerrainDepthToHiZ || cmd == null || depthMaterial == null || bakedData == null || !bakedData.IsValid ||
            instanceMesh == null || allInstancePosBuffer == null || allInstancePosIDBuffer == null || depthArgsBuffer == null ||
            heightMapArray == null || normalMapArray == null)
        {
            return false;
        }

        if (hizDepthProperties == null)
        {
            hizDepthProperties = new MaterialPropertyBlock();
        }
        else
        {
            hizDepthProperties.Clear();
        }

        BindTerrainRenderPropertiesIfDirty();
        hizDepthProperties.SetBuffer("_AllInstancesTransformBuffer", allInstancePosBuffer);
        hizDepthProperties.SetBuffer("_VisibleInstanceIDBuffer", allInstancePosIDBuffer);
        cmd.DrawMeshInstancedIndirect(instanceMesh, 0, depthMaterial, shaderPass, depthArgsBuffer, 0, hizDepthProperties);
        return true;
    }

    private void DrawShadowMap(Camera drawCamera)
    {
        if (!castShadowMap || mat == null || instanceMesh == null || allInstancePosBuffer == null ||
            heightMapArray == null || normalMapArray == null)
        {
            return;
        }

        ComputeBuffer shadowIdBuffer = shadowMapUsesAllPatches ? allInstancePosIDBuffer : visibleInstancePosIDBuffer;
        ComputeBuffer shadowArgs = shadowMapUsesAllPatches ? depthArgsBuffer : argsBuffer;
        if (shadowIdBuffer == null || shadowArgs == null)
        {
            return;
        }

        if (shadowProperties == null)
        {
            shadowProperties = new MaterialPropertyBlock();
        }
        else
        {
            shadowProperties.Clear();
        }

        BindTerrainRenderPropertiesIfDirty();
        shadowProperties.SetBuffer("_AllInstancesTransformBuffer", allInstancePosBuffer);
        shadowProperties.SetBuffer("_VisibleInstanceIDBuffer", shadowIdBuffer);
        shadowProperties.SetFloat(TerrainDebugColorModeId, 0.0f);
        Graphics.DrawMeshInstancedIndirect(instanceMesh, 0, mat, nodeBounds, shadowArgs, 0, shadowProperties,
            ShadowCastingMode.ShadowsOnly, false, gameObject.layer, drawCamera);
    }

    private void ReadBackDebugStats(bool useCulling)
    {
        if (!DebugGizmos)
        {
            debugVisibleNodeCount = 0;
            nextDebugReadbackFrame = 0;
            return;
        }

        int frameInterval = Mathf.Max(1, debugReadbackFrameInterval);
        if (Time.frameCount < nextDebugReadbackFrame)
        {
            return;
        }

        nextDebugReadbackFrame = Time.frameCount + frameInterval;

        argsBuffer.GetData(args);
        lastVisiblePatchCount = args[1];
        if (useCulling && hizStatsBuffer != null)
        {
            hizStatsBuffer.GetData(hizStats);
        }
        else
        {
            ClearHiZStats();
        }

        int visibleCount = (int)args[1];
        EnsureVisibleNodeInfoCapacity(visibleCount);
        debugVisibleNodeCount = visibleCount;

        if (!useCulling)
        {
            for (int i = 0; i < activeNodeCount; i++)
            {
                visibleNodeInfoArray[i] = activeNodeInfoUpload[i];
            }
        }
        else if (visibleCount > 0 && visibleNodeInfoBuffer != null)
        {
            visibleNodeInfoBuffer.GetData(visibleNodeInfoArray, 0, 0, visibleCount);
        }
    }

    private void OnDrawGizmos()
    {
        if (!DebugGizmos)
        {
            return;
        }

        if (camera != null)
        {
            Gizmos.color = Color.yellow;
            Gizmos.matrix = Matrix4x4.TRS(camera.transform.position, camera.transform.rotation, Vector3.one);
            Gizmos.DrawFrustum(Vector3.zero, camera.fieldOfView, camera.farClipPlane, camera.nearClipPlane, camera.aspect);
            Gizmos.matrix = Matrix4x4.identity;
        }

        if (visibleNodeInfoArray == null)
        {
            return;
        }

        int lodCount = bakedData != null ? Mathf.Max(1, bakedData.LodCount - 1) : 1;
        for (int i = 0; i < debugVisibleNodeCount; i++)
        {
            GpuTerrainNodeInfo nodeInfo = visibleNodeInfoArray[i];
            Vector4 rect = nodeInfo.rect;
            float minHeight = nodeInfo.heightMinMax.x;
            float maxHeight = nodeInfo.heightMinMax.y;
            Gizmos.color = Color.Lerp(Color.red, Color.blue, (float)nodeInfo.mip / lodCount);
            Gizmos.DrawWireCube(
                new Vector3(rect.x + rect.z * 0.5f, (minHeight + maxHeight) * 0.5f, rect.y + rect.w * 0.5f),
                new Vector3(rect.z, Mathf.Max(0.1f, maxHeight - minHeight), rect.w));
        }
    }

    public void SetShowcaseCullingMode(GpuDrivenShowcaseCullingMode mode)
    {
        showcaseCullingMode = mode;
        UpdateHiZGeneratorState(mode.UsesHiZ());
        materialStateDirty = true;
        ApplyMaterialState(mode != GpuDrivenShowcaseCullingMode.None);
    }

    public void SetShowcaseDebugView(GpuDrivenShowcaseDebugView view)
    {
        DebugGizmos = view == GpuDrivenShowcaseDebugView.SceneWire;
        if (!DebugGizmos)
        {
            debugVisibleNodeCount = 0;
            ClearHiZStats();
            nextDebugReadbackFrame = 0;
        }
        terrainGpuBindingsDirty = true;
    }

    public void SetShowcaseDebugColorMode(bool enabled)
    {
        if (showcaseDebugColorMode == enabled)
        {
            return;
        }

        showcaseDebugColorMode = enabled;
        if (enabled)
        {
            if (materialDebugMode != TerrainMaterialDebugMode.LodColor)
            {
                materialDebugModeBeforeShowcaseColor = materialDebugMode;
            }

            materialDebugMode = TerrainMaterialDebugMode.LodColor;
        }
        else if (materialDebugMode == TerrainMaterialDebugMode.LodColor)
        {
            materialDebugMode = materialDebugModeBeforeShowcaseColor;
        }

        terrainGpuBindingsDirty = true;
        terrainRenderPropertiesDirty = true;
        materialStateDirty = true;
        ApplyMaterialState(showcaseCullingMode != GpuDrivenShowcaseCullingMode.None);
    }

    public void CollectShowcaseStats(ref GpuDrivenShowcaseStats stats)
    {
        stats.terrainPatchCount += activeNodeCount;
        stats.hizEnabled |= lastHizActive;

        if (!DebugGizmos)
        {
            return;
        }

        stats.terrainVisiblePatchCount += (int)lastVisiblePatchCount;
        stats.terrainHiZTestedPatchCount += (int)hizStats[0];
        stats.terrainHiZRejectedPatchCount += (int)hizStats[1];
        stats.terrainFrustumVisiblePatchCount += (int)hizStats[4];
        stats.terrainFrustumRejectedPatchCount += (int)hizStats[5];

        if (lastHizActive)
        {
            stats.status = "Hi-Z rejected " + hizStats[1] + " terrain patches";
        }
        else
        {
            stats.status = "Scene wire debug active";
        }
    }

    private void ApplyMaterialState(bool useVisibleIdBuffer)
    {
        if (mat == null)
        {
            return;
        }

        ComputeBuffer idBuffer = useVisibleIdBuffer ? visibleInstancePosIDBuffer : allInstancePosIDBuffer;
        if (idBuffer != null && (materialStateDirty || appliedVisibleIdBuffer != idBuffer))
        {
            mat.SetBuffer("_VisibleInstanceIDBuffer", idBuffer);
            appliedVisibleIdBuffer = idBuffer;
        }

        bool lodDebugColorMode = materialDebugMode == TerrainMaterialDebugMode.LodColor;
        if (materialStateDirty || appliedLodDebugColorMode != lodDebugColorMode)
        {
            mat.SetFloat(TerrainDebugColorModeId, lodDebugColorMode ? 1.0f : 0.0f);
            appliedLodDebugColorMode = lodDebugColorMode;
        }

        int materialDebugModeValue = (int)materialDebugMode;
        if (materialStateDirty || appliedMaterialDebugMode != materialDebugModeValue)
        {
            mat.SetInt(TerrainMaterialDebugModeId, materialDebugModeValue);
            appliedMaterialDebugMode = materialDebugModeValue;
        }

        if (materialStateDirty || !hasAppliedReceiveShadowMap || appliedReceiveShadowMap != receiveShadowMap)
        {
            if (receiveShadowMap)
            {
                mat.DisableKeyword("_RECEIVE_SHADOWS_OFF");
            }
            else
            {
                mat.EnableKeyword("_RECEIVE_SHADOWS_OFF");
            }

            appliedReceiveShadowMap = receiveShadowMap;
            hasAppliedReceiveShadowMap = true;
        }

        materialStateDirty = false;
    }

    private bool IsHiZReady()
    {
        return WantsHiZ() && depthTextureGenerator.TryGetCurrentHiZ(camera, out _, out _, out _, out _);
    }

    private bool WantsHiZ()
    {
        return showcaseCullingMode.UsesHiZ() &&
               depthTextureGenerator != null &&
               depthTextureGenerator.DepthTexture != null;
    }

    private void UpdateHiZGeneratorState(bool useCulling)
    {
        if (depthTextureGenerator != null)
        {
            depthTextureGenerator.useHiz = useCulling && depthTextureGenerator.DepthTexture != null;
        }
    }

    private bool BindHiZTextureIfReady(out Vector4 hizMapSize, out Matrix4x4 hizMatrixVP, out Vector3 hizCameraPositionWS)
    {
        if (WantsHiZ() &&
            depthTextureGenerator.TryGetCurrentHiZ(camera, out RenderTexture hizMap, out hizMapSize, out hizMatrixVP, out hizCameraPositionWS))
        {
            cullingComputeShader.SetTexture(cullTerrainKernel, HizMapId, hizMap);
            return true;
        }

        if (cullingComputeShader != null && cullTerrainKernel >= 0)
        {
            // 即使 _UseHiZ=false，也绑定有效纹理，避免启动阶段 compute kernel 报 _HizMap 未设置。
            cullingComputeShader.SetTexture(cullTerrainKernel, HizMapId, Texture2D.blackTexture);
        }

        hizMapSize = Vector4.zero;
        hizMatrixVP = Matrix4x4.identity;
        hizCameraPositionWS = camera != null ? camera.transform.position : Vector3.zero;
        return false;
    }

    private void ClearHiZStats()
    {
        for (int i = 0; i < hizStats.Length; i++)
        {
            hizStats[i] = 0;
        }
    }

    private void ReleaseBuffers()
    {
        allInstancePosBuffer?.Release();
        allInstancePosBuffer = null;
        visibleInstancePosIDBuffer?.Release();
        visibleInstancePosIDBuffer = null;
        allInstancePosIDBuffer?.Release();
        allInstancePosIDBuffer = null;
        argsBuffer?.Release();
        argsBuffer = null;
        depthArgsBuffer?.Release();
        depthArgsBuffer = null;
        visibleNodeInfoBuffer?.Release();
        visibleNodeInfoBuffer = null;
        terrainLeafLookupInfoBuffer?.Release();
        terrainLeafLookupInfoBuffer = null;
        terrainLeafMipLookupBuffer?.Release();
        terrainLeafMipLookupBuffer = null;
        hizStatsBuffer?.Release();
        hizStatsBuffer = null;
        cullTerrainKernel = -1;
        clearTerrainLeafLookupKernel = -1;
        buildTerrainLeafLookupKernel = -1;
        buildTerrainNeighborsKernel = -1;
        terrainGpuBindingsDirty = true;
        materialStateDirty = true;
        appliedVisibleIdBuffer = null;
    }

    private void ReleaseTerrainTextureArrays()
    {
        heightMapArray = null;
        normalMapArray = null;
        controlMapArray = null;
        layerDiffuseArray = null;
        layerNormalArray = null;
        layerMaskArray = null;
        terrainRenderPropertiesDirty = true;
    }

    private void ReleaseUploadArrays()
    {
        if (activeNodeInfoUpload.IsCreated)
        {
            activeNodeInfoUpload.Dispose();
        }

        if (allNodeIdsUpload.IsCreated)
        {
            allNodeIdsUpload.Dispose();
        }

        if (argsUpload.IsCreated)
        {
            argsUpload.Dispose();
        }

        if (hizStatsResetUpload.IsCreated)
        {
            hizStatsResetUpload.Dispose();
        }

        if (terrainLeafLookupInfoUpload.IsCreated)
        {
            terrainLeafLookupInfoUpload.Dispose();
        }
    }

    private static bool IsOpenGLClipSpace()
    {
        GraphicsDeviceType deviceType = SystemInfo.graphicsDeviceType;
        return deviceType == GraphicsDeviceType.OpenGLCore ||
               deviceType == GraphicsDeviceType.OpenGLES2 ||
               deviceType == GraphicsDeviceType.OpenGLES3;
    }

    [Serializable]
    private sealed class TerrainLodConfig
    {
        public float distance;
        public Color debugColor;

        public TerrainLodConfig(float distance, Color debugColor)
        {
            this.distance = distance;
            this.debugColor = debugColor;
        }
    }

    private enum TerrainMaterialDebugMode
    {
        Lit = 0,
        LodColor = 1,
        LayerBlend = 2,
        ControlWeights = 3,
        Layer0 = 4,
        Layer1 = 5,
        Layer2 = 6,
        Layer3 = 7,
        HasLayerData = 8
    }

}

[StructLayout(LayoutKind.Sequential)]
internal struct GpuTerrainLeafLookupInfo
{
    public Vector4 originCellSizeMaxX;
    public Vector4 sizeOffsetMaxZ;

    public GpuTerrainLeafLookupInfo(Vector4 originCellSizeMaxX, Vector4 sizeOffsetMaxZ)
    {
        this.originCellSizeMaxX = originCellSizeMaxX;
        this.sizeOffsetMaxZ = sizeOffsetMaxZ;
    }
}
