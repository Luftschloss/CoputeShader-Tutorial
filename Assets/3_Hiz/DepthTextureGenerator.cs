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
    [SerializeField] DepthTextureSizeMode textureSizeMode = DepthTextureSizeMode.ScreenHalfPowerOfTwo;
    [SerializeField] int fixedTextureSize = 1024;
    [SerializeField] DepthTexturePrecision texturePrecision = DepthTexturePrecision.RHalf;
    [SerializeField] bool rebuildOnScreenResize = true;
    [SerializeField] bool useExternalDepthTexture;

    Vector2Int depthTextureSize = Vector2Int.zero;
    // 对外暴露给剔除使用的有效 HZB mip 数；按当前规则不会额外生成最后的 1x1 层。
    int activeDepthTextureMipCount = 1;
    public int DepthTextureSize
    {
        get
        {
            Vector2Int dimensions = DepthTextureDimensions;
            return Mathf.Max(dimensions.x, dimensions.y);
        }
    }

    public int DepthTextureWidth => DepthTextureDimensions.x;
    public int DepthTextureHeight => DepthTextureDimensions.y;

    public Vector2Int DepthTextureDimensions
    {
        get
        {
            if (useExternalDepthTexture && depthTexture != null)
            {
                depthTextureSize = new Vector2Int(depthTexture.width, depthTexture.height);
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
            return depthTexture != null ? activeDepthTextureMipCount : 0;
        }
    }

    public RenderTextureFormat DepthTextureFormat => useExternalDepthTexture && depthTexture != null
        ? depthTexture.format
        : GetConfiguredDepthTextureFormat();

    public string DepthTextureDescription => DepthTextureWidth + "x" + DepthTextureHeight + " " + DepthTextureFormat;

    public bool useHiz;

    Material depthTextureMaterial;

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
            depthTextureSize = depthTexture != null
                ? new Vector2Int(depthTexture.width, depthTexture.height)
                : Vector2Int.zero;
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
        // x/y 是真实的非正方形 HZB 尺寸，z 是剔除侧允许采样的有效 mip 数。
        lastHiZMapSize = depthTexture != null
            ? new Vector4(depthTexture.width, depthTexture.height, activeDepthTextureMipCount, 0.0f)
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
            depthTextureSize = depthTexture != null
                ? new Vector2Int(depthTexture.width, depthTexture.height)
                : Vector2Int.zero;
            activeDepthTextureMipCount = depthTexture != null
                ? Mathf.Min(depthTexture.mipmapCount, CalculateHiZMipCount(depthTextureSize))
                : 0;
            if (!wasCreated && depthTexture != null)
            {
                InvalidateHiZHistory();
            }
            return;
        }

        Vector2Int targetSize = CalculateDepthTextureSize();
        int targetMipCount = CalculateHiZMipCount(targetSize);
        RenderTextureFormat targetFormat = GetConfiguredDepthTextureFormat();
        if (depthTexture != null &&
            depthTexture.IsCreated() &&
            depthTexture.width == targetSize.x &&
            depthTexture.height == targetSize.y &&
            depthTexture.mipmapCount == targetMipCount &&
            depthTexture.format == targetFormat &&
            depthTexture.useMipMap &&
            depthTexture.enableRandomWrite)
        {
            depthTextureSize = targetSize;
            activeDepthTextureMipCount = targetMipCount;
            return;
        }

        ReleaseManagedDepthTexture();
        RenderTextureDescriptor descriptor = new RenderTextureDescriptor(targetSize.x, targetSize.y, targetFormat, 0)
        {
            autoGenerateMips = false,
            useMipMap = true,
            mipCount = targetMipCount,
            enableRandomWrite = true
        };
        depthTexture = new RenderTexture(descriptor);
        depthTexture.name = "GPU Driven Hi-Z Depth";
        depthTexture.filterMode = FilterMode.Point;
        depthTexture.wrapMode = TextureWrapMode.Clamp;
        depthTexture.Create();
        depthTextureSize = targetSize;
        activeDepthTextureMipCount = targetMipCount;
        InvalidateHiZHistory();
    }

    Vector2Int CalculateDepthTextureSize()
    {
        if (textureSizeMode == DepthTextureSizeMode.FixedPowerOfTwo)
        {
            int fixedSize = Mathf.Max(1, Mathf.NextPowerOfTwo(Mathf.Max(1, fixedTextureSize)));
            return new Vector2Int(fixedSize, fixedSize);
        }

        int sourceWidth = ownerCamera != null && ownerCamera.pixelWidth > 0 ? ownerCamera.pixelWidth : Screen.width;
        int sourceHeight = ownerCamera != null && ownerCamera.pixelHeight > 0 ? ownerCamera.pixelHeight : Screen.height;
        // HZBSize = RoundUpToPowerOfTwo(ViewRect.Size) >> 1，保留相机宽高比。
        return new Vector2Int(
            CalculateHZBDimension(sourceWidth),
            CalculateHZBDimension(sourceHeight));
    }

    static int CalculateHZBDimension(int viewSize)
    {
        // 极小视口也至少保留有效 mip0。
        return Mathf.Max(1, Mathf.NextPowerOfTwo(Mathf.Max(1, viewSize)) >> 1);
    }

    static int CalculateHiZMipCount(Vector2Int size)
    {
        int maxSize = Mathf.Max(size.x, size.y, 1);
        // NumMips = max(floor(log2(max(HZBSize.X, HZBSize.Y))), 1)。
        return Mathf.Max(Mathf.FloorToInt(Mathf.Log(maxSize, 2.0f)), 1);
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
        int h = depthTexture.height;
        RenderTexture currentRenderTexture = null;//当前mipmapLevel对应的mipmap
        RenderTexture preRenderTexture = null;//上一层的mipmap，即mipmapLevel-1对应的mipmap

        int mipCount = Mathf.Min(activeDepthTextureMipCount, depthTexture.mipmapCount);
        for (int mipmapLevel = 0; mipmapLevel < mipCount; mipmapLevel++)
        {
            currentRenderTexture = RenderTexture.GetTemporary(w, h, 0, DepthTextureFormat);
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
            h = Mathf.Max(1, h / 2);
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
        depthTextureSize = Vector2Int.zero;
        activeDepthTextureMipCount = 1;
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
        ScreenHalfPowerOfTwo,
        FixedPowerOfTwo
    }

    public enum DepthTexturePrecision
    {
        RHalf,
        RFloat
    }
}
