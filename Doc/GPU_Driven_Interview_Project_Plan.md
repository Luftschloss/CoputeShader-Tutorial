# Unity GPU Driven 面试项目半个月冲刺计划

## 1. 项目定位

### 项目名称

**Unity URP 移动端 GPU Driven 大场景渲染管线 Demo**

### 项目目标

在半个月内基于当前 Unity 项目，完成一个可以用于腾讯《王者荣耀》U3D 客户端渲染岗位面试展示的技术项目。

项目不追求功能数量，而追求技术链路完整、问题讲得深入、性能数据可验证、工程结构可阅读。最终目标是让面试官看到你具备以下能力：

- 能在 Unity/URP 中设计并落地一套渲染子系统。
- 理解 GPU Driven 的数据流、剔除、压缩、间接绘制和调试方式。
- 能处理大场景地形、植被、LOD、Hi-Z、虚拟纹理/资源流送等实际项目问题。
- 能从移动端 GPU 架构、Vulkan、带宽、overdraw、兼容性角度解释技术取舍。
- 能把技术能力转化为美术管线、性能优化和团队效率价值。

### 推荐对外描述

> 这是一个面向 Unity URP 移动端大场景的 GPU Driven 渲染子系统。项目使用 Compute Shader 进行视锥剔除和 Hi-Z 遮挡剔除，通过 DrawMeshInstancedIndirect 渲染地形 patch 和植被实例，并提供 LOD 可视化、剔除统计、性能对比、调试视图和轻量虚拟纹理/贴图流送实验。

## 2. 岗位命中点

### JD 对应关系

| JD 要求 | 项目对应内容 |
| --- | --- |
| Unity 渲染管线建设 | 基于 URP 搭建大场景渲染主场景、RendererFeature/CommandBuffer、调试 UI |
| 引擎底层功能和框架 | GPU Driven 可见性系统、间接绘制、统一 buffer 管理、调试工具 |
| 光照、渲染、管线、GPUDriven | 地形/植被渲染、Hi-Z、LOD、Indirect Draw、Shader 实现 |
| 资源管理 | 轻量虚拟纹理或地表贴图 page cache/streaming |
| 高级图形效果 | 大地形、草海、风动、LOD 可视化、地形接缝处理 |
| 美术制作管线 | 密度图/参数化植被、地表材质层、可调试工具 |
| 性能优化和兼容性 | Unity Profiler、Frame Debugger、RenderDoc 数据、移动端 Vulkan/带宽分析 |
| 机器学习和可微渲染 | 作为扩展讨论，不作为半个月主线 |

### 面试反馈反向补强

之前反馈中提到候选人的主要问题是“做过但深度不足”。本项目需要重点补强这些点：

- 不只说 GPU Driven 流程，要能讲清 buffer 布局、counter、AppendBuffer、CopyCount、IndirectArgs 的细节。
- 不只说 Hi-Z 原理，要能讲清 reversed-Z、D3D/OpenGL depth range、保守遮挡、mip 选择、上一帧遮挡延迟。
- 不只说地形 LOD，要能讲清裂缝原因、neighbor 信息、skirt、edge morph、LOD popping 的取舍。
- 不只说虚拟纹理用法，要能讲清 SVT/RVT 区别、page table、cache miss、tile border、mip bleeding。
- 不只说 Vulkan 名词，要能讲清 subpass/input attachment 为什么降低移动端带宽。

## 3. 最终交付物

### 必须交付

1. **Unity 可运行主场景**
   - 一个主入口场景，例如 `GPUDriven_Showcase.unity`。
   - 能一键运行，默认展示大地形、植被、相机飞行、统计 UI。

2. **GPU Driven 地形系统**
   - 地形 patch LOD。
   - 视锥剔除。
   - Hi-Z 遮挡剔除。
   - `DrawMeshInstancedIndirect` 渲染。
   - LOD 颜色可视化。

3. **GPU Driven 植被系统**
   - 草/植被实例生成。
   - cluster/tile 级粗剔除。
   - 可选实例级精剔除。
   - 简单风动 shader。
   - 支持密度、距离、LOD 参数调节。

4. **Hi-Z 深度金字塔**
   - 从相机深度生成 mip chain。
   - 支持可视化每级 mip。
   - 支持开关：
     - 无剔除
     - 仅视锥剔除
     - 视锥 + Hi-Z

