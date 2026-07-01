using UnityEngine;

public enum GpuDrivenShowcaseCullingMode
{
    None = 0,
    Frustum = 1,
    FrustumAndHiZ = 2
}

public enum GpuDrivenShowcaseDebugView
{
    None = 0,
    SceneWire = 1
}

public struct GpuDrivenShowcaseStats
{
    public float cpuFrameMs;
    public int terrainPatchCount;
    public int terrainVisiblePatchCount;
    public int terrainFrustumVisiblePatchCount;
    public int terrainFrustumRejectedPatchCount;
    public int terrainHiZTestedPatchCount;
    public int terrainHiZRejectedPatchCount;
    public int foliageInstanceCount;
    public int foliageVisibleInstanceCount;
    public bool hizEnabled;
    public string status;
}

public interface IGpuDrivenShowcaseModule
{
    string DisplayName { get; }
    void SetCullingMode(GpuDrivenShowcaseCullingMode mode);
    void SetDebugView(GpuDrivenShowcaseDebugView view);
    void SetDebugColorMode(bool enabled);
    void CollectStats(ref GpuDrivenShowcaseStats stats);
}

public static class GpuDrivenShowcaseModeUtility
{
    public static bool UsesFrustum(this GpuDrivenShowcaseCullingMode mode)
    {
        return mode == GpuDrivenShowcaseCullingMode.Frustum ||
               mode == GpuDrivenShowcaseCullingMode.FrustumAndHiZ;
    }

    public static bool UsesHiZ(this GpuDrivenShowcaseCullingMode mode)
    {
        return mode == GpuDrivenShowcaseCullingMode.FrustumAndHiZ;
    }

    public static string ToDisplayName(this GpuDrivenShowcaseCullingMode mode)
    {
        switch (mode)
        {
            case GpuDrivenShowcaseCullingMode.None:
                return "No Culling";
            case GpuDrivenShowcaseCullingMode.Frustum:
                return "Frustum";
            case GpuDrivenShowcaseCullingMode.FrustumAndHiZ:
                return "Frustum + Hi-Z";
            default:
                return mode.ToString();
        }
    }

    public static string ToDisplayName(this GpuDrivenShowcaseDebugView view)
    {
        switch (view)
        {
            case GpuDrivenShowcaseDebugView.None:
                return "Off";
            case GpuDrivenShowcaseDebugView.SceneWire:
                return "Scene Wire";
            default:
                return view.ToString();
        }
    }
}
