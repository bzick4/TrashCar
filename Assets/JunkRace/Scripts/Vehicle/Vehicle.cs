using System;
using System.Collections.Generic;
using UnityEngine;

namespace JunkRace
{
    /// <summary>
    /// Машина = корпус-корень с одним Rigidbody. Прикрученные детали становятся
    /// его детьми, и их коллайдеры работают как один составной коллайдер.
    /// Все решения (прикрепить, оторвать, физика деталей) — только на авторитете.
    /// </summary>
    [RequireComponent(typeof(Rigidbody), typeof(Part))]
    public class Vehicle : MonoBehaviour
    {
        [SerializeField] Transform seat;
        [Tooltip("Сдвиг центра масс вниз, чтобы не кувыркалась от каждого чиха.")]
        [SerializeField] Vector3 centerOfMassOffset = new Vector3(0f, -0.3f, 0f);
        [SerializeField] float waterDrag = 1.5f;
        [Tooltip("Гашение вращения в воде, чтобы машина не болталась.")]
        [SerializeField] float waterAngularDrag = 2f;
        [Tooltip("Поворот в воде при наличии руля (рад/с²).")]
        [SerializeField] float waterTurn = 1.5f;
        [Tooltip("Насколько можно крутить машину в прыжке (рад/с² ), если есть руль.")]
        [SerializeField] float airControl = 3f;
        [Tooltip("Сколько секунд без земли, прежде чем включится управление в воздухе. Иначе лёгкая машина,\n" +
                 "подскакивая на кочках, ловит толчки «газ — нос вниз» и клюёт на ходу.")]
        [SerializeField] float airControlDelay = 0.3f;

        [Header("Сброс (R)")]
        [Tooltip("Куда смотрит дорога, если на сцене нет TrackBounds. Сброс ставит машину носом сюда.")]
        [SerializeField] Vector3 roadForward = Vector3.forward;
        [Tooltip("Ниже этой высоты машина считается упавшей за край и сбрасывается сама.")]
        [SerializeField] float fallResetY = -15f;
        [Tooltip("На сколько метров назад по дороге отодвинуть машину при сбросе.")]
        [SerializeField] float resetBack = 2f;
        [Tooltip("На какой высоте над землёй поставить машину при сбросе (дальше упадёт на колёса).")]
        [SerializeField] float resetHeight = 1.2f;
        [Tooltip("Сколько секунд можно пробыть вне трассы (сгенерированный маршрут), прежде чем вернёт.")]
        [SerializeField] float offRouteDelay = 1.2f;

        [Header("Вылет водителя")]
        [Tooltip("С какого удара (резкая смена скорости, м/с) водитель может вылететь.")]
        [SerializeField] float ejectFromDv = 5f;
        [Tooltip("Удар, после которого водитель в кресле вылетает наверняка.")]
        [SerializeField] float ejectFullDv = 17f;
        [Tooltip("Без кресла водитель стоит — вылетает во столько раз чаще.")]
        [SerializeField] float standingEjectFactor = 1.5f;
        float ejectCooldown;
        bool pendingEject;
        Vector3 ejectFling;
        float airTime;
        float offRouteTime, routeCheckTimer;
        bool onRoute = true;
        Vector3 lastSafePosition;
        bool hasSafePosition;
        const float ResetMargin = 3f;   // отступ от ограждений при возврате на трассу

        public int TeamId;   // для командного режима, пока не используется

        /// <summary>
        /// Хозяин машины: каждый игрок строит свою. Первый, кто прикрутил деталь или сел за руль, — хозяин.
        /// Чужую машину нельзя водить, разбирать и докручивать (пассажиром — можно).
        /// </summary>
        public const ulong NoOwner = ulong.MaxValue;
        public ulong OwnerId { get; private set; } = NoOwner;
        public bool IsOwned => OwnerId != NoOwner;
        public void ApplyOwner(ulong id) => OwnerId = id;
        /// <summary>Этот игрок может водить и строить машину (ничья или его).</summary>
        public bool MayUse(ulong id) => !IsOwned || OwnerId == id;

