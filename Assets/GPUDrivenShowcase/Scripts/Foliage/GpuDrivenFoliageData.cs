using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

[CreateAssetMenu(menuName = "GPU Driven/Foliage Data", fileName = "GpuDrivenFoliageData")]
public sealed class GpuDrivenFoliageData : ScriptableObject
{
    [SerializeField] private List<GpuDrivenFoliagePrototype> prototypes = new List<GpuDrivenFoliagePrototype>();
    [SerializeField] private List<GpuDrivenFoliageInstance> instances = new List<GpuDrivenFoliageInstance>();
    [SerializeField] private Bounds worldBounds = new Bounds(Vector3.zero, Vector3.one);

    public IReadOnlyList<GpuDrivenFoliagePrototype> Prototypes => prototypes;
    public IReadOnlyList<GpuDrivenFoliageInstance> Instances => instances;
    public Bounds WorldBounds => worldBounds;
    public int PrototypeCount => prototypes != null ? prototypes.Count : 0;
    public int InstanceCount => instances != null ? instances.Count : 0;

    public void SetBakedData(
        IList<GpuDrivenFoliagePrototype> bakedPrototypes,
        IList<GpuDrivenFoliageInstance> bakedInstances,
        Bounds bakedWorldBounds)
    {
        prototypes = bakedPrototypes != null
            ? new List<GpuDrivenFoliagePrototype>(bakedPrototypes)
            : new List<GpuDrivenFoliagePrototype>();
        instances = bakedInstances != null
            ? new List<GpuDrivenFoliageInstance>(bakedInstances)
            : new List<GpuDrivenFoliageInstance>();
        worldBounds = bakedWorldBounds;
    }
}

[Serializable]
public struct GpuDrivenFoliagePrototype
{
    public GameObject prefab;
    public Mesh mesh;
    public Material sourceMaterial;
    public int subMeshIndex;
    public Bounds localBounds;
    public ShadowCastingMode shadowCastingMode;
    public bool receiveShadows;
}

[Serializable]
public struct GpuDrivenFoliageInstance
{
    public int prototypeIndex;
    public Vector4 row0;
    public Vector4 row1;
    public Vector4 row2;
    public Vector4 row3;

    public Matrix4x4 LocalToWorld
    {
        get
        {
            Matrix4x4 matrix = Matrix4x4.identity;
            matrix.SetRow(0, row0);
            matrix.SetRow(1, row1);
            matrix.SetRow(2, row2);
            matrix.SetRow(3, row3);
            return matrix;
        }
    }

    public static GpuDrivenFoliageInstance FromMatrix(int prototypeIndex, Matrix4x4 localToWorld)
    {
        return new GpuDrivenFoliageInstance
        {
            prototypeIndex = prototypeIndex,
            row0 = localToWorld.GetRow(0),
            row1 = localToWorld.GetRow(1),
            row2 = localToWorld.GetRow(2),
            row3 = localToWorld.GetRow(3)
        };
    }
}
