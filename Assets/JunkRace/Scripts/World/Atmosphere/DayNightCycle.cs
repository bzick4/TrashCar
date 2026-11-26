using UnityEngine;

namespace JunkRace
{
    /// <summary>
    /// Смена дня и ночи: сутки за 24 минуты (игровой час = минута). Солнце встаёт на востоке в 6:00,
    /// садится на западе в 18:00; ночью тот же свет становится луной. Небо, дымка и окружающий свет
    /// плавно идут через палитры дня, заката и ночи, ночью на небе звёзды.
    ///
    /// У всех игроков одинаково: час старта и сторона света — по сиду трассы, ход времени — по серверному
    /// времени сети (в одиночной игре — по своим часам, на паузе стоит). Погода сверху приглушает и
    /// охлаждает солнце (Weather.SunDim / SunCold) и сгущает дымку.
    ///
    /// Для проверки: в инспекторе «Тест: час» (−1 — обычный ход; 0–24 — стоять на этом часе), только у себя.
    /// </summary>
    public class DayNightCycle : MonoBehaviour
    {
        public static DayNightCycle Instance { get; private set; }

        public const float SecondsPerHour = 60f;   // 24 минуты = сутки

        /// <summary>Текущее время суток, часы 0..24.</summary>
        public float Hour { get; private set; }

        [Header("Проверка (только у себя)")]
        [Tooltip("−1 — время идёт как обычно. 0–24 — стоять на этом часе.")]
        [SerializeField, Range(-1f, 24f)] float testHour = -1f;
        [Tooltip("Сейчас, часы (только показ).")]
        [SerializeField] float nowHour;

        float startHour, azimuth, localStart;
        Light sun;
        float sunShadows = 1f;

        // ---- Палитры ----
        static readonly StylizedAtmosphere.Palette Day = new StylizedAtmosphere.Palette
        {
            // Цвета дымки взяты с панорам неба (Art/Sky): дымка у горизонта сливается с картинкой.
            Zenith = new Color(0.58f, 0.73f, 0.94f), Horizon = new Color(0.71f, 0.78f, 0.95f), Near = new Color(0.70f, 0.76f, 0.91f),
            Glow = new Color(1f, 0.88f, 0.66f), GlowStrength = 0.35f,
        };
        static readonly StylizedAtmosphere.Palette Golden = new StylizedAtmosphere.Palette
        {
            Zenith = new Color(0.99f, 0.91f, 0.83f), Horizon = new Color(0.98f, 0.87f, 0.77f), Near = new Color(0.95f, 0.79f, 0.71f),
            Glow = new Color(1f, 0.64f, 0.36f), GlowStrength = 0.75f,
        };
        static readonly StylizedAtmosphere.Palette Night = new StylizedAtmosphere.Palette
        {
            Zenith = new Color(0.11f, 0.16f, 0.33f), Horizon = new Color(0.17f, 0.22f, 0.40f), Near = new Color(0.14f, 0.17f, 0.33f),
            Glow = new Color(0.78f, 0.85f, 1f), GlowStrength = 0.3f,   // ореол луны
        };
        static readonly Color SunDay = new Color(1f, 0.95f, 0.86f), SunLow = new Color(1f, 0.70f, 0.44f), Moon = new Color(0.62f, 0.72f, 1f);
        static readonly Color ColdTint = new Color(0.9f, 0.95f, 1.05f);
        const float SunIntensity = 1.6f, MoonIntensity = 0.45f;
        const float SunMaxElevation = 62f;

        /// <summary>Свалка разложена (сид известен): запустить сутки. Повторно — заменить.</summary>
        public static void Begin(int seed)
        {
            if (Instance != null) Destroy(Instance.gameObject);
            var go = new GameObject("Смена дня и ночи");
            var c = go.AddComponent<DayNightCycle>();
            var rng = new System.Random(seed * 31 + 7);
            c.startHour = 7f + (float)rng.NextDouble() * 10f;       // старт днём: с 7 до 17 часов
            if (GameSession.IsTutorial) c.startHour = 10f;           // обучение — ясное утро
            c.azimuth = (float)rng.NextDouble() * 360f;              // где у этой трассы восток
            c.localStart = Time.time;
        }

