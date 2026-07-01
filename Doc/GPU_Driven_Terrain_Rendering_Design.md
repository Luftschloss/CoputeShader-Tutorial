# GPU Driven Terrain 渲染与着色方案

更新时间：2026-07-02

本文基于当前工程实现，重新评估 `GPUDrivenTerrain` 的渲染/着色方案。旧版文档中有一些方向仍然成立，但也有部分内容已经被当前实现覆盖，需要调整优先级。

核心结论：

- 地形编辑仍然以 Unity Terrain / Terrain Tools 为主。
- `GpuTerrainBakedData` 是运行时地形渲染的数据源。
- 运行时 `GPUTerrain` 只消费离线 Bake 数据，不再从 Unity `TerrainData` 重建地形节点。
- 当前阶段应优先补齐 TerrainLayer、control/splat、holes 和 URP Lit 着色，而不是继续重构 LOD/Culling 基础结构。
- 运行时 dirty sync 可以作为后续能力，不应作为当前渲染白模问题的前置条件。
- 阶段 2 的 RVT + SVT 混合方案见 `Doc/GPU_Driven_Terrain_RVT_SVT_Hybrid_Design.md`，建议作为阶段 1 直接 TerrainLayer 混合稳定后的材质缓存和远景流送扩展。

## 当前实现现状

主要文件：

- `Assets/5_Terrain/GPUTerrain.cs`
- `Assets/5_Terrain/GpuTerrainBakedData.cs`
- `Assets/GPUDrivenShowcase/Editor/GpuTerrainBakedDataEditor.cs`
- `Assets/5_Terrain/TerrainCulling.compute`
- `Assets/5_Terrain/GPUTerrain.shader`
- `Assets/5_Terrain/GPUTerrainForwardBase.hlsl`
- `Assets/5_Terrain/GPUTerrainHiZDepth.shader`
- `Assets/GPUDrivenShowcase/Scripts/URP/GpuDrivenHizFeature.cs`

当前 Bake 数据已经包含：

- Terrain tile 的 world origin / size。
- quadtree patch nodes。
- root node indices。
- patch world rect。
- patch `heightMinMax`，已经在 Editor Bake 阶段预生成。
- patch mip / LOD level。
- parent / child node index。
- height `Texture2DArray`，当前格式为 `RHalf`。
- normal `Texture2DArray`，当前格式为 `RGB24`。

当前运行时渲染流程：

```text
GpuTerrainBakedData
  -> CPU quadtree LOD 选择 active nodes
  -> 生成 neighbor seam mask
  -> 上传 active node buffer
  -> TerrainCulling.compute 做 frustum + Hi-Z 剔除
  -> visible patch id append buffer
  -> indirect args
  -> Graphics.DrawMeshInstancedIndirect
  -> GPUTerrain.shader ForwardLit / ShadowCaster
```

当前 shader 能力：

- vertex 阶段根据 patch rect 和 mesh 顶点生成 world xz。
- 从 `_TerrainHeightmapTextureArray` 采样高度并位移 y。
- 从 `_TerrainNormalmapTextureArray` 采样法线。
- 使用 neighbor mask 做 LOD seam snapping。
- 支持 C# 传入的 LOD debug color array。
- ForwardLit 目前仍是 `_BaseColor * main light/shadow` 的简化着色。
- ShadowCaster pass 已存在。
- `GPUTerrainHiZDepth.shader` 存在，`GPUTerrain.DrawHiZDepth` 也存在，但当前 `GpuDrivenHizFeature` 主要从 URP camera depth target 构建 Hi-Z Pyramid。除非有 RenderPass 显式调用 `DrawHiZDepth`，否则不能认为 terrain depth injection 已完整接入。

当前 Debug 策略：

- Showcase `DebugView` 只保留 `Off` / `Scene Wire`。
- `Scene Wire` 用于 SceneView patch 线框和必要的 GPU readback stats。
- Off 状态不读回 visible/frustum/Hi-Z 统计。
- 旧的 Hi-Z 贴图预览 overlay 和 shader 已清理。

## 对旧方案的调整评估

### 1. Culling Bounds 已不再是主要缺口

旧方案里强调“每个 patch 用 9 个 height sample 估 min/max 不准确，需要补 min/max”。这部分已经完成：

