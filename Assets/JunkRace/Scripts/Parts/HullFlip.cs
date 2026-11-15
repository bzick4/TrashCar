using UnityEngine;

namespace JunkRace
{
    /// <summary>
    /// Корпус можно перевернуть (R): стол ножками вверх или вниз, ванна дном вверх.
    /// Переворачивается только вид и коллайдеры (контейнер body), а места крепления остаются удобными:
    /// «пол» и колёса сдвигаются на заданную высоту, так что и в перевёрнутом виде всё прикручивается.
    /// Прикрученные детали едут вместе со своими местами.
    /// </summary>
    [RequireComponent(typeof(Part))]
    public class HullFlip : MonoBehaviour
    {
        [Tooltip("Вид и коллайдеры корпуса (всё, кроме мест крепления).")]
        [SerializeField] Transform body;
        [Tooltip("Высота, вокруг которой переворачиваем (центр корпуса или столешницы).")]
        [SerializeField] float pivotY;
        [Tooltip("Насколько сдвинуть «пол» (руль, моторы, кресла, верх) в перевёрнутом виде.")]
        [SerializeField] float floorShift;
        [Tooltip("Насколько сдвинуть места колёс в перевёрнутом виде.")]
        [SerializeField] float wheelShift;

        public bool Flipped { get; private set; }

        Part part;
        Transform seat;
        Vector3[] basePoints;
        Vector3 baseSeat;

        void Awake()
        {
            part = GetComponent<Part>();
        }

        void CacheBase()
        {
            if (basePoints != null) return;
            var points = part.Points;
            basePoints = new Vector3[points.Length];
            for (int i = 0; i < points.Length; i++) basePoints[i] = points[i].transform.localPosition;
            var v = GetComponent<Vehicle>();
            seat = v != null && v.Seat != transform ? v.Seat : null;
            if (seat != null) baseSeat = seat.localPosition;
        }

        /// <summary>Перевернуть (выполняется везде: на хосте и у клиентов по событию).</summary>
        public void ApplyFlipped(bool flipped)
        {
            if (flipped == Flipped || body == null) return;
            CacheBase();
            Flipped = flipped;

            // Поворот на 180° вокруг продольной оси через pivotY: y → 2·pivotY − y.
            body.localRotation = flipped ? Quaternion.Euler(0f, 0f, 180f) : Quaternion.identity;
            body.localPosition = flipped ? new Vector3(0f, 2f * pivotY, 0f) : Vector3.zero;

            var points = part.Points;
            for (int i = 0; i < points.Length; i++)
            {
                // Борта висят на боках корпуса: корпус переворачивается в том же объёме, им двигаться не нужно.
                float shift = !flipped ? 0f : points[i].Slot == SlotType.Wheel ? wheelShift : points[i].Slot == SlotType.Side ? 0f : floorShift;
                points[i].transform.localPosition = basePoints[i] + Vector3.up * shift;
            }
            if (seat != null) seat.localPosition = baseSeat + Vector3.up * (flipped ? floorShift : 0f);

            Physics.SyncTransforms();   // чтобы bounds коллайдеров были уже перевёрнутыми
            part.RefreshShape();
            var vehicle = GetComponent<Vehicle>();
            if (vehicle != null) vehicle.MarkMassDirty();

            // Лежит на земле — приподнять, чтобы перевёрнутый не провалился в землю.
            if (Authority.IsAuthority && !part.IsCarried && TryGetComponent(out Rigidbody rb) && !rb.isKinematic)
            {
                rb.position += Vector3.up * (Mathf.Abs(wheelShift) + Mathf.Abs(floorShift) + 0.3f);
                rb.WakeUp();
            }
        }
    }
}
