Shader "ComputeShader/Instace//CullInstance"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Color("Color", Color) = (1,1,1,1)
    }
        SubShader
    {
        Tags { "RenderType" = "Opaque" }
        LOD 100

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5

            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
            };

#if SHADER_TARGET >= 45
            StructuredBuffer<float4x4> rtsBuffer;
#endif

            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed4 _Color;

            v2f vert (appdata v, uint instanceID : SV_InstanceID)
            {
                v2f o;
#if SHADER_TARGET >= 45
                o.vertex = mul(UNITY_MATRIX_VP, mul(rtsBuffer[instanceID], v.vertex));
#endif
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                // sample the texture
                fixed4 col = tex2D(_MainTex, i.uv);
                
                return col*_Color;
            }
            ENDCG
        }
    }
}
