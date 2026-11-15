using System.Collections.Generic;
using UnityEngine;

namespace JunkRace
{
    /// <summary>
    /// Боковина: дверь от машины, оконная рама, кусок шифера. В слоте «Борт» прикрывает машину:
    /// остальным деталям нужен удар сильнее, чтобы отвалиться, и водителя реже выкидывает.
    /// Зато тяжёлая (медленнее, но устойчивее) и ловит воздух.
    /// </summary>
    public class ArmorPart : Part
    {
        /// <summary>Больше этой защиты не набрать, сколько бортов ни навешивай.</summary>
        public const float MaxProtection = 0.6f;

        [Tooltip("Защита в слоте «Борт», 0..1: на столько процентов крепче держатся остальные детали.")]
        [SerializeField, Range(0f, 1f)] float protection = 0.2f;

        public float Protection => protection;
        public override SlotType NativeSlot => SlotType.Side;

        /// <summary>
        /// Не на «Борту» щит всё равно встаёт вдоль корпуса, лицом наружу: сбоку — вдоль борта,
        /// спереди и сзади — поперёк. Иначе на боковой точке (колесо, подножка) он торчал бы поперёк машины.
        /// </summary>
        public override Quaternion MountRotation(AttachPoint point)
        {
            var owner = point.Owner;
            if (point.Slot == SlotType.Side || owner == null) return Quaternion.identity;
            Vector3 at = owner.transform.InverseTransformPoint(point.transform.position);
            float yaw = Mathf.Abs(at.x) >= Mathf.Abs(at.z) * 0.5f ? Mathf.Sign(at.x) * 90f : (at.z >= 0f ? 0f : 180f);
            Quaternion inHull = Quaternion.Inverse(owner.transform.rotation) * point.transform.rotation;
            return Quaternion.Inverse(inHull) * Quaternion.Euler(0f, yaw, 0f);
        }

        /// <summary>Сколько защиты даёт деталь в этом слоте (работает только в слоте «Борт»).</summary>
        public static float ProtectionOf(Part part, SlotType slot) =>
            part is ArmorPart a && slot == SlotType.Side ? a.protection : 0f;

        /// <summary>Во сколько раз крепче держатся детали при такой суммарной защите.</summary>
        public static float Strength(float protection) => 1f / (1f - Mathf.Clamp(protection, 0f, MaxProtection));

        public override void DescribeStats(List<string> lines)
        {
            lines.Add($"На борту: детали держатся крепче (+{protection * 100f:0}%)");
            lines.Add("Водителя реже выкидывает");
            lines.Add("Тяжёлая: медленнее, но устойчивее");
            base.DescribeStats(lines);
        }
    }
}
