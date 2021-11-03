#ifndef GPUTERRAIN_FORWARD_BASE_INCLUDED
#define GPUTERRAIN_FORWARD_BASE_INCLUDED

struct VertexPositionInputs
{
    float3 positionWS; // World space position
    float3 positionVS; // View space position
    float4 positionCS; // Homogeneous clip space position
    float4 positionNDC;// Homogeneous normalized device coordinates
};

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
    float3 viewDir : TEXCOORD4;
    half3 vertexSH : TEXCOORD5; // SH
    half4 fogFactorAndVertexLight : TEXCOORD6; // x: fogFactor, yzw: vertex light
    float3 positionWS : TEXCOORD7;
#if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
    float4 shadowCoord              : TEXCOORD8;
#endif
    float4 clipPos : SV_POSITION;
};

struct NodeInfoData
{
    float4 rect;
    int mipmap;
    int neighbor;
};

struct InputData
{
    float3  positionWS;
    half3   normalWS;
    half3   viewDirectionWS;
    float4  shadowCoord;
    half    fogCoord;
    half3   vertexLighting;
    half3   bakedGI;
    float2  normalizedScreenSpaceUV;
    half4   shadowMask;
};

float3 Unity_SafeNormalize(float3 inVec)
{
    float dp3 = max(0.001f, dot(inVec, inVec));
    return inVec * rsqrt(dp3);
}

StructuredBuffer<NodeInfoData> _AllInstancesTransformBuffer;
StructuredBuffer<uint> _VisibleInstanceIDBuffer;

sampler2D _TerrainHeightmapTexture;
sampler2D _TerrainNormalmapTexture;
float4 terrainParam;

void InitializeInputData(Varyings input, half3 normalTS, out InputData inputData)
{
    inputData = (InputData)0;
    inputData.positionWS = input.positionWS;
    half3 viewDirWS = input.viewDir;

#if SHADER_HINT_NICE_QUALITY
    viewDirWS = SafeNormalize(viewDirWS);
#endif

    half3 normalWS = TransformObjectToWorldNormal(normalize(tex2D(_TerrainNormalmapTexture, input.positionWS.xz / terrainParam.x).rgb * 2 - 1));
    half3 tangentWS = cross(GetObjectToWorldMatrix()._13_23_33, normalWS);
    inputData.normalWS = TransformTangentToWorld(normalTS, half3x3(-tangentWS, cross(normalWS, tangentWS), normalWS));
    half3 SH = SampleSH(inputData.normalWS.xyz);

    inputData.normalWS = NormalizeNormalPerPixel(inputData.normalWS);
    inputData.viewDirectionWS = viewDirWS;

    inputData.shadowCoord = TransformWorldToShadowCoord(inputData.positionWS);

    inputData.fogCoord = input.fogFactorAndVertexLight.x;
    inputData.vertexLighting = input.fogFactorAndVertexLight.yzw;
    inputData.bakedGI = SAMPLE_GI(input.uvMainAndLM.zw, SH, inputData.normalWS);
}

Varyings TerrainVertex(Attributes input, uint instanceID : SV_InstanceID)
{
    Varyings output = (Varyings)0;
    NodeInfoData infoData = _AllInstancesTransformBuffer[_VisibleInstanceIDBuffer[instanceID]];
    float4 rect = infoData.rect;
    int neighbor = infoData.neighbor;
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

    float2 positionWS = rect.zw * 0.25 * (input.positionOS.xz + diff) + rect.xy; //we pre-transform to posWS in C# now
    VertexPositionInputs vertexInput;
    vertexInput.positionWS = mul(unity_ObjectToWorld, positionWS.xyy);
    float height = UnpackHeightmap(tex2D(_TerrainHeightmapTexture, vertexInput.positionWS.xz));
    float3 normalWS = tex2D(_TerrainNormalmapTexture, vertexInput.positionWS.xz).rgb * 2 - 1;
    vertexInput.positionWS.y = height * terrainParam.y * 2;
    vertexInput.positionVS = UnityObjectToViewPos(positionWS.xyy);
    vertexInput.positionCS = UnityObjectToClipPos(positionWS.xyy);

    half3 viewDirWS = _WorldSpaceCameraPos - vertexInput.positionWS;
#if !SHADER_HINT_NICE_QUALITY
    viewDirWS = Unity_SafeNormalize(viewDirWS);
#endif
    output.normal = normalWS;
    output.viewDir = viewDirWS;
    output.vertexSH = ShadeSH9(float4(output.normal, 1.0));
    output.positionWS = vertexInput.positionWS;
    output.clipPos = vertexInput.positionCS;

    return output;
}



half4 TerrainFragment(Varyings input) : SV_Target
{
    InputData inputData;
    InitializeInputData(input, half3(0, 0, 1), inputData);
    //return half4(inputData.normalWS * 0.5 + 0.5, 1);
    half3 albedo = 1;
    float metallic = 0;
    float smoothness = 0.5;
    float occlusion = 1;
    float alpha = 1;
    half4 color = UniversalFragmentPBR(inputData, albedo, metallic, /* specular */half3(0.0h, 0.0h, 0.0h), smoothness, occlusion, /* emission */half3(0, 0, 0), alpha);

    color.rgb = MixFog(color.rgb, inputData.fogCoord);

    return color;
}

#endif