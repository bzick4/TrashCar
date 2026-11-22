using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;

namespace JunkRace
{
    /// <summary>
    /// Обучение (первый запуск игры, одиночка). Свалка раскладывается под шаги: корпус перед игроком,
    /// два колеса рядом, третье и четвёртое — в кучах (в первой куче первый поиск пустой — учим рыться ещё раз),
    /// мотор, руль и кресло — неподалёку. Каждый шаг сначала объясняет окно (игра на паузе, управление
    /// выключено — «Понятно» / Enter), потом сверху висит задание, а делать можно только то, что оно просит:
    /// брать — только нужные детали, прикручивать — только на свой корпус в нужное место, рыться — только
    /// в отмеченной куче, садиться и жать G — когда дойдёт черёд. После сборки — короткая гонка по лесу,
    /// финиш — и в главное меню. Пропустить — Esc → «Пропустить обучение».
    /// </summary>
    public class Tutorial : MonoBehaviour
    {
        public static Tutorial Instance { get; private set; }

        /// <summary>Открыто окно с объяснением: игра на паузе, управление выключено.</summary>
        public static bool ModalOpen => Instance != null && Instance.modal != null;

        enum Step
        {
            Welcome, WalkToHull, GrabWheel, AttachWheel, SecondWheel,
            SearchFirst, SearchAgain, AttachThird, SearchSecondPile, AttachFourth,
            Engine, Steering, Seat, EnterCar, Ready, WaitRace, RaceIntro, Racing, Done
        }

        class Popup
        {
            public string Title, Text, Button;
            public System.Action Closed;
        }

        Step step = Step.Welcome;
        Popup modal;
        float modalShownAt;
        string task;

        // Что разложено под обучение.
        Vehicle car;
        Part hull, engine, steering, seat;
        readonly List<Part> wheels = new List<Part>();          // 0,1 — на виду, 2 — в куче A, 3 — в куче B
        JunkPile pileA, pileB;
        readonly HashSet<Part> unlocked = new HashSet<Part>();  // что уже можно брать
        bool laidOut;

        UiPainter paint;
        static Texture2D panelTex, arrowTex;
        static RectOffset panelBorder;
        static RectOffset PanelBorder => panelBorder ??= new RectOffset(70, 70, 70, 70);

        // ============================ Запуск ============================

        /// <summary>Свалка разложена (RouteGenerator.PrepareYard): в обучении — переложить под шаги.</summary>
        public static void Begin()
        {
            if (!GameSession.IsTutorial) return;
            if (Instance != null) Destroy(Instance.gameObject);
            var go = new GameObject("Обучение");
            go.AddComponent<Tutorial>().LayOut();
        }

        /// <summary>Пропустить (Esc-меню): больше само не начнётся.</summary>
        public static void Skip()
        {
            GameSession.TutorialDone = true;
            GameSession.ReturnToMenu();
        }

        void Awake() => Instance = this;

        void OnEnable() => World.SearchFinished += OnSearchFinished;
        void OnDisable() => World.SearchFinished -= OnSearchFinished;

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            paint?.Destroy();
            if (Time.timeScale == 0f && !InGameMenu.IsOpen) Time.timeScale = 1f;
        }

        // ============================ Раскладка ============================

        static List<AttachPoint> PointsOf(Part owner) => AttachPoint.All.Where(p => p != null && p.Owner == owner).ToList();

        static bool HasSlot(Part hullPart, SlotType slot) => PointsOf(hullPart).Any(p => p.Slot == slot);

