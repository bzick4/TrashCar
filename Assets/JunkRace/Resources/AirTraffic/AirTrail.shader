Shader "JunkRace/AirTrail"
{
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Transparent" "RenderType"="Transparent" }
        Cull Off
        ZWrite Off
        Blend SrcAlpha OneMinusSrcAlpha
        Pass
        {
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Assets/JunkRace/Shaders/AtmosphereCommon.hlsl"

            struct Vertex
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                half4 color : COLOR;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float edge : TEXCOORD1;
                half4 color : COLOR;
            };

            Varyings Vert(Vertex input)
            {
                Varyings output;
                output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionCS = TransformWorldToHClip(output.positionWS);
                output.edge = input.uv.y;
                output.color = input.color;
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                half softness = smoothstep(0, 0.4, min(input.edge, 1 - input.edge));
                half3 color = input.color.rgb * lerp(1.0, 0.46, _JR_AtmoNight.x);
                return half4(JR_ApplyAtmosphere(color, input.positionWS), input.color.a * softness);
            }
            ENDHLSL
        }
    }
}
