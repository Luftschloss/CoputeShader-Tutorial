using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

public static class GpuDrivenTerrainFoliageBaker
{
    private const string DefaultAssetPath = "Assets/GPUDrivenShowcase/Generated/GpuDrivenFoliageData.asset";
    private const string GeneratedSubAssetPrefix = "Generated GPU Foliage";
    private const string BaseMapProperty = "_BaseMap";
    private const string MainTexProperty = "_MainTex";
    private const string BaseColorProperty = "_BaseColor";
    private const string ColorProperty = "_Color";
    private const string CutoffProperty = "_Cutoff";
    private const string BumpMapProperty = "_BumpMap";
    private const string NormalMapProperty = "_NormalMap";
    private const string BumpScaleProperty = "_BumpScale";
    private const string MaskMapProperty = "_MaskMap";
    private const string MetallicGlossMapProperty = "_MetallicGlossMap";
    private const string OcclusionMapProperty = "_OcclusionMap";
    private const string EmissionMapProperty = "_EmissionMap";
    private const string BillboardProperty = "_GpuDrivenFoliageBillboard";
    private const string AlphaCutoffProperty = "_AlphaCutoff";

    private enum SourceTextureRole
    {
        BaseMap,
        NormalMap,
        MaskMap,
        MetallicGlossMap,
        OcclusionMap,
        EmissionMap
    }

    public struct Settings
    {
        public bool includeTreeInstances;
        public bool includeDetailInstances;
        public bool bakeTextureDetailsAsCrossQuads;
        public bool autoAssignToSceneRenderer;
        public int randomSeed;
        public int maxInstancesPerDetailCell;
        public float alphaCutoff;
        public ShadowCastingMode treeShadowCastingMode;
        public bool treeReceiveShadows;
        public ShadowCastingMode detailShadowCastingMode;
        public bool detailReceiveShadows;

        public static Settings Default
        {
            get
            {
                return new Settings
                {
                    includeTreeInstances = true,
                    includeDetailInstances = true,
                    bakeTextureDetailsAsCrossQuads = true,
                    autoAssignToSceneRenderer = true,
                    randomSeed = 12345,
                    maxInstancesPerDetailCell = 64,
                    alphaCutoff = 0.35f,
                    treeShadowCastingMode = ShadowCastingMode.On,
                    treeReceiveShadows = true,
                    detailShadowCastingMode = ShadowCastingMode.Off,
                    detailReceiveShadows = true
                };
            }
        }
    }

    public static GpuDrivenFoliageData BakeFromTerrains(
        Terrain[] sourceTerrains,
        string assetPath,
        Settings settings,
        bool showDialog)
    {
        List<Terrain> terrains = CollectValidTerrains(sourceTerrains);
        if (terrains.Count == 0)
        {
            if (showDialog)
            {
                EditorUtility.DisplayDialog("Bake GPU Foliage", "No Terrain objects found.", "OK");
            }
            return null;
        }

        assetPath = string.IsNullOrEmpty(assetPath) ? DefaultAssetPath : assetPath;
        if (!assetPath.StartsWith("Assets/"))
        {
            if (showDialog)
            {
                EditorUtility.DisplayDialog("Bake GPU Foliage", "Choose an output path under the Assets folder.", "OK");
            }
            return null;
        }

        settings.maxInstancesPerDetailCell = Mathf.Max(1, settings.maxInstancesPerDetailCell);
        settings.alphaCutoff = Mathf.Clamp01(settings.alphaCutoff);

        GpuDrivenFoliageData outputData = null;
        try
        {
            EditorUtility.DisplayProgressBar("Bake GPU Foliage", "Preparing output asset...", 0.05f);
            outputData = EnsureOutputData(assetPath);
            if (outputData == null)
            {
                return null;
            }

            RemoveGeneratedSubAssets(assetPath);

            EditorUtility.DisplayProgressBar("Bake GPU Foliage", "Reading Unity Terrain vegetation...", 0.25f);
            BakeContext context = new BakeContext(outputData, settings);
            context.Bake(terrains);

            Bounds worldBounds = context.HasBounds ? context.WorldBounds : BuildFallbackBounds(terrains);
            outputData.SetBakedData(context.Prototypes, context.Instances, worldBounds);
            EditorUtility.SetDirty(outputData);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(assetPath);

            if (settings.autoAssignToSceneRenderer)
            {
                AssignToSceneRenderers(outputData);
            }
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            if (showDialog)
            {
                EditorUtility.DisplayDialog("Bake GPU Foliage", exception.Message, "OK");
            }
            return null;
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }

        if (showDialog && outputData != null)
        {
            EditorUtility.DisplayDialog(
                "Bake GPU Foliage",
                "Baked " + outputData.PrototypeCount + " foliage prototypes and " +
                outputData.InstanceCount + " instances.",
                "OK");
        }

        return outputData;
    }

