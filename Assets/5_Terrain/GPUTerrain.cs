using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

public class GPUTerrain : MonoBehaviour
{
    [Header("Data")]
    [SerializeField]Terrain terrain;
    [SerializeField] Mesh instanceMesh;
    Bounds nodeBounds = new Bounds(Vector3.zero, new Vector3(100.0f, 100.0f, 100.0f));

    [Header("GPU Terrain Paramas")]
    public int resolution = 64;
    public int LOD = 4;
    public float[] lodDistance;

    [SerializeField]RenderTexture heightMap;
    [SerializeField]Texture2D normalMap;

    //Terrain Quad Node
    TerrainNode rootNode;
    List<NodeInfo> allNodeInfo;

    #region Compute Shader
    ComputeBuffer allInstancePosBuffer;
    ComputeBuffer visibleInstancePosIDBuffer;
    ComputeBuffer argsBuffer;
    uint[] args = new uint[5] { 0, 0, 0, 0, 0 };
    ComputeBuffer shadowBuffer;

    [SerializeField]ComputeShader cullingComputeShader;
    int cullTerrainKernel;
    int cullTerrainShadowKernel;

    [SerializeField]Material mat;
    #endregion

    const string profilerTag = "Gpu Terrain";

    Camera camera;
    public DepthTextureGenerator depthTextureGenerator;

    private void Start()
    {
        
    }

    private void OnEnable()
    {
        camera = Camera.main;
        GenerateTerrainNode();
        GetTerrainTexture(out heightMap, out normalMap);
        UpdateQuadTreeNode();
        UpdateComputeBuffer();

        Shader.SetGlobalTexture("_TerrainHeightmapTexture", heightMap);
        Shader.SetGlobalTexture("_TerrainNormalmapTexture", normalMap);
        Shader.SetGlobalVector("terrainParam", terrain.terrainData.size);
    }

    void GetTerrainTexture(out RenderTexture heightMap, out Texture2D normalMap)
    {
        heightMap = terrain.terrainData.heightmapTexture;
        int width = heightMap.width;
        int height = heightMap.height;
        normalMap = new Texture2D(width, height, TextureFormat.ARGB32, -1, true);

        var colors = new Color[width * height];
        int index = 0;
        for (int i = 0; i < width; i++)
        {
            for (int j = 0; j < height; j++)
            {
                var normal = terrain.terrainData.GetInterpolatedNormal((float)i / width, (float)j / height);
                //Todo:世界坐标下的NormalMap？
                colors[index++] = new Color(normal.z * 0.5f + 0.5f, normal.y * 0.5f + 0.5f, normal.x * 0.5f + 0.5f);
            }
        }
        normalMap.SetPixelData(colors, 0);
        normalMap.Apply();        
    }