5. **性能统计面板**
   - 原始 patch/instance 数。
   - 视锥剔除后数量。
   - Hi-Z 剔除后数量。
   - DrawCall 数。
   - CPU frame time。
   - GPU frame time，如 Unity 版本和平台支持。

6. **技术说明文档**
   - 架构图。
   - 数据流。
   - 关键算法。
   - 性能对比。
   - 已知限制。
   - 面试问答准备。

### 加分交付

1. **轻量虚拟纹理/贴图流送实验**
   - page table。
   - texture array cache。
   - page request。
   - fallback mip。
   - cache miss 可视化。

2. **移动端或 Vulkan 验证**
   - Android Vulkan 跑通截图。
   - RenderDoc 截帧。
   - Unity Profiler 数据。
   - 若没有真机，也至少准备 PC Vulkan/D3D11 对比说明。

3. **3 分钟展示视频**
   - 开关剔除模式。
   - 展示 LOD、Hi-Z、统计变化。
   - 展示地形接缝处理。
   - 展示性能对比。

## 4. 项目架构建议

### 目录建议

```text
Assets/
  GPUDrivenShowcase/
    Scenes/
      GPUDriven_Showcase.unity
    Scripts/
      Core/
        GpuDrivenRenderer.cs
        GpuBufferManager.cs
        IndirectDrawArgs.cs
      Terrain/
        GpuTerrainRenderer.cs
        TerrainPatchBuilder.cs
        TerrainLodController.cs
        TerrainDebugView.cs
      Foliage/
        GpuFoliageRenderer.cs
        FoliageClusterBuilder.cs
        FoliageDebugView.cs
      HiZ/
        HizDepthPyramid.cs
        HizDebugView.cs
      VT/
        VirtualTextureManager.cs
        VirtualTexturePageTable.cs
      UI/
        RuntimeDebugPanel.cs
    Shaders/
      Terrain/
        GpuTerrain.shader
        GpuTerrainForwardPass.hlsl
      Foliage/
        GpuFoliage.shader
      HiZ/
        HizDepthPyramid.compute
      Culling/
        TerrainCulling.compute
        FoliageCulling.compute
      VT/
        VirtualTexture.compute
    Materials/
    Meshes/
    Textures/
```

当前项目已有以下基础，可以迁移或重构：

- `Assets/2_View Frustum Culling`
- `Assets/3_Hiz`
- `Assets/5_Terrain`
- `Assets/StreamingAssets/VirtualTexture.cs`

建议把这些整理到一个统一 showcase 目录中，保留旧目录作为参考，不要一上来大范围重命名所有资源，避免 Unity meta 引用损坏。

### 系统数据流

```text
Camera
  -> Build View/Projection/Frustum Data
  -> Generate Depth Pyramid
  -> Dispatch Terrain Culling
       input: terrain patch buffer, Hi-Z texture, camera data
       output: visible patch id buffer, indirect args
  -> Dispatch Foliage Culling
       input: foliage cluster/instance buffer, Hi-Z texture, camera data
       output: visible instance buffer, indirect args
  -> Draw Terrain Indirect
  -> Draw Foliage Indirect
  -> Render Debug Overlay
```

### Buffer 设计建议

```hlsl
struct TerrainPatch
{
    float4 rect;          // x,z,width,height
    float2 heightMinMax;  // conservative height bound
    uint lod;
    uint neighborMask;
};

struct FoliageCluster
{
    float4 boundsXZ;      // x,z,width,height
    float2 heightMinMax;
    uint instanceOffset;
    uint instanceCount;
};

struct VisibleItem
{
    uint id;
};
```

间接绘制参数：

```text
args[0] = index count per instance
args[1] = visible instance count
args[2] = start index
args[3] = base vertex
args[4] = start instance
```

必须能在面试中解释 `AppendStructuredBuffer`、counter reset、`ComputeBuffer.CopyCount` 到 indirect args 的流程。

## 5. 半个月详细计划

### 第 1 天：确定主线和整理现状

目标：

- 明确最终只做一个主 showcase。
- 跑通当前已有场景。
- 记录已有问题和可复用模块。

任务：

- 打开并运行以下现有场景：
  - `2_View Frustum Culling`
  - `3_Hiz/HizGrass`
  - `5_Terrain/GPUDrivenTerrain`
- 记录每个场景当前是否可运行、是否报错、FPS、主要功能。
- 建立 `GPUDrivenShowcase` 新目录。
- 建立主场景 `GPUDriven_Showcase.unity`。
- 整理 README/技术文档大纲。

