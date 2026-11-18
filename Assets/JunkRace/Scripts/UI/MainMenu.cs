using System.Collections.Generic;
using UnityEngine;

namespace JunkRace
{
    /// <summary>
    /// Главное меню на IMGUI: ноль настройки сцены, всё работает из коробки.
    /// Сеть живёт в CoopSession, так что позже экран можно переделать на UI Toolkit, не трогая сеть.
    /// </summary>
    public class MainMenu : MonoBehaviour
    {
        enum Screen { Main, Browser, Create, Password, Code, Busy, Lobby }

        const float AutoRefreshInterval = 4f;

        Screen screen = Screen.Main;
        string playerName = "";
        string serverName = "";
        string createPassword = "";
        string joinPassword = "";
        string joinCode = "";
        string message;
        ServerInfo joinTarget;
        Vector2 scroll;

        List<ServerInfo> servers = new List<ServerInfo>();
        bool refreshing;
        float lastRefresh = -100f;

        CoopSession Session => CoopSession.Instance;

        // Анимированные иконки главного экрана (колесо у «Соло», куча у «Кооп»).
        MenuIconFx fx;
        Rect soloRect, coopRect;     // прямоугольники кнопок внутри области главного экрана
        Vector2 mainAreaOrigin;
        Screen lastDrawnScreen = Screen.Main;

        void Start()
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            Time.timeScale = 1f;
            playerName = GameSession.PlayerName;
            message = GameSession.PendingMessage;
            GameSession.PendingMessage = null;
            if (!string.IsNullOrEmpty(message)) screen = Screen.Browser;   // вернулись после дисконнекта — сразу в список
            if (Session != null) Session.Failed += OnSessionFailed;
            fx = new MenuIconFx();
        }

        void OnDestroy()
        {
            fx?.Dispose();   // убить твины DOTween, которые крутят иконки
            if (Session != null) Session.Failed -= OnSessionFailed;
        }

        void OnSessionFailed(string reason)
        {
            message = reason;
            if (screen == Screen.Busy || screen == Screen.Lobby) screen = Screen.Browser;
        }

        void Update()
        {
            fx?.Update(Time.unscaledDeltaTime);
            if (Session == null) return;

            switch (Session.Current)
            {
                case CoopSession.State.Busy: screen = Screen.Busy; break;
                case CoopSession.State.InLobby: screen = Screen.Lobby; break;
                case CoopSession.State.Idle:
                    if (screen == Screen.Busy || screen == Screen.Lobby) screen = Screen.Browser;
                    break;
            }

            if (screen == Screen.Browser && !refreshing && Time.unscaledTime - lastRefresh > AutoRefreshInterval)
                RefreshServers();
        }

        async void RefreshServers()
        {
            if (refreshing || Session == null) return;
            refreshing = true;
            lastRefresh = Time.unscaledTime;
            var list = await Session.QueryAsync();
            if (this == null) return;   // сцену успели сменить
            servers = list;
            refreshing = false;
            lastRefresh = Time.unscaledTime;
        }

        // ================== Экраны ==================