        /// <summary>Пересчитать массу и центр масс (корпус перевернули).</summary>
        public void MarkMassDirty() => massDirty = true;

        public Rigidbody Body { get; private set; }
        public Part RootPart { get; private set; }
        public IReadOnlyList<Part> Parts => parts;
        public int PartCount => parts.Count;

        public IPartInput Driver { get; private set; }
        public bool HasDriver => Driver != null;
        /// <summary>В машине кто-то есть: водитель или пассажир в кресле.</summary>
        public bool HasAnyone
        {
            get
            {
                if (HasDriver) return true;
                foreach (var p in parts) if (p.IsOccupied) return true;
                return false;
            }
        }
        public IPartInput Input => Driver ?? NullPartInput.Instance;
        public Transform Seat => seat != null ? seat : transform;

        public event Action<Part> PartAttached;
        public event Action<Part> PartDetached;
        public static event Action<Vehicle> AnyDriverEntered;

        readonly List<Part> parts = new List<Part>();
        readonly List<(Part part, Vector3 impulse)> pendingBreaks = new List<(Part, Vector3)>();
        bool massDirty = true;
        int groundedWheels, groundedThisStep;
        int paddlingWheels, paddlingThisStep;

        /// <summary>Сколько тяги мотор даёт каждому колесу на земле в этом шаге физики (Н, со знаком газа).</summary>
        public float WheelDriveForce { get; private set; }
        /// <summary>Сумма мощности деталей в слотах «Мотор», Вт.</summary>
        public float TotalPower { get; private set; }
        public float DragCoefficient { get; private set; }
        /// <summary>Качество руля 0..1: сумма AsSteering деталей в слотах «Руль», не больше 1.</summary>
        public float SteerQuality { get; private set; }
        /// <summary>Защита от бортов (ArmorPart в слоте «Борт»), 0..ArmorPart.MaxProtection.</summary>
        public float Protection { get; private set; }
        bool armorLeft, armorRight;

        /// <summary>Висит ли боковина на этой стороне (знак side: минус — слева). Колёса отодвигаются от неё.</summary>
        public bool HasSideArmor(float side) => side < 0f ? armorLeft : armorRight;
        public bool IsGrounded => groundedWheels > 0;
        /// <summary>Сколько колёс держало машину в прошлом шаге (на земле или в воде), минимум 1.</summary>
        public int SupportingWheels => Mathf.Max(1, groundedWheels + paddlingWheels);
        /// <summary>Машина в воде (по прошлому шагу физики).</summary>
        public bool InWater { get; private set; }
        /// <summary>Скорость вперёд, м/с (на клиенте — по движению трансформа).</summary>
        public float ForwardSpeed => Vector3.Dot(Authority.IsAuthority ? Body.linearVelocity : clientVelocity, transform.forward);

        Vector3 lastPosition, clientVelocity;

        /// <summary>Колесо сообщает, что стоит на земле.</summary>
        public void ReportGrounded() => groundedThisStep++;
        /// <summary>Колесо сообщает, что гребёт в воде.</summary>
        public void ReportPaddling() => paddlingThisStep++;

        /// <summary>Все машины на сцене — вместо FindObjectsByType (он перебирает всю сцену).</summary>
        public static readonly List<Vehicle> All = new List<Vehicle>();
        void OnEnable() => All.Add(this);
        void OnDisable() => All.Remove(this);

        void Awake()
        {
            Body = GetComponent<Rigidbody>();
            Body.interpolation = RigidbodyInterpolation.Interpolate;
            RootPart = GetComponent<Part>();
            RootPart.Vehicle = this;
            parts.Add(RootPart);
            if (!TryGetComponent<EngineSound>(out _)) gameObject.AddComponent<EngineSound>();   // звук мотора (молчит, пока мотора нет)
        }

