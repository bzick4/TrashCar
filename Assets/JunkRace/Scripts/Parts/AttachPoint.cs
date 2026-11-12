using System.Collections.Generic;
using UnityEngine;

namespace JunkRace
{
    /// <summary>
    /// Тип места на машине. Место решает, как работает деталь:
    /// в слоте колеса всё катится как колесо, в слоте мотора — даёт мощность (если может), и т. д.
    /// </summary>
    public enum SlotType { Body, Wheel, Steering, Engine, Seat }

    /// <summary>
    /// Точка крепления («место под скотч»). Дочерний объект детали.
    /// Прикрученная деталь встаёт в позицию и поворот точки.
    /// Принимает любую деталь: колесо в слот руля, зонт на багажник — пожалуйста.
    /// </summary>
    public class AttachPoint : MonoBehaviour
    {
        public static readonly List<AttachPoint> All = new List<AttachPoint>();

        [Tooltip("Что это за место. От него зависит, как будет работать прикрученная деталь.")]
        [SerializeField] SlotType slot = SlotType.Body;
        [Tooltip("Только для колёс: поворачивает ли рулём (передняя пара).")]
        [SerializeField] bool steers = false;
        [Tooltip("Подпись для игрока. Пусто = имя объекта без «AP_».")]
        [SerializeField] string label = "";

        public SlotType Slot => slot;
        public bool Steers => slot == SlotType.Wheel && steers;
        public string Label => string.IsNullOrEmpty(label) ? DefaultLabel() : label;

        public Part Owner { get; private set; }
        public Part Occupant { get; internal set; }
        public bool Occupied => Occupant != null;

        static readonly string[] SlotNames = { "Кузов", "Колесо", "Руль", "Мотор", "Сиденье" };
        public static string SlotName(SlotType slot) => SlotNames[(int)slot];

        void Awake() => Owner = GetComponentInParent<Part>();
        void OnEnable() => All.Add(this);
        void OnDisable() => All.Remove(this);

        /// <summary>Ставить можно что угодно куда угодно — как оно будет работать, решает слот.</summary>
        public bool Accepts(Part part) => part != null;

        string DefaultLabel()
        {
            string n = name.StartsWith("AP_") ? name.Substring(3) : name;
            return n.Replace('_', ' ');
        }

        void OnDrawGizmos()
        {
            Gizmos.color = Occupied ? Color.red
                : slot == SlotType.Wheel ? Color.cyan
                : slot == SlotType.Steering ? Color.yellow
                : slot == SlotType.Engine ? new Color(1f, 0.5f, 0f)
                : slot == SlotType.Seat ? Color.magenta
                : Color.green;
            Gizmos.DrawWireSphere(transform.position, 0.15f);
            Gizmos.DrawRay(transform.position, transform.forward * 0.4f);
        }
    }
}
