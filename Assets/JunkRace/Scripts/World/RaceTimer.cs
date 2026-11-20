using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using DG.Tweening;

namespace JunkRace
{
    public enum MatchPhase : byte { Build, Countdown, Race, Results, Loading, RaceLoading }

    /// <summary>
    /// Ход заезда: загрузка (свалка, шейдеры — ждём всех) → сборка (таймер; «Готов» — G) →
    /// загрузка трассы (строится у всех одновременно) → отсчёт 3-2-1 на решётке → гонка → таблица мест.
    /// Решает хост (в соло — сам игрок) и рассылает фазы и финиши; места в гонке каждый считает сам
    /// по позициям машин на маршруте (маршрут и позиции у всех одни).
    /// </summary>
    public class RaceTimer : MonoBehaviour
    {
        public static RaceTimer Instance { get; private set; }

        /// <summary>Сменилась фаза (только на авторитете) — сеть рассылает.</summary>
        public static event Action PhaseChanged;
        /// <summary>Кто-то финишировал (только на авторитете) — сеть рассылает.</summary>
        public static event Action<Racer> Finished;

        [Tooltip("Сколько секунд на сборку машины.")]
        [SerializeField] float buildTime = 300f;
        [SerializeField] float countdownTime = 3f;
        [Tooltip("Сколько ждать остальных после первого финиша, с.")]
        [SerializeField] float waitAfterFirst = 90f;

        public class Racer
        {
            public ulong Id;
            public string Name;
            public float Time = -1f;   // < 0 — ещё едет (или не доехал)
            public int Parts;
            public bool Finished => Time >= 0f;
        }

        public MatchPhase Phase { get; private set; } = MatchPhase.Loading;

        /// <summary>Ждём, пока все загрузятся: экран загрузки, игроки стоят.</summary>
        public static bool IsLoading => Instance != null
            && ((Instance.Phase == MatchPhase.Loading && !Instance.localPrepared) || Instance.Phase == MatchPhase.RaceLoading);

        /// <summary>
        /// Детали брать, рыться в кучах, садиться нельзя: ещё не все загрузились (кто загрузился раньше —
        /// бегает по свалке, но фору не получает) или строится трасса.
        /// </summary>
        public static bool InteractionLocked => Instance != null
            && (Instance.Phase == MatchPhase.Loading || Instance.Phase == MatchPhase.RaceLoading);

        /// <summary>У меня свалка уже готова (дальше ждём остальных уже без экрана загрузки).</summary>
        bool localPrepared;

        /// <summary>Хост сам подготовился к этой загрузке (свалка/трасса построены, шейдеры прогреты).</summary>
        bool hostPrepared;

        /// <summary>Кто на сборке нажал «Готов» (у всех одинаково — рассылает хост).</summary>
        readonly HashSet<ulong> buildReady = new HashSet<ulong>();
        public bool IsBuildReady(ulong id) => buildReady.Contains(id);
        /// <summary>Сменился список «готов» на сборке (только на авторитете) — сеть рассылает.</summary>
        public static event Action BuildReadyChanged;
        public IEnumerable<ulong> BuildReadyIds => buildReady;

        /// <summary>Хост: игрок нажал / отжал «Готов» на сборке.</summary>
        public void ServerSetBuildReady(ulong id, bool ready)
        {
            if (Phase != MatchPhase.Build) return;
            bool changed = ready ? buildReady.Add(id) : buildReady.Remove(id);
            if (changed) BuildReadyChanged?.Invoke();
        }

        /// <summary>Клиент: список «готов» с хоста.</summary>
        public void ApplyBuildReady(IEnumerable<ulong> ids)
        {
            buildReady.Clear();
            foreach (var id in ids) buildReady.Add(id);
        }

        [Tooltip("Дольше этого не ждём отставших, с.")]
        [SerializeField] float loadingTimeout = 120f;
        float loadingStart;
        /// <summary>Клиенты, у которых трасса уже построена (прислали «готов»).</summary>
        readonly HashSet<ulong> readyClients = new HashSet<ulong>();

        /// <summary>Хост: клиент загрузился (построил трассу и получил всё состояние).</summary>
        public void ServerPlayerReady(ulong clientId) => readyClients.Add(clientId);

        static int ExpectedPlayers(out int ready, HashSet<ulong> readySet)
        {
            var nm = Unity.Netcode.NetworkManager.Singleton;
            if (nm == null || !nm.IsListening) { ready = 1; return 1; }
            ready = 1;   // сам хост
            foreach (var id in nm.ConnectedClientsIds)
                if (id != Unity.Netcode.NetworkManager.ServerClientId && readySet.Contains(id)) ready++;
            // Ждём всех из лобби, а не только уже подключившихся по сети: кто-то мог ещё не успеть подключиться.
            int session = CoopSession.Instance != null ? CoopSession.Instance.SessionPlayerCount : 0;
            return Mathf.Max(nm.ConnectedClientsIds.Count, session);
        }
        public IReadOnlyList<Racer> Racers => racers;

