using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(GpuDrivenFoliageBaker))]
public sealed class GpuDrivenFoliageBakerEditor : Editor
{
    [MenuItem("GPU Driven Showcase/Create Foliage Baker")]
    private static void CreateFoliageBaker()
    {
        GameObject gameObject = new GameObject("GPU Driven Foliage");
        Undo.RegisterCreatedObjectUndo(gameObject, "Create GPU Driven Foliage");

        GpuDrivenFoliageRenderer renderer = gameObject.AddComponent<GpuDrivenFoliageRenderer>();
        GpuDrivenFoliageBaker baker = gameObject.AddComponent<GpuDrivenFoliageBaker>();
        baker.assignToRenderer = renderer;
        baker.terrain = Object.FindObjectOfType<Terrain>(true);

        Selection.activeGameObject = gameObject;
        EditorGUIUtility.PingObject(gameObject);
    }

    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        GpuDrivenFoliageBaker baker = (GpuDrivenFoliageBaker)target;
        GUILayout.Space(8);
        if (GUILayout.Button("Bake GPU Driven Foliage Data", GUILayout.Height(32)))
        {
            Bake(baker);
        }
    }

    private static void Bake(GpuDrivenFoliageBaker baker)
    {
        if (baker.terrain == null || baker.terrain.terrainData == null)
        {
            Debug.LogError("GpuDrivenFoliageBaker requires a Terrain.");
            return;
        }

        List<ResolvedPrototype> resolvedPrototypes = ResolvePrototypes(baker);
        if (resolvedPrototypes.Count == 0)
        {
            Debug.LogError("GpuDrivenFoliageBaker has no valid foliage prefab prototypes.");
            return;
        }

        GpuDrivenFoliageData outputData = EnsureOutputData(baker);
        if (outputData == null)
        {
            return;
        }

        Random.State previousState = Random.state;
        Random.InitState(baker.seed);

        Terrain terrain = baker.terrain;
        TerrainData terrainData = terrain.terrainData;
        Vector3 terrainPosition = terrain.transform.position;
        Vector3 terrainSize = terrainData.size;
        Bounds worldBounds = new Bounds();
        bool hasBounds = false;
        List<GpuDrivenFoliageInstance> instances = new List<GpuDrivenFoliageInstance>(baker.instanceCount);

        int attempts = 0;
        int maxAttempts = Mathf.Max(1, baker.instanceCount * Mathf.Max(1, baker.maxAttemptsPerInstance));
        while (instances.Count < baker.instanceCount && attempts < maxAttempts)
        {
            attempts++;
            int prototypeIndex = PickPrototype(resolvedPrototypes);
            ResolvedPrototype resolved = resolvedPrototypes[prototypeIndex];
            GpuDrivenFoliagePlacementPrototype placement = resolved.placement;

            float x = terrainPosition.x + Random.value * terrainSize.x;
            float z = terrainPosition.z + Random.value * terrainSize.z;
            float uvX = Mathf.InverseLerp(terrainPosition.x, terrainPosition.x + terrainSize.x, x);
            float uvZ = Mathf.InverseLerp(terrainPosition.z, terrainPosition.z + terrainSize.z, z);
            float height = terrain.SampleHeight(new Vector3(x, 0.0f, z)) + terrainPosition.y;
            Vector3 normal = terrainData.GetInterpolatedNormal(uvX, uvZ).normalized;
            float slope = Vector3.Angle(normal, Vector3.up);

            if (height < placement.heightRange.x || height > placement.heightRange.y ||
                slope < placement.slopeRange.x || slope > placement.slopeRange.y)
            {
                continue;
            }

            float scale = Random.Range(
                Mathf.Min(placement.uniformScaleRange.x, placement.uniformScaleRange.y),
                Mathf.Max(placement.uniformScaleRange.x, placement.uniformScaleRange.y));
            Quaternion rotation = placement.alignToTerrainNormal
                ? Quaternion.FromToRotation(Vector3.up, normal)
                : Quaternion.identity;
            if (placement.randomYaw)
            {
                rotation = Quaternion.AngleAxis(Random.Range(0.0f, 360.0f), Vector3.up) * rotation;
            }

            Vector3 position = new Vector3(x, height + placement.yOffset, z);
            Matrix4x4 matrix = Matrix4x4.TRS(position, rotation, Vector3.one * scale) * resolved.meshLocalToPrefab;
            instances.Add(GpuDrivenFoliageInstance.FromMatrix(prototypeIndex, matrix));

            Bounds instanceBounds = TransformBounds(resolved.prototype.localBounds, matrix);
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

        Random.state = previousState;

        List<GpuDrivenFoliagePrototype> prototypes = new List<GpuDrivenFoliagePrototype>(resolvedPrototypes.Count);
        for (int i = 0; i < resolvedPrototypes.Count; i++)
        {
            prototypes.Add(resolvedPrototypes[i].prototype);
        }

        if (!hasBounds)
        {
            worldBounds = new Bounds(terrainPosition + terrainSize * 0.5f, terrainSize);
        }

        outputData.SetBakedData(prototypes, instances, worldBounds);
        EditorUtility.SetDirty(outputData);
        AssetDatabase.SaveAssets();

        if (baker.assignToRenderer != null)
        {
            Undo.RecordObject(baker.assignToRenderer, "Assign Foliage Data");
            baker.assignToRenderer.SetFoliageData(outputData);
            EditorUtility.SetDirty(baker.assignToRenderer);
        }

        Debug.Log("Baked " + instances.Count + " GPU driven foliage instances into " +
                  AssetDatabase.GetAssetPath(outputData) + ".");
    }

    private static List<ResolvedPrototype> ResolvePrototypes(GpuDrivenFoliageBaker baker)
    {
        List<ResolvedPrototype> resolved = new List<ResolvedPrototype>();
        if (baker.prototypes == null)
        {
            return resolved;
        }

        for (int i = 0; i < baker.prototypes.Count; i++)
        {
            GpuDrivenFoliagePlacementPrototype placement = baker.prototypes[i];
            if (placement == null || placement.prefab == null || placement.weight <= 0.0f)
            {
                continue;
            }

            MeshFilter meshFilter = placement.prefab.GetComponentInChildren<MeshFilter>(true);
            MeshRenderer meshRenderer = placement.prefab.GetComponentInChildren<MeshRenderer>(true);
            if (meshFilter == null || meshFilter.sharedMesh == null)
            {
                Debug.LogWarning("Skipping foliage prefab without MeshFilter: " + placement.prefab.name);
                continue;
            }

            Mesh mesh = meshFilter.sharedMesh;
            int subMeshIndex = Mathf.Clamp(placement.subMeshIndex, 0, Mathf.Max(0, mesh.subMeshCount - 1));
            Material material = placement.materialOverride;
            if (material == null && meshRenderer != null)
            {
                Material[] materials = meshRenderer.sharedMaterials;
                if (materials != null && materials.Length > 0)
                {
                    material = materials[Mathf.Min(subMeshIndex, materials.Length - 1)];
                }
            }

            GpuDrivenFoliagePrototype prototype = new GpuDrivenFoliagePrototype
            {
                prefab = placement.prefab,
                mesh = mesh,
                sourceMaterial = material,
                subMeshIndex = subMeshIndex,
                localBounds = mesh.bounds,
                shadowCastingMode = placement.shadowCastingMode,
                receiveShadows = placement.receiveShadows
            };

            resolved.Add(new ResolvedPrototype
            {
                placement = placement,
                prototype = prototype,
                meshLocalToPrefab = placement.prefab.transform.worldToLocalMatrix * meshFilter.transform.localToWorldMatrix,
                cumulativeWeight = placement.weight
            });
        }

        float cumulative = 0.0f;
        for (int i = 0; i < resolved.Count; i++)
        {
            cumulative += resolved[i].cumulativeWeight;
            ResolvedPrototype item = resolved[i];
            item.cumulativeWeight = cumulative;
            resolved[i] = item;
        }

        return resolved;
    }

    private static int PickPrototype(List<ResolvedPrototype> resolvedPrototypes)
    {
        float totalWeight = resolvedPrototypes[resolvedPrototypes.Count - 1].cumulativeWeight;
        float value = Random.value * totalWeight;
        for (int i = 0; i < resolvedPrototypes.Count; i++)
        {
            if (value <= resolvedPrototypes[i].cumulativeWeight)
            {
                return i;
            }
        }

        return resolvedPrototypes.Count - 1;
    }

    private static GpuDrivenFoliageData EnsureOutputData(GpuDrivenFoliageBaker baker)
    {
        if (baker.outputData != null)
        {
            return baker.outputData;
        }

        string path = string.IsNullOrEmpty(baker.defaultAssetPath)
            ? "Assets/GPUDrivenShowcase/Generated/GpuDrivenFoliageData.asset"
            : baker.defaultAssetPath;
        string directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
            AssetDatabase.Refresh();
        }

        GpuDrivenFoliageData data = ScriptableObject.CreateInstance<GpuDrivenFoliageData>();
        AssetDatabase.CreateAsset(data, AssetDatabase.GenerateUniqueAssetPath(path));
        Undo.RecordObject(baker, "Assign Foliage Data");
        baker.outputData = data;
        EditorUtility.SetDirty(baker);
        return data;
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

    private struct ResolvedPrototype
    {
        public GpuDrivenFoliagePlacementPrototype placement;
        public GpuDrivenFoliagePrototype prototype;
        public Matrix4x4 meshLocalToPrefab;
        public float cumulativeWeight;
    }
}
