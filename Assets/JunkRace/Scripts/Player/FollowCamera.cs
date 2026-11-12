using UnityEngine;
using UnityEngine.Rendering;

namespace JunkRace
{
    /// <summary>
    /// Камера игрока. По умолчанию от первого лица, V — переключить на вид сзади.
    /// От первого лица: пешком мышь поворачивает самого игрока, деталь в руках висит перед глазами
    /// и следует за взглядом; за рулём камера сидит на сиденье и можно вертеть головой.
    /// </summary>
    public class FollowCamera : MonoBehaviour
    {
        [SerializeField] PlayerController player;

        [Header("Первое лицо")]
        [SerializeField] bool firstPerson = true;
        [Tooltip("Высота глаз над центром игрока, м.")]
        [SerializeField] float eyeHeight = 0.6f;   // игрок 1.7 м: центр капсулы на 0.85, глаза чуть ниже макушки
        [Tooltip("Насколько можно повернуть голову за рулём, градусы.")]
        [SerializeField] float driveLookLimit = 120f;

        [Header("Вид сзади")]
        [SerializeField] float distance = 7f;
        [SerializeField] float drivingDistanceMultiplier = 1.6f;
        [SerializeField] float height = 1.5f;

        [Header("Чувствительность")]
        [SerializeField] float mouseSensitivity = 0.15f;
        [SerializeField] float stickSpeed = 140f;

        public bool FirstPerson => firstPerson;

        float yaw, pitch = 15f, currentDistance;
        float driveYaw;                 // поворот головы относительно машины
        bool wasDriving;
        PlayerCarry carry;
        Vector3 holdDefaultPos;
        Renderer[] bodyRenderers;
        bool bodyHidden;
        GUIStyle crossStyle;

        void Start()
        {
            Cursor.lockState = CursorLockMode.Locked;
            currentDistance = distance;
            var cam = GetComponent<Camera>();
            if (cam) cam.nearClipPlane = 0.05f;
            if (player)
            {
                yaw = player.transform.eulerAngles.y;
                carry = player.GetComponent<PlayerCarry>();
                if (carry && carry.HoldPoint) holdDefaultPos = carry.HoldPoint.localPosition;
                // Своё тело от первого лица не рисуем, но тень оставляем.
                bodyRenderers = player.GetComponentsInChildren<Renderer>(true);
            }
            if (firstPerson) pitch = 5f;
            ApplyMode();
        }

        void LateUpdate()
        {
            if (player == null) return;
            if (InGameMenu.IsOpen) { PlaceCamera(); return; }

            if (player.Controls.ToggleView.WasPressedThisFrame())
            {
                firstPerson = !firstPerson;
                pitch = firstPerson ? 5f : 15f;
                ApplyMode();
            }

            Vector2 mouse = player.Controls.LookMouse.ReadValue<Vector2>();
            Vector2 stick = player.Controls.LookStick.ReadValue<Vector2>();
            float dx = mouse.x * mouseSensitivity + stick.x * stickSpeed * Time.deltaTime;
            float dy = mouse.y * mouseSensitivity + stick.y * stickSpeed * Time.deltaTime;

            bool driving = player.InVehicle;
            if (driving != wasDriving)
            {
                if (driving) driveYaw = 0f;                                  // сели — смотрим вперёд по машине
                else yaw = player.transform.eulerAngles.y;                   // вышли — смотрим туда же, куда машина
                wasDriving = driving;
            }

            if (firstPerson && driving)
            {
                driveYaw = Mathf.Clamp(driveYaw + dx, -driveLookLimit, driveLookLimit);
                pitch = Mathf.Clamp(pitch - dy, -60f, 70f);
            }
            else
            {
                yaw += dx;
                pitch = firstPerson ? Mathf.Clamp(pitch - dy, -80f, 80f) : Mathf.Clamp(pitch - dy, -10f, 70f);
            }

            // Пешком от первого лица игрок поворачивается вместе со взглядом —
            // в том же кадре, чтобы деталь в руках не отставала от камеры.
            if (firstPerson && !driving)
                player.transform.rotation = Quaternion.Euler(0f, yaw, 0f);

            PlaceHoldPoint();
            PlaceCamera();
        }

        void PlaceCamera()
        {
            if (firstPerson)
            {
                if (player.InVehicle)
                {
                    Transform seat = player.transform.parent != null ? player.transform.parent : player.transform;
                    Vector3 eye = player.transform.position + seat.up * eyeHeight;
                    transform.SetPositionAndRotation(eye, seat.rotation * Quaternion.Euler(pitch, driveYaw, 0f));
                }
                else
                {
                    Vector3 eye = player.transform.position + Vector3.up * eyeHeight;
                    transform.SetPositionAndRotation(eye, Quaternion.Euler(pitch, yaw, 0f));
                }
                return;
            }

            float wanted = player.InVehicle ? distance * drivingDistanceMultiplier : distance;
            currentDistance = Mathf.Lerp(currentDistance, wanted, Time.deltaTime * 3f);
            Vector3 target = player.transform.position + Vector3.up * height;
            Quaternion rot = Quaternion.Euler(pitch, yaw, 0f);
            transform.SetPositionAndRotation(target - rot * Vector3.forward * currentDistance, rot);
        }

        /// <summary>
        /// От первого лица деталь в руках — перед глазами, чуть ниже, и ходит за взглядом вверх-вниз.
        /// Большие детали держим дальше, чтобы не закрывали весь экран.
        /// </summary>
        void PlaceHoldPoint()
        {
            if (carry == null || carry.HoldPoint == null) return;
            var hp = carry.HoldPoint;
            if (!firstPerson || player.InVehicle)
            {
                // Большое (корпус с деталями) держим подальше, чтобы не проходило сквозь игрока.
                hp.localPosition = holdDefaultPos + Vector3.forward * Mathf.Max(0f, carry.HeldRadius - 0.6f);
                hp.localRotation = Quaternion.identity;
                return;
            }
            float dist = 1.0f + carry.HeldRadius;
            float holdPitch = Mathf.Clamp(pitch, -30f, 50f);
            hp.localPosition = Vector3.up * eyeHeight + Quaternion.Euler(holdPitch, 0f, 0f) * new Vector3(0f, -0.45f, dist);
            hp.localRotation = Quaternion.identity;   // деталь держим ровно: так проще сравнить с «призраком»
        }

        void ApplyMode()
        {
            if (bodyRenderers == null) return;
            bool hide = firstPerson;
            if (hide == bodyHidden) return;
            foreach (var r in bodyRenderers)
                if (r) r.shadowCastingMode = hide ? ShadowCastingMode.ShadowsOnly : ShadowCastingMode.On;
            bodyHidden = hide;
        }

        void OnGUI()
        {
            if (!firstPerson || InGameMenu.IsOpen || player == null) return;
            if (crossStyle == null)
            {
                crossStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 22 };
                crossStyle.normal.textColor = new Color(1f, 1f, 1f, 0.8f);
            }
            GUI.Label(new Rect(Screen.width / 2f - 20f, Screen.height / 2f - 20f, 40f, 40f), "+", crossStyle);
        }
    }
}
