using UnityEngine;

namespace JunkRace
{
    [CreateAssetMenu(menuName = "JunkRace/Деталь/Мотор", fileName = "Мотор", order = 2)]
    public class EngineDefinition : PartDefinition
    {
        [Tooltip("Мощность, Вт (735.5 Вт = 1 л.с.).")]
        [SerializeField] float power = 4000f;
    }
}
