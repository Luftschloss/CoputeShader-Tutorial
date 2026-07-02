# GPU Driven Terrain 当前实现说明

相关核心文件：

- `Assets/5_Terrain/GPUTerrain.cs`
- `Assets/5_Terrain/GpuTerrainBakedData.cs`
- `Assets/GPUDrivenShowcase/Editor/GpuTerrainBakedDataEditor.cs`
- `Assets/5_Terrain/TerrainCulling.compute`
- `Assets/5_Terrain/GPUTerrain.shader`
- `Assets/5_Terrain/GPUTerrainForwardBase.hlsl`
- `Assets/5_Terrain/GPUTerrainHiZDepth.shader`
- `Assets/3_Hiz/DepthTextureGenerator.cs`
- `Assets/GPUDrivenShowcase/Scripts/URP/GpuDrivenHizFeature.cs`
- `Assets/GPUDrivenShowcase/Scripts/Core/GpuDrivenShowcaseController.cs`
- `Assets/GPUDrivenShowcase/Scripts/UI/GpuDrivenShowcaseRuntimePanel.cs`

## 当前一句话架构

当前 Terrain 仍然使用 Unity Terrain / Terrain Tools 作为编辑入口，但运行时不再依赖 `TerrainData` 重建地形节点。Editor Bake 将 Unity Terrain 的高度、法线、材质层、控制图、patch quadtree 和每个节点的高度范围离线写入 `GpuTerrainBakedData`；运行时 `GPUTerrain` 只消费这个 baked asset，做 CPU LOD 选择、GPU frustum + Hi-Z culling、indirect patch draw 和基础 TerrainLayer 混合渲染。

核心链路：

```text
Unity Terrain 编辑
  -> Editor Bake: GpuTerrainBakedData
  -> Runtime GPUTerrain
      -> CPU 选择当前 active patch LOD
      -> 上传 NodeInfoData 到 GPU buffer
      -> TerrainCulling.compute 做 frustum / Hi-Z 剔除
      -> visible id append buffer + indirect args
      -> GPUTerrain shader 按 patch displacement 绘制
```

## 运行时数据源已经 Bake-only

当前运行时 `GPUTerrain` 的唯一地形数据源是：

```csharp
[SerializeField] private GpuTerrainBakedData bakedData;
```

运行时不再做这些事：

- 不从 scene `Terrain` / `TerrainData` 读取高度。
- 不运行时重建 `TerrainNode` 或 quadtree。
- 不运行时生成 height / normal texture array。
- 不运行时从 TerrainLayer 读取 diffuse / normal / mask 源贴图。

这轮重构的重点是把确定的静态地形数据从运行时移到 Editor Bake，减少移动相机时的 GC 和 CPU spikes。`TerrainNode.cs` 这类运行时 node rebuild 路径已经不再是主链路。

当前 `GpuTerrainBakedData` 版本：

- `CurrentDataVersion = 3`
- `MinimumSupportedDataVersion = 2`

`IsValid` 会检查版本、terrain 数量、node 数量、root node、height/normal texture array 是否存在，以及 texture array depth 是否覆盖 terrain 数量。材质数据不参与 `IsValid`，而是单独由 `HasLayerData` 判断。

## GpuTerrainBakedData 内容

`GpuTerrainBakedData` 当前存储两类数据：几何/LOD 数据和第一阶段 TerrainLayer 材质数据。

几何/LOD 数据：

- `patchSize`
- `lodCount`
- `TerrainTileInfo[] terrains`
- `BakedNode[] nodes`
- `int[] rootNodeIndices`
- `Texture2DArray heightMapArray`
- `Texture2DArray normalMapArray`

材质数据：

- `Texture2DArray controlMapArray`
- `Texture2DArray layerDiffuseArray`
- `Texture2DArray layerNormalArray`
- `Texture2DArray layerMaskArray`
- `Vector4[] terrainLayerIndices`
- `Vector4[] layerTileSizeOffsets`
- `Vector4[] layerPbrParams`

全局限制：

- `MaxTerrainCount = 64`
- `MaxTerrainLayerCount = 64`

`TerrainTileInfo` 保存单个 Unity Terrain tile 的世界原点和尺寸，并派生 shader 所需参数：

```csharp
TerrainParams = (size.x, size.y, size.z, worldOrigin.y)
OriginSize    = (worldOrigin.x, worldOrigin.z, size.x, size.z)
```

`BakedNode` 是 baked quadtree 的 flat array 节点：

```csharp
public struct BakedNode
{
    public Vector4 rect;          // world x, world z, width, depth
    public Vector2 heightMinMax;  // world-space min/max y
    public int mip;
    public int terrainIndex;
    public int parentIndex;
    public int childStart;
    public int childCount;
    public int childIndex0;
    public int childIndex1;
    public int childIndex2;
    public int childIndex3;
}
```

