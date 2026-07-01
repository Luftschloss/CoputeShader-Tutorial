using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

[RequireComponent(typeof(Camera))]
public class DepthTextureGenerator : MonoBehaviour
{
    public Shader depthTextureShader;
    [SerializeField]RenderTexture depthTexture;
    public RenderTexture DepthTexture
    {
        get
        {
            EnsureDepthTexture();
            return depthTexture;
        }
    }

    public DepthType depthType;

    [Header("Hi-Z Texture Precision")]
    [SerializeField] DepthTextureSizeMode textureSizeMode = DepthTextureSizeMode.ScreenPowerOfTwo;
    [SerializeField, Range(0.25f, 2.0f)] float resolutionScale = 1.0f;
    [SerializeField] int fixedTextureSize = 1024;
    [SerializeField] int minTextureSize = 512;
    [SerializeField] int maxTextureSize = 2048;
    [SerializeField] DepthTexturePrecision texturePrecision = DepthTexturePrecision.RFloat;
    [SerializeField] bool rebuildOnScreenResize = true;
    [SerializeField] bool useExternalDepthTexture;

    int depthTextureSize = 0;
    public int DepthTextureSize
    {
        get
        {
            if (useExternalDepthTexture && depthTexture != null)
            {
                depthTextureSize = depthTexture.width;
                return depthTextureSize;
            }

            depthTextureSize = CalculateDepthTextureSize();
            return depthTextureSize;
        }
    }

    public int DepthTextureMipCount
    {
        get
        {
            EnsureDepthTexture();
            return depthTexture != null ? depthTexture.mipmapCount : 0;
        }
    }

    public RenderTextureFormat DepthTextureFormat => useExternalDepthTexture && depthTexture != null
        ? depthTexture.format
        : GetConfiguredDepthTextureFormat();

    public string DepthTextureDescription => DepthTextureSize + " " + DepthTextureFormat;

    public bool useHiz;

    Material depthTextureMaterial;

    int depthTextureShaderID;

    CommandBuffer depthMipmapGenerateCMD;
    Camera ownerCamera;
    bool useBuiltinCommandBuffer;
    Camera lastHiZCamera;
    Vector3 lastHiZCameraPosition;
    Matrix4x4 lastHiZMatrixVP = Matrix4x4.identity;
    Vector4 lastHiZMapSize;
    int lastHiZUpdateFrame = -1;


    void Start()
    {
        ownerCamera = GetComponent<Camera>();
        if (depthTextureShader == null)
            depthTextureShader = Shader.Find("ComputeShader/DepthTextureMipmapCalculator");
        depthTextureMaterial = new Material(depthTextureShader);
        ownerCamera.depthTextureMode |= DepthTextureMode.Depth;
        depthTextureShaderID = Shader.PropertyToID("_CameraDepthTexture");
        EnsureDepthTexture();

        useBuiltinCommandBuffer = GraphicsSettings.currentRenderPipeline == null;
        if (useBuiltinCommandBuffer)
        {
            depthMipmapGenerateCMD = new CommandBuffer();
            depthMipmapGenerateCMD.name = "Generate DepthMipmapTexture";
            if(depthType == DepthType.CurFrame)
                ownerCamera.AddCommandBuffer(CameraEvent.AfterDepthTexture, depthMipmapGenerateCMD);
            else if (depthType == DepthType.LastFrame)
                ownerCamera.AddCommandBuffer(CameraEvent.AfterForwardOpaque, depthMipmapGenerateCMD);
        }
    }

    void Update()
    {
        if (rebuildOnScreenResize)
        {
            EnsureDepthTexture();
        }
    }

    public void ForceRebuildDepthTexture()
    {
        InvalidateHiZHistory();
        if (useExternalDepthTexture)
        {
            if (depthTexture != null && !depthTexture.IsCreated())
            {
                depthTexture.Create();
            }
            depthTextureSize = depthTexture != null ? depthTexture.width : 0;
            return;
        }

        ReleaseManagedDepthTexture();
        EnsureDepthTexture();
    }

