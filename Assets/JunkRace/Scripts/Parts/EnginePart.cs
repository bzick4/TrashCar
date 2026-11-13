using System.Collections.Generic;
using UnityEngine;

namespace JunkRace
{
    /// <summary>
    /// Мотор: в слоте «Мотор» даёт мощность всем колёсам машины. В кузове — просто груз.
    /// Мощность в ваттах (1 л.с. ≈ 735 Вт). Скорость и разгон считаются из неё в Vehicle.
    /// </summary>
    public class EnginePart : Part
    {
        [SerializeField] float power = 4000f;
        [Tooltip("Что трясти, когда жмут газ (для вида).")]
        [SerializeField] Transform shaker;

        public float Power => power;
        public override SlotType NativeSlot => SlotType.Engine;
        public override float AsEnginePower => power;

        Vector3 shakerBase;

        protected override void Awake()
        {
            base.Awake();
            if (shaker) shakerBase = shaker.localPosition;
        }

        void Update()
        {
            if (!shaker) return;
            bool running = Vehicle != null && GivesPowerIn(Slot);
            float amount = running ? 0.01f + Mathf.Abs(Input.Throttle) * 0.04f : 0f;
            shaker.localPosition = shakerBase + Random.insideUnitSphere * amount;
        }

        public override void DescribeStats(List<string> lines)
        {
            lines.Add($"Мощность: {power / 735.5f:0.0} л.с.");
            base.DescribeStats(lines);
        }
    }
}