- `GpuTerrainBakedData.BakedNode.heightMinMax` 已存在。
- `GpuTerrainNodeInfo.heightMinMax` 已上传到 GPU。
- `TerrainCulling.compute` 已直接读取 `heightMinMax`。

因此当前文档不应继续把“补 patch min/max”作为主要里程碑。

后续仍可考虑 GPU min/max pyramid，但它的主要价值已经变成：

- 支持运行时 dirty region 增量更新。
- 支持 runtime editing。
- 支持更复杂的 screen-error LOD。

对于当前离线 Bake 数据路径，它不是白模渲染的前置条件。

### 2. Runtime Dirty Sync 优先级应下调

当前项目已经明确运行时只需要数据，不需要传入地形重建 Node。也就是说近期方向应是：

```text
Unity Terrain authoring
  -> Editor Bake
  -> GpuTerrainBakedData
  -> Runtime renderer consume baked data
```

因此：

- 不建议当前阶段做运行时 TerrainData dirty callback。
- 不建议在运行时重建 quadtree。
- 不建议为了地编同步把 `GPUTerrain` 重新变回 TerrainData mirror builder。

如果后续确实需要运行时地形编辑，再单独做 dirty sync。

### 3. 当前最大缺口是材质数据和着色

目前 terrain 仍接近白模，根因不是 culling/LOD，而是缺少：

- TerrainLayer palette。
- alphamap/control/splat texture。
- holes texture。
- TerrainLayer diffuse/normal/mask map。
- TerrainLayer tiling/offset。
- metallic/smoothness/occlusion 等 PBR 参数。
- 与 URP Lit 更接近的 SurfaceData/InputData 流程。

所以后续实现重点应调整为：

```text
补齐 baked material data
  -> shader 支持 control blend
  -> shader 支持 TerrainLayer PBR
  -> Forward/Shadow/Depth/Hi-Z pass 一致处理 holes
```

### 4. Hi-Z Terrain Depth 需要明确接入方式

当前 Hi-Z Pyramid 输入来自 URP camera depth target。这个方案可以继续保留，但要明确两个模式：

1. Camera Depth 模式
   - Terrain 正常写入 camera depth。
   - Hi-Z pass 在合适的 RenderPassEvent 之后读取 camera depth。
   - 简单，和 URP pipeline 兼容性较好。

2. Terrain Depth Injection 模式
   - 在 Hi-Z pass 内显式调用 `GPUTerrain.DrawHiZDepth`。
   - terrain 可以作为 foliage/object 的提前 occluder。
   - 必须保证 holes、depth compare、reversed-Z 逻辑完全一致。

当前实现更接近第 1 种。`GPUTerrainHiZDepth.shader` 可以保留，但文档不能假设它已经完整参与当前 Hi-Z 输入。

## 推荐总体方案

推荐继续采用：

```text
Unity Terrain 编辑
        |
        v
Editor Bake
  - tile metadata
  - quadtree nodes
  - patch heightMinMax
  - height/normal arrays
  - control/holes arrays
  - TerrainLayer palette
        |
        v
GpuTerrainBakedData
        |
        v
Runtime GPUTerrain
  - CPU LOD node selection
  - neighbor seam mask
  - GPU frustum + Hi-Z culling
  - indirect draw
        |
        v
GPUTerrain shader
  - height displacement
  - control/splat blend
  - layer albedo/normal/mask
  - URP lighting/shadow/depth consistency
```

当前不建议切到：

- 纯 geometry clipmap。
- 完全 GPU quadtree traversal。
- BatchRendererGroup。
- 大规模 Draw API 迁移。

原因是这些方向不能直接解决当前白模问题，且会打断已经稳定下来的 Bake-only runtime 架构。

## 渲染数据设计

### 保留现有 Patch 数据

当前 GPU patch 数据结构已经够用：

```hlsl
struct NodeInfoData
{
    float4 rect;          // world x, world z, width, depth
    float2 heightMinMax;  // world-space min/max y
    int mipmap;
    int neighbor;
    int terrainIndex;
    int padding;
};
```

它负责：

- vertex displacement 的空间定位。
- frustum bounds。
- Hi-Z bounds。
- SceneView wireframe bounds。
- LOD debug color。

不建议把材质层信息塞进每个 patch。Terrain 材质是 tile/layer 维度的数据，应单独绑定。

