using UnityEngine;

namespace JunkRace
{
    [CreateAssetMenu(menuName = "JunkRace/Деталь/Руль", fileName = "Руль", order = 3)]
    public class SteeringDefinition : PartDefinition
    {
        [Tooltip("Насколько резво разворачивает машину.")]
        [SerializeField] float yawAccel = 1.5f;
    }
}