这里没有运行时对象树，children 用显式 index 访问。这样运行时 LOD traversal 只是在数组里递归或索引，不需要创建 node 对象。

## GPU NodeInfoData 布局

CPU 上传给 GPU 的每个 active patch 是 `GpuTerrainNodeInfo`：

```csharp
[StructLayout(LayoutKind.Sequential)]
public struct GpuTerrainNodeInfo
{
    public Vector4 rect;
    public Vector2 heightMinMax;
    public int mip;
    public int neighbor;
    public int terrainIndex;
    public int padding;
}
```

HLSL / Compute 侧对应：

```hlsl
struct NodeInfoData
{
    float4 rect;
    float2 heightMinMax;
    int mipmap;
    int neighbor;
    int terrainIndex;
    int padding;
};
```

当前 stride 是 `40` bytes。这个结构是 terrain culling、vertex displacement、shadow pass、Hi-Z depth pass 共用的数据载体。

非常规点：`heightMinMax` 已经是 world-space y，不是 heightmap 0..1。GPU culling 不再采样高度图估算 bounds，而是直接使用 Editor Bake 生成的保守范围。

## Editor Bake 流程

入口：

```text
GPU Driven Showcase/Terrain/Bake Terrain Data...
GPU Driven Showcase/Terrain/Bake Selected Terrains
GPU Driven Showcase/Terrain/Bake All Scene Terrains
```

Bake 主流程在 `GpuTerrainBakedDataEditor.Bake`：

1. 收集有效 `Terrain`，按 world z 再 world x 排序，保证 terrain slice 顺序稳定。
2. 校验所有 terrain 的 `heightmapResolution` 相同。
3. 校验所有 terrain 的 `alphamapResolution` 相同。这个约束是第一阶段 control texture baking 的要求。
4. 按 `patchSize` 切每个 terrain tile 的 root patch。
5. 对每个 root patch 递归生成 quadtree node。
6. Bake height texture array。
7. Bake normal texture array。
8. Bake TerrainLayer / control / layer texture arrays。
9. 以 sub-asset 方式保存 texture arrays 到同一个 `GpuTerrainBakedData.asset`。
10. 可选自动赋值给 scene 中所有 `GPUTerrain`。

### Node Bake

每个 root patch 的 mip 从 `lodCount - 1` 开始，递归二分到 mip 0。child 顺序：

```text
0: left-bottom
1: right-bottom
2: left-top
3: right-top
```

当前 `BakedNode.mip` 含义：

- `mip == 0` 是最细 patch，永远可以直接渲染。
- `mip > 0` 是更粗层级的 patch。
- root patch mip 通常是 `lodCount - 1`。

height min/max 生成方式：

- leaf node：根据 node rect 映射到 heightmap uv，取覆盖范围内 height samples 的 min/max，再转 world y。
- parent node：直接取 4 个 child 的 min/max 合并。

这比 compute shader 中每 patch 取 9 个 height sample 更可靠，因为它覆盖 node 对应的 heightmap sample 范围，并且由 parent 逐层聚合。

### Height / Normal Texture Array

Height：

- 格式：`TextureFormat.RHalf`
- 尺寸：`terrainData.heightmapResolution`
- depth：terrain count
- linear：`true`
- mipChain：`false`
- wrap：`Clamp`
- filter：`Bilinear`
- 内容：height 0..1，shader 中乘 terrain height size 加 world origin y。

Normal：

- 格式：`TextureFormat.RGB24`
- 尺寸：和 height array 相同
- depth：terrain count
- linear：`true`
- mipChain：`false`
- wrap：`Clamp`
- filter：`Bilinear`
- 来源：`terrainData.GetInterpolatedNormal(u, v)`

当前 normal 写入有一个需要注意的 swizzle：

```csharp
R = normal.z * 0.5 + 0.5
G = normal.y * 0.5 + 0.5
B = normal.x * 0.5 + 0.5
```

shader 侧目前直接：

```hlsl
normalWS = sample.rgb * 2 - 1;
```

也就是说当前 normal texture 中的 RGB 实际是 `zyx` 排布，但 shader 没有再反 swizzle。这个实现需要在后续验证视觉光照方向时重点检查；目前基础光照已经依赖这张 normal。

### TerrainLayer 材质 Bake

第一阶段材质 Bake 只支持每个 Unity Terrain 的前 4 个 TerrainLayer，对应 control RGBA。超过 4 层的 Unity Terrain layer 当前不会参与 runtime blend。

全局 layer palette：

- 遍历所有 terrains 的前 4 个 `TerrainLayer`。
- 按引用去重，形成全局 `layers` list。
- 每个 terrain 保存一个 `Vector4 terrainLayerIndices`，表示本 terrain 的 local layer 0..3 对应到全局 layer texture array 的 slice。
- 如果 terrain 没有 layer，插入一个 null fallback layer。

