using System.Collections.Generic;
using UnityEngine;

namespace JunkRace
{
    /// <summary>
    /// Брызги и пена на воде — чисто для вида, у каждого игрока свои (машины видны всем, вот и считаем у всех):
    ///   • всплеск, когда машина въезжает в воду (чем быстрее — тем больше);
    ///   • пена за кормой и брызги у носа, пока машина плывёт или идёт вброд;
    ///   • брызги из-под колёс в воде (зовёт WheelPart).
    /// Три общие системы частиц на сцену: капли (падают), пена (лежит на воде и расплывается), водяная пыль.
    /// Появляется сама вместе с первой водой в сцене (WaterZone).
    /// </summary>
    public class WaterFx : MonoBehaviour
    {
        static WaterFx instance;

        ParticleSystem drops, foam, mist;
        readonly Dictionary<Vehicle, (bool inWater, Vector3 pos, float wakeDebt)> state = new Dictionary<Vehicle, (bool, Vector3, float)>();
        readonly List<Vehicle> gone = new List<Vehicle>();

        /// <summary>Вода появилась в сцене — нужны брызги.</summary>
        public static void Ensure()
        {
            if (instance != null) return;
            instance = new GameObject("Брызги").AddComponent<WaterFx>();
        }

        void Awake()
        {
            var dot = SoftDot();
            drops = Make("Капли", dot, 1500, 1.6f, ParticleSystemRenderMode.Billboard);
            foam = Make("Пена", dot, 1200, 0f, ParticleSystemRenderMode.HorizontalBillboard);
            mist = Make("Водяная_пыль", dot, 400, -0.05f, ParticleSystemRenderMode.Billboard);

            Fade(drops, new[] { new GradientAlphaKey(0.95f, 0f), new GradientAlphaKey(0.9f, 0.7f), new GradientAlphaKey(0f, 1f) });
            Fade(foam, new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(0.8f, 0.08f), new GradientAlphaKey(0.55f, 0.5f), new GradientAlphaKey(0f, 1f) });
            Fade(mist, new[] { new GradientAlphaKey(0.4f, 0f), new GradientAlphaKey(0.25f, 0.4f), new GradientAlphaKey(0f, 1f) });
            Grow(foam, 0.6f, 2.2f);
            Grow(mist, 0.6f, 2f);
        }

        void OnDestroy()
        {
            if (instance == this) instance = null;
        }

        // ======================= Машины в воде =======================

        void LateUpdate()
        {
            float dt = Time.deltaTime;
            if (dt <= 0f) return;
            gone.Clear();
            foreach (var kv in state) if (kv.Key == null) gone.Add(kv.Key);
            foreach (var v in gone) state.Remove(v);

            foreach (var v in Vehicle.All)
            {
                if (v == null || v.RootPart == null) continue;
                Vector3 center = v.Body != null ? v.Body.worldCenterOfMass : v.transform.position;
                Vector3 bottom = center - Vector3.up * 0.35f;
                bool inWater = WaterZone.TryGetDepth(bottom, out float depth);
                state.TryGetValue(v, out var s);
                Vector3 vel = s.pos != Vector3.zero ? (center - s.pos) / dt : Vector3.zero;
                if (vel.sqrMagnitude > 900f) vel = Vector3.zero;   // телепорт (сброс на трассу)
                float wake = s.wakeDebt;

                if (inWater)
                {
                    Vector3 surface = new Vector3(center.x, bottom.y + depth, center.z);
                    float speed = vel.magnitude;
                    // Въехал (или упал) в воду — всплеск.
                    if (!s.inWater && speed > 2f) Splash(surface, Mathf.Clamp01(speed / 12f) + Mathf.Clamp01(-vel.y / 6f) * 0.5f);

                    // Плывёт / идёт вброд — пена за кормой и брызги у носа.
                    Vector3 flat = new Vector3(vel.x, 0f, vel.z);
                    float fs = flat.magnitude;
                    if (fs > 1.2f)
                    {
                        Vector3 dir = flat / fs;
                        wake += dt * Mathf.Min(fs, 14f) * 2.2f;
                        while (wake >= 1f)
                        {
                            wake -= 1f;
                            Vector3 side = new Vector3(dir.z, 0f, -dir.x) * Random.Range(-1f, 1f);
                            Foam(surface - dir * Random.Range(1.2f, 2f) + side, Random.Range(0.9f, 1.6f) * Mathf.Lerp(0.8f, 1.4f, fs / 12f));
                            if (Random.value < 0.6f) Drops(surface + dir * 1.4f + side * 0.8f, flat * 0.3f + Vector3.up * Random.Range(1.5f, 3f), 2);
                        }
                    }
                }
                else wake = 0f;
                state[v] = (inWater, center, wake);
            }
        }

        // ======================= Эффекты =======================

