using UnityEngine;

namespace JunkRace
{
    /// <summary>
    /// Пыль и камешки из-под колёс. Две общие системы частиц на всю сцену (дешево): пыль — облачка цвета
    /// дороги, растут и тают; камешки — мелкие, тёмные, падают. Чисто визуально, у каждого игрока своё.
    /// </summary>
    public static class WheelDust
    {
        static ParticleSystem dust, gravel;
        static Material mat;

        static ParticleSystem Make(string name, float gravity, bool grow)
        {
            var go = new GameObject(name);
            Object.DontDestroyOnLoad(go);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.loop = true;   // система «играет» всегда, частицы даём сами через Emit
            main.playOnAwake = false;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = grow ? 1500 : 800;
            main.gravityModifier = gravity;
            var em = ps.emission;
            em.enabled = false;
            var shape = ps.shape;
            shape.enabled = false;

            var col = ps.colorOverLifetime;
            col.enabled = true;
            var g = new Gradient();
            g.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                      grow ? new[] { new GradientAlphaKey(0.55f, 0f), new GradientAlphaKey(0.35f, 0.4f), new GradientAlphaKey(0f, 1f) }
                           : new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 0.7f), new GradientAlphaKey(0f, 1f) });
            col.color = g;
            if (grow)
            {
                var size = ps.sizeOverLifetime;
                size.enabled = true;
                size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.EaseInOut(0f, 0.6f, 1f, 2.4f));
                var vel = ps.limitVelocityOverLifetime;
                vel.enabled = true;
                vel.dampen = 0.15f;
                vel.limit = 0.5f;
            }
            var r = go.GetComponent<ParticleSystemRenderer>();
            r.sharedMaterial = Material();
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.sortMode = ParticleSystemSortMode.Distance;
            ps.Play();
            return ps;
        }

        static Material Material()
        {
            if (mat != null) return mat;
            // Мягкий круг: края прозрачные.
            const int n = 64;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float dx = (x + 0.5f) / n * 2f - 1f, dy = (y + 0.5f) / n * 2f - 1f;
                    float a = Mathf.Clamp01(1f - Mathf.Sqrt(dx * dx + dy * dy));
                    px[y * n + x] = new Color32(255, 255, 255, (byte)(Mathf.SmoothStep(0f, 1f, a) * 255));
                }
            tex.SetPixels32(px);
            tex.Apply();

            var shader = Shader.Find("Universal Render Pipeline/Particles/Unlit") ?? Shader.Find("Sprites/Default");
            mat = new Material(shader) { name = "WheelDust", mainTexture = tex };
            if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", tex);
            if (mat.HasProperty("_Surface"))
            {
                // Прозрачный, обычное смешивание.
                mat.SetFloat("_Surface", 1f);
                mat.SetFloat("_Blend", 0f);
                mat.SetOverrideTag("RenderType", "Transparent");
                mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                mat.SetInt("_ZWrite", 0);
                mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            }
            return mat;
        }

        /// <summary>
        /// Колесо катится по земле: amount — сколько частиц выпустить (может быть дробным, копится снаружи),
        /// velocity — скорость колеса, color — цвет поверхности.
        /// </summary>
        public static void Emit(Vector3 at, Vector3 velocity, Vector3 up, Color color, int count, bool stones)
        {
            if (count <= 0) return;
            if (dust == null) dust = Make("Пыль_колёс", -0.05f, true);
            if (gravel == null) gravel = Make("Камешки_колёс", 2.2f, false);

            float speed = velocity.magnitude;
            Vector3 back = speed > 0.1f ? -velocity / speed : Vector3.zero;
            Color dustColor = Color.Lerp(color, Color.white, 0.25f);
            for (int i = 0; i < count; i++)
            {
                var p = new ParticleSystem.EmitParams
                {
                    position = at + Random.insideUnitSphere * 0.15f + up * 0.1f,
                    velocity = back * Random.Range(0.5f, 1.5f) + up * Random.Range(0.4f, 1.2f) + Random.insideUnitSphere * 0.5f,
                    startSize = Random.Range(0.35f, 0.75f) * Mathf.Lerp(0.8f, 1.6f, Mathf.Clamp01(speed / 20f)),
                    startLifetime = Random.Range(0.7f, 1.4f),
                    startColor = dustColor,
                    rotation = Random.Range(0f, 360f),
                };
                dust.Emit(p, 1);
            }
            if (!stones) return;
            Color stoneColor = color * 0.55f;
            stoneColor.a = 1f;
            int n = Mathf.Max(1, count / 2);
            for (int i = 0; i < n; i++)
            {
                var p = new ParticleSystem.EmitParams
                {
                    position = at + up * 0.05f,
                    velocity = back * Random.Range(1.5f, 3.5f) + up * Random.Range(1.5f, 3f) + Random.insideUnitSphere * 0.8f,
                    startSize = Random.Range(0.05f, 0.11f),
                    startLifetime = Random.Range(0.4f, 0.7f),
                    startColor = stoneColor,
                };
                gravel.Emit(p, 1);
            }
        }

        static readonly System.Collections.Generic.Dictionary<long, Color> surfaceCache = new System.Collections.Generic.Dictionary<long, Color>();

        /// <summary>Цвет поверхности под колесом: материал той части меша, куда попал луч.</summary>
        public static Color SurfaceColor(RaycastHit hit)
        {
            var c = hit.collider;
            if (c == null) return DefaultDust;
            int sub = 0;
            if (c is MeshCollider mc && mc.sharedMesh != null && mc.sharedMesh.isReadable && mc.sharedMesh.subMeshCount > 1 && hit.triangleIndex >= 0)
            {
                var mesh = mc.sharedMesh;
                int index = hit.triangleIndex * 3;
                for (int i = 0; i < mesh.subMeshCount; i++)
                {
                    var d = mesh.GetSubMesh(i);
                    if (index >= d.indexStart && index < d.indexStart + d.indexCount) { sub = i; break; }
                }
            }
            long key = ((long)c.GetInstanceID() << 8) | (long)sub;
            if (surfaceCache.TryGetValue(key, out var cached)) return cached;
            Color color = DefaultDust;
            var r = c.GetComponent<Renderer>() ?? c.GetComponentInParent<Renderer>();
            if (r != null)
            {
                var mats = r.sharedMaterials;
                var m = mats.Length > sub ? mats[sub] : mats.Length > 0 ? mats[0] : null;
                if (m != null && m.HasProperty("_BaseColor")) color = m.GetColor("_BaseColor");
                else if (m != null && m.HasProperty("_Color")) color = m.color;
                // У текстурной поверхности (дорога, трава) цвет материала белый — берём средний цвет картинки.
                var tex = m != null && m.HasProperty("_BaseMap") ? m.GetTexture("_BaseMap") : null;
                if (tex != null) color *= Average(tex);
            }
            color.a = 1f;
            if (surfaceCache.Count > 512) surfaceCache.Clear();
            surfaceCache[key] = color;
            return color;
        }

        static readonly System.Collections.Generic.Dictionary<Texture, Color> averages = new System.Collections.Generic.Dictionary<Texture, Color>();

        /// <summary>Средний цвет текстуры: уменьшаем на видеокарте до 1×1 и читаем пиксель (текстура может быть не читаемой).</summary>
        static Color Average(Texture tex)
        {
            if (averages.TryGetValue(tex, out var c)) return c;
            c = Color.white;
            var rt = RenderTexture.GetTemporary(8, 8, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var prev = RenderTexture.active;
            try
            {
                Graphics.Blit(tex, rt);
                RenderTexture.active = rt;
                var read = new Texture2D(8, 8, TextureFormat.RGBA32, false);
                read.ReadPixels(new Rect(0, 0, 8, 8), 0, 0);
                read.Apply();
                Color sum = Color.black;
                foreach (var px in read.GetPixels()) sum += px;
                c = sum / 64f;
                c.a = 1f;
                Object.Destroy(read);
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
            }
            averages[tex] = c;
            return c;
        }

        static readonly Color DefaultDust = new Color(0.62f, 0.55f, 0.45f);
    }
}
