using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public sealed class GpuDrivenHizFeature : ScriptableRendererFeature
{
    [SerializeField] private ComputeShader hizMapCompute;
    [SerializeField] private RenderPassEvent passEvent = RenderPassEvent.BeforeRenderingTransparents;

    private GpuDrivenHizPass pass;
    private RenderPassEvent EffectivePassEvent => passEvent;

    public override void Create()
    {
#if UNITY_EDITOR
        if (hizMapCompute == null)
        {
            hizMapCompute = UnityEditor.AssetDatabase.LoadAssetAtPath<ComputeShader>(
                "Assets/GPUDrivenShowcase/Shaders/GpuDrivenHizMap.compute");
        }
#endif
        if (hizMapCompute == null)
        {
            Debug.LogError("GPU Driven Hi-Z compute shader is missing.");
        }

        pass = new GpuDrivenHizPass(hizMapCompute)
        {
            renderPassEvent = EffectivePassEvent
        };
    }

    public override void SetupRenderPasses(ScriptableRenderer renderer, in RenderingData renderingData)
    {
        pass?.SetDepthSource(renderer.cameraDepthTargetHandle);
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        pass?.ClearSetup();

        if (pass == null || !pass.IsValid)
        {
            return;
        }

        if (renderingData.cameraData.isSceneViewCamera || renderingData.cameraData.isPreviewCamera)
        {
            return;
        }

        DepthTextureGenerator generator = ResolveDepthTextureGenerator(renderingData.cameraData.camera);
        if (generator == null || !generator.useHiz || generator.DepthTexture == null)
        {
            return;
        }

        pass.renderPassEvent = EffectivePassEvent;
        pass.Setup(generator);
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
        }

        return null;
    }

    private sealed class GpuDrivenHizPass : ScriptableRenderPass
    {
        private const int BlitKernel = 0;
        private const int ReduceKernel = 1;
        private readonly ComputeShader hizMapCompute;
        private DepthTextureGenerator generator;
        private RTHandle cameraDepthSource;

        private static readonly int InTexId = Shader.PropertyToID("InTex");
        private static readonly int MipTexId = Shader.PropertyToID("MipTex");
        private static readonly int MipCopyTexId = Shader.PropertyToID("MipCopyTex");
        private static readonly int PingTexId = Shader.PropertyToID("GpuDrivenHizPingTex");
        private static readonly int PongTexId = Shader.PropertyToID("GpuDrivenHizPongTex");
        private static readonly int SrcTexSizeId = Shader.PropertyToID("_SrcTexSize");
        private static readonly int InputTexSizeId = Shader.PropertyToID("_InputTexSize");
        private static readonly int DstTexSizeId = Shader.PropertyToID("_DstTexSize");
        private static readonly int MipId = Shader.PropertyToID("_Mip");

        public bool IsValid => hizMapCompute != null;

        public GpuDrivenHizPass(ComputeShader hizMapCompute)
        {
            this.hizMapCompute = hizMapCompute;
            ConfigureKeywords();
            ConfigureInput(ScriptableRenderPassInput.Depth);
        }

        public void Setup(DepthTextureGenerator targetGenerator)
        {
            generator = targetGenerator;
        }

        public void SetDepthSource(RTHandle source)
        {
            cameraDepthSource = source;
        }

        public void ClearSetup()
        {
            generator = null;
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            RenderTexture depthTexture = generator != null ? generator.DepthTexture : null;
            if (depthTexture == null)
            {
                return;
            }

            if (cameraDepthSource == null)
            {
                return;
            }

            CommandBuffer cmd = CommandBufferPool.Get("GPU Driven Hi-Z Pyramid");
            int dstWidth = depthTexture.width;
            int dstHeight = depthTexture.height;
            uint threadX;
            uint threadY;
            uint threadZ;
            hizMapCompute.GetKernelThreadGroupSizes(BlitKernel, out threadX, out threadY, out threadZ);

            cmd.SetComputeTextureParam(hizMapCompute, BlitKernel, InTexId, cameraDepthSource);
            cmd.SetComputeTextureParam(hizMapCompute, BlitKernel, MipTexId, depthTexture, 0);
            cmd.SetComputeVectorParam(hizMapCompute, SrcTexSizeId, new Vector4(
                renderingData.cameraData.camera.pixelWidth,
                renderingData.cameraData.camera.pixelHeight,
                0.0f,
                0.0f));
            cmd.SetComputeVectorParam(hizMapCompute, DstTexSizeId, new Vector4(dstWidth, dstHeight, 0.0f, 0.0f));

#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            GetTempHizMapTexture(cmd, PingTexId, depthTexture.width, generator.DepthTextureFormat);
            cmd.SetComputeTextureParam(hizMapCompute, BlitKernel, MipCopyTexId, new RenderTargetIdentifier(PingTexId));
#endif

            int groupX = Mathf.CeilToInt(dstWidth / (float)threadX);
            int groupY = Mathf.CeilToInt(dstHeight / (float)threadY);
            cmd.DispatchCompute(hizMapCompute, BlitKernel, groupX, groupY, 1);

            hizMapCompute.GetKernelThreadGroupSizes(ReduceKernel, out threadX, out threadY, out threadZ);
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            cmd.SetComputeTextureParam(hizMapCompute, ReduceKernel, InTexId, new RenderTargetIdentifier(PingTexId));
#else
            cmd.SetComputeTextureParam(hizMapCompute, ReduceKernel, InTexId, depthTexture);
#endif

            int pingTex = PingTexId;
            int pongTex = PongTexId;
            for (int mip = 1; mip < depthTexture.mipmapCount; mip++)
            {
                int inputWidth = dstWidth;
                int inputHeight = dstHeight;
                dstWidth = Mathf.CeilToInt(dstWidth / 2.0f);
                dstHeight = Mathf.CeilToInt(dstHeight / 2.0f);
                cmd.SetComputeVectorParam(hizMapCompute, InputTexSizeId, new Vector4(inputWidth, inputHeight, 0.0f, 0.0f));
                cmd.SetComputeVectorParam(hizMapCompute, DstTexSizeId, new Vector4(dstWidth, dstHeight, 0.0f, 0.0f));
                cmd.SetComputeIntParam(hizMapCompute, MipId, mip);
                cmd.SetComputeTextureParam(hizMapCompute, ReduceKernel, MipTexId, depthTexture, mip);

#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
                GetTempHizMapTexture(cmd, pongTex, dstWidth, generator.DepthTextureFormat);
                cmd.SetComputeTextureParam(hizMapCompute, ReduceKernel, MipCopyTexId, new RenderTargetIdentifier(pongTex));
#endif

                groupX = Mathf.CeilToInt(dstWidth / (float)threadX);
                groupY = Mathf.CeilToInt(dstHeight / (float)threadY);
                cmd.DispatchCompute(hizMapCompute, ReduceKernel, groupX, groupY, 1);

#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
                cmd.ReleaseTemporaryRT(pingTex);
                cmd.SetComputeTextureParam(hizMapCompute, ReduceKernel, InTexId, new RenderTargetIdentifier(pongTex));
                int temp = pingTex;
                pingTex = pongTex;
                pongTex = temp;
#endif
            }

#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            cmd.ReleaseTemporaryRT(pingTex);
#endif

            Camera camera = renderingData.cameraData.camera;
            Matrix4x4 matrixVP = GL.GetGPUProjectionMatrix(camera.projectionMatrix, false) * camera.worldToCameraMatrix;
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
            generator.MarkHiZUpdated(camera, matrixVP);
        }

        public void Dispose()
        {
        }

        private void ConfigureKeywords()
        {
            if (hizMapCompute == null)
            {
                return;
            }

            if (SystemInfo.usesReversedZBuffer)
            {
                hizMapCompute.EnableKeyword("_REVERSE_Z");
            }
            else
            {
                hizMapCompute.DisableKeyword("_REVERSE_Z");
            }

#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            hizMapCompute.EnableKeyword("_PING_PONG_COPY");
#else
            hizMapCompute.DisableKeyword("_PING_PONG_COPY");
#endif
        }

        private static void GetTempHizMapTexture(CommandBuffer cmd, int nameId, int size, RenderTextureFormat format)
        {
            RenderTextureDescriptor desc = new RenderTextureDescriptor(size, size, format, 0, 1)
            {
                autoGenerateMips = false,
                useMipMap = false,
                enableRandomWrite = true
            };
            cmd.GetTemporaryRT(nameId, desc, FilterMode.Point);
        }

    }
}
