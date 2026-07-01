using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public sealed class GpuDrivenHizFeature : ScriptableRendererFeature
{
    public static int LastTerrainDepthDrawCount { get; private set; }

    [SerializeField] private Shader mipmapShader;
    [SerializeField] private Shader depthCopyShader;
    [SerializeField] private Shader terrainDepthShader;
    [SerializeField] private RenderPassEvent passEvent = RenderPassEvent.AfterRenderingOpaques;
    [SerializeField] private bool flipDepthY;

    private GpuDrivenHizPass pass;
    private bool enqueuePass;
    private static readonly int FlipYId = Shader.PropertyToID("_FlipY");
    private RenderPassEvent EffectivePassEvent => passEvent < RenderPassEvent.AfterRenderingOpaques
        ? RenderPassEvent.AfterRenderingOpaques
        : passEvent;

    public override void Create()
    {
        if (mipmapShader == null)
        {
            mipmapShader = Shader.Find("ComputeShader/DepthTextureMipmapCalculator");
        }
        if (depthCopyShader == null)
        {
            depthCopyShader = Shader.Find("GPU Driven/URP Depth To RFloat");
        }
        if (terrainDepthShader == null)
        {
            terrainDepthShader = Shader.Find("GPU Driven/GPUTerrain Hi-Z Depth");
        }

        pass = new GpuDrivenHizPass(depthCopyShader, mipmapShader, terrainDepthShader)
        {
            renderPassEvent = EffectivePassEvent
        };
    }

    public override void SetupRenderPasses(ScriptableRenderer renderer, in RenderingData renderingData)
    {
        pass?.ClearSetup();

        if (!enqueuePass || pass == null || !pass.IsValid)
        {
            return;
        }

        DepthTextureGenerator generator = ResolveDepthTextureGenerator(renderingData.cameraData.camera);
        if (generator == null || !generator.useHiz || generator.DepthTexture == null)
        {
            return;
        }

        pass.Setup(generator);
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        enqueuePass = false;

        if (pass == null || !pass.IsValid)
        {
            return;
        }

        if (renderingData.cameraData.isPreviewCamera)
        {
            return;
        }

        LastTerrainDepthDrawCount = 0;

        DepthTextureGenerator generator = ResolveDepthTextureGenerator(renderingData.cameraData.camera);
        if (generator == null || !generator.useHiz || generator.DepthTexture == null)
        {
            return;
        }

        pass.renderPassEvent = EffectivePassEvent;
        pass.SetFlipY(flipDepthY);
        enqueuePass = true;
        renderer.EnqueuePass(pass);
    }

    protected override void Dispose(bool disposing)
    {
        pass?.Dispose();
        pass = null;
    }

    private static DepthTextureGenerator ResolveDepthTextureGenerator(Camera camera)
    {
        if (camera != null)
        {
            DepthTextureGenerator generator = camera.GetComponent<DepthTextureGenerator>();
            if (generator != null)
            {
                return generator;
            }

            if (camera.cameraType == CameraType.SceneView)
            {
                return Object.FindObjectOfType<DepthTextureGenerator>(true);
            }
        }

        return null;
    }

    private sealed class GpuDrivenHizPass : ScriptableRenderPass
    {
        private readonly Material mipmapMaterial;
        private readonly Material depthCopyMaterial;
        private readonly Material terrainDepthMaterial;
        private DepthTextureGenerator generator;

        public bool IsValid => mipmapMaterial != null && depthCopyMaterial != null;

        public GpuDrivenHizPass(Shader depthCopyShader, Shader mipmapShader, Shader terrainDepthShader)
        {
            depthCopyMaterial = depthCopyShader != null ? CoreUtils.CreateEngineMaterial(depthCopyShader) : null;
            mipmapMaterial = mipmapShader != null ? CoreUtils.CreateEngineMaterial(mipmapShader) : null;
            terrainDepthMaterial = terrainDepthShader != null ? CoreUtils.CreateEngineMaterial(terrainDepthShader) : null;
            ConfigureInput(ScriptableRenderPassInput.Depth);
        }

        public void Setup(DepthTextureGenerator targetGenerator)
        {
            generator = targetGenerator;
        }

        public void ClearSetup()
        {
            generator = null;
        }

        public void SetFlipY(bool value)
        {
            if (depthCopyMaterial != null)
            {
                depthCopyMaterial.SetFloat(FlipYId, value ? 1.0f : 0.0f);
            }
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            LastTerrainDepthDrawCount = 0;
            RenderTexture depthTexture = generator != null ? generator.DepthTexture : null;
            if (depthTexture == null)
            {
                return;
            }

            CommandBuffer cmd = CommandBufferPool.Get("GPU Driven Hi-Z Pyramid");
            RenderTexture previous = null;
            RenderTexture current = null;
            List<RenderTexture> temporaries = new List<RenderTexture>(16);
            int width = depthTexture.width;
            int mip = 0;
            RenderTextureFormat depthTextureFormat = generator.DepthTextureFormat;

            while (width > 8)
            {
                current = RenderTexture.GetTemporary(width, width, 0, depthTextureFormat);
                current.filterMode = FilterMode.Point;
                current.wrapMode = TextureWrapMode.Clamp;
                temporaries.Add(current);

                if (previous == null)
                {
                    cmd.SetRenderTarget(current);
                    cmd.DrawProcedural(Matrix4x4.identity, depthCopyMaterial, 0, MeshTopology.Triangles, 3, 1);
                    LastTerrainDepthDrawCount = DrawGpuTerrainDepth(cmd);
                }
                else
                {
                    cmd.Blit(previous, current, mipmapMaterial);
                }

                cmd.CopyTexture(current, 0, 0, depthTexture, 0, mip);
                previous = current;
                width >>= 1;
                mip++;
            }

            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);

            for (int i = 0; i < temporaries.Count; i++)
            {
                RenderTexture.ReleaseTemporary(temporaries[i]);
            }
        }

        public void Dispose()
        {
            CoreUtils.Destroy(depthCopyMaterial);
            CoreUtils.Destroy(mipmapMaterial);
            CoreUtils.Destroy(terrainDepthMaterial);
        }

        private int DrawGpuTerrainDepth(CommandBuffer cmd)
        {
            if (terrainDepthMaterial == null)
            {
                return 0;
            }

            int drawCount = 0;
            int shaderPass = SystemInfo.usesReversedZBuffer ? 0 : 1;
            for (int i = 0; i < GPUTerrain.ActiveTerrainCount; i++)
            {
                GPUTerrain terrain = GPUTerrain.GetActiveTerrain(i);
                if (terrain != null && terrain.DrawHiZDepth(cmd, terrainDepthMaterial, shaderPass))
                {
                    drawCount++;
                }
            }

            return drawCount;
        }
    }
}
