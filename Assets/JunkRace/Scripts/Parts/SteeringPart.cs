using System.Collections.Generic;
using UnityEngine;

namespace JunkRace
{
    /// <summary>
    /// Руль / весло. В слоте «Руль» разрешает поворот колёс (SteerQuality = 1)
    /// и немного крутит машину сам. В кузове — просто груз.
    /// </summary>
    public class SteeringPart : Part
    {
        [Tooltip("Насколько руль сам доворачивает машину, рад/с². Не зависит от массы:\n" +
                 "раньше это был момент в Н·м, и лёгкую тележку он раскручивал волчком.")]
        [SerializeField] float yawAccel = 1.5f;
        [SerializeField] Transform wheelVisual;
        [Tooltip("Насколько хорошо рулит, 0..1. Настоящий руль — 1, ноутбук — туго.")]
        [SerializeField, Range(0f, 1f)] float quality = 1f;
        [Tooltip("На сколько градусов поворачивается вид при полном повороте.")]
        [SerializeField] float visualTurn = 90f;
        [Tooltip("Строчка для карточки детали.")]
        [SerializeField] string steerHint = "";
        float steer;

        public override SlotType NativeSlot => SlotType.Steering;
        public override float AsSteering => quality;

        public override void DescribeStats(List<string> lines)
        {
            lines.Add(quality >= 0.999f ? "Колёса начинают поворачивать" : $"Рулит туго ({quality * 100f:0}%)");
            if (!string.IsNullOrEmpty(steerHint)) lines.Add(steerHint);
            if (yawAccel >= 2.2f) lines.Add("Резкий: машину легко развернуть");
            lines.Add("Можно крутиться в прыжке");
            base.DescribeStats(lines);
        }

        public override void TickAsSteering(float dt)
        {
            steer = Input.Steer;
            Vehicle.Body.AddTorque(Vehicle.transform.up * steer * yawAccel * Vehicle.SteerQuality, ForceMode.Acceleration);
        }

        public override void OnDetached()
        {
            base.OnDetached();
            steer = 0f;
            if (wheelVisual) wheelVisual.localRotation = Quaternion.identity;   // Update больше не крутится
        }

        void Update()
        {
            if (wheelVisual) wheelVisual.localRotation = Quaternion.Euler(0f, steer * visualTurn, 0f);
        }
    }
}
