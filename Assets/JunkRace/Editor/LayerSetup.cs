using UnityEditor;
using UnityEngine;

namespace JunkRace.EditorTools
{
    /// <summary>
    /// Слои декораций трассы: по ним камера отсекает мелочь вдали (Camera.layerCullDistances).
    /// Создаются сами при загрузке редактора, если их ещё нет.
    /// </summary>
    [InitializeOnLoad]
    public static class LayerSetup
    {
        static LayerSetup() => EditorApplication.delayCall += Ensure;

        public static void Ensure()
        {
            EnsureLayer(RouteGenerator.SmallDecorLayer);
            EnsureLayer(RouteGenerator.TreeLayer);
        }

        static void EnsureLayer(string name)
        {
            if (LayerMask.NameToLayer(name) >= 0) return;
            var assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset");
            if (assets == null || assets.Length == 0) return;
            var so = new SerializedObject(assets[0]);
            var layers = so.FindProperty("layers");
            for (int i = 8; i < layers.arraySize; i++)
            {
                var p = layers.GetArrayElementAtIndex(i);
                if (!string.IsNullOrEmpty(p.stringValue)) continue;
                p.stringValue = name;
                so.ApplyModifiedPropertiesWithoutUndo();
                Debug.Log($"JunkRace: создан слой «{name}» ({i}).");
                return;
            }
            Debug.LogWarning($"JunkRace: нет свободного слоя для «{name}».");
        }
    }
}
