using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.EnhancedTouch;
using UnityEngine.InputSystem.LowLevel;

namespace JunkRace
{
    /// <summary>
    /// Сенсорное управление для телефона (горизонтальный экран, Telegram Mini App).
    ///
    /// Кнопки на экране «нажимают» виртуальный геймпад: у GameControls уже есть привязки под геймпад,
    /// поэтому игроку, машине, сети и обучению всё равно, откуда пришло нажатие.
    ///   Пешком:  джойстик (левая половина), «Действие» (взять / порыться / прикрутить — только когда есть что),
    ///            «Сесть», «Перевернуть», прыжок, «Готов» и меню сверху.
    ///   За рулём: < > слева, газ и тормоз справа, над газом — ускоритель (если стоит) и ручник.
    ///   Камера: свайп по свободной части экрана.
    /// </summary>
    public sealed class TouchControls : MonoBehaviour
    {
        // ======================= Включение =======================

        /// <summary>Сенсорный режим: телефон/планшет (в браузере — по экрану), Telegram или включено вручную (F9 в редакторе).</summary>
        public static bool TouchMode { get; private set; }
        /// <summary>Внутри Telegram Mini App.</summary>
        public static bool InTelegram { get; private set; }
        /// <summary>Свайп камеры за этот кадр, в пикселях экрана.</summary>
        public static Vector2 LookDelta { get; private set; }
        /// <summary>Режим захвата мыши для игры: на сенсоре курсор не захватываем (на телефоне pointer lock нет).</summary>
        public static CursorLockMode GameLock => TouchMode ? CursorLockMode.None : CursorLockMode.Locked;

#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] static extern int JunkIsTouchDevice();
        [DllImport("__Internal")] static extern int JunkTelegramInit();
#endif

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Boot()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            try { InTelegram = JunkTelegramInit() == 1; } catch { InTelegram = false; }
            try { TouchMode = InTelegram || JunkIsTouchDevice() == 1; } catch { TouchMode = false; }
#else
            TouchMode = Application.isMobilePlatform;
#endif
            if (TouchMode) MobileQuality();
            var go = new GameObject("Сенсорное управление");
            DontDestroyOnLoad(go);
            go.AddComponent<TouchControls>();
        }

        /// <summary>Телефон: без лишней нагрузки — 60 кадров максимум, без сглаживания, тени попроще.</summary>
        static void MobileQuality()
        {
            Application.targetFrameRate = 60;
            QualitySettings.antiAliasing = 0;
            QualitySettings.shadowDistance = Mathf.Min(QualitySettings.shadowDistance, 40f);
            QualitySettings.lodBias = Mathf.Min(QualitySettings.lodBias, 0.7f);
            QualitySetup.ApplyMobile();
        }

        // ======================= Виртуальный геймпад =======================

        Gamepad pad;

        void EnsurePad()
        {
            if (pad != null && pad.added) return;
            pad = InputSystem.AddDevice<Gamepad>("JunkTouchPad");
        }

        void OnDestroy()
        {
            if (pad != null && pad.added) InputSystem.RemoveDevice(pad);
            paint?.Destroy();
        }

        static GamepadState With(GamepadState s, GamepadButton b, bool on) => on ? s.WithButton(b, true) : s;

        // ======================= Касания =======================

        enum Ctl
        {
            None, Stick, Look,
            Action, Enter, Rotate, Jump, Ready, Menu, View,
            Left, Right, Gas, Brake, Boost, Handbrake, Exit, Flip, Again
        }

        struct Btn
        {
            public Ctl Id;
            public Vector2 Center;   // в пикселях, от левого верхнего угла (как у UiPainter)
            public float Radius;
            public string Label;
            public bool Pill;        // вытянутая плашка (меню, «Готов»), а не круг
            public Vector2 Size;
        }

        readonly List<Btn> buttons = new List<Btn>();
        readonly Dictionary<int, Ctl> owner = new Dictionary<int, Ctl>();
        readonly HashSet<Ctl> held = new HashSet<Ctl>();
        readonly HashSet<int> alive = new HashSet<int>();
        readonly List<int> gone = new List<int>();
        int stickTouch = -1;
        Vector2 stickOrigin, stickNow;
        bool stickSprint;
        float k;   // масштаб под экран
        int lastHeight;

