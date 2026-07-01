# GPU Driven Terrain 阶段 2：RVT + SVT 混合方案

更新时间：2026-07-02

本文整理 `GPUDrivenTerrain` 在阶段 1 TerrainLayer/control 渲染之后的阶段 2 材质虚拟纹理方案。目标不是立即替换当前直接 TerrainLayer 混合路径，而是在保持 Unity Terrain 地编和离线 Bake 架构不变的前提下，逐步引入可控的地形材质缓存、运行时局部更新和远景流送能力。

## 结论

推荐采用“RVT 负责近中景运行时材质缓存，SVT 负责远景/低分辨率 mip 离线 Bake + 流送”的混合方案。

```text
Unity Terrain / Terrain Tools 地编
  -> Editor Bake 几何、height、normal、control、TerrainLayer 源数据
  -> Editor Bake SVT 低分辨率静态材质页
  -> Runtime RVT 生成近中景高分辨率材质页
  -> GPUTerrain shader 优先采样虚拟材质，缺页时回退到阶段 1 直接 TerrainLayer 混合
```

阶段 2 不建议一开始依赖 Unity 内置 Streaming Virtual Texturing 作为唯一后端。Unity 2022.3 和当前 Unity 6 文档仍把 SVT 标记为 experimental，且工作流更偏 HDRP/ShaderGraph。当前工程是 URP + 手写 HLSL + indirect patch renderer，因此更稳妥的做法是先抽象项目自己的 virtual material cache，再保留后续接 Unity SVT、SparseTexture 或平台原生 tiled resources 的可能。

## 参考链接

- Unity Manual：Streaming Virtual Texturing  
  https://docs.unity3d.com/Manual/svt-streaming-virtual-texturing.html
- Unity Manual 2022.3：How Streaming Virtual Texturing works  
  https://docs.unity3d.com/2022.3/Documentation/Manual/svt-how-it-works.html
- Unity Manual 2022.3：Cache Management for Virtual Texturing  
  https://docs.unity3d.com/2022.3/Documentation/Manual/svt-cache-management.html
- Unreal Engine：Runtime Virtual Texturing  
  https://dev.epicgames.com/documentation/en-us/unreal-engine/runtime-virtual-texturing-in-unreal-engine
- Unreal Engine：Streaming Virtual Texturing  
  https://dev.epicgames.com/documentation/en-us/unreal-engine/streaming-virtual-texturing-in-unreal-engine
- DirectX Specs：Sampler Feedback  
  https://microsoft.github.io/DirectX-Specs/d3d/SamplerFeedback.html

## 为什么要做混合方案

阶段 1 的直接 TerrainLayer/control 混合能快速解决白模问题，并且保留最简单的运行时路径。但当材质层、贴花、道路、对象融合和远景地形规模增加后，会遇到几个明确瓶颈：

- Fragment 每像素需要采样 control、多个 diffuse、多个 normal、多个 mask，采样成本随 layer 数上升。
- 道路、贴花、湿地、雪线、对象和地形融合如果都实时叠在主 shader 中，shader 会变复杂，variant 和带宽都会膨胀。
- 大世界远景不需要每帧重新计算完整 TerrainLayer 混合，适合用离线结果。
- 如果后续支持运行时地编或局部材质脏区，只更新受影响材质页比更新整张 texture array 更可控。

Unreal 的 RVT 文档给出的主流方向也类似：RVT 是运行时按需生成的 shading cache，适合 Landscape、Landscape Splines、decal-like 材质；当 RVT 覆盖很大世界时，低分辨率 mip 运行时生成会很贵，因此更适合 Bake 成 Streaming Virtual Texture，运行时只生成高分辨率 mip。

## 名词边界

### RVT

这里的 RVT 指项目自定义的 Runtime Virtual Texture / Runtime Virtual Material Cache：

- 运行时按可见区域生成地形材质页。
- 输入来自阶段 1 的 TerrainLayer/control/holes/height/normal 数据。
- 后续可以叠加 road、decal、object blend、湿度/积雪等静态或低频变化内容。
- 输出是主 terrain shader 可以直接采样的一组材质贴图。

它本质上是“地形材质缓存”，不是几何 LOD，也不是 Unity 内置 VT 的直接封装。

### SVT

这里的 SVT 指离线生成并按需加载的 Streaming Virtual Texture pages：