        /// <summary>Во время отсчёта машины стоят на решётке.</summary>
        public static bool HoldVehicles => Instance != null && (Instance.Phase == MatchPhase.Countdown || Instance.Phase == MatchPhase.RaceLoading);

        readonly List<Racer> racers = new List<Racer>();
        float phaseEnd;          // конец сборки / отсчёта (Time.time)
        float raceStart = -1f;   // Time.time старта гонки
        float resultsAt = -1f;   // Time.time конца гонки
        float firstFinish = -1f;

        /// <summary>Время гонки, с (-1 — ещё не стартовали).</summary>
        public float Elapsed => raceStart < 0f ? -1f : (resultsAt >= 0f ? resultsAt : Time.time) - raceStart;
        /// <summary>Сколько осталось до конца сборки / отсчёта, с.</summary>
        public float Remaining => Mathf.Max(0f, phaseEnd - Time.time);
        public bool IsFinished => Phase == MatchPhase.Results;
        /// <summary>Для сети: осталось (сборка, отсчёт) или прошло (гонка, итоги).</summary>
        public float PhaseTime => Phase == MatchPhase.Build || Phase == MatchPhase.Countdown ? Remaining
                                : Phase == MatchPhase.Loading || Phase == MatchPhase.RaceLoading ? 0f : Mathf.Max(0f, Elapsed);

        void Awake()
        {
            Instance = this;
            loadingStart = Time.time;
        }

        void Start()
        {
            // Хост и соло: свалка раскладывается в RouteGenerator.Start, здесь — прогрев шейдеров.
            if (Authority.IsAuthority) StartCoroutine(Prepare(false, () => { hostPrepared = true; localPrepared = true; }));
        }

        /// <summary>Клиент: всё состояние с хоста получено — прогреваем и сообщаем «готов».</summary>
        public void ClientPrepare(Action sendReady) =>
            StartCoroutine(Prepare(false, () => { localPrepared = true; sendReady?.Invoke(); }));

        /// <summary>
        /// Подготовка под экраном загрузки: (трасса) и шейдеры. Кадр-другой ждём, чтобы экран загрузки
        /// успел появиться — тяжёлое делаем уже под ним.
        /// </summary>
        System.Collections.IEnumerator Prepare(bool buildRoute, Action done)
        {
            yield return null;
            yield return null;
            if (buildRoute && RouteGenerator.Instance != null) RouteGenerator.Instance.BuildRoute();
            yield return null;
            // Shader.WarmupAllShaders() тут не годится: в Unity 6 + URP он сыплет ошибками keyword space
            // и шейдеры Metal всё равно не прогревает. Шейдеры компилируются, пока кадры рисуются под экраном загрузки.
            yield return null;
            done?.Invoke();
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            DOTween.Kill(WarnTween);
            paint?.Destroy();
            loadingPaint?.Destroy();
        }

        // ============================ Хост ============================

        void Update()
        {
            if (Phase == MatchPhase.Results && GameSession.CanRestart && !InGameMenu.IsOpen)
            {
                bool again = (Keyboard.current != null && (Keyboard.current.enterKey.wasPressedThisFrame || Keyboard.current.numpadEnterKey.wasPressedThisFrame))
                             || (Gamepad.current != null && Gamepad.current.buttonEast.wasPressedThisFrame);
                if (again) { GameSession.RestartTrack(); return; }
            }
            if (!Authority.IsAuthority) return;
            switch (Phase)
            {
                case MatchPhase.Loading:
                {
                    // Таймер сборки — общий: стартует, когда трасса готова у всех (или отставших ждали слишком долго).
                    bool built = RouteGenerator.Instance == null || RouteGenerator.Instance.YardReady;
                    int expected = ExpectedPlayers(out int ready, readyClients);
                    bool timedOut = Time.time - loadingStart > loadingTimeout;
                    if (built && hostPrepared && (ready >= expected || timedOut))
                    {
                        Debug.Log($"[JunkRace] Старт сборки: готовы {ready}/{expected}" + (timedOut ? " — остальных не дождались" : ""));
                        Phase = MatchPhase.Build;
                        phaseEnd = Time.time + buildTime;
                        PhaseChanged?.Invoke();
                    }
                    break;
                }
                case MatchPhase.Build:
                    // Старт — когда все нажали «Готов» (G) или кончилось время. Просто сесть в машину — не старт.
                    if (AllBuildReady() || (Remaining <= 0f && !GameSession.IsDevRoom)) EnterRaceLoading();
                    break;
                case MatchPhase.RaceLoading:
                {
                    int expected = ExpectedPlayers(out int ready, readyClients);
                    bool timedOut = Time.time - loadingStart > loadingTimeout;
                    if (hostPrepared && (ready >= expected || timedOut))
                    {
                        Debug.Log($"[JunkRace] Трасса готова у {ready}/{expected}" + (timedOut ? " — остальных не дождались" : ""));
                        StartCountdown();
                    }
                    break;
                }
                case MatchPhase.Countdown:
                    if (Remaining <= 0f)
                    {
                        Phase = MatchPhase.Race;
                        raceStart = Time.time;
                        PhaseChanged?.Invoke();
                    }
                    break;
                case MatchPhase.Race:
                    bool everyone = racers.Count > 0;
                    foreach (var r in racers) if (!r.Finished && World.Get(r.Id) != null) everyone = false;
                    if (everyone || (firstFinish >= 0f && Time.time - firstFinish > waitAfterFirst)) EndRace();
                    break;
            }
        }

