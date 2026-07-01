# GPU Driven Current Implementation

This document summarizes the current GPU Driven terrain, Hi-Z, foliage, and showcase control implementation in this Unity project.

Related forward-looking terrain rendering design:

- `Doc/GPU_Driven_Terrain_Rendering_Design.md`

## Scope

The current implementation is split into four runtime areas:

- GPU terrain rendering and culling: `Assets/5_Terrain`
- Hi-Z depth pyramid generation: `Assets/3_Hiz` and `Assets/GPUDrivenShowcase/Scripts/URP`
- GPU driven foliage baking and rendering: `Assets/GPUDrivenShowcase/Scripts/Foliage`
- Shared showcase controls and debug UI: `Assets/GPUDrivenShowcase/Scripts/Core`, `UI`, and `Editor`

The main scene wiring is in `Assets/5_Terrain/GPUDrivenTerrain.unity`.

## GPU Terrain

### Main Files

- `Assets/5_Terrain/GPUTerrain.cs`
- `Assets/5_Terrain/GpuTerrainBakedData.cs`
- `Assets/GPUDrivenShowcase/Editor/GpuTerrainBakedDataEditor.cs`
- `Assets/5_Terrain/TerrainCulling.compute`
- `Assets/5_Terrain/GPUTerrain.shader`
- `Assets/5_Terrain/GPUTerrainForwardBase.hlsl`
- `Assets/5_Terrain/GPUTerrainHiZDepth.shader`
- `Assets/5_Terrain/Quad.asset`

`TerrainNode.cs` has been removed. Runtime terrain rendering no longer receives `Terrain` objects or rebuilds quadtree nodes from `TerrainData`.

### Editor Baking

Terrain authoring still uses Unity Terrain and Terrain Tools. The GPU terrain renderer consumes baked mirror data generated in the Editor.

Use:

```text
GPU Driven Showcase/Terrain/Bake Terrain Data...
GPU Driven Showcase/Terrain/Bake Selected Terrains
GPU Driven Showcase/Terrain/Bake All Scene Terrains
```

The bake tool creates or updates a `GpuTerrainBakedData` asset. The asset stores:

- per terrain world origin and size
- baked height `Texture2DArray`
- baked normal `Texture2DArray`
- flat quadtree node array
- root node indices
- per node world-space rect
- per node world-space `heightMinMax`
- node mip, terrain index, parent, and explicit child indices

When `Assign To GPUTerrain` is enabled, the tool assigns the baked asset to every scene `GPUTerrain` and marks the scene dirty.

The current baked node asset version is `2`. Older baked assets are rejected by `GpuTerrainBakedData.IsValid`; rebake terrain data after pulling this version.

### Runtime Data Model

`GPUTerrain` has a single serialized data dependency:

```csharp
[SerializeField] private GpuTerrainBakedData bakedData;
```

At runtime it binds:

- `bakedData.HeightMapArray`
- `bakedData.NormalMapArray`
- terrain tile params derived from baked origin/size
- active node buffers built from the baked node array

The runtime still performs camera-dependent LOD selection, neighbor mask generation, GPU frustum culling, Hi-Z culling, indirect drawing, shadow drawing, and showcase stats. It does not sample `TerrainData`, rebuild node trees from `Terrain`, or generate height/normal texture arrays at runtime.

Maximum terrain count is currently `GpuTerrainBakedData.MaxTerrainCount` (`64`).

### Baked Node Layout

Runtime patch data uploaded to GPU is:

```csharp
struct GpuTerrainNodeInfo
{
    Vector4 rect;          // world x, world z, width, depth
    Vector2 heightMinMax;  // world-space min/max y
    int mip;
    int neighbor;
    int terrainIndex;
    int padding;
}
```

The HLSL/compute `NodeInfoData` layout mirrors this 40-byte struct.

### CPU LOD Selection

LOD selection is still CPU driven, but it traverses the baked flat node array:

- root nodes come from `bakedData.RootNodeIndices`
- children are addressed by explicit `childIndex0..3` values
- `mip == 0` is always emitted
- higher mip nodes are emitted when camera XZ distance is greater than `lodDistance[mip]`
- otherwise the traversal recurses into children

`GPUTerrain` rebuilds active terrain nodes when:

- baked resources are dirty
- LOD rebuild is forced
- camera XZ movement exceeds the effective rebuild distance

The effective rebuild distance is:

```text
max(lodRebuildDistanceThreshold, leafPatchSize * 0.5)
```

`leafPatchSize` is `bakedData.PatchSize / 2^(bakedData.LodCount - 1)`. With `patchSize = 64` and `lodCount = 4`, the actual finest rendered patch is `8` world units, so the runtime will not rebuild LOD more often than every `4` world units unless LOD is forced.

The moving-camera LOD path avoids per-rebuild managed allocations:

- active patch upload data is kept in a persistent `NativeArray<GpuTerrainNodeInfo>`
- all-patch ID data is kept in a persistent `NativeArray<uint>` and uploaded only when buffer capacity changes
- indirect args and Hi-Z stats reset uploads use persistent `NativeArray<uint>` buffers
- compute buffers are sized to baked node capacity, so ordinary LOD count changes do not recreate buffers
- compute kernel lookup and static buffer bindings are redone only when buffers/resources change
- the traversal first records active baked node indices; if the active set did not change, it skips neighbor rebuild and GPU buffer upload
- neighbor lookup uses a per-terrain leaf-cell lookup table instead of recursively searching every root tree for every edge

### LOD Crack Handling

LOD seam handling is still CPU neighbor marking plus vertex shader edge snapping.

After active nodes are collected, `GPUTerrain` looks up top, bottom, left, and right active neighbors. If the neighboring active node is coarser, it writes the matching bit:

- bit `1`: top
- bit `2`: bottom
- bit `4`: left
- bit `8`: right

`Quad.asset` remains a 4x4 patch grid with edge vertices marked by vertex colors. The shader uses the neighbor mask to snap fine edge vertices onto the coarse LOD edge.

### GPU Culling

Terrain culling runs in `TerrainCulling.compute`, kernel `CullTerrain`.

Input buffers:

- `_AllInstancesPosWSBuffer`: all active `NodeInfoData`
- `_VisibleInstancesOnlyPosWSIDBuffer`: append buffer of visible patch IDs
- `result`: optional debug append buffer
- `_HiZStatsBuffer`: stats buffer

Per patch:

1. Read baked world rect and baked `heightMinMax`.
2. Build a world-space AABB from the rect and min/max height.
3. Test the AABB against frustum planes.
4. If Hi-Z is enabled, project bounds to screen, choose a mip level, sample four Hi-Z pixels, and reject occluded patches.
5. Append visible patch ID.

The old 9-point runtime height sampling path has been removed. This makes frustum/Hi-Z bounds deterministic and conservative for the baked terrain state.

Stats layout currently used by terrain:

- `[0]`: Hi-Z tested
- `[1]`: Hi-Z rejected
- `[2]`: Hi-Z skipped
- `[3]`: dispatched/input count
- `[4]`: frustum visible
- `[5]`: frustum rejected

### Terrain Drawing

Visible patch IDs are copied into an indirect args buffer:

```csharp
ComputeBuffer.CopyCount(visibleInstancePosIDBuffer, argsBuffer, 4);
```

Rendering is issued with:

```csharp
Graphics.DrawMeshInstancedIndirect(instanceMesh, 0, mat, nodeBounds, argsBuffer, ...);
```

The material receives:

- `_AllInstancesTransformBuffer`
- `_VisibleInstanceIDBuffer`
- `_TerrainHeightmapTextureArray`
- `_TerrainNormalmapTextureArray`
- `_TerrainParams`
- `_TerrainOriginSizes`
- `_TerrainCount`

The vertex shader uses `_VisibleInstanceIDBuffer[instanceID]` to fetch `NodeInfoData`, maps world XZ into the baked terrain slice, samples baked height/normal arrays, and outputs displaced terrain vertices.

### Terrain Hi-Z Occluder Depth

Terrain can also write itself into the Hi-Z source depth pyramid through `GPUTerrain.DrawHiZDepth()`.

This uses `GPUTerrainHiZDepth.shader` and draws all active terrain patch IDs, not only currently visible IDs, into mip 0 while the Hi-Z pass is building the pyramid.

The feature is controlled by:

- `writeTerrainDepthToHiZ`
- `GpuDrivenHizFeature`
- `DepthTextureGenerator.useHiz`

This is important because terrain itself can occlude terrain patches and foliage behind it.

## Hi-Z Depth Pyramid

### Main Files

- `Assets/3_Hiz/DepthTextureGenerator.cs`
- `Assets/3_Hiz/DepthTextureMipmapCalculator.shader`
- `Assets/GPUDrivenShowcase/Scripts/URP/GpuDrivenHizFeature.cs`
- `Assets/GPUDrivenShowcase/Shaders/URPDepthToRFloat.shader`
- `Assets/GPUDrivenShowcase/Shaders/HiZDebugView.shader`

### Depth Texture Resource

`DepthTextureGenerator` owns or references the Hi-Z texture.

Important settings:

- `textureSizeMode`: screen power of two or fixed power of two.
- `resolutionScale`
- `fixedTextureSize`
- `minTextureSize`
- `maxTextureSize`
- `texturePrecision`: `RFloat` or `RHalf`
- `useExternalDepthTexture`

Managed textures are square, mipmapped render textures with point filtering:

```csharp
depthTexture.autoGenerateMips = false;
depthTexture.useMipMap = true;
depthTexture.filterMode = FilterMode.Point;
```

### URP Path

`GpuDrivenHizFeature` is the current URP integration.

It enqueues `GpuDrivenHizPass` after opaques. The pass:

1. Copies URP camera depth into mip 0 through `URPDepthToRFloat.shader`.
2. Draws GPU terrain depth into mip 0 through `GPUTerrainHiZDepth.shader`.
3. Repeatedly downsamples previous mip through `DepthTextureMipmapCalculator.shader`.
4. Copies each temporary texture into the final Hi-Z texture mip.

For reversed Z, terrain depth draw uses the max-depth pass. For non-reversed Z, it uses the min-depth pass.

### Built-In Pipeline Fallback

`DepthTextureGenerator` still has a built-in command buffer path in `OnPreRender()`. It is used only when `GraphicsSettings.currentRenderPipeline == null`.

The current showcase path is URP.

### Hi-Z Compare Convention

Compute shaders account for:

- reversed Z through `_UseReversedZ`
- OpenGL clip depth through `_IsOpenGL` or `isOpenGL`
- conservative screen-space mip selection from projected bounds size

For reversed Z:

- conservative depth in texture uses min of sampled Hi-Z values
- object is rejected when `depthInTexture > objectDepth`

For non-reversed Z:

- conservative depth in texture uses max of sampled Hi-Z values
- object is rejected when `depthInTexture < objectDepth`

## GPU Driven Foliage

### Main Files

- `Assets/GPUDrivenShowcase/Scripts/Foliage/GpuDrivenFoliageBaker.cs`
- `Assets/GPUDrivenShowcase/Editor/GpuDrivenFoliageBakerEditor.cs`
- `Assets/GPUDrivenShowcase/Scripts/Foliage/GpuDrivenFoliageData.cs`
- `Assets/GPUDrivenShowcase/Scripts/Foliage/GpuDrivenFoliageRenderer.cs`
- `Assets/GPUDrivenShowcase/Shaders/GpuDrivenFoliageCulling.compute`
- `Assets/GPUDrivenShowcase/Shaders/GpuDrivenFoliageIndirect.shader`

### Editor Baking

Use the menu:

```text
GPU Driven Showcase/Create Foliage Baker
```

This creates a `GPU Driven Foliage` GameObject with:

- `GpuDrivenFoliageRenderer`
- `GpuDrivenFoliageBaker`

The baker generates `GpuDrivenFoliageData` assets from prefab prototypes.

Per prototype placement settings:

- prefab
- optional material override
- weight
- uniform scale range
- slope range
- height range
- Y offset
- align to terrain normal
- random yaw
- sub mesh index
- shadow casting
- receive shadows

Bake flow:

1. Resolve each prefab to first child `MeshFilter` and `MeshRenderer`.
2. Pick random terrain XZ positions.
3. Query height by `terrain.SampleHeight(...) + terrainPosition.y`.
4. Query normal by `terrainData.GetInterpolatedNormal(uvX, uvZ)`.
5. Reject by height and slope ranges.
6. Build a TRS matrix.
7. Multiply by mesh local-to-prefab transform.
8. Store one `GpuDrivenFoliageInstance` per accepted placement.
9. Store combined world bounds.
10. Save or update `GpuDrivenFoliageData`.

Current limitation: the baker supports one source `Terrain`, not `TerrainList`.

### Baked Data Asset

`GpuDrivenFoliageData` stores:

- `List<GpuDrivenFoliagePrototype>`
- `List<GpuDrivenFoliageInstance>`
- combined `Bounds worldBounds`

`GpuDrivenFoliagePrototype` stores mesh/material/submesh/local bounds/shadow settings.

`GpuDrivenFoliageInstance` stores:

- prototype index
- four rows of the local-to-world matrix

Rows are converted back into `Matrix4x4` at runtime.

### Runtime Grouping

`GpuDrivenFoliageRenderer.Rebuild()` groups all instances by prototype.

For each valid prototype group, it creates a `PrototypeRuntime` with:

- all matrices buffer
- visible matrices append buffer
- indirect args buffer
- runtime material
- instance count

Runtime materials are created from `GPU Driven/Foliage Indirect` and copy basic source material properties:

- `_BaseMap`
- `_BaseColor`
- `_Cutoff`

### Foliage Culling

`GpuDrivenFoliageCulling.compute` culls one prototype group at a time.

Input:

- `_AllMatrices`: all matrices for the prototype group.
- `_BoundsCenter` and `_BoundsExtents`: prototype local bounds.
- `_VPMatrix`
- optional `_HiZMap`

For each instance:

1. Transform the prototype local bounds corners by local-to-world.
2. Frustum test projected bounds.
3. If Hi-Z is enabled, use projected screen rect and depth to test occlusion.
4. Append visible matrix into `_VisibleMatrices`.

