using System.Collections.Generic;
using UnityEngine;

namespace JunkRace
{
    public enum PartCategory { Hull, Drive, Control, Special }

    /// <summary>
    /// Любая деталь со свалки. В свободном состоянии у неё свой Rigidbody.
    /// Когда прикручена к машине — свой Rigidbody удаляется, и её коллайдеры
    /// становятся частью одного составного тела машины (дёшево и для физики, и для сети).
    ///
    /// «Место решает»: как работает деталь, зависит от слота, куда её прикрутили (Slot).
    /// В слоте колеса что угодно катится как колесо (параметры AsWheel*), в слоте руля
    /// даёт AsSteering, в слоте мотора — AsEnginePower. В кузове работают только
    /// самостоятельные детали (Tick: вентилятор, зонт). Масса и сопротивление воздуха — везде.
    /// </summary>
    public class Part : MonoBehaviour
    {
        [SerializeField] PartCategory category = PartCategory.Special;
        [SerializeField] string displayName = "Хлам";
        [Tooltip("Масса детали, кг. Складывается в массу машины.")]
        [SerializeField] float mass = 10f;
        [Tooltip("Насколько сильный удар (импульс, Н·с) нужен, чтобы деталь отвалилась.")]
        [SerializeField] float breakImpulse = 400f;
        [Tooltip("Плавучесть в воде, Н на метр погружения. 0 = тонет.")]
        [SerializeField] float buoyancy = 0f;
        [Tooltip("Сопротивление воздуха. Чем больше, тем ниже максималка.")]
        [SerializeField] float airDrag = 0.05f;
        [Tooltip("Смешное описание для карточки детали.")]
        [SerializeField, TextArea] string description = "";

        public PartCategory Category => category;
        public string DisplayName => displayName;
        public float Mass => mass;
        public float BreakImpulse => breakImpulse;
        public float Buoyancy => buoyancy;
        public float AirDrag => airDrag;
        public string Description => description;

        static readonly string[] CategoryNames = { "Корпус", "Движение", "Управление", "Спец" };
        public string CategoryName => CategoryNames[(int)category];

        /// <summary>Строки характеристик для карточки. Наследники добавляют свои.</summary>
        public virtual void DescribeStats(List<string> lines)
        {
            lines.Add($"Вес: {mass:0} кг");
            if (buoyancy > 0f) lines.Add("Плавает");
            if (breakImpulse < 350f) lines.Add("Легко отваливается");
        }

        /// <summary>Машина, к которой прикручена деталь (у корпуса-корня — его собственная).</summary>
        public Vehicle Vehicle { get; internal set; }
        /// <summary>Точка, на которой деталь висит. null — деталь свободна (или это корень машины).</summary>
        public AttachPoint MountedOn { get; internal set; }
        public bool IsCarried { get; private set; }
        /// <summary>Номер детали в сети (порядок в иерархии сцены). -1 — не назначен.</summary>
        public int NetIndex { get; internal set; } = -1;

        /// <summary>
        /// Для коопа «каждый на своей детали»: если задан, деталь слушает его, а не водителя машины.
        /// </summary>
        public IPartInput InputOverride { get; set; }

        protected IPartInput Input =>
            InputOverride ?? (Vehicle != null ? Vehicle.Input : NullPartInput.Instance);

        public AttachPoint[] Points { get; private set; }
        Collider[] ownColliders;

        /// <summary>
        /// Можно ли взять в руки. Прикрученную деталь тоже можно: её снимут с машины (переставить),
        /// но не пока машину кто-то несёт. Корпус уносится целиком, со всем прикрученным,
        /// если за рулём никого (нашёл крутой корпус в куче — тащи на старт).
        /// </summary>
        public bool CanBeCarried =>
            !IsCarried && !IsHidden && !IsOccupied &&
            (MountedOn != null
                ? Vehicle != null && Vehicle.RootPart != this && !Vehicle.RootPart.IsCarried
                : Vehicle == null || (Vehicle.RootPart == this && !Vehicle.HasAnyone));

