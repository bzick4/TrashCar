using System.Collections.Generic;
using UnityEngine;

namespace JunkRace
{
    /// <summary>
    /// Колесо. Сама физика колеса (луч-подвеска) живёт в Part.TickAsWheel и включается слотом «Колесо»,
    /// здесь только хорошие параметры и вид шины.
    /// Тягу даёт мотор машины (Vehicle.WheelDriveForce), колесо лишь ограничивает её своим сцеплением.
    /// Поворачивает, если стоит в поворотном слоте и на машине есть руль (Vehicle.SteerQuality).
    /// В слоте руля работает как руль, но туго (60%). В кузове — просто груз.
    /// </summary>
    public class WheelPart : Part
    {
        [Header("Подвеска")]
        [SerializeField] float radius = 0.4f;
        [Tooltip("Ход подвески, м: насколько шина может опуститься ниже места крепления.")]
        [SerializeField] float suspension = 0.25f;
        [SerializeField] float spring = 4000f;
        [SerializeField] float damper = 350f;

        [Header("Езда")]
        [Tooltip("Сколько тяги колесо может передать на дорогу, Н. Больше — лучше разгон.")]
        [SerializeField] float traction = 700f;
        [Tooltip("Боковое сцепление. Меньше — больше заносит.")]
        [SerializeField] float grip = 500f;
        [SerializeField] bool steerable = true;
        [SerializeField] float maxSteerAngle = 30f;
        [SerializeField] float rollingResistance = 3f;
        [Tooltip("Как гребёт в воде: доля тяги мотора. 0.5 — обычное колесо, больше 1 — спасательный круг.")]
        [SerializeField] float paddle = 0.5f;

        [SerializeField] Transform visual;

        /// <summary>Точка колеса стоит на столько снаружи борта (см. AddHullPoints). Тонкие колёса прижимаем к борту.</summary>
        const float MountOutset = 0.25f;
        const float BoardGap = 0.02f;
        /// <summary>Толщина боковины (ArmorPart) с запасом: шина на этой стороне встаёт снаружи неё.</summary>
        const float SideArmorThickness = 0.09f;
        [Tooltip("Полуширина модели шины (ставит сборщик сцены). 0 — мерить по мешам.")]
        [SerializeField] float modelHalfWidth;
        float halfWidth = -1f;

        public override SlotType NativeSlot => SlotType.Wheel;
        public override float AsSteering => 0.6f;   // колесо вместо руля: крутить можно, но туго

        public override float AsWheelRadius => radius;
        public override float AsWheelSuspension => suspension;
        public override float AsWheelSpring => spring;
        public override float AsWheelDamper => damper;
        public override float AsWheelTraction => traction;
        public override float AsWheelGrip => grip;
        public override float AsWheelRollingResistance => rollingResistance;
        public override float AsWheelMaxSteerAngle => steerable ? maxSteerAngle : 0f;
        public override float AsWheelPaddle => paddle;

        protected override void Awake()
        {
            base.Awake();
            if (modelHalfWidth > 0f) halfWidth = modelHalfWidth;
            else if (visual != null) halfWidth = MeasureHalfWidth();   // пока шина стоит прямо
        }

        protected override void LateUpdate()
        {
            if (visual == null) return;
            visual.localRotation = Quaternion.Euler(0f, wheelSteerAngle, 0f) * Quaternion.Euler(wheelSpin, 0f, 0f);

            // Шина опускается до земли: подвеска — это ход колеса, а не воздух под ним.
            // Считаем прямо тут (и на клиенте тоже), коллайдер детали при этом не двигается.
            if (Vehicle != null && MountedOn != null && Slot == SlotType.Wheel)
            {
                Vector3 down = -Vehicle.transform.up;
                float drop = suspension;
                if (GroundCast(transform.position, down, radius + suspension, out var hit))
                    drop = Mathf.Clamp(hit.distance - radius, 0f, suspension);
                visual.localPosition = transform.InverseTransformDirection(down) * drop + InsetTowardHull();
            }
            else visual.localPosition = Vector3.zero;
        }

