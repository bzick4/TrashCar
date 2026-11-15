using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using ToonShadersPro.URP;

namespace JunkRace.EditorTools
{
    /// <summary>
    /// Стилизованный визуал: материалы на шейдере JunkRace/Toon, тёплое солнце и цветные тени,
    /// небо, мягкий туман и постобработка (цветокор, свечение, виньетка).
    /// Применяется при сборке трассы, а для уже открытой сцены — через меню
    /// JunkRace → Визуал: применить к открытой сцене.
    /// </summary>
    public static class LookBuilder
    {
        /// <summary>Шейдер из ассета Toon Shaders Pro; свой JunkRace/Toon — запасной.</summary>
        public const string ProToonShaderName = "Toon Shaders Pro/URP/Toon";
        public const string ToonShaderName = "JunkRace/Toon";
        static readonly string[] RendererPaths = { "Assets/Settings/PC_Renderer.asset", "Assets/Settings/Mobile_Renderer.asset" };
        const string SettingsFolder = "Assets/JunkRace/Settings";
        const string ProfilePath = SettingsFolder + "/JunkRaceLook.asset";
        const string SkyPath = SettingsFolder + "/JunkRaceSky.mat";

        static readonly Color FogColor = new Color(0.74f, 0.82f, 0.9f);

        // Один материал на цвет: раньше у каждого кубика был свой (сотни штук) —
        // лишняя память и хуже батчинг. Кэш живёт в пределах одной сборки сцены.
        static readonly Dictionary<Color32, Material> shared = new Dictionary<Color32, Material>();

        /// <summary>Сбросить кэш общих материалов (перед сборкой или обработкой новой сцены).</summary>
        public static void ResetMaterialCache() => shared.Clear();

        public static Material MakeMaterial(Color color)
        {
            Color32 key = color;
            if (shared.TryGetValue(key, out var cached) && cached != null) return cached;
            var m = CreateMaterial(color);
            shared[key] = m;
            return m;
        }

        static Material CreateMaterial(Color color)
        {
            var pro = Shader.Find(ProToonShaderName);
            if (pro != null) return MakeProMaterial(pro, color);

            var shader = Shader.Find(ToonShaderName);
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            return new Material(shader) { color = color };
        }

        /// <summary>
        /// Настройки Toon Shaders Pro под наш стиль: мягкая граница света, голубоватые тени,
        /// тёплый ободок по краю, слабый блик. Ключевые слова включаем сами — инспектор
        /// ассета делает это только при ручной правке.
        /// </summary>
        const string ProMaterialName = "JR Toon";

        /// <summary>Материал модели → наш toon с тем же цветом и текстурой.</summary>
        public static Material ConvertMaterial(Material src)
        {
            if (src == null) return MakeMaterial(Color.white);
            Color color = src.HasProperty("_BaseColor") ? src.GetColor("_BaseColor")
                        : src.HasProperty("_Color") ? src.GetColor("_Color") : Color.white;
            Texture tex = src.HasProperty("_BaseMap") ? src.GetTexture("_BaseMap")
                        : src.HasProperty("_MainTex") ? src.GetTexture("_MainTex") : null;
            if (tex == null) return MakeMaterial(color);   // без текстуры — общий по цвету
            // С текстурой — наш JunkRace/Toon: шейдер ассета умножает на цвет дважды,
            // и для цвета мы это компенсируем, а для текстуры нельзя — она бы потемнела.
            var own = Shader.Find(ToonShaderName);
            var m = own != null ? new Material(own) { color = color } : CreateMaterial(color);
            if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", tex);
            if (own != null) m.SetFloat("_NoiseStrength", 0f);   // у текстуры свои пятна
            m.name = ProMaterialName + " " + src.name;
            return m;
        }