        bool AllBuildReady()
        {
            int n = 0;
            foreach (var a in World.All())
            {
                if (!buildReady.Contains(a.ClientId)) return false;
                n++;
            }
            return n > 0;
        }

        /// <summary>Сборка кончилась: всех с машинами — за руль, и у всех строится трасса (вторая загрузка).</summary>
        void EnterRaceLoading()
        {
            racers.Clear();
            foreach (var a in new List<IAvatar>(World.All()))
            {
                var v = World.ServerSeatForRace(a);
                if (v == null) continue;
                racers.Add(new Racer { Id = a.ClientId, Name = a.DisplayName });
            }
            readyClients.Clear();
            hostPrepared = false;
            loadingStart = Time.time;
            Phase = MatchPhase.RaceLoading;
            PhaseChanged?.Invoke();
            StartCoroutine(Prepare(true, () => hostPrepared = true));
        }

        /// <summary>Трасса готова у всех: машины на решётку и отсчёт.</summary>
        void StartCountdown()
        {
            var route = RouteMap.Current;
            for (int i = 0; i < racers.Count; i++)
            {
                var v = VehicleOf(racers[i].Id);
                if (v == null || route == null) continue;
                route.GridSlot(i, out var pos, out var fwd);
                v.PlaceAt(pos, fwd);
            }
            Phase = MatchPhase.Countdown;
            phaseEnd = Time.time + countdownTime;
            PhaseChanged?.Invoke();
        }

        void EndRace()
        {
            Phase = MatchPhase.Results;
            resultsAt = Time.time;
            PhaseChanged?.Invoke();
        }

        /// <summary>Машина въехала в финиш (только авторитет).</summary>
        public void Finish(Vehicle v)
        {
            if (Phase != MatchPhase.Race || v == null) return;
            ulong id = ulong.MaxValue;
            if (v.IsOwned) id = v.OwnerId;
            else foreach (var a in World.All()) if (a.Driving == v) { id = a.ClientId; break; }
            if (id == ulong.MaxValue) return;

            var r = FindOrAdd(id);
            if (r.Finished) return;
            r.Time = Time.time - raceStart;
            r.Parts = v.PartCount;
            if (firstFinish < 0f) firstFinish = Time.time;
            Finished?.Invoke(r);
        }

        Racer Find(ulong id)
        {
            foreach (var r in racers) if (r.Id == id) return r;
            return null;
        }

        /// <summary>Опоздал к старту, но доехал — тоже в таблицу.</summary>
        Racer FindOrAdd(ulong id)
        {
            var r = Find(id);
            if (r != null) return r;
            var a = World.Get(id);
            r = new Racer { Id = id, Name = a != null ? a.DisplayName : "Игрок" };
            racers.Add(r);
            return r;
        }

        // ============================ Применение (клиент) ============================

        /// <summary>Фаза с хоста. time — осталось (сборка, отсчёт) или прошло (гонка, итоги).</summary>
        public void ApplyPhase(MatchPhase phase, float time, IList<ulong> ids)
        {
            var was = Phase;
            Phase = phase;
            if (phase == MatchPhase.RaceLoading && was != MatchPhase.RaceLoading)
                StartCoroutine(Prepare(true, () => World.SendReady()));   // трасса строится у всех одновременно
            else if (phase == MatchPhase.Countdown || phase == MatchPhase.Race || phase == MatchPhase.Results)
                RouteGenerator.Instance?.BuildRoute();   // зашёл позже — трасса сразу (если уже есть — ничего)
            switch (phase)
            {
                case MatchPhase.Build:
                case MatchPhase.Countdown:
                    phaseEnd = Time.time + time;
                    break;
                case MatchPhase.Loading:
                case MatchPhase.RaceLoading:
                    break;
                case MatchPhase.Race:
                    raceStart = Time.time - time;
                    break;
                case MatchPhase.Results:
                    raceStart = Time.time - time;
                    resultsAt = Time.time;
                    break;
            }
            if (ids != null) foreach (var id in ids) FindOrAdd(id);
        }

        public void ApplyFinish(ulong id, float time, int parts)
        {
            var r = FindOrAdd(id);
            r.Time = time;
            r.Parts = parts;
        }

        // ============================ Места ============================

        readonly List<(Racer r, float key)> order = new List<(Racer, float)>();
        readonly List<Racer> standings = new List<Racer>();

