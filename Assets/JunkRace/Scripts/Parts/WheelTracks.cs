using UnityEngine;
using UnityEngine.Rendering;

namespace JunkRace
{
    /// <summary>
    /// Следы колёс: один общий меш на сцену — кольцевой буфер отрезков-лент. Колесо дописывает отрезок
    /// каждые ~0.3 м пути; когда буфер полон, новые отрезки занимают место самых старых, а те,
    /// что на очереди, заранее плавно гаснут. Чисто визуально, у каждого игрока своё.
    /// На снегу след тёмный и заметный, на грунте — слабее, на траве — едва.
    /// </summary>
    public class WheelTracks : MonoBehaviour
    {
        /// <summary>Состояние следа одного колеса (хранит само колесо).</summary>
        public class Trail
        {
            internal Vector3 L, R, Fwd;
            internal float V;
            internal bool Has;
        }

        const int MaxSegments = 3000;
        const int FadeSegments = 400;     // столько самых старых отрезков гаснут перед перезаписью
        const float MinStep = 0.3f;       // длина отрезка, м
        const float MaxStep = 3f;         // дальше — разрыв (прыжок, сброс на трассу)
        const float Lift = 0.025f;        // над поверхностью, чтобы не мерцало
        const float TreadTile = 0.8f;     // повтор рисунка протектора вдоль, м

        static WheelTracks instance;

        Mesh mesh;
        Vector3[] verts;
        Vector2[] uvs;
        Color32[] colors;
        byte[] alpha;   // исходная прозрачность каждого отрезка
        int head;       // куда пишем следующий отрезок
        int written;    // сколько всего записано (до заполнения буфера)
        bool dirty;

        static WheelTracks Instance
        {
            get
            {
                if (instance == null) instance = new GameObject("Следы_колёс").AddComponent<WheelTracks>();
                return instance;
            }
        }

        void Awake()
        {
            verts = new Vector3[MaxSegments * 4];
            uvs = new Vector2[MaxSegments * 4];
            colors = new Color32[MaxSegments * 4];
            alpha = new byte[MaxSegments];
            var tris = new int[MaxSegments * 6];
            for (int i = 0; i < MaxSegments; i++)
            {
                int v = i * 4, t = i * 6;
                tris[t] = v; tris[t + 1] = v + 1; tris[t + 2] = v + 2;
                tris[t + 3] = v; tris[t + 4] = v + 2; tris[t + 5] = v + 3;
            }
            mesh = new Mesh { name = "Следы_колёс" };
            mesh.MarkDynamic();
            mesh.vertices = verts;
            mesh.uv = uvs;
            mesh.colors32 = colors;
            mesh.triangles = tris;
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 100000f);   // следы везде — не отсекать

            gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = gameObject.AddComponent<MeshRenderer>();
            r.sharedMaterial = MakeMaterial();
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
        }

        void OnDestroy()
        {
            if (instance == this) instance = null;
            if (mesh != null) Destroy(mesh);
        }

        void LateUpdate()
        {
            if (!dirty) return;
            dirty = false;
            mesh.vertices = verts;
            mesh.uv = uvs;
            mesh.colors32 = colors;
        }

        /// <summary>Колесо оторвалось от земли (или сменилось что-то ещё): следующий отрезок начнёт новый след.</summary>
        public static void Break(Trail t) { if (t != null) t.Has = false; }

        /// <summary>
        /// Колесо стоит на земле в точке point. halfWidth — полуширина шины, surface — цвет поверхности под ней.
        /// </summary>
        public static void Touch(Trail t, Vector3 point, Vector3 normal, Vector3 velocity, float halfWidth, Color surface)
        {
            if (t == null) return;
            Vector3 flat = Vector3.ProjectOnPlane(velocity, normal);
            if (flat.sqrMagnitude > 0.25f) t.Fwd = flat.normalized;
            else if (t.Fwd == Vector3.zero) return;   // стоим на месте и не знаем куда — ждём
            Vector3 right = Vector3.Cross(normal, t.Fwd).normalized * Mathf.Clamp(halfWidth, 0.06f, 0.35f);
            Vector3 c = point + normal * Lift;
            Vector3 l = c - right, r = c + right;
            if (!t.Has) { t.L = l; t.R = r; t.V = 0f; t.Has = true; return; }
            float step = Vector3.Distance((t.L + t.R) * 0.5f, c);
            if (step < MinStep) return;
            if (step > MaxStep) { t.L = l; t.R = r; t.V = 0f; return; }
            float v = t.V + step / TreadTile;
            Instance.Push(t.L, t.R, r, l, t.V, v, Tint(surface));
            t.L = l; t.R = r; t.V = v % 64f;   // остаток — без потери точности на длинной трассе
        }

