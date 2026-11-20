using UnityEngine;
using UnityEngine.Rendering;

namespace JunkRace
{
    /// <summary>
    /// Воздух сцены в духе Over the Hill: одна палитра на дымку, небо, окружающий свет и холмы-задник.
    /// Каждый кадр выставляет глобальные параметры шейдеров (_JR_Atmo*), которые читают:
    ///   • Hidden/JunkRace/Atmosphere — полноэкранная дымка по глубине (фича Full Screen Pass в рендерере);
    ///   • JunkRace/Sky Gradient — небо;
    ///   • JunkRace/Toon Terrain — холмы.
    /// Пресет меняется в инспекторе, цвета можно крутить вручную (пресет станет «Свой»).
    /// Работает и в редакторе без Play — видно сразу.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public class StylizedAtmosphere : MonoBehaviour
    {
        public enum Preset { ЗолотойЧас, Полдень, Пасмурно, Свой, Мультяшный }

        public static StylizedAtmosphere Instance { get; private set; }

        [Tooltip("Готовая палитра. Поменяй — цвета ниже перезапишутся.")]
        [SerializeField] Preset preset = Preset.Мультяшный;

        [Header("Цвета")]
        [SerializeField] Color skyZenith = new Color(0.42f, 0.56f, 0.80f);
        [Tooltip("Цвет горизонта: с ним сливаются дальние холмы и низ неба.")]
        [SerializeField] Color horizon = new Color(0.98f, 0.80f, 0.60f);
        [Tooltip("Цвет дымки вблизи (первые сотни метров).")]
        [SerializeField] Color nearHaze = new Color(0.85f, 0.78f, 0.70f);
        [Tooltip("Подсветка дымки и неба в сторону солнца.")]
        [SerializeField] Color sunGlow = new Color(1f, 0.74f, 0.45f);
        [SerializeField, Range(0f, 1f)] float sunGlowStrength = 0.6f;

        [Header("Дымка по расстоянию")]
        [Tooltip("До этой дистанции воздух чистый, м.")]
        [SerializeField] float startDistance = 30f;
        [Tooltip("Каждые столько метров дымка становится вдвое плотнее. Меньше — туманнее.")]
        [SerializeField] float halfDistance = 340f;
        [Tooltip("Максимум дымки на горизонте. 1 — дальние горы полностью тают в небе.")]
        [SerializeField, Range(0f, 1f)] float maxOpacity = 0.93f;

        [Header("Дымка по высоте (низины гуще, вершины чище)")]
        [SerializeField] float baseHeight = 0f;
        [Tooltip("Насколько быстро дымка редеет с высотой, 1/м.")]
        [SerializeField] float heightFalloff = 0.012f;
        [SerializeField, Range(0f, 1f)] float heightStrength = 0.6f;

        [Header("Солнце и окружение")]
        [Tooltip("Ставить цвет и силу солнца из палитры (поворот солнца не трогаем — его выбирает трасса).")]
        [SerializeField] bool driveSun = true;
        [SerializeField] Color sunColor = new Color(1f, 0.86f, 0.68f);
        [SerializeField] float sunIntensity = 1.5f;
        [Tooltip("Окружающий свет (небо / горизонт / земля) из палитры.")]
        [SerializeField] bool driveAmbient = true;

        [Header("Дальность")]
        [Tooltip("Дальность камеры, м. Должна покрывать холмы-задник.")]
        [SerializeField] float cameraFar = 2600f;
        [Tooltip("Деревья у дороги рисуются до этой дистанции (дальше их всё равно съедает дымка).")]
        [SerializeField] float treeCullDistance = 320f;
        [Tooltip("Мелочь у дороги (кусты, камешки) — до этой дистанции.")]
        [SerializeField] float smallCullDistance = 110f;
        [Tooltip("Выключить обычный туман Unity, пока работает дымка (иначе туман будет дважды).")]
        [SerializeField] bool disableUnityFog = true;

        public float TreeCullDistance => treeCullDistance;
        public float SmallCullDistance => smallCullDistance;

        static readonly int NearId = Shader.PropertyToID("_JR_AtmoNear");
        static readonly int FarId = Shader.PropertyToID("_JR_AtmoFar");
        static readonly int ZenithId = Shader.PropertyToID("_JR_AtmoZenith");
        static readonly int SunId = Shader.PropertyToID("_JR_AtmoSun");
        static readonly int SunDirId = Shader.PropertyToID("_JR_AtmoSunDir");
        static readonly int DistId = Shader.PropertyToID("_JR_AtmoDist");
        static readonly int HeightId = Shader.PropertyToID("_JR_AtmoHeight");
        static readonly int NightId = Shader.PropertyToID("_JR_AtmoNight");

        // Какой пресет уже записан в цвета. Сохраняется со сценой, иначе после перекомпиляции
        // пресет применился бы заново и стёр ручные правки.
        [SerializeField, HideInInspector] int appliedPreset = -1;
        bool savedFog;
        bool fogSaved;

        // ================= Жизненный цикл =================

        void OnEnable()
        {
            Instance = this;
            if (preset != Preset.Свой && appliedPreset != (int)preset) ApplyPreset(preset);
            ApplyLighting();
            PushGlobals();
        }

        void OnDisable()
        {
            if (Instance == this) Instance = null;
            Shader.SetGlobalVector(SunDirId, Vector4.zero);   // w = 0 — шейдеры перестают красить дымку
            if (fogSaved) { RenderSettings.fog = savedFog; fogSaved = false; }
        }

        void Start()
        {
            if (Application.isPlaying) ApplyCamera();
        }

        void OnValidate()
        {
            if (preset != Preset.Свой && appliedPreset != (int)preset) ApplyPreset(preset);
            if (!isActiveAndEnabled) return;
            ApplyLighting();
            PushGlobals();
        }

        // Глобальные параметры дымки — только когда что-то поменялось (солнце повернули, пресет сменили),
        // а не каждый кадр. Направление солнца сверяем с прошлым.
        Vector3 pushedSun = new Vector3(float.NaN, 0f, 0f);
        Light cachedSun;

        void Update()
        {
            if (cachedSun == null) cachedSun = FindSun();
            Vector3 toSun = cachedSun != null ? -cachedSun.transform.forward : Vector3.up;
            if (toSun != pushedSun) PushGlobals();
        }

        // ================= Время суток =================

        /// <summary>Палитра неба и дымки (как у пресета).</summary>
        public struct Palette
        {
            public Color Zenith, Horizon, Near, Glow;
            public float GlowStrength;

            public static Palette Lerp(Palette a, Palette b, float t) => new Palette
            {
                Zenith = Color.Lerp(a.Zenith, b.Zenith, t),
                Horizon = Color.Lerp(a.Horizon, b.Horizon, t),
                Near = Color.Lerp(a.Near, b.Near, t),
                Glow = Color.Lerp(a.Glow, b.Glow, t),
                GlowStrength = Mathf.Lerp(a.GlowStrength, b.GlowStrength, t),
            };
        }

        public Palette BasePalette => new Palette { Zenith = skyZenith, Horizon = horizon, Near = nearHaze, Glow = sunGlow, GlowStrength = sunGlowStrength };

        Palette? timeOfDay;
        float night;

        /// <summary>
        /// Смена дня и ночи: палитра неба и дымки на этот момент, night 0..1 — звёзды.
        /// Окружающий свет — из той же палитры. Зовётся каждый кадр.
        /// </summary>
        public void SetTimeOfDay(Palette p, float nightAmount)
        {
            timeOfDay = p;
            night = Mathf.Clamp01(nightAmount);
            if (driveAmbient)
            {
                RenderSettings.ambientMode = AmbientMode.Trilight;
                RenderSettings.ambientSkyColor = Color.Lerp(p.Zenith, Color.white, 0.15f * (1f - night * 0.7f));
                RenderSettings.ambientEquatorColor = Color.Lerp(p.Horizon, p.Near, 0.5f);
                RenderSettings.ambientGroundColor = p.Near * 0.5f;
            }
            PushGlobals();
        }

        static float Lum(Color c) => c.r * 0.3f + c.g * 0.59f + c.b * 0.11f;

        // ================= Погода =================

        Color weatherColor = Color.white;
        float weatherBlend, weatherDistance = 1f;

        /// <summary>
        /// Дымка от погоды: blend — насколько перекрасить в color (0 — ясно), distanceScale — во сколько раз
        /// сократить «полудистанцию» дымки (меньше — гуще). Зовётся при плавной смене погоды.
        /// </summary>
        public void SetWeather(Color color, float blend, float distanceScale)
        {
            weatherColor = color;
            weatherBlend = Mathf.Clamp01(blend);
            weatherDistance = Mathf.Clamp(distanceScale, 0.05f, 1f);
            PushGlobals();
        }

        // ================= Применение =================

        /// <summary>Глобальные параметры для шейдеров — каждый кадр (солнце могло повернуться).</summary>
        void PushGlobals()
        {
            Vector3 toSun = Vector3.up;
            var sun = FindSun();
            if (sun != null) toSun = -sun.transform.forward;
            pushedSun = toSun;

            // SetGlobalColor сам переводит цвет в линейное пространство, если проект в Linear.
            // Время суток (DayNightCycle) подменяет палитру; погода (Weather) сверху подмешивает свой цвет
            // и сгущает дымку. Настройки компонента не трогаются.
            var pal = timeOfDay ?? BasePalette;
            float w = weatherBlend;
            // Цвет непогоды — в яркости текущего неба: ночью метель тёмная, а не светящаяся.
            float bright = Mathf.Clamp(Lum(pal.Horizon) / 0.9f, 0.1f, 1f);
            var wc = weatherColor * bright; wc.a = 1f;
            Shader.SetGlobalColor(NearId, Color.Lerp(pal.Near, wc, w));
            Shader.SetGlobalColor(FarId, Color.Lerp(pal.Horizon, wc, w));
            Shader.SetGlobalColor(ZenithId, Color.Lerp(pal.Zenith, wc, w * 0.7f));
            Shader.SetGlobalColor(SunId, new Color(pal.Glow.r, pal.Glow.g, pal.Glow.b, pal.GlowStrength * (1f - w)));
            Shader.SetGlobalVector(NightId, new Vector4(night * (1f - w), 0f, 0f, 0f));
            Shader.SetGlobalVector(SunDirId, new Vector4(toSun.x, toSun.y, toSun.z, 1f));
            Shader.SetGlobalVector(DistId, new Vector4(startDistance * Mathf.Lerp(1f, 0.2f, w), Mathf.Max(1f, halfDistance * weatherDistance),
                                                       Mathf.Lerp(maxOpacity, 1f, w), 0f));
            Shader.SetGlobalVector(HeightId, new Vector4(baseHeight, Mathf.Max(0f, heightFalloff), heightStrength, 0f));
        }

        void ApplyLighting()
        {
            if (disableUnityFog)
            {
                if (!fogSaved) { savedFog = RenderSettings.fog; fogSaved = true; }
                RenderSettings.fog = false;
            }
            // Обычный туман всё равно настраиваем под палитру — на случай, если фичу дымки выключат.
            RenderSettings.fogColor = horizon;
            RenderSettings.fogMode = FogMode.Exponential;
            RenderSettings.fogDensity = 0.693f / Mathf.Max(1f, halfDistance);

            if (driveAmbient)
            {
                RenderSettings.ambientMode = AmbientMode.Trilight;
                RenderSettings.ambientSkyColor = Color.Lerp(skyZenith, Color.white, 0.15f);
                RenderSettings.ambientEquatorColor = Color.Lerp(horizon, nearHaze, 0.5f);
                RenderSettings.ambientGroundColor = nearHaze * 0.5f;
            }

            if (driveSun)
            {
                var sun = FindSun();
                if (sun != null)
                {
                    sun.color = sunColor;
                    sun.intensity = sunIntensity;
                }
            }
        }

        void ApplyCamera()
        {
            var cam = Camera.main;
            if (cam == null) return;
            cam.farClipPlane = Mathf.Max(cam.farClipPlane, cameraFar);
            cam.clearFlags = CameraClearFlags.Skybox;
        }

        static Light FindSun()
        {
            if (RenderSettings.sun != null) return RenderSettings.sun;
            foreach (var l in FindObjectsByType<Light>(FindObjectsSortMode.None))
                if (l.type == LightType.Directional) return l;
            return null;
        }

        // ================= Пресеты =================

        [ContextMenu("Применить пресет заново")]
        void ReapplyPreset()
        {
            if (preset == Preset.Свой) return;
            ApplyPreset(preset);
            ApplyLighting();
            PushGlobals();
        }

        void ApplyPreset(Preset p)
        {
            appliedPreset = (int)p;
            switch (p)
            {
                case Preset.ЗолотойЧас:   // тёплый вечер, как арт меню
                    skyZenith = new Color(0.42f, 0.56f, 0.80f);
                    horizon = new Color(0.98f, 0.80f, 0.60f);
                    nearHaze = new Color(0.85f, 0.78f, 0.70f);
                    sunGlow = new Color(1f, 0.74f, 0.45f);
                    sunGlowStrength = 0.6f;
                    sunColor = new Color(1f, 0.86f, 0.68f);
                    sunIntensity = 1.5f;
                    halfDistance = 340f;
                    maxOpacity = 0.93f;
                    break;
                case Preset.Полдень:      // ясный день, голубая даль
                    skyZenith = new Color(0.33f, 0.56f, 0.90f);
                    horizon = new Color(0.80f, 0.88f, 0.95f);
                    nearHaze = new Color(0.72f, 0.80f, 0.86f);
                    sunGlow = new Color(1f, 0.96f, 0.86f);
                    sunGlowStrength = 0.3f;
                    sunColor = new Color(1f, 0.96f, 0.9f);
                    sunIntensity = 1.6f;
                    halfDistance = 420f;
                    maxOpacity = 0.92f;
                    break;
                case Preset.Мультяшный:   // сочный ясный день: голубое небо, прозрачная даль, чуть тёплое солнце
                    skyZenith = new Color(0.30f, 0.55f, 0.92f);
                    horizon = new Color(0.84f, 0.90f, 0.97f);
                    nearHaze = new Color(0.76f, 0.84f, 0.92f);
                    sunGlow = new Color(1f, 0.88f, 0.66f);
                    sunGlowStrength = 0.35f;
                    sunColor = new Color(1f, 0.95f, 0.86f);
                    sunIntensity = 1.6f;
                    halfDistance = 560f;
                    maxOpacity = 0.9f;
                    startDistance = 45f;
                    heightStrength = 0.5f;
                    break;
                case Preset.Пасмурно:     // серо-зелёный, плотная дымка
                    skyZenith = new Color(0.60f, 0.65f, 0.70f);
                    horizon = new Color(0.80f, 0.82f, 0.82f);
                    nearHaze = new Color(0.70f, 0.73f, 0.72f);
                    sunGlow = new Color(0.95f, 0.95f, 0.92f);
                    sunGlowStrength = 0.1f;
                    sunColor = new Color(0.88f, 0.9f, 0.95f);
                    sunIntensity = 0.9f;
                    halfDistance = 220f;
                    maxOpacity = 0.96f;
                    break;
            }
        }
    }
}