        /// <summary>
        /// Детали, прикрученные прямо в сцене (тележка уже с колёсиками), регистрируем как прикрученные.
        /// Делается одинаково у хоста и клиентов: сцена у всех одна.
        /// </summary>
        void Start()
        {
            foreach (var point in RootPart.Points)
            {
                if (point == null || point.Occupied) continue;
                for (int i = 0; i < point.transform.childCount; i++)
                {
                    if (point.transform.GetChild(i).TryGetComponent(out Part p) && p != RootPart && p.Vehicle == null)
                    {
                        ApplyAttach(p, point);
                        break;
                    }
                }
            }
        }

        /// <summary>На месте водителя прикручено кресло (иначе водитель стоит).</summary>
        public bool HasDriverChair
        {
            get
            {
                foreach (var p in parts) if (p is SeatPart s && s.IsDriverChair) return true;
                return false;
            }
        }

        /// <summary>Шанс вылететь при ударе такой силы (Δv, м/с): в кресле — по силе удара, стоя — в 1.5 раза выше.</summary>
        public float EjectChance(float dv)
        {
            float chance = Mathf.Clamp01((dv - ejectFromDv) / Mathf.Max(0.1f, ejectFullDv - ejectFromDv));
            if (!HasDriverChair) chance *= standingEjectFactor;
            return Mathf.Clamp01(chance);
        }

        public void SetDriver(IPartInput driver)
        {
            Driver = driver;
            ejectCooldown = Time.time + 1f;   // только сел — не выкидываем от толчка при посадке
            pendingEject = false;
            if (driver != null) AnyDriverEntered?.Invoke(this);
        }

        public bool Has(PartCategory category)
        {
            foreach (var p in parts) if (p.Category == category) return true;
            return false;
        }

        // ---------------- Прикрепление / отрыв ----------------

        public bool CanAttach(Part part, AttachPoint point)
        {
            return part != null && point != null
                && !point.Occupied
                && point.Owner != null && point.Owner.Vehicle == this
                && point.Owner != part
                && part.Vehicle == null
                && part.GetComponent<Vehicle>() == null   // корпус-корень не прикручивается к другой машине
                && point.Accepts(part);
        }

        /// <summary>Прикрутить (только авторитет). Клиенты получают событие и вызывают ApplyAttach.</summary>
        public bool Attach(Part part, AttachPoint point)
        {
            if (!Authority.IsAuthority) return false;
            if (!CanAttach(part, point)) return false;
            ApplyAttach(part, point);
            return true;
        }

        /// <summary>Само прикручивание без проверок. На клиенте вызывается по событию с хоста.</summary>
        public void ApplyAttach(Part part, AttachPoint point)
        {
            if (part == null || point == null) return;
            if (point.Occupant != null && point.Occupant != part) return;

            if (part.TryGetComponent(out Rigidbody rb)) DestroyImmediate(rb);

            part.transform.SetParent(point.transform, false);
            part.transform.localPosition = Vector3.zero;
            part.transform.localRotation = part.MountRotation(point);
            part.SetCollidersEnabled(true);
            if (point.Slot != SlotType.Wheel) SettleOnHull(part, point);
            part.SetMountedPhysics(true);

            point.Occupant = part;
            part.MountedOn = point;
            part.Vehicle = this;
            parts.Add(part);
            massDirty = true;

            part.OnAttached();
            PartAttached?.Invoke(part);
        }

        static readonly RaycastHit[] settleHits = new RaycastHit[16];

