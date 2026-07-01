using System;
using System.Runtime.InteropServices;
using UnityEngine;

[CreateAssetMenu(fileName = "GpuTerrainBakedData", menuName = "GPU Driven/Terrain Baked Data")]
public sealed class GpuTerrainBakedData : ScriptableObject
{
    public const int MaxTerrainCount = 64;
    public const int MaxTerrainLayerCount = 64;
    private const int CurrentDataVersion = 3;
    private const int MinimumSupportedDataVersion = 2;

    [SerializeField] private int dataVersion;
    [SerializeField] private float patchSize = 64.0f;
    [SerializeField] private int lodCount = 4;
    [SerializeField, HideInInspector] private TerrainTileInfo[] terrains = Array.Empty<TerrainTileInfo>();
    [SerializeField, HideInInspector] private BakedNode[] nodes = Array.Empty<BakedNode>();
    [SerializeField, HideInInspector] private int[] rootNodeIndices = Array.Empty<int>();
    [SerializeField, HideInInspector] private Texture2DArray heightMapArray;
    [SerializeField, HideInInspector] private Texture2DArray normalMapArray;
    [SerializeField, HideInInspector] private Texture2DArray controlMapArray;
    [SerializeField, HideInInspector] private Texture2DArray layerDiffuseArray;
    [SerializeField, HideInInspector] private Texture2DArray layerNormalArray;
    [SerializeField, HideInInspector] private Texture2DArray layerMaskArray;
    [SerializeField, HideInInspector] private Vector4[] terrainLayerIndices = Array.Empty<Vector4>();
    [SerializeField, HideInInspector] private Vector4[] layerTileSizeOffsets = Array.Empty<Vector4>();
    [SerializeField, HideInInspector] private Vector4[] layerPbrParams = Array.Empty<Vector4>();

    public float PatchSize => patchSize;
    public int LodCount => lodCount;
    public int TerrainCount => terrains != null ? terrains.Length : 0;
    public int NodeCount => nodes != null ? nodes.Length : 0;
    public int RootNodeCount => rootNodeIndices != null ? rootNodeIndices.Length : 0;
    public TerrainTileInfo[] Terrains => terrains;
    public BakedNode[] Nodes => nodes;
    public int[] RootNodeIndices => rootNodeIndices;
    public Texture2DArray HeightMapArray => heightMapArray;
    public Texture2DArray NormalMapArray => normalMapArray;
    public Texture2DArray ControlMapArray => controlMapArray;
    public Texture2DArray LayerDiffuseArray => layerDiffuseArray;
    public Texture2DArray LayerNormalArray => layerNormalArray;
    public Texture2DArray LayerMaskArray => layerMaskArray;
    public Vector4[] TerrainLayerIndices => terrainLayerIndices;
    public Vector4[] LayerTileSizeOffsets => layerTileSizeOffsets;
    public Vector4[] LayerPbrParams => layerPbrParams;
    public int LayerCount => layerTileSizeOffsets != null ? layerTileSizeOffsets.Length : 0;

    public bool IsValid => dataVersion >= MinimumSupportedDataVersion &&
                           dataVersion <= CurrentDataVersion &&
                           terrains != null && terrains.Length > 0 &&
                           terrains.Length <= MaxTerrainCount &&
                           nodes != null && nodes.Length > 0 &&
                           rootNodeIndices != null && rootNodeIndices.Length > 0 &&
                           heightMapArray != null &&
                           normalMapArray != null &&
                           heightMapArray.depth >= terrains.Length &&
                           normalMapArray.depth >= terrains.Length;

    public bool HasLayerData => IsValid &&
                                controlMapArray != null &&
                                layerDiffuseArray != null &&
                                layerNormalArray != null &&
                                layerMaskArray != null &&
                                terrainLayerIndices != null &&
                                layerTileSizeOffsets != null &&
                                layerPbrParams != null &&
                                controlMapArray.depth >= TerrainCount &&
                                layerDiffuseArray.depth >= LayerCount &&
                                layerNormalArray.depth >= LayerCount &&
                                layerMaskArray.depth >= LayerCount &&
                                terrainLayerIndices.Length >= TerrainCount &&
                                layerPbrParams.Length >= LayerCount &&
                                LayerCount > 0 &&
                                LayerCount <= MaxTerrainLayerCount;