        /// <summary>Цвет следа: снег продавлен в голубую тень, грунт — темнее, трава — чуть-чуть.</summary>
        static Color32 Tint(Color s)
        {
            float lum = s.r * 0.3f + s.g * 0.59f + s.b * 0.11f;
            bool snow = lum > 0.7f;
            bool grass = s.g > s.r * 1.1f && s.g > s.b;
            Color c;
            float a;
            if (snow) { c = new Color(0.45f, 0.52f, 0.66f); a = 0.75f; }
            else if (grass) { c = s * 0.45f; a = 0.3f; }
            else { c = s * 0.4f; a = 0.45f; }
            c.a = a;
            return c;
        }

        void Push(Vector3 a0, Vector3 a1, Vector3 b1, Vector3 b0, float v0, float v1, Color32 color)
        {
            int i = head, v = i * 4;
            verts[v] = a0; verts[v + 1] = b0; verts[v + 2] = b1; verts[v + 3] = a1;
            uvs[v] = new Vector2(0f, v0); uvs[v + 1] = new Vector2(0f, v1); uvs[v + 2] = new Vector2(1f, v1); uvs[v + 3] = new Vector2(1f, v0);
            alpha[i] = color.a;
            for (int k = 0; k < 4; k++) colors[v + k] = color;
            head = (head + 1) % MaxSegments;
            if (written < MaxSegments) written++;

            // Самые старые отрезки (следующие за головой) гаснут: ближайший к перезаписи — почти прозрачный.
            if (written >= MaxSegments - FadeSegments)
                for (int k = 0; k < FadeSegments; k++)
                {
                    int j = (head + k) % MaxSegments;
                    if (alpha[j] == 0) continue;
                    byte fa = (byte)(alpha[j] * (k + 1) / (FadeSegments + 1));
                    int jv = j * 4;
                    for (int q = 0; q < 4; q++) colors[jv + q].a = fa;
                }
            dirty = true;
        }

        static Material MakeMaterial()
        {
            // Протектор: мягкие края поперёк, полосы-ёлочка вдоль. Альфа — сила следа.
            const int w = 32, h = 64;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Repeat, name = "Протектор" };
            var px = new Color32[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float u = (x + 0.5f) / w, vv = (y + 0.5f) / h;
                    float edge = Mathf.SmoothStep(0f, 1f, Mathf.Min(u, 1f - u) / 0.18f);
                    // Шашки протектора: косые полосы, зеркально от середины.
                    float slant = vv * 4f + Mathf.Abs(u - 0.5f) * 1.5f;
                    float tread = Mathf.Lerp(0.55f, 1f, Mathf.SmoothStep(0.35f, 0.65f, Mathf.Abs(Mathf.Repeat(slant, 1f) - 0.5f) * 2f));
                    px[y * w + x] = new Color32(255, 255, 255, (byte)(edge * tread * 255f));
                }
            tex.SetPixels32(px);
            tex.Apply(true);

            var shader = Shader.Find("Universal Render Pipeline/Particles/Unlit") ?? Shader.Find("Sprites/Default");
            var m = new Material(shader) { name = "WheelTracks", mainTexture = tex };
            if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", tex);
            if (m.HasProperty("_Surface"))
            {
                m.SetFloat("_Surface", 1f);
                m.SetFloat("_Blend", 0f);
                m.SetOverrideTag("RenderType", "Transparent");
                m.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
                m.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                m.SetInt("_ZWrite", 0);
                m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            }
            // Раньше остальной прозрачности (пыль, вода) — следы лежат на земле под ней.
            m.renderQueue = (int)RenderQueue.Transparent - 50;
            return m;
        }
    }
}