Control：

- 格式：`TextureFormat.RGBA32`
- 尺寸：`alphamapResolution`
- depth：terrain count
- linear：`true`
- mipChain：`false`
- wrap：`Clamp`
- filter：`Bilinear`
- 内容：`alphamaps[..., 0..3]` 写入 RGBA。

Layer diffuse / normal / mask：

- 统一尺寸：`1024 x 1024`
- 格式：`TextureFormat.RGBA32`
- depth：global layer count
- mipChain：`true`
- wrap：`Repeat`
- filter：`Trilinear`
- aniso：`4`

读回颜色空间：

- diffuse 用 sRGB readback。
- normal / mask 用 linear readback。
- readback 前临时把源纹理设置为 `FilterMode.Trilinear`、`mipMapBias = 0`、`anisoLevel = 1`，读完恢复。

Normal map 兼容处理：

```csharp
if (color.a > 0 && (color.r < 0.05 || color.b < 0.05))
    return new Color(color.a, color.g, 1, 1);
```

这是为了兼容部分 Unity normal map 导入后把 X 放到 alpha 的格式。当前 shader 还没有真正 blend layer normal，只是数据已经 Bake 进去。

Layer 参数：

```text
_TerrainLayerTileSizeOffsets[layer] = (tileSize.x, tileSize.y, tileOffset.x, tileOffset.y)
_TerrainLayerPbrParams[layer]       = (normalScale, metallic, smoothness, 0)
```

当前 runtime shader 只实际使用了 diffuse 和 tile size/offset。normal/mask/PBR 参数已经绑定，但渲染还没有完整接入 PBR。

## Runtime 初始化和资源绑定

`GPUTerrain.OnEnable`：

1. `EnsureLodConfigDefaults()`，并迁移旧的 `lodDistance` / `lodDebugColors`。
2. 注册到静态 `ActiveTerrains`。
3. 获取 `Camera.main`。
4. `RebuildTerrainResources()`。
5. `ApplyMaterialState()`。
6. `BindTerrainRenderProperties()`。

`RebuildTerrainResources()` 做这些事：

- Release 旧 buffer / texture references。
- 标记 GPU binding dirty。
- 清 active patch / debug visible count / visible count。
- 校验 `bakedData.IsValid`。
- 计算所有 terrain tile 的 combined world bounds，作为 indirect draw bounds。
- 直接引用 baked texture arrays。
- 更新 shader 参数数组。
- 创建 terrain leaf lookup。
- 创建/扩容 persistent upload arrays。
- 如果 camera 存在，立即跑一次 LOD rebuild。

资源绑定分两层：

- 全局 shader 参数：`Shader.SetGlobalTexture/VectorArray/Int`。
- 材质参数：`mat.SetTexture/VectorArray/Int`。

同时也支持 `MaterialPropertyBlock` 路径，用于 Shadow pass 和 Hi-Z depth pass。

注意：`BindTerrainRenderProperties()` 当前每帧 `LateUpdate` 都会调用，并且会执行 `UpdateTerrainShaderArrays()`。这里是为了保证材质和全局参数同步，后续如果继续优化 CPU，可考虑只在 baked data / LOD debug color / material debug mode 变化时刷新。

## CPU LOD 选择

当前 LOD 选择仍然在 CPU 上做，但数据已经从 runtime tree object 改为 baked flat array。

触发 LOD rebuild 的条件：

- `forceLodRebuild == true`
- GPU buffer 尚未创建
- camera XZ 移动超过阈值

有效阈值：

```text
max(lodRebuildDistanceThreshold, leafPatchSize * 0.5)
```

其中：

```text
leafPatchSize = bakedData.PatchSize / 2^(bakedData.LodCount - 1)
```

这避免相机小幅移动导致每帧重建 LOD。例如 patchSize=64、lodCount=4 时，leafPatchSize=8，实际最小 rebuild 距离至少 4。

LOD traversal 逻辑：

```text
CollectActiveNodes(root)
  if node.mip <= 0 -> emit
  else if distance(cameraXZ, node.center) >= lodDistance[node.mip] -> emit
  else if no children -> emit
  else recurse children
```

也就是说 `lodConfigs[mip].distance` 越大，该 mip 越晚被使用。缺失 config 时 `GetLodDistance` 返回 0，会导致对应 mip 更容易 emit，这一点配置时要注意。

`lodConfigs` 是复合类：

```csharp
private sealed class TerrainLodConfig
{
    public float distance;
    public Color debugColor;
}
```

它同时决定 LOD 距离和 shader debug color。旧的 `lodDistance` / `lodDebugColors` 会在 `MigrateLegacyLodConfig()` 中迁移。

## LOD rebuild 性能优化

为了降低移动时 `Gpu Terrain LOD` 的 GC 和 CPU 开销，当前实现做了几类优化。

