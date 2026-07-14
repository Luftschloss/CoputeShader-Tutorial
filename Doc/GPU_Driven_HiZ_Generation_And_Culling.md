# GPU Driven Hi-Z 生成与剔除细节

本文记录当前项目里的 Hi-Z 生成、绑定和剔除约定。重点对应这些文件：

- `Assets/3_Hiz/DepthTextureGenerator.cs`
- `Assets/GPUDrivenShowcase/Scripts/URP/GpuDrivenHizFeature.cs`
- `Assets/GPUDrivenShowcase/Shaders/GpuDrivenHizMap.compute`
- `Assets/5_Terrain/GPUTerrain.cs`
- `Assets/5_Terrain/TerrainCulling.compute`
- `Assets/GPUDrivenShowcase/Scripts/Foliage/GpuDrivenFoliageRenderer.cs`
- `Assets/GPUDrivenShowcase/Shaders/GpuDrivenFoliageCulling.compute`
- `Assets/3_Hiz/DrawGrass.cs`
- `Assets/3_Hiz/GrassCulling.compute`

## 总体流程

当前 URP 路径由 `GpuDrivenHizFeature` 生成 Hi-Z pyramid：

1. 相机上挂 `DepthTextureGenerator`，并由 terrain / foliage / grass culling 侧把 `useHiz` 置为 true。
2. `DepthTextureGenerator` 创建或复用 Hi-Z `RenderTexture`。
3. `GpuDrivenHizFeature` 在 `RenderPassEvent.BeforeRenderingTransparents` 读取 `renderer.cameraDepthTargetHandle`。
4. `GpuDrivenHizMap.compute` 的 `Blit` kernel 从 camera depth 直接构建 Hi-Z mip0。
5. `CSMain` kernel 逐层 reduce 后续 mip。
6. pass 执行后调用 `DepthTextureGenerator.MarkHiZUpdated(camera, matrixVP)`，记录生成本次 pyramid 使用的相机、VP、相机位置和 Hi-Z 尺寸。
7. terrain / foliage / grass culling 绑定 `_HizMap`、尺寸向量和相关矩阵后执行遮挡剔除。

## HZB 尺寸和 mip 数

默认尺寸模式是 `DepthTextureSizeMode.ScreenHalfPowerOfTwo`。当前实现不再先 blit 到固定 `512x512`，而是按当前视口尺寸直接得到 HZB mip0：

```text
HZBSize.X = RoundUpToPowerOfTwo(ViewRect.Width()) >> 1
HZBSize.Y = RoundUpToPowerOfTwo(ViewRect.Height()) >> 1
NumMips = max(floor(log2(max(HZBSize.X, HZBSize.Y))), 1)
```

实现对应：

- `CalculateHZBDimension(viewSize)`：`Mathf.NextPowerOfTwo(viewSize) >> 1`，并至少为 1。
- `CalculateHiZMipCount(size)`：按最大边计算 `floor(log2(maxSize))`，并至少为 1。
- `_HizMapSize` / `_DepthTextureSize` 的约定是 `(width, height, activeMipCount, 0)`。

注意：`activeMipCount` 是剔除侧允许采样的有效 mip 数，不等同于“完整降到 1x1 的 mip 链”。例如 `1024x512` 会生成 10 层，合法 mip 是 `0..9`；最大边的最后有效尺寸是 `2`，不会额外暴露最终 `1` 那一层。这样和当前 HZB 规则保持一致，并避免采样未生成的 mip。

固定尺寸模式 `FixedPowerOfTwo` 仍保留，用于调试或强制固定 HZB 尺寸；外部 depth texture 模式会用外部贴图的尺寸，并把有效 mip 数限制到外部贴图已有 `mipmapCount` 内。

## mip0 生成

`GpuDrivenHizMap.compute` 的 `Blit` kernel 不是简单复制 camera depth。它把每个 HZB mip0 像素映射到 camera depth 上对应的源像素范围：

```hlsl
srcMin = floor(dstPixel * SrcSize / DstSize)
srcMax = ceil((dstPixel + 1) * SrcSize / DstSize) - 1
```

由于 HZB mip0 是 half power-of-two，源图到目标图的单轴比例不会超过 2，所以四个角样本覆盖该目标像素对应的最多 `2x2` 源范围。随后用 `ReduceDepth` 得到保守深度。

后续 mip 由 `CSMain` 从上一层做 `2x2` reduce。尺寸每层分别折半，因此非正方形 HZB 是合法路径；shader 通过 clamp 处理奇数尺寸和边界。

Windows / Editor 下启用 `_PING_PONG_COPY`。这条路径同时写目标 mip 和临时 ping-pong RT，下一个 mip 从临时 RT 读，避免同一个 mipmapped UAV 在不同 mip 间读写时遇到后端限制。

## reversed-Z 和比较方向

`GpuDrivenHizMap.compute` 的 reduce 方向和剔除比较必须配套：

```hlsl
#if _REVERSE_Z
    mipDepth = min(d1, d2, d3, d4)
#else
    mipDepth = max(d1, d2, d3, d4)
#endif
```

含义：

- reversed-Z：raw depth 越大越近，mip 保存该区域内最远的遮挡深度。只有采样到的保守深度仍然比被测物更近时，才能剔除。
- normal-Z：raw depth 越小越近，mip 保存该区域内最远的遮挡深度。只有该最远遮挡深度仍然比被测物更近时，才能剔除。

