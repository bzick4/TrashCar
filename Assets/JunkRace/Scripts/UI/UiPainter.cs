using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace JunkRace
{
    /// <summary>
    /// «Рисование по кадру» поверх UGUI — замена OnGUI для HUD (IMGUI в Linear выбеливает цвета).
    /// Код каждый кадр между Begin() и End() зовёт Label/Tex/Sliced/Box/Button с прямоугольниками
    /// в пикселях экрана (от левого верхнего угла, y вниз — как в OnGUI). Рисовалка берёт объекты UGUI
    /// из пула и выставляет им место, размер и цвет; лишние прячет. Порядок вызовов = порядок слоёв.
    ///
    /// Scale — как GUI.matrix со сдвигом в ноль: прямоугольники, шрифты и рамки умножаются на него
    /// (код, рассчитанный на 1080 по высоте, ставит Scale = Screen.height / 1080).
    /// Все слои живут на одном холсте «HUD» (свой у каждой сцены), order — порядок слоя.
    /// </summary>
    public sealed class UiPainter
    {
        public struct Style
        {
            public int Size;
            public Color Color;
            public bool Bold, Wrap, Rich;
            public TextAnchor Align;

            public Style(int size, Color color, TextAnchor align = TextAnchor.UpperLeft, bool bold = false, bool wrap = true, bool rich = true)
            {
                Size = size; Color = color; Align = align; Bold = bold; Wrap = wrap; Rich = rich;
            }

            public Style With(int size) { var s = this; s.Size = size; return s; }
            public Style With(Color color) { var s = this; s.Color = color; return s; }
            public Style With(TextAnchor align) { var s = this; s.Align = align; return s; }
            public Style WithBold(bool bold) { var s = this; s.Bold = bold; return s; }
        }

        static RectTransform canvas;   // общий холст HUD текущей сцены

        RectTransform layer;
        readonly string name;
        readonly int order;
        readonly List<Graphic> items = new List<Graphic>();
        readonly List<Kind> kinds = new List<Kind>();
        readonly List<Button> buttons = new List<Button>();
        readonly HashSet<Button> clicked = new HashSet<Button>();
        int used;
        public float Scale = 1f;

        enum Kind { Text, Raw, Image, Button }

        /// <summary>Новый слой. order — выше число, выше слой (среди слоёв HUD).</summary>
        public UiPainter(string name, int order)
        {
            this.name = name;
            this.order = order;
            EnsureLayer();
        }

        /// <summary>Холст HUD — свой у каждой сцены: после смены сцены слой создаётся заново (пул — тоже).</summary>
        void EnsureLayer()
        {
            if (layer != null) return;
            items.Clear(); kinds.Clear(); buttons.Clear(); clicked.Clear();
            if (canvas == null)
            {
                var go = new GameObject("HUD", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
                var c = go.GetComponent<Canvas>();
                c.renderMode = RenderMode.ScreenSpaceOverlay;
                c.sortingOrder = 10;
                go.GetComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;   // единица = пиксель, как в OnGUI
                canvas = (RectTransform)go.transform;
                Ui.EnsureEventSystem();
            }
            layer = Ui.Stretch(Ui.Node(name, canvas));
            layer.gameObject.AddComponent<Order>().Value = order;
            Order.Sort(canvas);
        }

        /// <summary>Порядок слоя на холсте HUD.</summary>
        sealed class Order : MonoBehaviour
        {
            public int Value;

            public static void Sort(Transform canvas)
            {
                var list = new List<Order>(canvas.GetComponentsInChildren<Order>(true));
                list.Sort((a, b) => a.Value.CompareTo(b.Value));
                for (int i = 0; i < list.Count; i++) list[i].transform.SetSiblingIndex(i);
            }
        }

        public void Destroy()
        {
            if (layer != null) Object.Destroy(layer.gameObject);
        }

        public bool Visible
        {
            set { if (layer != null && layer.gameObject.activeSelf != value) layer.gameObject.SetActive(value); }
        }

        public void Begin()
        {
            EnsureLayer();
            used = 0;
            Scale = 1f;
        }

        public void End()
        {
            for (int i = used; i < items.Count; i++)
                if (items[i].gameObject.activeSelf) items[i].gameObject.SetActive(false);
            clicked.Clear();
        }

        // ======================= Рисование =======================

        public Text Label(Rect r, string text, Style st, float scale = 1f)
        {
            var t = (Text)Next(Kind.Text, r, scale);
            if (t.text != text) t.text = text;
            int size = Mathf.Max(1, Mathf.RoundToInt(st.Size * Scale));
            if (t.fontSize != size) t.fontSize = size;
            var fs = st.Bold ? FontStyle.Bold : FontStyle.Normal;
            if (t.fontStyle != fs) t.fontStyle = fs;
            if (t.alignment != st.Align) t.alignment = st.Align;
            var wrap = st.Wrap ? HorizontalWrapMode.Wrap : HorizontalWrapMode.Overflow;
            if (t.horizontalOverflow != wrap) t.horizontalOverflow = wrap;
            if (t.supportRichText != st.Rich) t.supportRichText = st.Rich;
            t.color = st.Color;
            return t;
        }

        public void Tex(Rect r, Texture tex, Color? tint = null, float scale = 1f) => Tex(r, tex, new Rect(0f, 0f, 1f, 1f), tint, scale);

        public void Tex(Rect r, Texture tex, Rect uv, Color? tint = null, float scale = 1f)
        {
            if (tex == null) return;
            var img = (RawImage)Next(Kind.Raw, r, scale);
            if (img.texture != tex) img.texture = tex;
            if (img.uvRect != uv) img.uvRect = uv;
            img.color = tint ?? Color.white;
        }

        /// <summary>
        /// Картинка с 9-slice: border — в пикселях картинки; borderScale — во сколько раз рисовать рамку
        /// (как GUI.Box под матрицей масштаба). Середина тянется.
        /// </summary>
        public void Sliced(Rect r, Texture2D tex, RectOffset border, float borderScale, Color? tint = null, float scale = 1f)
        {
            if (tex == null) return;
            var img = (Image)Next(Kind.Image, r, scale);
            var sprite = Ui.Sprite(tex, border);
            if (img.sprite != sprite) img.sprite = sprite;
            if (img.type != Image.Type.Sliced) img.type = Image.Type.Sliced;
            float mult = 1f / Mathf.Max(0.0001f, borderScale * Scale);
            if (!Mathf.Approximately(img.pixelsPerUnitMultiplier, mult)) img.pixelsPerUnitMultiplier = mult;
            img.color = tint ?? Color.white;
        }

        /// <summary>Однотонный прямоугольник.</summary>
        public void Box(Rect r, Color color)
        {
            var img = (Image)Next(Kind.Image, r, 1f);
            if (img.sprite != null) img.sprite = null;
            if (img.type != Image.Type.Simple) img.type = Image.Type.Simple;
            img.color = color;
        }

        /// <summary>Кнопка на картинке малой кнопки меню. true — нажали с прошлого кадра.</summary>
        public bool Button(Rect r, string text, int fontSize = 24)
        {
            var img = (Image)Next(Kind.Button, r, 1f);
            var b = img.GetComponent<Button>();
            var t = img.GetComponentInChildren<Text>(true);
            if (t.text != text) t.text = text;
            int size = Mathf.RoundToInt(fontSize * Scale);
            if (t.fontSize != size) t.fontSize = size;
            return clicked.Remove(b);
        }

        // ======================= Размер текста =======================

        static TextGenerator generator;

        /// <summary>Размер текста стилем st в одну строку (как GUIStyle.CalcSize) — в единицах прямоугольников (до Scale).</summary>
        public Vector2 Measure(string text, Style st)
        {
            var s = Settings(st, HorizontalWrapMode.Overflow, 10000f);
            return new Vector2(generator.GetPreferredWidth(text, s), generator.GetPreferredHeight(text, s)) / Scale;
        }

        /// <summary>Высота текста с переносом по ширине width (как GUIStyle.CalcHeight) — в единицах прямоугольников.</summary>
        public float MeasureHeight(string text, Style st, float width)
        {
            var s = Settings(st, HorizontalWrapMode.Wrap, width * Scale);
            return generator.GetPreferredHeight(text, s) / Scale;
        }

        TextGenerationSettings Settings(Style st, HorizontalWrapMode wrap, float width)
        {
            generator ??= new TextGenerator();
            return new TextGenerationSettings
            {
                font = Ui.Font,
                fontSize = Mathf.Max(1, Mathf.RoundToInt(st.Size * Scale)),
                fontStyle = st.Bold ? FontStyle.Bold : FontStyle.Normal,
                richText = st.Rich,
                scaleFactor = 1f,
                lineSpacing = 1f,
                textAnchor = TextAnchor.UpperLeft,
                horizontalOverflow = wrap,
                verticalOverflow = VerticalWrapMode.Overflow,
                generationExtents = new Vector2(width, 10000f),
                color = Color.white,
                updateBounds = true,
                pivot = Vector2.zero,
            };
        }

        // ======================= Пул =======================

        Graphic Next(Kind kind, Rect r, float scale)
        {
            // Ищем в пуле объект нужного вида с текущего места; нет — создаём и вставляем.
            int i = used;
            while (i < items.Count && kinds[i] != kind) i++;
            Graphic g;
            if (i < items.Count)
            {
                g = items[i];
                if (i != used)
                {
                    items.RemoveAt(i); kinds.RemoveAt(i);
                    items.Insert(used, g); kinds.Insert(used, kind);
                }
            }
            else
            {
                g = Create(kind);
                items.Insert(used, g); kinds.Insert(used, kind);
            }
            if (g.transform.GetSiblingIndex() != used) g.transform.SetSiblingIndex(used);
            used++;
            if (!g.gameObject.activeSelf) g.gameObject.SetActive(true);

            var rt = g.rectTransform;
            float k = Scale;
            var pos = new Vector2((r.x + r.width * 0.5f) * k, -(r.y + r.height * 0.5f) * k);
            var size = new Vector2(r.width * k, r.height * k);
            if (rt.anchoredPosition != pos) rt.anchoredPosition = pos;
            if (rt.sizeDelta != size) rt.sizeDelta = size;
            var sc = new Vector3(scale, scale, 1f);
            if (rt.localScale != sc) rt.localScale = sc;
            return g;
        }

        Graphic Create(Kind kind)
        {
            var rt = Ui.Node(kind.ToString(), layer);
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            Graphic g;
            switch (kind)
            {
                case Kind.Text:
                {
                    var t = rt.gameObject.AddComponent<Text>();
                    t.font = Ui.Font;
                    t.verticalOverflow = VerticalWrapMode.Overflow;
                    t.raycastTarget = false;
                    g = t;
                    break;
                }
                case Kind.Raw:
                    g = rt.gameObject.AddComponent<RawImage>();
                    g.raycastTarget = false;
                    break;
                case Kind.Button:
                {
                    var skin = Ui.Small;
                    var img = rt.gameObject.AddComponent<Image>();
                    img.sprite = skin.Normal;
                    img.type = skin.Normal != null ? Image.Type.Sliced : Image.Type.Simple;
                    img.color = skin.Normal != null ? Color.white : new Color(0.3f, 0.27f, 0.24f);
                    var b = rt.gameObject.AddComponent<Button>();
                    b.targetGraphic = img;
                    if (skin.Normal != null)
                    {
                        b.transition = Selectable.Transition.SpriteSwap;
                        b.spriteState = new SpriteState { highlightedSprite = skin.Hover, pressedSprite = skin.Pressed, selectedSprite = skin.Normal };
                    }
                    var nav = b.navigation; nav.mode = Navigation.Mode.None; b.navigation = nav;
                    b.onClick.AddListener(() => clicked.Add(b));
                    buttons.Add(b);
                    var label = Ui.Label(rt, "", skin.FontSize, skin.TextColor, true, TextAnchor.MiddleCenter);
                    Ui.Stretch(label.rectTransform);
                    g = img;
                    break;
                }
                default:
                    g = rt.gameObject.AddComponent<Image>();
                    g.raycastTarget = false;
                    break;
            }
            return g;
        }
    }
}