### Persistent upload memory

这些数组长期复用：

- `NativeArray<GpuTerrainNodeInfo> activeNodeInfoUpload`
- `NativeArray<uint> allNodeIdsUpload`
- `NativeArray<uint> argsUpload`
- `NativeArray<uint> hizStatsResetUpload`
- `int[] activeNodeIndices`
- `int[] previousActiveNodeIndices`
- `GpuTerrainNodeInfo[] visibleNodeInfoArray`

这样普通 LOD rebuild 不需要每次分配 managed list / array。

### Buffer capacity 按 baked node 数量创建

`UpdateComputeBuffer()` 使用：

```text
requiredCapacity = max(bakedData.NodeCount, activeNodeCount, 1)
```

常规 active node 数量变化不会 recreate compute buffer。只有 baked node 容量变大或资源被释放后才会重建 buffer。

### Active set unchanged early-out

`RebuildVisibleTerrainNodes()` 先只收集 `activeNodeIndices`，然后和 `previousActiveNodeIndices` 对比：

- 如果 active set 未变化，跳过 `BuildActiveNodeInfo`、lookup、neighbor 和 GPU upload。
- 如果变化，才更新 node info、lookup、neighbor 和 buffer。

这是目前移动卡顿优化中最关键的点之一，因为很多相机移动只改变位置但不跨 LOD rebuild boundary。

### Kernel / static bindings dirty flag

`terrainGpuBindingsDirty` 控制 compute shader buffer、material buffer、keyword 等静态绑定。普通帧不会重复 FindKernel 或重复绑定全部 GPU resources。

### Profiler 分段

LOD rebuild 中当前分段：

- `Gpu Terrain LOD Traverse`
- `Gpu Terrain LOD Build Info`
- `Gpu Terrain LOD Lookup`
- `Gpu Terrain LOD Neighbors`
- `Gpu Terrain LOD Upload`

之前观察到移动时主要开销在 Neighbors 和 Lookup，这也是后续优化重点。

## Neighbor 和裂缝处理

当前裂缝处理是 CPU 计算 neighbor mask，vertex shader 用 vertex color 对边缘点做偏移。

`Quad.asset` 是 4x4 patch grid。边缘 vertex color 用来标记哪些顶点在 LOD 过渡时需要对齐到粗 LOD 边：

- `color.r` / `color.g` 影响 x 方向不同边。
- `color.b` / `color.a` 影响 z 方向不同边。

CPU mask：

```text
bit 1: top
bit 2: bottom
bit 4: left
bit 8: right
```

代码中：

```csharp
if (HasCoarserNeighbor(centerX, centerZ + rect.w, terrainIndex, mip)) mask |= 1;
if (HasCoarserNeighbor(centerX, centerZ - rect.w, terrainIndex, mip)) mask |= 1 << 1;
if (HasCoarserNeighbor(centerX - rect.z, centerZ, terrainIndex, mip)) mask |= 1 << 2;
if (HasCoarserNeighbor(centerX + rect.z, centerZ, terrainIndex, mip)) mask |= 1 << 3;
```

shader 中：

```hlsl
if (neighbor & 1) diff.x = -input.color.r;
if (neighbor & 2) diff.x = -input.color.g;
if (neighbor & 4) diff.y = -input.color.b;
if (neighbor & 8) diff.y = -input.color.a;

float2 horPositionWS = rect.zw * 0.25 * (input.positionOS.xz + diff) + rect.xy;
```

非常规点：neighbor 查找没有每次递归搜索 quadtree，而是用 `TerrainLeafLookup`。

`TerrainLeafLookup` 针对每个 terrain 创建 leaf grid：

- cell size = leafPatchSize。
- width/depth 来自 terrain size / leafPatchSize。
- 每次 active set 变化后清 stamp，再把每个 active node 覆盖的 leaf cells 标记为该 node 的 mip。
- 查询某个 worldX/worldZ 时直接定位 cell，取当前 active node mip。

跨 terrain 边界时：

1. 先查 preferred terrain。
2. 如果查不到，遍历其他 terrain lookup，找到包含该 world point 的 terrain 后再查。

这解决多 terrain tile 边界的 LOD seam 查找，但也意味着 terrain tile 的 world rect 必须连续且 Bake 顺序稳定。

## GPU Culling

GPU culling 在 `TerrainCulling.compute` 的 `CullTerrain` kernel。

输入：

- `_AllInstancesPosWSBuffer`：当前 active patch 的 `NodeInfoData`。
- `_VisibleInstancesOnlyPosWSIDBuffer`：visible id append buffer。
- `result`：debug visible node append buffer，仅 DebugGizmos 时用。
- `_HiZStatsBuffer`：debug stats。
- `_VPMatrix`：当前相机 VP。
- `_HizCameraMatrixVP`：生成 Hi-Z 时的相机 VP。
- `_HizCameraPositionWS`：生成 Hi-Z 时的相机位置。
- `_FrustumPadding`：clip-space padding，默认范围 0..0.2。
- `_HizDepthBias`：Hi-Z bounds 向相机方向偏移的 bias。
- `_UseHiZ` / `_CollectStats` / `_WriteDebugResult`。

