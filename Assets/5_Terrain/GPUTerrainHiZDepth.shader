Shader "GPU Driven/GPUTerrain Hi-Z Depth"
{
    Properties
    {
        [HideInInspector] _Cull("__cull", Float) = 2.0
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Opaque" }

        Pass
        {
            Name "HiZDepthMax"
            Tags { "LightMode" = "GpuDrivenHiZDepthMax" }

            ZWrite Off
            ZTest Always
            Cull[_Cull]
            ColorMask R
            Blend One One
            BlendOp Max

            HLSLPROGRAM
            #pragma target 4.5
            #pragma multi_compile_instancing
            #pragma vertex TerrainVertex
            #pragma fragment HiZDepthFragment

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "GPUTerrainForwardBase.hlsl"

            float4 HiZDepthFragment(Varyings input) : SV_Target
            {
                return float4(input.clipPos.z, 0.0f, 0.0f, 1.0f);
            }
            ENDHLSL
        }

        Pass
        {
            Name "HiZDepthMin"
            Tags { "LightMode" = "GpuDrivenHiZDepthMin" }

            ZWrite Off
            ZTest Always
            Cull[_Cull]
            ColorMask R
            Blend One One
            BlendOp Min

            HLSLPROGRAM
            #pragma target 4.5
            #pragma multi_compile_instancing
            #pragma vertex TerrainVertex
            #pragma fragment HiZDepthFragment

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "GPUTerrainForwardBase.hlsl"

            float4 HiZDepthFragment(Varyings input) : SV_Target
            {
                return float4(input.clipPos.z, 0.0f, 0.0f, 1.0f);
            }
            ENDHLSL
        }
    }
}