        /// <summary>На детали кто-то сидит (кресло) — её нельзя унести из-под человека.</summary>
        public virtual bool IsOccupied => false;

        /// <summary>Спрятана в куче мусора: невидима, без коллайдеров, стоит под землёй. Находится поиском.</summary>
        public bool IsHidden { get; private set; }
        /// <summary>Была спрятана в куче с начала заезда (для синхронизации с новыми игроками).</summary>
        public bool WasStashed { get; private set; }

        /// <summary>Спрятать в кучу: убрать вид, коллайдеры и физику, увезти под землю.</summary>
        public void Hide(Vector3 parkPosition)
        {
            IsHidden = true;
            WasStashed = true;
            foreach (var r in GetComponentsInChildren<Renderer>(true)) r.enabled = false;
            SetCollidersEnabled(false);
            if (TryGetComponent(out Rigidbody rb))
            {
                rb.interpolation = RigidbodyInterpolation.None;
                rb.isKinematic = true;
                rb.detectCollisions = false;
                rb.position = parkPosition;
            }
            transform.position = parkPosition;
        }

        /// <summary>Достать из кучи. На клиенте тело остаётся кинематическим — позицию пришлёт хост.</summary>
        public void Reveal(Vector3 position, Quaternion rotation, Vector3 velocity)
        {
            if (!IsHidden) return;
            IsHidden = false;
            transform.SetPositionAndRotation(position, rotation);
            foreach (var r in GetComponentsInChildren<Renderer>(true)) r.enabled = true;
            SetCollidersEnabled(true);
            if (TryGetComponent(out Rigidbody rb))
            {
                rb.position = position;
                rb.rotation = rotation;
                rb.detectCollisions = true;
                rb.isKinematic = !Authority.IsAuthority;
                if (!rb.isKinematic)
                {
                    rb.interpolation = RigidbodyInterpolation.Interpolate;
                    rb.linearVelocity = velocity;
                    rb.angularVelocity = Random.insideUnitSphere * 3f;
                }
            }
        }

        /// <summary>Точки плавучести (нижние углы детали, в её локальных координатах). null — не плавает.</summary>
        public Vector3[] FloatPoints { get; private set; }

        // ================= Слоты: «место решает» =================

        /// <summary>В каком слоте деталь сейчас. Свободная деталь и корень машины — «Кузов».</summary>
        public SlotType Slot => MountedOn != null ? MountedOn.Slot : SlotType.Body;

        /// <summary>Родной слот: где деталь работает как задумано.</summary>
        public virtual SlotType NativeSlot => SlotType.Body;

        /// <summary>Работает ли деталь сама по себе в кузове (вентилятор толкает, зонт тормозит).</summary>
        public virtual bool WorksInBody => false;

        /// <summary>Насколько деталь годится в руль, 0..1. Настоящий руль — 1.</summary>
        public virtual float AsSteering => 0f;

        /// <summary>Мощность, Вт, если деталь стоит в слоте мотора.</summary>
        public virtual float AsEnginePower => 0f;

        // Параметры «как колесо». У хлама плохие: мало сцепления и тяги, скребёт по земле.
        float junkRadius = 0.3f;
        public virtual float AsWheelRadius => junkRadius;
        public virtual float AsWheelSuspension => 0.25f;
        public virtual float AsWheelSpring => 4000f;
        public virtual float AsWheelDamper => 350f;
        public virtual float AsWheelTraction => 150f;
        public virtual float AsWheelGrip => 200f;
        public virtual float AsWheelRollingResistance => 25f;
        public virtual float AsWheelMaxSteerAngle => 30f;
        /// <summary>Доля тяги, которую деталь в слоте колеса даёт в воде, гребя как лопасть.</summary>
        public virtual float AsWheelPaddle => 0.5f;

        protected float wheelSpin, wheelSteerAngle;