    public static void AssignToSceneRenderers(GpuDrivenFoliageData data)
    {
        if (data == null)
        {
            return;
        }

        GpuDrivenFoliageRenderer[] renderers = UnityEngine.Object.FindObjectsOfType<GpuDrivenFoliageRenderer>(true);
        if (renderers.Length == 0)
        {
            GameObject gameObject = new GameObject("GPU Driven Foliage");
            Undo.RegisterCreatedObjectUndo(gameObject, "Create GPU Driven Foliage Renderer");
            GpuDrivenFoliageRenderer renderer = gameObject.AddComponent<GpuDrivenFoliageRenderer>();
            renderer.SetFoliageData(data);
            EditorUtility.SetDirty(renderer);
            EditorSceneManager.MarkSceneDirty(gameObject.scene);
            return;
        }

        for (int i = 0; i < renderers.Length; i++)
        {
            Undo.RecordObject(renderers[i], "Assign GPU Foliage Data");
            renderers[i].SetFoliageData(data);
            EditorUtility.SetDirty(renderers[i]);
            EditorSceneManager.MarkSceneDirty(renderers[i].gameObject.scene);
        }

        GpuDrivenShowcaseController controller = UnityEngine.Object.FindObjectOfType<GpuDrivenShowcaseController>(true);
        if (controller != null)
        {
            controller.RefreshModules();
        }
    }

    private static GpuDrivenFoliageData EnsureOutputData(string assetPath)
    {
        string directory = Path.GetDirectoryName(assetPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
            AssetDatabase.Refresh();
        }

        UnityEngine.Object existingMainAsset = AssetDatabase.LoadMainAssetAtPath(assetPath);
        if (existingMainAsset != null && !(existingMainAsset is GpuDrivenFoliageData))
        {
            throw new InvalidDataException("Output path is already used by a non-foliage asset: " + assetPath);
        }

        GpuDrivenFoliageData existing = existingMainAsset as GpuDrivenFoliageData;
        if (existing != null)
        {
            return existing;
        }

        GpuDrivenFoliageData created = ScriptableObject.CreateInstance<GpuDrivenFoliageData>();
        AssetDatabase.CreateAsset(created, assetPath);
        return created;
    }

