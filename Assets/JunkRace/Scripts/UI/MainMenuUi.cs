using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace JunkRace
{
    /// <summary>
    /// Главное меню на UGUI (замена IMGUI-меню MainMenu: там цвета выцветали). Логика та же — экраны,
    /// сеть через CoopSession, анимированные иконки MenuIconFx. Всё собирается из кода.
    ///
    /// Старый MainMenu в сцене остаётся: при загрузке сцены этот скрипт выключает его и встаёт рядом.
    /// Вернуть старое меню: UseUgui = false.
    /// </summary>
    public class MainMenuUi : MonoBehaviour
    {
        public const bool UseUgui = true;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Hook()
        {
            if (!UseUgui) return;
            SceneManager.sceneLoaded += (s, _) => Replace();
            Replace();
        }

        static void Replace()
        {
            foreach (var old in FindObjectsByType<MainMenu>(FindObjectsSortMode.None))
            {
                if (!old.enabled) continue;
                old.enabled = false;
                if (old.GetComponent<MainMenuUi>() == null) old.gameObject.AddComponent<MainMenuUi>();
            }
        }

        enum Screen { Main, Browser, Create, Password, Code, Busy, Lobby, Sound }

        const float AutoRefreshInterval = 4f;

        Screen screen = Screen.Main;
        string playerName = "";
        string serverName = "";
        string createPassword = "";
        string joinPassword = "";
        string joinCode = "";
        string message;
        ServerInfo joinTarget;
        float copiedAt = -100f;

        List<ServerInfo> servers = new List<ServerInfo>();
        bool refreshing;
        float lastRefresh = -100f;

        CoopSession Session => CoopSession.Instance;

        // UGUI
        RectTransform canvas, mainArea, panel, panelContent, fxLayer;
        Screen? built;
        readonly List<Action> refreshers = new List<Action>();   // обновление живых подписей текущего экрана

        // Анимированные иконки главного экрана (колесо у «Соло», куча у «Кооп»).
        MenuIconFx fx;
        Ui.UiButton soloButton, coopButton;

        void Start()
        {
            // Первый запуск игры — сразу на свалку, в обучение (пройдено или пропущено — больше не начинается).
            if (!GameSession.TutorialDone && string.IsNullOrEmpty(GameSession.PendingMessage))
            {
                GameSession.StartTutorial();
                return;
            }
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            Time.timeScale = 1f;
            playerName = GameSession.PlayerName;
            message = GameSession.PendingMessage;
            GameSession.PendingMessage = null;
            if (!string.IsNullOrEmpty(message)) screen = Screen.Browser;   // вернулись после дисконнекта — сразу в список
            if (Session != null) Session.Failed += OnSessionFailed;
            fx = new MenuIconFx();

            canvas = Ui.Canvas("Меню", 0, transform);
            var bgTex = Ui.Load("menu_background");
            if (bgTex != null)
            {
                var bg = Ui.Raw("Фон", canvas, bgTex);
                var fit = bg.gameObject.AddComponent<AspectRatioFitter>();
                fit.aspectMode = AspectRatioFitter.AspectMode.EnvelopeParent;
                fit.aspectRatio = (float)bgTex.width / bgTex.height;
            }
            else Ui.Stretch(Ui.Image("Фон", canvas, null, new Color(0.12f, 0.11f, 0.10f)).rectTransform);

            // Главный экран — прямо поверх арта слева; остальные — на панели.
            mainArea = Ui.Node("Главный", canvas);
            mainArea.anchorMin = mainArea.anchorMax = mainArea.pivot = new Vector2(0.06f, 1f);
            mainArea.pivot = new Vector2(0f, 1f);
            mainArea.anchoredPosition = new Vector2(0f, -40f);
            mainArea.sizeDelta = new Vector2(640f, 1000f);

            panel = Ui.Node("Панель", canvas);
            panel.anchorMin = panel.anchorMax = new Vector2(0.06f, 1f);
            panel.pivot = new Vector2(0f, 1f);
            panel.anchoredPosition = new Vector2(0f, -70f);
            panel.sizeDelta = new Vector2(780f, 940f);
            Ui.Plate(panel, Ui.Load("panel"), new RectOffset(140, 140, 140, 140), new Color(0.20f, 0.18f, 0.16f, 0.97f));
            panelContent = Ui.Stretch(Ui.Node("Содержимое", panel), 74f, 74f, 74f, 74f);

            // Слой анимаций — поверх всего, клики не ловит.
            fxLayer = Ui.Stretch(Ui.Node("Анимации", canvas));
            fx.Layer = fxLayer;
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
            if (canvas == null) return;   // первый запуск: Start сразу ушёл в обучение, меню не строилось
            if (Session != null)
            {
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

            if (built != screen) Build();
            foreach (var r in refreshers) r();

            fx?.Update(Time.unscaledDeltaTime);
        }

        void LateUpdate()
        {
            if (fx == null || !fx.Available || screen != Screen.Main || soloButton == null || coopButton == null) return;
            Rect solo = InLayer(soloButton.Rect), coop = InLayer(coopButton.Rect);
            Vector2 mouse = Mouse.current != null ? Mouse.current.position.ReadValue() : Vector2.zero;
            fx.WheelHovered = !fx.Busy && RectTransformUtility.RectangleContainsScreenPoint(soloButton.Rect, mouse, null);
            fx.PileHovered = !fx.Busy && RectTransformUtility.RectangleContainsScreenPoint(coopButton.Rect, mouse, null);
            fx.Draw(solo, coop);
        }

        /// <summary>Прямоугольник элемента в координатах слоя анимаций (от левого верхнего угла, y вниз).</summary>
        Rect InLayer(RectTransform rt)
        {
            var c = new Vector3[4];
            rt.GetWorldCorners(c);   // 0 — низ-лево, 2 — верх-право
            Vector2 a = fxLayer.InverseTransformPoint(c[1]), b = fxLayer.InverseTransformPoint(c[3]);
            var r = fxLayer.rect;
            return Rect.MinMaxRect(a.x - r.xMin, r.yMax - a.y, b.x - r.xMin, r.yMax - b.y);
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

        // ================== Сборка экранов ==================

        void Build()
        {
            built = screen;
            refreshers.Clear();
            soloButton = coopButton = null;
            Ui.Clear(mainArea);
            Ui.Clear(panelContent);
            bool main = screen == Screen.Main;
            mainArea.gameObject.SetActive(main);
            panel.gameObject.SetActive(!main);
            if (main) { fx?.Reset(); BuildMain(); return; }
            fxLayer.gameObject.SetActive(false);

            // Каждый экран — в свежем узле: раскладка на нём своя, старый узел удалён целиком.
            content = Ui.Stretch(Ui.Node("Экран_" + screen, panelContent));
            var col = Ui.Column(content, 4f);
            col.childForceExpandWidth = true;
            switch (screen)
            {
                case Screen.Browser: BuildBrowser(); break;
                case Screen.Create: BuildCreate(); break;
                case Screen.Password: BuildPassword(); break;
                case Screen.Code: BuildCode(); break;
                case Screen.Busy: BuildBusy(); break;
                case Screen.Lobby: BuildLobby(); break;
                case Screen.Sound: BuildSound(); break;
            }
            ErrorLine(content, false);
        }

        RectTransform content;   // узел текущего экрана на панели

        void Show(Screen s)
        {
            screen = s;
        }

        void ErrorLine(Transform parent, bool shadow)
        {
            var err = Ui.Label(parent, "", 24, Ui.ErrorColor, wrap: true, shadow: shadow);
            refreshers.Add(() =>
            {
                err.text = message ?? "";
                err.gameObject.SetActive(!string.IsNullOrEmpty(message));
            });
        }

        void BuildMain()
        {
            fxLayer.gameObject.SetActive(fx != null && fx.Available);
            var area = Ui.Stretch(Ui.Node("Экран_Main", mainArea));
            Ui.Column(area, 4f);
            var logo = Ui.Load("logo");
            if (logo != null)
            {
                var img = Ui.Image("Логотип", area, Ui.Sprite(logo));
                img.preserveAspect = true;
                Ui.Size(img, 640f, 258f);
                Ui.Label(area, "собери тачку из хлама и доедь", 21, Ui.Cream, true, shadow: true);
                Ui.Space(area, 30f);
            }
            else
            {
                Ui.Space(area, 40f);
                Ui.Label(area, "JUNK RACE", 84, Ui.Accent, true, TextAnchor.MiddleCenter);
                Ui.Space(area, 70f);
            }

            const float bw = 560f;
            bool Busy() => fx != null && fx.Available && fx.Busy;

            soloButton = Ui.Button(area, "Соло", "solo", Ui.Big, () =>
            {
                if (Busy()) return;
                if (fx != null && fx.Available) fx.LaunchWheel(GameSession.StartSolo);
                else GameSession.StartSolo();
            }, bw);
            Ui.Space(area, 8f);
            Ui.Button(area, "Обучение", null, Ui.Big, () => { if (!Busy()) GameSession.StartTutorial(); }, bw);
            Ui.Space(area, 8f);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            // Только для разработки: в обычной сборке игры кнопки нет.
            Ui.Button(area, "Тест-комната", "wrench", Ui.Big, () => { if (!Busy()) GameSession.StartDevRoom(); }, bw);
            Ui.Label(area, "все детали рядами, сборка без таймера", 21, Ui.Cream, true, shadow: true);
            Ui.Space(area, 8f);
#endif
            coopButton = Ui.Button(area, "Кооп", "coop", Ui.Big, () =>
            {
                if (Busy()) return;
                message = Session == null ? "В сцене нет NetworkManager с CoopSession. Пересобери сцены через меню JunkRace." : null;
                if (Session == null) return;
                void Open() { Show(Screen.Browser); lastRefresh = -100f; }
                if (fx != null && fx.Available) fx.LaunchPile(Open); else Open();
            }, bw);
            Ui.Space(area, 8f);

            // Закрытый режим: та же кнопка, но серая и с замком.
            var ultra = Ui.Button(area, "Ультра кооп", "lock", Ui.Big, null, bw);
            ultra.Interactable = false;
            var grey = new Color(0.55f, 0.55f, 0.55f, 1f);
            ultra.Button.targetGraphic.color = grey;
            ultra.Text.color *= grey;
            if (ultra.Icon != null) ultra.Icon.color = grey;
            Ui.Label(area, "скоро: несколько команд на одной свалке", 21, Ui.Cream, true, shadow: true);

            Ui.Space(area, 0f, true);
            var bottom = Ui.Node("Низ", area);
            Ui.Row(bottom, 12f);
            // В браузере выйти из игры нельзя (Application.Quit не работает) — кнопки нет.
            if (Application.platform != RuntimePlatform.WebGLPlayer)
                Ui.Button(bottom, "Выход", "back", Ui.Big, () => { if (!Busy()) Application.Quit(); }, 300f);
            Ui.Button(bottom, "Звук", null, Ui.Big, () => { if (!Busy()) Show(Screen.Sound); }, 240f);
            ErrorLine(area, true);
        }

        void BuildSound()
        {
            Ui.Header(content, "Звук");
            Ui.Space(content, 24f);
            Ui.VolumeSliders(content, 560f);
            Ui.Space(content, 0f, true);
            Ui.Button(content, "Назад", "back", Ui.Big, () => { GameAudio.Save(); Show(Screen.Main); });
        }

        void BuildBrowser()
        {
            Ui.Header(content, "Кооп");
            Ui.Body(content, "Имя игрока");
            Ui.Field(content, playerName, 16, v => playerName = v);
            Ui.Space(content, 20f);

            var head = Ui.Node("Заголовок_списка", content);
            Ui.Row(head, 8f);
            var title = Ui.Body(head, "Серверы");
            Ui.Space(head, 0f, true);
            Ui.Button(head, "Обновить", "refresh", Ui.Small, RefreshServers, 220f);

            var list = Ui.Scroll(content, 330f, new Color(0f, 0f, 0f, 0.25f));
            List<ServerInfo> shown = null;
            int shownCount = -1;
            bool shownRefreshing = false;
            refreshers.Add(() =>
            {
                title.text = refreshing ? "Серверы (ищем...)" : "Серверы";
                if (shown == servers && shownCount == servers.Count && (servers.Count > 0 || shownRefreshing == refreshing)) return;
                shown = servers; shownCount = servers.Count; shownRefreshing = refreshing;
                Ui.Clear(list);
                if (servers.Count == 0)
                    Ui.Hint(list, refreshing ? "Ищем серверы..." : "Пока никого. Создай свой сервер!");
                else
                    foreach (var s in servers) ServerRow(list, s);
            });

            Ui.Space(content, 16f);
            Ui.Button(content, "Создать сервер", "key", Ui.Big, () =>
            {
                if (!CheckName()) return;
                if (string.IsNullOrEmpty(serverName)) serverName = $"Сервер {playerName.Trim()}";
                message = null;
                Show(Screen.Create);
            });
            var row = Ui.Node("Кнопки", content);
            Ui.Row(row, 8f);
            Ui.Size(Ui.Button(row, "Войти по коду", "copy", Ui.Small, () =>
            {
                if (!CheckName()) return;
                message = null;
                joinPassword = "";
                Show(Screen.Code);
            }).Rect, -1f, -1f, 1f);
            Ui.Size(Ui.Button(row, "Назад", "back", Ui.Small, () => { message = null; Show(Screen.Main); }).Rect, -1f, -1f, 1f);
        }

        void ServerRow(Transform list, ServerInfo s)
        {
            var rowArt = Ui.Load("row_plate");
            var row = Ui.Node("Сервер", list);
            Ui.Plate(row, rowArt, new RectOffset(30, 125, 22, 22), new Color(0.27f, 0.24f, 0.21f));
            Ui.Row(row, 8f, rowArt != null ? new RectOffset(34, 34, 12, 12) : new RectOffset(14, 14, 8, 8));
            Color ink = rowArt != null ? Ui.Ink : Color.white, inkSoft = rowArt != null ? Ui.InkSoft : Ui.HintColor;

            var info = Ui.Node("Инфо", row);
            Ui.Column(info, 2f);
            var nameRow = Ui.Node("Имя", info);
            Ui.Row(nameRow, 6f);
            var lockTex = s.HasPassword ? Ui.Icon("lock") : null;
            if (lockTex != null)
            {
                var ic = Ui.Image("Замок", nameRow, Ui.Sprite(lockTex));
                ic.preserveAspect = true;
                Ui.Size(ic, 34f, 34f);
            }
            Ui.Label(nameRow, s.HasPassword && lockTex == null ? s.Name + "   [пароль]" : s.Name, 28, ink, rowArt != null);
            string status = s.Started ? "идёт игра" : s.IsFull ? "полон" : "ждут игроков";
            Ui.Label(info, $"{s.Players}/{s.MaxPlayers}  ·  {status}", 21, inkSoft);
            Ui.Space(row, 0f, true);

            var join = Ui.Button(row, "Войти", null, Ui.Small, () =>
            {
                if (!CheckName()) return;
                joinTarget = s;
                message = null;
                if (s.HasPassword) { joinPassword = ""; Show(Screen.Password); }
                else { SaveName(); Session.Join(playerName, s, null); }
            }, 160f);
            join.Interactable = !s.Started && !s.IsFull;
            Ui.Space(list, 6f);
        }

        void BuildCreate()
        {
            Ui.Header(content, "Новый сервер");
            Ui.Body(content, "Название сервера");
            Ui.Field(content, serverName, 24, v => serverName = v);
            Ui.Space(content, 16f);
            Ui.Body(content, "Пароль (необязательно)");
            PasswordRow(createPassword, v => createPassword = v);
            Ui.Space(content, 6f);
            Ui.Hint(content, $"Пусто — заходят все. Иначе от {CoopSession.MinPasswordLength} символов.");
            Ui.Space(content, 30f);
            Ui.Hint(content, $"Максимум игроков: {GameSession.CoopMaxPlayers}");

            Ui.Space(content, 0f, true);
            Ui.Button(content, "Создать", "key", Ui.Big, () =>
            {
                if (createPassword.Length > 0 && createPassword.Length < CoopSession.MinPasswordLength)
                {
                    message = $"Пароль слишком короткий: нужно от {CoopSession.MinPasswordLength} символов.";
                    return;
                }
                SaveName();
                message = null;
                Session.Host(playerName, serverName, createPassword);
            });
            Ui.Button(content, "Назад", "back", Ui.Big, () => { message = null; Show(Screen.Browser); });
        }

        void BuildPassword()
        {
            Ui.Header(content, joinTarget.Name);
            Ui.Body(content, "Сервер с паролем");
            PasswordRow(joinPassword, v => joinPassword = v);
            Ui.Space(content, 0f, true);
            Ui.Button(content, "Подключиться", null, Ui.Big, () =>
            {
                if (joinPassword.Length < CoopSession.MinPasswordLength)
                {
                    message = $"Пароль — от {CoopSession.MinPasswordLength} символов.";
                    return;
                }
                SaveName();
                message = null;
                Session.Join(playerName, joinTarget, joinPassword);
            });
            Ui.Button(content, "Назад", "back", Ui.Big, () => { message = null; Show(Screen.Browser); });
        }

        void BuildCode()
        {
            Ui.Header(content, "Вход по коду");
            Ui.Body(content, "Код сервера (хост видит его в лобби)");
            Ui.Field(content, joinCode, 12, v => joinCode = v);
            Ui.Body(content, "Пароль (если есть)");
            PasswordRow(joinPassword, v => joinPassword = v);

            Ui.Space(content, 0f, true);
            Ui.Button(content, "Подключиться", null, Ui.Big, () =>
            {
                if (joinCode.Trim().Length == 0) { message = "Введи код."; return; }
                SaveName();
                message = null;
                Session.JoinByCode(playerName, joinCode, joinPassword);
            });
            Ui.Button(content, "Назад", "back", Ui.Big, () => { message = null; Show(Screen.Browser); });
        }

        void BuildBusy()
        {
            Ui.Header(content, "Секунду...");
            var text = Ui.Body(content, "");
            refreshers.Add(() => text.text = Session != null ? Session.BusyText : "");
        }

        void BuildLobby()
        {
            var s = Session;
            var header = Ui.Header(content, "");

            // Код для друзей на бирке + кнопка «скопировать».
            var codeRow = Ui.Node("Код", content);
            Ui.Row(codeRow, 8f);
            var tagArt = Ui.Load("tag");
            var tag = Ui.Node("Бирка", codeRow);
            Ui.Plate(tag, tagArt, new RectOffset(44, 40, 20, 20), new Color(0f, 0f, 0f, 0f));
            Ui.Size(tag, 300f, 60f);
            var tagText = Ui.Label(Ui.Stretch(Ui.Node("Текст", tag), tagArt != null ? 54f : 0f, 0f, 34f, 0f), "", 28, tagArt != null ? Ui.Ink : Color.white, true);
            Ui.Stretch(tagText.rectTransform);
            var copy = Ui.Button(codeRow, "Копировать", "copy", Ui.Small, () =>
            {
                GUIUtility.systemCopyBuffer = s.JoinCode;
                copiedAt = Time.unscaledTime;
            }, 300f);
            var players = Ui.Body(content, "");
            Ui.Space(content, 6f);

            var slotArt = Ui.Load("player_slot");
            var hostIcon = Ui.Icon("host");
            var readyIcon = Ui.Sprite(Ui.Icon("ready"));
            var notReadyIcon = Ui.Sprite(Ui.Icon("not_ready"));
            var slots = new List<(UnityEngine.UI.Image host, Text name, UnityEngine.UI.Image ready, Text readyText)>();
            for (int i = 0; i < GameSession.CoopMaxPlayers; i++)
            {
                var slot = Ui.Node("Слот", content);
                Ui.Plate(slot, slotArt, new RectOffset(80, 65, 22, 22), new Color(0.27f, 0.24f, 0.21f));
                Ui.Size(slot, -1f, 72f);
                Ui.Row(slot, 6f, slotArt != null ? new RectOffset(96, 72, 12, 12) : new RectOffset(14, 14, 8, 8));
                UnityEngine.UI.Image host = null;
                if (i == 0 && hostIcon != null)
                {
                    host = Ui.Image("Хост", slot, Ui.Sprite(hostIcon));
                    host.preserveAspect = true;
                    Ui.Size(host, 40f, 40f);
                }
                var name = Ui.Label(slot, "", 28, Ui.Cream, true);
                Ui.Space(slot, 0f, true);
                var ready = Ui.Image("Готов", slot, readyIcon);
                ready.preserveAspect = true;
                Ui.Size(ready, 52f, 44f);
                var readyText = Ui.Label(slot, "", 26, Ui.ReadyColor, true);
                slots.Add((host, name, ready, readyText));
                Ui.Space(content, 3f);
            }

            Ui.Space(content, 20f);
            var status = Ui.Label(content, "", 28, new Color(0.9f, 0.85f, 0.75f), align: TextAnchor.MiddleCenter, wrap: true);

            Ui.Space(content, 0f, true);
            var readyButton = Ui.Button(content, "Готов", "ready", Ui.Big, () => s.SetReady(!s.LocalReady));
            Ui.Hint(content, "Старт — когда все в лобби нажмут «Готов». Можно и одному.");
            Ui.Button(content, "Выйти", "back", Ui.Big, () => { s.Leave(); Show(Screen.Browser); lastRefresh = -100f; });

            refreshers.Add(() =>
            {
                if (s == null) return;
                header.text = s.ServerName;
                tagText.text = $"Код: {s.JoinCode}";
                copy.Text.text = copiedAt > Time.unscaledTime - 1.5f ? "Готово!" : "Копировать";
                players.text = $"Игроки {s.Players.Count}/{GameSession.CoopMaxPlayers}";
                for (int i = 0; i < slots.Count; i++)
                {
                    var (host, name, ready, readyText) = slots[i];
                    bool taken = i < s.Players.Count;
                    if (host != null) host.gameObject.SetActive(taken);
                    if (taken)
                    {
                        name.text = (i == 0 && host == null ? "[хост]  " : "") + s.Players[i];
                        name.color = Ui.Cream;
                        name.fontStyle = FontStyle.Bold;
                        bool isReady = i < s.PlayersReady.Count && s.PlayersReady[i];
                        var icon = isReady ? readyIcon : notReadyIcon;
                        ready.gameObject.SetActive(icon != null);
                        ready.sprite = icon;
                        readyText.gameObject.SetActive(icon == null);
                        readyText.text = isReady ? "готов" : "не готов";
                        readyText.color = isReady ? Ui.ReadyColor : new Color(0.85f, 0.78f, 0.68f);
                    }
                    else
                    {
                        name.text = "— свободно —";
                        name.color = new Color(0.85f, 0.78f, 0.68f);
                        name.fontStyle = FontStyle.Normal;
                        ready.gameObject.SetActive(false);
                        readyText.gameObject.SetActive(false);
                    }
                }
                status.text = s.CountdownLeft >= 0f ? $"Все готовы! Старт через {Mathf.CeilToInt(s.CountdownLeft)}..."
                            : s.AllReady ? "Все готовы, ждём подключения по сети..."
                            : "Ждём, пока все нажмут «Готов»";
                readyButton.Text.text = s.LocalReady ? "Не готов" : "Готов";
                if (readyButton.Icon != null) readyButton.Icon.sprite = s.LocalReady ? notReadyIcon : readyIcon;
            });
        }

        /// <summary>Поле пароля во всю ширину и кнопка «показать» рядом.</summary>
        void PasswordRow(string value, Action<string> onChange)
        {
            var row = Ui.Node("Пароль", content);
            Ui.Row(row, 10f);
            var field = Ui.Field(row, value, CoopSession.MaxPasswordLength, onChange, !showPassword);
            Ui.Size(field, -1f, -1f, 1f);
            var toggle = Ui.Button(row, showPassword ? "скрыть" : "показать", null, Ui.Small, null, 170f);
            toggle.Button.onClick.AddListener(() =>
            {
                showPassword = !showPassword;
                field.contentType = showPassword ? InputField.ContentType.Standard : InputField.ContentType.Password;
                field.ForceLabelUpdate();
                toggle.Text.text = showPassword ? "скрыть" : "показать";
            });
        }

        bool showPassword;

        // ================== Хелперы ==================

        bool CheckName()
        {
            if (playerName.Trim().Length > 0) return true;
            message = "Сначала введи имя игрока.";
            return false;
        }

        void SaveName() => GameSession.PlayerName = playerName.Trim();
    }
}