        protected virtual void Awake()
        {
            // Кешируем ДО любых прикреплений, чтобы не схватить коллайдеры чужих деталей.
            // Только свои: в сцене к корпусу могут быть уже прикручены детали (колёсики тележки).
            var cols = new List<Collider>();
            foreach (var c in GetComponentsInChildren<Collider>(true))
                if (c.GetComponentInParent<Part>() == this) cols.Add(c);
            ownColliders = cols.ToArray();
            var pts = new List<AttachPoint>();
            foreach (var ap in GetComponentsInChildren<AttachPoint>(true))
                if (ap.GetComponentInParent<Part>() == this) pts.Add(ap);
            Points = pts.ToArray();
            if (TryGetComponent(out Rigidbody rb)) rb.mass = mass;

            // Радиус «колеса» для хлама: от точки крепления до низа детали.
            bool any = false;
            Bounds b = default;
            foreach (var c in ownColliders)
            {
                if (c == null || !c.enabled) continue;
                if (!any) { b = c.bounds; any = true; }
                else b.Encapsulate(c.bounds);
            }
            if (any) junkRadius = Mathf.Clamp(transform.position.y - b.min.y, 0.15f, 0.7f);

            // Плавучесть считаем по четырём нижним углам: так всплывает днище, а не центр,
            // и у машины появляется выравнивающий момент — не кувыркается на волне.
            if (buoyancy > 0f)
            {
                if (any)
                {
                    Vector3 lo = transform.InverseTransformPoint(b.min), hi = transform.InverseTransformPoint(b.max);
                    Vector3 mn = Vector3.Min(lo, hi), mx = Vector3.Max(lo, hi);
                    FloatPoints = new[]
                    {
                        new Vector3(mn.x, mn.y, mn.z), new Vector3(mx.x, mn.y, mn.z),
                        new Vector3(mn.x, mn.y, mx.z), new Vector3(mx.x, mn.y, mx.z),
                    };
                }
                else FloatPoints = new[] { Vector3.zero };
            }
        }

        /// <summary>Поведение в кузове (и у корня машины). Вызывается машиной в FixedUpdate. Только на авторитете.</summary>
        public virtual void Tick(float dt) { }

        /// <summary>Поведение в слоте руля. Качество руля машина уже учла в SteerQuality.</summary>
        public virtual void TickAsSteering(float dt) { }

        /// <summary>
        /// Поведение в слоте колеса: луч-подвеска (пружина + демпфер), боковое сцепление,
        /// тяга от мотора ограничена сцеплением. Работает для любой детали — с её параметрами AsWheel*.
        /// </summary>
        /// <summary>Коэффициент трения шины: сила шины не больше μ × прижатие к земле.</summary>
        const float TireFriction = 1.5f;