        static Material MakeProMaterial(Shader shader, Color color)
        {
            // Шейдер ассета умножает на цвет дважды (в BRDF и ещё раз в конце) — цвета выходят
            // в квадрате, тёмные почти чёрные. Даём корень: в итоге получается ровно задуманный цвет.
            var c = new Color(Mathf.Sqrt(color.r), Mathf.Sqrt(color.g), Mathf.Sqrt(color.b), color.a);
            var m = new Material(shader) { color = c, name = ProMaterialName };
            m.SetColor("_LightTint", Color.white);
            m.SetColor("_MiddleTint", new Color(0.75f, 0.75f, 0.8f));
            m.SetColor("_ShadowTint", new Color(0.38f, 0.42f, 0.6f));
            m.SetVector("_DiffuseThresholds", new Vector4(-0.05f, 0.1f, 0f, 0f));
            m.SetVector("_ShadowThresholds", new Vector4(0.2f, 0.6f, 0f, 0f));
            m.SetFloat("_GIStrength", 1f);
            m.SetFloat("_Smoothness", 0.25f);
            m.SetColor("_SpecularColor", new Color(0.12f, 0.12f, 0.12f));
            m.SetVector("_SpecularThresholds", new Vector4(0.01f, 0.03f, 0f, 0f));
            m.SetColor("_RimColor", new Color(0.35f, 0.32f, 0.28f));
            m.SetVector("_RimThresholds", new Vector4(0.72f, 0.85f, 0f, 0f));
            m.SetFloat("_ReceiveShadows", 1f);
            m.EnableKeyword("_RECEIVE_SHADOWS_ON");
            return m;
        }

        [MenuItem("JunkRace/Визуал: применить к открытой сцене")]
        public static void ApplyToOpenScene()
        {
            ResetMaterialCache();
            int swapped = 0;
            foreach (var r in Object.FindObjectsByType<MeshRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var mats = r.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    var m = mats[i];
                    if (m == null) continue;
                    bool texturedOwn = m.shader != null && m.shader.name == ToonShaderName
                                       && m.HasProperty("_BaseMap") && m.GetTexture("_BaseMap") != null;
                    if (texturedOwn) continue;   // текстурные модели остаются на нашем шейдере
                    if (IsPlainLit(m.shader)) mats[i] = ConvertMaterial(m);
                    else if (m.shader != null && m.shader.name == ProToonShaderName && !m.name.StartsWith(ProMaterialName))
                        mats[i] = MakeMaterial(m.color);   // сделан до поправки яркости — переделываем
                    else continue;
                    changed = true;
                    swapped++;
                }
                if (changed) r.sharedMaterials = mats;
            }

            ApplyEnvironment(Camera.main);
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            Debug.Log($"JunkRace: визуал применён, перекрашено материалов: {swapped}. Сохрани сцену.");
        }

        static bool IsPlainLit(Shader s) =>
            s != null && (s.name == "Universal Render Pipeline/Lit" || s.name == "Universal Render Pipeline/Simple Lit"
                          || s.name == "Standard" || s.name == ToonShaderName);

        /// <summary>Солнце, небо, туман, окружение и постобработка для активной сцены.</summary>
        public static void ApplyEnvironment(Camera cam)
        {
            // ---- Солнце: тёплое, не в зените, чтобы тени были длинными и читались формы ----
            Light sun = null;
            foreach (var l in Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
                if (l.type == LightType.Directional) { sun = l; break; }
            if (sun == null)
            {
                sun = new GameObject("Directional Light").AddComponent<Light>();
                sun.type = LightType.Directional;
            }
            sun.color = new Color(1f, 0.92f, 0.8f);
            sun.intensity = 1.5f;
            sun.shadows = LightShadows.Soft;
            sun.shadowStrength = 0.9f;
            sun.transform.rotation = Quaternion.Euler(38f, -40f, 0f);
            RenderSettings.sun = sun;

            // ---- Окружение: небо голубое, горизонт тёплый, снизу — земля ----
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.6f, 0.74f, 0.96f);
            RenderSettings.ambientEquatorColor = new Color(0.86f, 0.8f, 0.72f);
            RenderSettings.ambientGroundColor = new Color(0.45f, 0.38f, 0.32f);

            RenderSettings.skybox = LoadOrCreateSky();

            // ---- Туман: дальние объекты растворяются в цвете горизонта ----
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogColor = FogColor;
            RenderSettings.fogStartDistance = 35f;
            RenderSettings.fogEndDistance = 220f;

            DynamicGI.UpdateEnvironment();

            // ---- Постобработка и контуры ----
            var profile = LoadOrCreateProfile();
            EnsureOutlines(profile);
            foreach (var path in RendererPaths) EnsureOutlineFeature(path);
            Volume volume = null;
            foreach (var v in Object.FindObjectsByType<Volume>(FindObjectsSortMode.None))
                if (v.isGlobal) { volume = v; break; }
            if (volume == null) volume = new GameObject("Визуал (постобработка)").AddComponent<Volume>();
            volume.isGlobal = true;
            volume.priority = 1f;
            volume.sharedProfile = profile;

            if (cam != null)
            {
                cam.clearFlags = CameraClearFlags.Skybox;
                cam.farClipPlane = 400f;
                var data = cam.GetUniversalAdditionalCameraData();
                data.renderPostProcessing = true;
                data.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
                data.renderShadows = true;
            }
        }

