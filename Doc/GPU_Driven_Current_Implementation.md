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
- `Assets/5_Terrain/TerrainNode.cs`
- `Assets/5_Terrain/TerrainCulling.compute`
- `Assets/5_Terrain/GPUTerrain.shader`
- `Assets/5_Terrain/GPUTerrainForwardBase.hlsl`
- `Assets/5_Terrain/GPUTerrainHiZDepth.shader`
- `Assets/5_Terrain/Quad.asset`

### Data Model

`GPUTerrain` supports both the original single `Terrain terrain` field and the newer `List<Terrain> terrainList`.

Runtime terrain data is normalized into:

- `terrainRuntimeData`: filtered valid Terrain entries.
- `terrainParams[64]`: per terrain `(size.x, size.y, size.z, terrainWorldY)`.
- `terrainOriginSizes[64]`: per terrain `(originWorldX, originWorldZ, size.x, size.z)`.
- `heightMapArray`: `Texture2DArray` storing normalized local height.
- `normalMapArray`: `Texture2DArray` storing terrain normals.
- `rootNodes`: one quadtree root per Terrain chunk.

Maximum terrain count is currently `64`.

### Height And Normal Texture Arrays

`BuildTerrainTextureArrays()` builds one height and normal slice per Terrain.

Height data is generated on CPU from Unity Terrain:

```csharp
height01 = terrainData.GetInterpolatedHeight(u, v) / terrainData.size.y;
```

Shader decode uses the inverse:

```hlsl
positionWS.y = terrainDataParam.w + height * terrainDataParam.y;
```

This keeps GPU terrain height aligned with Unity Terrain local height and world Y offset.

Normals are generated from `TerrainData.GetInterpolatedNormal(u, v)` and stored in a texture array. The shader samples the matching terrain slice by `terrainIndex`.

Current limitation: the texture array uses the first terrain's heightmap width and height for all slices. Terrain chunks are expected to share heightmap resolution.

### Patch Generation

For each Terrain, `GenerateTerrainNode()` creates a root rect in world XZ:

```text
(terrainPos.x, terrainPos.z, terrainSize.x, terrainSize.z)
```

The root is subdivided into patch nodes by `resolution`. Each patch receives:

- world-space rect
- mip level `LOD - 1`
- terrain index

Patch data is stored as `NodeInfo`:

```csharp
float4 rect;
int mip;
int neighbor;
int terrainIndex;
int padding;
```

This struct is mirrored in HLSL/compute as `NodeInfoData`.

### CPU LOD Selection

LOD selection is CPU driven.

`TerrainNode.CollectNodeInfo()` compares horizontal camera distance to `lodDistance[mip]`:

- `mip == 0` is always emitted.
- Higher mip nodes are emitted when the camera is far enough.
- Otherwise the node recurses into children.

`GPUTerrain` rebuilds visible terrain nodes when:

- terrain resources are dirty
- LOD rebuild is forced
- camera XZ movement exceeds `lodRebuildDistanceThreshold`

### LOD Crack Handling

LOD seam handling is done by CPU neighbor marking plus vertex shader edge snapping.

After active nodes are collected, `UpdateQuadTreeNode()` looks up top, bottom, left, and right active neighbors. If a neighbor exists and has a coarser mip:

```csharp
topNode.mip > nodeInfo.mip
```

the matching bit is written to `nodeInfo.neighbor`:

- bit `1`: top
- bit `2`: bottom
- bit `4`: left
- bit `8`: right

`Quad.asset` is a 4x4 patch grid with edge vertices marked by vertex colors. The shader uses those vertex colors to offset the fine edge vertices onto the coarse LOD edge:

```hlsl
if (neighbor & 1) diff.x = -input.color.r;
if (neighbor & 2) diff.x = -input.color.g;
if (neighbor & 4) diff.y = -input.color.b;
if (neighbor & 8) diff.y = -input.color.a;
```

Then world XZ is computed from the adjusted position:

```hlsl
float2 horPositionWS = rect.zw * 0.25 * (input.positionOS.xz + diff) + rect.xy;
```

This removes T-junction cracks between different LOD patches. It does not perform geomorphing, so LOD transitions can still pop.

### GPU Culling

Terrain culling runs in `TerrainCulling.compute`, kernel `CullTerrain`.

Input buffers:

- `_AllInstancesPosWSBuffer`: all active `NodeInfoData`.
- `_VisibleInstancesOnlyPosWSIDBuffer`: append buffer of visible patch IDs.
- `result`: optional debug append buffer.
- `_HiZStatsBuffer`: stats buffer.

Per patch:

1. Read `terrainIndex` and patch rect.
2. Sample terrain height array at corners, center, and edge centers.
3. Build a conservative world-space AABB from sampled min/max height plus `_TerrainHeightPadding`.
4. Test against frustum planes.
5. If Hi-Z is enabled, project bounds to screen, choose a mip level, sample four Hi-Z pixels, and reject occluded patches.
6. Append visible patch ID.

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

The vertex shader uses `_VisibleInstanceIDBuffer[instanceID]` to fetch the selected `NodeInfoData`.

### Terrain Hi-Z Occluder Depth

Terrain can also write itself into the Hi-Z source depth pyramid through `GPUTerrain.DrawHiZDepth()`.

This uses `GPUTerrainHiZDepth.shader` and draws all terrain patch IDs, not only currently visible IDs, into mip 0 while the Hi-Z pass is building the pyramid.

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