        public void TickAsWheel(float dt)
        {
            var vehicle = Vehicle;
            var body = vehicle.Body;
            Transform car = vehicle.transform;
            Vector3 up = car.up;

            bool steers = MountedOn != null && MountedOn.Steers;
            wheelSteerAngle = steers ? Input.Steer * AsWheelMaxSteerAngle * vehicle.SteerQuality : 0f;

            float radius = AsWheelRadius, suspension = AsWheelSuspension;
            if (!GroundCast(transform.position, -up, radius + suspension, out var hit))
            {
                Paddle(vehicle, body, car, up, radius);
                return;
            }
            vehicle.ReportGrounded();

            Vector3 vel = body.GetPointVelocity(hit.point);
            Quaternion steerRot = Quaternion.AngleAxis(wheelSteerAngle, up);
            Vector3 fwd = Vector3.ProjectOnPlane(steerRot * car.forward, hit.normal).normalized;
            Vector3 right = Vector3.ProjectOnPlane(steerRot * car.right, hit.normal).normalized;

            // Силы колеса не сильнее, чем позволяет масса, которая на него приходится. Иначе лёгкая машина
            // (тележка, паллет) за один шаг физики перелетает через равновесие — трясётся и крутится.
            // У тяжёлых машин ограничение не срабатывает.
            float share = body.mass / Mathf.Max(1, vehicle.SupportingWheels);
            float maxDamping = share / dt * 0.5f;
            float spring = Mathf.Min(AsWheelSpring, share / (dt * dt) * 0.25f);
            float damper = Mathf.Min(AsWheelDamper, maxDamping);

            float compression = radius + suspension - hit.distance;
            float springForce = Mathf.Max(0f, compression * spring - Vector3.Dot(vel, up) * damper);

            float fwdSpeed = Vector3.Dot(vel, fwd);
            float traction = AsWheelTraction;
            float drive = Mathf.Clamp(vehicle.WheelDriveForce, -traction, traction);

            // Пружина давит по нормали к земле. Если давить вдоль оси машины, то при крене
            // (например, колёс всего три) у силы есть горизонтальная часть — и машина сама уезжает.
            // Прикладываем над местом крепления, а не в точке касания: луч идёт вдоль наклонённой оси,
            // и при задранном носе точка касания убегает вперёд — получался рычаг, ставивший машину на дыбы.
            Vector3 mount = transform.position;
            body.AddForceAtPosition(hit.normal * springForce, mount);

            // Тормоз: без водителя машина стоит на ручнике; с водителем без газа на малой скорости
            // тоже держится (не ползёт). На скорости без газа катится по инерции.
            bool noThrottle = Mathf.Abs(vehicle.Input.Throttle) < 0.05f;
            bool brake = !vehicle.HasDriver || (noThrottle && Mathf.Abs(fwdSpeed) < 1.5f);
            float kSide = Mathf.Min(AsWheelGrip, maxDamping);
            float kRoll = Mathf.Min(AsWheelRollingResistance + (brake ? AsWheelGrip : 0f), maxDamping);

            Vector3 tire = -right * Vector3.Dot(vel, right) * kSide
                         - fwd * fwdSpeed * kRoll
                         + fwd * drive;

            // Шина держит не больше, чем её прижимает к земле (трение ≈ μ·N). Оторвавшееся или почти
            // не прижатое колесо не тянет и не держит — иначе лёгкая тележка получала 3g тяги,
            // а колёса, оставшиеся на земле при задранном носе, докручивали её в сальто.
            tire = Vector3.ClampMagnitude(tire, springForce * TireFriction);

            // Силы шины прикладываем на высоте центра масс ПО ВЕРТИКАЛИ МИРА: тогда горизонтальная сила
            // не крутит машину ни вперёд-назад, ни набок, даже если машина уже наклонена.
            // (По оси машины было нельзя: при задранном носе задние колёса оказывались ниже центра масс
            // и задирали нос ещё сильнее.)
            Vector3 tirePoint = new Vector3(mount.x, body.worldCenterOfMass.y, mount.z);
            body.AddForceAtPosition(tire, tirePoint);
            wheelSpin += fwdSpeed / Mathf.Max(radius, 0.05f) * Mathf.Rad2Deg * dt;
        }

        /// <summary>Колесо не достаёт до земли, но в воде — гребёт (слабее, чем на дороге).</summary>
        void Paddle(Vehicle vehicle, Rigidbody body, Transform car, Vector3 up, float radius)
        {
            Vector3 bottom = transform.position - up * radius;
            if (!WaterZone.TryGetDepth(bottom, out _)) return;
            vehicle.ReportPaddling();

            Vector3 fwd = Vector3.ProjectOnPlane(car.forward, Vector3.up);
            if (fwd.sqrMagnitude < 0.01f) return;   // машина стоит на носу — грести некуда
            fwd.Normalize();
            float traction = AsWheelTraction;
            float push = Mathf.Clamp(vehicle.WheelDriveForce, -traction, traction) * AsWheelPaddle;
            Vector3 at = transform.position + up * Vector3.Dot(body.worldCenterOfMass - transform.position, up);
            body.AddForceAtPosition(fwd * push, at);
            wheelSpin += Mathf.Sign(push) * Mathf.Min(Mathf.Abs(push), 200f) * 0.05f;   // для вида: колесо крутится
        }

