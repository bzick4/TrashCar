using UnityEngine;

namespace JunkRace
{
    /// <summary>
    /// Чужой игрок в твоём мире: капсула с именем.
    /// Позиция приходит по сети и плавно догоняется. На хосте он ещё и держит детали и рулит машиной.
    /// </summary>
    public class RemoteAvatar : MonoBehaviour, IAvatar
    {
        static readonly Color[] Colors =
        {
            new Color(0.2f, 0.6f, 1f), new Color(0.3f, 0.9f, 0.4f),
            new Color(0.9f, 0.3f, 0.9f), new Color(1f, 0.9f, 0.2f),
        };

        public ulong ClientId { get; private set; }
        public string DisplayName { get; set; }
        public Transform HoldPoint { get; private set; }
        public Part Held { get; private set; }
        public Vehicle Driving { get; private set; }
        public SeatPart Seat { get; private set; }
        public readonly NetPartInput NetInput = new NetPartInput();
        public IPartInput DriveInput => NetInput;

        Vector3 targetPos;
        float targetYaw;
        bool hasPose;
        GUIStyle labelStyle;

        public static RemoteAvatar Create(ulong clientId, string displayName)
        {
            // Как у локального игрока: 1.7 м, 0.6 м в ширину. Корень не масштабируем — только вид.
            var go = new GameObject($"Remote_{clientId}_{displayName}");
            var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            Destroy(body.GetComponent<Collider>());
            body.transform.SetParent(go.transform, false);
            body.transform.localScale = new Vector3(0.6f, 0.85f, 0.6f);
            Paint(body, Colors[(int)(clientId % (ulong)Colors.Length)]);

            var nose = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Destroy(nose.GetComponent<Collider>());
            nose.transform.SetParent(go.transform, false);
            nose.transform.localPosition = new Vector3(0f, 0.45f, 0.3f);
            nose.transform.localScale = new Vector3(0.22f, 0.15f, 0.22f);
            Paint(nose, Color.black);

            var avatar = go.AddComponent<RemoteAvatar>();
            avatar.ClientId = clientId;
            avatar.DisplayName = displayName;
            avatar.HoldPoint = new GameObject("HoldPoint").transform;
            avatar.HoldPoint.SetParent(go.transform, false);
            avatar.HoldPoint.localPosition = new Vector3(0f, 0.3f, 1.5f);
            go.transform.position = new Vector3(0f, -100f, 0f);   // до первой позиции прячем
            return avatar;
        }

        public void SetHeld(Part part) => Held = part;

        public void SetDriving(Vehicle vehicle)
        {
            Driving = vehicle;
            if (vehicle != null)
            {
                transform.SetParent(vehicle.Seat, false);
                transform.localPosition = Vector3.zero;
                transform.localRotation = Quaternion.identity;
            }
            else
            {
                transform.SetParent(null, true);
                transform.rotation = Quaternion.Euler(0f, transform.eulerAngles.y, 0f);
                NetInput.Throttle = 0f;
                NetInput.Steer = 0f;
                NetInput.Special = false;
            }
        }

        public void SetSeat(SeatPart seat, Vector3 fling)
        {
            Seat = seat;
            if (seat != null)
            {
                transform.SetParent(seat.SitPoint, false);
                transform.localPosition = Vector3.zero;
                transform.localRotation = Quaternion.identity;
            }
            else
            {
                // Куда он вылетел, сообщит его же клиент позой — здесь просто отцепляемся.
                transform.SetParent(null, true);
                transform.rotation = Quaternion.Euler(0f, transform.eulerAngles.y, 0f);
            }
        }

        public void SetPose(Vector3 position, float yaw)
        {
            targetPos = position;
            targetYaw = yaw;
            if (!hasPose && Driving == null && Seat == null)
            {
                transform.SetPositionAndRotation(position, Quaternion.Euler(0f, yaw, 0f));
                hasPose = true;
            }
        }

        void Update()
        {
            if (Driving != null || Seat != null || !hasPose) return;
            float k = 1f - Mathf.Exp(-15f * Time.unscaledDeltaTime);
            if ((transform.position - targetPos).sqrMagnitude > 100f) transform.position = targetPos;
            else transform.position = Vector3.Lerp(transform.position, targetPos, k);
            transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.Euler(0f, targetYaw, 0f), k);
        }

        void OnGUI()
        {
            var cam = Camera.main;
            if (cam == null || InGameMenu.IsOpen) return;
            Vector3 screen = cam.WorldToScreenPoint(transform.position + Vector3.up * 1.2f);
            if (screen.z <= 0f) return;
            labelStyle ??= new GUIStyle(GUI.skin.label) { fontSize = 18, alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
            GUI.Label(new Rect(screen.x - 100f, Screen.height - screen.y - 12f, 200f, 24f), DisplayName, labelStyle);
        }

        static void Paint(GameObject go, Color color)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            go.GetComponent<Renderer>().material = new Material(shader) { color = color };
        }
    }
}