    private static void RemoveGeneratedSubAssets(string assetPath)
    {
        UnityEngine.Object[] subAssets = AssetDatabase.LoadAllAssetRepresentationsAtPath(assetPath);
        for (int i = 0; i < subAssets.Length; i++)
        {
            UnityEngine.Object subAsset = subAssets[i];
            if (subAsset != null && subAsset.name.StartsWith(GeneratedSubAssetPrefix, StringComparison.Ordinal))
            {
                UnityEngine.Object.DestroyImmediate(subAsset, true);
            }
        }
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

    private static Bounds BuildFallbackBounds(List<Terrain> terrains)
    {
        Bounds bounds = new Bounds();
        bool hasBounds = false;
        for (int i = 0; i < terrains.Count; i++)
        {
            Terrain terrain = terrains[i];
            Bounds terrainBounds = new Bounds(
                terrain.transform.position + terrain.terrainData.size * 0.5f,
                terrain.terrainData.size);
            if (!hasBounds)
            {
                bounds = terrainBounds;
                hasBounds = true;
            }
            else
            {
                bounds.Encapsulate(terrainBounds);
            }
        }
        return hasBounds ? bounds : new Bounds(Vector3.zero, Vector3.one);
    }

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

    private static Vector3 SampleTerrainPosition(Terrain terrain, float normalizedX, float normalizedZ)
    {
        TerrainData terrainData = terrain.terrainData;
        Vector3 origin = terrain.transform.position;
        Vector3 size = terrainData.size;
        float x = origin.x + Mathf.Clamp01(normalizedX) * size.x;
        float z = origin.z + Mathf.Clamp01(normalizedZ) * size.z;
        float y = origin.y + terrainData.GetInterpolatedHeight(Mathf.Clamp01(normalizedX), Mathf.Clamp01(normalizedZ));
        return new Vector3(x, y, z);
    }

    private static float Random01(uint seed)
    {
        return (Hash(seed) & 0x00FFFFFFu) / 16777215.0f;
    }

    private static uint Hash(uint value)
    {
        unchecked
        {
            value ^= value >> 16;
            value *= 0x7feb352du;
            value ^= value >> 15;
            value *= 0x846ca68bu;
            value ^= value >> 16;
            return value;
        }
    }

    private static uint BuildSeed(int randomSeed, int terrainIndex, int prototypeIndex, int x, int y, int instanceIndex)
    {
        unchecked
        {
            uint seed = (uint)randomSeed;
            seed = seed * 397u ^ (uint)terrainIndex;
            seed = seed * 397u ^ (uint)prototypeIndex;
            seed = seed * 397u ^ (uint)x;
            seed = seed * 397u ^ (uint)y;
            seed = seed * 397u ^ (uint)instanceIndex;
            return seed;
        }
    }

    private sealed class BakeContext
    {
        private readonly GpuDrivenFoliageData outputData;
        private readonly Settings settings;
        private readonly Dictionary<PrototypeKey, int> prototypeLookup = new Dictionary<PrototypeKey, int>();
        private readonly Dictionary<int, List<PrototypePart>> prefabPartCache = new Dictionary<int, List<PrototypePart>>();
        private readonly Dictionary<int, List<PrototypePart>> textureDetailPartCache = new Dictionary<int, List<PrototypePart>>();
        private readonly List<GpuDrivenFoliagePrototype> prototypes = new List<GpuDrivenFoliagePrototype>();
        private readonly List<GpuDrivenFoliageInstance> instances = new List<GpuDrivenFoliageInstance>();
        private Bounds worldBounds;
        private bool hasBounds;

        public List<GpuDrivenFoliagePrototype> Prototypes => prototypes;
        public List<GpuDrivenFoliageInstance> Instances => instances;
        public Bounds WorldBounds => worldBounds;
        public bool HasBounds => hasBounds;

        public BakeContext(GpuDrivenFoliageData outputData, Settings settings)
        {
            this.outputData = outputData;
            this.settings = settings;
        }

        public void Bake(List<Terrain> terrains)
        {
            for (int terrainIndex = 0; terrainIndex < terrains.Count; terrainIndex++)
            {
                Terrain terrain = terrains[terrainIndex];
                TerrainData terrainData = terrain.terrainData;

                if (settings.includeTreeInstances)
                {
                    BakeTrees(terrainIndex, terrain, terrainData);
                }

                if (settings.includeDetailInstances)
                {
                    BakeDetails(terrainIndex, terrain, terrainData);
                }
            }
        }

        private void BakeTrees(int terrainIndex, Terrain terrain, TerrainData terrainData)
        {
            TreePrototype[] treePrototypes = terrainData.treePrototypes;
            TreeInstance[] treeInstances = terrainData.treeInstances;
            if (treePrototypes == null || treeInstances == null || treePrototypes.Length == 0 || treeInstances.Length == 0)
            {
                return;
            }

            Dictionary<int, List<PrototypePart>> terrainTreePartCache = new Dictionary<int, List<PrototypePart>>();
            for (int i = 0; i < treeInstances.Length; i++)
            {
                TreeInstance tree = treeInstances[i];
                int prototypeIndex = tree.prototypeIndex;
                if (prototypeIndex < 0 || prototypeIndex >= treePrototypes.Length)
                {
                    continue;
                }

                if (!terrainTreePartCache.TryGetValue(prototypeIndex, out List<PrototypePart> parts))
                {
                    GameObject prefab = treePrototypes[prototypeIndex].prefab;
                    parts = GetPrefabParts(
                        prefab,
                        settings.treeShadowCastingMode,
                        settings.treeReceiveShadows);
                    terrainTreePartCache.Add(prototypeIndex, parts);
                }

                if (parts.Count == 0)
                {
                    continue;
                }

                Vector3 position = SampleTerrainPosition(terrain, tree.position.x, tree.position.z);
                Quaternion rotation = Quaternion.AngleAxis(tree.rotation * Mathf.Rad2Deg, Vector3.up);
                Vector3 scale = new Vector3(tree.widthScale, tree.heightScale, tree.widthScale);
                Matrix4x4 rootMatrix = Matrix4x4.TRS(position, rotation, scale);
                AddParts(parts, rootMatrix);
            }
        }

        private void BakeDetails(int terrainIndex, Terrain terrain, TerrainData terrainData)
        {
            DetailPrototype[] detailPrototypes = terrainData.detailPrototypes;
            if (detailPrototypes == null || detailPrototypes.Length == 0 || terrainData.detailWidth <= 0 || terrainData.detailHeight <= 0)
            {
                return;
            }

            int detailWidth = terrainData.detailWidth;
            int detailHeight = terrainData.detailHeight;
            for (int detailIndex = 0; detailIndex < detailPrototypes.Length; detailIndex++)
            {
                DetailPrototype detail = detailPrototypes[detailIndex];
                List<PrototypePart> parts = GetDetailParts(detail);
                if (parts.Count == 0)
                {
                    continue;
                }

                int[,] layer = terrainData.GetDetailLayer(0, 0, detailWidth, detailHeight, detailIndex);
                for (int y = 0; y < detailHeight; y++)
                {
                    for (int x = 0; x < detailWidth; x++)
                    {
                        int count = Mathf.Min(layer[y, x], settings.maxInstancesPerDetailCell);
                        for (int instanceIndex = 0; instanceIndex < count; instanceIndex++)
                        {
                            uint seed = BuildSeed(settings.randomSeed, terrainIndex, detailIndex, x, y, instanceIndex);
                            float jitterX = Random01(seed + 1u);
                            float jitterZ = Random01(seed + 2u);
                            float normalizedX = (x + jitterX) / detailWidth;
                            float normalizedZ = (y + jitterZ) / detailHeight;
                            Vector3 position = SampleTerrainPosition(terrain, normalizedX, normalizedZ);

                            float width = Mathf.Lerp(
                                Mathf.Min(detail.minWidth, detail.maxWidth),
                                Mathf.Max(detail.minWidth, detail.maxWidth),
                                Random01(seed + 3u));
                            float height = Mathf.Lerp(
                                Mathf.Min(detail.minHeight, detail.maxHeight),
                                Mathf.Max(detail.minHeight, detail.maxHeight),
                                Random01(seed + 4u));
                bool billboard = parts.Count > 0 && prototypes[parts[0].prototypeIndex].billboard;
                Quaternion rotation = billboard
                    ? Quaternion.identity
                    : Quaternion.AngleAxis(Random01(seed + 5u) * 360.0f, Vector3.up);
                Matrix4x4 rootMatrix = Matrix4x4.TRS(position, rotation, new Vector3(width, height, width));
                AddParts(parts, rootMatrix);
                        }
                    }
                }
            }
        }

        private List<PrototypePart> GetDetailParts(DetailPrototype detail)
        {
            if (detail.prototype != null)
            {
                return GetPrefabParts(
                    detail.prototype,
                    settings.detailShadowCastingMode,
                    settings.detailReceiveShadows);
            }

            if (settings.bakeTextureDetailsAsCrossQuads && detail.prototypeTexture != null)
            {
                return GetTextureDetailParts(detail);
            }

            return new List<PrototypePart>();
        }

        private List<PrototypePart> GetPrefabParts(GameObject prefab, ShadowCastingMode shadowCastingMode, bool receiveShadows)
        {
            if (prefab == null)
            {
                return new List<PrototypePart>();
            }

            int cacheKey = prefab.GetInstanceID() ^
                           ((int)shadowCastingMode << 24) ^
                           (receiveShadows ? 0x40000000 : 0);
            if (prefabPartCache.TryGetValue(cacheKey, out List<PrototypePart> cached))
            {
                return cached;
            }

            List<PrototypePart> parts = new List<PrototypePart>();
            MeshFilter[] meshFilters = prefab.GetComponentsInChildren<MeshFilter>(true);
            for (int i = 0; i < meshFilters.Length; i++)
            {
                MeshFilter meshFilter = meshFilters[i];
                Mesh mesh = meshFilter.sharedMesh;
                if (mesh == null)
                {
                    continue;
                }

                MeshRenderer renderer = meshFilter.GetComponent<MeshRenderer>();
                Material[] materials = renderer != null ? renderer.sharedMaterials : null;
                Matrix4x4 meshLocalToPrefab = prefab.transform.worldToLocalMatrix * meshFilter.transform.localToWorldMatrix;
                AddMeshParts(prefab, mesh, materials, meshLocalToPrefab, shadowCastingMode, receiveShadows, parts);
            }

            SkinnedMeshRenderer[] skinnedRenderers = prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int i = 0; i < skinnedRenderers.Length; i++)
            {
                SkinnedMeshRenderer renderer = skinnedRenderers[i];
                Mesh mesh = renderer.sharedMesh;
                if (mesh == null)
                {
                    continue;
                }

                Matrix4x4 meshLocalToPrefab = prefab.transform.worldToLocalMatrix * renderer.transform.localToWorldMatrix;
                AddMeshParts(prefab, mesh, renderer.sharedMaterials, meshLocalToPrefab, shadowCastingMode, receiveShadows, parts);
            }

            if (parts.Count == 0)
            {
                Debug.LogWarning("Skipping terrain vegetation prefab without renderable mesh: " + prefab.name);
            }

            prefabPartCache.Add(cacheKey, parts);
            return parts;
        }

        private void AddMeshParts(
            GameObject prefab,
            Mesh mesh,
            Material[] materials,
            Matrix4x4 meshLocalToPrefab,
            ShadowCastingMode shadowCastingMode,
            bool receiveShadows,
            List<PrototypePart> parts)
        {
            int subMeshCount = Mathf.Max(1, mesh.subMeshCount);
            for (int subMeshIndex = 0; subMeshIndex < subMeshCount; subMeshIndex++)
            {
                Material material = null;
                if (materials != null && materials.Length > 0)
                {
                    material = materials[Mathf.Min(subMeshIndex, materials.Length - 1)];
                }

                int prototypeIndex = GetOrAddPrototype(
                    prefab,
                    mesh,
                    material,
                    subMeshIndex,
                    shadowCastingMode,
                    receiveShadows);
                parts.Add(new PrototypePart
                {
                    prototypeIndex = prototypeIndex,
                    localToRoot = meshLocalToPrefab
                });
            }
        }

        private List<PrototypePart> GetTextureDetailParts(DetailPrototype detail)
        {
            bool billboard = detail.renderMode == DetailRenderMode.GrassBillboard;
            int cacheKey = detail.prototypeTexture.GetInstanceID() ^ (billboard ? unchecked((int)0x80000000) : 0);
            if (textureDetailPartCache.TryGetValue(cacheKey, out List<PrototypePart> cached))
            {
                return cached;
            }

            Mesh mesh = CreateTextureDetailMesh(detail.prototypeTexture.name, billboard);
            AssetDatabase.AddObjectToAsset(mesh, outputData);
            EditorUtility.SetDirty(mesh);

            int prototypeIndex = GetOrAddPrototype(
                null,
                mesh,
                null,
                0,
                settings.detailShadowCastingMode,
                settings.detailReceiveShadows,
                billboard,
                detail.prototypeTexture,
                detail.healthyColor,
                settings.alphaCutoff);

            List<PrototypePart> parts = new List<PrototypePart>
            {
                new PrototypePart
                {
                    prototypeIndex = prototypeIndex,
                    localToRoot = Matrix4x4.identity
                }
            };
            textureDetailPartCache.Add(cacheKey, parts);
            return parts;
        }

        private int GetOrAddPrototype(
            GameObject prefab,
            Mesh mesh,
            Material material,
            int subMeshIndex,
            ShadowCastingMode shadowCastingMode,
            bool receiveShadows,
            bool billboard = false,
            Texture explicitBaseMap = null,
            Color explicitBaseColor = default,
            float explicitAlphaCutoff = 0.0f)
        {
            PrototypeKey key = new PrototypeKey(mesh, material, subMeshIndex, shadowCastingMode, receiveShadows, billboard);
            if (prototypeLookup.TryGetValue(key, out int prototypeIndex))
            {
                return prototypeIndex;
            }

            Material runtimeMaterial = CreateRuntimeMaterialAsset(
                material,
                mesh,
                subMeshIndex,
                explicitBaseMap,
                explicitBaseColor,
                explicitAlphaCutoff,
                billboard);
            Texture baseMap = ExtractBaseMap(runtimeMaterial);
            Color baseColor = ExtractBaseColor(runtimeMaterial);
            float alphaCutoff = ExtractCutoff(runtimeMaterial);
            prototypeIndex = prototypes.Count;
            prototypeLookup.Add(key, prototypeIndex);
            prototypes.Add(new GpuDrivenFoliagePrototype
            {
                prefab = prefab,
                mesh = mesh,
                sourceMaterial = runtimeMaterial,
                baseMap = baseMap,
                baseColor = baseColor,
                alphaCutoff = alphaCutoff,
                subMeshIndex = Mathf.Clamp(subMeshIndex, 0, Mathf.Max(0, mesh.subMeshCount - 1)),
                localBounds = mesh.bounds,
                shadowCastingMode = shadowCastingMode,
                receiveShadows = receiveShadows,
                billboard = billboard
            });
            return prototypeIndex;
        }

        private void AddParts(List<PrototypePart> parts, Matrix4x4 rootMatrix)
        {
            for (int i = 0; i < parts.Count; i++)
            {
                PrototypePart part = parts[i];
                if (part.prototypeIndex < 0 || part.prototypeIndex >= prototypes.Count)
                {
                    continue;
                }

                Matrix4x4 matrix = rootMatrix * part.localToRoot;
                instances.Add(GpuDrivenFoliageInstance.FromMatrix(part.prototypeIndex, matrix));
                Bounds instanceBounds = TransformBounds(prototypes[part.prototypeIndex].localBounds, matrix);
                if (!hasBounds)
                {
                    worldBounds = instanceBounds;
                    hasBounds = true;
                }
                else
                {
                    worldBounds.Encapsulate(instanceBounds);
                }
            }
        }

        private Mesh CreateTextureDetailMesh(string sourceName, bool billboard)
        {
            Mesh mesh = new Mesh
            {
                name = GeneratedSubAssetPrefix + " Mesh " + sourceName
            };
            if (billboard)
            {
                mesh.vertices = new[]
                {
                    new Vector3(-0.5f, 0.0f, 0.0f),
                    new Vector3(-0.5f, 1.0f, 0.0f),
                    new Vector3(0.5f, 1.0f, 0.0f),
                    new Vector3(0.5f, 0.0f, 0.0f)
                };
                mesh.uv = new[]
                {
                    new Vector2(0.0f, 0.0f),
                    new Vector2(0.0f, 1.0f),
                    new Vector2(1.0f, 1.0f),
                    new Vector2(1.0f, 0.0f)
                };
                mesh.triangles = new[]
                {
                    0, 1, 2,
                    0, 2, 3
                };
                mesh.RecalculateNormals();
                mesh.bounds = new Bounds(new Vector3(0.0f, 0.5f, 0.0f), Vector3.one);
                return mesh;
            }

            Vector3[] vertices =
            {
                new Vector3(-0.5f, 0.0f, 0.0f),
                new Vector3(-0.5f, 1.0f, 0.0f),
                new Vector3(0.5f, 1.0f, 0.0f),
                new Vector3(0.5f, 0.0f, 0.0f),
                new Vector3(0.0f, 0.0f, -0.5f),
                new Vector3(0.0f, 1.0f, -0.5f),
                new Vector3(0.0f, 1.0f, 0.5f),
                new Vector3(0.0f, 0.0f, 0.5f)
            };
            Vector2[] uvs =
            {
                new Vector2(0.0f, 0.0f),
                new Vector2(0.0f, 1.0f),
                new Vector2(1.0f, 1.0f),
                new Vector2(1.0f, 0.0f),
                new Vector2(0.0f, 0.0f),
                new Vector2(0.0f, 1.0f),
                new Vector2(1.0f, 1.0f),
                new Vector2(1.0f, 0.0f)
            };
            int[] triangles =
            {
                0, 1, 2,
                0, 2, 3,
                4, 5, 6,
                4, 6, 7
            };
            mesh.vertices = vertices;
            mesh.uv = uvs;
            mesh.triangles = triangles;
            mesh.RecalculateNormals();
            mesh.bounds = new Bounds(new Vector3(0.0f, 0.5f, 0.0f), Vector3.one);
            return mesh;
        }

        private Material CreateRuntimeMaterialAsset(
            Material source,
            Mesh mesh,
            int subMeshIndex,
            Texture explicitBaseMap,
            Color explicitBaseColor,
            float explicitAlphaCutoff,
            bool billboard)
        {
            Shader shader = Shader.Find("GPU Driven/Foliage Indirect");
            if (shader == null)
            {
                shader = Shader.Find("Universal Render Pipeline/Unlit");
            }

            Material material = new Material(shader)
            {
                name = GeneratedSubAssetPrefix + " Material " + (mesh != null ? mesh.name : "Foliage") + " Sub" + subMeshIndex
            };
            Texture baseMap = explicitBaseMap != null ? explicitBaseMap : ExtractSourceTexture(source, SourceTextureRole.BaseMap);
            Color baseColor = explicitBaseColor.a > 0.0f ? explicitBaseColor : ExtractBaseColor(source);
            float cutoff = explicitAlphaCutoff > 0.0f ? explicitAlphaCutoff : ExtractCutoff(source);
            Texture normalMap = ExtractSourceTexture(source, SourceTextureRole.NormalMap);
            Texture maskMap = ExtractSourceTexture(source, SourceTextureRole.MaskMap);
            Texture metallicGlossMap = ExtractSourceTexture(source, SourceTextureRole.MetallicGlossMap);
            Texture occlusionMap = ExtractSourceTexture(source, SourceTextureRole.OcclusionMap);
            Texture emissionMap = ExtractSourceTexture(source, SourceTextureRole.EmissionMap);
            if (material.HasProperty(BaseMapProperty))
            {
                material.SetTexture(BaseMapProperty, baseMap);
            }
            if (material.HasProperty(MainTexProperty))
            {
                material.SetTexture(MainTexProperty, baseMap);
            }
            if (material.HasProperty(BaseColorProperty))
            {
                material.SetColor(BaseColorProperty, baseColor);
            }
            if (material.HasProperty(ColorProperty))
            {
                material.SetColor(ColorProperty, baseColor);
            }
            if (material.HasProperty(CutoffProperty))
            {
                material.SetFloat(CutoffProperty, cutoff);
            }
            SetTextureIfPresent(material, BumpMapProperty, normalMap);
            SetTextureIfPresent(material, NormalMapProperty, normalMap);
            CopyFloat(source, material, BumpScaleProperty);
            SetTextureIfPresent(material, MaskMapProperty, maskMap ?? metallicGlossMap);
            SetTextureIfPresent(material, MetallicGlossMapProperty, metallicGlossMap ?? maskMap);
            SetTextureIfPresent(material, OcclusionMapProperty, occlusionMap);
            SetTextureIfPresent(material, EmissionMapProperty, emissionMap);
            if (material.HasProperty(BillboardProperty))
            {
                material.SetFloat(BillboardProperty, billboard ? 1.0f : 0.0f);
            }
            AssetDatabase.AddObjectToAsset(material, outputData);
            EditorUtility.SetDirty(material);
            return material;
        }

        private static void SetTextureIfPresent(Material target, string targetProperty, Texture texture)
        {
            if (target != null && texture != null && target.HasProperty(targetProperty))
            {
                target.SetTexture(targetProperty, texture);
            }
        }

        private static void CopyFloat(Material source, Material target, string property)
        {
            if (source != null && target != null && source.HasProperty(property) && target.HasProperty(property))
            {
                target.SetFloat(property, source.GetFloat(property));
            }
        }

        private static Texture ExtractBaseMap(Material material)
        {
            return ExtractSourceTexture(material, SourceTextureRole.BaseMap);
        }

        private static Texture ExtractSourceTexture(Material material, SourceTextureRole role)
        {
            if (material == null)
            {
                return null;
            }

            string[] preferredProperties = GetPreferredTextureProperties(role);
            for (int i = 0; i < preferredProperties.Length; i++)
            {
                if (TryGetTexture(material, preferredProperties[i], out Texture texture))
                {
                    return texture;
                }
            }

            Texture bestTexture = null;
            int bestScore = 0;
            string[] textureProperties = material.GetTexturePropertyNames();
            for (int i = 0; i < textureProperties.Length; i++)
            {
                string propertyName = textureProperties[i];
                if (!TryGetTexture(material, propertyName, out Texture texture))
                {
                    continue;
                }

                // ShaderGraph 会把公开贴图序列化成 Texture2D_xxx 这类随机属性名，
                // 因此这里同时参考属性名、贴图名、资源路径和 importer 类型来识别用途。
                int score = ScoreSourceTexture(role, propertyName, texture);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestTexture = texture;
                }
            }

            return bestTexture;
        }

