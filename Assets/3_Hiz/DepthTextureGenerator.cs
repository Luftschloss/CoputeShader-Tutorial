using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

[RequireComponent(typeof(Camera))]
public class DepthTextureGenerator : MonoBehaviour
{
    public Shader depthTextureShader;
    RenderTexture depthTexture;
    public RenderTexture DepthTexture => depthTexture;

    int depthTextureSize = 0;
    public int DepthTextureSize
    {
        get
        {
            if (depthTextureSize == 0)
                depthTextureSize = Mathf.NextPowerOfTwo(Mathf.Max(Screen.width, Screen.height));
            return depthTextureSize;
        }
    }

    Material depthTextureMaterial;

    const RenderTextureFormat depthTextureFormat = RenderTextureFormat.RFloat; //深度取值范围0-1，单通道即可

    int depthTextureShaderID;

    CommandBuffer depthMipmapGenerateCMD;


    void Start()
    {
        depthTextureMaterial = new Material(depthTextureShader);
        Camera.main.depthTextureMode |= DepthTextureMode.Depth;
        depthTextureShaderID = Shader.PropertyToID("_CameraDepthTexture");
        InitDepthTexture();

        depthMipmapGenerateCMD = new CommandBuffer();
        depthMipmapGenerateCMD.name = "Generate DepthMipmapTexture";
        Camera.main.AddCommandBuffer(CameraEvent.AfterDepthTexture, depthMipmapGenerateCMD);
    }

    void InitDepthTexture()
    {
        if (depthTexture != null) return;
        depthTexture = new RenderTexture(DepthTextureSize, DepthTextureSize, 0, depthTextureFormat);
        depthTexture.autoGenerateMips = false;
        depthTexture.useMipMap = true;
        depthTexture.filterMode = FilterMode.Point;
        depthTexture.Create();
    }

    //生成mipmap
    void OnPostRender()
    {
        depthMipmapGenerateCMD.Clear();

        int w = depthTexture.width;
        int mipmapLevel = 0;

        RenderTexture currentRenderTexture = null;//当前mipmapLevel对应的mipmap
        RenderTexture preRenderTexture = null;//上一层的mipmap，即mipmapLevel-1对应的mipmap

        //如果当前的mipmap的宽高大于8，则计算下一层的mipmap
        while (w > 8)
        {
            currentRenderTexture = RenderTexture.GetTemporary(w, w, 0, depthTextureFormat);
            currentRenderTexture.filterMode = FilterMode.Point;
            if (preRenderTexture == null)
            {
                //Mipmap[0]即copy原始的深度图
                depthMipmapGenerateCMD.Blit(BuiltinRenderTextureType.Depth, currentRenderTexture);
            }
            else
            {
                //将Mipmap[i] Blit到Mipmap[i+1]上
                depthMipmapGenerateCMD.Blit(preRenderTexture, currentRenderTexture, depthTextureMaterial);
                RenderTexture.ReleaseTemporary(preRenderTexture);
            }
            depthMipmapGenerateCMD.CopyTexture(currentRenderTexture, 0, 0, depthTexture, 0, mipmapLevel);
            preRenderTexture = currentRenderTexture;

            w /= 2;
            mipmapLevel++;
        }
        RenderTexture.ReleaseTemporary(preRenderTexture);
    }

    void OnDestroy()
    {
        depthTexture?.Release();
        Destroy(depthTexture);
    }
}