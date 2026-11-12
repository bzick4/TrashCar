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
        }

        void OnDestroy()
        {
            if (Session != null) Session.Failed -= OnSessionFailed;
        }

        void OnSessionFailed(string reason)
        {
            message = reason;
            if (screen == Screen.Busy || screen == Screen.Lobby) screen = Screen.Browser;
        }

        void Update()
        {
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

            GUI.DrawTexture(new Rect(0, 0, w, 1080), MenuStyles.BackgroundTex);
            var panel = new Rect((w - 760f) / 2f, 90f, 760f, 900f);
            GUI.Box(panel, GUIContent.none, MenuStyles.Panel);
            GUILayout.BeginArea(new Rect(panel.x + 40, panel.y + 30, panel.width - 80, panel.height - 60));

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
            GUILayout.Space(40);
            GUILayout.Label("JUNK RACE", MenuStyles.Title);
            GUILayout.Label("собери тачку из хлама и доедь", MenuStyles.Subtitle);
            GUILayout.Space(70);

            if (Button("Соло")) GameSession.StartSolo();
            GUILayout.Space(14);
            if (Button("Кооп"))
            {
                message = Session == null ? "В сцене нет NetworkManager с CoopSession. Пересобери сцены через меню JunkRace." : null;
                if (Session != null) { screen = Screen.Browser; lastRefresh = -100f; }
            }
            GUILayout.Space(14);

            GUI.enabled = false;
            Button("Ультра кооп  (закрыто)");
            GUI.enabled = true;
            GUILayout.Label("скоро: несколько команд на одной свалке", MenuStyles.Hint);

            GUILayout.FlexibleSpace();
            if (Button("Выход")) Application.Quit();
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
            if (GUILayout.Button("Обновить", MenuStyles.SmallButton, GUILayout.Width(180))) RefreshServers();
            GUILayout.EndHorizontal();

            scroll = GUILayout.BeginScrollView(scroll, MenuStyles.ListBox, GUILayout.Height(360));
            if (servers.Count == 0)
                GUILayout.Label(refreshing ? "Ищем серверы..." : "Пока никого. Создай свой сервер!", MenuStyles.Hint);
            else
                foreach (var s in servers) DrawServerRow(s);
            GUILayout.EndScrollView();

            GUILayout.Space(16);
            if (Button("Создать сервер") && CheckName())
            {
                if (string.IsNullOrEmpty(serverName)) serverName = $"Сервер {playerName.Trim()}";
                message = null;
                screen = Screen.Create;
            }
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Войти по коду", MenuStyles.SmallButton) && CheckName())
            {
                message = null;
                joinPassword = "";
                screen = Screen.Code;
            }
            if (GUILayout.Button("Назад", MenuStyles.SmallButton)) { message = null; screen = Screen.Main; }
            GUILayout.EndHorizontal();
        }

        void DrawServerRow(ServerInfo s)
        {
            GUILayout.BeginHorizontal(MenuStyles.Row);
            GUILayout.BeginVertical();
            GUILayout.Label(s.Name + (s.HasPassword ? "   [пароль]" : ""), MenuStyles.Text);
            string status = s.Started ? "идёт игра" : s.IsFull ? "полон" : "ждут игроков";
            GUILayout.Label($"{s.Players}/{s.MaxPlayers}  ·  {status}", MenuStyles.Hint);
            GUILayout.EndVertical();
            GUILayout.FlexibleSpace();

            GUI.enabled = !s.Started && !s.IsFull;
            if (GUILayout.Button("Войти", MenuStyles.SmallButton, GUILayout.Width(150)) && CheckName())
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
            createPassword = GUILayout.PasswordField(createPassword, '*', 64, MenuStyles.Field);
            GUILayout.Label($"Пусто — заходят все. Иначе минимум {CoopSession.MinPasswordLength} символов (требование Unity).", MenuStyles.Hint);
            GUILayout.Space(30);
            GUILayout.Label($"Максимум игроков: {GameSession.CoopMaxPlayers}", MenuStyles.Hint);

            GUILayout.FlexibleSpace();
            if (Button("Создать"))
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
            if (Button("Назад")) { message = null; screen = Screen.Browser; }
        }

        void DrawPassword()
        {
            GUILayout.Label(joinTarget.Name, MenuStyles.Header);
            GUILayout.Label("Сервер с паролем", MenuStyles.Text);
            joinPassword = GUILayout.PasswordField(joinPassword, '*', 64, MenuStyles.Field);
            GUILayout.FlexibleSpace();
            if (Button("Подключиться"))
            {
                SaveName();
                message = null;
                Session.Join(playerName, joinTarget, joinPassword);
            }
            if (Button("Назад")) { message = null; screen = Screen.Browser; }
        }

        void DrawCode()
        {
            GUILayout.Label("Вход по коду", MenuStyles.Header);
            GUILayout.Label("Код сервера (хост видит его в лобби)", MenuStyles.Text);
            joinCode = GUILayout.TextField(joinCode, 12, MenuStyles.Field);
            GUILayout.Label("Пароль (если есть)", MenuStyles.Text);
            joinPassword = GUILayout.PasswordField(joinPassword, '*', 64, MenuStyles.Field);

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
            if (Button("Назад")) { message = null; screen = Screen.Browser; }
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
            GUILayout.Label($"Код для друзей: {s.JoinCode}", MenuStyles.Text);
            GUILayout.Label($"Игроки {s.Players.Count}/{GameSession.CoopMaxPlayers}", MenuStyles.Text);
            GUILayout.Space(10);

            for (int i = 0; i < GameSession.CoopMaxPlayers; i++)
            {
                GUILayout.BeginHorizontal(MenuStyles.Row);
                if (i < s.Players.Count)
                    GUILayout.Label((i == 0 ? "[хост]  " : "") + s.Players[i], MenuStyles.Text);
                else
                    GUILayout.Label("— свободно —", MenuStyles.Hint);
                GUILayout.EndHorizontal();
            }

            GUILayout.Space(20);
            if (s.CountdownLeft >= 0f)
                GUILayout.Label($"Все в сборе! Старт через {Mathf.CeilToInt(s.CountdownLeft)}...", MenuStyles.Subtitle);
            else
                GUILayout.Label("Ждём игроков...", MenuStyles.Subtitle);

            GUILayout.FlexibleSpace();
            if (s.IsHost)
            {
                if (Button("Начать сейчас (тест)")) s.ForceStart();
                GUILayout.Label("Пока можно стартовать и одному", MenuStyles.Hint);
            }
            if (Button("Выйти")) { s.Leave(); screen = Screen.Browser; lastRefresh = -100f; }
        }

        // ================== Хелперы ==================

        bool CheckName()
        {
            if (playerName.Trim().Length > 0) return true;
            message = "Сначала введи имя игрока.";
            return false;
        }

        void SaveName() => GameSession.PlayerName = playerName.Trim();

        static bool Button(string text) => GUILayout.Button(text, MenuStyles.Button);
    }

    /// <summary>Стили IMGUI-меню. Создаются один раз.</summary>
    static class MenuStyles
    {
        public static GUIStyle Title, Subtitle, Header, Text, Hint, Error, Button, SmallButton, Field, Panel, Row, ListBox;
        public static Texture2D BackgroundTex;

        public static void Ensure()
        {
            if (Title != null) return;

            BackgroundTex = Tex(new Color(0.12f, 0.11f, 0.10f));
            var panelTex = Tex(new Color(0.20f, 0.18f, 0.16f, 0.97f));
            var rowTex = Tex(new Color(0.27f, 0.24f, 0.21f));
            var listTex = Tex(new Color(0.15f, 0.14f, 0.12f));
            Color accent = new Color(1f, 0.62f, 0.15f);

            Title = new GUIStyle(GUI.skin.label) { fontSize = 84, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            Title.normal.textColor = accent;
            Subtitle = new GUIStyle(GUI.skin.label) { fontSize = 28, alignment = TextAnchor.MiddleCenter, wordWrap = true };
            Subtitle.normal.textColor = new Color(0.9f, 0.85f, 0.75f);
            Header = new GUIStyle(GUI.skin.label) { fontSize = 46, fontStyle = FontStyle.Bold };
            Header.normal.textColor = accent;
            Text = new GUIStyle(GUI.skin.label) { fontSize = 28 };
            Text.normal.textColor = Color.white;
            Hint = new GUIStyle(GUI.skin.label) { fontSize = 21, wordWrap = true };
            Hint.normal.textColor = new Color(0.7f, 0.67f, 0.6f);
            Error = new GUIStyle(GUI.skin.label) { fontSize = 24, wordWrap = true };
            Error.normal.textColor = new Color(1f, 0.4f, 0.35f);

            Button = new GUIStyle(GUI.skin.button) { fontSize = 32, fixedHeight = 74, fontStyle = FontStyle.Bold };
            SmallButton = new GUIStyle(GUI.skin.button) { fontSize = 24, fixedHeight = 56 };
            Field = new GUIStyle(GUI.skin.textField) { fontSize = 28, fixedHeight = 54, padding = new RectOffset(12, 12, 10, 10) };

            Panel = new GUIStyle(GUI.skin.box);
            Panel.normal.background = panelTex;
            Row = new GUIStyle(GUI.skin.box) { padding = new RectOffset(14, 14, 8, 8), margin = new RectOffset(0, 0, 4, 4) };
            Row.normal.background = rowTex;
            ListBox = new GUIStyle(GUI.skin.box) { padding = new RectOffset(8, 8, 8, 8) };
            ListBox.normal.background = listTex;
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
