# GPU Driven Terrain Rendering Design

本文档说明 GPUDrivenTerrain 后续渲染侧的数据补全和优化方案。

核心前提：

- 地形编辑仍以 Unity Terrain / Terrain Tools 为主。
- Unity `TerrainData` 是权威数据源，负责高度、splat/control、holes、TerrainLayer、碰撞、烘焙和美术工作流。
- GPU patch renderer 只做运行时渲染镜像和加速结构，不另起一套地编数据格式。
- 当前实现保留：CPU quadtree LOD、compute culling、Hi-Z、`DrawMeshInstancedIndirect` patch 绘制。

## Current Baseline

当前实现位于：

- `Assets/5_Terrain/GPUTerrain.cs`
- `Assets/5_Terrain/TerrainNode.cs`
- `Assets/5_Terrain/TerrainCulling.compute`
- `Assets/5_Terrain/GPUTerrain.shader`
- `Assets/5_Terrain/GPUTerrainForwardBase.hlsl`
- `Assets/5_Terrain/GPUTerrainHiZDepth.shader`

现有数据已经包含：

- Terrain 列表和每个 Terrain 的 world origin / size。
- heightmap `Texture2DArray`。
- normal `Texture2DArray`。
- world-space patch rect。
- patch LOD mip。
- neighbor mask，用于 LOD seam snapping。
- visible patch append buffer。
- indirect args buffer。
- terrain Hi-Z depth draw path。

当前主要缺口：

- 没有接入 Unity TerrainLayer、control/splat、holes、mask map、法线细节，导致渲染仍接近白模。
- height/normal 通过 CPU 逐像素采样生成，适合初始化，不适合 Terrain 编辑后的频繁同步。
- culling bounds 只在 compute 中采样 9 个高度点，遇到高频山体时不够稳定。
- patch 数据只满足绘制和剔除，还没有材质、min/max、误差、debug 统计等扩展字段。
- `Graphics.DrawMeshInstancedIndirect` 在 Unity 2022.3 文档中已标记 obsolete，新代码推荐 `Graphics.RenderMeshIndirect`。

## Recommended Direction

采用“Unity Terrain authoring + GPU patch rendering mirror”的方案。

```text
Unity TerrainData
  heightmapTexture
  alphamapTextures
  holesTexture
  terrainLayers
  size/origin
  editor changes
        |
        v
GpuTerrainDataBridge
  dirty tracking
  GPU resource copy/update
  layer palette build
  patch metadata build
        |
        v
GpuTerrainRenderer
  LOD selection
  frustum culling
  Hi-Z culling
  indirect draw
        |
        v
GpuTerrainLit.shader
  height displacement
  splat blend
  normal/mask/PBR lighting
  debug views
```

这个方向和当前工程最匹配。它保留 Unity Terrain 的地编能力，同时把运行时地形主体绘制交给 GPU patch renderer。

## Authoring Contract

Unity Terrain 继续负责：

- 高度编辑。
- 纹理层绘制。
- holes 编辑。
- TerrainLayer 管理。
- TerrainCollider。
- NavMesh / 烘焙依赖。
- 美术 Terrain Tools 工作流。

GPU renderer 不负责：

- 自定义 sculpt/paint 工具。
- 替代 TerrainData 序列化。
- 替代 TerrainCollider。
- 替代美术地编界面。

GPU renderer 负责：

- 读取 TerrainData 并生成渲染资源。
- 在 TerrainData 变化时增量同步 dirty region。
- 渲染 patch terrain。
- 输出 depth/shadow/Hi-Z occluder。
- 和 foliage / object GPU culling 共用 terrain 高度、法线、可见性数据。

## Data Modules

建议将当前 `GPUTerrain` 拆成以下逻辑模块。早期可以仍在一个 MonoBehaviour 中实现，但数据职责应按此拆分。

### GpuTerrainWorld

全局 terrain renderer 入口。

职责：

- 管理多个 `Terrain` tile。
- 维护全局 `TerrainLayerPalette`。
- 管理全局 buffers 和 texture arrays。
- 提供 debug stats。
- 驱动 LOD rebuild、culling dispatch、draw。

### GpuTerrainTile

每个 Unity Terrain 对应一个 tile。

建议字段：

