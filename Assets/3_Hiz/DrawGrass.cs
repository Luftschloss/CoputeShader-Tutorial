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

        //CommandBuffer a = new CommandBuffer();
        
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