        /// <summary>
        /// Сдвиг шины к корпусу: точка крепления — на MountOutset от борта, а тонкая шина (люк) туда не дотягивается
        /// и висит в воздухе. Сдвигаем только вид, подвеска считается от точки как раньше.
        /// </summary>
        Vector3 InsetTowardHull()
        {
            if (halfWidth < 0f) halfWidth = MeasureHalfWidth();
            float side = Vehicle.transform.InverseTransformPoint(transform.position).x;
            if (Mathf.Abs(side) < 0.01f) return Vector3.zero;
            // На этой стороне висит боковина — шина встаёт снаружи неё.
            float armor = Vehicle.HasSideArmor(side) ? SideArmorThickness : 0f;
            float toHull = GapToHull(side);
            float inset = (float.IsNaN(toHull) ? MountOutset - halfWidth : toHull) - BoardGap - armor;
            // Без модели корпуса — только к борту; по модели можно и наружу (корпус шире коробок).
            if (float.IsNaN(toHull) ? inset <= 0f : Mathf.Abs(inset) < 0.002f) return Vector3.zero;
            Vector3 inward = -Vehicle.transform.right * Mathf.Sign(side);
            return transform.InverseTransformDirection(inward) * inset;
        }

        AttachPoint gapPoint;
        Vector3 gapPointPos;
        float gapToHull = float.NaN;

        /// <summary>
        /// Зазор от внутренней грани шины до видимой модели корпуса (по вершинам), в полосе высоты и длины колеса.
        /// NaN — модели нет, тогда считаем, что борт на MountOutset от точки. Пересчёт — только когда сменилась точка.
        /// </summary>
        float GapToHull(float side)
        {
            var point = MountedOn;
            if (point == gapPoint && point != null && point.transform.localPosition == gapPointPos) return gapToHull;
            gapPoint = point;
            gapToHull = float.NaN;
            if (point == null) return gapToHull;
            gapPointPos = point.transform.localPosition;
            var pts = HullShape.Points(point.Owner, transform);
            if (pts.Count == 0) return gapToHull;
            // Колесо в своих координатах: ось — X. Коробка шины, к корпусу — по X внутрь.
            Vector3 inward = transform.InverseTransformDirection(-Vehicle.transform.right * Mathf.Sign(side));
            Vector3 axis = Mathf.Abs(inward.x) >= Mathf.Abs(inward.z) ? new Vector3(Mathf.Sign(inward.x), 0f, 0f) : new Vector3(0f, 0f, Mathf.Sign(inward.z));
            Vector3 size = new Vector3(radius * 2f, radius * 2f, radius * 2f);
            if (axis.x != 0f) size.x = halfWidth * 2f; else size.z = halfWidth * 2f;
            if (HullShape.Gap(pts, new Bounds(Vector3.zero, size), axis, out float gap, 0.8f) && gap > -0.3f && gap < 0.8f)
                gapToHull = gap;
            return gapToHull;
        }

        /// <summary>Полуширина шины вдоль оси колеса (локальная X детали), по мешам вида.</summary>
        float MeasureHalfWidth()
        {
            float max = 0f;
            Matrix4x4 toLocal = transform.worldToLocalMatrix;
            foreach (var mf in visual.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                Matrix4x4 m = toLocal * mf.transform.localToWorldMatrix;
                Bounds b = mf.sharedMesh.bounds;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 c = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    max = Mathf.Max(max, Mathf.Abs(m.MultiplyPoint3x4(c).x));
                }
            }
            return max > 0f ? max : MountOutset;
        }

        public override void DescribeStats(List<string> lines)
        {
            lines.Add($"Тяга до {traction:0} Н");
            lines.Add(grip >= 600f ? "Держит дорогу" : grip <= 350f ? "Скользкое" : "Сцепление среднее");
            if (paddle >= 1f) lines.Add("В воде гребёт отлично");
            if (radius >= 0.55f) lines.Add("Большое: машина стоит высоко");
            if (rollingResistance >= 6f) lines.Add("Мягкое: на дороге тормозит, нужен мотор помощнее");
            base.DescribeStats(lines);
        }
    }
}