        /// <summary>
        /// Вид. Хлам в поворотном слоте колеса поворачиваем целиком (крутить его как колесо не будем:
        /// он не катится, а скребёт). WheelPart крутит свою шину сам.
        /// </summary>
        protected virtual void LateUpdate()
        {
            if (MountedOn != null && MountedOn.Steers)
                transform.localRotation = Quaternion.Euler(0f, wheelSteerAngle, 0f);
        }

        public virtual void OnAttached() { wheelSteerAngle = 0f; }
        public virtual void OnDetached() { wheelSteerAngle = 0f; }

        public void SetCollidersEnabled(bool enabled)
        {
            foreach (var c in ownColliders) if (c) c.enabled = enabled;
        }

        static PhysicsMaterial slippery;
        PhysicsMaterial[] originalMaterials;

        /// <summary>
        /// Прикрученная деталь скользкая: если шина или хлам заденет землю, это упор, а не подсечка
        /// (трение у самой земли далеко под центром масс кувыркало машину). Езду даёт луч-подвеска,
        /// «скребёт» — через сопротивление качению. Снятая деталь возвращает свой материал, чтобы катиться.
        /// </summary>
        public void SetMountedPhysics(bool mounted)
        {
            if (originalMaterials == null)
            {
                originalMaterials = new PhysicsMaterial[ownColliders.Length];
                for (int i = 0; i < ownColliders.Length; i++)
                    originalMaterials[i] = ownColliders[i] ? ownColliders[i].sharedMaterial : null;
            }
            if (mounted && slippery == null)
            {
                slippery = new PhysicsMaterial("Прикручено (скользко)")
                {
                    dynamicFriction = 0f,
                    staticFriction = 0f,
                    frictionCombine = PhysicsMaterialCombine.Minimum,
                    bounciness = 0f,
                    bounceCombine = PhysicsMaterialCombine.Minimum,
                };
            }
            for (int i = 0; i < ownColliders.Length; i++)
                if (ownColliders[i]) ownColliders[i].sharedMaterial = mounted ? slippery : originalMaterials[i];
        }

        public void BeginCarry(Transform holdPoint)
        {
            IsCarried = true;
            if (TryGetComponent(out Rigidbody rb))
            {
                // Интерполяция у кинематического тела перезаписывает позицию каждый кадр
                // и мешает ему ехать за родителем (руками игрока) — деталь «улетает». Выключаем на время переноски.
                rb.interpolation = RigidbodyInterpolation.None;
                rb.isKinematic = true;
                rb.detectCollisions = false;
            }
            SetCollidersEnabled(false);
            transform.SetParent(holdPoint, false);
            transform.localPosition = Vector3.zero;
            transform.localRotation = Quaternion.identity;
        }

        public void EndCarry(Vector3 throwVelocity)
        {
            IsCarried = false;
            transform.SetParent(null, true);
            SetCollidersEnabled(true);
            if (TryGetComponent(out Rigidbody rb))
            {
                // На клиенте физики нет — тело остаётся кинематическим, позицию пришлёт хост.
                rb.detectCollisions = true;
                rb.isKinematic = !Authority.IsAuthority;
                if (!rb.isKinematic)
                {
                    rb.interpolation = RigidbodyInterpolation.Interpolate;
                    rb.linearVelocity = throwVelocity;
                }
            }
        }

        // ---- Вспомогательное для наследников ----

        static readonly RaycastHit[] hitBuffer = new RaycastHit[16];

        /// <summary>Луч вниз, игнорирующий саму машину и триггеры.</summary>
        protected bool GroundCast(Vector3 origin, Vector3 dir, float maxDist, out RaycastHit best)
        {
            best = default;
            int n = Physics.RaycastNonAlloc(origin, dir, hitBuffer, maxDist, ~0, QueryTriggerInteraction.Ignore);
            float bestDist = float.MaxValue;
            bool found = false;
            for (int i = 0; i < n; i++)
            {
                var h = hitBuffer[i];
                if (Vehicle != null && h.rigidbody == Vehicle.Body) continue;
                if (h.collider.GetComponentInParent<PlayerController>()) continue;
                if (h.distance < bestDist) { bestDist = h.distance; best = h; found = true; }
            }
            return found;
        }
    }
}
