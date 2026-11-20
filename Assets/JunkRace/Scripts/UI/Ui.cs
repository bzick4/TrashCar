using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace JunkRace
{
    /// <summary>
    /// Набор деталей интерфейса на UGUI, собираемых из кода (как и всё в проекте — без префабов).
    /// Холст под 1080p: 1 единица = 1 пиксель экрана высотой 1080, поэтому размеры и шрифты
    /// совпадают с прежним IMGUI-меню. Цвета UGUI в Linear выводит точно, без выцветания IMGUI.
    /// </summary>
    public static class Ui
    {
        public static readonly Color Accent = new Color(1f, 0.62f, 0.15f);
        public static readonly Color Ink = new Color(0.17f, 0.10f, 0.06f);      // тёмный текст на оранжевом и бежевом
        public static readonly Color InkSoft = new Color(0.36f, 0.26f, 0.20f);
        public static readonly Color Cream = new Color(0.98f, 0.93f, 0.82f);
        public static readonly Color HintColor = new Color(0.7f, 0.67f, 0.6f);
        public static readonly Color ErrorColor = new Color(1f, 0.4f, 0.35f);
        public static readonly Color ReadyColor = new Color(0.45f, 0.9f, 0.4f);

        static Font font;

        /// <summary>
        /// Шрифт всего интерфейса: первый из Resources/Fonts (сейчас Noto Sans — с кириллицей).
        /// Другой шрифт — положить его в ту папку вместо этого. Нет ни одного — встроенный.
        /// </summary>
        public static Font Font
        {
            get
            {
                if (font != null) return font;
                var fonts = Resources.LoadAll<Font>("Fonts");
                font = fonts.Length > 0 ? fonts[0] : Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                return font;
            }
        }

        // ======================= Холст =======================

        /// <summary>Холст поверх экрана, масштаб под высоту 1080. order — порядок среди холстов.</summary>
        public static RectTransform Canvas(string name, int order, Transform parent = null)
        {
            var go = new GameObject(name, typeof(UnityEngine.Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            if (parent != null) go.transform.SetParent(parent, false);
            var canvas = go.GetComponent<UnityEngine.Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = order;
            var scaler = go.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 1f;   // как прежнее меню: всё по высоте 1080
            scaler.referencePixelsPerUnit = 100f;
            EnsureEventSystem();
            return (RectTransform)go.transform;
        }

        /// <summary>Нужен для кликов. Один на сцену; с новой системой ввода — её модуль.</summary>
        public static void EnsureEventSystem()
        {
            if (EventSystem.current != null || UnityEngine.Object.FindFirstObjectByType<EventSystem>() != null) return;
            var go = new GameObject("EventSystem", typeof(EventSystem));
            go.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
        }

        // ======================= Картинки =======================

        static readonly Dictionary<(Texture2D, Vector4), UnityEngine.Sprite> sprites = new Dictionary<(Texture2D, Vector4), UnityEngine.Sprite>();

        /// <summary>Спрайт из картинки Resources/UI. border — 9-slice в пикселях картинки (left, right, top, bottom, как у RectOffset).</summary>
        public static UnityEngine.Sprite Sprite(Texture2D tex, RectOffset border = null)
        {
            if (tex == null) return null;
            var b = border == null ? Vector4.zero : new Vector4(border.left, border.bottom, border.right, border.top);
            if (sprites.TryGetValue((tex, b), out var s) && s != null) return s;
            s = UnityEngine.Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, b);
            s.name = tex.name;
            sprites[(tex, b)] = s;
            return s;
        }

        public static Texture2D Load(string name) => Resources.Load<Texture2D>("UI/" + name);

        static readonly Dictionary<string, Texture2D> icons = new Dictionary<string, Texture2D>();

        /// <summary>Иконка из Resources/UI/icon_{name}. null, если нет.</summary>
        public static Texture2D Icon(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (!icons.TryGetValue(name, out var t)) icons[name] = t = Load("icon_" + name);
            return t;
        }

        // ======================= Основа =======================

        public static RectTransform Node(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        /// <summary>Растянуть на весь родитель с отступами.</summary>
        public static RectTransform Stretch(RectTransform rt, float left = 0f, float top = 0f, float right = 0f, float bottom = 0f)
        {
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.pivot = new Vector2(0.5f, 0.5f);
            rt.offsetMin = new Vector2(left, bottom); rt.offsetMax = new Vector2(-right, -top);
            return rt;
        }

        /// <summary>Прямоугольник от левого верхнего угла родителя (как Rect в IMGUI: y вниз).</summary>
        public static RectTransform Place(RectTransform rt, float x, float y, float w, float h)
        {
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, -y);
            rt.sizeDelta = new Vector2(w, h);
            return rt;
        }

        public static LayoutElement Size(Component c, float width = -1f, float height = -1f, float flexW = -1f, float flexH = -1f)
        {
            var le = c.GetComponent<LayoutElement>() ?? c.gameObject.AddComponent<LayoutElement>();
            if (width >= 0f) { le.preferredWidth = width; le.minWidth = width; }
            if (height >= 0f) { le.preferredHeight = height; le.minHeight = height; }
            if (flexW >= 0f) le.flexibleWidth = flexW;
            if (flexH >= 0f) le.flexibleHeight = flexH;
            return le;
        }

        public static UnityEngine.UI.Image Image(string name, Transform parent, UnityEngine.Sprite sprite, Color? color = null, bool sliced = false)
        {
            var img = Node(name, parent).gameObject.AddComponent<UnityEngine.UI.Image>();
            img.sprite = sprite;
            img.color = color ?? Color.white;
            img.type = sliced && sprite != null ? UnityEngine.UI.Image.Type.Sliced : UnityEngine.UI.Image.Type.Simple;
            img.raycastTarget = false;
            return img;
        }

        public static RawImage Raw(string name, Transform parent, Texture tex)
        {
            var img = Node(name, parent).gameObject.AddComponent<RawImage>();
            img.texture = tex;
            img.raycastTarget = false;
            return img;
        }

        // ======================= Раскладка =======================

        public static VerticalLayoutGroup Column(RectTransform rt, float spacing = 0f, RectOffset padding = null, TextAnchor align = TextAnchor.UpperLeft)
        {
            var g = rt.gameObject.AddComponent<VerticalLayoutGroup>();
            g.spacing = spacing;
            g.padding = padding ?? new RectOffset();
            g.childAlignment = align;
            g.childControlWidth = g.childControlHeight = true;
            g.childForceExpandWidth = false;
            g.childForceExpandHeight = false;
            return g;
        }

        public static HorizontalLayoutGroup Row(RectTransform rt, float spacing = 0f, RectOffset padding = null, TextAnchor align = TextAnchor.MiddleLeft)
        {
            var g = rt.gameObject.AddComponent<HorizontalLayoutGroup>();
            g.spacing = spacing;
            g.padding = padding ?? new RectOffset();
            g.childAlignment = align;
            g.childControlWidth = g.childControlHeight = true;
            g.childForceExpandWidth = false;
            g.childForceExpandHeight = false;
            return g;
        }

        /// <summary>Пустое место в раскладке; flexible — растягивается (как GUILayout.FlexibleSpace).</summary>
        public static RectTransform Space(Transform parent, float size, bool flexible = false)
        {
            var rt = Node("Space", parent);
            var le = rt.gameObject.AddComponent<LayoutElement>();
            le.minHeight = le.preferredHeight = size;
            le.minWidth = le.preferredWidth = size;
            if (flexible) le.flexibleHeight = le.flexibleWidth = 1f;
            return rt;
        }

        // ======================= Текст =======================

        public static Text Label(Transform parent, string text, int size, Color color, bool bold = false,
                                 TextAnchor align = TextAnchor.MiddleLeft, bool wrap = false, bool shadow = false)
        {
            var t = Node("Text", parent).gameObject.AddComponent<Text>();
            t.font = Font;
            t.text = text;
            t.fontSize = size;
            t.fontStyle = bold ? FontStyle.Bold : FontStyle.Normal;
            t.color = color;
            t.alignment = align;
            t.horizontalOverflow = wrap ? HorizontalWrapMode.Wrap : HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.raycastTarget = false;
            t.supportRichText = true;
            if (shadow)
            {
                var sh = t.gameObject.AddComponent<Shadow>();
                sh.effectColor = new Color(0f, 0f, 0f, 0.85f);
                sh.effectDistance = new Vector2(2f, -2f);
            }
            return t;
        }

        public static Text Header(Transform p, string text) => Label(p, text, 46, Accent, true);
        public static Text Body(Transform p, string text) => Label(p, text, 28, Color.white);
        public static Text Hint(Transform p, string text) => Label(p, text, 21, HintColor, wrap: true);
        public static Text Error(Transform p) => Label(p, "", 24, ErrorColor, wrap: true);

        // ======================= Кнопки =======================

        public sealed class Skin
        {
            public UnityEngine.Sprite Normal, Hover, Pressed;
            public float Height;
            public int FontSize;
            public RectOffset Padding;
            public Color TextColor;
        }

        static Skin big, small;

        /// <summary>Большая кнопка (btn_*.png).</summary>
        public static Skin Big => big ??= MakeSkin("btn", new RectOffset(56, 40, 34, 36), 92f, 32, new RectOffset(34, 30, 20, 22));

        /// <summary>Малая кнопка (btn_small_*.png).</summary>
        public static Skin Small => small ??= MakeSkin("btn_small", new RectOffset(30, 30, 26, 28), 70f, 24, new RectOffset(20, 20, 18, 20));

        static Skin MakeSkin(string prefix, RectOffset border, float height, int fontSize, RectOffset padding)
        {
            var n = Load(prefix + "_normal");
            return new Skin
            {
                Normal = Sprite(n, border),
                Hover = Sprite(Load(prefix + "_hover") ?? n, border),
                Pressed = Sprite(Load(prefix + "_pressed") ?? n, border),
                Height = height,
                FontSize = fontSize,
                Padding = padding,
                TextColor = n != null ? Ink : Color.white,
            };
        }

        public sealed class UiButton
        {
            public UnityEngine.UI.Button Button;
            public Text Text;
            public UnityEngine.UI.Image Icon;
            public RectTransform Rect;
            public bool Interactable { get => Button.interactable; set => Button.interactable = value; }
        }

        /// <summary>Кнопка на картинке: иконка слева от подписи, обе по центру. width ≤ 0 — по раскладке.</summary>
        public static UiButton Button(Transform parent, string text, string icon, Skin skin, Action onClick, float width = -1f)
        {
            var rt = Node("Button_" + text, parent);
            var bg = rt.gameObject.AddComponent<UnityEngine.UI.Image>();
            bg.sprite = skin.Normal;
            bg.type = skin.Normal != null ? UnityEngine.UI.Image.Type.Sliced : UnityEngine.UI.Image.Type.Simple;
            bg.color = skin.Normal != null ? Color.white : new Color(0.3f, 0.27f, 0.24f);
            var b = rt.gameObject.AddComponent<UnityEngine.UI.Button>();
            b.targetGraphic = bg;
            if (skin.Normal != null)
            {
                b.transition = Selectable.Transition.SpriteSwap;
                b.spriteState = new SpriteState { highlightedSprite = skin.Hover, pressedSprite = skin.Pressed, selectedSprite = skin.Normal, disabledSprite = skin.Normal };
            }
            var nav = b.navigation; nav.mode = Navigation.Mode.None; b.navigation = nav;   // без «залипшего» выделения
            if (onClick != null) b.onClick.AddListener(() => onClick());
            Size(rt, width, skin.Height);

            // Содержимое: иконка + текст, по центру внутри отступов.
            var content = Stretch(Node("Content", rt), skin.Padding.left, skin.Padding.top, skin.Padding.right, skin.Padding.bottom);
            var row = Row(content, 10f, null, TextAnchor.MiddleCenter);
            row.childControlWidth = row.childControlHeight = true;
            var result = new UiButton { Button = b, Rect = rt };
            var iconTex = skin.Normal != null ? Icon(icon) : null;
            if (iconTex != null)
            {
                float s = skin.Height - skin.Padding.top - skin.Padding.bottom;
                result.Icon = Image("Icon", content, Sprite(iconTex));
                result.Icon.preserveAspect = true;
                Size(result.Icon, s * iconTex.width / Mathf.Max(1, iconTex.height), s);
            }
            result.Text = Label(content, text, skin.FontSize, skin.TextColor, true, TextAnchor.MiddleCenter);
            return result;
        }

        // ======================= Поле ввода =======================

        static UnityEngine.Sprite fieldSprite;
        static bool fieldLoaded;

        /// <summary>Поле ввода (input_field.png). password — звёздочки.</summary>
        public static InputField Field(Transform parent, string value, int maxLength, Action<string> onChange, bool password = false)
        {
            if (!fieldLoaded) { fieldLoaded = true; fieldSprite = Sprite(Load("input_field"), new RectOffset(26, 26, 22, 22)); }
            var rt = Node("Field", parent);
            var bg = rt.gameObject.AddComponent<UnityEngine.UI.Image>();
            bg.sprite = fieldSprite;
            bg.type = fieldSprite != null ? UnityEngine.UI.Image.Type.Sliced : UnityEngine.UI.Image.Type.Simple;
            bg.color = fieldSprite != null ? Color.white : new Color(0.15f, 0.14f, 0.12f);
            Size(rt, -1f, 64f, 1f);
            var text = Label(Stretch(Node("TextArea", rt), 26f, 12f, 26f, 10f), "", 28, Cream);
            Stretch(text.rectTransform);
            text.supportRichText = false;
            var f = rt.gameObject.AddComponent<InputField>();
            f.targetGraphic = bg;
            f.textComponent = text;
            f.characterLimit = maxLength;
            f.contentType = password ? InputField.ContentType.Password : InputField.ContentType.Standard;
            f.asteriskChar = '*';
            f.caretColor = Cream;
            f.customCaretColor = true;
            f.selectionColor = new Color(1f, 0.62f, 0.15f, 0.45f);
            f.text = value ?? "";
            var nav = f.navigation; nav.mode = Navigation.Mode.None; f.navigation = nav;
            if (onChange != null) f.onValueChanged.AddListener(v => onChange(v));
            return f;
        }

        // ======================= Панели =======================

        /// <summary>Картинка-подложка с 9-slice; без картинки — однотонная.</summary>
        public static UnityEngine.UI.Image Plate(RectTransform rt, Texture2D tex, RectOffset border, Color fallback)
        {
            var img = rt.gameObject.AddComponent<UnityEngine.UI.Image>();
            img.sprite = Sprite(tex, border);
            img.type = img.sprite != null ? UnityEngine.UI.Image.Type.Sliced : UnityEngine.UI.Image.Type.Simple;
            img.color = img.sprite != null ? Color.white : fallback;
            img.raycastTarget = false;
            return img;
        }

        // ======================= Ползунок =======================

        public static UnityEngine.UI.Slider Slider(Transform parent, float value, Action<float> onChange, float width)
        {
            var rt = Node("Slider", parent);
            Size(rt, width, 28f);
            var rail = Image("Rail", rt, null, new Color(0.08f, 0.07f, 0.06f, 0.85f));
            Stretch(rail.rectTransform, 0f, 9f, 0f, 9f);
            var fillArea = Stretch(Node("FillArea", rt), 0f, 9f, 0f, 9f);
            var fill = Image("Fill", fillArea, null, Accent);
            fill.rectTransform.sizeDelta = Vector2.zero;
            var handleArea = Stretch(Node("HandleArea", rt), 12f, 0f, 12f, 0f);
            var handle = Image("Handle", handleArea, null, Cream);
            handle.rectTransform.sizeDelta = new Vector2(24f, 0f);
            handle.raycastTarget = true;
            var s = rt.gameObject.AddComponent<UnityEngine.UI.Slider>();
            s.fillRect = fill.rectTransform;
            s.handleRect = handle.rectTransform;
            s.targetGraphic = handle;
            s.direction = UnityEngine.UI.Slider.Direction.LeftToRight;
            s.minValue = 0f; s.maxValue = 1f;
            s.value = value;
            var nav = s.navigation; nav.mode = Navigation.Mode.None; s.navigation = nav;
            rail.raycastTarget = true;   // клик по рельсу двигает ползунок
            if (onChange != null) s.onValueChanged.AddListener(v => onChange(v));
            return s;
        }

        /// <summary>Ползунки громкости (общая, музыка, двигатель, эффекты): подпись с процентом и ползунок.</summary>
        public static void VolumeSliders(Transform parent, float width)
        {
            for (int i = 0; i < GameAudio.Names.Length; i++)
            {
                var c = (GameAudio.Channel)i;
                var label = Body(parent, "");
                string name = GameAudio.Names[i];
                void Show(float v) => label.text = $"{name}: {Mathf.RoundToInt(v * 100f)}%";
                Show(GameAudio.Get(c));
                Slider(parent, GameAudio.Get(c), v => { GameAudio.Set(c, v); Show(v); }, width);
                Space(parent, 14f);
            }
        }

        // ======================= Список с прокруткой =======================

        /// <summary>Прокручиваемый список: возвращает Content, куда класть строки (вертикально, по ширине).</summary>
        public static RectTransform Scroll(Transform parent, float height, Color background)
        {
            var root = Node("Scroll", parent);
            Size(root, -1f, height, 1f);
            var bg = root.gameObject.AddComponent<UnityEngine.UI.Image>();
            bg.color = background;
            var viewport = Stretch(Node("Viewport", root), 8f, 8f, 8f, 8f);
            viewport.gameObject.AddComponent<RectMask2D>();
            var vpImage = viewport.gameObject.AddComponent<UnityEngine.UI.Image>();   // ловит прокрутку колесом
            vpImage.color = new Color(0f, 0f, 0f, 0f);
            var content = Node("Content", viewport);
            content.anchorMin = new Vector2(0f, 1f); content.anchorMax = new Vector2(1f, 1f); content.pivot = new Vector2(0.5f, 1f);
            content.offsetMin = content.offsetMax = Vector2.zero;
            var col = Column(content, 0f);
            col.childForceExpandWidth = true;
            var fit = content.gameObject.AddComponent<ContentSizeFitter>();
            fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            var sr = root.gameObject.AddComponent<ScrollRect>();
            sr.viewport = viewport;
            sr.content = content;
            sr.horizontal = false;
            sr.movementType = ScrollRect.MovementType.Clamped;
            sr.scrollSensitivity = 30f;
            return content;
        }

        public static void Clear(Transform t)
        {
            for (int i = t.childCount - 1; i >= 0; i--)
            {
                var go = t.GetChild(i).gameObject;
                go.SetActive(false);   // Destroy срабатывает в конце кадра — до этого не мешать раскладке
                UnityEngine.Object.Destroy(go);
            }
        }
    }
}