        /// <summary>Гонщики по местам: финишировавшие по времени, остальные — кто дальше по трассе.</summary>
        public List<Racer> Standings()
        {
            order.Clear();
            var route = RouteMap.Current;
            foreach (var r in racers)
            {
                float key;
                if (r.Finished) key = 1e9f - r.Time;
                else
                {
                    var v = VehicleOf(r.Id);
                    key = v != null && route != null ? route.Progress(v.transform.position) : -1e9f;
                }
                order.Add((r, key));
            }
            order.Sort((a, b) => b.key.CompareTo(a.key));
            standings.Clear();
            foreach (var o in order) standings.Add(o.r);
            return standings;
        }

        public static Vehicle VehicleOf(ulong id)
        {
            var a = World.Get(id);
            if (a != null && a.Driving != null) return a.Driving;
            foreach (var v in Vehicle.All) if (v.OwnerId == id) return v;
            return null;
        }

        // ============================ Экран ============================
        // HUD рисуется через UiPainter (UGUI, каждый кадр): IMGUI в Linear выбеливал цвета.

        bool cursorFreed;
        float placesTimer;
        readonly List<Racer> shownPlaces = new List<Racer>();
        UiPainter paint, loadingPaint;

        static readonly Color LabelColor = new Color(0.92f, 0.92f, 0.92f);
        static readonly UiPainter.Style StyleText = new UiPainter.Style(28, LabelColor, bold: true);
        static readonly UiPainter.Style StyleCenter = StyleText.With(TextAnchor.MiddleCenter);
        static readonly UiPainter.Style StyleBig = new UiPainter.Style(120, LabelColor, TextAnchor.MiddleCenter, true, false);
        static readonly UiPainter.Style StyleRow = new UiPainter.Style(22, LabelColor);
        static readonly UiPainter.Style StyleRowCenter = StyleRow.With(TextAnchor.MiddleCenter);
        static readonly UiPainter.Style StyleRowBold = StyleRow.WithBold(true);
        static readonly UiPainter.Style StyleWarnCenter = StyleRowCenter.With(new Color(1f, 0.55f, 0.35f));
        static readonly Color HudLight = new Color(0.96f, 0.92f, 0.84f);
        static readonly UiPainter.Style StyleHudWarn = new UiPainter.Style(16, new Color(1f, 0.97f, 0.9f), TextAnchor.MiddleCenter, true, false);
        static readonly UiPainter.Style StyleSign = new UiPainter.Style(24, new Color(1f, 0.75f, 0.3f), TextAnchor.MiddleCenter, true, false);
        static readonly UiPainter.Style StyleLeft = new UiPainter.Style(18, HudLight, TextAnchor.MiddleLeft, false, false);

        void LateUpdate()
        {
            paint ??= new UiPainter("Гонка", 10);
            loadingPaint ??= new UiPainter("Загрузка", 100);   // поверх всего HUD
            paint.Begin();
            loadingPaint.Begin();
            if (!InGameMenu.IsOpen) DrawHud();
            paint.End();
            loadingPaint.End();
        }

        void DrawHud()
        {
            float w = Screen.width, h = Screen.height;
            switch (Phase)
            {
                case MatchPhase.Loading:
                    if (localPrepared) DrawWaitingOthers(w);
                    else DrawLoading("Загрузка свалки");
                    break;
                case MatchPhase.RaceLoading:
                    DrawLoading("Строим трассу");
                    break;
                case MatchPhase.Build:
                    DrawBuild(w);
                    break;
                case MatchPhase.Countdown:
                    if (!DrawCountdown(w, h)) paint.Label(new Rect(0, h * 0.3f, w, 160), Mathf.CeilToInt(Remaining).ToString(), StyleBig);
                    break;
                case MatchPhase.Race:
                {
                    float t = Elapsed;
                    if (t < 1.2f) paint.Label(new Rect(0, h * 0.3f, w, 160), "ВПЕРЁД!", StyleBig);
                    placesTop = DrawRaceClock(t);
                    DrawPlaces();
                    break;
                }
                case MatchPhase.Results:
                    DrawResults();
                    break;
            }
        }

        static Texture2D loadingTex;

        /// <summary>Сам загрузился, другие ещё нет: бегать можно, брать — нельзя.</summary>
        void DrawWaitingOthers(float w)
        {
            string who = "";
            var nm = Unity.Netcode.NetworkManager.Singleton;
            if (Authority.IsAuthority && nm != null && nm.IsListening)
            {
                int expected = ExpectedPlayers(out int ready, readyClients);
                who = $" ({ready}/{expected})";
            }
            paint.Label(new Rect(0, 16, w, 40), "Ждём остальных игроков" + who, StyleCenter);
            paint.Label(new Rect(0, 52, w, 30), "Можно осмотреться. Брать детали — когда загрузятся все и пойдёт таймер", StyleRowCenter);
        }

        // ============================ Верх экрана: табличка, лента, предупреждение ============================

        static Texture2D[] digitTex;
        static Texture2D digitColon, timerSign, hintBanner, warnPlate, racePlate, iconBuilding, iconReady;
        static bool hudLoaded;
        float placesTop = 60f;

