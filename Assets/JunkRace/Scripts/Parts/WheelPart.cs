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
                visual.localPosition = transform.InverseTransformDirection(down) * drop;
            }
            else visual.localPosition = Vector3.zero;
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
