using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace JunkRace
{
    /// <summary>
    /// Погода для красоты: снег, метель, дождь, ветер с листьями. Своя на каждом участке трассы —
    /// по биому и сиду (у всех игроков одинаково): в снегу — снег или метель, в лесу и горах — бывает ясно,
    /// дождь или ветер. Частицы летят вокруг камеры (у каждого своё), смена плавная, в непогоду дымка гуще
    /// и солнце тусклее. В редакторе и Development Build F8 перебирает погоду по кругу.
    /// </summary>
    public class Weather : MonoBehaviour
    {
        public enum Kind { Clear, Snow, Blizzard, Rain, Wind }

        public static Weather Instance { get; private set; }
        /// <summary>Погода, к которой сейчас идёт смена.</summary>
        public Kind Current { get; private set; }
        /// <summary>Непогода: на сколько приглушить солнце (0..1) и насколько охладить его цвет (0..1).</summary>
        public static float SunDim { get; private set; }
        public static float SunCold { get; private set; }

        const float ChangeSeconds = 4f;   // за сколько погода сменяется полностью
        const int KindCount = 5;

        RouteMap map;
        readonly List<float> starts = new List<float>();
        readonly List<Kind> kinds = new List<Kind>();
        Vector3 wind;                     // направление и сила ветра, м/с (горизонтально)
        readonly float[] amount = new float[KindCount];
        ParticleSystem snow, blizzard, rain, leaves;
        Light sun;
        float sunBase = -1f;
        Color sunBaseColor = Color.white;
        int forced = -1;                  // F8: принудительная погода (-1 — по трассе)
        float lookupTimer;
        bool covered;       // над камерой крыша (тоннель) — осадки стихают
        float open = 1f;    // 1 — под открытым небом, 0 — под крышей (плавно)
        Vector3 lastCam;
        bool hasLastCam;
        Vector3 camVel;
        Vector4 lastMood = new Vector4(-1f, 0f, 0f, 0f);

        /// <summary>Трасса построена: разложить погоду по участкам. Повторный вызов заменяет прежнюю.</summary>
        public static void Begin(RouteMap map, List<float> sectionStarts, List<Biome> sectionBiomes, int seed)
        {
            if (Instance != null) Destroy(Instance.gameObject);
            var w = new GameObject("Погода").AddComponent<Weather>();
            w.Setup(map, sectionStarts, sectionBiomes, seed);
        }

        void Awake() => Instance = this;

        void OnDestroy()
        {
            if (Instance == this) { Instance = null; SunDim = SunCold = 0f; }
            if (sun != null && sunBase >= 0f) { sun.intensity = sunBase; sun.color = sunBaseColor; }
            if (StylizedAtmosphere.Instance != null) StylizedAtmosphere.Instance.SetWeather(Color.white, 0f, 1f);
        }

        void Setup(RouteMap m, List<float> sectionStarts, List<Biome> sectionBiomes, int seed)
        {
            map = m;
            // Свой генератор: погода не сдвигает случайности трассы.
            var rng = new System.Random(seed * 7919 + 17);
            for (int i = 0; i < sectionStarts.Count; i++)
            {
                starts.Add(sectionStarts[i]);
                double r = rng.NextDouble();
                Kind k;
                switch (sectionBiomes[i])
                {
                    case Biome.Snow: k = r < 0.55 ? Kind.Snow : Kind.Blizzard; break;
                    case Biome.Forest: k = r < 0.45 ? Kind.Clear : r < 0.75 ? Kind.Rain : Kind.Wind; break;
                    case Biome.Desert: k = r < 0.65 ? Kind.Clear : Kind.Wind; break;   // в пустыне — жара или ветер с песком
                    default: k = r < 0.45 ? Kind.Clear : r < 0.65 ? Kind.Rain : Kind.Wind; break;
                }
                kinds.Add(k);
            }
            float a = (float)rng.NextDouble() * Mathf.PI * 2f;
            wind = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * (6f + (float)rng.NextDouble() * 4f);

            var dot = SoftDot();
            snow = Make("Снег", dot, 9000, false);
            blizzard = Make("Метель", dot, 9000, true);
            rain = Make("Дождь", dot, 5000, true);
            leaves = Make("Листья", Leaf(), 400, false);
            SetupSnow();
            SetupBlizzard();
            SetupRain();
            SetupLeaves();
        }

        // ================= Каждый кадр =================

        void LateUpdate()
        {
            var cam = Camera.main;
            if (cam == null) return;
            float dt = Time.deltaTime;
            Vector3 c = cam.transform.position;
            if (hasLastCam && dt > 1e-4f) camVel = Vector3.Lerp(camVel, (c - lastCam) / dt, 1f - Mathf.Exp(-dt * 3f));
            if (hasLastCam && (c - lastCam).sqrMagnitude > 400f) camVel = Vector3.zero;   // телепорт камеры
            lastCam = c;
            hasLastCam = true;

            if (Debug.isDebugBuild && Keyboard.current != null && Keyboard.current.f8Key.wasPressedThisFrame)
                forced = forced + 1 >= KindCount ? -1 : forced + 1;

            lookupTimer -= dt;
            if (lookupTimer <= 0f)
            {
                lookupTimer = 0.25f;
                // Камера в тоннеле — осадки стихают (место тоннеля знает генератор трассы).
                covered = RouteGenerator.Instance != null && RouteGenerator.Instance.InsideTunnel(c);
                Current = forced >= 0 ? (Kind)forced : KindAt(c);
            }

            float step = dt / ChangeSeconds;
            for (int i = 0; i < KindCount; i++)
                amount[i] = Mathf.MoveTowards(amount[i], i == (int)Current ? 1f : 0f, step);

            // Источники — над камерой и с упреждением по её скорости: на скорости в снег въезжаешь, а не догоняешь его.
            Vector3 lead = new Vector3(camVel.x, 0f, camVel.z);
            Vector3 upwind = -wind.normalized;
            open = Mathf.MoveTowards(open, covered ? 0f : 1f, dt * 2f);
            Place(snow, c + Vector3.up * 12f + lead * 2f, amount[(int)Kind.Snow] * 1100f * open);
            Place(blizzard, c + Vector3.up * 5f + upwind * 14f + lead * 1f, amount[(int)Kind.Blizzard] * 3800f * open);
            Place(rain, c + Vector3.up * 16f + lead * 0.9f - wind * 0.15f, amount[(int)Kind.Rain] * 3200f * open);
            Place(leaves, c + Vector3.up * 3f + upwind * 22f + lead * 1f, amount[(int)Kind.Wind] * 45f * open);

            ApplyMood();
        }

        Kind KindAt(Vector3 p)
        {
            if (map == null || starts.Count == 0) return Kind.Clear;
            float progress = map.Progress(p);
            if (progress < starts[0]) return Kind.Clear;   // свалка и выезд — ясно
            int i = starts.Count - 1;
            while (i > 0 && progress < starts[i]) i--;
            return kinds[i];
        }

        static void Place(ParticleSystem ps, Vector3 at, float rate)
        {
            ps.transform.position = at;
            var em = ps.emission;
            em.rateOverTime = rate;
        }

        /// <summary>Дымка и солнце — по смеси погод.</summary>
        void ApplyMood()
        {
            float sn = amount[(int)Kind.Snow], bz = amount[(int)Kind.Blizzard], rn = amount[(int)Kind.Rain];
            float total = sn + bz + rn;
            if (StylizedAtmosphere.Instance != null)
            {
                Color col = Color.white;
                if (total > 1e-3f)
                    col = (new Color(0.86f, 0.9f, 0.97f) * sn + new Color(0.88f, 0.91f, 0.96f) * bz + new Color(0.62f, 0.67f, 0.74f) * rn) / total;
                float blend = sn * 0.35f + bz * 0.8f + rn * 0.5f;
                float dist = 1f - sn * 0.5f - bz * 0.85f - rn * 0.55f;
                // Дымку пересылаем шейдерам только когда она меняется (во время смены погоды).
                var key = new Vector4(blend, dist, col.r + col.g * 3f + col.b * 9f, 0f);
                if (key != lastMood) { lastMood = key; StylizedAtmosphere.Instance.SetWeather(col, blend, dist); }
            }
            // Насколько приглушить и охладить солнце — смена дня и ночи берёт это сама.
            SunDim = sn * 0.15f + bz * 0.5f + rn * 0.4f;
            SunCold = sn * 0.75f + bz * 0.9f + rn * 0.5f;
            if (DayNightCycle.Instance != null) return;
            if (sun == null) { sun = RenderSettings.sun; if (sun != null) { sunBase = sun.intensity; sunBaseColor = sun.color; } }
            if (sun != null)
            {
                sun.intensity = sunBase * (1f - sn * 0.15f - bz * 0.5f - rn * 0.4f);
                // Холодный свет: снег белый с голубыми тенями, а не кремовый от закатного солнца.
                float cold = sn * 0.75f + bz * 0.9f + rn * 0.5f;
                sun.color = Color.Lerp(sunBaseColor, new Color(0.9f, 0.95f, 1.05f), cold);
            }
        }

        // ================= Частицы =================

        ParticleSystem Make(string name, Texture2D tex, int max, bool stretched)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.loop = true;
            main.playOnAwake = false;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = max;
            main.startSpeed = 0f;
            main.scalingMode = ParticleSystemScalingMode.Shape;
            var em = ps.emission;
            em.rateOverTime = 0f;
            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Box;

            var r = go.GetComponent<ParticleSystemRenderer>();
            r.sharedMaterial = Mat(tex);
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.renderMode = stretched ? ParticleSystemRenderMode.Stretch : ParticleSystemRenderMode.Billboard;
            r.minParticleSize = 0f;
            r.maxParticleSize = stretched ? 0.03f : 0.02f;   // доля экрана: у самой камеры хлопья не во весь кадр
            ps.Play();
            return ps;
        }

        static void Velocity(ParticleSystem ps, Vector3 min, Vector3 max)
        {
            var v = ps.velocityOverLifetime;
            v.enabled = true;
            v.space = ParticleSystemSimulationSpace.World;
            v.x = new ParticleSystem.MinMaxCurve(min.x, max.x);
            v.y = new ParticleSystem.MinMaxCurve(min.y, max.y);
            v.z = new ParticleSystem.MinMaxCurve(min.z, max.z);
        }

        static void Fade(ParticleSystem ps, float alpha)
        {
            var col = ps.colorOverLifetime;
            col.enabled = true;
            var g = new Gradient();
            g.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                      new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(alpha, 0.1f), new GradientAlphaKey(alpha, 0.85f), new GradientAlphaKey(0f, 1f) });
            col.color = g;
        }

        static void Noise(ParticleSystem ps, float strength, float frequency)
        {
            var n = ps.noise;
            n.enabled = true;
            n.strength = strength;
            n.frequency = frequency;
            n.quality = ParticleSystemNoiseQuality.Low;
            n.scrollSpeed = 0.3f;
        }

        void SetupSnow()
        {
            var main = snow.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(7f, 10f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.06f, 0.14f);
            main.startColor = new Color(1f, 1f, 1f, 1f);
            var shape = snow.shape;
            shape.scale = new Vector3(60f, 2f, 60f);
            Vector3 w = wind * 0.15f;
            Velocity(snow, new Vector3(w.x - 0.4f, -2f, w.z - 0.4f), new Vector3(w.x + 0.4f, -1.1f, w.z + 0.4f));
            Noise(snow, 0.6f, 0.35f);
            Fade(snow, 0.95f);
        }

        void SetupBlizzard()
        {
            var main = blizzard.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(2.5f, 3.5f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.06f, 0.13f);
            main.startColor = new Color(1f, 1f, 1f, 1f);
            var shape = blizzard.shape;
            shape.scale = new Vector3(60f, 16f, 60f);
            Vector3 w = wind * 1.4f;
            Velocity(blizzard, new Vector3(w.x - 1.5f, -4.5f, w.z - 1.5f), new Vector3(w.x + 1.5f, -2.5f, w.z + 1.5f));
            Noise(blizzard, 1.5f, 0.4f);
            Fade(blizzard, 0.9f);
            var r = blizzard.GetComponent<ParticleSystemRenderer>();
            r.velocityScale = 0.02f;
            r.lengthScale = 1f;
        }

        void SetupRain()
        {
            var main = rain.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(1.1f, 1.4f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.025f, 0.04f);
            main.startColor = new Color(0.78f, 0.84f, 0.95f, 1f);
            var shape = rain.shape;
            shape.scale = new Vector3(45f, 1f, 45f);
            Vector3 w = wind * 0.25f;
            Velocity(rain, new Vector3(w.x, -20f, w.z), new Vector3(w.x, -16f, w.z));
            Fade(rain, 0.5f);
            var r = rain.GetComponent<ParticleSystemRenderer>();
            r.velocityScale = 0.06f;
            r.lengthScale = 1f;
        }

        void SetupLeaves()
        {
            var main = leaves.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(6f, 8f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.12f, 0.24f);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.85f, 0.55f, 0.15f), new Color(0.45f, 0.62f, 0.2f));
            var shape = leaves.shape;
            shape.scale = new Vector3(60f, 8f, 60f);
            Vector3 w = wind;
            Velocity(leaves, new Vector3(w.x - 1.5f, -0.8f, w.z - 1.5f), new Vector3(w.x + 1.5f, 0.6f, w.z + 1.5f));
            Noise(leaves, 2f, 0.5f);
            Fade(leaves, 1f);
            var rot = leaves.rotationOverLifetime;
            rot.enabled = true;
            rot.z = new ParticleSystem.MinMaxCurve(-4f, 4f);
        }

        // ================= Материал и картинки =================

        static readonly Dictionary<Texture2D, Material> mats = new Dictionary<Texture2D, Material>();

        static Material Mat(Texture2D tex)
        {
            if (mats.TryGetValue(tex, out var m) && m != null) return m;
            var shader = Shader.Find("Universal Render Pipeline/Particles/Unlit") ?? Shader.Find("Sprites/Default");
            m = new Material(shader) { name = "Weather_" + tex.name, mainTexture = tex };
            if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", tex);
            if (m.HasProperty("_Surface"))
            {
                m.SetFloat("_Surface", 1f);
                m.SetFloat("_Blend", 0f);
                m.SetOverrideTag("RenderType", "Transparent");
                m.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                m.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                m.SetInt("_ZWrite", 0);
                m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            }
            mats[tex] = m;
            return m;
        }

        static Texture2D dotTex, leafTex;

        static Texture2D SoftDot()
        {
            if (dotTex != null) return dotTex;
            dotTex = Draw("Снежинка", 32, (x, y) => Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(1f - Mathf.Sqrt(x * x + y * y)) * 1.6f));
            return dotTex;
        }

        static Texture2D Leaf()
        {
            if (leafTex != null) return leafTex;
            // Лист: заострённый эллипс с прожилкой.
            leafTex = Draw("Лист", 32, (x, y) =>
            {
                float w = 0.55f * (1f - y * y);
                float inside = w > 0f ? Mathf.Clamp01((w - Mathf.Abs(x)) * 12f) : 0f;
                float vein = Mathf.Abs(x) < 0.04f ? 0.75f : 1f;
                return inside * vein;
            });
            return leafTex;
        }

        static Texture2D Draw(string name, int n, System.Func<float, float, float> alpha)
        {
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp, name = name };
            var px = new Color32[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float a = alpha((x + 0.5f) / n * 2f - 1f, (y + 0.5f) / n * 2f - 1f);
                    px[y * n + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(a) * 255f));
                }
            tex.SetPixels32(px);
            tex.Apply(true);
            return tex;
        }
    }
}
