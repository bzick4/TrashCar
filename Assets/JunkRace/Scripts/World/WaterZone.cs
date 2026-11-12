using System.Collections.Generic;
using UnityEngine;

namespace JunkRace
{
    /// <summary>Лужа/речка: BoxCollider-триггер. Верх коробки = поверхность воды.</summary>
    [RequireComponent(typeof(BoxCollider))]
    public class WaterZone : MonoBehaviour
    {
        static readonly List<WaterZone> zones = new List<WaterZone>();
        BoxCollider box;

        void Awake()
        {
            box = GetComponent<BoxCollider>();
            box.isTrigger = true;
        }

        void OnEnable() => zones.Add(this);
        void OnDisable() => zones.Remove(this);

        public static bool TryGetDepth(Vector3 point, out float depth)
        {
            foreach (var z in zones)
            {
                Bounds b = z.box.bounds;
                if (b.Contains(point)) { depth = b.max.y - point.y; return true; }
            }
            depth = 0f;
            return false;
        }
    }
}
