#ifndef GPUTERRAIN_FORWARD_BASE_INCLUDED
#define GPUTERRAIN_FORWARD_BASE_INCLUDED


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
    float4 clipPos : SV_POSITION;
    half4 color : COLOR;
};

struct NodeInfoData
{
    float4 rect;
    int mipmap;
    int neighbor;
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

    float2 horPositionWS = rect.zw * 0.25 * (input.positionOS.xz + diff) + rect.xy;
    float3 positionWS = horPositionWS.xyy;
    float height = UnpackHeightmap(tex2Dlod(_TerrainHeightmapTexture, float4(positionWS.x / terrainParam.x, positionWS.z/ terrainParam.z, 0, 0)));
    float3 normalWS = tex2Dlod(_TerrainNormalmapTexture, float4(positionWS.xz, 0.0, 0)).rgb * 2 - 1;
    positionWS.y = height * terrainParam.y * 2;
    output.normal = normalWS;
    output.clipPos = UnityObjectToClipPos(positionWS);
    output.color = lerp(half4(1.0, 0.0, 0.0, 1.0), half4(0.0, 0.0, 1.0, 1.0), (float)infoData.mipmap / 3);
    return output;
}



half4 TerrainFragment(Varyings input) : SV_Target
{
    float4 color = input.color;

    return color;
}

#endif