        /// <summary>
        /// Деталь встаёт вплотную на корпус: точка крепления может висеть над полом (модель корпуса
        /// не совпала с коробками, перевёрнутый корпус) — опускаем деталь, пока низом не коснётся
        /// своего корпуса (не дальше 0.6 м). Одинаково у хоста и клиентов: геометрия у всех одна.
        /// </summary>
        void SettleOnHull(Part part, AttachPoint point)
        {
            var owner = point.Owner;
            if (owner == null) return;
            if (SettleOnModel(part, point, owner)) return;
            Physics.SyncTransforms();

            bool any = false;
            Bounds b = default;
            foreach (var c in part.GetComponentsInChildren<Collider>())
            {
                if (!c.enabled || c.isTrigger || c.GetComponentInParent<Part>() != part) continue;
                if (!any) { b = c.bounds; any = true; } else b.Encapsulate(c.bounds);
            }
            if (!any) return;

            Vector3 down = -point.transform.up;
            // Луч из центра детали вниз; низ детали — по её рамке вдоль «вниз» точки.
            Vector3 from = b.center;
            float toBottom = Mathf.Abs(Vector3.Dot(b.extents, new Vector3(Mathf.Abs(down.x), Mathf.Abs(down.y), Mathf.Abs(down.z))));
            const float maxGap = 0.6f;
            int n = Physics.RaycastNonAlloc(from, down, settleHits, toBottom + maxGap, ~0, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                var h = settleHits[i];
                if (h.collider.GetComponentInParent<Part>() != owner) continue;   // только свой корпус
                if (h.distance < best) best = h.distance;
            }
            if (best == float.MaxValue) return;
            float gap = best - toBottom;
            if (gap > 0.01f && gap <= maxGap) part.transform.position += down * gap;
        }

        /// <summary>
        /// Прижать деталь к видимой модели корпуса: сначала вниз (стоит на корпусе), если под ней корпуса нет —
        /// вбок, к корпусу (багажник, борта, подножки). Двигаем и вверх, если деталь утонула.
        /// </summary>
        bool SettleOnModel(Part part, AttachPoint point, Part owner)
        {
            var frame = point.transform;
            var hullPts = HullShape.Points(owner, frame);
            if (hullPts.Count == 0) return false;

            bool any = false;
            Bounds b = default;
            foreach (var c in part.GetComponentsInChildren<Collider>())
            {
                if (!c.enabled || c.isTrigger || c.GetComponentInParent<Part>() != part) continue;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 w;
                    if (c is BoxCollider box)
                    {
                        Vector3 k = new Vector3((i & 1) == 0 ? -0.5f : 0.5f, (i & 2) == 0 ? -0.5f : 0.5f, (i & 4) == 0 ? -0.5f : 0.5f);
                        w = c.transform.TransformPoint(box.center + Vector3.Scale(box.size, k));
                    }
                    else
                    {
                        Bounds cb = c.bounds;
                        w = cb.center + Vector3.Scale(cb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    }
                    Vector3 l = frame.InverseTransformPoint(w);
                    if (!any) { b = new Bounds(l, Vector3.zero); any = true; } else b.Encapsulate(l);
                }
            }
            if (!any) return false;

            const float maxGap = 0.6f, maxSink = 0.35f, clearance = 0.005f;
            Vector3 axis = Vector3.down;
            float gap = 0f;
            // Боковина висит на борту нижним краем — её только придвигаем к корпусу, по высоте не трогаем.
            bool side = point.Slot == SlotType.Side;
            if (side || !HullShape.Gap(hullPts, b, axis, out gap) || gap > maxGap || gap < -maxSink)
            {
                // Под деталью корпуса нет — тянемся к нему вбок, по главной оси от детали к центру корпуса.
                Vector3 to = frame.InverseTransformPoint(owner.transform.position) - b.center;
                to.y = 0f;
                axis = Mathf.Abs(to.x) >= Mathf.Abs(to.z) ? new Vector3(Mathf.Sign(to.x), 0f, 0f) : new Vector3(0f, 0f, Mathf.Sign(to.z));
                if (!HullShape.Gap(hullPts, b, axis, out gap) || gap > maxGap || gap < -maxSink) return true;
            }
            part.transform.position += frame.TransformDirection(axis) * (gap - clearance);
            return true;
        }

        /// <summary>Оторвать (только авторитет).</summary>
        public void Detach(Part part, Vector3 impulse)
        {
            if (!Authority.IsAuthority) return;
            ApplyDetach(part, impulse);
        }