        private static string[] GetPreferredTextureProperties(SourceTextureRole role)
        {
            switch (role)
            {
                case SourceTextureRole.BaseMap:
                    return new[]
                    {
                        BaseMapProperty,
                        MainTexProperty,
                        "_BaseColorMap",
                        "_BaseColorTexture",
                        "_AlbedoMap",
                        "_AlbedoTexture",
                        "_DiffuseMap"
                    };
                case SourceTextureRole.NormalMap:
                    return new[]
                    {
                        BumpMapProperty,
                        NormalMapProperty,
                        "_Normal",
                        "_NormalTexture"
                    };
                case SourceTextureRole.MaskMap:
                    return new[]
                    {
                        MaskMapProperty,
                        "_PackedMaskMap",
                        "_MaskTexture"
                    };
                case SourceTextureRole.MetallicGlossMap:
                    return new[]
                    {
                        MetallicGlossMapProperty,
                        "_MetallicMap",
                        "_MetallicGloss",
                        "_SpecGlossMap"
                    };
                case SourceTextureRole.OcclusionMap:
                    return new[]
                    {
                        OcclusionMapProperty,
                        "_AOMap",
                        "_AmbientOcclusionMap",
                        "_OcclusionTexture"
                    };
                case SourceTextureRole.EmissionMap:
                    return new[]
                    {
                        EmissionMapProperty,
                        "_EmissionTexture",
                        "_EmissiveMap"
                    };
                default:
                    return Array.Empty<string>();
            }
        }