验收标准：

- 主场景可以打开。
- 确定哪些代码直接复用，哪些重构。
- 写下当前工程基线数据。

### 第 2 天：统一主场景和调试 UI

目标：

- 把项目从多个学习 demo 收敛为一个技术展示 demo。

任务：

- 搭建大地形场景。
- 加入可移动相机。
- 加入 Runtime Debug Panel。
- 面板显示：
  - 当前剔除模式。
  - terrain patch 总数/可见数。
  - foliage 总数/可见数。
  - CPU frame time。
  - FPS。
- 添加快捷键：
  - `1` 无剔除。
  - `2` 视锥剔除。
  - `3` 视锥 + Hi-Z。
  - `4` LOD debug。
  - `5` Hi-Z debug。

验收标准：

- 主场景有完整 UI。
- 面试演示时不需要在 Inspector 里临时点参数。

### 第 3 天：重构 GPU Driven 地形基础

目标：

- 将现有 `GPUTerrain` 整理成稳定的地形 patch 渲染系统。

任务：

- 生成 terrain patch buffer。
- 每个 patch 记录：
  - rect。
  - lod。
  - neighbor mask。
  - height min/max。
- 使用 `DrawMeshInstancedIndirect` 绘制地形。
- Shader 中根据 patch rect 采样 heightmap。
- 显示 LOD debug color。

验收标准：

- 地形能由 patch 间接绘制出来。
- 不依赖 Unity Terrain 默认渲染展示主体。
- LOD 层级颜色清楚。

### 第 4 天：补全地形 LOD 选择逻辑

目标：

- 做出能解释清楚的 quadtree/clipmap LOD。

任务：

- 根据相机距离选择 patch LOD。
- 每帧更新 active patch list。
- 保持 patch 数量稳定，避免明显闪烁。
- 加入 LOD 参数：
  - patch size。
  - max LOD。
  - lod distance。
- 统计各 LOD patch 数量。

验收标准：

- 相机移动时 LOD 正常切换。
- Debug UI 能显示各级 LOD 数量。
- 能解释为什么这个 LOD 选择适合大场景。

### 第 5 天：重点处理地形接缝

目标：

- 把“地形接缝”做成项目亮点，而不是隐藏问题。

任务：

- 实现 neighbor LOD mask。
- Shader 中对边界顶点做 stitching 或 morph。
- 至少支持以下一种方案：
  - 高 LOD 边界顶点对齐低 LOD。
  - skirt 几何边。
  - edge morph。
- 增加 debug：
  - 显示 neighbor mask。
  - 显示接缝边。

验收标准：

- 不同 LOD patch 相邻时无明显裂缝。
- 文档中明确写出接缝成因和解决方案。

面试必须能讲：

- 裂缝来自相邻 patch 顶点密度不同，高度采样点不一致。
- skirt 简单稳定但会引入额外几何和可能的视觉遮挡。
- edge morph 更自然，但 shader 和邻接信息更复杂。
- neighbor mask 可以由 CPU quadtree 或 GPU LOD pass 生成。

### 第 6 天：Hi-Z 深度金字塔稳定化

目标：

- 把当前 Hi-Z 从 demo 能力升级为可解释、可调试的系统。

任务：

- 生成 depth pyramid。
- 支持当前帧或上一帧深度。
- 支持 reversed-Z / non-reversed-Z 说明。
- 支持 D3D/OpenGL depth range 差异说明。
- 增加 Hi-Z mip 可视化窗口。
- 检查 mip 生成时 min/max 规则：
  - D3D reversed-Z 常见场景要注意比较方向。
  - 传统 depth 越小越近，reversed-Z 越大越近。

验收标准：

- 能在屏幕上查看各级 Hi-Z mip。
- 能解释当前项目使用的是 min depth 还是 max depth。
- 能解释遮挡测试为什么要保守。

### 第 7 天：地形 Hi-Z 遮挡剔除

目标：

- 地形 patch 支持 GPU 视锥剔除 + Hi-Z 遮挡剔除。

任务：

- Compute shader 中对 patch AABB 做 clip space 投影。
- 得到 screen rect。
- 根据 rect size 选择 mip。
- 采样 Hi-Z。
- 写入 visible patch append buffer。
- CopyCount 到 indirect args。
- Debug UI 显示：
  - 原始 patch 数。
  - 视锥后 patch 数。
  - Hi-Z 后 patch 数。

