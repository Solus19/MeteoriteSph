Shader "MeteoriteSPH3D/ParticleGPUInstanced"
{
    Properties
    {
        _Radius ("Radius", Float) = 0.13
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
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct Particle
            {
                float3 position;
                float age;
                float3 velocity;
                float temperature;
                float density;
                float pressure;
                float nearDensity;
                float recentGroundContact;
                float mass;
                float active;
                float pad0;
                float pad1;
            };

            StructuredBuffer<Particle> _Particles;
            float _Radius;
            float _CellSize;
            int _LayerViewEnabled;
            int _LayerViewAxis;
            int _SingleLayerMode;
            int _VisibleLayerMin;
            int _VisibleLayerMax;
            int _SingleVisibleLayer;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                uint instanceID : SV_InstanceID;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 normal : TEXCOORD0;
                float temp : TEXCOORD1;
                float3 worldPos : TEXCOORD2;
                float active : TEXCOORD3;
            };

            v2f vert(appdata v)
            {
                Particle p = _Particles[v.instanceID];
                float active = step(0.5, p.active);
                float3 world = p.position + v.vertex.xyz * (_Radius * 2.0 * active);
                world = lerp(float3(-100000.0, -100000.0, -100000.0), world, active);
                v2f o;
                o.pos = mul(UNITY_MATRIX_VP, float4(world, 1.0));
                o.normal = normalize(v.normal);
                o.temp = p.temperature;
                o.worldPos = p.position;
                o.active = p.active;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                if (i.active < 0.5) discard;
                if (_LayerViewEnabled != 0)
                {
                    float coord = i.worldPos.y;
                    if (_LayerViewAxis == 0) coord = i.worldPos.x;
                    else if (_LayerViewAxis == 2) coord = i.worldPos.z;
                    int layer = (int)floor(coord / max(0.0001, _CellSize));
                    if (_SingleLayerMode != 0)
                    {
                        if (layer != _SingleVisibleLayer) discard;
                    }
                    else
                    {
                        if (layer < _VisibleLayerMin || layer > _VisibleLayerMax) discard;
                    }
                }
                float t = saturate(i.temp / 650.0);
                fixed3 cold = fixed3(0.33, 0.25, 0.17);
                fixed3 red = fixed3(0.95, 0.20, 0.05);
                fixed3 orange = fixed3(1.0, 0.55, 0.04);
                fixed3 yellow = fixed3(1.0, 0.95, 0.35);
                fixed3 c = lerp(cold, red, saturate(t * 2.2));
                c = lerp(c, orange, saturate((t - 0.35) * 2.0));
                c = lerp(c, yellow, saturate((t - 0.72) * 3.0));
                float light = 0.72 + 0.28 * saturate(i.normal.y * 0.5 + 0.5);
                return fixed4(c * light, 1.0);
            }
            ENDCG
        }
    }
}
