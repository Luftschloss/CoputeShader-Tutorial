using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;

public class GPUTerrain : MonoBehaviour
{
    private static readonly List<GPUTerrain> activeTerrains = new List<GPUTerrain>();
    private const int MaxTerrainCount = 64;
    private static readonly int TerrainDebugColorModeId = Shader.PropertyToID("_TerrainDebugColorMode");
    private static readonly int TerrainHeightmapTextureArrayId = Shader.PropertyToID("_TerrainHeightmapTextureArray");
    private static readonly int TerrainNormalmapTextureArrayId = Shader.PropertyToID("_TerrainNormalmapTextureArray");
    private static readonly int TerrainParamsId = Shader.PropertyToID("_TerrainParams");
    private static readonly int TerrainOriginSizesId = Shader.PropertyToID("_TerrainOriginSizes");
    private static readonly int TerrainCountId = Shader.PropertyToID("_TerrainCount");
    private static readonly int HiZWorldDepthBiasId = Shader.PropertyToID("_HiZWorldDepthBias");
    private static readonly int HiZCameraPositionWSId = Shader.PropertyToID("_HiZCameraPositionWS");
    private static readonly int UseReversedZId = Shader.PropertyToID("_UseReversedZ");
    private static readonly int DepthTextureSizeId = Shader.PropertyToID("depthTextureSize");

    public static int ActiveTerrainCount => activeTerrains.Count;
    public static GPUTerrain GetActiveTerrain(int index) => activeTerrains[index];

    [Header("Data")]
    [SerializeField] List<Terrain> terrainList = new List<Terrain>();
    [SerializeField] Mesh instanceMesh;
    Bounds nodeBounds = new Bounds(Vector3.zero, new Vector3(1000.0f, 1000.0f, 1000.0f));

    [Header("GPU Terrain Parameters")]
    public float resolution = 64;
    public int LOD = 4;
    public float[] lodDistance;
    [SerializeField, Range(0.0f, 5.0f)] float lodRebuildDistanceThreshold = 0.5f;

    [SerializeField]RenderTexture heightMap;
    [SerializeField]Texture2D normalMap;
    Texture2DArray heightMapArray;
    Texture2DArray normalMapArray;
    readonly List<TerrainRuntimeData> terrainRuntimeData = new List<TerrainRuntimeData>();
    readonly Vector4[] terrainParams = new Vector4[MaxTerrainCount];
    readonly Vector4[] terrainOriginSizes = new Vector4[MaxTerrainCount];

    [Header("Hi-Z Terrain Culling")]
    [SerializeField] bool writeTerrainDepthToHiZ = true;
    [SerializeField, Range(0.0f, 100.0f)] float hizDepthBias = 1.0f;
    [SerializeField, Range(0.0f, 1.0f)] float terrainHeightPaddingScale = 0.1f;
    [SerializeField, Range(0.0f, 2.0f)] float terrainHeightPaddingResolutionScale = 0.5f;
    [SerializeField, Range(0.0f, 0.2f)] float frustumPadding = 0.03f;

    [Header("Shadow Map")]
    [SerializeField] bool castShadowMap = true;
    [SerializeField] bool receiveShadowMap = true;
    [SerializeField] bool shadowMapUsesAllPatches = true;

    // CPU-maintained terrain quadtree nodes.
    readonly List<TerrainNode> rootNodes = new List<TerrainNode>();
    List<NodeInfo> allNodeInfo;

    #region Compute Shader
    ComputeBuffer allInstancePosBuffer;
    ComputeBuffer visibleInstancePosIDBuffer;
    ComputeBuffer allInstancePosIDBuffer;
    ComputeBuffer argsBuffer;
    uint[] args = new uint[5] { 0, 0, 0, 0, 0 };
    ComputeBuffer depthArgsBuffer;
    ComputeBuffer hizStatsBuffer;
    const int HiZStatsCount = 6;
    readonly uint[] hizStats = new uint[HiZStatsCount];
    readonly uint[] hizStatsReset = new uint[HiZStatsCount];

    [SerializeField]ComputeShader cullingComputeShader;
    int cullTerrainKernel;