验收标准：

- 遮挡物后面的 terrain patch 能被剔除。
- 切换模式时可见数量变化明显。
- 不出现明显错误剔除。

### 第 8 天：植被系统 cluster 化

目标：

- 草/植被从单纯实例 demo 升级为可扩展的大量实例系统。

任务：

- 将草划分为 foliage cluster/tile。
- 每个 cluster 有 AABB 和 instance range。
- 先按 cluster 做视锥/Hi-Z 粗剔除。
- 可选：对可见 cluster 内实例再做距离/随机密度裁剪。
- 使用 `DrawMeshInstancedIndirect` 绘制。

验收标准：

- 支持 10 万级草实例。
- 剔除开关对性能和可见数量有明显影响。
- 不需要 CPU 每帧遍历所有草实例做判断。

### 第 9 天：植被美术参数和风动

目标：

- 让项目体现“美术管线价值”。

任务：

- 支持密度图或简单 procedural density。
- 参数：
  - 密度。
  - 最大距离。
  - cluster size。
  - 风强度。
  - 风速度。
  - 随机颜色/高度。
- Shader 中实现轻量风动。
- 支持 vegetation debug color。

验收标准：

- 场景视觉上有大面积草/植被。
- 参数能实时调节。
- 能说明这些参数如何交给 TA/美术使用。

### 第 10 天：轻量虚拟纹理/贴图流送原型

目标：

- 做一个能讲清 SVT/RVT 原理的最小闭环。

任务：

- 建立 page table texture。
- 建立 texture array cache。
- 根据相机位置请求地表 tile。
- 支持 page load/unload。
- Shader 根据 page table 采样对应 texture array slice。
- 增加 debug：
  - page table view。
  - loaded page count。
  - cache miss/fallback color。

验收标准：

- 地表贴图能按区域切换或流送。
- 能展示 page table 和 cache。
- 即使功能简化，也要能讲清工业 SVT 还缺什么。

### 第 11 天：虚拟纹理细节补强和文档化

目标：

- 把虚拟纹理从“代码原型”变成“可面试讲深度”的材料。

任务：

- 处理 tile border/padding。
- 说明 mip bleeding 原因。
- 增加 fallback mip。
- 写清 SVT/RVT 区别：
  - SVT 关注超大纹理按需加载和采样。
  - RVT 关注把材质/地形/mesh 输出缓存到 runtime virtual texture，供后续采样复用。
  - Virtual Heightfield Mesh 是基于高度场/虚拟纹理进行几何表达的方案，不等同于 SVT。

验收标准：

- 文档中有一节完整解释虚拟纹理。
- 面试能回答“cache miss 怎么办”“接缝怎么处理”“page table 存什么”。

### 第 12 天：性能 Profiling 和数据对比

目标：

- 准备可信的性能数据，而不是只展示视觉效果。

任务：

- 使用 Unity Profiler 记录：
  - CPU main thread。
  - render thread。
  - batch/draw call。
  - memory。
- 使用 Frame Debugger 检查绘制流程。
- 如可行，使用 RenderDoc 截帧。
- 准备三组对比：
  - 无剔除。
  - 视锥剔除。
  - 视锥 + Hi-Z。
- 准备不同实例规模：
  - 1 万。
  - 5 万。
  - 10 万。
  - 20 万，如机器允许。

验收标准：

- 文档中有性能表格。
- 能明确说明瓶颈在 CPU 还是 GPU。
- 能解释为什么某些情况下 Hi-Z 反而不一定收益。

性能表格模板：

| 场景 | Patch/Instance 总数 | 可见数 | CPU ms | GPU ms | FPS | 说明 |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| 无剔除 | | | | | | |
| 视锥剔除 | | | | | | |
| 视锥 + Hi-Z | | | | | | |

### 第 13 天：移动端/Vulkan 专题准备

目标：

- 针对面试反馈中 Vulkan、subpass、带宽、厂商方案短板做准备。

任务：

- 若有 Android 设备：
  - 打 Android Vulkan 包。
  - 记录是否运行正常。
  - 截图或录屏。
- 若没有设备：
  - 在文档中写清理论分析和计划。
