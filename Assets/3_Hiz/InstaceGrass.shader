Shader "ComputeShader/Instace/Grass"
{
    Properties
    {
        _Color ("Color", Color) = (1,1,1,1)
        _MainTex ("Albedo (RGB)", 2D) = "white" {}
        _Alpha("Alpha", Range(0, 1)) = 0.5
    }
    SubShader
    {
         Pass {

        Tags {"LightMode"="ForwardBase"}
        LOD 200
        //Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        CGPROGRAM
        // Physically based Standard lighting model, and enable shadows on all light types
        #pragma vertex vert
        #pragma fragment frag
        #pragma target 4.5

        #include "UnityCG.cginc"

        sampler2D _MainTex;

        struct v2f
        {
            float4 pos : SV_POSITION;
            float2 uv_MainTex : TEXCOORD0;
        };

        half _Alpha;
        half4 _Color;

        #if SHADER_TARGET >= 45
        StructuredBuffer<float4x4> rtsBuffer;
        #endif

        UNITY_INSTANCING_BUFFER_START(Props)
            // put more per-instance properties here
        UNITY_INSTANCING_BUFFER_END(Props)

        v2f vert (appdata_full v, uint instanceID : SV_InstanceID)
        {
            #if SHADER_TARGET >= 45
            unity_ObjectToWorld = rtsBuffer[instanceID];
            unity_WorldToObject = unity_ObjectToWorld;
            unity_WorldToObject._14_24_34 *= -1;
            unity_WorldToObject._11_22_33 = 1.0f / unity_WorldToObject._11_22_33;
            #endif

            float3 worldPosition = mul(unity_ObjectToWorld, float4(v.vertex.xyz, 1.0)).xyz;
            v2f o;
            o.pos = mul(UNITY_MATRIX_VP, float4(worldPosition, 1.0f));
            o.uv_MainTex = v.texcoord;
            return o;
        }

        fixed4 frag (v2f i) : SV_Target
        {
            fixed4 albedo = tex2D(_MainTex, i.uv_MainTex);
            fixed4 output = fixed4(albedo.rgb * _Color.rgb, albedo.w);
            return output;
        }
        ENDCG
        }
    }
}
