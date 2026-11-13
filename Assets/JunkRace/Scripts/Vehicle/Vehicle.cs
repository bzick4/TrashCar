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
        float airTime;
        Vector3 lastSafePosition;
        bool hasSafePosition;
        const float ResetMargin = 3f;   // отступ от ограждений при возврате на трассу

        public int TeamId;   // для командного режима, пока не используется

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

        void Awake()
        {
            Body = GetComponent<Rigidbody>();
            Body.interpolation = RigidbodyInterpolation.Interpolate;
            RootPart = GetComponent<Part>();
            RootPart.Vehicle = this;
            parts.Add(RootPart);
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

        public void SetDriver(IPartInput driver)
        {
            Driver = driver;
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
            part.transform.localRotation = Quaternion.identity;
            part.SetCollidersEnabled(true);
            part.SetMountedPhysics(true);

            point.Occupant = part;
            part.MountedOn = point;
            part.Vehicle = this;
            parts.Add(part);
            massDirty = true;

            part.OnAttached();
            PartAttached?.Invoke(part);
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

            Body.position = target;
            Body.rotation = Quaternion.LookRotation(fwd, Vector3.up);
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
                if (h.collider.GetComponentInParent<PlayerController>() || h.collider.GetComponentInParent<RemoteAvatar>()) continue;
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

            // Упала за край — сама на трассу. Пока стоит колёсами на трассе — запоминаем место.
            if (Body.position.y < fallResetY) Flip();
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
            float throttle = Input.Throttle;
            if (TotalPower > 0f && pushing > 0)
            {
                // Тяга = мощность / скорость. На старте упирается в сцепление колёс (они сами обрежут).
                float speed = Mathf.Abs(Vector3.Dot(Body.linearVelocity, transform.forward));
                WheelDriveForce = throttle * TotalPower / Mathf.Max(speed, VehicleStats.MinSpeedForPower) / pushing;
            }
            else WheelDriveForce = 0f;

            // Место решает: поведение детали задаёт слот, в котором она стоит.
            for (int i = 0; i < parts.Count; i++)
            {
                var p = parts[i];
                switch (p.Slot)
                {
                    case SlotType.Wheel: p.TickAsWheel(dt); break;
                    case SlotType.Steering: p.TickAsSteering(dt); break;
                    case SlotType.Engine: break;   // мощность уже учтена в TotalPower
                    default: p.Tick(dt); break;    // кузов и корень: работают только самостоятельные детали
                }
            }

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

        void RecalculateMass()
        {
            float total = 0f, power = 0f, drag = 0f, steer = 0f;
            Vector3 com = Vector3.zero;
            foreach (var p in parts)
            {
                // Масса и воздух — у всех, в любом слоте.
                total += p.Mass;
                com += transform.InverseTransformPoint(p.transform.position) * p.Mass;
                drag += p.AirDrag;
                if (p.GivesPowerIn(p.Slot)) power += p.AsEnginePower;
                if (p.Slot == SlotType.Steering) steer += p.AsSteering;
            }
            TotalPower = power;
            SteerQuality = Mathf.Clamp01(steer);
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
                if (impulse < part.BreakImpulse) continue;

                // Отрываем не здесь, а в FixedUpdate: менять иерархию посреди коллбэка физики — к беде.
                pendingBreaks.Add((part, contact.normal * impulse * 0.02f + Vector3.up * 2f));
            }
        }
    }
}