        /// <summary>Сам отрыв. На клиенте — по событию с хоста, тело остаётся кинематическим.</summary>
        public void ApplyDetach(Part part, Vector3 impulse)
        {
            if (part == null || part.Vehicle != this || part == RootPart) return;

            // Сначала отваливается всё, что висело на этой детали.
            foreach (var p in part.Points)
                if (p.Occupant != null) ApplyDetach(p.Occupant, impulse * 0.5f);

            bool authority = Authority.IsAuthority;
            Vector3 velocity = authority ? Body.GetPointVelocity(part.transform.position) : Vector3.zero;

            // Кресло сорвало вместе с пассажиром: его выбрасывает по инерции (у каждого — у себя, по событию отрыва).
            if (part is SeatPart seat && seat.Occupant != null)
            {
                Vector3 carVel = authority ? velocity : transform.forward * ForwardSpeed;
                World.ApplyLeaveSeat(seat.Occupant, carVel * 0.7f + impulse.normalized * 3f + Vector3.up * 4f);
            }

            if (part.MountedOn != null) part.MountedOn.Occupant = null;
            part.MountedOn = null;
            part.Vehicle = null;
            parts.Remove(part);
            part.transform.SetParent(null, true);
            part.SetMountedPhysics(false);

            var rb = part.gameObject.AddComponent<Rigidbody>();
            rb.mass = part.Mass;
            if (authority)
            {
                rb.interpolation = RigidbodyInterpolation.Interpolate;
                rb.linearVelocity = velocity;
                rb.AddForce(impulse, ForceMode.Impulse);
            }
            else
            {
                rb.isKinematic = true;
            }

            massDirty = true;
            part.OnDetached();
            PartDetached?.Invoke(part);
        }

        /// <summary>Поставить машину на колёса (кнопка R).</summary>
        /// <summary>
        /// Сброс (кнопка R): ставит машину на колёса носом по направлению дороги,
        /// на пару метров назад, на землю (или на воду, если там река).
        /// </summary>
        public void Flip()
        {
            if (!Authority.IsAuthority) return;

            // Сгенерированный маршрут: ближайшая дорога, назад по ней, носом вдоль дороги.
            var route = RouteMap.Current;
            if (route != null)
            {
                Vector3 start = Body.position;
                if ((start.y < fallResetY || route.IsOff(start)) && hasSafePosition) start = lastSafePosition;
                if (route.ResetPose(start, resetBack, ResetMargin, out Vector3 rp, out Vector3 rf))
                {
                    // Высота дороги известна из маршрута — не лучом (луч зацепит дерево или валун на дороге).
                    PlaceForReset(new Vector3(rp.x, rp.y + resetHeight, rp.z), rf);
                    onRoute = true;
                    offRouteTime = 0f;
                    return;
                }
            }

            var track = TrackBounds.Instance;
            Vector3 fwd;
            if (track != null) fwd = track.Forward;
            else
            {
                fwd = Vector3.ProjectOnPlane(roadForward, Vector3.up);
                fwd = fwd.sqrMagnitude > 0.01f ? fwd.normalized : Vector3.forward;
            }

            // Вылетела с трассы — возвращаем внутрь ограждений; упала ниже края — от последнего места на трассе.
            Vector3 from = Body.position;
            if (from.y < fallResetY && hasSafePosition) from = lastSafePosition;
            Vector3 target = from - fwd * resetBack;
            if (track != null) target = track.Clamp(target, ResetMargin);

            float surface = FindResetSurface(target, out bool found);
            if (!found && hasSafePosition)
            {
                target = lastSafePosition;
                if (track != null) target = track.Clamp(target, ResetMargin);
                surface = FindResetSurface(target, out found);
            }
            if (!found) surface = Mathf.Max(target.y, 0f);
            target.y = surface + resetHeight;
            PlaceForReset(target, fwd);
        }

        /// <summary>Поставить машину в точку (стартовая решётка): на землю под ней, носом по forward.</summary>
        public void PlaceAt(Vector3 point, Vector3 forward)
        {
            if (!Authority.IsAuthority) return;
            float surface = FindResetSurface(point, out bool found);
            if (!found) surface = point.y;
            PlaceForReset(new Vector3(point.x, surface + resetHeight, point.z), forward);
            onRoute = true;
            offRouteTime = 0f;
        }