    [SerializeField]Material mat;
    #endregion

    const string profilerTag = "Gpu Terrain";

    Camera camera;

    public DepthTextureGenerator depthTextureGenerator;
    GpuDrivenShowcaseCullingMode showcaseCullingMode = GpuDrivenShowcaseCullingMode.FrustumAndHiZ;
    GpuDrivenShowcaseDebugView showcaseDebugView = GpuDrivenShowcaseDebugView.None;
    bool terrainColorDebug;
    uint lastVisiblePatchCount;
    float nextStatsReadbackTime;
    bool lastHizActive;
    Vector2 lastLodBuildCameraXZ;
    bool forceLodRebuild = true;
    bool terrainResourcesDirty;
    MaterialPropertyBlock hizDepthProperties;
    MaterialPropertyBlock shadowProperties;

    private void OnEnable()
    {
        if (!activeTerrains.Contains(this))
        {
            activeTerrains.Add(this);
        }

        camera = Camera.main;
        RebuildTerrainResources();
        ApplyMaterialState(showcaseCullingMode != GpuDrivenShowcaseCullingMode.None);

        BindTerrainRenderProperties();
    }

    private void OnValidate()
    {
        if (terrainList == null)
        {
            terrainList = new List<Terrain>();
        }

        terrainResourcesDirty = true;
        forceLodRebuild = true;
    }

    void RebuildTerrainResources()
    {
        resolution = Mathf.Max(1.0f, resolution);
        LOD = Mathf.Max(1, LOD);
        RefreshTerrainRuntimeData();
        UpdateNodeBounds();
        GenerateTerrainNode();
        BuildTerrainTextureArrays();
        if (camera != null)
        {
            RebuildVisibleTerrainNodes();
        }

        terrainResourcesDirty = false;
    }

    void RefreshTerrainRuntimeData()
    {
        terrainRuntimeData.Clear();
        List<Terrain> sourceTerrains = terrainList;

        if (sourceTerrains == null)
        {
            return;
        }

        for (int i = 0; i < sourceTerrains.Count && terrainRuntimeData.Count < MaxTerrainCount; i++)
        {
            Terrain item = sourceTerrains[i];
            if (item == null || item.terrainData == null)
            {
                continue;
            }

            terrainRuntimeData.Add(new TerrainRuntimeData(item));
        }
    }

    void UpdateNodeBounds()
    {
        bool hasBounds = false;
        Bounds combinedBounds = default;
        for (int i = 0; i < terrainRuntimeData.Count; i++)
        {
            Terrain sourceTerrain = terrainRuntimeData[i].terrain;
            Vector3 terrainPosition = sourceTerrain.transform.position;
            Vector3 terrainSize = sourceTerrain.terrainData.size;
            Bounds terrainBounds = new Bounds(
                terrainPosition + new Vector3(terrainSize.x * 0.5f, terrainSize.y * 0.5f, terrainSize.z * 0.5f),
                terrainSize);

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

        nodeBounds = hasBounds
            ? combinedBounds
            : new Bounds(Vector3.zero, new Vector3(1000.0f, 1000.0f, 1000.0f));
    }

    void BuildTerrainTextureArrays()
    {
        ReleaseTerrainTextureArrays();
        heightMap = null;
        normalMap = null;

        if (terrainRuntimeData.Count == 0)
        {
            return;
        }

        RenderTexture firstHeightMap = terrainRuntimeData[0].terrain.terrainData.heightmapTexture;
        if (firstHeightMap == null)
        {
            return;
        }

        int width = firstHeightMap.width;
        int height = firstHeightMap.height;
        heightMapArray = new Texture2DArray(width, height, terrainRuntimeData.Count, TextureFormat.RGBAHalf, false, true)
        {
            name = "GPU Terrain Height Array",
            wrapMode = TextureWrapMode.Clamp,
            filterMode = firstHeightMap.filterMode,
            hideFlags = HideFlags.HideAndDontSave
        };
        normalMapArray = new Texture2DArray(width, height, terrainRuntimeData.Count, TextureFormat.ARGB32, false, true)
        {
            name = "GPU Terrain Normal Array",
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            hideFlags = HideFlags.HideAndDontSave
        };

        for (int i = 0; i < terrainRuntimeData.Count; i++)
        {
            TerrainRuntimeData runtimeData = terrainRuntimeData[i];
            TerrainData terrainData = runtimeData.terrain.terrainData;
            CopyTerrainHeightsToArray(terrainData, i, width, height);
            CopyTerrainNormalsToArray(terrainData, i, width, height);
        }

        heightMapArray.Apply(false, false);
        normalMapArray.Apply(false, false);
        heightMap = firstHeightMap;
        UpdateTerrainShaderArrays();
    }

    void CopyTerrainHeightsToArray(TerrainData terrainData, int slice, int width, int height)
    {
        Color[] colors = new Color[width * height];
        int index = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float u = width > 1 ? (float)x / (width - 1) : 0.0f;
                float v = height > 1 ? (float)y / (height - 1) : 0.0f;
                // Store normalized terrain-local height; runtime decodes with height01 * terrainData.size.y.
                float height01 = terrainData.GetInterpolatedHeight(u, v) / Mathf.Max(terrainData.size.y, 1e-5f);
                colors[index++] = new Color(height01, height01, height01, height01);
            }
        }

