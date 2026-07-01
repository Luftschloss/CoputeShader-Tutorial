using UnityEngine;
using UnityEngine.Rendering;

public class MipmapGenerator2 : MonoBehaviour
{
    public Texture2D srcT2D;
    public Shader depthTextureShader;
    Material depthTextureMaterial;

    [SerializeField] RenderTexture depthTexture;

    [SerializeField] int size = 1024;
    [SerializeField] int mipCount = 0;

    void Start()
    {
        if (depthTextureShader == null)
            depthTextureShader = Shader.Find("ComputeShader/DepthTextureMipmapCalculator");
        depthTextureMaterial = new Material(depthTextureShader);
        GenerateRWTexture(size, size, mipCount);
    }

    void GenerateRWTexture(int width, int height, int mipmap = 0)
    {
        if (depthTexture != null)
            return;
        depthTexture= RenderTexture.GetTemporary(width, height, 0);
        depthTexture.useMipMap = mipmap > 0;
        depthTexture.autoGenerateMips = false;
        depthTexture.enableRandomWrite = true;
        depthTexture.filterMode = FilterMode.Point;
        depthTexture.Create();
    }

    private void OnPreRender()
    {
        int w = depthTexture.width;
        int mipmapLevel = 0;
        RenderTexture currentRenderTexture = null;//当前mipmapLevel对应的mipmap
        RenderTexture preRenderTexture = null;//上一层的mipmap，即mipmapLevel-1对应的mipmap

        //如果当前的mipmap的宽高大于8，则计算下一层的mipmap
        for (int i = 0; i < mipCount; i++)
        {
            currentRenderTexture = RenderTexture.GetTemporary(w, w, 0, RenderTextureFormat.RFloat);
            currentRenderTexture.filterMode = FilterMode.Point;
            if (preRenderTexture == null)
            {
                //Mipmap[0]copy原始的深度图
                Graphics.Blit(srcT2D, currentRenderTexture);
            }
            else
            {
                //将Mipmap[i] Blit到Mipmap[i+1]上
                Graphics.Blit(preRenderTexture, currentRenderTexture, depthTextureMaterial);
                RenderTexture.ReleaseTemporary(preRenderTexture);
            }
            Graphics.CopyTexture(currentRenderTexture, 0, 0, depthTexture, 0, mipmapLevel);
            preRenderTexture = currentRenderTexture;

            w /= 2;
            mipmapLevel++;
        }
        RenderTexture.ReleaseTemporary(preRenderTexture);
    }
}