        private static bool TryGetTexture(Material material, string propertyName, out Texture texture)
        {
            texture = null;
            if (material == null || string.IsNullOrEmpty(propertyName) || !material.HasProperty(propertyName))
            {
                return false;
            }

            texture = material.GetTexture(propertyName);
            return texture != null;
        }

        private static int ScoreSourceTexture(SourceTextureRole role, string propertyName, Texture texture)
        {
            string key = BuildTextureSearchKey(propertyName, texture);
            TextureImporterType importerType = GetTextureImporterType(texture);
            switch (role)
            {
                case SourceTextureRole.BaseMap:
                    return ScoreBaseMap(key, importerType);
                case SourceTextureRole.NormalMap:
                    return ScoreNormalMap(key, importerType);
                case SourceTextureRole.MaskMap:
                    return ScoreMaskMap(key);
                case SourceTextureRole.MetallicGlossMap:
                    return ScoreMetallicGlossMap(key);
                case SourceTextureRole.OcclusionMap:
                    return ScoreOcclusionMap(key);
                case SourceTextureRole.EmissionMap:
                    return ScoreEmissionMap(key);
                default:
                    return 0;
            }
        }

        private static string BuildTextureSearchKey(string propertyName, Texture texture)
        {
            string textureName = texture != null ? texture.name : string.Empty;
            string assetPath = texture != null ? AssetDatabase.GetAssetPath(texture) : string.Empty;
            return ((propertyName ?? string.Empty) + " " + textureName + " " + assetPath).ToLowerInvariant();
        }

