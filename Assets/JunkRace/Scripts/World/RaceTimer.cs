using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

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
        [SerializeField] float buildTime = 180f;
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

        void OnDestroy() { if (Instance == this) Instance = null; }

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
                    if (AllBuildReady() || Remaining <= 0f) EnterRaceLoading();
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

        static Vehicle VehicleOf(ulong id)
        {
            var a = World.Get(id);
            if (a != null && a.Driving != null) return a.Driving;
            foreach (var v in Vehicle.All) if (v.OwnerId == id) return v;
            return null;
        }

        // ============================ Экран ============================

        GUIStyle style, big, center, row, rowCenter, rowBold;
        bool cursorFreed;
        float placesTimer;
        readonly List<Racer> shownPlaces = new List<Racer>();

        void OnGUI()
        {
            if (InGameMenu.IsOpen) return;
            style ??= new GUIStyle(GUI.skin.label) { fontSize = 28, fontStyle = FontStyle.Bold };
            center ??= new GUIStyle(style) { alignment = TextAnchor.MiddleCenter };
            big ??= new GUIStyle(style) { fontSize = 120, alignment = TextAnchor.MiddleCenter };
            row ??= new GUIStyle(GUI.skin.label) { fontSize = 22 };
            rowCenter ??= new GUIStyle(row) { alignment = TextAnchor.MiddleCenter };
            rowBold ??= new GUIStyle(row) { fontStyle = FontStyle.Bold };
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
                    GUI.Label(new Rect(0, h * 0.3f, w, 160), Mathf.CeilToInt(Remaining).ToString(), big);
                    break;
                case MatchPhase.Race:
                {
                    float t = Elapsed;
                    if (t < 1.2f) GUI.Label(new Rect(0, h * 0.3f, w, 160), "ВПЕРЁД!", big);
                    GUI.Label(new Rect(20, 20, 300, 40), FormatTime(t), style);
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
            GUI.Label(new Rect(0, 16, w, 40), "Ждём остальных игроков" + who, center);
            GUI.Label(new Rect(0, 52, w, 30), "Можно осмотреться. Брать детали — когда загрузятся все и пойдёт таймер", rowCenter);
        }

        void DrawBuild(float w)
        {
            float t = Remaining;
            GUI.Label(new Rect(0, 16, w, 40), $"Сборка: {(int)(t / 60)}:{(int)(t % 60):00}", center);
            ulong me = World.LocalId;
            bool meReady = buildReady.Contains(me);
            GUI.Label(new Rect(0, 52, w, 30), meReady
                ? "Ты готов. Ждём остальных (G — отменить)"
                : "Собери машину и нажми G — «Готов». Когда готовы все — старт", rowCenter);

            // Кто готов.
            var sb = new System.Text.StringBuilder();
            foreach (var a in World.All())
            {
                if (sb.Length > 0) sb.Append("     ");
                sb.Append(a.DisplayName).Append(buildReady.Contains(a.ClientId) ? " — готов" : " — собирает");
            }
            GUI.Label(new Rect(0, 84, w, 30), sb.ToString(), rowCenter);

            // Подсказка, если машина не доедет.
            string problem = CarProblem(me);
            if (problem != null) GUI.Label(new Rect(0, 116, w, 30), problem, warnCenter ??= MakeWarn());
        }

        GUIStyle warnCenter;
        GUIStyle MakeWarn()
        {
            var st = new GUIStyle(rowCenter);
            st.normal.textColor = new Color(1f, 0.55f, 0.35f);
            return st;
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

        void DrawLoading(string title)
        {
            // Поверх всего: пока не загрузились все, ничего не видно и не двигается.
            GUI.depth = -100;
            if (loadingTex == null)
            {
                loadingTex = new Texture2D(1, 1);
                loadingTex.SetPixel(0, 0, new Color(0.12f, 0.11f, 0.10f, 1f));
                loadingTex.Apply();
            }
            float w = Screen.width, h = Screen.height;
            GUI.DrawTexture(new Rect(0, 0, w, h), loadingTex);
            int dots = 1 + (int)(Time.unscaledTime * 2f) % 3;
            GUI.Label(new Rect(0, h * 0.42f, w, 50), title + new string('.', dots), center);
            var nm = Unity.Netcode.NetworkManager.Singleton;
            if (nm != null && nm.IsListening)
            {
                int expected = ExpectedPlayers(out int ready, readyClients);
                string who = Authority.IsAuthority
                    ? $"Игроки готовы: {ready}/{expected}"
                    : "Ждём остальных игроков — таймер у всех стартует одновременно";
                GUI.Label(new Rect(0, h * 0.42f + 56, w, 34), who, rowCenter);
            }
        }

        void DrawPlaces()
        {
            // Места — 4 раза в секунду (поиск по маршруту не бесплатный), а OnGUI зовётся несколько раз за кадр.
            if (Time.unscaledTime >= placesTimer)
            {
                placesTimer = Time.unscaledTime + 0.25f;
                shownPlaces.Clear();
                shownPlaces.AddRange(Standings());
            }
            if (shownPlaces.Count == 0) return;
            ulong me = World.LocalId;
            int myPlace = shownPlaces.FindIndex(r => r.Id == me);
            if (myPlace >= 0) GUI.Label(new Rect(20, 60, 400, 40), $"Место {myPlace + 1}/{shownPlaces.Count}", style);
            float y = 110f;
            for (int i = 0; i < shownPlaces.Count; i++, y += 26f)
            {
                var r = shownPlaces[i];
                string mark = r.Id == me ? "▶ " : "   ";
                GUI.Label(new Rect(20, y, 500, 26), $"{mark}{i + 1}. {r.Name}{(r.Finished ? "  — " + FormatTime(r.Time) : "")}", row);
            }
        }

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
            GUI.Box(box, "");
            GUI.Label(new Rect(box.x, box.y + 12, bw, 40), "Итоги заезда", center);
            float y = box.y + 64f;
            if (list.Count == 0) GUI.Label(new Rect(box.x, y, bw, 30), "Никто не стартовал", rowCenter);
            for (int i = 0; i < list.Count; i++, y += 34f)
            {
                var r = list[i];
                var st = r.Id == World.LocalId ? rowBold : row;
                GUI.Label(new Rect(box.x + 30, y, 60, 30), $"{i + 1}.", st);
                GUI.Label(new Rect(box.x + 80, y, 260, 30), r.Name, st);
                GUI.Label(new Rect(box.x + 340, y, 130, 30), r.Finished ? FormatTime(r.Time) : "не доехал", st);
                GUI.Label(new Rect(box.x + 480, y, 150, 30), r.Finished ? $"деталей: {r.Parts}" : "", st);
            }
            if (GameSession.CanRestart)
            {
                if (GUI.Button(new Rect(box.x + (bw - 300f) / 2f, box.y + bh - 90, 300f, 44f), "Ещё заезд (Enter)"))
                    GameSession.RestartTrack();
            }
            else GUI.Label(new Rect(box.x, box.y + bh - 86, bw, 34), "Ждём, когда хост запустит ещё заезд…", rowCenter);
            GUI.Label(new Rect(box.x, box.y + bh - 40, bw, 30), "Esc — меню", rowCenter);
        }

        static string FormatTime(float t) => t < 0f ? "--:--" : $"{(int)(t / 60):00}:{t % 60:00.0}";
    }
}
