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
            Controls = new GameControls();
            driveInput = new PlayerPartInput(Controls);
            if (cameraTransform == null && Camera.main) cameraTransform = Camera.main.transform;
            if (cameraTransform) followCamera = cameraTransform.GetComponent<FollowCamera>();
            World.Local = this;
            // Свой цвет — по номеру в сети, как тебя видят остальные (соло — жёлтый).
            var nm = Unity.Netcode.NetworkManager.Singleton;
            ulong myId = nm != null && nm.IsListening ? nm.LocalClientId : 0UL;
            CharacterVisual.Attach(transform, CharacterVisual.PlayerColor(myId));
        }

        void OnDestroy()
        {
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
            if (!InGameMenu.IsOpen && Controls.Ready.WasPressedThisFrame()
                && RaceTimer.Instance != null && RaceTimer.Instance.Phase == MatchPhase.Build)
                World.RequestBuildReady(!RaceTimer.Instance.IsBuildReady(World.LocalId));

            if (InGameMenu.IsOpen)
            {
                // Кооп: мир идёт — стоим на месте, но падаем, если были в прыжке.
                if (!InVehicle && GameSession.Mode != GameMode.Solo) Move(false);
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
            if (RaceTimer.InteractionLocked) return;   // ждём остальных: бегать можно, садиться и вертеть — нет
            if (Controls.Enter.WasPressedThisFrame()) TryEnterVehicle();
            if (Controls.Rotate.WasPressedThisFrame() && carry != null)
            {
                // R: перевернуть корпус (в руках или под прицелом) — стол ножками вверх или вниз.
                var target = carry.Held != null ? carry.Held : carry.LookTarget;
                if (target != null && target.TryGetComponent(out HullFlip flip)) World.RequestFlipHull(flip);
            }
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

            cc.Move((dir * moveSpeed + knockback + Vector3.up * verticalVelocity) * Time.deltaTime);
        }

        /// <summary>F: сесть на ближайшее свободное место — за руль или пассажиром в кресло.</summary>
        void TryEnterVehicle()
        {
            if (carry != null && carry.Held != null) return;

            // Сначала — кресло, на которое смотришь. Иначе водительское место (оно в центре корпуса)
            // почти всегда ближе, и F сажал за руль вместо кресла.
            if (carry != null && carry.LookTarget is SeatPart aimed)
            {
                // Кресло водителя — за руль этой машины.
                if (aimed.IsDriverChair && !aimed.Vehicle.HasDriver && !aimed.Vehicle.RootPart.IsCarried && aimed.Vehicle.MayUse(World.LocalId))
                {
                    World.RequestEnter(aimed.Vehicle);
                    return;
                }
                if (aimed.CanSit)
                {
                    World.RequestSit(aimed);
                    return;
                }
            }

            Vehicle bestVehicle = null;
            SeatPart bestSeat = null;
            float bestDist = enterRadius;
            // Своя машина без водителя — сначала за руль: иначе у машины на боку кресло оказывалось ближе
            // места водителя, F сажал пассажиром, и сброс (R) не работал.
            foreach (var v in Vehicle.All)
            {
                if (v.HasDriver || v.RootPart.IsCarried || v.RootPart.IsHidden || !v.MayUse(World.LocalId)) continue;
                float d = Mathf.Min(Vector3.Distance(transform.position, v.Seat.position),
                                    Vector3.Distance(transform.position, v.transform.position));
                if (d < bestDist) { bestDist = d; bestVehicle = v; }
            }
            if (bestVehicle != null) { World.RequestEnter(bestVehicle); return; }

            bestDist = enterRadius;
            foreach (var s in FindObjectsByType<SeatPart>(FindObjectsSortMode.None))
            {
                if (!s.CanSit) continue;
                float d = Vector3.Distance(transform.position, s.SitPoint.position);
                if (d < bestDist) { bestDist = d; bestSeat = s; }
            }
            if (bestSeat != null) World.RequestSit(bestSeat);
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

        void OnGUI()
        {
            if (InGameMenu.IsOpen) return;
            if (driving != null)
                GUI.Label(new Rect(20, Screen.height - 40, 1100, 30),
                    "W/S — газ   A/D — руль   Space — спец   R — сброс на дорогу   F — выйти   V — вид"
                    + (driving.HasDriverChair ? "" : "   · без кресла: стоишь, при ударе вылетишь легче"));
            else if (seat != null)
                GUI.Label(new Rect(20, Screen.height - 40, 900, 30), seat.Vehicle != null && !seat.Vehicle.HasDriver
                    ? "Ты пассажир, за рулём никого.   R — сброс на дорогу   F — выйти   V — вид"
                    : "Ты пассажир. Держись!   F — выйти   V — вид");
        }
    }
}