- 准备以下主题说明：
  - Tile-based GPU 和 immediate-mode GPU 的区别。
  - Vulkan subpass/input attachment 如何减少中间 RT 写回和读取。
  - Deferred rendering 在移动端为什么容易 bandwidth heavy。
  - Forward+/Clustered 为什么常用于移动端多光源。
  - VRS、foveated rendering、low resolution rendering 的原理和适用边界。
  - Early-Z、HSR、硬件剔除和软件剔除的关系。

验收标准：

- 文档有“移动端与 Vulkan 分析”章节。
- 面试被问到这些名词时至少能讲原理、适用场景和限制。

### 第 14 天：整理技术说明文档和架构图

目标：

- 让项目从“代码能跑”变成“能打动面试官”。

任务：

- 完成最终技术文档：
  - 项目目标。
  - 系统架构。
  - 渲染流程。
  - Buffer 数据结构。
  - Hi-Z 细节。
  - 地形 LOD 和接缝。
  - 虚拟纹理。
  - 性能数据。
  - 移动端取舍。
  - 已知问题和后续优化。
- 绘制架构图：
  - Camera -> Depth Pyramid -> Culling -> Indirect Args -> Draw。
- 准备代码导览路线：
  - 入口脚本。
  - culling compute。
  - terrain shader。
  - Hi-Z 生成。
  - debug UI。

验收标准：

- 文档能在 10 分钟内讲完。
- 面试官追问任何模块，都能定位到代码和解释取舍。

### 第 15 天：录制 Demo 和模拟面试

目标：

- 完成最终展示闭环。

任务：

- 录制 3 分钟视频：
  - 0:00-0:30 项目目标和场景展示。
  - 0:30-1:10 剔除模式切换和统计变化。
  - 1:10-1:50 地形 LOD 和接缝 debug。
  - 1:50-2:20 Hi-Z mip/遮挡可视化。
  - 2:20-2:50 虚拟纹理/资源流送。
  - 2:50-3:00 性能数据总结。
- 模拟 1 小时面试问答。
- 整理简历项目描述。

验收标准：

- 有可运行项目。
- 有视频。
- 有技术文档。
- 有性能数据。
- 有面试问答稿。

## 6. 技术实现重点

### GPU Driven 主流程

必须讲清楚：

1. CPU 初始化 patch/instance 数据。
2. 数据上传到 `StructuredBuffer`。
3. 每帧设置相机矩阵、Hi-Z texture、剔除参数。
4. Compute shader 判断可见性。
5. 可见对象 id 写入 `AppendStructuredBuffer`。
6. `CopyCount` 把可见数量复制到 indirect args。
7. Shader 通过 `SV_InstanceID` 读取 visible id。
8. `DrawMeshInstancedIndirect` 发起绘制。

注意事项：

- Append buffer 每帧要 reset counter。
- Indirect args 的第二个 uint 是 instance count。
- Bounds 不能太小，否则 Unity 会在 CPU 侧把整个 indirect draw 裁掉。
- Debug readback 会造成 GPU/CPU 同步，不能在正式性能数据中频繁使用。

### Hi-Z 遮挡剔除

必须讲清楚：

- 深度金字塔的每一级保存一个区域的保守深度。
- 投影 AABB 到屏幕空间得到 screen rect。
- 根据 screen rect 大小选择 mip level。
- 采样对应 mip 的深度，与当前对象最近/最远深度比较。
- 保守原则是宁可不剔除，也不能错误剔除可见物体。

风险点：

- 相机近裁面穿过 AABB 时，screen rect 可能异常。
- AABB 部分在相机后方时需要特殊处理。
- 当前帧 Hi-Z 可能与当前剔除互相依赖，因此常用上一帧深度或 depth prepass。
- Reversed-Z 下比较方向容易写反。

### 地形 LOD 和接缝

必须讲清楚：

- 地形 patch LOD 不同导致边界顶点数量和采样点不一致。
- 常见解决方案：
  - skirt：边缘向下拉一圈几何，简单稳定。
  - stitching index：根据邻居 LOD 选择边界索引。
  - edge morph：高 LOD 边界顶点向低 LOD 对齐。
  - geomorph：LOD 切换时平滑过渡，减少 popping。
- 本项目建议优先做 neighbor mask + edge morph 或 skirt，因为半个月内最可控。

### 虚拟纹理

必须讲清楚：

- Page table 存虚拟页到物理 cache 的映射。
- Texture array/cache 存实际加载的 tile。
- Shader 先查 page table，再采样物理纹理。
- Cache miss 时使用 fallback mip 或低清页。
- Tile border/padding 用来减少双线性采样跨 tile 造成的接缝。

