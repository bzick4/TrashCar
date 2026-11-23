using UnityEditor;
using UnityEngine;

namespace JunkRace.EditorTools
{
    /// <summary>
    /// Импорт звуков игры: музыка (Resources/Audio/Music) — потоком с диска и сжатая, чтобы песни не сидели
    /// целиком в памяти; звуки мотора (Resources/Audio/Engine) — короткие, распакованы в память, моно.
    /// </summary>
    public class AudioImportSettings : AssetPostprocessor
    {
        const string Music = "Assets/JunkRace/Resources/Audio/Music/";
        const string Engine = "Assets/JunkRace/Resources/Audio/Engine/";

        void OnPreprocessAudio()
        {
            var importer = (AudioImporter)assetImporter;
            var s = importer.defaultSampleSettings;
            if (assetPath.StartsWith(Music))
            {
                s.loadType = AudioClipLoadType.Streaming;
                s.compressionFormat = AudioCompressionFormat.Vorbis;
                s.quality = 0.6f;
                importer.loadInBackground = true;
                // Веб: потоковой загрузки там нет — трек держим сжатым в памяти (Vorbis декодирует сам Unity).
                var web = importer.GetOverrideSampleSettings("WebGL");
                web.loadType = AudioClipLoadType.CompressedInMemory;
                web.compressionFormat = AudioCompressionFormat.Vorbis;
                web.quality = 0.4f;   // веб: музыка в файле данных — экономим; на телефоне разницу почти не слышно
                importer.SetOverrideSampleSettings("WebGL", web);   // становится включённым, как только задан

            }
            else if (assetPath.StartsWith(Engine))
            {
                s.loadType = AudioClipLoadType.DecompressOnLoad;
                s.compressionFormat = AudioCompressionFormat.Vorbis;
                s.quality = 0.7f;
                importer.forceToMono = true;
            }
            else return;
            importer.defaultSampleSettings = s;
        }
    }
}
