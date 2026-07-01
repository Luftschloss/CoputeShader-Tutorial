Shader "GPU Driven/Hi-Z Debug View"
{
    Properties
    {
        _MainTex("Hi-Z Texture", 2D) = "black" {}
        _Mip("Mip", Float) = 0
        _Linearize("Linearize", Float) = 1
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Opaque" }
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            float _Mip;
            float _Linearize;

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = input.uv;
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float rawDepth = SAMPLE_TEXTURE2D_LOD(_MainTex, sampler_MainTex, input.uv, _Mip).r;
                float displayDepth;

                if (_Linearize > 0.5f)
                {
                    displayDepth = 1.0f - saturate(Linear01Depth(rawDepth, _ZBufferParams));
                }
                else
                {
#if defined(UNITY_REVERSED_Z)
                    displayDepth = rawDepth;
#else
                    displayDepth = 1.0f - rawDepth;
#endif
                }

                return half4(displayDepth.xxx, 1.0h);
            }
            ENDHLSL
        }
    }
}
