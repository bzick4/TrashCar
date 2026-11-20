#ifndef JUNKRACE_ATMOSPHERE_COMMON
#define JUNKRACE_ATMOSPHERE_COMMON

// Общие параметры воздушной дымки. Их каждый кадр выставляет компонент StylizedAtmosphere
// (Shader.SetGlobal*), так что дымка, небо и рельеф всегда в одной палитре.
float4 _JR_AtmoNear;     // rgb — цвет дымки вблизи
float4 _JR_AtmoFar;      // rgb — цвет горизонта (с ним сливается небо)
float4 _JR_AtmoZenith;   // rgb — цвет неба в зените
float4 _JR_AtmoSun;      // rgb — подсветка дымки со стороны солнца, a — сила
float4 _JR_AtmoSunDir;   // xyz — направление НА солнце, w — 1, если компонент активен
float4 _JR_AtmoDist;     // x — начало дымки, м; y — «половина дымки», м; z — максимум (0..1)
float4 _JR_AtmoHeight;   // x — базовая высота, м; y — спад с высотой (1/м); z — сила влияния высоты
float4 _JR_AtmoNight;    // x — ночь 0..1 (звёзды на небе), ставит смена дня и ночи

// Сколько дымки на точке: экспонента по расстоянию (каждые «половина» метров — ещё вдвое плотнее),
// в низинах гуще, на вершинах прозрачнее — так дальние гряды ложатся слоями силуэтов.
float JR_AtmosphereAmount(float3 positionWS, out float distanceT)
{
    float3 toPoint = positionWS - _WorldSpaceCameraPos;
    float dist = length(toPoint);
    float d = max(0.0, dist - _JR_AtmoDist.x);
    float amount = 1.0 - exp2(-d / max(_JR_AtmoDist.y, 1.0));
    distanceT = amount;

    float above = max(0.0, positionWS.y - _JR_AtmoHeight.x);
    float heightFactor = saturate(0.35 + 0.65 * exp(-above * _JR_AtmoHeight.y));
    amount *= lerp(1.0, heightFactor, _JR_AtmoHeight.z);
    return saturate(amount * _JR_AtmoDist.z);
}

// Цвет дымки в направлении взгляда: от ближнего к горизонту, тёплее в сторону солнца.
half3 JR_AtmosphereColor(float3 viewDirWS, float distanceT)
{
    half3 c = lerp(_JR_AtmoNear.rgb, _JR_AtmoFar.rgb, saturate(distanceT * 1.15));
    half sunAmount = pow(saturate(dot(viewDirWS, _JR_AtmoSunDir.xyz)), 6.0) * _JR_AtmoSun.a;
    return lerp(c, _JR_AtmoSun.rgb, saturate(sunAmount));
}

half3 JR_ApplyAtmosphere(half3 color, float3 positionWS)
{
    if (_JR_AtmoSunDir.w < 0.5) return color;   // компонента в сцене нет — ничего не делаем
    float t;
    float amount = JR_AtmosphereAmount(positionWS, t);
    float3 viewDir = normalize(positionWS - _WorldSpaceCameraPos);
    return lerp(color, JR_AtmosphereColor(viewDir, t), amount);
}

#endif
