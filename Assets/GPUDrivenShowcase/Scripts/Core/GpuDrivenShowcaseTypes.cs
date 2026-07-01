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
    Lod = 1,
    HiZ = 2,
    Bounds = 3
}

public struct GpuDrivenShowcaseStats
{
    public float cpuFrameMs;
    public int terrainPatchCount;
    public int terrainVisiblePatchCount;
    public int terrainCullingDispatchedPatchCount;
    public int terrainFrustumVisiblePatchCount;
    public int terrainFrustumRejectedPatchCount;
    public int terrainHiZTestedPatchCount;
    public int terrainHiZRejectedPatchCount;
    public int terrainHiZSkippedPatchCount;
    public int foliageInstanceCount;
    public int foliageVisibleInstanceCount;
    public int hizTerrainDepthDrawCount;
    public bool terrainDepthOccluderEnabled;
    public bool terrainColorDebugEnabled;
    public bool terrainShadowCasterEnabled;
    public bool terrainShadowReceiverEnabled;
    public bool hizEnabled;
    public string depthTextureDescription;
    public string status;
}

public interface IGpuDrivenShowcaseModule
{
    string DisplayName { get; }
    void SetCullingMode(GpuDrivenShowcaseCullingMode mode);
    void SetDebugView(GpuDrivenShowcaseDebugView view);
    void SetTerrainColorDebug(bool enabled);
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
            case GpuDrivenShowcaseDebugView.Lod:
                return "LOD";
            case GpuDrivenShowcaseDebugView.HiZ:
                return "Hi-Z";
            case GpuDrivenShowcaseDebugView.Bounds:
                return "Bounds";
            default:
                return view.ToString();
        }
    }
}
