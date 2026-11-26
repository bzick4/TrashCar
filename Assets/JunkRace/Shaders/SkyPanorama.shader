// Небо из четырёх нарисованных панорам (equirect 2:1): день, закат/рассвет, сумерки с луной, глубокая ночь.
// Смена дня и ночи (DayNightCycle) каждый кадр ставит веса смешивания _JR_SkyBlend (x день, y закат,
// z сумерки, w ночь) и азимут солнца _JR_SkyYaw — панорама поворачивается так, чтобы нарисованное
// солнце (и луна в ночных картинках) стояло со стороны настоящего света. Погода (_JR_AtmoNight.y)
// затягивает небо цветом горизонта дымки. Нет смены суток — показывается день.
Shader "JunkRace/Sky Panorama"
{
    Properties
    {
        _DayTex("День", 2D) = "white" {}
        _SunsetTex("Закат / рассвет", 2D) = "white" {}
        _DuskTex("Сумерки (луна)", 2D) = "black" {}
        _NightTex("Ночь", 2D) = "black" {}
        _SunU("Где на картинках солнце (U, 0..1)", Range(0, 1)) = 0.915
        _Exposure("Яркость", Range(0.3, 2)) = 1
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

            TEXTURE2D(_DayTex);    SAMPLER(sampler_DayTex);
            TEXTURE2D(_SunsetTex); SAMPLER(sampler_SunsetTex);
            TEXTURE2D(_DuskTex);   SAMPLER(sampler_DuskTex);
            TEXTURE2D(_NightTex);  SAMPLER(sampler_NightTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _DayTex_ST;
                float4 _SunsetTex_ST;
                float4 _DuskTex_ST;
                float4 _NightTex_ST;
                half _SunU;
                half _Exposure;
            CBUFFER_END

            float4 _JR_SkyBlend;   // веса: x день, y закат, z сумерки, w ночь
            float _JR_SkyYaw;      // азимут солнца, радианы (как atan2(x, z) направления на солнце)

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

            // UV панорамы: солнце картинки (U = _SunU) — в азимуте yaw. Без мипов (LOD 0) — нет шва на стыке.
            float2 PanoUV(float3 dir, float yaw)
            {
                float phi = atan2(dir.x, dir.z);
                float u = frac((phi - yaw) / (2.0 * PI) + _SunU);
                float v = asin(clamp(dir.y, -1.0, 1.0)) / PI + 0.5;
                return float2(u, v);
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float3 dir = normalize(input.dir);
                float4 w = _JR_SkyBlend;
                float sum = w.x + w.y + w.z + w.w;
                if (sum < 0.001) { w = float4(1, 0, 0, 0); sum = 1; }   // смены суток нет — день
                w /= sum;

                // Днём и на закате рисованное солнце — со стороны солнца; ночью луна — с противоположной.
                float2 uvSun = PanoUV(dir, _JR_SkyYaw);
                float2 uvMoon = PanoUV(dir, _JR_SkyYaw + PI);

                half3 c = 0;
                if (w.x > 0.001) c += w.x * SAMPLE_TEXTURE2D_LOD(_DayTex, sampler_DayTex, uvSun, 0).rgb;
                if (w.y > 0.001) c += w.y * SAMPLE_TEXTURE2D_LOD(_SunsetTex, sampler_SunsetTex, uvSun, 0).rgb;
                if (w.z > 0.001) c += w.z * SAMPLE_TEXTURE2D_LOD(_DuskTex, sampler_DuskTex, uvMoon, 0).rgb;
                if (w.w > 0.001) c += w.w * SAMPLE_TEXTURE2D_LOD(_NightTex, sampler_NightTex, uvMoon, 0).rgb;
                c *= _Exposure;

                // Непогода: небо затягивает цветом горизонта дымки.
                float weather = saturate(_JR_AtmoNight.y);
                if (_JR_AtmoSunDir.w > 0.5) c = lerp(c, _JR_AtmoFar.rgb, weather * 0.85);
                return half4(c, 1.0);
            }
            ENDHLSL
        }
    }
}