        enum Mode { Off, Foot, Drive, Passenger, Results }
        Mode mode;

        void OnEnable() => EnhancedTouchSupport.Enable();
        void OnDisable() => EnhancedTouchSupport.Disable();

        void Update()
        {
#if UNITY_EDITOR
            // Проверка без телефона: F9 — сенсорный режим, касания — мышью.
            if (Keyboard.current != null && Keyboard.current.f9Key.wasPressedThisFrame)
            {
                TouchMode = !TouchMode;
                if (TouchMode) UnityEngine.InputSystem.EnhancedTouch.TouchSimulation.Enable();
                else UnityEngine.InputSystem.EnhancedTouch.TouchSimulation.Disable();
            }
#endif
            // Первое касание включает сенсорный режим: телефон без явных признаков, а в редакторе — Unity Remote
            // (касания с телефона приходят в редактор как Touchscreen).
            if (!TouchMode && Touchscreen.current != null && Touchscreen.current.primaryTouch.press.wasPressedThisFrame)
            {
                TouchMode = true;
#if !UNITY_EDITOR
                MobileQuality();
#endif
            }
            LookDelta = Vector2.zero;
            if (!TouchMode) { mode = Mode.Off; return; }

            if (Cursor.lockState != CursorLockMode.None) { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }

            // Повернули экран / изменилось окно — пересчитать масштаб 3D под новую высоту.
            if (Screen.height != lastHeight) { lastHeight = Screen.height; if (!Application.isEditor) QualitySetup.Apply(); }

            k = Mathf.Clamp(Mathf.Min(Screen.height / 720f, Screen.width / 1280f), 0.55f, 3f);
            mode = PickMode();
            Layout();
            ReadTouches();
            WritePad();
        }

        Mode PickMode()
        {
            var me = World.Local as PlayerController;
            var rt = RaceTimer.Instance;
            if (me == null || rt == null || RaceTimer.IsLoading || InGameMenu.Blocked) return Mode.Off;
            if (rt.Phase == MatchPhase.Results) return Mode.Results;
            if (me.IsDriving) return Mode.Drive;
            if (me.InVehicle) return Mode.Passenger;
            return Mode.Foot;
        }

        // ======================= Раскладка =======================

        void Add(Ctl id, float x, float y, float r, string label)
        {
            buttons.Add(new Btn { Id = id, Center = new Vector2(x, y), Radius = r * k, Label = label });
        }

        void AddPill(Ctl id, float x, float y, float w, float h, string label)
        {
            buttons.Add(new Btn { Id = id, Center = new Vector2(x, y), Size = new Vector2(w, h) * k, Radius = Mathf.Max(w, h) * 0.5f * k, Label = label, Pill = true });
        }