每个 patch 做：

1. 从 `rect + heightMinMax` 构建 8 个 world-space AABB 顶点。
2. 用 `_VPMatrix` 做 frustum test。
3. frustum 外直接 reject。
4. 如果 `_UseHiZ`，用 `_HizCameraMatrixVP` 投影 bounds 到 Hi-Z uv-depth 空间。
5. 根据屏幕 bounding rect 尺寸选 Hi-Z mip。
6. 采样 4 个角点 depth，判断是否被遮挡。
7. visible 则 append patch id。

Stats layout：

```text
[0] Hi-Z tested
[1] Hi-Z rejected
[2] Hi-Z skipped
[3] dispatch/input count
[4] frustum visible
[5] frustum rejected
```

Frustum test 支持 OpenGL clip space 差异：

- OpenGL near plane 使用 `z < -w`。
- D3D/Metal/Vulkan 风格使用 `z < 0`。

Hi-Z test 支持 reversed-Z：

```hlsl
#if _REVERSE_Z
    depth = maxP.z;
    occluded if all sampled hiz depth > depth
#else
    depth = minP.z;
    occluded if all sampled hiz depth < depth
#endif
```

这里的判定和 `GpuDrivenHizMap.compute` 的 mip reduce 方向配套：

- reversed-Z：mip 保存 min depth。
- normal-Z：mip 保存 max depth。

非常规点：frustum 使用当前相机 VP，Hi-Z 使用生成 pyramid 时记录的 VP。这样可以避免同一帧内 camera 或 matrix 不一致导致用错误坐标系采样 Hi-Z。

## Hi-Z Pyramid 生成

URP 路径在 `GpuDrivenHizFeature` 中实现，pass event 默认：

```csharp
RenderPassEvent.BeforeRenderingTransparents
```

Feature 只对 GameView camera 生效：

- SceneView camera 跳过。
- Preview camera 跳过。
- camera 上必须有 `DepthTextureGenerator`。
- `DepthTextureGenerator.useHiz == true`。
- `DepthTextureGenerator.DepthTexture != null`。

实际输入是 URP 的 `renderer.cameraDepthTargetHandle`。pass 做两步：

1. `Blit` kernel 把 camera depth 拷贝到 Hi-Z RT mip 0。
2. `CSMain` kernel 逐 mip reduce 到完整 pyramid。

`DepthTextureGenerator` 管理 Hi-Z render texture：

- size 可按屏幕最近 power-of-two 生成，也可固定。
- 默认 min/max 范围可配，当前支持 RFloat / RHalf。
- `autoGenerateMips = false`
- `useMipMap = true`
- `enableRandomWrite = true`
- `filterMode = Point`
- `wrapMode = Clamp`

Windows / Editor 下启用了 `_PING_PONG_COPY`：

- compute 从前一层临时 ping-pong texture 读。
- 同时写目标 mip 和下一轮读用临时 texture。
- 这是为了规避部分平台/后端上同一个 mipmapped UAV 读写不同 mip 的限制或异常。

`DepthTextureGenerator.MarkHiZUpdated(camera, matrixVP)` 会记录：

- 生成 Hi-Z 的 camera。
- camera position。
- GPU projection matrix * worldToCameraMatrix。
- Hi-Z texture width/height/mipCount。
- 当前 frame。

`GPUTerrain` 和 foliage culling 通过 `TryGetCurrentHiZ` 获取这些信息；如果 camera 不匹配或从未更新，则本帧不启用 Hi-Z。

### Terrain depth injection 当前状态

当前代码里存在：

- `GPUTerrain.DrawHiZDepth(CommandBuffer cmd, Material depthMaterial, int shaderPass)`
- `GPUTerrainHiZDepth.shader`
- `writeTerrainDepthToHiZ`

但当前 `GpuDrivenHizFeature` 没有搜索或调用 `GPUTerrain.DrawHiZDepth`。也就是说，目前实际 Hi-Z pyramid 输入主要来自 URP camera depth target，而不是显式把 GPU terrain 重新画进 Hi-Z mip 0。

注意：

- 如果 GPU terrain 已经在 camera depth 生成前/期间写入了 depth，它会自然进入 camera depth。
- 当前 terrain forward draw 在 `LateUpdate` 里用 `Graphics.DrawMeshInstancedIndirect` 发起，不是 URP pass 管线中的标准 renderer，因此不能默认认为它一定已经进入 URP camera depth target。
- `GPUTerrainHiZDepth.shader` 更像是预留的 terrain depth injection 实现入口，但还没接到 URP Hi-Z feature。