        void OnGUI()
        {
            MenuStyles.Ensure();
            float scale = UnityEngine.Screen.height / 1080f;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
            float w = UnityEngine.Screen.width / scale;

            if (MenuStyles.MenuBackground != null)
                GUI.DrawTexture(new Rect(0, 0, w, 1080), MenuStyles.MenuBackground, ScaleMode.ScaleAndCrop);
            else
                GUI.DrawTexture(new Rect(0, 0, w, 1080), MenuStyles.BackgroundTex);

            if (screen == Screen.Main && lastDrawnScreen != Screen.Main) fx?.Reset();
            lastDrawnScreen = screen;

            if (screen == Screen.Main && MenuStyles.HasArt)
            {
                // Главный экран: логотип и кнопки прямо поверх арта, свалка справа остаётся видна.
                mainAreaOrigin = new Vector2(Mathf.Max(40f, w * 0.06f), 40f);
                GUILayout.BeginArea(new Rect(mainAreaOrigin.x, mainAreaOrigin.y, 640f, 1000f));
                DrawMain();
                GUILayout.EndArea();

                // Иконки рисуем вне области, чтобы разлетающиеся детали не обрезались.
                if (fx != null && fx.Available)
                {
                    var solo = new Rect(soloRect.position + mainAreaOrigin, soloRect.size);
                    var coop = new Rect(coopRect.position + mainAreaOrigin, coopRect.size);
                    Vector2 mouse = Event.current.mousePosition;
                    fx.WheelHovered = !fx.Busy && solo.Contains(mouse);
                    fx.PileHovered = !fx.Busy && coop.Contains(mouse);
                    fx.Draw(solo, coop);
                }
                return;
            }

            // Остальные экраны: панель слева (по центру — если нет арта, как было раньше).
            float panelX = MenuStyles.HasArt ? Mathf.Max(40f, w * 0.06f) : (w - 760f) / 2f;
            var panel = new Rect(panelX, 70f, 780f, 940f);
            GUI.Box(panel, GUIContent.none, MenuStyles.Panel);
            float inset = MenuStyles.HasArt ? 74f : 40f;
            GUILayout.BeginArea(new Rect(panel.x + inset, panel.y + inset, panel.width - inset * 2f, panel.height - inset * 2f));

            switch (screen)
            {
                case Screen.Main: DrawMain(); break;
                case Screen.Browser: DrawBrowser(); break;
                case Screen.Create: DrawCreate(); break;
                case Screen.Password: DrawPassword(); break;
                case Screen.Code: DrawCode(); break;
                case Screen.Busy: DrawBusy(); break;
                case Screen.Lobby: DrawLobby(); break;
            }

            if (!string.IsNullOrEmpty(message))
            {
                GUILayout.Space(10);
                GUILayout.Label(message, MenuStyles.Error);
            }

            GUILayout.EndArea();
        }

        void DrawMain()
        {
            bool art = MenuStyles.HasArt;
            if (MenuStyles.Logo != null)
            {
                var logoRect = GUILayoutUtility.GetRect(640f, 258f, GUILayout.Width(640f), GUILayout.Height(258f));
                GUI.DrawTexture(logoRect, MenuStyles.Logo, ScaleMode.ScaleToFit);
                MenuStyles.ShadowLabel("собери тачку из хлама и доедь", MenuStyles.ShadowHint);
                GUILayout.Space(30);
            }
            else
            {
                GUILayout.Space(40);
                GUILayout.Label("JUNK RACE", MenuStyles.Title);
                GUILayout.Label("собери тачку из хлама и доедь", MenuStyles.Subtitle);
                GUILayout.Space(70);
            }

            float bw = art ? 560f : 680f;
            bool animated = art && fx != null && fx.Available;
            // Пока играет анимация нажатия, нажатия игнорируем (кнопки не гасим, чтобы не мигали).
            bool busy = animated && fx.Busy;

            if (Button("Соло", "solo", GUILayout.Width(bw)) && !busy)
            {
                if (animated) fx.LaunchWheel(GameSession.StartSolo);
                else GameSession.StartSolo();
            }
            if (Event.current.type == EventType.Repaint) soloRect = GUILayoutUtility.GetLastRect();
            GUILayout.Space(8);
            if (Button("Тест-комната", "wrench", GUILayout.Width(bw)) && !busy) GameSession.StartDevRoom();
            HintOnArt("все детали рядами, сборка без таймера");
            GUILayout.Space(8);
            if (Button("Кооп", "coop", GUILayout.Width(bw)) && !busy)
            {
                message = Session == null ? "В сцене нет NetworkManager с CoopSession. Пересобери сцены через меню JunkRace." : null;
                if (Session != null)
                {
                    if (animated) fx.LaunchPile(() => { screen = Screen.Browser; lastRefresh = -100f; });
                    else { screen = Screen.Browser; lastRefresh = -100f; }
                }
            }
            if (Event.current.type == EventType.Repaint) coopRect = GUILayoutUtility.GetLastRect();
            GUILayout.Space(8);

            // Закрытый режим: та же кнопка, но серая и с замком.
            GUI.enabled = false;
            var prevColor = GUI.color;
            if (art) GUI.color = new Color(0.55f, 0.55f, 0.55f, 1f);
            Button("Ультра кооп", "lock", GUILayout.Width(bw));
            GUI.color = prevColor;
            GUI.enabled = true;
            HintOnArt("скоро: несколько команд на одной свалке");

            GUILayout.FlexibleSpace();
            if (Button("Выход", "back", GUILayout.Width(art ? 300f : bw)) && !busy) Application.Quit();
            if (art && !string.IsNullOrEmpty(message)) MenuStyles.ShadowLabel(message, MenuStyles.Error);
        }