        void Layout()
        {
            buttons.Clear();
            if (mode == Mode.Off) return;
            float W = Screen.width, H = Screen.height;
            var me = World.Local as PlayerController;
            var rt = RaceTimer.Instance;
            float m = 24f * k;   // отступ от края

            // Сверху справа: меню, слева от него — «Готов» на сборке.
            AddPill(Ctl.Menu, W - m - 50f * k, m + 26f * k, 100f, 52f, "Меню");
            if (mode == Mode.Results)
            {
                if (GameSession.CanRestart && !GameSession.IsTutorial)
                    AddPill(Ctl.Again, W * 0.5f, H - m - 40f * k, 240f, 72f, "Ещё раз");
                return;
            }
            if (rt.Phase == MatchPhase.Build && Tutorial.AllowReady)
            {
                bool ready = rt.IsBuildReady(World.LocalId);
                // Под «Меню», а не слева от него: сверху справа в Telegram висят его кнопки (⌄ ⋯).
                AddPill(Ctl.Ready, W - m - 80f * k, m + 26f * k + 66f * k, 160f, 52f, ready ? "Не готов" : "Готов");
            }

            if (mode == Mode.Foot)
            {
                var carry = me.GetComponent<PlayerCarry>();
                string action = ActionLabel(carry);
                float ax = W - m - 80f * k, ay = H - m - 80f * k;
                if (action != null) Add(Ctl.Action, ax, ay, 78f, action);
                Add(Ctl.Jump, ax - 170f * k, H - m - 52f * k, 50f, "Прыжок");
                if (!RaceTimer.InteractionLocked)
                {
                    if (Tutorial.AllowEnter && me.CanEnterNearby) Add(Ctl.Enter, ax, ay - 190f * k, 56f, "Сесть");
                    if (CanRotate(carry)) Add(Ctl.Rotate, ax - 150f * k, ay - 140f * k, 50f, "Перевер-\nнуть");
                }
                Add(Ctl.View, m + 40f * k, m + 26f * k + 70f * k, 36f, "Вид");
                return;
            }

            // За рулём и пассажиром: выйти, сброс, вид — маленькие по центру сверху (слева — таймер и места).
            float sy = m + 40f * k, cx = W * 0.5f;
            Add(Ctl.Exit, cx - 100f * k, sy, 40f, "Выйти");
            Add(Ctl.Flip, cx, sy, 40f, "Сброс");
            Add(Ctl.View, cx + 100f * k, sy, 36f, "Вид");
            if (mode == Mode.Passenger) return;

            // Руль: < > слева внизу.
            float by = H - m - 80f * k;
            Add(Ctl.Left, m + 80f * k, by, 74f, "<");
            Add(Ctl.Right, m + 80f * k + 180f * k, by, 74f, ">");

            // Газ справа, тормоз левее; над газом — ускоритель и ручник.
            // Газ поднят над спидометром (он в правом нижнем углу).
            float gx = W - m - 86f * k, gy = by - 120f * k;
            Add(Ctl.Gas, gx, gy, 84f, "Газ");
            Add(Ctl.Brake, gx - 190f * k, by + 14f * k, 64f, "Тормоз");
            Add(Ctl.Handbrake, gx - 170f * k, gy - 130f * k, 52f, "Ручник");
            if (HasBoost(me.Driving)) Add(Ctl.Boost, gx, gy - 170f * k, 60f, "Ускор.");
        }

        static string ActionLabel(PlayerCarry carry)
        {
            if (carry == null || RaceTimer.InteractionLocked) return null;
            if (carry.IsSearching) return "Отмена";
            if (carry.Held != null) return carry.SnapTarget != null ? "Прикрутить" : "Бросить";
            if (carry.LookTarget != null) return carry.LookTarget.Vehicle != null ? "Снять" : "Взять";
            if (carry.LookPile != null) return "Порыться";
            return null;
        }

        static bool CanRotate(PlayerCarry carry)
        {
            if (carry == null) return false;
            var target = carry.Held != null ? carry.Held : carry.LookTarget;
            return target != null && target.TryGetComponent<HullFlip>(out _);
        }

        static bool HasBoost(Vehicle v)
        {
            if (v == null) return false;
            var parts = v.Parts;
            for (int i = 0; i < parts.Count; i++)
            {
                var p = parts[i];
                if (p is ThrusterPart t && t.UsesSpecialButton) return true;
                if (p is UmbrellaPart) return true;
            }
            return false;
        }

        // ======================= Касания → кнопки =======================

