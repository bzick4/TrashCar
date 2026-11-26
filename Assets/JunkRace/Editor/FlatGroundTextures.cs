using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace JunkRace.EditorTools
{
    /// <summary>
    /// Общий стиль земли: вместо фотографических текстур дороги, откосов и полей — плоские цвета из палитры JunkPalette.
    /// Цвет каждой текстуры — ближайший цвет палитры к её среднему цвету, поэтому биомы сохраняют свой оттенок.
    /// Переходы между биомами — плавный градиент между цветами верха и низа исходной картинки.
    /// </summary>
    public static class FlatGroundTextures
    {
        const string PalettePath = "Assets/JunkRace/Art/Palette/JunkPalette.png";
        const string TexturesFolder = "Assets/Prefab/Route/Textures";
        const int PaletteCells = 8;   // палитра 8×8 клеток

        [MenuItem("JunkRace/Палитра/Насыщенность палитры +30%")]
        public static void BoostPaletteSaturation() => BoostSaturation(PalettePath, 1.3f);

        /// <summary>Насыщенность каждого пикселя картинки × factor (по HSV), файл перезаписывается.</summary>
        public static void BoostSaturation(string path, float factor)
        {
            var tex = Load(path);
            var px = tex.GetPixels();
            for (int i = 0; i < px.Length; i++)
            {
                Color.RGBToHSV(px[i], out float h, out float s, out float v);
                px[i] = Color.HSVToRGB(h, Mathf.Clamp01(s * factor), v);
            }
            tex.SetPixels(px);
            Save(tex, path);
            Object.DestroyImmediate(tex);
            Debug.Log($"[JunkRace] Насыщенность палитры ×{factor}: {path}");
        }

        [MenuItem("JunkRace/Палитра/Плоские текстуры дороги и полей")]
        public static void MakeFlatGroundTextures()
        {
            var palette = ReadPalette();
            int done = 0;
            foreach (var guid in AssetDatabase.FindAssets("t:Texture2D", new[] { TexturesFolder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                string name = Path.GetFileNameWithoutExtension(path);
                if (name == "junky") continue;   // земля свалки — отдельно
                var src = Load(path);
                Texture2D dst;
                if (name.StartsWith("transition_"))
                {
                    // Верх и низ картинки — разные биомы: градиент между их цветами (ориентация как у исходника).
                    var top = Nearest(palette, Average(src, 0.8f, 1f));
                    var bottom = Nearest(palette, Average(src, 0f, 0.2f));
                    dst = new Texture2D(4, 64, TextureFormat.RGBA32, false);
                    for (int y = 0; y < 64; y++)
                    {
                        var c = Color.Lerp(bottom, top, Mathf.SmoothStep(0f, 1f, y / 63f));
                        for (int x = 0; x < 4; x++) dst.SetPixel(x, y, c);
                    }
                }
                else
                {
                    var c = Nearest(palette, Average(src, 0f, 1f));
                    dst = new Texture2D(4, 4, TextureFormat.RGBA32, false);
                    var fill = new Color[16];
                    for (int i = 0; i < fill.Length; i++) fill[i] = c;
                    dst.SetPixels(fill);
                }
                Object.DestroyImmediate(src);
                Save(dst, path);
                Object.DestroyImmediate(dst);
                done++;
            }
            Debug.Log($"[JunkRace] Плоские текстуры земли: {done} шт. в {TexturesFolder}");
        }

        const string DesertPalettePath = "Assets/JunkRace/Art/Palette/JunkPalette_Desert.png";
        const string PaletteMaterialPath = "Assets/JunkRace/Art/Palette/JunkPalette.mat";
        const string DesertMaterialPath = "Assets/JunkRace/Art/Palette/JunkPalette_Desert.mat";

        /// <summary>
        /// Пустынная палитра: копия JunkPalette, где все зелёные цвета заменены на пыльно-рыжие (яркость сохраняется).
        /// Деревья и ёлки с этой палитрой выглядят выжженными солнцем. Пересоздаётся при каждом вызове,
        /// поэтому следует за правками основной палитры. Возвращает материал с пустынной палитрой.
        /// </summary>
        [MenuItem("JunkRace/Палитра/Пустынная палитра (зелёный → рыжий)")]
        public static Material MakeDesertPalette()
        {
            if (AssetDatabase.LoadAssetAtPath<Texture2D>(DesertPalettePath) == null)
                AssetDatabase.CopyAsset(PalettePath, DesertPalettePath);   // вместе с настройками импорта (без сглаживания)
            var tex = Load(PalettePath);
            var px = tex.GetPixels();
            for (int i = 0; i < px.Length; i++)
            {
                Color.RGBToHSV(px[i], out float h, out float sat, out float v);
                if (h < 0.17f || h > 0.47f || sat < 0.15f) continue;      // не зелёный
                px[i] = Color.HSVToRGB(0.075f, Mathf.Clamp(sat * 0.85f, 0.35f, 0.65f), Mathf.Clamp01(v * 1.05f + 0.05f));
            }
            tex.SetPixels(px);
            Save(tex, DesertPalettePath);
            Object.DestroyImmediate(tex);

            var mat = AssetDatabase.LoadAssetAtPath<Material>(DesertMaterialPath);
            if (mat == null)
            {
                AssetDatabase.CopyAsset(PaletteMaterialPath, DesertMaterialPath);
                mat = AssetDatabase.LoadAssetAtPath<Material>(DesertMaterialPath);
            }
            var desertTex = AssetDatabase.LoadAssetAtPath<Texture2D>(DesertPalettePath);
            if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", desertTex);
            if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", desertTex);
            EditorUtility.SetDirty(mat);
            AssetDatabase.SaveAssets();
            return mat;
        }

        // ---- помощники ----

        static Texture2D Load(string path)
        {
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            tex.LoadImage(File.ReadAllBytes(path));
            return tex;
        }

        static void Save(Texture2D tex, string path)
        {
            tex.Apply();
            File.WriteAllBytes(path, tex.EncodeToPNG());
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        }

        static List<Color> ReadPalette()
        {
            var tex = Load(PalettePath);
            var result = new List<Color>();
            int cw = tex.width / PaletteCells, ch = tex.height / PaletteCells;
            for (int row = 0; row < PaletteCells; row++)
                for (int col = 0; col < PaletteCells; col++)
                    result.Add(tex.GetPixel(col * cw + cw / 2, row * ch + ch / 2));
            Object.DestroyImmediate(tex);
            return result;
        }

        /// <summary>Средний цвет полосы картинки по высоте от v0 до v1 (0 — низ, 1 — верх).</summary>
        static Color Average(Texture2D tex, float v0, float v1)
        {
            int y0 = Mathf.FloorToInt(v0 * (tex.height - 1)), y1 = Mathf.CeilToInt(v1 * (tex.height - 1));
            int step = Mathf.Max(1, tex.width / 64);
            Color sum = Color.clear;
            int n = 0;
            for (int y = y0; y <= y1; y += step)
                for (int x = 0; x < tex.width; x += step)
                {
                    sum += tex.GetPixel(x, y);
                    n++;
                }
            return n > 0 ? sum / n : Color.gray;
        }

        static Color Nearest(List<Color> palette, Color c)
        {
            Color best = c;
            float bestD = float.MaxValue;
            foreach (var p in palette)
            {
                float dr = p.r - c.r, dg = p.g - c.g, db = p.b - c.b;
                // Глаз чувствительнее к зелёному — веса как у яркости.
                float d = 0.3f * dr * dr + 0.59f * dg * dg + 0.11f * db * db;
                if (d < bestD) { bestD = d; best = p; }
            }
            best.a = 1f;
            return best;
        }
    }
}
