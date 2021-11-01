using System.Collections;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

public class GPUTerrain : MonoBehaviour
{
    [Header("Data")]
    [SerializeField]Terrain terrain;


    [Header("GPU Terrain Paramas")]
    public int LOD = 4;
    public int resolution = 64;
    public float[] lodDistance;

    [SerializeField]RenderTexture heightMap;
    [SerializeField]Texture2D normalMap;

    //Terrain Quad Node
    TerrainNode rootNode;
    List<NodeInfo> allNodeInfo;


    private void Start()
    {
        
    }

    private void OnEnable()
    {
        GenerateTerrainNode();
        GetTerrainTexture(out heightMap, out normalMap);
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
        if(rootNode == null)
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
            lodDistance = new float[LOD];
            for (int i = 0; i < LOD; i++)
            {
                lodDistance[i] = 100 * Mathf.Pow(2, i);
            }
        }
    }

    

    void UpdateQuadTreeNode()
    {
        allNodeInfo = new List<NodeInfo>();


    }

    private void Update()
    {
        
    }
}