当前 Hi-Z culling 框架和 terrain depth injection 函数都在，但真正稳妥的后续改进是把 `DrawHiZDepth` 接进 `GpuDrivenHizFeature` 的 mip0 构建阶段，明确让 terrain 作为 occluder 参与 Hi-Z。

## Indirect Draw

`GPUTerrain` 维护两个 id buffer：

- `allInstancePosIDBuffer`：0..capacity-1，用于 no culling、shadow all patches、Hi-Z depth all active patches。
- `visibleInstancePosIDBuffer`：append buffer，用于 culling 后 forward draw。

普通 culling draw：

```csharp
visibleInstancePosIDBuffer.SetCounterValue(0);
cullingComputeShader.Dispatch(...);
ComputeBuffer.CopyCount(visibleInstancePosIDBuffer, argsBuffer, 4);
Graphics.DrawMeshInstancedIndirect(instanceMesh, 0, mat, nodeBounds, argsBuffer, ...);
```

No culling 模式：

- 不 dispatch culling。
- `args[1] = activeNodeCount`。
- `_VisibleInstanceIDBuffer` 绑定 `allInstancePosIDBuffer`。

这里 `argsBuffer` 是标准 5 uint indirect args：

```text
[0] index count per instance
[1] instance count
[2] start index
[3] base vertex
[4] start instance
```

## Vertex Shader Patch 生成

`GPUTerrainForwardBase.hlsl` 的 `TerrainVertexCommon` 同时服务：

- ForwardLit pass
- ShadowCaster pass
- Hi-ZDepth pass

核心逻辑：

1. 通过 `SV_InstanceID` 访问 `_VisibleInstanceIDBuffer`。
2. 用 id 从 `_AllInstancesTransformBuffer` 取 `NodeInfoData`。
3. 根据 patch `rect` 和 quad mesh `positionOS.xz` 算 world XZ。
4. 根据 neighbor mask 和 vertex color 修正边缘 vertex，处理 LOD seam。
5. 根据 `terrainIndex` 和 `_TerrainOriginSizes` 算 terrain UV。
6. 从 `_TerrainHeightmapTextureArray` 采样 height。
7. 从 `_TerrainNormalmapTextureArray` 采样 normal。
8. 生成 world position 和 clip position。
9. 输出 LOD debug color。

高度解码：

```hlsl
height = packedHeight.r;
positionWS.y = terrainOriginY + height * terrainHeightSize;
```

Terrain UV：

```hlsl
(positionWS.xz - terrainOrigin.xz) / terrainSize.xz
```

这里 patch mesh 本身只是一个归一化网格，真实地形几何完全由 baked node rect + heightmap displacement 生成。

## Shader 渲染状态

当前 `GPUTerrain.shader` 有两个 pass：

- `ForwardLit`，LightMode=`UniversalForward`
- `ShadowCaster`，LightMode=`ShadowCaster`

目前还没有专门的：

- DepthOnly pass
- DepthNormals pass
- GBuffer pass
- Holes clip pass
- 完整 URP TerrainLit PBR pass

ForwardLit 当前材质路径：

1. `SampleTerrainLayerBlend` 采样 control RGBA。
2. 根据 `_TerrainLayerIndices[terrainIndex]` 找 4 个 global layer slice。
3. 用 world XZ、layer tile size/offset 采样 layer diffuse array。
4. 按 weights 混合得到 albedo。
5. 乘 `_BaseColor.rgb`。
6. 如果 `_TerrainMaterialDebugMode >= 2`，直接输出材质 debug。
7. Lit 模式下做一个简单主光 Lambert：

```hlsl
color = albedo * (0.25 + ndotl * shadowAttenuation * 0.75) * mainLight.color;
```

8. 如果 `_TerrainDebugColorMode > 0.5`，再乘 LOD debug color。

当前基础光照特性：

- 支持主光方向和主光 shadow attenuation。
- `_RECEIVE_SHADOWS_OFF` keyword 可关闭接收阴影。
- 没有附加光。
- 没有 SH / lightmap GI。
- 没有 fog 混合。
- 没有 layer normal blend。
- 没有 metallic/smoothness/mask PBR。

ShadowCaster：

- 复用 `TerrainVertexCommon`。
- `GPUTERRAIN_SHADOW_CASTER_PASS` 下会调用 `ApplyShadowBias`。
- fragment 返回 0，只写深度。

`DrawShadowMap()`：

- 默认 `castShadowMap = true`。
- `shadowMapUsesAllPatches = true` 时使用 all active patches 绘制 shadow caster。
- 否则使用 visible patches。
- 用 `ShadowCastingMode.ShadowsOnly` 额外发起一次 indirect draw。

## 材质 Debug 模式

