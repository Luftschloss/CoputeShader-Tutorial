using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(GpuTerrainBakedData))]
public sealed class GpuTerrainBakedDataInspector : Editor
{
    public override bool HasPreviewGUI()
    {
        return false;
    }

    public override void OnInspectorGUI()
    {
        GpuTerrainBakedData data = (GpuTerrainBakedData)target;
        if (data == null)
        {
            return;
        }

        EditorGUILayout.LabelField("GPU Terrain Baked Data", EditorStyles.boldLabel);
        using (new EditorGUI.DisabledScope(true))
        {
            EditorGUILayout.Toggle("Valid", data.IsValid);
            EditorGUILayout.FloatField("Root Patch Size", data.PatchSize);
            EditorGUILayout.IntField("LOD Levels", data.LodCount);
            EditorGUILayout.IntField("Terrain Count", data.TerrainCount);
            EditorGUILayout.IntField("Root Node Count", data.RootNodeCount);
            EditorGUILayout.IntField("Total Node Count", data.NodeCount);
        }

        GUILayout.Space(8.0f);
        EditorGUILayout.LabelField("Texture Arrays", EditorStyles.boldLabel);
        DrawTextureArraySummary("Height Map", data.HeightMapArray);
        DrawTextureArraySummary("Normal Map", data.NormalMapArray);

        GUILayout.Space(8.0f);
        EditorGUILayout.HelpBox(
            "Large baked arrays are intentionally hidden and preview is disabled to avoid Editor stalls.",
            MessageType.Info);

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Open Bake Tool"))
            {
                GpuTerrainBakedDataEditor.OpenWindow();
            }

            if (GUILayout.Button("Copy Asset Path"))
            {
                EditorGUIUtility.systemCopyBuffer = AssetDatabase.GetAssetPath(data);
            }
        }
    }

    private static void DrawTextureArraySummary(string label, Texture2DArray textureArray)
    {
        using (new EditorGUI.DisabledScope(true))
        {
            if (textureArray == null)
            {
                EditorGUILayout.LabelField(label, "None");
                return;
            }

            string summary = textureArray.width + "x" + textureArray.height +
                             " x " + textureArray.depth +
                             " slices, " + textureArray.format;
            EditorGUILayout.LabelField(label, summary);
        }
    }
}

[CustomPreview(typeof(GpuTerrainBakedData))]
public sealed class GpuTerrainBakedDataPreview : ObjectPreview
{
    public override bool HasPreviewGUI()
    {
        return false;
    }

    public override void OnPreviewGUI(Rect r, GUIStyle background)
    {
    }
}
