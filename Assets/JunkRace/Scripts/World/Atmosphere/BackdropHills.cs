using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Serialization;

namespace JunkRace
{
    /// <summary>
    /// Холмы-задник вокруг трассы, как дали в Over the Hill: гряда за грядой до горизонта,
    /// каждая следующая бледнее в дымке. Чисто декорация — без коллайдеров (дальше 6 м от дороги
    /// машину всё равно возвращает на трассу).
    ///
    /// Рельеф строится по сиду трассы и точкам дороги: возле дороги холмы уходят под землю,
    /// дальше плавно поднимаются, к горизонту — горы. На ближних склонах — лес (GPU instancing,
    /// тысячи деревьев без GameObject'ов).
    ///
    /// Вызывает RouteGenerator: при раскладке свалки и после постройки трассы. У всех игроков
    /// одинаково — всё по сиду.
    /// </summary>
    [ExecuteAlways]
    public class BackdropHills : MonoBehaviour
    {
        public static BackdropHills Instance { get; private set; }

        /// <summary>
        /// Трасса в одном биоме: холмы перекрашиваются под него (пустыня — песок и красный камень без леса,
        /// снег — белые склоны). null — как в материале (трава, камень, снежные вершины).
        /// </summary>
        public static Biome? Look;
        Material lookMaterial;
        Biome? lookFor;

        [Header("Материалы (ставит меню JunkRace → Атмосфера)")]
        [SerializeField] Material terrainMaterial;
        [SerializeField] Material treeCrownMaterial;
        [SerializeField] Material treeTrunkMaterial;

        [Header("Своя модель дерева (пусто — простая ёлка из кода)")]
        [Tooltip("Меш дерева: высота 1, низ на нуле. Материалы — по одному на сабмеш, с включённым GPU Instancing.")]
        [SerializeField] Mesh treeMesh;
        [SerializeField] Material[] treeMeshMaterials;

        [Header("Форма")]
        [Tooltip("Ближе этого к дороге холмов нет (уходят под землю), м. Генератор трассы ставит декорации до ~90 м — не меньше.")]
        [SerializeField] float clearDistance = 90f;
        [Tooltip("За сколько метров холмы поднимаются до полной высоты.")]
        [SerializeField] float riseDistance = 240f;
        [Tooltip("Насколько далеко от дороги тянется рельеф, м.")]
        [SerializeField] float extent = 2200f;
        [Tooltip("Шаг сетки рельефа, м. Меньше — детальнее и тяжелее.")]
        [SerializeField] float cellSize = 24f;
        [Tooltip("Высота ближних холмов, м.")]
        [SerializeField] float nearHeight = 70f;
        [Tooltip("Высота гор у горизонта, м.")]
        [SerializeField] float farHeight = 300f;
        [Tooltip("Размер холмов по горизонтали, м.")]
        [SerializeField] float featureSize = 380f;
        [Tooltip("Масштаб перепадов высоты между холмами, м.")]
        [FormerlySerializedAs("ridgeSpacing")]
        [SerializeField] float reliefScale = 230f;
        /// <summary>
        /// Высота рельефа там, где холмов нет (под трассой). Глубоко: под трассой есть низины — ущелья до −8 м
        /// и русла рек до −2.6 м; раньше подложка лежала на −0.7 и вылезала в них зелёным полом.
        /// Сверху её всё равно закрывает земля трассы (плиты на 260 м вокруг, холмы — с 90 м).
        /// </summary>
        const float belowGround = -12f;

        [Header("Лес на склонах")]
        [SerializeField] int treeCount = 2600;
        [SerializeField] float treeMinDistance = 120f;
        [SerializeField] float treeMaxDistance = 1100f;
        [SerializeField] Vector2 treeHeight = new Vector2(7f, 15f);
        [Tooltip("Доля площади под лесом (пятнами).")]
        [SerializeField, Range(0f, 1f)] float forestCoverage = 0.5f;
        [Tooltip("Выше этой высоты деревьев нет (там камень и снег), м.")]
        [SerializeField] float treeLine = 140f;
        [Tooltip("Тени от дальних деревьев: красиво, но дорого.")]
        [SerializeField] bool treeShadows = false;