    /// <summary>
    /// 初始化Terrain QuadTreeNode
    /// </summary>
    void GenerateTerrainNode()
    {
        if (rootNode == null)
        {
            Vector3 terrainPos = terrain.transform.position;
            Vector3 terrainSize = terrain.terrainData.size;
            float4 rect = new float4(terrainPos.x, terrainPos.z, terrainSize.x, terrainSize.z);
            rootNode = new TerrainNode(rect);
            var childNode = new List<TerrainNode>();
            for (var i = rect.x; i < rect.x + rect.z; i += resolution)
            {
                for (var j = rect.y; j < rect.y + rect.w; j += resolution)
                {
                    childNode.Add(new TerrainNode(new float4(i, j, resolution, resolution), LOD - 1));
                }
            }
            rootNode.children = childNode.ToArray();   
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
        rootNode.CollectNodeInfo(center, allNodeInfo, lodDistance);
        for (int i = 0; i < allNodeInfo.Count; i++)
        {
            var nodeInfo = allNodeInfo[i];
            var nodeCenter = new Vector2(nodeInfo.rect.x + nodeInfo.rect.z * 0.5f, nodeInfo.rect.y + nodeInfo.rect.w * 0.5f);
            var topNode = rootNode.GetActiveNode(nodeCenter + new Vector2(0, nodeInfo.rect.w));
            var bottomNode = rootNode.GetActiveNode(nodeCenter + new Vector2(0, -nodeInfo.rect.w));
            var leftNode = rootNode.GetActiveNode(nodeCenter + new Vector2(-nodeInfo.rect.z, 0));
            var rightNode = rootNode.GetActiveNode(nodeCenter + new Vector2(nodeInfo.rect.z, 0));
            var nei = new bool4(
                topNode != null && topNode.mip > nodeInfo.mip, 
                bottomNode != null && bottomNode.mip > nodeInfo.mip, 
                leftNode != null && leftNode.mip > nodeInfo.mip, 
                rightNode != null && rightNode.mip > nodeInfo.mip);
            nodeInfo.neighbor = (1 * (nei.x ? 1 : 0)) + ((1 << 1) * (nei.y ? 1 : 0)) + ((1 << 2) * (nei.z ? 1 : 0)) + ((1 << 3) * (nei.w ? 1 : 0));
            allNodeInfo[i] = nodeInfo;
        }
    }

    void UpdateComputeBuffer()
    {
        if(allInstancePosBuffer == null)
            allInstancePosBuffer = new ComputeBuffer(allNodeInfo.Count, Marshal.SizeOf(typeof(NodeInfo)));
        else if(allInstancePosBuffer.count != allNodeInfo.Count)
        {
            allInstancePosBuffer.Release();
            allInstancePosBuffer = new ComputeBuffer(allNodeInfo.Count, Marshal.SizeOf(typeof(NodeInfo)));
        }
        allInstancePosBuffer.SetData(allNodeInfo);

        if (visibleInstancePosIDBuffer == null)
            visibleInstancePosIDBuffer = new ComputeBuffer(allNodeInfo.Count, sizeof(uint), ComputeBufferType.Append);

        if (argsBuffer != null)
            argsBuffer.Release();
        argsBuffer = new ComputeBuffer(1, args.Length * sizeof(uint), ComputeBufferType.IndirectArguments);
        args[0] = instanceMesh.GetIndexCount(0);
        args[1] = (uint)allNodeInfo.Count;
        args[2] = instanceMesh.GetIndexStart(0);
        args[3] = instanceMesh.GetBaseVertex(0);
        args[4] = 0;
        argsBuffer.SetData(args);

        if (shadowBuffer != null)
            shadowBuffer.Release();
        shadowBuffer = new ComputeBuffer(1, args.Length * sizeof(uint), ComputeBufferType.IndirectArguments);
        shadowBuffer.SetData(args);

        if(Debug)
        {
            if (visiableNodeInfoBuffer == null)
                visiableNodeInfoBuffer = new ComputeBuffer(allNodeInfo.Count, Marshal.SizeOf(typeof(NodeInfo)), ComputeBufferType.Append);
        }

        cullTerrainKernel = this.cullingComputeShader.FindKernel("CullTerrain");
        cullTerrainShadowKernel = this.cullingComputeShader.FindKernel("CullTerrainShadow");
        cullingComputeShader.SetBuffer(cullTerrainKernel, "_AllInstancesPosWSBuffer", allInstancePosBuffer);
        cullingComputeShader.SetBuffer(cullTerrainKernel, "_VisibleInstancesOnlyPosWSIDBuffer", visibleInstancePosIDBuffer);
        if(Debug)
            cullingComputeShader.SetBuffer(cullTerrainKernel, "result", visiableNodeInfoBuffer);
        cullingComputeShader.SetTexture(cullTerrainKernel, "_HeightMap", heightMap);
        cullingComputeShader.SetBuffer(cullTerrainShadowKernel, "_AllInstancesPosWSBuffer", allInstancePosBuffer);
        cullingComputeShader.SetBuffer(cullTerrainShadowKernel, "_VisibleInstancesOnlyPosWSIDBuffer", visibleInstancePosIDBuffer);
        cullingComputeShader.SetTexture(cullTerrainShadowKernel, "_HeightMap", heightMap);
        cullingComputeShader.SetFloat("_TerrainHeightSize", 2 * terrain.terrainData.size.y);
        mat.SetBuffer("_AllInstancesTransformBuffer", allInstancePosBuffer);
        mat.SetBuffer("_VisibleInstanceIDBuffer", visibleInstancePosIDBuffer);
    }

    private void Update()
    {
        var hizRT = depthTextureGenerator.DepthTexture;
        cullingComputeShader.SetTexture(cullTerrainKernel, "_HiZMap", hizRT);
        cullingComputeShader.SetVector("_HizSize", new Vector4(hizRT.width, hizRT.height, 0, 0));
        Matrix4x4 v = camera.worldToCameraMatrix;
        Matrix4x4 p = camera.projectionMatrix;
        Matrix4x4 vp = p * v;
        visibleInstancePosIDBuffer.SetCounterValue(0);
        if(Debug)
            visiableNodeInfoBuffer.SetCounterValue(0);
        cullingComputeShader.SetMatrix("_VPMatrix", vp);
        cullingComputeShader.Dispatch(cullTerrainKernel, Mathf.CeilToInt(allNodeInfo.Count / 64f), 1, 1);
        ComputeBuffer.CopyCount(visibleInstancePosIDBuffer, argsBuffer, 4);

        if (Debug)
        {
            argsBuffer.GetData(args);
            if (visibleNodeInfoArray == null)
                visibleNodeInfoArray = new NodeInfo[args[1]];
            visiableNodeInfoBuffer.GetData(visibleNodeInfoArray);
        }
        Graphics.DrawMeshInstancedIndirect(instanceMesh, 0, mat, nodeBounds, argsBuffer);
    }

    private void OnDisable()
    {
        allInstancePosBuffer?.Release();
        allInstancePosBuffer = null;

        visibleInstancePosIDBuffer?.Release();
        visibleInstancePosIDBuffer = null;
        
        argsBuffer?.Release();
        argsBuffer = null;

        shadowBuffer?.Release();
        shadowBuffer = null;

        visiableNodeInfoBuffer?.Release();
        visiableNodeInfoBuffer = null;
    }

    #region Debug

    NodeInfo[] visibleNodeInfoArray;
    public bool Debug;
    ComputeBuffer visiableNodeInfoBuffer;

    private void OnDrawGizmos()
    {
        if(Debug)
        {
            if(allNodeInfo!= null)
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
}