### 扩展 Terrain Tile Render Info

当前 `TerrainTileInfo` 只有 origin 和 size。后续需要为材质绑定补充 tile 渲染信息：

```csharp
struct GpuTerrainTileRenderInfo
{
    public int terrainIndex;
    public Vector3 worldOrigin;
    public Vector3 worldSize;
    public int heightmapResolution;
    public int controlResolution;
    public int controlTextureOffset;
    public int controlTextureCount;
    public int layerRemapOffset;
    public int layerCount;
    public int holesSlice;
}
```

用途：

- 从 terrain index 找到 control texture slice。
- 从 control RGBA 通道找到全局 TerrainLayer index。
- 从 terrain index 找到 holes slice。
- 处理不同 Terrain tile 的 layer 数量和 control texture 数量。

### 新增 TerrainLayer Palette

从所有 `TerrainData.terrainLayers` 构建全局去重表：

```csharp
struct GpuTerrainLayerInfo
{
    public int diffuseSlice;
    public int normalSlice;
    public int maskSlice;
    public Vector4 tileSizeOffset;      // xy = tileSize, zw = tileOffset
    public Vector4 diffuseRemapMin;
    public Vector4 diffuseRemapMax;
    public Vector4 maskMapRemapMin;
    public Vector4 maskMapRemapMax;
    public float normalScale;
    public float metallic;
    public float smoothness;
    public float padding;
}
```

GPU 资源：

- `_TerrainLayerDiffuseArray`
- `_TerrainLayerNormalArray`
- `_TerrainLayerMaskArray`
- `_TerrainLayerInfoBuffer`

注意：

- `Texture2DArray` 每个 slice 必须同尺寸同格式。
- 第一阶段建议统一项目层贴图尺寸，例如 1024 或 2048。
- 如果 TerrainLayer 源贴图尺寸不一致，Bake 阶段重采样。
- diffuse 使用 sRGB。
- normal/mask 使用 linear。

### 新增 Control / Alphamap 数据

Unity Terrain alphamap 每张 RGBA 控制 4 个 layer。

建议 Bake 为：

- `_TerrainControlTextureArray`
- `_TerrainControlLayerIndices`

```hlsl
TEXTURE2D_ARRAY(_TerrainControlTextureArray);
StructuredBuffer<uint4> _TerrainControlLayerIndices;
```

索引关系：

```text
controlSlice = tile.controlTextureOffset + controlIndex
layerIndices = _TerrainControlLayerIndices[tile.layerRemapOffset + controlIndex]
RGBA weights -> 4 个全局 TerrainLayer palette index
```

第一阶段先支持每个 terrain tile 最多 4 层。跑通后再扩展到 8/12/16 层。

### 新增 Holes 数据

Unity Terrain holes 建议 Bake 到：

- `_TerrainHolesTextureArray`
- 格式：`R8`
- 约定：`1 = solid`，`0 = hole`

必须在以下 pass 中一致处理：

- ForwardLit。
- ShadowCaster。
- DepthOnly。
- DepthNormals。
- HiZDepth，如果启用 terrain depth injection。

如果 holes 只在 Forward clip，而 depth/shadow/Hi-Z 不 clip，会导致洞口阴影、深度和遮挡错误。

## Shader 改造方案

### 保留当前 Vertex Path

当前 vertex path 是合理的：

```text
instance id
 -> visible patch id
 -> NodeInfoData
 -> patch rect + mesh vertex
 -> world xz
 -> terrain uv
 -> height array sample
 -> world y
 -> normal array sample
 -> clip position
```

继续保留：

- `_TerrainHeightmapTextureArray` 高度位移。
- `_TerrainNormalmapTextureArray` 法线采样。
- neighbor seam snapping。
- `_TerrainLodDebugColors`。

后续可优化：

- 增加 geomorph，减少 LOD popping。
- 明确 normal bake/encode/decode 规范。当前 normal bake 的通道写入顺序为 `z, y, x`，shader 端需要持续保持一致。

### 替换 Fragment 白模着色

当前 fragment 约等于：

```text
_BaseColor * simple main light/shadow
```

目标流程：

