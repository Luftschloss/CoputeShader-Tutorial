#ifndef GPUTERRAIN_FORWARD_BASE_INCLUDED
#define GPUTERRAIN_FORWARD_BASE_INCLUDED

#if defined(GPUTERRAIN_SHADOW_CASTER_PASS)
#define GPUTERRAIN_APPLY_SHADOW_BIAS 1
#else
#define GPUTERRAIN_APPLY_SHADOW_BIAS 0
#endif


struct Attributes
{
    float4 positionOS   : POSITION;
    float2 texcoord     : TEXCOORD0;
    half4 color         : COLOR;
};

struct Varyings
{
    float4 uvMainAndLM : TEXCOORD0; // xy: control, zw: lightmap
#ifndef TERRAIN_SPLAT_BASEPASS
    float4 uvSplat01 : TEXCOORD1; // xy: splat0, zw: splat1
    float4 uvSplat23 : TEXCOORD2; // xy: splat2, zw: splat3
#endif
    float3 normal : TEXCOORD3;
    float3 positionWS : TEXCOORD4;
    float2 terrainUV : TEXCOORD5;
    float terrainIndex : TEXCOORD6;
    float4 clipPos : SV_POSITION;
    half4 color : COLOR;
};

struct NodeInfoData
{
    float4 rect;
    float2 heightMinMax;
    int mipmap;
    int neighbor;
    int terrainIndex;
    int padding;
};

float3 Unity_SafeNormalize(float3 inVec)
{
    float dp3 = max(0.001f, dot(inVec, inVec));
    return inVec * rsqrt(dp3);
}

StructuredBuffer<NodeInfoData> _AllInstancesTransformBuffer;
StructuredBuffer<uint> _VisibleInstanceIDBuffer;

TEXTURE2D_ARRAY(_TerrainHeightmapTextureArray);
SAMPLER(sampler_TerrainHeightmapTextureArray);
TEXTURE2D_ARRAY(_TerrainNormalmapTextureArray);
SAMPLER(sampler_TerrainNormalmapTextureArray);
TEXTURE2D_ARRAY(_TerrainControlTextureArray);
SAMPLER(sampler_TerrainControlTextureArray);
TEXTURE2D_ARRAY(_TerrainLayerDiffuseArray);
SAMPLER(sampler_TerrainLayerDiffuseArray);
TEXTURE2D_ARRAY(_TerrainLayerNormalArray);
SAMPLER(sampler_TerrainLayerNormalArray);
TEXTURE2D_ARRAY(_TerrainLayerMaskArray);
SAMPLER(sampler_TerrainLayerMaskArray);

float4 _TerrainParams[64];
float4 _TerrainOriginSizes[64];
float4 _TerrainLayerIndices[64];
float4 _TerrainLayerTileSizeOffsets[64];
float4 _TerrainLayerPbrParams[64];
float4 _TerrainLodDebugColors[16];
float4 _BaseColor;
int _TerrainCount;
int _TerrainLayerCount;
int _TerrainLodDebugColorCount;
int _TerrainMaterialDebugMode;
float _TerrainDebugColorMode;
float _TerrainHasLayerData;

#if GPUTERRAIN_APPLY_SHADOW_BIAS
float3 _LightDirection;
float3 _LightPosition;
#endif

float DecodeTerrainHeight(float4 packedHeight)
{
    return packedHeight.r;
}

uint GetSafeTerrainIndex(int terrainIndex)
{
    return (uint)clamp(terrainIndex, 0, max(_TerrainCount - 1, 0));
}

float2 GetTerrainUV(float2 positionWSXZ, uint terrainIndex)
{
    float4 terrainOriginSize = _TerrainOriginSizes[terrainIndex];
    return saturate((positionWSXZ - terrainOriginSize.xy) / max(terrainOriginSize.zw, 1e-5f));
}

half4 GetTerrainLodDebugColor(int mipmap)
{
    int colorCount = clamp(_TerrainLodDebugColorCount, 1, 16);
    int colorIndex = clamp(mipmap, 0, colorCount - 1);
    return half4(_TerrainLodDebugColors[colorIndex]);
}

int GetSafeTerrainLayerIndex(float rawIndex)
{
    return clamp((int)round(rawIndex), 0, max(_TerrainLayerCount - 1, 0));
}