        // Окна под текст внутри картинок (доли картинки).
        static readonly Rect SignInner = new Rect(0.07f, 0.45f, 0.86f, 0.44f);
        static readonly Rect BannerInner = new Rect(0.17f, 0.22f, 0.66f, 0.57f);
        static readonly Rect RaceInner = new Rect(0.31f, 0.35f, 0.62f, 0.49f);
        // RectOffset нельзя создавать при инициализации MonoBehaviour — только при первом использовании.
        static RectOffset warnBorder, placesBorder;
        static RectOffset WarnBorder => warnBorder ??= new RectOffset(170, 170, 0, 0);
        static RectOffset PlacesBorder => placesBorder ??= new RectOffset(70, 70, 70, 70);

        bool LoadHud()
        {
            if (!hudLoaded)
            {
                hudLoaded = true;
                digitTex = new Texture2D[10];
                for (int i = 0; i < 10; i++) digitTex[i] = Resources.Load<Texture2D>("UI/hud/digit_" + i);
                digitColon = Resources.Load<Texture2D>("UI/hud/digit_colon");
                timerSign = Resources.Load<Texture2D>("UI/hud/timer_sign");
                hintBanner = Resources.Load<Texture2D>("UI/hud/hint_banner");
                warnPlate = Resources.Load<Texture2D>("UI/hud/warn_plate");
                racePlate = Resources.Load<Texture2D>("UI/hud/race_plate");
                iconBuilding = Resources.Load<Texture2D>("UI/hud/icon_status_building");
                iconReady = Resources.Load<Texture2D>("UI/hud/icon_status_ready");
                placesPanel = Resources.Load<Texture2D>("UI/hud/card_panel");
            }
            bool ok = timerSign != null && hintBanner != null && warnPlate != null && digitColon != null;
            if (ok) foreach (var d in digitTex) if (d == null) { ok = false; break; }
            return ok;
        }

        static Rect Inner(Rect r, Rect frac) => new Rect(r.x + r.width * frac.x, r.y + r.height * frac.y, r.width * frac.width, r.height * frac.height);

        static float DigitsWidth(string text, float height)
        {
            float w = 0f;
            foreach (char c in text)
            {
                var t = c == ':' ? digitColon : c >= '0' && c <= '9' ? digitTex[c - '0'] : null;
                if (t != null) w += height * t.width / t.height * 0.92f;
            }
            return w;
        }

        /// <summary>Цифры из картинок (0–9 и двоеточие) по центру области.</summary>
        void DrawDigits(string text, Rect area, float height)
        {
            float x = area.center.x - DigitsWidth(text, height) / 2f, y = area.center.y - height / 2f;
            foreach (char c in text)
            {
                var t = c == ':' ? digitColon : c >= '0' && c <= '9' ? digitTex[c - '0'] : null;
                if (t == null) continue;
                float cw = height * t.width / t.height;
                float ch = c == ':' ? height * 0.8f : height;
                paint.Tex(new Rect(x, y + (height - ch) / 2f, cw, ch), t);
                x += cw * 0.92f;   // буквы чуть внахлёст — как приклёпанные
            }
        }

        void DrawBuild(float w)
        {
            float t = Remaining;
            ulong me = World.LocalId;
            bool dev = GameSession.IsDevRoom;
            string hint = dev ? "Все детали разложены рядами. Собери машину и нажми G — поехать на трассу"
                : buildReady.Contains(me) ? "Ты готов. Ждём остальных (G — отменить)"
                : "Собери машину и нажми G — «Готов». Когда готовы все — старт";
            string problem = CarProblem(me);

            if (!LoadHud())
            {
                paint.Label(new Rect(0, 16, w, 40), dev ? "Тест-комната: без таймера" : $"Сборка: {(int)(t / 60)}:{(int)(t % 60):00}", StyleCenter);
                paint.Label(new Rect(0, 52, w, 30), hint, StyleRowCenter);
                if (problem != null) paint.Label(new Rect(0, 116, w, 30), problem, StyleWarnCenter);
                return;
            }

            float k = Screen.height / 1080f;
            // Табличка на цепях: время сборки (подсказка и список игроков — в окне Tab).
            float sw = 240f * k, sh = sw * timerSign.height / timerSign.width;
            var sign = new Rect((w - sw) / 2f, -14f * k, sw, sh);
            paint.Tex(sign, timerSign);
            var signIn = Inner(sign, SignInner);
            if (dev) paint.Label(signIn, "ТЕСТ", StyleSign.With(Mathf.RoundToInt(24f * k)));
            else DrawDigits($"{(int)(t / 60)}:{(int)(t % 60):00}", signIn, signIn.height * 0.86f);

            // Маленькая плашка под таймером — только пока машины ещё нет (меньше двух деталей).
            // Появление и исчезновение — DOTween (выпрыгивает с перелётом, уходит сжимаясь).
            var mine = VehicleOf(me);
            bool need = mine == null || mine.PartCount < 2;
            if (need) warnMsg = mine == null ? "Нет машины: возьми корпус" : "Прикрути к корпусу первую деталь";
            if (need != warnTarget)
            {
                warnTarget = need;
                DOTween.Kill(WarnTween);
                DOTween.To(() => warnShow, v => warnShow = v, need ? 1f : 0f, need ? 0.35f : 0.25f)
                    .SetEase(need ? Ease.OutBack : Ease.InBack).SetUpdate(true).SetId(WarnTween);
            }
            if (warnShow > 0.01f && warnMsg != null)
            {
                var st = StyleHudWarn.With(Mathf.RoundToInt(16f * k));
                float ph = 44f * k, pw = paint.Measure(warnMsg, st).x + 2f * 175f * (ph / warnPlate.height) + 20f * k;
                var plate = new Rect((w - pw) / 2f, sign.yMax - 2f * k, pw, ph);
                // Масштаб от центра плашки + прозрачность.
                float sc = Mathf.Max(0.01f, warnShow);
                var tint = new Color(1f, 1f, 1f, Mathf.Clamp01(warnShow));
                paint.Sliced(plate, warnPlate, WarnBorder, ph / warnPlate.height, tint, sc);
                paint.Label(plate, warnMsg, st.With(st.Color * tint), sc);
            }
        }