        private static TextureImporterType GetTextureImporterType(Texture texture)
        {
            string path = texture != null ? AssetDatabase.GetAssetPath(texture) : string.Empty;
            TextureImporter importer = !string.IsNullOrEmpty(path)
                ? AssetImporter.GetAtPath(path) as TextureImporter
                : null;
            return importer != null ? importer.textureType : TextureImporterType.Default;
        }

        private static int ScoreBaseMap(string key, TextureImporterType importerType)
        {
            if (importerType == TextureImporterType.NormalMap)
            {
                return 0;
            }

            int score = 0;
            if (ContainsAny(key, "basecolor", "base_color", "base-color", "basemap", "base_map"))
            {
                score += 160;
            }
            if (ContainsAny(key, "albedo", "diffuse"))
            {
                score += 130;
            }
            if (ContainsAny(key, "maintex", "main_tex"))
            {
                score += 110;
            }
            if (key.Contains("color"))
            {
                score += 45;
            }
            if (ContainsAny(key, "terraincolorblend", "normal", "bump", "mask", "metallic", "smoothness", "roughness", "occlusion", "emission", "emissive", "thickness", "height"))
            {
                score -= 120;
            }
            return Mathf.Max(0, score);
        }

        private static int ScoreNormalMap(string key, TextureImporterType importerType)
        {
            int score = importerType == TextureImporterType.NormalMap ? 160 : 0;
            if (ContainsAny(key, "normal", "normalmap", "normal_map"))
            {
                score += 140;
            }
            if (ContainsAny(key, "bump", "_nrm", "-nrm"))
            {
                score += 90;
            }
            if (ContainsAny(key, "basecolor", "albedo", "diffuse", "mask", "emission", "emissive", "thickness"))
            {
                score -= 80;
            }
            return Mathf.Max(0, score);
        }

