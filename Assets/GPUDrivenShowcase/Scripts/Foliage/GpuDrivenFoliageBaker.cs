using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

public sealed class GpuDrivenFoliageBaker : MonoBehaviour
{
    [Header("Output")]
    public Terrain terrain;
    public GpuDrivenFoliageData outputData;
    public GpuDrivenFoliageRenderer assignToRenderer;
    public string defaultAssetPath = "Assets/GPUDrivenShowcase/Generated/GpuDrivenFoliageData.asset";

    [Header("Random Distribution")]
    [Min(0)] public int seed = 12345;
    [Min(0)] public int instanceCount = 20000;
    [Range(1, 100)] public int maxAttemptsPerInstance = 20;

    [Header("Prototype Settings")]
    public List<GpuDrivenFoliagePlacementPrototype> prototypes = new List<GpuDrivenFoliagePlacementPrototype>();
}

[Serializable]
public sealed class GpuDrivenFoliagePlacementPrototype
{
    public GameObject prefab;
    public Material materialOverride;
    [Min(0.0f)] public float weight = 1.0f;
    public Vector2 uniformScaleRange = new Vector2(0.8f, 1.25f);
    public Vector2 slopeRange = new Vector2(0.0f, 35.0f);
    public Vector2 heightRange = new Vector2(-10000.0f, 10000.0f);
    public float yOffset;
    public bool alignToTerrainNormal;
    public bool randomYaw = true;
    public int subMeshIndex;
    public ShadowCastingMode shadowCastingMode = ShadowCastingMode.On;
    public bool receiveShadows = true;
}
