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
    private const TextureFormat HeightMapArrayFormat = TextureFormat.RHalf;
    private const TextureFormat NormalMapArrayFormat = TextureFormat.RGB24;

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

        GpuTerrainBakedData bakedData = CreateInstance<GpuTerrainBakedData>();
        bakedData.SetData(
            patchSize,
            lodCount,
            terrainInfos,
            nodes.ToArray(),
            rootIndices.ToArray(),
            heightArray,
            normalArray);
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
        GpuTerrainBakedData existing = AssetDatabase.LoadAssetAtPath<GpuTerrainBakedData>(path);
        if (existing != null)
        {
            RemoveEmbeddedTextureArrays(path);
            AssetDatabase.AddObjectToAsset(heightArray, existing);
            AssetDatabase.AddObjectToAsset(normalArray, existing);
            existing.SetData(
                asset.PatchSize,
                asset.LodCount,
                asset.Terrains,
                asset.Nodes,
                asset.RootNodeIndices,
                heightArray,
                normalArray);
            DestroyImmediate(asset);
            EditorUtility.SetDirty(existing);
            EditorUtility.SetDirty(heightArray);
            EditorUtility.SetDirty(normalArray);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(path);
            return existing;
        }

        AssetDatabase.CreateAsset(asset, path);
        AssetDatabase.AddObjectToAsset(heightArray, asset);
        AssetDatabase.AddObjectToAsset(normalArray, asset);
        EditorUtility.SetDirty(asset);
        EditorUtility.SetDirty(heightArray);
        EditorUtility.SetDirty(normalArray);
        AssetDatabase.SaveAssets();
        AssetDatabase.ImportAsset(path);
        return asset;
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
}