### 移动端/Vulkan

必须讲清楚：

- 移动端 tile-based GPU 会把 tile 内颜色/深度尽量留在片上内存。
- Vulkan subpass/input attachment 可以减少中间结果写回外部内存再读回。
- Deferred rendering 会产生 GBuffer，移动端 bandwidth 压力较大。
- Forward+/Clustered 可以在移动端用较低带宽支持多光源。
- GPU Driven 减少 CPU draw submit 压力，但 compute culling 本身也有成本。

## 7. 面试展示脚本

### 3 分钟版本

1. 项目目标：
   - “这个 demo 是一个 Unity URP 移动端大场景 GPU Driven 渲染子系统，主要解决 CPU 提交、不可见对象绘制、地形 LOD 和资源流送问题。”

2. 场景展示：
   - 展示大地形和植被。
   - 显示统计面板。

3. 剔除流程：
   - 切换无剔除、视锥剔除、Hi-Z 剔除。
   - 指出 visible count 和 frame time 变化。

4. 地形 LOD：
   - 打开 LOD debug color。
   - 移动相机，展示 LOD 切换。
   - 打开接缝 debug，说明 neighbor mask。

5. Hi-Z：
   - 打开 Hi-Z mip debug。
   - 说明 screen rect 选 mip 和保守深度。

6. 总结：
   - “这个项目我重点关注的是可落地的管线，不是单点效果。后续可以扩展到 GPU scene、clustered lighting、occlusion history 和完整 SVT。”

### 10 分钟版本

结构：

1. 为什么做这个项目。
2. 总体架构。
3. 数据结构。
4. 剔除流程。
5. 地形 LOD 和接缝。
6. Hi-Z 细节。
7. 虚拟纹理。
8. 性能数据。
9. 移动端取舍。
10. 已知问题和后续优化。

## 8. 简历项目描述

可以写成：

> 基于 Unity URP 实现了一套移动端大场景 GPU Driven 渲染 Demo，包含地形 patch LOD、植被 cluster、GPU 视锥剔除、Hi-Z 遮挡剔除、DrawMeshInstancedIndirect 间接绘制、LOD/Hi-Z 可视化调试和轻量虚拟纹理实验。项目通过 Compute Shader 完成可见性计算和 indirect args 生成，降低 CPU 提交压力，并针对地形 LOD 接缝、深度金字塔保守剔除、移动端带宽和 Vulkan subpass 等问题进行了专项分析和性能对比。

## 9. 面试高频问题准备

### GPU Driven

- GPU Driven 相比传统 CPU culling + draw call 的收益是什么？
- Unity 中 `DrawMeshInstancedIndirect` 的 args buffer 每个字段是什么？
- 为什么要用 AppendBuffer？
- `CopyCount` 的目标 offset 为什么通常是 4？
- GPU culling 的缺点是什么？
- 什么时候 GPU culling 不划算？

### Hi-Z

- Hi-Z mip 里应该存 min depth 还是 max depth？
- Reversed-Z 对比较方向有什么影响？
- 为什么遮挡剔除要保守？
- AABB 跨近裁面怎么处理？
- 当前帧 Hi-Z 和上一帧 Hi-Z 的区别是什么？
- 为什么小物体用 Hi-Z 可能不划算？

### 地形

- Quadtree LOD 和 clipmap LOD 有什么区别？
- 地形裂缝为什么出现？
- skirt、stitching、morph 各有什么优缺点？
- LOD popping 怎么处理？
- 地形 normal 怎么生成和采样？
- 地形 shadow caster 怎么处理？

### 虚拟纹理

- SVT 和 RVT 有什么区别？
- Page table 存什么？
- 物理纹理 cache 怎么管理？
- cache miss 怎么处理？
- tile border 为什么必要？
- mip bleeding 怎么解决？

### 移动端/Vulkan

- Tile-based GPU 为什么对带宽敏感？
- Vulkan subpass 为什么能省带宽？
- Deferred rendering 在移动端有什么问题？
- Forward+ 和 Clustered lighting 的区别是什么？
- VRS 和 foveated rendering 的适用场景是什么？
- HSR、Early-Z、Hi-Z、软件剔除之间是什么关系？

## 10. 风险和取舍

### 最大风险