        void PlaceForReset(Vector3 position, Vector3 forward)
        {
            Body.position = position;
            Body.rotation = Quaternion.LookRotation(forward, Vector3.up);
            Body.linearVelocity = Vector3.zero;
            Body.angularVelocity = Vector3.zero;
            airTime = 0f;
        }

        static readonly RaycastHit[] resetHits = new RaycastHit[32];

        /// <summary>
        /// Верхняя опора под точкой: земля, мост, трамплин. Свою машину, игроков и свободный хлам пропускаем.
        /// Если там река — поверхность воды.
        /// </summary>
        float FindResetSurface(Vector3 point, out bool found)
        {
            Vector3 from = new Vector3(point.x, point.y + 30f, point.z);
            int n = Physics.RaycastNonAlloc(from, Vector3.down, resetHits, 80f, ~0, QueryTriggerInteraction.Ignore);
            float bestDist = float.MaxValue, y = point.y;
            found = false;
            for (int i = 0; i < n; i++)
            {
                var h = resetHits[i];
                if (h.rigidbody == Body) continue;
                if (h.rigidbody != null && !h.rigidbody.isKinematic) continue;   // свободные детали и чужие машины
                if (h.collider is CharacterController) continue;   // игрок (у чужих аватаров коллайдера нет)
                if (h.distance < bestDist) { bestDist = h.distance; y = h.point.y; found = true; }
            }
            if (found && WaterZone.TryGetDepth(new Vector3(point.x, y + 0.05f, point.z), out float depth))
                y += depth;   // под водой — встаём на поверхность
            return y;
        }

        // ---------------- Физика ----------------