`TerrainMaterialDebugMode` 当前值：

```text
0 Lit
1 LodColor
2 LayerBlend
3 ControlWeights
4 Layer0
5 Layer1
6 Layer2
7 Layer3
8 HasLayerData
```

`GPUTerrain.shader` 中 `_TerrainMaterialDebugMode` 暴露为 enum property。C# 侧也同步设置 global/material/property block。

行为：

- `Lit`：正常 TerrainLayer diffuse blend + 简单主光 Lambert。
- `LodColor`：Lit 结果乘 LOD debug color。
- `LayerBlend`：直接输出 TerrainLayer 混合后的 albedo，不走光照。
- `ControlWeights`：输出 control RGB。
- `Layer0..3`：输出对应 layer diffuse 采样结果。
- `HasLayerData`：有材质数据为绿，否则红。

Showcase GUI 中的 `5 LOD Color` 是独立 toggle，不属于 `DebugView` 枚举。开启时调用 `SetShowcaseDebugColorMode(true)`，临时把 Terrain 的 `materialDebugMode` 切到 `LodColor`；关闭时恢复开启前的材质 debug mode。

这样做的原因：之前已把 DebugView 收敛成只控制 SceneView wire，LOD Color 是 shader 颜色调试，不应该重新塞回 DebugView 枚举。

## Showcase 控制和调试

`GpuDrivenShowcaseController` 负责统一控制 terrain 和 foliage 模块。

Culling mode：

```text
1 None
2 Frustum
3 Frustum + Hi-Z
```

Debug view：

```text
Off
4 Scene Wire
```

独立 color mode：

```text
5 LOD Color
```

GUI：

- F1 展开/收起整个窗口。
- DebugView 开启时只保留 SceneView wire 展示。
- CPU 等待 GPU 的 stats 只在 SceneWire debug 开启时读取。
- Debug 关闭时不做 `argsBuffer.GetData`、`hizStatsBuffer.GetData`、`visibleNodeInfoBuffer.GetData` 这类同步读回。

`GPUTerrain.OnDrawGizmos()` 在 SceneWire 模式下绘制：

- 当前 camera frustum。
- visible terrain patch AABB wire cube。
- wire color 按 mip 从红到蓝插值。

注意：SceneView 能看到 GPUDriven 地形本身，是因为 `LateUpdate` 中 Editor 下 forward draw 使用：

```csharp
#if UNITY_EDITOR
Camera drawCamera = null;
#else
Camera drawCamera = camera;
#endif
```

Editor 下传 `null` 让 draw 对所有相机可见，包含 SceneView；Player 下只对主 camera 绘制。

## Hi-Z 和 Debug Stats 的同步边界

`ReadBackDebugStats` 只有 `DebugGizmos` 开启时才会做 GPU readback：

- `argsBuffer.GetData(args)`
- `hizStatsBuffer.GetData(hizStats)`
- `visibleNodeInfoBuffer.GetData(...)`

这避免普通运行时因为 stats 显示引入 CPU-GPU sync。当前 GUI Stats 在 Debug 关闭时只显示轻量信息：mode、debug、color、CPU frame、Hi-Z on/off、terrain patches、foliage instances、status。

## Foliage 和 Terrain 的关系

本文主要描述 Terrain，但当前 Showcase controller 同时绑定：

- `GPUTerrain`
- `DrawGrass`
- 实现 `IGpuDrivenShowcaseModule` 的其他模块，例如 `GpuDrivenFoliageRenderer`

Foliage 也使用同一个 `DepthTextureGenerator.DepthTexture` 做 Hi-Z culling。Terrain 的 culling/debug mode 会通过 `IGpuDrivenShowcaseModule` 分发；`DebugColorMode` 对 foliage 是空实现，只影响 Terrain。

## 当前实现中特别值得讲的点

### 1. 地形编辑仍走 Unity Terrain，但 runtime 是数据驱动

这是当前方案和直接自研地编的区别。美术/设计仍可用 Unity Terrain、TerrainLayer、alphamap、Terrain Tools。运行时只看 baked mirror data，减少 runtime 复杂度。

### 2. Static terrain node 和 height bounds 放 Editor Bake

地形确定后，patch rect、quadtree、heightMinMax 都是静态数据。把这些放到 Editor Bake 后，GPU culling 不需要再采样 heightmap 估算高度范围，也不需要运行时建树。

### 3. CPU LOD 还保留，但做了低 GC 优化

当前不是全 GPU quadtree traversal。LOD selection 仍在 CPU，因为实现简单、可调试、和 Unity C# 数据结构衔接成本低。优化重点放在：flat baked array、persistent NativeArray、active set unchanged early-out、leaf lookup neighbor。

### 4. Hi-Z 使用生成时 VP，而不是当前 VP