        readonly List<GameObject> chunks = new List<GameObject>();
        Matrix4x4[] trees = new Matrix4x4[0];
        int treeTotal;
        Bounds area;
        Mesh generatedTree;
        Material fallbackTerrain, fallbackCrown, fallbackTrunk;

        // Сетка
        float[] heights, distances;
        int cols, rows;
        Vector2 origin;
        float noiseX, noiseZ, forestX, forestZ;

        const int ChunkCells = 64;

        void OnEnable() => Instance = this;

        void OnDisable()
        {
            if (Instance == this) Instance = null;
            if (!Application.isPlaying) Clear();   // предпросмотр в редакторе не копим
        }

        void OnDestroy() => Clear();

        // ============================ Постройка ============================

        /// <summary>Построить задник вокруг точек дороги. Повторный вызов перестраивает.</summary>
        public void Build(IEnumerable<Vector3> roadPoints, int seed)
        {
            Clear();

            // Прореживаем точки: для поля расстояний хватит одной на ~15 м.
            var pts = new List<Vector2>();
            foreach (var p in roadPoints)
            {
                var v = new Vector2(p.x, p.z);
                if (pts.Count == 0 || (v - pts[pts.Count - 1]).sqrMagnitude > 225f) pts.Add(v);
            }
            if (pts.Count == 0) pts.Add(new Vector2(transform.position.x, transform.position.z));

            Vector2 min = pts[0], max = pts[0];
            foreach (var v in pts) { min = Vector2.Min(min, v); max = Vector2.Max(max, v); }
            min -= Vector2.one * extent;
            max += Vector2.one * extent;

            float cell = Mathf.Max(4f, cellSize);
            // Не больше ~300 тыс. вершин, иначе крупнее шаг.
            while ((max.x - min.x) / cell * (max.y - min.y) / cell > 300000f) cell *= 1.25f;

            cols = Mathf.CeilToInt((max.x - min.x) / cell) + 1;
            rows = Mathf.CeilToInt((max.y - min.y) / cell) + 1;
            origin = min;

            var rng = new System.Random(seed ^ 0x5EED);
            noiseX = rng.Next(-20000, 20000);
            noiseZ = rng.Next(-20000, 20000);
            forestX = rng.Next(-20000, 20000);
            forestZ = rng.Next(-20000, 20000);

            heights = new float[cols * rows];
            distances = new float[cols * rows];
            for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                float x = origin.x + c * cell, z = origin.y + r * cell;
                float best = float.MaxValue;
                var here = new Vector2(x, z);
                for (int i = 0; i < pts.Count; i++)
                {
                    float d2 = (pts[i] - here).sqrMagnitude;
                    if (d2 < best) best = d2;
                }
                float d = Mathf.Sqrt(best);
                distances[r * cols + c] = d;
                heights[r * cols + c] = Height(x, z, d);
            }

            var mat = terrainMaterial != null ? terrainMaterial : FallbackTerrain();
            mat = Tinted(mat);
            for (int r0 = 0; r0 < rows - 1; r0 += ChunkCells)
            for (int c0 = 0; c0 < cols - 1; c0 += ChunkCells)
                BuildChunk(c0, r0, Mathf.Min(ChunkCells, cols - 1 - c0), Mathf.Min(ChunkCells, rows - 1 - r0), cell, mat);

            area = new Bounds(new Vector3((min.x + max.x) / 2f, farHeight / 2f, (min.y + max.y) / 2f),
                              new Vector3(max.x - min.x, farHeight * 2f + 50f, max.y - min.y));
            if (Look != Biome.Desert) ScatterTrees(seed, cell);   // в пустыне леса на холмах нет
        }