        void FixedUpdate()
        {
            if (!Authority.IsAuthority) return;
            if (RootPart.IsHidden || RootPart.IsCarried) return;   // спрятан в куче или его несут в руках
            float dt = Time.fixedDeltaTime;

            if (pendingBreaks.Count > 0)
            {
                foreach (var (part, impulse) in pendingBreaks)
                    if (part != null && part.Vehicle == this) Detach(part, impulse);
                pendingBreaks.Clear();
            }

            if (massDirty) RecalculateMass();

            // Водителя выбросило ударом (решено в OnCollisionEnter; выходить из машины посреди коллбэка физики нельзя).
            if (pendingEject)
            {
                pendingEject = false;
                if (HasDriver) World.ServerEjectDriver(this, ejectFling);
            }

            // Стоит без водителя, и физика его уже усыпила (пустой корпус на свалке) — ни сил, ни проверок
            // не нужно. От удара или прикрученной детали тело проснётся само, и шаг пойдёт как обычно.
            if (!HasDriver && Body.IsSleeping()) return;

            // Упала за край — сама на трассу. Пока стоит колёсами на трассе — запоминаем место.
            var route = RouteMap.Current;
            if (Body.position.y < fallResetY) Flip();
            else if (route != null)
            {
                // Открытый мир: проверяем раз в 0.2 с (поиск по маршруту не бесплатный).
                routeCheckTimer -= dt;
                if (routeCheckTimer <= 0f)
                {
                    routeCheckTimer = 0.2f;
                    onRoute = !route.IsOff(Body.position);
                }
                if (onRoute)
                {
                    offRouteTime = 0f;
                    if (groundedWheels > 0) { lastSafePosition = Body.position; hasSafePosition = true; }
                }
                else if (HasDriver)
                {
                    // Срезать не выйдет: уехал далеко или упал с горной дороги — обратно на трассу.
                    offRouteTime += dt;
                    if (offRouteTime > offRouteDelay) Flip();
                }
            }
            else if (groundedWheels > 0 && (TrackBounds.Instance == null || TrackBounds.Instance.Contains(Body.position, 1f)))
            {
                lastSafePosition = Body.position;
                hasSafePosition = true;
            }

            // Колёса отчитались в прошлом шаге — по ним делим мощность мотора.
            groundedWheels = groundedThisStep;
            groundedThisStep = 0;
            paddlingWheels = paddlingThisStep;
            paddlingThisStep = 0;
            int pushing = groundedWheels + paddlingWheels;
            // Отсчёт перед стартом: стоим на решётке, газ не работает.
            bool hold = RaceTimer.HoldVehicles;
            float throttle = hold ? 0f : Input.Throttle;
            if (hold)
            {
                Body.linearVelocity = new Vector3(0f, Mathf.Min(Body.linearVelocity.y, 0f), 0f);
                Body.angularVelocity = Vector3.zero;
            }
            if (TotalPower > 0f && pushing > 0)
            {
                // Тяга = мощность / скорость. На старте упирается в сцепление колёс (они сами обрежут).
                float speed = Mathf.Abs(Vector3.Dot(Body.linearVelocity, transform.forward));
                WheelDriveForce = throttle * TotalPower / Mathf.Max(speed, VehicleStats.MinSpeedForPower) / pushing;
            }
            else WheelDriveForce = 0f;

            // Место решает: поведение детали задаёт слот, в котором она стоит.
            // Списки по ролям собраны заранее (RecalculateMass — при каждой прикрутке/откручивании):
            // не перебираем весь хлам и не зовём пустые методы у деталей, которым нечего делать.
            for (int i = 0; i < wheelParts.Count; i++) wheelParts[i].TickAsWheel(dt);
            for (int i = 0; i < steeringParts.Count; i++) steeringParts[i].TickAsSteering(dt);
            for (int i = 0; i < bodyTickers.Count; i++) bodyTickers[i].Tick(dt);

            // Сопротивление воздуха: растёт с квадратом скорости — отсюда естественная максималка.
            Vector3 v = Body.linearVelocity;
            Body.AddForce(-v * v.magnitude * DragCoefficient);

            // Управление в прыжке: газ — нос вверх/вниз, руль — разворот.
            // В воде это не прыжок: газ не должен задирать нос. Там только поворот рулём.
            airTime = groundedWheels == 0 && !InWater ? airTime + dt : 0f;
            if (airTime > airControlDelay && SteerQuality > 0f)
                Body.AddRelativeTorque(new Vector3(throttle * airControl * 0.7f, Input.Steer * airControl, 0f) * SteerQuality, ForceMode.Acceleration);
            else if (groundedWheels == 0 && InWater && SteerQuality > 0f)
                Body.AddTorque(Vector3.up * Input.Steer * waterTurn * SteerQuality, ForceMode.Acceleration);

            ApplyWater();
        }

        void Update()
        {
            if (Authority.IsAuthority || Time.deltaTime <= 0f) { lastPosition = transform.position; return; }
            clientVelocity = Vector3.Lerp(clientVelocity, (transform.position - lastPosition) / Time.deltaTime, 0.2f);
            lastPosition = transform.position;
        }

        // Детали по ролям: колёса, рули и самостоятельные (вентилятор, зонт). Мотору тика не нужно —
        // его мощность уже в TotalPower.
        readonly List<Part> wheelParts = new List<Part>();
        readonly List<Part> steeringParts = new List<Part>();
        readonly List<IBodyTicker> bodyTickers = new List<IBodyTicker>();

