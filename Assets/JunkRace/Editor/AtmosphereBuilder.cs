using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace JunkRace.EditorTools
{
    /// <summary>
    /// Воздух в духе Over the Hill: дымка по глубине (Full Screen Pass в рендерерах URP),
    /// небо-градиент, атмосфера и холмы-задник в сцене. Вызывается при сборке трассы,
    /// а для открытой сцены — меню JunkRace → Атмосфера: применить к открытой сцене.
    /// Материалы создаются один раз в Assets/JunkRace/Settings — потом их можно крутить руками.
    /// </summary>
    public static class AtmosphereBuilder
    {
        static readonly string[] RendererPaths = { "Assets/Settings/PC_Renderer.asset", "Assets/Settings/Mobile_Renderer.asset" };
        const string SettingsFolder = "Assets/JunkRace/Settings";
        const string FogMatPath = SettingsFolder + "/JR_Atmosphere.mat";
        const string SkyMatPath = SettingsFolder + "/JR_SkyGradient.mat";
        const string PanoMatPath = SettingsFolder + "/JR_SkyPanorama.mat";
        const string SkyArtFolder = "Assets/JunkRace/Art/Sky";
        const string TerrainMatPath = SettingsFolder + "/JR_ToonTerrain.mat";
        const string CrownMatPath = SettingsFolder + "/JR_TreeCrown.mat";
        const string TrunkMatPath = SettingsFolder + "/JR_TreeTrunk.mat";
        const string FeatureName = "Атмосфера (дымка)";

        [MenuItem("JunkRace/Атмосфера: применить к открытой сцене")]
        static void ApplyToOpenScene()
        {
            Apply(Camera.main);
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            Debug.Log("JunkRace: атмосфера применена (дымка, небо, холмы-задник). Сохрани сцену. " +
                      "Холмы строятся в игре по сиду трассы; предпросмотр — ПКМ по компоненту BackdropHills.");
        }

        [MenuItem("JunkRace/Атмосфера: убрать дымку из рендереров")]
        static void RemoveFeature()
        {
            foreach (var path in RendererPaths)
            {
                var data = AssetDatabase.LoadAssetAtPath<ScriptableRendererData>(path);
                if (data == null) continue;
                foreach (var f in data.rendererFeatures)
                    if (f is FullScreenPassRendererFeature && f.name == FeatureName) f.SetActive(false);
                EditorUtility.SetDirty(data);
            }
            AssetDatabase.SaveAssets();
            Debug.Log("JunkRace: фича дымки выключена (не удалена) — включить обратно можно в рендерере.");
        }

        public static void Apply(Camera cam)
        {
            EnsureFolder();
            var fogMat = LoadOrCreate(FogMatPath, "Hidden/JunkRace/Atmosphere", null);
            var skyMat = LoadOrCreate(SkyMatPath, "JunkRace/Sky Gradient", null);
            var terrainMat = LoadOrCreate(TerrainMatPath, "JunkRace/Toon Terrain", null);
            var crownMat = LoadOrCreate(CrownMatPath, "JunkRace/Toon", m => { m.color = new Color(0.22f, 0.38f, 0.24f); m.SetFloat("_NoiseStrength", 0.12f); });
            var trunkMat = LoadOrCreate(TrunkMatPath, "JunkRace/Toon", m => m.color = new Color(0.38f, 0.27f, 0.18f));
            if (crownMat != null) crownMat.enableInstancing = true;
            if (trunkMat != null) trunkMat.enableInstancing = true;

            if (fogMat != null)
                foreach (var path in RendererPaths) EnsureFogFeature(path, fogMat);

            // Объект атмосферы в сцене.
            var atmo = Object.FindFirstObjectByType<StylizedAtmosphere>();
            if (atmo == null) atmo = new GameObject("Атмосфера").AddComponent<StylizedAtmosphere>();
            var hills = Object.FindFirstObjectByType<BackdropHills>();
            if (hills == null)
            {
                var go = new GameObject("Холмы-задник");
                go.transform.SetParent(atmo.transform, false);
                hills = go.AddComponent<BackdropHills>();
            }
            var so = new SerializedObject(hills);
            so.FindProperty("terrainMaterial").objectReferenceValue = terrainMat;
            so.FindProperty("treeCrownMaterial").objectReferenceValue = crownMat;
            so.FindProperty("treeTrunkMaterial").objectReferenceValue = trunkMat;
            so.ApplyModifiedPropertiesWithoutUndo();

            // Нарисованные панорамы (Art/Sky/sky_day|sunset|dusk|night.png) важнее градиента.
            var pano = PanoramaSky();
            if (pano != null) RenderSettings.skybox = pano;
            else if (skyMat != null) RenderSettings.skybox = skyMat;
            DynamicGI.UpdateEnvironment();

            if (cam != null)
            {
                cam.clearFlags = CameraClearFlags.Skybox;
                cam.farClipPlane = 2600f;
                // Дымке нужна глубина: фича сама её запросит, но включим и на камере — надёжнее.
                var data = cam.GetUniversalAdditionalCameraData();
                data.requiresDepthOption = CameraOverrideOption.On;
            }

            AssetDatabase.SaveAssets();
        }

        /// <summary>Материал неба из четырёх панорам; null — картинок нет.</summary>
        static Material PanoramaSky()
        {
            var day = AssetDatabase.LoadAssetAtPath<Texture2D>(SkyArtFolder + "/sky_day.png");
            if (day == null) return null;
            var mat = LoadOrCreate(PanoMatPath, "JunkRace/Sky Panorama", null);
            if (mat == null) return null;
            mat.SetTexture("_DayTex", day);
            foreach (var (prop, file) in new[] { ("_SunsetTex", "sky_sunset"), ("_DuskTex", "sky_dusk"), ("_NightTex", "sky_night") })
            {
                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(SkyArtFolder + "/" + file + ".png");
                mat.SetTexture(prop, tex != null ? tex : day);
            }
            EditorUtility.SetDirty(mat);
            return mat;
        }

        /// <summary>Full Screen Pass с материалом дымки, перед постобработкой (после контуров).</summary>
        static void EnsureFogFeature(string rendererPath, Material mat)
        {
            var data = AssetDatabase.LoadAssetAtPath<ScriptableRendererData>(rendererPath);
            if (data == null) return;
            foreach (var f in data.rendererFeatures)
                if (f is FullScreenPassRendererFeature fs && fs.passMaterial == mat) { f.SetActive(true); return; }

            var feature = ScriptableObject.CreateInstance<FullScreenPassRendererFeature>();
            feature.name = FeatureName;
            feature.passMaterial = mat;
            feature.passIndex = 0;
            feature.injectionPoint = FullScreenPassRendererFeature.InjectionPoint.BeforeRenderingPostProcessing;
            feature.fetchColorBuffer = true;
            feature.requirements = ScriptableRenderPassInput.Depth;
            AssetDatabase.AddObjectToAsset(feature, data);
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feature, out _, out long localId);

            var so = new SerializedObject(data);
            var list = so.FindProperty("m_RendererFeatures");
            var map = so.FindProperty("m_RendererFeatureMap");
            list.arraySize++;
            list.GetArrayElementAtIndex(list.arraySize - 1).objectReferenceValue = feature;
            map.arraySize++;
            map.GetArrayElementAtIndex(map.arraySize - 1).longValue = localId;
            so.ApplyModifiedPropertiesWithoutUndo();
            data.SetDirty();
            EditorUtility.SetDirty(data);
            AssetDatabase.SaveAssets();
        }

        static Material LoadOrCreate(string path, string shaderName, System.Action<Material> setup)
        {
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m != null) return m;
            var shader = Shader.Find(shaderName);
            if (shader == null)
            {
                Debug.LogWarning($"JunkRace: не найден шейдер {shaderName} — материал {path} не создан.");
                return null;
            }
            m = new Material(shader) { name = System.IO.Path.GetFileNameWithoutExtension(path) };
            setup?.Invoke(m);
            AssetDatabase.CreateAsset(m, path);
            return m;
        }

        static void EnsureFolder()
        {
            if (!AssetDatabase.IsValidFolder(SettingsFolder))
                AssetDatabase.CreateFolder("Assets/JunkRace", "Settings");
        }
    }
}