        const string WarnTween = "warn_plate";
        float warnShow;
        bool warnTarget;
        string warnMsg;


        /// <summary>Подсказка на сборке (для окна Tab).</summary>
        public string BuildHint(ulong id) =>
            GameSession.IsDevRoom ? "Все детали разложены рядами. Собери машину и нажми G — поехать на трассу"
            : buildReady.Contains(id) ? "Ты готов. Ждём остальных (G — отменить)"
            : "Собери машину и нажми G — «Готов». Когда готовы все — старт";

        /// <summary>Чего не хватает машине игрока (для окна Tab), null — всё есть.</summary>
        public static string ProblemOf(ulong id) => CarProblem(id);

        /// <summary>Отсчёт 3-2-1 нарисованными цифрами: каждая «выпрыгивает» и чуть уменьшается.</summary>
        bool DrawCountdown(float w, float h)
        {
            if (!LoadHud()) return false;
            int n = Mathf.CeilToInt(Remaining);
            if (n < 0 || n > 9) return false;
            float frac = Remaining - Mathf.Floor(Remaining);   // 1 → 0 за секунду
            float pop = 1f + 0.35f * Mathf.Pow(Mathf.Clamp01(frac - 0.6f) / 0.4f, 2f);
            float dh = 230f * (h / 1080f) * pop;
            DrawDigits(n.ToString(), new Rect(0f, h * 0.36f - dh / 2f, w, dh), dh);
            return true;
        }

        /// <summary>Время заезда на табличке с секундомером (слева вверху). Возвращает низ таблички — дальше места.</summary>
        float DrawRaceClock(float t)
        {
            if (!LoadHud() || racePlate == null)
            {
                paint.Label(new Rect(20, 20, 300, 40), FormatTime(t), StyleText);
                return 60f;
            }
            float k = Screen.height / 1080f;
            float pw = 330f * k, ph = pw * racePlate.height / racePlate.width;
            var plate = new Rect(16f * k, 12f * k, pw, ph);
            paint.Tex(plate, racePlate);
            var inner = Inner(plate, RaceInner);
            DrawDigits($"{(int)(t / 60)}:{(int)(t % 60):00}", inner, inner.height * 0.9f);
            return plate.yMax + 4f * k;
        }

        /// <summary>Чего не хватает своей машине (null — всё есть).</summary>
        static string CarProblem(ulong id)
        {
            var v = VehicleOf(id);
            if (v == null) return "У тебя ещё нет машины: возьми корпус и прикрути к нему деталь";
            int wheels = 0;
            foreach (var p in v.Parts) if (p.Slot == SlotType.Wheel) wheels++;
            var missing = new List<string>();
            if (wheels < 3) missing.Add("колёс");
            if (v.SteerQuality <= 0f) missing.Add("руля");
            if (v.TotalPower <= 0f) missing.Add("мотора");
            return missing.Count == 0 ? null : "Не хватает: " + string.Join(", ", missing);
        }

        // ---- Экран загрузки: случайная картинка из Resources/UI/loading, полоска справа внизу ----
        static Texture2D[] loadingArts;
        static Texture2D barFrame, barFill, barPlate;
        static bool loadingArtLoaded;
        Texture2D loadingArt;
        float loadingArtFor = -1f;

        // Окно под заливку внутри рамки (доли картинки loading_frame.png).
        static readonly Rect BarSlot = new Rect(0.083f, 0.36f, 0.835f, 0.43f);

