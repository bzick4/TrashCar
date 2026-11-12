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
        float steer;

        public override SlotType NativeSlot => SlotType.Steering;
        public override float AsSteering => 1f;

        public override void DescribeStats(List<string> lines)
        {
            lines.Add("Колёса начинают поворачивать");
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
        }

        void Update()
        {
            if (wheelVisual) wheelVisual.localRotation = Quaternion.Euler(0f, steer * 90f, 0f);
        }
    }
}
