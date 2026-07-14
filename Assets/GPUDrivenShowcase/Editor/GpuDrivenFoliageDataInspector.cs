using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(GpuDrivenFoliageData))]
public sealed class GpuDrivenFoliageDataInspector : Editor
{
    private const int MaxVisiblePrototypes = 64;

    private bool showPrototypes = true;
    private int[] cachedInstanceCounts;
    private int cachedTotalInstances = -1;

    public override void OnInspectorGUI()
    {
        GpuDrivenFoliageData data = (GpuDrivenFoliageData)target;
        if (data == null)
        {
            return;
        }

        EditorGUILayout.LabelField("GPU Driven Foliage Data", EditorStyles.boldLabel);
        using (new EditorGUI.DisabledScope(true))
        {
            EditorGUILayout.IntField("Prototype Count", data.PrototypeCount);
            EditorGUILayout.IntField("Instance Count", data.InstanceCount);
            Bounds bounds = data.WorldBounds;
            EditorGUILayout.Vector3Field("Bounds Center", bounds.center);
            EditorGUILayout.Vector3Field("Bounds Size", bounds.size);
        }

        EditorGUILayout.Space(6.0f);
        EditorGUILayout.HelpBox(
            "实例矩阵数据量可能很大，Inspector 默认不展开 instances 列表，避免选中资产时卡死。",
            MessageType.Info);

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Open Terrain Bake Tool"))
            {
                GpuTerrainBakedDataEditor.OpenWindow();
            }

            if (GUILayout.Button("Refresh Instance Counts"))
            {
                RebuildInstanceCountCache(data);
            }
        }

        EditorGUILayout.Space(6.0f);
        DrawPrototypeSummary(data);
    }

    private void DrawPrototypeSummary(GpuDrivenFoliageData data)
    {
        showPrototypes = EditorGUILayout.Foldout(showPrototypes, "Prototypes", true);
        if (!showPrototypes)
        {
            return;
        }

        IReadOnlyList<GpuDrivenFoliagePrototype> prototypes = data.Prototypes;
        int visibleCount = Mathf.Min(data.PrototypeCount, MaxVisiblePrototypes);
        for (int i = 0; i < visibleCount; i++)
        {
            GpuDrivenFoliagePrototype prototype = prototypes[i];
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                string countText = cachedInstanceCounts != null && i < cachedInstanceCounts.Length
                    ? "  instances: " + cachedInstanceCounts[i]
                    : string.Empty;
                EditorGUILayout.LabelField("#" + i + countText, EditorStyles.boldLabel);
                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.ObjectField("Prefab", prototype.prefab, typeof(GameObject), false);
                    EditorGUILayout.ObjectField("Mesh", prototype.mesh, typeof(Mesh), false);
                    EditorGUILayout.ObjectField("Material", prototype.sourceMaterial, typeof(Material), false);
                    EditorGUILayout.ObjectField("Base Map", prototype.baseMap, typeof(Texture), false);
                    EditorGUILayout.IntField("Sub Mesh", prototype.subMeshIndex);
                    EditorGUILayout.Toggle("Billboard", prototype.billboard);
                    EditorGUILayout.BoundsField("Local Bounds", prototype.localBounds);
                }
            }
        }

        if (data.PrototypeCount > MaxVisiblePrototypes)
        {
            EditorGUILayout.HelpBox(
                "Only showing first " + MaxVisiblePrototypes + " prototypes out of " + data.PrototypeCount + ".",
                MessageType.None);
        }

        if (cachedInstanceCounts != null)
        {
            EditorGUILayout.LabelField("Cached Count Total", cachedTotalInstances.ToString());
        }
    }

    private void RebuildInstanceCountCache(GpuDrivenFoliageData data)
    {
        cachedInstanceCounts = new int[data.PrototypeCount];
        cachedTotalInstances = 0;
        IReadOnlyList<GpuDrivenFoliageInstance> instances = data.Instances;
        for (int i = 0; i < instances.Count; i++)
        {
            int prototypeIndex = instances[i].prototypeIndex;
            if (prototypeIndex >= 0 && prototypeIndex < cachedInstanceCounts.Length)
            {
                cachedInstanceCounts[prototypeIndex]++;
                cachedTotalInstances++;
            }
        }
    }
}
