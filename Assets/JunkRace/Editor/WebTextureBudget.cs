using UnityEditor;
using UnityEngine;

namespace JunkRace.EditorTools
{
    /// <summary>
    /// Веб (телефон): текстуры мира, машин и деталей — 512 px и сжатие ASTC 8×8. Это самая большая часть
    /// файла данных; на ПК ничего не меняется (правила только для WebGL). Интерфейс (Resources/UI) не трогаем — он чёткий.
    /// </summary>
    public static class WebTextureBudget
    {
        static readonly string[] Folders =
        {
            "Assets/Prefab/Route", "Assets/Prefab/Car", "Assets/Prefab/junky", "Assets/Prefab/Toon",
            "Assets/Toon", "Assets/Junkyard",
        };
        const int MaxSize = 512;

        [MenuItem("JunkRace/Сборка/Веб: ужать текстуры мира")]
        public static void Apply()
        {
            int changed = 0;
            var guids = AssetDatabase.FindAssets("t:Texture2D", Folders);
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var g in guids)
                {
                    string path = AssetDatabase.GUIDToAssetPath(g);
                    if (path.Contains("/Plugins/") || path.Contains("TextMesh")) continue;
                    var imp = AssetImporter.GetAtPath(path) as TextureImporter;
                    if (imp == null) continue;
                    var s = imp.GetPlatformTextureSettings("WebGL");
                    int size = Mathf.Min(imp.maxTextureSize, MaxSize);
                    if (s.overridden && s.maxTextureSize == size && s.format == TextureImporterFormat.ASTC_8x8) continue;
                    s.overridden = true;
                    s.maxTextureSize = size;
                    s.format = TextureImporterFormat.ASTC_8x8;
                    s.textureCompression = TextureImporterCompression.Compressed;
                    imp.SetPlatformTextureSettings(s);
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                    changed++;
                }
            }
            finally { AssetDatabase.StopAssetEditing(); }
            AssetDatabase.SaveAssets();
            Debug.Log($"Веб-текстуры: {changed} шт. — до {MaxSize} px, ASTC 8×8");
        }
    }
}
