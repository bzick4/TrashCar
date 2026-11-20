// Небо-градиент в палитре дымки: внизу ровно цвет горизонта (с ним сливаются дальние холмы),
// вверх — к цвету зенита. Солнце — мягкий диск с ореолом. Цвета берутся из StylizedAtmosphere.
Shader "JunkRace/Sky Gradient"
{
    Properties
    {
        _HorizonSharpness("Резкость горизонта", Range(0.5, 8)) = 2.5
        _HorizonLift("Подъём горизонта", Range(-0.2, 0.3)) = 0.02
        _SunSize("Размер солнца", Range(0.005, 0.15)) = 0.04
        _SunGlow("Ореол солнца", Range(0, 2)) = 0.6
        _Banding("Ступеньки градиента (0 — плавно)", Range(0, 12)) = 0
    }

    SubShader
    {
        Tags { "Queue" = "Background" "RenderType" = "Background" "PreviewType" = "Skybox" "RenderPipeline" = "UniversalPipeline" }
        Cull Off
        ZWrite Off

        Pass
        {
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "AtmosphereCommon.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half _HorizonSharpness;
                half _HorizonLift;
                half _SunSize;
                half _SunGlow;
                half _Banding;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS : SV_POSITION; float3 dir : TEXCOORD0; UNITY_VERTEX_OUTPUT_STEREO };

            Varyings Vert(Attributes input)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                o.dir = input.positionOS.xyz;
                return o;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float3 dir = normalize(input.dir);
                float up = saturate((dir.y - _HorizonLift) / (1.0 - _HorizonLift));
                float t = 1.0 - pow(1.0 - up, _HorizonSharpness);
                if (_Banding > 0.5) t = floor(t * _Banding + 0.5) / _Banding;   // мультяшные полосы, если хочется

                half3 horizon = _JR_AtmoFar.rgb;
                half3 color = lerp(horizon, _JR_AtmoZenith.rgb, t);

                // Подсветка неба со стороны солнца + ореол и диск.
                float s = dot(dir, _JR_AtmoSunDir.xyz);
                half glow = pow(saturate(s), 48.0) * _SunGlow + pow(saturate(s), 5.0) * _SunGlow * 0.35;
                color += _JR_AtmoSun.rgb * glow * (1.0 - t * 0.5);
                float sunCos = cos(_SunSize);
                half disc = smoothstep(sunCos - 0.0006, sunCos + 0.0006, s);
                color = lerp(color, _JR_AtmoSun.rgb * 1.6 + 0.4, disc * step(-0.02, dir.y));

                // Ночью — звёзды: редкие точки по сетке направлений, у горизонта гаснут.
                if (_JR_AtmoNight.x > 0.001)
                {
                    float3 cell = floor(dir * 260.0);
                    float h = frac(sin(dot(cell, float3(12.9898, 78.233, 37.719))) * 43758.5453);
                    float star = step(0.9968, h) * smoothstep(0.04, 0.3, dir.y);
                    float tw = 0.65 + 0.35 * sin(_Time.y * (1.5 + h * 3.0) + h * 40.0);   // мерцание
                    color += star * tw * _JR_AtmoNight.x * half3(0.95, 0.97, 1.0);
                }

                // Ниже горизонта — ровная дымка (за краем мира ничего не видно).
                if (dir.y < _HorizonLift) color = lerp(horizon, _JR_AtmoNear.rgb, saturate((_HorizonLift - dir.y) * 3.0));

                if (_JR_AtmoSunDir.w < 0.5) color = half3(0.55, 0.68, 0.9);   // компонента нет — просто голубое
                return half4(color, 1.0);
            }
            ENDHLSL
        }
    }
}