        void ReadTouches()
        {
            held.Clear();
            alive.Clear();
            stickSprint = false;
            float W = Screen.width, H = Screen.height;

            foreach (var t in UnityEngine.InputSystem.EnhancedTouch.Touch.activeTouches)
            {
                int id = t.touchId;
                var ph = t.phase;
                if (ph == UnityEngine.InputSystem.TouchPhase.Ended || ph == UnityEngine.InputSystem.TouchPhase.Canceled) continue;
                alive.Add(id);
                Vector2 p = t.screenPosition;
                Vector2 gui = new Vector2(p.x, H - p.y);

                if (!owner.TryGetValue(id, out var ctl))
                {
                    ctl = HitTest(gui);
                    if (ctl == Ctl.None)
                    {
                        // Пешком: левая нижняя часть экрана — джойстик, где коснулся, там и центр.
                        if (mode == Mode.Foot && stickTouch < 0 && gui.x < W * 0.42f && gui.y > H * 0.3f)
                        {
                            ctl = Ctl.Stick;
                            stickTouch = id;
                            stickOrigin = gui;
                        }
                        else ctl = mode == Mode.Off ? Ctl.None : Ctl.Look;
                    }
                    owner[id] = ctl;
                }

                switch (ctl)
                {
                    case Ctl.Stick: stickNow = gui; break;
                    case Ctl.Look: LookDelta += t.delta; break;
                    case Ctl.None: break;
                    default:
                        // Палец съехал с кнопки газа/руля на соседнюю — для педалей и руля переключаемся.
                        if (IsSlideable(ctl))
                        {
                            var under = HitTest(gui);
                            if (under != Ctl.None && IsSlideable(under)) { ctl = under; owner[id] = under; }
                        }
                        held.Add(ctl);
                        break;
                }
            }

            gone.Clear();
            foreach (var kv in owner) if (!alive.Contains(kv.Key)) gone.Add(kv.Key);
            foreach (int id in gone)
            {
                owner.Remove(id);
                if (id == stickTouch) stickTouch = -1;
            }
            if (mode != Mode.Foot) stickTouch = -1;
            if (mode == Mode.Off) owner.Clear();
        }

        static bool IsSlideable(Ctl c) => c == Ctl.Left || c == Ctl.Right || c == Ctl.Gas || c == Ctl.Brake;

        Ctl HitTest(Vector2 gui)
        {
            Ctl best = Ctl.None;
            float bestD = float.MaxValue;
            foreach (var b in buttons)
            {
                float d;
                if (b.Pill)
                {
                    var r = new Rect(b.Center - b.Size * 0.5f, b.Size);
                    r.xMin -= 8f * k; r.xMax += 8f * k; r.yMin -= 8f * k; r.yMax += 8f * k;
                    if (!r.Contains(gui)) continue;
                    d = 0f;
                }
                else
                {
                    d = Vector2.Distance(gui, b.Center);
                    if (d > b.Radius * 1.2f) continue;   // чуть шире картинки: по маленьким кнопкам пальцем
                }
                if (d < bestD) { bestD = d; best = b.Id; }
            }
            return best;
        }

        Vector2 StickVector()
        {
            if (stickTouch < 0) return Vector2.zero;
            float r = 70f * k;
            Vector2 v = stickNow - stickOrigin;
            if (v.magnitude > r * 1.35f) stickSprint = true;   // палец далеко за кругом — бег
            v /= r;
            if (v.magnitude > 1f) v.Normalize();
            return new Vector2(v.x, -v.y);   // экран: y вниз, джойстик: вверх — вперёд
        }

        void WritePad()
        {
            EnsurePad();
            var s = new GamepadState();
            if (mode == Mode.Foot)
            {
                s.leftStick = StickVector();
                s = With(s, GamepadButton.LeftStick, stickSprint);
                s = With(s, GamepadButton.West, held.Contains(Ctl.Action));
                s = With(s, GamepadButton.North, held.Contains(Ctl.Enter));
                s = With(s, GamepadButton.DpadDown, held.Contains(Ctl.Rotate));
                s = With(s, GamepadButton.South, held.Contains(Ctl.Jump));
            }
            else if (mode == Mode.Drive || mode == Mode.Passenger)
            {
                float steer = (held.Contains(Ctl.Right) ? 1f : 0f) - (held.Contains(Ctl.Left) ? 1f : 0f);
                s.leftStick = new Vector2(steer, 0f);
                s.rightTrigger = held.Contains(Ctl.Gas) ? 1f : 0f;
                s.leftTrigger = held.Contains(Ctl.Brake) ? 1f : 0f;
                s = With(s, GamepadButton.South, held.Contains(Ctl.Boost));
                s = With(s, GamepadButton.East, held.Contains(Ctl.Handbrake));
                s = With(s, GamepadButton.North, held.Contains(Ctl.Exit));
                s = With(s, GamepadButton.Select, held.Contains(Ctl.Flip));
            }
            else if (mode == Mode.Results)
            {
                s = With(s, GamepadButton.East, held.Contains(Ctl.Again));
            }
            s = With(s, GamepadButton.DpadLeft, held.Contains(Ctl.Ready));
            s = With(s, GamepadButton.Start, held.Contains(Ctl.Menu));
            s = With(s, GamepadButton.RightStick, held.Contains(Ctl.View));

            InputSystem.QueueStateEvent(pad, s);
        }

