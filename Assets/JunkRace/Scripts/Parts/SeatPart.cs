using System.Collections.Generic;
using UnityEngine;

namespace JunkRace
{
    /// <summary>
    /// Кресло: место для пассажира (F — сесть). Работает в слоте «Сиденье» и в кузове.
    /// Слоты сидений — по бокам над колёсами: колёсам не мешают (стоят выше шины, а сидящий игрок
    /// без коллайдера). Зато при ударе о препятствие кресло отрывается (breakImpulse) —
    /// и пассажира выбрасывает вместе с ним, а машина едет дальше.
    /// </summary>
    public class SeatPart : Part
    {
        [Tooltip("Куда садится игрок (центр капсулы игрока).")]
        [SerializeField] Transform sitPoint;

        /// <summary>Кто сидит. Выставляет World.ApplySit / ApplyLeaveSeat.</summary>
        public IAvatar Occupant { get; internal set; }
        public Transform SitPoint => sitPoint != null ? sitPoint : transform;

        public override SlotType NativeSlot => SlotType.Seat;
        public override bool WorksInBody => true;   // кресло на кузове — тоже место
        public override bool IsOccupied => Occupant != null;

        /// <summary>Можно ли сесть: прикручено к машине, в слоте сиденья или кузова, свободно.</summary>
        public bool CanSit =>
            Occupant == null && !IsHidden && !IsCarried && Vehicle != null && MountedOn != null
            && (Slot == SlotType.Seat || Slot == SlotType.Body) && !Vehicle.RootPart.IsCarried;

        public override void DescribeStats(List<string> lines)
        {
            lines.Add("Место для пассажира (F — сесть)");
            lines.Add(BreakImpulse >= 800f ? "Держится крепко" : "При ударе слетает вместе с пассажиром");
            base.DescribeStats(lines);
        }
    }
}