        void DrawBrowser()
        {
            GUILayout.Label("Кооп", MenuStyles.Header);
            GUILayout.Label("Имя игрока", MenuStyles.Text);
            playerName = GUILayout.TextField(playerName, 16, MenuStyles.Field);
            GUILayout.Space(20);

            GUILayout.BeginHorizontal();
            GUILayout.Label(refreshing ? "Серверы (ищем...)" : "Серверы", MenuStyles.Text);
            GUILayout.FlexibleSpace();
            if (SmallButton("Обновить", "refresh", GUILayout.Width(220))) RefreshServers();
            GUILayout.EndHorizontal();

            scroll = GUILayout.BeginScrollView(scroll, MenuStyles.ListBox, GUILayout.Height(330));
            if (servers.Count == 0)
                GUILayout.Label(refreshing ? "Ищем серверы..." : "Пока никого. Создай свой сервер!", MenuStyles.Hint);
            else
                foreach (var s in servers) DrawServerRow(s);
            GUILayout.EndScrollView();

            GUILayout.Space(16);
            if (Button("Создать сервер", "key") && CheckName())
            {
                if (string.IsNullOrEmpty(serverName)) serverName = $"Сервер {playerName.Trim()}";
                message = null;
                screen = Screen.Create;
            }
            GUILayout.BeginHorizontal();
            if (SmallButton("Войти по коду", "copy") && CheckName())
            {
                message = null;
                joinPassword = "";
                screen = Screen.Code;
            }
            if (SmallButton("Назад", "back")) { message = null; screen = Screen.Main; }
            GUILayout.EndHorizontal();
        }

