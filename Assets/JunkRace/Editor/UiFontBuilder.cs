using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

namespace JunkRace.EditorTools
{
    /// <summary>
    /// Шрифт интерфейса для TextMeshPro: SDF-ассет из нашего Noto Sans (Resources/Fonts).
    /// SDF-текст остаётся чётким при любом масштабе — в отличие от старого Text, который на телефоне мылился.
    /// Латиница, кириллица и знаки запекаются заранее; чего нет — досоздаётся на лету (ассет динамический).
    /// </summary>
    public static class UiFontBuilder
    {
        const string FontPath = "Assets/JunkRace/Resources/Fonts/NotoSans-VariableFont_wdth,wght.ttf";
        public const string AssetPath = "Assets/JunkRace/Resources/Fonts/UiFont SDF.asset";

        const string Chars =
            " !\"#$%&'()*+,-./0123456789:;<=>?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[\\]^_`abcdefghijklmnopqrstuvwxyz{|}~" +
            "АБВГДЕЁЖЗИЙКЛМНОПРСТУФХЦЧШЩЪЫЬЭЮЯабвгдеёжзийклмнопрстуфхцчшщъыьэюя" +
            "«»—–…№°×·•←→↑↓✓€₽";

        [MenuItem("JunkRace/UI/Собрать шрифт TMP")]
        public static void Build()
        {
            var font = AssetDatabase.LoadAssetAtPath<Font>(FontPath);
            if (font == null) { Debug.LogError("Нет шрифта " + FontPath); return; }

            var fa = TMP_FontAsset.CreateFontAsset(font, 56, 6, GlyphRenderMode.SDFAA, 1024, 1024, AtlasPopulationMode.Dynamic, true);
            fa.name = "UiFont SDF";
            AssetDatabase.DeleteAsset(AssetPath);
            AssetDatabase.CreateAsset(fa, AssetPath);
            fa.material.name = "UiFont SDF Material";
            AssetDatabase.AddObjectToAsset(fa.material, fa);
            foreach (var t in fa.atlasTextures)
            {
                t.name = "UiFont SDF Atlas";
                AssetDatabase.AddObjectToAsset(t, fa);
            }
            fa.TryAddCharacters(Chars, out string missing);
            if (!string.IsNullOrEmpty(missing)) Debug.Log("Шрифт TMP: в Noto Sans нет символов: " + missing);
            // Новые страницы атласа (если не влезло в одну) — тоже в ассет.
            foreach (var t in fa.atlasTextures)
                if (t != null && !AssetDatabase.Contains(t)) { t.name = "UiFont SDF Atlas"; AssetDatabase.AddObjectToAsset(t, fa); }
            EditorUtility.SetDirty(fa);

            // Не чистить запечённые символы при сборке: иначе в вебе весь алфавит пришлось бы рисовать заново на лету.
            var settings = Resources.Load<TMP_Settings>("TMP Settings");
            if (settings != null)
            {
                var so = new SerializedObject(settings);
                var p = so.FindProperty("m_ClearDynamicDataOnBuild");
                if (p != null) { p.boolValue = false; so.ApplyModifiedPropertiesWithoutUndo(); }
                var def = so.FindProperty("m_defaultFontAsset");
                if (def != null) { def.objectReferenceValue = fa; so.ApplyModifiedPropertiesWithoutUndo(); }
                EditorUtility.SetDirty(settings);
            }
            AssetDatabase.SaveAssets();
            Debug.Log($"Шрифт TMP собран: {AssetPath}, символов {fa.characterTable.Count}, страниц атласа {fa.atlasTextureCount}");
        }
    }
}