```csharp
struct GpuTerrainTile
{
    public int terrainIndex;
    public Terrain terrain;
    public TerrainData terrainData;
    public Vector3 worldOrigin;
    public Vector3 worldSize;
    public int heightmapResolution;
    public int alphamapResolution;
    public int holesResolution;
    public int layerOffset;
    public int layerCount;
}
```

### TerrainLayerPalette

全局材质层表。多个 Terrain 可能引用相同 TerrainLayer，应去重。

建议字段：

```csharp
struct GpuTerrainLayer
{
    public int albedoSlice;
    public int normalSlice;
    public int maskSlice;
    public Vector4 tileSizeOffset;      // x,y = tileSize, z,w = tileOffset
    public Vector4 diffuseRemapMin;
    public Vector4 diffuseRemapMax;
    public Vector4 maskRemapMin;
    public Vector4 maskRemapMax;
    public float normalScale;
    public float metallic;
    public float smoothness;
    public float padding;
}
```

GPU 资源：

- `_TerrainLayerAlbedoArray`
- `_TerrainLayerNormalArray`
- `_TerrainLayerMaskArray`
- `_TerrainLayerParamsBuffer`

### GpuTerrainPatch

patch 是渲染和剔除的基本单位。当前 `NodeInfo` 可以扩展为：

```hlsl
struct GpuTerrainPatch
{
    float4 rect;              // world x, world z, width, depth
    float2 heightMinMax;      // world-space min/max y for this patch
    uint terrainIndex;
    uint lod;
    uint neighborMask;
    uint materialPageMask;    // optional, for future streaming/debug
    float geomError;          // optional, screen-error LOD
    uint flags;               // holes/all-empty/debug flags
};
```

第一阶段可以保持 C# struct 与当前 `NodeInfo` 兼容，只新增 `heightMinMax`。

## GPU Resources

### Height

目标：shader 和 compute 都直接读取 GPU height resource。

建议保留 texture array 方案：

- `_TerrainHeightmapTextureArray`
- format: `R16` 或 `RFloat`，当前 `RGBAHalf` 可先保留但浪费带宽。
- slice = terrain index。
- value = normalized height01。

同步策略：

- 初始化时从 `TerrainData.heightmapTexture` copy 到 array/RT。
- Terrain 编辑后，仅更新 dirty rect 对应区域。
- 如果 Unity Terrain 的 heightmap resolution 不一致，按 resolution 分组维护多个 renderer batch，或强制项目规范统一分辨率。

### Normal

当前 CPU 生成 normal texture 可作为 fallback。推荐升级为 compute 从 height 生成：

- `_TerrainNormalmapTextureArray`
- format: `ARGB32` 或 `RG16_SNORM`。
- dirty rect 更新时外扩 1-2 texel 重新生成。

优势：

- Terrain 编辑后不用 CPU 遍历整张 heightmap。
- 法线与 GPU height 保持一致。
- 可以同时输出 slope/curvature，供材质自动混合使用。

### Control / Splat

Unity Terrain 的 alphamap 每 4 层一张 RGBA control texture。

GPU 侧建议：

- `_TerrainControlTextureArray`
- slice = terrainIndex * controlTextureCountPerTerrain + controlIndex
- RGBA = 4 个 layer 权重。

每个 tile 还需要 layer remap：

```hlsl
StructuredBuffer<uint4> _TerrainControlLayerIndices;
// index = terrainIndex * maxControlTextures + controlIndex
// value = global layer indices for rgba
```

渲染时流程：

```text
terrainUV -> sample control0/control1/... -> gather non-zero weights
globalLayerIndex -> sample albedo/normal/mask texture arrays
blend material response
```

第一阶段建议支持最多 4 层，跑通后扩展到 8/12/16 层。

### Holes

Unity Terrain holes 应同步到：

- `_TerrainHolesTextureArray`
- format: `R8`。
- 0 = hole, 1 = solid。

shader 中：

- vertex 阶段不裁剪。
- fragment 阶段采样 holes，低于阈值 alpha clip。
- depth/shadow/Hi-Z depth pass 必须使用同样 holes clip，否则可见性和阴影会错。

### MinMax Height Pyramid

当前 culling compute 采样 9 个高度点估算 bounds。建议补充 min/max pyramid：