        void LayOut()
        {
            var gate = RouteGenerator.Instance != null ? RouteGenerator.Instance.transform.position : Vector3.zero;
            var all = FindObjectsByType<Part>(FindObjectsSortMode.None).Where(p => p != null && p.gameObject.activeInHierarchy).ToList();

            // Корпус: 4 места под колёса, мотор, руль и водительское кресло — чтобы рассказать про всё.
            var hulls = all.Where(p => p.TryGetComponent(out Vehicle v) && v.PartCount <= 1 && !p.IsCarried).ToList();
            int Score(Part h)
            {
                var pts = PointsOf(h);
                int s = Mathf.Min(4, pts.Count(p => p.Slot == SlotType.Wheel)) * 10;
                if (pts.Any(p => p.Slot == SlotType.Engine)) s += 3;
                if (pts.Any(p => p.Slot == SlotType.Steering)) s += 3;
                if (pts.Any(p => p.IsDriver)) s += 3;
                return s;
            }
            hull = hulls.OrderByDescending(Score).ThenBy(h => h.IsHidden ? 1 : 0).FirstOrDefault();
            if (hull == null) { Debug.LogWarning("[JunkRace] Обучение: на свалке нет корпуса"); return; }
            car = hull.GetComponent<Vehicle>();
            var fwd = Vector3.forward;
            Place(hull, gate + new Vector3(3f, 1.2f, -20f), Quaternion.LookRotation(fwd));

            var loose = all.Where(p => p != hull && p.GetComponent<Vehicle>() == null && p.Vehicle == null && p.MountedOn == null).ToList();
            var points = PointsOf(hull);

            // Колёса: те, что встают на все колёсные места (одинаковые — красивее).
            var wheelPoints = points.Where(p => p.Slot == SlotType.Wheel).ToList();
            var wheelCandidates = loose.Where(p => p.NativeSlot == SlotType.Wheel && wheelPoints.All(wp => car.CanAttach(p, wp))).ToList();
            var byKind = wheelCandidates.GroupBy(p => p.DisplayName).OrderByDescending(g => g.Count()).SelectMany(g => g).ToList();
            wheels.AddRange(byKind.Take(4));
            if (wheels.Count < 4)   // одинаковых не хватило — любые, что встают хоть на одно место
                wheels.AddRange(loose.Where(p => p.NativeSlot == SlotType.Wheel && !wheels.Contains(p) && wheelPoints.Any(wp => car.CanAttach(p, wp)))
                                     .Take(4 - wheels.Count));
            if (wheels.Count < 4) Debug.LogWarning($"[JunkRace] Обучение: колёс нашлось только {wheels.Count}");

            engine = Pick(loose, SlotType.Engine);
            steering = Pick(loose, SlotType.Steering);
            seat = Pick(loose, SlotType.Seat);

            // Кучи: две ближайшие к корпусу.
            var piles = JunkPile.All.Where(p => p != null).OrderBy(p => (p.transform.position - hull.transform.position).sqrMagnitude).ToList();
            pileA = piles.Count > 0 ? piles[0] : null;
            pileB = piles.Count > 1 ? piles[1] : pileA;

            // На виду: два колеса у корпуса, мотор, руль и кресло — чуть дальше по коридору старта.
            var face = Quaternion.Euler(0f, 30f, 0f);
            if (wheels.Count > 0) Place(wheels[0], gate + new Vector3(-0.5f, 0.8f, -23.5f), face);
            if (wheels.Count > 1) Place(wheels[1], gate + new Vector3(-1.5f, 0.8f, -20.5f), face);
            if (engine != null) Place(engine, gate + new Vector3(-3f, 0.8f, -14f), face);
            if (steering != null) Place(steering, gate + new Vector3(0.5f, 0.8f, -11f), face);
            if (seat != null) Place(seat, gate + new Vector3(4.5f, 0.8f, -13f), face);

            // В кучах: третье колесо (первый поиск — пусто) и четвёртое.
            if (wheels.Count > 2 && pileA != null)
            {
                StashInto(pileA, wheels[2]);
                pileA.ForcedRolls.Clear();
                pileA.ForcedRolls.Enqueue(null);
                pileA.ForcedRolls.Enqueue(wheels[2]);
            }
            if (wheels.Count > 3 && pileB != null)
            {
                StashInto(pileB, wheels[3]);
                if (pileB != pileA) pileB.ForcedRolls.Clear();
                pileB.ForcedRolls.Enqueue(wheels[3]);
            }
            Physics.SyncTransforms();
            laidOut = true;
            Debug.Log($"[JunkRace] Обучение: корпус «{hull.DisplayName}», колёс {wheels.Count}, мотор {engine?.DisplayName ?? "—"}, " +
                      $"руль {steering?.DisplayName ?? "—"}, кресло {seat?.DisplayName ?? "—"}");
        }

        Part Pick(List<Part> loose, SlotType slot)
        {
            var point = PointsOf(hull).FirstOrDefault(p => p.Slot == slot && (slot != SlotType.Seat || p.IsDriver))
                        ?? PointsOf(hull).FirstOrDefault(p => p.Slot == slot);
            return loose.Where(p => p.NativeSlot == slot && !wheels.Contains(p) && (point == null || car.CanAttach(p, point)))
                        .OrderBy(p => p.IsHidden ? 1 : 0).FirstOrDefault();
        }