float2 GetTerrainLayerUV(float2 positionWSXZ, int layerIndex)
{
    float4 tileSizeOffset = _TerrainLayerTileSizeOffsets[layerIndex];
    float2 tileSize = max(tileSizeOffset.xy, 1e-5f);
    return (positionWSXZ - tileSizeOffset.zw) / tileSize;
}

half3 SampleTerrainLayerDiffuse(float2 positionWSXZ, int layerIndex)
{
    float2 layerUV = GetTerrainLayerUV(positionWSXZ, layerIndex);
    return SAMPLE_TEXTURE2D_ARRAY(_TerrainLayerDiffuseArray, sampler_TerrainLayerDiffuseArray, layerUV, layerIndex).rgb;
}

half4 SampleTerrainControlWeights(float2 terrainUV, int terrainIndex)
{
    half4 weights = SAMPLE_TEXTURE2D_ARRAY(_TerrainControlTextureArray, sampler_TerrainControlTextureArray, terrainUV, terrainIndex);
    half weightSum = max(dot(weights, half4(1.0h, 1.0h, 1.0h, 1.0h)), 1e-4h);
    return weights / weightSum;
}

half3 SampleTerrainLayerBlend(float2 terrainUV, float2 positionWSXZ, float terrainIndexValue)
{
    if (_TerrainHasLayerData < 0.5f || _TerrainLayerCount <= 0)
    {
        return _BaseColor.rgb;
    }

    int terrainIndex = clamp((int)round(terrainIndexValue), 0, max(_TerrainCount - 1, 0));
    half4 weights = SampleTerrainControlWeights(terrainUV, terrainIndex);
    float4 layerIndices = _TerrainLayerIndices[terrainIndex];
    int layer0 = GetSafeTerrainLayerIndex(layerIndices.x);
    int layer1 = GetSafeTerrainLayerIndex(layerIndices.y);
    int layer2 = GetSafeTerrainLayerIndex(layerIndices.z);
    int layer3 = GetSafeTerrainLayerIndex(layerIndices.w);

    half3 color =
        SampleTerrainLayerDiffuse(positionWSXZ, layer0) * weights.r +
        SampleTerrainLayerDiffuse(positionWSXZ, layer1) * weights.g +
        SampleTerrainLayerDiffuse(positionWSXZ, layer2) * weights.b +
        SampleTerrainLayerDiffuse(positionWSXZ, layer3) * weights.a;

    return color * _BaseColor.rgb;
}

half3 GetTerrainMaterialDebugColor(float2 terrainUV, float2 positionWSXZ, float terrainIndexValue)
{
    int terrainIndex = clamp((int)round(terrainIndexValue), 0, max(_TerrainCount - 1, 0));
    half3 debugColor = half3(1.0h, 0.0h, 0.0h);

    if (_TerrainMaterialDebugMode == 8)
    {
        debugColor = _TerrainHasLayerData > 0.5f ? half3(0.0h, 1.0h, 0.0h) : half3(1.0h, 0.0h, 0.0h);
    }
    else if (_TerrainHasLayerData >= 0.5f && _TerrainLayerCount > 0)
    {
        if (_TerrainMaterialDebugMode == 3)
        {
            debugColor = SampleTerrainControlWeights(terrainUV, terrainIndex).rgb;
        }
        else
        {
            float4 layerIndices = _TerrainLayerIndices[terrainIndex];
            int layerIndex = GetSafeTerrainLayerIndex(layerIndices.x);
            if (_TerrainMaterialDebugMode == 5)
            {
                layerIndex = GetSafeTerrainLayerIndex(layerIndices.y);
            }
            else if (_TerrainMaterialDebugMode == 6)
            {
                layerIndex = GetSafeTerrainLayerIndex(layerIndices.z);
            }
            else if (_TerrainMaterialDebugMode == 7)
            {
                layerIndex = GetSafeTerrainLayerIndex(layerIndices.w);
            }

            debugColor = SampleTerrainLayerDiffuse(positionWSXZ, layerIndex);
        }
    }

    return debugColor;
}

#if GPUTERRAIN_APPLY_SHADOW_BIAS
float3 TerrainGetShadowLightDirection(float3 positionWS)
{
#if _CASTING_PUNCTUAL_LIGHT_SHADOW
    return normalize(_LightPosition - positionWS);
#else
    return _LightDirection;
#endif
}
#endif