terrain culling 中的判断：

```hlsl
#if _REVERSE_Z
    depth = maxP.z;
    occluded = d1 > depth && d2 > depth && d3 > depth && d4 > depth;
#else
    depth = minP.z;
    occluded = d1 < depth && d2 < depth && d3 < depth && d4 < depth;
#endif
```

## terrain Hi-Z 剔除

`GPUTerrain` 的 terrain path 是当前最完整的 Hi-Z 消费者：

1. `BindHiZTextureIfReady` 通过 `DepthTextureGenerator.TryGetCurrentHiZ(camera, ...)` 获取当前相机对应的 Hi-Z 快照。
2. 如果 Hi-Z 未生成、相机不匹配或还在启动阶段，则 `_UseHiZ=false`，并绑定 `Texture2D.blackTexture` 到 `_HizMap`。
3. 如果 Hi-Z 就绪，则绑定 `_HizMap`、`_HizMapSize`、`_HizCameraMatrixVP`、`_HizCameraPositionWS`。

`TerrainCulling.compute` 的 `CullTerrain` 先做 frustum culling：

- `_VPMatrix` 使用当前相机 VP。
- OpenGL clip space 的 near plane 使用 `z < -w`。
- D3D/Metal/Vulkan 风格使用 `z < 0`。

frustum 通过后，如果 `_UseHiZ=true`，再做 Hi-Z occlusion：

1. 从 patch 的 `rect + heightMinMax` 构建 world-space AABB。
2. 用 `_HizCameraMatrixVP` 投影 8 个角到 Hi-Z 的 uv-depth 空间。
3. 根据投影后的 uv AABB 和 `_HizMapSize.xy` 计算 mip0 像素跨度。
4. 用 `ceil(log2(max(projectedWidth, projectedHeight)))` 选 mip，并 clamp 到 `_HizMapSize.z - 1`。
5. 在该 mip 上采样投影矩形的四个角。
6. 四个角都证明该 patch 在遮挡深度之后时才剔除。

terrain 特意使用生成 Hi-Z 时记录的 `_HizCameraMatrixVP`，而不是直接复用当前 `_VPMatrix`。这样可以避免同一帧内相机或投影矩阵变化后，用不匹配的坐标去采样已经生成好的 Hi-Z。

`_HizDepthBias` 会把 bounds 朝相机方向轻微偏移，用于降低共面或接近共面时的误剔除概率。

debug stats buffer 当前约定：

```text
[0] Hi-Z tested
[1] Hi-Z rejected
[2] Hi-Z skipped
[3] dispatch/input count
[4] frustum visible
[5] frustum rejected
```

## foliage 和 legacy grass

`GpuDrivenFoliageRenderer`、`DrawGrass` 使用同一张 `DepthTextureGenerator.DepthTexture` 和同一个尺寸约定：

```text
_DepthTextureSize / depthTextureSize = (width, height, activeMipCount, 0)
```

它们都会用 `activeMipCount - 1` 限制最高采样 mip，并在 Hi-Z 不可用时绑定 `Texture2D.blackTexture`，避免启动阶段 compute shader 报纹理槽未设置。

需要注意：当前 foliage / legacy grass path 比 terrain path 简化。它们主要根据 `DepthTexture` 是否存在来绑定 Hi-Z，并使用当前 `_VPMatrix` 投影 bounds；没有像 terrain 一样通过 `TryGetCurrentHiZ` 获取生成 Hi-Z 时的历史 VP。后续如果 foliage 对相机时序更敏感，应把它改成和 terrain 一样消费 `MarkHiZUpdated` 记录的快照。

## 启动和 fallback

Unity compute shader 在 dispatch 前通常要求 kernel 使用到的纹理属性已经绑定。即使 `_UseHiZ=false`，如果 `_HizMap` / `_HiZMap` / `hizTexture` 没有设置，也可能出现类似错误：

```text
Compute shader (TerrainCulling): Property (_HizMap) at kernel index (0) is not set
```

因此当前绑定策略是：

- Hi-Z ready：绑定真实 Hi-Z RT。
- Hi-Z disabled / not ready：绑定 `Texture2D.blackTexture`，同时把尺寸向量置零或 `_UseHiZ=false`。

这样可以保证启动帧、相机切换帧和 Hi-Z 还没生成的帧不会因为纹理槽未设置而报错。

## 当前限制

terrain depth injection 入口已经存在：

- `GPUTerrain.DrawHiZDepth(CommandBuffer cmd, Material depthMaterial, int shaderPass)`
- `Assets/5_Terrain/GPUTerrainHiZDepth.shader`
- `writeTerrainDepthToHiZ`

但当前 `GpuDrivenHizFeature` 没有调用 `GPUTerrain.DrawHiZDepth`。所以实际 Hi-Z 输入主要来自 URP camera depth target。只有当 GPU terrain 已经在 URP camera depth 生成阶段写入 depth 时，它才会自然成为 Hi-Z occluder。

`DepthTextureGenerator.OnPreRender` 里还保留了 built-in render pipeline 的旧 command buffer 生成路径。当前 showcase 的主路径是 URP `GpuDrivenHizFeature`，后续如果完全不支持 built-in 管线，可以再单独评估是否移除这段 legacy path。