- Editor Bake 阶段生成远景或低分辨率 mip。
- Runtime 根据 camera / visible patch / feedback 请求加载。
- 加载到和 RVT 共用的 physical page cache。
- 适合静态地形底色、远景 normal、PBR mask 和宏观材质变化。

阶段 2 的 SVT 可以先用项目自定义 page manifest + page texture asset 实现，不强依赖 Unity 内置 experimental SVT。

## 和当前实现的关系

当前工程已经具备阶段 2 的基础：

- `GpuTerrainBakedData` 已经是运行时唯一地形数据源。
- height、normal、patch node、heightMinMax、LOD 和 culling 已经 Bake 化。
- 阶段 1 已经补了 `controlMapArray`、TerrainLayer texture arrays、layer remap 和 tile size/offset。
- `GPUTerrain` 已经可以统一绑定 baked data 给 shader。

阶段 2 要保持这些原则：

- 不把 Unity `TerrainData` 重新接回运行时 renderer。
- 不每帧重建 quadtree 或材质节点。
- 阶段 1 直接混合路径必须保留为 fallback 和对照路径。
- RVT/SVT 只接管材质采样，不改当前 patch renderer、LOD traversal 和 Hi-Z culling 的主结构。

## 总体架构

```text
Editor
  TerrainData
    -> Geometry Bake
       - terrain tile info
       - quadtree nodes
       - height min/max
       - height/normal arrays
    -> Material Source Bake
       - TerrainLayer palette
       - control maps
       - holes maps
       - layer diffuse/normal/mask arrays
    -> SVT Low-Mip Bake
       - virtual material page manifest
       - low-resolution albedo pages
       - low-resolution normal pages
       - low-resolution mask pages

Runtime
  GPUTerrain
    -> CPU active patch list
    -> GPU frustum / Hi-Z culling
    -> GpuTerrainVirtualTextureManager
       - request visible pages
       - load SVT pages
       - render RVT pages
       - update page table
    -> GPUTerrain shader
       - sample virtual material
       - fallback to direct TerrainLayer blend when missing
```

## 数据拆分

### 继续放在 GpuTerrainBakedData 的数据

- terrain tile origin / size。
- quadtree node。
- root node index。
- patch rect。
- patch height min/max。
- patch mip / LOD。
- height `Texture2DArray`。
- baked geometric normal `Texture2DArray`。
- TerrainLayer/control 源数据。

这些数据属于 geometry source 和 material source，仍然由 `GPUTerrain` 直接消费。

### 新增独立虚拟材质资产

建议新增 `GpuTerrainVirtualTextureData`，由 `GpuTerrainBakedData` 引用，避免把几何 Bake 资产继续膨胀成一个巨型 asset。

```text
GpuTerrainVirtualTextureData
  virtualGroups[]
    worldOriginXZ
    worldSizeXZ
    virtualSize
    tileSize
    borderSize
    mipCount
    runtimeRvtMipEnd
    streamedSvtMipStart
    pageManifest
  materialStacks[]
    albedo format
    normal format
    mask format
    optional height/coverage format
  svtPageAssets[]
```

建议按 virtual group 管理，而不是直接按 terrain tile 管理。原因是当前 demo 有多个相邻 Terrain tile，按 world-space group 生成虚拟纹理能减少 tile 边界 seam。对于不连续的 terrain，可以拆成多个 virtual group。

### Page Key

推荐 page key：

```text
groupIndex
mip
pageX
pageY
stackIndex
```

渲染或 Bake page 时，再根据 page 覆盖的 world rect 去查找对应 Terrain tile 和 TerrainLayer/control 数据。这样 shader 只需要基于 world XZ 算 virtual UV，不需要关心当前 patch 属于哪个 Unity Terrain tile。

## 材质输出格式

阶段 2 推荐先固定 3 个 stack，后续再扩展：

| Stack | 内容 | Prototype 格式 | Production 方向 |
| --- | --- | --- | --- |
| Albedo | base color + optional coverage | `ARGB32 sRGB` RenderTexture | BC7/BC3 或平台等价格式 |
| Normal | world normal 或 terrain tangent normal | `ARGBHalf` 或 `RGHalf` | BC5 / octahedral RG |
| Mask | metallic / occlusion / smoothness / flags | `ARGB32 linear` | BC7/BC3/BC1 分平台 |

