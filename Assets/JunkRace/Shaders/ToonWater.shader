// Мультяшная вода: прозрачная, у берега и на мелком — бирюзовая, в глубине — синяя (по глубине сцены
// под поверхностью). Белая пена полосой там, где вода касается берега, камней и машин. Рябь — бегущие
// светлые прожилки, на солнце — блики. Освещение — от солнца/луны и окружающего света, сверху — дымка,
// так что вода живёт вместе со сменой дня и ночи. Нужна текстура глубины камеры (в URP она включена).
Shader "JunkRace/Toon Water"
{
    Properties
    {
        _ShallowColor("Мелко", Color) = (0.36, 0.86, 0.86, 0.55)
        _DeepColor("Глубоко", Color) = (0.07, 0.33, 0.68, 0.93)
        _DepthMax("Глубина до «глубоко», м", Float) = 1.8
        _FoamColor("Пена", Color) = (1, 1, 1, 0.95)
        _FoamDistance("Ширина пены, м", Float) = 0.45
        _RippleScale("Размер ряби", Float) = 0.45
        _RippleSpeed("Скорость ряби", Float) = 0.55
        _RippleStrength("Яркость ряби", Range(0, 1)) = 0.35
        _Sparkle("Блики", Range(0, 2)) = 1
    }

    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Back

        Pass
        {
            Name "ToonWater"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "AtmosphereCommon.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _ShallowColor;
                half4 _DeepColor;
                float _DepthMax;
                half4 _FoamColor;
                float _FoamDistance;
                float _RippleScale;
                float _RippleSpeed;
                half _RippleStrength;
                half _Sparkle;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float4 screenPos : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(Attributes input)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                o.screenPos = ComputeScreenPos(o.positionCS);
                return o;
            }

            float Hash(float2 p) { return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453); }

            // Плавный шум по сетке (значения в узлах, гладкая интерполяция).
            float Noise(float2 p)
            {
                float2 i = floor(p), f = frac(p);
                float2 u = f * f * (3.0 - 2.0 * f);
                float a = Hash(i), b = Hash(i + float2(1, 0)), c = Hash(i + float2(0, 1)), d = Hash(i + float2(1, 1));
                return lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y);
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float2 suv = input.screenPos.xy / input.screenPos.w;
                float sceneEye = LinearEyeDepth(SampleSceneDepth(suv), _ZBufferParams);
                float surfEye = input.screenPos.w;
                float depth = max(0.0, sceneEye - surfEye);   // сколько воды под этой точкой (вдоль взгляда)

                float t = _Time.y * _RippleSpeed;
                float2 xz = input.positionWS.xz * _RippleScale;
                float n1 = Noise(xz + float2(t, t * 0.6));
                float n2 = Noise(xz * 1.7 - float2(t * 0.8, -t * 0.5));
                float ripple = n1 + n2;   // 0..2

                // Цвет по глубине: мелко — бирюза, глубоко — синь; две ступени — мультяшно, без «грязной» середины.
                float dt = saturate(depth / _DepthMax);
                dt = smoothstep(0.0, 1.0, dt);
                half4 water = lerp(_ShallowColor, _DeepColor, dt);

                // Прожилки ряби: узкие светлые полосы на гребнях шума.
                half streak = smoothstep(1.18, 1.24, ripple) * (1.0 - smoothstep(1.34, 1.42, ripple));
                water.rgb = lerp(water.rgb, half3(0.85, 0.97, 1.0), streak * _RippleStrength);

                // Пена у кромки: чем мельче, тем больше; край рваный от шума.
                float edge = 1.0 - saturate(depth / _FoamDistance);
                half foam = step(0.55 + (n1 - 0.5) * 0.6, edge);

                // Свет: солнце (или луна) сверху + окружающий; вода сама немного светится цветом неба.
                Light mainLight = GetMainLight();
                half3 ambient = SampleSH(half3(0, 1, 0));
                half3 lit = mainLight.color * 0.75 + ambient * 0.8;
                half3 color = water.rgb * lit;

                // Блики: нормаль чуть качается по ряби — отражение солнца искрит пятнами.
                float3 n = normalize(float3((n1 - 0.5) * 0.35, 1.0, (n2 - 0.5) * 0.35));
                float3 viewDir = normalize(_WorldSpaceCameraPos - input.positionWS);
                float3 h = normalize(mainLight.direction + viewDir);
                half spec = step(0.985, saturate(dot(n, h))) * _Sparkle;
                color += mainLight.color * spec;

                half alpha = water.a;
                color = lerp(color, _FoamColor.rgb * lit * 1.1, foam);
                alpha = lerp(alpha, _FoamColor.a, foam);
                alpha = saturate(alpha + spec * 0.5);

                color = JR_ApplyAtmosphere(color, input.positionWS);
                return half4(color, alpha);
            }
            ENDHLSL
        }
    }
}