        /// <summary>Деталь — на место: из кучи достаём, лежащую переносим, скорость обнуляем.</summary>
        static void Place(Part part, Vector3 position, Quaternion rotation)
        {
            if (part.IsHidden)
            {
                foreach (var pile in JunkPile.All) if (pile != null && pile.Holds(part)) pile.Unstash(part);
                part.Reveal(position, rotation, Vector3.zero);
            }
            part.transform.SetPositionAndRotation(position, rotation);
            if (part.TryGetComponent(out Rigidbody rb))
            {
                rb.position = position;
                rb.rotation = rotation;
                if (!rb.isKinematic) { rb.linearVelocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
            }
        }

        static void StashInto(JunkPile target, Part part)
        {
            // Числим только за нужной кучей; лежащую на виду Stash прячет, спрятанную — перепарковывает под неё.
            foreach (var pile in JunkPile.All) if (pile != null && pile != target && pile.Holds(part)) pile.Unstash(part);
            target.Stash(part);
        }

        // ============================ Правила: что можно делать сейчас ============================

        static bool Active => Instance != null && Instance.laidOut && Instance.step < Step.Done;

        /// <summary>Можно ли взять эту деталь.</summary>
        public static bool AllowGrab(Part part)
        {
            if (!Active || part == null) return true;
            var t = Instance;
            if (part == t.hull) return t.step >= Step.WalkToHull;   // перевернуть / подвинуть свой корпус
            return t.unlocked.Contains(part) && part.Vehicle == null;
        }

        /// <summary>Можно ли рыться в этой куче.</summary>
        public static bool AllowSearch(JunkPile pile)
        {
            if (!Active) return true;
            var t = Instance;
            return t.step switch
            {
                Step.SearchFirst or Step.SearchAgain => pile == t.pileA,
                Step.SearchSecondPile => pile == t.pileB,
                _ => false,
            };
        }

        /// <summary>Можно ли прикрутить деталь в это место: только на свой корпус и в место по назначению.</summary>
        public static bool AllowAttach(Part part, AttachPoint point)
        {
            if (!Active || part == null || point == null) return true;
            var t = Instance;
            if (t.car == null || point.Owner == null || point.Owner.Vehicle != t.car) return false;
            var native = part.NativeSlot;
            var pts = PointsOf(t.hull);
            if (native == SlotType.Seat && pts.Any(p => p.IsDriver && !p.Occupied)) return point.IsDriver;
            if (pts.Any(p => p.Slot == native && !p.Occupied)) return point.Slot == native;
            return point.Slot != SlotType.Wheel;   // места по назначению нет — куда угодно, только не на колёса
        }

        public static bool AllowEnter => !Active || Instance.step >= Step.EnterCar;
        public static bool AllowReady => !Active || Instance.step >= Step.Ready;

        // ============================ Ход обучения ============================

        int WheelsOn => car == null ? 0 : car.Parts.Count(p => p.MountedOn != null && p.Slot == SlotType.Wheel);
        bool Mounted(Part p) => p != null && car != null && p.Vehicle == car;
        Part Held => World.Local?.Held;

        void Update()
        {
            if (!laidOut) return;
            var rt = RaceTimer.Instance;
            if (rt == null || RaceTimer.IsLoading) return;

            // Окно открыто: мир стоит (меню паузы само ставит и снимает паузу — держим её, пока окно не закрыто).
            if (modal != null)
            {
                if (!InGameMenu.IsOpen)
                {
                    Time.timeScale = 0f;
                    Cursor.lockState = CursorLockMode.None;
                    Cursor.visible = true;
                    // Только Enter (и A на геймпаде): E и F в тот же кадр ушли бы игроку — бросил бы деталь или вылез.
                    bool ok = Keyboard.current != null && (Keyboard.current.enterKey.wasPressedThisFrame || Keyboard.current.numpadEnterKey.wasPressedThisFrame)
                              || Gamepad.current != null && Gamepad.current.buttonSouth.wasPressedThisFrame;
                    if (ok && Time.unscaledTime - modalShownAt > 0.6f) CloseModal();
                }
                return;
            }

            switch (step)
            {
                case Step.Welcome:
                    if (rt.Phase == MatchPhase.Build && World.Local != null)
                        Show(Step.WalkToHull, "Добро пожаловать на свалку!",
                            "Здесь собирают тачки из хлама и гоняют на них наперегонки.\n\nСейчас по шагам соберём твою первую машину и прокатимся на ней. " +
                            "Ходить — W A S D, бег — Shift, прыжок — Space, осмотреться — мышь.",
                            "Подойди к корпусу — он отмечен стрелкой");
                    break;

                case Step.WalkToHull:
                    if (World.Local is MonoBehaviour me && hull != null && Flat(me.transform.position - hull.transform.position) < 4.5f)
                    {
                        Unlock(wheels.Take(2));
                        Show(Step.GrabWheel, "Корпус",
                            $"Это «{hull.DisplayName}» — корпус, основа машины. Всё остальное крепится к нему: колёса, мотор, руль, кресло.\n\n" +
                            "У каждого корпуса свои места под детали: сколько колёс, где мотор, где сидит водитель. " +
                            "Если корпус лёг вверх ногами — подойди и нажми R.",
                            "Возьми колесо: подойди и нажми E");
                    }
                    break;

                case Step.GrabWheel:
                    if (Held != null && wheels.Contains(Held))
                        Show(Step.AttachWheel, "Прикручиваем",
                            "Пока деталь в руках, на корпусе зелёными шариками светятся места, куда она встанет, " +
                            "а на ближайшем — полупрозрачный «призрак» детали.\n\nПодойди с колесом к месту и нажми E — прикрутить. " +
                            "Нажмёшь E вдали от места — деталь просто упадёт на землю.",
                            "Прикрути колесо к корпусу (E у зелёного места)");
                    break;

                case Step.AttachWheel:
                    if (WheelsOn >= 1) { step = Step.SecondWheel; task = "Возьми второе колесо и прикрути его"; }
                    break;

                case Step.SecondWheel:
                    if (WheelsOn >= 2)
                        Show(Step.SearchFirst, "Кучи мусора",
                            "Больше колёс на виду нет — остальное спрятано в кучах мусора.\n\n" +
                            "Подойди к куче и нажми E — порыться. Поиск занимает пару секунд: стой рядом и не отходи, иначе он прервётся. " +
                            "Найденное вылетит к твоим ногам.",
                            "Порыться в отмеченной куче: подойди и нажми E");
                    break;

                case Step.AttachThird:
                    if (WheelsOn >= 3)
                        Show(Step.SearchSecondPile, "Ещё одна куча",
                            "Три колеса — уже можно ехать, но на четырёх машина устойчивее и не валится на бок.\n\n" +
                            "Последнее колесо — в другой куче. Каждая куча — лотерея: в одной густо, в другой пусто, так что не бойся бегать между ними.",
                            "Найди четвёртое колесо в другой куче");
                    break;

                case Step.AttachFourth:
                    if (WheelsOn >= 4) ShowEngine();
                    break;

                case Step.Engine:
                    if (Mounted(engine) || engine == null) ShowSteering();
                    break;

                case Step.Steering:
                    if (Mounted(steering) || steering == null) ShowSeat();
                    break;

                case Step.Seat:
                    if (Mounted(seat) || seat == null) ShowEnter();
                    break;

                case Step.EnterCar:
                    if (World.Local?.Driving == car && car != null)
                        Show(Step.Ready, "Готов к старту",
                            "Машина собрана!\n\nКогда всё прикручено, нажми G — «Готов». В обычной игре на сборку есть таймер, " +
                            "а старт — когда готовы все игроки или время вышло. Прикручивать детали можно и после G — пока не стартовали.\n\n" +
                            "Tab — подробности о машине: скорость, мощность, чего не хватает.",
                            "Нажми G — «Готов»");
                    break;

                case Step.Ready:
                    if (rt.Phase != MatchPhase.Build) { step = Step.WaitRace; task = null; }
                    break;

                case Step.WaitRace:
                    if (rt.Phase == MatchPhase.Countdown)
                        Show(Step.Racing, "Гонка!",
                            "Короткий заезд по лесу — доедь до финиша.\n\n" +
                            "W / S — газ и тормоз (задний ход), A / D — руль. Перевернулся или застрял — R, вернёт на дорогу. " +
                            "По трассе есть кучи хлама, брёвна и горки — объезжай или прыгай.\n\nСрезать через лес нельзя: вернёт назад.",
                            "Доедь до финиша!");
                    break;

                case Step.Racing:
                    if (rt.Phase == MatchPhase.Results)
                    {
                        float time = rt.Elapsed;
                        Show(Step.Done, "Финиш!",
                            $"Время: {(int)(time / 60)}:{time % 60:00.0}. Обучение пройдено!\n\n" +
                            "В настоящей игре детали каждый раз разложены по-новому, корпусов несколько, а на сборку — таймер. " +
                            "В «Кооп» можно собирать и гонять с друзьями.\n\nУдачи на свалке!",
                            null, "В меню", () => { GameSession.TutorialDone = true; GameSession.ReturnToMenu(); });
                    }
                    break;
            }
        }

        void ShowEngine()
        {
            if (engine == null) { ShowSteering(); return; }
            Unlock(engine);
            Show(Step.Engine, "Мотор",
                $"«{engine.DisplayName}» — мотор. Он крутит колёса: без мотора машина не поедет.\n\n" +
                "Моторы бывают разные: мощнее — быстрее разгон, но тяжелее. Можно поставить несколько, если есть места.",
                "Возьми мотор и прикрути его на место мотора");
        }

        void ShowSteering()
        {
            if (steering == null) { ShowSeat(); return; }
            Unlock(steering);
            Show(Step.Steering, "Руль",
                $"«{steering.DisplayName}» — руль. Без него передние колёса не поворачивают, и машина едет только прямо.",
                "Возьми руль и прикрути его");
        }

        void ShowSeat()
        {
            if (seat == null) { ShowEnter(); return; }
            Unlock(seat);
            Show(Step.Seat, "Кресло водителя",
                $"«{seat.DisplayName}» — кресло. Ехать можно и стоя, но в кресле держишься крепче: при ударе не вылетишь из машины.\n\n" +
                "Ставь на место водителя.",
                "Возьми кресло и прикрути на место водителя");
        }

        void ShowEnter() =>
            Show(Step.EnterCar, "За руль",
                "Корпус, четыре колеса, мотор, руль и кресло — машина готова!\n\nПодойди к ней и нажми F — сесть за руль. F ещё раз — выйти.",
                "Сядь за руль: подойди к машине и нажми F");

        void OnSearchFinished(ulong id, JunkPile pile, Part found)
        {
            if (!laidOut || id != World.LocalId) return;
            if (step == Step.SearchFirst && pile == pileA)
            {
                if (found == null)
                    Show(Step.SearchAgain, "Пусто? Бывает",
                        "Не каждый поиск что-то приносит — в куче может быть спрятано ещё. Порыться можно сколько угодно раз, " +
                        "пока куча не опустеет: тогда так и напишет — «тут всё выгребли».",
                        "Порыться в той же куче ещё раз");
                else OnThirdFound(found);
            }
            else if (step == Step.SearchAgain && pile == pileA && found != null) OnThirdFound(found);
            else if (step == Step.SearchSecondPile && pile == pileB && found != null)
            {
                Unlock(found);
                step = Step.AttachFourth;
                task = "Нашёл! Прикрути четвёртое колесо";
            }
        }

        void OnThirdFound(Part found)
        {
            Unlock(found);
            Show(Step.AttachThird, "Нашёл!",
                $"«{found.DisplayName}» вылетело из кучи. Подними и прикрути, как первые два.",
                "Прикрути третье колесо");
        }

        void Unlock(Part p) { if (p != null) unlocked.Add(p); }
        void Unlock(IEnumerable<Part> parts) { foreach (var p in parts) Unlock(p); }

        void Show(Step next, string title, string text, string nextTask, string button = "Понятно", System.Action closed = null)
        {
            step = next;
            task = nextTask;
            modal = new Popup { Title = title, Text = text, Button = button, Closed = closed };
            modalShownAt = Time.unscaledTime;
        }

        void CloseModal()
        {
            var closed = modal.Closed;
            modal = null;
            Time.timeScale = 1f;
            if (!InGameMenu.IsOpen && closed == null)
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
            closed?.Invoke();
        }

        static float Flat(Vector3 d) { d.y = 0f; return d.magnitude; }

        // ============================ Экран ============================

        static readonly UiPainter.Style TitleStyle = new UiPainter.Style(40, Ui.Accent, TextAnchor.UpperCenter, true, true);
        static readonly UiPainter.Style TextStyle = new UiPainter.Style(24, Ui.Cream, TextAnchor.UpperLeft, false, true);
        static readonly UiPainter.Style TaskStyle = new UiPainter.Style(24, Ui.Cream, TextAnchor.MiddleCenter, true, false);
        static readonly UiPainter.Style KeyStyle = new UiPainter.Style(18, Ui.HintColor, TextAnchor.MiddleCenter, false, false);
        static readonly UiPainter.Style MarkStyle = new UiPainter.Style(44, Ui.Accent, TextAnchor.MiddleCenter, true, false);
        static readonly UiPainter.Style MarkLabel = new UiPainter.Style(20, Ui.Cream, TextAnchor.MiddleCenter, true, false);

        void LateUpdate()
        {
            paint ??= new UiPainter("Обучение", 50);
            paint.Begin();
            if (laidOut && !InGameMenu.IsOpen && !RaceTimer.IsLoading) Draw();
            paint.End();
        }

        void Draw()
        {
            if (panelTex == null) panelTex = Resources.Load<Texture2D>("UI/hud/card_panel");
            float w = Screen.width, h = Screen.height, k = h / 1080f;

            if (modal != null) { DrawModal(w, h, k); return; }
            if (string.IsNullOrEmpty(task)) return;

            // Задание: плашка сверху, под табличкой таймера.
            var st = TaskStyle.With(Mathf.RoundToInt(24f * k));
            float tw = paint.Measure(task, st).x + 120f * k, th = 76f * k;
            // На сборке сверху висит табличка «УЧЁБА» — встаём сразу под неё, в гонке — к самому верху.
            bool build = RaceTimer.Instance != null && RaceTimer.Instance.Phase == MatchPhase.Build;
            var plate = new Rect((w - tw) / 2f, (build ? 125f : 12f) * k, tw, th);
            if (panelTex != null) paint.Sliced(plate, panelTex, PanelBorder, 0.35f * k);
            else paint.Box(plate, new Color(0f, 0f, 0f, 0.6f));
            paint.Label(plate, task, st);
            paint.Label(new Rect(0, plate.yMax + 2f * k, w, 26f * k), "Esc — меню (там можно пропустить обучение)", KeyStyle.With(Mathf.RoundToInt(16f * k)));

            var target = Target(out string label);
            if (target.HasValue) DrawMarker(target.Value, label, w, h, k);
        }

        void DrawModal(float w, float h, float k)
        {
            paint.Box(new Rect(0, 0, w, h), new Color(0.05f, 0.04f, 0.03f, 0.55f));
            float pw = Mathf.Min(900f * k, w - 40f), pad = 70f * k;
            var text = TextStyle.With(Mathf.RoundToInt(24f * k));
            float textH = paint.MeasureHeight(modal.Text, text, pw - pad * 2f);
            float ph = pad + 56f * k + 16f * k + textH + 30f * k + 70f * k + pad * 0.8f;
            var box = new Rect((w - pw) / 2f, (h - ph) / 2f, pw, ph);
            if (panelTex != null) paint.Sliced(box, panelTex, PanelBorder, 0.6f * k);
            else paint.Box(box, new Color(0.2f, 0.18f, 0.16f, 0.97f));

            float y = box.y + pad;
            paint.Label(new Rect(box.x + pad, y, pw - pad * 2f, 56f * k), modal.Title, TitleStyle.With(Mathf.RoundToInt(40f * k)));
            y += 56f * k + 16f * k;
            paint.Label(new Rect(box.x + pad, y, pw - pad * 2f, textH + 4f), modal.Text, text);
            y += textH + 30f * k;
            float bw = 340f * k, bh = 64f * k;
            if (paint.Button(new Rect(box.x + (pw - bw) / 2f, y, bw, bh), modal.Button + "  (Enter)", Mathf.RoundToInt(26f * k))
                && Time.unscaledTime - modalShownAt > 0.3f)
                CloseModal();
        }

        /// <summary>Куда показать стрелку на текущем шаге.</summary>
        Vector3? Target(out string label)
        {
            label = null;
            Part held = Held;
            Part part = null;
            switch (step)
            {
                case Step.WalkToHull: label = "Корпус"; return Top(hull);
                case Step.GrabWheel:
                case Step.SecondWheel when held == null:
                    part = wheels.Take(2).FirstOrDefault(p => p != null && p.Vehicle == null);
                    label = "Колесо";
                    break;
                case Step.SearchFirst:
                case Step.SearchAgain:
                    label = "Куча"; return pileA != null ? pileA.LabelPosition + Vector3.up * 0.6f : null;
                case Step.SearchSecondPile:
                    label = "Куча"; return pileB != null ? pileB.LabelPosition + Vector3.up * 0.6f : null;
                case Step.AttachThird when held == null:
                    part = wheels.Count > 2 ? wheels[2] : null; label = "Колесо"; break;
                case Step.AttachFourth when held == null:
                    part = wheels.Count > 3 ? wheels[3] : null; label = "Колесо"; break;
                case Step.Engine when held == null: part = engine; label = "Мотор"; break;
                case Step.Steering when held == null: part = steering; label = "Руль"; break;
                case Step.Seat when held == null: part = seat; label = "Кресло"; break;
                case Step.EnterCar: label = "Машина"; return Top(hull);
            }
            if (part != null && part.Vehicle == null && !part.IsHidden) return Top(part);
            // В руках деталь — стрелка на корпус: туда нести.
            if (held != null && step >= Step.GrabWheel && step <= Step.Seat) { label = "Корпус"; return Top(hull); }
            return null;
        }

        static Vector3? Top(Part p)
        {
            if (p == null) return null;
            bool any = false;
            Bounds b = default;
            foreach (var r in p.GetComponentsInChildren<Renderer>())
            {
                if (!r.enabled) continue;
                if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
            }
            return any ? new Vector3(b.center.x, b.max.y + 0.5f, b.center.z) : p.transform.position + Vector3.up;
        }

        /// <summary>Прыгающая стрелка над целью; цель за кадром — стрелка у края экрана.</summary>
        void DrawMarker(Vector3 world, string label, float w, float h, float k)
        {
            var cam = Camera.main;
            if (cam == null) return;
            Vector3 sp = cam.WorldToScreenPoint(world);
            bool behind = sp.z < 0f;
            if (behind) sp = -sp;
            float x = sp.x, y = h - sp.y;
            float margin = 70f * k;
            bool off = behind || x < margin || x > w - margin || y < margin || y > h - margin;
            float bob = Mathf.Sin(Time.unscaledTime * 5f) * 8f * k;
            if (off)
            {
                // Направление от центра экрана — стрелка на краю.
                var c = new Vector2(w / 2f, h / 2f);
                var d = new Vector2(x, y) - c;
                if (behind) d = new Vector2(d.x, Mathf.Abs(d.y) + 1f);
                if (d.sqrMagnitude < 1f) d = Vector2.down;
                float s = Mathf.Min((w / 2f - margin) / Mathf.Max(1e-3f, Mathf.Abs(d.x)), (h / 2f - margin) / Mathf.Max(1e-3f, Mathf.Abs(d.y)));
                var e = c + d * s;
                string arrow = Mathf.Abs(d.x) > Mathf.Abs(d.y) ? (d.x > 0 ? "→" : "←") : (d.y > 0 ? "↓" : "↑");
                paint.Label(new Rect(e.x - 60f * k, e.y - 40f * k, 120f * k, 56f * k), arrow, MarkStyle.With(Mathf.RoundToInt(44f * k)));
                paint.Label(new Rect(e.x - 100f * k, e.y + 12f * k, 200f * k, 30f * k), label, MarkLabel.With(Mathf.RoundToInt(20f * k)));
                return;
            }
            float dist = World.Local is MonoBehaviour me ? Vector3.Distance(me.transform.position, world) : 0f;
            paint.Label(new Rect(x - 120f * k, y - 70f * k + bob, 240f * k, 30f * k), $"{label} · {dist:0} м", MarkLabel.With(Mathf.RoundToInt(20f * k)));
            arrowTex ??= Resources.Load<Texture2D>("UI/hud/marker_arrow");
            if (arrowTex != null)
            {
                float ah = 54f * k, aw = ah * arrowTex.width / arrowTex.height;
                paint.Tex(new Rect(x - aw / 2f, y - 40f * k + bob, aw, ah), arrowTex);
            }
            else paint.Label(new Rect(x - 60f * k, y - 44f * k + bob, 120f * k, 56f * k), "↓", MarkStyle.With(Mathf.RoundToInt(44f * k)));
        }
    }
}