        void DrawServerRow(ServerInfo s)
        {
            GUILayout.BeginHorizontal(MenuStyles.Row);
            GUILayout.BeginVertical();
            var lockIcon = s.HasPassword ? MenuStyles.Icon("lock") : null;
            if (s.HasPassword && lockIcon == null)
                GUILayout.Label(s.Name + "   [пароль]", MenuStyles.RowText);
            else
                GUILayout.Label(new GUIContent(" " + s.Name, lockIcon), MenuStyles.RowText, GUILayout.Height(34));
            string status = s.Started ? "идёт игра" : s.IsFull ? "полон" : "ждут игроков";
            GUILayout.Label($"{s.Players}/{s.MaxPlayers}  ·  {status}", MenuStyles.RowHint);
            GUILayout.EndVertical();
            GUILayout.FlexibleSpace();

            GUI.enabled = !s.Started && !s.IsFull;
            if (GUILayout.Button("Войти", MenuStyles.SmallButton, GUILayout.Width(160)) && CheckName())
            {
                joinTarget = s;
                message = null;
                if (s.HasPassword) { joinPassword = ""; screen = Screen.Password; }
                else { SaveName(); Session.Join(playerName, s, null); }
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();
        }

        void DrawCreate()
        {
            GUILayout.Label("Новый сервер", MenuStyles.Header);
            GUILayout.Label("Название сервера", MenuStyles.Text);
            serverName = GUILayout.TextField(serverName, 24, MenuStyles.Field);
            GUILayout.Space(16);
            GUILayout.Label("Пароль (необязательно)", MenuStyles.Text);
            createPassword = PasswordRow(createPassword);
            GUILayout.Space(6);
            GUILayout.Label($"Пусто — заходят все. Иначе от {CoopSession.MinPasswordLength} символов.", MenuStyles.Hint);
            GUILayout.Space(30);
            GUILayout.Label($"Максимум игроков: {GameSession.CoopMaxPlayers}", MenuStyles.Hint);

            GUILayout.FlexibleSpace();
            if (Button("Создать", "key"))
            {
                if (createPassword.Length > 0 && createPassword.Length < CoopSession.MinPasswordLength)
                {
                    message = $"Пароль слишком короткий: нужно от {CoopSession.MinPasswordLength} символов.";
                }
                else
                {
                    SaveName();
                    message = null;
                    Session.Host(playerName, serverName, createPassword);
                }
            }
            if (Button("Назад", "back")) { message = null; screen = Screen.Browser; }
        }

        void DrawPassword()
        {
            GUILayout.Label(joinTarget.Name, MenuStyles.Header);
            GUILayout.Label("Сервер с паролем", MenuStyles.Text);
            joinPassword = PasswordRow(joinPassword);
            GUILayout.FlexibleSpace();
            if (Button("Подключиться"))
            {
                if (joinPassword.Length < CoopSession.MinPasswordLength)
                    message = $"Пароль — от {CoopSession.MinPasswordLength} символов.";
                else
                {
                    SaveName();
                    message = null;
                    Session.Join(playerName, joinTarget, joinPassword);
                }
            }
            if (Button("Назад", "back")) { message = null; screen = Screen.Browser; }
        }

        void DrawCode()
        {
            GUILayout.Label("Вход по коду", MenuStyles.Header);
            GUILayout.Label("Код сервера (хост видит его в лобби)", MenuStyles.Text);
            joinCode = GUILayout.TextField(joinCode, 12, MenuStyles.Field);
            GUILayout.Label("Пароль (если есть)", MenuStyles.Text);
            joinPassword = PasswordRow(joinPassword);

            GUILayout.FlexibleSpace();
            if (Button("Подключиться"))
            {
                if (joinCode.Trim().Length == 0) message = "Введи код.";
                else
                {
                    SaveName();
                    message = null;
                    Session.JoinByCode(playerName, joinCode, joinPassword);
                }
            }
            if (Button("Назад", "back")) { message = null; screen = Screen.Browser; }
        }

        void DrawBusy()
        {
            GUILayout.Label("Секунду...", MenuStyles.Header);
            GUILayout.Label(Session != null ? Session.BusyText : "", MenuStyles.Text);
        }

        void DrawLobby()
        {
            var s = Session;
            GUILayout.Label(s.ServerName, MenuStyles.Header);

            // Код для друзей на бирке + кнопка «скопировать».
            GUILayout.BeginHorizontal();
            GUILayout.Label($"Код: {s.JoinCode}", MenuStyles.Tag, GUILayout.Width(300));
            GUILayout.Space(8);
            if (SmallButton(copiedAt > Time.unscaledTime - 1.5f ? "Готово!" : "Копировать", "copy", GUILayout.Width(300)))
            {
                GUIUtility.systemCopyBuffer = s.JoinCode;
                copiedAt = Time.unscaledTime;
            }
            GUILayout.EndHorizontal();
            GUILayout.Label($"Игроки {s.Players.Count}/{GameSession.CoopMaxPlayers}", MenuStyles.Text);
            GUILayout.Space(6);

            var hostIcon = MenuStyles.Icon("host");
            var readyIcon = MenuStyles.Icon("ready");
            var notReadyIcon = MenuStyles.Icon("not_ready");
            for (int i = 0; i < GameSession.CoopMaxPlayers; i++)
            {
                GUILayout.BeginHorizontal(MenuStyles.Slot);
                if (i < s.Players.Count)
                {
                    if (i == 0 && hostIcon != null)
                        GUILayout.Label(new GUIContent(" " + s.Players[i], hostIcon), MenuStyles.SlotText, GUILayout.Height(44));
                    else
                        GUILayout.Label((i == 0 ? "[хост]  " : "") + s.Players[i], MenuStyles.SlotText, GUILayout.Height(44));
                    GUILayout.FlexibleSpace();
                    bool ready = i < s.PlayersReady.Count && s.PlayersReady[i];
                    var icon = ready ? readyIcon : notReadyIcon;
                    if (icon != null) GUILayout.Label(icon, GUILayout.Width(52), GUILayout.Height(44));
                    else GUILayout.Label(ready ? "готов" : "не готов", ready ? MenuStyles.Ready : MenuStyles.SlotHint);
                }
                else
                    GUILayout.Label("— свободно —", MenuStyles.SlotHint, GUILayout.Height(44));
                GUILayout.EndHorizontal();
            }

            GUILayout.Space(20);
            if (s.CountdownLeft >= 0f)
                GUILayout.Label($"Все готовы! Старт через {Mathf.CeilToInt(s.CountdownLeft)}...", MenuStyles.Subtitle);
            else if (s.AllReady)
                GUILayout.Label("Все готовы, ждём подключения по сети...", MenuStyles.Subtitle);
            else
                GUILayout.Label("Ждём, пока все нажмут «Готов»", MenuStyles.Subtitle);

            GUILayout.FlexibleSpace();
            if (Button(s.LocalReady ? "Не готов" : "Готов", s.LocalReady ? "not_ready" : "ready")) s.SetReady(!s.LocalReady);
            GUILayout.Label("Старт — когда все в лобби нажмут «Готов». Можно и одному.", MenuStyles.Hint);
            if (Button("Выйти", "back")) { s.Leave(); screen = Screen.Browser; lastRefresh = -100f; }
        }

        // ================== Хелперы ==================

        bool CheckName()
        {
            if (playerName.Trim().Length > 0) return true;
            message = "Сначала введи имя игрока.";
            return false;
        }

        void SaveName() => GameSession.PlayerName = playerName.Trim();

        float copiedAt = -100f;

        static bool Button(string text, params GUILayoutOption[] options) => GUILayout.Button(text, MenuStyles.Button, options);

        /// <summary>Большая кнопка с иконкой слева (если иконки нет — просто текст).</summary>
        static bool Button(string text, string icon, params GUILayoutOption[] options) =>
            GUILayout.Button(WithIcon(text, icon), MenuStyles.Button, options);

        static bool SmallButton(string text, string icon, params GUILayoutOption[] options) =>
            GUILayout.Button(WithIcon(text, icon), MenuStyles.SmallButton, options);

        static GUIContent WithIcon(string text, string icon)
        {
            var tex = MenuStyles.HasArt ? MenuStyles.Icon(icon) : null;
            return tex != null ? new GUIContent("  " + text, tex) : new GUIContent(text);
        }

        /// <summary>Подсказка на главном экране: поверх арта — с тенью, иначе как раньше.</summary>
        static void HintOnArt(string text)
        {
            if (MenuStyles.HasArt) MenuStyles.ShadowLabel(text, MenuStyles.ShadowHint);
            else GUILayout.Label(text, MenuStyles.Hint);
        }

        bool showPassword;

        /// <summary>
        /// Поле пароля во всю ширину и кнопка «показать» рядом. Это обычное текстовое поле с прозрачным
        /// текстом, а звёздочки рисуем поверх: у GUILayout.PasswordField при масштабе меню клик мимо поля.
        /// </summary>
        string PasswordRow(string value)
        {
            value ??= "";
            GUILayout.BeginHorizontal();
            value = GUILayout.TextField(value, CoopSession.MaxPasswordLength,
                                        showPassword ? MenuStyles.Field : MenuStyles.HiddenField, GUILayout.ExpandWidth(true));
            if (!showPassword && Event.current.type == EventType.Repaint && value.Length > 0)
                MenuStyles.FieldMask.Draw(GUILayoutUtility.GetLastRect(), new GUIContent(new string('*', value.Length)), false, false, false, false);
            GUILayout.Space(10);
            if (GUILayout.Button(showPassword ? "скрыть" : "показать", MenuStyles.SmallButton, GUILayout.Width(170)))
                showPassword = !showPassword;
            GUILayout.EndHorizontal();
            return value;
        }
    }

