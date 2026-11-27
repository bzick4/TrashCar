using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace JunkRace
{
    /// <summary>
    /// Генератор трассы: выезд со свалки → биомы (лес, горы) в случайном порядке → финиш.
    /// Каждый биом — несколько кусков и обязательная развилка: две ветки разного характера,
    /// которые потом сходятся. Трасса каждый раз новая, но у всех игроков одна:
    /// хост выбирает сид и рассылает его, клиенты строят по нему то же самое.
    ///
    /// Мир открытый (ограждений нет), но далеко от дороги не уехать — машину вернёт на трассу.
    /// </summary>
    public class RouteGenerator : MonoBehaviour
    {
        public static RouteGenerator Instance { get; private set; }

        /// <summary>Слои декораций: камера отсекает их вдали (мелочь раньше, деревья позже).</summary>
        public const string SmallDecorLayer = "JR_DecorSmall", TreeLayer = "JR_DecorTrees";
        const float SmallDecorDistance = 70f, TreeDistance = 160f;
        /// <summary>Сид текущей трассы (0 — ещё не построена).</summary>
        public static int Seed { get; private set; }

        [Tooltip("Материал-образец: из него делаются все материалы трассы (тот же toon-шейдер, что у остальной сцены).")]
        [SerializeField] Material template;
        [Tooltip("Обычных кусков в каждом биоме (плюс развилка).")]
        [SerializeField] int chunksPerBiome = 3;
        [Tooltip("Сколько можно уехать за край дороги, прежде чем вернёт на трассу, м.")]
        [SerializeField] float offRouteMargin = 6f;

        [Header("Модели (пусто — примитивы). Заполняет TestSceneBuilder из Assets/Prefab/Route")]
        [SerializeField] GameObject[] treeModels;
        [SerializeField] GameObject[] pineModels;
        [SerializeField] GameObject[] rockModels;
        [SerializeField] GameObject[] mountainModels;
        [SerializeField] GameObject[] logModels;
        [Header("Текстуры дороги и земли (ставит сборщик сцены; пусто — однотонные цвета)")]
        [SerializeField] Material roadForest;
        [SerializeField] Material sideForest;
        [SerializeField] Material roadMountain;
        [SerializeField] Material sideMountain;
        [SerializeField] Material groundGrass;
        [SerializeField] Material roadSnow;
        [SerializeField] Material sideSnow;
        [SerializeField] Material groundSnow;
        [SerializeField] Material roadDesert;
        [SerializeField] Material sideDesert;
        [SerializeField] Material groundMountain;
        [Tooltip("Песок пологих берегов реки (Textures/desert_ground.png; пусто — песочный цвет).")]
        [SerializeField] Material groundSand;
        [Tooltip("Картинки-переходы земли (Textures/transition_ground_<верх>_<низ>.png): полоса поперёк карты на границе участков.")]
        [SerializeField] RoadTransition[] groundTransitions;
        [Tooltip("Картинки-переходы дороги между биомами (Textures/transition_road_<верх>_<низ>.png): " +
                 "на стыке биомов дорога плавно меняется. Обратный порядок — та же картинка, перевёрнутая.")]
        [SerializeField] RoadTransition[] roadTransitions;
        [Tooltip("Ёлки в снегу: те же модели, текстура хвои с белым (Pine/*_textures/*_snow.png).")]
        [SerializeField] GameObject[] snowPineModels;
        [Tooltip("Снежный биом (Assets/Prefab/Route/Snow): горы mountain_*, камни stone_*, сугробы drift_*, снеговик.")]
        [SerializeField] GameObject[] snowMountainModels;
        [SerializeField] GameObject[] snowStoneModels;
        [SerializeField] GameObject[] driftModels;
        [SerializeField] GameObject[] snowmanModels;
        [Tooltip("Пустыня: обычные деревья и ёлки с пустынной палитрой (зелень пыльно-рыжая).")]
        [SerializeField] GameObject[] desertTreeModels;
        [Header("Мелочь у дороги (Assets/Prefab/Route: bush, Stone, Other)")]
        [SerializeField] GameObject[] bushModels;
        [SerializeField] GameObject[] stoneModels;
        [SerializeField] GameObject[] stumpModels;
        [SerializeField] GameObject[] postModels;
        [SerializeField] GameObject[] blockModels;
        [SerializeField] GameObject[] junkModels;
        [SerializeField] GameObject[] fenceModels;
        [Tooltip("Арка старта и финиша (Prefab/Route/Gate/finish): вписана в коробку 1×1×0.5, низ на нуле.")]
        [SerializeField] GameObject[] archModels;
        [Tooltip("Шлагбаум на выезде со свалки (Prefab/Route/Gate/boom): вписан в 1×1×0.5, низ на нуле, столб справа.")]
        [SerializeField] GameObject[] boomModels;

        /// <summary>Переход дороги: на картинке сверху биом From, снизу — To.</summary>
        [System.Serializable]
        public struct RoadTransition
        {
            public Biome From, To;
            public Material Material;
        }

        public string Summary { get; private set; } = "";

        bool test;   // эта трасса — тестовая (TestRoute)

        /// <summary>
        /// Тестовая трасса (только в редакторе, меню «JunkRace → Тестовая трасса»): брод → мост → тоннель.
        /// В сборке — всегда выключена.
        /// </summary>
        public static bool TestRoute
        {
#if UNITY_EDITOR
            get => UnityEditor.EditorPrefs.GetBool(TestRouteKey, false);
            set => UnityEditor.EditorPrefs.SetBool(TestRouteKey, value);
#else
            get => false;
            set { }
#endif
        }
        public const string TestRouteKey = "JunkRace.TestRoute";
        internal static bool UseTestRoute => TestRoute && GameSession.Mode == GameMode.Solo && GameSession.Biome == RaceBiome.Random && !GameSession.IsTutorial;
        /// <summary>Участки трассы: прогресс начала (м от старта) и биом — по ним погода.</summary>
        public readonly List<float> SectionStarts = new List<float>();
        public readonly List<Biome> SectionBiomes = new List<Biome>();
        /// <summary>Трасса построена и включена (RouteMap.Current выставлен).</summary>
        public bool Generated => finalized;
        bool finalized;
        /// <summary>Стройка трассы по кусочкам (GenerateSteps), идёт в фоне ещё на свалке.</summary>
        IEnumerator<object> job;
        Coroutine routeDriver;
        bool jobDone, jobFast;
        int jobSeed;
        List<string> jobNames;

        const float Step = 2f;
        const float SideTile = 6f;
        const float RoadTile = 9f;       // повтор текстуры дороги вдоль, м (≈ ширина лесной дороги — без растяжения)
        const float RoadEdge = 1.1f;     // ширина травяной обочины, м
        const float EdgeU = 0.12f;       // доля картинки дороги, занятая обочиной       // плитка текстуры откоса, м           // шаг точек осевой линии, м
        const float MaxYaw = 72f;        // дорога не разворачивается назад — не пересечёт сама себя
        const float MaxHeight = 40f;
        const float GroundY = -0.05f;    // земля чуть ниже дороги на равнине (не мерцает)
        const float ForestWidth = 9f, MountainWidth = 8f;

        RouteMap map;
        Transform root;
        System.Random rng;
        Biome biome;
        int forks;
        float routeLength;   // длина основной линии (развилки считаем по длине между площадками)

        // «Черепаха»: где сейчас конец дороги.
        Vector3 pos;
        float yaw, width;
        RoutePath main;

        readonly Dictionary<Color32, Material> materials = new Dictionary<Color32, Material>();
        Mesh cube, sphere, cylinder;

        static readonly Color ForestRoad = new Color(0.55f, 0.42f, 0.3f), ForestSide = new Color(0.4f, 0.3f, 0.2f);
        static readonly Color MountainRoad = new Color(0.56f, 0.55f, 0.53f), MountainSide = new Color(0.42f, 0.4f, 0.38f);
        static readonly Color Grass = new Color(0.42f, 0.55f, 0.3f);
        static readonly Color SnowRoad = new Color(0.86f, 0.89f, 0.95f), SnowSide = new Color(0.7f, 0.76f, 0.86f);
        static readonly Color SnowWhite = new Color(0.8f, 0.85f, 0.93f);   // чуть приглушён: иначе сугроб пересвечен и без объёма
        /// <summary>Сабмешей у дороги: верх и откос на каждый биом.</summary>
        const int BiomeCount = 4;
        const int RoadSubs = BiomeCount * 2;

        void Awake()
        {
            Instance = this;
            Seed = 0;
        }

        void OnDestroy()
        {
            if (Instance != this) return;
            Instance = null;
            RouteMap.Current = null;
        }

        void Start()
        {
            // Хост и одиночная игра выбирают сид сами; клиент ждёт сид от хоста (ApplySeed).
            // Сразу раскладываем только свалку; трассу строим после сборки (BuildRoute) — у всех одновременно.
            if (Authority.IsAuthority && !YardReady)
                PrepareYard(new System.Random().Next(1, int.MaxValue), CountPlayers());
        }

        /// <summary>Клиент: сид и число игроков пришли от хоста.</summary>
        public void ApplySeed(int seed, int players)
        {
            if (seed > 0 && !YardReady) PrepareYard(seed, players);
        }

        /// <summary>Свалка разложена (сид известен). Трасса может быть ещё не построена.</summary>
        public bool YardReady { get; private set; }

        /// <summary>Сид выбран: кучи, детали и декорации на свалке — по нему, у всех одинаково.</summary>
        public void PrepareYard(int seed, int players)
        {
            if (YardReady) return;
            Seed = seed;
            Players = Mathf.Clamp(players, 1, 4);
            BuildSurroundings();
            YardShuffle.Apply(seed, transform.position, Players);
            YardReady = true;
            StartRouteJob();             // трасса начинает строиться в фоне, пока собирают машины
            DayNightCycle.Begin(seed);   // сутки идут с этого момента: час старта и восток — по сиду
            TestCarBuilder.Begin();      // тестовая трасса: машину соберёт сама
            Tutorial.Begin();            // обучение: шаги и раскладка деталей под них

            // Холмы-задник вокруг свалки (после постройки трассы перестроятся вокруг неё).
            if (BackdropHills.Instance != null)
            {
                var around = new List<Vector3>();
                for (float z = -YardShuffle.YardDepth; z <= 80f; z += 10f) around.Add(transform.position + new Vector3(0f, 0f, z));
                BackdropHills.Look = HillsLook;
                BackdropHills.Instance.Build(around, seed);
            }
        }

        Transform tempRoot;
        const float BoomZ = 3f;   // шлагбаум — сразу за аркой старта (у арки башни глубиной ~±1.8 м)

        /// <summary>
        /// Земля вокруг свалки (по бокам и сзади — там стоят крупные декорации) и, до постройки трассы,
        /// заглушка впереди: полоса земли и шлагбаум на выезде — чтобы не уехать в пустоту раньше старта.
        /// </summary>
        void BuildSurroundings()
        {
            PreparePrimitives();
            rng = new System.Random(Seed ^ 0x5EED);   // модели шлагбаума выбираются случайно — до трассы rng ещё нет
            Vector3 g = transform.position;
            const float far = 300f, yardHalf = YardShuffle.YardHalf, yardDepth = YardShuffle.YardDepth;

            root = new GameObject("Окрестности_свалки").transform;
            root.SetParent(transform, false);
            float zc = g.z - far / 2f;
            GroundPiece("Земля_Л", new Vector3(g.x - yardHalf - far / 2f, GroundY - 1f, zc), new Vector3(far, 2f, far), BaseBiome);
            GroundPiece("Земля_П", new Vector3(g.x + yardHalf + far / 2f, GroundY - 1f, zc), new Vector3(far, 2f, far), BaseBiome);
            float backLen = far - yardDepth;
            GroundPiece("Земля_Зад", new Vector3(g.x, GroundY - 1f, g.z - yardDepth - backLen / 2f), new Vector3(yardHalf * 2f, 2f, backLen), BaseBiome);

            // Временное — до трассы.
            tempRoot = new GameObject("До_старта").transform;
            tempRoot.SetParent(transform, false);
            root = tempRoot;
            GroundPiece("Земля_впереди", new Vector3(g.x, GroundY - 1f, g.z + 40f - 0.5f), new Vector3(yardHalf * 2f + far * 2f, 2f, 80f), BaseBiome);
            var boom = Piece("Шлагбаум", cube, new Vector3(g.x, 0.6f, g.z + BoomZ), Quaternion.identity, new Vector3(14f, 1.2f, 0.4f), new Color(0.9f, 0.15f, 0.1f), Col.Box);
            if (Any(boomModels))
            {
                // Одна арка-ворота во всю ширину выезда. Коробка — только коллайдер (стартовая блокировка).
                Hidden(boom);
                Model(boomModels, new Vector3(g.x, 0f, g.z + BoomZ), Quaternion.identity, new Vector3(15f, 4f, 0.9f * 2f), Decor.Solid);
            }
            else
                Piece("Шлагбаум_полоса", cube, new Vector3(g.x, 0.6f, g.z + BoomZ - 0.22f), Quaternion.identity, new Vector3(14.02f, 0.3f, 0.02f), Color.white, Col.None);
            root = null;
        }

        // ---------------- Стройка трассы по кусочкам ----------------
        // Трассу целиком за один кадр в браузере телефона не построить: мусор в памяти там убирается только
        // между кадрами, за один длинный кадр он копится — память раздувается, и Telegram перезагружает страницу.
        // Поэтому стройка — по кусочкам: начинается в фоне сразу, как разложена свалка (по чуть-чуть каждый кадр,
        // пока игроки собирают машины), а по «Готов» остаток достраивается под экраном загрузки быстрее.
        // Трасса строится выключенной и включается целиком в FinalizeRoute. Порядок шагов (и rng) тот же,
        // что и раньше, — у всех игроков по одному сиду выходит одна и та же трасса.

        /// <summary>Миллисекунд на стройку за кадр: на свалке — понемногу, под экраном загрузки — побольше.</summary>
        float SliceBudget => jobFast ? 0.025f : (Application.isMobilePlatform || TouchControls.TouchMode ? 0.004f : 0.008f);

        void StartRouteJob()
        {
            if (job != null || finalized || !YardReady) return;
            jobDone = false;
            job = CreateRouteSteps(Seed, Players);
            routeDriver = StartCoroutine(DriveRouteJob(job));
        }

        System.Collections.IEnumerator DriveRouteJob(IEnumerator<object> steps)
        {
            while (job == steps && !jobDone)
            {
                float t0 = Time.realtimeSinceStartup;
                while (job == steps && !jobDone && Time.realtimeSinceStartup - t0 < SliceBudget)
                    if (!steps.MoveNext()) jobDone = true;
                yield return null;
            }
            routeDriver = null;
        }

        void StopRouteDriver()
        {
            if (routeDriver == null) return;
            StopCoroutine(routeDriver);
            routeDriver = null;
        }

        /// <summary>Построить трассу сразу (зашёл посреди гонки): что не достроено — за этот кадр. Повторно — ничего.</summary>
        public void BuildRoute()
        {
            if (finalized || !YardReady) return;
            StartRouteJob();
            StopRouteDriver();
            while (!jobDone && job != null) if (!job.MoveNext()) jobDone = true;
            FinalizeRoute();
        }

        /// <summary>Вторая загрузка (после «Готов»): дождаться трассы, достраивая её быстрее, и включить.</summary>
        public System.Collections.IEnumerator BuildRouteRoutine()
        {
            if (finalized || !YardReady) yield break;
            jobFast = true;
            StartRouteJob();
            while (!jobDone) yield return null;
            FinalizeRoute();
        }

        /// <summary>Сколько игроков на старте: от этого — сколько деталей на свалке.</summary>
        public static int Players { get; private set; } = 1;

        static int CountPlayers()
        {
            var nm = Unity.Netcode.NetworkManager.Singleton;
            int n = nm != null && nm.IsListening && nm.IsServer ? nm.ConnectedClientsIds.Count : 1;
            return Mathf.Clamp(n, 1, 4);
        }

        // ============================ Генерация ============================

        /// <summary>Построить трассу целиком сразу (редакторские инструменты и проверки).</summary>
        public void Generate(int seed, int players = 1)
        {
            StopRouteDriver();
            job?.Dispose();
            if (routeRoot != null)
            {
                routeRoot.gameObject.SetActive(false);
                if (Application.isPlaying) Destroy(routeRoot.gameObject);
                else DestroyImmediate(routeRoot.gameObject);
            }
            if (RouteMap.Current == map) RouteMap.Current = null;
            finalized = false;
            job = CreateRouteSteps(seed, players);
            jobDone = false;
            while (job.MoveNext()) { }
            jobDone = true;
            FinalizeRoute();
        }

        IEnumerator<object> CreateRouteSteps(int seed, int players)
        {
            jobSeed = seed;
            Seed = seed;
            Players = Mathf.Clamp(players, 1, 4);
            rng = new System.Random(seed);
            forks = 0;
            forkFills.Clear();
            SectionStarts.Clear();
            SectionBiomes.Clear();
            rivers.Clear();
            canyons.Clear();
            zones = new List<(float z, Biome biome)>();
            zonesLeft.Clear();
            zonesRight.Clear();
            routeLength = 0f;
            routeGoal = 0f;
            circuit = false;
            laneHalf = 0f;
            riversBefore = 0;
            legXMin = -1e9f;
            legXMax = 1e9f;
            circuitMarks.Clear();
            underBridge.Clear();
            forksAllowed = true;
            map = new RouteMap { OffMargin = offRouteMargin };
            root = new GameObject("Маршрут").transform;
            root.SetParent(transform, false);
            root.gameObject.SetActive(false);   // строится в фоне невидимой — включится целиком в FinalizeRoute
            routeRoot = root;
            PreparePrimitives();
            smallLayer = LayerMask.NameToLayer(SmallDecorLayer);
            treeLayer = LayerMask.NameToLayer(TreeLayer);

            // Свалка — тоже часть маршрута (для сброса), но дорогу на ней не строим.
            var yard = map.NewPath("Свалка");
            yard.BuildMesh = false;
            for (float z = -YardShuffle.YardDepth; z <= 0.01f; z += 4f)
                yard.Add(new RouteSample { Pos = transform.position + new Vector3(0f, 0f, z), Forward = Vector3.forward, HalfWidth = YardShuffle.YardHalf, Road = true });

            pos = transform.position;
            yaw = 0f;
            width = 14f;
            biome = BaseBiome;
            yard.StartProgress = -yard.Length;
            main = map.NewPath("Дорога");
            AddSample(main);
            map.Start = main.Samples[0];
            Gate(pos, 0f, 14f, new Color(0.95f, 0.75f, 0.15f));
            Run(main, 24f, 0f, 0f, ForestWidth);

            // Участки. Первые три — все биомы в случайном порядке (каждый есть на каждой трассе), дальше — любой,
            // кроме предыдущего. Участков столько, чтобы набрать длину, выбранную перед гонкой; в каждом 1–3 куска.
            var order = BiomePool();
            Shuffle(order);
            test = UseTestRoute;
            bool tutorial = GameSession.IsTutorial;
            int fixedSections = 0;
            if (tutorial) { fixedSections = 1; order = new List<Biome> { Biome.Forest }; }   // обучение: короткий лес
            else if (test) { fixedSections = 3; order = new List<Biome> { Biome.Forest, Biome.Snow, Biome.Mountains }; }   // сперва вода: брод, мост, потом тоннель
            var names = new List<string>();
            jobNames = names;
            circuit = GameSession.IsCircuit && !test;
            map.Circuit = circuit;
            if (circuit) { BuildCircuit(names); yield return null; }
            else
            {
                float target = SprintTarget[(int)GameSession.Size];
                routeGoal = target;
                for (int i = 0; ; i++)
                {
                    if (fixedSections > 0 ? i >= fixedSections : i >= 1 && routeLength >= target - 80f) break;
                    var b = NextBiome(order, i);
                    biome = b;
                    names.Add(BiomeName(b));
                    SectionStarts.Add(main.StartProgress + main.Length);
                    zones.Add((pos.z, b));
                    SectionBiomes.Add(b);
                    float left = target - routeLength;
                    bool fork = rng.NextDouble() < 0.6 || forks == 0 && left < 700f;   // хотя бы одна развилка
                    int chunks = fixedSections > 0 ? 2 + rng.Next(2) : Mathf.Clamp(Mathf.RoundToInt((left - SectionOverhead(b)) / 90f), 1, 3);
                    if (tutorial) { TutorialForest(); yield return null; continue; }
                    BuildSection(b, chunks, fork);
                    yield return null;
                }

                // Финиш и площадка после него.
                Run(main, 30f, RandRange(-10f, 10f), -Mathf.Min(pos.y, 4f));
                var end = main.Last;
                map.FinishProgress = main.StartProgress + main.Length;
                Finish(end);
                Run(main, 30f, 0f, 0f);
            }
            PickSun();   // солнце выбираем тут (порядок rng прежний), а ставим при включении трассы
            yield return null;

            foreach (var p in map.Paths) p.SmoothForwards();
            foreach (var c in canyons) c.Fill();   // профиль ущелий — до декора: ему нужно знать, где низина
            var railway = tutorial ? null : RouteRailway.Plan(root, map, toReverse, GroundY,
                RailwayCrossingSite, seed, GameSession.Size);
            yield return null;
            foreach (var p in map.Paths) if (p.BuildMesh) { BuildRoadMesh(p); yield return null; }
            foreach (var f in forkFills) BuildForkFill(f.left, f.right);
            forkFills.Clear();
            yield return null;
            int firstDecoration = root.childCount;
            var deco = DecorateSteps();
            while (deco.MoveNext()) yield return null;
            if (circuit) { CircuitRidge(); yield return null; }
            if (railway != null)
            {
                for (int i = firstDecoration; i < root.childCount; i++)
                {
                    var decoration = root.GetChild(i);
                    if (decoration.name.StartsWith("Мост", System.StringComparison.Ordinal)) continue;
                    var renderers = decoration.GetComponentsInChildren<Renderer>(true);
                    var bounds = new Bounds(decoration.position, decoration.localScale);
                    if (renderers.Length > 0) bounds = renderers[0].bounds;
                    foreach (var renderer in renderers) bounds.Encapsulate(renderer.bounds);
                    if (railway.Obstructs(bounds)) decoration.gameObject.SetActive(false);
                    if (i % 128 == 0) yield return null;
                }
            }
            BuildGround(firstDecoration, railway);
            yield return null;
            if (circuit) FinishCircuit();
            if (railway != null)
            {
                int layer = LayerMask.NameToLayer(TreeLayer);
                if (layer >= 0) railway.gameObject.layer = layer;
                var railwayBuild = railway.Build(map, GroundY);
                while (railwayBuild.MoveNext()) yield return null;
            }
            RouteAirTraffic.Build(root, map, toReverse, seed);
            root = null;
        }

        Transform routeRoot;

        /// <summary>Трасса достроена: включить её и всё, что от неё зависит (солнце, холмы, погода).</summary>
        void FinalizeRoute()
        {
            if (finalized || !jobDone) return;
            finalized = true;
            StopRouteDriver();
            job?.Dispose();
            job = null;
            if (tempRoot != null) { Destroy(tempRoot.gameObject); tempRoot = null; }   // шлагбаум и заглушка — прочь
            int seed = jobSeed;
            var names = jobNames ?? new List<string>();
            routeRoot.gameObject.SetActive(true);
            ApplyPickedSun();

            // Веб (телефон в Telegram): склейка копирует меши всей трассы разом — пик памяти, на котором
            // браузер убивает страницу. Там обходимся без неё (URP и так группирует отрисовку через SRP Batcher).
            if (Application.platform != RuntimePlatform.WebGLPlayer) StaticBatchingUtility.Combine(routeRoot.gameObject);
            RouteMap.Current = map;
            ApplyCullDistances();

            // Холмы-задник до горизонта вокруг всей трассы (без коллайдеров, по тому же сиду).
            if (BackdropHills.Instance != null)
            {
                var points = new List<Vector3>();
                foreach (var p in map.Paths) foreach (var s in p.Samples) points.Add(s.Pos);
                BackdropHills.Look = HillsLook;
                BackdropHills.Instance.Build(points, seed);
            }

            if (circuit) CircuitSections();   // участки по ходу круга — для погоды
            Weather.Begin(map, SectionStarts, SectionBiomes, seed);

            Summary = circuit
                ? $"Кольцо: {string.Join(" → ", names)} · круг ~{Mathf.RoundToInt(map.LapLength / 10f) * 10} м × {GameSession.Laps} · сид {seed}"
                : $"Маршрут: {string.Join(" → ", names)} · развилок: {forks} · ~{Mathf.RoundToInt(routeLength / 10f) * 10} м · сид {seed}";
            Debug.Log("[JunkRace] " + Summary);
        }

        // ============================ Длина гонки ============================

        /// <summary>
        /// Расчётная средняя скорость машины из хлама с препятствиями, м/с: по ней длина трассы под выбранное время.
        /// Обучение (~750 м) проходится примерно за 2 минуты.
        /// </summary>
        const float DesignSpeed = 6.5f;
        /// <summary>Спринт: быстрый ~2 мин, средний 3–4, долгий 5–6.</summary>
        static readonly float[] SprintTarget = { 120f * DesignSpeed, 210f * DesignSpeed, 330f * DesignSpeed };
        /// <summary>Кольцо, один круг: быстрое 1–2 мин, среднее 2,5–3, большое 4–5.</summary>
        static readonly float[] LapTarget = { 90f * DesignSpeed, 165f * DesignSpeed, 270f * DesignSpeed };

        static string BiomeName(Biome b) => b == Biome.Forest ? "Лес" : b == Biome.Mountains ? "Горы" : b == Biome.Desert ? "Пустыня" : "Снег";

        /// <summary>Биомы трассы: выбранный один или все (рандом).</summary>
        static List<Biome> BiomePool()
        {
            var pick = GameSession.Biome;
            if (pick != RaceBiome.Random) return new List<Biome> { ToBiome(pick) };
            return new List<Biome> { Biome.Forest, Biome.Mountains, Biome.Snow, Biome.Desert };
        }

        static Biome ToBiome(RaceBiome b) => b == RaceBiome.Mountains ? Biome.Mountains : b == RaceBiome.Snow ? Biome.Snow
                                            : b == RaceBiome.Desert ? Biome.Desert : Biome.Forest;

        /// <summary>Биом у свалки, на разворотах кольца и в переходе восьмёрки: выбранный, при рандоме — лес.</summary>
        Biome BaseBiome => GameSession.Biome == RaceBiome.Random || GameSession.IsTutorial ? Biome.Forest : ToBiome(GameSession.Biome);

        /// <summary>Холмы-задник в цветах биома, если трасса в одном биоме (рандом — как есть).</summary>
        Biome? HillsLook => GameSession.Biome == RaceBiome.Random || GameSession.IsTutorial ? (Biome?)null : BaseBiome;

        /// <summary>Следующий биом после prev: другой, если есть из чего выбрать.</summary>
        Biome OtherBiome(Biome prev, List<Biome> pool)
        {
            if (pool.Count == 1) return pool[0];
            Biome b;
            do b = pool[rng.Next(pool.Count)]; while (b == prev);
            return b;
        }

        static float BiomeWidth(Biome b) => b == Biome.Mountains ? MountainWidth : b == Biome.Snow ? SnowWidth : b == Biome.Desert ? DesertWidth : ForestWidth;

        /// <summary>
        /// Сколько метров участок добавляет сверх своих кусков: въезд, а в горах ещё подъём (70–140 м) и спуск
        /// к равнине — без этого горное кольцо выходило в полтора раза длиннее заказанного.
        /// </summary>
        static float SectionOverhead(Biome b) => b == Biome.Mountains ? 260f : 60f;

        /// <summary>Сколько трассы хотим (спринт — весь, кольцо — круг): по ней — где ставить обязательные тоннель и реку.</summary>
        float routeGoal;

        /// <summary>
        /// На каждой трассе есть и ущелье с тоннелем, и река: если к трети пути тоннеля ещё не было — ставим его,
        /// к половине без реки — реку. Возвращает true, если кусок поставлен (вместо случайного).
        /// </summary>
        bool Must(float w)
        {
            if (test || GameSession.IsTutorial || routeGoal <= 0f) return false;
            float frac = routeLength / routeGoal;
            if (canyons.Count == 0 && frac >= 0.3f) { CanyonCut(w); return true; }
            if (rivers.Count == 0 && frac >= 0.5f && !riverInSection) { RiverCrossing(w); return true; }
            return false;
        }

        /// <summary>Биом участка i: первые три — из перемешанного списка, дальше — любой, кроме предыдущего.</summary>
        Biome NextBiome(List<Biome> order, int i)
        {
            if (i < order.Count) return order[i];
            return OtherBiome(lastBiome, BiomePool());
        }

        Biome lastBiome => SectionBiomes.Count > 0 ? SectionBiomes[SectionBiomes.Count - 1] : Biome.Forest;

        // ============================ Кольцо: овал на хвостике ============================
        //
        //            ┌──── дальний разворот ────┐
        //   ветка «туда» (x − L)        ветка «обратно» (x + L)
        //            └── ближний разворот ──────┘
        //                     │ ← развилка J, линия круга — чуть дальше по ходу
        //                 хвостик от ворот свалки
        //
        // Обе ветки строятся обычным генератором «вперёд по z» (реки, ущелья, земля полосами работают как
        // в спринте), каждая — в своей полосе по x и на своей половине карты. Ветку «обратно» потом
        // разворачиваем. Между ветками — каменная гряда: прячет шов земли и не даёт срезать.

        bool circuit;
        float laneX, laneHalf;           // полоса, в которой держится ветка (0 — без ограничения)
        float legXMin = -1e9f, legXMax = 1e9f;   // реки этой ветки — только на её половине
        readonly List<(float z, Biome biome)> zonesLeft = new List<(float, Biome)>(), zonesRight = new List<(float, Biome)>();
        /// <summary>
        /// Участки на кольце: начало и конец (как строили) и биом — после разворота пересчитаем в прогресс круга.
        /// У развёрнутой ветки участок по ходу начинается там, где его строили последним.
        /// </summary>
        readonly List<(Vector3 from, Vector3 to, Biome biome, bool reversed)> circuitMarks = new List<(Vector3, Vector3, Biome, bool)>();
        readonly List<(float z0, float z1)> ridges = new List<(float, float)>();   // где между ветками ставить гряду
        /// <summary>Пути ветки «обратно»: строились «вперёд по z», развернём после постройки всего вида.</summary>
        readonly List<RoutePath> toReverse = new List<RoutePath>();
        /// <summary>Восьмёрка: нижняя дорога под мостом — опоры моста на неё не ставим.</summary>
        readonly List<RouteSample> underBridge = new List<RouteSample>();

        static readonly float[] LaneOffset = { 40f, 55f, 70f };   // полуширина овала по размеру круга, м
        const float OverpassHeight = 6.5f;   // восьмёрка: настил моста над нижней дорогой, м
        const float OverpassDeck = 26f;      // мост — столько метров в обе стороны от пересечения (дальше насыпь)
        const float OverpassRamp = 38f;      // длина въезда на насыпь, м

        struct Turtle { public RoutePath Main; public Vector3 Pos; public float Yaw, Width; public Biome Biome; }
        Turtle Save() => new Turtle { Main = main, Pos = pos, Yaw = yaw, Width = width, Biome = biome };
        void Load(Turtle t) { main = t.Main; pos = t.Pos; yaw = t.Yaw; width = t.Width; biome = t.Biome; }

        void BuildCircuit(List<string> names)
        {
            int size = (int)GameSession.Size;
            float L = LaneOffset[size];
            float lapTarget = LapTarget[size];
            bool eight = GameSession.Shape == RaceShape.Eight;
            forksAllowed = size > 0;   // на быстром кольце ветки слишком близко — развилке негде разойтись
            Vector3 g = transform.position;
            ridges.Clear();
            toReverse.Clear();
            underBridge.Clear();

            // Хвостик: прямо от ворот к развилке (дорога кольца проходит поперёк его конца).
            Run(main, 14f, 0f, 0f, ForestWidth);
            var tail = main;
            Vector3 J = pos + Vector3.forward * (ForestWidth / 2f - 1f);   // хвостик заходит на дорогу кольца на метр
            float T = tail.StartProgress + tail.Length + (ForestWidth / 2f - 1f);
            float zs = J.z + L;   // центр ближнего разворота: отсюда начинаются ветки
            float crossDz = Mathf.Max(150f, 1.6f * L);   // восьмёрка: длина перехода между полосами по z
            float crossLen = eight ? 2f * Mathf.Sqrt(4f * L * L + crossDz * crossDz) : 0f;
            float legTarget = Mathf.Max(110f, (lapTarget - 2f * Mathf.PI * L - crossLen) / 2f);
            float xl = J.x - L, xr = J.x + L, mid = g.x;

            // Ближний разворот, левая половина: от развилки (ход −x) к началу ветки А (ход +z).
            var nearLeft = map.NewPath("Кольцо_разворот_1");
            nearLeft.StartProgress = T;
            Arc(nearLeft, new Vector3(J.x, 0f, zs), L, -90f, -180f);

            routeGoal = lapTarget;
            var orderA = BiomePool();
            var orderB = new List<Biome>(orderA);
            Shuffle(orderA);
            Shuffle(orderB);
            int iA = 0, iB = 0;

            // Ветка А — по ходу «вверх»: начинается в левой полосе.
            var aPath = map.NewPath("Кольцо_А");
            aPath.StartProgress = nearLeft.StartProgress + nearLeft.Length;
            StartLeg(aPath, xl, zs);
            Part(xl, false, eight ? legTarget / 2f : legTarget, orderA, ref iA, names, false);
            var a = Save();

            // Ветка Б — строим тоже «вверх» (в правой полосе), по ней поедут вниз: потом развернём.
            int bFirst = map.Paths.Count;
            var bPath = map.NewPath("Кольцо_Б");
            bPath.StartProgress = 0f;
            StartLeg(bPath, xr, zs);
            Part(xr, true, eight ? legTarget / 2f : legTarget, orderB, ref iB, names, true);
            var b = Save();

            if (eight)
            {
                // Низы веток — до одной высоты z, дальше переход наискосок: А — в правую полосу, Б — в левую.
                float z1 = Mathf.Max(a.Pos.z, b.Pos.z);
                Load(a); Extend(z1); a = Save();
                Load(b); Extend(z1); b = Save();
                float z2 = z1 + crossDz;
                zonesLeft.Add((z1, BaseBiome));
                zonesRight.Add((z1, BaseBiome));
                ridges.Add((zs + 12f, z1 - 8f));

                biome = BaseBiome;
                int aFrom = a.Main.Samples.Count;
                Hermite(a.Main, a.Pos, new Vector3(xr, g.y, z2), Vector3.forward * crossDz, Vector3.forward * crossDz, true);
                int bFrom = b.Main.Samples.Count;
                Hermite(b.Main, b.Pos, new Vector3(xl, g.y, z2), Vector3.forward * crossDz, Vector3.forward * crossDz, true);
                Overpass(a.Main, aFrom, b.Main, bFrom);

                // Верха веток: А — в правой полосе, Б — в левой.
                Load(a); main = a.Main; pos = new Vector3(xr, g.y, z2); yaw = 0f; biome = BaseBiome;
                Part(xr, true, legTarget / 2f, orderA, ref iA, names, false);
                a = Save();
                Load(b); main = b.Main; pos = new Vector3(xl, g.y, z2); yaw = 0f; biome = BaseBiome;
                Part(xl, false, legTarget / 2f, orderB, ref iB, names, true);
                b = Save();
                float zEnd = Mathf.Max(a.Pos.z, b.Pos.z);
                Load(a); Extend(zEnd); a = Save();
                Load(b); Extend(zEnd); b = Save();
                ridges.Add((z2 + 8f, zEnd - 10f));
            }
            else
            {
                float zEnd = Mathf.Max(a.Pos.z, b.Pos.z);
                Load(a); Extend(zEnd); a = Save();
                Load(b); Extend(zEnd); b = Save();
                ridges.Add((zs + 12f, zEnd - 10f));
            }
            // За ветками — снова лес по всей ширине: дальний разворот пересекает шов половин.
            zonesLeft.Add((a.Pos.z + 5f, BaseBiome));
            zonesRight.Add((a.Pos.z + 5f, BaseBiome));
            laneHalf = 0f;
            legXMin = -1e9f; legXMax = 1e9f;
            zones = zonesLeft;

            // Дальний разворот: плавная «U» от конца ветки А (ход +z) к концу ветки Б (ход −z).
            var far = map.NewPath("Кольцо_разворот_2");
            far.StartProgress = a.Main.StartProgress + a.Main.Length * a.Main.ProgressScale;
            biome = BaseBiome;
            float k = Mathf.Abs(b.Pos.x - a.Pos.x) * 1.6f;
            Hermite(far, a.Pos, b.Pos, Vector3.forward * k, Vector3.back * k, false);

            // Ветка Б целиком (с развилками) — в прогресс по ходу «вниз»: конец постройки = начало по ходу.
            float bLen = b.Main.StartProgress + b.Main.Length * b.Main.ProgressScale;
            float baseP = far.StartProgress + far.Length;
            for (int i = bFirst; i < map.Paths.Count; i++)
            {
                var p = map.Paths[i];
                if (p == far) break;
                p.StartProgress = baseP + (bLen - (p.StartProgress + p.Length * p.ProgressScale));
                toReverse.Add(p);
            }

            // Ближний разворот, правая половина: от конца ветки Б (ход −z) к развилке (ход −x).
            var nearRight = map.NewPath("Кольцо_разворот_3");
            nearRight.StartProgress = baseP + bLen;
            biome = BaseBiome;
            Arc(nearRight, new Vector3(J.x, 0f, zs), L, 0f, -90f);
            StartBlock = BuildBlock("Заграждение_старта", nearRight.At(nearRight.Length - 12f));
            // Хвостик к свалке: перегородим, когда все уйдут на круг (до тех пор спрятано).
            TailBlock = BuildBlock("Заграждение_хвостика", tail.At(tail.Length - 3f));
            TailBlock.SetActive(false);

            map.LapStart = T;
            map.LapLength = nearRight.StartProgress + nearRight.Length - T;
            map.LapLine = T + LapLineOffset;
            map.FinishProgress = map.LapLine;

            // Линия круга — арка на ближнем развороте, чуть дальше развилки по ходу.
            var line = nearLeft.At(LapLineOffset);
            float lineYaw = Mathf.Atan2(line.Forward.x, line.Forward.z) * Mathf.Rad2Deg;
            Gate(line.Pos, lineYaw, line.HalfWidth * 2f + 2f, new Color(0.2f, 0.85f, 0.3f));

            // Чекпоинты — поровну по кругу (между ними ~150–250 м).
            int n = Mathf.Clamp(Mathf.RoundToInt(map.LapLength / 200f), 3, 8);
            map.Checkpoints = new float[n];
            for (int i = 0; i < n; i++) map.Checkpoints[i] = map.LapLength * (i + 1) / (n + 1);
            forksAllowed = true;
        }

        bool forksAllowed = true;

        /// <summary>
        /// Кольцо: заграждение поперёк дороги сразу «против хода» от развилки — со старта не свернуть не туда.
        /// Убирает RaceTimer, когда все гонщики проедут первый чекпоинт.
        /// </summary>
        public GameObject StartBlock { get; private set; }

        /// <summary>
        /// Кольцо: заграждение на хвостике у развилки — чтобы на круге не свернуть случайно к свалке.
        /// Сначала спрятано; RaceTimer ставит его, когда убирает StartBlock (все уже на круге).
        /// </summary>
        public GameObject TailBlock { get; private set; }

        /// <summary>Шлагбаумы навстречу поперёк дороги в точке s + невидимая стенка во всю ширину.</summary>
        GameObject BuildBlock(string name, RouteSample s)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);   // не в «Маршрут»: тот статически объединяется, а это уберём
            var rot = Quaternion.LookRotation(s.Forward, Vector3.up);
            go.transform.SetPositionAndRotation(s.Pos, rot);
            float span = s.HalfWidth * 2f + 3f;
            var col = go.AddComponent<BoxCollider>();
            col.center = new Vector3(0f, 1.2f, 0f);
            col.size = new Vector3(span, 2.4f, 0.6f);
            if (Any(boomModels))
            {
                // Одна арка-ворота во всю ширину дороги (как на выезде со свалки).
                var m = Model(boomModels, s.Pos, rot, new Vector3(span + 0.4f, 4f, 0.9f * 2f), Decor.Solid);
                m.transform.SetParent(go.transform, true);
                foreach (var t in m.GetComponentsInChildren<Transform>(true)) t.gameObject.isStatic = false;
            }
            else
            {
                var bar = Piece("Заграждение", cube, s.Pos + Vector3.up * 0.9f, rot, new Vector3(span, 1.2f, 0.4f), new Color(0.9f, 0.15f, 0.1f), Col.None);
                bar.isStatic = false;
                bar.transform.SetParent(go.transform, true);
            }
            return go;
        }

        /// <summary>Линия круга — столько метров по ходу от развилки (не на самом стыке с хвостиком).</summary>
        const float LapLineOffset = 25f;

        void StartLeg(RoutePath path, float x, float z)
        {
            main = path;
            pos = new Vector3(x, transform.position.y, z);
            yaw = 0f;
            width = ForestWidth;
            biome = BaseBiome;
            AddSample(path);
        }

        /// <summary>Дотянуть ветку прямой до этого z (концы веток — на одной высоте).</summary>
        void Extend(float z)
        {
            if (z - pos.z > 0.5f) Run(main, z - pos.z, -yaw, 0f, ForestWidth);
        }

        /// <summary>
        /// Кусок ветки кольца в полосе laneCenter (своя половина карты — для рек, ущелий и земли): участки
        /// биомов «вперёд по z», пока не наберётся длина; в конце — к земле и носом строго вперёд.
        /// </summary>
        void Part(float laneCenter, bool rightHalf, float target, List<Biome> order, ref int index, List<string> names, bool reversed)
        {
            laneX = laneCenter;
            laneHalf = Mathf.Abs(laneCenter - transform.position.x) * 0.55f;
            zones = rightHalf ? zonesRight : zonesLeft;
            float mid = transform.position.x;
            if (rightHalf) { legXMin = mid; legXMax = 1e9f; } else { legXMin = -1e9f; legXMax = mid; }
            riversBefore = rivers.Count;
            float start = routeLength;
            if (target < 160f)
            {
                // Короткий кусок (восьмёрка небольшого круга): участка не набрать — просто дорога с поворотами.
                float left = target;
                while (left > 1f) { float len = Mathf.Min(left, RandRange(25f, 40f)); Run(main, len, RandRange(-20f, 20f), 0f, BiomeWidth(biome)); left -= len; }
                laneHalf = 0f;
                Run(main, 12f, -yaw, -pos.y, ForestWidth);
                pos.y = transform.position.y;
                return;
            }
            for (int i = 0; i == 0 || routeLength - start < target - 60f; i++, index++)
            {
                var bi = index < order.Count ? order[index] : OtherBiome(biome, BiomePool());
                biome = bi;
                names.Add(BiomeName(bi));
                zones.Add((pos.z, bi));
                Vector3 sectionFrom = pos;
                int chunks = Mathf.Clamp(Mathf.RoundToInt((target - (routeLength - start) - SectionOverhead(bi)) / 90f), 1, 3);
                bool fork = forksAllowed && rng.NextDouble() < 0.4;
                BuildSection(bi, chunks, fork);
                circuitMarks.Add((sectionFrom, pos, bi, reversed));
            }
            laneHalf = 0f;
            while (pos.y > 3f) Run(main, 40f, RandRange(-10f, 10f), -Mathf.Min(pos.y - 1f, 8f), ForestWidth);
            Run(main, 24f, -yaw, -pos.y, ForestWidth);   // ровно вперёд и на землю
            pos.y = transform.position.y;
        }

        /// <summary>
        /// Восьмёрка: переход ветки А поднимаем мостом над переходом ветки Б. Пересечение — где кривые ближе всего;
        /// там настил (мост с перилами), по сторонам — насыпь с плавным въездом.
        /// </summary>
        void Overpass(RoutePath upper, int uFrom, RoutePath lower, int lFrom)
        {
            int bu = uFrom, bl = lFrom;
            float best = float.MaxValue;
            for (int i = uFrom; i < upper.Samples.Count; i++)
                for (int j = lFrom; j < lower.Samples.Count; j++)
                {
                    Vector3 d = upper.Samples[i].Pos - lower.Samples[j].Pos;
                    d.y = 0f;
                    if (d.sqrMagnitude < best) { best = d.sqrMagnitude; bu = i; bl = j; }
                }
            float c = upper.Dist[bu];
            for (int i = uFrom; i < upper.Samples.Count; i++)
            {
                float d = Mathf.Abs(upper.Dist[i] - c);
                float t = Mathf.Clamp01((d - OverpassDeck) / OverpassRamp);
                var s = upper.Samples[i];
                s.Pos.y = transform.position.y + OverpassHeight * (1f - t * t * (3f - 2f * t));
                s.Bridge = d <= OverpassDeck;
                upper.Samples[i] = s;
            }
            for (int j = Mathf.Max(lFrom, bl - 25); j < Mathf.Min(lower.Samples.Count, bl + 25); j++) underBridge.Add(lower.Samples[j]);
        }

        /// <summary>Под мостом восьмёрки — нижняя дорога: опору сюда не ставим.</summary>
        bool OnUnderpass(Vector3 p, float margin)
        {
            foreach (var s in underBridge)
            {
                Vector3 d = p - s.Pos;
                d.y = 0f;
                if (d.magnitude < s.HalfWidth + margin) return true;
            }
            return false;
        }

        /// <summary>Дуга окружности (центр, радиус, углы от a0 к a1 в градусах; 0° — +x, −90° — −z), ровная, на земле.</summary>
        void Arc(RoutePath path, Vector3 center, float r, float a0, float a1)
        {
            int steps = Mathf.Max(4, Mathf.CeilToInt(Mathf.Abs(a1 - a0) * Mathf.Deg2Rad * r / Step));
            float sign = Mathf.Sign(a1 - a0);
            for (int i = 0; i <= steps; i++)
            {
                float a = Mathf.Lerp(a0, a1, i / (float)steps) * Mathf.Deg2Rad;
                var p = new Vector3(center.x + r * Mathf.Cos(a), transform.position.y, center.z + r * Mathf.Sin(a));
                var f = new Vector3(-Mathf.Sin(a), 0f, Mathf.Cos(a)) * sign;
                path.Add(new RouteSample { Pos = p, Forward = f.normalized, HalfWidth = ForestWidth / 2f, Road = true, Biome = biome });
            }
            routeLength += path.Length;
        }

        /// <summary>Кривая Эрмита из a в b с касательными m0, m1, по земле. skipFirst — a уже есть в пути (продолжаем его).</summary>
        void Hermite(RoutePath path, Vector3 a, Vector3 b, Vector3 m0, Vector3 m1, bool skipFirst)
        {
            Vector3 P(float t)
            {
                float t2 = t * t, t3 = t2 * t;
                return (2f * t3 - 3f * t2 + 1f) * a + (t3 - 2f * t2 + t) * m0 + (-2f * t3 + 3f * t2) * b + (t3 - t2) * m1;
            }
            float approx = 0f;
            Vector3 prev = a;
            for (int i = 1; i <= 50; i++) { var q = P(i / 50f); approx += Vector3.Distance(prev, q); prev = q; }
            int steps = Mathf.Max(8, Mathf.CeilToInt(approx / Step));
            for (int i = skipFirst ? 1 : 0; i <= steps; i++)
            {
                float t = i / (float)steps;
                var p = P(t);
                p.y = Mathf.Lerp(a.y, b.y, t);
                var f = P(Mathf.Min(1f, t + 0.01f)) - P(Mathf.Max(0f, t - 0.01f));
                f.y = 0f;
                path.Add(new RouteSample { Pos = p, Forward = f.normalized, HalfWidth = ForestWidth / 2f, Road = true, Biome = biome });
            }
            routeLength += approx;
            pos = b;
            yaw = Mathf.Atan2(m1.x, m1.z) * Mathf.Rad2Deg;
        }

        /// <summary>Развернуть путь: точки в обратном порядке, направления — наоборот.</summary>
        static void Reverse(RoutePath path)
        {
            var samples = new List<RouteSample>(path.Samples);
            samples.Reverse();
            var sf = path.StartForward;
            path.StartForward = path.EndForward.HasValue ? -path.EndForward.Value : (Vector3?)null;
            path.EndForward = sf.HasValue ? -sf.Value : (Vector3?)null;
            path.Samples.Clear();
            path.Dist.Clear();
            foreach (var s0 in samples)
            {
                var s = s0;
                s.Forward = -s.Forward;
                path.Add(s);
            }
        }

        /// <summary>Кольцо: каменная гряда между ветками (прячет шов земли половин и не даёт срезать).</summary>
        void CircuitRidge()
        {
            float mid = transform.position.x;
            if (!Any(rockModels)) return;
            foreach (var (z0, z1) in ridges)
                for (float z = z0; z < z1; z += RandRange(9f, 13f))
                {
                    var p = new Vector3(mid + RandRange(-3f, 3f), 0f, z);
                    if (!map.Nearest(p, false, out var h)) continue;
                    float r = Mathf.Min(13f, h.Distance - h.Sample.HalfWidth - 3f);   // не налезть на дорогу
                    if (r < 3f) continue;
                    float hgt = RandRange(1.1f, 1.6f) * r;
                    // Образец камня — по центру единичного куба: поднимаем, чтобы утонул лишь чуть-чуть.
                    Model(rockModels, p + Vector3.up * hgt * 0.35f, Quaternion.Euler(0f, RandRange(0f, 360f), 0f),
                          new Vector3(r * 2f, hgt, r * 2f * RandRange(0.8f, 1.2f)), Decor.Solid, true);
                }
        }

        /// <summary>
        /// Кольцо после постройки вида: ветку Б разворачиваем (по ней едут вниз), ущелья на ней получают копию
        /// пути как строили (тоннель считается по ней). Потом — вешки чекпоинтов (им нужен прогресс по ходу).
        /// </summary>
        void FinishCircuit()
        {
            foreach (var p in toReverse)
            {
                foreach (var c in canyons) if (c.Path == p) c.Path = Clone(p);
                Reverse(p);
            }
            toReverse.Clear();

            if (map.Checkpoints == null) return;
            foreach (float cp in map.Checkpoints)
            {
                var s = map.AtLap(cp);
                for (int k = -1; k <= 1; k += 2)
                {
                    Vector3 p = s.Pos + s.Right * k * (s.HalfWidth + 0.8f);
                    if (Any(postModels)) Model(postModels, p, Quaternion.LookRotation(s.Forward), new Vector3(1.4f, 2.6f, 1.4f), Decor.Solid, true);
                    else Piece("Чекпоинт_столб", cube, p + Vector3.up * 1.3f, Quaternion.identity, new Vector3(0.25f, 2.6f, 0.25f), Color.white, Col.Box);
                    Piece("Чекпоинт_флаг", cube, p + Vector3.up * 2.4f + s.Forward * 0.45f, Quaternion.LookRotation(s.Right), new Vector3(0.05f, 0.55f, 0.8f),
                          new Color(1f, 0.55f, 0.1f), Col.None);
                }
            }
        }

        static RoutePath Clone(RoutePath p)
        {
            var c = new RoutePath(p.Name) { StartProgress = p.StartProgress, ProgressScale = p.ProgressScale, BuildMesh = false };
            foreach (var s in p.Samples) c.Add(s);
            return c;
        }

        /// <summary>Кольцо: начала участков — в прогресс круга, по порядку (по ним погода).</summary>
        void CircuitSections()
        {
            var list = new List<(float p, Biome b)>();
            foreach (var m in circuitMarks) list.Add((map.Progress(m.reversed ? m.to : m.from), m.biome));
            list.Sort((a, c) => a.p.CompareTo(c.p));
            SectionStarts.Clear();
            SectionBiomes.Clear();
            SectionStarts.Add(0f);
            SectionBiomes.Add(BaseBiome);   // хвостик и ближний разворот — биом свалки
            foreach (var x in list) { SectionStarts.Add(x.p); SectionBiomes.Add(x.b); }
        }

        // ---------------- Биомы ----------------

        void BuildForest(int chunks, bool fork)
        {
            riverInSection = false;
            Run(main, 20f, RandRange(-35f, 35f), -Mathf.Min(pos.y, 1f), ForestWidth);
            if (test) { RiverCrossing(ForestWidth, 2); ForestSweep(); return; }   // тестовая: сразу река вброд
            int forkAt = fork ? rng.Next(0, chunks + 1) : -1;
            for (int i = 0; i < chunks; i++)
            {
                if (i == forkAt) ForestFork();
                if (Must(ForestWidth)) continue;
                // Река — не чаще одной на участок и не больше двух на трассу.
                int kinds = CanRiver ? 7 : 6;
                switch (rng.Next(kinds))
                {
                    case 0: ForestRoadChunk(); break;
                    case 1: ForestSlalom(); break;
                    case 2: ForestLogs(); break;
                    case 3: ForestHills(); break;
                    case 4: ForestSweep(); break;
                    case 5: CanyonCut(ForestWidth); break;   // лесной овраг с тоннелем
                    default: RiverCrossing(ForestWidth); break;
                }
            }
            if (forkAt == chunks) ForestFork();
        }

        void BuildMountains(int chunks, bool fork)
        {
            riverInSection = false;
            Run(main, 20f, RandRange(-35f, 35f), 0f, MountainWidth);
            if (test)
            {
                // Тестовая: сразу ущелье с тоннелем, потом развилка «тоннель вниз / перевал вверх».
                MountainCanyon();
                MountainTunnelFork(true);
                return;
            }
            // В горы — сначала вверх: развилке с прыжком нужна высота, иначе падать некуда.
            MountainClimb();
            if (rng.Next(2) == 0) MountainClimb();
            int forkAt = fork ? rng.Next(0, chunks) : -1;
            for (int i = 0; i < chunks; i++)
            {
                if (i == forkAt) MountainFork();
                if (Must(MountainWidth)) continue;
                switch (rng.Next(CanRiver ? 6 : 5))
                {
                    case 0: MountainSerpentine(); break;
                    case 1: MountainRockfall(); break;
                    case 2: MountainLedge(); break;
                    case 3: MountainDip(); break;
                    case 4: MountainCanyon(); break;                // ущелье ниже земли с тоннелем
                    default: RiverCrossing(MountainWidth); break;   // горная речка в долине
                }
            }
            // Спуск к равнине.
            while (pos.y > 3f) Run(main, 60f, RandRange(-20f, 20f), -Mathf.Min(pos.y - 1f, RandRange(8f, 12f)));
        }

        /// <summary>
        /// Трасса обучения: один лес без рек и развилок, ~1 км — на собранной из хлама машине
        /// это около 2–2,5 минут. Куски — все лесные по разу, чтобы попробовать и слалом, и брёвна, и горки.
        /// </summary>
        void TutorialForest()
        {
            riverInSection = true;   // без рек
            Run(main, 20f, RandRange(-20f, 20f), -Mathf.Min(pos.y, 1f), ForestWidth);
            ForestRoadChunk();
            ForestSweep();
            ForestSlalom();
            ForestRoadChunk();
            ForestLogs();
            ForestSweep();
            ForestHills();
            ForestRoadChunk();
            // +40%: обучение проходили и пешком — пусть заезд будет заметнее.
            ForestSlalom();
            ForestRoadChunk();
            ForestSweep();
        }

        // ---------------- Куски леса ----------------

        void ForestRoadChunk() => Run(main, 70f, RandRange(-30f, 30f), RandRange(-1.5f, 1.5f), ForestWidth);

        /// <summary>Слалом: деревья прямо на дороге, у краёв по очереди.</summary>
        void ForestSlalom()
        {
            float start = main.Length;
            float sign = rng.Next(2) == 0 ? 1f : -1f;
            for (int i = 0; i < 4; i++, sign = -sign) Run(main, 18f, 25f * sign, 0f, ForestWidth);
            int side = 1;
            for (float d = start + 10f; d < main.Length - 6f; d += 12f, side = -side)
            {
                var s = main.At(d);
                RoadJunk(s.Pos + s.Right * side * (s.HalfWidth - 2.2f), s.Pos.y);   // слалом — между кучами мусора
            }
        }

        /// <summary>Брёвна поперёк дороги — кочки, на которых лёгкие машины подпрыгивают.</summary>
        void ForestLogs()
        {
            float start = main.Length;
            Run(main, 60f, RandRange(-15f, 15f), 0f, ForestWidth);
            for (float d = start + 12f; d < main.Length - 8f; d += RandRange(11f, 16f))
                Log(main.At(d), RandRange(0.18f, 0.28f));
        }

        // ---------------- Реки ----------------

        const float WaterY = -0.4f;      // поверхность воды
        const float FordY = -1.5f;       // дорога вброд (глубина ~1.1 м: плавучие плывут, остальные гребут колёсами)
        const float RiverBedY = -2.6f;   // дно реки
        static readonly Color WaterColor = new Color(0.2f, 0.45f, 0.9f);

        /// <summary>
        /// Река поперёк карты (дорога идёт только вперёд по z — пересекает каждую один раз): русло петляет
        /// по синусоиде, но у брода идёт ровно поперёк дороги; берега пологие, песчаные.
        /// </summary>
        struct River
        {
            public float Zc, HalfW, RoadX, Amp, Freq, Phase, BankW;
            public Biome Biome;   // берега и вода — по биому участка
            /// <summary>Кольцо: река только на своей половине карты (вторая ветка идёт по другой земле).</summary>
            public float XMin, XMax;
            public bool HasX(float x) => x >= XMin && x <= XMax;

            /// <summary>Середина русла на этом x: у дороги — ровно Zc, дальше изгиб нарастает.</summary>
            public float CenterZ(float x)
            {
                float k = Mathf.Clamp01((Mathf.Abs(x - RoadX) - 15f) / 45f);
                k = k * k * (3f - 2f * k);
                return Zc + Amp * k * Mathf.Sin(x * Freq + Phase);
            }

            /// <summary>Полоса по z, где лежит река с берегами (дальше — обычная земля).</summary>
            public float ZMin => Zc - Amp - HalfW - BankW - 1f;
            public float ZMax => Zc + Amp + HalfW + BankW + 1f;
        }

        readonly List<River> rivers = new List<River>();
        const float RiverBank = 9f;   // ширина пологого пляжа от кромки воды до травы, м
        const float RiverCell = 9f;   // «клетка» ширины реки (≈ ширина дороги), м
        const float BridgeArch = 1.4f;   // горб моста посередине, м
        const float BridgeDeck = 0.45f;  // толщина настила, м
        bool riverInSection;

        /// <summary>Можно ли на этом участке реку: не больше одной на участок и двух на трассу.</summary>
        bool CanRiver => rivers.Count - riversBefore < 2 && !riverInSection;
        int riversBefore;   // кольцо: реки считаем на каждой ветке свои

        /// <summary>
        /// Река поперёк дороги (в любом биоме): спуск к воде, брод, подъём на другой берег.
        /// В горах дорога высоко — сначала спускаемся к равнине серпантином, а не обрывом.
        /// </summary>
        void RiverCrossing(float width, int forceCells = 0)
        {
            riverInSection = true;
            while (pos.y > 3f) Run(main, 40f, RandRange(-12f, 12f), -Mathf.Min(pos.y - 1f, 8f), width);
            if (pos.y > 0.3f) Run(main, 16f, 0f, -pos.y, width);
            Run(main, 14f, -yaw * 0.7f, 0f, width);   // почти поперёк реки
            // Ширина воды — от 1 до 4 клеток (клетка ≈ ширина дороги). Узкую переходим вброд, широкую — по мосту.
            int cells = forceCells > 0 ? forceCells : 1 + rng.Next(4);
            float waterW = cells * RiverCell;
            float z0 = pos.z, x0 = pos.x, w0, w1;
            if (cells < 3)
            {
                // Брод: спуск, ровное дно, подъём. Дорога уходит под воду примерно на трети спуска.
                float L = Mathf.Clamp(waterW * 0.55f, 6f, 10f), edge = 0.33f * L;
                float flat = Mathf.Max(2f, waterW - 2f * (L - edge));
                Run(main, L, 0f, FordY - pos.y, width + 1f, FordY);
                Run(main, flat, RandRange(-4f, 4f), 0f, width + 1f, FordY);
                Run(main, L, 0f, -FordY, width, FordY);
                w0 = z0 + edge; w1 = pos.z - edge;
            }
            else
            {
                // Мост: с берега на берег над водой, с небольшим горбом посередине.
                const float pad = 5f;   // на пляже по краям
                float span = waterW + 2f * pad;
                bridgeMode = true;
                Run(main, span / 2f, 0f, BridgeArch, width);
                Run(main, span / 2f, 0f, -BridgeArch, width);
                bridgeMode = false;
                w0 = z0 + pad; w1 = pos.z - pad;
            }
            float x1 = pos.x;
            rivers.Add(new River
            {
                Zc = (w0 + w1) / 2f, HalfW = (w1 - w0) / 2f, RoadX = (x0 + x1) / 2f,
                Amp = RandRange(6f, 10f), Freq = 2f * Mathf.PI / RandRange(90f, 150f), Phase = RandRange(0f, 2f * Mathf.PI),
                BankW = RiverBank, Biome = biome,
                XMin = legXMin, XMax = legXMax,
            });
            Run(main, 12f, 0f, 0f, width);
        }

        /// <summary>В низине: река с пляжем или ущелье — без деревьев, камней и гор (имя — историческое).</summary>
        bool InRiver(Vector3 p, float margin = 1f)
        {
            if (NearRiver(p, margin)) return true;
            foreach (var c in canyons) if (c.Covers(p, margin)) return true;
            return false;
        }

        bool NearRiver(Vector3 p, float margin)
        {
            foreach (var r in rivers)
                if (r.HasX(p.x) && Mathf.Abs(p.z - r.CenterZ(p.x)) < r.HalfW + r.BankW * 0.5f + margin)
                    return true;
            return false;
        }

        RouteRailway.CrossingSite RailwayCrossingSite(Vector3 p, float margin)
        {
            if (NearRiver(p, margin) || OnUnderpass(p, margin))
                return RouteRailway.CrossingSite.Blocked;
            foreach (var c in canyons)
            {
                if (!c.Covers(p, margin)) continue;
                if (p.y >= GroundY - 0.1f && InsideTunnel(new Vector3(p.x, GroundY - TunnelRoof, p.z)))
                    return RouteRailway.CrossingSite.AboveTunnel;
                return RouteRailway.CrossingSite.Blocked;
            }
            return RouteRailway.CrossingSite.Surface;
        }

        /// <summary>Точка дороги под водой (брод): столбики и мусор там ни к чему.</summary>
        bool UnderWater(Vector3 p)
        {
            if (p.y >= WaterY + 0.2f) return false;
            foreach (var r in rivers) if (r.HasX(p.x) && Mathf.Abs(p.z - r.CenterZ(p.x)) < r.HalfW + 3f) return true;
            return false;
        }

        /// <summary>Холмы: дорога то вверх, то вниз — лёгкие машины подлетают на гребнях.</summary>
        void ForestHills()
        {
            float sign = rng.Next(2) == 0 ? 1f : -1f;
            for (int i = 0; i < 4; i++, sign = -sign)
                Run(main, RandRange(14f, 20f), RandRange(-10f, 10f), sign * RandRange(2.5f, 4.5f), ForestWidth);
            if (pos.y > 1f) Run(main, 16f, 0f, -pos.y, ForestWidth);
        }

        /// <summary>Длинная широкая дуга — можно разогнаться.</summary>
        void ForestSweep()
        {
            float sign = rng.Next(2) == 0 ? 1f : -1f;
            Run(main, 50f, 50f * sign, 0f, ForestWidth + 2f);
            Run(main, 50f, -60f * sign, 0f, ForestWidth);
        }

        void ForestFork()
        {
            var pool = new List<Branch>
            {
                new Branch { Name = "Тропа", Width = 5f, Bulge = 10f, Wobble = 5f, Waves = 3, Kind = BranchKind.Trail },
                new Branch { Name = "Объезд", Width = ForestWidth, Bulge = 26f, Wobble = 0f, Waves = 0, Hill = 2.5f, Kind = BranchKind.Plain },
                new Branch { Name = "Горка", Width = 7f, Bulge = 16f, Wobble = 0f, Waves = 0, Hill = 6f, Kind = BranchKind.Plain },
                new Branch { Name = "Змейка", Width = 6f, Bulge = 14f, Wobble = 8f, Waves = 4, Kind = BranchKind.Plain },
            };
            Shuffle(pool);
            Fork(pool[0], pool[1], RandRange(90f, 130f));
        }

        // ---------------- Куски гор ----------------

        void MountainClimb() => Run(main, 70f, RandRange(-20f, 20f), RandRange(8f, 12f), MountainWidth);

        void MountainSerpentine()
        {
            float sign = rng.Next(2) == 0 ? 1f : -1f;
            Run(main, 18f, 40f * sign, 2f, MountainWidth);
            Run(main, 26f, -80f * sign, 3f);
            Run(main, 26f, 80f * sign, 3f);
            Run(main, 18f, -40f * sign, 2f);
        }

        /// <summary>Камнепад: валуны на дороге, объезжай.</summary>
        void MountainRockfall()
        {
            float start = main.Length;
            Run(main, 60f, RandRange(-20f, 20f), RandRange(-2f, 2f), MountainWidth);
            for (float d = start + 10f; d < main.Length - 8f; d += RandRange(8f, 13f))
            {
                var s = main.At(d);
                float r = RandRange(0.5f, 0.9f);
                float lat = RandRange(-(s.HalfWidth - r - 0.5f), s.HalfWidth - r - 0.5f);
                Boulder(s.Pos + s.Right * lat + Vector3.up * (r * 0.7f), r, true);
            }
        }

        /// <summary>Карниз: узкая дорога с длинным поворотом над обрывом.</summary>
        void MountainLedge()
        {
            float sign = rng.Next(2) == 0 ? 1f : -1f;
            Run(main, 14f, 0f, 1f, 5.5f);
            Run(main, 50f, 55f * sign, RandRange(2f, 5f), 5.5f);
            Run(main, 16f, -25f * sign, 0f, MountainWidth);
        }

        /// <summary>Провал: вниз в ложбину и снова вверх.</summary>
        void MountainDip()
        {
            float depth = Mathf.Min(pos.y - 2f, RandRange(6f, 10f));
            if (depth < 3f) { MountainClimb(); return; }
            Run(main, 35f, RandRange(-15f, 15f), -depth, MountainWidth);
            Run(main, 15f, 0f, 0f, MountainWidth + 2f);
            Run(main, 35f, RandRange(-15f, 15f), depth, MountainWidth);
        }

        // ---------------- Ущелье с тоннелем ----------------

        const float CanyonShoulder = 1.5f;   // обочина на дне ущелья, м
        const float CanyonWall = 3.5f;       // ширина скальной стены по горизонтали, м

        /// <summary>
        /// Ущелье: кусок дороги ниже уровня земли между скальными стенами; в середине — тоннель
        /// (крыша вровень с землёй). Rows — точки дороги по ущелью (заполняются после сглаживания).
        /// </summary>
        sealed class Canyon
        {
            public RoutePath Path;
            public int From, To;                  // точки пути: начало спуска и конец подъёма
            public bool Right;                    // кольцо: на правой половине карты
            public Biome Biome = Biome.Mountains; // земля и стены ущелья — по биому
            public float TunnelFrom, TunnelTo;    // тоннель: расстояния по пути
            public readonly List<RouteSample> Rows = new List<RouteSample>();

            public void Fill()
            {
                Rows.Clear();
                for (int i = From; i <= To && i < Path.Samples.Count; i++) Rows.Add(Path.Samples[i]);
            }

            public float ZMin => Rows.Count > 0 ? Rows[0].Pos.z : 0f;
            public float ZMax => Rows.Count > 0 ? Rows[Rows.Count - 1].Pos.z : 0f;

            /// <summary>Точка дороги ущелья на этом z (Rows идут вперёд по z).</summary>
            public bool At(float z, out RouteSample s)
            {
                s = default;
                if (Rows.Count < 2 || z < ZMin || z > ZMax) return false;
                for (int i = 0; i + 1 < Rows.Count; i++)
                {
                    var a = Rows[i]; var b = Rows[i + 1];
                    if (z < a.Pos.z || z > b.Pos.z) continue;
                    float t = b.Pos.z - a.Pos.z > 1e-4f ? (z - a.Pos.z) / (b.Pos.z - a.Pos.z) : 0f;
                    s = RoutePath.Lerp(a, b, t);
                    return true;
                }
                return false;
            }

            /// <summary>Полуширина по x (дорога может идти наискосок).</summary>
            public static float HalfX(RouteSample s) => s.HalfWidth / Mathf.Max(0.6f, Mathf.Abs(s.Forward.z));

            /// <summary>
            /// Предмет с центром p и радиусом margin задевает ущелье (дно или стены, там где дорога ниже земли):
            /// проверяем все строки ущелья в пределах его размера по z, а не только под центром.
            /// </summary>
            public bool Covers(Vector3 p, float margin)
            {
                if (Rows.Count < 2 || p.z + margin < ZMin || p.z - margin > ZMax) return false;
                foreach (var s in Rows)
                {
                    if (s.Pos.y > -0.8f || Mathf.Abs(s.Pos.z - p.z) > margin + 1f) continue;
                    if (Mathf.Abs(p.x - s.Pos.x) < HalfX(s) + CanyonShoulder + CanyonWall + margin) return true;
                }
                return false;
            }
        }

        readonly List<Canyon> canyons = new List<Canyon>();

        /// <summary>Ущелье: спуск ниже земли, дно между скалами (в середине тоннель), подъём обратно.</summary>
        void MountainCanyon() => CanyonCut(MountainWidth);

        /// <summary>
        /// Ущелье с тоннелем — в любом биоме (земля и стены — его): спуск ниже земли, дно, в середине тоннель, подъём.
        /// </summary>
        void CanyonCut(float w)
        {
            // К земле (в горах дорога высоко), чуть выпрямиться — ущелье идёт вперёд.
            while (pos.y > 3f) Run(main, 40f, RandRange(-12f, 12f), -Mathf.Min(pos.y - 1f, 8f), w);
            if (pos.y > 0.2f) Run(main, 14f, 0f, -pos.y, w);
            Run(main, 10f, -yaw * 0.5f, 0f, w);
            int from = main.Samples.Count - 1;
            float depth = RandRange(6.5f, 8f);
            Run(main, 34f, RandRange(-8f, 8f), -depth, w, -depth);
            float bottom0 = main.Length;
            float tunnel = RandRange(34f, 100f);   // длина тоннеля, м
            Run(main, tunnel + 12f, RandRange(-14f, 14f), 0f, w, -depth);
            Run(main, 34f, RandRange(-8f, 8f), depth, w, -depth);
            canyons.Add(new Canyon
            {
                Right = legXMin > -1e8f,
                Biome = biome,
                Path = main, From = from, To = main.Samples.Count - 1,
                TunnelFrom = bottom0 + 6f, TunnelTo = bottom0 + 6f + tunnel,
            });
            Run(main, 10f, 0f, 0f, w);
        }

        void MountainFork()
        {
            var jump = new Branch { Name = "Прыжок", Width = MountainWidth, Bulge = 8f, Wobble = 0f, Waves = 0, Kind = BranchKind.Jump };
            var pool = new List<Branch>
            {
                new Branch { Name = "Серпантин", Width = 7f, Bulge = 22f, Wobble = 9f, Waves = 4, Kind = BranchKind.Plain },
                new Branch { Name = "Перевал", Width = 7f, Bulge = 18f, Wobble = 0f, Waves = 0, Hill = 7f, Kind = BranchKind.Plain },
                new Branch { Name = "Тропа", Width = 5f, Bulge = 12f, Wobble = 4f, Waves = 3, Kind = BranchKind.Trail },
            };
            Shuffle(pool);
            // Развилка с тоннелем (раз из трёх): одна ветка уходит вниз под землю, вторая — вверх («Перевал»)
            // или серпантином / тропой.
            if (rng.Next(3) == 0) { MountainTunnelFork(false); return; }
            // Прыжок — в двух случаях из трёх, иначе две обычные ветки.
            if (rng.Next(3) > 0) Fork(jump, pool[0], 110f);
            else Fork(pool[0], pool[1], RandRange(100f, 130f));
        }

        // ---------------- Участок: по биому ----------------

        void BuildSection(Biome b, int chunks, bool fork)
        {
            switch (b)
            {
                case Biome.Forest: BuildForest(chunks, fork); break;
                case Biome.Mountains: BuildMountains(chunks, fork); break;
                case Biome.Desert: BuildDesert(chunks, fork); break;
                default: BuildSnow(chunks, fork); break;
            }
        }

        // ---------------- Пустыня ----------------

        const float DesertWidth = 10f;
        static readonly Color DesertRoad = new Color(0.78f, 0.64f, 0.45f), DesertSide = new Color(0.7f, 0.55f, 0.38f);

        /// <summary>Пустыня: длинные прямые, дюны-горки, мусорный слалом, оазис-река, каньон с тоннелем.</summary>
        void BuildDesert(int chunks, bool fork)
        {
            riverInSection = false;
            Run(main, 20f, RandRange(-30f, 30f), -Mathf.Min(pos.y, 2f), DesertWidth);
            int forkAt = fork ? rng.Next(0, chunks + 1) : -1;
            for (int i = 0; i < chunks; i++)
            {
                if (i == forkAt) ForestFork();
                if (Must(DesertWidth)) continue;
                switch (rng.Next(CanRiver ? 6 : 5))
                {
                    case 0: DesertStraight(); break;
                    case 1: DesertDunes(); break;
                    case 2: DesertWrecks(); break;
                    case 3: DesertSweep(); break;
                    case 4: CanyonCut(DesertWidth); break;      // каньон с тоннелем в скале
                    default: RiverCrossing(DesertWidth); break;  // река через оазис
                }
            }
            if (forkAt == chunks) ForestFork();
            while (pos.y > 2f) Run(main, 40f, RandRange(-15f, 15f), -Mathf.Min(pos.y, RandRange(6f, 9f)), DesertWidth);
            Run(main, 12f, 0f, -pos.y, DesertWidth);
        }

        /// <summary>Длинная прямая — газ в пол.</summary>
        void DesertStraight() => Run(main, 90f, RandRange(-18f, 18f), RandRange(-1f, 1f), DesertWidth);

        /// <summary>Дюны: дорога волнами повыше лесных горок — на гребнях машины взлетают.</summary>
        void DesertDunes()
        {
            float sign = rng.Next(2) == 0 ? 1f : -1f;
            for (int i = 0; i < 5; i++, sign = -sign)
                Run(main, RandRange(13f, 18f), RandRange(-12f, 12f), sign * RandRange(3f, 5.5f), DesertWidth);
            if (pos.y > 1f) Run(main, 16f, 0f, -pos.y, DesertWidth);
        }

        /// <summary>Брошенный хлам на дороге: слалом между кучами мусора.</summary>
        void DesertWrecks()
        {
            float start = main.Length;
            float sign = rng.Next(2) == 0 ? 1f : -1f;
            for (int i = 0; i < 3; i++, sign = -sign) Run(main, 22f, 20f * sign, 0f, DesertWidth + 2f);
            int side = 1;
            for (float d = start + 10f; d < main.Length - 6f; d += 11f, side = -side)
            {
                var s = main.At(d);
                RoadJunk(s.Pos + s.Right * side * (s.HalfWidth - 2.4f), s.Pos.y);
            }
        }

        /// <summary>Широкая дуга по равнине.</summary>
        void DesertSweep()
        {
            float sign = rng.Next(2) == 0 ? 1f : -1f;
            Run(main, 60f, 45f * sign, 0f, DesertWidth + 2f);
            Run(main, 60f, -55f * sign, 0f, DesertWidth);
        }

        /// <summary>Пустыня у дороги: камни, сухие коряги, ржавый хлам, редкие кусты; подальше — скалы-столовые горы.</summary>
        void DecorateDesert(RouteSample s, int side)
        {
            if (Any(stoneModels) && rng.NextDouble() < 0.35)
                Scatter(stoneModels, s, side, 1f, 14f, 0.3f, 0.9f, 0.8f, true);
            if (Any(stumpModels) && rng.NextDouble() < 0.12)
                Scatter(stumpModels, s, side, 2f, 18f, 0.4f, 0.8f, 1f, true);
            if (Any(bushModels) && rng.NextDouble() < 0.05)
                Scatter(bushModels, s, side, 2f, 15f, 0.4f, 0.8f, 1f);
            if (Any(junkModels) && rng.NextDouble() < 0.05)
                Scatter(junkModels, s, side, 1.5f, 10f, 0.6f, 1f, 1f, true);
            // Редкие выжженные деревья и ёлки (пыльно-рыжая крона).
            if (Any(desertTreeModels) && rng.NextDouble() < 0.1)
            {
                Vector3 p = s.Pos + s.Right * side * (s.HalfWidth + RandRange(4f, 35f)) + s.Forward * RandRange(-2f, 2f);
                p.y = GroundY;
                if (map.IsClear(p, 2f) && !InRiver(p, 2f))
                {
                    float h = RandRange(3.5f, 6.5f);
                    Hidden(Piece("Ствол", cylinder, p + Vector3.up * (h * 0.3f), Quaternion.identity, new Vector3(0.4f, h * 0.3f, 0.4f), Color.white, Col.Capsule, Decor.Tree));
                    Model(desertTreeModels, p, Quaternion.Euler(0f, RandRange(0f, 360f), 0f), Vector3.one * h, Decor.Tree);
                }
            }
            // Скалы средней дистанции и столовые горы на горизонте — вместо леса.
            if (Any(rockModels) && rng.NextDouble() < 0.14)
            {
                float r = RandRange(3f, 8f);
                Vector3 p = s.Pos + s.Right * side * (s.HalfWidth + r * 1.2f + RandRange(8f, 40f));
                p.y = GroundY;
                if (map.IsClear(p, r * 1.15f) && !InRiver(p, r * 1.15f))
                    Model(rockModels, new Vector3(p.x, GroundY + r * 0.7f, p.z), Quaternion.Euler(0f, RandRange(0f, 360f), 0f),
                          new Vector3(r * 2.2f, r * 1.6f, r * 2f), Decor.Tree);
            }
            if (Any(rockModels) && rng.NextDouble() < 0.06)
            {
                float r = RandRange(14f, 30f);
                Vector3 p = s.Pos + s.Right * side * (s.HalfWidth + r * 1.3f + RandRange(20f, 60f));
                if (map.IsClear(new Vector3(p.x, GroundY, p.z), r * 1.25f) && !InRiver(p, r * 1.25f))
                {
                    float h = RandRange(0.35f, 0.6f);   // плоские — «столовые»
                    Model(rockModels, new Vector3(p.x, GroundY + r * h * 0.5f, p.z), Quaternion.Euler(0f, RandRange(0f, 360f), 0f),
                          new Vector3(r * 2f, r * h * 1.6f, r * 2f * RandRange(0.8f, 1.2f)), Decor.Far);
                }
            }
            RoadsideExtras(s, side, false);
        }

        // ---------------- Куски снега ----------------

        const float SnowWidth = 9f;

        /// <summary>Начало каждого участка по z и его биом (дорога идёт только вперёд по z): по ним земля.</summary>
        List<(float z, Biome biome)> zones = new List<(float, Biome)>();

        void BuildSnow(int chunks, bool fork)
        {
            riverInSection = false;
            if (test)
            {
                Run(main, 20f, RandRange(-30f, 30f), -Mathf.Min(pos.y, 2f), SnowWidth);
                RiverCrossing(SnowWidth, 4);   // тестовая: широкая река — по мосту
                SnowDrifts();
                Run(main, 12f, 0f, -pos.y, SnowWidth);
                return;
            }
            // Сначала к равнине (после гор).
            Run(main, 20f, RandRange(-30f, 30f), -Mathf.Min(pos.y, 2f), SnowWidth);
            int forkAt = fork ? rng.Next(0, chunks + 1) : -1;
            for (int i = 0; i < chunks; i++)
            {
                if (i == forkAt) SnowFork();
                if (Must(SnowWidth)) continue;
                switch (rng.Next(CanRiver ? 6 : 5))
                {
                    case 0: SnowRoadChunk(); break;
                    case 1: SnowDrifts(); break;
                    case 2: SnowPass(); break;
                    case 3: SnowWaves(); break;
                    case 4: CanyonCut(SnowWidth); break;     // снежный каньон с тоннелем
                    default: RiverCrossing(SnowWidth); break;   // ледяная речка
                }
            }
            if (forkAt == chunks) SnowFork();
            while (pos.y > 2f) Run(main, 40f, RandRange(-15f, 15f), -Mathf.Min(pos.y, RandRange(6f, 9f)), SnowWidth);
            Run(main, 12f, 0f, -pos.y, SnowWidth);
        }

        void SnowRoadChunk() => Run(main, 70f, RandRange(-35f, 35f), RandRange(-1f, 1f), SnowWidth);

        /// <summary>Сугробы поперёк дороги: мягкие кочки, на скорости машину подбрасывает.</summary>
        void SnowDrifts()
        {
            float start = main.Length;
            Run(main, 70f, RandRange(-20f, 20f), 0f, SnowWidth);
            for (float d = start + 12f; d < main.Length - 8f; d += RandRange(9f, 15f))
            {
                var s = main.At(d);
                // Либо через всю дорогу, либо с одного края — можно объехать.
                if (rng.Next(3) == 0) Drift(s, 0f, s.HalfWidth * 2.2f, RandRange(0.25f, 0.4f));
                else
                {
                    float side = rng.Next(2) == 0 ? -1f : 1f, len = s.HalfWidth * RandRange(0.9f, 1.3f);
                    Drift(s, side * (s.HalfWidth - len / 2f + 0.5f), len, RandRange(0.35f, 0.55f));
                }
            }
        }

        /// <summary>Перевал: подъём, гребень, спуск — невысокий, с поворотами.</summary>
        void SnowPass()
        {
            float h = RandRange(6f, 10f), sign = rng.Next(2) == 0 ? 1f : -1f;
            Run(main, 45f, 30f * sign, h, SnowWidth - 1f);
            Run(main, 20f, -30f * sign, 0f, SnowWidth - 1f);
            Run(main, 45f, RandRange(-20f, 20f), -Mathf.Min(pos.y, h), SnowWidth);
        }

        /// <summary>Волны наметённого снега: пологие горки подряд.</summary>
        void SnowWaves()
        {
            float sign = rng.Next(2) == 0 ? 1f : -1f;
            for (int i = 0; i < 5; i++, sign = -sign)
                Run(main, RandRange(10f, 14f), RandRange(-12f, 12f), sign * RandRange(1.5f, 2.5f), SnowWidth);
            if (pos.y > 0.5f) Run(main, 14f, 0f, -pos.y, SnowWidth);
        }

        void SnowFork()
        {
            var pool = new List<Branch>
            {
                new Branch { Name = "Целина", Width = 5f, Bulge = 10f, Wobble = 5f, Waves = 3, Kind = BranchKind.Trail },
                new Branch { Name = "Наст", Width = SnowWidth, Bulge = 24f, Wobble = 0f, Waves = 0, Kind = BranchKind.Plain },
                new Branch { Name = "Сопка", Width = 7f, Bulge = 16f, Wobble = 0f, Waves = 0, Hill = 6f, Kind = BranchKind.Plain },
                new Branch { Name = "Позёмка", Width = 6f, Bulge = 14f, Wobble = 7f, Waves = 4, Kind = BranchKind.Plain },
            };
            Shuffle(pool);
            Fork(pool[0], pool[1], RandRange(90f, 130f));
        }

        /// <summary>
        /// Сугроб на дороге: приплюснутый эллипсоид, утопленный в дорогу, — кочка высотой height.
        /// lateral — смещение центра от оси, length — длина поперёк дороги.
        /// </summary>
        void Drift(RouteSample s, float lateral, float length, float height)
        {
            float depth = RandRange(1.6f, 2.4f);           // вдоль дороги
            float sy = height * 3f;                        // высота эллипсоида; торчит треть
            var rot = Quaternion.LookRotation(s.Forward) * Quaternion.Euler(0f, RandRange(-8f, 8f), 0f);
            var go = Piece("Сугроб", sphere, s.Pos + s.Right * lateral + Vector3.up * (height - sy / 2f), rot,
                           new Vector3(length, sy, depth), SnowWhite, Col.None);
            go.AddComponent<MeshCollider>().sharedMesh = sphere;
        }

        /// <summary>Солнце под своим углом на каждой трассе: тени и свет другие — и трасса ощущается иначе.</summary>
        void ApplySun()
        {
            if (!sunPicked) return;
            var sun = RenderSettings.sun;
            if (sun == null)
                foreach (var l in FindObjectsByType<Light>(FindObjectsSortMode.None))
                    if (l.type == LightType.Directional) { sun = l; break; }
            if (sun == null) return;
            if (!sunBaseSaved) { sunBaseColor = sun.color; sunBaseSaved = true; }
            sun.transform.rotation = sunPick;
            // Чуть теплее или холоднее: утро, день, вечер.
            float warm = sunWarm;
            sun.color = sunBaseColor * new Color(1f + 0.08f * warm, 1f, 1f - 0.1f * warm);
        }

        Quaternion sunPick;
        float sunWarm;
        bool sunPicked;

        /// <summary>Солнце для трассы — по сиду, в том же месте rng, что и раньше; ставится в ApplyPickedSun.</summary>
        void PickSun()
        {
            sunPick = Quaternion.Euler(RandRange(28f, 62f), RandRange(0f, 360f), 0f);
            sunWarm = RandRange(-1f, 1f);
            sunPicked = true;
        }

        void ApplyPickedSun() { if (sunPicked) ApplySun(); }

        bool sunBaseSaved;
        Color sunBaseColor;

        // ---------------- Дорога ----------------

        void AddSample(RoutePath path, bool road = true)
        {
            path.Add(new RouteSample
            {
                Pos = pos,
                Forward = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward,
                HalfWidth = width / 2f,
                Road = road,
                Biome = biome,
                Bridge = bridgeMode,
            });
        }

        bool bridgeMode;   // точки, которые сейчас кладём, — мост

        /// <summary>
        /// Проехать length метров: повернуть на turn градусов (плавной дугой), подняться на rise
        /// (плавно в начале и в конце), ширина плавно меняется к widthTo.
        /// </summary>
        void Run(RoutePath path, float length, float turn, float rise, float widthTo = -1f, float minY = 0f)
        {
            float yaw0 = yaw, y0 = pos.y, w0 = width, w1 = widthTo > 0f ? widthTo : width;
            float lo = -MaxYaw, hi = MaxYaw;
            if (laneHalf > 0f)
            {
                // Кольцо: ветка держится своей полосы — у края разрешён поворот только обратно к середине полосы.
                float k = Mathf.Clamp((pos.x - laneX) / laneHalf, -1.5f, 1.5f);
                if (k > 0f) hi = Mathf.Lerp(MaxYaw, -35f, Mathf.Clamp01(k));
                else lo = Mathf.Lerp(-MaxYaw, 35f, Mathf.Clamp01(-k));
            }
            turn = Mathf.Clamp(yaw0 + turn, lo, hi) - yaw0;
            rise = Mathf.Clamp(y0 + rise, minY, MaxHeight) - y0;
            int steps = Mathf.Max(1, Mathf.CeilToInt(length / Step));
            float ds = length / steps;
            routeLength += length;
            for (int i = 1; i <= steps; i++)
            {
                float t = i / (float)steps;
                yaw = yaw0 + turn * t;
                Vector3 dir = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
                pos += dir * ds;
                pos.y = y0 + rise * Smooth(t);
                width = Mathf.Lerp(w0, w1, t);
                AddSample(path);
            }
        }

        enum BranchKind { Plain, Trail, Jump, Tunnel }

        struct Branch
        {
            public string Name;
            public float Width, Bulge, Wobble, Hill;
            public int Waves;
            public BranchKind Kind;
        }

        /// <summary>
        /// Развилка: дорога расширяется в площадку, дальше две ветки (влево и вправо),
        /// которые через length метров сходятся в такую же площадку.
        /// </summary>
        void Fork(Branch left, Branch right, float length)
        {
            forks++;
            // Ветки выходят вплотную друг к другу (без проёма): щель между ними растёт постепенно,
            // а начало клина закрывает заливка (BuildForkFill) — на высокой дороге не провалиться.
            float w = width, gap = 0f;
            // Каждая ветка — своим внутренним краем ровно по оси: узкая тропа не оставляет проёма у широкой.
            float offL = left.Width / 2f + gap / 2f, offR = right.Width / 2f + gap / 2f;
            float plaza = Mathf.Max(offL + left.Width / 2f, offR + right.Width / 2f) * 2f;
            Run(main, 12f, -yaw * 0.5f, 0f, plaza);   // заодно чуть выпрямляемся

            Vector3 start = pos;
            Quaternion rot = Quaternion.Euler(0f, yaw, 0f);
            Vector3 fwd = rot * Vector3.forward, rightDir = rot * Vector3.right;
            float forkProgress = main.StartProgress + main.Length;
            main.EndForward = fwd;
            var pair = new[] { BuildBranch(left, start - rightDir * offL, fwd, rightDir, -1f, length),
                               BuildBranch(right, start + rightDir * offR, fwd, rightDir, 1f, length) };
            forkFills.Add((pair[0], pair[1]));
            foreach (var bp in pair)
            {
                bp.StartProgress = forkProgress;
                bp.ProgressScale = bp.Length > 1f ? length / bp.Length : 1f;
            }

            // Основная дорога продолжается от места встречи веток — новым путём, без перемычки посередине.
            pos = start + fwd * length;
            routeLength += length;
            main = map.NewPath("Дорога");
            main.StartForward = fwd;
            main.StartProgress = forkProgress + length;
            AddSample(main);
            Run(main, 12f, 0f, 0f, w);
        }

        RoutePath BuildBranch(Branch b, Vector3 start, Vector3 fwd, Vector3 rightDir, float side, float length)
        {
            var path = map.NewPath(b.Name);
            path.StartForward = path.EndForward = fwd;
            int n = Mathf.CeilToInt(length / 1f);
            for (int i = 0; i <= n; i++)
            {
                float t = i / (float)n;
                float bump = Mathf.Sin(Mathf.PI * t);
                bump *= bump;   // 0 на концах вместе с производной — ветки выходят и входят прямо
                float lateral = side * b.Bulge * bump + b.Wobble * Mathf.Sin(2f * Mathf.PI * b.Waves * t) * bump;
                float h = b.Hill * bump;
                bool road = true;
                if (b.Kind == BranchKind.Jump) h += JumpProfile(t, out road);
                if (b.Kind == BranchKind.Tunnel) h += TunnelProfile(t);
                Vector3 p = start + fwd * (length * t) + rightDir * lateral;
                p.y = start.y + h;
                path.Add(new RouteSample { Pos = p, Forward = fwd, HalfWidth = b.Width / 2f, Road = road, Biome = biome });
            }
            path.SmoothForwards();   // до расстановки препятствий: им нужно верное «вбок»

            // Ветка вниз: ущелье с тоннелем на дне (тоннель — середина ровного дна).
            if (b.Kind == BranchKind.Tunnel)
                canyons.Add(new Canyon
                {
                    Right = legXMin > -1e8f,
                    Path = path, From = 0, To = path.Samples.Count - 1,
                    TunnelFrom = path.Dist[Mathf.RoundToInt(n * 0.36f)], TunnelTo = path.Dist[Mathf.RoundToInt(n * 0.64f)],
                });

            // Препятствия веток.
            if (b.Kind == BranchKind.Trail && biome == Biome.Snow)
            {
                // Целина: сугробы у краёв по очереди — петляй между ними.
                float k = rng.Next(2) == 0 ? -1f : 1f;
                for (float d = 10f; d < path.Length - 10f; d += RandRange(8f, 12f), k = -k)
                {
                    var s = path.At(d);
                    if (s.Road) Drift(s, k * s.HalfWidth * 0.45f, s.HalfWidth * 1.2f, RandRange(0.35f, 0.5f));
                }
            }
            else if (b.Kind == BranchKind.Trail)
            {
                for (float d = 10f; d < path.Length - 10f; d += RandRange(7f, 11f))
                {
                    var s = path.At(d);
                    RoadJunk(s.Pos + s.Right * (rng.Next(2) == 0 ? -1f : 1f) * (s.HalfWidth - 1.1f), s.Pos.y);
                }
                Log(path.At(path.Length * 0.3f), 0.2f);
                Log(path.At(path.Length * 0.7f), 0.2f);
            }
            return path;
        }

        const float TunnelBranchDepth = 6.5f;

        /// <summary>
        /// Развилка: одна ветка — «Тоннель» (под землю), вторая — «Перевал» (вверх) или серпантин / тропа.
        /// upAndDown — вторая точно «Перевал». Тоннелю нужна земля — если дорога высоко, сперва спускаемся.
        /// </summary>
        void MountainTunnelFork(bool upAndDown)
        {
            var pool = new List<Branch>
            {
                new Branch { Name = "Серпантин", Width = 7f, Bulge = 22f, Wobble = 9f, Waves = 4, Kind = BranchKind.Plain },
                new Branch { Name = "Перевал", Width = 7f, Bulge = 18f, Wobble = 0f, Waves = 0, Hill = 7f, Kind = BranchKind.Plain },
                new Branch { Name = "Тропа", Width = 5f, Bulge = 12f, Wobble = 4f, Waves = 3, Kind = BranchKind.Trail },
            };
            Shuffle(pool);
            while (pos.y > 3f) Run(main, 40f, RandRange(-12f, 12f), -Mathf.Min(pos.y - 1f, 8f), MountainWidth);
            if (pos.y > 0.2f) Run(main, 14f, 0f, -pos.y, MountainWidth);
            var tunnel = new Branch { Name = "Тоннель", Width = MountainWidth, Bulge = 22f, Wobble = 0f, Waves = 0, Kind = BranchKind.Tunnel };
            var other = upAndDown || rng.Next(2) == 0 ? pool.Find(x => x.Name == "Перевал") : pool[0];
            float len = RandRange(150f, 190f);   // дно ~60 м: тоннель не короче 34 м
            if (rng.Next(2) == 0) Fork(tunnel, other, len); else Fork(other, tunnel, len);
        }

        /// <summary>Ветка-тоннель: спуск ниже земли, длинное ровное дно (там тоннель), подъём обратно.</summary>
        static float TunnelProfile(float t)
        {
            if (t < 0.15f || t > 0.85f) return 0f;
            if (t < 0.32f) return -TunnelBranchDepth * Smooth((t - 0.15f) / 0.17f);
            if (t < 0.68f) return -TunnelBranchDepth;
            return -TunnelBranchDepth * (1f - Smooth((t - 0.68f) / 0.17f));
        }

        /// <summary>
        /// Ветка-прыжок: трамплин 1.6 м → пропасть ~8 м → посадка чуть ниже → обратно к уровню дороги.
        /// Долетает с ~35 км/ч; медленная машина падает — и её вернёт к трамплину.
        /// </summary>
        static float JumpProfile(float t, out bool road)
        {
            road = !(t > 0.48f && t < 0.55f);
            if (t <= 0.40f) return 0f;
            if (t <= 0.48f) return 1.6f * Smooth((t - 0.40f) / 0.08f);
            if (t < 0.55f) return 0f;
            if (t <= 0.72f) return Mathf.Lerp(-0.3f, 0f, Smooth((t - 0.55f) / 0.17f));
            return 0f;
        }

        // ============================ Геометрия дороги ============================

        /// <summary>
        /// Меш дороги: верх (по биомам) и откосы до земли — горная дорога выглядит насыпью.
        /// Над пропастью дороги нет, края закрыты торцами.
        /// </summary>
        void BuildRoadMesh(RoutePath path)
        {
            var s = path.Samples;
            if (s.Count < 2) return;
            var verts = new List<Vector3>();
            var uvs = new List<Vector2>();
            int transCount = roadTransitions != null ? roadTransitions.Length : 0;
            // [биом*2 + 0] верх, [биом*2 + 1] откосы, дальше — переходы, настил моста (дерево), брод под водой (по биому).
            int bridgeSub = RoadSubs + transCount, fordSub = bridgeSub + 1;
            var subs = new List<int>[fordSub + BiomeCount];
            for (int i = 0; i < subs.Length; i++) subs[i] = new List<int>();

            // Стыки биомов на этом пути: участок длиной в ширину дороги (картинка не растянута) — картинкой-переходом.
            var joins = new List<(float center, float half, int sub, bool flip)>();
            for (int i = 0; i + 1 < s.Count; i++)
            {
                if (s[i].Biome == s[i + 1].Biome) continue;
                int k = FindTransition(s[i].Biome, s[i + 1].Biome, out bool flip);
                if (k >= 0) joins.Add(((path.Dist[i] + path.Dist[i + 1]) * 0.5f, Mathf.Max(4f, s[i].HalfWidth), RoadSubs + k, flip));
            }

            float bottom = GroundY - 0.5f;
            // Вдоль дороги текстура повторяется через «ширину дороги» метров — картинка не растянута.
            // Откосы: квадратные плитки SideTile м по длине и по высоте.
            for (int i = 0; i + 1 < s.Count; i++)
            {
                var a = s[i];
                var b = s[i + 1];
                Vector3 aL = a.Pos - a.Right * a.HalfWidth, aR = a.Pos + a.Right * a.HalfWidth;
                Vector3 bL = b.Pos - b.Right * b.HalfWidth, bR = b.Pos + b.Right * b.HalfWidth;
                // Откосы — до земли, а у брода (дорога ниже земли) — до дна реки.
                float ab = Mathf.Min(bottom, a.Pos.y - 0.6f), bb = Mathf.Min(bottom, b.Pos.y - 0.6f);
                Vector3 aLb = new Vector3(aL.x, ab, aL.z), aRb = new Vector3(aR.x, ab, aR.z);
                Vector3 bLb = new Vector3(bL.x, bb, bL.z), bRb = new Vector3(bR.x, bb, bR.z);
                int top = (int)a.Biome * 2, sideSub = top + 1;
                // Вдоль — по общему прогрессу трассы: у развилки рисунок веток продолжает рисунок площадки.
                float v0 = (path.StartProgress + path.Dist[i]) / RoadTile, v1 = (path.StartProgress + path.Dist[i + 1]) / RoadTile;
                float d0 = path.Dist[i] / SideTile, d1 = path.Dist[i + 1] / SideTile;

                // На стыке биомов верх дороги — картинкой-переходом: v идёт от биома «до» к биому «после».
                int topSub = top;
                float mid = (path.Dist[i] + path.Dist[i + 1]) * 0.5f;
                foreach (var j in joins)
                {
                    if (Mathf.Abs(mid - j.center) >= j.half) continue;
                    topSub = j.sub;
                    float t0 = Mathf.Clamp01((path.Dist[i] - (j.center - j.half)) / (2f * j.half));
                    float t1 = Mathf.Clamp01((path.Dist[i + 1] - (j.center - j.half)) / (2f * j.half));
                    // На картинке верх (v = 1) — биом From. Едем From → To: v от 1 к 0; обратно — от 0 к 1.
                    v0 = j.flip ? t0 : 1f - t0;
                    v1 = j.flip ? t1 : 1f - t1;
                    break;
                }

                // Брод под водой — не «дорога», а отмель: текстура берега, сквозь воду колея не видна.
                bool ford = UnderWater(a.Pos) && UnderWater(b.Pos);
                if (ford) { topSub = fordSub + (int)a.Biome; sideSub = topSub; }
                // Мост: тонкий настил (дерево по бокам и снизу), под ним течёт вода.
                bool bridge = a.Bridge && b.Bridge;
                if (bridge)
                {
                    ab = a.Pos.y - BridgeDeck; bb = b.Pos.y - BridgeDeck;
                    aLb = new Vector3(aL.x, ab, aL.z); aRb = new Vector3(aR.x, ab, aR.z);
                    bLb = new Vector3(bL.x, bb, bL.z); bRb = new Vector3(bR.x, bb, bR.z);
                    sideSub = bridgeSub;
                    Quad(verts, uvs, subs[bridgeSub], aLb, aRb, bRb, bLb, new Vector2(0f, v0), new Vector2(1f, v0), new Vector2(1f, v1), new Vector2(0f, v1));   // низ
                }

                if (a.Road && b.Road)
                {
                    // Поперёк три полосы: обочины с травой постоянной ширины (RoadEdge м), середина — грунт.
                    // Иначе на широкой площадке развилки трава по краям раздувалась бы на метры.
                    float ea = Mathf.Min(RoadEdge, a.HalfWidth * 0.3f), eb = Mathf.Min(RoadEdge, b.HalfWidth * 0.3f);
                    Vector3 aL2 = aL + a.Right * ea, aR2 = aR - a.Right * ea, bL2 = bL + b.Right * eb, bR2 = bR - b.Right * eb;
                    Quad(verts, uvs, subs[topSub], aL, bL, bL2, aL2, new Vector2(0f, v0), new Vector2(0f, v1), new Vector2(EdgeU, v1), new Vector2(EdgeU, v0));
                    Quad(verts, uvs, subs[topSub], aL2, bL2, bR2, aR2, new Vector2(EdgeU, v0), new Vector2(EdgeU, v1), new Vector2(1f - EdgeU, v1), new Vector2(1f - EdgeU, v0));
                    Quad(verts, uvs, subs[topSub], aR2, bR2, bR, aR, new Vector2(1f - EdgeU, v0), new Vector2(1f - EdgeU, v1), new Vector2(1f, v1), new Vector2(1f, v0));
                    Quad(verts, uvs, subs[sideSub], aL, aLb, bLb, bL, SideUV(aL, d0), SideUV(aLb, d0), SideUV(bLb, d1), SideUV(bL, d1));
                    Quad(verts, uvs, subs[sideSub], aR, bR, bRb, aRb, SideUV(aR, d0), SideUV(bR, d1), SideUV(bRb, d1), SideUV(aRb, d0));
                }
                // Торцы у пропасти.
                float wA = a.HalfWidth * 2f / SideTile, wB = b.HalfWidth * 2f / SideTile;
                if (a.Road && !b.Road) Quad(verts, uvs, subs[sideSub], aL, aLb, aRb, aR,
                                            SideUV(aL, 0f), SideUV(aLb, 0f), SideUV(aRb, wA), SideUV(aR, wA));
                if (!a.Road && b.Road) Quad(verts, uvs, subs[sideSub], bL, bR, bRb, bLb,
                                            SideUV(bL, 0f), SideUV(bR, wB), SideUV(bRb, wB), SideUV(bLb, 0f));
            }

            // Торцы в начале и в конце пути: на высоте не видно «изнанки» дороги на стыках.
            EndCap(s[0], true, verts, uvs, subs, bottom);
            EndCap(s[s.Count - 1], false, verts, uvs, subs, bottom);

            FinishRoadMesh("Дорога_" + path.Name, verts, uvs, subs);
        }

        void FinishRoadMesh(string name, List<Vector3> verts, List<Vector2> uvs, List<int>[] subs)
        {
            var mesh = new Mesh { name = name };
            if (verts.Count > 65000) mesh.indexFormat = IndexFormat.UInt32;
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.subMeshCount = subs.Length;
            for (int i = 0; i < subs.Length; i++) mesh.SetTriangles(subs[i], i);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            var go = new GameObject(name);
            go.transform.SetParent(root, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mats = new List<Material>
            {
                roadForest ? roadForest : Mat(ForestRoad), sideForest ? sideForest : Mat(ForestSide),
                roadMountain ? roadMountain : Mat(MountainRoad), sideMountain ? sideMountain : Mat(MountainSide),
                roadSnow ? roadSnow : Mat(SnowRoad), sideSnow ? sideSnow : Mat(SnowSide),
                roadDesert ? roadDesert : Mat(DesertRoad), sideDesert ? sideDesert : Mat(DesertSide),
            };
            if (roadTransitions != null) foreach (var t in roadTransitions) mats.Add(t.Material);
            mats.Add(Mat(PortalWood));   // настил моста
            for (int k = 0; k < BiomeCount; k++) mats.Add(BankMaterial((Biome)k, false));   // брод под водой
            if (mats.Count > subs.Length) mats.RemoveRange(subs.Length, mats.Count - subs.Length);
            go.AddComponent<MeshRenderer>().sharedMaterials = mats.ToArray();
            go.AddComponent<MeshCollider>().sharedMesh = mesh;
            go.isStatic = true;
        }

        /// <summary>Картинка-переход для пары биомов (в любом порядке); flip — едем снизу картинки вверх.</summary>
        int FindTransition(Biome from, Biome to, out bool flip)
        {
            flip = false;
            if (roadTransitions == null) return -1;
            for (int k = 0; k < roadTransitions.Length; k++)
            {
                var t = roadTransitions[k];
                if (t.Material == null) continue;
                if (t.From == from && t.To == to) return k;
                if (t.From == to && t.To == from) { flip = true; return k; }
            }
            return -1;
        }

        void EndCap(RouteSample a, bool start, List<Vector3> verts, List<Vector2> uvs, List<int>[] subs, float bottom)
        {
            if (!a.Road || a.Pos.y < 0.5f) return;   // на равнине торца не видно
            Vector3 l = a.Pos - a.Right * a.HalfWidth, r = a.Pos + a.Right * a.HalfWidth;
            float y = Mathf.Min(bottom, a.Pos.y - 0.6f);
            Vector3 lb = new Vector3(l.x, y, l.z), rb = new Vector3(r.x, y, r.z);
            float w = a.HalfWidth * 2f / SideTile;
            int sub = (int)a.Biome * 2 + 1;
            if (start) Quad(verts, uvs, subs[sub], l, r, rb, lb, SideUV(l, 0f), SideUV(r, w), SideUV(rb, w), SideUV(lb, 0f));
            else Quad(verts, uvs, subs[sub], l, lb, rb, r, SideUV(l, 0f), SideUV(lb, 0f), SideUV(rb, w), SideUV(r, w));
        }

        readonly List<(RoutePath left, RoutePath right)> forkFills = new List<(RoutePath, RoutePath)>();

        /// <summary>
        /// Клин между ветками у развилки и у места встречи: настил от внутреннего края левой ветки
        /// до внутреннего края правой, пока щель не шире FillGap. Дальше — обрыв со стенкой.
        /// </summary>
        void BuildForkFill(RoutePath left, RoutePath right)
        {
            const float FillGap = 3.5f;
            int n = Mathf.Min(left.Samples.Count, right.Samples.Count);
            if (n < 2) return;
            var verts = new List<Vector3>();
            var uvs = new List<Vector2>();
            var subs = new List<int>[RoadSubs];
            for (int i = 0; i < subs.Length; i++) subs[i] = new List<int>();
            float bottom = GroundY - 0.5f;

            Vector3 In(RouteSample a, float side) => a.Pos + a.Right * (side * a.HalfWidth);
            float Gap(int i) { var a = In(left.Samples[i], 1f); var b = In(right.Samples[i], -1f); a.y = b.y = 0f; return Vector3.Distance(a, b); }

            void Strip(int from, int step)
            {
                int i = from;
                while (i + step >= 0 && i + step < n && Gap(i + step) < FillGap)
                {
                    var la = left.Samples[i]; var lb = left.Samples[i + step];
                    var ra = right.Samples[i]; var rb = right.Samples[i + step];
                    if (!(la.Road && lb.Road && ra.Road && rb.Road)) break;
                    Vector3 a0 = In(la, 1f), a1 = In(lb, 1f), b0 = In(ra, -1f), b1 = In(rb, -1f);
                    int top = (int)la.Biome * 2;
                    float v0 = i * 0.1f, v1 = (i + step) * 0.1f;
                    if (step > 0) Quad(verts, uvs, subs[top], a0, a1, b1, b0, new Vector2(0.35f, v0), new Vector2(0.35f, v1), new Vector2(0.65f, v1), new Vector2(0.65f, v0));
                    else Quad(verts, uvs, subs[top], a0, b0, b1, a1, new Vector2(0.35f, v0), new Vector2(0.65f, v0), new Vector2(0.65f, v1), new Vector2(0.35f, v1));
                    i += step;
                }
                if (i == from) return;
                // Стенка на конце клина (дальше — щель между ветками).
                var l = left.Samples[i]; var r = right.Samples[i];
                Vector3 p0 = In(l, 1f), p1 = In(r, -1f);
                float y0 = Mathf.Min(bottom, p0.y - 0.6f), y1 = Mathf.Min(bottom, p1.y - 0.6f);
                Vector3 q0 = new Vector3(p0.x, y0, p0.z), q1 = new Vector3(p1.x, y1, p1.z);
                int side = (int)l.Biome * 2 + 1;
                float w = Vector3.Distance(p0, p1) / SideTile;
                // Стенка смотрит в щель: у развилки — вперёд, у места встречи — назад.
                if (step > 0) Quad(verts, uvs, subs[side], p0, q0, q1, p1, SideUV(p0, 0f), SideUV(q0, 0f), SideUV(q1, w), SideUV(p1, w));
                else Quad(verts, uvs, subs[side], p0, p1, q1, q0, SideUV(p0, 0f), SideUV(p1, w), SideUV(q1, w), SideUV(q0, 0f));
            }
            Strip(0, 1);
            Strip(n - 1, -1);
            if (verts.Count == 0) return;
            FinishRoadMesh("Развилка_" + left.Name + "_" + right.Name, verts, uvs, subs);
        }

        static Vector2 SideUV(Vector3 p, float along) => new Vector2(along, p.y / SideTile);

        /// <summary>Четырёхугольник a-b-c-d (по часовой, если смотреть с лицевой стороны).</summary>
        static void Quad(List<Vector3> verts, List<Vector2> uvs, List<int> tris, Vector3 a, Vector3 b, Vector3 c, Vector3 d,
                         Vector2 ua = default, Vector2 ub = default, Vector2 uc = default, Vector2 ud = default)
        {
            int i = verts.Count;
            verts.Add(a); verts.Add(b); verts.Add(c); verts.Add(d);
            uvs.Add(ua); uvs.Add(ub); uvs.Add(uc); uvs.Add(ud);
            tris.Add(i); tris.Add(i + 1); tris.Add(i + 2);
            tris.Add(i); tris.Add(i + 2); tris.Add(i + 3);
        }

        void BuildGround(int firstDecoration, RouteRailway railway)
        {
            var b = new Bounds(transform.position, Vector3.zero);
            var flatAreas = new List<Bounds>();
            foreach (var path in map.Paths)
            {
                for (int i = 0; i < path.Samples.Count; i++)
                {
                    var sample = path.Samples[i];
                    b.Encapsulate(new Vector3(sample.Pos.x, 0f, sample.Pos.z));
                    if (i == 0) continue;
                    var previous = path.Samples[i - 1];
                    var corridor = new Bounds(previous.Pos, Vector3.zero);
                    corridor.Encapsulate(sample.Pos);
                    float margin = Mathf.Max(previous.HalfWidth, sample.HalfWidth) + 6f;
                    corridor.Expand(new Vector3(2f * margin, 0f, 2f * margin));
                    flatAreas.Add(corridor);
                }
            }
            for (int i = firstDecoration; i < root.childCount; i++)
            {
                if (!root.GetChild(i).gameObject.activeSelf) continue;
                foreach (var renderer in root.GetChild(i).GetComponentsInChildren<Renderer>())
                    flatAreas.Add(renderer.bounds);
            }
            if (railway != null) railway.ReserveGround(flatAreas);

            b.Expand(new Vector3(260f, 0f, 260f));
            if (!circuit) { GroundIn(b, rivers, canyons, flatAreas); return; }

            // Кольцо: у каждой ветки своя половина карты (свои реки, ущелья и биомы по z), шов — под каменной грядой.
            float mid = transform.position.x;
            var left = new Bounds(); left.SetMinMax(b.min, new Vector3(mid, b.max.y, b.max.z));
            var right = new Bounds(); right.SetMinMax(new Vector3(mid, b.min.y, b.min.z), b.max);
            var keep = zones;
            zones = zonesLeft;
            GroundIn(left, rivers.FindAll(r => r.XMax <= mid + 0.1f), canyons.FindAll(c => !c.Right), flatAreas);
            zones = zonesRight;
            GroundIn(right, rivers.FindAll(r => r.XMin >= mid - 0.1f), canyons.FindAll(c => c.Right), flatAreas);
            zones = keep;
        }

        void GroundIn(Bounds b, List<River> riverSet, List<Canyon> canyonSet, List<Bounds> flatAreas)
        {
            // Под свалкой земля своя — наша начинается от выезда.
            float zMin = transform.position.z - 0.5f;
            float zMax = b.max.z;
            // Земля полосами между реками; в реках — русло с пологими берегами и вода.
            // Особые полосы по z — реки и ущелья: земля в них своя (рельеф с руслом или вырезом).
            var special = new List<(float zMin, float zMax, System.Action build)>();
            foreach (var r in riverSet) { var rr = r; special.Add((rr.ZMin, rr.ZMax, () => BuildRiver(b, rr))); }
            foreach (var c in canyonSet) if (c.Rows.Count > 1) { var cc = c; special.Add((cc.ZMin, cc.ZMax, () => BuildCanyon(b, cc))); }
            special.Sort((p, q) => p.zMin.CompareTo(q.zMin));
            float from = zMin;
            foreach (var sp in special)
            {
                GroundStrip(b, from, sp.zMin, flatAreas);
                sp.build();
                from = Mathf.Max(from, sp.zMax);
            }
            GroundStrip(b, from, zMax, flatAreas);   // по бокам и позади свалки землю уже положил BuildSurroundings
        }

        static readonly Color SandColor = new Color(0.82f, 0.72f, 0.52f);

        /// <summary>
        /// Река: рельеф (дно, пологий песчаный пляж, полоса мокрого песка, трава до краёв полосы) одним мешем
        /// с коллайдером, поверх — вода. Рельеф строится в «системе русла»: строки — отступы от середины русла,
        /// поэтому изгибы гладкие при любом шаге; крайние строки — прямые, вровень с соседними плитами земли.
        /// </summary>
        void BuildRiver(Bounds b, River r)
        {
            float h = r.HalfW, B = r.BankW;
            // Отступы от середины: дно → кромка воды → пляж → трава.
            var half = new List<float> { 0.3f * h, 0.55f * h, 0.75f * h, 0.88f * h, 0.96f * h, h, h + 0.6f, h + 1.5f, h + 3f, h + 5f, h + 7f, h + B };
            var offsets = new List<float>();
            for (int i = half.Count - 1; i >= 0; i--) offsets.Add(-half[i]);
            offsets.Add(0f);
            offsets.AddRange(half);

            float Height(float o)
            {
                float u = Mathf.Abs(o);
                if (u <= h) return Mathf.Lerp(RiverBedY, WaterY - 0.04f, Mathf.Pow(u / h, 1.6f));   // у кромки мелко и полого
                float t = Mathf.Clamp01((u - h) / B);
                return Mathf.Lerp(WaterY - 0.04f, GroundY, t * t * (3f - 2f * t));
            }

            int cols = Mathf.Max(2, Mathf.CeilToInt(b.size.x / 2f) + 1);
            int rows = offsets.Count + 2;   // + прямые края
            var verts = new List<Vector3>(cols * rows);
            var uvs = new List<Vector2>(cols * rows);
            for (int i = 0; i < cols; i++)
            {
                float x = Mathf.Lerp(b.min.x, b.max.x, i / (float)(cols - 1));
                float c = r.CenterZ(x);
                verts.Add(new Vector3(x, GroundY, r.ZMin));
                foreach (var o in offsets) verts.Add(new Vector3(x, Height(o), c + o));
                verts.Add(new Vector3(x, GroundY, r.ZMax));
            }
            foreach (var v in verts) uvs.Add(new Vector2(v.x, v.z) / GroundTile);

            // Сабмеши: 0 — трава, 1 — песок (дно и пляж), 2 — мокрый песок у кромки.
            var subs = new[] { new List<int>(), new List<int>(), new List<int>() };
            for (int i = 0; i + 1 < cols; i++)
                for (int j = 0; j + 1 < rows; j++)
                {
                    int sub = 0;
                    if (j > 0 && j + 1 < rows - 1)
                    {
                        float om = Mathf.Abs((offsets[j - 1] + offsets[j]) * 0.5f);
                        sub = om > h && om < h + 1.6f ? 2 : om <= h + B * 0.6f ? 1 : 0;
                    }
                    int a = i * rows + j, n = (i + 1) * rows + j;
                    subs[sub].Add(a); subs[sub].Add(a + 1); subs[sub].Add(n + 1);
                    subs[sub].Add(a); subs[sub].Add(n + 1); subs[sub].Add(n);
                }

            var mesh = new Mesh { name = "Река_рельеф" };
            if (verts.Count > 65000) mesh.indexFormat = IndexFormat.UInt32;
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.subMeshCount = 3;
            for (int k = 0; k < 3; k++) mesh.SetTriangles(subs[k], k);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            var biomeHere = r.Biome;
            var grass = GroundMaterial(biomeHere);
            if (grass != null) grass = Tiled(grass, Vector2.one);   // UV рельефа — уже в плитках по 10 м
            else grass = Mat(Grass);
            var sand = BankMaterial(biomeHere, false);
            var wet = BankMaterial(biomeHere, true);

            var go = new GameObject("Река_берега");
            go.transform.SetParent(root, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterials = new[] { grass, sand, wet };
            mr.shadowCastingMode = ShadowCastingMode.Off;
            go.AddComponent<MeshCollider>().sharedMesh = mesh;
            go.isStatic = true;

            // Вода: зона для физики (коробка-триггер, верх — поверхность) на всю полосу русла и плоская поверхность.
            // Ниже уровня воды рельеф есть только в русле — значит всё, что в коробке ниже поверхности, в реке.
            float wz0 = r.Zc - r.Amp - h - 1f, wz1 = r.Zc + r.Amp + h + 1f;
            var water = Piece("Река", cube, new Vector3(b.center.x, (WaterY + RiverBedY - 1f) / 2f, (wz0 + wz1) / 2f), Quaternion.identity,
                              new Vector3(b.size.x, WaterY - RiverBedY + 1f, wz1 - wz0), WaterColor, Col.None, Decor.Far);
            var waterMat = WaterMaterial;
            if (waterMat != null)
            {
                Hidden(water);
                if (biomeHere == Biome.Snow) waterMat = IcyWater(waterMat);
                WaterSurface(b.min.x, b.max.x, r.ZMin, r.ZMax, WaterY, waterMat);   // где берег выше воды — вода просто не видна
            }
            water.AddComponent<BoxCollider>();   // триггер (WaterZone сам выставит)
            water.AddComponent<WaterZone>();
        }

        /// <summary>
        /// Ущелье: рельеф полосой по z на всю ширину карты — ровная земля, по краям ущелья скальные стены вниз,
        /// дно на уровне дороги. Строки — точки дороги (по z), столбцы — отступы по x от дороги.
        /// В середине — тоннель: крыша вровень с землёй, деревянные порталы, фонари внутри.
        /// </summary>
        void BuildCanyon(Bounds b, Canyon c)
        {
            var groundSrc = GroundMaterial(c.Biome);
            var groundMat = groundSrc != null ? Tiled(groundSrc, Vector2.one) : Mat(GroundColor(c.Biome));
            var wallSrc = c.Biome == Biome.Forest ? sideForest : c.Biome == Biome.Snow ? sideSnow : c.Biome == Biome.Desert ? sideDesert : sideMountain;
            var wallMat = wallSrc != null ? wallSrc : Mat(MountainSide);

            // Вид: над тоннелем — та же земля, что вокруг; у входа и выхода — скальный склон с открытым проёмом,
            // внутри — каменные стены и потолок (отдельный меш). Физика: вырез по всей длине (там дорога)
            // плюс крыша-коллайдер над тоннелем — по земле сверху можно ходить.
            var collision = CanyonGrid(b, c, false, out _, out _);
            var colGo = new GameObject("Ущелье_физика");
            colGo.transform.SetParent(root, false);
            colGo.AddComponent<MeshCollider>().sharedMesh = collision;
            colGo.isStatic = true;

            var view = CanyonGrid(b, c, true, out _, out _);
            var go = new GameObject("Ущелье");
            go.transform.SetParent(root, false);
            go.AddComponent<MeshFilter>().sharedMesh = view;
            go.AddComponent<MeshRenderer>().sharedMaterials = new[] { groundMat, wallMat };
            go.isStatic = true;

            BuildTunnel(c, wallMat, groundMat);
        }

        const float TunnelRoof = 1.2f;

        /// <summary>Точка внутри тоннеля (под потолком) — для погоды: там не идёт дождь и снег.</summary>
        public bool InsideTunnel(Vector3 p)
        {
            if (p.y > GroundY - TunnelRoof) return false;
            foreach (var c in canyons)
            {
                var rows = c.Rows;
                for (int j = 0; j + 1 < rows.Count; j++)
                {
                    if (p.z < rows[j].Pos.z || p.z > rows[j + 1].Pos.z) continue;
                    if (!InTunnel(c, j) && !InTunnel(c, j + 1)) break;
                    var s = rows[j];
                    if (Mathf.Abs(p.x - s.Pos.x) < Canyon.HalfX(s) + CanyonShoulder + 0.35f) return true;
                    break;
                }
            }
            return false;
        }   // толщина земли над тоннелем, м (потолок — на GroundY − TunnelRoof)

        bool InTunnel(Canyon c, int row) =>
            c.TunnelTo - c.TunnelFrom >= 10f && c.Path.Dist[c.From + row] >= c.TunnelFrom && c.Path.Dist[c.From + row] <= c.TunnelTo;

        /// <summary>
        /// Сетка ущелья полосой по z на всю ширину карты: строки — точки дороги, столбцы — отступы по x.
        /// view = true: над тоннелем строки ровные (земля целая), у порталов — проём (центральные квадраты пропущены).
        /// </summary>
        Mesh CanyonGrid(Bounds b, Canyon c, bool view, out List<int> ground, out List<int> walls)
        {
            var rows = c.Rows;
            var verts = new List<Vector3>();
            var uvs = new List<Vector2>();
            int cols = 0;
            for (int j = 0; j < rows.Count; j++)
            {
                var s = rows[j];
                bool flat = view && InTunnel(c, j);   // над тоннелем — земля вровень с окружающей
                float hx = Canyon.HalfX(s), y = s.Pos.y, z = s.Pos.z;
                float deep = Mathf.Max(0f, GroundY - y);
                float floor = y - 0.08f;
                float jag(float side) => Mathf.Sin(z * 0.37f + side * 1.7f) * 0.5f + Mathf.Sin(z * 1.13f + side) * 0.25f;
                int before = verts.Count;
                void Col(float x, float h)
                {
                    h = flat ? GroundY : Mathf.Min(h, GroundY);
                    verts.Add(new Vector3(x, h, z));
                    uvs.Add(new Vector2(x, z) / GroundTile);
                }
                Col(b.min.x, GroundY);
                for (int side = -1; side <= 1; side += 2)
                {
                    var list = new (float o, float h)[]
                    {
                        (hx + CanyonShoulder + CanyonWall + 2f, GroundY),
                        (hx + CanyonShoulder + CanyonWall, GroundY),
                        (hx + CanyonShoulder + CanyonWall * 0.55f + jag(side) * 0.6f, y + deep * 0.72f + jag(-side) * 0.4f),
                        (hx + CanyonShoulder + 0.35f, y + deep * 0.18f),
                        (hx + CanyonShoulder, floor),
                        (hx * 0.5f, floor),
                    };
                    if (side < 0) foreach (var e in list) Col(s.Pos.x - e.o, e.h);
                    else
                    {
                        Col(s.Pos.x, floor);   // середина
                        for (int k = list.Length - 1; k >= 0; k--) Col(s.Pos.x + list[k].o, list[k].h);
                    }
                }
                Col(b.max.x, GroundY);
                cols = verts.Count - before;
            }

            // Квадраты: крутые (перепад > 1 м) — скала, остальные — земля. У порталов центр пропускаем — проём.
            ground = new List<int>(); walls = new List<int>();
            for (int j = 0; j + 1 < rows.Count; j++)
            {
                bool portal = view && InTunnel(c, j) != InTunnel(c, j + 1);   // вход/выход: проём открыт
                for (int i = 0; i + 1 < cols; i++)
                {
                    if (portal && i >= 4 && i + 1 <= cols - 5) continue;   // проём в тоннель (от низа стены до низа стены)
                    int a = j * cols + i, n = (j + 1) * cols + i;
                    float lo = Mathf.Min(Mathf.Min(verts[a].y, verts[a + 1].y), Mathf.Min(verts[n].y, verts[n + 1].y));
                    float hi = Mathf.Max(Mathf.Max(verts[a].y, verts[a + 1].y), Mathf.Max(verts[n].y, verts[n + 1].y));
                    var list = hi - lo > 1f ? walls : ground;
                    // Строки — по z, столбцы — по x: обход по часовой, если смотреть сверху.
                    list.Add(a); list.Add(n); list.Add(n + 1);
                    list.Add(a); list.Add(n + 1); list.Add(a + 1);
                }
            }
            // Скале — UV по вертикали (вдоль ущелья и по высоте), земле — сверху.
            if (view)
                foreach (int idx in walls) uvs[idx] = new Vector2(verts[idx].z / SideTile + verts[idx].x / SideTile * 0.5f, verts[idx].y / SideTile);

            var mesh = new Mesh { name = view ? "Ущелье" : "Ущелье_физика" };
            if (verts.Count > 65000) mesh.indexFormat = IndexFormat.UInt32;
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            if (view)
            {
                mesh.subMeshCount = 2;
                mesh.SetTriangles(ground, 0);
                mesh.SetTriangles(walls, 1);
            }
            else
            {
                var all = new List<int>(ground); all.AddRange(walls);
                mesh.SetTriangles(all, 0);
            }
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        static readonly Color PortalWood = new Color(0.42f, 0.28f, 0.17f);
        static readonly Color LampColor = new Color(1f, 0.82f, 0.5f);

        /// <summary>
        /// Тоннель: каменные стены и потолок изнутри, невидимая крыша-коллайдер под землёй над тоннелем,
        /// деревянные порталы у входа и выхода, фонари под потолком.
        /// </summary>
        void BuildTunnel(Canyon c, Material wallMat, Material floorMat)
        {
            if (c.TunnelTo - c.TunnelFrom < 10f) return;
            float ceil = GroundY - TunnelRoof;
            var verts = new List<Vector3>();
            var uvs = new List<Vector2>();
            var tris = new List<int>();
            var floorTris = new List<int>();
            void Q(Vector3 a, Vector3 b2, Vector3 c2, Vector3 d, Vector2 ua, Vector2 ub, Vector2 uc, Vector2 ud) => Quad(verts, uvs, tris, a, b2, c2, d, ua, ub, uc, ud);
            var rows = c.Rows;
            for (int j = 0; j + 1 < rows.Count; j++)
            {
                if (!InTunnel(c, j) && !InTunnel(c, j + 1)) continue;
                var s0 = rows[j]; var s1 = rows[j + 1];
                float l0 = s0.Pos.x - Canyon.HalfX(s0) - CanyonShoulder - 0.35f, r0 = s0.Pos.x + Canyon.HalfX(s0) + CanyonShoulder + 0.35f;
                float l1 = s1.Pos.x - Canyon.HalfX(s1) - CanyonShoulder - 0.35f, r1 = s1.Pos.x + Canyon.HalfX(s1) + CanyonShoulder + 0.35f;
                float f0 = s0.Pos.y - 0.2f, f1 = s1.Pos.y - 0.2f, z0 = s0.Pos.z, z1 = s1.Pos.z;
                // Левая стена (смотрит внутрь, +x), правая (−x), потолок (вниз).
                Q(new Vector3(l0, f0, z0), new Vector3(l0, ceil, z0), new Vector3(l1, ceil, z1), new Vector3(l1, f1, z1),
                  new Vector2(z0, f0) / SideTile, new Vector2(z0, ceil) / SideTile, new Vector2(z1, ceil) / SideTile, new Vector2(z1, f1) / SideTile);
                Q(new Vector3(r0, f0, z0), new Vector3(r1, f1, z1), new Vector3(r1, ceil, z1), new Vector3(r0, ceil, z0),
                  new Vector2(z0, f0) / SideTile, new Vector2(z1, f1) / SideTile, new Vector2(z1, ceil) / SideTile, new Vector2(z0, ceil) / SideTile);
                Q(new Vector3(l0, ceil, z0), new Vector3(r0, ceil, z0), new Vector3(r1, ceil, z1), new Vector3(l1, ceil, z1),
                  new Vector2(l0, z0) / SideTile, new Vector2(r0, z0) / SideTile, new Vector2(r1, z1) / SideTile, new Vector2(l1, z1) / SideTile);
                // Пол от стены до стены (дорога лежит сверху): обочины не дырявые.
                Quad(verts, uvs, floorTris, new Vector3(l0, f0 + 0.1f, z0), new Vector3(l1, f1 + 0.1f, z1), new Vector3(r1, f1 + 0.1f, z1), new Vector3(r0, f0 + 0.1f, z0),
                     new Vector2(l0, z0) / GroundTile, new Vector2(l1, z1) / GroundTile, new Vector2(r1, z1) / GroundTile, new Vector2(r0, z0) / GroundTile);
            }
            // Над входом и выходом — скала от потолка до земли (иначе над балкой видна пустота).
            foreach (float d in new[] { c.TunnelFrom, c.TunnelTo })
            {
                var s = c.Path.At(d);
                float hx = Canyon.HalfX(s) + CanyonShoulder + 0.35f;
                Vector3 a0 = new Vector3(s.Pos.x - hx, ceil - 0.05f, s.Pos.z), a1 = new Vector3(s.Pos.x + hx, ceil - 0.05f, s.Pos.z);
                Vector3 b0 = new Vector3(s.Pos.x - hx, GroundY, s.Pos.z), b1 = new Vector3(s.Pos.x + hx, GroundY, s.Pos.z);
                Vector2 u(Vector3 p) => new Vector2(p.x, p.y) / SideTile;
                Q(a0, b0, b1, a1, u(a0), u(b0), u(b1), u(a1));   // смотрит назад (−z)
                Q(a0, a1, b1, b0, u(a0), u(a1), u(b1), u(b0));   // и вперёд (+z) — видно с обеих сторон
            }
            var mesh = new Mesh { name = "Тоннель" };
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.subMeshCount = 2;
            mesh.SetTriangles(tris, 0);
            mesh.SetTriangles(floorTris, 1);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            var go = new GameObject("Тоннель");
            go.transform.SetParent(root, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterials = new[] { wallMat, floorMat };
            mr.shadowCastingMode = ShadowCastingMode.Off;   // тень даёт земля сверху — в тоннеле темно, горят фонари
            go.isStatic = true;

            // Крыша-коллайдер: невидимые плиты над тоннелем (верх вровень с землёй) — сверху можно ходить.
            var path = c.Path;
            for (float d = c.TunnelFrom; d < c.TunnelTo; d += 4f)
            {
                var s = path.At(d + 2f);
                float w = (Canyon.HalfX(s) + CanyonShoulder + CanyonWall + 1.5f) * 2f;
                Hidden(Piece("Тоннель_крыша", cube, new Vector3(s.Pos.x, GroundY - TunnelRoof / 2f, s.Pos.z), Quaternion.identity,
                             new Vector3(w, TunnelRoof, 4.3f), MountainGround, Col.Box));
            }
            Portal(path.At(c.TunnelFrom));
            Portal(path.At(c.TunnelTo));
            // Фонари под потолком: тёплый свет, без теней (дёшево).
            for (float d = c.TunnelFrom + 6f; d < c.TunnelTo - 3f; d += 12f)
            {
                var s = path.At(d);
                Vector3 at = new Vector3(s.Pos.x, ceil - 0.3f, s.Pos.z);
                Piece("Фонарь", sphere, at, Quaternion.identity, Vector3.one * 0.35f, LampColor * 1.4f, Col.None, Decor.Small);
                var lightGo = new GameObject("Свет_тоннеля");
                lightGo.transform.SetParent(root, false);
                lightGo.transform.position = at - Vector3.up * 0.4f;
                var l = lightGo.AddComponent<Light>();
                l.type = LightType.Point;
                l.color = LampColor;
                l.range = 11f;
                l.intensity = 3f;
                l.shadows = LightShadows.None;
            }
        }

        /// <summary>Портал тоннеля: два столба по краям дороги и балка под потолком — как вход в шахту.</summary>
        void Portal(RouteSample s)
        {
            var rot = Quaternion.LookRotation(s.Forward);
            Vector3 right = rot * Vector3.right;
            float top = GroundY - TunnelRoof - 0.6f, h = top - s.Pos.y;
            for (int k = -1; k <= 1; k += 2)
                Piece("Портал_столб", cube, s.Pos + right * k * (s.HalfWidth + 0.5f) + Vector3.up * (h / 2f), rot,
                      new Vector3(0.6f, h, 0.6f), PortalWood, Col.Box);
            Piece("Портал_балка", cube, new Vector3(s.Pos.x, top + 0.3f, s.Pos.z), rot,
                  new Vector3(s.HalfWidth * 2f + 2.2f, 0.6f, 0.7f), PortalWood, Col.Box);
        }

        readonly Dictionary<(Biome, bool), Material> bankMats = new Dictionary<(Biome, bool), Material>();

        /// <summary>
        /// Берег реки по биому: в лесу — песок, в горах — горный грунт с камешками, в снегу — снег.
        /// wet — мокрая полоса у воды: темнее; снег — в голубизну (подтаявший, с ледком). Им же — брод под водой.
        /// </summary>
        Material BankMaterial(Biome biome, bool wet)
        {
            if (bankMats.TryGetValue((biome, wet), out var m) && m != null) return m;
            Material src = biome == Biome.Snow ? groundSnow : biome == Biome.Mountains ? groundMountain : groundSand;
            Color color = biome == Biome.Snow ? SnowWhite : biome == Biome.Mountains ? MountainGround : SandColor;
            Color wetTint = biome == Biome.Snow ? new Color(0.78f, 0.86f, 0.98f, 1f) : new Color(0.7f, 0.68f, 0.66f, 1f);
            if (src != null)
            {
                m = Tiled(src, Vector2.one);
                if (wet)
                {
                    m = new Material(m) { name = src.name + " мокрый" };
                    m.color = src.color * wetTint;
                }
            }
            else m = Mat(wet ? color * wetTint : color);
            bankMats[(biome, wet)] = m;
            return m;
        }

        static Material waterMaterial, icyWater;
        static bool waterMaterialLoaded;

        /// <summary>Вода в снегу: холоднее и бледнее, пена гуще (ледяная крошка).</summary>
        static Material IcyWater(Material src)
        {
            if (icyWater != null) return icyWater;
            icyWater = new Material(src) { name = src.name + " ледяная" };
            icyWater.SetColor("_ShallowColor", new Color(0.62f, 0.88f, 0.94f, 0.62f));
            icyWater.SetColor("_DeepColor", new Color(0.13f, 0.32f, 0.52f, 0.94f));
            icyWater.SetFloat("_BandCount", 2.4f);
            icyWater.SetFloat("_BandSpeed", 0.22f);
            return icyWater;
        }

        /// <summary>Мультяшная вода (Resources/Materials/ToonWater), null — нет.</summary>
        static Material WaterMaterial
        {
            get
            {
                if (!waterMaterialLoaded) { waterMaterialLoaded = true; waterMaterial = Resources.Load<Material>("Materials/ToonWater"); }
                return waterMaterial;
            }
        }

        /// <summary>Поверхность воды: плоский прямоугольник на высоте y (рябь и пена — в шейдере, по мировым координатам).</summary>
        void WaterSurface(float x0, float x1, float z0, float z1, float y, Material mat)
        {
            var mesh = new Mesh { name = "Вода" };
            mesh.SetVertices(new List<Vector3> { new Vector3(x0, y, z0), new Vector3(x0, y, z1), new Vector3(x1, y, z1), new Vector3(x1, y, z0) });
            mesh.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            var go = new GameObject("Вода_поверхность");
            go.transform.SetParent(root, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            go.isStatic = true;
        }

        /// <summary>Плита земли с травой: текстура повторяется каждые GroundTile метров, как бы ни была велика плита.</summary>
        GameObject GroundPiece(string name, Vector3 position, Vector3 size, Biome biome = Biome.Forest)
        {
            Color fallback = GroundColor(biome);
            var go = Piece(name, cube, position, Quaternion.identity, size, fallback, Col.Box, Decor.Far);
            var src = GroundMaterial(biome);
            if (src != null) go.GetComponent<MeshRenderer>().sharedMaterial = Tiled(src, new Vector2(size.x / GroundTile, size.z / GroundTile));
            return go;
        }

        const float GroundTile = 10f;
        readonly Dictionary<(Material, Vector2), Material> tiled = new Dictionary<(Material, Vector2), Material>();

        Material Tiled(Material src, Vector2 scale)
        {
            scale = new Vector2(Mathf.Max(1f, Mathf.Round(scale.x)), Mathf.Max(1f, Mathf.Round(scale.y)));
            if (tiled.TryGetValue((src, scale), out var m) && m != null) return m;
            m = new Material(src) { name = src.name + " x" + scale.x + "x" + scale.y };
            if (m.HasProperty("_BaseMap")) m.SetTextureScale("_BaseMap", scale);
            else m.mainTextureScale = scale;
            tiled[(src, scale)] = m;
            return m;
        }

        static readonly Color MountainGround = new Color(0.58f, 0.45f, 0.32f);

        Material GroundMaterial(Biome biome) => biome == Biome.Snow ? groundSnow : biome == Biome.Mountains ? groundMountain
                                              : biome == Biome.Desert ? groundSand : groundGrass;

        static Color GroundColor(Biome biome) => biome == Biome.Snow ? SnowWhite : biome == Biome.Mountains ? MountainGround
                                               : biome == Biome.Desert ? SandColor : Grass;

        const float GroundBlend = 10f;   // ширина полосы-перехода земли, м (картинка квадратная — по GroundTile)

        /// <summary>
        /// Полоса земли поперёк карты от z0 до z1: у каждого участка своя земля, на границе участков —
        /// полоса-переход (если есть картинка для этой пары биомов), иначе просто стык.
        /// </summary>
        void GroundStrip(Bounds b, float z0, float z1, List<Bounds> flatAreas)
        {
            if (z1 - z0 < 0.1f) return;
            // Биом на z: до первого участка — лес (выезд со свалки).
            Biome At(float z)
            {
                var r = BaseBiome;
                foreach (var zn in zones) if (z >= zn.z) r = zn.biome;
                return r;
            }

            float from = z0;
            Biome cur = At(z0 + 0.01f);
            foreach (var zn in zones)
            {
                if (zn.z <= z0 || zn.z >= z1 || zn.biome == cur) continue;
                int k = FindGroundTransition(cur, zn.biome, out bool flip);
                float h = GroundBlend / 2f;
                if (k >= 0 && zn.z - h > from + 0.1f && zn.z + h < z1)
                {
                    Plate(from, zn.z - h, cur);
                    TransitionPlate(b, zn.z - h, zn.z + h, groundTransitions[k].Material, flip);
                    from = zn.z + h;
                }
                else
                {
                    Plate(from, zn.z, cur);
                    from = zn.z;
                }
                cur = zn.biome;
            }
            Plate(from, z1, cur);

            void Plate(float p0, float p1, Biome biome)
            {
                if (p1 - p0 < 0.1f) return;
                BuildGroundRelief(b.min.x, b.max.x, p0, p1, biome, flatAreas);
            }
        }

        void BuildGroundRelief(float xMin, float xMax, float zMin, float zMax, Biome biome, List<Bounds> flatAreas)
        {
            const float cell = 6f, fade = 14f;
            int cols = Mathf.CeilToInt((xMax - xMin) / cell) + 1;
            int rows = Mathf.CeilToInt((zMax - zMin) / cell) + 1;
            float dx = (xMax - xMin) / (cols - 1);
            float dz = (zMax - zMin) / (rows - 1);
            var vertices = new Vector3[cols * rows];
            var uv = new Vector2[vertices.Length];
            var masks = new float[vertices.Length];
            var triangles = new List<int>((cols - 1) * (rows - 1) * 6);

            for (int row = 0; row < rows; row++)
            for (int col = 0; col < cols; col++)
            {
                int i = row * cols + col;
                float x = xMin + col * dx, z = zMin + row * dz;
                vertices[i] = new Vector3(x, GroundY, z);
                uv[i] = new Vector2(x, z) / GroundTile;
                // Плоские края стыкуются с переходами, реками, ущельями и соседними полосами.
                float edge = Mathf.Min(Mathf.Min(col, cols - 1 - col) * dx, Mathf.Min(row, rows - 1 - row) * dz);
                masks[i] = Mathf.Clamp01(edge / fade);
                if (row == rows - 1 || col == cols - 1) continue;
                triangles.Add(i); triangles.Add(i + cols); triangles.Add(i + cols + 1);
                triangles.Add(i); triangles.Add(i + cols + 1); triangles.Add(i + 1);
            }

            // Запас в одну ячейку оставляет плоскими даже треугольники, пересекающие границы объекта.
            foreach (var area in flatAreas)
            {
                Vector3 min = area.min, max = area.max;
                float pad = cell + fade;
                int c0 = Mathf.Max(0, Mathf.CeilToInt((min.x - pad - xMin) / dx));
                int c1 = Mathf.Min(cols - 1, Mathf.FloorToInt((max.x + pad - xMin) / dx));
                int r0 = Mathf.Max(0, Mathf.CeilToInt((min.z - pad - zMin) / dz));
                int r1 = Mathf.Min(rows - 1, Mathf.FloorToInt((max.z + pad - zMin) / dz));
                for (int row = r0; row <= r1; row++)
                for (int col = c0; col <= c1; col++)
                {
                    int i = row * cols + col;
                    var point = vertices[i];
                    float x = Mathf.Max(min.x - point.x, point.x - max.x);
                    float z = Mathf.Max(min.z - point.z, point.z - max.z);
                    float mask = Mathf.Clamp01((Mathf.Max(x, z) - cell) / fade);
                    masks[i] = Mathf.Min(masks[i], mask);
                }
            }

            float seedX = Seed % 997, seedZ = Seed % 991;
            for (int i = 0; i < vertices.Length; i++)
            {
                if (masks[i] == 0f) continue;
                float x = vertices[i].x, z = vertices[i].z;
                float broad = Mathf.PerlinNoise(seedX + x / 58f, seedZ + z / 58f);
                float detail = Mathf.PerlinNoise(seedZ + x / 22f, seedX + z / 22f);
                float height = Mathf.Max(0f, broad * 0.8f + detail * 0.2f - 0.24f) * 22f;
                vertices[i].y += height * Mathf.SmoothStep(0f, 1f, masks[i]);
            }

            var mesh = new Mesh { name = "Земля_рельеф_" + biome };
            if (vertices.Length > 65000) mesh.indexFormat = IndexFormat.UInt32;
            mesh.vertices = vertices;
            mesh.uv = uv;
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            var go = new GameObject(mesh.name);
            go.transform.SetParent(root, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            var material = GroundMaterial(biome);
            renderer.sharedMaterial = material != null ? Tiled(material, Vector2.one) : Mat(GroundColor(biome));
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            go.AddComponent<MeshCollider>().sharedMesh = mesh;
            go.isStatic = true;
        }

        int FindGroundTransition(Biome from, Biome to, out bool flip)
        {
            flip = false;
            if (groundTransitions == null) return -1;
            for (int k = 0; k < groundTransitions.Length; k++)
            {
                var t = groundTransitions[k];
                if (t.Material == null) continue;
                if (t.From == from && t.To == to) return k;
                if (t.From == to && t.To == from) { flip = true; return k; }
            }
            return -1;
        }

        /// <summary>
        /// Полоса-переход земли: плоский меш со своими UV (вдоль x — повтор каждые GroundBlend м,
        /// по z — картинка один раз: верх картинки — биом «до», ближе к свалке) и коллайдер-плита под ним.
        /// </summary>
        void TransitionPlate(Bounds b, float z0, float z1, Material mat, bool flip)
        {
            Hidden(Piece("Земля_переход", cube, new Vector3(b.center.x, GroundY - 1f, (z0 + z1) / 2f), Quaternion.identity,
                         new Vector3(b.size.x, 2f, z1 - z0), Grass, Col.Box, Decor.Far));
            float x0 = b.min.x, x1 = b.max.x, reps = Mathf.Max(1f, Mathf.Round(b.size.x / GroundBlend));
            // Верх картинки (v = 1) — биом From. Без flip From лежит на меньшем z.
            float vNear = flip ? 0f : 1f, vFar = 1f - vNear;
            var mesh = new Mesh { name = "Земля_переход" };
            mesh.SetVertices(new List<Vector3> { new Vector3(x0, GroundY, z0), new Vector3(x0, GroundY, z1), new Vector3(x1, GroundY, z1), new Vector3(x1, GroundY, z0) });
            mesh.SetUVs(0, new List<Vector2> { new Vector2(0f, vNear), new Vector2(0f, vFar), new Vector2(reps, vFar), new Vector2(reps, vNear) });
            mesh.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            var go = new GameObject("Земля_переход_вид");
            go.transform.SetParent(root, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            go.isStatic = true;
        }

        // ============================ Декорации ============================

        IEnumerator<object> DecorateSteps()
        {
            foreach (var path in map.Paths)
            {
                if (!path.BuildMesh) continue;
                BridgeDecor(path);
                // Мусор прямо на дороге: бочки, покрышки, мешки кучкой — объезжай или тарань.
                if (Any(junkModels))
                    for (float d = 40f; d < path.Length - 10f; d += RandRange(25f, 55f))
                    {
                        var s = path.At(d);
                        if (!s.Road || s.Bridge || UnderWater(s.Pos) || rng.NextDouble() > 0.5) continue;
                        int n = rng.Next(1, 4);
                        float lat = RandRange(-(s.HalfWidth - 1.5f), s.HalfWidth - 1.5f);
                        for (int i = 0; i < n; i++)
                        {
                            Vector3 p = s.Pos + s.Right * (lat + RandRange(-1f, 1f)) + s.Forward * RandRange(-1.5f, 1.5f);
                            p.y = s.Pos.y;
                            Model(junkModels, p, Quaternion.Euler(0f, RandRange(0f, 360f), 0f), Vector3.one * RandRange(0.6f, 0.9f), Decor.Solid, true);
                        }
                    }
                // Столбики по краям — чтобы в открытом мире было видно, где дорога.
                const float postInset = 0.5f;
                for (float d = 4f; d < path.Length; d += 12f)
                {
                    var s = path.At(d);
                    if (!s.Road || s.Bridge || UnderWater(s.Pos)) continue;   // у брода столбики под водой ни к чему, у моста — перила
                    for (int k = -1; k <= 1; k += 2)
                    {
                        Vector3 p = s.Pos + s.Right * k * Mathf.Max(0f, s.HalfWidth - postInset);
                        if (Any(postModels))
                        {
                            Model(postModels, p, Quaternion.LookRotation(s.Forward), Vector3.one * 0.9f, Decor.Small, true);
                            continue;
                        }
                        Piece("Столбик", cube, p + Vector3.up * 0.4f, Quaternion.identity, new Vector3(0.15f, 0.8f, 0.15f), Color.white, Col.None, Decor.Small);
                        Piece("Столбик_верх", cube, p + Vector3.up * 0.85f, Quaternion.identity, new Vector3(0.17f, 0.12f, 0.17f), new Color(0.9f, 0.2f, 0.1f), Col.None, Decor.Small);
                    }
                }

                for (float d = 0f; d < path.Length; d += 5f)
                {
                    if (d > 0f && Mathf.RoundToInt(d) % 60 == 0) yield return null;   // по ~60 м за шаг
                    var s = path.At(d);
                    for (int k = -1; k <= 1; k += 2)
                    {
                        switch (s.Biome)
                        {
                            case Biome.Forest: DecorateForest(s, k); break;
                            case Biome.Mountains: DecorateMountains(s, k); break;
                            case Biome.Desert: DecorateDesert(s, k); break;
                            default: DecorateSnow(s, k); break;
                        }
                    }
                }
            }
        }

        void DecorateForest(RouteSample s, int side)
        {
            // Ближний ряд деревьев (о них сносит кресла снаружи) и лес подальше.
            if (rng.NextDouble() < 0.55)
                TryTree(s.Pos + s.Right * side * (s.HalfWidth + RandRange(2.5f, 8f)), 2.5f);
            for (int i = 0; i < 3; i++)
                if (rng.NextDouble() < 0.75)
                    TryTree(s.Pos + s.Right * side * (s.HalfWidth + RandRange(9f, 45f)) + s.Forward * RandRange(-2.5f, 2.5f), 3f);
            if (rng.NextDouble() < 0.25)
            {
                Vector3 p = s.Pos + s.Right * side * (s.HalfWidth + RandRange(1.5f, 12f));
                p.y = GroundY;
                if (map.IsClear(p, 1.2f) && !InRiver(p)) Bush(p);
            }
            // Подлесок: кусты погуще у обочины, пни и камни подальше.
            if (Any(bushModels) && rng.NextDouble() < 0.45)
                Scatter(bushModels, s, side, 1.2f, 7f, 0.6f, 1.3f, 1f);
            if (Any(stumpModels) && rng.NextDouble() < 0.08)
                Scatter(stumpModels, s, side, 2f, 15f, 0.45f, 0.8f, 1f, true);
            if (Any(stoneModels) && rng.NextDouble() < 0.08)
                Scatter(stoneModels, s, side, 1.5f, 12f, 0.3f, 0.8f, 0.8f, true);
            RoadsideExtras(s, side, true);
        }

        /// <summary>Модель на земле сбоку от дороги: dist — от края дороги, height — высота модели.</summary>
        GameObject Scatter(GameObject[] set, RouteSample s, int side, float near, float far, float minH, float maxH, float clear, bool solid = false)
        {
            Vector3 p = s.Pos + s.Right * side * (s.HalfWidth + RandRange(near, far)) + s.Forward * RandRange(-2f, 2f);
            p.y = GroundY;
            if (!map.IsClear(p, clear) || InRiver(p, clear)) return null;
            return Model(set, p, Quaternion.Euler(0f, RandRange(0f, 360f), 0f), Vector3.one * RandRange(minH, maxH), Decor.Small, solid);
        }

        /// <summary>Заборы вдоль леса, свалочный мусор и бетонные блоки у обочины.</summary>
        void RoadsideExtras(RouteSample s, int side, bool forest)
        {
            // Мусор кучками: бочка, покрышка, мешки — свалка же рядом.
            if (Any(junkModels) && rng.NextDouble() < 0.05)
            {
                Vector3 c = s.Pos + s.Right * side * (s.HalfWidth + RandRange(1.5f, 5f));
                c.y = GroundY;
                int n = rng.Next(2, 5);
                for (int i = 0; i < n; i++)
                {
                    Vector3 p = c + new Vector3(RandRange(-1.3f, 1.3f), 0f, RandRange(-1.3f, 1.3f));
                    if (map.IsClear(p, 0.6f) && !InRiver(p, 0.6f))
                        Model(junkModels, p, Quaternion.Euler(0f, RandRange(0f, 360f), 0f), Vector3.one * RandRange(0.6f, 0.95f), Decor.Small, true);
                }
            }
            // Забор — пролёт за пролётом вдоль дороги, на отступе.
            if (forest && Any(fenceModels) && rng.NextDouble() < 0.04)
            {
                float off = s.HalfWidth + RandRange(3f, 5f);
                int n = rng.Next(3, 8);
                const float span = 2.2f, h = 1.1f;
                for (int i = 0; i < n; i++)
                {
                    Vector3 p = s.Pos + s.Right * side * off + s.Forward * (i * span);
                    p.y = GroundY;
                    if (!map.IsClear(p, 1f) || InRiver(p, 1f)) break;
                    // Длинная сторона образца — X: кладём её вдоль дороги, растягиваем на пролёт.
                    var rot = Quaternion.LookRotation(s.Right * side);
                    Model(fenceModels, p, rot, new Vector3(span, h, h), Decor.Small, true);
                }
            }
            // Бетонные блоки в горах — рядком у подножия насыпи.
            if (!forest && Any(blockModels) && rng.NextDouble() < 0.04)
            {
                int n = rng.Next(2, 4);
                float off = s.HalfWidth + RandRange(1f, 3f);
                for (int i = 0; i < n; i++)
                {
                    Vector3 p = s.Pos + s.Right * side * off + s.Forward * (i * 1.4f);
                    p.y = GroundY;
                    if (!map.IsClear(p, 0.8f) || InRiver(p, 0.8f)) break;
                    Model(blockModels, p, Quaternion.LookRotation(s.Forward) * Quaternion.Euler(0f, RandRange(-8f, 8f), 0f), Vector3.one * 0.9f, Decor.Small, true);
                }
            }
        }

        void DecorateMountains(RouteSample s, int side)
        {
            if (rng.NextDouble() < 0.4)
            {
                // Камни у подножия насыпи.
                Vector3 p = s.Pos + s.Right * side * (s.HalfWidth + RandRange(1f, 6f));
                float r = RandRange(0.6f, 2.2f);
                p.y = GroundY + r * 0.4f;
                if (map.IsClear(p, r) && !InRiver(p, r)) Boulder(p, r, false);
            }
            // Ельник: по одной у дороги и рощицами подальше.
            if (rng.NextDouble() < 0.4)
            {
                Vector3 p = s.Pos + s.Right * side * (s.HalfWidth + RandRange(6f, 25f));
                p.y = GroundY;
                if (map.IsClear(p, 2f) && !InRiver(p, 2f)) Pine(p);
            }
            if (rng.NextDouble() < 0.12)
            {
                Vector3 c = s.Pos + s.Right * side * (s.HalfWidth + RandRange(20f, 70f));
                int n = rng.Next(3, 8);
                for (int i = 0; i < n; i++)
                {
                    Vector3 p = c + new Vector3(RandRange(-7f, 7f), 0f, RandRange(-7f, 7f));
                    p.y = GroundY;
                    if (map.IsClear(p, 2f) && !InRiver(p, 2f)) Pine(p);
                }
            }
            // Скалы средней дистанции — между дорогой и горами на горизонте, чтобы не было голой равнины.
            if (Any(rockModels) && rng.NextDouble() < 0.1)
            {
                float r = RandRange(2.5f, 6f);
                Vector3 p = s.Pos + s.Right * side * (s.HalfWidth + r * 1.2f + RandRange(8f, 40f));
                p.y = GroundY;
                if (map.IsClear(p, r * 1.15f) && !InRiver(p, r * 1.15f))
                    Model(rockModels, new Vector3(p.x, GroundY + r * 0.85f, p.z), Quaternion.Euler(0f, RandRange(0f, 360f), 0f),
                          new Vector3(r * 2.1f, r * 1.7f, r * 1.9f), Decor.Tree);
            }
            if (Any(mountainModels) && rng.NextDouble() < 0.06)
            {
                // Холм поближе: та же модель горы, но поменьше.
                float r = RandRange(8f, 16f);
                Vector3 p = s.Pos + s.Right * side * (s.HalfWidth + r * 1.3f + RandRange(15f, 45f));
                if (map.IsClear(new Vector3(p.x, GroundY, p.z), r * 1.25f) && !InRiver(p, r * 1.25f))
                {
                    float h = RandRange(0.6f, 1f);
                    Model(mountainModels, new Vector3(p.x, GroundY + r * MountainBoost * 1.6f * h * 0.5f, p.z), Quaternion.Euler(0f, RandRange(0f, 360f), 0f),
                          new Vector3(r * MountainBoost * 2f, r * MountainBoost * 1.6f * h, r * MountainBoost * 2f * RandRange(0.8f, 1.2f)), Decor.Far);
                }
            }
            if (Any(stoneModels) && rng.NextDouble() < 0.3)
                Scatter(stoneModels, s, side, 0.8f, 10f, 0.3f, 1f, 0.8f, true);
            if (Any(bushModels) && rng.NextDouble() < 0.12)
                Scatter(bushModels, s, side, 2f, 12f, 0.5f, 1f, 1f);
            RoadsideExtras(s, side, false);
            if (rng.NextDouble() < 0.12)
            {
                // Горы на горизонте: большие сплюснутые «холмы» со снежными шапками.
                float r = RandRange(18f, 40f);
                Vector3 p = s.Pos + s.Right * side * (s.HalfWidth + r * 1.3f + RandRange(15f, 50f));
                float h = RandRange(0.7f, 1.2f);
                p.y = GroundY + r * h;   // низ на земле (горы не утапливаем — меньше значит меньше)
                if (!map.IsClear(new Vector3(p.x, GroundY, p.z), r * 1.25f) || InRiver(p, r * 1.25f)) return;
                if (Any(mountainModels))
                {
                    // Модель горы уже со снегом: стоит низом на земле, только поворот по вертикали.
                    float k = RandRange(0.8f, 1.2f);
                    Model(mountainModels, new Vector3(p.x, GroundY + r * MountainBoost * 1.6f * h * 0.5f, p.z), Quaternion.Euler(0f, RandRange(0f, 360f), 0f),
                          new Vector3(r * MountainBoost * 2f, r * MountainBoost * 1.6f * h, r * MountainBoost * 2f * k), Decor.Far);
                    return;
                }
                Piece("Гора", sphere, p, Quaternion.Euler(0f, RandRange(0f, 360f), 0f), new Vector3(r * 2f, r * 2f * h, r * 2f * RandRange(0.8f, 1.2f)),
                      new Color(0.5f, 0.47f, 0.44f), Col.None, Decor.Far);
                if (r > 25f)
                    Piece("Снег", sphere, p + Vector3.up * (r * h * 0.82f), Quaternion.identity, new Vector3(r * 0.7f, r * 0.45f, r * 0.7f), new Color(0.95f, 0.96f, 0.98f), Col.None, Decor.Far);
            }
        }

        void DecorateSnow(RouteSample s, int side)
        {
            // Заснеженный ельник: ряд у дороги и густо подальше.
            if (rng.NextDouble() < 0.45)
            {
                Vector3 p = s.Pos + s.Right * side * (s.HalfWidth + RandRange(3f, 9f));
                p.y = GroundY;
                if (map.IsClear(p, 2f) && !InRiver(p, 2f)) Pine(p, true);
            }
            for (int i = 0; i < 2; i++)
                if (rng.NextDouble() < 0.6)
                {
                    Vector3 p = s.Pos + s.Right * side * (s.HalfWidth + RandRange(10f, 45f)) + s.Forward * RandRange(-2.5f, 2.5f);
                    p.y = GroundY;
                    if (map.IsClear(p, 2.5f) && !InRiver(p, 2.5f)) Pine(p, true);
                }
            // Сугробы у обочины (без коллайдера — просто вид).
            if (Any(driftModels))
            {
                if (rng.NextDouble() < 0.35) Scatter(driftModels, s, side, 1f, 12f, 0.6f, 1.6f, 1.5f);
            }
            else if (rng.NextDouble() < 0.3)
            {
                Vector3 p = s.Pos + s.Right * side * (s.HalfWidth + RandRange(1f, 10f)) + s.Forward * RandRange(-2f, 2f);
                float r = RandRange(1f, 2.5f);
                if (map.IsClear(new Vector3(p.x, GroundY, p.z), r) && !InRiver(p, r))
                    Piece("Сугроб", sphere, new Vector3(p.x, GroundY, p.z), Quaternion.Euler(0f, RandRange(0f, 360f), 0f),
                          new Vector3(r * 2f, r * RandRange(0.5f, 0.8f), r * RandRange(1.2f, 1.8f)), SnowWhite, Col.None, Decor.Small);
            }
            // Камни из-под снега и редкий снеговик у обочины (для антуража).
            if (Any(snowStoneModels) && rng.NextDouble() < 0.12)
                Scatter(snowStoneModels, s, side, 1.5f, 14f, 0.4f, 1.1f, 1f, true);
            if (Any(snowmanModels) && rng.NextDouble() < 0.015)
                Scatter(snowmanModels, s, side, 2f, 6f, 1.6f, 2f, 1f, true);
            RoadsideExtras(s, side, false);
            var mountains = Any(snowMountainModels) ? snowMountainModels : mountainModels;
            if (!Any(mountains)) return;
            // Снежные горы рядом: ближе и чаще, чем в горном биоме.
            if (rng.NextDouble() < 0.1)
            {
                float r = RandRange(12f, 22f);
                Vector3 p = s.Pos + s.Right * side * (s.HalfWidth + r * 1.3f + RandRange(12f, 35f));
                if (map.IsClear(new Vector3(p.x, GroundY, p.z), r * 1.25f) && !InRiver(p, r * 1.25f))
                {
                    float h = RandRange(0.8f, 1.3f);
                    Model(mountains, new Vector3(p.x, GroundY + r * MountainBoost * 1.6f * h * 0.5f, p.z), Quaternion.Euler(0f, RandRange(0f, 360f), 0f),
                          new Vector3(r * MountainBoost * 2f, r * MountainBoost * 1.6f * h, r * MountainBoost * 2f * RandRange(0.8f, 1.2f)), Decor.Far);
                }
            }
            if (rng.NextDouble() < 0.1)
            {
                float r = RandRange(25f, 45f);
                Vector3 p = s.Pos + s.Right * side * (s.HalfWidth + r * 1.3f + RandRange(20f, 60f));
                if (map.IsClear(new Vector3(p.x, GroundY, p.z), r * 1.25f) && !InRiver(p, r * 1.25f))
                {
                    float h = RandRange(1f, 1.5f);
                    Model(mountains, new Vector3(p.x, GroundY + r * MountainBoost * 1.6f * h * 0.5f, p.z), Quaternion.Euler(0f, RandRange(0f, 360f), 0f),
                          new Vector3(r * MountainBoost * 2f, r * MountainBoost * 1.6f * h, r * MountainBoost * 2f * RandRange(0.8f, 1.2f)), Decor.Far);
                }
            }
        }

        /// <summary>
        /// Мост: перила с обеих сторон (столбики и брус, твёрдые — с моста не слететь) и опоры до дна реки.
        /// </summary>
        void BridgeDecor(RoutePath path)
        {
            var sm = path.Samples;
            int i = 0;
            while (i < sm.Count)
            {
                if (!sm[i].Bridge) { i++; continue; }
                int start = i;
                while (i < sm.Count && sm[i].Bridge) i++;
                float d0 = path.Dist[Mathf.Max(0, start - 1)], d1 = path.Dist[i - 1];
                // Перила: столбики через 2 м, между ними брус.
                for (int k = -1; k <= 1; k += 2)
                {
                    RouteSample prev = default;
                    bool has = false;
                    for (float d = d0; d <= d1 + 0.01f; d += 2f)
                    {
                        var s = path.At(Mathf.Min(d, d1));
                        Vector3 p = s.Pos + s.Right * k * (s.HalfWidth - 0.1f);
                        Piece("Мост_столбик", cube, p + Vector3.up * 0.5f, Quaternion.LookRotation(s.Forward), new Vector3(0.18f, 1f, 0.18f), PortalWood, Col.Box);
                        if (has)
                        {
                            Vector3 q = prev.Pos + prev.Right * k * (prev.HalfWidth - 0.1f);
                            Vector3 mid = (p + q) * 0.5f + Vector3.up * 0.95f;
                            Vector3 dir = p - q;
                            if (dir.sqrMagnitude > 0.01f)
                                Piece("Мост_перила", cube, mid, Quaternion.LookRotation(dir), new Vector3(0.14f, 0.14f, dir.magnitude + 0.1f), PortalWood, Col.Box);
                        }
                        prev = s; has = true;
                    }
                }
                // Опоры: пары столбов до дна через ~7 м.
                for (float d = d0 + 3f; d < d1 - 2f; d += 7f)
                {
                    var s = path.At(d);
                    float top = s.Pos.y - BridgeDeck, h = top - RiverBedY;
                    for (int k = -1; k <= 1; k += 2)
                        if (!OnUnderpass(s.Pos + s.Right * k * (s.HalfWidth - 0.8f), 1.5f))
                        Piece("Мост_опора", cube, s.Pos + s.Right * k * (s.HalfWidth - 0.8f) + Vector3.up * (top - s.Pos.y - h / 2f),
                              Quaternion.LookRotation(s.Forward), new Vector3(0.7f, h, 0.7f), new Color(PortalWood.r * 0.85f, PortalWood.g * 0.85f, PortalWood.b * 0.85f), Col.Box);
                }
            }
        }

        /// <summary>
        /// Препятствие прямо на дороге — кучка мусора (бочки, покрышки, мешки), твёрдая.
        /// Деревья на дороге убраны: мешали больше, чем веселили. Нет моделей мусора — валун.
        /// </summary>
        void RoadJunk(Vector3 at, float groundY)
        {
            if (!Any(junkModels)) { Boulder(new Vector3(at.x, groundY + 0.5f, at.z), 0.7f, true); return; }
            int n = rng.Next(2, 4);
            for (int i = 0; i < n; i++)
            {
                Vector3 p = at + new Vector3(RandRange(-0.8f, 0.8f), 0f, RandRange(-0.8f, 0.8f));
                p.y = groundY;
                Model(junkModels, p, Quaternion.Euler(0f, RandRange(0f, 360f), 0f), Vector3.one * RandRange(0.65f, 0.95f), Decor.Solid, true);
            }
        }

        void TryTree(Vector3 p, float clearance)
        {
            if (InRiver(p, 2f)) return;
            p.y = GroundY;
            if (map.IsClear(p, clearance)) Tree(p, GroundY, RandRange(0.25f, 0.4f), false);
        }

        // ============================ Объекты ============================

        enum Col { None, Box, Sphere, Capsule }

        /// <summary>
        /// Solid — важное (препятствия, дорога, арки): с тенью, видно всегда.
        /// Small — мелочь (столбики, кусты, камни): без тени, пропадает дальше 70 м.
        /// Tree — деревья: с тенью, пропадают дальше 160 м (там уже туман).
        /// Far — горы на горизонте и земля: без тени, видно всегда.
        /// </summary>
        enum Decor { Solid, Small, Tree, Far }

        int smallLayer = -1, treeLayer = -1;

        void Tree(Vector3 at, float groundY, float trunkRadius, bool onRoad)
        {
            Decor kind = onRoad ? Decor.Solid : Decor.Tree;
            float h = RandRange(3f, 5f);
            Vector3 basePos = new Vector3(at.x, groundY, at.z);
            if (Any(treeModels))
            {
                float c0 = RandRange(2.4f, 3.6f);
                Hidden(Piece("Ствол", cylinder, basePos + Vector3.up * (h / 2f), Quaternion.identity,
                             new Vector3(trunkRadius * 2f, h / 2f, trunkRadius * 2f), Color.white, Col.Capsule, kind));
                Model(treeModels, basePos, Quaternion.Euler(0f, RandRange(0f, 360f), 0f), Vector3.one * (h + c0 * 0.8f), kind);
                return;
            }
            Piece("Ствол", cylinder, basePos + Vector3.up * (h / 2f), Quaternion.identity,
                  new Vector3(trunkRadius * 2f, h / 2f, trunkRadius * 2f), new Color(0.4f, 0.27f, 0.16f), Col.Capsule, kind);
            Color leaves = Color.Lerp(new Color(0.22f, 0.45f, 0.2f), new Color(0.35f, 0.55f, 0.22f), (float)rng.NextDouble());
            float c = RandRange(2.4f, 3.6f);
            Piece("Крона", sphere, basePos + Vector3.up * (h + c * 0.25f), Quaternion.identity, new Vector3(c, c * 0.9f, c), leaves, Col.None, kind);
            if (!onRoad && rng.NextDouble() < 0.5)
                Piece("Крона", sphere, basePos + Vector3.up * (h + c * 0.65f) + new Vector3(RandRange(-0.4f, 0.4f), 0f, RandRange(-0.4f, 0.4f)),
                      Quaternion.identity, Vector3.one * c * 0.65f, leaves * 1.08f, Col.None, kind);
        }

        void Pine(Vector3 at, bool snowy = false)
        {
            float h = RandRange(4f, 7f);
            var set = snowy && Any(snowPineModels) ? snowPineModels : pineModels;
            if (Any(set))
            {
                Hidden(Piece("Ствол", cylinder, at + Vector3.up * (h * 0.3f), Quaternion.identity, new Vector3(0.4f, h * 0.3f, 0.4f), Color.white, Col.Capsule, Decor.Tree));
                Model(set, at, Quaternion.Euler(0f, RandRange(0f, 360f), 0f), Vector3.one * h, Decor.Tree);
                return;
            }
            Piece("Ствол", cylinder, at + Vector3.up * (h * 0.3f), Quaternion.identity, new Vector3(0.4f, h * 0.3f, 0.4f), new Color(0.35f, 0.24f, 0.15f), Col.Capsule, Decor.Tree);
            Color needles = snowy ? new Color(0.62f, 0.72f, 0.7f) : new Color(0.16f, 0.32f, 0.2f);
            for (int i = 0; i < 3; i++)
            {
                float t = i / 3f, w = Mathf.Lerp(2.6f, 1f, t);
                Piece("Хвоя", cube, at + Vector3.up * (h * (0.45f + t * 0.4f)), Quaternion.Euler(0f, 45f * i, 0f), new Vector3(w, h * 0.22f, w), needles, Col.None, Decor.Tree);
            }
        }

        void Bush(Vector3 at)
        {
            float r = RandRange(0.8f, 1.5f);
            if (Any(bushModels))
            {
                Model(bushModels, at, Quaternion.Euler(0f, RandRange(0f, 360f), 0f), Vector3.one * r, Decor.Small);
                return;
            }
            Piece("Куст", sphere, at + Vector3.up * (r * 0.3f), Quaternion.identity, new Vector3(r * 1.4f, r, r * 1.4f), new Color(0.28f, 0.48f, 0.22f), Col.None, Decor.Small);
        }

        void Boulder(Vector3 at, float r, bool onRoad)
        {
            var rot = Quaternion.Euler(RandRange(0f, 360f), RandRange(0f, 360f), RandRange(0f, 360f));
            Color c = Color.Lerp(new Color(0.45f, 0.43f, 0.4f), new Color(0.6f, 0.58f, 0.55f), (float)rng.NextDouble());
            var stone = Piece(onRoad ? "Валун" : "Камень", sphere, at, rot, new Vector3(r * 2f, r * 1.6f, r * 1.8f), c, Col.Sphere,
                              onRoad ? Decor.Solid : Decor.Small);
            if (Any(rockModels))
            {
                Hidden(stone);
                // У моделей камней снизу подставка с травой — не кувыркаем, только поворот по вертикали,
                // и ставим низом на землю (центр шара-коллайдера поднят на 0.4r в поле и на 0.7r на дороге).
                float ground = at.y - r * (onRoad ? 0.7f : 0.4f);
                float height = r * 1.7f;
                Model(rockModels, new Vector3(at.x, ground + height * 0.5f, at.z), Quaternion.Euler(0f, rot.eulerAngles.y, 0f),
                      new Vector3(r * 2.1f, height, r * 1.9f), onRoad ? Decor.Solid : Decor.Small);
            }
        }

        /// <summary>Бревно поперёк дороги, наполовину утоплено — получается кочка.</summary>
        void Log(RouteSample s, float r)
        {
            float len = s.HalfWidth * 2f + 1f;
            var rot = Quaternion.LookRotation(s.Forward) * Quaternion.Euler(0f, 0f, 90f);
            var log = Piece("Бревно", cylinder, s.Pos + Vector3.up * (r * 0.6f), rot, new Vector3(r * 2f, len / 2f, r * 2f), new Color(0.45f, 0.3f, 0.18f), Col.Capsule);
            if (!Any(logModels)) return;
            Hidden(log);
            // Несколько брёвен в ряд: одно растянутое на всю ширину дороги выглядело бы макарониной.
            int n = Mathf.Max(1, Mathf.RoundToInt(len / (r * 2f * 3f)));
            float seg = len / n;
            var along = Quaternion.LookRotation(s.Forward);
            for (int i = 0; i < n; i++)
            {
                float off = -len / 2f + seg * (i + 0.5f);
                Model(logModels, s.Pos + Vector3.up * (r * 0.6f) + s.Right * off, along, new Vector3(seg * 1.02f, r * 2f, r * 2f), Decor.Solid);
            }
        }

        /// <summary>Арка (старт, финиш).</summary>
        /// <summary>Горы и холмы из моделей крупнее расчётного радиуса — смотрятся внушительнее на горизонте.</summary>
        const float MountainBoost = 1.6f;

        // Арка-модель: просвет между башнями — 42% ширины, его середина — на 47% ширины слева (модель чуть несимметрична).
        const float ArchGap = 0.42f, ArchGapCenter = 0.47f;

        void Gate(Vector3 at, float yawDeg, float span, Color color)
        {
            var rot = Quaternion.Euler(0f, yawDeg, 0f);
            Vector3 r = rot * Vector3.right;
            bool model = Any(archModels);
            for (int k = -1; k <= 1; k += 2)
            {
                var post = Piece("Столб", cube, at + r * k * (span / 2f + 0.6f) + Vector3.up * 2.5f, rot, new Vector3(0.6f, 5f, 0.6f), color, Col.Box);
                if (model) Hidden(post);   // столбы остаются коллайдерами внутри башен
            }
            if (!model)
            {
                Piece("Перекладина", cube, at + Vector3.up * 5.2f, rot, new Vector3(span + 1.8f, 0.8f, 0.5f), color, Col.None);
                return;
            }
            // Просвет арки — во всю ширину дороги; по высоте сжимаем (иначе арка выше леса), башни чуть шире — мультяшно.
            float w = (span + 1.2f) / ArchGap;
            float h = Mathf.Min(w * 0.73f, span * 0.85f);
            float d = h * 0.3f;
            var c = at - r * (ArchGapCenter - 0.5f) * w;
            Model(archModels, c, rot, new Vector3(w, h, d * 2f), Decor.Solid);
        }

        void Finish(RouteSample s)
        {
            float yawDeg = Mathf.Atan2(s.Forward.x, s.Forward.z) * Mathf.Rad2Deg;
            float span = s.HalfWidth * 2f + 2f;
            Gate(s.Pos, yawDeg, span, new Color(0.2f, 0.85f, 0.3f));
            var go = new GameObject("Финиш");
            go.transform.SetParent(root, false);
            go.transform.SetPositionAndRotation(s.Pos + Vector3.up * 3f, Quaternion.Euler(0f, yawDeg, 0f));
            go.AddComponent<BoxCollider>().size = new Vector3(span + 10f, 6f, 2f);
            go.AddComponent<FinishLine>();
        }

        static bool Any(GameObject[] set) => set != null && set.Length > 0 && set[0] != null;

        /// <summary>Прячет вид примитива — остаётся только коллайдер (вид даёт модель).</summary>
        static void Hidden(GameObject go)
        {
            DestroyImmediate(go.GetComponent<MeshRenderer>());
            DestroyImmediate(go.GetComponent<MeshFilter>());
        }

        /// <summary>Копия случайного образца модели: позиция, поворот и масштаб — как у примитива.</summary>
        GameObject Model(GameObject[] set, Vector3 position, Quaternion rotation, Vector3 scale, Decor decor, bool solid = false)
        {
            var src = set[rng.Next(set.Length)];
            var go = Instantiate(src, position, rotation, root);
            go.transform.localScale = scale;
            int layer = decor == Decor.Small ? smallLayer : decor == Decor.Tree ? treeLayer : -1;
            foreach (var t in go.GetComponentsInChildren<Transform>(true))
            {
                if (layer >= 0) t.gameObject.layer = layer;
                t.gameObject.isStatic = true;
            }
            if (decor == Decor.Small || decor == Decor.Far)
                foreach (var r in go.GetComponentsInChildren<Renderer>(true)) r.shadowCastingMode = ShadowCastingMode.Off;
            go.SetActive(true);
            if (solid) FitBox(go);
            return go;
        }

        /// <summary>
        /// Твёрдая коробка по габаритам модели: у образцов коллайдеры срезаны при подгонке, а мусор, бетон,
        /// камни и пни должны мешать — в них врезаются, а не проезжают насквозь.
        /// </summary>
        static void FitBox(GameObject go)
        {
            bool any = false;
            Bounds b = default;
            foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                var mb = mf.sharedMesh.bounds;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 c = mb.center + Vector3.Scale(mb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    Vector3 l = go.transform.InverseTransformPoint(mf.transform.TransformPoint(c));
                    if (!any) { b = new Bounds(l, Vector3.zero); any = true; } else b.Encapsulate(l);
                }
            }
            if (!any) return;
            var box = go.AddComponent<BoxCollider>();
            box.center = b.center;
            box.size = b.size * 0.9f;   // чуть меньше картинки: не цепляться за воздух у краёв
        }

        GameObject Piece(string name, Mesh mesh, Vector3 position, Quaternion rotation, Vector3 scale, Color color, Col collider,
                         Decor decor = Decor.Solid)
        {
            var go = new GameObject(name);
            go.transform.SetParent(root, false);
            go.transform.SetPositionAndRotation(position, rotation);
            go.transform.localScale = scale;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = Mat(color);
            if (decor == Decor.Small || decor == Decor.Far) mr.shadowCastingMode = ShadowCastingMode.Off;
            if (decor == Decor.Small && smallLayer >= 0) go.layer = smallLayer;
            if (decor == Decor.Tree && treeLayer >= 0) go.layer = treeLayer;
            switch (collider)
            {
                case Col.Box: go.AddComponent<BoxCollider>(); break;
                case Col.Sphere: go.AddComponent<SphereCollider>(); break;
                case Col.Capsule: go.AddComponent<CapsuleCollider>(); break;
            }
            go.isStatic = true;
            return go;
        }

        /// <summary>Мелочь и деревья вдали не рисуем — их всё равно прячет туман.</summary>
        void ApplyCullDistances()
        {
            var cam = Camera.main;
            if (cam == null) return;
            var d = cam.layerCullDistances;
            if (d == null || d.Length != 32) d = new float[32];
            // С дымкой (StylizedAtmosphere) видно дальше: деревья тают в воздухе, а не выскакивают.
            var atmo = StylizedAtmosphere.Instance;
            if (smallLayer >= 0) d[smallLayer] = atmo != null ? atmo.SmallCullDistance : SmallDecorDistance;
            if (treeLayer >= 0) d[treeLayer] = atmo != null ? atmo.TreeCullDistance : TreeDistance;
            cam.layerCullDistances = d;
            cam.layerCullSpherical = true;   // по расстоянию, а не по плоскости — не мигает при повороте головы
        }

        void PreparePrimitives()
        {
            if (cube != null) return;
            cube = PrimitiveMesh(PrimitiveType.Cube);
            sphere = PrimitiveMesh(PrimitiveType.Sphere);
            cylinder = PrimitiveMesh(PrimitiveType.Cylinder);
        }

        static Mesh PrimitiveMesh(PrimitiveType type)
        {
            var tmp = GameObject.CreatePrimitive(type);
            var mesh = tmp.GetComponent<MeshFilter>().sharedMesh;
            Destroy(tmp);
            return mesh;
        }

        /// <summary>Материал по цвету — из образца, один на цвет.</summary>
        Material Mat(Color color)
        {
            Color32 key = color;
            if (materials.TryGetValue(key, out var m) && m != null) return m;
            if (template != null)
            {
                m = new Material(template);
                // Шейдер Toon Shaders Pro умножает на цвет дважды — компенсируем, как в остальной сцене.
                bool squares = template.shader != null && template.shader.name.StartsWith("Toon Shaders Pro");
                m.color = squares ? new Color(Mathf.Sqrt(color.r), Mathf.Sqrt(color.g), Mathf.Sqrt(color.b), color.a) : color;
            }
            else
            {
                var shader = Shader.Find("Universal Render Pipeline/Lit");
                m = new Material(shader) { color = color };
            }
            m.enableInstancing = true;
            materials[key] = m;
            return m;
        }

        // ============================ Мелочи ============================

        float RandRange(float a, float b) => a + (float)rng.NextDouble() * (b - a);

        void Shuffle<T>(List<T> list)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }

        static float Smooth(float t) => t * t * (3f - 2f * t);
    }
}
