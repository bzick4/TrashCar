using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

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
        public const string ToonShaderName = "JunkRace/Toon";
        const string SettingsFolder = "Assets/JunkRace/Settings";
        const string ProfilePath = SettingsFolder + "/JunkRaceLook.asset";
        const string SkyPath = SettingsFolder + "/JunkRaceSky.mat";

        static readonly Color FogColor = new Color(0.74f, 0.82f, 0.9f);

        public static Material MakeMaterial(Color color)
        {
            var shader = Shader.Find(ToonShaderName);
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            return new Material(shader) { color = color };
        }

        [MenuItem("JunkRace/Визуал: применить к открытой сцене")]
        public static void ApplyToOpenScene()
        {
            int swapped = 0;
            foreach (var r in Object.FindObjectsByType<MeshRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var mats = r.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    var m = mats[i];
                    if (m == null || !IsPlainLit(m.shader)) continue;
                    mats[i] = MakeMaterial(m.color);
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
            s != null && (s.name == "Universal Render Pipeline/Lit" || s.name == "Universal Render Pipeline/Simple Lit" || s.name == "Standard");

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

            // ---- Постобработка ----
            var profile = LoadOrCreateProfile();
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
