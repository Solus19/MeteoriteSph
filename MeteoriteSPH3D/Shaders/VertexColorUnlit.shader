Shader "MeteoriteSPH3D/VertexColorUnlit"
{
    Properties
    {
        _AmbientStrength ("Ambient Strength", Range(0.0, 1.0)) = 0.34
        _DiffuseStrength ("Diffuse Strength", Range(0.0, 2.0)) = 1.02
        _EdgeShade ("Edge Shade", Range(0.0, 0.35)) = 0.025
        _TopFaceBoost ("Top Face Boost", Range(0.0, 0.5)) = 0.10
        _SideFaceDarken ("Side Face Darken", Range(0.0, 0.5)) = 0.12
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        Pass
        {
            Cull Back
            ZWrite On
            ZTest LEqual

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            float _AmbientStrength;
            float _DiffuseStrength;
            float _EdgeShade;
            float _TopFaceBoost;
            float _SideFaceDarken;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float4 color : COLOR;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 worldNormal : TEXCOORD0;
                float4 color : COLOR;
                float2 uv : TEXCOORD1;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.worldNormal = UnityObjectToWorldNormal(v.normal);
                o.color = v.color;
                o.uv = v.uv;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float3 n = normalize(i.worldNormal);
                float3 l = normalize(_WorldSpaceLightPos0.xyz);
                float ndl = saturate(dot(n, l));
                float lighting = _AmbientStrength + ndl * _DiffuseStrength;

                float topMask = saturate(n.y);
                float sideMask = 1.0 - topMask;
                float faceFactor = 1.0 + topMask * _TopFaceBoost - sideMask * _SideFaceDarken;

                float2 d = min(i.uv, 1.0 - i.uv);
                float edgeDist = min(d.x, d.y);
                float edgeMask = 1.0 - smoothstep(0.0, 0.018, edgeDist);

                fixed3 baseColor = i.color.rgb * lighting * faceFactor;
                fixed3 finalColor = baseColor * (1.0 - edgeMask * _EdgeShade);
                return fixed4(finalColor, 1.0);
            }
            ENDCG
        }
    }
}
