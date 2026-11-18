// Полноэкранная воздушная дымка (как в Over the Hill / Firewatch): по буферу глубины восстанавливаем
// мировую позицию каждого пикселя и подмешиваем цвет дымки. Работает поверх любых шейдеров сцены
// (Toon Shaders Pro, URP Lit, свои), поэтому всё растворяется в горизонте одинаково.
// Подключается фичей Full Screen Pass в рендерере URP (ставит меню JunkRace → Атмосфера).
Shader "Hidden/JunkRace/Atmosphere"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off
        ZTest Always
        Cull Off
        Blend Off

        Pass
        {
            Name "Atmosphere"

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "AtmosphereCommon.hlsl"

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                half4 color = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, input.texcoord);

                // Глубину и мировую позицию берём по экранным координатам пикселя (как в примере Unity
                // «Reconstruct world position») — так не бывает переворота по вертикали на DirectX.
                float2 uv = input.positionCS.xy / _ScaledScreenParams.xy;
                float depth = SampleSceneDepth(uv);
                #if UNITY_REVERSED_Z
                    if (depth <= 1e-6) return color;          // небо: его рисует свой шейдер в тех же цветах
                #else
                    if (depth >= 1.0 - 1e-6) return color;
                    depth = lerp(UNITY_NEAR_CLIP_VALUE, 1.0, depth);
                #endif

                float3 positionWS = ComputeWorldSpacePosition(uv, depth, UNITY_MATRIX_I_VP);
                color.rgb = JR_ApplyAtmosphere(color.rgb, positionWS);
                return color;
            }
            ENDHLSL
        }
    }
}
