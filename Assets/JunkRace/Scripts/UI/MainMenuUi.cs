using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using DG.Tweening;

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

        enum Screen { Main, Browser, Create, Password, Code, Busy, Lobby, Sound, Race, Biome, Playlist }

        /// <summary>Глубина экрана: вперёд (глубже) — старое окно уезжает влево, новое выезжает справа; назад — наоборот.</summary>
        static int Depth(Screen s) => s switch
        {
            Screen.Main => 0,
            Screen.Browser or Screen.Sound or Screen.Race or Screen.Playlist => 1,
            Screen.Create or Screen.Password or Screen.Code or Screen.Biome => 2,
            Screen.Busy => 3,
            _ => 4,
        };

        bool sliding;                  // окна в пути — новый экран построится, когда старое уедет
        Vector2 mainHome, panelHome;   // где окна стоят на месте
        const string SlideTween = "menu_slide";

        /// <summary>Экран «Гонка» открыт из кооп-создания (в конце — создать сервер), иначе — соло.</summary>
        bool raceForCoop;

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
            if (!GameSession.TutorialDone && !GameSession.TutorialAutoStarted && string.IsNullOrEmpty(GameSession.PendingMessage))
            {
                GameSession.TutorialAutoStarted = true;
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
            mainHome = mainArea.anchoredPosition;
            panelHome = panel.anchoredPosition;

            // Круглые кнопки в правом нижнем углу: обучение (?) и плейлист (ноты). Только на главном экране.
            BuildCorner();

            // Слой анимаций — поверх всего, клики не ловит.
            fxLayer = Ui.Stretch(Ui.Node("Анимации", canvas));
            fx.Layer = fxLayer;
        }

        RectTransform corner;   // круглые кнопки в правом нижнем углу
        static Sprite discSprite;

        static Sprite Disc()
        {
            if (discSprite != null) return discSprite;
            const int n = 128;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            var px = new Color32[n * n];
            float c = (n - 1) * 0.5f, R = n * 0.5f - 1f;
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
                    px[y * n + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(R - d + 0.5f) * 255f));
                }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            discSprite = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), n);
            return discSprite;
        }

        void BuildCorner()
        {
            corner = Ui.Node("Углы", canvas);
            corner.anchorMin = corner.anchorMax = corner.pivot = new Vector2(1f, 0f);
            corner.anchoredPosition = new Vector2(-40f, 40f);
            corner.sizeDelta = new Vector2(230f, 100f);
            CornerButton(corner, 0f, "?", null, () => { if (!(fx != null && fx.Available && fx.Busy)) GameSession.StartTutorial(); });
            CornerButton(corner, 120f, null, "note", () => Show(Screen.Playlist));
        }

        void CornerButton(RectTransform parent, float x, string text, string icon, Action onClick)
        {
            var rt = Ui.Node("Круглая", parent);
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0f, 0f);
            rt.anchoredPosition = new Vector2(x, 0f);
            rt.sizeDelta = new Vector2(100f, 100f);
            var bg = rt.gameObject.AddComponent<UnityEngine.UI.Image>();
            bg.sprite = Disc();
            bg.color = new Color(0.12f, 0.09f, 0.07f, 0.85f);
            var b = rt.gameObject.AddComponent<UnityEngine.UI.Button>();
            b.targetGraphic = bg;
            var nav = b.navigation; nav.mode = Navigation.Mode.None; b.navigation = nav;
            b.onClick.AddListener(() => onClick());
            if (text != null)
            {
                var t = Ui.Label(rt, text, 60, Ui.Cream, true, TextAnchor.MiddleCenter);
                Ui.Stretch(t.rectTransform);
            }
            if (icon != null)
            {
                var img = Ui.Image("Значок", rt, Ui.Sprite(Ui.Icon(icon)), Color.white);
                img.preserveAspect = true;
                Ui.Stretch(img.rectTransform, 26f, 26f, 26f, 26f);
            }
        }

        /// <summary>Плейлист: треки по номерам, рядом — значок воспроизведения. Играет по порядку, пока не уйдём играть.</summary>
        void BuildPlaylist()
        {
            Ui.Header(content, "Плейлист");
            Ui.Hint(content, "Играет по порядку. Уйдёшь на свалку или в гонку — музыка пойдёт по своим правилам.");
            Ui.Space(content, 12f);
            var list = Ui.Scroll(content, 560f, new Color(0f, 0f, 0f, 0.25f));
            int now = MusicPlayer.PlaylistIndex;
            int n = MusicPlayer.PlaylistCount;
            if (n == 0) Ui.Hint(list, "Треков пока нет.");
            for (int i = 0; i < n; i++)
            {
                int idx = i;
                string label = "Трек " + (i + 1) + (i == now ? "   · играет" : "");
                Ui.Button(list, label, "play", Ui.Small, () => { MusicPlayer.PlayFromPlaylist(idx); built = null; }, -1f);
            }
            Ui.Space(content, 0f, true);
            Ui.Button(content, "Назад", "back", Ui.Big, () => Show(Screen.Main));
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

            if (built != screen && !sliding) Slide();
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
            if (corner != null) corner.gameObject.SetActive(main);
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
                case Screen.Race: BuildRace(); break;
                case Screen.Biome: BuildBiome(); break;
                case Screen.Playlist: BuildPlaylist(); break;
            }
            ErrorLine(content, false);
        }

        RectTransform content;   // узел текущего экрана на панели

        /// <summary>
        /// Смена экрана с анимацией (DOTween): старое окно уезжает за край экрана, потом строится новое
        /// и выезжает с другой стороны с лёгким перелётом. Перестройка того же экрана (built = null) — без анимации.
        /// </summary>
        void Slide()
        {
            if (built == null) { Build(); return; }
            var from = built.Value;
            var to = screen;
            float dir = Depth(to) < Depth(from) ? 1f : -1f;   // куда уезжает старое: назад — вправо, вперёд — влево
            float w = canvas.rect.width + 200f;
            var oldRt = from == Screen.Main ? mainArea : panel;
            var oldHome = from == Screen.Main ? mainHome : panelHome;
            sliding = true;
            DOTween.Kill(SlideTween);
            DOTween.To(() => oldRt.anchoredPosition, v => oldRt.anchoredPosition = v, oldHome + new Vector2(dir * w, 0f), 0.22f)
                .SetEase(Ease.InCubic).SetUpdate(true).SetId(SlideTween)
                .OnComplete(() =>
                {
                    oldRt.anchoredPosition = oldHome;
                    Build();
                    var newRt = to == Screen.Main ? mainArea : panel;
                    var newHome = to == Screen.Main ? mainHome : panelHome;
                    newRt.anchoredPosition = newHome - new Vector2(dir * w, 0f);
                    DOTween.To(() => newRt.anchoredPosition, v => newRt.anchoredPosition = v, newHome, 0.32f)
                        .SetEase(Ease.OutBack, 0.8f).SetUpdate(true).SetId(SlideTween)
                        .OnComplete(() => sliding = false);
                });
        }

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
                void Open() { raceForCoop = false; message = null; Show(Screen.Race); }
                if (fx != null && fx.Available) fx.LaunchWheel(Open);
                else Open();
            }, bw);
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
            Ui.Label(info, $"{s.Players}/{s.MaxPlayers}  ·  {status}" + (string.IsNullOrEmpty(s.Race) ? "" : $"  ·  {s.Race}"), 21, inkSoft);
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

        /// <summary>
        /// Какая гонка: спринт или кольцо, длина (с примерным временем) и круги для кольца. Соло — «Поехали»,
        /// хост — «Создать сервер» (настройки уйдут игрокам вместе с трассой). Выбор запоминается.
        /// </summary>
        void BuildRace()
        {
            Ui.Header(content, "Гонка");
            var kind = GameSession.Kind;
            void Redraw() { GameSession.SaveRace(); built = null; }
            void Mark(Ui.UiButton b, bool on)
            {
                // Невыбранное — притушено, как закрытый режим в главном меню.
                var c = on ? Color.white : new Color(0.55f, 0.55f, 0.55f, 1f);
                b.Button.targetGraphic.color = c;
                if (b.Icon != null) b.Icon.color = c;
                if (!on) b.Text.color *= c;
            }

            var kinds = Ui.Node("Вид", content);
            Ui.Row(kinds, 10f);
            var sprint = Ui.Button(kinds, "Спринт", "sprint", Ui.Big, () => { GameSession.Kind = RaceKind.Sprint; Redraw(); });
            var ring = Ui.Button(kinds, "Кольцо", "ring", Ui.Big, () => { GameSession.Kind = RaceKind.Circuit; Redraw(); });
            Ui.Size(sprint.Rect, -1f, -1f, 1f);
            Ui.Size(ring.Rect, -1f, -1f, 1f);
            Mark(sprint, kind == RaceKind.Sprint);
            Mark(ring, kind == RaceKind.Circuit);
            Ui.Hint(content, kind == RaceKind.Sprint
                ? "От ворот свалки — через лес, горы и снег — до финишной свалки."
                : "Кольцо рядом со свалкой: несколько кругов, чекпоинты по пути. Восьмёрка — посередине мост над дорогой. Время — на один круг.");
            Ui.Space(content, 12f);

            Ui.Body(content, "Длина");
            for (int i = 0; i < 3; i++)
            {
                var size = (RaceSize)i;
                var b = Ui.Button(content, $"{GameSession.SizeName(kind, size)}  ·  {GameSession.SizeTime(kind, size)}", null, Ui.Small,
                                  () => { GameSession.Size = size; Redraw(); });
                Mark(b, GameSession.Size == size);
                Ui.Space(content, 2f);
            }

            if (kind == RaceKind.Circuit)
            {
                Ui.Space(content, 10f);
                var shapes = Ui.Node("Форма", content);
                Ui.Row(shapes, 10f);
                var oval = Ui.Button(shapes, "Овал", "ring", Ui.Small, () => { GameSession.Shape = RaceShape.Oval; Redraw(); });
                var eight = Ui.Button(shapes, "Восьмёрка", "eight", Ui.Small, () => { GameSession.Shape = RaceShape.Eight; Redraw(); });
                Ui.Size(oval.Rect, -1f, -1f, 1f);
                Ui.Size(eight.Rect, -1f, -1f, 1f);
                Mark(oval, GameSession.Shape == RaceShape.Oval);
                Mark(eight, GameSession.Shape == RaceShape.Eight);
                Ui.Space(content, 6f);
                var lapRow = Ui.Node("Круги", content);
                Ui.Row(lapRow, 10f);
                Ui.Body(lapRow, "Кругов:");
                Ui.Button(lapRow, "−", null, Ui.Small, () => { GameSession.Laps--; Redraw(); }, 90f).Interactable = GameSession.Laps > 1;
                Ui.Label(lapRow, GameSession.Laps.ToString(), 36, Ui.Accent, true, TextAnchor.MiddleCenter);
                Ui.Button(lapRow, "+", null, Ui.Small, () => { GameSession.Laps++; Redraw(); }, 90f).Interactable = GameSession.Laps < GameSession.MaxLaps;
            }

            Ui.Space(content, 0f, true);
            Ui.Button(content, "Далее: местность", null, Ui.Big, () => { GameSession.SaveRace(); Show(Screen.Biome); });
            Ui.Button(content, "Назад", "back", Ui.Big, () => { message = null; Show(raceForCoop ? Screen.Create : Screen.Main); });
        }

        /// <summary>
        /// Местность: карточки биомов с картинками (Resources/UI/biome_*.png; нет картинки — плашка с названием).
        /// «Рандом» — все биомы вперемешку. Реки и тоннели есть в любом.
        /// </summary>
        void BuildBiome()
        {
            Ui.Header(content, "Местность");
            Ui.Hint(content, "Реки с мостами и тоннели — в любой. «Рандом» — всё вперемешку.");
            Ui.Space(content, 8f);
            var grid = Ui.Node("Карточки", content);
            var gl = grid.gameObject.AddComponent<GridLayoutGroup>();
            gl.cellSize = new Vector2(305f, 150f);
            gl.spacing = new Vector2(12f, 12f);
            gl.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            gl.constraintCount = 2;
            Ui.Size(grid, -1f, 3f * 150f + 2f * 12f);
            var order = new[] { RaceBiome.Random, RaceBiome.Forest, RaceBiome.Desert, RaceBiome.Mountains, RaceBiome.Snow };
            foreach (var b in order) BiomeCard(grid, b);

            Ui.Space(content, 0f, true);
            if (raceForCoop)
                Ui.Button(content, "Создать сервер", "key", Ui.Big, () =>
                {
                    GameSession.SaveRace();
                    message = null;
                    Session.Host(playerName, serverName, createPassword);
                });
            else
                Ui.Button(content, "Поехали", "solo", Ui.Big, () => { GameSession.SaveRace(); GameSession.StartSolo(); });
            Ui.Button(content, "Назад", "back", Ui.Big, () => { message = null; Show(Screen.Race); });
        }

        void BiomeCard(Transform parent, RaceBiome b)
        {
            bool on = GameSession.Biome == b;
            var card = Ui.Node("Биом_" + b, parent);
            var art = Ui.Load("biome_" + b.ToString().ToLowerInvariant());
            // Рамка: выбранная — оранжевая, остальные — тёмные.
            var frame = Ui.Image("Рамка", card, null, on ? Ui.Accent : new Color(0.12f, 0.11f, 0.1f, 1f));
            Ui.Stretch(frame.rectTransform);
            frame.raycastTarget = true;
            var pic = art != null ? (Graphic)Ui.Raw("Картинка", card, art) : Ui.Image("Картинка", card, null, new Color(0.3f, 0.27f, 0.24f));
            Ui.Stretch(pic.rectTransform, 5f, 5f, 5f, 5f);
            if (art != null) ((RawImage)pic).uvRect = CoverUv(art, 295f / 140f);
            pic.color = on ? Color.white : new Color(0.6f, 0.6f, 0.6f, 1f);
            // Подпись снизу на тёмной полосе.
            var band = Ui.Image("Полоса", card, null, new Color(0f, 0f, 0f, 0.6f));
            band.rectTransform.anchorMin = new Vector2(0f, 0f);
            band.rectTransform.anchorMax = new Vector2(1f, 0f);
            band.rectTransform.pivot = new Vector2(0.5f, 0f);
            band.rectTransform.offsetMin = new Vector2(5f, 5f);
            band.rectTransform.offsetMax = new Vector2(-5f, 47f);
            var label = Ui.Label(band.transform, (art == null && b == RaceBiome.Random ? "? " : "") + GameSession.BiomeName(b), 28,
                                 on ? Ui.Accent : Ui.Cream, true, TextAnchor.MiddleCenter);
            Ui.Stretch(label.rectTransform);
            var btn = card.gameObject.AddComponent<Button>();
            btn.targetGraphic = frame;
            var nav = btn.navigation; nav.mode = Navigation.Mode.None; btn.navigation = nav;
            btn.onClick.AddListener(() => { GameSession.Biome = b; GameSession.SaveRace(); built = null; });
        }

        /// <summary>Картинку — заполнить рамку без растяжения (лишнее по краям обрезать).</summary>
        static Rect CoverUv(Texture t, float aspect)
        {
            float ta = (float)t.width / t.height;
            if (ta > aspect) { float w = aspect / ta; return new Rect((1f - w) / 2f, 0f, w, 1f); }
            float h = ta / aspect;
            return new Rect(0f, (1f - h) / 2f, 1f, h);
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
            Ui.Button(content, "Далее: гонка", "key", Ui.Big, () =>
            {
                if (createPassword.Length > 0 && createPassword.Length < CoopSession.MinPasswordLength)
                {
                    message = $"Пароль слишком короткий: нужно от {CoopSession.MinPasswordLength} символов.";
                    return;
                }
                SaveName();
                message = null;
                raceForCoop = true;
                Show(Screen.Race);   // дальше — какая будет гонка, и там «Создать сервер»
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
            var slots = new List<(UnityEngine.UI.Image host, UiText name, UnityEngine.UI.Image ready, UiText readyText)>();
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
                string race = s.RaceLabel;
                players.text = $"Игроки {s.Players.Count}/{GameSession.CoopMaxPlayers}" + (string.IsNullOrEmpty(race) ? "" : $"   ·   {race}");
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
                field.contentType = showPassword ? TMPro.TMP_InputField.ContentType.Standard : TMPro.TMP_InputField.ContentType.Password;
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