        /// <summary>Всплеск на поверхности воды: strength 0..1.5.</summary>
        public static void Splash(Vector3 surface, float strength)
        {
            if (instance == null) return;
            strength = Mathf.Clamp(strength, 0.2f, 1.5f);
            int n = Mathf.RoundToInt(28 * strength);
            for (int i = 0; i < n; i++)
            {
                Vector2 r = Random.insideUnitCircle;
                var v = new Vector3(r.x, 0f, r.y) * Random.Range(1.5f, 4f) * strength + Vector3.up * Random.Range(3f, 7f) * strength;
                instance.drops.Emit(new ParticleSystem.EmitParams
                {
                    position = surface + new Vector3(r.x, 0.1f, r.y) * 0.8f,
                    velocity = v,
                    startSize = Random.Range(0.1f, 0.25f),
                    startLifetime = Random.Range(0.7f, 1.3f),
                    startColor = new Color(0.86f, 0.95f, 1f),
                }, 1);
            }
            for (int i = 0; i < 6; i++)
            {
                Vector2 r = Random.insideUnitCircle * 1.6f * strength;
                Foam(surface + new Vector3(r.x, 0f, r.y), Random.Range(1.6f, 2.8f) * strength);
            }
            for (int i = 0; i < 4; i++)
            {
                instance.mist.Emit(new ParticleSystem.EmitParams
                {
                    position = surface + Random.insideUnitSphere * 0.8f + Vector3.up * 0.6f,
                    velocity = Vector3.up * Random.Range(0.5f, 1.5f) + Random.insideUnitSphere * 0.6f,
                    startSize = Random.Range(1.2f, 2.2f) * strength,
                    startLifetime = Random.Range(0.6f, 1f),
                    startColor = Color.white,
                }, 1);
            }
        }

        /// <summary>Пятно пены на воде: лежит плашмя, расплывается и тает.</summary>
        public static void Foam(Vector3 surface, float size)
        {
            if (instance == null) return;
            instance.foam.Emit(new ParticleSystem.EmitParams
            {
                position = surface + Vector3.up * 0.04f,
                velocity = Vector3.zero,
                startSize = size,
                startLifetime = Random.Range(1.4f, 2.4f),
                startColor = Color.white,
                rotation = Random.Range(0f, 360f),
            }, 1);
        }

        static void Drops(Vector3 at, Vector3 velocity, int count)
        {
            for (int i = 0; i < count; i++)
                instance.drops.Emit(new ParticleSystem.EmitParams
                {
                    position = at + Random.insideUnitSphere * 0.3f,
                    velocity = velocity + Random.insideUnitSphere * 1.2f,
                    startSize = Random.Range(0.07f, 0.16f),
                    startLifetime = Random.Range(0.5f, 0.9f),
                    startColor = new Color(0.86f, 0.95f, 1f),
                }, 1);
        }

        /// <summary>
        /// Колесо в воде: брызги назад и вверх, гуще на скорости. surface — точка на поверхности над колесом,
        /// velocity — скорость колеса, radius — радиус колеса, dt — время кадра.
        /// </summary>
        public static void WheelSpray(Vector3 surface, Vector3 velocity, float radius, float dt)
        {
            if (instance == null) return;
            float speed = velocity.magnitude;
            if (speed < 1f) return;
            float rate = Mathf.Min(speed, 15f) * 2.5f * Mathf.Clamp(radius / 0.4f, 0.6f, 1.6f);   // капель в секунду
            float n = rate * dt;
            int count = (int)n + (Random.value < n - (int)n ? 1 : 0);
            if (count == 0) return;
            Vector3 back = -velocity / speed;
            Drops(surface, back * Random.Range(1f, 2.5f) + Vector3.up * Random.Range(1.5f, 3.5f), count);
            if (Random.value < dt * 6f) Foam(surface, Random.Range(0.6f, 1.1f));
        }

        // ======================= Частицы =======================

        ParticleSystem Make(string name, Texture2D tex, int max, float gravity, ParticleSystemRenderMode mode)
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
            main.gravityModifier = gravity;
            main.startSpeed = 0f;
            var em = ps.emission; em.enabled = false;
            var shape = ps.shape; shape.enabled = false;
            var r = go.GetComponent<ParticleSystemRenderer>();
            r.renderMode = mode;
            r.sharedMaterial = MakeMaterial(tex);
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.sortMode = ParticleSystemSortMode.Distance;
            ps.Play();
            return ps;
        }

        static void Fade(ParticleSystem ps, GradientAlphaKey[] alpha)
        {
            var col = ps.colorOverLifetime;
            col.enabled = true;
            var g = new Gradient();
            g.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) }, alpha);
            col.color = g;
        }

        static void Grow(ParticleSystem ps, float from, float to)
        {
            var size = ps.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.EaseInOut(0f, from, 1f, to));
        }

        static Material mat;

        static Material MakeMaterial(Texture2D tex)
        {
            if (mat != null) return mat;
            var shader = Shader.Find("Universal Render Pipeline/Particles/Unlit") ?? Shader.Find("Sprites/Default");
            mat = new Material(shader) { name = "WaterFx", mainTexture = tex };
            if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", tex);
            if (mat.HasProperty("_Surface"))
            {
                mat.SetFloat("_Surface", 1f);
                mat.SetFloat("_Blend", 0f);
                mat.SetOverrideTag("RenderType", "Transparent");
                mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                mat.SetInt("_ZWrite", 0);
                mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            }
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent + 1;   // поверх воды
            return mat;
        }

        static Texture2D dot;

        static Texture2D SoftDot()
        {
            if (dot != null) return dot;
            const int n = 64;
            dot = new Texture2D(n, n, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp, name = "Капля" };
            var px = new Color32[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float dx = (x + 0.5f) / n * 2f - 1f, dy = (y + 0.5f) / n * 2f - 1f;
                    float a = Mathf.Clamp01(1f - Mathf.Sqrt(dx * dx + dy * dy));
                    px[y * n + x] = new Color32(255, 255, 255, (byte)(Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(a * 2.2f)) * 255));
                }
            dot.SetPixels32(px);
            dot.Apply(true);
            return dot;
        }
    }
}
