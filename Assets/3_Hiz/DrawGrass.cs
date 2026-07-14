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
    uint lastVisibleGrassCount;
    float nextStatsReadbackTime;
    bool lastHizActive;
    bool debugStatsEnabled;
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

    void Start()
    {
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = 60;

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
    }

    void InitComputeShader()
    {
        kernel = compute.FindKernel("GrassCulling");
        compute.SetInt("grassCount", grassCount);
        Vector4 depthTextureSize = depthTextureGenerator != null
            ? new Vector4(depthTextureGenerator.DepthTextureWidth, depthTextureGenerator.DepthTextureHeight, depthTextureGenerator.DepthTextureMipCount, 0.0f)
            : new Vector4(Screen.width, Screen.height, Mathf.FloorToInt(Mathf.Log(Mathf.Max(Screen.width, Screen.height), 2.0f)) + 1, 0.0f);
        compute.SetVector("depthTextureSize", depthTextureSize);
        compute.SetBool("isOpenGL", IsOpenGLClipSpace());
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
        lastHizActive = useHiz && depthTextureGenerator != null && depthTextureGenerator.DepthTexture != null;
        if (compute != null)
        {
            compute.SetBool("useHiz", lastHizActive);
        }

        if (useCulling)
        {
            if (depthTextureGenerator != null && depthTextureGenerator.DepthTexture != null)
            {
                RenderTexture depthTexture = depthTextureGenerator.DepthTexture;
                compute.SetVector("depthTextureSize", new Vector4(
                    depthTexture.width,
                    depthTexture.height,
                    depthTextureGenerator.DepthTextureMipCount,
                    0.0f));
                compute.SetTexture(kernel, hizTextureId, depthTexture);
            }
            else
            {
                compute.SetVector("depthTextureSize", Vector4.zero);
                compute.SetTexture(kernel, hizTextureId, Texture2D.blackTexture);
            }
            compute.SetMatrix(vpMatrixId, GL.GetGPUProjectionMatrix(mainCamera.projectionMatrix, false) * mainCamera.worldToCameraMatrix);
            cullResultBuffer.SetCounterValue(0);
            compute.SetBuffer(kernel, cullResultBufferId, cullResultBuffer);
            compute.Dispatch(kernel, 1 + grassCount / 640, 1, 1);
            grassMaterial.SetBuffer(rtsBufferId, cullResultBuffer);

            // 获取实际要渲染的数量
            ComputeBuffer.CopyCount(cullResultBuffer, argsBuffer, sizeof(uint));
        }
        else
        {
            grassMaterial.SetBuffer(rtsBufferId, grassMatrixBuffer);
            args[1] = (uint)grassCount;
            argsBuffer.SetData(args);
            lastVisibleGrassCount = args[1];
        }

        if (debugStatsEnabled && useCulling && Time.unscaledTime >= nextStatsReadbackTime)
        {
            nextStatsReadbackTime = Time.unscaledTime + 0.25f;
            argsBuffer.GetData(args);
            lastVisibleGrassCount = args[1];
        }
        Graphics.DrawMeshInstancedIndirect(grassMesh, subMeshIndex, grassMaterial,
            new Bounds(Vector3.zero, new Vector3(1000.0f, 1000.0f, 1000.0f)),
            argsBuffer, 0, null, ShadowCastingMode.On, true, gameObject.layer, mainCamera);
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

    bool useCulling;

    bool useHiz;

    public void SetMode(int index)
    {
        switch (index)
        {
            case 0:
                useCulling = false;
                useHiz = false;
                break;
            case 1:
                useCulling = true;
                useHiz = false;
                break;
            case 2:
                useCulling = true;
                useHiz = true;
                break;
        }
        if (depthTextureGenerator != null)
        {
            depthTextureGenerator.useHiz = useCulling && depthTextureGenerator.DepthTexture != null;
        }
        if (compute != null)
        {
            compute.SetBool("useHiz", useHiz);
        }
    }

    public void SetShowcaseCullingMode(GpuDrivenShowcaseCullingMode mode)
    {
        useCulling = mode.UsesFrustum();
        useHiz = mode.UsesHiZ();
        if (depthTextureGenerator != null)
        {
            depthTextureGenerator.useHiz = useCulling && depthTextureGenerator.DepthTexture != null;
        }
        if (compute != null)
        {
            compute.SetBool("useHiz", useHiz);
        }
    }

    public void SetShowcaseDebugView(GpuDrivenShowcaseDebugView view)
    {
        debugStatsEnabled = view == GpuDrivenShowcaseDebugView.SceneWire;
    }

    public void CollectShowcaseStats(ref GpuDrivenShowcaseStats stats)
    {
        stats.foliageInstanceCount += grassCount;
        stats.foliageVisibleInstanceCount += useCulling ? (int)lastVisibleGrassCount : grassCount;
        stats.hizEnabled |= lastHizActive;
    }

    static bool IsOpenGLClipSpace()
    {
        var deviceType = SystemInfo.graphicsDeviceType;
        return deviceType == UnityEngine.Rendering.GraphicsDeviceType.OpenGLCore ||
               deviceType == UnityEngine.Rendering.GraphicsDeviceType.OpenGLES2 ||
               deviceType == UnityEngine.Rendering.GraphicsDeviceType.OpenGLES3;
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