Varyings TerrainVertexCommon(Attributes input, uint instanceID)
{
    Varyings output = (Varyings)0;
    NodeInfoData infoData = _AllInstancesTransformBuffer[_VisibleInstanceIDBuffer[instanceID]];
    float4 rect = infoData.rect;
    int neighbor = infoData.neighbor;
    uint terrainIndex = GetSafeTerrainIndex(infoData.terrainIndex);
    float4 terrainDataParam = _TerrainParams[terrainIndex];
    float2 diff = 0;
    if (neighbor & 1)
    {
        diff.x = -input.color.r;
    }
    if (neighbor & 2)
    {
        diff.x = -input.color.g;
    }
    if (neighbor & 4)
    {
        diff.y = -input.color.b;
    }
    if (neighbor & 8)
    {
        diff.y = -input.color.a;
    }

    float2 horPositionWS = rect.zw * 0.25 * (input.positionOS.xz + diff) + rect.xy;
    float3 positionWS = horPositionWS.xyy;
    float2 terrainUV = GetTerrainUV(positionWS.xz, terrainIndex);
    float terrainSlice = (float)terrainIndex;
    float height = DecodeTerrainHeight(SAMPLE_TEXTURE2D_ARRAY_LOD(_TerrainHeightmapTextureArray, sampler_TerrainHeightmapTextureArray, terrainUV, terrainSlice, 0.0f));
    float3 normalWS = SAMPLE_TEXTURE2D_ARRAY_LOD(_TerrainNormalmapTextureArray, sampler_TerrainNormalmapTextureArray, terrainUV, terrainSlice, 0.0f).rgb * 2 - 1;
    positionWS.y = terrainDataParam.w + height * terrainDataParam.y;

#if GPUTERRAIN_APPLY_SHADOW_BIAS
    positionWS = ApplyShadowBias(positionWS, normalWS, TerrainGetShadowLightDirection(positionWS));
#endif

    output.normal = normalWS;
    output.positionWS = positionWS;
    output.terrainUV = terrainUV;
    output.terrainIndex = (float)terrainIndex;
    output.clipPos = TransformWorldToHClip(positionWS);
#if GPUTERRAIN_APPLY_SHADOW_BIAS
#if defined(UNITY_REVERSED_Z)
    output.clipPos.z = min(output.clipPos.z, UNITY_NEAR_CLIP_VALUE);
#else
    output.clipPos.z = max(output.clipPos.z, UNITY_NEAR_CLIP_VALUE);
#endif
#endif
    output.color = GetTerrainLodDebugColor(infoData.mipmap);
    return output;
}

Varyings TerrainVertex(Attributes input, uint instanceID : SV_InstanceID)
{
    return TerrainVertexCommon(input, instanceID);
}

#if defined(GPUTERRAIN_SHADOW_CASTER_PASS)
Varyings TerrainShadowVertex(Attributes input, uint instanceID : SV_InstanceID)
{
    return TerrainVertexCommon(input, instanceID);
}
#endif

#if defined(GPUTERRAIN_FORWARD_LIT_PASS)
half4 TerrainFragment(Varyings input) : SV_Target
{
    float4 shadowCoord = TransformWorldToShadowCoord(input.positionWS);
    Light mainLight = GetMainLight(shadowCoord);
    float3 normalWS = Unity_SafeNormalize(input.normal);
    float ndotl = saturate(dot(normalWS, mainLight.direction));
#if defined(_RECEIVE_SHADOWS_OFF)
    float shadowAttenuation = 1.0f;
#else
    float shadowAttenuation = mainLight.shadowAttenuation;
#endif
    float3 albedo = SampleTerrainLayerBlend(input.terrainUV, input.positionWS.xz, input.terrainIndex);
    if (_TerrainMaterialDebugMode >= 2)
    {
        if (_TerrainMaterialDebugMode == 2)
        {
            return half4(albedo, _BaseColor.a);
        }

        return half4(GetTerrainMaterialDebugColor(input.terrainUV, input.positionWS.xz, input.terrainIndex), _BaseColor.a);
    }

    float3 color = albedo * (0.25 + ndotl * shadowAttenuation * 0.75) * mainLight.color;

    if (_TerrainDebugColorMode > 0.5f)
    {
        color *= input.color.xyz;
    }

    return half4(color, _BaseColor.a);
}
#endif

#endif
