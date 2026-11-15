using UnityEngine;

namespace JunkRace
{
    [CreateAssetMenu(menuName = "JunkRace/Деталь/Зонт", fileName = "Зонт", order = 5)]
    public class UmbrellaDefinition : PartDefinition
    {
        [Tooltip("Торможение падения.")]
        [SerializeField] float drag = 150f;
    }
}
