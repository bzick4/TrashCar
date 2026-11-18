// Рельеф без текстур в духе Over the Hill / art of rally: цвет решают наклон и высота
// (трава на пологом, камень на крутом, снег на вершинах), крупные пятна сухой травы,
// мягкая граница света и цветная тень — как у остальных моделей. Дымку добавляет постэффект.
Shader "JunkRace/Toon Terrain"
{
    Properties
    {
        [Header(Colors)]
        _GrassColor("Трава", Color) = (0.42, 0.56, 0.27, 1)
        _DryGrassColor("Сухая трава", Color) = (0.70, 0.64, 0.36, 1)
        _RockColor("Камень", Color) = (0.55, 0.50, 0.46, 1)
        _SnowColor("Снег", Color) = (0.95, 0.95, 0.97, 1)

        [Header(Rock on slopes)]
        _RockSlope("Наклон, где начинается камень", Range(0, 1)) = 0.38
        _RockBlend("Мягкость перехода в камень", Range(0.005, 0.3)) = 0.05

        [Header(Snow on peaks)]
        _SnowHeight("Высота снега, м", Float) = 170
        _SnowBlend("Мягкость снега, м", Float) = 12

        [Header(Patches)]
        _PatchScale("Масштаб пятен сухой травы", Float) = 0.006
        _PatchStrength("Сила пятен", Range(0, 1)) = 0.65
        _BrushScale("Масштаб мелких мазков", Float) = 0.08
        _BrushStrength("Сила мазков", Range(0, 0.3)) = 0.06
        _Facets("Гранёность (0 — гладко, 1 — low-poly)", Range(0, 1)) = 0.35

        [Header(Light)]
        _ShadowColor("Оттенок тени", Color) = (0.42, 0.45, 0.65, 1)
        _RampOffset("Граница света", Range(-1, 1)) = 0.0
        _RampSoftness("Мягкость границы", Range(0.01, 1)) = 0.25
        _AmbientStrength("Свет неба", Range(0, 2)) = 0.7

        [HideInInspector] _BaseColor("Base", Color) = (1, 1, 1, 1)
        [HideInInspector] _BaseMap("Base", 2D) = "white" {}
        [HideInInspector] _Cutoff("Alpha Cutoff", Range(0, 1)) = 0.5
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex Vert
            #pragma fragment Frag

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fragment _ _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Fog.hlsl"
            #pragma multi_compile_instancing

            #include "ToonTerrainInput.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                half3  normalWS   : TEXCOORD1;
                half   fogFactor  : TEXCOORD2;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(Attributes input)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                VertexPositionInputs pos = GetVertexPositionInputs(input.positionOS.xyz);
                o.positionCS = pos.positionCS;
                o.positionWS = pos.positionWS;
                o.normalWS = GetVertexNormalInputs(input.normalOS).normalWS;
                o.fogFactor = ComputeFogFactor(pos.positionCS.z);
                return o;
            }

            float JR_Hash2(float2 p)
            {
                p = frac(p * float2(0.3183099, 0.3678794) + 0.1);
                p *= 17.0;
                return frac(p.x * p.y * (p.x + p.y));
            }

            float JR_Noise2(float2 x)
            {
                float2 i = floor(x);
                float2 f = frac(x);
                f = f * f * (3.0 - 2.0 * f);
                return lerp(lerp(JR_Hash2(i), JR_Hash2(i + float2(1, 0)), f.x),
                            lerp(JR_Hash2(i + float2(0, 1)), JR_Hash2(i + float2(1, 1)), f.x), f.y);
            }

            half Ramp(half x)
            {
                return smoothstep(_RampOffset - _RampSoftness, _RampOffset + _RampSoftness, x);
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                // Нормаль: смесь гладкой и граневой (грани дают «бумажный» low-poly рельеф).
                half3 smoothN = normalize(input.normalWS);
                half3 faceN = normalize(cross(ddy(input.positionWS), ddx(input.positionWS)));
                if (dot(faceN, smoothN) < 0) faceN = -faceN;
                half3 n = normalize(lerp(smoothN, faceN, _Facets));

                float2 xz = input.positionWS.xz;
                half patch = JR_Noise2(xz * _PatchScale) * 0.7 + JR_Noise2(xz * _PatchScale * 3.1 + 7.3) * 0.3;
                half brush = JR_Noise2(xz * _BrushScale) - 0.5;

                // Трава: пятнами сухая.
                half dry = smoothstep(0.45, 0.75, patch) * _PatchStrength;
                half3 albedo = lerp(_GrassColor.rgb, _DryGrassColor.rgb, dry);

                // Камень на крутых склонах (по гладкой нормали — граница не рябит).
                half slope = 1.0 - smoothN.y;
                half rock = smoothstep(_RockSlope - _RockBlend, _RockSlope + _RockBlend, slope + (patch - 0.5) * 0.08);
                albedo = lerp(albedo, _RockColor.rgb, rock);

                // Снег на вершинах: держится на пологом, сползает с крутого.
                half snowLine = _SnowHeight + (patch - 0.5) * _SnowBlend * 3.0;
                half snow = smoothstep(snowLine - _SnowBlend, snowLine + _SnowBlend, input.positionWS.y) * (1.0 - rock * 0.6);
                albedo = lerp(albedo, _SnowColor.rgb, saturate(snow));

                albedo *= 1.0 + brush * 2.0 * _BrushStrength;

                // Свет: мягкая ступенька и цветная тень, как у JunkRace/Toon.
                float4 shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                Light mainLight = GetMainLight(shadowCoord, input.positionWS, half4(1, 1, 1, 1));
                half lit = Ramp(dot(n, mainLight.direction)) * smoothstep(0.2, 0.8, mainLight.shadowAttenuation);

                half3 ambient = SampleSH(n) * _AmbientStrength;
                #if defined(_SCREEN_SPACE_OCCLUSION)
                    AmbientOcclusionFactor ao = GetScreenSpaceAmbientOcclusion(GetNormalizedScreenSpaceUV(input.positionCS));
                    ambient *= ao.indirectAmbientOcclusion;
                    lit *= ao.directAmbientOcclusion;
                #endif

                half3 color = albedo * (mainLight.color * lerp(_ShadowColor.rgb, half3(1, 1, 1), lit) + ambient);
                color = MixFog(color, input.fogFactor);   // если дымка-постэффект выключена, работает обычный туман
                return half4(color, 1.0);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On
            ZTest LEqual
            ColorMask 0

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex ShadowPassVertex
            #pragma fragment ShadowPassFragment
            #pragma multi_compile_instancing
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #include "ToonTerrainInput.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/Shaders/ShadowCasterPass.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On
            ColorMask R

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex DepthOnlyVertex
            #pragma fragment DepthOnlyFragment
            #pragma multi_compile_instancing
            #include "ToonTerrainInput.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/Shaders/DepthOnlyPass.hlsl"
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }
            ZWrite On

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex DepthNormalsVertex
            #pragma fragment DepthNormalsFragment
            #pragma multi_compile_instancing
            #include "ToonTerrainInput.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/Shaders/DepthNormalsPass.hlsl"
            ENDHLSL
        }
    }

    FallBack "Universal Render Pipeline/Simple Lit"
}
