#ifndef JUNKRACE_TOON_TERRAIN_INPUT
#define JUNKRACE_TOON_TERRAIN_INPUT

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
// _BaseMap, SampleAlbedoAlpha, Alpha — нужны стандартным проходам URP (тени, глубина).
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/SurfaceInput.hlsl"

CBUFFER_START(UnityPerMaterial)
    float4 _BaseMap_ST;
    half4 _BaseColor;
    half _Cutoff;

    half4 _GrassColor;
    half4 _DryGrassColor;
    half4 _RockColor;
    half4 _SnowColor;
    half4 _ShadowColor;

    half _RockSlope;
    half _RockBlend;
    half _SnowHeight;
    half _SnowBlend;
    half _PatchScale;
    half _PatchStrength;
    half _BrushScale;
    half _BrushStrength;
    half _Facets;

    half _RampOffset;
    half _RampSoftness;
    half _AmbientStrength;
CBUFFER_END

#endif