```text
terrainUV
 -> sample control texture(s)
 -> RGBA 通道 remap 到全局 TerrainLayer index
 -> sample layer diffuse/normal/mask
 -> weight blend
 -> 构建 URP SurfaceData/InputData
 -> UniversalFragmentPBR
```

第一阶段最小目标：

- 4-layer weight blend。
- diffuse/albedo 贴图。
- normal map + TerrainLayer normalScale。
- metallic/smoothness 从 mask map 或 TerrainLayer 默认值读取。
- main light shadow receive。

第二阶段再增加：

- 8 层以上混合。
- 基于 mask height 的 height blend。
- macro tint/detail。
- slope/cliff triplanar 强化。

### HLSL 文件拆分建议

`GPUTerrainForwardBase.hlsl` 如果继续塞 layer blend 会很快变大，建议在接入 TerrainLayer 时拆分：

```text
Assets/5_Terrain/
  GPUTerrain.shader
  GPUTerrainInput.hlsl          // buffers, texture arrays, tile/layer structs
  GPUTerrainPatchVertex.hlsl    // patch vertex, height displacement, seam snapping
  GPUTerrainLayerBlend.hlsl     // control sampling and layer blending
  GPUTerrainLighting.hlsl       // URP SurfaceData/InputData helpers
```

短期也可以先在现有文件里实现，跑通后再拆。

## Render Pass 要求

当前已有：

- `ForwardLit`
- `ShadowCaster`
- 独立 `GPUTerrainHiZDepth.shader`

推荐目标 pass：

1. `ForwardLit`
   - 完整 TerrainLayer 着色。
   - 接收阴影。
   - holes clip。

2. `ShadowCaster`
   - 同样高度位移。
   - 同样 holes clip。
   - 不启用 debug color。

3. `DepthOnly`
   - 如果 URP depth prepass 或后续效果需要 terrain depth，必须补。
   - 同样高度位移和 holes clip。

4. `DepthNormals`
   - 如果需要 SSAO、decal 或依赖 normal texture 的后处理，必须补。
   - 同样高度位移和 holes clip。

5. `HiZDepth`
   - 仅在 terrain depth injection 模式需要。
   - 同样高度位移和 holes clip。
   - reversed-Z / normal-Z 逻辑必须和 Hi-Z compare 一致。

## Hi-Z 接入决策

当前状态：

- `GpuDrivenHizFeature` 从 URP camera depth target 生成 Hi-Z Pyramid。
- Terrain culling 绑定 `_HizMap`、`_HizMapSize`、`_HizCameraMatrixVP`、`_HizCameraPositionWS`、`_HizDepthBias`。
- Terrain culling 使用 baked `heightMinMax` 做 conservative bounds。

建议：

- 短期继续使用 camera depth 模式。
- 检查 RenderPassEvent，确认生成 Hi-Z 时 terrain 是否已经写入 camera depth。
- 如果 terrain 必须提前作为 foliage/object occluder，再补一个明确的 terrain depth injection pass，调用 `GPUTerrain.DrawHiZDepth`。
- 不要恢复旧的 Hi-Z preview GUI 和无用 debug counters。

## LOD 与 Culling

当前 LOD：

- CPU quadtree selection。
- 距离阈值来自 `TerrainLodConfig.distance`。
- LOD debug color 来自 `TerrainLodConfig.debugColor`。
- neighbor mask 用于 seam snapping。
- active node 变化时才上传数据。

建议保留当前方案。当前更高价值的工作是材质数据和 pass 完整性。

后续优化顺序：

1. Geomorph
   - 解决 LOD popping。
   - 保留 neighbor snapping 防裂缝。

2. Screen-error LOD
   - 材质路径稳定后再从距离阈值升级。
   - 如有需要，为 node 存储 geometric error。

3. GPU LOD Selection
   - 仅当 CPU LOD 再次成为瓶颈时考虑。
   - 当前 baked quadtree 数据可以支撑后续 compute traversal。

当前 culling：

- Frustum 使用 `rect + heightMinMax` AABB。
- Hi-Z 使用投影 bounds 和四角采样。
- Stats/readback 只在 Scene Wire DebugView 开启时执行。

建议继续保留，不要回退到每帧 height texture 采样估 bounds。

## Editor Bake 扩展方案

继续扩展 `GpuTerrainBakedDataEditor`，不要把运行时变回数据构建器。

