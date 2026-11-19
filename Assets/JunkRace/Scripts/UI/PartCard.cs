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

        // Арт панелей (Resources/UI/hud). Нет картинок — рисуем по-старому, полупрозрачной коробкой.
        Texture2D cardPanel, cardTitle, cardTag, cardDivider, statsPanel, barBg, barFill;
        readonly Dictionary<string, Texture2D> icons = new Dictionary<string, Texture2D>();
        GUIStyle cardPanelStyle, statsPanelStyle, plateText, tagText, paperText, paperTitle, paperValue, rowLabel, rowValue;
        bool HasArt => cardPanel != null && statsPanel != null;

        /// <summary>Строка характеристики: иконка, подпись, значение (с цветом), стрелка лучше/хуже, шкала 0..1 (или нет).</summary>
        struct StatRow { public string Icon, Label, Value; public int Arrow; public float Bar; }
        readonly List<StatRow> rows = new List<StatRow>();
        readonly StringBuilder footer = new StringBuilder();
        /// <summary>Пишем на светлой бумаге (Tab): цвета потемнее, иначе на бежевом не читаются.</summary>
        bool onPaper;
        string CUp => onPaper ? "#2E7D1E" : Up;
        string CDown => onPaper ? "#B3261E" : Down;
        string CSame => onPaper ? "#5B4A3A" : Same;
        string CMeh => onPaper ? "#9A6A00" : Meh;

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
                vehicles.AddRange(Vehicle.All);
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
                GUI.Label(new Rect(20f, 64f, 400f, 30f), "<color=#C8C2B8>Tab — характеристики машины</color>", text);   // слева: справа карточка детали
            }

            GUI.matrix = Matrix4x4.identity;
        }

        // ============================ Карточка детали ============================

        void DrawPartCard(float screenW, Part part)
        {
            sb.Clear(); rows.Clear(); footer.Clear();
            onPaper = false;
            sb.Append("<color=#8FE39A>Ставится как: ").Append(part.FitsAs()).Append("</color>\n");
            if (!string.IsNullOrEmpty(part.Description))
                sb.Append("<i>").Append(part.Description).Append("</i>\n");

            lines.Clear();
            part.DescribeStats(lines);
            if (lines.Count > 0) sb.Append('\n');
            foreach (var l in lines) sb.Append("• ").Append(l).Append('\n');

            var hull = part.GetComponent<Vehicle>();
            if (hull != null)
            {
                sb.Append("\n<b>Как корпус машины:</b>");
                AppendEstimate(VehicleStats.Estimate(hull));
            }
            else if (part.MountedOn != null && part.Vehicle != null)
            {
                // Уже прикручена: что она даёт сейчас и что будет, если снять.
                var v = part.Vehicle;
                sb.Append("\n<b>Стоит на «").Append(v.RootPart.DisplayName).Append("» → ").Append(part.MountedOn.Label).Append(":</b>\n");
                AppendSlotEffect(part, part.MountedOn.Slot, part.MountedOn.Steers);
                sb.Append("<b>Если снять:</b>");
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
                    footer.Append("<color=#C8C2B8>Подойди к корпусу, чтобы сравнить</color>");
                }
            }
            string body = sb.ToString().TrimEnd('\n');

            if (!HasArt)
            {
                string all = "<size=20><color=#C8C2B8>" + part.CategoryName + "</color></size>\n" + body + "\n" + RowsAsText() + footer;
                float width0 = 430f;
                float height0 = 70f + text.CalcHeight(new GUIContent(all), width0 - 36f);
                var r0 = new Rect(screenW - width0 - 20f, 110f, width0, height0);
                GUI.Box(r0, GUIContent.none, box);
                GUI.Label(new Rect(r0.x + 18f, r0.y + 12f, width0 - 36f, 40f), part.DisplayName, title);
                GUI.Label(new Rect(r0.x + 18f, r0.y + 56f, width0 - 36f, height0 - 60f), all, text);
                return;
            }

            // ---- С артом: ржавая панель, жёлтая плашка с названием, оранжевый ярлык категории ----
            const float W = 470f, Pad = 44f;
            float cw = W - Pad * 2f;
            float tagH = 46f, bodyH = text.CalcHeight(new GUIContent(body), cw);
            float rowsH = RowsHeight(), footH = footer.Length > 0 ? text.CalcHeight(new GUIContent(footer.ToString()), cw) + 6f : 0f;
            float divH = rows.Count > 0 ? 26f : 0f;
            float h = 74f + tagH + 8f + bodyH + divH + rowsH + footH + 44f;
            var rect = new Rect(screenW - W - 24f, 104f, W, Mathf.Min(h, 960f));

            NineSlice(rect, cardPanelStyle, 0.55f);
            float plateW = W + 16f, plateH = plateW * cardTitle.height / cardTitle.width;
            var plate = new Rect(rect.x - 8f, rect.y - plateH * 0.35f, plateW, plateH);
            GUI.DrawTexture(plate, cardTitle);
            GUI.Label(new Rect(plate.x + 40f, plate.y, plate.width - 80f, plate.height), part.DisplayName, plateText);

            float y = plate.yMax + 4f;
            float tagW = tagH * cardTag.width / cardTag.height;
            var tag = new Rect(rect.x + Pad - 6f, y, tagW, tagH);
            GUI.DrawTexture(tag, cardTag);
            GUI.Label(new Rect(tag.x + tagH * 0.9f, tag.y, tag.width - tagH * 1.3f, tag.height), part.CategoryName, tagText);
            y += tagH + 8f;

            GUI.Label(new Rect(rect.x + Pad, y, cw, bodyH), body, text);
            y += bodyH;
            if (rows.Count > 0)
            {
                GUI.DrawTexture(new Rect(rect.x + Pad - 10f, y + 8f, cw + 20f, 12f), cardDivider);
                y += divH;
                y = DrawRows(rect.x + Pad, y, cw, rowLabel, rowValue);
            }
            if (footH > 0f) GUI.Label(new Rect(rect.x + Pad, y + 4f, cw, footH), footer.ToString(), text);
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
                case SlotType.Side:
                    if (part is ArmorPart armor) { color = Up; what = $"прикрывает машину (+{armor.Protection * 100f:0}%)"; }
                    else if (part.NativeSlot == SlotType.Engine && part.GivesPowerIn(slot))
                    { color = Up; what = $"работает как мотор ({part.AsEnginePower / 735.5f:0.0} л.с.)"; }
                    else if (part.WorksInBody) { color = Up; what = "работает"; }
                    else { color = Down; what = "здесь не работает, просто груз"; }
                    break;
                default:
                    if (part is ArmorPart) { color = Meh; what = "просто груз: защищает только на борту"; }
                    else if (part.NativeSlot == SlotType.Engine && part.GivesPowerIn(slot))
                    { color = Up; what = $"работает как мотор ({part.AsEnginePower / 735.5f:0.0} л.с.)"; }
                    else if (part.WorksInBody) { color = Up; what = "работает"; }
                    else { color = Down; what = "здесь не работает, просто груз"; }
                    break;
            }
            sb.Append("В слоте «").Append(AttachPoint.SlotName(slot)).Append("»: <color=").Append(color).Append('>')
              .Append(what).Append("</color>\n");
        }

        void AppendComparison(VehicleEstimate before, VehicleEstimate after)
        {
            Row("speed", "Макс. скорость", before.TopSpeedKmh, after.TopSpeedKmh, "км/ч", true, "0");
            Row("accel", "Разгон", before.Accel, after.Accel, "м/с²", true, "0.0");
            Row("weight", "Вес", before.Mass, after.Mass, "кг", false, "0");
            if (Mathf.Abs(before.SteerQuality - after.SteerQuality) > 0.001f)
                Row("steering", "Руль", before.SteerQuality * 100f, after.SteerQuality * 100f, "%", true, "0");
            if (Mathf.Abs(before.Protection - after.Protection) > 0.001f)
                Row("protection", "Защита", before.Protection * 100f, after.Protection * 100f, "%", true, "0");
            if (before.Wheels != after.Wheels)
                Row("wheel", "Колёс", before.Wheels, after.Wheels, "", true, "0");
            if (before.Floats != after.Floats) Flag(after.Floats ? "floats" : "sinks", "Плавает", before.Floats, after.Floats);
            if (!after.CanDrive) footer.Append("<color=").Append(CDown).Append(">Ехать всё ещё нечем: нужен мотор и колёса или вентилятор</color>");
        }

        void Row(string icon, string label, float before, float after, string unit, bool higherIsBetter, string fmt)
        {
            float diff = after - before;
            bool same = Mathf.Abs(diff) < 0.05f;
            string color = same ? CSame : (diff > 0f) == higherIsBetter ? CUp : CDown;
            rows.Add(new StatRow
            {
                Icon = icon, Label = label, Bar = -1f,
                Value = before.ToString(fmt) + " → <color=" + color + "><b>" + after.ToString(fmt) + "</b>" + (unit.Length > 0 ? " " + unit : "") + "</color>",
                Arrow = same ? 0 : (diff > 0f) == higherIsBetter ? 1 : -1,
            });
        }

        void Flag(string icon, string label, bool before, bool after)
        {
            rows.Add(new StatRow
            {
                Icon = icon, Label = label, Bar = -1f, Arrow = after ? 1 : -1,
                Value = (before ? "да" : "нет") + " → <color=" + (after ? CUp : CDown) + "><b>" + (after ? "да" : "нет") + "</b></color>",
            });
        }

        void Stat(string icon, string label, string value, float bar = -1f) =>
            rows.Add(new StatRow { Icon = icon, Label = label, Value = value, Bar = bar });

        /// <summary>Характеристики машины строками со шкалами. Шкалы — от «типичного хлама» до «очень бодро».</summary>
        void AppendEstimate(VehicleEstimate e)
        {
            if (e.CanDrive)
            {
                Stat("speed", "Макс. скорость", "~" + e.TopSpeedKmh.ToString("0") + " км/ч", e.TopSpeedKmh / 90f);
                Stat("accel", "Разгон", e.Accel.ToString("0.0") + " м/с²", e.Accel / 8f);
            }
            else footer.Append("<color=").Append(CDown).Append(">Не едет: нужен мотор и колёса или вентилятор</color>");
            Stat("weight", "Вес", e.Mass.ToString("0") + " кг", e.Mass / 300f);
            Stat("power", "Мощность", e.PowerHp.ToString("0.0") + " л.с.", e.PowerHp / 12f);
            Stat("wheel", "Колёс", e.Wheels.ToString());
            Stat("steering", "Руль", !e.CanSteer ? "<color=" + CDown + ">нет</color>"
                : e.SteerQuality >= 0.999f ? "есть" : "<color=" + CMeh + ">туго " + (e.SteerQuality * 100f).ToString("0") + "%</color>",
                e.CanSteer ? e.SteerQuality : 0f);
            if (e.Protection > 0f)
                Stat("protection", "Защита бортами", "+" + (e.Protection * 100f).ToString("0") + "%", e.Protection / ArmorPart.MaxProtection);
            Stat(e.Floats ? "floats" : "sinks", "В воде", e.Floats ? "плавает" : "<color=" + CDown + ">тонет</color>");
        }

        string RowsAsText()
        {
            var t = new StringBuilder();
            foreach (var r in rows) t.Append(r.Label).Append(": ").Append(r.Value).Append('\n');
            return t.ToString();
        }

        const float RowH = 38f, BarH = 16f;

        float RowsHeight()
        {
            float h = 0f;
            foreach (var r in rows) h += RowH + (r.Bar >= 0f ? BarH + 4f : 0f);
            return h;
        }

        /// <summary>Строки: иконка слева, подпись, значение справа, стрелка лучше/хуже; под строкой — шкала.</summary>
        float DrawRows(float x, float y, float width, GUIStyle label, GUIStyle value)
        {
            const float Icon = 32f;
            foreach (var r in rows)
            {
                if (r.Icon != null && TryIcon(r.Icon, out var ic)) GUI.DrawTexture(new Rect(x, y + (RowH - Icon) / 2f, Icon, Icon), ic, ScaleMode.ScaleToFit);
                float arrowW = r.Arrow != 0 ? 26f : 0f;
                GUI.Label(new Rect(x + Icon + 8f, y, width * 0.48f, RowH), r.Label, label);
                GUI.Label(new Rect(x + Icon + 8f, y, width - Icon - 8f - arrowW, RowH), r.Value, value);
                if (r.Arrow != 0 && TryIcon(r.Arrow > 0 ? "up" : "down", out var ar))
                    GUI.DrawTexture(new Rect(x + width - 24f, y + (RowH - 24f) / 2f, 24f, 24f), ar, ScaleMode.ScaleToFit);
                y += RowH;
                if (r.Bar >= 0f)
                {
                    var trough = new Rect(x + Icon + 8f, y - 2f, width - Icon - 8f, BarH);
                    if (barBg != null) GUI.DrawTexture(trough, barBg);
                    float p = Mathf.Clamp01(r.Bar);
                    if (barFill != null && p > 0.01f)
                    {
                        // Заливка внутри жёлоба (по картинке — ~6% поля слева/справа и ~30% сверху/снизу).
                        var inner = new Rect(trough.x + trough.width * 0.055f, trough.y + trough.height * 0.3f, trough.width * 0.89f, trough.height * 0.4f);
                        GUI.DrawTextureWithTexCoords(new Rect(inner.x, inner.y, inner.width * p, inner.height), barFill, new Rect(0f, 0f, p, 1f));
                    }
                    y += BarH + 4f;
                }
            }
            return y;
        }

        bool TryIcon(string name, out Texture2D tex)
        {
            if (!icons.TryGetValue(name, out tex))
            {
                tex = Resources.Load<Texture2D>("UI/hud/icon_stat_" + name);
                icons[name] = tex;
            }
            return tex != null;
        }

        /// <summary>9-slice панель в уменьшенном масштабе: края картинки не раздуваются на маленькой панели.</summary>
        static void NineSlice(Rect r, GUIStyle style, float scale)
        {
            var m = GUI.matrix;
            GUI.matrix = m * Matrix4x4.Scale(new Vector3(scale, scale, 1f));
            GUI.Box(new Rect(r.x / scale, r.y / scale, r.width / scale, r.height / scale), GUIContent.none, style);
            GUI.matrix = m;
        }

        // ============================ Панель машины ============================

        void DrawVehiclePanel(Vehicle v)
        {
            sb.Clear(); rows.Clear(); footer.Clear();
            onPaper = HasArt;
            string header;
            if (v == null)
            {
                header = "Машина";
                footer.Append("Рядом нет машины. Подойди к корпусу.");
            }
            else
            {
                header = v.RootPart.DisplayName + (player.IsDriving ? "  <size=18>(ты за рулём)</size>" : player.IsSeated ? "  <size=18>(ты пассажир)</size>" : "");
                AppendEstimate(VehicleStats.Estimate(v));
                sb.Append("<b>Детали (").Append(v.PartCount).Append("):</b> ");
                bool first = true;
                foreach (var p in v.Parts) { if (!first) sb.Append(", "); sb.Append(p.DisplayName); first = false; }
            }

            if (!HasArt)
            {
                string body0 = RowsAsText() + footer + "\n" + sb;
                float width0 = 420f;
                float height0 = 70f + text.CalcHeight(new GUIContent(body0), width0 - 36f);
                var r0 = new Rect(20f, 110f, width0, Mathf.Min(height0, 900f));
                GUI.Box(r0, GUIContent.none, box);
                GUI.Label(new Rect(r0.x + 18f, r0.y + 12f, width0 - 36f, 40f), header, title);
                GUI.Label(new Rect(r0.x + 18f, r0.y + 56f, width0 - 36f, r0.height - 60f), body0, text);
                return;
            }

            // ---- Планшет механика: бумага, тёмный текст, характеристики шкалами ----
            const float W = 470f, PadX = 58f, Top = 120f, Bottom = 78f, S = 0.5f;
            float cw = W - PadX * 2f;
            float headH = paperTitle.CalcHeight(new GUIContent(header), cw);
            string parts = sb.ToString(), foot = footer.ToString();
            float footH = foot.Length > 0 ? paperText.CalcHeight(new GUIContent(foot), cw) + 6f : 0f;
            float partsH = parts.Length > 0 ? paperText.CalcHeight(new GUIContent(parts), cw) + 10f : 0f;
            float h = Top + headH + 8f + RowsHeight() + footH + partsH + Bottom;
            var rect = new Rect(20f, 96f, W, Mathf.Min(h, 960f));
            NineSlice(rect, statsPanelStyle, S);

            float x = rect.x + PadX, y = rect.y + Top;
            GUI.Label(new Rect(x, y, cw, headH), header, paperTitle);
            y += headH + 8f;
            y = DrawRows(x, y, cw, paperText, paperValue);
            if (footH > 0f) { GUI.Label(new Rect(x, y + 4f, cw, footH), foot, paperText); y += footH; }
            if (partsH > 0f)
            {
                if (TryIcon("parts", out var wrench)) GUI.DrawTexture(new Rect(x, y + 6f, 26f, 26f), wrench, ScaleMode.ScaleToFit);
                GUI.Label(new Rect(x + 32f, y + 6f, cw - 32f, partsH), parts, paperText);
            }
        }

        GUIStyle speedStyle;

        void DrawSpeedometer(float screenW)
        {
            var v = player.CurrentVehicle;
            if (v == null) return;
            float kmh = Mathf.Abs(v.ForwardSpeed) * 3.6f;
            speedStyle ??= new GUIStyle(title) { fontSize = 64, alignment = TextAnchor.LowerRight };
            var style = speedStyle;
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
            text = new GUIStyle(GUI.skin.label) { fontSize = 21, wordWrap = true, richText = true };
            text.normal.textColor = new Color(0.95f, 0.92f, 0.86f);

            cardPanel = Resources.Load<Texture2D>("UI/hud/card_panel");
            cardTitle = Resources.Load<Texture2D>("UI/hud/card_title");
            cardTag = Resources.Load<Texture2D>("UI/hud/card_tag");
            cardDivider = Resources.Load<Texture2D>("UI/hud/card_divider");
            statsPanel = Resources.Load<Texture2D>("UI/hud/stats_panel");
            barBg = Resources.Load<Texture2D>("UI/hud/stat_bar");
            barFill = Resources.Load<Texture2D>("UI/hud/stat_fill");
            if (cardTitle == null || cardTag == null || cardDivider == null) cardPanel = null;   // без полного набора — старый вид

            // Рамки по краям картинок (в пикселях картинки): середина растягивается, края — нет.
            cardPanelStyle = new GUIStyle { border = new RectOffset(70, 70, 70, 70) };
            cardPanelStyle.normal.background = cardPanel;
            statsPanelStyle = new GUIStyle { border = new RectOffset(95, 100, 220, 145) };
            statsPanelStyle.normal.background = statsPanel;

            Color ink = new Color(0.24f, 0.15f, 0.08f);
            plateText = new GUIStyle(GUI.skin.label) { fontSize = 28, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, richText = true, clipping = TextClipping.Clip };
            plateText.normal.textColor = ink;
            tagText = new GUIStyle(GUI.skin.label) { fontSize = 19, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            tagText.normal.textColor = ink;
            rowLabel = new GUIStyle(GUI.skin.label) { fontSize = 20, alignment = TextAnchor.MiddleLeft, richText = true };
            rowLabel.normal.textColor = new Color(0.82f, 0.78f, 0.7f);
            rowValue = new GUIStyle(rowLabel) { alignment = TextAnchor.MiddleRight };
            rowValue.normal.textColor = new Color(0.97f, 0.94f, 0.88f);
            paperTitle = new GUIStyle(GUI.skin.label) { fontSize = 27, fontStyle = FontStyle.Bold, wordWrap = true, richText = true };
            paperTitle.normal.textColor = ink;
            paperText = new GUIStyle(GUI.skin.label) { fontSize = 19, wordWrap = true, richText = true, alignment = TextAnchor.MiddleLeft };
            paperText.normal.textColor = new Color(0.3f, 0.22f, 0.15f);
            paperValue = new GUIStyle(paperText) { alignment = TextAnchor.MiddleRight, wordWrap = false };
            paperValue.normal.textColor = ink;
        }

        void OnDestroy()
        {
            if (boxTex) Destroy(boxTex);
        }
    }
}