        /// <summary>Материал холмов в цветах выбранного биома (копия; исходный не трогаем).</summary>
        Material Tinted(Material src)
        {
            if (Look == null || Look == Biome.Forest || Look == Biome.Mountains) return src;
            if (lookMaterial == null || lookFor != Look)
            {
                if (lookMaterial != null) Destroy(lookMaterial);
                lookMaterial = new Material(src) { name = src.name + "_" + Look };
                lookFor = Look;
                if (Look == Biome.Desert)
                {
                    lookMaterial.SetColor("_GrassColor", new Color(0.86f, 0.70f, 0.44f));
                    lookMaterial.SetColor("_DryGrassColor", new Color(0.93f, 0.80f, 0.55f));
                    lookMaterial.SetColor("_RockColor", new Color(0.72f, 0.42f, 0.28f));
                    lookMaterial.SetColor("_SnowColor", new Color(0.95f, 0.84f, 0.62f));
                }
                else
                {
                    lookMaterial.SetColor("_GrassColor", new Color(0.90f, 0.93f, 0.98f));
                    lookMaterial.SetColor("_DryGrassColor", new Color(0.84f, 0.88f, 0.95f));
                }
            }
            return lookMaterial;
        }

        /// <summary>Высота рельефа в точке, d — расстояние до дороги.</summary>
        float Height(float x, float z, float d)
        {
            float mask = Smooth01((d - clearDistance) / Mathf.Max(1f, riseDistance));
            if (mask <= 0f) return belowGround;

            float far = Smooth01((d - clearDistance - riseDistance) / Mathf.Max(1f, extent * 0.7f));
            float amp = Mathf.Lerp(nearHeight, farHeight, far);

            float s = 1f / Mathf.Max(10f, featureSize);
            float rolling = Fbm((x + noiseX) * s, (z + noiseZ) * s, 4);
            float ridged = Ridged((x - noiseZ) * s * 0.8f, (z + noiseX) * s * 0.8f, 3);
            float shape = Mathf.Lerp(rolling, ridged, far * 0.75f);

            // Перепады высоты зависят от координат, поэтому гряды не повторяют контур дороги.
            float reliefFrequency = 1f / Mathf.Max(30f, reliefScale);
            float relief = Fbm((x - noiseX) * reliefFrequency, (z - noiseZ) * reliefFrequency, 2);
            float variation = Mathf.Lerp(0.44f, 1f, relief);

            float h = amp * shape * variation;
            return Mathf.Lerp(belowGround, h, mask);
        }