1. 半个月内想做太多，导致每个模块都浅。
2. 工程不稳定，面试演示时跑不起来。
3. 只有视觉效果，没有性能数据。
4. 能跑但讲不清底层原理。

### 取舍原则

- 优先完整闭环，不优先功能数量。
- 优先可解释，不优先复杂炫技。
- 优先调试视图和性能数据，不优先美术精度。
- 优先地形 LOD/Hi-Z/GPU Driven 三条主线，不要被 ML 渲染分散精力。

### 可砍功能

如果时间不够，按以下顺序砍：

1. 完整虚拟纹理，只保留 page table 原型和文档分析。
2. Android 真机验证，只保留 PC Vulkan/理论分析。
3. 复杂 PBR 地表材质，只保留简单 splat 和 debug。
4. 实例级草剔除，只保留 cluster 级剔除。

### 不能砍的功能

1. 主场景可运行。
2. GPU Driven 间接绘制。
3. 视锥剔除。
4. Hi-Z 遮挡剔除。
5. 地形 LOD 和接缝处理说明。
6. 性能对比数据。
7. 技术说明文档。

## 11. 最终检查清单

### 工程检查

- [ ] 主场景能一键运行。
- [ ] 无 Console error。
- [ ] 无明显资源丢失。
- [ ] 剔除模式切换正常。
- [ ] LOD debug 正常。
- [ ] Hi-Z debug 正常。
- [ ] 性能统计正常。
- [ ] 退出 Play Mode 后 buffer 正确释放。

### 文档检查

- [ ] 有架构图。
- [ ] 有数据流说明。
- [ ] 有 buffer 结构说明。
- [ ] 有 Hi-Z 原理说明。
- [ ] 有地形接缝说明。
- [ ] 有虚拟纹理说明。
- [ ] 有移动端/Vulkan 分析。
- [ ] 有性能表格。
- [ ] 有已知问题。

### 面试检查

- [ ] 能 3 分钟讲完项目亮点。
- [ ] 能 10 分钟讲完整架构。
- [ ] 能解释每个核心 compute shader。
- [ ] 能解释 indirect args。
- [ ] 能解释 Hi-Z 比较方向。
- [ ] 能解释地形裂缝解决方案。
- [ ] 能解释 SVT/RVT 区别。
- [ ] 能解释 Vulkan subpass 和带宽。

## 12. 建议最终项目标题

推荐标题：

**Unity URP Mobile GPU Driven Terrain & Foliage Rendering Pipeline**

中文标题：

**Unity URP 移动端 GPU Driven 地形与植被渲染管线**

这个标题比“GPU Driven Demo”更好，因为它同时传达了：

- Unity/URP。
- 移动端意识。
- GPU Driven。
- 地形和植被这类真实游戏场景。
- 管线能力，而不是单点效果。

## 13. 执行记录

### 2026-06-29 第一阶段：必须交互骨架

已完成：

- 新增 `Assets/GPUDrivenShowcase` 目录。
- 新增运行时控制器 `GpuDrivenShowcaseController`。
- 新增运行时 IMGUI 面板 `GpuDrivenShowcaseRuntimePanel`。
- 新增运行时 bootstrap，进入 Play Mode 后自动创建控制器和面板。
- 新增自由相机控制 `GpuDrivenFreeCamera`。
- 新增 Editor 菜单：
  - `GPU Driven Showcase/Add Interaction Layer To Current Scene`
  - `GPU Driven Showcase/Create Empty Showcase Scene`
  - `GPU Driven Showcase/Build Default Scene Asset`
- 接入现有 `GPUTerrain`：
  - 支持无剔除、视锥剔除、视锥 + Hi-Z 三种模式。
  - 支持 LOD/Bounds debug 触发 Gizmos。
  - 支持地形 patch 总数/可见数统计。
  - 增加全量 visible id buffer，修正无剔除模式下 indirect draw 计数问题。
  - 增加 compute dispatch 越界保护。
- 接入现有 `DrawGrass`：
  - 支持无剔除、视锥剔除、视锥 + Hi-Z 三种模式。
  - 修正无剔除模式下 `CopyCount` 覆盖全量 args 的问题。
  - 增加 grass compute dispatch 越界保护。
  - 支持草实例总数/可见数统计。

运行方式：

