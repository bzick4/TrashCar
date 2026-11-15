using UnityEditor;

namespace JunkRace.EditorTools
{
    /// <summary>
    /// Текстуры моделей из Assets/Prefab (Tripo кладёт 4K) — не больше 1024: в мультяшном стиле разницы
    /// не видно, а памяти в 16 раз меньше. Работает и для новых моделей, и при извлечении текстур из FBX.
    /// </summary>
    public class ModelTextureLimit : AssetPostprocessor
    {
        const string Folder = "Assets/Prefab/";
        public const int MaxSize = 1024;

        void OnPreprocessTexture()
        {
            if (!assetPath.StartsWith(Folder)) return;
            var importer = (TextureImporter)assetImporter;
            if (importer.maxTextureSize > MaxSize) importer.maxTextureSize = MaxSize;
        }

        [MenuItem("JunkRace/Текстуры моделей: ужать до 1024")]
        static void ReimportAll()
        {
            int n = 0;
            foreach (var guid in AssetDatabase.FindAssets("t:Texture2D", new[] { "Assets/Prefab" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (AssetImporter.GetAtPath(path) is TextureImporter ti && ti.maxTextureSize > MaxSize)
                {
                    ti.maxTextureSize = MaxSize;
                    ti.SaveAndReimport();
                    n++;
                }
            }
            UnityEngine.Debug.Log($"JunkRace: текстур ужато до {MaxSize}: {n}.");
        }
    }
}