当前 Bake 已生成：

- nodes。
- height min/max。
- height array。
- normal array。

后续增加：

1. Terrain 组合法性校验
   - 当前 `BuildHeightMapArray` 默认所有 Terrain heightmap resolution 一致。
   - 应增加显式校验和错误提示。
   - 混合分辨率要么不支持，要么分组生成多个 baked asset。

2. TerrainLayer palette
   - 去重 TerrainLayer 引用。
   - Bake/resample diffuse、normal、mask。
   - 序列化 layer info。

3. Control texture array
   - 读取 `TerrainData.alphamapTextures` 或 alphamap data。
   - Bake RGBA control slices。
   - 构建 tile -> global layer remap。

4. Holes texture array
   - 读取 Terrain holes。
   - Bake R8 solid/hole mask。

5. 作为 sub-asset 保存
   - Height array。
   - Normal array。
   - Control array。
   - Holes array。
   - Layer diffuse/normal/mask arrays。

6. 自动赋值给场景中的 `GPUTerrain`

## Runtime 绑定方案

扩展 `GPUTerrain` 的资源绑定：

```csharp
private static readonly int TerrainControlTextureArrayId = Shader.PropertyToID("_TerrainControlTextureArray");
private static readonly int TerrainHolesTextureArrayId = Shader.PropertyToID("_TerrainHolesTextureArray");
private static readonly int TerrainLayerDiffuseArrayId = Shader.PropertyToID("_TerrainLayerDiffuseArray");
private static readonly int TerrainLayerNormalArrayId = Shader.PropertyToID("_TerrainLayerNormalArray");
private static readonly int TerrainLayerMaskArrayId = Shader.PropertyToID("_TerrainLayerMaskArray");
private static readonly int TerrainTileRenderInfoBufferId = Shader.PropertyToID("_TerrainTileRenderInfoBuffer");
private static readonly int TerrainLayerInfoBufferId = Shader.PropertyToID("_TerrainLayerInfoBuffer");
private static readonly int TerrainControlLayerIndicesId = Shader.PropertyToID("_TerrainControlLayerIndices");
```

和现有资源一起绑定：

- `_TerrainHeightmapTextureArray`
- `_TerrainNormalmapTextureArray`
- `_TerrainParams`
- `_TerrainOriginSizes`
- `_TerrainCount`
- `_TerrainLodDebugColors`
- `_TerrainLodDebugColorCount`

短期资源 owner 仍放在 `GPUTerrain`，当类继续膨胀后再拆 `GpuTerrainRuntimeResources` 或类似 helper。

## Debug 策略

Showcase DebugView：

- `Off`：不做 GPU readback stats，GUI 保持精简。
- `Scene Wire`：SceneView patch 线框和必要统计。

Shader/material debug：

- `_TerrainDebugColorMode` 可以保留为材质/Inspector 级 debug。
- 后续 control weight、layer index、normal、holes 等 debug 可以做成临时 shader keyword 或材质属性。
- 不要重新把这些都塞回 Showcase Runtime GUI。

## 实施里程碑

### Milestone 1：4-Layer TerrainLayer 渲染

目标：从白模/单色变成可用地表材质。

任务：

- 在 baked asset 中增加 TerrainLayer palette。
- 增加 layer diffuse/normal/mask texture arrays。
- 每个 terrain tile 先支持 1 张 control texture，也就是最多 4 层。
- 增加 tile layer remap。
- shader 支持 4-layer weight blend。
- 保留当前 height/normal displacement。

验收：

- GPU terrain 的大色块分布与 Unity Terrain 一致。
- TerrainLayer tile size/offset 生效。
- 主光和阴影仍正常。
- 手动打开 LOD debug color 时仍可显示。

### Milestone 2：Holes 与 Pass 一致性

目标：可见、阴影、深度、Hi-Z 行为一致。

任务：

- Bake holes texture array。
- shader 增加 holes sample 和 clip。
- ForwardLit、ShadowCaster、DepthOnly、DepthNormals、HiZDepth 共用 holes clip。
- 根据 URP depth 需求补 DepthOnly pass。
- 如需要 SSAO/decal，补 DepthNormals pass。

验收：

- holes 视觉上打开。
- holes 不投影。
- holes 不写 depth。
- Hi-Z 不通过 holes 错误遮挡后方物体。