`DepthTextureGenerator` 记录生成 Hi-Z 的 matrixVP 和 camera position。Compute culling 使用这套数据投影 bounds。这个细节可以避免同帧 camera 状态变化造成 Hi-Z uv-depth 不一致。

### 5. SceneView 显示是显式兼容的

Editor 下 indirect draw camera 参数传 null，SceneView 能看到 GPU terrain。Debug wire 通过 `OnDrawGizmos` 显示 visible patch bounds。

### 6. Debug readback 被限制在 SceneWire 模式

Stats 读回会产生 CPU 等 GPU 同步。当前只有 DebugGizmos 开启才读回 visible count、Hi-Z stats 和 visible node info。

### 7. 材质数据已经 Bake 进 asset，但 shader 只接了第一阶段

`GpuTerrainBakedData` 已包含 diffuse/normal/mask/PBR params。当前 shader 只用 diffuse + control 做 albedo blend，并加简单主光 Lambert。Normal blend、mask、PBR、RVT/SVT 仍是后续阶段。

## 当前主要边界和风险

### TerrainLayer 支持边界

- 每个 Unity Terrain 当前只支持前 4 个 TerrainLayer。
- control map 只有 RGBA 一张 texture array。
- 多 control map / 超过 4 层还没有做 add pass 或 texture array paging。

### Normal 方向需要复查

Bake normal 时写入是 `(z, y, x)`，shader 当前直接解为 `(r, g, b)`。如果光照方向看起来不对，应优先检查这个 swizzle。

### Hi-Z terrain occluder 注入未接入 URP Feature

`DrawHiZDepth` 和 `GPUTerrainHiZDepth.shader` 已存在，但当前 `GpuDrivenHizFeature` 没调用。若要保证 GPU terrain 自身参与 Hi-Z occlusion，需要把 terrain depth injection 接到 mip0 构建前或构建中。

### Shader 不是完整 TerrainLit

当前 ForwardLit 是基础实现：主光 Lambert + shadow attenuation。没有完整 URP PBR、additional lights、fog、GI、normal blend、mask map、holes、DepthNormals。

### CPU LOD 仍可能是瓶颈

尽管做了优化，LOD traversal/lookup/neighbors 仍在 CPU。极大地形、多 terrain tile、频繁跨 LOD 边界时，CPU 仍可能出现开销。下一步可以考虑：

- neighbor mask 增量更新。
- lookup grid 更紧凑的数据结构。
- job 化 LOD traversal 和 neighbor。
- GPU-driven quadtree / meshlet-like cluster selection。

### 多 Terrain 要求尺寸/分辨率一致性

当前 Bake 要求所有 terrain heightmapResolution 一致，alphamapResolution 一致。不同分辨率 terrain 需要先扩展 Bake 逻辑，按 terrain slice 支持不同分辨率或统一 resample。

### Holes 未接入

Unity Terrain holes 当前没有 Bake 到 GPU terrain shader。后续应 Bake holes texture array，并在 ForwardLit、ShadowCaster、DepthOnly/HiZDepth 中统一 clip。

## 归纳

可以按这条线讲：

1. 为什么仍使用 Unity Terrain 编辑，但 runtime 改成 baked asset。
2. `GpuTerrainBakedData` 里有哪些静态数据，尤其是 flat quadtree 和 heightMinMax。
3. CPU LOD 如何从 baked root nodes 选择 active patches。
4. 如何避免移动时 GC：persistent arrays、buffer capacity、active set early-out。
5. 如何处理 LOD seam：neighbor mask + quad vertex color snapping。
6. Compute culling 如何用 baked AABB 做 frustum 和 Hi-Z。
7. Hi-Z pyramid 如何从 URP camera depth 生成，为什么记录生成时 VP。
8. Indirect draw 如何用 visible id buffer + args buffer。
9. Shader 如何从 patch rect + heightmap array 生成真实地形，并做 TerrainLayer blend。
10. 当前边界：材质还是第一阶段、terrain depth injection 未接 Feature、holes/PBR/RVT/SVT 还未完成。

## 后续路线和当前文档关联

当前实现对应阶段 1：Bake-only terrain data + GPU patch renderer + direct TerrainLayer blend。

后续材质方案见：

- `Doc/GPU_Driven_Terrain_Rendering_Design.md`
- `Doc/GPU_Driven_Terrain_RVT_SVT_Hybrid_Design.md`

推荐后续优先级：

1. 修正/确认 normal swizzle，并补齐基础光照到 fog/GI/additional light 或直接迁移 SimpleLit/PBR。
2. 把 `DrawHiZDepth` 接进 `GpuDrivenHizFeature`，明确 terrain occluder injection。
3. Bake holes texture array，并统一所有 pass clip。
4. 扩展 TerrainLayer 超 4 层支持。
5. 在第一阶段材质稳定后，再做 RVT + SVT 混合方案。
