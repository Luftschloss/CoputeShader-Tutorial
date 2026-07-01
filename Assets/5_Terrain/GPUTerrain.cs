using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using UnityEngine.Serialization;

public class GPUTerrain : MonoBehaviour
{
    private static readonly List<GPUTerrain> ActiveTerrains = new List<GPUTerrain>();
    private const int HiZStatsCount = 6;
    private const int IndirectArgsCount = 5;
    private const int NodeInfoStride = 40;
    private const int MaxTerrainLodDebugColorCount = 16;
    private static readonly int TerrainDebugColorModeId = Shader.PropertyToID("_TerrainDebugColorMode");
    private static readonly int TerrainLodDebugColorsId = Shader.PropertyToID("_TerrainLodDebugColors");
    private static readonly int TerrainLodDebugColorCountId = Shader.PropertyToID("_TerrainLodDebugColorCount");
    private static readonly int TerrainHeightmapTextureArrayId = Shader.PropertyToID("_TerrainHeightmapTextureArray");
    private static readonly int TerrainNormalmapTextureArrayId = Shader.PropertyToID("_TerrainNormalmapTextureArray");
    private static readonly int TerrainParamsId = Shader.PropertyToID("_TerrainParams");
    private static readonly int TerrainOriginSizesId = Shader.PropertyToID("_TerrainOriginSizes");
    private static readonly int TerrainCountId = Shader.PropertyToID("_TerrainCount");
    private static readonly int HizDepthBiasId = Shader.PropertyToID("_HizDepthBias");
    private static readonly int HizCameraPositionWSId = Shader.PropertyToID("_HizCameraPositionWS");
    private static readonly int HizCameraMatrixVPId = Shader.PropertyToID("_HizCameraMatrixVP");
    private static readonly int HizMapId = Shader.PropertyToID("_HizMap");
    private static readonly int HizMapSizeId = Shader.PropertyToID("_HizMapSize");
    private const string ReverseZKeyword = "_REVERSE_Z";

    public static int ActiveTerrainCount => ActiveTerrains.Count;
    public static GPUTerrain GetActiveTerrain(int index) => ActiveTerrains[index];

    [Header("Baked Data")]
    [SerializeField] private GpuTerrainBakedData bakedData;
    [SerializeField] private Mesh instanceMesh;

    [Header("LOD")]
    [SerializeField] private TerrainLodConfig[] lodConfigs =
    {
        new TerrainLodConfig(100.0f, new Color(1.0f, 0.0f, 0.0f, 1.0f)),
        new TerrainLodConfig(200.0f, new Color(0.75f, 0.0f, 0.25f, 1.0f)),
        new TerrainLodConfig(500.0f, new Color(0.5f, 0.0f, 0.5f, 1.0f)),
        new TerrainLodConfig(1000.0f, new Color(0.25f, 0.0f, 0.75f, 1.0f)),
        new TerrainLodConfig(0.0f, new Color(0.0f, 0.0f, 1.0f, 1.0f))
    };
    [SerializeField, HideInInspector, FormerlySerializedAs("lodDistance")] private float[] legacyLodDistance;
    [SerializeField, HideInInspector, FormerlySerializedAs("lodDebugColors")] private Color[] legacyLodDebugColors;
    [SerializeField, Range(0.0f, 5.0f)] private float lodRebuildDistanceThreshold = 0.5f;

    [Header("Hi-Z Terrain Culling")]
    [SerializeField] private bool writeTerrainDepthToHiZ;
    [SerializeField, Range(0.0f, 100.0f)] private float hizDepthBias = 1.0f;
    [SerializeField, Range(0.0f, 0.2f)] private float frustumPadding = 0.03f;

    [Header("Shadow Map")]
    [SerializeField] private bool castShadowMap = true;
    [SerializeField] private bool receiveShadowMap = true;
    [SerializeField] private bool shadowMapUsesAllPatches = true;

    [Header("Rendering")]
    [SerializeField] private ComputeShader cullingComputeShader;
    [SerializeField] private Material mat;

    public DepthTextureGenerator depthTextureGenerator;
    public bool DebugGizmos;

