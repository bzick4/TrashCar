#ifndef JUNKRACE_TOON_INPUT
#define JUNKRACE_TOON_INPUT

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
// _BaseMap, SampleAlbedoAlpha, Alpha — нужны стандартным проходам URP (тени, глубина).
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/SurfaceInput.hlsl"

CBUFFER_START(UnityPerMaterial)
    float4 _BaseMap_ST;
    half4 _BaseColor;
    half4 _ShadowColor;
    half4 _RimColor;
    half _Cutoff;
    half _RampOffset;
    half _RampSoftness;
    half _AmbientStrength;
    half _RimStrength;
    half _RimPower;
    half _SpecStrength;
    half _SpecSize;
    half _NoiseScale;
    half _NoiseStrength;
CBUFFER_END

#endif