        heightMapArray.SetPixels(colors, slice, 0);
    }

    void CopyTerrainNormalsToArray(TerrainData terrainData, int slice, int width, int height)
    {
        Color[] colors = GenerateTerrainNormalColors(terrainData, width, height);
        normalMapArray.SetPixels(colors, slice, 0);
        if (slice == 0)
        {
            Texture2D normalTexture = new Texture2D(width, height, TextureFormat.ARGB32, false, true)
            {
                name = "GPU Terrain Normal",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave
            };
            normalTexture.SetPixels(colors);
            normalTexture.Apply(false, false);
            normalMap = normalTexture;
        }
    }

    Color[] GenerateTerrainNormalColors(TerrainData terrainData, int width, int height)
    {
        var colors = new Color[width * height];
        int index = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float u = width > 1 ? (float)x / (width - 1) : 0.0f;
                float v = height > 1 ? (float)y / (height - 1) : 0.0f;
                var normal = terrainData.GetInterpolatedNormal(u, v);
                colors[index++] = new Color(normal.z * 0.5f + 0.5f, normal.y * 0.5f + 0.5f, normal.x * 0.5f + 0.5f);
            }
        }

        return colors;
    }

    void ReleaseTerrainTextureArrays()
    {
        if (heightMapArray != null)
        {
            DestroyGeneratedObject(heightMapArray);
            heightMapArray = null;
        }

        if (normalMapArray != null)
        {
            DestroyGeneratedObject(normalMapArray);
            normalMapArray = null;
        }

        if (normalMap != null)
        {
            DestroyGeneratedObject(normalMap);
            normalMap = null;
        }
    }

    void DestroyGeneratedObject(Object generatedObject)
    {
        if (generatedObject == null || (generatedObject.hideFlags & HideFlags.HideAndDontSave) == 0)
        {
            return;
        }

        if (Application.isPlaying)
        {
            Destroy(generatedObject);
        }
        else
        {
            DestroyImmediate(generatedObject);
        }
    }

    void UpdateTerrainShaderArrays()
    {
        for (int i = 0; i < terrainParams.Length; i++)
        {
            terrainParams[i] = Vector4.zero;
            terrainOriginSizes[i] = Vector4.zero;
        }

        for (int i = 0; i < terrainRuntimeData.Count; i++)
        {
            Terrain sourceTerrain = terrainRuntimeData[i].terrain;
            Vector3 terrainPosition = sourceTerrain.transform.position;
            Vector3 terrainSize = sourceTerrain.terrainData.size;
            terrainParams[i] = new Vector4(terrainSize.x, terrainSize.y, terrainSize.z, terrainPosition.y);
            terrainOriginSizes[i] = new Vector4(terrainPosition.x, terrainPosition.z, terrainSize.x, terrainSize.z);
        }
    }

    void BindTerrainComputeProperties()
    {
        if (cullingComputeShader == null || heightMapArray == null)
        {
            return;
        }

        cullingComputeShader.SetTexture(cullTerrainKernel, TerrainHeightmapTextureArrayId, heightMapArray);
        cullingComputeShader.SetVectorArray(TerrainParamsId, terrainParams);
        cullingComputeShader.SetVectorArray(TerrainOriginSizesId, terrainOriginSizes);
        cullingComputeShader.SetInt(TerrainCountId, terrainRuntimeData.Count);
        cullingComputeShader.SetFloat("_TerrainHeightPadding", GetMaxTerrainHeightPadding());
    }

    float GetMaxTerrainHeightPadding()
    {
        float maxHeight = 0.0f;
        for (int i = 0; i < terrainRuntimeData.Count; i++)
        {
            maxHeight = Mathf.Max(maxHeight, terrainRuntimeData[i].terrain.terrainData.size.y);
        }

        return Mathf.Max(
            maxHeight * terrainHeightPaddingScale,
            Mathf.Max(1.0f, resolution) * terrainHeightPaddingResolutionScale);
    }

    /// <summary>
    /// Initialize terrain quadtree nodes.
    /// </summary>
    void GenerateTerrainNode()
    {
        rootNodes.Clear();
        for (int terrainIndex = 0; terrainIndex < terrainRuntimeData.Count; terrainIndex++)
        {
            Terrain sourceTerrain = terrainRuntimeData[terrainIndex].terrain;
            Vector3 terrainPos = sourceTerrain.transform.position;
            Vector3 terrainSize = sourceTerrain.terrainData.size;
            float4 rect = new float4(terrainPos.x, terrainPos.z, terrainSize.x, terrainSize.z);
            TerrainNode rootNode = new TerrainNode(rect);
            var childNode = new List<TerrainNode>();
            float patchSize = Mathf.Max(1.0f, resolution);
            for (var i = rect.x; i < rect.x + rect.z; i += patchSize)
            {
                for (var j = rect.y; j < rect.y + rect.w; j += patchSize)
                {
                    float nodeWidth = Mathf.Min(patchSize, rect.x + rect.z - i);
                    float nodeHeight = Mathf.Min(patchSize, rect.y + rect.w - j);
                    childNode.Add(new TerrainNode(new float4(i, j, nodeWidth, nodeHeight), LOD - 1, terrainIndex));
                }
            }

            rootNode.children = childNode.ToArray();
            rootNodes.Add(rootNode);
        }
    }

    void UpdateQuadTreeNode()
    {
        if (allNodeInfo == null)
            allNodeInfo = new List<NodeInfo>();
        else
            allNodeInfo.Clear();
        Vector3 camPos = camera.transform.position;
        Vector2 center = new Vector2(camPos.x, camPos.z);
        for (int rootIndex = 0; rootIndex < rootNodes.Count; rootIndex++)
        {
            rootNodes[rootIndex].CollectNodeInfo(center, allNodeInfo, lodDistance);
        }

        for (int i = 0; i < allNodeInfo.Count; i++)
        {
            var nodeInfo = allNodeInfo[i];
            var nodeCenter = new Vector2(nodeInfo.rect.x + nodeInfo.rect.z * 0.5f, nodeInfo.rect.y + nodeInfo.rect.w * 0.5f);
            var topNode = FindActiveTerrainNode(nodeCenter + new Vector2(0, nodeInfo.rect.w));
            var bottomNode = FindActiveTerrainNode(nodeCenter + new Vector2(0, -nodeInfo.rect.w));
            var leftNode = FindActiveTerrainNode(nodeCenter + new Vector2(-nodeInfo.rect.z, 0));
            var rightNode = FindActiveTerrainNode(nodeCenter + new Vector2(nodeInfo.rect.z, 0));
            var nei = new bool4(
                topNode != null && topNode.mip > nodeInfo.mip, 
                bottomNode != null && bottomNode.mip > nodeInfo.mip, 
                leftNode != null && leftNode.mip > nodeInfo.mip, 
                rightNode != null && rightNode.mip > nodeInfo.mip);
            nodeInfo.neighbor = (1 * (nei.x ? 1 : 0)) + ((1 << 1) * (nei.y ? 1 : 0)) + ((1 << 2) * (nei.z ? 1 : 0)) + ((1 << 3) * (nei.w ? 1 : 0));
            allNodeInfo[i] = nodeInfo;
        }
    }

    TerrainNode FindActiveTerrainNode(Vector2 center)
    {
        for (int i = 0; i < rootNodes.Count; i++)
        {
            TerrainNode activeNode = rootNodes[i].GetActiveNode(center);
            if (activeNode != null)
            {
                return activeNode;
            }
        }

        return null;
    }

    void UpdateComputeBuffer()
    {
        if (allNodeInfo == null || allNodeInfo.Count == 0 || instanceMesh == null || cullingComputeShader == null ||
            mat == null || heightMapArray == null || normalMapArray == null)
        {
            return;
        }

        if(allInstancePosBuffer == null)
            allInstancePosBuffer = new ComputeBuffer(allNodeInfo.Count, Marshal.SizeOf(typeof(NodeInfo)));
        else if(allInstancePosBuffer.count != allNodeInfo.Count)
        {
            allInstancePosBuffer.Release();
            allInstancePosBuffer = new ComputeBuffer(allNodeInfo.Count, Marshal.SizeOf(typeof(NodeInfo)));
        }
        allInstancePosBuffer.SetData(allNodeInfo);

        if (visibleInstancePosIDBuffer == null || visibleInstancePosIDBuffer.count != allNodeInfo.Count)
        {
            visibleInstancePosIDBuffer?.Release();
            visibleInstancePosIDBuffer = new ComputeBuffer(allNodeInfo.Count, sizeof(uint), ComputeBufferType.Append);
        }
        if (allInstancePosIDBuffer == null || allInstancePosIDBuffer.count != allNodeInfo.Count)
        {
            allInstancePosIDBuffer?.Release();
            allInstancePosIDBuffer = new ComputeBuffer(allNodeInfo.Count, sizeof(uint));
            uint[] ids = new uint[allNodeInfo.Count];
            for (int i = 0; i < ids.Length; i++)
            {
                ids[i] = (uint)i;
            }
            allInstancePosIDBuffer.SetData(ids);
        }

        if (argsBuffer == null)
            argsBuffer = new ComputeBuffer(1, args.Length * sizeof(uint), ComputeBufferType.IndirectArguments);
        args[0] = instanceMesh.GetIndexCount(0);
        args[1] = (uint)allNodeInfo.Count;
        args[2] = instanceMesh.GetIndexStart(0);
        args[3] = instanceMesh.GetBaseVertex(0);
        args[4] = 0;
        argsBuffer.SetData(args);

        if (depthArgsBuffer == null)
            depthArgsBuffer = new ComputeBuffer(1, args.Length * sizeof(uint), ComputeBufferType.IndirectArguments);
        depthArgsBuffer.SetData(args);

        if (hizStatsBuffer == null || hizStatsBuffer.count != HiZStatsCount)
        {
            hizStatsBuffer?.Release();
            hizStatsBuffer = new ComputeBuffer(HiZStatsCount, sizeof(uint));
        }

        if (visibleNodeInfoBuffer == null || visibleNodeInfoBuffer.count != allNodeInfo.Count)
        {
            visibleNodeInfoBuffer?.Release();
            visibleNodeInfoBuffer = new ComputeBuffer(allNodeInfo.Count, Marshal.SizeOf(typeof(NodeInfo)), ComputeBufferType.Append);
        }

        cullTerrainKernel = this.cullingComputeShader.FindKernel("CullTerrain");
        cullingComputeShader.SetBuffer(cullTerrainKernel, "_AllInstancesPosWSBuffer", allInstancePosBuffer);
        cullingComputeShader.SetBuffer(cullTerrainKernel, "_VisibleInstancesOnlyPosWSIDBuffer", visibleInstancePosIDBuffer);
        cullingComputeShader.SetBuffer(cullTerrainKernel, "result", visibleNodeInfoBuffer);
        cullingComputeShader.SetBuffer(cullTerrainKernel, "_HiZStatsBuffer", hizStatsBuffer);
        BindTerrainComputeProperties();
        cullingComputeShader.SetFloat("_FrustumPadding", frustumPadding);
        cullingComputeShader.SetFloat(HiZWorldDepthBiasId, hizDepthBias);
        cullingComputeShader.SetInt(UseReversedZId, SystemInfo.usesReversedZBuffer ? 1 : 0);
        mat.SetBuffer("_AllInstancesTransformBuffer", allInstancePosBuffer);
        ApplyMaterialState(showcaseCullingMode != GpuDrivenShowcaseCullingMode.None);

        if (depthTextureGenerator != null)
        {
            cullingComputeShader.SetInt("depthTextureSize", depthTextureGenerator.DepthTextureSize);
        }
        cullingComputeShader.SetBool("isOpenGL", IsOpenGLClipSpace());
        cullingComputeShader.SetInt("_InstanceCount", allNodeInfo.Count);
        cullingComputeShader.SetBool("_UseHiZ", IsHiZReady());
        cullingComputeShader.SetBool("_WriteDebugResult", DebugGizmos);
    }

    private void Update()
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

        if (terrainRuntimeData.Count == 0 || heightMapArray == null || normalMapArray == null)
        {
            return;
        }

        BindTerrainRenderProperties();

        if (ShouldRebuildVisibleTerrainNodes())
        {
            Profiler.BeginSample(profilerTag);
            RebuildVisibleTerrainNodes();
            Profiler.EndSample();
        }

        if (allNodeInfo == null || allNodeInfo.Count == 0 || allInstancePosBuffer == null ||
            visibleInstancePosIDBuffer == null || argsBuffer == null)
        {
            return;
        }

        bool useCulling = showcaseCullingMode != GpuDrivenShowcaseCullingMode.None;
        bool useHiz = IsHiZReady();
        lastHizActive = useHiz;
        if (!useHiz)
        {
            ClearHiZStats();
        }
        UpdateHiZGeneratorState(useHiz);
        BindHiZTextureIfReady();

        Matrix4x4 v = camera.worldToCameraMatrix;
        Matrix4x4 p = GL.GetGPUProjectionMatrix(camera.projectionMatrix, false);
        Matrix4x4 vp = p * v;
        visibleInstancePosIDBuffer.SetCounterValue(0);
        if (visibleNodeInfoBuffer != null)
            visibleNodeInfoBuffer.SetCounterValue(0);
        cullingComputeShader.SetMatrix("_VPMatrix", vp);
        cullingComputeShader.SetVector(HiZCameraPositionWSId, camera.transform.position);
        cullingComputeShader.SetInt("_InstanceCount", allNodeInfo.Count);
        if (depthTextureGenerator != null)
        {
            cullingComputeShader.SetInt(DepthTextureSizeId, depthTextureGenerator.DepthTextureSize);
        }
        cullingComputeShader.SetBool("_UseHiZ", useHiz);
        cullingComputeShader.SetBool("_WriteDebugResult", DebugGizmos);
        ApplyMaterialState(useCulling);

        if (useCulling)
        {
            if (hizStatsBuffer != null)
            {
                hizStatsBuffer.SetData(hizStatsReset);
            }
            cullingComputeShader.Dispatch(cullTerrainKernel, Mathf.CeilToInt(allNodeInfo.Count / 64f), 1, 1);
            ComputeBuffer.CopyCount(visibleInstancePosIDBuffer, argsBuffer, 4);
        }
        else
        {
            args[1] = (uint)allNodeInfo.Count;
            argsBuffer.SetData(args);
            lastVisiblePatchCount = args[1];
            ClearHiZStats();
        }

        bool readBackStats = DebugGizmos || Time.unscaledTime >= nextStatsReadbackTime;
        if (readBackStats)
        {
            nextStatsReadbackTime = Time.unscaledTime + 0.25f;
        }

        if (DebugGizmos)
        {
            argsBuffer.GetData(args);
            lastVisiblePatchCount = args[1];
            if (useHiz && hizStatsBuffer != null)
            {
                hizStatsBuffer.GetData(hizStats);
            }
            int visibleCount = (int)args[1];
            if (visibleNodeInfoArray == null || visibleNodeInfoArray.Length != visibleCount)
                visibleNodeInfoArray = new NodeInfo[visibleCount];
            if (!useCulling && allNodeInfo != null)
            {
                visibleNodeInfoArray = allNodeInfo.ToArray();
            }
            else if (visibleCount > 0 && visibleNodeInfoBuffer != null)
            {
                visibleNodeInfoBuffer.GetData(visibleNodeInfoArray);
            }
        }
        else if (readBackStats)
        {
            argsBuffer.GetData(args);
            lastVisiblePatchCount = args[1];
            if (useHiz && hizStatsBuffer != null)
            {
                hizStatsBuffer.GetData(hizStats);
            }
        }
        Graphics.DrawMeshInstancedIndirect(instanceMesh, 0, mat, nodeBounds, argsBuffer, 0, null,
            ShadowCastingMode.Off, receiveShadowMap, gameObject.layer, camera);
        DrawShadowMap();
    }

    void ClearHiZStats()
    {
        for (int i = 0; i < hizStats.Length; i++)
        {
            hizStats[i] = 0;
        }
    }

    bool ShouldRebuildVisibleTerrainNodes()
    {
        if (forceLodRebuild || allNodeInfo == null || allInstancePosBuffer == null)
        {
            return true;
        }

        Vector2 cameraXZ = new Vector2(camera.transform.position.x, camera.transform.position.z);
        float sqrThreshold = lodRebuildDistanceThreshold * lodRebuildDistanceThreshold;
        return (cameraXZ - lastLodBuildCameraXZ).sqrMagnitude >= sqrThreshold;
    }

    void RebuildVisibleTerrainNodes()
    {
        UpdateQuadTreeNode();
        UpdateComputeBuffer();
        lastLodBuildCameraXZ = new Vector2(camera.transform.position.x, camera.transform.position.z);
        forceLodRebuild = false;
    }



    private void OnDisable()
    {
        activeTerrains.Remove(this);

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

        ReleaseTerrainTextureArrays();
    }

    public bool DrawHiZDepth(CommandBuffer cmd, Material depthMaterial, int shaderPass)
    {
        if (!writeTerrainDepthToHiZ || cmd == null || depthMaterial == null || terrainRuntimeData.Count == 0 || instanceMesh == null)
        {
            return false;
        }

        if (allInstancePosBuffer == null || allInstancePosIDBuffer == null || depthArgsBuffer == null)
        {
            return false;
        }

        if (heightMapArray == null || normalMapArray == null)
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

    bool IsHiZReady()
    {
        return showcaseCullingMode.UsesHiZ() &&
               depthTextureGenerator != null &&
               depthTextureGenerator.DepthTexture != null;
    }

    void UpdateHiZGeneratorState(bool useCulling)
    {
        if (depthTextureGenerator != null)
        {
            depthTextureGenerator.useHiz = useCulling && depthTextureGenerator.DepthTexture != null;
        }
    }

    void BindHiZTextureIfReady()
    {
        if (depthTextureGenerator != null && depthTextureGenerator.DepthTexture != null)
        {
            cullingComputeShader.SetTexture(cullTerrainKernel, "_HiZMap", depthTextureGenerator.DepthTexture);
        }
    }

    void BindTerrainRenderProperties()
    {
        if (heightMapArray == null || normalMapArray == null)
        {
            return;
        }

        UpdateTerrainShaderArrays();
        Shader.SetGlobalTexture(TerrainHeightmapTextureArrayId, heightMapArray);
        Shader.SetGlobalTexture(TerrainNormalmapTextureArrayId, normalMapArray);
        Shader.SetGlobalVectorArray(TerrainParamsId, terrainParams);
        Shader.SetGlobalVectorArray(TerrainOriginSizesId, terrainOriginSizes);
        Shader.SetGlobalInt(TerrainCountId, terrainRuntimeData.Count);

        if (mat != null)
        {
            BindTerrainRenderProperties(mat);
        }
    }

    void BindTerrainRenderProperties(Material targetMaterial)
    {
        if (targetMaterial == null || heightMapArray == null || normalMapArray == null)
        {
            return;
        }

        targetMaterial.SetTexture(TerrainHeightmapTextureArrayId, heightMapArray);
        targetMaterial.SetTexture(TerrainNormalmapTextureArrayId, normalMapArray);
        targetMaterial.SetVectorArray(TerrainParamsId, terrainParams);
        targetMaterial.SetVectorArray(TerrainOriginSizesId, terrainOriginSizes);
        targetMaterial.SetInt(TerrainCountId, terrainRuntimeData.Count);
    }

    void BindTerrainRenderProperties(MaterialPropertyBlock propertyBlock)
    {
        if (propertyBlock == null || heightMapArray == null || normalMapArray == null)
        {
            return;
        }

        propertyBlock.SetTexture(TerrainHeightmapTextureArrayId, heightMapArray);
        propertyBlock.SetTexture(TerrainNormalmapTextureArrayId, normalMapArray);
        propertyBlock.SetVectorArray(TerrainParamsId, terrainParams);
        propertyBlock.SetVectorArray(TerrainOriginSizesId, terrainOriginSizes);
        propertyBlock.SetInt(TerrainCountId, terrainRuntimeData.Count);
    }

    void ApplyMaterialState(bool useVisibleIdBuffer)
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

    void DrawShadowMap()
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

    #region Debug

    NodeInfo[] visibleNodeInfoArray;
    public bool DebugGizmos;
    ComputeBuffer visibleNodeInfoBuffer;

    private void OnDrawGizmos()
    {
        if(DebugGizmos)
        {
            if (camera != null)
            {
                Gizmos.color = Color.yellow;
                Gizmos.matrix = Matrix4x4.TRS(camera.transform.position, camera.transform.rotation, Vector3.one);
                Gizmos.DrawFrustum(Vector3.zero, camera.fieldOfView, camera.farClipPlane, camera.nearClipPlane, camera.aspect);
                Gizmos.matrix = Matrix4x4.identity;
            }

            if(allNodeInfo!= null && visibleNodeInfoArray != null)
            {
                foreach (var nodeInfo in visibleNodeInfoArray)
                {
                    var rect = nodeInfo.rect;
                    Gizmos.color = Color.Lerp(Color.red, Color.blue, (float)nodeInfo.mip / (LOD - 1));
                    Gizmos.DrawWireCube(new Vector3(rect.x + rect.z * 0.5f, 0, rect.y + rect.w * 0.5f), new Vector3(rect.z, 10, rect.w));
                }
            }
        }
    }

    #endregion

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
    }

    public void SetTerrainColorDebug(bool enabled)
    {
        terrainColorDebug = enabled;
        ApplyMaterialState(showcaseCullingMode != GpuDrivenShowcaseCullingMode.None);
    }

    public void CollectShowcaseStats(ref GpuDrivenShowcaseStats stats)
    {
        stats.terrainPatchCount += allNodeInfo != null ? allNodeInfo.Count : 0;
        stats.terrainVisiblePatchCount += (int)lastVisiblePatchCount;
        stats.terrainHiZTestedPatchCount += (int)hizStats[0];
        stats.terrainHiZRejectedPatchCount += (int)hizStats[1];
        stats.terrainHiZSkippedPatchCount += (int)hizStats[2];
        stats.terrainCullingDispatchedPatchCount += (int)hizStats[3];
        stats.terrainFrustumVisiblePatchCount += (int)hizStats[4];
        stats.terrainFrustumRejectedPatchCount += (int)hizStats[5];
        stats.terrainDepthOccluderEnabled |= writeTerrainDepthToHiZ;
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

    static bool IsOpenGLClipSpace()
    {
        var deviceType = SystemInfo.graphicsDeviceType;
        return deviceType == UnityEngine.Rendering.GraphicsDeviceType.OpenGLCore ||
               deviceType == UnityEngine.Rendering.GraphicsDeviceType.OpenGLES2 ||
               deviceType == UnityEngine.Rendering.GraphicsDeviceType.OpenGLES3;
    }

    private struct TerrainRuntimeData
    {
        public Terrain terrain;

        public TerrainRuntimeData(Terrain terrain)
        {
            this.terrain = terrain;
        }
    }
}
