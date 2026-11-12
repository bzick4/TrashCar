using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace JunkRace
{
    /// <summary>
    /// Мини-меню деталей и машины:
    ///   • смотришь на деталь или держишь её — карточка справа: что это и как изменится машина (было → станет);
    ///   • Tab / крестовина вверх — характеристики машины, рядом с которой стоишь или которой рулишь;
    ///   • за рулём — спидометр.
    /// Всё считается локально по VehicleStats, по сети ничего не ходит.
    /// </summary>
    [RequireComponent(typeof(PlayerController), typeof(PlayerCarry))]
    public class PartCard : MonoBehaviour
    {
        const float NearbyRadius = 10f;
        const string Up = "#7CFC7C", Down = "#FF7A6B", Same = "#C8C2B8", Meh = "#FFD25A";

        PlayerController player;
        PlayerCarry carry;
        bool showVehicle;
        readonly List<Vehicle> vehicles = new List<Vehicle>();
        float nextVehicleScan;
        readonly List<string> lines = new List<string>();
        readonly StringBuilder sb = new StringBuilder();

        GUIStyle box, title, text;
        Texture2D boxTex;

        void Awake()
        {
            player = GetComponent<PlayerController>();
            carry = GetComponent<PlayerCarry>();
        }

        void Update()
        {
            if (InGameMenu.IsOpen) return;
            if (player.Controls.Stats.WasPressedThisFrame()) showVehicle = !showVehicle;

            if (Time.unscaledTime >= nextVehicleScan)
            {
                nextVehicleScan = Time.unscaledTime + 1f;
                vehicles.Clear();
                vehicles.AddRange(FindObjectsByType<Vehicle>(FindObjectsSortMode.None));
            }
        }

        // ============================ Что показывать ============================

        Vehicle NearestVehicle(Part exclude)
        {
            Vehicle best = null;
            float bestDist = NearbyRadius;
            foreach (var v in vehicles)
            {
                if (v == null || (exclude != null && v.RootPart == exclude)) continue;
                float d = Vector3.Distance(transform.position, v.transform.position);
                if (d < bestDist) { bestDist = d; best = v; }
            }
            return best;
        }

        void OnGUI()
        {
            if (InGameMenu.IsOpen) return;
            EnsureStyles();

            float scale = Screen.height / 1080f;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
            float w = Screen.width / scale;

            if (player.InVehicle) DrawSpeedometer(w);

            Part candidate = carry.Held != null ? carry.Held : carry.LookTarget;
            if (candidate != null) DrawPartCard(w, candidate);

            if (showVehicle)
            {
                var v = player.InVehicle ? player.CurrentVehicle : NearestVehicle(null);
                DrawVehiclePanel(v);
            }
            else
            {
                GUI.Label(new Rect(w - 360f, 70f, 340f, 30f), "<color=#C8C2B8>Tab — характеристики машины</color>", text);
            }

            GUI.matrix = Matrix4x4.identity;
        }

        // ============================ Карточка детали ============================

        void DrawPartCard(float screenW, Part part)
        {
            sb.Clear();
            sb.Append("<size=20><color=#C8C2B8>").Append(part.CategoryName).Append("</color></size>\n");
            if (!string.IsNullOrEmpty(part.Description))
                sb.Append("<i>").Append(part.Description).Append("</i>\n");
            sb.Append('\n');

            lines.Clear();
            part.DescribeStats(lines);
            foreach (var l in lines) sb.Append("• ").Append(l).Append('\n');

            var hull = part.GetComponent<Vehicle>();
            if (hull != null)
            {
                sb.Append("\n<b>Как корпус машины:</b>\n");
                AppendEstimate(VehicleStats.Estimate(hull));
            }
            else if (part.MountedOn != null && part.Vehicle != null)
            {
                // Уже прикручена: что она даёт сейчас и что будет, если снять.
                var v = part.Vehicle;
                sb.Append("\n<b>Стоит на «").Append(v.RootPart.DisplayName).Append("» → ").Append(part.MountedOn.Label).Append(":</b>\n");
                AppendSlotEffect(part, part.MountedOn.Slot, part.MountedOn.Steers);
                sb.Append("\n<b>Если снять:</b>\n");
                AppendComparison(VehicleStats.Estimate(v), VehicleStats.EstimateWithout(v, part));
            }
            else
            {
                // Куда прикрутим: в точку под прицелом, иначе к ближайшей машине в родной слот детали.
                AttachPoint point = carry.SnapTarget != null && carry.SnapTarget.Owner != null && carry.SnapTarget.Owner.Vehicle != null
                    ? carry.SnapTarget : null;
                Vehicle target = point != null ? point.Owner.Vehicle : NearestVehicle(part);
                if (target != null)
                {
                    sb.Append("\n<b>Если прикрутить к «").Append(target.RootPart.DisplayName).Append('»');
                    if (point != null) sb.Append(" → ").Append(point.Label);
                    sb.Append(":</b>\n");
                    AppendSlotEffect(part, point != null ? point.Slot : part.NativeSlot, point != null && point.Steers);
                    AppendComparison(VehicleStats.Estimate(target), VehicleStats.EstimateWith(target, part, point));
                }
                else
                {
                    sb.Append("\n<color=#C8C2B8>Подойди к корпусу, чтобы сравнить</color>");
                }
            }

            string body = sb.ToString();
            float width = 430f;
            float height = 70f + text.CalcHeight(new GUIContent(body), width - 36f);
            var rect = new Rect(screenW - width - 20f, 110f, width, height);
            GUI.Box(rect, GUIContent.none, box);
            GUI.Label(new Rect(rect.x + 18f, rect.y + 12f, width - 36f, 40f), part.DisplayName, title);
            GUI.Label(new Rect(rect.x + 18f, rect.y + 56f, width - 36f, height - 60f), body, text);
        }

        /// <summary>Строка «В слоте «Руль»: колесо будет рулить туго (60%)» — как деталь сработает в этом месте.</summary>
        void AppendSlotEffect(Part part, SlotType slot, bool steers)
        {
            string name = part.DisplayName;
            string color, what;
            switch (slot)
            {
                case SlotType.Wheel:
                    if (part.NativeSlot == SlotType.Wheel)
                    { color = Up; what = steers ? "работает как колесо, поворачивает рулём" : "работает как колесо"; }
                    else
                    { color = Meh; what = $"«{name}» будет колесом, но плохим: скребёт по земле"; }
                    break;
                case SlotType.Steering:
                {
                    float q = part.AsSteering;
                    if (q >= 0.999f) { color = Up; what = "рулит"; }
                    else if (q > 0f) { color = Meh; what = $"«{name}» будет рулить туго ({q * 100f:0}%)"; }
                    else { color = Down; what = "здесь не работает"; }
                    break;
                }
                case SlotType.Engine:
                {
                    float hp = part.AsEnginePower / 735.5f;
                    if (part.NativeSlot == SlotType.Engine) { color = Up; what = $"работает как мотор ({hp:0.0} л.с.)"; }
                    else if (hp > 0f) { color = Meh; what = $"«{name}» будет крутить колёса слабо ({hp:0.0} л.с.)"; }
                    else { color = Down; what = "здесь не работает"; }
                    break;
                }
                default:
                    if (part.WorksInBody) { color = Up; what = "работает"; }
                    else { color = Down; what = "здесь не работает, просто груз"; }
                    break;
            }
            sb.Append("В слоте «").Append(AttachPoint.SlotName(slot)).Append("»: <color=").Append(color).Append('>')
              .Append(what).Append("</color>\n");
        }

        void AppendComparison(VehicleEstimate before, VehicleEstimate after)
        {
            Row("Макс. скорость", before.TopSpeedKmh, after.TopSpeedKmh, "км/ч", true, "0");
            Row("Разгон", before.Accel, after.Accel, "м/с²", true, "0.0");
            Row("Вес", before.Mass, after.Mass, "кг", false, "0");
            if (Mathf.Abs(before.SteerQuality - after.SteerQuality) > 0.001f)
                Row("Руль", before.SteerQuality * 100f, after.SteerQuality * 100f, "%", true, "0");
            if (before.Wheels != after.Wheels)
                sb.Append("Колёс: ").Append(before.Wheels).Append(" → ").Append(after.Wheels).Append('\n');
            if (before.Floats != after.Floats) Flag("Плавает", before.Floats, after.Floats);
            if (!after.CanDrive) sb.Append("<color=").Append(Down).Append(">Ехать всё ещё нечем: нужен мотор и колёса или вентилятор</color>\n");
        }

        void Row(string label, float before, float after, string unit, bool higherIsBetter, string fmt)
        {
            float diff = after - before;
            string color = Mathf.Abs(diff) < 0.05f ? Same : (diff > 0f) == higherIsBetter ? Up : Down;
            string arrow = Mathf.Abs(diff) < 0.05f ? "" : diff > 0f ? " ▲" : " ▼";
            sb.Append(label).Append(": ").Append(before.ToString(fmt)).Append(" → <color=").Append(color).Append('>')
              .Append(after.ToString(fmt)).Append(' ').Append(unit).Append(arrow).Append("</color>\n");
        }

        void Flag(string label, bool before, bool after)
        {
            string color = after ? Up : Down;
            sb.Append(label).Append(": ").Append(before ? "да" : "нет").Append(" → <color=").Append(color).Append('>')
              .Append(after ? "да" : "нет").Append("</color>\n");
        }

        void AppendEstimate(VehicleEstimate e)
        {
            if (e.CanDrive)
            {
                sb.Append("Макс. скорость: ~").Append(e.TopSpeedKmh.ToString("0")).Append(" км/ч\n");
                sb.Append("Разгон: ").Append(e.Accel.ToString("0.0")).Append(" м/с²\n");
            }
            else sb.Append("<color=").Append(Down).Append(">Не едет: нужен мотор и колёса или вентилятор</color>\n");
            sb.Append("Вес: ").Append(e.Mass.ToString("0")).Append(" кг\n");
            sb.Append("Мощность: ").Append(e.PowerHp.ToString("0.0")).Append(" л.с.   Колёс: ").Append(e.Wheels).Append('\n');
            sb.Append("Руль: ").Append(!e.CanSteer ? "<color=" + Down + ">нет — колёса не поворачивают</color>"
                : e.SteerQuality >= 0.999f ? "есть"
                : "<color=" + Meh + ">туго (" + (e.SteerQuality * 100f).ToString("0") + "%)</color>").Append('\n');
            sb.Append("В воде: ").Append(e.Floats ? "плавает" : "<color=" + Down + ">тонет</color>").Append('\n');
        }

        // ============================ Панель машины ============================

        void DrawVehiclePanel(Vehicle v)
        {
            sb.Clear();
            string header;
            if (v == null)
            {
                header = "Машина";
                sb.Append("<color=#C8C2B8>Рядом нет машины. Подойди к корпусу.</color>");
            }
            else
            {
                header = v.RootPart.DisplayName + (player.IsDriving ? "  (ты за рулём)" : player.IsSeated ? "  (ты пассажир)" : "");
                AppendEstimate(VehicleStats.Estimate(v));
                sb.Append("\n<b>Детали (").Append(v.PartCount).Append("):</b>\n");
                foreach (var p in v.Parts) sb.Append("• ").Append(p.DisplayName).Append('\n');
            }

            string body = sb.ToString();
            float width = 420f;
            float height = 70f + text.CalcHeight(new GUIContent(body), width - 36f);
            var rect = new Rect(20f, 110f, width, Mathf.Min(height, 900f));
            GUI.Box(rect, GUIContent.none, box);
            GUI.Label(new Rect(rect.x + 18f, rect.y + 12f, width - 36f, 40f), header, title);
            GUI.Label(new Rect(rect.x + 18f, rect.y + 56f, width - 36f, rect.height - 60f), body, text);
        }

        void DrawSpeedometer(float screenW)
        {
            var v = player.CurrentVehicle;
            if (v == null) return;
            float kmh = Mathf.Abs(v.ForwardSpeed) * 3.6f;
            var style = new GUIStyle(title) { fontSize = 64, alignment = TextAnchor.LowerRight };
            GUI.Label(new Rect(screenW - 420f, 1080f - 150f, 400f, 90f), $"{kmh:0} <size=28>км/ч</size>", style);
        }

        // ============================ Стили ============================

        void EnsureStyles()
        {
            if (box != null) return;
            boxTex = new Texture2D(1, 1);
            boxTex.SetPixel(0, 0, new Color(0.12f, 0.11f, 0.10f, 0.88f));
            boxTex.Apply();

            box = new GUIStyle(GUI.skin.box);
            box.normal.background = boxTex;
            title = new GUIStyle(GUI.skin.label) { fontSize = 30, fontStyle = FontStyle.Bold, richText = true };
            title.normal.textColor = new Color(1f, 0.62f, 0.15f);
            text = new GUIStyle(GUI.skin.label) { fontSize = 22, wordWrap = true, richText = true };
            text.normal.textColor = Color.white;
        }

        void OnDestroy()
        {
            if (boxTex) Destroy(boxTex);
        }
    }
}