- `_TerrainMinMaxHeightTextureArray`
- 每个 mip texel 存该区域 min/max height01。
- format: `RGHalf` 或 `RGFloat`。

用途：

- patch culling 的 heightMinMax。
- patch bounds debug。
- foliage placement/culling。
- screen-error LOD。
- terrain depth occluder conservative bounds。

生成策略：

- 初始化全量生成。
- Terrain height dirty rect 后，从 dirty rect 对应 mip0 区域开始逐级更新。

### Optional Macro/Base Maps

中远景材质可以增加：

- `_TerrainMacroColorArray`
- `_TerrainMacroNormalArray`
- `_TerrainAOArray`

这些不是第一阶段必需，但能明显改善 tiled texture 重复感。

## Shader Upgrade

新增或重构为 `GPUTerrainLit.shader`。

### Vertex Stage

输入：

- patch rect。
- visible patch id。
- terrain index。
- height texture。
- normal texture。
- neighbor mask。

输出：

- world position。
- terrain UV。
- material UV。
- normalWS。
- tangent basis 或 derivative 信息。
- lod/debug color。

保留当前 edge snapping，后续增加 geomorph：

```text
fine edge vertex -> align to coarser neighbor edge
optional morph factor -> reduce LOD popping
```

### Fragment Stage

最小目标：

- sample control map。
- sample 4 terrain layers。
- blend albedo。
- blend normal。
- blend mask map。
- feed URP lighting。
- receive main light shadow。
- holes alpha clip。

材质混合优先级：

1. Weight blend: 最容易和 Unity Terrain 结果对齐。
2. Height blend: 第二阶段引入，用 TerrainLayer mask/height 改善层间过渡。
3. Triplanar cliff: 第三阶段针对陡坡补强。

Debug modes：

- LOD color。
- terrain index。
- control weights。
- layer index。
- height。
- normal。
- min/max bounds。
- holes。

## Terrain Editing Sync

因为地编仍由 Unity Terrain 负责，GPU renderer 应监听或检测 TerrainData 变化，然后同步镜像资源。

建议分两档实现。

### Phase 1: Explicit Rebuild

先提供 Inspector 按钮或运行时快捷键：

```text
Rebuild GPU Terrain Resources
```

行为：

- 重新扫描 Terrain list。
- 重新构建 height/normal/control/holes/layer arrays。
- 重新构建 patch buffers。

适合先跑通渲染材质。

### Phase 2: Dirty Sync

引入 dirty tracking：

```csharp
struct TerrainDirtyRegion
{
    public int terrainIndex;
    public RectInt heightRect;
    public RectInt controlRect;
    public RectInt holesRect;
    public bool heightDirty;
    public bool controlDirty;
    public bool holesDirty;
    public bool layersDirty;
}
```

同步策略：

- height dirty：copy height region，regenerate normal region，update min/max pyramid，update affected patch bounds。
- control dirty：copy alphamap region，update control texture array。
- holes dirty：copy holes region，update holes texture array，mark affected patches。
- layers dirty：rebuild layer palette and remap buffers。

Unity `TerrainData` 提供 heightmap、alphamap、holes、terrainLayers、dirty/sync 等 API，可作为同步依据。

## LOD Strategy

当前 LOD 由 CPU quadtree 距离阈值控制，可继续使用。

优化顺序：

### 1. Patch Height Bounds

在 `GpuTerrainPatch` 中存 `heightMinMax`，compute culling 直接读，不再每帧采 9 点高度。

好处：

- 减少 compute texture fetch。
- bounds 更保守稳定。
- debug 可视化清晰。

### 2. Screen Error LOD

从距离阈值升级为 screen error：

```text
screenError = geomError / distance * projectionScale
if screenError < threshold -> use coarser LOD
```

其中 `geomError` 可来自：

- Unity `TerrainData.GetMaximumHeightError()`。
- 自己从 height mip/minmax 计算。
- 每 LOD 固定误差估算。

### 3. Geomorph

当前 snapping 能解决裂缝，但 LOD 切换会 pop。

后续增加：

- per patch morph factor。
- vertex shader 根据距离将高频顶点逐步贴近低 LOD 采样点。

### 4. GPU LOD Selection

不建议第一阶段做 GPU QuadTree。

