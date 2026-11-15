using UnityEngine;

namespace JunkRace
{
    [CreateAssetMenu(menuName = "JunkRace/Деталь/Колесо", fileName = "Колесо", order = 1)]
    public class WheelDefinition : PartDefinition
    {
        [Tooltip("Радиус, м.")]
        [SerializeField] float radius = 0.4f;
        [Tooltip("Ход подвески, м.")]
        [SerializeField] float suspension = 0.25f;
        [SerializeField] float spring = 4000f;
        [SerializeField] float damper = 350f;
        [Tooltip("Наибольшая тяга, Н.")]
        [SerializeField] float traction = 700f;
        [Tooltip("Боковое сцепление.")]
        [SerializeField] float grip = 500f;
        [SerializeField] bool steerable = true;
        [SerializeField] float maxSteerAngle = 30f;
        [Tooltip("Сопротивление качению: мягкие и вязкие колёса тормозят на дороге.")]
        [SerializeField] float rollingResistance = 3f;
        [Tooltip("Как гребёт в воде.")]
        [SerializeField] float paddle = 0.5f;
    }
}
