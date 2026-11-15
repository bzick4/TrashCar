using UnityEngine;

namespace JunkRace
{
    [CreateAssetMenu(menuName = "JunkRace/Деталь/Толкатель (вентилятор, пылесос)", fileName = "Толкатель", order = 4)]
    public class ThrusterDefinition : PartDefinition
    {
        [Tooltip("Тяга, Н.")]
        [SerializeField] float thrust = 2500f;
        [Tooltip("Работает только пока зажат пробел.")]
        [SerializeField] bool useSpecialButton = false;
        [SerializeField] float spinSpeed = 1500f;
    }
}