        // ======================= Рисование =======================

        UiPainter paint;
        static Texture2D disc, ring;
        static readonly Color Fill = new Color(0.08f, 0.06f, 0.05f, 0.42f);
        static readonly Color FillHeld = new Color(1f, 0.62f, 0.15f, 0.75f);
        static readonly Color Edge = new Color(1f, 0.93f, 0.82f, 0.55f);

        void LateUpdate()
        {
            if (!TouchMode && paint == null) return;
            paint ??= new UiPainter("Сенсор", 60);
            paint.Begin();
            if (TouchMode && mode != Mode.Off) Draw();
            paint.End();
        }

        void Draw()
        {
            EnsureTextures();
            if (mode == Mode.Foot)
            {
                // Джойстик: при касании — там, где палец; без касания — бледный на обычном месте.
                float r = 70f * k;
                Vector2 c = stickTouch >= 0 ? stickOrigin : new Vector2(24f * k + 110f * k, Screen.height - 24f * k - 110f * k);
                float a = stickTouch >= 0 ? 1f : 0.55f;
                paint.Tex(Circle(c, r), disc, new Color(0.08f, 0.06f, 0.05f, 0.3f * a));
                paint.Tex(Circle(c, r), ring, new Color(1f, 0.93f, 0.82f, 0.5f * a));
                Vector2 v = StickVector();
                Vector2 knob = c + new Vector2(v.x, -v.y) * r;
                paint.Tex(Circle(knob, r * 0.45f), disc, stickSprint ? FillHeld : new Color(1f, 0.93f, 0.82f, 0.55f * a));
            }

            foreach (var b in buttons)
            {
                bool on = held.Contains(b.Id);
                var st = new UiPainter.Style(Mathf.RoundToInt(FontFor(b) * k), on ? Ui.Ink : Ui.Cream, TextAnchor.MiddleCenter, true, false);
                if (b.Pill)
                {
                    var r = new Rect(b.Center - b.Size * 0.5f, b.Size);
                    bool accent = b.Id == Ctl.Ready || b.Id == Ctl.Again;
                    paint.Box(r, on ? FillHeld : accent ? new Color(1f, 0.62f, 0.15f, 0.55f) : Fill);
                    paint.Label(r, b.Label, st);
                }
                else
                {
                    var r = Circle(b.Center, b.Radius);
                    paint.Tex(r, disc, on ? FillHeld : b.Id == Ctl.Action ? new Color(1f, 0.62f, 0.15f, 0.5f) : Fill);
                    paint.Tex(r, ring, Edge);
                    paint.Label(r, b.Label, st);
                }
            }
        }

        static float FontFor(Btn b)
        {
            if (b.Id == Ctl.Left || b.Id == Ctl.Right) return 44f;
            if (b.Id == Ctl.Gas) return 26f;
            return b.Pill ? 22f : 18f;
        }

        static Rect Circle(Vector2 c, float r) => new Rect(c.x - r, c.y - r, r * 2f, r * 2f);

        static void EnsureTextures()
        {
            if (disc != null) return;
            disc = MakeCircle(false);
            ring = MakeCircle(true);
        }

        static Texture2D MakeCircle(bool outline)
        {
            const int n = 128;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            var px = new Color32[n * n];
            float c = (n - 1) * 0.5f, R = n * 0.5f - 1f;
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
                    float a = Mathf.Clamp01(R - d + 0.5f);                    // гладкий край
                    if (outline) a *= Mathf.Clamp01(d - (R - 4f) + 0.5f);     // кольцо толщиной ~4 px
                    px[y * n + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            return tex;
        }
    }
}