当 CPU LOD rebuild 成为瓶颈后，再改为：

- CPU 上传 root patch buffer。
- compute 选择 LOD。
- append active patch。
- compute culling active patch。
- output visible patch id and indirect args。

## Culling Strategy

保留当前 frustum + Hi-Z culling。

优化点：

- `heightMinMax` 从 patch buffer 读取。
- projected rect 计算使用 conservative bounds。
- Hi-Z compare 保持 reversed-Z / normal-Z 双路径。
- terrain depth pass 必须写 holes clip 后的 terrain。
- debug stats 按 terrain/frustum/Hi-Z 分类保留。

建议 culling data flow：

```text
allActivePatchBuffer
  -> TerrainCulling.compute
       frustum test
       Hi-Z test
       optional holes/all-empty skip
  -> visiblePatchIdBuffer
  -> indirect args
  -> render
```

## Draw API Roadmap

短期：

- 保留 `Graphics.DrawMeshInstancedIndirect`，降低改动风险。

中期：

- 迁移到 `Graphics.RenderMeshIndirect`。
- 使用 `GraphicsBuffer.IndirectDrawIndexedArgs`。
- 支持每 LOD mesh 或多个 draw command。

长期：

- 如果需要和 Unity SRP batch / culling 更深整合，评估 BatchRendererGroup。
- BatchRendererGroup 适合自定义大量实例和 custom terrain patch，但接入复杂度明显高于当前 indirect draw。

## Render Passes

GPU terrain 至少需要以下 pass：

- ForwardLit / DeferredLit。
- ShadowCaster。
- DepthOnly。
- DepthNormals。
- HiZDepth。
- Debug。

当前已有：

- ForwardLit。
- ShadowCaster。
- HiZDepth。

待补：

- DepthOnly：给 URP depth prepass / effects 使用。
- DepthNormals：给 SSAO、decal、后处理使用。
- Debug pass 或 shader keyword。

## Recommended Implementation Milestones

### Milestone 1: TerrainLayer Rendering

目标：从白模变为可用地表材质。

任务：

- 建 `TerrainLayerPalette`。
- 构建 albedo/normal/mask texture arrays。
- 构建 control texture array。
- shader 支持 4 layer splat blend。
- holes alpha clip。
- debug control/layer。

验收：

- GPU terrain 与 Unity Terrain 默认材质的大色块分布一致。
- TerrainLayer tile size/offset 生效。
- 主光、阴影、法线方向正确。

### Milestone 2: Data Sync And Dirty Regions

目标：Unity Terrain 编辑后 GPU renderer 可更新。

任务：

- 显式 rebuild 按钮。
- editor/runtime dirty 标记。
- height/control/holes partial copy。
- normal compute update。
- min/max pyramid update。

验收：

- Unity Terrain Tools 修改高度后，GPU terrain 可刷新。
- 纹理刷层后，GPU terrain control blend 更新。
- holes 更新后，forward/depth/shadow/Hi-Z 一致。

### Milestone 3: Culling Bounds

目标：提升剔除稳定性和性能。

任务：

- 生成 per patch `heightMinMax`。
- culling compute 改为读取 patch min/max。
- debug bounds 显示 min/max。
- 统计 9 点采样版本和 min/max 版本差异。

验收：

- 山峰/峡谷不会被错误 Hi-Z/frustum 剔除。
- culling compute texture fetch 减少。

### Milestone 4: LOD Quality

目标：减少 popping，提升 patch 数稳定性。

任务：

- screen error LOD。
- geomorph。
- LOD stats。
- neighbor debug。

验收：

- 相机移动时 LOD 变化更平滑。
- seam 无明显裂缝。

### Milestone 5: Draw API And Scaling

目标：为更大地形和更多平台做准备。

任务：

- 评估 `RenderMeshIndirect`。
- 按 LOD 或材质分组 multi draw。
- 评估 BatchRendererGroup。
- 分 resolution / layer count 建 batch。

验收：

- 支持多个 Terrain tile。
- draw command 和 resource binding 清晰。

## File-Level Changes

建议新增：