        void DrawLoading(string title)
        {
            if (loadingTex == null)
            {
                loadingTex = new Texture2D(1, 1);
                loadingTex.SetPixel(0, 0, new Color(0.12f, 0.11f, 0.10f, 1f));
                loadingTex.Apply();
            }
            if (!loadingArtLoaded)
            {
                loadingArtLoaded = true;
                loadingArts = Resources.LoadAll<Texture2D>("UI/loading");
                barFrame = Resources.Load<Texture2D>("UI/loading_frame");
                barFill = Resources.Load<Texture2D>("UI/loading_fill");
                barPlate = Resources.Load<Texture2D>("UI/loading_plate");
            }
            // Новая загрузка — новая случайная картинка (у каждого своя, это просто фон).
            if (loadingArtFor != loadingStart)
            {
                loadingArtFor = loadingStart;
                loadingArt = loadingArts != null && loadingArts.Length > 0 ? loadingArts[UnityEngine.Random.Range(0, loadingArts.Length)] : null;
            }

            var p = loadingPaint;
            float w = Screen.width, h = Screen.height;
            var full = new Rect(0, 0, w, h);
            p.Tex(full, loadingTex);
            if (loadingArt != null) p.Tex(full, loadingArt, UiDraw.CropUv(full, loadingArt));

            // Прогресс: настоящего процента нет — плавно ползёт к 90%, в сети подтягивается по готовым игрокам.
            float t = Time.time - loadingStart;
            float progress = 0.9f * (1f - Mathf.Exp(-t / 3f));
            var nm = Unity.Netcode.NetworkManager.Singleton;
            string who = null;
            if (nm != null && nm.IsListening)
            {
                int expected = ExpectedPlayers(out int ready, readyClients);
                if (expected > 0) progress = Mathf.Max(progress, 0.95f * ready / expected);
                who = Authority.IsAuthority
                    ? $"Игроки готовы: {ready}/{expected}"
                    : "Ждём остальных игроков";
            }

            float k = h / 1080f;
            if (barFrame != null && barFill != null)
            {
                float bw = 620f * k, bh = bw * barFrame.height / barFrame.width;
                var frame = new Rect(w - bw - 40f * k, h - bh - 40f * k, bw, bh);
                var slot = new Rect(frame.x + frame.width * BarSlot.x, frame.y + frame.height * BarSlot.y,
                                    frame.width * BarSlot.width, frame.height * BarSlot.height);
                p.Tex(frame, barFrame);
                // Полосы не сжимаются: показываем левую часть картинки шириной progress.
                float pr = Mathf.Clamp01(progress);
                if (pr > 0.01f)
                    p.Tex(new Rect(slot.x, slot.y, slot.width * pr, slot.height), barFill, new Rect(0f, 0f, pr, 1f));

                // Табличка с надписью над правым краем полосы.
                int dots = 1 + (int)(Time.unscaledTime * 2f) % 3;
                var st = new UiPainter.Style(Mathf.RoundToInt(32f * k), new Color(0.27f, 0.17f, 0.1f), TextAnchor.MiddleCenter, true, true);
                float pw = 300f * k, ph = barPlate != null ? pw * barPlate.height / barPlate.width : 90f * k;
                var plate = new Rect(frame.xMax - pw - 30f * k, frame.y - ph + 6f * k, pw, ph);
                if (barPlate != null) p.Tex(plate, barPlate);
                p.Label(new Rect(plate.x + 24f * k, plate.y, plate.width - 48f * k, plate.height),
                        title + new string('.', dots) + (who != null ? "\n<size=" + Mathf.RoundToInt(20f * k) + ">" + who + "</size>" : ""), st);
                return;
            }

            // Нет картинок полоски — как раньше: надпись по центру.
            int d2 = 1 + (int)(Time.unscaledTime * 2f) % 3;
            p.Label(new Rect(0, h * 0.42f, w, 50), title + new string('.', d2), StyleCenter);
            if (who != null) p.Label(new Rect(0, h * 0.42f + 56, w, 34), who, StyleRowCenter);
        }

