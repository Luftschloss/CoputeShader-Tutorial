Shader "GPU Driven/Foliage Indirect"
{
    Properties
    {
        _BaseMap("Base Map", 2D) = "white" {}
        _BaseColor("Base Color", Color) = (1, 1, 1, 1)
        _Cutoff("Alpha Cutoff", Range(0, 1)) = 0.35
        _BumpMap("Normal Map", 2D) = "bump" {}
        _NormalMap("Normal Map Alias", 2D) = "bump" {}
        _BumpScale("Normal Scale", Float) = 1.0
        _MaskMap("Mask Map", 2D) = "white" {}
        _MetallicGlossMap("Metallic Gloss Map", 2D) = "white" {}
        _OcclusionMap("Occlusion Map", 2D) = "white" {}
        _EmissionMap("Emission Map", 2D) = "black" {}
        _GpuDrivenFoliageBillboard("Billboard", Float) = 0
        _GpuDrivenFoliageDebugColorMode("Debug Color Mode", Float) = 0
        _GpuDrivenFoliageDebugColor("Debug Color", Color) = (1, 1, 1, 1)
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "TransparentCutout" "Queue" = "AlphaTest" }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }
            Cull Off
            ZWrite On

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_fog
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);
            StructuredBuffer<float4x4> _GpuDrivenFoliageMatrices;

            CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            half4 _BaseColor;
            half _Cutoff;
            float _GpuDrivenFoliageBillboard;
            float _GpuDrivenFoliageDebugColorMode;
            half4 _GpuDrivenFoliageDebugColor;
            CBUFFER_END

            struct Attributes
            {
                float3 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                uint instanceID : SV_InstanceID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                float2 uv : TEXCOORD2;
                float fogCoord : TEXCOORD3;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                float4x4 localToWorld = _GpuDrivenFoliageMatrices[input.instanceID];
                float3 positionWS;
                float3 normalWS;
                if (_GpuDrivenFoliageBillboard > 0.5f)
                {
                    float3 centerWS = mul(localToWorld, float4(0.0f, 0.0f, 0.0f, 1.0f)).xyz;
                    float width = length(mul((float3x3)localToWorld, float3(1.0f, 0.0f, 0.0f)));
                    float height = length(mul((float3x3)localToWorld, float3(0.0f, 1.0f, 0.0f)));
                    float3 cameraRightWS = normalize(float3(UNITY_MATRIX_I_V[0][0], UNITY_MATRIX_I_V[1][0], UNITY_MATRIX_I_V[2][0]));
                    float3 upWS = float3(0.0f, 1.0f, 0.0f);
                    positionWS = centerWS + cameraRightWS * input.positionOS.x * width + upWS * input.positionOS.y * height;
                    normalWS = normalize(GetCameraPositionWS() - (centerWS + upWS * height * 0.5f));
                }
                else
                {
                    positionWS = mul(localToWorld, float4(input.positionOS, 1.0f)).xyz;
                    normalWS = normalize(mul((float3x3)localToWorld, input.normalOS));
                }
                output.positionCS = TransformWorldToHClip(positionWS);
                output.positionWS = positionWS;
                output.normalWS = normalWS;
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                output.fogCoord = ComputeFogFactor(output.positionCS.z);
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                half4 baseMap = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv);
                half4 color = baseMap * _BaseColor;
                clip(color.a - _Cutoff);
                if (_GpuDrivenFoliageDebugColorMode > 0.5f)
                {
                    return half4(_GpuDrivenFoliageDebugColor.rgb, color.a);
                }

                float4 shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                Light mainLight = GetMainLight(shadowCoord);
                half3 normalWS = normalize(input.normalWS);
                half ndotl = saturate(dot(normalWS, mainLight.direction));
                half3 lit = color.rgb * mainLight.color * (0.25 + ndotl * mainLight.shadowAttenuation * 0.75);
                lit = MixFog(lit, input.fogCoord);
                return half4(lit, color.a);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            Cull Off
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);
            StructuredBuffer<float4x4> _GpuDrivenFoliageMatrices;

            CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            half4 _BaseColor;
            half _Cutoff;
            float _GpuDrivenFoliageBillboard;
            CBUFFER_END

            struct Attributes
            {
                float3 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                uint instanceID : SV_InstanceID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                float4x4 localToWorld = _GpuDrivenFoliageMatrices[input.instanceID];
                float3 positionWS;
                if (_GpuDrivenFoliageBillboard > 0.5f)
                {
                    float3 centerWS = mul(localToWorld, float4(0.0f, 0.0f, 0.0f, 1.0f)).xyz;
                    float width = length(mul((float3x3)localToWorld, float3(1.0f, 0.0f, 0.0f)));
                    float height = length(mul((float3x3)localToWorld, float3(0.0f, 1.0f, 0.0f)));
                    float3 cameraRightWS = normalize(float3(UNITY_MATRIX_I_V[0][0], UNITY_MATRIX_I_V[1][0], UNITY_MATRIX_I_V[2][0]));
                    float3 upWS = float3(0.0f, 1.0f, 0.0f);
                    positionWS = centerWS + cameraRightWS * input.positionOS.x * width + upWS * input.positionOS.y * height;
                }
                else
                {
                    positionWS = mul(localToWorld, float4(input.positionOS, 1.0f)).xyz;
                }
                output.positionCS = TransformWorldToHClip(positionWS);
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                half4 baseMap = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv);
                clip(baseMap.a * _BaseColor.a - _Cutoff);
                return 0;
            }
            ENDHLSL
        }
    }
}
