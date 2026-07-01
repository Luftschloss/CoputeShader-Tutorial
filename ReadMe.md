# GPU Driven Terrain Learn

Unity GPU Driven rendering showcase. The current focus is `GPUDrivenTerrain`: Unity Terrain is still used for authoring, while runtime rendering is handled by baked terrain data, compute culling, Hi-Z occlusion, and indirect patch drawing.

## Environment

- Unity `2022.3.62f3`
- URP `14.0.12`
- Main demo scene: `Assets/5_Terrain/GPUDrivenTerrain.unity`
- Main renderer asset: `Assets/GPUDrivenShowcase/Settings/GPUDriven_UniversalRenderer.asset`

## Current Terrain Pipeline

Authoring remains based on Unity Terrain / Terrain Tools. Runtime terrain rendering uses baked static data:

- `GpuTerrainBakedData` stores terrain tiles, patch quadtree nodes, min/max height, root nodes, height texture array, and normal texture array.
- `GPUTerrain` consumes baked data only at runtime and no longer rebuilds Terrain nodes from Unity Terrain.
- `TerrainCulling.compute` performs frustum culling and reference-style Hi-Z occlusion culling.
- `GPUTerrain.shader` and `GPUTerrainForwardBase.hlsl` render terrain patches with height displacement, normal sampling, seam snapping, shadow pass, and LOD debug color.

## Editor Tools

GPU Driven Showcase editor tools are under:

- `Assets/GPUDrivenShowcase/Editor/GpuTerrainBakedDataEditor.cs`
- `Assets/GPUDrivenShowcase/Editor/GpuTerrainBakedDataInspector.cs`
- `Assets/GPUDrivenShowcase/Editor/GpuDrivenUrpSetup.cs`

Use the terrain bake tooling to generate or refresh `GpuTerrainBakedData` assets before runtime rendering.

## Runtime Showcase Panel

The runtime panel can be collapsed with `F1` or the panel collapse button. `Debug View` is intentionally reduced to two states:

- `Off`: show only compact non-sync runtime stats.
- `Scene Wire`: draw SceneView terrain patch wireframes and enable visible/frustum/Hi-Z stats that require GPU readback.

Legacy Hi-Z texture preview overlay/debug shader code has been removed from the Showcase path.

## Hi-Z

URP Hi-Z generation is implemented by:

- `Assets/GPUDrivenShowcase/Scripts/URP/GpuDrivenHizFeature.cs`
- `Assets/GPUDrivenShowcase/Shaders/GpuDrivenHizMap.compute`
- `Assets/3_Hiz/DepthTextureGenerator.cs`

`GpuDrivenHizFeature` reads the URP camera depth target and builds a mipmapped Hi-Z texture. Terrain culling binds:

- `_HizMap`
- `_HizMapSize`
- `_HizCameraMatrixVP`
- `_HizCameraPositionWS`
- `_HizDepthBias`

Hi-Z terrain occlusion follows the reference project style: project patch bounds to UVD, select mip by projected size, sample four corners, and branch by `_REVERSE_Z` keyword.

## Terrain LOD And Debug Color

`GPUTerrain` exposes a compound LOD config array:

```csharp
TerrainLodConfig
{
    float distance;
    Color debugColor;
}
```

`distance` controls CPU-side quadtree LOD selection. `debugColor` is uploaded to `_TerrainLodDebugColors` and selected in the vertex shader by patch mip level. This replaces the old hardcoded red-to-blue shader lerp.

## Large Assets

Large imported demo assets are tracked through Git LFS. See `.gitattributes` for the current LFS paths, especially `Assets/TerrainDemoScene_URP/...`.

## Docs

Additional design notes:

- `Doc/GPU_Driven_Current_Implementation.md`
- `Doc/GPU_Driven_Terrain_Rendering_Design.md`