    public void SetData(
        float patchSize,
        int lodCount,
        TerrainTileInfo[] terrainInfos,
        BakedNode[] bakedNodes,
        int[] roots,
        Texture2DArray bakedHeightMapArray,
        Texture2DArray bakedNormalMapArray,
        Texture2DArray bakedControlMapArray = null,
        Texture2DArray bakedLayerDiffuseArray = null,
        Texture2DArray bakedLayerNormalArray = null,
        Texture2DArray bakedLayerMaskArray = null,
        Vector4[] bakedTerrainLayerIndices = null,
        Vector4[] bakedLayerTileSizeOffsets = null,
        Vector4[] bakedLayerPbrParams = null)
    {
        dataVersion = CurrentDataVersion;
        this.patchSize = Mathf.Max(1.0f, patchSize);
        this.lodCount = Mathf.Max(1, lodCount);
        terrains = terrainInfos ?? Array.Empty<TerrainTileInfo>();
        nodes = bakedNodes ?? Array.Empty<BakedNode>();
        rootNodeIndices = roots ?? Array.Empty<int>();
        heightMapArray = bakedHeightMapArray;
        normalMapArray = bakedNormalMapArray;
        controlMapArray = bakedControlMapArray;
        layerDiffuseArray = bakedLayerDiffuseArray;
        layerNormalArray = bakedLayerNormalArray;
        layerMaskArray = bakedLayerMaskArray;
        terrainLayerIndices = bakedTerrainLayerIndices ?? Array.Empty<Vector4>();
        layerTileSizeOffsets = bakedLayerTileSizeOffsets ?? Array.Empty<Vector4>();
        layerPbrParams = bakedLayerPbrParams ?? Array.Empty<Vector4>();
    }

    [Serializable]
    public struct TerrainTileInfo
    {
        public Vector3 worldOrigin;
        public Vector3 size;

        public TerrainTileInfo(Vector3 worldOrigin, Vector3 size)
        {
            this.worldOrigin = worldOrigin;
            this.size = size;
        }

        public Vector4 TerrainParams => new Vector4(size.x, size.y, size.z, worldOrigin.y);
        public Vector4 OriginSize => new Vector4(worldOrigin.x, worldOrigin.z, size.x, size.z);
    }

    [Serializable]
    public struct BakedNode
    {
        public Vector4 rect;
        public Vector2 heightMinMax;
        public int mip;
        public int terrainIndex;
        public int parentIndex;
        public int childStart;
        public int childCount;
        public int childIndex0;
        public int childIndex1;
        public int childIndex2;
        public int childIndex3;

        public BakedNode(
            Vector4 rect,
            Vector2 heightMinMax,
            int mip,
            int terrainIndex,
            int parentIndex,
            int childStart,
            int childCount,
            int childIndex0,
            int childIndex1,
            int childIndex2,
            int childIndex3)
        {
            this.rect = rect;
            this.heightMinMax = heightMinMax;
            this.mip = mip;
            this.terrainIndex = terrainIndex;
            this.parentIndex = parentIndex;
            this.childStart = childStart;
            this.childCount = childCount;
            this.childIndex0 = childIndex0;
            this.childIndex1 = childIndex1;
            this.childIndex2 = childIndex2;
            this.childIndex3 = childIndex3;
        }

        public Vector2 Center => new Vector2(rect.x + rect.z * 0.5f, rect.y + rect.w * 0.5f);

        public int GetChildIndex(int child)
        {
            switch (child)
            {
                case 0:
                    return childIndex0;
                case 1:
                    return childIndex1;
                case 2:
                    return childIndex2;
                case 3:
                    return childIndex3;
                default:
                    return -1;
            }
        }

        public bool Contains(Vector2 point)
        {
            return point.x >= rect.x && point.x <= rect.x + rect.z &&
                   point.y >= rect.y && point.y <= rect.y + rect.w;
        }
    }
}

[StructLayout(LayoutKind.Sequential)]
public struct GpuTerrainNodeInfo
{
    public Vector4 rect;
    public Vector2 heightMinMax;
    public int mip;
    public int neighbor;
    public int terrainIndex;
    public int padding;

    public GpuTerrainNodeInfo(Vector4 rect, Vector2 heightMinMax, int mip, int terrainIndex)
    {
        this.rect = rect;
        this.heightMinMax = heightMinMax;
        this.mip = mip;
        neighbor = 0;
        this.terrainIndex = terrainIndex;
        padding = 0;
    }
}
