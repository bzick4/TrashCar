using UnityEngine;

namespace JunkRace
{
    /// <summary>
    /// Границы трассы и направление дороги. По ним кнопка R возвращает машину на трассу,
    /// если она вылетела за ограждение или упала за край.
    /// </summary>
    public class TrackBounds : MonoBehaviour
    {
        public static TrackBounds Instance { get; private set; }

        [SerializeField] float minX = -21f, maxX = 21f, minZ = -40f, maxZ = 145f;
        [Tooltip("Куда смотрит дорога (к финишу).")]
        [SerializeField] Vector3 forward = Vector3.forward;

        public Vector3 Forward
        {
            get
            {
                Vector3 f = Vector3.ProjectOnPlane(forward, Vector3.up);
                return f.sqrMagnitude > 0.01f ? f.normalized : Vector3.forward;
            }
        }

        void Awake() => Instance = this;
        void OnDestroy() { if (Instance == this) Instance = null; }

        public bool Contains(Vector3 p, float margin = 0f) =>
            p.x >= minX + margin && p.x <= maxX - margin && p.z >= minZ + margin && p.z <= maxZ - margin;

        /// <summary>Ближайшая точка внутри трассы с отступом от ограждений.</summary>
        public Vector3 Clamp(Vector3 p, float margin)
        {
            p.x = Mathf.Clamp(p.x, minX + margin, maxX - margin);
            p.z = Mathf.Clamp(p.z, minZ + margin, maxZ - margin);
            return p;
        }
    }
}