        private static int ScoreMaskMap(string key)
        {
            int score = 0;
            if (ContainsAny(key, "maskmap", "mask_map", "packedmask", "packed_mask"))
            {
                score += 160;
            }
            if (key.Contains("mask"))
            {
                score += 100;
            }
            if (ContainsAny(key, "normal", "bump", "basecolor", "albedo", "diffuse", "emission", "emissive", "thickness"))
            {
                score -= 80;
            }
            return Mathf.Max(0, score);
        }

        private static int ScoreMetallicGlossMap(string key)
        {
            int score = 0;
            if (ContainsAny(key, "metallicgloss", "metallic_gloss", "specgloss", "spec_gloss"))
            {
                score += 160;
            }
            if (ContainsAny(key, "metallic", "smoothness", "roughness", "gloss"))
            {
                score += 110;
            }
            if (ContainsAny(key, "normal", "bump", "basecolor", "albedo", "diffuse", "emission", "emissive", "thickness"))
            {
                score -= 80;
            }
            return Mathf.Max(0, score);
        }

        private static int ScoreOcclusionMap(string key)
        {
            int score = 0;
            if (ContainsAny(key, "ambientocclusion", "ambient_occlusion", "occlusion"))
            {
                score += 150;
            }
            if (ContainsSeparatedToken(key, "ao"))
            {
                score += 110;
            }
            if (ContainsAny(key, "normal", "bump", "basecolor", "albedo", "diffuse", "emission", "emissive"))
            {
                score -= 80;
            }
            return Mathf.Max(0, score);
        }