建议优先存 world normal。RVT 是多来源合成结果，world-space normal 更适合地形、道路、贴花、静态物体写入同一个缓存。Unreal RVT 也推荐 common space normal 以改善多 primitive 写入和读取时的混合表现。

可选 stack：

- Height：用于 parallax、blend 或未来 runtime edit 脏区判断。
- MaterialId：用于 debug、脚步声、粒子、地表类型查询。
- Coverage/Holes：用于所有 pass 保持 clip 一致。

## Page 尺寸建议

Prototype：

- `tileSize = 128`。
- `borderSize = 4`。
- 每帧最多更新 4 到 8 个 RVT page。
- 每帧最多上传 2 到 4 个 SVT page。

Desktop 目标：

- `tileSize = 256`。
- `borderSize = 4` 或 `8`。
- physical atlas 例如 `4096 x 4096`，每个 stack 独立 atlas。
- 256 tile + 4 border 时，一个 4096 atlas 可容纳约 `15 x 15 = 225` 个 physical pages。

低端或移动目标：

- `tileSize = 128`。
- 降低 normal/mask 分辨率或把 far mip 只保留 albedo。
- page upload budget 需要更保守。

Border 必须真实渲染邻域内容，而不是简单 clamp。否则 virtual page 之间会在双线性、三线性和各向异性采样下出现缝。

## Mip 策略

统一使用常规 mip 编号：

- mip 0 是最高分辨率。
- mip 越大，分辨率越低。

推荐策略：

```text
mip 0..2
  近景/中近景，Runtime RVT 生成。

mip 3..N
  中远景/远景，Editor Bake 成 SVT page，Runtime 流送。

缺页
  先回退到已驻留父 mip。
  仍缺失时回退到阶段 1 直接 TerrainLayer 混合。
```

`runtimeRvtMipEnd` 和 `streamedSvtMipStart` 不要写死。它们应该是 asset 配置项，并且允许重叠 1 个 mip 用于过渡。例如 mip 2 和 mip 3 都可用时，按距离或屏幕导数做轻微 cross fade，减少 RVT/SVT 切换 pop。

## Page 请求来源

### 阶段 2A：CPU active patch 推导

第一版不要做 GPU feedback readback。当前 `GPUTerrain` 已经有 CPU LOD traversal 的 active node 列表，可以直接根据 active patches 估算需要的 virtual pages：

```text
active node rect in world XZ
  -> virtual group UV rect
  -> desired mip from patch LOD / screen size / camera distance
  -> page request list
```

优点：

- 不引入新的 GPU readback。
- 容易 debug。
- 和现有 LOD 逻辑一致。
- 双目相机时可以用左右眼或主 XR camera 的合并视锥来请求页面。

缺点：

- 会比像素级 feedback 多请求一些 page。
- 对遮挡后的页面不够精确。

这对第一版是可接受的，因为阶段 2 首要目标是稳定落地材质缓存。

### 阶段 2B：低分辨率 feedback pass

第二版再加 GPU feedback：

- 用低分辨率 RT 写入 `groupIndex / mip / pageX / pageY`。
- 用 `AsyncGPUReadback` 读取并去重。
- 页面请求延迟 1 到 3 帧是可接受的。
- feedback 只用于补充精确请求，不应替代 CPU active patch fallback。

Unity 文档描述的 SVT 也是通过采样时写 tile ID，再异步读回并生成加载请求。DirectX Sampler Feedback 则是硬件级纹理访问反馈。Unity URP 当前不应假设这些都可直接使用，所以项目内部先实现一个简单 feedback RT 更可控。

## Runtime Page Cache

新增 `GpuTerrainVirtualTextureManager`：

```text
GpuTerrainVirtualTextureManager
  pageTableTexture
  physicalAlbedoAtlas
  physicalNormalAtlas
  physicalMaskAtlas
  residentPages
  requestQueue
  svtUploadQueue
  rvtRenderQueue
  lruState
```

职责：

- 根据 camera 和 active patches 生成 page requests。
- 判断 page 是否 resident。
- 为缺失 page 分配 physical tile。
- 优先使用已 Bake 的 SVT page。
- 对近景缺失 page 发起 RVT render。
- 更新 page table。
- 控制每帧 render/upload budget。
- 提供 debug stats。

Cache 策略：