        void BuildChunk(int c0, int r0, int cw, int rh, float cell, Material mat)
        {
            int vc = cw + 1, vr = rh + 1;
            var verts = new Vector3[vc * vr];
            var normals = new Vector3[vc * vr];
            for (int r = 0; r < vr; r++)
            for (int c = 0; c < vc; c++)
            {
                int gc = c0 + c, gr = r0 + r;
                float h = heights[gr * cols + gc];
                verts[r * vc + c] = new Vector3(origin.x + gc * cell, h, origin.y + gr * cell);
                float hl = heights[gr * cols + Mathf.Max(0, gc - 1)];
                float hr = heights[gr * cols + Mathf.Min(cols - 1, gc + 1)];
                float hd = heights[Mathf.Max(0, gr - 1) * cols + gc];
                float hu = heights[Mathf.Min(rows - 1, gr + 1) * cols + gc];
                normals[r * vc + c] = new Vector3(hl - hr, 2f * cell, hd - hu).normalized;
            }

            var tris = new int[cw * rh * 6];
            int t = 0;
            for (int r = 0; r < rh; r++)
            for (int c = 0; c < cw; c++)
            {
                int a = r * vc + c, b = a + 1, d = a + vc, e = d + 1;
                // Ровная подложка под трассой не нужна (её всё равно закрывает земля трассы, а в ущелья и русла
                // она бы вылезала) — квадраты, где холмов нет, не рисуем вовсе.
                if (verts[a].y <= belowGround + 0.01f && verts[b].y <= belowGround + 0.01f
                    && verts[d].y <= belowGround + 0.01f && verts[e].y <= belowGround + 0.01f) continue;
                // Диагональ через одну — грани не выстраиваются в «ёлочку».
                if (((c0 + c) + (r0 + r)) % 2 == 0)
                {
                    tris[t++] = a; tris[t++] = d; tris[t++] = e;
                    tris[t++] = a; tris[t++] = e; tris[t++] = b;
                }
                else
                {
                    tris[t++] = a; tris[t++] = d; tris[t++] = b;
                    tris[t++] = b; tris[t++] = d; tris[t++] = e;
                }
            }

            if (t < tris.Length) System.Array.Resize(ref tris, t);
            if (t == 0) return;   // весь кусок ровный — рисовать нечего
            var mesh = new Mesh { name = "Задник_холмы" };
            mesh.indexFormat = vc * vr > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.vertices = verts;
            mesh.normals = normals;
            mesh.triangles = tris;
            mesh.RecalculateBounds();

            var go = new GameObject("Холмы_" + c0 + "_" + r0);
            go.hideFlags = HideFlags.DontSave;   // строится заново по сиду, в сцену не сохраняем
            go.transform.SetParent(transform, false);
            go.transform.position = Vector3.zero;
            go.transform.rotation = Quaternion.identity;
            go.isStatic = true;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = ShadowCastingMode.On;
            mr.receiveShadows = true;
            chunks.Add(go);
        }

        // ============================ Лес ============================

        void ScatterTrees(int seed, float cell)
        {
            treeTotal = 0;
            if (treeCount <= 0) { trees = new Matrix4x4[0]; return; }
            trees = new Matrix4x4[treeCount];
            var rng = new System.Random(seed * 31 + 7);
            int attempts = treeCount * 6;
            float w = (cols - 1) * cell, hgt = (rows - 1) * cell;
            float forestScale = 1f / 220f;

            for (int i = 0; i < attempts && treeTotal < treeCount; i++)
            {
                float x = origin.x + (float)rng.NextDouble() * w;
                float z = origin.y + (float)rng.NextDouble() * hgt;
                float d = Sample(distances, x, z, cell);
                if (d < treeMinDistance || d > treeMaxDistance) continue;

                // Лес пятнами, и гуще ближе к дороге — ближние склоны выглядят «живыми».
                float forest = Mathf.PerlinNoise((x + forestX) * forestScale, (z + forestZ) * forestScale);
                float nearBias = 1f - Mathf.InverseLerp(treeMinDistance, treeMaxDistance, d) * 0.35f;
                if (forest * nearBias < 1f - forestCoverage) continue;

                float y = Sample(heights, x, z, cell);
                if (y > treeLine || y < 0.5f) continue;
                float slope = Slope(x, z, cell);
                if (slope > 0.55f) continue;   // на крутом — камень, деревьев нет

                float size = Mathf.Lerp(treeHeight.x, treeHeight.y, (float)rng.NextDouble());
                float girth = size * Mathf.Lerp(0.75f, 1.05f, (float)rng.NextDouble());
                var rot = Quaternion.Euler(0f, (float)rng.NextDouble() * 360f, 0f);
                trees[treeTotal++] = Matrix4x4.TRS(new Vector3(x, y - 0.3f, z), rot, new Vector3(girth, size, girth));
            }
        }