- 打开任意已有场景，例如 `Assets/5_Terrain/GPUDrivenTerrain.unity` 或 `Assets/3_Hiz/HizGrass.unity`。
- 直接进入 Play Mode。
- 左上角自动出现 `GPU Driven Showcase` 面板。
- 快捷键：
  - `1` 无剔除。
  - `2` 视锥剔除。
  - `3` 视锥 + Hi-Z。
  - `4` LOD debug。
  - `5` Hi-Z debug。
  - `F1` 显示/隐藏面板。
  - `F5` 重新绑定模块。
  - 鼠标右键 + `WASDQE` 控制相机。

验证记录：

- 当前环境未在 PATH 和默认 Unity Hub 路径找到 Unity 可执行文件，因此没有跑 batchmode C# 编译。
- `TerrainCulling.compute` 和 `GrassCulling.compute` 的 shader compiler 日志显示已成功编译。
- 运行时统计中的 visible count 目前低频使用 `GetData` 采样，只作为交互面板反馈；正式性能数据阶段需要改为更严格的 profiling 方式，避免 GPU/CPU 同步影响结论。

下一步：

- 创建正式 `GPUDriven_Showcase.unity` 主场景并把地形、Hi-Z、草模块合并进去。
- 将当前 IMGUI 面板保留为工程调试入口，后续再按需要替换成 uGUI/UIToolkit。
- 开始第二阶段：统一主场景、参数面板和可视化数据流。

修复记录：

- 地形视锥剔除从“8 个 AABB 顶点至少一个在 NDC 内”改为“只有 AABB 完全落在某个 clip plane 外才剔除”。旧判断会误剔穿过屏幕边缘的大地形块，快速转镜头时尤其明显。
- 地形 Hi-Z 增加快速相机运动保护。检测到相机旋转/位移突变时，短暂降级为视锥剔除，避免上一帧/未稳定深度金字塔造成错误遮挡。
- 对跨近裁面或投影异常的 terrain patch 保守通过 Hi-Z，避免 screen rect 和 mip 选择异常导致露块。
- 进一步修正 terrain culling 的 GPU clip-space 一致性：C# 侧传入 `GL.GetGPUProjectionMatrix` 后的 VP，compute 侧按当前图形 API 判断 D3D/OpenGL 深度范围。
- 修正 terrain patch 高度 bounds：heightmap 采样改为 world XZ -> terrain UV，并加入高度 padding，避免高度包围盒过小造成边界误剔。
- Frustum plane test 加入 clip-space padding，并对相机运动方向/边界处 patch 更保守处理。Hi-Z 在相机快速移动时更长时间退回 Frustum。

### URP 迁移记录

已完成：

- 新增 URP RendererFeature：`GpuDrivenHizFeature`。
  - 在 URP `AfterRenderingPrePasses` 阶段生成 Hi-Z depth pyramid。
  - 继续输出到 `DepthTextureGenerator.DepthTexture`，地形/草的 compute culling 不需要改引用。
- `DepthTextureGenerator` 改为双路径：
  - Built-in 管线下保留旧 `CameraEvent + CommandBuffer`。
  - URP 管线下只负责初始化和持有 Hi-Z RenderTexture，实际生成交给 RendererFeature。
- `GPUTerrain.shader` 迁移到 URP include：
  - `UnityCG.cginc` -> `Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl`
  - `UnityObjectToClipPos` -> `TransformWorldToHClip`
  - Forward pass 增加 `LightMode = UniversalForward`。
- `DepthTextureMipmapCalculator.shader` 迁移到 URP `Core.hlsl`。
- 新增 Editor 菜单：
  - `GPU Driven Showcase/Setup URP Pipeline`
  - 自动创建 URP Pipeline Asset、UniversalRendererData，并添加 `GPU Driven Hi-Z` RendererFeature。

迁移使用方式：

- 在 Unity Editor 中执行 `GPU Driven Showcase/Setup URP Pipeline`。
- 确认 `Project Settings/Graphics` 和当前 Quality Level 已指向 `Assets/GPUDrivenShowcase/Settings/GPUDriven_URP.asset`。
- 打开 `GPUDrivenTerrain` 或 showcase 场景运行，`3 Hi-Z` 会走 URP RendererFeature 生成的 depth pyramid。

注意：

- 当前迁移范围只覆盖 GPUDriven 主线：地形、草、Hi-Z、Showcase 面板。
- `Assets/0_CommandBuffer` 下的 Built-in CommandBuffer 示例暂未迁移到 URP RendererFeature，它们属于旧学习 demo，不作为当前面试主线。