- LRU 淘汰，但 pinned 住当前帧和上一帧可见 page。
- 父 mip 比子 mip 更难淘汰，避免大范围缺页变黑。
- 同一 page 的 albedo/normal/mask 必须作为一个 residency unit，避免不同 stack mip 不一致。
- 上传和渲染分预算，避免 IO 或 page render 抢占主渲染。

## RVT Page 生成

新增 `GpuTerrainRvtPageRenderer`，用 GPU pass 生成材质页：

```text
For each dirty/runtime page:
  setup orthographic page projection in world XZ
  bind TerrainLayer/control/height/normal source
  draw full-screen quad or dispatch compute
  sample phase 1 layer blend
  composite roads/decals/object blend if enabled
  output albedo/normal/mask
  write border pixels
```

第一版建议使用 shader full-screen quad，而不是 compute：

- 更容易复用阶段 1 的 HLSL layer blend。
- RenderTexture MRT 可以同时写 albedo/normal/mask。
- 调试 RenderDoc/Frame Debugger 更直观。

后续如果 page 数量大，再评估 compute 批量生成。

## SVT Page Bake

Editor Bake 阶段新增低 mip page 生成：

```text
For each virtual group:
  for mip >= streamedSvtMipStart:
    for each page:
      render static material result
      write page asset / page texture
      append manifest entry
```

第一版可以把 page 作为 Unity asset/sub-asset 存储，方便调试。资源规模变大后再切到 Addressables、AssetBundle 或自定义二进制包。

SVT Bake 内容只包含静态或低频内容：

- TerrainLayer/control 混合结果。
- baked geometric normal 或 layer normal 合成。
- mask/PBR。
- holes/coverage。
- 静态道路和静态 decal 可选。

不建议放入：

- 高频动态对象。
- 每帧变化的湿度、脚印、爆炸痕迹。
- camera-dependent lighting。

## Shader 采样路径

`GPUTerrainForwardBase.hlsl` 后续建议拆出：

```text
GPUTerrainLayerBlend.hlsl
GPUTerrainVirtualMaterial.hlsl
GPUTerrainLighting.hlsl
```

fragment path：

```text
worldXZ
  -> virtualUV
  -> desiredMip
  -> pageTable lookup
  -> physical atlas sample
  -> material result
  -> URP lighting
```

缺页 fallback：

```text
if exact page resident:
  sample exact page
else if parent page resident:
  sample parent mip
else:
  SampleTerrainLayerBlend(...)
```

注意事项：

- 不在 vertex shader 采样虚拟材质。虚拟纹理请求和 feedback 都以 fragment 可见性为核心，vertex 采样容易请求不足。
- page table 采样使用 point sampling。
- physical atlas 采样可以 linear，但需要 border。
- page table 更新要双缓冲或显式同步，避免 shader 读到半更新状态。

## 与现有文件的落点

建议新增：

```text
Assets/5_Terrain/GpuTerrainVirtualTextureData.cs
Assets/5_Terrain/GpuTerrainVirtualTextureManager.cs
Assets/5_Terrain/GpuTerrainRvtPageRenderer.cs
Assets/5_Terrain/GPUTerrainVirtualMaterial.hlsl
Assets/GPUDrivenShowcase/Editor/GpuTerrainVirtualTextureBaker.cs
Assets/GPUDrivenShowcase/Editor/GpuTerrainVirtualTextureDataInspector.cs
```

建议修改：

```text
Assets/5_Terrain/GpuTerrainBakedData.cs
  增加 virtual material data 引用和版本兼容。

Assets/GPUDrivenShowcase/Editor/GpuTerrainBakedDataEditor.cs
  增加阶段 2 Bake 入口。
  保留阶段 1 TerrainLayer/source bake。

Assets/5_Terrain/GPUTerrain.cs
  创建/释放 virtual texture manager。
  传 active patches 或 visible patch hints。
  绑定 page table 和 physical atlas。

Assets/5_Terrain/GPUTerrainForwardBase.hlsl
  增加 virtual material sample 分支。
  保留 direct layer blend fallback。

Assets/GPUDrivenShowcase
  DebugView 增加 page residency / missing / fallback mip 的 SceneView 可视化。
```

## 实施里程碑

### Milestone 2.1：固定 Atlas 材质缓存

目标：证明“预合成材质页”能正确替代直接 TerrainLayer 混合。

任务：

- 建一个固定大小 material atlas，不做 page table。
- 从 active patches 选一小批 page。
- 运行时渲染 albedo/normal/mask page。
- terrain shader 根据 world XZ sample atlas。
- 与阶段 1 direct blend 做 debug 对比。

