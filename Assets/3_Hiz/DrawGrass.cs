using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

public class DrawGrass : MonoBehaviour
{
    public Mesh grassMesh;
    public int subMeshIndex = 0;
    public Material grassMaterial;
    /// <summary>
    /// 每行草的数量
    /// </summary>
    public int GrassCountPerRaw = 300;

    public DepthTextureGenerator depthTextureGenerator;
    /// <summary>
    /// 剔除的ComputeShader
    /// </summary>
    public ComputeShader compute;
    /// <summary>
    /// 总的草的数目
    /// </summary>
    int grassCount;
    int kernel;
    Camera mainCamera;
    [SerializeField]Terrain terrain;

    uint[] args = new uint[5] { 0, 0, 0, 0, 0 };
    ComputeBuffer argsBuffer;
    /// <summary>
    /// 所有草的世界坐标矩阵
    /// </summary>
    ComputeBuffer grassMatrixBuffer;
    /// <summary>
    /// 剔除后的结果
    /// </summary>
    ComputeBuffer cullResultBuffer;

    int cullResultBufferId, vpMatrixId, rtsBufferId, hizTextureId;

    #region Depth
    [SerializeField]private Shader depthTextureShader;
    Material depthTextureMaterial;
    int depthTextureSize;

    RenderTargetIdentifier depthIdentifier;
    int depthTexID = Shader.PropertyToID("CopiedDepthTex");
    int colorTexID = Shader.PropertyToID("CopiedColorTex");

    RenderTexture colorTexture;
    RenderTexture depthTexture;

    RenderTexture depthBufferTexture;
    RenderTexture colorBufferTexture;

    CommandBuffer copyDepthCMD;
    CommandBuffer copyColorCMD;

    List<CommandBuffer> depthMipmapCMDs = new List<CommandBuffer>();
    
    #endregion

    void Start()
    {
        grassCount = GrassCountPerRaw * GrassCountPerRaw;
        mainCamera = Camera.main;

        if (grassMesh != null)
        {
            args[0] = grassMesh.GetIndexCount(subMeshIndex);
            args[1] = (uint)grassCount;
            args[2] = grassMesh.GetIndexStart(subMeshIndex);
            args[3] = grassMesh.GetBaseVertex(subMeshIndex);
            args[4] = 0;
        }

        InitComputeBuffer();
        InitGrassPosition();
        InitComputeShader();
        InitCommandBuffer();
    }

    RenderTargetIdentifier depth;

    void InitCommandBuffer()
    {
        depthIdentifier  = new RenderTargetIdentifier(depthTexID);
        depthTextureSize = Mathf.NextPowerOfTwo(Mathf.Max(Screen.width, Screen.height));
        depthTextureMaterial = new Material(depthTextureShader);

        colorBufferTexture = RenderTexture.GetTemporary(Screen.width, Screen.height, 0, RenderTextureFormat.Default, RenderTextureReadWrite.Default);
        colorBufferTexture.name = "ColorBuffer";
        depthBufferTexture = new RenderTexture(Screen.width, Screen.height, 24, RenderTextureFormat.Depth);
        depthBufferTexture.name = "DepthBuffer";

        // Copy Depth
        depthTexture = new RenderTexture(depthTextureSize, depthTextureSize, 0, RenderTextureFormat.RHalf);
        depthTexture.name = "DepthTex";
        depthTexture.autoGenerateMips = false;
        depthTexture.useMipMap = true;
        depthTexture.filterMode = FilterMode.Point;
        copyDepthCMD = new CommandBuffer();
        copyDepthCMD.name = "CommandBuffer_DepthBuffer";
        copyDepthCMD.Blit(depthBufferTexture.depthBuffer, depthTexture.colorBuffer);
        mainCamera.AddCommandBuffer(CameraEvent.AfterForwardOpaque, copyDepthCMD);
        Shader.SetGlobalTexture(depthTexID, depthTexture);

        //Copy Color
        colorTexture = new RenderTexture(Screen.width, Screen.height, 0, RenderTextureFormat.Default);
        colorTexture.name = "AfterSkyboxTex";
        copyColorCMD = new CommandBuffer();
        copyColorCMD.name = "CommandBuffer_ColorBuffer";
        copyColorCMD.Blit(colorBufferTexture, colorTexture);
        mainCamera.AddCommandBuffer(CameraEvent.AfterSkybox, copyColorCMD);
        Shader.SetGlobalTexture(colorTexID, colorTexture);

        int w = depthTextureSize;
        int mipmapTempID = Shader.PropertyToID("Temp");
        int mipmapLevel = 0;

        int preTempID = -1;
        int currentTempID = -1;
        
        RenderTargetIdentifier preIdentifier = new RenderTargetIdentifier();
        RenderTargetIdentifier currentIdentifier = new RenderTargetIdentifier();

        RenderTexture currentRenderTexture = null;//当前mipmapLevel对应的mipmap
        RenderTexture preRenderTexture = null;//上一层的mipmap，即mipmapLevel-1对应的mipmap

        //如果当前的mipmap的宽高大于8，则计算下一层的mipmap
        while (w > 8)
        {
            currentTempID = Shader.PropertyToID("DepthMipmap" + mipmapLevel);
            CommandBuffer depthMipmapCmd = new CommandBuffer();
            depthMipmapCmd.GetTemporaryRT(currentTempID, w,w,0, FilterMode.Point, RenderTextureFormat.RHalf);
            currentIdentifier = new RenderTargetIdentifier(currentTempID);

            currentRenderTexture = RenderTexture.GetTemporary(w, w, 0, RenderTextureFormat.RHalf);
            currentRenderTexture.filterMode = FilterMode.Point;
            //if (preTempID == -1)
            if (preRenderTexture == null)
            {
                //Mipmap[0]即copy原始的深度图
                depthMipmapCmd.Blit(depthTexture.colorBuffer, currentRenderTexture);
            }
            else
            {
                //将Mipmap[i] Blit到Mipmap[i+1]上
                depthMipmapCmd.Blit(preRenderTexture, currentRenderTexture, depthTextureMaterial);
                //depthMipmapCmd.ReleaseTemporaryRT(preTempID);
                RenderTexture.ReleaseTemporary(preRenderTexture);
            }
            depthMipmapCmd.CopyTexture(currentRenderTexture, 0, 0, depthTexture, 0, mipmapLevel);
        
            mainCamera.AddCommandBuffer(CameraEvent.AfterForwardOpaque, depthMipmapCmd);
            depthMipmapCMDs.Add(depthMipmapCmd);

            preRenderTexture = currentRenderTexture;
            preTempID = currentTempID;
            preIdentifier = currentIdentifier;
            w /= 2;
            mipmapLevel++;
        }
        RenderTexture.ReleaseTemporary(preRenderTexture);
    }

