using UnityEngine;

namespace JunkRace
{
    /// <summary>Триггер финиша. Засчитывает машину с водителем.</summary>
    [RequireComponent(typeof(Collider))]
    public class FinishLine : MonoBehaviour
    {
        void Awake() => GetComponent<Collider>().isTrigger = true;

        void OnTriggerEnter(Collider other)
        {
            if (!Authority.IsAuthority) return;
            var rb = other.attachedRigidbody;
            if (rb == null || !rb.TryGetComponent(out Vehicle v) || !v.HasDriver) return;
            if (RaceTimer.Instance) RaceTimer.Instance.Finish(v);
        }
    }
}