    /// <summary>
    /// Стили IMGUI-меню. Создаются один раз.
    /// Картинки берутся из Resources/UI (кнопки, панели, иконки, логотип, фон) и растягиваются по 9-slice
    /// через GUIStyle.border. Если какой-то картинки нет — остаётся старый однотонный вид, ничего не ломается.
    /// Размеры картинок подогнаны под экран 1080p (меню и так масштабируется под высоту 1080).
    /// </summary>
    static class MenuStyles
    {
        public static GUIStyle Title, Subtitle, Header, Text, Hint, Error, Ready, Button, SmallButton, Field, HiddenField, FieldMask,
                               Panel, Row, RowText, RowHint, Slot, SlotText, SlotHint, Tag, ListBox, ShadowHint;
        public static Texture2D BackgroundTex;   // однотонная подложка (затемнение в паузе)
        public static Texture2D MenuBackground;  // арт свалки, может быть null
        public static Texture2D Logo;            // может быть null
        public static bool HasArt;

        static readonly Dictionary<string, Texture2D> icons = new Dictionary<string, Texture2D>();
        static readonly Color Accent = new Color(1f, 0.62f, 0.15f);
        static readonly Color Ink = new Color(0.17f, 0.10f, 0.06f);       // тёмный текст на оранжевом и бежевом
        static readonly Color InkSoft = new Color(0.36f, 0.26f, 0.20f);
        static readonly Color Cream = new Color(0.98f, 0.93f, 0.82f);