        void DrawPlaces()
        {
            // Места — 4 раза в секунду (поиск по маршруту не бесплатный).
            if (Time.unscaledTime >= placesTimer)
            {
                placesTimer = Time.unscaledTime + 0.25f;
                shownPlaces.Clear();
                shownPlaces.AddRange(Standings());
            }
            if (shownPlaces.Count == 0) return;
            ulong me = World.LocalId;
            int myPlace = shownPlaces.FindIndex(r => r.Id == me);
            float top = placesTop;

            if (!LoadHud() || placesPanel == null)
            {
                if (myPlace >= 0) paint.Label(new Rect(20, top, 400, 40), $"Место {myPlace + 1}/{shownPlaces.Count}", StyleText);
                float y0 = top + 48f;
                for (int i = 0; i < shownPlaces.Count; i++, y0 += 26f)
                {
                    var r = shownPlaces[i];
                    string mark = r.Id == me ? "▶ " : "   ";
                    paint.Label(new Rect(20, y0, 500, 26), $"{mark}{i + 1}. {r.Name}{(r.Finished ? "  — " + FormatTime(r.Time) : "")}", StyleRow);
                }
                return;
            }

            // Ржавая панель (как у карточки детали): сверху «МЕСТО 2 из 4», ниже — строки гонщиков.
            // Все цифры одного размера с текстом строки; рамка растёт вниз по числу гонщиков.
            float k = Screen.height / 1080f;
            float pw = 300f * k;
            float padX = 44f * k, padTop = 40f * k, padBottom = 50f * k;   // рамка картинки толстая — отступаем от неё
            float rowH = 34f * k, headH = 40f * k, gap = 8f * k;
            float ph = padTop + headH + gap + shownPlaces.Count * rowH + padBottom;
            var panel = new Rect(16f * k, top, pw, ph);
            paint.Sliced(panel, placesPanel, PlacesBorder, 0.45f * k);

            float x = panel.x + padX, y = panel.y + padTop, cw = pw - padX * 2f;
            float dh = rowH * 0.72f;   // высота нарисованной цифры — чуть выше строчной буквы
            var left = StyleLeft.With(Mathf.RoundToInt(18f * k));
            if (myPlace >= 0)
            {
                // «МЕСТО 2 из 4» в одну строку: слово, цифра, «из», цифра — одного размера.
                var bold = left.WithBold(true);
                float wx = x;
                float wWord = paint.Measure("МЕСТО", bold).x;
                paint.Label(new Rect(wx, y, wWord + 4f, headH), "МЕСТО", bold);
                wx += wWord + 10f * k;
                string mine = (myPlace + 1).ToString();
                float mw = DigitsWidth(mine, dh);
                DrawDigits(mine, new Rect(wx, y, mw, headH), dh);
                wx += mw + 10f * k;
                float wOf = paint.Measure("из", left).x;
                paint.Label(new Rect(wx, y, wOf + 4f, headH), "из", left);
                wx += wOf + 10f * k;
                string all = shownPlaces.Count.ToString();
                DrawDigits(all, new Rect(wx, y, DigitsWidth(all, dh), headH), dh);
            }
            y += headH + gap;
            for (int i = 0; i < shownPlaces.Count; i++, y += rowH)
            {
                var r = shownPlaces[i];
                bool me2 = r.Id == me;
                string pos = (i + 1).ToString();
                DrawDigits(pos, new Rect(x, y, DigitsWidth(pos, dh), rowH), dh);
                string name = me2 ? "<color=#FFB347><b>" + r.Name + "</b></color>" : r.Name;
                if (r.Finished) name += "  <color=#C8C2B8>" + FormatTime(r.Time) + "</color>";
                paint.Label(new Rect(x + 34f * k, y, cw - 34f * k, rowH), name, left);
            }
        }

        static Texture2D placesPanel;

        void DrawResults()
        {
            if (Time.unscaledTime >= placesTimer)
            {
                placesTimer = Time.unscaledTime + 0.5f;
                shownPlaces.Clear();
                shownPlaces.AddRange(Standings());
            }
            // Мышь — чтобы нажать кнопку.
            if (!cursorFreed) { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; cursorFreed = true; }
            var list = shownPlaces;
            float bw = 640f, bh = 170f + Mathf.Max(1, list.Count) * 34f;
            var box = new Rect((Screen.width - bw) / 2f, (Screen.height - bh) / 2f, bw, bh);
            paint.Box(box, new Color(0f, 0f, 0f, 0.7f));
            paint.Label(new Rect(box.x, box.y + 12, bw, 40), "Итоги заезда", StyleCenter);
            float y = box.y + 64f;
            if (list.Count == 0) paint.Label(new Rect(box.x, y, bw, 30), "Никто не стартовал", StyleRowCenter);
            for (int i = 0; i < list.Count; i++, y += 34f)
            {
                var r = list[i];
                var st = r.Id == World.LocalId ? StyleRowBold : StyleRow;
                paint.Label(new Rect(box.x + 30, y, 60, 30), $"{i + 1}.", st);
                paint.Label(new Rect(box.x + 80, y, 260, 30), r.Name, st);
                paint.Label(new Rect(box.x + 340, y, 130, 30), r.Finished ? FormatTime(r.Time) : "не доехал", st);
                paint.Label(new Rect(box.x + 480, y, 150, 30), r.Finished ? $"деталей: {r.Parts}" : "", st);
            }
            if (GameSession.CanRestart)
            {
                if (paint.Button(new Rect(box.x + (bw - 320f) / 2f, box.y + bh - 100, 320f, 60f), "Ещё заезд (Enter)"))
                    GameSession.RestartTrack();
            }
            else paint.Label(new Rect(box.x, box.y + bh - 86, bw, 34), "Ждём, когда хост запустит ещё заезд…", StyleRowCenter);
            paint.Label(new Rect(box.x, box.y + bh - 40, bw, 30), "Esc — меню", StyleRowCenter);
        }

        static string FormatTime(float t) => t < 0f ? "--:--" : $"{(int)(t / 60):00}:{t % 60:00.0}";
    }
}
