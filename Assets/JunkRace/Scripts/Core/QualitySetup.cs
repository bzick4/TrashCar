using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace JunkRace
{
    /// <summary>
    /// Настройки графики игрока: масштаб рендера. На больших экранах (4K) рендерим меньше и растягиваем —
    /// контуры, SSAO и свечение считаются для каждого пикселя, и пикселей в 4K вчетверо больше, чем в Full HD.
    /// </summary>
    public static class QualitySetup
    {
        public static readonly string[] ScaleNames = { "Авто", "100%", "85%", "75%", "50%" };
        static readonly float[] Scales = { -1f, 1f, 0.85f, 0.75f, 0.5f };
        const string Key = "jr_render_scale";

        public static int ScaleIndex
        {
            get => Mathf.Clamp(PlayerPrefs.GetInt(Key, 0), 0, Scales.Length - 1);
            set { PlayerPrefs.SetInt(Key, Mathf.Clamp(value, 0, Scales.Length - 1)); PlayerPrefs.Save(); Apply(); }
        }

        /// <summary>
        /// Фактический масштаб: «Авто» — 75% на экранах выше 1600 строк, иначе 100%.
        /// Телефон: 3D — не больше ~1000 строк по высоте (на iPhone это ~85%, на плотных экранах меньше);
        /// интерфейс поверх рисуется в полном разрешении экрана и остаётся чётким.
        /// </summary>
        public static float EffectiveScale
        {
            get
            {
                float s = Scales[ScaleIndex];
                if (s > 0f) return s;
                if (TouchControls.TouchMode && !Application.isEditor) return Mathf.Clamp(1000f / Mathf.Max(1, Screen.height), 0.5f, 1f);
                return Screen.height > 1600 ? 0.75f : 1f;
            }
        }

        static bool mobileApplied;

        /// <summary>
        /// Телефон (сборка, не редактор — там это переписало бы ассет): без HDR, тени попроще и ближе,
        /// без SSAO (самый дорогой эффект на пиксель). Контуры и дымку оставляем — это стиль игры.
        /// </summary>
        public static void ApplyMobile()
        {
            if (mobileApplied || Application.isEditor) return;
            if (!(GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset urp)) return;
            mobileApplied = true;
            urp.supportsHDR = false;
            urp.shadowDistance = Mathf.Min(urp.shadowDistance, 35f);
            urp.mainLightShadowmapResolution = 1024;
            var field = typeof(UniversalRenderPipelineAsset).GetField("m_RendererDataList",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (field?.GetValue(urp) is ScriptableRendererData[] list)
                foreach (var data in list)
                {
                    if (data == null) continue;
                    foreach (var f in data.rendererFeatures)
                        if (f != null && (f.name.Contains("AmbientOcclusion") || f.GetType().Name.Contains("AmbientOcclusion"))) f.SetActive(false);
                }
            Apply();
        }

        // ---------------- Лимит кадров ----------------
        // Без лимита игра рисует сотни кадров впустую — греет компьютер (особенно два окна на одном Mac).

        public static readonly string[] FpsNames = { "60", "90", "120", "144", "30", "без лимита" };
        static readonly int[] FpsValues = { 60, 90, 120, 144, 30, -1 };
        const string FpsKey = "jr_fps_limit";
        /// <summary>В фоне (окно не в фокусе) хватит и этого.</summary>
        const int BackgroundFps = 30;

        public static int FpsIndex
        {
            get => Mathf.Clamp(PlayerPrefs.GetInt(FpsKey, 0), 0, FpsValues.Length - 1);
            set { PlayerPrefs.SetInt(FpsKey, Mathf.Clamp(value, 0, FpsValues.Length - 1)); PlayerPrefs.Save(); ApplyFps(); }
        }

        static void ApplyFps()
        {
            QualitySettings.vSyncCount = 0;   // лимит задаём сами (vSync в редакторе всё равно не работает)
            int fps = FpsValues[FpsIndex];
            if (!Application.isFocused && (fps <= 0 || fps > BackgroundFps)) fps = BackgroundFps;
            Application.targetFrameRate = fps;
        }

        static float originalScale = -1f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Init()
        {
            Apply();
            if (TouchControls.TouchMode) ApplyMobile();
            ApplyFps();
            Application.focusChanged += _ => ApplyFps();
            // В редакторе масштаб пишется прямо в ассет пайплайна — по выходу из игры возвращаем как было.
            Application.quitting += Restore;
        }

        public static void Apply()
        {
            if (!(GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset urp)) return;
            if (originalScale < 0f) originalScale = urp.renderScale;
            urp.renderScale = EffectiveScale;
        }

        static void Restore()
        {
            if (originalScale < 0f) return;
            if (GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset urp) urp.renderScale = originalScale;
        }
    }
}
