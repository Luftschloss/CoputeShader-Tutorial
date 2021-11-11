using UnityEngine;

public class MipmapGenerator : MonoBehaviour
{
    public Texture2D srcT2D;
    public ComputeShader computeShader;

    int blitCopyKernelID, downSampleKernelID;
    int srcTexID, mipTexID, copyMipTexID;
    int srcSizeID, dstSizeID;
    int mipID;

    [SerializeField] RenderTexture mipRT;
    [SerializeField] RenderTexture copyMipRT;

    [SerializeField] bool PingPong = false;
    [SerializeField] int size = 1024;
    [SerializeField] int mipCount = 0;

    void Start()
    {
        blitCopyKernelID = computeShader.FindKernel("BlitCopy");
        downSampleKernelID = computeShader.FindKernel("DownSample");
        srcTexID = Shader.PropertyToID("SrcTex");
        mipTexID = Shader.PropertyToID("MipTex");
        copyMipTexID = Shader.PropertyToID("MipCopyTex");
        srcSizeID = Shader.PropertyToID("srcTexSize");
        dstSizeID = Shader.PropertyToID("dstTexSize");
        mipID = Shader.PropertyToID("Mip");

        if (PingPong)
            Shader.EnableKeyword("_PING_PONG_COPY");
        else
            Shader.DisableKeyword("_PING_PONG_COPY");
        GenerateMipmap(size, size, mipCount);

    }

    void GenerateMipmap(int width, int height, int mipmaps)
    {
        int dstWidth = width;
        int dstHeight = height;
        mipRT = GenerateRWTexture(width, height, mipmaps);
        if (PingPong)
            copyMipRT = GenerateRWTexture(width, height, mipmaps);
        //BlitCopy
        computeShader.SetVector(srcSizeID, new Vector4(srcT2D.width, srcT2D.height, 0, 0));
        computeShader.SetVector(dstSizeID, new Vector4(width, height, 0, 0));
        computeShader.SetTexture(blitCopyKernelID, srcTexID, srcT2D);
        computeShader.SetTexture(blitCopyKernelID, mipTexID, mipRT, 0);
        if (PingPong)
            computeShader.SetTexture(blitCopyKernelID, copyMipTexID, copyMipRT, 0);
        computeShader.Dispatch(blitCopyKernelID, Mathf.CeilToInt(width / 8f), 
            Mathf.CeilToInt(height / 8f), 1);

        //Mip
        if (PingPong)
            computeShader.SetTexture(downSampleKernelID, srcTexID, copyMipRT);
        else
            computeShader.SetTexture(downSampleKernelID, srcTexID, mipRT);
        RenderTexture pingTex = copyMipRT;
        RenderTexture pongTex = null;
        for (int i = 1; i < mipmaps; i++)
        {
            dstWidth = Mathf.CeilToInt(dstWidth / 2.0f);
            dstHeight = Mathf.CeilToInt(dstHeight / 2.0f);
            computeShader.SetVector(dstSizeID, new Vector4(dstWidth, dstHeight, 0, 0));
            computeShader.SetInt(mipID, i);
            computeShader.SetTexture(downSampleKernelID, mipTexID, mipRT, i);
            if (PingPong)
            {
                pongTex = GenerateRWTexture(dstWidth, dstHeight);
                computeShader.SetTexture(downSampleKernelID, copyMipTexID, pongTex);
            }
            computeShader.Dispatch(downSampleKernelID, Mathf.CeilToInt(dstHeight / 8f),
                Mathf.CeilToInt(dstHeight / 8f), 1);
            if(PingPong)
            {
                RenderTexture.ReleaseTemporary(pingTex);
                computeShader.SetTexture(downSampleKernelID, srcTexID, pongTex);
                pingTex = pongTex;
            }
        }
        if(PingPong)
            RenderTexture.ReleaseTemporary(pingTex);
    }

    RenderTexture GenerateRWTexture(int width, int height, int mipmap = 0)
    {
        var rt = RenderTexture.GetTemporary(width, height, 0);
        rt.useMipMap = mipmap > 0;
        rt.autoGenerateMips = false;
        rt.enableRandomWrite = true;
        rt.filterMode = FilterMode.Point;
        rt.Create();
        return rt;
    }
}