        /// <summary>Иконка из Resources/UI/icon_{name}. null, если нет.</summary>
        public static Texture2D Icon(string name)
        {
            if (!icons.TryGetValue(name, out var t))
            {
                t = Resources.Load<Texture2D>("UI/icon_" + name);
                icons[name] = t;
            }
            return t;
        }

        public static void Ensure()
        {
            if (Title != null) return;

            BackgroundTex = Tex(new Color(0.12f, 0.11f, 0.10f));
            var panelTex = Tex(new Color(0.20f, 0.18f, 0.16f, 0.97f));
            var rowTex = Tex(new Color(0.27f, 0.24f, 0.21f));
            var listTex = Tex(new Color(0.15f, 0.14f, 0.12f));

            MenuBackground = Resources.Load<Texture2D>("UI/menu_background");
            Logo = Resources.Load<Texture2D>("UI/logo");
            var btnN = Load("btn_normal");
            HasArt = btnN != null;

            Title = new GUIStyle(GUI.skin.label) { fontSize = 84, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            Title.normal.textColor = Accent;
            Subtitle = new GUIStyle(GUI.skin.label) { fontSize = 28, alignment = TextAnchor.MiddleCenter, wordWrap = true };
            Subtitle.normal.textColor = new Color(0.9f, 0.85f, 0.75f);
            Header = new GUIStyle(GUI.skin.label) { fontSize = 46, fontStyle = FontStyle.Bold };
            Header.normal.textColor = Accent;
            Text = new GUIStyle(GUI.skin.label) { fontSize = 28 };
            Text.normal.textColor = Color.white;
            Hint = new GUIStyle(GUI.skin.label) { fontSize = 21, wordWrap = true };
            Hint.normal.textColor = new Color(0.7f, 0.67f, 0.6f);
            ShadowHint = new GUIStyle(Hint) { fontStyle = FontStyle.Bold };
            ShadowHint.normal.textColor = Cream;
            Ready = new GUIStyle(GUI.skin.label) { fontSize = 26, fontStyle = FontStyle.Bold };
            Ready.normal.textColor = new Color(0.45f, 0.9f, 0.4f);
            Error = new GUIStyle(GUI.skin.label) { fontSize = 24, wordWrap = true };
            Error.normal.textColor = new Color(1f, 0.4f, 0.35f);

            // ---- Кнопки ----
            Button = new GUIStyle(GUI.skin.button) { fontSize = 32, fixedHeight = 74, fontStyle = FontStyle.Bold };
            SmallButton = new GUIStyle(GUI.skin.button) { fontSize = 24, fixedHeight = 56 };
            if (HasArt)
            {
                Skin(Button, btnN, Load("btn_hover"), Load("btn_pressed"), new RectOffset(56, 40, 34, 36));
                Button.fixedHeight = 92;
                Button.padding = new RectOffset(34, 30, 20, 22);
                Button.clipping = TextClipping.Overflow;
                Button.margin = new RectOffset(0, 0, 6, 6);
                Button.imagePosition = ImagePosition.ImageLeft;
                SetText(Button, Ink);

                Skin(SmallButton, Load("btn_small_normal"), Load("btn_small_hover"), Load("btn_small_pressed"), new RectOffset(30, 30, 26, 28));
                SmallButton.fixedHeight = 70;
                SmallButton.fontStyle = FontStyle.Bold;
                SmallButton.padding = new RectOffset(20, 20, 18, 20);
                SmallButton.clipping = TextClipping.Overflow;
                SmallButton.margin = new RectOffset(4, 4, 4, 4);
                SetText(SmallButton, Ink);
            }

            // ---- Поля ввода ----
            Field = new GUIStyle(GUI.skin.textField) { fontSize = 28, fixedHeight = 54, padding = new RectOffset(12, 12, 10, 10) };
            var fieldTex = Load("input_field");
            if (fieldTex != null)
            {
                Skin(Field, fieldTex, fieldTex, fieldTex, new RectOffset(26, 26, 22, 22));
                Field.focused.background = Field.onFocused.background = fieldTex;
                Field.fixedHeight = 64;
                Field.padding = new RectOffset(26, 26, 16, 14);
                SetText(Field, Cream);
                Field.focused.textColor = Field.onFocused.textColor = Color.white;
            }
            // Поле пароля: тот же вид, но текст невидим (поверх рисуем звёздочки стилем FieldMask).
            HiddenField = new GUIStyle(Field);
            var clear = new Color(0f, 0f, 0f, 0f);
            HiddenField.normal.textColor = HiddenField.hover.textColor = HiddenField.focused.textColor = HiddenField.active.textColor = clear;
            HiddenField.onNormal.textColor = HiddenField.onHover.textColor = HiddenField.onFocused.textColor = HiddenField.onActive.textColor = clear;
            FieldMask = new GUIStyle(GUI.skin.label) { fontSize = 28, padding = Field.padding, alignment = Field.alignment, clipping = TextClipping.Clip };
            FieldMask.normal.textColor = Field.normal.textColor;

            // ---- Панель ----
            Panel = new GUIStyle(GUI.skin.box);
            var panelArt = Load("panel");
            Panel.normal.background = panelArt != null ? panelArt : panelTex;
            if (panelArt != null) Panel.border = new RectOffset(140, 140, 140, 140);

            // ---- Строка списка серверов (бежевая табличка, текст тёмный) ----
            Row = new GUIStyle(GUI.skin.box) { padding = new RectOffset(14, 14, 8, 8), margin = new RectOffset(0, 0, 4, 4) };
            var rowArt = Load("row_plate");
            Row.normal.background = rowArt != null ? rowArt : rowTex;
            RowText = new GUIStyle(Text);
            RowHint = new GUIStyle(Hint) { wordWrap = false };
            if (rowArt != null)
            {
                Row.border = new RectOffset(30, 125, 22, 22);
                Row.padding = new RectOffset(34, 34, 12, 12);
                Row.margin = new RectOffset(0, 0, 6, 6);
                RowText.fontStyle = FontStyle.Bold;
                RowText.normal.textColor = Ink;
                RowHint.normal.textColor = InkSoft;
            }

            // ---- Слот игрока в лобби (имя пишется в тёмной прорези) ----
            Slot = new GUIStyle(Row);
            SlotText = new GUIStyle(Text);
            SlotHint = new GUIStyle(Hint) { wordWrap = false };
            var slotArt = Load("player_slot");
            if (slotArt != null)
            {
                Slot.normal.background = slotArt;
                Slot.border = new RectOffset(80, 65, 22, 22);
                Slot.padding = new RectOffset(96, 72, 12, 12);
                Slot.fixedHeight = 72;
                Slot.margin = new RectOffset(0, 0, 3, 3);
                SlotText.fontStyle = FontStyle.Bold;
                SlotText.normal.textColor = Cream;
                SlotText.alignment = TextAnchor.MiddleLeft;
                SlotHint.normal.textColor = new Color(0.85f, 0.78f, 0.68f);
                SlotHint.alignment = TextAnchor.MiddleLeft;
            }

            // ---- Бирка (код сервера) ----
            Tag = new GUIStyle(Text) { fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft };
            var tagArt = Load("tag");
            if (tagArt != null)
            {
                Tag.normal.background = tagArt;
                Tag.border = new RectOffset(44, 40, 20, 20);
                Tag.padding = new RectOffset(54, 34, 10, 10);
                Tag.fixedHeight = 60;
                Tag.normal.textColor = Ink;
            }

            ListBox = new GUIStyle(GUI.skin.box) { padding = new RectOffset(8, 8, 8, 8) };
            ListBox.normal.background = HasArt ? Tex(new Color(0f, 0f, 0f, 0.25f)) : listTex;
        }

        /// <summary>Подпись с тенью — для текста прямо поверх фона-арта.</summary>
        public static void ShadowLabel(string text, GUIStyle style, params GUILayoutOption[] options)
        {
            var content = new GUIContent(text);
            var rect = GUILayoutUtility.GetRect(content, style, options);
            if (Event.current.type != EventType.Repaint) return;
            var shadow = new GUIStyle(style);
            shadow.normal.textColor = new Color(0f, 0f, 0f, 0.85f);
            shadow.Draw(new Rect(rect.x + 2, rect.y + 2, rect.width, rect.height), content, false, false, false, false);
            style.Draw(rect, content, false, false, false, false);
        }

        static Texture2D Load(string name) => Resources.Load<Texture2D>("UI/" + name);

        static void Skin(GUIStyle s, Texture2D normal, Texture2D hover, Texture2D active, RectOffset border)
        {
            if (normal == null) return;
            s.normal.background = s.onNormal.background = normal;
            s.hover.background = s.onHover.background = hover != null ? hover : normal;
            s.active.background = s.onActive.background = active != null ? active : normal;
            s.focused.background = s.onFocused.background = normal;
            s.border = border;
            s.overflow = new RectOffset(0, 0, 0, 0);
        }

        static void SetText(GUIStyle s, Color c)
        {
            s.normal.textColor = s.hover.textColor = s.active.textColor = s.focused.textColor = c;
            s.onNormal.textColor = s.onHover.textColor = s.onActive.textColor = s.onFocused.textColor = c;
        }

        static Texture2D Tex(Color c)
        {
            var t = new Texture2D(1, 1);
            t.SetPixel(0, 0, c);
            t.Apply();
            return t;
        }
    }
}