        static Material LoadOrCreateSky()
        {
            var sky = AssetDatabase.LoadAssetAtPath<Material>(SkyPath);
            if (sky != null) return sky;
            var shader = Shader.Find("Skybox/Procedural");
            if (shader == null) return RenderSettings.skybox;
            EnsureFolder();
            sky = new Material(shader);
            sky.SetFloat("_SunSize", 0.05f);
            sky.SetFloat("_SunSizeConvergence", 4f);
            sky.SetFloat("_AtmosphereThickness", 0.75f);
            sky.SetColor("_SkyTint", new Color(0.55f, 0.68f, 0.9f));
            sky.SetColor("_GroundColor", FogColor);
            sky.SetFloat("_Exposure", 1.25f);
            AssetDatabase.CreateAsset(sky, SkyPath);
            return sky;
        }

        /// <summary>Профиль создаётся один раз; дальше его можно крутить руками — пересборка не затрёт.</summary>
        static VolumeProfile LoadOrCreateProfile()
        {
            var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(ProfilePath);
            if (profile != null) return profile;

            EnsureFolder();
            profile = ScriptableObject.CreateInstance<VolumeProfile>();
            AssetDatabase.CreateAsset(profile, ProfilePath);

            var tone = Add<Tonemapping>(profile);
            tone.mode.value = TonemappingMode.Neutral;

            var color = Add<ColorAdjustments>(profile);
            color.postExposure.value = 0.15f;
            color.contrast.value = 12f;
            color.saturation.value = 20f;

            var white = Add<WhiteBalance>(profile);
            white.temperature.value = 6f;

            var bloom = Add<Bloom>(profile);
            bloom.threshold.value = 1f;
            bloom.intensity.value = 0.35f;
            bloom.scatter.value = 0.65f;

            var vignette = Add<Vignette>(profile);
            vignette.intensity.value = 0.22f;
            vignette.smoothness.value = 0.45f;

            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssets();
            return profile;
        }

        /// <summary>Контуры Toon Shaders Pro: по глубине и нормалям, тёмные, гаснут вдали.</summary>
        static void EnsureOutlines(VolumeProfile profile)
        {
            if (profile.Has<OutlineSettings>()) return;
            var o = Add<OutlineSettings>(profile);
            o.outlineType.value = OutlineType.DepthNormalOutlines;
            o.outlineColor.value = new Color(0.12f, 0.1f, 0.09f, 1f);
            o.depthSensitivity.value = 0.02f;
            o.depthStrength.value = 0.9f;
            o.normalSensitivity.value = 0.25f;
            o.normalStrength.value = 0.6f;
            o.colorStrength.value = 0f;
            o.outlineFadeStart.value = 30f;
            o.outlineFadeEnd.value = 80f;
            o.renderPassEvent.value = PostProcessRenderPassEvent.BeforeURPPostProcessing;
            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssets();
        }

        /// <summary>Добавляет в рендерер фичу контуров (без неё настройки в профиле ничего не рисуют).</summary>
        static void EnsureOutlineFeature(string rendererPath)
        {
            var data = AssetDatabase.LoadAssetAtPath<ScriptableRendererData>(rendererPath);
            if (data == null) return;
            foreach (var f in data.rendererFeatures)
                if (f is OutlineFeature) return;

            var feature = ScriptableObject.CreateInstance<OutlineFeature>();
            feature.name = "Outlines";
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

        static T Add<T>(VolumeProfile profile) where T : VolumeComponent
        {
            var c = profile.Add<T>(true);
            c.name = typeof(T).Name;
            c.hideFlags = HideFlags.HideInInspector | HideFlags.HideInHierarchy;
            AssetDatabase.AddObjectToAsset(c, profile);
            return c;
        }

        static void EnsureFolder()
        {
            if (!AssetDatabase.IsValidFolder(SettingsFolder))
                AssetDatabase.CreateFolder("Assets/JunkRace", "Settings");
        }
    }
}
