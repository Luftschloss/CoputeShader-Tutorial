using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class ViewFrustumCulling : MonoBehaviour
{
    public int instanceCount = 1000;

    int lastInstanceCount = -1;

    public ComputeShader cullingShader;

    public Mesh instanceMesh;
    public Material instanceMat;
    Bounds meshBound = new Bounds(new Vector3(0, 0.5f, 0), new Vector3(0.9f, 1.05f, 0.45f));

    [SerializeField]Bounds instanceArea; 

    [SerializeField]bool debug = false;
    

    Camera cam;
    ComputeBuffer localToWorldMatrixBuffer;
    ComputeBuffer cullResult;
    ComputeBuffer argsBuffer;
    uint[] args = new uint[5] { 0, 0, 0, 0, 0 };

    List<Matrix4x4> localToWorldMatrixs = new List<Matrix4x4>();
    int kernel;

    void Start()
    {
        cam = Camera.main;
        kernel = cullingShader.FindKernel("ViewPortCulling");
        cullResult = new ComputeBuffer(instanceCount, sizeof(float) * 16, ComputeBufferType.Append);
        argsBuffer = new ComputeBuffer(1, sizeof(uint) * args.Length, ComputeBufferType.IndirectArguments);
        UpdateBuffer();
    }

    void UpdateBuffer()
    {
        if (localToWorldMatrixBuffer != null)
            localToWorldMatrixBuffer.Release();
        localToWorldMatrixBuffer = new ComputeBuffer(instanceCount, sizeof(float) * 16);
        Matrix4x4[] localToWorldMatrixArray = new Matrix4x4[instanceCount];
        for (int i = 0; i < instanceCount; i++)
        {
            float x = Random.Range(instanceArea.min.x, instanceArea.max.x);
            float y = Random.Range(instanceArea.min.y, instanceArea.max.y);
            float z = Random.Range(instanceArea.min.z, instanceArea.max.z);

            Vector3 randomPos = new Vector3(x, y, z);
            Quaternion randomRot = Random.rotation.normalized;
            localToWorldMatrixArray[i] = Matrix4x4.TRS(randomPos, randomRot, Vector3.one);
        }
        localToWorldMatrixBuffer.SetData(localToWorldMatrixArray);

        if (instanceMesh != null)
        {
            //5个参数
            //1.offset: index count per instance
            //2.instance count
            //3.start index location
            //4.base vertex location
            //5.start instance location.
            args[0] = (uint)instanceMesh.GetIndexCount(0);
            args[1] = (uint)instanceCount;
            args[2] = (uint)instanceMesh.GetIndexStart(0);
            args[3] = (uint)instanceMesh.GetBaseVertex(0);
            args[4] = 0;
        }
        else
        {
            args[0] = args[1] = args[2] = args[3] = 0;
        }
        argsBuffer.SetData(args);

        lastInstanceCount = instanceCount;
    }

    private void Update()
    {
        if (lastInstanceCount != instanceCount)
            UpdateBuffer();
        Vector4[] planes = CameraTool.GetFrustumPlane(cam);
        cullingShader.SetBuffer(kernel, "input", localToWorldMatrixBuffer);
        cullResult.SetCounterValue(0);
        cullingShader.SetInt("instanceCount", instanceCount);
        cullingShader.SetBuffer(kernel, "cullresult", cullResult);
        cullingShader.SetVectorArray("planes", planes);
        cullingShader.Dispatch(kernel, 1 + (instanceCount / 640), 1, 1);
        instanceMat.SetBuffer("rtsBuffer", cullResult);

        ComputeBuffer.CopyCount(cullResult, argsBuffer, sizeof(uint));
        Graphics.DrawMeshInstancedIndirect(instanceMesh, 0, instanceMat, meshBound, argsBuffer);
    }

    private void OnDisable()
    {
        localToWorldMatrixBuffer?.Release();
        localToWorldMatrixBuffer = null;

        cullResult?.Dispose();
        cullResult = null;

        argsBuffer?.Dispose();
        argsBuffer = null;
    }

    private void OnDrawGizmos()
    {
        if (debug)
        {
            Gizmos.color = Color.red;
            Gizmos.DrawWireCube(instanceArea.center, instanceArea.size);
        }
    }
}
