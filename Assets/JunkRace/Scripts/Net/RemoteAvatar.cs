using UnityEngine;

namespace JunkRace
{
    /// <summary>
    /// Чужой игрок в твоём мире: капсула с именем.
    /// Позиция приходит по сети и плавно догоняется. На хосте он ещё и держит детали и рулит машиной.
    /// </summary>
    public class RemoteAvatar : MonoBehaviour, IAvatar
    {
        void Awake() => useGUILayout = false;   // OnGUI без GUILayout — без лишнего прохода раскладки


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
            body.name = "Тело";
            Destroy(body.GetComponent<Collider>());
            body.transform.SetParent(go.transform, false);
            body.transform.localScale = new Vector3(0.6f, 0.85f, 0.6f);
            Paint(body, CharacterVisual.PlayerColor(clientId));

            var nose = GameObject.CreatePrimitive(PrimitiveType.Cube);
            nose.name = "Нос";
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
            CharacterVisual.Attach(go.transform, CharacterVisual.PlayerColor(clientId));   // человечек своего цвета — как у него самого
            return avatar;
        }

        public void SetHeld(Part part)
        {
            Held = part;
            // Держит у рук — как у себя самого (PlayerCarry.HoldOffset).
            if (HoldPoint != null)
                HoldPoint.localPosition = part != null ? PlayerCarry.HoldOffset(PlayerCarry.RadiusOf(part)) : new Vector3(0f, 0.3f, 1.5f);
        }

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

        /// <summary>Куда полетел — пришлёт его же клиент позой.</summary>
        public void Fling(Vector3 velocity) { }

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
            var shader = Shader.Find("Toon Shaders Pro/URP/Toon");
            // Этот шейдер умножает на цвет дважды — компенсируем корнем.
            if (shader != null) color = new Color(Mathf.Sqrt(color.r), Mathf.Sqrt(color.g), Mathf.Sqrt(color.b), color.a);
            if (shader == null) shader = Shader.Find("JunkRace/Toon");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            go.GetComponent<Renderer>().material = new Material(shader) { color = color };
        }
    }
}
