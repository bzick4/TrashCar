using TMPro;
using UnityEngine;

namespace JunkRace
{
    /// <summary>
    /// Текст интерфейса на TextMeshPro (SDF — чёткий при любом масштабе, в отличие от старого Text).
    /// Снаружи — привычные свойства старого Text (TextAnchor, FontStyle, перенос), чтобы код меню и HUD
    /// не переписывать: они переводятся в настройки TMP.
    /// </summary>
    public class UiText : TextMeshProUGUI
    {
        static TMP_FontAsset uiFont;
        static Material shadowMaterial;
        static bool loaded;

        /// <summary>Шрифт интерфейса (Resources/Fonts/UiFont SDF — собирается из Noto Sans в редакторе).</summary>
        public static TMP_FontAsset UiFont
        {
            get
            {
                if (!loaded)
                {
                    loaded = true;
                    uiFont = Resources.Load<TMP_FontAsset>("Fonts/UiFont SDF");
                    if (uiFont == null) uiFont = TMP_Settings.defaultFontAsset;
                }
                return uiFont;
            }
        }

        /// <summary>Новый текст на объекте: шрифт, без переноса, не ловит клики.</summary>
        public static UiText Add(GameObject go)
        {
            var t = go.AddComponent<UiText>();
            if (UiFont != null) t.font = UiFont;
            t.richText = true;
            t.raycastTarget = false;
            t.overflowMode = TextOverflowModes.Overflow;
            t.textWrappingMode = TextWrappingModes.NoWrap;
            t.extraPadding = true;
            return t;
        }

        /// <summary>Тень под текстом (как старый Shadow): подложка SDF через общий материал.</summary>
        public void UseShadow()
        {
            if (UiFont == null) return;
            if (shadowMaterial == null)
            {
                shadowMaterial = new Material(UiFont.material) { name = "UiFont SDF Shadow" };
                shadowMaterial.EnableKeyword(ShaderUtilities.Keyword_Underlay);
                shadowMaterial.SetColor(ShaderUtilities.ID_UnderlayColor, new Color(0f, 0f, 0f, 0.85f));
                shadowMaterial.SetFloat(ShaderUtilities.ID_UnderlayOffsetX, 0.6f);
                shadowMaterial.SetFloat(ShaderUtilities.ID_UnderlayOffsetY, -0.6f);
                shadowMaterial.SetFloat(ShaderUtilities.ID_UnderlaySoftness, 0.2f);
            }
            fontSharedMaterial = shadowMaterial;
        }

        // ---- Свойства в стиле старого UnityEngine.UI.Text ----

        public new TextAnchor alignment
        {
            get => anchor;
            set
            {
                if (anchor == value && alignSet) return;
                anchor = value;
                alignSet = true;
                base.alignment = ToTmp(value);
            }
        }
        TextAnchor anchor = TextAnchor.UpperLeft;
        bool alignSet;

        public new FontStyle fontStyle
        {
            get => (base.fontStyle & FontStyles.Bold) != 0
                ? ((base.fontStyle & FontStyles.Italic) != 0 ? FontStyle.BoldAndItalic : FontStyle.Bold)
                : ((base.fontStyle & FontStyles.Italic) != 0 ? FontStyle.Italic : FontStyle.Normal);
            set
            {
                var s = FontStyles.Normal;
                if (value == FontStyle.Bold || value == FontStyle.BoldAndItalic) s |= FontStyles.Bold;
                if (value == FontStyle.Italic || value == FontStyle.BoldAndItalic) s |= FontStyles.Italic;
                if (base.fontStyle != s) base.fontStyle = s;
            }
        }

        public new int fontSize
        {
            get => Mathf.RoundToInt(base.fontSize);
            set { if (!Mathf.Approximately(base.fontSize, value)) base.fontSize = value; }
        }

        /// <summary>Wrap — перенос по ширине; Overflow — одной строкой.</summary>
        public UnityEngine.HorizontalWrapMode horizontalOverflow
        {
            get => textWrappingMode == TextWrappingModes.NoWrap ? UnityEngine.HorizontalWrapMode.Overflow : UnityEngine.HorizontalWrapMode.Wrap;
            set
            {
                var m = value == UnityEngine.HorizontalWrapMode.Wrap ? TextWrappingModes.Normal : TextWrappingModes.NoWrap;
                if (textWrappingMode != m) textWrappingMode = m;
            }
        }

        /// <summary>Текст и так вылезает за рамку по вертикали (как VerticalWrapMode.Overflow).</summary>
        public UnityEngine.VerticalWrapMode verticalOverflow { get; set; } = UnityEngine.VerticalWrapMode.Overflow;

        public bool supportRichText
        {
            get => richText;
            set { if (richText != value) richText = value; }
        }

        static TextAlignmentOptions ToTmp(TextAnchor a)
        {
            switch (a)
            {
                case TextAnchor.UpperLeft: return TextAlignmentOptions.TopLeft;
                case TextAnchor.UpperCenter: return TextAlignmentOptions.Top;
                case TextAnchor.UpperRight: return TextAlignmentOptions.TopRight;
                case TextAnchor.MiddleLeft: return TextAlignmentOptions.Left;
                case TextAnchor.MiddleCenter: return TextAlignmentOptions.Center;
                case TextAnchor.MiddleRight: return TextAlignmentOptions.Right;
                case TextAnchor.LowerLeft: return TextAlignmentOptions.BottomLeft;
                case TextAnchor.LowerCenter: return TextAlignmentOptions.Bottom;
                default: return TextAlignmentOptions.BottomRight;
            }
        }

        // ---- Замер текста (для раскладки UiPainter) ----

        static UiText measurer;

        /// <summary>Размер текста: одной строкой (width = 0) или с переносом по ширине.</summary>
        public static Vector2 Measure(string text, int size, bool bold, bool rich, float width)
        {
            if (measurer == null)
            {
                // Объект активен (у выключенного TMP не просыпается и меряет неверно — 1 строку вместо абзаца),
                // но без холста — ничего не рисует.
                var go = new GameObject("Замер текста") { hideFlags = HideFlags.HideAndDontSave };
                measurer = Add(go);
            }
            measurer.fontSize = size;
            measurer.fontStyle = bold ? FontStyle.Bold : FontStyle.Normal;
            measurer.richText = rich;
            measurer.textWrappingMode = width > 0f ? TextWrappingModes.Normal : TextWrappingModes.NoWrap;
            return measurer.GetPreferredValues(text ?? "", width > 0f ? width : 100000f, 100000f);
        }
    }
}