    public void MarkHiZUpdated(Camera sourceCamera)
    {
        Matrix4x4 matrixVP = sourceCamera != null
            ? GL.GetGPUProjectionMatrix(sourceCamera.projectionMatrix, false) * sourceCamera.worldToCameraMatrix
            : Matrix4x4.identity;
        MarkHiZUpdated(sourceCamera, matrixVP);
    }

    public void MarkHiZUpdated(Camera sourceCamera, Matrix4x4 hizMatrixVP)
    {
        if (sourceCamera == null)
        {
            InvalidateHiZHistory();
            return;
        }

        lastHiZCamera = sourceCamera;
        lastHiZCameraPosition = sourceCamera.transform.position;
        lastHiZMatrixVP = hizMatrixVP;
        lastHiZMapSize = depthTexture != null
            ? new Vector4(depthTexture.width, depthTexture.height, depthTexture.mipmapCount, 0.0f)
            : Vector4.zero;
        lastHiZUpdateFrame = Time.frameCount;
    }

    public bool TryGetCurrentHiZ(Camera sourceCamera, out RenderTexture hizMap, out Vector4 hizMapSize, out Matrix4x4 hizMatrixVP, out Vector3 hizCameraPositionWS)
    {
        if (sourceCamera == null || depthTexture == null || lastHiZUpdateFrame < 0 || lastHiZCamera != sourceCamera)
        {
            hizMap = null;
            hizMapSize = Vector4.zero;
            hizMatrixVP = Matrix4x4.identity;
            hizCameraPositionWS = sourceCamera != null ? sourceCamera.transform.position : Vector3.zero;
            return false;
        }

        hizMap = depthTexture;
        hizMapSize = lastHiZMapSize;
        hizMatrixVP = lastHiZMatrixVP;
        hizCameraPositionWS = lastHiZCameraPosition;
        return true;
    }

    void EnsureDepthTexture()
    {
        if (useExternalDepthTexture)
        {
            bool wasCreated = depthTexture != null && depthTexture.IsCreated();
            if (depthTexture != null && !depthTexture.IsCreated())
            {
                depthTexture.Create();
            }
            depthTextureSize = depthTexture != null ? depthTexture.width : 0;
            if (!wasCreated && depthTexture != null)
            {
                InvalidateHiZHistory();
            }
            return;
        }

        int targetSize = CalculateDepthTextureSize();
        RenderTextureFormat targetFormat = GetConfiguredDepthTextureFormat();
        if (depthTexture != null &&
            depthTexture.IsCreated() &&
            depthTexture.width == targetSize &&
            depthTexture.height == targetSize &&
            depthTexture.format == targetFormat &&
            depthTexture.useMipMap &&
            depthTexture.enableRandomWrite)
        {
            depthTextureSize = targetSize;
            return;
        }

        ReleaseManagedDepthTexture();
        depthTexture = new RenderTexture(targetSize, targetSize, 0, targetFormat);
        depthTexture.name = "GPU Driven Hi-Z Depth";
        depthTexture.autoGenerateMips = false;
        depthTexture.useMipMap = true;
        depthTexture.enableRandomWrite = true;
        depthTexture.filterMode = FilterMode.Point;
        depthTexture.wrapMode = TextureWrapMode.Clamp;
        depthTexture.Create();
        depthTextureSize = targetSize;
        InvalidateHiZHistory();
    }

    int CalculateDepthTextureSize()
    {
        int baseSize = textureSizeMode == DepthTextureSizeMode.FixedPowerOfTwo
            ? fixedTextureSize
            : Mathf.CeilToInt(Mathf.Max(Screen.width, Screen.height) * resolutionScale);
        int minSize = Mathf.Max(8, Mathf.NextPowerOfTwo(Mathf.Max(1, minTextureSize)));
        int maxSize = Mathf.Max(minSize, Mathf.NextPowerOfTwo(Mathf.Max(1, maxTextureSize)));
        int size = Mathf.NextPowerOfTwo(Mathf.Max(8, baseSize));
        return Mathf.Clamp(size, minSize, maxSize);
    }

