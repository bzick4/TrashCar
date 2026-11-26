using UnityEngine;

namespace JunkRace
{
    /// <summary>
    /// Локальный игрок: ходит, прыгает, садится в машину.
    /// Ходьба своя (каждый двигает себя сам), а всё, что меняет общий мир, идёт через World.Request*:
    /// офлайн и на хосте выполняется сразу, у клиента — после подтверждения хоста.
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public class PlayerController : MonoBehaviour, IAvatar
    {
        [SerializeField] float moveSpeed = 6f;
        [Tooltip("Бег (Shift): во столько раз быстрее шага.")]
        [SerializeField] float sprintMultiplier = 1.7f;
        [SerializeField] float jumpHeight = 1.2f;
        [SerializeField] float gravity = -20f;
        [SerializeField] float turnSpeed = 720f;
        [SerializeField] float enterRadius = 3f;
        [SerializeField] Transform cameraTransform;

        public GameControls Controls { get; private set; }
        public bool IsDriving => driving != null;
        /// <summary>Сидит пассажиром в кресле.</summary>
        public bool IsSeated => seat != null;
        /// <summary>В машине: за рулём или пассажиром (не ходит, камера на сиденье).</summary>
        public bool InVehicle => driving != null || seat != null;
        /// <summary>Машина, в которой сидит (за рулём или пассажиром).</summary>
        public Vehicle CurrentVehicle => driving != null ? driving : seat != null ? seat.Vehicle : null;

        // ---- IAvatar ----
        public ulong ClientId => World.LocalId;
        public string DisplayName => GameSession.PlayerName;
        public Transform HoldPoint => carry.HoldPoint;
        public Part Held => carry.Held;
        public Vehicle Driving => driving;
        public SeatPart Seat => seat;
        public IPartInput DriveInput => driveInput;

        /// <summary>Вид от первого лица (его включает/выключает FollowCamera по V).</summary>
        public bool FirstPersonView => followCamera != null && followCamera.FirstPerson;
        public Transform CameraTransform => cameraTransform;

        CharacterController cc;
        PlayerCarry carry;
        FollowCamera followCamera;
        PlayerPartInput driveInput;
        Vehicle driving;
        SeatPart seat;
        float verticalVelocity;
        Vector3 knockback;   // выбросило из сорванного кресла — летим по инерции

        void Awake()
        {
            cc = GetComponent<CharacterController>();
            carry = GetComponent<PlayerCarry>();
            InitializeControls();
            if (cameraTransform == null && Camera.main) cameraTransform = Camera.main.transform;
            if (cameraTransform) followCamera = cameraTransform.GetComponent<FollowCamera>();
            World.Local = this;
            // Свой цвет — по номеру в сети, как тебя видят остальные (соло — жёлтый).
            var nm = Unity.Netcode.NetworkManager.Singleton;
            ulong myId = nm != null && nm.IsListening ? nm.LocalClientId : 0UL;
            CharacterVisual.Attach(transform, CharacterVisual.PlayerColor(myId));
        }

        void OnEnable()
        {
            // При перекомпиляции в Play Mode Awake не повторяется, а карты ввода не сериализуются.
            if (Controls != null) return;
            InitializeControls();
            World.Local = this;
            if (driving != null) driving.SetDriver(driveInput);
        }

        void InitializeControls()
        {
            Controls = new GameControls();
            Controls.SetDriving(InVehicle);
            driveInput = new PlayerPartInput(Controls);
        }

        void OnDestroy()
        {
            hintPaint?.Destroy();
            if (ReferenceEquals(World.Local, this)) World.Local = null;
            Controls?.Dispose();
        }

        void Update()
        {
            if (RaceTimer.IsLoading)
            {
                if (!InVehicle) Move(false);   // экран загрузки: стоим (но не висим в воздухе)
                return;
            }
            // G — «Готов» на сборке (и пешком, и за рулём).
            if (!InGameMenu.Blocked && Controls.Ready.WasPressedThisFrame() && Tutorial.AllowReady
                && RaceTimer.Instance != null && RaceTimer.Instance.Phase == MatchPhase.Build)
                World.RequestBuildReady(!RaceTimer.Instance.IsBuildReady(World.LocalId));

            if (InGameMenu.Blocked)
            {
                // Кооп: мир идёт — стоим на месте, но падаем, если были в прыжке.
                if (!InVehicle && (GameSession.Mode != GameMode.Solo || !InGameMenu.IsOpen)) Move(false);
                return;
            }

            if (InVehicle)
            {
                // Сброс — водителю, а пассажиру — если за рулём никого (лёг на бок, вышел, сел обратно в кресло).
                if (Controls.Flip.WasPressedThisFrame() && (driving != null || (seat != null && seat.Vehicle != null && !seat.Vehicle.HasDriver)))
                    World.RequestFlip();
                if (Controls.Exit.WasPressedThisFrame()) World.RequestExit();
                return;
            }

            Move();
            CheckFootFall();
            if (RaceTimer.InteractionLocked) return;   // ждём остальных: бегать можно, садиться и вертеть — нет
            if (Controls.Enter.WasPressedThisFrame() && Tutorial.AllowEnter) TryEnterVehicle();
            if (Controls.Rotate.WasPressedThisFrame())
            {
                // R: перевернуть корпус (в руках или под прицелом) — стол ножками вверх или вниз.
                // Корпуса рядом нет — сброс пешком на дорогу (как R за рулём).
                var target = carry == null ? null : carry.Held != null ? carry.Held : carry.LookTarget;
                if (target != null && target.TryGetComponent(out HullFlip flip)) World.RequestFlipHull(flip);
                else if (carry == null || carry.Held == null) ResetOnFoot();
            }
        }

        // ---- Пешком упал с трассы: обратно на дорогу ----

        const float FootFallY = -25f;          // ниже — точно улетел с карты
        const float FootFallDelay = 1f;        // сколько лететь, прежде чем вернуть
        Vector3 lastSafeFoot;
        bool hasSafeFoot;
        float footFallTime, footCheckTimer, safeSaveTimer;
        bool footBelowRoad;

        /// <summary>
        /// Выбросило из машины и сорвался с горной дороги (или улетел за край свалки): через секунду
        /// падения ставим обратно на ближайшую дорогу. По ровной земле рядом с трассой гулять можно —
        /// возвращаем только тех, кто ниже дороги на RouteMap.FallDepth или ниже края карты.
        /// </summary>
        void CheckFootFall()
        {
            float dt = Time.deltaTime;
            Vector3 p = transform.position;
            var route = RouteMap.Current;

            footCheckTimer -= dt;
            if (footCheckTimer <= 0f)
            {
                footCheckTimer = 0.25f;   // поиск по маршруту не бесплатный
                footBelowRoad = route != null && route.Nearest(p, false, out var h) && h.Below > route.FallDepth;
            }
            bool falling = p.y < FootFallY || footBelowRoad;

            if (!falling && cc.isGrounded)
            {
                safeSaveTimer -= dt;
                if (safeSaveTimer <= 0f) { safeSaveTimer = 0.5f; lastSafeFoot = p; hasSafeFoot = true; }
            }

            footFallTime = falling ? footFallTime + dt : 0f;
            if (footFallTime > FootFallDelay) ResetOnFoot();
        }

        /// <summary>Поставить игрока на ближайшую дорогу (от последнего места, где он твёрдо стоял).</summary>
        void ResetOnFoot()
        {
            footFallTime = 0f;
            var route = RouteMap.Current;
            Vector3 from = hasSafeFoot ? lastSafeFoot : transform.position;
            Vector3 to;
            Vector3 fwd = transform.forward;
            if (route != null && route.ResetPose(from, 2f, 1.5f, out var pos, out var f)) { to = pos + Vector3.up * 1.2f; fwd = f; }
            else if (hasSafeFoot) to = lastSafeFoot + Vector3.up * 0.5f;
            else return;
            verticalVelocity = 0f;
            knockback = Vector3.zero;
            Teleport(to);
            fwd.y = 0f;
            if (fwd.sqrMagnitude > 0.01f) transform.rotation = Quaternion.LookRotation(fwd.normalized);
            footBelowRoad = false;
        }

        void Move(bool allowInput = true)
        {
            Vector2 input = allowInput ? Controls.Move.ReadValue<Vector2>() : Vector2.zero;
            Vector3 fwd = Vector3.forward, right = Vector3.right;
            if (cameraTransform)
            {
                fwd = Vector3.ProjectOnPlane(cameraTransform.forward, Vector3.up).normalized;
                right = Vector3.ProjectOnPlane(cameraTransform.right, Vector3.up).normalized;
            }
            Vector3 dir = Vector3.ClampMagnitude(fwd * input.y + right * input.x, 1f);

            // От первого лица игрока поворачивает камера (за мышью), здесь только вид сзади.
            if (!FirstPersonView && dir.sqrMagnitude > 0.01f)
            {
                Quaternion target = Quaternion.LookRotation(dir);
                transform.rotation = Quaternion.RotateTowards(transform.rotation, target, turnSpeed * Time.deltaTime);
            }

            if (cc.isGrounded)
            {
                if (verticalVelocity < 0f) verticalVelocity = -2f;
                if (allowInput && Controls.Jump.WasPressedThisFrame()) verticalVelocity = Mathf.Sqrt(jumpHeight * -2f * gravity);
            }
            verticalVelocity += gravity * Time.deltaTime;

            // Инерция после вылета из кресла: на земле быстро гасится, в полёте — медленно.
            knockback = Vector3.MoveTowards(knockback, Vector3.zero, (cc.isGrounded ? 12f : 1.5f) * Time.deltaTime);

            float speed = moveSpeed * (allowInput && Controls.Sprint.IsPressed() ? sprintMultiplier : 1f);
            cc.Move((dir * speed + knockback + Vector3.up * verticalVelocity) * Time.deltaTime);
        }

        /// <summary>F: сесть на ближайшее свободное место — за руль или пассажиром в кресло.</summary>
        void TryEnterVehicle()
        {
            if (!FindEnter(out var vehicle, out var chair)) return;
            if (vehicle != null) World.RequestEnter(vehicle);
            else World.RequestSit(chair);
        }

        float enterCheckTimer;
        bool canEnterCached;

        /// <summary>Рядом есть куда сесть (для сенсорной кнопки «Сесть»; пересчёт 5 раз в секунду).</summary>
        public bool CanEnterNearby
        {
            get
            {
                enterCheckTimer -= Time.unscaledDeltaTime;
                if (enterCheckTimer <= 0f) { enterCheckTimer = 0.2f; canEnterCached = !InVehicle && FindEnter(out _, out _); }
                return canEnterCached;
            }
        }

        /// <summary>Куда сядет F: за руль машины (vehicle) или в кресло (chair). false — некуда.</summary>
        bool FindEnter(out Vehicle vehicle, out SeatPart chair)
        {
            vehicle = null;
            chair = null;
            if (carry != null && carry.Held != null) return false;

            // Сначала — кресло, на которое смотришь. Иначе водительское место (оно в центре корпуса)
            // почти всегда ближе, и F сажал за руль вместо кресла.
            if (carry != null && carry.LookTarget is SeatPart aimed)
            {
                // Кресло водителя — за руль этой машины.
                if (aimed.IsDriverChair && !aimed.Vehicle.HasDriver && !aimed.Vehicle.RootPart.IsCarried && aimed.Vehicle.MayUse(World.LocalId))
                {
                    vehicle = aimed.Vehicle;
                    return true;
                }
                if (aimed.CanSit)
                {
                    chair = aimed;
                    return true;
                }
            }

            float bestDist = enterRadius;
            // Своя машина без водителя — сначала за руль: иначе у машины на боку кресло оказывалось ближе
            // места водителя, F сажал пассажиром, и сброс (R) не работал.
            foreach (var v in Vehicle.All)
            {
                if (v.HasDriver || v.RootPart.IsCarried || v.RootPart.IsHidden || !v.MayUse(World.LocalId)) continue;
                float d = Mathf.Min(Vector3.Distance(transform.position, v.Seat.position),
                                    Vector3.Distance(transform.position, v.transform.position));
                if (d < bestDist) { bestDist = d; vehicle = v; }
            }
            if (vehicle != null) return true;

            bestDist = enterRadius;
            foreach (var s in FindObjectsByType<SeatPart>(FindObjectsSortMode.None))
            {
                if (!s.CanSit) continue;
                float d = Vector3.Distance(transform.position, s.SitPoint.position);
                if (d < bestDist) { bestDist = d; chair = s; }
            }
            return chair != null;
        }

        public void Teleport(Vector3 position)
        {
            bool was = cc.enabled;
            cc.enabled = false;
            transform.position = position;
            cc.enabled = was;
        }

        // ---- Вызывается из World.Apply* ----

        public void SetHeld(Part part) => carry.Held = part;

        public void Fling(Vector3 velocity)
        {
            if (InVehicle) return;
            // Выбросило вперёд по ходу: отодвигаем от машины, чтобы не застрять в корпусе.
            Vector3 flat = new Vector3(velocity.x, 0f, velocity.z);
            if (flat.sqrMagnitude > 0.01f)
            {
                cc.enabled = false;
                transform.position += flat.normalized * 1.5f + Vector3.up * 0.8f;
                cc.enabled = true;
            }
            verticalVelocity = Mathf.Max(verticalVelocity, velocity.y);
            knockback = Vector3.ClampMagnitude(flat, 20f);
        }

        public void SetSeat(SeatPart s, Vector3 fling)
        {
            if (s != null)
            {
                seat = s;
                cc.enabled = false;   // сидящий без коллайдера: не толкает ни машину, ни колёса, ни соседей
                knockback = Vector3.zero;
                transform.SetParent(s.SitPoint, false);
                transform.localPosition = Vector3.zero;
                transform.localRotation = Quaternion.identity;
                Controls.SetDriving(true);
            }
            else if (seat != null)
            {
                var old = seat;
                seat = null;
                Vector3 pos = transform.position;
                transform.SetParent(null, true);
                if (fling.sqrMagnitude < 0.01f && old.Vehicle != null)
                {
                    // Сам вышел — встаём рядом, снаружи машины.
                    Vector3 side = pos - old.Vehicle.transform.position;
                    side.y = 0f;
                    side = side.sqrMagnitude > 0.01f ? side.normalized : old.Vehicle.transform.right;
                    pos += side * 0.8f + Vector3.up * 0.5f;
                }
                transform.SetPositionAndRotation(pos, Quaternion.Euler(0f, transform.eulerAngles.y, 0f));
                verticalVelocity = fling.y;
                knockback = new Vector3(fling.x, 0f, fling.z);
                cc.enabled = true;
                Controls.SetDriving(false);
            }
        }

        public void SetDriving(Vehicle vehicle)
        {
            if (vehicle != null)
            {
                driving = vehicle;
                cc.enabled = false;   // выключенный CharacterController не становится частью коллайдера машины
                transform.SetParent(vehicle.Seat, false);
                transform.localPosition = Vector3.zero;
                transform.localRotation = Quaternion.identity;
                Controls.SetDriving(true);
            }
            else if (driving != null)
            {
                var v = driving;
                driving = null;
                transform.SetParent(null, true);
                // Вбок по горизонтали: у машины на боку её «право» смотрит в небо или в землю.
                Vector3 side = Vector3.ProjectOnPlane(v.transform.right, Vector3.up);
                if (side.sqrMagnitude < 0.04f) side = Vector3.ProjectOnPlane(v.transform.up, Vector3.up);
                side = side.sqrMagnitude > 0.0001f ? side.normalized : Vector3.right;
                transform.position = v.transform.position + side * 2f + Vector3.up * 1.2f;
                Vector3 look = Vector3.ProjectOnPlane(v.transform.forward, Vector3.up);
                transform.rotation = look.sqrMagnitude > 0.0001f ? Quaternion.LookRotation(look) : Quaternion.identity;
                verticalVelocity = 0f;
                cc.enabled = true;
                Controls.SetDriving(false);
            }
        }

        UiPainter hintPaint;   // подсказка внизу — через UGUI (IMGUI в Linear выбеливал)
        static readonly UiPainter.Style HintStyle = new UiPainter.Style(16, new Color(0.92f, 0.92f, 0.92f), TextAnchor.UpperLeft, wrap: false);

        void LateUpdate()
        {
            hintPaint ??= new UiPainter("Управление", 15);
            hintPaint.Begin();
            if (!InGameMenu.IsOpen && !TouchControls.TouchMode) DrawHint();   // на сенсоре подписи на самих кнопках
            hintPaint.End();
        }

        void DrawHint()
        {
            if (driving != null)
                hintPaint.Label(new Rect(20, Screen.height - 40, 1100, 30),
                    "W/S или ↑/↓ — газ   A/D или ←/→ — руль   Space — спец   R — сброс на дорогу   F — выйти   V — вид"
                    + (driving.HasDriverChair ? "" : "   · без кресла: стоишь, при ударе вылетишь легче"), HintStyle);
            else if (seat != null)
                hintPaint.Label(new Rect(20, Screen.height - 40, 900, 30), seat.Vehicle != null && !seat.Vehicle.HasDriver
                    ? "Ты пассажир, за рулём никого.   R — сброс на дорогу   F — выйти   V — вид"
                    : "Ты пассажир. Держись!   F — выйти   V — вид", HintStyle);
        }
    }
}
