using System.Collections.Generic;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace JunkRace.EditorTools
{
    /// <summary>
    /// Меню: JunkRace → Собрать всё (меню + трасса).
    /// Создаёт сцену главного меню с NetworkManager и тестовую трассу
    /// (свалка → трамплин → бассейн → финиш) с игроком и кучей деталей из примитивов.
    /// Обе сцены прописываются в Build Settings, меню открывается — жми Play.
    /// </summary>
    public static class TestSceneBuilder
    {
        const string MenuPath = "Assets/Scenes/" + GameSession.MenuScene + ".unity";
        const string TrackPath = "Assets/Scenes/" + GameSession.TrackScene + ".unity";

        [MenuItem("JunkRace/Собрать всё (меню + трасса)")]
        public static void BuildAll()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            BuildTrackScene();
            BuildMenuScene();
            EditorBuildSettings.scenes = new[]
            {
                new EditorBuildSettingsScene(MenuPath, true),
                new EditorBuildSettingsScene(TrackPath, true),
            };
            EditorSceneManager.OpenScene(MenuPath);
            Debug.Log("[JunkRace] Готово: меню и трасса собраны и добавлены в Build Settings. Жми Play.");
        }

        [MenuItem("JunkRace/Пересобрать только трассу")]
        public static void BuildTrackOnly()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            BuildTrackScene();
        }

        /// <summary>Старая тестовая трасса (три полосы, река, мост) — вместо генерации, для проверки машин.</summary>
        [MenuItem("JunkRace/Пересобрать трассу: старая тестовая (река, мост)")]
        public static void BuildOldTestTrack()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            useOldTestTrack = true;
            try { BuildTrackScene(); }
            finally { useOldTestTrack = false; }
        }

        static bool useOldTestTrack;

        static void BuildTrackScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            Random.InitState(42);
            LookBuilder.ResetMaterialCache();

            if (useOldTestTrack) BuildTrack();
            else BuildYardAndRoute();
            var player = BuildPlayer(new Vector3(0f, 1.2f, -32f));
            SetupCamera(player);
            new GameObject("RaceTimer").AddComponent<RaceTimer>();
            new GameObject("InGameMenu").AddComponent<InGameMenu>();
            new GameObject("JunkNet").AddComponent<JunkNet>();   // в соло сам выключится
            BuildJunkyard();
            BuildYardProps();
            PartDefinitions.BindAll();   // характеристики деталей — в ассеты Data/Parts (существующие главнее)
            LookBuilder.ApplyEnvironment(Camera.main);
            AtmosphereBuilder.Apply(Camera.main);   // дымка, небо, холмы-задник (в духе Over the Hill)

            EditorSceneManager.SaveScene(scene, TrackPath);
        }

        static void BuildMenuScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

            var cam = Camera.main;
            if (cam != null)
            {
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.12f, 0.11f, 0.10f);
            }

            var netGo = new GameObject("NetworkManager");
            var nm = netGo.AddComponent<NetworkManager>();
            var transport = netGo.AddComponent<UnityTransport>();
            if (nm.NetworkConfig == null) nm.NetworkConfig = new NetworkConfig();
            nm.NetworkConfig.NetworkTransport = transport;
            nm.NetworkConfig.EnableSceneManagement = true;
            nm.NetworkConfig.ConnectionApproval = false;   // доступ решает сессия Unity Multiplayer Services
            netGo.AddComponent<CoopSession>();
            EditorUtility.SetDirty(nm);

            new GameObject("MainMenu").AddComponent<MainMenu>();

            EditorSceneManager.SaveScene(scene, MenuPath);
        }

        // ---------------- Трасса ----------------

        // Трасса для теста машин. Ширина 42 м, три полосы по 14 м; поперёк — река 10 м.
        //   слева  — подъём на полку и трамплин строго через реку на посадочный спуск;
        //   центр  — просто река: спрыгнул с берега, выплыл по выезду;
        //   справа — горбатый мост с перилами.
        // По краям — красно-белые ограждения. Ось Z — вдоль трассы, вперёд к финишу.
        const float TrackWidth = YardShuffle.YardHalf * 2f, LaneWidth = 14f;   // ширина свалки
        const float RiverStart = 60f, RiverEnd = 70f;
        const float TrackStart = -YardShuffle.YardDepth, TrackEnd = 145f;

        static void BuildTrack()
        {
            var track = new GameObject("Track").transform;
            // Границы трассы: по ним R возвращает вылетевшую машину внутрь ограждений.
            var bounds = track.gameObject.AddComponent<TrackBounds>();
            Set(bounds, "minX", -TrackWidth / 2f);
            Set(bounds, "maxX", TrackWidth / 2f);
            Set(bounds, "minZ", TrackStart);
            Set(bounds, "maxZ", TrackEnd);
            Color ground = new Color(0.45f, 0.42f, 0.38f);
            Color wood = new Color(0.6f, 0.4f, 0.2f);
            Color concrete = new Color(0.62f, 0.62f, 0.6f);
            float half = TrackWidth / 2f;
            float left = -LaneWidth, right = LaneWidth;   // центры полос

            // Земля — толстые плиты: их торцы и есть берега реки (под берег не заплыть).
            Slab("Земля_Старт", track, TrackStart, RiverStart, ground);
            Slab("Земля_Финиш", track, RiverEnd, TrackEnd, ground);

            // Река
            float riverMid = (RiverStart + RiverEnd) / 2f, riverLen = RiverEnd - RiverStart;
            Box("Дно_реки", track, new Vector3(0f, -3.5f, riverMid), new Vector3(TrackWidth, 1f, riverLen), ground * 0.7f);
            var water = Box("Вода", track, new Vector3(0f, -1.7f, riverMid), new Vector3(TrackWidth, 2.6f, riverLen), new Color(0.2f, 0.45f, 0.9f));
            water.AddComponent<WaterZone>();   // поверхность — верх коробки, y = -0.4

            // Разметка полос на старте
            for (int s = -1; s <= 1; s += 2)
                Prim(PrimitiveType.Cube, "Разметка", track, new Vector3(s * LaneWidth / 2f, 0.01f, 30f), new Vector3(0.3f, 0.02f, 30f), Color.white, false);

            // ---- Слева: подъём → полка → трамплин → прыжок над водой → пирс → посадочный спуск ----
            // Расчёт: наши машины едут ~40 км/ч. Кромка 2.3 м под 15°, над водой 6 м до пирса на 1.2 м —
            // долетает с ~31 км/ч на кромке. Не долетел — в реку, выплывай к выезду в центре.
            const float shelf = 1.5f, lip = 2.3f, pierTop = 1.2f;
            const float kickStart = RiverStart - 3f, pierStart = RiverStart + 6f;
            Ramp("Подъём", track, left, LaneWidth, new Vector2(20f, 0f), new Vector2(42f, shelf), 4f, wood);
            Box("Полка", track, new Vector3(left, (shelf - 4f) / 2f, (42f + kickStart) / 2f), new Vector3(LaneWidth, shelf + 4f, kickStart - 42f), wood);
            Ramp("Трамплин", track, left, LaneWidth, new Vector2(kickStart, shelf), new Vector2(RiverStart - 0.2f, lip), lip + 1f, wood * 1.1f);
            // Пирс стоит в реке на дне: верх 1.2 м, от z = 66 до берега.
            Box("Пирс", track, new Vector3(left, (pierTop - 3f) / 2f, (pierStart + RiverEnd) / 2f), new Vector3(LaneWidth, pierTop + 3f, RiverEnd - pierStart), wood);
            Ramp("Посадка", track, left, LaneWidth, new Vector2(RiverEnd, pierTop), new Vector2(RiverEnd + 12f, 0f), 2f, wood);
            for (int s = -1; s <= 1; s += 2)
            {
                float x = left + s * (LaneWidth / 2f - 0.15f);
                Railing(track, "Борт_подъёма", x, new Vector2(20f, 0f), new Vector2(42f, shelf));
                Railing(track, "Борт_полки", x, new Vector2(42f, shelf), new Vector2(kickStart, shelf));
                Railing(track, "Борт_трамплина", x, new Vector2(kickStart, shelf), new Vector2(RiverStart - 0.2f, lip));
                Railing(track, "Борт_пирса", x, new Vector2(pierStart, pierTop), new Vector2(RiverEnd, pierTop));
                Railing(track, "Борт_посадки", x, new Vector2(RiverEnd, pierTop), new Vector2(RiverEnd + 12f, 0f));
            }

            // ---- Центр: выезд из реки ----
            Ramp("Выезд_из_реки", track, 0f, LaneWidth, new Vector2(RiverStart + 4f, -1.8f), new Vector2(RiverEnd + 0.3f, 0f), 0.5f, ground);

            // ---- Справа: горбатый мост ----
            const float deckWidth = 8f, crest = 1.2f;
            float bridgeA = RiverStart - 1.5f, bridgeB = RiverEnd + 1.5f, crestZ = (bridgeA + bridgeB) / 2f;
            Ramp("Мост_въезд", track, right, deckWidth, new Vector2(bridgeA, 0f), new Vector2(crestZ, crest), 0.3f, concrete);
            Ramp("Мост_съезд", track, right, deckWidth, new Vector2(crestZ, crest), new Vector2(bridgeB, 0f), 0.3f, concrete);
            Box("Мост_опора", track, new Vector3(right, -1.1f, crestZ), new Vector3(deckWidth - 2f, 3.8f, 0.8f), concrete * 0.85f);
            for (int s = -1; s <= 1; s += 2)
            {
                float x = right + s * (deckWidth / 2f - 0.15f);
                Railing(track, "Перила_моста", x, new Vector2(bridgeA, 0f), new Vector2(crestZ, crest));
                Railing(track, "Перила_моста", x, new Vector2(crestZ, crest), new Vector2(bridgeB, 0f));
            }

            // ---- Ограждения по краям и в торцах (до дна реки, чтобы и из воды не выплыть за край) ----
            const float segment = 5f;
            for (int s = -1; s <= 1; s += 2)
            {
                int i = 0;
                for (float z = TrackStart; z < TrackEnd; z += segment, i++)
                {
                    float len = Mathf.Min(segment, TrackEnd - z);
                    Box("Ограждение", track, new Vector3(s * (half + 0.15f), -1.35f, z + len / 2f), new Vector3(0.3f, 4.3f, len),
                        i % 2 == 0 ? new Color(0.85f, 0.15f, 0.1f) : Color.white);
                }
            }
            Box("Ограждение_старт", track, new Vector3(0f, 0.4f, TrackStart - 0.15f), new Vector3(TrackWidth + 0.6f, 0.8f, 0.3f), new Color(0.85f, 0.15f, 0.1f));
            Box("Ограждение_конец", track, new Vector3(0f, 0.4f, TrackEnd + 0.15f), new Vector3(TrackWidth + 0.6f, 0.8f, 0.3f), new Color(0.85f, 0.15f, 0.1f));

            // Финиш — триггер через всю ширину.
            var finish = Box("Финиш", track, new Vector3(0f, 3f, 135f), new Vector3(TrackWidth, 6f, 2f), new Color(0.2f, 0.9f, 0.3f));
            finish.AddComponent<FinishLine>();
        }

        /// <summary>
        /// Свалка (z от -40 до 0) с ограждением с трёх сторон; спереди открыт выезд,
        /// а дальше — трасса, которую при запуске строит RouteGenerator (каждый раз новую).
        /// </summary>
        static void BuildYardAndRoute()
        {
            var yard = new GameObject("Свалка_площадка").transform;
            var yardGround = Slab("Земля_свалки", yard, TrackStart, 0f, new Color(0.45f, 0.42f, 0.38f));
            DressYardGround(yardGround);
            float half = TrackWidth / 2f;
            var fenceModel = ModelFitter.FindAll(RouteModelsFolder + "/Yard").Find(m => m.name == "yard_fence");
            if (fenceModel != null)
            {
                // Забор из модели: коллайдеры — плиты вдоль сторон, вид — секции по ~4 м.
                YardFence(yard, fenceModel, new Vector3(-half - 0.15f, 0f, TrackStart), new Vector3(-half - 0.15f, 0f, 0f));
                YardFence(yard, fenceModel, new Vector3(half + 0.15f, 0f, 0f), new Vector3(half + 0.15f, 0f, TrackStart));
                YardFence(yard, fenceModel, new Vector3(half + 0.15f, 0f, TrackStart - 0.15f), new Vector3(-half - 0.15f, 0f, TrackStart - 0.15f));
                // Сужение к выезду: трасса начинается шириной 14 м.
                YardFence(yard, fenceModel, new Vector3(-half - 0.15f, 0f, -0.15f), new Vector3(-7f, 0f, -0.15f));
                YardFence(yard, fenceModel, new Vector3(7f, 0f, -0.15f), new Vector3(half + 0.15f, 0f, -0.15f));
            }
            else
            {
                const float segment = 5f;
                for (int s = -1; s <= 1; s += 2)
                {
                    int i = 0;
                    for (float z = TrackStart; z < 0f; z += segment, i++)
                        Box("Ограждение", yard, new Vector3(s * (half + 0.15f), 0.4f, z + segment / 2f), new Vector3(0.3f, 0.8f, segment),
                            i % 2 == 0 ? new Color(0.85f, 0.15f, 0.1f) : Color.white);
                }
                Box("Ограждение_зад", yard, new Vector3(0f, 0.4f, TrackStart - 0.15f), new Vector3(TrackWidth + 0.6f, 0.8f, 0.3f), new Color(0.85f, 0.15f, 0.1f));
                for (int s = -1; s <= 1; s += 2)
                    Box("Ограждение_выезд", yard, new Vector3(s * (7f + (half - 7f) / 2f), 0.4f, -0.15f), new Vector3(half - 7f, 0.8f, 0.3f), new Color(0.85f, 0.15f, 0.1f));
            }

            var gen = new GameObject("Генератор трассы").AddComponent<RouteGenerator>();
            gen.transform.position = Vector3.zero;
            Set(gen, "template", LookBuilder.MakeMaterial(Color.white));
            BuildRouteModels(gen);
            BuildRouteTextures(gen);
        }

        const string RouteModelsFolder = "Assets/Prefab/Route";

        /// <summary>
        /// Образцы моделей для трассы из Assets/Prefab/Route/{Tree,Pine,Rock,Mountain,Log}: подогнаны под единичный
        /// размер и спрятаны под генератором. При запуске генератор клонирует их вместо примитивов.
        /// Новую модель достаточно положить в нужную папку и пересобрать сцену.
        /// </summary>
        static void BuildRouteModels(RouteGenerator gen)
        {
            var box = new GameObject("Образцы моделей").transform;
            box.SetParent(gen.transform, false);
            // Деревья: высота 1, низ на нуле (пропорции свои). Камни и бревно: вписаны в единичный куб.
            SetObjects(gen, "treeModels", RouteTemplates(box, "Tree", new Vector3(0f, 0.5f, 0f), new Vector3(100f, 1f, 100f), true));
            SetObjects(gen, "pineModels", RouteTemplates(box, "Pine", new Vector3(0f, 0.5f, 0f), new Vector3(100f, 1f, 100f), true));
            // Ёлки в снегу: те же модели, текстура хвои заменена на *_snow.png рядом с исходной.
            // Снежный биом: свои модели из Snow/ (ёлки tree_*, если есть, иначе — Pine с заснеженной текстурой).
            var snowTrees = RouteTemplates(box, "Snow", new Vector3(0f, 0.5f, 0f), new Vector3(100f, 1f, 100f), true, "tree_snow_a", "tree_snow_b");
            SetObjects(gen, "snowPineModels", snowTrees.Count > 0 ? snowTrees
                : SnowVariants(RouteTemplates(box, "Pine", new Vector3(0f, 0.5f, 0f), new Vector3(100f, 1f, 100f), true)));
            // Пустыня: те же деревья и ёлки, но с пустынной палитрой — зелень пыльно-рыжая.
            var desertTrees = RouteTemplates(box, "Tree", new Vector3(0f, 0.5f, 0f), new Vector3(100f, 1f, 100f), true);
            desertTrees.AddRange(RouteTemplates(box, "Pine", new Vector3(0f, 0.5f, 0f), new Vector3(100f, 1f, 100f), true));
            SetObjects(gen, "desertTreeModels", DesertVariants(desertTrees));
            SetObjects(gen, "snowMountainModels", RouteTemplates(box, "Snow", Vector3.zero, Vector3.one, false, "mountain_rocks_large", "mountain_rocks_medium"));
            SetObjects(gen, "snowStoneModels", RouteTemplates(box, "Snow", new Vector3(0f, 0.5f, 0f), new Vector3(100f, 1f, 100f), true, "stone_rocks_small"));
            SetObjects(gen, "driftModels", RouteTemplates(box, "Snow", new Vector3(0f, 0.5f, 0f), new Vector3(100f, 1f, 100f), true, "drift_pile", "drift_bunker"));
            SetObjects(gen, "snowmanModels", RouteTemplates(box, "Snow", new Vector3(0f, 0.5f, 0f), new Vector3(100f, 1f, 100f), true, "snowman"));
            SetObjects(gen, "rockModels", RouteTemplates(box, "Rock", Vector3.zero, Vector3.one, false));
            // Горы на горизонте (со снежными шапками): вписаны в единичный куб, как и сферы-заглушки.
            SetObjects(gen, "mountainModels", RouteTemplates(box, "Mountain", Vector3.zero, Vector3.one, false));
            // Бревно — длинной стороной по X (поперёк дороги).
            SetObjects(gen, "logModels", RouteTemplates(box, "Log", Vector3.zero, new Vector3(1f, 0.999f, 0.999f), false));

            // Мелочь у дороги: высота 1, низ на нуле, свои пропорции — генератор масштабирует по высоте.
            Vector3 up = new Vector3(0f, 0.5f, 0f), tall = new Vector3(100f, 1f, 100f);
            SetObjects(gen, "bushModels", RouteTemplates(box, "bush", up, tall, true));
            SetObjects(gen, "stoneModels", RouteTemplates(box, "Stone", up, tall, true));
            SetObjects(gen, "stumpModels", RouteTemplates(box, "Other", up, tall, true, "stump", "deadwood"));
            SetObjects(gen, "postModels", RouteTemplates(box, "Other", up, tall, true, "post"));
            SetObjects(gen, "blockModels", RouteTemplates(box, "Other", up, tall, true, "beton", "barrier", "signpost"));
            SetObjects(gen, "junkModels", RouteTemplates(box, "Other", up, tall, true, "barrel", "tire", "bag"));
            // Забор: вписан в коробку 1×1×0.25, длинной стороной вдоль X — генератор растягивает X на пролёт.
            SetObjects(gen, "fenceModels", FenceTemplates(box, "fence"));
            // Арка старта/финиша и шлагбаум: вписаны в 1×1×0.5 (длинной стороной по X), низ на нуле.
            SetObjects(gen, "archModels", RouteTemplates(box, "Gate", up, new Vector3(1f, 1f, 0.5f), false, "finish"));
            SetObjects(gen, "boomModels", RouteTemplates(box, "Gate", up, new Vector3(1f, 1f, 0.5f), false, "boom"));
        }

        const float FenceHeight = 2.4f, FenceStep = 4f;

        /// <summary>
        /// Забор свалки от a до b: невидимая плита-коллайдер во всю длину и секции модели по ~4 м
        /// (целое число, растянуты ровно на длину). Лицом наружу не важно — модель с двух сторон одинаковая.
        /// </summary>
        static void YardFence(Transform parent, GameObject model, Vector3 a, Vector3 b)
        {
            Vector3 dir = b - a;
            float len = dir.magnitude;
            if (len < 0.5f) return;
            dir /= len;
            var rot = Quaternion.LookRotation(Vector3.Cross(dir, Vector3.up), Vector3.up);   // X модели — вдоль забора
            var wall = Box("Забор_коллайдер", parent, (a + b) / 2f + Vector3.up * FenceHeight / 2f, Vector3.one, Color.white);
            wall.transform.rotation = rot;
            wall.transform.localScale = new Vector3(len, FenceHeight, 0.3f);
            Object.DestroyImmediate(wall.GetComponent<MeshRenderer>());
            Object.DestroyImmediate(wall.GetComponent<MeshFilter>());
            wall.isStatic = true;

            int n = Mathf.Max(1, Mathf.RoundToInt(len / FenceStep));
            float seg = len / n;
            for (int i = 0; i < n; i++)
            {
                var t = new GameObject("Забор").transform;
                t.SetParent(parent, false);
                t.SetPositionAndRotation(a + dir * (seg * (i + 0.5f)), rot);
                // Чуть внахлёст — без щелей между секциями.
                ModelFitter.Attach(model, t, new Vector3(0f, FenceHeight / 2f, 0f), new Vector3(seg * 1.04f, FenceHeight, 0.35f), false);
                foreach (var tr in t.GetComponentsInChildren<Transform>(true)) tr.gameObject.isStatic = true;
            }
        }

        /// <summary>
        /// Текстуры дороги, откосов и травы из Assets/Prefab/Route/Textures (forest_road.png и т.д.).
        /// Нет файла — генератор красит однотонным цветом, как раньше.
        /// </summary>
        static void BuildRouteTextures(RouteGenerator gen)
        {
            var so = new SerializedObject(gen);
            void Use(string field, string file)
            {
                string path = RouteModelsFolder + "/Textures/" + file + ".png";
                if (AssetImporter.GetAtPath(path) is TextureImporter ti && (ti.wrapMode != TextureWrapMode.Repeat || ti.anisoLevel < 4))
                {
                    ti.wrapMode = TextureWrapMode.Repeat;
                    ti.maxTextureSize = 1024;
                    ti.anisoLevel = 4;   // дорога под острым углом — без размытия вдали
                    ti.SaveAndReimport();
                }
                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                Material m = null;
                if (tex != null)
                {
                    var src = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = file };
                    src.SetTexture("_BaseMap", tex);
                    m = LookBuilder.ConvertMaterial(src);
                    m.enableInstancing = true;
                }
                so.FindProperty(field).objectReferenceValue = m;
            }
            Use("roadForest", "forest_road");
            Use("sideForest", "forest_side");
            Use("roadMountain", "mountain_road");
            Use("sideMountain", "mountain_side");
            Use("groundGrass", "grass");
            Use("roadSnow", "snow_road");
            Use("sideSnow", "snow_side");
            Use("roadDesert", "desert_road");
            Use("sideDesert", "desert_side");
            Use("groundSnow", "snow_ground");

            Use("groundMountain", "mountain_ground");
            Use("groundSand", "desert_ground");   // пологие песчаные берега реки
            // Переходы: transition_road_<верх>_<низ>.png и transition_ground_<верх>_<низ>.png.
            // Биомы, которых в игре ещё нет (пустыня), пропускаем.
            Transitions(so, "roadTransitions", "transition_road_", TextureWrapMode.Clamp);
            Transitions(so, "groundTransitions", "transition_ground_", TextureWrapMode.Repeat);   // поперёк карты повторяется
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>Картинки-переходы между биомами → массив (From, To, Material) у генератора.</summary>
        static void Transitions(SerializedObject so, string field, string prefix, TextureWrapMode wrapU)
        {
            var trans = so.FindProperty(field);
            trans.arraySize = 0;
            foreach (var guid in AssetDatabase.FindAssets(prefix + " t:Texture2D", new[] { RouteModelsFolder + "/Textures" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                string file = System.IO.Path.GetFileNameWithoutExtension(path);
                if (!file.StartsWith(prefix)) continue;
                var parts = file.Substring(prefix.Length).Split('_');
                if (parts.Length != 2 || !TryBiome(parts[0], out var from) || !TryBiome(parts[1], out var to)) continue;
                if (AssetImporter.GetAtPath(path) is TextureImporter ti && (ti.wrapModeU != wrapU || ti.wrapModeV != TextureWrapMode.Clamp || ti.anisoLevel < 4))
                {
                    // По длине картинка кладётся один раз: края не подмешивают противоположный биом.
                    ti.wrapModeU = wrapU;
                    ti.wrapModeV = TextureWrapMode.Clamp;
                    ti.maxTextureSize = 1024;
                    ti.anisoLevel = 4;
                    ti.SaveAndReimport();
                }
                var src = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = System.IO.Path.GetFileNameWithoutExtension(path) };
                src.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>(path));
                var m = LookBuilder.ConvertMaterial(src);
                m.enableInstancing = true;
                int i = trans.arraySize++;
                var e = trans.GetArrayElementAtIndex(i);
                e.FindPropertyRelative("From").enumValueIndex = (int)from;
                e.FindPropertyRelative("To").enumValueIndex = (int)to;
                e.FindPropertyRelative("Material").objectReferenceValue = m;
            }
        }

        /// <summary>Пустынные копии образцов: материал палитры JunkPalette заменён на JunkPalette_Desert (зелёный → рыжий).</summary>
        static List<GameObject> DesertVariants(List<GameObject> templates)
        {
            // Любой материал, у которого текстура — JunkPalette (в т.ч. toon), получает копию с пустынной палитрой.
            var desertTex = JunkRace.EditorTools.FlatGroundTextures.MakeDesertPalette().GetTexture("_BaseMap");
            var palette = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/JunkRace/Art/Palette/JunkPalette.png");
            var swapped = new Dictionary<Material, Material>();
            foreach (var t in templates)
            {
                t.name += "_desert";
                foreach (var r in t.GetComponentsInChildren<Renderer>(true))
                {
                    var mats = r.sharedMaterials;
                    for (int i = 0; i < mats.Length; i++)
                    {
                        var m = mats[i];
                        if (m == null) continue;
                        if (!swapped.TryGetValue(m, out var d))
                        {
                            d = m;
                            foreach (var prop in new[] { "_BaseMap", "_MainTex" })
                                if (m.HasProperty(prop) && m.GetTexture(prop) == palette)
                                {
                                    if (d == m) d = new Material(m) { name = m.name + " desert" };
                                    d.SetTexture(prop, desertTex);
                                }
                            swapped[m] = d;
                        }
                        mats[i] = d;
                    }
                    r.sharedMaterials = mats;
                }
            }
            return templates;
        }

        /// <summary>
        /// Снежные копии образцов: у каждого материала с текстурой X.png, рядом с которой лежит X_snow.png,
        /// текстура заменяется на снежную. Образцы без снежных текстур остаются как есть.
        /// </summary>
        static List<GameObject> SnowVariants(List<GameObject> templates)
        {
            var swapped = new Dictionary<Material, Material>();
            foreach (var t in templates)
            {
                t.name += "_snow";
                foreach (var r in t.GetComponentsInChildren<Renderer>(true))
                {
                    var mats = r.sharedMaterials;
                    for (int i = 0; i < mats.Length; i++)
                    {
                        var m = mats[i];
                        if (m == null || !m.HasProperty("_BaseMap")) continue;
                        if (!swapped.TryGetValue(m, out var snow))
                        {
                            snow = m;
                            string path = AssetDatabase.GetAssetPath(m.GetTexture("_BaseMap"));
                            if (!string.IsNullOrEmpty(path))
                            {
                                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(System.IO.Path.ChangeExtension(path, null) + "_snow.png");
                                if (tex != null)
                                {
                                    snow = new Material(m) { name = m.name + " snow" };
                                    snow.SetTexture("_BaseMap", tex);
                                }
                            }
                            swapped[m] = snow;
                        }
                        mats[i] = snow;
                    }
                    r.sharedMaterials = mats;
                }
            }
            return templates;
        }

        /// <summary>Имя биома в файле: forest, mountains (mountain), snow.</summary>
        static bool TryBiome(string name, out Biome biome)
        {
            if (name == "mountain") name = "mountains";
            return System.Enum.TryParse(name, true, out biome) && System.Enum.IsDefined(typeof(Biome), biome);
        }

        static List<GameObject> FenceTemplates(Transform box, params string[] files)
        {
            var result = new List<GameObject>();
            foreach (var model in ModelFitter.FindAll(RouteModelsFolder + "/Other"))
            {
                if (System.Array.IndexOf(files, model.name) < 0) continue;
                if (AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(model)) is ModelImporter mi && !mi.isReadable)
                {
                    mi.isReadable = true;   // для статического объединения мешей
                    mi.SaveAndReimport();
                }
                var t = new GameObject("Other_" + model.name);
                t.transform.SetParent(box, false);
                // Сначала по своим пропорциям — узнать, вдоль какой оси модель длинная.
                var probe = ModelFitter.Attach(model, t.transform, Vector3.zero, Vector3.one, true);
                var b = ModelFitter.VertexBounds(t, t.transform);
                Object.DestroyImmediate(probe);
                Quaternion? turn = b.size.z > b.size.x ? Quaternion.Euler(0f, 90f, 0f) : (Quaternion?)null;
                ModelFitter.Attach(model, t.transform, new Vector3(0f, 0.5f, 0f), new Vector3(1f, 1f, 0.25f), false, turn);
                t.SetActive(false);
                result.Add(t);
            }
            return result;
        }

        /// <param name="only">Только эти файлы из папки (без расширения); пусто — все.</param>
        static List<GameObject> RouteTemplates(Transform box, string folder, Vector3 center, Vector3 size, bool keepProportions, params string[] only)
        {
            var result = new List<GameObject>();
            foreach (var model in ModelFitter.FindAll(RouteModelsFolder + "/" + folder))
            {
                if (only.Length > 0 && System.Array.IndexOf(only, model.name) < 0) continue;
                // Статическое объединение мешей при запуске требует, чтобы меш был доступен для чтения.
                string path = AssetDatabase.GetAssetPath(model);
                if (AssetImporter.GetAtPath(path) is ModelImporter mi && !mi.isReadable)
                {
                    mi.isReadable = true;
                    mi.SaveAndReimport();
                }
                var t = new GameObject(folder + "_" + model.name);
                t.transform.SetParent(box, false);
                ModelFitter.Attach(model, t.transform, center, size, keepProportions);
                t.SetActive(false);
                result.Add(t);
            }
            return result;
        }

        static void SetObjects(Object target, string field, List<GameObject> items)
        {
            var so = new SerializedObject(target);
            var prop = so.FindProperty(field);
            prop.arraySize = items.Count;
            for (int i = 0; i < items.Count; i++) prop.GetArrayElementAtIndex(i).objectReferenceValue = items[i];
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>Плита земли от z0 до z1 толщиной 4 м (верх на y = 0).</summary>
        /// <summary>
        /// Земля свалки — текстура Assets/Prefab/Route/Textures/junky.png (утрамбованная земля с гравием и пятнами),
        /// повтор каждые YardTile метров. Нет картинки — остаётся серой.
        /// </summary>
        static void DressYardGround(GameObject ground)
        {
            const float YardTile = 8f;
            string path = RouteModelsFolder + "/Textures/junky.png";
            if (AssetImporter.GetAtPath(path) is TextureImporter ti && (ti.wrapMode != TextureWrapMode.Repeat || ti.anisoLevel < 4 || ti.maxTextureSize > 1024))
            {
                ti.wrapMode = TextureWrapMode.Repeat;
                ti.maxTextureSize = 1024;
                ti.anisoLevel = 4;   // землю видно под острым углом — без размытия вдали
                ti.SaveAndReimport();
            }
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (tex == null) return;
            var src = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = "junky_ground" };
            src.SetTexture("_BaseMap", tex);
            var m = LookBuilder.ConvertMaterial(src);
            var size = ground.transform.localScale;
            var tiling = new Vector2(size.x / YardTile, size.z / YardTile);
            if (m.HasProperty("_BaseMap")) m.SetTextureScale("_BaseMap", tiling);
            else m.mainTextureScale = tiling;
            ground.GetComponent<Renderer>().sharedMaterial = m;
        }

        static GameObject Slab(string name, Transform parent, float z0, float z1, Color color) =>
            Box(name, parent, new Vector3(0f, -2f, (z0 + z1) / 2f), new Vector3(TrackWidth, 4f, z1 - z0), color);

        /// <summary>
        /// Наклонная плита: верхняя поверхность идёт от (z, y) = from до to на полосе с центром x.
        /// Толщина отсчитывается вниз от поверхности — толстая плита сама служит сплошной насыпью.
        /// </summary>
        static GameObject Ramp(string name, Transform parent, float x, float width, Vector2 from, Vector2 to, float thickness, Color color)
        {
            Vector3 a = new Vector3(x, from.y, from.x), b = new Vector3(x, to.y, to.x);
            Vector3 dir = b - a;
            Quaternion rot = Quaternion.LookRotation(dir.normalized, Vector3.up);
            Vector3 center = (a + b) / 2f - rot * Vector3.up * (thickness / 2f);
            var go = Box(name, parent, center, new Vector3(width, thickness, dir.magnitude), color);
            go.transform.rotation = rot;
            return go;
        }

        /// <summary>Борт высотой 0.8 м, идущий по поверхности от from до to.</summary>
        static void Railing(Transform parent, string name, float x, Vector2 from, Vector2 to)
        {
            const float h = 0.8f;
            Ramp(name, parent, x, 0.3f, from + new Vector2(0f, h), to + new Vector2(0f, h), h + 0.05f, new Color(0.9f, 0.55f, 0.1f));
        }

        // ---------------- Игрок и камера ----------------

        static PlayerController BuildPlayer(Vector3 pos)
        {
            // Игрок 1.7 м и 0.6 м в ширину — чтобы в корпус влезали четверо.
            // Корень не масштабируем (CharacterController масштабируется вместе с ним), уменьшаем только вид.
            var go = new GameObject("Player");
            go.transform.position = pos;
            Prim(PrimitiveType.Capsule, "Тело", go.transform, Vector3.zero, new Vector3(0.6f, 0.85f, 0.6f), new Color(1f, 0.55f, 0.1f), false);

            var cc = go.AddComponent<CharacterController>();
            cc.height = 1.7f;
            cc.radius = 0.3f;

            Prim(PrimitiveType.Cube, "Нос", go.transform, new Vector3(0f, 0.45f, 0.3f), new Vector3(0.22f, 0.15f, 0.22f), Color.black, false);

            var hold = new GameObject("HoldPoint").transform;
            hold.SetParent(go.transform, false);
            hold.localPosition = new Vector3(0f, 0.3f, 1.5f);

            var pc = go.AddComponent<PlayerController>();
            var carry = go.AddComponent<PlayerCarry>();
            go.AddComponent<PartCard>();
            Set(carry, "holdPoint", hold);
            return pc;
        }

        static void SetupCamera(PlayerController player)
        {
            var cam = Camera.main;
            if (cam == null) cam = new GameObject("Main Camera") { tag = "MainCamera" }.AddComponent<Camera>();
            var follow = cam.gameObject.AddComponent<FollowCamera>();
            Set(follow, "player", player);
            Set(player, "cameraTransform", cam.transform);
        }

        // ---------------- Свалка ----------------

        static void BuildJunkyard()
        {
            var yard = new GameObject("Junkyard").transform;

            // Сколько чего на свалке при 1, 2, 3, 4 игроках. Сцена собирается на четверых,
            // при запуске лишнее выключается (PlayerScaled + YardShuffle). Один игрок: все корпуса,
            // все моторы, 10 колёс, 2 руля, 1 кресло. Каждый следующий — ещё на машину и запчасти.

            // Корпуса (8, 8, 10, 12)
            Scaled(yard, new[] { 1, 1, 2, 2 }, () => MakeBathtub(yard, RandomYardPos()), hull: true);
            Scaled(yard, new[] { 1, 1, 1, 2 }, () => MakePallet(yard, RandomYardPos()), hull: true);
            Scaled(yard, new[] { 1, 1, 2, 2 }, () => MakeTable(yard, RandomYardPos()), hull: true);
            Scaled(yard, new[] { 1, 1, 1, 1 }, () => MakeWardrobe(yard, RandomYardPos()), hull: true);
            Scaled(yard, new[] { 1, 1, 1, 1 }, () => MakeSofa(yard, RandomYardPos()), hull: true);
            Scaled(yard, new[] { 1, 1, 1, 2 }, () => MakeFlamingo(yard, RandomYardPos()), hull: true);
            Scaled(yard, new[] { 1, 1, 1, 1 }, () => MakeDoor(yard, RandomYardPos()), hull: true);
            Scaled(yard, new[] { 1, 1, 1, 1 }, () => MakeSurfboard(yard, RandomYardPos()), hull: true);

            // Колёса (12, 15, 21, 26)
            Color rubber = new Color(0.15f, 0.15f, 0.15f);
            Scaled(yard, new[] { 2, 3, 4, 5 }, () =>
                MakeWheel(yard, "Колесо квадроцикла", "Держит дорогу. Почти.", 11f, 0.45f, 900f, 750f, new Color(0.35f, 0.25f, 0.15f),
                          model: "wheel-tractor-dark-back", visualYaw: 180f));
            Scaled(yard, new[] { 2, 2, 3, 4 }, () =>
                MakeWheel(yard, "Колесо", "Обычное, от чего-то маленького. Скучное, зато катится и держит.", 7f, 0.38f, 650f, 600f, rubber, model: "wheel"));
            Scaled(yard, new[] { 2, 2, 3, 4 }, () =>
                MakeWheel(yard, "Люк", "Чугунный. Тяжёлый, цепкий и не отваливается никогда.", 40f, 0.35f, 650f, 900f,
                          new Color(0.3f, 0.3f, 0.32f), WheelStyle.Manhole, thickness: 0.04f, breakImpulse: 3000f, rolling: 5f, model: "luk_wheel"));
            Scaled(yard, new[] { 2, 3, 4, 4 }, () =>
                MakeWheel(yard, "Колесо от телеги", "Деревянное, большое и лёгкое. Катится легко, держит плохо, отлетает от чиха.", 2f, 0.6f, 400f, 250f,
                          rubber, WheelStyle.Bicycle, thickness: 0.025f, breakImpulse: 250f, rolling: 1.5f, model: "cart_wheel"));
            Scaled(yard, new[] { 2, 3, 4, 5 }, () =>
                MakeWheel(yard, "Спасательный круг", "В воде — лучшее колесо. На дороге мягкий и вязкий: нужен мотор помощнее.", 2f, 0.4f, 250f, 220f,
                          new Color(1f, 0.45f, 0.1f), WheelStyle.Ring, thickness: 0.12f, rolling: 8f, buoyancy: 1500f, paddle: 1.6f, model: "sos_wheel"));
            Scaled(yard, new[] { 2, 2, 3, 4 }, () =>
                MakeWheel(yard, "Иллюминатор", "Корабельный, латунный. Тяжёлый и крепкий, по воде гребёт — привык.", 18f, 0.35f, 750f, 700f,
                          new Color(0.78f, 0.6f, 0.25f), WheelStyle.Porthole, thickness: 0.06f, breakImpulse: 1800f, rolling: 4f, paddle: 0.9f, model: "porthole"));

            // Моторы и толкатели (5, 7, 9, 12)
            Scaled(yard, new[] { 1, 2, 2, 3 }, () =>
                MakeEngine(yard, "Мотор газонокосилки", "Классика. Заводится с третьего рывка.", 15f, 4000f, 450f, new Color(0.85f, 0.15f, 0.1f), new Vector3(0.6f, 0.5f, 0.6f), "lawn mower"));
            Scaled(yard, new[] { 1, 1, 2, 2 }, () =>
                MakeEngine(yard, "Мотор от стиралки", "Тяжёлый и тихий. На отжиме неплох.", 32f, 3000f, 800f, Color.white, new Vector3(0.8f, 0.8f, 0.8f), "washing machine"));
            Scaled(yard, new[] { 0, 1, 1, 2 }, () =>
                MakeEngine(yard, "Электромотор", "Тяговый, от погрузчика. Тяжёлый, но тянет ровно и мощно.", 26f, 5500f, 900f, new Color(0.15f, 0.4f, 0.4f), new Vector3(0.55f, 0.45f, 0.5f), "ElectricMotorAndCraddle"));
            Scaled(yard, new[] { 1, 2, 2, 3 }, () =>
                MakeEngine(yard, "Мотоциклетный двигатель", "Лёгкий мотор с кикстартером.", 7f, 7500f, 220f, Color.gray, new Vector3(0.41f, 0.46f, 0.51f), "Motorcycle_engine"));
            Scaled(yard, new[] { 1, 1, 2, 2 }, () => MakeFan(yard));
            Scaled(yard, new[] { 1, 1, 1, 2 }, () => MakeVacuum(yard));

            // Рули (2, 4, 5, 6) — первый (обычный) лежит у старта.
            Scaled(yard, new[] { 1, 2, 2, 3 }, () => MakeSteering(yard, SteerStyle.Wheel));
            Scaled(yard, new[] { 0, 1, 1, 1 }, () => MakeSteering(yard, SteerStyle.Laptop));

            // Борта (4, 7, 10, 14): защищают машину от разваливания, но тяжёлые.
            Scaled(yard, new[] { 2, 3, 4, 6 }, () => MakeArmor(yard, ArmorStyle.CarDoor));
            Scaled(yard, new[] { 1, 2, 3, 4 }, () => MakeArmor(yard, ArmorStyle.WindowFrame));

            // Мелочь
            Scaled(yard, new[] { 1, 1, 2, 2 }, () => MakeUmbrella(yard));
            Scaled(yard, new[] { 1, 1, 1, 2 }, () => MakeDuck(yard));

            // Кресла (1, 3, 5, 6) — первое (офисное) лежит у старта.
            Scaled(yard, new[] { 1, 2, 2, 3 }, () => MakeSeat(yard, SeatStyle.Office));
            Scaled(yard, new[] { 0, 1, 2, 2 }, () => MakeSeat(yard, SeatStyle.Car));
            Scaled(yard, new[] { 0, 0, 1, 1 }, () => MakeSeat(yard, SeatStyle.Toilet));

            DistributeIntoPiles(yard, BuildPiles());
        }

        /// <summary>
        /// Сделать counts[3] штук (на четверых); копия номер i есть с того числа игроков,
        /// при котором counts[игроки - 1] > i (PlayerScaled).
        /// </summary>
        /// <summary>Мусора на свалке больше в столько раз (корпуса — нет: их и так по числу игроков).</summary>
        const float JunkMultiplier = 3f;   // втрое больше исходного

        static void Scaled(Transform yard, int[] counts, System.Action make, bool hull = false)
        {
            if (!hull)
            {
                counts = (int[])counts.Clone();
                for (int k = 0; k < counts.Length; k++) counts[k] = Mathf.CeilToInt(counts[k] * JunkMultiplier);
            }
            for (int i = 0; i < counts[3]; i++)
            {
                int before = yard.childCount;
                make();
                int min = 1;
                while (min < 4 && counts[min - 1] <= i) min++;
                for (int c = before; c < yard.childCount; c++)
                {
                    var child = yard.GetChild(c);
                    var ps = child.GetComponent<PlayerScaled>() ?? child.gameObject.AddComponent<PlayerScaled>();
                    ps.MinPlayers = min;
                }
            }
        }

        // ---------------- Декорации свалки ----------------

        const string PropsFolder = "Assets/Prefab/other";

        /// <summary>Модели из Assets/Prefab/other и сколько штук каждой. Нет в списке — по одной.</summary>
        static readonly Dictionary<string, int> PropCounts = new Dictionary<string, int>
        {
            { "Block", 9 }, { "Reel", 9 }, { "TV02", 9 },          // мелочь — втрое больше исходного
            { "Car0", 5 }, { "Car1", 5 }, { "GasPump", 4 },
            { "Bus", 1 }, { "Tank", 1 }, { "Windmill", 1 },
        };

        /// <summary>
        /// Декорации на свалку: вид в нашем стиле, коробка-коллайдер по габаритам. Где стоят — решается
        /// при запуске (YardShuffle, по сиду): мелкие внутри, крупные (больше 3.5 м в радиусе или выше 5 м) — за забором.
        /// </summary>
        [MenuItem("JunkRace/Декорации свалки: поставить в открытой сцене")]
        public static void BuildYardProps()
        {
            var old = GameObject.Find("Декорации_свалки");
            if (old != null) Object.DestroyImmediate(old);
            if (!AssetDatabase.IsValidFolder(PropsFolder)) return;
            var root = new GameObject("Декорации_свалки").transform;
            int n = 0;
            foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { PropsFolder }))
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid));
                if (prefab == null) continue;
                int count = PropCounts.TryGetValue(prefab.name, out int c) ? c : 1;
                for (int i = 0; i < count; i++, n++) MakeProp(prefab, root);
            }
            if (!Application.isPlaying) EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            Debug.Log($"JunkRace: декораций на свалке: {n}.");
        }

        static void MakeProp(GameObject prefab, Transform root)
        {
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, root);
            go.name = prefab.name;
            go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            foreach (var c in go.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(c);

            var renderers = go.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0) { Object.DestroyImmediate(go); return; }
            foreach (var r in renderers)
            {
                var mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++) mats[i] = LookBuilder.ConvertMaterial(mats[i]);
                r.sharedMaterials = mats;
            }

            Bounds b = renderers[0].bounds;
            foreach (var r in renderers) b.Encapsulate(r.bounds);
            // Ставим на землю (низом на 0) и пока — в случайное место свалки (при запуске переставится).
            Vector3 lift = Vector3.up * -b.min.y;
            go.transform.position = lift;
            Vector3 scale = go.transform.lossyScale;
            var box = go.AddComponent<BoxCollider>();
            box.center = new Vector3(b.center.x / scale.x, b.center.y / scale.y, b.center.z / scale.z);   // рамка мерилась с корнем в нуле
            box.size = new Vector3(b.size.x / scale.x, b.size.y / scale.y, b.size.z / scale.z);
            var p = RandomYardPos();
            go.transform.position = new Vector3(p.x, lift.y, p.z);

            var prop = go.AddComponent<YardProp>();
            prop.Radius = Mathf.Max(b.extents.x, b.extents.z);
            prop.Big = prop.Radius > 3.5f || b.size.y > 5f;
        }

        // ---------------- Кресла ----------------

        enum SeatStyle { Office, Car, Toilet }

        /// <summary>Кресло: начало координат — низ (ставится на точку), смотрит вперёд (+Z).</summary>
        static void MakeSeat(Transform parent, SeatStyle style)
        {
            string name, desc;
            float mass, breakImpulse;
            switch (style)
            {
                case SeatStyle.Office: name = "Офисное кресло"; desc = "Крутится. Слетает при первом же ударе — вместе с тобой."; mass = 8f; breakImpulse = 300f; break;
                case SeatStyle.Car: name = "Автокресло"; desc = "С ремнём. Держится крепко, почти никогда не слетает."; mass = 14f; breakImpulse = 1200f; break;
                default: name = "Унитаз"; desc = "Сидеть удобно. Держится средне. Выглядит… смело."; mass = 25f; breakImpulse = 600f; break;
            }
            var p = MakePart<SeatPart>(name, PartCategory.Special, parent, RandomYardPos(), mass, breakImpulse, 0f, desc, 0.08f);
            var t = p.transform;
            var col = p.gameObject.AddComponent<BoxCollider>();
            float seatTop;
            switch (style)
            {
                case SeatStyle.Office:
                {
                    Color black = new Color(0.12f, 0.12f, 0.14f);
                    Prim(PrimitiveType.Cube, "Крестовина", t, new Vector3(0f, 0.03f, 0f), new Vector3(0.5f, 0.05f, 0.08f), black, false);
                    Prim(PrimitiveType.Cube, "Крестовина", t, new Vector3(0f, 0.03f, 0f), new Vector3(0.08f, 0.05f, 0.5f), black, false);
                    Prim(PrimitiveType.Cylinder, "Стойка", t, new Vector3(0f, 0.2f, 0f), new Vector3(0.06f, 0.15f, 0.06f), Color.gray, false);
                    Prim(PrimitiveType.Cube, "Сиденье", t, new Vector3(0f, 0.4f, 0f), new Vector3(0.5f, 0.1f, 0.5f), black, false);
                    Prim(PrimitiveType.Cube, "Спинка", t, new Vector3(0f, 0.72f, -0.22f), new Vector3(0.48f, 0.55f, 0.08f), black, false);
                    col.center = new Vector3(0f, 0.48f, -0.02f); col.size = new Vector3(0.5f, 0.95f, 0.54f);
                    seatTop = 0.45f;
                    break;
                }
                case SeatStyle.Car:
                {
                    Color fabric = new Color(0.2f, 0.2f, 0.3f);
                    Prim(PrimitiveType.Cube, "Подушка", t, new Vector3(0f, 0.1f, 0.02f), new Vector3(0.5f, 0.18f, 0.5f), fabric, false);
                    var back = Prim(PrimitiveType.Cube, "Спинка", t, new Vector3(0f, 0.55f, -0.22f), new Vector3(0.5f, 0.75f, 0.12f), fabric, false);
                    back.transform.localRotation = Quaternion.Euler(-10f, 0f, 0f);
                    Prim(PrimitiveType.Cube, "Подголовник", t, new Vector3(0f, 1.0f, -0.28f), new Vector3(0.28f, 0.18f, 0.1f), fabric, false);
                    Prim(PrimitiveType.Cube, "Ремень", t, new Vector3(0.12f, 0.55f, -0.15f), new Vector3(0.06f, 0.7f, 0.02f), new Color(0.8f, 0.1f, 0.1f), false);
                    col.center = new Vector3(0f, 0.55f, -0.05f); col.size = new Vector3(0.5f, 1.1f, 0.55f);
                    seatTop = 0.2f;
                    break;
                }
                default:
                {
                    Color white = new Color(0.95f, 0.95f, 0.97f);
                    Prim(PrimitiveType.Cube, "Чаша", t, new Vector3(0f, 0.2f, 0.05f), new Vector3(0.38f, 0.4f, 0.5f), white, false);
                    Prim(PrimitiveType.Cube, "Бачок", t, new Vector3(0f, 0.55f, -0.25f), new Vector3(0.42f, 0.35f, 0.16f), white, false);
                    Prim(PrimitiveType.Cube, "Сиденье", t, new Vector3(0f, 0.42f, 0.05f), new Vector3(0.4f, 0.04f, 0.5f), new Color(0.85f, 0.75f, 0.55f), false);
                    col.center = new Vector3(0f, 0.37f, -0.02f); col.size = new Vector3(0.42f, 0.74f, 0.62f);
                    seatTop = 0.44f;
                    break;
                }
            }
            // Сидящий игрок: центр капсулы чуть выше сиденья (ноги «в кресле»).
            string seatModel = style == SeatStyle.Office ? "chairDesk_Palette" : style == SeatStyle.Car ? "car chair" : "toilet";
            bool office = style == SeatStyle.Office;
            DressPart(p, SeatFolder, seatModel, col.center, col.size,
                      preRotation: office ? Quaternion.Euler(0f, 180f, 0f) : (Quaternion?)null, preserveFacing: office);
            var sit = new GameObject("SitPoint").transform;
            sit.SetParent(t, false);
            sit.localPosition = new Vector3(0f, seatTop + 0.4f, 0f);
            Set(p, "sitPoint", sit);
        }

        // ---------------- Кучи мусора ----------------
        // Пока заглушки из кубиков; потом их можно заменить префабами с компонентом JunkPile.

        static readonly Vector3[] PilePositions =
        {
            new Vector3(-9f, 0f, -24f), new Vector3(2f, 0f, -26f), new Vector3(10f, 0f, -21f),
            new Vector3(-11f, 0f, -9f), new Vector3(0f, 0f, -7f), new Vector3(11f, 0f, -8f),
            // Ещё куч: 16 вместо 6. Где именно они встанут — решает YardShuffle по сиду.
            new Vector3(-20f, 0f, -40f), new Vector3(18f, 0f, -38f), new Vector3(0f, 0f, -48f),
            new Vector3(-22f, 0f, -18f), new Vector3(22f, 0f, -16f), new Vector3(-6f, 0f, -36f),
            new Vector3(-24f, 0f, -50f), new Vector3(24f, 0f, -50f), new Vector3(14f, 0f, -30f), new Vector3(-14f, 0f, -30f),
        };

        static List<JunkPile> BuildPiles()
        {
            var root = new GameObject("Кучи_мусора").transform;
            var list = new List<JunkPile>();
            for (int i = 0; i < PilePositions.Length; i++)
                list.Add(MakePile(root, "Куча_" + (i + 1), PilePositions[i]));
            return list;
        }

        const string TrashFolder = "Assets/Prefab/junky/Trash";
        /// <summary>Насколько опустить модель кучи после постановки на землю, м.</summary>
        const float PileSink = 0f;
        /// <summary>Высота самой кучи (объекта «Куча_N») на сцене — подобрано на глаз.</summary>
        const float PileRootY = -0.55f;
        /// <summary>Размер модели кучи: поперечник и высота, м.</summary>
        const float PileWidth = 7.6f, PileHeight = 3.2f;
        /// <summary>С какого расстояния (от центра кучи) можно в ней рыться.</summary>
        const float PileReach = PileWidth / 2f + 1.5f;

        /// <summary>Опускает/поднимает модель так, чтобы её низ был на уровне owner (земля кучи).</summary>
        static void SitOnGround(Transform model, Transform owner)
        {
            float minY = float.MaxValue;
            foreach (var r in model.GetComponentsInChildren<Renderer>())
                minY = Mathf.Min(minY, r.bounds.min.y);
            if (minY < float.MaxValue) model.position += Vector3.up * (owner.position.y - minY);
        }

        /// <summary>Ставит в кучу модель из папки Trash. false — модели нет.</summary>
        static int pileModelIndex;

        /// <summary>
        /// Подгонка тона моделей куч друг к другу: множитель цвета. Эталон — trash_Pile;
        /// junky светлее и рыжее (средний цвет текстуры 0.46/0.38/0.33 против 0.39/0.36/0.32).
        /// </summary>
        static readonly Dictionary<string, Color> PileTints = new Dictionary<string, Color>
        {
            { "junky", new Color(0.85f, 0.93f, 0.99f) },
        };

        static readonly Dictionary<Material, Material> tintedMaterials = new Dictionary<Material, Material>();

        static void TintPile(GameObject view, Color tint)
        {
            foreach (var r in view.GetComponentsInChildren<Renderer>(true))
            {
                var mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                {
                    var m = mats[i];
                    if (m == null) continue;
                    if (!tintedMaterials.TryGetValue(m, out var tinted) || tinted == null)
                    {
                        tinted = new Material(m) { name = m.name + " (тон)" };
                        tinted.color = m.color * tint;
                        tintedMaterials[m] = tinted;
                    }
                    mats[i] = tinted;
                }
                r.sharedMaterials = mats;
            }
        }

        static bool DressPile(Transform t)
        {
            // Моделей куч может быть несколько — каждой куче случайная.
            var models = ModelFitter.FindAll(TrashFolder);
            if (models.Count == 0) return false;
            // По очереди, а не случайно: моделей поровну (две модели — 50/50).
            var model = models[pileModelIndex++ % models.Count];
            var turn = new GameObject("Поворот").transform;
            turn.SetParent(t, false);
            turn.localRotation = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);
            var view = ModelFitter.Attach(model, turn, new Vector3(0f, PileHeight / 2f, 0f),
                                          new Vector3(PileWidth, PileHeight, PileWidth), keepProportions: true);
            if (PileTints.TryGetValue(model.name, out var tint)) TintPile(view, tint);
            ModelFitter.AddConvexColliders(view);
            // keepProportions уменьшает по самой тесной оси — ставим низом на землю.
            SitOnGround(view.transform, t);
            view.transform.position += Vector3.up * PileSink;   // слегка утопить в землю: у модели снизу пустоты
            AddPileLod(view, model.name);
            return true;
        }

        static void AddPileLod(GameObject view, string modelName)
        {
            var lodMesh = AssetDatabase.LoadAssetAtPath<Mesh>($"{TrashFolder}/{modelName}_LOD1.asset");
            if (lodMesh == null || view.GetComponent<LODGroup>() != null) return;
            var source = view.GetComponentInChildren<MeshRenderer>(true);
            if (source == null) return;

            var lodView = new GameObject(lodMesh.name) { layer = source.gameObject.layer };
            lodView.transform.SetParent(source.transform, false);
            lodView.AddComponent<MeshFilter>().sharedMesh = lodMesh;
            var low = lodView.AddComponent<MeshRenderer>();
            low.sharedMaterials = source.sharedMaterials;
            low.shadowCastingMode = source.shadowCastingMode;
            low.receiveShadows = source.receiveShadows;

            var group = view.AddComponent<LODGroup>();
            group.SetLODs(new[]
            {
                new LOD(0.08f, new Renderer[] { source }),
                new LOD(0f, new Renderer[] { low }),
            });
            group.RecalculateBounds();
        }

        /// <summary>Заменить вид всех куч в открытой сцене на модель — без полной пересборки.</summary>
        [MenuItem("JunkRace/Кучи: поставить модель в открытой сцене")]
        public static void DressPilesInOpenScene()
        {
            if (ModelFitter.FindFirst(TrashFolder) == null)
            {
                Debug.LogWarning($"JunkRace: в {TrashFolder} нет модели кучи.");
                return;
            }
            LookBuilder.ResetMaterialCache();
            int n = 0;
            foreach (var pile in Object.FindObjectsByType<JunkPile>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var t = pile.transform;
                var p = t.position;
                t.position = new Vector3(p.x, PileRootY, p.z);
                // Детали из тайника лежат не внутри кучи, а на свалке — внутри только «декорации».
                for (int i = t.childCount - 1; i >= 0; i--)
                {
                    var child = t.GetChild(i);
                    if (child.GetComponentInChildren<Part>(true) != null) continue;
                    Object.DestroyImmediate(child.gameObject);
                }
                if (DressPile(t)) { n++; Set(pile, "reach", PileReach); }
            }
            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
            Debug.Log($"JunkRace: кучи заменены на модель: {n}. Сохрани сцену.");
        }

        static JunkPile MakePile(Transform parent, string name, Vector3 pos)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.position = new Vector3(pos.x, PileRootY, pos.z);
            var t = go.transform;

            // Есть модель в Assets/Prefab/junky/Trash — ставим её (каждую кучу повёрнутой по-своему).
            if (DressPile(t))
            {
                var dressed = go.AddComponent<JunkPile>();
                Set(dressed, "reach", PileReach);
                return dressed;
            }

            Color[] colors =
            {
                new Color(0.45f, 0.25f, 0.12f), new Color(0.4f, 0.4f, 0.42f), new Color(0.25f, 0.23f, 0.2f),
                new Color(0.3f, 0.35f, 0.45f), new Color(0.32f, 0.4f, 0.25f), new Color(0.55f, 0.5f, 0.4f),
            };
            const float radius = 1.8f, height = 1.4f;
            for (int i = 0; i < 20; i++)
            {
                float r = Random.Range(0f, radius);
                float a = Random.Range(0f, Mathf.PI * 2f);
                float h = (1f - r / radius) * height;   // выше в центре — горка
                bool tire = Random.value < 0.2f;
                GameObject bit;
                if (tire)
                {
                    float d = Random.Range(0.6f, 0.9f);
                    bit = Prim(PrimitiveType.Cylinder, "Покрышка", t, new Vector3(Mathf.Cos(a) * r, h * 0.8f + 0.1f, Mathf.Sin(a) * r),
                               new Vector3(d, 0.12f, d), new Color(0.12f, 0.12f, 0.12f), false);
                    bit.AddComponent<BoxCollider>();   // у примитива-цилиндра коллайдер — капсула, на плоской покрышке это шар
                }
                else
                {
                    var size = new Vector3(Random.Range(0.4f, 1.1f), Random.Range(0.25f, 0.7f), Random.Range(0.4f, 1.1f));
                    bit = Prim(PrimitiveType.Cube, "Хлам", t, new Vector3(Mathf.Cos(a) * r, h * 0.8f + size.y * 0.3f, Mathf.Sin(a) * r),
                               size, colors[Random.Range(0, colors.Length)]);
                }
                bit.transform.localRotation = Quaternion.Euler(Random.Range(-25f, 25f), Random.Range(0f, 360f), Random.Range(-25f, 25f));
            }
            return go.AddComponent<JunkPile>();
        }

        /// <summary>
        /// Раскладка по кучам: ванна и паллет стоят на виду (с чего-то надо начать), остальные корпуса спрятаны.
        /// Прочие детали: примерно половина спрятана в кучах, остальные валяются рядом с ними.
        /// </summary>
        static void DistributeIntoPiles(Transform yard, List<JunkPile> piles)
        {
            if (piles.Count == 0) return;
            var stashes = new List<Part>[piles.Count];
            for (int i = 0; i < stashes.Length; i++) stashes[i] = new List<Part>();

            var loose = new List<Part>();
            foreach (Transform child in yard)
                if (child.TryGetComponent(out Part part)) loose.Add(part);

            // Для удобства тестов: руль и одно кресло сразу лежат у старта, рядом с игроком.
            Part starterChair = null, starterSteering = null;
            foreach (var part in loose)
            {
                if (part is SteeringPart && starterSteering == null)
                {
                    starterSteering = part;
                    part.transform.position = new Vector3(-1.5f, 1f, -29f);
                }
                else if (part is SeatPart && starterChair == null)
                {
                    starterChair = part;
                    part.transform.position = new Vector3(1.5f, 1f, -29f);
                }
            }

            // Что спрятано в кучах, а что лежит рядом — решается при запуске (YardShuffle): по сиду и числу игроков.
            for (int i = 0; i < piles.Count; i++) SetList(piles[i], "stash", new List<Part>());
        }

        static Vector3 RandomYardPos() =>
            new Vector3(Random.Range(-YardShuffle.YardHalf + 9f, YardShuffle.YardHalf - 9f), 1.5f, Random.Range(-24f, -3f));   // не у старта игроков (z < -26); при запуске YardShuffle разложит по всей свалке

        // ---------------- Корпуса ----------------
        // Начало координат корпуса — его «пол» (там, где стоят руль и моторы), если не сказано иное.

        static void MakeBathtub(Transform parent, Vector3 pos)
        {
            var p = MakePart<Part>("Ванна", PartCategory.Hull, parent, pos, 60f, 99999f, 5000f, "Чугунная. Плавает, как ни странно.", 0.45f);
            Color white = new Color(0.95f, 0.95f, 0.97f);
            var t = p.transform;

            // Габариты под модель bathtub: дно тонкое (~12% высоты), внутренний пол — на нуле.
            // От низа дна (-0.1) до верха бортов (0.65).
            Prim(PrimitiveType.Cube, "Дно", t, new Vector3(0f, -0.05f, 0f), new Vector3(2f, 0.1f, 3f), white);
            Prim(PrimitiveType.Cube, "Борт_Л", t, new Vector3(-0.92f, 0.3f, 0f), new Vector3(0.16f, 0.7f, 3f), white);
            Prim(PrimitiveType.Cube, "Борт_П", t, new Vector3(0.92f, 0.3f, 0f), new Vector3(0.16f, 0.7f, 3f), white);
            Prim(PrimitiveType.Cube, "Борт_Пер", t, new Vector3(0f, 0.3f, 1.42f), new Vector3(2f, 0.7f, 0.16f), white);
            Prim(PrimitiveType.Cube, "Борт_Зад", t, new Vector3(0f, 0.3f, -1.42f), new Vector3(2f, 0.7f, 0.16f), white);
            MakeVehicle(p, AddHullPoints(t, 2f, 3f, floorY: 0f, topY: 0.7f));
            DressHull(p, "bathtub", new Vector3(0f, 0.275f, 0f), new Vector3(2f, 0.75f, 3f));
            // Дном вверх: сидишь на днище (верх 0.65), колёса там же.
            MakeFlippable(p, pivotY: 0.275f, floorShift: 0.65f, wheelShift: 0f);
        }

        /// <summary>
        /// Корпус можно перевернуть (R). Вид и коллайдеры — в контейнер «Корпус», места крепления остаются снаружи:
        /// при перевороте они сдвигаются на floorShift (пол) и wheelShift (колёса).
        /// </summary>
        static void MakeFlippable(Part hull, float pivotY, float floorShift, float wheelShift)
        {
            var t = hull.transform;
            var body = new GameObject("Корпус").transform;
            body.SetParent(t, false);
            var move = new List<Transform>();
            for (int i = 0; i < t.childCount; i++)
            {
                var c = t.GetChild(i);
                if (c == body || c.name == "Seat" || c.GetComponent<AttachPoint>() != null) continue;
                move.Add(c);
            }
            foreach (var c in move) c.SetParent(body, false);
            var flip = hull.gameObject.AddComponent<HullFlip>();
            Set(flip, "body", body);
            Set(flip, "pivotY", pivotY);
            Set(flip, "floorShift", floorShift);
            Set(flip, "wheelShift", wheelShift);
        }

        const string BodyFolder = "Body", WheelFolder = "wheel";

        /// <summary>
        /// Есть модель корпуса в Assets/Prefab/Car/Body — она заменяет вид. Коробки-примитивы
        /// остаются невидимыми коллайдерами, так что физика корпуса не меняется.
        /// </summary>
        static void DressHull(Part hull, string file, Vector3 center, Vector3 size, Quaternion? preRotation = null, bool flat = false)
        {
            // Детали и колёса прижимаются к вершинам модели корпуса (HullShape) — меш должен читаться в игре.
            var model = ModelFitter.Find(BodyFolder, file);
            if (model != null && AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(model)) is ModelImporter mi && !mi.isReadable)
            {
                mi.isReadable = true;
                mi.SaveAndReimport();
            }
            DressPart(hull, BodyFolder, file, center, size, preRotation, flat);
        }

        /// <summary>Модель из Assets/Prefab/Car/{folder}/{file} заменяет вид детали; примитивы остаются коллайдерами.</summary>
        static bool DressPart(Part part, string folder, string file, Vector3 center, Vector3 size, Quaternion? preRotation = null,
                              bool flat = false, bool preserveFacing = false)
        {
            var model = ModelFitter.Find(folder, file);
            if (model == null) return false;
            ModelFitter.StripOwnRenderers(part);
            ModelFitter.Attach(model, part.transform, center, size, false, preRotation, flat, preserveFacing);
            return true;
        }

        /// <summary>Модель заменяет вид только внутри узла (баранка руля крутится — модель вместе с ней).</summary>
        static bool DressNode(Transform node, string folder, string file, Vector3 center, Vector3 size, bool flat = false)
        {
            var model = ModelFitter.Find(folder, file);
            if (model == null) return false;
            foreach (var r in node.GetComponentsInChildren<MeshRenderer>(true))
            {
                var mf = r.GetComponent<MeshFilter>();
                Object.DestroyImmediate(r);
                if (mf != null) Object.DestroyImmediate(mf);
            }
            ModelFitter.Attach(model, node, center, size, false, null, flat);
            return true;
        }

        static void MakePallet(Transform parent, Vector3 pos)
        {
            var p = MakePart<Part>("Паллет", PartCategory.Hull, parent, pos, 20f, 99999f, 0f, "Лёгкий и плоский. Плавать не умеет.", 0.25f);
            Color wood = new Color(0.75f, 0.6f, 0.4f);
            var t = p.transform;
            Prim(PrimitiveType.Cube, "Основа", t, new Vector3(0f, -0.1f, 0f), new Vector3(1.6f, 0.2f, 2.4f), wood);
            for (int i = -1; i <= 1; i++)
                Prim(PrimitiveType.Cube, "Доска", t, new Vector3(i * 0.6f, 0.05f, 0f), new Vector3(0.3f, 0.1f, 2.4f), wood * 0.9f);
            MakeVehicle(p, AddHullPoints(t, 1.6f, 2.4f, floorY: 0.1f, topY: 0.5f));
            DressHull(p, "pallet", new Vector3(0f, -0.05f, 0f), new Vector3(1.6f, 0.3f, 2.4f));
            MakeFlippable(p, pivotY: -0.05f, floorShift: 0f, wheelShift: 0f);
        }

        /// <summary>Перевёрнутый стол: ножками вверх — отличные поручни.</summary>
        static void MakeTable(Transform parent, Vector3 pos)
        {
            const float w = 1.6f, l = 2.4f, legH = 0.75f;   // большой обеденный: чтобы в салон влезли кресла
            var p = MakePart<Part>("Стол", PartCategory.Hull, parent, pos, 25f, 99999f, 3500f,
                                   "Перевёрнутый. Ножки — отличные поручни. На воде держится, но мокро.", 0.3f);
            Color wood = new Color(0.55f, 0.36f, 0.2f);
            var t = p.transform;
            Prim(PrimitiveType.Cube, "Столешница", t, new Vector3(0f, -0.03f, 0f), new Vector3(w, 0.06f, l), wood);
            for (int sx = -1; sx <= 1; sx += 2)
                for (int sz = -1; sz <= 1; sz += 2)
                    Prim(PrimitiveType.Cube, "Ножка", t, new Vector3(sx * (w / 2f - 0.08f), legH / 2f, sz * (l / 2f - 0.08f)),
                         new Vector3(0.08f, legH, 0.08f), wood * 0.85f);
            MakeVehicle(p, AddHullPoints(t, w, l, floorY: 0f, topY: 0.4f));
            // Модель стола обычно стоит на ножках — переворачиваем: у нас он вверх ногами.
            DressHull(p, "table", new Vector3(0f, (legH - 0.06f) / 2f, 0f), new Vector3(w, legH + 0.06f, l), Quaternion.Euler(180f, 0f, 0f));
            // Ножками вниз: столешница на месте, колёса опускаются ниже ножек.
            MakeFlippable(p, pivotY: -0.03f, floorShift: 0f, wheelShift: -legH);
        }

        /// <summary>Шкаф, лежащий на спине. Едешь на дверцах, как на крыше.</summary>
        static void MakeWardrobe(Transform parent, Vector3 pos)
        {
            const float w = 1.0f, l = 2.0f, h = 0.6f;
            var p = MakePart<Part>("Шкаф", PartCategory.Hull, parent, pos, 70f, 99999f, 6000f,
                                   "Лежит на спине. Тяжёлый и прочный, плавает как плот. Едешь на дверцах.", 0.4f);
            Color wood = new Color(0.45f, 0.3f, 0.18f);
            var t = p.transform;
            // Начало координат — верх (дверцы), сам ящик ниже.
            Prim(PrimitiveType.Cube, "Ящик", t, new Vector3(0f, -h / 2f, 0f), new Vector3(w, h, l), wood);
            Prim(PrimitiveType.Cube, "Щель_дверей", t, new Vector3(0f, 0.002f, 0f), new Vector3(0.02f, 0.01f, l * 0.95f), wood * 0.4f, false);
            Prim(PrimitiveType.Cube, "Ручка_Л", t, new Vector3(-0.07f, 0.03f, 0.2f), new Vector3(0.04f, 0.05f, 0.15f), new Color(0.8f, 0.7f, 0.3f), false);
            Prim(PrimitiveType.Cube, "Ручка_П", t, new Vector3(0.07f, 0.03f, 0.2f), new Vector3(0.04f, 0.05f, 0.15f), new Color(0.8f, 0.7f, 0.3f), false);
            MakeVehicle(p, AddHullPoints(t, w, l, floorY: 0f, topY: 0.4f, wheelY: -h / 2f));
            // Модель шкафа стоит, дверцами вбок (+X в Unity) — кладём на спину: высота шкафа вдоль машины,
            // потом поворот вокруг длинной оси, чтобы дверцы смотрели вверх.
            DressHull(p, "wardrobe", new Vector3(0f, -h / 2f, 0f), new Vector3(w, h, l), Quaternion.Euler(0f, 0f, 90f) * Quaternion.Euler(-90f, 0f, 0f));
            MakeFlippable(p, pivotY: -h / 2f, floorShift: 0f, wheelShift: 0f);
        }

        /// <summary>Диван: мягкий, с подлокотниками. Спинка вдоль левого борта.</summary>
        static void MakeSofa(Transform parent, Vector3 pos)
        {
            const float w = 0.95f, l = 2.0f, h = 0.44f;
            var p = MakePart<Part>("Диван", PartCategory.Hull, parent, pos, 45f, 99999f, 2500f,
                                   "Мягкий. На кочках пружинит, на воде держится, как матрас. Сидеть — удобнее некуда.", 0.35f);
            Color fabric = new Color(0.35f, 0.45f, 0.6f);
            var t = p.transform;
            // Начало координат — верх сидушки.
            Prim(PrimitiveType.Cube, "Сидушка", t, new Vector3(0f, -h / 2f, 0f), new Vector3(w, h, l), fabric);
            Prim(PrimitiveType.Cube, "Спинка", t, new Vector3(-w / 2f + 0.08f, 0.25f, 0f), new Vector3(0.16f, 0.5f, l), fabric * 0.85f);
            for (int s = -1; s <= 1; s += 2)
                Prim(PrimitiveType.Cube, "Подлокотник", t, new Vector3(0f, 0.12f, s * (l / 2f - 0.08f)), new Vector3(w, 0.24f, 0.16f), fabric * 0.9f);
            MakeVehicle(p, AddHullPoints(t, w, l, floorY: 0f, topY: 0.5f, wheelY: -0.25f, engines: 1));
            DressHull(p, "sofa", new Vector3(0f, (0.5f - h) / 2f, 0f), new Vector3(w, 0.5f + h, l));
        }

        /// <summary>Надувной фламинго: почти ничего не весит и плавает лучше всех, но ловит ветер.</summary>
        static void MakeFlamingo(Transform parent, Vector3 pos)
        {
            const float w = 1.1f, l = 1.8f;
            var p = MakePart<Part>("Надувной фламинго", PartCategory.Hull, parent, pos, 4f, 99999f, 3500f,
                                   "Надувной. Лёгкий и плавает лучше всех, но ловит ветер, как парус. Не наступать каблуком.", 0.4f);
            Color pink = new Color(1f, 0.45f, 0.65f);
            var t = p.transform;
            // Коллайдер — коробка (сфера с разным масштабом осей в Unity считается шаром), вид — сплюснутый шар.
            var box = Prim(PrimitiveType.Cube, "Тело_коллайдер", t, new Vector3(0f, -0.18f, 0f), new Vector3(w * 0.9f, 0.36f, l * 0.9f), pink);
            Object.DestroyImmediate(box.GetComponent<MeshRenderer>());
            Prim(PrimitiveType.Sphere, "Тело", t, new Vector3(0f, -0.15f, 0f), new Vector3(w, 0.45f, l), pink, false);
            var neck = Prim(PrimitiveType.Cylinder, "Шея", t, new Vector3(0f, 0.4f, l / 2f - 0.05f), new Vector3(0.14f, 0.4f, 0.14f), pink, false);
            neck.transform.localRotation = Quaternion.Euler(-15f, 0f, 0f);
            Prim(PrimitiveType.Sphere, "Голова", t, new Vector3(0f, 0.85f, l / 2f + 0.05f), Vector3.one * 0.24f, pink, false);
            Prim(PrimitiveType.Cube, "Клюв", t, new Vector3(0f, 0.8f, l / 2f + 0.22f), new Vector3(0.08f, 0.08f, 0.16f), new Color(0.15f, 0.15f, 0.15f), false);
            MakeVehicle(p, AddHullPoints(t, w, l, floorY: 0f, topY: 0.3f, wheelY: -0.2f, engines: 1));
            DressHull(p, "inflatable flamingo", new Vector3(0f, 0.3f, 0.08f), new Vector3(w, 1.35f, l + 0.15f));
        }

        /// <summary>Межкомнатная дверь, снятая с петель. Плоская, как паллет, но длиннее и плавает.</summary>
        static void MakeDoor(Transform parent, Vector3 pos)
        {
            const float w = 0.85f, l = 2.0f, th = 0.05f;
            var p = MakePart<Part>("Дверь", PartCategory.Hull, parent, pos, 18f, 99999f, 2200f,
                                   "Межкомнатная, снята с петель. Плоская, плавает. Ручка — для красоты.", 0.2f);
            Color wood = new Color(0.82f, 0.72f, 0.55f);
            var t = p.transform;
            Prim(PrimitiveType.Cube, "Полотно", t, new Vector3(0f, -th / 2f, 0f), new Vector3(w, th, l), wood);
            Prim(PrimitiveType.Cube, "Филёнка", t, new Vector3(0f, 0.003f, 0.45f), new Vector3(w * 0.7f, 0.01f, 0.7f), wood * 0.9f, false);
            Prim(PrimitiveType.Cube, "Филёнка", t, new Vector3(0f, 0.003f, -0.45f), new Vector3(w * 0.7f, 0.01f, 0.7f), wood * 0.9f, false);
            Prim(PrimitiveType.Cube, "Ручка", t, new Vector3(w / 2f - 0.08f, 0.03f, 0f), new Vector3(0.04f, 0.04f, 0.14f), new Color(0.8f, 0.7f, 0.3f), false);
            MakeVehicle(p, AddHullPoints(t, w, l, floorY: 0f, topY: 0.4f, engines: 1));
            DressHull(p, "door", new Vector3(0f, -th / 2f, 0f), new Vector3(w, th, l), flat: true);
            MakeFlippable(p, pivotY: -th / 2f, floorShift: 0f, wheelShift: 0f);
        }

        /// <summary>Доска для сёрфинга: лёгкая и обтекаемая — быстрая, но узкая и вертлявая.</summary>
        static void MakeSurfboard(Transform parent, Vector3 pos)
        {
            const float w = 0.6f, l = 2.2f, th = 0.08f;
            var p = MakePart<Part>("Доска для сёрфинга", PartCategory.Hull, parent, pos, 6f, 99999f, 2500f,
                                   "Обтекаемая и скользкая: самая быстрая, но узкая — переворачивается на раз. По воде — как дома.", 0.08f);
            Color paint = new Color(0.2f, 0.75f, 0.85f);
            var t = p.transform;
            Prim(PrimitiveType.Cube, "Доска", t, new Vector3(0f, -th / 2f, -0.1f), new Vector3(w, th, l - 0.2f), paint);
            Prim(PrimitiveType.Sphere, "Нос", t, new Vector3(0f, -th / 2f, l / 2f - 0.25f), new Vector3(w, th, 0.6f), paint, false);
            Prim(PrimitiveType.Cube, "Полоса", t, new Vector3(0f, 0.002f, 0f), new Vector3(0.08f, 0.01f, l - 0.3f), Color.white, false);
            Prim(PrimitiveType.Cube, "Плавник", t, new Vector3(0f, -th - 0.08f, -l / 2f + 0.2f), new Vector3(0.02f, 0.16f, 0.2f), paint * 0.7f, false);
            MakeVehicle(p, AddHullPoints(t, w, l, floorY: 0f, topY: 0.35f, engines: 1));
            DressHull(p, "surf", new Vector3(0f, -th / 2f, 0f), new Vector3(w, th, l), flat: true);
            MakeFlippable(p, pivotY: -th / 2f, floorShift: 0f, wheelShift: 0f);
        }

        /// <summary>
        /// Места крепления на корпусе. Порядок точек важен для сети (индекс в Part.Points) — у всех одна сцена, так что ок.
        /// floorY — высота «пола», где стоят руль и моторы; topY — высота верхних точек кузова.
        /// wheelsUnder — колёса под корпусом (тележка), иначе — по бокам снаружи.
        /// </summary>
        /// <returns>Место водителя (центр капсулы игрока) — для MakeVehicle.</returns>
        static Vector3 AddHullPoints(Transform hull, float width, float length, float floorY, float topY,
                                     float wheelY = 0f, bool wheelsUnder = false, int wheelPairs = 3, int engines = 2)
        {
            // Салон. Широкий корпус — как обычная машина: водитель слева впереди, руль перед ним,
            // справа пассажир, сзади ещё двое. Узкий (шкаф, тележка) — водитель по центру, рядом никто не влезет.
            bool wide = width >= 1.4f;
            float innerX = Mathf.Min(width / 2f - 0.3f, 0.6f);
            float driverX = wide ? -innerX : 0f;
            float driverZ = wide ? 0.3f : -Mathf.Min(0.3f, length * 0.15f);
            float rearZ = driverZ - 0.6f;
            float steeringZ = Mathf.Min(driverZ + 0.6f, length / 2f - 0.15f);

            float x = wheelsUnder ? width / 2f - 0.05f : width / 2f + 0.25f;
            float z = wheelsUnder ? length / 2f - 0.08f : length / 2f - 0.35f;

            // Колёса: передняя пара поворачивает рулём. По умолчанию шесть, как у вездехода.
            Point(hull, "Колесо_ПЛ", new Vector3(-x, wheelY, z), SlotType.Wheel, true);
            Point(hull, "Колесо_ПП", new Vector3(x, wheelY, z), SlotType.Wheel, true);
            if (wheelPairs >= 3)
            {
                Point(hull, "Колесо_СЛ", new Vector3(-x, wheelY, 0f), SlotType.Wheel);
                Point(hull, "Колесо_СП", new Vector3(x, wheelY, 0f), SlotType.Wheel);
            }
            Point(hull, "Колесо_ЗЛ", new Vector3(-x, wheelY, -z), SlotType.Wheel);
            Point(hull, "Колесо_ЗП", new Vector3(x, wheelY, -z), SlotType.Wheel);

            // Руль — прямо перед водителем, а под водителем — место для водительского кресла.
            Point(hull, "Руль", new Vector3(driverX, floorY, steeringZ), SlotType.Steering);
            Set(Point(hull, "Водитель", new Vector3(driverX, floorY, driverZ), SlotType.Seat), "driver", true);

            // Моторы в самом хвосте — чтобы не залезали в кресла салона.
            float engineZ = -length / 2f + Mathf.Min(0.4f, length * 0.25f);
            if (engines == 1)
                Point(hull, "Мотор", new Vector3(0f, floorY + 0.35f, engineZ), SlotType.Engine);
            else
            {
                float ex = Mathf.Min(0.45f, width * 0.28f);
                Point(hull, "Мотор_Л", new Vector3(-ex, floorY + 0.35f, engineZ), SlotType.Engine);
                Point(hull, "Мотор_П", new Vector3(ex, floorY + 0.35f, engineZ), SlotType.Engine);
            }

            // Кузов: тут работают только самостоятельные детали (вентилятор, зонт), остальное — груз.
            float sideX = Mathf.Max(width / 2f - 0.3f, width * 0.3f);
            Point(hull, "Верх_Перед", new Vector3(0f, topY, length / 2f - 0.35f));
            Point(hull, "Верх_Лево", new Vector3(-sideX, topY, 0f));
            Point(hull, "Верх_Право", new Vector3(sideX, topY, 0f));
            Point(hull, "Багажник", new Vector3(0f, floorY + 0.2f, -length / 2f - 0.3f));   // сзади снаружи, смотрит вперёд

            // Кресла салона: справа от водителя и задний ряд (только в широком корпусе).
            if (wide)
            {
                Point(hull, "Салон_ПП", new Vector3(innerX, floorY, driverZ), SlotType.Seat);
                Point(hull, "Салон_ЗЛ", new Vector3(-innerX, floorY, rearZ), SlotType.Seat);
                Point(hull, "Салон_ЗП", new Vector3(innerX, floorY, rearZ), SlotType.Seat);
            }

            // Подножки — дополнительные места снаружи, по бокам над передними и задними колёсами.
            // Выше любой шины (самая большая — велосипедная, 0.6 м), так что колёсам не мешают. Зато торчат
            // наружу: въехал боком в столб — кресло сорвёт вместе с пассажиром.
            float seatX = wheelsUnder ? width / 2f + 0.3f : x;
            float seatY = wheelY + 0.7f;
            Point(hull, "Подножка_ПЛ", new Vector3(-seatX, seatY, z), SlotType.Seat);
            Point(hull, "Подножка_ПП", new Vector3(seatX, seatY, z), SlotType.Seat);
            Point(hull, "Подножка_ЗЛ", new Vector3(-seatX, seatY, -z), SlotType.Seat);
            Point(hull, "Подножка_ЗП", new Vector3(seatX, seatY, -z), SlotType.Seat);

            // Борта: снаружи по бокам и спереди, вплотную к корпусу. Боковина смотрит наружу (+Z детали).
            float sideY = (topY + Mathf.Min(floorY, wheelY)) / 2f;
            Point(hull, "Борт_Л", new Vector3(-(width / 2f + 0.04f), sideY, 0f), SlotType.Side).transform.localRotation = Quaternion.Euler(0f, -90f, 0f);
            Point(hull, "Борт_П", new Vector3(width / 2f + 0.04f, sideY, 0f), SlotType.Side).transform.localRotation = Quaternion.Euler(0f, 90f, 0f);
            Point(hull, "Борт_Перед", new Vector3(0f, sideY, length / 2f + 0.04f), SlotType.Side);

            return new Vector3(driverX, floorY + 0.9f, driverZ);   // игрок 1.7 м: центр капсулы на 0.85 над полом
        }

        /// <summary>Деталь, уже прикрученная в сцене: без своего Rigidbody, дочерняя к точке. Vehicle.Start её зарегистрирует.</summary>
        static void PreMount(Part part, Transform point)
        {
            if (point == null) { Debug.LogWarning("[JunkRace] Нет точки для " + part.name); return; }
            if (part.TryGetComponent(out Rigidbody rb)) Object.DestroyImmediate(rb);
            part.transform.SetParent(point, false);
            part.transform.localPosition = Vector3.zero;
            part.transform.localRotation = Quaternion.identity;
        }

        static void MakeVehicle(Part hull, Vector3 seatPos)
        {
            var seat = new GameObject("Seat").transform;
            seat.SetParent(hull.transform, false);
            seat.localPosition = seatPos;
            var v = hull.gameObject.AddComponent<Vehicle>();
            Set(v, "seat", seat);
        }

        // ---------------- Колёса ----------------

        enum WheelStyle { Tire, Bicycle, Manhole, Ring, Porthole }

        /// <param name="thickness">Половина ширины шины.</param>
        static WheelPart MakeWheel(Transform parent, string name, string description, float mass, float radius,
                                   float traction, float grip, Color color, WheelStyle style = WheelStyle.Tire,
                                   float thickness = 0.12f, float breakImpulse = 600f, float rolling = 3f,
                                   float buoyancy = 0f, float paddle = 0.5f, string model = null, float visualYaw = 0f)
        {
            var p = MakePart<WheelPart>(name, PartCategory.Drive, parent, RandomYardPos(), mass, breakImpulse, buoyancy, description);
            // Коллайдер в форме шины (плоский цилиндр): свободное колесо катится как колесо, а не как мяч.
            // Размер как у видимой шины. На машине машину держит луч-подвеска,
            // а коллайдер работает упором только при жёстком приземлении.
            AddTireCollider(p.transform, radius, Mathf.Max(thickness * 2f, 0.05f));

            var pivot = new GameObject("Pivot").transform;
            pivot.SetParent(p.transform, false);
            // Есть модель в Assets/Prefab/Car/wheel — ставим её внутрь вращающейся части вместо примитивов.
            var wheelModel = string.IsNullOrEmpty(model) ? null : ModelFitter.Find(WheelFolder, model);
            if (wheelModel != null)
            {
                var modelGo = ModelFitter.AttachWheel(wheelModel, pivot, radius * 2f, visualYaw);
                // Ширина шины — по модели: под неё и коллайдер, и прижим к борту (WheelPart.InsetTowardHull).
                float half = ModelFitter.VertexBounds(modelGo, p.transform).extents.x;
                var col = p.transform.Find("Коллайдер_шины");
                if (col != null) col.localScale = new Vector3(radius * 2f, Mathf.Max(half, 0.025f), radius * 2f);
                Set(p, "modelHalfWidth", half);
            }
            else switch (style)
            {
                case WheelStyle.Tire:
                {
                    var tire = Prim(PrimitiveType.Cylinder, "Шина", pivot, Vector3.zero, new Vector3(radius * 2f, thickness, radius * 2f), color, false);
                    tire.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);
                    Prim(PrimitiveType.Cube, "Метка", pivot, new Vector3(0f, radius * 0.75f, 0f), new Vector3(thickness * 2f + 0.02f, 0.12f, 0.12f), Color.yellow, false);
                    break;
                }
                case WheelStyle.Bicycle:
                {
                    Ring(pivot, "Покрышка", radius - 0.025f, 0.05f, thickness * 2f, 20, color, color);
                    Ring(pivot, "Обод", radius - 0.065f, 0.03f, thickness * 2f, 20, new Color(0.75f, 0.75f, 0.78f), new Color(0.75f, 0.75f, 0.78f));
                    for (int i = 0; i < 4; i++)
                    {
                        var spoke = Prim(PrimitiveType.Cube, "Спицы", pivot, Vector3.zero, new Vector3(0.01f, 0.01f, (radius - 0.07f) * 2f), new Color(0.8f, 0.8f, 0.82f), false);
                        spoke.transform.localRotation = Quaternion.Euler(i * 45f, 0f, 0f);
                    }
                    var hub = Prim(PrimitiveType.Cylinder, "Втулка", pivot, Vector3.zero, new Vector3(0.06f, thickness * 2f, 0.06f), Color.gray, false);
                    hub.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);
                    break;
                }
                case WheelStyle.Manhole:
                {
                    var disc = Prim(PrimitiveType.Cylinder, "Крышка", pivot, Vector3.zero, new Vector3(radius * 2f, thickness, radius * 2f), color, false);
                    disc.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);
                    // Рифление с обеих сторон.
                    for (int side = -1; side <= 1; side += 2)
                        for (int k = -1; k <= 1; k++)
                        {
                            Prim(PrimitiveType.Cube, "Рифление", pivot, new Vector3(side * (thickness + 0.005f), k * radius * 0.4f, 0f),
                                 new Vector3(0.012f, 0.03f, radius * 1.5f), color * 0.7f, false);
                            Prim(PrimitiveType.Cube, "Рифление", pivot, new Vector3(side * (thickness + 0.005f), 0f, k * radius * 0.4f),
                                 new Vector3(0.012f, radius * 1.5f, 0.03f), color * 0.7f, false);
                        }
                    break;
                }
                case WheelStyle.Ring:
                {
                    // Бублик из сегментов: оранжевые и белые четверти, как у настоящего круга.
                    float tube = thickness * 1.6f;
                    Ring(pivot, "Круг", radius - tube / 2f, tube, tube, 16, color, Color.white);
                    break;
                }
                case WheelStyle.Porthole:
                {
                    // Латунное кольцо, стекло и болты по кругу.
                    Ring(pivot, "Рама", radius - 0.06f, 0.12f, thickness * 2f, 18, color, color);
                    var glass = Prim(PrimitiveType.Cylinder, "Стекло", pivot, Vector3.zero, new Vector3((radius - 0.1f) * 2f, thickness * 0.6f, (radius - 0.1f) * 2f),
                                     new Color(0.55f, 0.75f, 0.85f), false);
                    glass.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);
                    for (int i = 0; i < 8; i++)
                    {
                        float a = i * Mathf.PI / 4f;
                        for (int side = -1; side <= 1; side += 2)
                            Prim(PrimitiveType.Cube, "Болт", pivot, new Vector3(side * (thickness + 0.01f), Mathf.Cos(a) * (radius - 0.06f), Mathf.Sin(a) * (radius - 0.06f)),
                                 Vector3.one * 0.035f, color * 0.6f, false);
                    }
                    break;
                }
            }

            Set(p, "radius", radius);
            Set(p, "traction", traction);
            Set(p, "grip", grip);
            Set(p, "rollingResistance", rolling);
            Set(p, "paddle", paddle);
            Set(p, "visual", pivot);
            return p;
        }

        /// <summary>
        /// Кольцо из кубиков в плоскости YZ (ось — X, как у колеса). Цвета чередуются по четвертям
        /// (для спасательного круга), для однотонного кольца передать один цвет дважды.
        /// </summary>
        static void Ring(Transform parent, string name, float radius, float tube, float width, int segments, Color a, Color b)
        {
            float segLen = 2f * Mathf.PI * radius / segments * 1.08f;
            for (int i = 0; i < segments; i++)
            {
                float ang = i * 360f / segments;
                float rad = ang * Mathf.Deg2Rad;
                Color c = (i * 4 / segments) % 2 == 0 ? a : b;
                var seg = Prim(PrimitiveType.Cube, name, parent, new Vector3(0f, Mathf.Cos(rad) * radius, Mathf.Sin(rad) * radius),
                               new Vector3(width, tube, segLen), c, false);
                // Длинная сторона (Z) — по касательной к кольцу.
                seg.transform.localRotation = Quaternion.Euler(ang, 0f, 0f);
            }
        }

        static void MakeEngine(Transform parent, string name, string description, float mass, float power,
                               float breakImpulse, Color color, Vector3 size, string model = null)
        {
            var p = MakePart<EnginePart>(name, PartCategory.Drive, parent, RandomYardPos(), mass, breakImpulse, 0f, description);
            p.gameObject.AddComponent<BoxCollider>().size = size;
            var body = Prim(PrimitiveType.Cube, "Корпус", p.transform, Vector3.zero, size, color, false);
            Prim(PrimitiveType.Cylinder, "Выхлоп", body.transform, new Vector3(0.3f, 0.6f, -0.3f), new Vector3(0.15f, 0.3f, 0.15f), Color.gray, false);
            Set(p, "power", power);
            Set(p, "shaker", body.transform);
            // Модель — внутрь трясущегося корпуса (у него масштаб = size, поэтому рамка — единичная).
            if (!string.IsNullOrEmpty(model)) DressNode(body.transform, EngineFolder, model, Vector3.zero, Vector3.one);
        }

        static void MakeFan(Transform parent)
        {
            var p = MakePart<ThrusterPart>("Вентилятор", PartCategory.Drive, parent, RandomYardPos(), 12f, 500f, 0f, "Работает и на суше, и на воде. Прикрути задом наперёд — будет весело.", 0.15f);
            p.gameObject.AddComponent<BoxCollider>().size = new Vector3(0.9f, 0.9f, 0.3f);
            Prim(PrimitiveType.Cube, "Рамка", p.transform, Vector3.zero, new Vector3(0.9f, 0.9f, 0.1f), new Color(0.3f, 0.3f, 0.35f), false);
            var spinner = new GameObject("Лопасти").transform;
            spinner.SetParent(p.transform, false);
            spinner.localPosition = new Vector3(0f, 0f, -0.12f);
            Prim(PrimitiveType.Cube, "Лопасть1", spinner, Vector3.zero, new Vector3(0.8f, 0.12f, 0.03f), Color.white, false);
            Prim(PrimitiveType.Cube, "Лопасть2", spinner, Vector3.zero, new Vector3(0.12f, 0.8f, 0.03f), Color.white, false);
            Set(p, "thrust", 1200f);
            Set(p, "spinner", spinner);
            DressPart(p, SpecialFolder, "FanReplacement_Palette", Vector3.zero, new Vector3(0.9f, 0.9f, 0.3f));
        }

        static void MakeVacuum(Transform parent)
        {
            var p = MakePart<ThrusterPart>("Пылесос (турбо на пробел)", PartCategory.Drive, parent, RandomYardPos(), 10f, 500f, 0f, "Дует в обратку. Короткий рывок на пробел.");
            var vc = p.gameObject.AddComponent<CapsuleCollider>();
            vc.direction = 2; vc.height = 1f; vc.radius = 0.25f;
            var body = Prim(PrimitiveType.Capsule, "Корпус", p.transform, Vector3.zero, new Vector3(0.5f, 0.5f, 0.5f), new Color(0.6f, 0.2f, 0.8f), false);
            body.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            Set(p, "thrust", 2500f);
            Set(p, "useSpecialButton", true);
            DressPart(p, SpecialFolder, "hoover", Vector3.zero, new Vector3(0.5f, 0.5f, 1f));
        }

        enum SteerStyle { Wheel, Laptop }
        const string SteeringFolder = "Steering", SideFolder = "Side", SeatFolder = "Seat", EngineFolder = "Engine", SpecialFolder = "Special";

        static void MakeSteering(Transform parent, SteerStyle style)
        {
            string name, desc, hint = "";
            float mass, breakImpulse, quality = 1f, yaw = 1.5f, turn = 90f;
            switch (style)
            {
                case SteerStyle.Laptop:
                    name = "Ноутбук"; desc = "Открытый, экран к водителю. Крутишь, как руль, и надеешься на лучшее.";
                    mass = 2f; breakImpulse = 250f; quality = 0.6f; yaw = 1f; turn = 60f; hint = "Батарея села, но крутить можно"; break;
                default:
                    name = "Руль"; desc = "Без него колёса смотрят строго вперёд.";
                    mass = 4f; breakImpulse = 400f; break;
            }
            var p = MakePart<SteeringPart>(name, PartCategory.Control, parent, RandomYardPos(), mass, breakImpulse, 0f, desc);
            // Начало координат — низ стойки (на полу). Рукоятка на высоте рук сидящего (~0.8 м).
            var box = p.gameObject.AddComponent<BoxCollider>();
            box.center = new Vector3(0f, 0.45f, 0f);
            box.size = new Vector3(0.55f, 0.9f, 0.35f);
            Color column = Color.gray;
            Prim(PrimitiveType.Cylinder, "Стойка", p.transform, new Vector3(0f, 0.38f, 0.05f), new Vector3(0.07f, 0.38f, 0.07f), column, false);
            Prim(PrimitiveType.Cube, "Основание", p.transform, new Vector3(0f, 0.02f, 0.05f), new Vector3(0.3f, 0.04f, 0.25f), Color.gray * 0.7f, false);

            Transform visual;
            {
                // Баранка (и ноутбук) наклонены к водителю — колени проходят под ней.
                var tilt = new GameObject("Наклон").transform;
                tilt.SetParent(p.transform, false);
                tilt.localPosition = new Vector3(0f, 0.8f, 0f);
                tilt.localRotation = Quaternion.Euler(-55f, 0f, 0f);
                visual = new GameObject("Баранка").transform;
                visual.SetParent(tilt, false);
                if (style == SteerStyle.Laptop)
                {
                    Prim(PrimitiveType.Cube, "Ноутбук", visual, Vector3.zero, new Vector3(0.36f, 0.025f, 0.25f), new Color(0.25f, 0.25f, 0.28f), false);
                    Prim(PrimitiveType.Cube, "Яблочко", visual, new Vector3(0f, 0.014f, 0f), new Vector3(0.05f, 0.004f, 0.05f), new Color(0.85f, 0.85f, 0.9f), false);
                    // Модель — открытый ноутбук: ставим ровно (снимаем наклон баранки), экраном к водителю.
                    var laptop = ModelFitter.Find(SteeringFolder, "laptop");
                    if (laptop != null)
                    {
                        foreach (var r in visual.GetComponentsInChildren<MeshRenderer>(true)) { var mf = r.GetComponent<MeshFilter>(); Object.DestroyImmediate(r); if (mf) Object.DestroyImmediate(mf); }
                        var level = new GameObject("Ноутбук_ровно").transform;
                        level.SetParent(visual, false);
                        level.localRotation = Quaternion.Euler(55f, 0f, 0f) * Quaternion.Euler(0f, 180f, 0f);
                        ModelFitter.Attach(laptop, level, new Vector3(0f, 0.02f, 0f), new Vector3(0.38f, 0.28f, 0.28f), true);
                    }
                }
                else
                {
                    Prim(PrimitiveType.Cylinder, "Обод", visual, Vector3.zero, new Vector3(0.55f, 0.03f, 0.55f), Color.black, false);
                    Prim(PrimitiveType.Cube, "Спица", visual, new Vector3(0f, 0.04f, 0f), new Vector3(0.5f, 0.04f, 0.08f), Color.red, false);
                    DressNode(visual, SteeringFolder, "rudder", Vector3.zero, new Vector3(0.55f, 0.07f, 0.55f), flat: true);
                }
            }
            Set(p, "wheelVisual", visual);
            Set(p, "quality", quality);
            Set(p, "yawAccel", yaw);
            Set(p, "visualTurn", turn);
            SetString(p, "steerHint", hint);
        }

        // ---------------- Борта ----------------

        enum ArmorStyle { CarDoor, WindowFrame }

        /// <summary>
        /// Боковина. Начало координат — середина, плашмя к борту: длина по X, высота по Y, толщина по Z
        /// (наружу смотрит +Z). Точка «Борт» на корпусе развёрнута так, что +Z детали — наружу.
        /// </summary>
        static void MakeArmor(Transform parent, ArmorStyle style)
        {
            string name, desc, model;
            float mass, breakImpulse, protection, drag;
            Vector3 size;
            switch (style)
            {
                case ArmorStyle.CarDoor:
                    name = "Дверь от машины"; desc = "Настоящая, с ручкой. Тяжёлая и крепкая: прикрывает лучше всех.";
                    mass = 22f; breakImpulse = 2500f; protection = 0.25f; drag = 0.08f; size = new Vector3(1.1f, 0.7f, 0.08f); model = "car_door"; break;
                default:
                    name = "Оконная рама"; desc = "Деревянная, без стекла. Лёгкая, ветер свистит насквозь — почти не тормозит.";
                    mass = 9f; breakImpulse = 1200f; protection = 0.12f; drag = 0.02f; size = new Vector3(1.0f, 0.8f, 0.06f); model = "WindowReplacement_Palette"; break;
            }
            var p = MakePart<ArmorPart>(name, PartCategory.Armor, parent, RandomYardPos(), mass, breakImpulse, 0f, desc, drag);
            var t = p.transform;
            p.gameObject.AddComponent<BoxCollider>().size = size;
            switch (style)
            {
                case ArmorStyle.CarDoor:
                {
                    Color paint = new Color(0.75f, 0.2f, 0.15f);
                    Prim(PrimitiveType.Cube, "Дверь", t, new Vector3(0f, -0.1f, 0f), new Vector3(size.x, size.y - 0.2f, size.z), paint, false);
                    Prim(PrimitiveType.Cube, "Окно", t, new Vector3(0.05f, size.y / 2f - 0.1f, 0f), new Vector3(size.x - 0.2f, 0.2f, size.z * 0.5f), new Color(0.5f, 0.65f, 0.75f), false);
                    Prim(PrimitiveType.Cube, "Ручка", t, new Vector3(size.x / 2f - 0.15f, 0.05f, size.z / 2f + 0.01f), new Vector3(0.14f, 0.03f, 0.03f), Color.gray, false);
                    break;
                }
                default:
                {
                    Color wood = new Color(0.92f, 0.92f, 0.88f);
                    float bar = 0.07f;
                    for (int s = -1; s <= 1; s += 2)
                    {
                        Prim(PrimitiveType.Cube, "Брус", t, new Vector3(0f, s * (size.y - bar) / 2f, 0f), new Vector3(size.x, bar, size.z), wood, false);
                        Prim(PrimitiveType.Cube, "Брус", t, new Vector3(s * (size.x - bar) / 2f, 0f, 0f), new Vector3(bar, size.y, size.z), wood, false);
                    }
                    Prim(PrimitiveType.Cube, "Переплёт", t, Vector3.zero, new Vector3(0.04f, size.y, size.z * 0.8f), wood * 0.95f, false);
                    Prim(PrimitiveType.Cube, "Переплёт", t, Vector3.zero, new Vector3(size.x, 0.04f, size.z * 0.8f), wood * 0.95f, false);
                    break;
                }
            }
            // Крепится нижним краем: точка борта — низ щита, сам он встаёт вверх вдоль корпуса.
            Vector3 lift = new Vector3(0f, size.y / 2f, 0f);
            foreach (Transform child in t) child.localPosition += lift;
            p.GetComponent<BoxCollider>().center = lift;
            Set(p, "protection", protection);
            DressPart(p, SideFolder, model, lift, size);
        }

        static void MakeUmbrella(Transform parent)
        {
            var p = MakePart<UmbrellaPart>("Зонт (пробел)", PartCategory.Special, parent, RandomYardPos(), 3f, 300f, 0f, "Мягкая посадка после трамплина.", 0.2f);
            var col = p.gameObject.AddComponent<CapsuleCollider>();
            col.height = 1.4f; col.radius = 0.15f; col.center = new Vector3(0f, 0.6f, 0f);
            Prim(PrimitiveType.Cylinder, "Ручка", p.transform, new Vector3(0f, 0.6f, 0f), new Vector3(0.05f, 0.7f, 0.05f), Color.black, false);
            var canopy = Prim(PrimitiveType.Sphere, "Купол", p.transform, new Vector3(0f, 1.3f, 0f), new Vector3(0.5f, 0.25f, 0.5f), new Color(0.9f, 0.2f, 0.5f), false);
            Set(p, "canopy", canopy.transform);
            // Модель зонта: целиком в «купол» — раскрытие растягивает её вширь (закрытый — узкий).
            var model = ModelFitter.Find(SpecialFolder, "umbrella");
            if (model != null)
            {
                foreach (var r in p.GetComponentsInChildren<MeshRenderer>(true)) { var mf = r.GetComponent<MeshFilter>(); Object.DestroyImmediate(r); if (mf) Object.DestroyImmediate(mf); }
                var dome = new GameObject("Зонт_вид").transform;
                dome.SetParent(p.transform, false);
                dome.localScale = new Vector3(0.5f, 1f, 0.5f);   // UmbrellaPart двигает масштаб купола 0.5 ↔ 2
                var inner = new GameObject("Модель_зонта").transform;
                inner.SetParent(dome, false);
                inner.localScale = new Vector3(0.5f, 1f, 0.5f);  // раскрытый (×2) — как в файле
                ModelFitter.Attach(model, inner, new Vector3(0f, 0.75f, 0f), new Vector3(1.2f, 1.5f, 1.2f), true);
                Set(p, "canopy", dome);
            }
        }

        static void MakeDuck(Transform parent)
        {
            var p = MakePart<Part>("Утка-клаксон (бесполезна)", PartCategory.Special, parent, RandomYardPos(), 1f, 99999f, 300f, "Ничего не делает. Но все её берут.");
            p.gameObject.AddComponent<SphereCollider>().radius = 0.3f;
            Prim(PrimitiveType.Sphere, "Тело", p.transform, Vector3.zero, Vector3.one * 0.6f, Color.yellow, false);
            Prim(PrimitiveType.Sphere, "Голова", p.transform, new Vector3(0f, 0.35f, 0.2f), Vector3.one * 0.35f, Color.yellow, false);
            Prim(PrimitiveType.Cube, "Клюв", p.transform, new Vector3(0f, 0.33f, 0.4f), new Vector3(0.15f, 0.06f, 0.15f), new Color(1f, 0.5f, 0f), false);
            DressPart(p, SpecialFolder, "duck", new Vector3(0f, 0.1f, 0.08f), new Vector3(0.6f, 0.8f, 0.76f));
        }

        // ---------------- Хелперы ----------------

        static T MakePart<T>(string name, PartCategory cat, Transform parent, Vector3 pos,
                             float mass, float breakImpulse, float buoyancy,
                             string description = "", float airDrag = 0.05f) where T : Part
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.position = pos;
            var rb = go.AddComponent<Rigidbody>();
            rb.mass = mass;
            rb.interpolation = RigidbodyInterpolation.Interpolate;

            var part = go.AddComponent<T>();
            var so = new SerializedObject(part);
            so.FindProperty("category").enumValueIndex = (int)cat;
            so.FindProperty("displayName").stringValue = name;
            so.FindProperty("mass").floatValue = mass;
            so.FindProperty("breakImpulse").floatValue = breakImpulse;
            so.FindProperty("buoyancy").floatValue = buoyancy;
            so.FindProperty("description").stringValue = description;
            so.FindProperty("airDrag").floatValue = airDrag;
            so.ApplyModifiedPropertiesWithoutUndo();
            return part;
        }

        static AttachPoint Point(Transform owner, string name, Vector3 localPos,
                                 SlotType slot = SlotType.Body, bool steers = false)
        {
            var go = new GameObject("AP_" + name);
            go.transform.SetParent(owner, false);
            go.transform.localPosition = localPos;
            var ap = go.AddComponent<AttachPoint>();
            SetEnum(ap, "slot", (int)slot);
            Set(ap, "steers", steers);
            return ap;
        }

        /// <summary>
        /// Выпуклый MeshCollider из меша цилиндра, ось — вдоль X (как у шины).
        /// Готового коллайдера-цилиндра в Unity нет: у примитива-цилиндра коллайдер на самом деле капсула.
        /// </summary>
        static void AddTireCollider(Transform parent, float radius, float width)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            go.name = "Коллайдер_шины";
            go.transform.SetParent(parent, false);
            go.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);
            go.transform.localScale = new Vector3(radius * 2f, width / 2f, radius * 2f);   // меш цилиндра: Ø1, высота 2
            var mesh = go.GetComponent<MeshFilter>().sharedMesh;
            Object.DestroyImmediate(go.GetComponent<Collider>());
            Object.DestroyImmediate(go.GetComponent<MeshRenderer>());
            Object.DestroyImmediate(go.GetComponent<MeshFilter>());
            var mc = go.AddComponent<MeshCollider>();
            mc.sharedMesh = mesh;
            mc.convex = true;   // выпуклый — обязателен для Rigidbody
        }

        static GameObject Box(string name, Transform parent, Vector3 pos, Vector3 size, Color color)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.position = pos;
            go.transform.localScale = size;
            Paint(go, color);
            return go;
        }

        static GameObject Prim(PrimitiveType type, string name, Transform parent, Vector3 localPos,
                               Vector3 scale, Color color, bool keepCollider = true)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = scale;
            if (!keepCollider) Object.DestroyImmediate(go.GetComponent<Collider>());
            Paint(go, color);
            return go;
        }

        static void Paint(GameObject go, Color color)
        {
            go.GetComponent<Renderer>().sharedMaterial = LookBuilder.MakeMaterial(color);
        }

        static void Set(Object target, string field, Object value)
        {
            var so = new SerializedObject(target);
            so.FindProperty(field).objectReferenceValue = value;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        static void Set(Object target, string field, float value)
        {
            var so = new SerializedObject(target);
            so.FindProperty(field).floatValue = value;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        static void Set(Object target, string field, bool value)
        {
            var so = new SerializedObject(target);
            so.FindProperty(field).boolValue = value;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        static void SetString(Object target, string field, string value)
        {
            var so = new SerializedObject(target);
            so.FindProperty(field).stringValue = value;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        static void SetList(Object target, string field, List<Part> items)
        {
            var so = new SerializedObject(target);
            var prop = so.FindProperty(field);
            prop.arraySize = items.Count;
            for (int i = 0; i < items.Count; i++) prop.GetArrayElementAtIndex(i).objectReferenceValue = items[i];
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>Поле-enum по номеру значения (порядок как в объявлении enum).</summary>
        static void SetEnum(Object target, string field, int index)
        {
            var so = new SerializedObject(target);
            so.FindProperty(field).enumValueIndex = index;
            so.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
