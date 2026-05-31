Shader "MeteoriteSPH3D/VertexColorUnlit"
{
    Properties
    {
        _MainTex ("Dummy", 2D) = "white" {}
        _EdgeShade ("Edge Shade", Range(0.0, 0.35)) = 0.035
    }

    // URP version. The previous surface shader turns magenta in URP.
    // This pass keeps voxel vertex colors and supports real main-light shadows.
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" "RenderPipeline"="UniversalPipeline" }
        LOD 200
        Cull Back

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _EdgeShade;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float4 color      : COLOR;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS  : SV_POSITION;
                float3 positionWS  : TEXCOORD0;
                half3 normalWS     : TEXCOORD1;
                float2 uv          : TEXCOORD2;
                half4 color        : COLOR;
                float4 shadowCoord : TEXCOORD3;
                half fogFactor     : TEXCOORD4;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);

                VertexPositionInputs pos = GetVertexPositionInputs(IN.positionOS.xyz);
                VertexNormalInputs normal = GetVertexNormalInputs(IN.normalOS);

                OUT.positionCS = pos.positionCS;
                OUT.positionWS = pos.positionWS;
                OUT.normalWS = NormalizeNormalPerVertex(normal.normalWS);
                OUT.uv = IN.uv;
                OUT.color = IN.color;
                OUT.shadowCoord = GetShadowCoord(pos);
                OUT.fogFactor = ComputeFogFactor(pos.positionCS.z);
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(IN);

                half3 n = normalize(IN.normalWS);
                Light mainLight = GetMainLight(IN.shadowCoord);
                half ndl = saturate(dot(n, mainLight.direction));

                float2 d = min(IN.uv, 1.0 - IN.uv);
                float edgeDist = min(d.x, d.y);
                half edgeMask = 1.0h - smoothstep(0.0h, 0.018h, edgeDist);

                half3 baseColor = IN.color.rgb * (1.0h - edgeMask * (half)_EdgeShade);

                // Keep a little ambient so the crater is readable, but let cast shadows be visible.
                half3 ambient = max(SampleSH(n), half3(0.025h, 0.025h, 0.025h)) * 0.33h;
                half shadow = mainLight.shadowAttenuation;
                half3 direct = mainLight.color * (0.10h + 0.90h * ndl) * shadow;

                half3 finalColor = baseColor * (ambient + direct);
                finalColor = MixFog(finalColor, IN.fogFactor);
                return half4(finalColor, IN.color.a);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode"="ShadowCaster" }
            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull Back

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex ShadowPassVertex
            #pragma fragment ShadowPassFragment

            // Do not include URP Shadows.hlsl here. Some URP package versions call
            // LerpWhiteTo from Shadows.hlsl without pulling the helper in for this
            // minimal pass, which makes the whole voxel material magenta.
            // A plain depth-only shadow caster is enough for directional voxel shadows.
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings ShadowPassVertex(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);

                float4 positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                #if UNITY_REVERSED_Z
                    positionCS.z = min(positionCS.z, positionCS.w * UNITY_NEAR_CLIP_VALUE);
                #else
                    positionCS.z = max(positionCS.z, positionCS.w * UNITY_NEAR_CLIP_VALUE);
                #endif

                OUT.positionCS = positionCS;
                return OUT;
            }

            half4 ShadowPassFragment(Varyings IN) : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode"="DepthOnly" }
            ZWrite On
            ColorMask 0
            Cull Back

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex DepthOnlyVertex
            #pragma fragment DepthOnlyFragment

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings DepthOnlyVertex(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);
                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                return OUT;
            }

            half4 DepthOnlyFragment(Varyings IN) : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }
    }

    // Built-in Render Pipeline fallback.
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        LOD 200
        Cull Back

        CGPROGRAM
        #pragma target 3.0
        #pragma surface surf Lambert vertex:vert addshadow fullforwardshadows

        sampler2D _MainTex;
        float _EdgeShade;

        struct Input
        {
            float4 color : COLOR;
            float2 uv_MainTex;
        };

        void vert(inout appdata_full v, out Input o)
        {
            UNITY_INITIALIZE_OUTPUT(Input, o);
            o.color = v.color;
            o.uv_MainTex = v.texcoord.xy;
        }

        void surf(Input IN, inout SurfaceOutput o)
        {
            float2 d = min(IN.uv_MainTex, 1.0 - IN.uv_MainTex);
            float edgeDist = min(d.x, d.y);
            float edgeMask = 1.0 - smoothstep(0.0, 0.018, edgeDist);

            o.Albedo = IN.color.rgb * (1.0 - edgeMask * _EdgeShade);
            o.Alpha = IN.color.a;
        }
        ENDCG
    }

    Fallback "Diffuse"
}