```text
Assets/5_Terrain/Runtime/
  GpuTerrainWorld.cs
  GpuTerrainTile.cs
  GpuTerrainLayerPalette.cs
  GpuTerrainResourceBuilder.cs
  GpuTerrainDirtyTracker.cs
  GpuTerrainPatchBuilder.cs

Assets/5_Terrain/Shaders/
  GPUTerrainLit.shader
  GPUTerrainLitInput.hlsl
  GPUTerrainLayerBlend.hlsl
  TerrainNormalFromHeight.compute
  TerrainMinMaxPyramid.compute
```

为了避免 Unity meta 引用破坏，短期也可以先在现有 `Assets/5_Terrain` 目录下新增文件，不移动旧文件。

现有文件演进：

- `GPUTerrain.cs`
  - 增加 TerrainLayer/control/holes 资源构建。
  - 增加 dirty rebuild 入口。
  - 增加 patch `heightMinMax`。
- `TerrainCulling.compute`
  - 使用 patch `heightMinMax`。
  - 保留 Hi-Z compare。
- `GPUTerrainForwardBase.hlsl`
  - 拆分 vertex displacement 和 material layer blend。
  - 增加 holes clip。
- `GPUTerrain.shader`
  - 升级为 lit terrain shader 或保留为 debug shader。

## Risks

- Texture2DArray 要求 slice 尺寸和格式一致；Terrain tile 分辨率不一致时必须分组或做规范限制。
- TerrainLayer 数量过多会增加 fragment texture sample，需要限制每像素最多混合层数。
- Unity Terrain 默认 shader 的细节很多，第一阶段只追求视觉一致的大方向，不追求逐像素完全一致。
- Terrain 编辑后的 dirty callback 不一定覆盖所有导入/脚本修改路径，因此需要提供强制 rebuild。
- Hi-Z depth 中如果不处理 holes，会导致洞口后方物体被错误遮挡。
- CPU quadtree 在 tile 数很多时会成为瓶颈，但当前阶段先用它换稳定性。

## Mainstream References

### Unity APIs

- Unity TerrainData: https://docs.unity3d.com/2022.3/Documentation/ScriptReference/TerrainData.html
- Unity TerrainLayer: https://docs.unity3d.com/2022.3/Documentation/ScriptReference/TerrainLayer.html
- TerrainData dirty texture sync: https://docs.unity3d.com/2022.3/Documentation/ScriptReference/TerrainData.DirtyTextureRegion.html
- TerrainData sync texture: https://docs.unity3d.com/2022.3/Documentation/ScriptReference/TerrainData.SyncTexture.html
- TerrainData patch min/max heights: https://docs.unity3d.com/2022.3/Documentation/ScriptReference/TerrainData.GetPatchMinMaxHeights.html
- DrawMeshInstancedIndirect: https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Graphics.DrawMeshInstancedIndirect.html
- RenderMeshIndirect: https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Graphics.RenderMeshIndirect.html
- BatchRendererGroup: https://docs.unity3d.com/2022.3/Documentation/Manual/batch-renderer-group.html

### Terrain LOD / GPU Terrain Techniques

- GPU Gems 2, Chapter 2, Terrain Rendering Using GPU-Based Geometry Clipmaps: https://developer.nvidia.com/gpugems/gpugems2/part-i-geometric-complexity/chapter-2-terrain-rendering-using-gpu-based-geometry
- CDLOD source and paper links by Filip Strugar: https://github.com/fstrugar/CDLOD
- Thatcher Ulrich, Chunked LOD: http://tulrich.com/geekstuff/chunklod.html
- Terrain Rendering in Far Cry 5, Stephen McAuley, Ubisoft: local copy `Doc/TerrainRenderingFarCry5.pdf`

## Recommended Choice For This Project

本项目推荐优先实现：

```text
Unity TerrainData authoring
  + CPU quadtree / CDLOD-style patch selection
  + GPU frustum and Hi-Z culling
  + GPU terrain layer texture arrays
  + GPU min/max height data
  + indirect patch draw
```

不建议当前阶段切换到纯 geometry clipmap。Geometry clipmap 很适合超大连续高度场，但它和 Unity Terrain tile、TerrainLayer、holes、地编、碰撞工作流的整合成本更高。

不建议当前阶段做完全 GPU QuadTree。当前 CPU LOD 逻辑已经可用，优先补齐材质数据、dirty sync 和 culling bounds，收益更直接。