After dispatch:

```csharp
ComputeBuffer.CopyCount(runtime.visibleMatricesBuffer, runtime.argsBuffer, sizeof(uint));
```

The indirect args instance count is updated from the append buffer count.

### Foliage Drawing

Foliage draws with:

```csharp
Graphics.DrawMeshInstancedIndirect(...)
```

The shader reads:

```hlsl
StructuredBuffer<float4x4> _GpuDrivenFoliageMatrices;
```

Depending on culling mode, the renderer binds either:

- all matrices buffer
- visible matrices buffer

## Showcase Control Layer

### Main Files

- `Assets/GPUDrivenShowcase/Scripts/Core/GpuDrivenShowcaseTypes.cs`
- `Assets/GPUDrivenShowcase/Scripts/Core/GpuDrivenShowcaseController.cs`
- `Assets/GPUDrivenShowcase/Scripts/UI/GpuDrivenShowcaseRuntimePanel.cs`
- `Assets/GPUDrivenShowcase/Editor/GpuDrivenShowcaseSceneViewOverlay.cs`

### Culling Modes

`GpuDrivenShowcaseCullingMode`:

- `None`: draw all.
- `Frustum`: GPU frustum culling only.
- `FrustumAndHiZ`: frustum plus Hi-Z occlusion.

The controller forwards the selected mode to all registered modules through `IGpuDrivenShowcaseModule`.

### Debug Views

`GpuDrivenShowcaseDebugView`:

- `None`
- `Lod`
- `HiZ`
- `Bounds`

Terrain reacts to:

- LOD debug: enables `DebugGizmos` and terrain LOD debug state.
- Hi-Z debug: enables Hi-Z overlay/stat collection.
- Bounds debug: enables bounds gizmos/debug data.

SceneView Hi-Z debug overlay is only shown when the debug view is `HiZ`; it is not drawn in non-runtime mode unless Hi-Z debug is enabled.

### Runtime UI

`GpuDrivenShowcaseRuntimePanel` provides an IMGUI panel with:

- culling mode buttons
- debug view buttons
- terrain LOD color toggle
- terrain patch stats
- Hi-Z stats
- foliage visible/total stats

Hotkeys are handled by `GpuDrivenShowcaseController`.

## Data And Buffer Contract Summary

### Terrain Buffers

CPU:

- `allInstancePosBuffer`: `NodeInfo`
- `visibleInstancePosIDBuffer`: append `uint`
- `allInstancePosIDBuffer`: all patch IDs for depth pass
- `argsBuffer`: indirect draw args
- `depthArgsBuffer`: indirect draw args for Hi-Z terrain depth
- `visibleNodeInfoBuffer`: optional debug append buffer
- `hizStatsBuffer`: culling stats

GPU:

- `_AllInstancesTransformBuffer`
- `_VisibleInstanceIDBuffer`
- `_TerrainHeightmapTextureArray`
- `_TerrainNormalmapTextureArray`
- `_TerrainParams`
- `_TerrainOriginSizes`
- `_TerrainCount`

### Foliage Buffers

Per prototype:

- all matrices buffer
- visible matrices append buffer
- indirect args buffer

Global:

- stats buffer

GPU:

- `_AllMatrices`
- `_VisibleMatrices`
- `_GpuDrivenFoliageMatrices`
- `_HiZMap`

## Known Limitations

- Terrain texture arrays assume every terrain chunk uses the same heightmap resolution as the first terrain.
- Terrain supports up to 64 chunks.
- Terrain LOD is CPU selected; only culling is GPU compute driven.
- Terrain LOD crack handling snaps edge vertices but does not geomorph.
- Quadtree balance is not explicitly enforced, so large LOD deltas may still need additional handling.
- Foliage baker currently targets one Terrain, not a TerrainList.
- Foliage renderer groups by prototype and draws one indirect call per prototype.
- Foliage shader copies only basic material properties; complex prefab shaders are not reproduced.
- Hi-Z culling depends on the URP Renderer Feature being installed and `DepthTextureGenerator.useHiz` being enabled by modules.
- Runtime stats use readback throttling; they are useful for debugging, not per-frame gameplay logic.

## Typical Runtime Frame

1. Showcase controller applies culling/debug modes.
2. Terrain optionally rebuilds active LOD patch list when the camera has moved enough.
3. URP Hi-Z pass copies camera depth, draws terrain depth into mip 0, and builds the depth pyramid.
4. Terrain compute culls active patches against frustum and Hi-Z.
5. Terrain indirect args are updated and terrain is drawn.
6. Foliage compute culls matrices per prototype against frustum and Hi-Z.
7. Foliage indirect args are updated and foliage is drawn.
8. Runtime panel and SceneView overlay read throttled stats/debug textures when enabled.
