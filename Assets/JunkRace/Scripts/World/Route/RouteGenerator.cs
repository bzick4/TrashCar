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
        [Header("Мелочь у дороги (Assets/Prefab/Route: bush, Stone, Other)")]
        [SerializeField] GameObject[] bushModels;
        [SerializeField] GameObject[] stoneModels;
        [SerializeField] GameObject[] stumpModels;
        [SerializeField] GameObject[] postModels;
        [SerializeField] GameObject[] blockModels;
        [SerializeField] GameObject[] junkModels;
        [SerializeField] GameObject[] fenceModels;

        public string Summary { get; private set; } = "";
        public bool Generated => map != null;

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
            Seed = seed;
            Players = Mathf.Clamp(players, 1, 4);
            BuildSurroundings();
            YardShuffle.Apply(seed, transform.position, Players);
            YardReady = true;
        }

        Transform tempRoot;

        /// <summary>
        /// Земля вокруг свалки (по бокам и сзади — там стоят крупные декорации) и, до постройки трассы,
        /// заглушка впереди: полоса земли и шлагбаум на выезде — чтобы не уехать в пустоту раньше старта.
        /// </summary>
        void BuildSurroundings()
        {
            PreparePrimitives();
            Vector3 g = transform.position;
            const float far = 300f, yardHalf = 21f, yardDepth = 40f;

            root = new GameObject("Окрестности_свалки").transform;
            root.SetParent(transform, false);
            float zc = g.z - far / 2f;
            GroundPiece("Земля_Л", new Vector3(g.x - yardHalf - far / 2f, GroundY - 1f, zc), new Vector3(far, 2f, far));
            GroundPiece("Земля_П", new Vector3(g.x + yardHalf + far / 2f, GroundY - 1f, zc), new Vector3(far, 2f, far));
            float backLen = far - yardDepth;
            GroundPiece("Земля_Зад", new Vector3(g.x, GroundY - 1f, g.z - yardDepth - backLen / 2f), new Vector3(yardHalf * 2f, 2f, backLen));

            // Временное — до трассы.
            tempRoot = new GameObject("До_старта").transform;
            tempRoot.SetParent(transform, false);
            root = tempRoot;
            GroundPiece("Земля_впереди", new Vector3(g.x, GroundY - 1f, g.z + 40f - 0.5f), new Vector3(yardHalf * 2f + far * 2f, 2f, 80f));
            Piece("Шлагбаум", cube, new Vector3(g.x, 0.6f, g.z + 0.6f), Quaternion.identity, new Vector3(14f, 1.2f, 0.4f), new Color(0.9f, 0.15f, 0.1f), Col.Box);
            Piece("Шлагбаум_полоса", cube, new Vector3(g.x, 0.6f, g.z + 0.38f), Quaternion.identity, new Vector3(14.02f, 0.3f, 0.02f), Color.white, Col.None);
            root = null;
        }

        /// <summary>Построить трассу по сиду (вторая загрузка, после сборки машин). Повторно — ничего.</summary>
        public void BuildRoute()
        {
            if (Generated || !YardReady) return;
            if (tempRoot != null) { Destroy(tempRoot.gameObject); tempRoot = null; }   // шлагбаум и заглушка — прочь
            Generate(Seed, Players);
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

        public void Generate(int seed, int players = 1)
        {
            Seed = seed;
            Players = Mathf.Clamp(players, 1, 4);
            rng = new System.Random(seed);
            forks = 0;
            rivers.Clear();
            routeLength = 0f;
            map = new RouteMap { OffMargin = offRouteMargin };
            root = new GameObject("Маршрут").transform;
            root.SetParent(transform, false);
            PreparePrimitives();
            smallLayer = LayerMask.NameToLayer(SmallDecorLayer);
            treeLayer = LayerMask.NameToLayer(TreeLayer);

            // Свалка — тоже часть маршрута (для сброса), но дорогу на ней не строим.
            var yard = map.NewPath("Свалка");
            yard.BuildMesh = false;
            for (float z = -40f; z <= 0.01f; z += 4f)
                yard.Add(new RouteSample { Pos = transform.position + new Vector3(0f, 0f, z), Forward = Vector3.forward, HalfWidth = 21f, Road = true });

            pos = transform.position;
            yaw = 0f;
            width = 14f;
            biome = Biome.Forest;
            yard.StartProgress = -yard.Length;
            main = map.NewPath("Дорога");
            AddSample(main);
            map.Start = main.Samples[0];
            Gate(pos, 0f, 14f, new Color(0.95f, 0.75f, 0.15f));
            Run(main, 24f, 0f, 0f, ForestWidth);

            // Участки: 3–4 штуки, биомы чередуются, первый — случайный. В каждом 2–3 куска и, может, развилка.
            int sections = 3 + rng.Next(2);
            var b = rng.Next(2) == 0 ? Biome.Forest : Biome.Mountains;
            var names = new List<string>();
            for (int i = 0; i < sections; i++, b = b == Biome.Forest ? Biome.Mountains : Biome.Forest)
            {
                biome = b;
                names.Add(b == Biome.Forest ? "Лес" : "Горы");
                bool fork = forks == 0 && i == sections - 1 || rng.NextDouble() < 0.6;   // хотя бы одна развилка
                int chunks = 2 + rng.Next(2);
                if (b == Biome.Forest) BuildForest(chunks, fork); else BuildMountains(chunks, fork);
            }
            ApplySun();

            // Финиш и площадка после него.
            Run(main, 30f, RandRange(-10f, 10f), -Mathf.Min(pos.y, 4f));
            var end = main.Last;
            map.FinishProgress = main.StartProgress + main.Length;
            Finish(end);
            Run(main, 30f, 0f, 0f);

            foreach (var p in map.Paths) p.SmoothForwards();
            foreach (var p in map.Paths) if (p.BuildMesh) BuildRoadMesh(p);
            foreach (var f in forkFills) BuildForkFill(f.left, f.right);
            forkFills.Clear();
            Decorate();
            BuildGround();

            StaticBatchingUtility.Combine(root.gameObject);
            RouteMap.Current = map;
            ApplyCullDistances();

            Summary = $"Маршрут: {string.Join(" → ", names)} · развилок: {forks} · ~{Mathf.RoundToInt(routeLength / 10f) * 10} м · сид {seed}";
        }

        // ---------------- Биомы ----------------

        void BuildForest(int chunks, bool fork)
        {
            riverInSection = false;
            Run(main, 20f, RandRange(-35f, 35f), -Mathf.Min(pos.y, 1f), ForestWidth);
            int forkAt = fork ? rng.Next(0, chunks + 1) : -1;
            for (int i = 0; i < chunks; i++)
            {
                if (i == forkAt) ForestFork();
                // Река — не чаще одной на участок и не больше двух на трассу.
                int kinds = rivers.Count < 2 && !riverInSection ? 6 : 5;
                switch (rng.Next(kinds))
                {
                    case 0: ForestRoadChunk(); break;
                    case 1: ForestSlalom(); break;
                    case 2: ForestLogs(); break;
                    case 3: ForestHills(); break;
                    case 4: ForestSweep(); break;
                    default: ForestRiver(); riverInSection = true; break;
                }
            }
            if (forkAt == chunks) ForestFork();
        }

        void BuildMountains(int chunks, bool fork)
        {
            Run(main, 20f, RandRange(-35f, 35f), 0f, MountainWidth);
            // В горы — сначала вверх: развилке с прыжком нужна высота, иначе падать некуда.
            MountainClimb();
            if (rng.Next(2) == 0) MountainClimb();
            int forkAt = fork ? rng.Next(0, chunks) : -1;
            for (int i = 0; i < chunks; i++)
            {
                if (i == forkAt) MountainFork();
                switch (rng.Next(4))
                {
                    case 0: MountainSerpentine(); break;
                    case 1: MountainRockfall(); break;
                    case 2: MountainLedge(); break;
                    default: MountainDip(); break;
                }
            }
            // Спуск к равнине.
            while (pos.y > 3f) Run(main, 60f, RandRange(-20f, 20f), -Mathf.Min(pos.y - 1f, RandRange(8f, 12f)));
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
                Tree(s.Pos + s.Right * side * (s.HalfWidth - 2.2f), s.Pos.y, 0.35f, true);
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

        /// <summary>Реки поперёк карты: интервалы по z (дорога идёт только вперёд по z — пересекает каждую один раз).</summary>
        readonly List<Vector2> rivers = new List<Vector2>();
        bool riverInSection;

        /// <summary>Река поперёк дороги: спуск к воде, брод, подъём на другой берег.</summary>
        void ForestRiver()
        {
            if (pos.y > 0.3f) Run(main, 16f, 0f, -pos.y, ForestWidth);
            Run(main, 14f, -yaw * 0.7f, 0f, ForestWidth);   // почти поперёк реки
            float z0 = pos.z;
            Run(main, 10f, 0f, FordY - pos.y, ForestWidth + 1f, FordY);
            Run(main, RandRange(14f, 24f), RandRange(-8f, 8f), 0f, ForestWidth + 1f, FordY);
            Run(main, 10f, 0f, -FordY, ForestWidth, FordY);
            float z1 = pos.z;
            // Берег — там, где дорога уже ниже воды (~3 м от начала спуска).
            rivers.Add(new Vector2(z0 + 3f, z1 - 3f));
            Run(main, 12f, 0f, 0f, ForestWidth);
        }

        bool InRiver(Vector3 p, float margin = 1f)
        {
            foreach (var r in rivers) if (p.z > r.x - margin && p.z < r.y + margin) return true;
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
            // Прыжок — в двух случаях из трёх, иначе две обычные ветки.
            if (rng.Next(3) > 0) Fork(jump, pool[0], 110f);
            else Fork(pool[0], pool[1], RandRange(100f, 130f));
        }

        /// <summary>Солнце под своим углом на каждой трассе: тени и свет другие — и трасса ощущается иначе.</summary>
        void ApplySun()
        {
            var sun = RenderSettings.sun;
            if (sun == null)
                foreach (var l in FindObjectsByType<Light>(FindObjectsSortMode.None))
                    if (l.type == LightType.Directional) { sun = l; break; }
            if (sun == null) return;
            if (!sunBaseSaved) { sunBaseColor = sun.color; sunBaseSaved = true; }
            sun.transform.rotation = Quaternion.Euler(RandRange(28f, 62f), RandRange(0f, 360f), 0f);
            // Чуть теплее или холоднее: утро, день, вечер.
            float warm = RandRange(-1f, 1f);
            sun.color = sunBaseColor * new Color(1f + 0.08f * warm, 1f, 1f - 0.1f * warm);
        }

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
            });
        }

        /// <summary>
        /// Проехать length метров: повернуть на turn градусов (плавной дугой), подняться на rise
        /// (плавно в начале и в конце), ширина плавно меняется к widthTo.
        /// </summary>
        void Run(RoutePath path, float length, float turn, float rise, float widthTo = -1f, float minY = 0f)
        {
            float yaw0 = yaw, y0 = pos.y, w0 = width, w1 = widthTo > 0f ? widthTo : width;
            turn = Mathf.Clamp(yaw0 + turn, -MaxYaw, MaxYaw) - yaw0;
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

        enum BranchKind { Plain, Trail, Jump }

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
                Vector3 p = start + fwd * (length * t) + rightDir * lateral;
                p.y = start.y + h;
                path.Add(new RouteSample { Pos = p, Forward = fwd, HalfWidth = b.Width / 2f, Road = road, Biome = biome });
            }
            path.SmoothForwards();   // до расстановки препятствий: им нужно верное «вбок»

            // Препятствия веток.
            if (b.Kind == BranchKind.Trail)
            {
                for (float d = 10f; d < path.Length - 10f; d += RandRange(7f, 11f))
                {
                    var s = path.At(d);
                    Tree(s.Pos + s.Right * (rng.Next(2) == 0 ? -1f : 1f) * (s.HalfWidth - 0.9f), s.Pos.y, 0.25f, true);
                }
                Log(path.At(path.Length * 0.3f), 0.2f);
                Log(path.At(path.Length * 0.7f), 0.2f);
            }
            return path;
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
            var subs = new List<int>[4];   // [биом*2 + 0] верх, [биом*2 + 1] откосы
            for (int i = 0; i < subs.Length; i++) subs[i] = new List<int>();

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

                if (a.Road && b.Road)
                {
                    // Поперёк три полосы: обочины с травой постоянной ширины (RoadEdge м), середина — грунт.
                    // Иначе на широкой площадке развилки трава по краям раздувалась бы на метры.
                    float ea = Mathf.Min(RoadEdge, a.HalfWidth * 0.3f), eb = Mathf.Min(RoadEdge, b.HalfWidth * 0.3f);
                    Vector3 aL2 = aL + a.Right * ea, aR2 = aR - a.Right * ea, bL2 = bL + b.Right * eb, bR2 = bR - b.Right * eb;
                    Quad(verts, uvs, subs[top], aL, bL, bL2, aL2, new Vector2(0f, v0), new Vector2(0f, v1), new Vector2(EdgeU, v1), new Vector2(EdgeU, v0));
                    Quad(verts, uvs, subs[top], aL2, bL2, bR2, aR2, new Vector2(EdgeU, v0), new Vector2(EdgeU, v1), new Vector2(1f - EdgeU, v1), new Vector2(1f - EdgeU, v0));
                    Quad(verts, uvs, subs[top], aR2, bR2, bR, aR, new Vector2(1f - EdgeU, v0), new Vector2(1f - EdgeU, v1), new Vector2(1f, v1), new Vector2(1f, v0));
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
            go.AddComponent<MeshRenderer>().sharedMaterials = new[]
            {
                roadForest ? roadForest : Mat(ForestRoad), sideForest ? sideForest : Mat(ForestSide),
                roadMountain ? roadMountain : Mat(MountainRoad), sideMountain ? sideMountain : Mat(MountainSide),
            };
            go.AddComponent<MeshCollider>().sharedMesh = mesh;
            go.isStatic = true;
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
            var subs = new List<int>[4];
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

        void BuildGround()
        {
            var b = new Bounds(transform.position, Vector3.zero);
            foreach (var p in map.Paths) foreach (var s in p.Samples) b.Encapsulate(new Vector3(s.Pos.x, 0f, s.Pos.z));
            b.Expand(new Vector3(260f, 0f, 260f));
            // Под свалкой земля своя — наша начинается от выезда.
            float zMin = transform.position.z - 0.5f;
            float zMax = b.max.z;
            // Земля полосами между реками; в реках — вода и дно.
            rivers.Sort((p, q) => p.x.CompareTo(q.x));
            float from = zMin;
            foreach (var r in rivers)
            {
                GroundStrip(b, from, r.x);
                float len = r.y - r.x, zc = (r.x + r.y) / 2f;
                Piece("Дно_реки", cube, new Vector3(b.center.x, RiverBedY - 1f, zc), Quaternion.identity,
                      new Vector3(b.size.x, 2f, len), new Color(0.35f, 0.3f, 0.22f), Col.Box, Decor.Far);
                var water = Piece("Река", cube, new Vector3(b.center.x, (WaterY + RiverBedY) / 2f, zc), Quaternion.identity,
                                  new Vector3(b.size.x, WaterY - RiverBedY, len), WaterColor, Col.None, Decor.Far);
                water.AddComponent<BoxCollider>();   // триггер (WaterZone сам выставит): верх коробки — поверхность воды
                water.AddComponent<WaterZone>();
                from = r.y;
            }
            GroundStrip(b, from, zMax);   // по бокам и позади свалки землю уже положил BuildSurroundings
        }

        /// <summary>Плита земли с травой: текстура повторяется каждые GroundTile метров, как бы ни была велика плита.</summary>
        GameObject GroundPiece(string name, Vector3 position, Vector3 size)
        {
            var go = Piece(name, cube, position, Quaternion.identity, size, Grass, Col.Box, Decor.Far);
            if (groundGrass != null) go.GetComponent<MeshRenderer>().sharedMaterial = Tiled(groundGrass, new Vector2(size.x / GroundTile, size.z / GroundTile));
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

        void GroundStrip(Bounds b, float z0, float z1)
        {
            if (z1 - z0 < 0.1f) return;
            GroundPiece("Земля", new Vector3(b.center.x, GroundY - 1f, (z0 + z1) / 2f), new Vector3(b.size.x, 2f, z1 - z0));
        }

        // ============================ Декорации ============================

        void Decorate()
        {
            foreach (var path in map.Paths)
            {
                if (!path.BuildMesh) continue;
                // Столбики по краям — чтобы в открытом мире было видно, где дорога.
                for (float d = 4f; d < path.Length; d += 12f)
                {
                    var s = path.At(d);
                    if (!s.Road || s.Pos.y < WaterY + 0.2f) continue;   // у брода столбики под водой ни к чему
                    for (int k = -1; k <= 1; k += 2)
                    {
                        Vector3 p = s.Pos + s.Right * k * (s.HalfWidth + 0.4f);
                        if (Any(postModels))
                        {
                            Model(postModels, p, Quaternion.LookRotation(s.Forward), Vector3.one * 0.9f, Decor.Small);
                            continue;
                        }
                        Piece("Столбик", cube, p + Vector3.up * 0.4f, Quaternion.identity, new Vector3(0.15f, 0.8f, 0.15f), Color.white, Col.None, Decor.Small);
                        Piece("Столбик_верх", cube, p + Vector3.up * 0.85f, Quaternion.identity, new Vector3(0.17f, 0.12f, 0.17f), new Color(0.9f, 0.2f, 0.1f), Col.None, Decor.Small);
                    }
                }

                for (float d = 0f; d < path.Length; d += 5f)
                {
                    var s = path.At(d);
                    for (int k = -1; k <= 1; k += 2)
                    {
                        if (s.Biome == Biome.Forest) DecorateForest(s, k);
                        else DecorateMountains(s, k);
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
                Scatter(stumpModels, s, side, 2f, 15f, 0.45f, 0.8f, 1f);
            if (Any(stoneModels) && rng.NextDouble() < 0.08)
                Scatter(stoneModels, s, side, 1.5f, 12f, 0.3f, 0.8f, 0.8f);
            RoadsideExtras(s, side, true);
        }

        /// <summary>Модель на земле сбоку от дороги: dist — от края дороги, height — высота модели.</summary>
        GameObject Scatter(GameObject[] set, RouteSample s, int side, float near, float far, float minH, float maxH, float clear)
        {
            Vector3 p = s.Pos + s.Right * side * (s.HalfWidth + RandRange(near, far)) + s.Forward * RandRange(-2f, 2f);
            p.y = GroundY;
            if (!map.IsClear(p, clear) || InRiver(p, clear)) return null;
            return Model(set, p, Quaternion.Euler(0f, RandRange(0f, 360f), 0f), Vector3.one * RandRange(minH, maxH), Decor.Small);
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
                        Model(junkModels, p, Quaternion.Euler(0f, RandRange(0f, 360f), 0f), Vector3.one * RandRange(0.6f, 0.95f), Decor.Small);
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
                    Model(fenceModels, p, rot, new Vector3(span, h, h), Decor.Small);
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
                    Model(blockModels, p, Quaternion.LookRotation(s.Forward) * Quaternion.Euler(0f, RandRange(-8f, 8f), 0f), Vector3.one * 0.9f, Decor.Small);
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
                Vector3 p = s.Pos + s.Right * side * (s.HalfWidth + r + RandRange(8f, 40f));
                p.y = GroundY;
                if (map.IsClear(p, r) && !InRiver(p, r))
                    Model(rockModels, new Vector3(p.x, GroundY + r * 0.85f - r * 0.05f, p.z), Quaternion.Euler(0f, RandRange(0f, 360f), 0f),
                          new Vector3(r * 2.1f, r * 1.7f, r * 1.9f), Decor.Tree);
            }
            if (Any(mountainModels) && rng.NextDouble() < 0.06)
            {
                // Холм поближе: та же модель горы, но поменьше.
                float r = RandRange(8f, 16f);
                Vector3 p = s.Pos + s.Right * side * (s.HalfWidth + r + RandRange(15f, 45f));
                if (map.IsClear(new Vector3(p.x, GroundY, p.z), r * 0.9f))
                {
                    float h = RandRange(0.6f, 1f);
                    Model(mountainModels, new Vector3(p.x, GroundY + r * 1.6f * h * 0.5f - r * 0.1f, p.z), Quaternion.Euler(0f, RandRange(0f, 360f), 0f),
                          new Vector3(r * 2f, r * 1.6f * h, r * 2f * RandRange(0.8f, 1.2f)), Decor.Far);
                }
            }
            if (Any(stoneModels) && rng.NextDouble() < 0.3)
                Scatter(stoneModels, s, side, 0.8f, 10f, 0.3f, 1f, 0.8f);
            if (Any(bushModels) && rng.NextDouble() < 0.12)
                Scatter(bushModels, s, side, 2f, 12f, 0.5f, 1f, 1f);
            RoadsideExtras(s, side, false);
            if (rng.NextDouble() < 0.12)
            {
                // Горы на горизонте: большие сплюснутые «холмы» со снежными шапками.
                float r = RandRange(18f, 40f);
                Vector3 p = s.Pos + s.Right * side * (s.HalfWidth + r * 0.8f + RandRange(15f, 50f));
                p.y = GroundY - r * 0.35f;
                if (!map.IsClear(p, r * 0.9f)) return;
                float h = RandRange(0.7f, 1.2f);
                if (Any(mountainModels))
                {
                    // Модель горы уже со снегом: стоит на земле, только поворот по вертикали.
                    float k = RandRange(0.8f, 1.2f);
                    Model(mountainModels, new Vector3(p.x, GroundY - r * 0.15f, p.z), Quaternion.Euler(0f, RandRange(0f, 360f), 0f),
                          new Vector3(r * 2f, r * 1.6f * h, r * 2f * k), Decor.Far)
                        .transform.position += Vector3.up * (r * 1.6f * h * 0.5f);
                    return;
                }
                Piece("Гора", sphere, p, Quaternion.Euler(0f, RandRange(0f, 360f), 0f), new Vector3(r * 2f, r * 2f * h, r * 2f * RandRange(0.8f, 1.2f)),
                      new Color(0.5f, 0.47f, 0.44f), Col.None, Decor.Far);
                if (r > 25f)
                    Piece("Снег", sphere, p + Vector3.up * (r * h * 0.82f), Quaternion.identity, new Vector3(r * 0.7f, r * 0.45f, r * 0.7f), new Color(0.95f, 0.96f, 0.98f), Col.None, Decor.Far);
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

        void Pine(Vector3 at)
        {
            float h = RandRange(4f, 7f);
            if (Any(pineModels))
            {
                Hidden(Piece("Ствол", cylinder, at + Vector3.up * (h * 0.3f), Quaternion.identity, new Vector3(0.4f, h * 0.3f, 0.4f), Color.white, Col.Capsule, Decor.Tree));
                Model(pineModels, at, Quaternion.Euler(0f, RandRange(0f, 360f), 0f), Vector3.one * h, Decor.Tree);
                return;
            }
            Piece("Ствол", cylinder, at + Vector3.up * (h * 0.3f), Quaternion.identity, new Vector3(0.4f, h * 0.3f, 0.4f), new Color(0.35f, 0.24f, 0.15f), Col.Capsule, Decor.Tree);
            Color needles = new Color(0.16f, 0.32f, 0.2f);
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
                Model(rockModels, new Vector3(at.x, ground + height * 0.5f - r * 0.05f, at.z), Quaternion.Euler(0f, rot.eulerAngles.y, 0f),
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
        void Gate(Vector3 at, float yawDeg, float span, Color color)
        {
            var rot = Quaternion.Euler(0f, yawDeg, 0f);
            Vector3 r = rot * Vector3.right;
            for (int k = -1; k <= 1; k += 2)
                Piece("Столб", cube, at + r * k * (span / 2f + 0.6f) + Vector3.up * 2.5f, rot, new Vector3(0.6f, 5f, 0.6f), color, Col.Box);
            Piece("Перекладина", cube, at + Vector3.up * 5.2f, rot, new Vector3(span + 1.8f, 0.8f, 0.5f), color, Col.None);
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
        GameObject Model(GameObject[] set, Vector3 position, Quaternion rotation, Vector3 scale, Decor decor)
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
            return go;
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
            if (smallLayer >= 0) d[smallLayer] = SmallDecorDistance;
            if (treeLayer >= 0) d[treeLayer] = TreeDistance;
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

        GUIStyle style;

        void OnGUI()
        {
            if (!Generated || InGameMenu.IsOpen) return;
            if (RaceTimer.Instance != null && RaceTimer.Instance.Elapsed >= 0f) return;   // показываем, пока не стартовали
            style ??= new GUIStyle(GUI.skin.label) { fontSize = 22 };
            GUI.Label(new Rect(20, 90, 1200, 34), Summary, style);
        }
    }
}