        void Update()
        {
            if (treeTotal == 0) return;

            Mesh mesh = treeMesh;
            Material[] mats = treeMeshMaterials;
            if (mesh == null || mats == null || mats.Length == 0)
            {
                mesh = GeneratedTree();
                mats = new[] { treeCrownMaterial != null ? treeCrownMaterial : FallbackCrown(),
                               treeTrunkMaterial != null ? treeTrunkMaterial : FallbackTrunk() };
            }

            for (int sub = 0; sub < mesh.subMeshCount && sub < mats.Length; sub++)
            {
                var mat = mats[sub];
                if (mat == null) continue;
                if (!mat.enableInstancing) mat.enableInstancing = true;
                var rp = new RenderParams(mat)
                {
                    shadowCastingMode = treeShadows ? ShadowCastingMode.On : ShadowCastingMode.Off,
                    receiveShadows = true,
                    worldBounds = area,
                    layer = gameObject.layer,
                };
                for (int start = 0; start < treeTotal; start += 1023)
                    Graphics.RenderMeshInstanced(rp, mesh, sub, trees, Mathf.Min(1023, treeTotal - start), start);
            }
        }

        // ============================ Предпросмотр в редакторе ============================

        [ContextMenu("Предпросмотр: построить вокруг прямой дороги")]
        void PreviewInEditor()
        {
            var pts = new List<Vector3>();
            for (float z = -40f; z <= 600f; z += 10f) pts.Add(transform.position + new Vector3(Mathf.Sin(z * 0.01f) * 60f, 0f, z));
            Build(pts, 12345);
        }

