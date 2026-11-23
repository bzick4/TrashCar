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
        UiPainter crossPaint;   // прицел — через UGUI (IMGUI в Linear выбеливал)
        static readonly UiPainter.Style CrossStyle = new UiPainter.Style(22, new Color(1f, 1f, 1f, 0.8f), TextAnchor.MiddleCenter, wrap: false);

        void Start()
        {
            Cursor.lockState = TouchControls.GameLock;
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
            DrawCrosshair();
            if (player == null) return;
            if (Debug.isDebugBuild && UnityEngine.InputSystem.Keyboard.current != null
                && UnityEngine.InputSystem.Keyboard.current.f7Key.wasPressedThisFrame) DebugProbe();
            // Модель человечка ставится позже камеры — раз в полсекунды пересобираем список своих рендеров.
            rescanTimer -= Time.deltaTime;
            if (rescanTimer <= 0f) { rescanTimer = 0.5f; ApplyMode(true); }
            if (InGameMenu.Blocked) { PlaceCamera(); return; }

            if (player.Controls.ToggleView.WasPressedThisFrame())
            {
                firstPerson = !firstPerson;
                pitch = firstPerson ? 5f : 15f;
                ApplyMode();
            }

            // Сенсор: свайп вместо мыши (мышь в браузере телефона приходит эхом от касаний — её не берём).
            // Градусы на пиксель — от высоты экрана, чтобы на любом телефоне свайп на весь экран ≈ пол-оборота.
            Vector2 mouse = TouchControls.TouchMode
                ? TouchControls.LookDelta * (150f / Mathf.Max(200f, Screen.height)) / Mathf.Max(0.0001f, mouseSensitivity)
                : player.Controls.LookMouse.ReadValue<Vector2>();
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
            Vector3 target = player.transform.position + Vector3.up * height;
            Quaternion rot = Quaternion.Euler(pitch, yaw, 0f);
            // Камера не проходит сквозь стены (ущелье, тоннель, скалы): если между игроком и камерой
            // препятствие — сразу встаём перед ним, а когда путь свободен — плавно отъезжаем обратно.
            float allowed = Obstacle(target, -(rot * Vector3.forward), wanted);
            currentDistance = allowed < currentDistance ? allowed : Mathf.Lerp(currentDistance, allowed, Time.deltaTime * 3f);
            transform.SetPositionAndRotation(target - rot * Vector3.forward * currentDistance, rot);
        }

        /// <summary>
        /// Отладка (F7, только редактор и Development Build): что под игроком и что в центре экрана —
        /// имя, путь в иерархии, коллайдер, материалы и текстуры. Пишет в консоль.
        /// </summary>
        void DebugProbe()
        {
            var sb = new System.Text.StringBuilder("[JunkRace] F7 —");
            void Describe(string label, Ray ray)
            {
                sb.Append("\n").Append(label).Append(": ");
                var hits = Physics.RaycastAll(ray, 200f, ~0, QueryTriggerInteraction.Ignore);
                System.Array.Sort(hits, (x, y) => x.distance.CompareTo(y.distance));
                int shown = 0;
                foreach (var h in hits)
                {
                    if (h.collider.transform.IsChildOf(player.transform)) continue;
                    if (shown++ >= 3) break;
                    var t = h.collider.transform;
                    string path = t.name;
                    for (var q = t.parent; q != null; q = q.parent) path = q.name + "/" + path;
                    sb.Append("\n  • ").Append(path).Append("  [").Append(h.collider.GetType().Name).Append("]  точка ").Append(h.point.ToString("F2"));
                    var r = t.GetComponent<Renderer>();
                    if (r == null) sb.Append("  (без рендера)");
                    else foreach (var m in r.sharedMaterials)
                        sb.Append("\n      материал «").Append(m != null ? m.name : "null").Append("» шейдер ")
                          .Append(m != null && m.shader != null ? m.shader.name : "-").Append(" текстура ")
                          .Append(m != null && m.HasProperty("_BaseMap") && m.GetTexture("_BaseMap") != null ? m.GetTexture("_BaseMap").name : "нет")
                          .Append(" цвет ").Append(m != null && m.HasProperty("_BaseColor") ? m.GetColor("_BaseColor").ToString("F2") : m != null ? m.color.ToString("F2") : "-");
                }
                if (shown == 0) sb.Append("ничего");
            }
            Describe("Под ногами", new Ray(player.transform.position + Vector3.up * 0.5f, Vector3.down));
            Describe("В центре экрана", new Ray(transform.position, transform.forward));
            Debug.Log(sb.ToString());
        }

        const float CameraRadius = 0.3f;
        static readonly RaycastHit[] camHits = new RaycastHit[16];

        /// <summary>
        /// Насколько далеко камере можно отъехать от target в направлении dir (не дальше max): сфера-проба,
        /// своя машина, игроки и свободные детали не считаются.
        /// </summary>
        float Obstacle(Vector3 target, Vector3 dir, float max)
        {
            int n = Physics.SphereCastNonAlloc(target, CameraRadius, dir, camHits, max, ~0, QueryTriggerInteraction.Ignore);
            float best = max;
            var mine = player.InVehicle ? player.CurrentVehicle : null;
            for (int i = 0; i < n; i++)
            {
                var h = camHits[i];
                if (h.distance <= 0f) continue;   // проба началась внутри коллайдера (своя машина и т.п.)
                var c = h.collider;
                if (c is CharacterController) continue;
                var rb = c.attachedRigidbody;
                if (rb != null && !rb.isKinematic) continue;                       // свободные детали, машины на физике
                if (mine != null && c.GetComponentInParent<Vehicle>() == mine) continue;   // своя машина (у клиентов она кинематическая)
                if (c.transform.IsChildOf(player.transform)) continue;            // деталь в руках
                best = Mathf.Min(best, h.distance - 0.15f);
            }
            return Mathf.Max(0.6f, best);
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
                hp.localPosition = carry.Held != null ? PlayerCarry.HoldOffset(carry.HeldRadius) : holdDefaultPos;
                hp.localRotation = Quaternion.identity;
                return;
            }
            // От первого лица — тоже у рук, но пониже центра экрана: прицел и дорога видны.
            float dist = PlayerCarry.HandReach + 0.1f + carry.HeldRadius;
            float holdPitch = Mathf.Clamp(pitch, -30f, 50f);
            hp.localPosition = Vector3.up * eyeHeight + Quaternion.Euler(holdPitch, 0f, 0f) * new Vector3(0f, -0.55f, dist);
            hp.localRotation = Quaternion.identity;   // деталь держим ровно: так проще сравнить с «призраком»
        }

        float rescanTimer;

        /// <summary>
        /// От первого лица своё тело не рисуем (только тень) — кроме рук: их видно на руле и с деталью в руках.
        /// Детали в руках — не тело, их не трогаем.
        /// </summary>
        void ApplyMode(bool rescan = false)
        {
            if (player == null) return;
            bool hide = firstPerson;
            if (!rescan && hide == bodyHidden) return;
            bodyRenderers = player.GetComponentsInChildren<Renderer>(true);
            foreach (var r in bodyRenderers)
            {
                if (!r) continue;
                // Деталь в руках (она под игроком) — не тело. Но если сам игрок сидит в машине, над ним тоже
                // есть Part (кресло, корпус) — это не повод его пропускать, иначе тело так и останется скрытым.
                var part = r.GetComponentInParent<Part>();
                if (part != null && part.transform.IsChildOf(player.transform)) continue;
                bool arm = IsArm(r.transform);
                r.shadowCastingMode = hide && !arm ? ShadowCastingMode.ShadowsOnly : ShadowCastingMode.On;
            }
            bodyHidden = hide;
        }

        static bool IsArm(Transform t)
        {
            for (; t != null; t = t.parent)
                if (t.name == "Рука_Л" || t.name == "Рука_П") return true;
            return false;
        }

        void DrawCrosshair()
        {
            crossPaint ??= new UiPainter("Прицел", 12);
            crossPaint.Begin();
            if (firstPerson && !InGameMenu.IsOpen && player != null)
                crossPaint.Label(new Rect(Screen.width / 2f - 20f, Screen.height / 2f - 20f, 40f, 40f), "+", CrossStyle);
            crossPaint.End();
        }

        void OnDestroy() => crossPaint?.Destroy();
    }
}
