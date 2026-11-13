// Стилизованный «мультяшный» свет для JunkRace.
// Мягкая граница света и тени, цветные (холодные) тени, ободок по краю, маленький блик
// и лёгкие «мазки» шума в мировых координатах, чтобы плоские коробки не выглядели пластиком.
Shader "JunkRace/Toon"
{
    Properties
    {
        [MainColor] _BaseColor("Цвет", Color) = (1, 1, 1, 1)
        [MainTexture] _BaseMap("Текстура", 2D) = "white" {}

        [Header(Light and shadow)]
        _ShadowColor("Оттенок тени", Color) = (0.42, 0.45, 0.65, 1)
        _RampOffset("Граница света", Range(-1, 1)) = 0.0
        _RampSoftness("Мягкость границы", Range(0.01, 1)) = 0.12
        _AmbientStrength("Свет неба", Range(0, 2)) = 0.6

        [Header(Rim)]
        _RimColor("Цвет ободка", Color) = (1, 0.95, 0.85, 1)
        _RimStrength("Сила ободка", Range(0, 1)) = 0.2
        _RimPower("Ширина ободка", Range(1, 10)) = 4

        [Header(Highlight)]
        _SpecStrength("Сила блика", Range(0, 1)) = 0.15
        _SpecSize("Размер блика", Range(0, 0.2)) = 0.03

        [Header(Paint noise)]
        _NoiseScale("Масштаб пятен", Float) = 1.2
        _NoiseStrength("Сила пятен", Range(0, 0.5)) = 0.1

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
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fragment _ _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Fog.hlsl"
            #pragma multi_compile_instancing

            #include "ToonInput.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                half3  normalWS   : TEXCOORD2;
                half   fogFactor  : TEXCOORD3;
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
                VertexNormalInputs nrm = GetVertexNormalInputs(input.normalOS);
                o.positionCS = pos.positionCS;
                o.positionWS = pos.positionWS;
                o.normalWS = nrm.normalWS;
                o.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                o.fogFactor = ComputeFogFactor(pos.positionCS.z);
                return o;
            }

            // Дешёвый value-noise: даёт «неровную покраску» без текстур.
            float JR_Hash(float3 p)
            {
                p = frac(p * 0.3183099 + 0.1);
                p *= 17.0;
                return frac(p.x * p.y * p.z * (p.x + p.y + p.z));
            }

            float JR_Noise(float3 x)
            {
                float3 i = floor(x);
                float3 f = frac(x);
                f = f * f * (3.0 - 2.0 * f);
                return lerp(
                    lerp(lerp(JR_Hash(i + float3(0, 0, 0)), JR_Hash(i + float3(1, 0, 0)), f.x),
                         lerp(JR_Hash(i + float3(0, 1, 0)), JR_Hash(i + float3(1, 1, 0)), f.x), f.y),
                    lerp(lerp(JR_Hash(i + float3(0, 0, 1)), JR_Hash(i + float3(1, 0, 1)), f.x),
                         lerp(JR_Hash(i + float3(0, 1, 1)), JR_Hash(i + float3(1, 1, 1)), f.x), f.y),
                    f.z);
            }

            half Ramp(half x)
            {
                return smoothstep(_RampOffset - _RampSoftness, _RampOffset + _RampSoftness, x);
            }

            // Насколько точка освещена источником: мягкая ступенька по N·L, умноженная на тень.
            half LitAmount(Light light, half3 n)
            {
                half shadow = smoothstep(0.2, 0.8, light.shadowAttenuation);
                return Ramp(dot(n, light.direction)) * shadow;
            }

            half3 AdditionalLight(Light light, half3 albedo, half3 n)
            {
                return albedo * light.color * light.distanceAttenuation * LitAmount(light, n);
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                half4 tex = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv);
                half3 albedo = tex.rgb * _BaseColor.rgb;

                // Пятна «краски»: два слоя шума разного масштаба.
                float3 np = input.positionWS * _NoiseScale;
                half noise = JR_Noise(np) * 0.65 + JR_Noise(np * 2.7 + 13.1) * 0.35;
                albedo *= 1.0 + (noise - 0.5) * 2.0 * _NoiseStrength;

                half3 n = normalize(input.normalWS);
                half3 v = GetWorldSpaceNormalizeViewDir(input.positionWS);

                float4 shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                Light mainLight = GetMainLight(shadowCoord, input.positionWS, half4(1, 1, 1, 1));
                half lit = LitAmount(mainLight, n);

                half3 ambient = SampleSH(n) * _AmbientStrength;
                #if defined(_SCREEN_SPACE_OCCLUSION)
                    AmbientOcclusionFactor ao = GetScreenSpaceAmbientOcclusion(GetNormalizedScreenSpaceUV(input.positionCS));
                    ambient *= ao.indirectAmbientOcclusion;
                    lit *= ao.directAmbientOcclusion;
                #endif

                // Тень не чёрная, а окрашенная: солнце «подмешивает» оттенок тени.
                half3 color = albedo * (mainLight.color * lerp(_ShadowColor.rgb, half3(1, 1, 1), lit) + ambient);

                // Маленький мультяшный блик.
                half3 h = normalize(mainLight.direction + v);
                half spec = smoothstep(1.0 - _SpecSize - 0.01, 1.0 - _SpecSize + 0.01, dot(n, h)) * lit;
                color += mainLight.color * spec * _SpecStrength;

                // Ободок по силуэту — сильнее на освещённой стороне.
                half rim = pow(1.0 - saturate(dot(n, v)), _RimPower) * _RimStrength * (0.35 + 0.65 * lit);
                color += _RimColor.rgb * rim * mainLight.color;

                #if defined(_ADDITIONAL_LIGHTS)
                    InputData inputData = (InputData)0;
                    inputData.positionWS = input.positionWS;
                    inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
                    uint lightsCount = GetAdditionalLightsCount();
                    #if USE_CLUSTER_LIGHT_LOOP
                        [loop] for (uint dirIndex = 0; dirIndex < min(URP_FP_DIRECTIONAL_LIGHTS_COUNT, MAX_VISIBLE_LIGHTS); dirIndex++)
                            color += AdditionalLight(GetAdditionalLight(dirIndex, input.positionWS, half4(1, 1, 1, 1)), albedo, n);
                    #endif
                    LIGHT_LOOP_BEGIN(lightsCount)
                        color += AdditionalLight(GetAdditionalLight(lightIndex, input.positionWS, half4(1, 1, 1, 1)), albedo, n);
                    LIGHT_LOOP_END
                #endif

                color = MixFog(color, input.fogFactor);
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
            #include "ToonInput.hlsl"
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
            #include "ToonInput.hlsl"
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
            #include "ToonInput.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/Shaders/DepthNormalsPass.hlsl"
            ENDHLSL
        }
    }

    FallBack "Universal Render Pipeline/Simple Lit"
}