    void InitComputeShader()
    {
        kernel = compute.FindKernel("GrassCulling");
        compute.SetInt("grassCount", grassCount);
        compute.SetInt("depthTextureSize", depthTextureGenerator.DepthTextureSize);
        compute.SetBool("isOpenGL", Camera.main.projectionMatrix.Equals(GL.GetGPUProjectionMatrix(Camera.main.projectionMatrix, false)));
        compute.SetBuffer(kernel, "grassMatrixBuffer", grassMatrixBuffer);

        cullResultBufferId = Shader.PropertyToID("cullResultBuffer");
        vpMatrixId = Shader.PropertyToID("vpMatrix");

        hizTextureId = Shader.PropertyToID("hizTexture");
        rtsBufferId = Shader.PropertyToID("rtsBuffer");
    }

    void InitComputeBuffer()
    {
        if (grassMatrixBuffer != null) return;
        argsBuffer = new ComputeBuffer(1, args.Length * sizeof(uint), ComputeBufferType.IndirectArguments);
        argsBuffer.SetData(args);
        grassMatrixBuffer = new ComputeBuffer(grassCount, sizeof(float) * 16);
        cullResultBuffer = new ComputeBuffer(grassCount, sizeof(float) * 16, ComputeBufferType.Append);
    }

    void Update()
    {
        compute.SetTexture(kernel, hizTextureId, depthTextureGenerator.DepthTexture);
        compute.SetMatrix(vpMatrixId, GL.GetGPUProjectionMatrix(mainCamera.projectionMatrix, false) * mainCamera.worldToCameraMatrix);
        cullResultBuffer.SetCounterValue(0);
        compute.SetBuffer(kernel, cullResultBufferId, cullResultBuffer);
        compute.Dispatch(kernel, 1 + grassCount / 640, 1, 1);
        grassMaterial.SetBuffer(rtsBufferId, cullResultBuffer);

        //获取实际要渲染的数量
        ComputeBuffer.CopyCount(cullResultBuffer, argsBuffer, sizeof(uint));
        Graphics.DrawMeshInstancedIndirect(grassMesh, subMeshIndex, grassMaterial, new Bounds(Vector3.zero, new Vector3(500.0f, 500.0f, 100.0f)), argsBuffer);
    }

    /// <summary>
    /// 获取每个草的LocalToWorld矩阵
    /// </summary>
    void InitGrassPosition()
    {
        int width = 250;
        int widthStart = -width / 2;
        float step = (float)width / GrassCountPerRaw;

        Matrix4x4[] grassMatrixs = new Matrix4x4[grassCount];
        for (int i = 0; i < GrassCountPerRaw; i++)
        {
            for (int j = 0; j < GrassCountPerRaw; j++)
            {
                Vector3 position = new Vector3(widthStart + step * (i + Random.Range(0, 0.5f)), 0, widthStart + step * (j + Random.Range(0, 0.5f)));
                position.y = terrain.SampleHeight(position);
                grassMatrixs[i * GrassCountPerRaw + j] = Matrix4x4.TRS(position, Quaternion.identity, Vector3.one);
            }
        }
        grassMatrixBuffer.SetData(grassMatrixs);
    }

    void OnDisable()
    {
        grassMatrixBuffer?.Release();
        grassMatrixBuffer = null;

        cullResultBuffer?.Release();
        cullResultBuffer = null;

        argsBuffer?.Release();
        argsBuffer = null;
    }
}