验收：

- 近景地形颜色和阶段 1 基本一致。
- page 边界无明显 seam。
- camera 移动时不会整帧卡顿。

### Milestone 2.2：Page Table + LRU

目标：引入真正的虚拟页寻址。

任务：

- 增加 page table texture。
- 增加 physical page allocator。
- 用 active patches 生成 page requests。
- 实现 resident / missing / fallback mip。
- 增加 page debug view。

验收：

- page cache 有固定显存上限。
- 缺页不会显示黑块或紫块。
- 快速移动时优先显示父 mip 或 direct blend fallback。

### Milestone 2.3：SVT Low-Mip Bake

目标：把远景低分辨率材质移到离线生成。

任务：

- Editor 生成 mip `streamedSvtMipStart..N` 的 page manifest。
- Runtime 按 request 加载 SVT page。
- SVT page 上传到和 RVT 共用的 physical atlas。
- 加载预算可配置。

验收：

- 远景不依赖运行时生成完整低 mip。
- 进入新区域时低 mip 先出现，高分辨率 RVT 随后补齐。
- 资源大小和 page 数在 Inspector 可见。

### Milestone 2.4：RVT/SVT Hybrid 切换

目标：同一个 virtual material 同时使用 RVT 高分辨率页和 SVT 低分辨率页。

任务：

- 配置 `runtimeRvtMipEnd` 和 `streamedSvtMipStart`。
- 支持 1 个 mip 重叠过渡。
- 实现 RVT page 覆盖 SVT page 的优先级。
- 增加 missing page / fallback mip / source type debug。

验收：

- 近景来自 RVT，远景来自 SVT。
- 切换无明显 pop。
- page 更新和上传都有每帧预算。

### Milestone 2.5：道路/贴花/对象融合

目标：发挥 RVT 的核心价值。

任务：

- 静态 road/spline 写入 RVT page。
- 静态 decal 写入 RVT page。
- 可选支持 object-to-terrain blend mask。
- 编辑器脏区只重新 Bake/更新受影响 pages。

验收：

- 主 terrain shader 不因 road/decal 大幅膨胀。
- 贴花和道路能自然贴合地形。
- 编辑后只更新局部 page。

## Debug 和 Stats

建议 DebugView 保持轻量，默认关闭。开启时提供：

- SceneView page 边界。
- resident page 颜色。
- missing page 颜色。
- RVT page 和 SVT page 不同颜色。
- fallback mip heatmap。
- cache occupancy。
- request count。
- upload count。
- rvt render count。
- page render time。
- page upload time。
- Async readback pending count，仅在启用 feedback 后显示。

Stats 不要每帧做 GPU 同步读回。需要统计 GPU 时间时使用异步 query 或只在 DebugView 开启时采样。

## 风险和规避

- Unity 内置 SVT 仍是 experimental，且偏 HDRP/ShaderGraph。规避：阶段 2 先实现项目自定义 page cache，Unity SVT 只作为可选后端。
- Page border 不正确会产生缝。规避：所有 page render 都带 border，并从邻域真实采样。
- sRGB/linear 处理容易错。规避：albedo 明确 sRGB，normal/mask 明确 linear，Bake 和 runtime 使用同一 decode。
- Normal 空间不统一会导致道路/贴花融合异常。规避：RVT 输出优先使用 world normal。
- Cache thrashing 会造成闪烁或模糊。规避：固定预算、父 mip pin、调试 cache occupancy 和 eviction。
- GPU feedback readback 可能造成延迟。规避：第一版用 CPU active patches，feedback 只做增量优化。
- 双目相机可能一只眼缺页。规避：page request 使用双眼合并可见范围，或者对 XR camera 使用更保守 mip bias。
- 资源文件可能再次变大。规避：SVT page asset 独立目录，后续纳入 LFS/Addressables，Inspector 显示总大小。

## 推荐执行顺序

先做 Milestone 2.1 和 2.2，暂时不引入磁盘流送：

```text
固定 atlas
  -> page table
  -> runtime RVT page render
  -> shader fallback
  -> debug
```

等材质缓存路径稳定，再做 SVT low-mip bake 和 runtime upload。这样可以把问题拆开：先验证虚拟材质采样正确，再验证资源流送正确，最后验证 RVT/SVT 混合切换。
