using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public sealed class GpuTerrainBakedDataEditor : EditorWindow
{
    private const string DefaultAssetPath = "Assets/5_Terrain/GpuTerrainBakedData.asset";
    private const string PatchSizeKey = "GpuDrivenTerrainBake.PatchSize";
    private const string LodCountKey = "GpuDrivenTerrainBake.LodCount";
    private const string OutputPathKey = "GpuDrivenTerrainBake.OutputPath";
    private const string AutoAssignKey = "GpuDrivenTerrainBake.AutoAssign";
    private const string SourceModeKey = "GpuDrivenTerrainBake.SourceMode";
    private const string HeightMapArrayName = "GPU Terrain Height Map Array";
    private const string NormalMapArrayName = "GPU Terrain Normal Map Array";
    private const string ControlMapArrayName = "GPU Terrain Control Map Array";
    private const string LayerDiffuseArrayName = "GPU Terrain Layer Diffuse Array";
    private const string LayerNormalArrayName = "GPU Terrain Layer Normal Array";
    private const string LayerMaskArrayName = "GPU Terrain Layer Mask Array";
    private const TextureFormat HeightMapArrayFormat = TextureFormat.RHalf;
    private const TextureFormat NormalMapArrayFormat = TextureFormat.RGB24;
    private const TextureFormat ControlMapArrayFormat = TextureFormat.RGBA32;
    private const TextureFormat LayerDiffuseArrayFormat = TextureFormat.RGBA32;
    private const TextureFormat LayerNormalArrayFormat = TextureFormat.RGBA32;
    private const TextureFormat LayerMaskArrayFormat = TextureFormat.RGBA32;
    private const int LayerTextureSize = 1024;

    private SourceMode sourceMode;
    private float patchSize = 64.0f;
    private int lodCount = 4;
    private string outputPath = DefaultAssetPath;
    private bool autoAssignToSceneRenderers = true;
    private GpuTerrainBakedData outputAsset;

    private enum SourceMode
    {
        AllSceneTerrains,
        SelectedTerrains
    }

    [MenuItem("GPU Driven Showcase/Terrain/Bake Terrain Data...")]
    public static void OpenWindow()
    {
        GpuTerrainBakedDataEditor window = GetWindow<GpuTerrainBakedDataEditor>("GPU Terrain Bake");
        window.minSize = new Vector2(420.0f, 260.0f);
        window.Show();
    }

    [MenuItem("GPU Driven Showcase/Terrain/Bake Selected Terrains")]
    public static void BakeSelectedTerrains()
    {
        Terrain[] terrains = Selection.GetFiltered<Terrain>(SelectionMode.Editable | SelectionMode.ExcludePrefab);
        BakeTerrainsWithSaveDialog(terrains);
    }

    [MenuItem("GPU Driven Showcase/Terrain/Bake All Scene Terrains")]
    public static void BakeAllSceneTerrains()
    {
        Terrain[] terrains = Object.FindObjectsOfType<Terrain>(true);
        BakeTerrainsWithSaveDialog(terrains);
    }

    private void OnEnable()
    {
        patchSize = EditorPrefs.GetFloat(PatchSizeKey, 64.0f);
        lodCount = EditorPrefs.GetInt(LodCountKey, 4);
        outputPath = EditorPrefs.GetString(OutputPathKey, DefaultAssetPath);
        autoAssignToSceneRenderers = EditorPrefs.GetBool(AutoAssignKey, true);
        sourceMode = (SourceMode)EditorPrefs.GetInt(SourceModeKey, (int)SourceMode.AllSceneTerrains);
        outputAsset = AssetDatabase.LoadAssetAtPath<GpuTerrainBakedData>(outputPath);
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField("Terrain Source", EditorStyles.boldLabel);
        EditorGUI.BeginChangeCheck();
        sourceMode = (SourceMode)EditorGUILayout.EnumPopup("Source", sourceMode);
        Terrain[] terrains = GetSourceTerrains(sourceMode);
        EditorGUILayout.LabelField("Terrain Count", terrains.Length.ToString());

        GUILayout.Space(8.0f);
        EditorGUILayout.LabelField("Bake Settings", EditorStyles.boldLabel);
        patchSize = Mathf.Max(1.0f, EditorGUILayout.FloatField("Root Patch Size", patchSize));
        lodCount = Mathf.Max(1, EditorGUILayout.IntField("LOD Levels", lodCount));
        autoAssignToSceneRenderers = EditorGUILayout.Toggle("Assign To GPUTerrain", autoAssignToSceneRenderers);

        GUILayout.Space(8.0f);
        EditorGUILayout.LabelField("Output", EditorStyles.boldLabel);
        GpuTerrainBakedData selectedAsset = (GpuTerrainBakedData)EditorGUILayout.ObjectField(
            "Output Asset",
            outputAsset,
            typeof(GpuTerrainBakedData),
            false);
        if (selectedAsset != outputAsset)
        {
            outputAsset = selectedAsset;
            string selectedPath = outputAsset != null ? AssetDatabase.GetAssetPath(outputAsset) : string.Empty;
            if (!string.IsNullOrEmpty(selectedPath))
            {
                outputPath = selectedPath;
            }
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            outputPath = EditorGUILayout.TextField("Asset Path", outputPath);
            if (GUILayout.Button("Browse", GUILayout.Width(72.0f)))
            {
                string selectedPath = EditorUtility.SaveFilePanelInProject(
                    "Save GPU Terrain Baked Data",
                    Path.GetFileNameWithoutExtension(string.IsNullOrEmpty(outputPath) ? DefaultAssetPath : outputPath),
                    "asset",
                    "Choose where to save the baked terrain data.",
                    Path.GetDirectoryName(string.IsNullOrEmpty(outputPath) ? DefaultAssetPath : outputPath));

                if (!string.IsNullOrEmpty(selectedPath))
                {
                    outputPath = selectedPath;
                    outputAsset = AssetDatabase.LoadAssetAtPath<GpuTerrainBakedData>(outputPath);
                }
            }
        }

        if (EditorGUI.EndChangeCheck())
        {
            SavePrefs();
        }

        GUILayout.FlexibleSpace();
        using (new EditorGUI.DisabledScope(terrains.Length == 0 || string.IsNullOrEmpty(outputPath)))
        {
            if (GUILayout.Button("Bake Terrain Data", GUILayout.Height(34.0f)))
            {
                GpuTerrainBakedData bakedData = BakeTerrains(
                    terrains,
                    outputPath,
                    patchSize,
                    lodCount,
                    autoAssignToSceneRenderers,
                    true);
                if (bakedData != null)
                {
                    outputAsset = bakedData;
                }
            }
        }
    }

    public static GpuTerrainBakedData Bake(Terrain[] sourceTerrains, float patchSize, int lodCount)
    {
        patchSize = Mathf.Max(1.0f, patchSize);
        lodCount = Mathf.Max(1, lodCount);

        List<Terrain> terrains = CollectValidTerrains(sourceTerrains);
        if (terrains.Count == 0)
        {
            return null;
        }

        ValidateTerrainResolutions(terrains);

        GpuTerrainBakedData.TerrainTileInfo[] terrainInfos = new GpuTerrainBakedData.TerrainTileInfo[terrains.Count];
        List<GpuTerrainBakedData.BakedNode> nodes = new List<GpuTerrainBakedData.BakedNode>();
        List<int> rootIndices = new List<int>();

        for (int terrainIndex = 0; terrainIndex < terrains.Count; terrainIndex++)
        {
            Terrain terrain = terrains[terrainIndex];
            TerrainData terrainData = terrain.terrainData;
            Vector3 origin = terrain.transform.position;
            Vector3 size = terrainData.size;
            float[,] terrainHeights = terrainData.GetHeights(0, 0, terrainData.heightmapResolution, terrainData.heightmapResolution);
            terrainInfos[terrainIndex] = new GpuTerrainBakedData.TerrainTileInfo(origin, size);

            float maxX = origin.x + size.x;
            float maxZ = origin.z + size.z;
            for (float x = origin.x; x < maxX; x += patchSize)
            {
                for (float z = origin.z; z < maxZ; z += patchSize)
                {
                    float width = Mathf.Min(patchSize, maxX - x);
                    float depth = Mathf.Min(patchSize, maxZ - z);
                    int rootIndex = AddNodeRecursive(
                        nodes,
                        terrainData,
                        terrainHeights,
                        origin,
                        size,
                        new Vector4(x, z, width, depth),
                        lodCount - 1,
                        terrainIndex,
                        -1);
                    rootIndices.Add(rootIndex);
                }
            }
        }

        Texture2DArray heightArray = BuildHeightMapArray(terrains);
        Texture2DArray normalArray = BuildNormalMapArray(terrains, heightArray.width, heightArray.height);
        BakedMaterialData materialData = BuildMaterialData(terrains);

        GpuTerrainBakedData bakedData = CreateInstance<GpuTerrainBakedData>();
        bakedData.SetData(
            patchSize,
            lodCount,
            terrainInfos,
            nodes.ToArray(),
            rootIndices.ToArray(),
            heightArray,
            normalArray,
            materialData.controlMapArray,
            materialData.layerDiffuseArray,
            materialData.layerNormalArray,
            materialData.layerMaskArray,
            materialData.terrainLayerIndices,
            materialData.layerTileSizeOffsets,
            materialData.layerPbrParams);
        return bakedData;
    }

    private static GpuTerrainBakedData BakeTerrains(
        Terrain[] terrains,
        string assetPath,
        float patchSize,
        int lodCount,
        bool autoAssign,
        bool showDialog)
    {
        if (terrains == null || terrains.Length == 0)
        {
            EditorUtility.DisplayDialog("Bake GPU Terrain", "No Terrain objects found.", "OK");
            return null;
        }

        if (string.IsNullOrEmpty(assetPath) || !assetPath.StartsWith("Assets/"))
        {
            EditorUtility.DisplayDialog("Bake GPU Terrain", "Choose an output path under the Assets folder.", "OK");
            return null;
        }

        SavePrefs(patchSize, lodCount, assetPath, autoAssign, EditorPrefs.GetInt(SourceModeKey, 0));

        GpuTerrainBakedData savedAsset = null;
        try
        {
            EditorUtility.DisplayProgressBar("Bake GPU Terrain", "Building terrain nodes and texture arrays...", 0.25f);
            GpuTerrainBakedData bakedData = Bake(terrains, patchSize, lodCount);
            if (bakedData == null)
            {
                EditorUtility.DisplayDialog("Bake GPU Terrain", "No valid TerrainData found.", "OK");
                return null;
            }

            EditorUtility.DisplayProgressBar("Bake GPU Terrain", "Saving baked asset...", 0.85f);
            savedAsset = SaveAsset(bakedData, assetPath);
            if (autoAssign)
            {
                AssignToSceneTerrains(savedAsset);
            }
        }
        catch (InvalidDataException exception)
        {
            EditorUtility.DisplayDialog("Bake GPU Terrain", exception.Message, "OK");
            return null;
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }

        if (showDialog && savedAsset != null)
        {
            EditorUtility.DisplayDialog(
                "Bake GPU Terrain",
                "Baked " + savedAsset.TerrainCount + " terrains and " + savedAsset.NodeCount + " nodes.",
                "OK");
        }

        return savedAsset;
    }

    private static void BakeTerrainsWithSaveDialog(Terrain[] terrains)
    {
        if (terrains == null || terrains.Length == 0)
        {
            EditorUtility.DisplayDialog("Bake GPU Terrain", "No Terrain objects found.", "OK");
            return;
        }

        string currentPath = EditorPrefs.GetString(OutputPathKey, DefaultAssetPath);
        string path = EditorUtility.SaveFilePanelInProject(
            "Save GPU Terrain Baked Data",
            Path.GetFileNameWithoutExtension(currentPath),
            "asset",
            "Choose where to save the baked terrain data.",
            Path.GetDirectoryName(currentPath));

        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        float savedPatchSize = EditorPrefs.GetFloat(PatchSizeKey, 64.0f);
        int savedLodCount = EditorPrefs.GetInt(LodCountKey, 4);
        bool savedAutoAssign = EditorPrefs.GetBool(AutoAssignKey, true);
        BakeTerrains(terrains, path, savedPatchSize, savedLodCount, savedAutoAssign, true);
    }

    private static List<Terrain> CollectValidTerrains(Terrain[] sourceTerrains)
    {
        List<Terrain> terrains = new List<Terrain>();
        if (sourceTerrains != null)
        {
            for (int i = 0; i < sourceTerrains.Length; i++)
            {
                Terrain terrain = sourceTerrains[i];
                if (terrain != null && terrain.terrainData != null && !terrains.Contains(terrain))
                {
                    terrains.Add(terrain);
                }
            }
        }

        terrains.Sort((a, b) =>
        {
            Vector3 pa = a.transform.position;
            Vector3 pb = b.transform.position;
            int z = pa.z.CompareTo(pb.z);
            return z != 0 ? z : pa.x.CompareTo(pb.x);
        });
        return terrains;
    }

    private static Terrain[] GetSourceTerrains(SourceMode mode)
    {
        return mode == SourceMode.SelectedTerrains
            ? Selection.GetFiltered<Terrain>(SelectionMode.Editable | SelectionMode.ExcludePrefab)
            : Object.FindObjectsOfType<Terrain>(true);
    }

    private static void ValidateTerrainResolutions(List<Terrain> terrains)
    {
        TerrainData first = terrains[0].terrainData;
        int heightmapResolution = first.heightmapResolution;
        int alphamapResolution = first.alphamapResolution;
        for (int i = 1; i < terrains.Count; i++)
        {
            TerrainData terrainData = terrains[i].terrainData;
            if (terrainData.heightmapResolution != heightmapResolution)
            {
                throw new InvalidDataException("All baked terrains must use the same heightmap resolution.");
            }

            if (terrainData.alphamapResolution != alphamapResolution)
            {
                throw new InvalidDataException("All baked terrains must use the same alphamap resolution for phase 1 control texture baking.");
            }
        }
    }

    private static int AddNodeRecursive(
        List<GpuTerrainBakedData.BakedNode> nodes,
        TerrainData terrainData,
        float[,] terrainHeights,
        Vector3 terrainOrigin,
        Vector3 terrainSize,
        Vector4 rect,
        int mip,
        int terrainIndex,
        int parentIndex)
    {
        int nodeIndex = nodes.Count;
        nodes.Add(default);

        int childStart = -1;
        int childCount = 0;
        int childIndex0 = -1;
        int childIndex1 = -1;
        int childIndex2 = -1;
        int childIndex3 = -1;
        Vector2 heightMinMax;
        if (mip > 0)
        {
            float halfWidth = rect.z * 0.5f;
            float halfDepth = rect.w * 0.5f;
            childIndex0 = AddNodeRecursive(nodes, terrainData, terrainHeights, terrainOrigin, terrainSize, new Vector4(rect.x, rect.y, halfWidth, halfDepth), mip - 1, terrainIndex, nodeIndex);
            childIndex1 = AddNodeRecursive(nodes, terrainData, terrainHeights, terrainOrigin, terrainSize, new Vector4(rect.x + halfWidth, rect.y, halfWidth, halfDepth), mip - 1, terrainIndex, nodeIndex);
            childIndex2 = AddNodeRecursive(nodes, terrainData, terrainHeights, terrainOrigin, terrainSize, new Vector4(rect.x, rect.y + halfDepth, halfWidth, halfDepth), mip - 1, terrainIndex, nodeIndex);
            childIndex3 = AddNodeRecursive(nodes, terrainData, terrainHeights, terrainOrigin, terrainSize, new Vector4(rect.x + halfWidth, rect.y + halfDepth, halfWidth, halfDepth), mip - 1, terrainIndex, nodeIndex);
            childStart = childIndex0;
            childCount = 4;

            float minHeight = float.MaxValue;
            float maxHeight = float.MinValue;
            for (int i = 0; i < childCount; i++)
            {
                int childIndex = i == 0 ? childIndex0 : i == 1 ? childIndex1 : i == 2 ? childIndex2 : childIndex3;
                Vector2 childMinMax = nodes[childIndex].heightMinMax;
                minHeight = Mathf.Min(minHeight, childMinMax.x);
                maxHeight = Mathf.Max(maxHeight, childMinMax.y);
            }

            heightMinMax = new Vector2(minHeight, maxHeight);
        }
        else
        {
            heightMinMax = CalculateHeightMinMax(terrainData, terrainHeights, terrainOrigin, terrainSize, rect);
        }

        nodes[nodeIndex] = new GpuTerrainBakedData.BakedNode(
            rect,
            heightMinMax,
            mip,
            terrainIndex,
            parentIndex,
            childStart,
            childCount,
            childIndex0,
            childIndex1,
            childIndex2,
            childIndex3);
        return nodeIndex;
    }

    private static Vector2 CalculateHeightMinMax(TerrainData terrainData, float[,] terrainHeights, Vector3 terrainOrigin, Vector3 terrainSize, Vector4 rect)
    {
        int resolution = terrainData.heightmapResolution;
        float minU = Mathf.Clamp01((rect.x - terrainOrigin.x) / Mathf.Max(terrainSize.x, 1e-5f));
        float maxU = Mathf.Clamp01((rect.x + rect.z - terrainOrigin.x) / Mathf.Max(terrainSize.x, 1e-5f));
        float minV = Mathf.Clamp01((rect.y - terrainOrigin.z) / Mathf.Max(terrainSize.z, 1e-5f));
        float maxV = Mathf.Clamp01((rect.y + rect.w - terrainOrigin.z) / Mathf.Max(terrainSize.z, 1e-5f));

        int xMin = Mathf.Clamp(Mathf.FloorToInt(minU * (resolution - 1)), 0, resolution - 1);
        int xMax = Mathf.Clamp(Mathf.CeilToInt(maxU * (resolution - 1)), 0, resolution - 1);
        int yMin = Mathf.Clamp(Mathf.FloorToInt(minV * (resolution - 1)), 0, resolution - 1);
        int yMax = Mathf.Clamp(Mathf.CeilToInt(maxV * (resolution - 1)), 0, resolution - 1);
        float minHeight = float.MaxValue;
        float maxHeight = float.MinValue;
        for (int y = yMin; y <= yMax; y++)
        {
            for (int x = xMin; x <= xMax; x++)
            {
                float worldHeight = terrainOrigin.y + terrainHeights[y, x] * terrainSize.y;
                minHeight = Mathf.Min(minHeight, worldHeight);
                maxHeight = Mathf.Max(maxHeight, worldHeight);
            }
        }

        if (minHeight == float.MaxValue)
        {
            minHeight = terrainOrigin.y;
            maxHeight = terrainOrigin.y + terrainSize.y;
        }

        return new Vector2(minHeight, maxHeight);
    }

    private static Texture2DArray BuildHeightMapArray(List<Terrain> terrains)
    {
        int resolution = Mathf.Max(1, terrains[0].terrainData.heightmapResolution);
        Texture2DArray textureArray = new Texture2DArray(resolution, resolution, terrains.Count, HeightMapArrayFormat, false, true)
        {
            name = HeightMapArrayName,
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear
        };

        for (int slice = 0; slice < terrains.Count; slice++)
        {
            TerrainData terrainData = terrains[slice].terrainData;
            float[,] heights = terrainData.GetHeights(0, 0, resolution, resolution);
            Unity.Mathematics.half[] heights01 = new Unity.Mathematics.half[resolution * resolution];
            int index = 0;
            for (int y = 0; y < resolution; y++)
            {
                for (int x = 0; x < resolution; x++)
                {
                    heights01[index++] = (Unity.Mathematics.half)Mathf.Clamp01(heights[y, x]);
                }
            }

            textureArray.SetPixelData(heights01, 0, slice);
        }

        textureArray.Apply(false, false);
        return textureArray;
    }

    private static Texture2DArray BuildNormalMapArray(List<Terrain> terrains, int width, int height)
    {
        Texture2DArray textureArray = new Texture2DArray(width, height, terrains.Count, NormalMapArrayFormat, false, true)
        {
            name = NormalMapArrayName,
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear
        };

        for (int slice = 0; slice < terrains.Count; slice++)
        {
            TerrainData terrainData = terrains[slice].terrainData;
            Color32[] colors = new Color32[width * height];
            int index = 0;
            for (int y = 0; y < height; y++)
            {
                float v = height > 1 ? (float)y / (height - 1) : 0.0f;
                for (int x = 0; x < width; x++)
                {
                    float u = width > 1 ? (float)x / (width - 1) : 0.0f;
                    Vector3 normal = terrainData.GetInterpolatedNormal(u, v);
                    colors[index++] = new Color32(
                        Float01ToByte(normal.z * 0.5f + 0.5f),
                        Float01ToByte(normal.y * 0.5f + 0.5f),
                        Float01ToByte(normal.x * 0.5f + 0.5f),
                        255);
                }
            }

            textureArray.SetPixels32(colors, slice, 0);
        }

        textureArray.Apply(false, false);
        return textureArray;
    }

    private static BakedMaterialData BuildMaterialData(List<Terrain> terrains)
    {
        List<TerrainLayer> layers = new List<TerrainLayer>();
        for (int i = 0; i < terrains.Count; i++)
        {
            TerrainLayer[] terrainLayers = terrains[i].terrainData.terrainLayers;
            if (terrainLayers == null)
            {
                continue;
            }

            for (int j = 0; j < terrainLayers.Length && j < 4; j++)
            {
                TerrainLayer layer = terrainLayers[j];
                if (layer != null && !layers.Contains(layer))
                {
                    layers.Add(layer);
                }
            }

            if (terrainLayers.Length > 4)
            {
                Debug.LogWarning(
                    "GPU terrain material bake currently renders the first 4 TerrainLayers per terrain. " +
                    "Extra layers on '" + terrains[i].name + "' will fall back to layer 0 where no first-4 weight exists.",
                    terrains[i]);
            }
        }

        if (layers.Count == 0)
        {
            layers.Add(null);
        }

        int layerCount = Mathf.Min(layers.Count, GpuTerrainBakedData.MaxTerrainLayerCount);
        Vector4[] terrainLayerIndices = BuildTerrainLayerIndices(terrains, layers, layerCount);
        Vector4[] layerTileSizeOffsets = new Vector4[layerCount];
        Vector4[] layerPbrParams = new Vector4[layerCount];

        Texture2DArray layerDiffuseArray = CreateLayerTextureArray(LayerDiffuseArrayName, layerCount, LayerDiffuseArrayFormat, true, false);
        Texture2DArray layerNormalArray = CreateLayerTextureArray(LayerNormalArrayName, layerCount, LayerNormalArrayFormat, true, true);
        Texture2DArray layerMaskArray = CreateLayerTextureArray(LayerMaskArrayName, layerCount, LayerMaskArrayFormat, true, true);

        for (int i = 0; i < layerCount; i++)
        {
            TerrainLayer layer = layers[i];
            Texture2D diffuse = layer != null ? layer.diffuseTexture : null;
            Texture2D normal = layer != null ? layer.normalMapTexture : null;
            Texture2D mask = layer != null ? layer.maskMapTexture : null;

            CopyTextureToArraySlice(diffuse, layerDiffuseArray, i, Color.white, false, true);
            CopyTextureToArraySlice(normal, layerNormalArray, i, new Color(0.5f, 0.5f, 1.0f, 1.0f), true, false);
            CopyTextureToArraySlice(mask, layerMaskArray, i, Color.white, false, false);

            Vector2 tileSize = layer != null ? layer.tileSize : Vector2.one;
            Vector2 tileOffset = layer != null ? layer.tileOffset : Vector2.zero;
            layerTileSizeOffsets[i] = new Vector4(
                Mathf.Max(0.0001f, tileSize.x),
                Mathf.Max(0.0001f, tileSize.y),
                tileOffset.x,
                tileOffset.y);
            layerPbrParams[i] = new Vector4(
                layer != null ? layer.normalScale : 1.0f,
                layer != null ? layer.metallic : 0.0f,
                layer != null ? layer.smoothness : 0.5f,
                0.0f);
        }

        layerDiffuseArray.Apply(true, false);
        layerNormalArray.Apply(true, false);
        layerMaskArray.Apply(true, false);

        return new BakedMaterialData
        {
            controlMapArray = BuildControlMapArray(terrains),
            layerDiffuseArray = layerDiffuseArray,
            layerNormalArray = layerNormalArray,
            layerMaskArray = layerMaskArray,
            terrainLayerIndices = terrainLayerIndices,
            layerTileSizeOffsets = layerTileSizeOffsets,
            layerPbrParams = layerPbrParams
        };
    }

    private static Vector4[] BuildTerrainLayerIndices(List<Terrain> terrains, List<TerrainLayer> globalLayers, int layerCount)
    {
        Vector4[] terrainLayerIndices = new Vector4[terrains.Count];
        for (int terrainIndex = 0; terrainIndex < terrains.Count; terrainIndex++)
        {
            TerrainLayer[] localLayers = terrains[terrainIndex].terrainData.terrainLayers;
            int x = GetGlobalLayerIndex(localLayers, globalLayers, layerCount, 0);
            int y = GetGlobalLayerIndex(localLayers, globalLayers, layerCount, 1);
            int z = GetGlobalLayerIndex(localLayers, globalLayers, layerCount, 2);
            int w = GetGlobalLayerIndex(localLayers, globalLayers, layerCount, 3);
            terrainLayerIndices[terrainIndex] = new Vector4(x, y, z, w);
        }

        return terrainLayerIndices;
    }

    private static int GetGlobalLayerIndex(TerrainLayer[] localLayers, List<TerrainLayer> globalLayers, int layerCount, int localIndex)
    {
        if (localLayers == null || localIndex >= localLayers.Length || localLayers[localIndex] == null)
        {
            return 0;
        }

        int index = globalLayers.IndexOf(localLayers[localIndex]);
        if (index < 0)
        {
            return 0;
        }

        return Mathf.Clamp(index, 0, Mathf.Max(0, layerCount - 1));
    }

    private static Texture2DArray BuildControlMapArray(List<Terrain> terrains)
    {
        int resolution = Mathf.Max(1, terrains[0].terrainData.alphamapResolution);
        Texture2DArray textureArray = new Texture2DArray(resolution, resolution, terrains.Count, ControlMapArrayFormat, false, true)
        {
            name = ControlMapArrayName,
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear
        };

        for (int slice = 0; slice < terrains.Count; slice++)
        {
            TerrainData terrainData = terrains[slice].terrainData;
            int width = terrainData.alphamapWidth;
            int height = terrainData.alphamapHeight;
            int layerCount = terrainData.alphamapLayers;
            float[,,] alphamaps = terrainData.GetAlphamaps(0, 0, width, height);
            Color[] colors = new Color[resolution * resolution];
            int index = 0;
            for (int y = 0; y < resolution; y++)
            {
                int sourceY = Mathf.Clamp(Mathf.RoundToInt((float)y / Mathf.Max(1, resolution - 1) * (height - 1)), 0, height - 1);
                for (int x = 0; x < resolution; x++)
                {
                    int sourceX = Mathf.Clamp(Mathf.RoundToInt((float)x / Mathf.Max(1, resolution - 1) * (width - 1)), 0, width - 1);
                    colors[index++] = new Color(
                        layerCount > 0 ? alphamaps[sourceY, sourceX, 0] : 1.0f,
                        layerCount > 1 ? alphamaps[sourceY, sourceX, 1] : 0.0f,
                        layerCount > 2 ? alphamaps[sourceY, sourceX, 2] : 0.0f,
                        layerCount > 3 ? alphamaps[sourceY, sourceX, 3] : 0.0f);
                    Color control = colors[index - 1];
                    if (control.r + control.g + control.b + control.a <= 1e-5f)
                    {
                        colors[index - 1] = new Color(1.0f, 0.0f, 0.0f, 0.0f);
                    }
                }
            }

            textureArray.SetPixels(colors, slice, 0);
        }

        textureArray.Apply(false, false);
        return textureArray;
    }

    private static Texture2DArray CreateLayerTextureArray(string name, int layerCount, TextureFormat format, bool mipChain, bool linear)
    {
        return new Texture2DArray(LayerTextureSize, LayerTextureSize, Mathf.Max(1, layerCount), format, mipChain, linear)
        {
            name = name,
            wrapMode = TextureWrapMode.Repeat,
            filterMode = mipChain ? FilterMode.Trilinear : FilterMode.Bilinear,
            anisoLevel = 4
        };
    }

    private static void CopyTextureToArraySlice(Texture2D source, Texture2DArray target, int slice, Color fallback, bool normalMap, bool sRgb)
    {
        Color[] pixels = new Color[LayerTextureSize * LayerTextureSize];
        if (source == null)
        {
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = fallback;
            }
        }
        else
        {
            pixels = ReadTexturePixels(source, normalMap, sRgb);
        }

        target.SetPixels(pixels, slice, 0);
    }

    private static Color[] ReadTexturePixels(Texture2D source, bool normalMap, bool sRgb)
    {
        RenderTexture previous = RenderTexture.active;
        FilterMode previousFilterMode = source.filterMode;
        float previousMipMapBias = source.mipMapBias;
        int previousAnisoLevel = source.anisoLevel;
        RenderTextureReadWrite readWrite = sRgb ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear;
        RenderTexture temporary = RenderTexture.GetTemporary(LayerTextureSize, LayerTextureSize, 0, RenderTextureFormat.ARGB32, readWrite);
        Texture2D readable = new Texture2D(LayerTextureSize, LayerTextureSize, TextureFormat.RGBA32, false, !sRgb);
        try
        {
            source.filterMode = FilterMode.Trilinear;
            source.mipMapBias = 0.0f;
            source.anisoLevel = 1;

            Graphics.Blit(source, temporary);
            RenderTexture.active = temporary;
            readable.ReadPixels(new Rect(0, 0, LayerTextureSize, LayerTextureSize), 0, 0, false);
            readable.Apply(false, false);
            Color[] pixels = readable.GetPixels();
            if (normalMap)
            {
                for (int i = 0; i < pixels.Length; i++)
                {
                    pixels[i] = NormalizeNormalMapColor(pixels[i]);
                }
            }

            return pixels;
        }
        finally
        {
            source.filterMode = previousFilterMode;
            source.mipMapBias = previousMipMapBias;
            source.anisoLevel = previousAnisoLevel;
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(temporary);
            DestroyImmediate(readable);
        }
    }

    private static Color NormalizeNormalMapColor(Color color)
    {
        if (color.a > 0.0f && (color.r < 0.05f || color.b < 0.05f))
        {
            return new Color(color.a, color.g, 1.0f, 1.0f);
        }

        return color;
    }

    private static byte Float01ToByte(float value)
    {
        return (byte)Mathf.Clamp(Mathf.RoundToInt(value * 255.0f), 0, 255);
    }

    private static GpuTerrainBakedData SaveAsset(GpuTerrainBakedData asset, string path)
    {
        string directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
            AssetDatabase.Refresh();
        }

        Texture2DArray heightArray = asset.HeightMapArray;
        Texture2DArray normalArray = asset.NormalMapArray;
        Texture2DArray controlArray = asset.ControlMapArray;
        Texture2DArray layerDiffuseArray = asset.LayerDiffuseArray;
        Texture2DArray layerNormalArray = asset.LayerNormalArray;
        Texture2DArray layerMaskArray = asset.LayerMaskArray;
        GpuTerrainBakedData existing = AssetDatabase.LoadAssetAtPath<GpuTerrainBakedData>(path);
        if (existing != null)
        {
            RemoveEmbeddedTextureArrays(path);
            AddTextureArrayToAsset(heightArray, existing);
            AddTextureArrayToAsset(normalArray, existing);
            AddTextureArrayToAsset(controlArray, existing);
            AddTextureArrayToAsset(layerDiffuseArray, existing);
            AddTextureArrayToAsset(layerNormalArray, existing);
            AddTextureArrayToAsset(layerMaskArray, existing);
            existing.SetData(
                asset.PatchSize,
                asset.LodCount,
                asset.Terrains,
                asset.Nodes,
                asset.RootNodeIndices,
                heightArray,
                normalArray,
                controlArray,
                layerDiffuseArray,
                layerNormalArray,
                layerMaskArray,
                asset.TerrainLayerIndices,
                asset.LayerTileSizeOffsets,
                asset.LayerPbrParams);
            DestroyImmediate(asset);
            EditorUtility.SetDirty(existing);
            SetDirtyIfNotNull(heightArray);
            SetDirtyIfNotNull(normalArray);
            SetDirtyIfNotNull(controlArray);
            SetDirtyIfNotNull(layerDiffuseArray);
            SetDirtyIfNotNull(layerNormalArray);
            SetDirtyIfNotNull(layerMaskArray);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(path);
            return existing;
        }

        AssetDatabase.CreateAsset(asset, path);
        AddTextureArrayToAsset(heightArray, asset);
        AddTextureArrayToAsset(normalArray, asset);
        AddTextureArrayToAsset(controlArray, asset);
        AddTextureArrayToAsset(layerDiffuseArray, asset);
        AddTextureArrayToAsset(layerNormalArray, asset);
        AddTextureArrayToAsset(layerMaskArray, asset);
        EditorUtility.SetDirty(asset);
        SetDirtyIfNotNull(heightArray);
        SetDirtyIfNotNull(normalArray);
        SetDirtyIfNotNull(controlArray);
        SetDirtyIfNotNull(layerDiffuseArray);
        SetDirtyIfNotNull(layerNormalArray);
        SetDirtyIfNotNull(layerMaskArray);
        AssetDatabase.SaveAssets();
        AssetDatabase.ImportAsset(path);
        return asset;
    }

    private static void AddTextureArrayToAsset(Texture2DArray textureArray, Object asset)
    {
        if (textureArray != null)
        {
            AssetDatabase.AddObjectToAsset(textureArray, asset);
        }
    }

    private static void SetDirtyIfNotNull(Object asset)
    {
        if (asset != null)
        {
            EditorUtility.SetDirty(asset);
        }
    }

    private static void RemoveEmbeddedTextureArrays(string path)
    {
        Object[] subAssets = AssetDatabase.LoadAllAssetRepresentationsAtPath(path);
        for (int i = 0; i < subAssets.Length; i++)
        {
            if (subAssets[i] is Texture2DArray)
            {
                DestroyImmediate(subAssets[i], true);
            }
        }
    }

    private static void AssignToSceneTerrains(GpuTerrainBakedData asset)
    {
        if (asset == null)
        {
            return;
        }

        GPUTerrain[] gpuTerrains = Object.FindObjectsOfType<GPUTerrain>(true);
        for (int i = 0; i < gpuTerrains.Length; i++)
        {
            Undo.RecordObject(gpuTerrains[i], "Assign GPU Terrain Data");
            gpuTerrains[i].SetBakedData(asset);
            EditorUtility.SetDirty(gpuTerrains[i]);
            EditorSceneManager.MarkSceneDirty(gpuTerrains[i].gameObject.scene);
        }

        GpuDrivenShowcaseController controller = Object.FindObjectOfType<GpuDrivenShowcaseController>(true);
        if (controller != null)
        {
            controller.RefreshModules();
        }
    }

    private void SavePrefs()
    {
        SavePrefs(patchSize, lodCount, outputPath, autoAssignToSceneRenderers, (int)sourceMode);
    }

    private static void SavePrefs(float patchSize, int lodCount, string assetPath, bool autoAssign, int sourceMode)
    {
        EditorPrefs.SetFloat(PatchSizeKey, Mathf.Max(1.0f, patchSize));
        EditorPrefs.SetInt(LodCountKey, Mathf.Max(1, lodCount));
        EditorPrefs.SetString(OutputPathKey, string.IsNullOrEmpty(assetPath) ? DefaultAssetPath : assetPath);
        EditorPrefs.SetBool(AutoAssignKey, autoAssign);
        EditorPrefs.SetInt(SourceModeKey, sourceMode);
    }

    private struct BakedMaterialData
    {
        public Texture2DArray controlMapArray;
        public Texture2DArray layerDiffuseArray;
        public Texture2DArray layerNormalArray;
        public Texture2DArray layerMaskArray;
        public Vector4[] terrainLayerIndices;
        public Vector4[] layerTileSizeOffsets;
        public Vector4[] layerPbrParams;
    }
}