### Milestone 3：PBR Layer 质量

目标：接近 Unity TerrainLayer 的 URP Lit 表现。

任务：

- normal map + per-layer normalScale。
- mask map + TerrainLayer 默认 metallic/smoothness。
- 构建 URP `SurfaceData` / `InputData`。
- 支持 smoothness、metallic、occlusion。
- 可选 height blend。

验收：

- 地形能正确响应光照。
- 法线细节清晰且 LOD 切换稳定。
- 材质层过渡在 gameplay camera 下可接受。

### Milestone 4：资源规模控制

目标：控制显存和 shader sample 成本。

任务：

- 定义每个 terrain tile 最大 layer 数。
- 定义 TerrainLayer 贴图 array 分辨率策略。
- 明确混合 height/control 分辨率如何处理。
- 对不兼容 Terrain group 生成 bake-time 报告。
- 考虑按分辨率或 layer 数拆 baked asset / renderer batch。

验收：

- 大场景在 Bake 阶段给出明确错误，而不是运行时渲染错误。
- 显存占用可预估。
- shader sample 数有上限。

### Milestone 5：可选 Runtime Editing Sync

目标：如果后续需要运行时地形编辑，再支持增量同步。

任务：

- 追踪 height/control/holes dirty region。
- 局部更新 texture arrays。
- 对 dirty height 区域重新生成 normal。
- 更新受影响 patch 的 `heightMinMax`。
- 可选增加 GPU min/max pyramid。

验收：

- Unity Terrain 修改可以不全量 rebake 就反映到 GPU terrain。
- 运行时仍不每帧重建整棵 quadtree。

该里程碑不是当前白模渲染问题的前置项。

## 文件级改动建议

可能需要修改：

```text
Assets/5_Terrain/GpuTerrainBakedData.cs
  增加 tile render info。
  增加 layer palette info。
  增加 control/holes/layer texture array 引用。

Assets/GPUDrivenShowcase/Editor/GpuTerrainBakedDataEditor.cs
  Bake TerrainLayer palette。
  Bake control textures。
  Bake holes textures。
  增加分辨率和 layer 校验。

Assets/5_Terrain/GPUTerrain.cs
  绑定新增 textures 和 buffers。
  保持 runtime baked-data-only。

Assets/5_Terrain/GPUTerrain.shader
  扩展 ForwardLit。
  根据需求补 DepthOnly / DepthNormals。
  保持 ShadowCaster。

Assets/5_Terrain/GPUTerrainForwardBase.hlsl
  先扩展，后续拆成 input/vertex/layer/lighting helpers。
```

可选新增：

```text
Assets/5_Terrain/GPUTerrainInput.hlsl
Assets/5_Terrain/GPUTerrainPatchVertex.hlsl
Assets/5_Terrain/GPUTerrainLayerBlend.hlsl
Assets/5_Terrain/GPUTerrainLighting.hlsl
```

短期不建议移动现有文件目录，避免 Unity meta 和引用变化扩大。

## 风险与注意事项

- `Texture2DArray` slice 必须同尺寸同格式，Bake 阶段必须统一或分组。
- TerrainLayer 数量过多会显著增加 fragment sample 成本，先从 4 层开始。
- control resolution 和 heightmap resolution 可能不同，需要独立 UV 采样。
- normal map import/bake 规范必须固定，否则 shader decode 会错。
- holes 必须在所有写 color/depth/shadow 的 pass 中一致处理。
- `Graphics.DrawMeshInstancedIndirect` 当前可以继续使用，`Graphics.RenderMeshIndirect` 后续再评估。
- BatchRendererGroup 当前不推荐，复杂度高且不能直接解决白模问题。

## 推荐下一步

下一轮优先实现：

```text
Baked TerrainLayer material data
  + 4-layer control blend
  + holes
  + URP Lit shading
  + pass consistency
```

暂时不要优先投入：

- Runtime TerrainData dirty sync。
- 完全 GPU quadtree traversal。
- Geometry clipmap。
- BatchRendererGroup。
- 重新设计 culling bounds。

当前最有价值的调整，是把 `GpuTerrainBakedData` 从“几何数据资产”扩展成“几何 + 材质数据资产”，同时保持运行时 renderer 简单、稳定、数据驱动。