        void Awake() => Instance = this;

        static readonly int SkyBlendId = Shader.PropertyToID("_JR_SkyBlend");
        static readonly int SkyYawId = Shader.PropertyToID("_JR_SkyYaw");

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            Shader.SetGlobalVector(SkyBlendId, Vector4.zero);   // без смены суток небо — дневное
        }

        /// <summary>Секунды с начала отсчёта: в сети — серверное время (у всех одно), иначе свои часы.</summary>
        float Clock()
        {
            var nm = Unity.Netcode.NetworkManager.Singleton;
            if (nm != null && nm.IsListening) return (float)nm.ServerTime.Time;
            return Time.time - localStart;
        }

        void LateUpdate()
        {
            Hour = testHour >= 0f ? testHour : Mathf.Repeat(startHour + Clock() / SecondsPerHour, 24f);
            nowHour = Mathf.Round(Hour * 100f) / 100f;
            if (sun == null)
            {
                sun = RenderSettings.sun;
                if (sun == null)
                    foreach (var l in FindObjectsByType<Light>(FindObjectsSortMode.None))
                        if (l.type == LightType.Directional) { sun = l; break; }
                if (sun == null) return;
                sunShadows = sun.shadowStrength;
            }

            // Путь солнца: 6:00 — восход (восток), 12:00 — зенит, 18:00 — закат (запад).
            float arc = (Hour - 6f) / 12f * 180f;
            float elev = SunMaxElevation * Mathf.Sin(arc * Mathf.Deg2Rad);   // ниже нуля — ночь
            float yaw = azimuth + arc - 90f;

            float day = Ramp(4f, 22f, elev);       // 0 — низкое солнце, 1 — день
            float nightK = Ramp(0f, -10f, elev);   // 0 — сумерки ещё светлые, 1 — ночь

            // Небо и дымка: день → закат → ночь.
            var pal = StylizedAtmosphere.Palette.Lerp(Golden, Day, day);
            pal = StylizedAtmosphere.Palette.Lerp(pal, Night, nightK);
            if (StylizedAtmosphere.Instance != null) StylizedAtmosphere.Instance.SetTimeOfDay(pal, Ramp(-2f, -12f, elev));

            // Небо-панорама (JunkRace/Sky Panorama): веса картинок день / закат / сумерки с луной / глубокая ночь
            // и азимут солнца — нарисованное солнце стоит там же, где настоящее.
            float deep = Ramp(-15f, -45f, elev);
            Shader.SetGlobalVector(SkyBlendId, new Vector4(day * (1f - nightK), (1f - day) * (1f - nightK), nightK * (1f - deep), nightK * deep));
            Shader.SetGlobalFloat(SkyYawId, (yaw + 180f) * Mathf.Deg2Rad);

            // Свет: над горизонтом — солнце, ниже — луна с другой стороны неба. Смена — в сумерках,
            // когда свет почти погас, поэтому скачок направления не заметен. Низко не опускаем — тени не бесконечные.
            float intensity;
            Color color;
            if (elev >= 0f)
            {
                sun.transform.rotation = Quaternion.Euler(Mathf.Max(elev, 6f), yaw, 0f);
                intensity = SunIntensity * Ramp(-1f, 8f, elev);
                color = Color.Lerp(SunLow, SunDay, day);
                sun.shadowStrength = sunShadows;
            }
            else
            {
                sun.transform.rotation = Quaternion.Euler(Mathf.Max(-elev * 0.8f, 6f), yaw + 180f, 0f);
                intensity = MoonIntensity * Ramp(-1f, -10f, elev);
                color = Moon;
                sun.shadowStrength = sunShadows * 0.6f;
            }

            // Погода сверху: тучи приглушают и холодят свет.
            sun.intensity = intensity * (1f - Weather.SunDim);
            sun.color = Color.Lerp(color, ColdTint, Weather.SunCold * (1f - nightK));
        }

        /// <summary>Плавный переход 0 → 1, когда x идёт от a к b (b может быть меньше a).</summary>
        static float Ramp(float a, float b, float x)
        {
            float t = Mathf.Clamp01((x - a) / (b - a));
            return t * t * (3f - 2f * t);
        }
    }
}
