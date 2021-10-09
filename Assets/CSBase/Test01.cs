using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class Test01 : MonoBehaviour
{
    [Header("CSP1—FillRT")]
    public ComputeShader shader;
    public RenderTexture temp;

    void Start()
    {
        RunShader1();
        InitShader2();
    }

    void RunShader1()
    {
        int kernelIdx = shader.FindKernel("CSMain");
        temp = new RenderTexture(256, 256, 24);
        temp.enableRandomWrite = true;
        temp.Create();

        shader.SetTexture(kernelIdx, "Result", temp);
        shader.Dispatch(kernelIdx, 256 / 8, 256 / 8, 1);
    }

    [Header("CSP2—Particle")]
    public int mParticleCount = 20000;
    /// <summary>
    /// ComputeBufferType:https://docs.microsoft.com/en-us/windows/win32/direct3d11/direct3d-11-advanced-stages-cs-resources
    /// 
    /// Default：Default ComputeBuffer type (structured buffer)
    /// ComputeBuffer的默认类型，对应HLSL shader中的StructuredBuffer或RWStructuredBuffer，常用于自定义Struct的Buffer传递。
    /// 
    /// Raw：Raw ComputeBuffer type (byte address buffer)
    /// 把里面的内容（byte）做偏移，可用于寻址。它对应HLSL shader中的ByteAddressBuffer或RWByteAddressBuffer，用于着色器访问的底层DX11格式为无类型的R32。
    /// 
    /// Append：Append-Consume ComputeBuffer type
    /// 允许我们像处理Stack一样处理Buffer，例如动态添加和删除元素。它对应HLSL shader中的AppendStructuredBuffer或ConsumeStructuredBuffer。
    /// 
    /// Counter：ComputeBuffer with a counter
    /// 用作计数器，可以为RWStructuredBuffer添加一个计数器，然后在ComputeShader中使用IncrementCounter或DecrementCounter方法来增加或减少计数器的值。
    /// 由于Metal和Vulkan平台没有原生的计数器，因此我们需要一个额外的小buffer用来做计数器。
    /// 
    /// Constant：ComputeBuffer that you can use as a constant buffer (uniform buffer)
    /// 该buffer可以被当做Shader.SetConstantBuffer和Material.SetConstantBuffer中的参数。
    /// 如果想要绑定一个structured buffer那么还需要添加ComputeBufferType.Structured，但是在有些平台（例如DX11）不支持一个buffer即是constant又是structured的。
    /// 
    /// Structured：ComputeBuffer that you can use as a structured buffer
    /// 如果没有使用其他的ComputeBufferType那么等价于Default。
    /// 
    /// IndirectArguments：Indirect arguments
    /// 被用作 Graphics.DrawProceduralIndirect，ComputeShader.DispatchIndirect或Graphics.DrawMeshInstancedIndirect这些方法的参数。buffer大小至少要12字节，DX11底层UAV为R32_UINT，SRV为无类型的R32。
    /// 
    /// DrawIndirect
    /// GPUMemeory
    /// </summary>
    ComputeBuffer mParticleDataBuffer;
    int particleKernelId;
    public Material psMat;

    void InitShader2()
    {
        mParticleDataBuffer = new ComputeBuffer(mParticleCount, 28);
        ParticleData[] particleDatas = new ParticleData[mParticleCount];
        mParticleDataBuffer.SetData(particleDatas);
        particleKernelId = shader.FindKernel("UpdateParticle");
    }

    private void Update()
    {
        RunShader2();
    }

    void RunShader2()
    {
        shader.SetBuffer(particleKernelId, "dataBuffer", mParticleDataBuffer);
        shader.SetFloat("Time", Time.time % 10);
        shader.Dispatch(particleKernelId, mParticleCount / 1000, 1, 1);
        psMat.SetBuffer("_particleDataBuffer", mParticleDataBuffer);
    }

    void OnRenderObject()
    {
        psMat.SetPass(0);
        Graphics.DrawProceduralNow(MeshTopology.Points, mParticleCount);
    }

    void OnDestroy()
    {
        mParticleDataBuffer.Release();
        mParticleDataBuffer = null;
    }
}

/// <summary>
/// (3+4)*4
/// </summary>
struct ParticleData
{
    public Vector3 pos;
    public Color color;
}

/*
 * 一些补充：
 * 1、UAV（Unordered Access view）
 * Unordered 无序，Access 即访问，view代表的是“data in the required format”，RWTexture，RWStructuredBuffer这些类型都属于UAV的数据类型
 * 并且它们支持多线程在读取的同时写入，只能在Fragment Shader和Compute Shader中被使用（绑定），创建RT的enableRandomWrite需要开启
 * 
 * 2、SRV（Shader resource view）
 * 不能被读写的数据类型，例如Texure2D
 * 
 * 3、Wrap / WaveFront
 * numthreads定义每个线程组内线程的数量，那么使用numthreads(1,1,1)每个线程组只有一个线程嘛？NO！
 * GPU的模式是SIMT（single-instruction multiple-thread，单指令多线程），在NVIDIA的显卡中，一个SM(streaming multiprocessor)可调度多个wrap，每个wrap里会有32个线程。
 * 可以简单的理解为一个指令最少也会调度32个并行的线程。AMD的显卡中这个数量为64，称之为wavefront。
 * 即对于NVIDIA的显卡，设置线程组numthreads(1,1,1)，实际线程组依旧会有32个线程执行，但是多出来的31个线程完全就处于没有使用的状态，造成浪费。
 * 因此我们在使用numthreads时，最好将线程组的数量定义为64的倍数，这样两种显卡都可以顾及到。
 * 
 * 4、移动端支持
 * 可通过SystemInfo.supportsComputeShaders判断是否支持
 * OpenGL ES3.1之后，然而有些Android手机即使支持ComputeShader，但是对RWStructuredBuffer的支持并不友好。例如在某些OpenGL ES 3.1的手机上，只支持Fragment Shader内访问StructuredBuffer
 * Vulkan和Metal都支持
 * Shader Model 4.5及之后
 * 
 * 5、Compute Shader中全局变量的定义
 * static float3 boxSize3 = float3(3.0f, 3.0f, 3.0f); 
 */