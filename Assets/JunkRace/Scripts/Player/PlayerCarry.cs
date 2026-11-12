using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace JunkRace
{
    /// <summary>
    /// Подобрать / бросить / прикрутить деталь (E).
    /// Пока держишь деталь: на свободных местах машин рядом висят зелёные маркеры с подписью слота,
    /// а на ближайшем месте — зелёный «призрак» детали ровно в той позе, в которой она встанет.
    /// Здесь только выбор цели и подсказки; само действие делает World (с проверкой на хосте).
    /// </summary>
    [RequireComponent(typeof(PlayerController))]
    public class PlayerCarry : MonoBehaviour
    {
        [SerializeField] Transform holdPoint;
        [SerializeField] float grabRadius = 1.5f;
        [Tooltip("Насколько близко держимая деталь должна быть к месту, чтобы прикрутить.")]
        [SerializeField] float snapRadius = 1.5f;
        [SerializeField] float throwSpeed = 4f;
        [Tooltip("В каком радиусе от игрока подсвечивать свободные места на машинах, м.")]
        [SerializeField] float showPointsRadius = 12f;

        [Header("Первое лицо: всё по прицелу")]
        [Tooltip("Как далеко можно взять деталь взглядом, м.")]
        [SerializeField] float grabReach = 3.5f;
        [Tooltip("Как далеко можно прикрутить деталь взглядом, м.")]
        [SerializeField] float aimReach = 5f;
        [Tooltip("Насколько можно промахнуться прицелом мимо места, м (растёт с расстоянием).")]
        [SerializeField] float aimTolerance = 0.5f;

        static readonly Color FreeColor = new Color(0.25f, 1f, 0.35f, 0.3f);
        static readonly Color TargetColor = new Color(0.25f, 1f, 0.35f, 0.7f);
        static readonly Color GhostColor = new Color(0.35f, 1f, 0.45f, 0.4f);

        /// <summary>Что держим. Выставляет World через PlayerController.SetHeld.</summary>
        public Part Held { get; internal set; }
        public Transform HoldPoint => holdPoint;
        /// <summary>Деталь, которую можно взять прямо сейчас (на неё смотрит карточка).</summary>
        public Part LookTarget { get; private set; }
        /// <summary>Место, куда встанет деталь по E.</summary>
        public AttachPoint SnapTarget => snapTarget;

        /// <summary>Примерный радиус того, что в руках (0 — руки пустые). Камера держит большое подальше.</summary>
        public float HeldRadius
        {
            get
            {
                if (Held == null) return 0f;
                if (heldRadiusFor != Held)
                {
                    heldRadiusFor = Held;
                    heldRadius = 0.3f;
                    bool any = false;
                    Bounds b = default;
                    foreach (var r in Held.GetComponentsInChildren<Renderer>())
                    {
                        if (!any) { b = r.bounds; any = true; }
                        else b.Encapsulate(r.bounds);
                    }
                    if (any) heldRadius = b.extents.magnitude;
                }
                return heldRadius;
            }
        }

        PlayerController player;
        Part heldRadiusFor;
        float heldRadius;
        static readonly RaycastHit[] lookHits = new RaycastHit[32];
        AttachPoint snapTarget;

        // Свободные места рядом и маркеры к ним (пул, без лишних аллокаций).
        readonly List<AttachPoint> shownPoints = new List<AttachPoint>();
        readonly List<GameObject> markers = new List<GameObject>();
        readonly List<MeshRenderer> markerRenderers = new List<MeshRenderer>();
        Mesh sphereMesh;
        Material freeMat, targetMat, ghostMat;

        // Призрак: копии мешей держимой детали. Саму деталь не клонируем:
        // у неё AttachPoint'ы, которые регистрируются в AttachPoint.All.
        Transform ghost;
        Part ghostFor;

        Camera cam;
        GUIStyle labelStyle, shadowStyle, centerStyle;

        // ---- Поиск в кучах мусора ----
        [Header("Поиск в кучах")]
        [Tooltip("Как далеко можно выбрать кучу прицелом, м.")]
        [SerializeField] float pileAimReach = 4.5f;

        /// <summary>Куча, в которой можно порыться прямо сейчас.</summary>
        public JunkPile LookPile { get; private set; }
        public bool IsSearching => searchPile != null;

        JunkPile searchPile;
        float searchTime, searchDuration, waitTimer;
        Vector3 searchStart;
        bool waitingResult;
        string resultText;
        bool resultGood;
        float resultUntil;

        void Awake()
        {
            player = GetComponent<PlayerController>();
            if (holdPoint == null)
            {
                holdPoint = new GameObject("HoldPoint").transform;
                holdPoint.SetParent(transform, false);
                holdPoint.localPosition = new Vector3(0f, 1.0f, 1.4f);
            }

            var tmp = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            sphereMesh = tmp.GetComponent<MeshFilter>().sharedMesh;
            DestroyImmediate(tmp);

            freeMat = MakeTransparent(FreeColor);
            targetMat = MakeTransparent(TargetColor);
            ghostMat = MakeTransparent(GhostColor);
        }

        static Material MakeTransparent(Color color)
        {
            // Проект на URP: Sprites/Default рисуется прозрачным и без света — то, что нужно для подсказок.
            var shader = Shader.Find("Sprites/Default");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");
            return new Material(shader) { color = color };
        }

        void OnEnable() => World.SearchFinished += OnSearchFinished;
        void OnDisable() => World.SearchFinished -= OnSearchFinished;

        void OnDestroy()
        {
            foreach (var m in markers) if (m) Destroy(m);
            if (ghost) Destroy(ghost.gameObject);
            if (freeMat) Destroy(freeMat);
            if (targetMat) Destroy(targetMat);
            if (ghostMat) Destroy(ghostMat);
        }

        void Update()
        {
            if (InGameMenu.IsOpen) return;
            if (player.InVehicle)
            {
                if (searchPile != null && !waitingResult) CancelSearch(null);
                LookTarget = null;
                LookPile = null;
                snapTarget = null;
                shownPoints.Clear();
                UpdateMarkers();
                UpdateGhost();
                return;
            }

            LookTarget = Held == null ? FindGrabbable() : null;
            LookPile = Held == null && LookTarget == null ? FindPile() : null;
            CollectFreePoints();
            snapTarget = Held != null ? FindSnapTarget() : null;
            UpdateMarkers();
            UpdateGhost();
            if (searchPile != null) UpdateSearch();

            if (!player.Controls.Grab.WasPressedThisFrame()) return;

            if (searchPile != null)
            {
                if (!waitingResult) CancelSearch("Поиск отменён");
                return;
            }

            if (Held == null && LookTarget == null && LookPile != null)
            {
                StartSearch(LookPile);
                return;
            }

            if (Held != null)
            {
                Vector3 throwVelocity = snapTarget != null ? Vector3.zero : transform.forward * throwSpeed + Vector3.up * 2f;
                World.RequestRelease(snapTarget, throwVelocity);
            }
            else
            {
                if (LookTarget != null) World.RequestGrab(LookTarget);
            }
        }

        /// <summary>Луч прицела (центр экрана) — только от первого лица.</summary>
        bool TryGetAimRay(out Ray ray)
        {
            ray = default;
            if (!player.FirstPersonView) return false;
            if (cam == null) cam = Camera.main;
            if (cam == null) return false;
            ray = new Ray(cam.transform.position, cam.transform.forward);
            return true;
        }

        Part FindGrabbable()
        {
            if (TryGetAimRay(out var ray)) return FindLookedAt(ray);

            Part bestPart = null, bestHull = null;
            float partDist = float.MaxValue, hullDist = float.MaxValue;
            foreach (var col in Physics.OverlapSphere(holdPoint.position, grabRadius, ~0, QueryTriggerInteraction.Ignore))
            {
                var part = col.GetComponentInParent<Part>();
                if (part == null || !part.CanBeCarried) continue;
                float d = Vector3.Distance(holdPoint.position, col.ClosestPoint(holdPoint.position));
                if (IsHull(part)) { if (d < hullDist) { hullDist = d; bestHull = part; } }
                else if (d < partDist) { partDist = d; bestPart = part; }
            }
            return PreferPart(bestPart, partDist, bestHull, hullDist, 0.5f);
        }

        // ============================ Поиск в кучах ============================

        /// <summary>Куча под прицелом (от первого лица) или ближайшая рядом (вид сзади).</summary>
        JunkPile FindPile()
        {
            if (TryGetAimRay(out var ray))
            {
                int n = Physics.RaycastNonAlloc(ray, lookHits, pileAimReach, ~0, QueryTriggerInteraction.Ignore);
                JunkPile best = null;
                float bestDist = float.MaxValue;
                for (int i = 0; i < n; i++)
                {
                    var h = lookHits[i];
                    if (h.collider == null || h.distance >= bestDist) continue;
                    var pile = h.collider.GetComponentInParent<JunkPile>();
                    if (pile == null || !pile.InReach(transform.position)) continue;
                    best = pile;
                    bestDist = h.distance;
                }
                return best;
            }

            JunkPile nearest = null;
            float nearestSq = float.MaxValue;
            foreach (var pile in JunkPile.All)
            {
                if (pile == null || !pile.InReach(transform.position)) continue;
                float d = (pile.transform.position - transform.position).sqrMagnitude;
                if (d < nearestSq) { nearestSq = d; nearest = pile; }
            }
            return nearest;
        }

        void StartSearch(JunkPile pile)
        {
            searchPile = pile;
            searchTime = 0f;
            searchDuration = Random.Range(pile.SearchTimeMin, pile.SearchTimeMax);
            searchStart = transform.position;
            waitingResult = false;
            resultUntil = 0f;
        }

        void UpdateSearch()
        {
            if (waitingResult)
            {
                // Ответ хоста потерялся — не висим вечно.
                waitTimer += Time.deltaTime;
                if (waitTimer > 3f) { searchPile = null; waitingResult = false; ShowResult("Ничего не нашлось", false); }
                return;
            }

            // Рыться надо стоя у кучи: отошёл — поиск прерван.
            Vector3 moved = transform.position - searchStart;
            moved.y = 0f;
            if (moved.sqrMagnitude > 0.6f * 0.6f || !searchPile.InReach(transform.position) || Held != null)
            {
                CancelSearch("Отошёл — поиск прерван");
                return;
            }

            searchTime += Time.deltaTime;
            if (searchTime >= searchDuration)
            {
                waitingResult = true;   // до вызова: офлайн и на хосте ответ придёт прямо внутри RequestSearch
                waitTimer = 0f;
                World.RequestSearch(searchPile);
            }
        }

        void CancelSearch(string message)
        {
            searchPile = null;
            waitingResult = false;
            if (message != null) ShowResult(message, false);
        }

        void OnSearchFinished(ulong id, JunkPile pile, Part found)
        {
            if (id != World.LocalId) return;   // это рылся кто-то другой
            searchPile = null;
            waitingResult = false;
            if (found != null) ShowResult($"Нашёл: «{found.DisplayName}»!", true);
            else if (pile != null && pile.Remaining == 0) ShowResult("Пусто — тут всё выгребли", false);
            else ShowResult("Ничего полезного. Порыться ещё?", false);
        }

        void ShowResult(string text, bool good)
        {
            resultText = text;
            resultGood = good;
            resultUntil = Time.unscaledTime + 2.2f;
        }

        /// <summary>Деталь, на которую смотрит прицел: ближайшая по лучу, которую можно взять.</summary>
        Part FindLookedAt(Ray ray)
        {
            Part bestPart = null, bestHull = null;
            float partDist = float.MaxValue, hullDist = float.MaxValue;
            int n = Physics.SphereCastNonAlloc(ray, 0.2f, lookHits, grabReach, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                var h = lookHits[i];
                if (h.collider == null) continue;
                var part = h.collider.GetComponentInParent<Part>();
                if (part == null || !part.CanBeCarried) continue;
                if (IsHull(part)) { if (h.distance < hullDist) { hullDist = h.distance; bestHull = part; } }
                else if (h.distance < partDist) { partDist = h.distance; bestPart = part; }
            }
            return PreferPart(bestPart, partDist, bestHull, hullDist, 1.2f);
        }

        static bool IsHull(Part part) => part.GetComponent<Vehicle>() != null;

        /// <summary>
        /// Деталь важнее корпуса: кресло внутри ванны прицел видит через борт, и без этого правила
        /// выбиралась вся ванна. Корпус — только если деталей не задели или они далеко за ним.
        /// </summary>
        static Part PreferPart(Part part, float partDist, Part hull, float hullDist, float behindLimit)
        {
            if (part == null) return hull;
            if (hull != null && partDist > hullDist + behindLimit) return hull;
            return part;
        }

        /// <summary>Все свободные места на машинах в радиусе, куда можно прикрутить то, что держим.</summary>
        void CollectFreePoints()
        {
            shownPoints.Clear();
            if (Held == null) return;
            Vector3 me = transform.position;
            float r2 = showPointsRadius * showPointsRadius;
            foreach (var p in AttachPoint.All)
            {
                if (p == null || p.Owner == null) continue;
                var v = p.Owner.Vehicle;
                if (v == null || v == Held.Vehicle) continue;   // свои же точки (если несём корпус)
                if ((p.transform.position - me).sqrMagnitude > r2) continue;
                if (!v.CanAttach(Held, p)) continue;
                shownPoints.Add(p);
            }
        }

        AttachPoint FindSnapTarget()
        {
            if (TryGetAimRay(out var ray)) return FindAimedPoint(ray);

            AttachPoint best = null;
            float bestDist = snapRadius;
            Vector3 pos = Held.transform.position;
            foreach (var p in shownPoints)
            {
                float d = Vector3.Distance(pos, p.transform.position);
                if (d < bestDist) { bestDist = d; best = p; }
            }
            return best;
        }

        /// <summary>Место, ближе всего к линии прицела (с допуском, который растёт с расстоянием).</summary>
        AttachPoint FindAimedPoint(Ray ray)
        {
            AttachPoint best = null;
            float bestOff = float.MaxValue;
            foreach (var p in shownPoints)
            {
                Vector3 to = p.transform.position - ray.origin;
                float along = Vector3.Dot(to, ray.direction);
                if (along < 0.2f || along > aimReach) continue;
                float off = (to - ray.direction * along).magnitude;
                if (off > aimTolerance + along * 0.08f) continue;
                if (off < bestOff) { bestOff = off; best = p; }
            }
            return best;
        }

        // ============================ Маркеры ============================

        void UpdateMarkers()
        {
            int i = 0;
            for (; i < shownPoints.Count; i++)
            {
                var p = shownPoints[i];
                if (i >= markers.Count) AddMarker();
                bool isTarget = p == snapTarget;
                var t = markers[i].transform;
                t.SetPositionAndRotation(p.transform.position, p.transform.rotation);
                t.localScale = Vector3.one * (isTarget ? 0.3f : 0.22f);
                markerRenderers[i].sharedMaterial = isTarget ? targetMat : freeMat;
                if (!markers[i].activeSelf) markers[i].SetActive(true);
            }
            for (; i < markers.Count; i++)
                if (markers[i].activeSelf) markers[i].SetActive(false);
        }

        void AddMarker()
        {
            var go = new GameObject("SlotMarker");
            go.AddComponent<MeshFilter>().sharedMesh = sphereMesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = freeMat;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            markers.Add(go);
            markerRenderers.Add(r);
        }

        // ============================ Призрак ============================

        void UpdateGhost()
        {
            if (snapTarget == null || Held == null)
            {
                if (ghost != null && ghost.gameObject.activeSelf) ghost.gameObject.SetActive(false);
                return;
            }
            if (ghost == null || ghostFor != Held) BuildGhost(Held);

            // Ровно как встанет деталь: localPosition = 0, localRotation = identity относительно точки.
            var pt = snapTarget.transform;
            ghost.SetPositionAndRotation(pt.position, pt.rotation);
            ghost.localScale = Vector3.Scale(pt.lossyScale, Held.transform.localScale);
            if (!ghost.gameObject.activeSelf) ghost.gameObject.SetActive(true);
        }

        void BuildGhost(Part part)
        {
            if (ghost == null) ghost = new GameObject("AttachGhost").transform;
            for (int i = ghost.childCount - 1; i >= 0; i--)
            {
                var child = ghost.GetChild(i).gameObject;
                child.SetActive(false);
                Destroy(child);
            }

            // Позы мешей относительно самой детали.
            Matrix4x4 toPart = part.transform.worldToLocalMatrix;
            foreach (var mf in part.GetComponentsInChildren<MeshFilter>())
            {
                if (mf.sharedMesh == null) continue;
                if (!mf.TryGetComponent(out MeshRenderer mr) || !mr.enabled) continue;

                Matrix4x4 m = toPart * mf.transform.localToWorldMatrix;
                var go = new GameObject("Ghost_" + mf.name);
                go.transform.SetParent(ghost, false);
                go.transform.localPosition = m.GetColumn(3);
                go.transform.localRotation = m.rotation;
                go.transform.localScale = m.lossyScale;
                go.AddComponent<MeshFilter>().sharedMesh = mf.sharedMesh;
                var r = go.AddComponent<MeshRenderer>();
                r.sharedMaterial = ghostMat;
                r.shadowCastingMode = ShadowCastingMode.Off;
                r.receiveShadows = false;
            }
            ghostFor = part;
        }

        // ============================ Подписи и подсказка ============================

        void OnGUI()
        {
            if (player.InVehicle || InGameMenu.IsOpen) return;
            DrawPointLabels();
            DrawSearch();

            string hint = searchPile != null ? "E — бросить поиск"
                : Held == null && LookTarget == null && LookPile != null ? $"E — порыться в «{LookPile.DisplayName}»   F — сесть в машину"
                : Held == null
                ? (LookTarget is SeatPart chair && chair.CanSit
                    ? $"F — сесть в «{chair.DisplayName}»   E — снять (переставить)"
                    : LookTarget != null && LookTarget.MountedOn != null && LookTarget.Vehicle != null
                    ? $"E — снять «{LookTarget.DisplayName}» с «{LookTarget.Vehicle.RootPart.DisplayName}» (переставить)   F — сесть в машину"
                    : "E — взять деталь   F — сесть в машину   V — вид")
                : snapTarget != null ? $"E — прикрутить «{Held.DisplayName}» → {snapTarget.Label} ({AttachPoint.SlotName(snapTarget.Slot)})"
                : shownPoints.Count > 0 ? $"E — бросить «{Held.DisplayName}»   ({(player.FirstPersonView ? "наведи прицел на зелёное место" : "поднеси к зелёному месту")}, чтобы прикрутить)"
                : $"E — бросить «{Held.DisplayName}»";
            GUI.Label(new Rect(20, Screen.height - 40, 1200, 30), hint);
        }

        /// <summary>Полоска поиска под прицелом и надпись «нашёл / пусто».</summary>
        void DrawSearch()
        {
            EnsureStyles();
            float cx = Screen.width / 2f, y = Screen.height / 2f + 50f;

            if (searchPile != null)
            {
                float k = waitingResult ? 1f : Mathf.Clamp01(searchTime / Mathf.Max(searchDuration, 0.01f));
                centerStyle.normal.textColor = Color.white;
                GUI.Label(new Rect(cx - 200f, y - 30f, 400f, 26f), $"Роюсь в «{searchPile.DisplayName}»…", centerStyle);
                var back = new Rect(cx - 150f, y, 300f, 14f);
                var old = GUI.color;
                GUI.color = new Color(0f, 0f, 0f, 0.6f);
                GUI.DrawTexture(back, Texture2D.whiteTexture);
                GUI.color = new Color(1f, 0.75f, 0.2f, 0.95f);
                GUI.DrawTexture(new Rect(back.x + 2f, back.y + 2f, (back.width - 4f) * k, back.height - 4f), Texture2D.whiteTexture);
                GUI.color = old;
            }
            else if (Time.unscaledTime < resultUntil && !string.IsNullOrEmpty(resultText))
            {
                centerStyle.normal.textColor = resultGood ? new Color(0.5f, 1f, 0.55f) : new Color(1f, 0.85f, 0.6f);
                GUI.Label(new Rect(cx - 250f, y - 10f, 500f, 30f), resultText, centerStyle);
            }
        }

        void DrawPointLabels()
        {
            if (Held == null || shownPoints.Count == 0) return;
            if (cam == null) cam = Camera.main;
            if (cam == null) return;
            EnsureStyles();

            foreach (var p in shownPoints)
            {
                if (p == null) continue;
                Vector3 s = cam.WorldToScreenPoint(p.transform.position + Vector3.up * 0.3f);
                if (s.z <= 0f) continue;

                bool isTarget = p == snapTarget;
                string slot = AttachPoint.SlotName(p.Slot);
                if (p.Steers) slot += " (поворот)";
                string text = isTarget ? $"{p.Label} — {slot}" : slot;

                var rect = new Rect(s.x - 120f, Screen.height - s.y - 22f, 240f, 22f);
                labelStyle.fontStyle = isTarget ? FontStyle.Bold : FontStyle.Normal;
                shadowStyle.fontStyle = labelStyle.fontStyle;
                GUI.Label(new Rect(rect.x + 1f, rect.y + 1f, rect.width, rect.height), text, shadowStyle);
                GUI.Label(rect, text, labelStyle);
            }
        }

        void EnsureStyles()
        {
            if (labelStyle != null) return;
            labelStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 15 };
            labelStyle.normal.textColor = new Color(0.55f, 1f, 0.6f);
            centerStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 20, fontStyle = FontStyle.Bold };
            shadowStyle = new GUIStyle(labelStyle);
            shadowStyle.normal.textColor = new Color(0f, 0f, 0f, 0.8f);
        }
    }
}
