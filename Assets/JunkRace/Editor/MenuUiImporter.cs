using UnityEditor;

namespace JunkRace.EditorTools
{
    /// <summary>
    /// Настройки импорта для картинок меню (Assets/JunkRace/Resources/UI):
    /// тип «Editor GUI and Legacy GUI» — без мипмапов, без подгонки под степень двойки, без сжатия,
    /// с прозрачностью. Иначе IMGUI-кнопки размываются, а 9-slice съезжает.
    /// </summary>
    public class MenuUiImporter : AssetPostprocessor
    {
        const string Folder = "Assets/JunkRace/Resources/UI/";

        void OnPreprocessTexture()
        {
            if (!assetPath.StartsWith(Folder)) return;
            var importer = (TextureImporter)assetImporter;
            importer.textureType = TextureImporterType.GUI;
            importer.mipmapEnabled = false;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.alphaIsTransparency = true;
            importer.wrapMode = UnityEngine.TextureWrapMode.Clamp;
            importer.filterMode = UnityEngine.FilterMode.Bilinear;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.maxTextureSize = 2048;
        }
    }
}