        [ContextMenu("Предпросмотр: убрать")]
        public void Clear()
        {
            foreach (var go in chunks)
            {
                if (go == null) continue;
                var mf = go.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null) DestroySafe(mf.sharedMesh);
                DestroySafe(go);
            }
            chunks.Clear();
            treeTotal = 0;
        }

        // ============================ Утилиты ============================

        float Sample(float[] grid, float x, float z, float cell)
        {
            float fx = Mathf.Clamp((x - origin.x) / cell, 0f, cols - 1.001f);
            float fz = Mathf.Clamp((z - origin.y) / cell, 0f, rows - 1.001f);
            int c = (int)fx, r = (int)fz;
            float tx = fx - c, tz = fz - r;
            float a = grid[r * cols + c], b = grid[r * cols + c + 1];
            float d = grid[(r + 1) * cols + c], e = grid[(r + 1) * cols + c + 1];
            return Mathf.Lerp(Mathf.Lerp(a, b, tx), Mathf.Lerp(d, e, tx), tz);
        }

        float Slope(float x, float z, float cell)
        {
            float dx = Sample(heights, x + cell, z, cell) - Sample(heights, x - cell, z, cell);
            float dz = Sample(heights, x, z + cell, cell) - Sample(heights, x, z - cell, cell);
            var n = new Vector3(-dx, 2f * cell, -dz).normalized;
            return 1f - n.y;
        }

        static float Smooth01(float t)
        {
            t = Mathf.Clamp01(t);
            return t * t * (3f - 2f * t);
        }

        static float Fbm(float x, float z, int octaves)
        {
            float sum = 0f, amp = 1f, norm = 0f;
            for (int i = 0; i < octaves; i++)
            {
                sum += Mathf.PerlinNoise(x, z) * amp;
                norm += amp;
                amp *= 0.5f;
                x = x * 2.03f + 17.1f;
                z = z * 2.03f - 9.7f;
            }
            return sum / norm;
        }

        static float Ridged(float x, float z, int octaves)
        {
            float sum = 0f, amp = 1f, norm = 0f;
            for (int i = 0; i < octaves; i++)
            {
                float r = 1f - Mathf.Abs(Mathf.PerlinNoise(x, z) * 2f - 1f);
                sum += r * r * amp;
                norm += amp;
                amp *= 0.5f;
                x = x * 2.1f + 5.3f;
                z = z * 2.1f + 11.9f;
            }
            return sum / norm;
        }

        static void DestroySafe(Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) Destroy(o); else DestroyImmediate(o);
        }

        Material FallbackTerrain()
        {
            if (fallbackTerrain != null) return fallbackTerrain;
            var shader = Shader.Find("JunkRace/Toon Terrain");
            fallbackTerrain = new Material(shader != null ? shader : Shader.Find("Universal Render Pipeline/Lit"));
            return fallbackTerrain;
        }

        Material FallbackCrown() => fallbackCrown != null ? fallbackCrown : fallbackCrown = ToonMaterial(new Color(0.22f, 0.38f, 0.24f));
        Material FallbackTrunk() => fallbackTrunk != null ? fallbackTrunk : fallbackTrunk = ToonMaterial(new Color(0.38f, 0.27f, 0.18f));

        static Material ToonMaterial(Color c)
        {
            var shader = Shader.Find("JunkRace/Toon");
            var m = new Material(shader != null ? shader : Shader.Find("Universal Render Pipeline/Lit")) { color = c, enableInstancing = true };
            return m;
        }

        /// <summary>Ёлка из трёх конусов на стволе: высота 1, низ на нуле. Сабмеш 0 — хвоя, 1 — ствол.</summary>
        Mesh GeneratedTree()
        {
            if (generatedTree != null) return generatedTree;
            var verts = new List<Vector3>();
            var crown = new List<int>();
            var trunk = new List<int>();

            Prism(verts, trunk, 5, 0.06f, 0f, 0.26f);
            Cone(verts, crown, 7, 0.40f, 0.18f, 0.46f);
            Cone(verts, crown, 7, 0.31f, 0.42f, 0.38f);
            Cone(verts, crown, 7, 0.21f, 0.64f, 0.36f);

            generatedTree = new Mesh { name = "Ёлка_задник" };
            generatedTree.SetVertices(verts);
            generatedTree.subMeshCount = 2;
            generatedTree.SetTriangles(crown, 0);
            generatedTree.SetTriangles(trunk, 1);
            generatedTree.RecalculateNormals();   // вершины не общие — грани плоские, low-poly
            generatedTree.RecalculateBounds();
            return generatedTree;
        }

        static void Cone(List<Vector3> v, List<int> t, int sides, float radius, float baseY, float height)
        {
            var top = new Vector3(0f, baseY + height, 0f);
            for (int i = 0; i < sides; i++)
            {
                float a0 = i * Mathf.PI * 2f / sides, a1 = (i + 1) * Mathf.PI * 2f / sides;
                var p0 = new Vector3(Mathf.Cos(a0) * radius, baseY, Mathf.Sin(a0) * radius);
                var p1 = new Vector3(Mathf.Cos(a1) * radius, baseY, Mathf.Sin(a1) * radius);
                int s = v.Count;
                v.Add(p0); v.Add(top); v.Add(p1);            // бок
                t.Add(s); t.Add(s + 1); t.Add(s + 2);
                s = v.Count;
                v.Add(p0); v.Add(p1); v.Add(new Vector3(0f, baseY, 0f));   // дно
                t.Add(s); t.Add(s + 1); t.Add(s + 2);
            }
        }

        static void Prism(List<Vector3> v, List<int> t, int sides, float radius, float y0, float y1)
        {
            for (int i = 0; i < sides; i++)
            {
                float a0 = i * Mathf.PI * 2f / sides, a1 = (i + 1) * Mathf.PI * 2f / sides;
                var b0 = new Vector3(Mathf.Cos(a0) * radius, y0, Mathf.Sin(a0) * radius);
                var b1 = new Vector3(Mathf.Cos(a1) * radius, y0, Mathf.Sin(a1) * radius);
                var u0 = b0 + Vector3.up * (y1 - y0);
                var u1 = b1 + Vector3.up * (y1 - y0);
                int s = v.Count;
                v.Add(b0); v.Add(u0); v.Add(u1); v.Add(b1);
                t.Add(s); t.Add(s + 1); t.Add(s + 2);
                t.Add(s); t.Add(s + 2); t.Add(s + 3);
            }
        }
    }
}