    private readonly Vector4[] terrainParams = new Vector4[GpuTerrainBakedData.MaxTerrainCount];
    private readonly Vector4[] terrainOriginSizes = new Vector4[GpuTerrainBakedData.MaxTerrainCount];
    private readonly Vector4[] terrainLodDebugColors = new Vector4[MaxTerrainLodDebugColorCount];
    private readonly uint[] args = new uint[IndirectArgsCount] { 0, 0, 0, 0, 0 };
    private readonly uint[] hizStats = new uint[HiZStatsCount];

    private Texture2DArray heightMapArray;
    private Texture2DArray normalMapArray;
    private Bounds nodeBounds = new Bounds(Vector3.zero, new Vector3(1000.0f, 1000.0f, 1000.0f));
    private ComputeBuffer allInstancePosBuffer;
    private ComputeBuffer visibleInstancePosIDBuffer;
    private ComputeBuffer allInstancePosIDBuffer;
    private ComputeBuffer argsBuffer;
    private ComputeBuffer depthArgsBuffer;
    private ComputeBuffer hizStatsBuffer;
    private ComputeBuffer visibleNodeInfoBuffer;
    private NativeArray<GpuTerrainNodeInfo> activeNodeInfoUpload;
    private NativeArray<uint> allNodeIdsUpload;
    private NativeArray<uint> argsUpload;
    private NativeArray<uint> hizStatsResetUpload;
    private GpuTerrainNodeInfo[] visibleNodeInfoArray;
    private TerrainLeafLookup[] terrainLeafLookups = Array.Empty<TerrainLeafLookup>();
    private int[] activeNodeIndices = Array.Empty<int>();
    private int[] previousActiveNodeIndices = Array.Empty<int>();
    private int activeNodeCount;
    private int previousActiveNodeCount;
    private int debugVisibleNodeCount;
    private int cullTerrainKernel = -1;
    private Camera camera;
    private GpuDrivenShowcaseCullingMode showcaseCullingMode = GpuDrivenShowcaseCullingMode.FrustumAndHiZ;
    private GpuDrivenShowcaseDebugView showcaseDebugView = GpuDrivenShowcaseDebugView.None;
    private bool terrainColorDebug;
    private bool lastHizActive;
    private bool forceLodRebuild = true;
    private bool terrainResourcesDirty;
    private bool terrainGpuBindingsDirty = true;
    private bool nodeIdBufferDirty = true;
    private uint lastVisiblePatchCount;
    private float nextStatsReadbackTime;
    private Vector2 lastLodBuildCameraXZ;
    private MaterialPropertyBlock hizDepthProperties;
    private MaterialPropertyBlock shadowProperties;

    public void SetBakedData(GpuTerrainBakedData data)
    {
        bakedData = data;
        terrainResourcesDirty = true;
        forceLodRebuild = true;
    }

    private void OnEnable()
    {
        EnsureLodConfigDefaults();
        if (!ActiveTerrains.Contains(this))
        {
            ActiveTerrains.Add(this);
        }

        camera = Camera.main;
        RebuildTerrainResources();
        ApplyMaterialState(showcaseCullingMode != GpuDrivenShowcaseCullingMode.None);
        BindTerrainRenderProperties();
    }