    RenderTextureFormat GetConfiguredDepthTextureFormat()
    {
        return texturePrecision == DepthTexturePrecision.RHalf
            ? RenderTextureFormat.RHalf
            : RenderTextureFormat.RFloat;
    }

    void ReleaseManagedDepthTexture()
    {
        if (depthTexture == null || useExternalDepthTexture)
        {
            return;
        }

        depthTexture.Release();
        if (Application.isPlaying)
        {
            Destroy(depthTexture);
        }
        else
        {
            DestroyImmediate(depthTexture);
        }
        depthTexture = null;
        InvalidateHiZHistory();
    }

    //生成DepthMipmap
    void OnPreRender()
    {
        if (!useBuiltinCommandBuffer)
            return;

        if (depthMipmapGenerateCMD == null)
            return;

        depthMipmapGenerateCMD.Clear();

        if (!useHiz)
            return;

        EnsureDepthTexture();

        if (depthTextureMaterial == null)
            return;

        int w = depthTexture.width;
        RenderTexture currentRenderTexture = null;//当前mipmapLevel对应的mipmap
        RenderTexture preRenderTexture = null;//上一层的mipmap，即mipmapLevel-1对应的mipmap

        for (int mipmapLevel = 0; mipmapLevel < depthTexture.mipmapCount; mipmapLevel++)
        {
            currentRenderTexture = RenderTexture.GetTemporary(w, w, 0, DepthTextureFormat);
            currentRenderTexture.filterMode = FilterMode.Point;
            if (preRenderTexture == null)
            {
                //Mipmap[0]copy原始的深度图
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

        w = Mathf.Max(1, w / 2);
        }
        RenderTexture.ReleaseTemporary(preRenderTexture);
        MarkHiZUpdated(ownerCamera);
    }
   

    void OnDestroy()
    {
        if (useBuiltinCommandBuffer && ownerCamera != null && depthMipmapGenerateCMD != null)
        {
            if (depthType == DepthType.CurFrame)
                ownerCamera.RemoveCommandBuffer(CameraEvent.AfterDepthTexture, depthMipmapGenerateCMD);
            else if (depthType == DepthType.LastFrame)
                ownerCamera.RemoveCommandBuffer(CameraEvent.AfterForwardOpaque, depthMipmapGenerateCMD);
        }

        depthMipmapGenerateCMD?.Release();
        depthMipmapGenerateCMD = null;

        if (depthTextureMaterial != null)
        {
            Destroy(depthTextureMaterial);
            depthTextureMaterial = null;
        }

        ReleaseManagedDepthTexture();
    }

    void OnValidate()
    {
        fixedTextureSize = Mathf.Max(8, Mathf.NextPowerOfTwo(Mathf.Max(1, fixedTextureSize)));
        minTextureSize = Mathf.Max(8, Mathf.NextPowerOfTwo(Mathf.Max(1, minTextureSize)));
        maxTextureSize = Mathf.Max(minTextureSize, Mathf.NextPowerOfTwo(Mathf.Max(1, maxTextureSize)));
        depthTextureSize = 0;
        InvalidateHiZHistory();
    }

    void InvalidateHiZHistory()
    {
        lastHiZCamera = null;
        lastHiZUpdateFrame = -1;
    }

    public enum DepthType
    {
        CurFrame,
        LastFrame
    }

    public enum DepthTextureSizeMode
    {
        ScreenPowerOfTwo,
        FixedPowerOfTwo
    }

    public enum DepthTexturePrecision
    {
        RHalf,
        RFloat
    }
}