        void RecalculateMass()
        {
            float total = 0f, power = 0f, drag = 0f, steer = 0f, protection = 0f;
            armorLeft = armorRight = false;
            Vector3 com = Vector3.zero;
            wheelParts.Clear();
            steeringParts.Clear();
            bodyTickers.Clear();
            foreach (var p in parts)
            {
                switch (p.Slot)
                {
                    case SlotType.Wheel: wheelParts.Add(p); break;
                    case SlotType.Steering: steeringParts.Add(p); break;
                    case SlotType.Engine: break;
                    default: if (p is IBodyTicker t) bodyTickers.Add(t); break;
                }
                // Масса и воздух — у всех, в любом слоте.
                total += p.Mass;
                com += transform.InverseTransformPoint(p.transform.position) * p.Mass;
                drag += p.AirDrag;
                if (p.GivesPowerIn(p.Slot)) power += p.AsEnginePower;
                if (p.Slot == SlotType.Steering) steer += p.AsSteering;
                float prot = ArmorPart.ProtectionOf(p, p.Slot);
                if (prot > 0f)
                {
                    protection += prot;
                    float x = transform.InverseTransformPoint(p.MountedOn.transform.position).x;
                    if (x < -0.1f) armorLeft = true; else if (x > 0.1f) armorRight = true;
                }
            }
            TotalPower = power;
            SteerQuality = Mathf.Clamp01(steer);
            Protection = Mathf.Clamp(protection, 0f, ArmorPart.MaxProtection);
            DragCoefficient = drag;
            Body.mass = Mathf.Max(1f, total);
            // Центр масс — по весу деталей, а не по объёму коллайдеров (как считает Unity):
            // у лёгкого плоского паллета коллайдер большой, а тяжёлые моторы и колёса — по краям.
            Body.ResetCenterOfMass();
            Body.ResetInertiaTensor();
            Body.centerOfMass = (total > 0f ? com / total : Vector3.zero) + centerOfMassOffset;
            massDirty = false;
        }

        void ApplyWater()
        {
            bool inWater = false;
            foreach (var p in parts)
            {
                var pts = p.FloatPoints;
                if (p.Buoyancy > 0f && pts != null)
                {
                    // Всплывает каждый нижний угол отдельно — получается выравнивающий момент.
                    float share = p.Buoyancy / pts.Length;
                    foreach (var lp in pts)
                    {
                        Vector3 wp = p.transform.TransformPoint(lp);
                        if (!WaterZone.TryGetDepth(wp, out float depth)) continue;
                        inWater = true;
                        Body.AddForceAtPosition(Vector3.up * Mathf.Min(depth, 1f) * share, wp);
                    }
                }
                else if (WaterZone.TryGetDepth(p.transform.position, out _)) inWater = true;
            }
            InWater = inWater;
            if (inWater)
            {
                Body.AddForce(-Body.linearVelocity * waterDrag, ForceMode.Acceleration);
                Body.AddTorque(-Body.angularVelocity * waterAngularDrag, ForceMode.Acceleration);
            }
        }

        void OnCollisionEnter(Collision collision)
        {
            if (!Authority.IsAuthority) return;
            float impulse = collision.impulse.magnitude;
            if (impulse <= 0f) return;

            for (int i = 0; i < collision.contactCount; i++)
            {
                var contact = collision.GetContact(i);
                var part = contact.thisCollider.GetComponentInParent<Part>();
                if (part == null || part == RootPart || part.Vehicle != this) continue;
                // Борта прикрывают остальные детали; сами борта принимают удар на себя.
                float need = part is ArmorPart ? part.BreakImpulse : part.BreakImpulse * ArmorPart.Strength(Protection);
                if (impulse < need) continue;

                // Отрываем не здесь, а в FixedUpdate: менять иерархию посреди коллбэка физики — к беде.
                pendingBreaks.Add((part, contact.normal * impulse * 0.02f + Vector3.up * 2f));
            }

            // Сильный удар — водитель может вылететь. Сила — резкая смена скорости машины (импульс / масса).
            if (HasDriver && !pendingEject && Time.time >= ejectCooldown && !RaceTimer.HoldVehicles)
            {
                float dv = impulse / Mathf.Max(1f, Body.mass);
                float chance = EjectChance(dv) * (1f - Protection * 0.5f);
                if (chance > 0f && UnityEngine.Random.value < chance)
                {
                    pendingEject = true;
                    ejectCooldown = Time.time + 1f;
                    Vector3 n = collision.contactCount > 0 ? collision.GetContact(0).normal : Vector3.up;
                    ejectFling = Body.linearVelocity * 0.8f + n * 2f + Vector3.up * 5f;
                }
            }
        }
    }
}
