using UnityEngine;

namespace JunkRace
{
    /// <summary>
    /// Описание детали: характеристики и тексты в одном ассете, общем для всех одинаковых деталей.
    /// Баланс правится здесь — без пересборки сцены.
    ///
    /// Как применяется: при старте деталь (Part.Awake) переписывает свои поля значениями отсюда.
    /// Имена полей совпадают с полями компонентов (mass, radius, power…) — копирование идёт по имени.
    /// Ссылки на объекты сцены (визуал колеса, лопасти, точка посадки) остаются на самом компоненте.
    /// </summary>
    [CreateAssetMenu(menuName = "JunkRace/Деталь/Обычная", fileName = "Деталь", order = 0)]
    public class PartDefinition : ScriptableObject
    {
        [SerializeField] PartCategory category = PartCategory.Special;
        [SerializeField] string displayName = "Хлам";
        [Tooltip("Масса, кг.")]
        [SerializeField] float mass = 10f;
        [Tooltip("Удар (импульс), от которого деталь отваливается.")]
        [SerializeField] float breakImpulse = 400f;
        [Tooltip("Сила всплытия, Н. Больше веса (масса × 9.8) — держит машину на воде.")]
        [SerializeField] float buoyancy = 0f;
        [Tooltip("Сопротивление воздуха.")]
        [SerializeField] float airDrag = 0.05f;
        [SerializeField, TextArea] string description = "";

        public PartCategory Category => category;
        public string DisplayName => displayName;

        /// <summary>Переписать поля детали значениями из описания.</summary>
        public void ApplyTo(Part part) => JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(this), part);
    }
}