        private static int ScoreEmissionMap(string key)
        {
            int score = 0;
            if (ContainsAny(key, "emission", "emissive"))
            {
                score += 150;
            }
            if (ContainsAny(key, "normal", "bump", "basecolor", "albedo", "diffuse", "mask", "thickness"))
            {
                score -= 80;
            }
            return Mathf.Max(0, score);
        }

        private static bool ContainsAny(string value, params string[] tokens)
        {
            for (int i = 0; i < tokens.Length; i++)
            {
                if (value.Contains(tokens[i]))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool ContainsSeparatedToken(string value, string token)
        {
            int index = value.IndexOf(token, StringComparison.Ordinal);
            while (index >= 0)
            {
                bool leftSeparated = index == 0 || !char.IsLetterOrDigit(value[index - 1]);
                int rightIndex = index + token.Length;
                bool rightSeparated = rightIndex >= value.Length || !char.IsLetterOrDigit(value[rightIndex]);
                if (leftSeparated && rightSeparated)
                {
                    return true;
                }

                index = value.IndexOf(token, index + token.Length, StringComparison.Ordinal);
            }
            return false;
        }

        private static Color ExtractBaseColor(Material material)
        {
            if (material == null)
            {
                return Color.white;
            }

            if (material.HasProperty(BaseColorProperty))
            {
                return material.GetColor(BaseColorProperty);
            }
            return material.HasProperty(ColorProperty) ? material.GetColor(ColorProperty) : Color.white;
        }

        private float ExtractCutoff(Material material)
        {
            if (material == null)
            {
                return settings.alphaCutoff;
            }

            if (material.HasProperty(CutoffProperty))
            {
                return Mathf.Clamp01(material.GetFloat(CutoffProperty));
            }
            return material.HasProperty(AlphaCutoffProperty)
                ? Mathf.Clamp01(material.GetFloat(AlphaCutoffProperty))
                : settings.alphaCutoff;
        }
    }

    private struct PrototypePart
    {
        public int prototypeIndex;
        public Matrix4x4 localToRoot;
    }

    private struct PrototypeKey : IEquatable<PrototypeKey>
    {
        private readonly int meshId;
        private readonly int materialId;
        private readonly int subMeshIndex;
        private readonly ShadowCastingMode shadowCastingMode;
        private readonly bool receiveShadows;
        private readonly bool billboard;

        public PrototypeKey(
            Mesh mesh,
            Material material,
            int subMeshIndex,
            ShadowCastingMode shadowCastingMode,
            bool receiveShadows,
            bool billboard)
        {
            meshId = mesh != null ? mesh.GetInstanceID() : 0;
            materialId = material != null ? material.GetInstanceID() : 0;
            this.subMeshIndex = subMeshIndex;
            this.shadowCastingMode = shadowCastingMode;
            this.receiveShadows = receiveShadows;
            this.billboard = billboard;
        }

        public bool Equals(PrototypeKey other)
        {
            return meshId == other.meshId &&
                   materialId == other.materialId &&
                   subMeshIndex == other.subMeshIndex &&
                   shadowCastingMode == other.shadowCastingMode &&
                   receiveShadows == other.receiveShadows &&
                   billboard == other.billboard;
        }

        public override bool Equals(object obj)
        {
            return obj is PrototypeKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hashCode = meshId;
                hashCode = (hashCode * 397) ^ materialId;
                hashCode = (hashCode * 397) ^ subMeshIndex;
                hashCode = (hashCode * 397) ^ (int)shadowCastingMode;
                hashCode = (hashCode * 397) ^ (receiveShadows ? 1 : 0);
                hashCode = (hashCode * 397) ^ (billboard ? 1 : 0);
                return hashCode;
            }
        }
    }
}