    private void OnValidate()
    {
        EnsureLodConfigDefaults();
        terrainResourcesDirty = true;
        forceLodRebuild = true;
        terrainGpuBindingsDirty = true;
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

        BindTerrainRenderProperties();
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
        lastHizActive = useHiz;
        if (!useHiz)
        {
            ClearHiZStats();
        }

        UpdateHiZGeneratorState(wantsHiZ);

        Matrix4x4 vp = GL.GetGPUProjectionMatrix(camera.projectionMatrix, false) * camera.worldToCameraMatrix;
        visibleInstancePosIDBuffer.SetCounterValue(0);
        if (visibleNodeInfoBuffer != null)
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
        cullingComputeShader.SetBool("_WriteDebugResult", DebugGizmos);
        ApplyMaterialState(useCulling);

        if (useCulling)
        {
            if (hizStatsBuffer != null)
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

        ReadBackDebugStats(useCulling, useHiz);
#if UNITY_EDITOR
        Camera drawCamera = null;
#else
        Camera drawCamera = camera;
#endif
        Graphics.DrawMeshInstancedIndirect(instanceMesh, 0, mat, nodeBounds, argsBuffer, 0, null,
            ShadowCastingMode.Off, receiveShadowMap, gameObject.layer, drawCamera);
        DrawShadowMap();
    }

    private void OnDisable()
    {
        ActiveTerrains.Remove(this);
        ReleaseBuffers();
        ReleaseUploadArrays();
        ReleaseTerrainTextureArrays();
    }

    private void RebuildTerrainResources()
    {
        ReleaseBuffers();
        ReleaseTerrainTextureArrays();
        terrainGpuBindingsDirty = true;
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
        EnsureTerrainLeafLookups();
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

    private void EnsureLodConfigDefaults()
    {
        MigrateLegacyLodConfig();

        if (lodConfigs == null || lodConfigs.Length == 0)
        {
            lodConfigs = CreateDefaultLodConfigs();
            return;
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

    private void MigrateLegacyLodConfig()
    {
        bool hasLegacyDistance = legacyLodDistance != null && legacyLodDistance.Length > 0;
        bool hasLegacyColors = legacyLodDebugColors != null && legacyLodDebugColors.Length > 0;
        if (!hasLegacyDistance && !hasLegacyColors)
        {
            return;
        }

        int count = Mathf.Clamp(Mathf.Max(
            hasLegacyDistance ? legacyLodDistance.Length : 0,
            hasLegacyColors ? legacyLodDebugColors.Length : 0), 1, MaxTerrainLodDebugColorCount);
        TerrainLodConfig[] migratedConfigs = new TerrainLodConfig[count];
        for (int i = 0; i < count; i++)
        {
            float distance = hasLegacyDistance && i < legacyLodDistance.Length ? legacyLodDistance[i] : 0.0f;
            Color debugColor = hasLegacyColors && i < legacyLodDebugColors.Length
                ? legacyLodDebugColors[i]
                : GetDefaultLodDebugColor(i, count);
            migratedConfigs[i] = new TerrainLodConfig(distance, debugColor);
        }

        lodConfigs = migratedConfigs;
        legacyLodDistance = null;
        legacyLodDebugColors = null;
    }

    private static TerrainLodConfig[] CreateDefaultLodConfigs()
    {
        return new[]
        {
            new TerrainLodConfig(100.0f, GetDefaultLodDebugColor(0, 5)),
            new TerrainLodConfig(200.0f, GetDefaultLodDebugColor(1, 5)),
            new TerrainLodConfig(500.0f, GetDefaultLodDebugColor(2, 5)),
            new TerrainLodConfig(1000.0f, GetDefaultLodDebugColor(3, 5)),
            new TerrainLodConfig(0.0f, GetDefaultLodDebugColor(4, 5))
        };
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

    private void RebuildVisibleTerrainNodes()
    {
        activeNodeCount = 0;

        Vector2 cameraXZ = new Vector2(camera.transform.position.x, camera.transform.position.z);
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

        Profiler.BeginSample("Gpu Terrain LOD Lookup");
        BuildActiveNodeLookup();
        Profiler.EndSample();

        Profiler.BeginSample("Gpu Terrain LOD Neighbors");
        for (int i = 0; i < activeNodeCount; i++)
        {
            GpuTerrainNodeInfo nodeInfo = activeNodeInfoUpload[i];
            nodeInfo.neighbor = CalculateNeighborMask(nodeInfo);
            activeNodeInfoUpload[i] = nodeInfo;
        }
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

    private int CalculateNeighborMask(GpuTerrainNodeInfo nodeInfo)
    {
        if (bakedData != null && nodeInfo.mip >= bakedData.LodCount - 1)
        {
            return 0;
        }

        Vector4 rect = nodeInfo.rect;
        float centerX = rect.x + rect.z * 0.5f;
        float centerZ = rect.y + rect.w * 0.5f;
        int terrainIndex = nodeInfo.terrainIndex;
        int mip = nodeInfo.mip;
        int mask = 0;
        if (HasCoarserNeighbor(centerX, centerZ + rect.w, terrainIndex, mip))
            mask |= 1;
        if (HasCoarserNeighbor(centerX, centerZ - rect.w, terrainIndex, mip))
            mask |= 1 << 1;
        if (HasCoarserNeighbor(centerX - rect.z, centerZ, terrainIndex, mip))
            mask |= 1 << 2;
        if (HasCoarserNeighbor(centerX + rect.z, centerZ, terrainIndex, mip))
            mask |= 1 << 3;
        return mask;
    }

    private bool HasCoarserNeighbor(float worldX, float worldZ, int preferredTerrainIndex, int mip)
    {
        return TryFindActiveNodeMip(worldX, worldZ, preferredTerrainIndex, out int neighborMip) && neighborMip > mip;
    }

    private bool TryFindActiveNodeMip(float worldX, float worldZ, int preferredTerrainIndex, out int mip)
    {
        if (TryFindActiveNodeMipInTerrain(worldX, worldZ, preferredTerrainIndex, out mip))
        {
            return true;
        }

        for (int i = 0; i < terrainLeafLookups.Length; i++)
        {
            if (i == preferredTerrainIndex)
            {
                continue;
            }

            TerrainLeafLookup lookup = terrainLeafLookups[i];
            if (lookup != null &&
                lookup.ContainsWorldPoint(worldX, worldZ) &&
                TryFindActiveNodeMipInTerrain(worldX, worldZ, i, out mip))
            {
                return true;
            }
        }

        mip = 0;
        return false;
    }

    private bool TryFindActiveNodeMipInTerrain(float worldX, float worldZ, int terrainIndex, out int mip)
    {
        if (terrainIndex < 0 || terrainIndex >= terrainLeafLookups.Length)
        {
            mip = 0;
            return false;
        }

        TerrainLeafLookup lookup = terrainLeafLookups[terrainIndex];
        if (lookup != null && lookup.TryGetActiveNodeMip(worldX, worldZ, out mip))
        {
            return true;
        }

        mip = 0;
        return false;
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

        cullingComputeShader.SetBuffer(cullTerrainKernel, "_AllInstancesPosWSBuffer", allInstancePosBuffer);
        cullingComputeShader.SetBuffer(cullTerrainKernel, "_VisibleInstancesOnlyPosWSIDBuffer", visibleInstancePosIDBuffer);
        cullingComputeShader.SetBuffer(cullTerrainKernel, "result", visibleNodeInfoBuffer);
        cullingComputeShader.SetBuffer(cullTerrainKernel, "_HiZStatsBuffer", hizStatsBuffer);
        cullingComputeShader.SetFloat("_FrustumPadding", frustumPadding);
        cullingComputeShader.SetFloat(HizDepthBiasId, hizDepthBias);
        ConfigureCullingShaderKeywords();
        cullingComputeShader.SetBool("isOpenGL", IsOpenGLClipSpace());
        cullingComputeShader.SetInt("_InstanceCount", activeNodeCount);
        cullingComputeShader.SetBool("_UseHiZ", IsHiZReady());
        cullingComputeShader.SetBool("_WriteDebugResult", DebugGizmos);
        mat.SetBuffer("_AllInstancesTransformBuffer", allInstancePosBuffer);
        ApplyMaterialState(showcaseCullingMode != GpuDrivenShowcaseCullingMode.None);
        terrainGpuBindingsDirty = false;
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

    private void EnsureTerrainLeafLookups()
    {
        if (bakedData == null || !bakedData.IsValid)
        {
            terrainLeafLookups = Array.Empty<TerrainLeafLookup>();
            return;
        }

        GpuTerrainBakedData.TerrainTileInfo[] terrains = bakedData.Terrains;
        if (terrainLeafLookups.Length != terrains.Length)
        {
            terrainLeafLookups = new TerrainLeafLookup[terrains.Length];
        }

        float leafSize = GetLeafPatchSize();
        for (int i = 0; i < terrains.Length; i++)
        {
            Vector3 size = terrains[i].size;
            Vector3 origin = terrains[i].worldOrigin;
            int width = Mathf.Max(1, Mathf.CeilToInt(size.x / leafSize));
            int depth = Mathf.Max(1, Mathf.CeilToInt(size.z / leafSize));
            TerrainLeafLookup lookup = terrainLeafLookups[i];
            if (lookup == null || !lookup.Matches(origin.x, origin.z, size.x, size.z, leafSize, width, depth))
            {
                terrainLeafLookups[i] = new TerrainLeafLookup(origin.x, origin.z, size.x, size.z, leafSize, width, depth);
            }
            else
            {
                lookup.Clear();
            }
        }
    }

    private void BuildActiveNodeLookup()
    {
        for (int i = 0; i < terrainLeafLookups.Length; i++)
        {
            terrainLeafLookups[i]?.Clear();
        }

        for (int i = 0; i < activeNodeCount; i++)
        {
            GpuTerrainNodeInfo nodeInfo = activeNodeInfoUpload[i];
            int terrainIndex = nodeInfo.terrainIndex;
            if (terrainIndex >= 0 && terrainIndex < terrainLeafLookups.Length)
            {
                terrainLeafLookups[terrainIndex]?.MarkActiveNode(nodeInfo.rect, nodeInfo.mip);
            }
        }
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
        Shader.SetGlobalVectorArray(TerrainParamsId, terrainParams);
        Shader.SetGlobalVectorArray(TerrainOriginSizesId, terrainOriginSizes);
        Shader.SetGlobalInt(TerrainCountId, bakedData.TerrainCount);
        Shader.SetGlobalVectorArray(TerrainLodDebugColorsId, terrainLodDebugColors);
        Shader.SetGlobalInt(TerrainLodDebugColorCountId, GetTerrainLodDebugColorCount());

        if (mat != null)
        {
            BindTerrainRenderProperties(mat);
        }
    }

    private void BindTerrainRenderProperties(Material targetMaterial)
    {
        if (targetMaterial == null || heightMapArray == null || normalMapArray == null || bakedData == null)
        {
            return;
        }

        targetMaterial.SetTexture(TerrainHeightmapTextureArrayId, heightMapArray);
        targetMaterial.SetTexture(TerrainNormalmapTextureArrayId, normalMapArray);
        targetMaterial.SetVectorArray(TerrainParamsId, terrainParams);
        targetMaterial.SetVectorArray(TerrainOriginSizesId, terrainOriginSizes);
        targetMaterial.SetInt(TerrainCountId, bakedData.TerrainCount);
        targetMaterial.SetVectorArray(TerrainLodDebugColorsId, terrainLodDebugColors);
        targetMaterial.SetInt(TerrainLodDebugColorCountId, GetTerrainLodDebugColorCount());
    }

    private void BindTerrainRenderProperties(MaterialPropertyBlock propertyBlock)
    {
        if (propertyBlock == null || heightMapArray == null || normalMapArray == null || bakedData == null)
        {
            return;
        }

        propertyBlock.SetTexture(TerrainHeightmapTextureArrayId, heightMapArray);
        propertyBlock.SetTexture(TerrainNormalmapTextureArrayId, normalMapArray);
        propertyBlock.SetVectorArray(TerrainParamsId, terrainParams);
        propertyBlock.SetVectorArray(TerrainOriginSizesId, terrainOriginSizes);
        propertyBlock.SetInt(TerrainCountId, bakedData.TerrainCount);
        propertyBlock.SetVectorArray(TerrainLodDebugColorsId, terrainLodDebugColors);
        propertyBlock.SetInt(TerrainLodDebugColorCountId, GetTerrainLodDebugColorCount());
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

        hizDepthProperties.SetBuffer("_AllInstancesTransformBuffer", allInstancePosBuffer);
        hizDepthProperties.SetBuffer("_VisibleInstanceIDBuffer", allInstancePosIDBuffer);
        BindTerrainRenderProperties(hizDepthProperties);
        cmd.DrawMeshInstancedIndirect(instanceMesh, 0, depthMaterial, shaderPass, depthArgsBuffer, 0, hizDepthProperties);
        return true;
    }

    private void DrawShadowMap()
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

        shadowProperties.SetBuffer("_AllInstancesTransformBuffer", allInstancePosBuffer);
        shadowProperties.SetBuffer("_VisibleInstanceIDBuffer", shadowIdBuffer);
        shadowProperties.SetFloat(TerrainDebugColorModeId, 0.0f);
        BindTerrainRenderProperties(shadowProperties);
        Graphics.DrawMeshInstancedIndirect(instanceMesh, 0, mat, nodeBounds, shadowArgs, 0, shadowProperties,
            ShadowCastingMode.ShadowsOnly, false, gameObject.layer, null);
    }

    private void ReadBackDebugStats(bool useCulling, bool useHiz)
    {
        bool readBackStats = DebugGizmos || Time.unscaledTime >= nextStatsReadbackTime;
        if (readBackStats)
        {
            nextStatsReadbackTime = Time.unscaledTime + 0.25f;
        }

        if (!readBackStats)
        {
            return;
        }

        argsBuffer.GetData(args);
        lastVisiblePatchCount = args[1];
        if (useHiz && hizStatsBuffer != null)
        {
            hizStatsBuffer.GetData(hizStats);
        }

        if (!DebugGizmos)
        {
            debugVisibleNodeCount = 0;
            return;
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
        ApplyMaterialState(mode != GpuDrivenShowcaseCullingMode.None);
    }

    public void SetShowcaseDebugView(GpuDrivenShowcaseDebugView view)
    {
        showcaseDebugView = view;
        DebugGizmos = view == GpuDrivenShowcaseDebugView.Lod ||
                      view == GpuDrivenShowcaseDebugView.HiZ ||
                      view == GpuDrivenShowcaseDebugView.Bounds;
        terrainGpuBindingsDirty = true;
    }

    public void SetTerrainColorDebug(bool enabled)
    {
        terrainColorDebug = enabled;
        ApplyMaterialState(showcaseCullingMode != GpuDrivenShowcaseCullingMode.None);
    }

    public void CollectShowcaseStats(ref GpuDrivenShowcaseStats stats)
    {
        stats.terrainPatchCount += activeNodeCount;
        stats.terrainVisiblePatchCount += (int)lastVisiblePatchCount;
        stats.terrainHiZTestedPatchCount += (int)hizStats[0];
        stats.terrainHiZRejectedPatchCount += (int)hizStats[1];
        stats.terrainHiZSkippedPatchCount += (int)hizStats[2];
        stats.terrainCullingDispatchedPatchCount += (int)hizStats[3];
        stats.terrainFrustumVisiblePatchCount += (int)hizStats[4];
        stats.terrainFrustumRejectedPatchCount += (int)hizStats[5];
        stats.terrainDepthOccluderEnabled |= writeTerrainDepthToHiZ && GpuDrivenHizFeature.IsTerrainDepthInjectionEnabled;
        stats.terrainColorDebugEnabled |= terrainColorDebug;
        stats.terrainShadowCasterEnabled |= castShadowMap;
        stats.terrainShadowReceiverEnabled |= receiveShadowMap;
        if (depthTextureGenerator != null)
        {
            stats.depthTextureDescription = depthTextureGenerator.DepthTextureDescription;
        }
        stats.hizEnabled |= lastHizActive;
        if (terrainColorDebug)
        {
            stats.status = "Terrain LOD color debug active";
        }
        else if (showcaseDebugView == GpuDrivenShowcaseDebugView.Lod)
        {
            stats.status = "LOD debug active";
        }
        else if (lastHizActive)
        {
            stats.status = "Hi-Z rejected " + hizStats[1] + " terrain patches";
        }
    }

    private void ApplyMaterialState(bool useVisibleIdBuffer)
    {
        if (mat == null)
        {
            return;
        }

        ComputeBuffer idBuffer = useVisibleIdBuffer ? visibleInstancePosIDBuffer : allInstancePosIDBuffer;
        if (idBuffer != null)
        {
            mat.SetBuffer("_VisibleInstanceIDBuffer", idBuffer);
        }

        mat.SetFloat(TerrainDebugColorModeId, terrainColorDebug ? 1.0f : 0.0f);
        if (receiveShadowMap)
        {
            mat.DisableKeyword("_RECEIVE_SHADOWS_OFF");
        }
        else
        {
            mat.EnableKeyword("_RECEIVE_SHADOWS_OFF");
        }
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
        hizStatsBuffer?.Release();
        hizStatsBuffer = null;
        cullTerrainKernel = -1;
        terrainGpuBindingsDirty = true;
    }

    private void ReleaseTerrainTextureArrays()
    {
        heightMapArray = null;
        normalMapArray = null;
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

    private sealed class TerrainLeafLookup
    {
        private readonly float originX;
        private readonly float originZ;
        private readonly float maxX;
        private readonly float maxZ;
        private readonly float cellSize;
        private readonly int width;
        private readonly int depth;
        private readonly int[] activeNodeMipByCell;
        private readonly int[] cellStamps;
        private int currentStamp;

        public TerrainLeafLookup(float originX, float originZ, float terrainSizeX, float terrainSizeZ, float cellSize, int width, int depth)
        {
            this.originX = originX;
            this.originZ = originZ;
            this.cellSize = cellSize;
            this.width = width;
            this.depth = depth;
            maxX = originX + terrainSizeX;
            maxZ = originZ + terrainSizeZ;
            activeNodeMipByCell = new int[Mathf.Max(1, width * depth)];
            cellStamps = new int[activeNodeMipByCell.Length];
            Clear();
        }

        public bool Matches(float otherOriginX, float otherOriginZ, float terrainSizeX, float terrainSizeZ, float otherCellSize, int otherWidth, int otherDepth)
        {
            return Mathf.Approximately(originX, otherOriginX) &&
                   Mathf.Approximately(originZ, otherOriginZ) &&
                   Mathf.Approximately(maxX, otherOriginX + terrainSizeX) &&
                   Mathf.Approximately(maxZ, otherOriginZ + terrainSizeZ) &&
                   Mathf.Approximately(cellSize, otherCellSize) &&
                   width == otherWidth &&
                   depth == otherDepth;
        }

        public void Clear()
        {
            currentStamp++;
            if (currentStamp != int.MaxValue)
            {
                return;
            }

            currentStamp = 1;
            Array.Clear(cellStamps, 0, cellStamps.Length);
        }

        public void MarkActiveNode(Vector4 rect, int mip)
        {
            int minX = PositionToCellFloor(rect.x - originX);
            int minZ = PositionToCellFloor(rect.y - originZ);
            int maxX = PositionToCellCeil(rect.x + rect.z - originX) - 1;
            int maxZ = PositionToCellCeil(rect.y + rect.w - originZ) - 1;
            minX = Mathf.Clamp(minX, 0, width - 1);
            maxX = Mathf.Clamp(maxX, 0, width - 1);
            minZ = Mathf.Clamp(minZ, 0, depth - 1);
            maxZ = Mathf.Clamp(maxZ, 0, depth - 1);
            if (maxX < minX || maxZ < minZ)
            {
                return;
            }

            for (int z = minZ; z <= maxZ; z++)
            {
                int row = z * width;
                for (int x = minX; x <= maxX; x++)
                {
                    int cellIndex = row + x;
                    activeNodeMipByCell[cellIndex] = mip;
                    cellStamps[cellIndex] = currentStamp;
                }
            }
        }

        public bool TryGetActiveNodeMip(float worldX, float worldZ, out int mip)
        {
            int x = PositionToCellFloor(worldX - originX);
            int z = PositionToCellFloor(worldZ - originZ);
            if (x < 0 || x >= width || z < 0 || z >= depth)
            {
                mip = 0;
                return false;
            }

            int cellIndex = z * width + x;
            if (cellStamps[cellIndex] != currentStamp)
            {
                mip = 0;
                return false;
            }

            mip = activeNodeMipByCell[cellIndex];
            return true;
        }

        public bool ContainsWorldPoint(float worldX, float worldZ)
        {
            return worldX >= originX && worldX < maxX &&
                   worldZ >= originZ && worldZ < maxZ;
        }

        private int PositionToCellFloor(float localPosition)
        {
            return Mathf.FloorToInt(localPosition / cellSize);
        }

        private int PositionToCellCeil(float localPosition)
        {
            return Mathf.CeilToInt(localPosition / cellSize);
        }
    }
}
