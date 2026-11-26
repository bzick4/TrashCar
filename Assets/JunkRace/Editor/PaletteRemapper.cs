// JunkRace → Палитра → Перекрасить в палитру
// Делает КОПИИ выделенных префабов/моделей, у которых все треугольники
// смотрят в клетки JunkPalette.png, и вешает один общий материал.
// Оригиналы не трогаются. Результат: Assets/JunkRace/Art/Palettized/
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace JunkRace.EditorTools
{
    public class PaletteRemapper : EditorWindow
    {
        const string DefaultPalettePath = "Assets/JunkRace/Art/Palette/JunkPalette.png";
        const string DefaultMaterialPath = "Assets/JunkRace/Art/Palette/JunkPalette.mat";

        static readonly string[] RowNames =
        {
            "1 · Металл и ржавчина", "2 · Выцветшая краска", "3 · Природа и земля",
            "4 · Резина и нейтральные", "5 · Кожа и волосы", "6 · Цвета игроков",
            "7 · Дороги", "8 · Пейзаж"
        };

        Texture2D palette;
        Material paletteMaterial;
        int columns = 8, rows = 8;
        bool[] useRow = { true, true, true, true, false, false, true, true };
        int sampleRes = 128;
        string outFolder = "Assets/JunkRace/Art/Palettized";
        Vector2 scroll;
        readonly List<string> report = new List<string>();

        [MenuItem("JunkRace/Палитра/Перекрасить в палитру…")]
        static void Open() => GetWindow<PaletteRemapper>("Перекраска в палитру");

        void OnEnable()
        {
            if (!palette) palette = AssetDatabase.LoadAssetAtPath<Texture2D>(DefaultPalettePath);
            if (!paletteMaterial) paletteMaterial = AssetDatabase.LoadAssetAtPath<Material>(DefaultMaterialPath);
        }

        void OnGUI()
        {
            EditorGUILayout.HelpBox(
                "Выдели в Project префабы, модели или папки и нажми кнопку. " +
                "Оригиналы не меняются — рядом появятся копии *_Palette.", MessageType.Info);

            palette = (Texture2D)EditorGUILayout.ObjectField("Палитра", palette, typeof(Texture2D), false);
            paletteMaterial = (Material)EditorGUILayout.ObjectField(
                new GUIContent("Материал", "Пусто — создам JunkPalette.mat рядом с палитрой"),
                paletteMaterial, typeof(Material), false);

            using (new EditorGUILayout.HorizontalScope())
            {
                columns = EditorGUILayout.IntField("Колонок", columns);
                rows = EditorGUILayout.IntField("Рядов", rows);
            }

            sampleRes = EditorGUILayout.IntPopup(
                "Точность",
                sampleRes, new[] { "64", "128", "256", "512" }, new[] { 64, 128, 256, 512 });
            EditorGUILayout.HelpBox("64 — для больших текстур (усредняет цвет). 256–512 — для паков, которые уже на палитре (Kenney).", MessageType.None);

            outFolder = EditorGUILayout.TextField("Куда сохранять", outFolder);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Какие ряды палитры можно подбирать", EditorStyles.boldLabel);
            if (useRow.Length != rows) System.Array.Resize(ref useRow, Mathf.Max(1, rows));
            for (int r = 0; r < useRow.Length; r++)
                useRow[r] = EditorGUILayout.ToggleLeft(r < RowNames.Length ? RowNames[r] : $"Ряд {r + 1}", useRow[r]);
            EditorGUILayout.HelpBox(
                "Кожу и цвета игроков лучше выключать для хлама — иначе ржавая бочка " +
                "внезапно станет «телесной». Для персонажей — включай.", MessageType.None);

            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(!palette))
            {
                if (GUILayout.Button("Перекрасить выделенное", GUILayout.Height(32)))
                    RunOnSelection();
            }

            if (report.Count > 0)
            {
                EditorGUILayout.Space();
                scroll = EditorGUILayout.BeginScrollView(scroll);
                foreach (var line in report) EditorGUILayout.LabelField(line, EditorStyles.wordWrappedMiniLabel);
                EditorGUILayout.EndScrollView();
            }
        }

        // ───────────────────────── запуск ─────────────────────────

        void RunOnSelection()
        {
            report.Clear();
            var targets = CollectTargets();
            if (targets.Count == 0) { report.Add("Ничего не выделено (нужны префабы, модели или папки)."); return; }

            var pal = new PaletteLookup(palette, columns, rows, useRow);
            if (pal.Count == 0) { report.Add("Все ряды выключены — не из чего подбирать."); return; }

            var mat = EnsureMaterial();
            EnsureFolder(outFolder);
            EnsureFolder(outFolder + "/Meshes");

            var samplers = new Dictionary<Material, TexSampler>();
            var madeReadable = MakeReadable(targets);   // временно включаем Read/Write у моделей
            try
            {
                for (int i = 0; i < targets.Count; i++)
                {
                    var path = targets[i];
                    EditorUtility.DisplayProgressBar("Перекраска в палитру", Path.GetFileName(path), (float)i / targets.Count);
                    var src = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                    if (src) ProcessAsset(src, pal, mat, samplers, sampleRes);
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                foreach (var s in samplers.Values) s?.Dispose();
                AssetDatabase.SaveAssets();
                foreach (var imp in madeReadable) { imp.isReadable = false; imp.SaveAndReimport(); }
                AssetDatabase.Refresh();
            }
            report.Insert(0, $"Готово: {targets.Count} шт. → {outFolder}");
        }

        List<string> CollectTargets()
        {
            var result = new List<string>();
            foreach (var obj in Selection.GetFiltered<Object>(SelectionMode.Assets))
            {
                var path = AssetDatabase.GetAssetPath(obj);
                if (string.IsNullOrEmpty(path)) continue;
                if (AssetDatabase.IsValidFolder(path))
                {
                    foreach (var guid in AssetDatabase.FindAssets("t:GameObject", new[] { path }))
                        AddTarget(result, AssetDatabase.GUIDToAssetPath(guid));
                }
                else if (obj is GameObject) AddTarget(result, path);
            }
            return result;
        }

        void AddTarget(List<string> list, string path)
        {
            if (path.StartsWith(outFolder)) return;          // не перекрашиваем свои же результаты
            if (!list.Contains(path)) list.Add(path);
        }

        // ───────────────────────── один ассет ─────────────────────────

        static List<ModelImporter> MakeReadable(List<string> targets)
        {
            var changed = new List<ModelImporter>();
            foreach (var path in targets)
            {
                var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (!go) continue;
                var meshes = new List<Mesh>();
                foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true)) if (mf.sharedMesh) meshes.Add(mf.sharedMesh);
                foreach (var sm in go.GetComponentsInChildren<SkinnedMeshRenderer>(true)) if (sm.sharedMesh) meshes.Add(sm.sharedMesh);
                foreach (var mesh in meshes)
                {
                    if (mesh.isReadable) continue;
                    var imp = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(mesh)) as ModelImporter;
                    if (imp == null || imp.isReadable || changed.Contains(imp)) continue;
                    imp.isReadable = true;
                    imp.SaveAndReimport();
                    changed.Add(imp);
                }
            }
            return changed;
        }

        void ProcessAsset(GameObject src, PaletteLookup pal, Material mat, Dictionary<Material, TexSampler> samplers, int res)
        {
            var inst = Instantiate(src);
            inst.name = src.name;
            var meshCache = new Dictionary<string, Mesh>();
            int tris = 0;
            var used = new HashSet<int>();

            try
            {
                foreach (var r in inst.GetComponentsInChildren<Renderer>(true))
                {
                    Mesh mesh = null;
                    var mf = r.GetComponent<MeshFilter>();
                    var smr = r as SkinnedMeshRenderer;
                    if (r is MeshRenderer && mf) mesh = mf.sharedMesh;
                    else if (smr) mesh = smr.sharedMesh;
                    if (!mesh) continue;

                    var mats = r.sharedMaterials;
                    string key = mesh.GetInstanceID().ToString();
                    foreach (var m in mats) key += "_" + (m ? m.GetInstanceID() : 0);

                    if (!meshCache.TryGetValue(key, out var nm))
                    {
                        nm = Remap(mesh, mats, pal, samplers, res, used, ref tris);
                        if (!nm) continue;
                        nm.name = $"{src.name}_{mesh.name}_pal";
                        var meshPath = AssetDatabase.GenerateUniqueAssetPath($"{outFolder}/Meshes/{Sanitize(nm.name)}.asset");
                        AssetDatabase.CreateAsset(nm, meshPath);
                        meshCache[key] = nm;
                    }

                    if (mf) mf.sharedMesh = nm;
                    if (smr) smr.sharedMesh = nm;
                    r.sharedMaterials = new[] { mat };
                }

                var prefabPath = AssetDatabase.GenerateUniqueAssetPath($"{outFolder}/{Sanitize(src.name)}_Palette.prefab");
                PrefabUtility.SaveAsPrefabAsset(inst, prefabPath);
                report.Add($"✔ {src.name}: {tris} треуг., цветов палитры: {used.Count}");
            }
            catch (System.Exception e)
            {
                report.Add($"✖ {src.name}: {e.Message}");
                Debug.LogException(e);
            }
            finally
            {
                DestroyImmediate(inst);
            }
        }

        // ───────────────────────── перекраска меша ─────────────────────────

        static Mesh Remap(Mesh src, Material[] mats, PaletteLookup pal,
                          Dictionary<Material, TexSampler> samplers, int res, HashSet<int> used, ref int triCount)
        {
            if (!src.isReadable)
            {
                Debug.LogWarning($"[Палитра] Меш {src.name} не читается — включи Read/Write в импорте модели.", src);
                return null;
            }

            var v = src.vertices;
            var n = src.normals;
            var t = src.tangents;
            var uv = src.uv;
            var col = src.colors;
            var bw = src.boneWeights;
            bool hasN = n.Length == v.Length, hasT = t.Length == v.Length, hasUV = uv.Length == v.Length;
            bool hasC = col.Length == v.Length, hasBW = bw.Length == v.Length;

            var oV = new List<Vector3>(); var oN = new List<Vector3>(); var oT = new List<Vector4>();
            var oUV = new List<Vector2>(); var oC = new List<Color>(); var oBW = new List<BoneWeight>();
            var srcIndex = new List<int>();
            var oTri = new List<int>();

            for (int sub = 0; sub < src.subMeshCount; sub++)
            {
                if (src.GetTopology(sub) != MeshTopology.Triangles) continue;
                var m = mats.Length > 0 ? mats[Mathf.Min(sub, mats.Length - 1)] : null;
                var tint = MainColor(m);
                TexSampler sampler = null;
                if (m && !samplers.TryGetValue(m, out sampler))
                {
                    sampler = TexSampler.Create(m, res);
                    samplers[m] = sampler;
                }

                var tris = src.GetTriangles(sub);
                for (int i = 0; i + 2 < tris.Length; i += 3)
                {
                    int a = tris[i], b = tris[i + 1], c = tris[i + 2];

                    Color color = tint;
                    if (sampler != null && hasUV)
                        color *= sampler.Sample((uv[a] + uv[b] + uv[c]) / 3f);

                    int idx = pal.Nearest(color);
                    used.Add(idx);
                    var cell = pal.CellUV(idx);

                    foreach (int s in new[] { a, b, c })
                    {
                        oTri.Add(oV.Count);
                        srcIndex.Add(s);
                        oV.Add(v[s]);
                        if (hasN) oN.Add(n[s]);
                        if (hasT) oT.Add(t[s]);
                        if (hasC) oC.Add(col[s]);
                        if (hasBW) oBW.Add(bw[s]);
                        oUV.Add(cell);
                    }
                    triCount++;
                }
            }

            var dst = new Mesh
            {
                indexFormat = oV.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16
            };
            dst.SetVertices(oV);
            if (hasN) dst.SetNormals(oN); else dst.RecalculateNormals();
            if (hasT) dst.SetTangents(oT);
            if (hasC) dst.SetColors(oC);
            dst.SetUVs(0, oUV);
            dst.SetTriangles(oTri, 0);

            if (hasBW)
            {
                dst.boneWeights = oBW.ToArray();
                dst.bindposes = src.bindposes;
            }
            CopyBlendShapes(src, dst, srcIndex);
            dst.RecalculateBounds();
            return dst;
        }

        static void CopyBlendShapes(Mesh src, Mesh dst, List<int> srcIndex)
        {
            int vc = src.vertexCount;
            var dv = new Vector3[vc]; var dn = new Vector3[vc]; var dt = new Vector3[vc];
            var ov = new Vector3[srcIndex.Count]; var on = new Vector3[srcIndex.Count]; var ot = new Vector3[srcIndex.Count];

            for (int s = 0; s < src.blendShapeCount; s++)
            {
                string name = src.GetBlendShapeName(s);
                for (int f = 0; f < src.GetBlendShapeFrameCount(s); f++)
                {
                    src.GetBlendShapeFrameVertices(s, f, dv, dn, dt);
                    for (int i = 0; i < srcIndex.Count; i++)
                    {
                        int k = srcIndex[i];
                        ov[i] = dv[k]; on[i] = dn[k]; ot[i] = dt[k];
                    }
                    dst.AddBlendShapeFrame(name, src.GetBlendShapeFrameWeight(s, f), ov, on, ot);
                }
            }
        }

        // ───────────────────────── материал ─────────────────────────

        Material EnsureMaterial()
        {
            if (paletteMaterial) return paletteMaterial;

            var shader = Shader.Find("JunkRace/Toon");
            if (!shader) shader = Shader.Find("Universal Render Pipeline/Lit");
            var mat = new Material(shader) { name = "JunkPalette" };
            var texProp = FindMainTextureProperty(mat);
            if (texProp != null) mat.SetTexture(texProp, palette);
            else Debug.LogWarning($"[Палитра] У шейдера {shader.name} нет главной текстуры — назначь палитру вручную.");
            var colProp = FindMainColorProperty(mat);
            if (colProp != null) mat.SetColor(colProp, Color.white);

            var dir = Path.GetDirectoryName(AssetDatabase.GetAssetPath(palette))?.Replace('\\', '/') ?? "Assets";
            var path = AssetDatabase.GenerateUniqueAssetPath(dir + "/JunkPalette.mat");
            AssetDatabase.CreateAsset(mat, path);
            paletteMaterial = mat;
            return mat;
        }

        static string FindMainTextureProperty(Material m)
        {
            if (!m || !m.shader) return null;
            var sh = m.shader;
            for (int i = 0; i < sh.GetPropertyCount(); i++)
                if ((sh.GetPropertyFlags(i) & ShaderPropertyFlags.MainTexture) != 0)
                    return sh.GetPropertyName(i);
            foreach (var p in new[] { "_BaseMap", "_MainTex", "_BaseColorMap", "_Albedo", "_MainTexture", "_Texture" })
                if (m.HasProperty(p) && sh.FindPropertyIndex(p) >= 0 &&
                    sh.GetPropertyType(sh.FindPropertyIndex(p)) == ShaderPropertyType.Texture)
                    return p;
            return null;
        }

        static string FindMainColorProperty(Material m)
        {
            if (!m || !m.shader) return null;
            var sh = m.shader;
            for (int i = 0; i < sh.GetPropertyCount(); i++)
                if ((sh.GetPropertyFlags(i) & ShaderPropertyFlags.MainColor) != 0)
                    return sh.GetPropertyName(i);
            foreach (var p in new[] { "_BaseColor", "_Color", "_MainColor", "_Tint" })
                if (m.HasProperty(p) && sh.FindPropertyIndex(p) >= 0 &&
                    sh.GetPropertyType(sh.FindPropertyIndex(p)) == ShaderPropertyType.Color)
                    return p;
            return null;
        }

        static Color MainColor(Material m)
        {
            var p = FindMainColorProperty(m);
            return p != null ? m.GetColor(p) : Color.white;
        }

        // ───────────────────────── утилиты ─────────────────────────

        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            var parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }

        static string Sanitize(string s)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s;
        }

        /// <summary>Читает любую текстуру (даже без Read/Write) через уменьшенную копию.</summary>
        static Texture2D ReadDownscaled(Texture src, int w, int h)
        {
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var prev = RenderTexture.active;
            Graphics.Blit(src, rt);
            RenderTexture.active = rt;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false, false) { wrapMode = TextureWrapMode.Repeat };
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            return tex;
        }

        // ───────────────────────── чтение текстуры материала ─────────────────────────

        class TexSampler : System.IDisposable
        {
            Texture2D small;
            Vector2 scale = Vector2.one, offset;

            public static TexSampler Create(Material m, int res)
            {
                var prop = FindMainTextureProperty(m);
                if (prop == null) return null;
                var tex = m.GetTexture(prop);
                if (!tex) return null;

                res = Mathf.Max(8, res);
                int w = Mathf.Min(res, tex.width), h = Mathf.Min(res, tex.height);
                return new TexSampler
                {
                    small = ReadDownscaled(tex, w, h),
                    scale = m.GetTextureScale(prop),
                    offset = m.GetTextureOffset(prop)
                };
            }

            public Color Sample(Vector2 uv)
            {
                uv = Vector2.Scale(uv, scale) + offset;
                uv.x -= Mathf.Floor(uv.x);
                uv.y -= Mathf.Floor(uv.y);
                return small.GetPixelBilinear(uv.x, uv.y);
            }

            public void Dispose()
            {
                if (small) DestroyImmediate(small);
            }
        }

        // ───────────────────────── палитра + подбор цвета (Lab) ─────────────────────────

        class PaletteLookup
        {
            readonly List<Vector3> lab = new List<Vector3>();
            readonly List<int> cellIndex = new List<int>();
            readonly int columns, rows;

            public int Count => lab.Count;

            public PaletteLookup(Texture2D palette, int columns, int rows, bool[] useRow)
            {
                this.columns = Mathf.Max(1, columns);
                this.rows = Mathf.Max(1, rows);
                var read = ReadDownscaled(palette, palette.width, palette.height);
                for (int r = 0; r < this.rows; r++)
                {
                    if (r < useRow.Length && !useRow[r]) continue;
                    for (int c = 0; c < this.columns; c++)
                    {
                        // ряд 0 — верх картинки; ReadPixels хранит низ в y=0
                        int px = (int)((c + 0.5f) / this.columns * read.width);
                        int py = (int)((1f - (r + 0.5f) / this.rows) * read.height);
                        lab.Add(ToLab(read.GetPixel(px, py)));
                        cellIndex.Add(r * this.columns + c);
                    }
                }
                DestroyImmediate(read);
            }

            public int Nearest(Color srgb)
            {
                var q = ToLab(srgb);
                int best = 0; float bestD = float.MaxValue;
                for (int i = 0; i < lab.Count; i++)
                {
                    float d = (lab[i] - q).sqrMagnitude;
                    if (d < bestD) { bestD = d; best = i; }
                }
                return cellIndex[best];
            }

            public Vector2 CellUV(int index)
            {
                int c = index % columns, r = index / columns;
                return new Vector2((c + 0.5f) / columns, 1f - (r + 0.5f) / rows);
            }

            static Vector3 ToLab(Color c)
            {
                float R = Lin(c.r), G = Lin(c.g), B = Lin(c.b);
                float x = (R * 0.4124f + G * 0.3576f + B * 0.1805f) / 0.95047f;
                float y = (R * 0.2126f + G * 0.7152f + B * 0.0722f);
                float z = (R * 0.0193f + G * 0.1192f + B * 0.9505f) / 1.08883f;
                x = F(x); y = F(y); z = F(z);
                return new Vector3(116f * y - 16f, 500f * (x - y), 200f * (y - z));
            }

            static float Lin(float v) => v <= 0.04045f ? v / 12.92f : Mathf.Pow((v + 0.055f) / 1.055f, 2.4f);
            static float F(float t) => t > 0.008856f ? Mathf.Pow(t, 1f / 3f) : 7.787f * t + 16f / 116f;
        }
    }

    /// <summary>Палитра должна быть чёткой: без фильтрации, мипов и сжатия.</summary>
    class PaletteTextureImporter : AssetPostprocessor
    {
        void OnPreprocessTexture()
        {
            if (!assetPath.Contains("/Art/Palette/")) return;
            var ti = (TextureImporter)assetImporter;
            ti.textureType = TextureImporterType.Default;
            ti.sRGBTexture = true;
            ti.mipmapEnabled = false;
            ti.filterMode = FilterMode.Point;
            ti.wrapMode = TextureWrapMode.Clamp;
            ti.npotScale = TextureImporterNPOTScale.None;
            ti.textureCompression = TextureImporterCompression.Uncompressed;
            ti.isReadable = false;
        }
    }
}
