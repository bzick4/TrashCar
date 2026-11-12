using System.Collections.Generic;
using UnityEngine;

namespace JunkRace
{
    /// <summary>
    /// Вентилятор / пылесос: в кузове толкает машину туда, куда смотрит. Криво прикрутил — крутишься.
    /// В слоте «Мотор» крутит колёса, но слабо (~0.8 Вт на 1 Н тяги).
    /// </summary>
    public class ThrusterPart : Part
    {
        [SerializeField] float thrust = 2500f;
        [Tooltip("Работает от спец-кнопки, а не от газа (пылесос-ускоритель).")]
        [SerializeField] bool useSpecialButton = false;
        [SerializeField] Transform spinner;
        [SerializeField] float spinSpeed = 1500f;

        float power;

        public float Thrust => thrust;
        public bool UsesSpecialButton => useSpecialButton;

        public override bool WorksInBody => true;
        public override float AsEnginePower => thrust * 0.8f;

        public override void DescribeStats(List<string> lines)
        {
            lines.Add($"Тяга: {thrust:0} Н" + (useSpecialButton ? " (на пробел)" : ""));
            lines.Add("Толкает туда, куда смотрит");
            base.DescribeStats(lines);
        }

        public override void Tick(float dt)
        {
            power = useSpecialButton ? (Input.Special ? 1f : 0f) : Input.Throttle;
            // Толкаем на высоте центра масс (по вертикали мира): иначе вентилятор, стоящий выше центра масс,
            // опрокидывает лёгкую машину вперёд. Смещение вбок сохраняем — криво прикрутил, крутишься.
            var body = Vehicle.Body;
            Vector3 at = new Vector3(transform.position.x, body.worldCenterOfMass.y, transform.position.z);
            body.AddForceAtPosition(transform.forward * thrust * power, at);
        }

        public override void OnDetached()
        {
            base.OnDetached();
            power = 0f;
        }

        void Update()
        {
            if (spinner) spinner.Rotate(0f, 0f, spinSpeed * power * Time.deltaTime, Space.Self);
        }
    }
}
