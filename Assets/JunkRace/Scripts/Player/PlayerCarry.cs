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
                    heldRadius = RadiusOf(Held);
                }
                return heldRadius;
            }
        }

        /// <summary>Как далеко от рук держать деталь: ладони (~0.5 м перед грудью) у ближнего края детали.</summary>
        public const float HandReach = 0.5f;

        /// <summary>Примерный радиус детали по её виду.</summary>
        public static float RadiusOf(Part part)
        {
            if (part == null) return 0f;
            bool any = false;
            Bounds b = default;
            foreach (var r in part.GetComponentsInChildren<Renderer>())
            {
                if (!r.enabled) continue;
                if (!any) { b = r.bounds; any = true; }
                else b.Encapsulate(r.bounds);
            }
            // Диагональ рамки завышает (угол коробки дальше края) — берём чуть меньше.
            return any ? b.extents.magnitude * 0.85f : 0.3f;
        }

        /// <summary>Где держать деталь (в координатах игрока): у рук, большое — дальше, чтобы не проходило сквозь.</summary>
        public static Vector3 HoldOffset(float radius) => new Vector3(0f, 0.3f, HandReach + radius);

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
        // Пул кусков призрака: при смене детали не создаём/удаляем объекты, а переиспользуем.
        readonly List<MeshFilter> ghostPieces = new List<MeshFilter>();

        Camera cam;
        // Подписи рисуем через UiPainter (UGUI): IMGUI в Linear выбеливал цвета.
        UiPainter paint;
        UiPainter.Style labelStyle, shadowStyle, centerStyle, hintStyle;
        bool stylesReady;

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
            paint?.Destroy();
            foreach (var m in markers) if (m) Destroy(m);
            if (ghost) Destroy(ghost.gameObject);
            if (freeMat) Destroy(freeMat);
            if (targetMat) Destroy(targetMat);
            if (ghostMat) Destroy(ghostMat);
        }

        void Update()
        {
            if (InGameMenu.Blocked || RaceTimer.IsLoading) return;
            if (player.InVehicle || RaceTimer.InteractionLocked)
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
                if (LookTarget != null && World.MayUse(World.LocalId, LookTarget.Vehicle)) World.RequestGrab(LookTarget);
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
                if (part == null || !part.CanBeCarried || !Tutorial.AllowGrab(part)) continue;
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
                    if (pile == null || !pile.InReach(transform.position) || !Tutorial.AllowSearch(pile)) continue;
                    best = pile;
                    bestDist = h.distance;
                }
                return best;
            }

            JunkPile nearest = null;
            float nearestSq = float.MaxValue;
            foreach (var pile in JunkPile.All)
            {
                if (pile == null || !pile.InReach(transform.position) || !Tutorial.AllowSearch(pile)) continue;
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
                if (part == null || !part.CanBeCarried || !Tutorial.AllowGrab(part)) continue;
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
                if (!v.MayUse(World.LocalId)) continue;          // чужая машина — её строит другой
                if ((p.transform.position - me).sqrMagnitude > r2) continue;
                if (!v.CanAttach(Held, p) || !Tutorial.AllowAttach(Held, p)) continue;
                shownPoints.Add(p);
            }
            // Подсвечиваем только места, где деталь работает. Alt — все (прикрутить как груз).
            showingAll = UnityEngine.InputSystem.Keyboard.current != null && UnityEngine.InputSystem.Keyboard.current.leftAltKey.isPressed;
            hiddenNotFitting = 0;
            if (!showingAll)
            {
                int fit = 0;
                foreach (var p in shownPoints) if (Part.Fits(Held, p.Slot)) fit++;
                if (fit > 0)
                {
                    hiddenNotFitting = shownPoints.Count - fit;
                    shownPoints.RemoveAll(p => !Part.Fits(Held, p.Slot));
                }
            }
        }

        bool showingAll;
        int hiddenNotFitting;

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
            ghost.SetPositionAndRotation(pt.position, pt.rotation * Held.MountRotation(snapTarget));
            ghost.localScale = Vector3.Scale(pt.lossyScale, Held.transform.localScale);
            if (!ghost.gameObject.activeSelf) ghost.gameObject.SetActive(true);
        }

        void BuildGhost(Part part)
        {
            if (ghost == null) ghost = new GameObject("AttachGhost").transform;
            int used = 0;

            // Позы мешей относительно самой детали.
            Matrix4x4 toPart = part.transform.worldToLocalMatrix;
            foreach (var mf in part.GetComponentsInChildren<MeshFilter>())
            {
                if (mf.sharedMesh == null) continue;
                if (!mf.TryGetComponent(out MeshRenderer mr) || !mr.enabled) continue;

                Matrix4x4 m = toPart * mf.transform.localToWorldMatrix;
                var piece = GhostPiece(used++);
                var t = piece.transform;
                t.localPosition = m.GetColumn(3);
                t.localRotation = m.rotation;
                t.localScale = m.lossyScale;
                piece.sharedMesh = mf.sharedMesh;
                if (!piece.gameObject.activeSelf) piece.gameObject.SetActive(true);
            }
            for (int i = used; i < ghostPieces.Count; i++)
                if (ghostPieces[i].gameObject.activeSelf) ghostPieces[i].gameObject.SetActive(false);
            ghostFor = part;
        }

        MeshFilter GhostPiece(int index)
        {
            if (index < ghostPieces.Count) return ghostPieces[index];
            var go = new GameObject("GhostPiece");
            go.transform.SetParent(ghost, false);
            var mf = go.AddComponent<MeshFilter>();
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = ghostMat;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            ghostPieces.Add(mf);
            return mf;
        }

        // ============================ Подписи и подсказка ============================

        void LateUpdate()
        {
            paint ??= new UiPainter("Подсказки", 15);
            paint.Begin();
            if (!player.InVehicle && !InGameMenu.IsOpen && !RaceTimer.InteractionLocked) DrawLabels();
            paint.End();
        }

        void DrawLabels()
        {
            EnsureStyles();
            DrawPointLabels();
            DrawBuildGuide();
            DrawPileLabels();
            DrawSearch();

            string hint = searchPile != null ? "E — бросить поиск"
                : Held == null && LookTarget == null && LookPile != null ? $"E — порыться в «{LookPile.DisplayName}»   F — сесть в машину"
                : Held == null && LookTarget != null && !World.MayUse(World.LocalId, LookTarget.Vehicle)
                ? (LookTarget is SeatPart other && other.CanSit
                    ? $"F — сесть пассажиром в «{other.DisplayName}»   (машина игрока {World.OwnerName(LookTarget.Vehicle)})"
                    : $"Это машина игрока {World.OwnerName(LookTarget.Vehicle)} — её строит он. Найди свой корпус.")
                : Held == null
                ? (LookTarget is SeatPart chair && chair.CanSit
                    ? $"F — сесть в «{chair.DisplayName}»   E — снять (переставить)"
                    : LookTarget != null && LookTarget.MountedOn != null && LookTarget.Vehicle != null
                    ? $"E — снять «{LookTarget.DisplayName}» с «{LookTarget.Vehicle.RootPart.DisplayName}» (переставить)   F — сесть в машину"
                    : LookTarget != null && LookTarget.GetComponent<HullFlip>() != null
                    ? $"E — взять «{LookTarget.DisplayName}»   R — перевернуть   F — сесть в машину"
                    : "E — взять деталь   F — сесть в машину   V — вид")
                : Held.GetComponent<HullFlip>() != null && snapTarget == null ? $"E — бросить «{Held.DisplayName}»   R — перевернуть"
                : snapTarget != null ? $"E — прикрутить «{Held.DisplayName}» → {snapTarget.Label} ({AttachPoint.SlotName(snapTarget.Slot)})"
                : shownPoints.Count > 0 ? $"E — бросить «{Held.DisplayName}»   ({(player.FirstPersonView ? "наведи прицел на зелёное место" : "поднеси к зелёному месту")}, чтобы прикрутить)"
                : $"E — бросить «{Held.DisplayName}»";
            if (Held != null && hiddenNotFitting > 0) hint += "   Alt — все места (прикрутить как груз)";
            if (!TouchControls.TouchMode) paint.Label(new Rect(20, Screen.height - 40, 1200, 30), hint, hintStyle);
        }

        /// <summary>Полоска поиска под прицелом и надпись «нашёл / пусто».</summary>
        void DrawSearch()
        {
            EnsureStyles();
            float cx = Screen.width / 2f, y = Screen.height / 2f + 50f;

            if (searchPile != null)
            {
                float k = waitingResult ? 1f : Mathf.Clamp01(searchTime / Mathf.Max(searchDuration, 0.01f));
                paint.Label(new Rect(cx - 200f, y - 30f, 400f, 26f), $"Роюсь в «{searchPile.DisplayName}»…", centerStyle.With(Color.white));
                var back = new Rect(cx - 150f, y, 300f, 14f);
                paint.Box(back, new Color(0f, 0f, 0f, 0.6f));
                paint.Box(new Rect(back.x + 2f, back.y + 2f, (back.width - 4f) * k, back.height - 4f), new Color(1f, 0.75f, 0.2f, 0.95f));
            }
            else if (Time.unscaledTime < resultUntil && !string.IsNullOrEmpty(resultText))
            {
                paint.Label(new Rect(cx - 250f, y - 10f, 500f, 30f), resultText,
                            centerStyle.With(resultGood ? new Color(0.5f, 1f, 0.55f) : new Color(1f, 0.85f, 0.6f)));
            }
            else if (LookPile != null && Held == null)
            {
                // Можно рыться — говорим прямо у прицела, а не только мелкой строкой внизу.
                paint.Label(new Rect(cx - 200f, y - 10f, 400f, 30f), TouchControls.TouchMode ? "Порыться" : "E — порыться", centerStyle.With(new Color(1f, 0.85f, 0.35f)));
            }
        }

        const float PileLabelDistance = 10f;

        /// <summary>Подписи над кучами рядом: что это за куча и пустая ли. Та, в которой можно рыться, — ярче.</summary>
        void DrawPileLabels()
        {
            if (Held != null || JunkPile.All.Count == 0) return;
            if (cam == null) cam = Camera.main;
            if (cam == null) return;
            EnsureStyles();

            foreach (var pile in JunkPile.All)
            {
                if (pile == null) continue;
                Vector3 world = pile.LabelPosition;
                float dist = Vector3.Distance(transform.position, world);
                if (dist > PileLabelDistance) continue;
                Vector3 s = cam.WorldToScreenPoint(world);
                if (s.z <= 0f) continue;

                bool active = pile == LookPile;
                bool empty = pile.Remaining == 0;
                string text = active ? (TouchControls.TouchMode ? $"Порыться в «{pile.DisplayName}»" : $"[E] Порыться в «{pile.DisplayName}»")
                    : empty ? $"{pile.DisplayName} (пусто)"
                    : pile.InReach(transform.position) && player.FirstPersonView ? $"{pile.DisplayName} — наведи прицел"
                    : pile.DisplayName;

                // Дальние подписи бледнее.
                float alpha = Mathf.Lerp(1f, 0.35f, Mathf.InverseLerp(3f, PileLabelDistance, dist));
                Color color = active ? new Color(1f, 0.85f, 0.35f) : empty ? new Color(0.75f, 0.75f, 0.75f) : Color.white;
                color.a = alpha;

                var rect = new Rect(s.x - 160f, Screen.height - s.y - 14f, 320f, 28f);
                paint.Label(new Rect(rect.x + 1f, rect.y + 1f, rect.width, rect.height), text,
                            shadowStyle.WithBold(active).With(new Color(0f, 0f, 0f, 0.8f * alpha)));
                paint.Label(rect, text, labelStyle.WithBold(active).With(color));
            }
        }

        static readonly UiPainter.Style GuideStyle = new UiPainter.Style(18, Color.white, TextAnchor.MiddleCenter, true, false);

        /// <summary>
        /// Сборка: куда нести. Нет своей машины — метки над свободными корпусами («возьми корпус»).
        /// Есть — метка «Твоя машина» над ней, а если она за кадром — стрелка у края экрана.
        /// </summary>
        void DrawBuildGuide()
        {
            var timer = RaceTimer.Instance;
            if (timer == null || timer.Phase != MatchPhase.Build) return;
            if (cam == null) cam = Camera.main;
            if (cam == null) return;

            var own = RaceTimer.VehicleOf(World.LocalId);
            Vector3 me = transform.position;
            if (own == null)
            {
                // Ближайшие свободные корпуса (не больше пяти, чтобы не рябило).
                var hulls = new List<Vehicle>();
                foreach (var v in Vehicle.All)
                {
                    if (v == null || v.IsOwned || v.RootPart == null || v.RootPart.IsCarried) continue;
                    if (Held != null && Held.Vehicle == v) continue;
                    hulls.Add(v);
                }
                hulls.Sort((a, b) => (a.transform.position - me).sqrMagnitude.CompareTo((b.transform.position - me).sqrMagnitude));
                for (int i = 0; i < hulls.Count && i < 5; i++)
                    GuideMarker(hulls[i], $"▼ Корпус «{hulls[i].RootPart.DisplayName}»", new Color(1f, 0.85f, 0.3f), false);
            }
            else if ((own.transform.position - me).sqrMagnitude > 16f || Held != null)
                GuideMarker(own, "▼ Твоя машина", new Color(0.45f, 1f, 0.5f), true);
        }

        void GuideMarker(Vehicle v, string text, Color color, bool edgeArrow)
        {
            Vector3 top = v.transform.position + Vector3.up * 1.6f;
            foreach (var r in v.RootPart.GetComponentsInChildren<Renderer>())
                top.y = Mathf.Max(top.y, r.bounds.max.y + 0.5f);
            Vector3 s = cam.WorldToScreenPoint(top);
            float dist = Vector3.Distance(transform.position, v.transform.position);
            string label = $"{text}  {dist:0} м";
            bool onScreen = s.z > 0f && s.x > 0f && s.x < Screen.width && s.y > 0f && s.y < Screen.height;
            if (!onScreen)
            {
                if (!edgeArrow) return;
                // За кадром: прижимаем к краю экрана в сторону машины.
                if (s.z < 0f) { s.x = Screen.width - s.x; s.y = Screen.height - s.y; }
                Vector2 c = new Vector2(Screen.width / 2f, Screen.height / 2f);
                Vector2 d = new Vector2(s.x, s.y) - c;
                if (d.sqrMagnitude < 1f) d = Vector2.up;
                float k = Mathf.Min((Screen.width / 2f - 140f) / Mathf.Max(Mathf.Abs(d.x), 1f), (Screen.height / 2f - 60f) / Mathf.Max(Mathf.Abs(d.y), 1f));
                s = new Vector3(c.x + d.x * k, c.y + d.y * k, 1f);
                label = (d.x < 0f ? "◀ " : "") + text.TrimStart('▼', ' ') + $"  {dist:0} м" + (d.x >= 0f ? " ▶" : "");
            }
            var rect = new Rect(s.x - 160f, Screen.height - s.y - 14f, 320f, 28f);
            paint.Label(new Rect(rect.x + 1.5f, rect.y + 1.5f, rect.width, rect.height), label, GuideStyle.With(new Color(0f, 0f, 0f, 0.85f)));
            paint.Label(rect, label, GuideStyle.With(color));
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
                paint.Label(new Rect(rect.x + 1f, rect.y + 1f, rect.width, rect.height), text, shadowStyle.WithBold(isTarget));
                paint.Label(rect, text, labelStyle.WithBold(isTarget));
            }
        }

        void EnsureStyles()
        {
            if (stylesReady) return;
            stylesReady = true;
            labelStyle = new UiPainter.Style(15, new Color(0.55f, 1f, 0.6f), TextAnchor.MiddleCenter, wrap: false);
            centerStyle = new UiPainter.Style(20, Color.white, TextAnchor.MiddleCenter, true, false);
            shadowStyle = labelStyle.With(new Color(0f, 0f, 0f, 0.8f));
            hintStyle = new UiPainter.Style(16, new Color(0.92f, 0.92f, 0.92f), TextAnchor.UpperLeft, wrap: false);
        }
    }
}
