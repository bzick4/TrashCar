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
        const string MenuPath = "Assets/" + GameSession.MenuScene + ".unity";
        const string TrackPath = "Assets/" + GameSession.TrackScene + ".unity";

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

        static void BuildTrackScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            Random.InitState(42);

            BuildTrack();
            var player = BuildPlayer(new Vector3(0f, 1.2f, -32f));
            SetupCamera(player);
            new GameObject("RaceTimer").AddComponent<RaceTimer>();
            new GameObject("InGameMenu").AddComponent<InGameMenu>();
            new GameObject("JunkNet").AddComponent<JunkNet>();   // в соло сам выключится
            BuildJunkyard();

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
        const float TrackWidth = 42f, LaneWidth = 14f;
        const float RiverStart = 60f, RiverEnd = 70f;
        const float TrackStart = -40f, TrackEnd = 145f;

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

        /// <summary>Плита земли от z0 до z1 толщиной 4 м (верх на y = 0).</summary>
        static void Slab(string name, Transform parent, float z0, float z1, Color color) =>
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

            // Корпуса
            MakeBathtub(yard, new Vector3(-5f, 1f, -15f));
            MakePallet(yard, new Vector3(6f, 1f, -15f));
            MakeCart(yard, new Vector3(-10f, 1f, -7f));
            MakeTable(yard, new Vector3(10f, 1f, -7f));
            MakeWardrobe(yard, new Vector3(0f, 1f, -24f));

            // Колёса
            Color rubber = new Color(0.15f, 0.15f, 0.15f);
            for (int i = 0; i < 4; i++)
                MakeWheel(yard, "Колесо тележки", "Скрипит, но катится.", 6f, 0.4f, 450f, 400f, rubber);
            for (int i = 0; i < 2; i++)
                MakeWheel(yard, "Колесо квадроцикла", "Держит дорогу. Почти.", 11f, 0.45f, 900f, 750f, new Color(0.35f, 0.25f, 0.15f));
            for (int i = 0; i < 2; i++)
                MakeWheel(yard, "Люк", "Чугунный. Тяжёлый, цепкий и не отваливается никогда.", 40f, 0.35f, 650f, 900f,
                          new Color(0.3f, 0.3f, 0.32f), WheelStyle.Manhole, thickness: 0.04f, breakImpulse: 3000f, rolling: 5f);
            for (int i = 0; i < 4; i++)
                MakeWheel(yard, "Велосипедное колесо", "Большое и лёгкое. Катится легко, держит плохо, отлетает от чиха.", 2f, 0.6f, 400f, 250f,
                          rubber, WheelStyle.Bicycle, thickness: 0.025f, breakImpulse: 250f, rolling: 1.5f);
            for (int i = 0; i < 4; i++)
                MakeWheel(yard, "Спасательный круг", "В воде — лучшее колесо. На дороге мягкий и вязкий: нужен мотор помощнее.", 2f, 0.4f, 250f, 220f,
                          new Color(1f, 0.45f, 0.1f), WheelStyle.Ring, thickness: 0.12f, rolling: 8f, buoyancy: 1500f, paddle: 1.6f);

            // Остальное
            MakeEngine(yard, "Мотор газонокосилки", "Классика. Заводится с третьего рывка.", 15f, 4000f, 450f, new Color(0.85f, 0.15f, 0.1f), new Vector3(0.6f, 0.5f, 0.6f));
            MakeEngine(yard, "Мотор от стиралки", "Тяжёлый и тихий. На отжиме неплох.", 32f, 3000f, 800f, Color.white, new Vector3(0.8f, 0.8f, 0.8f));
            MakeEngine(yard, "Бензопила", "Злая, лёгкая и отваливается при каждом чихе.", 7f, 7500f, 220f, new Color(1f, 0.55f, 0f), new Vector3(0.3f, 0.35f, 0.9f));
            MakeFan(yard);
            MakeVacuum(yard);
            MakeSteering(yard);
            MakeUmbrella(yard);
            MakeDuck(yard);

            // Кресла для пассажиров
            for (int i = 0; i < 2; i++) MakeSeat(yard, SeatStyle.Office);
            for (int i = 0; i < 2; i++) MakeSeat(yard, SeatStyle.Car);
            MakeSeat(yard, SeatStyle.Toilet);

            DistributeIntoPiles(yard, BuildPiles());
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
        };

        static List<JunkPile> BuildPiles()
        {
            var root = new GameObject("Кучи_мусора").transform;
            var list = new List<JunkPile>();
            for (int i = 0; i < PilePositions.Length; i++)
                list.Add(MakePile(root, "Куча_" + (i + 1), PilePositions[i]));
            return list;
        }

        static JunkPile MakePile(Transform parent, string name, Vector3 pos)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.position = pos;
            var t = go.transform;

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

            foreach (var part in loose)
            {
                bool hull = part.GetComponent<Vehicle>() != null;
                if (hull && (part.DisplayName == "Ванна" || part.DisplayName == "Паллет")) continue;

                int k = Random.Range(0, piles.Count);
                Vector3 pilePos = piles[k].transform.position;
                if (hull || Random.value < 0.5f)
                {
                    stashes[k].Add(part);
                    part.transform.position = pilePos + Vector3.up * 0.5f;   // в редакторе видно, в какой куче
                }
                else
                {
                    float a = Random.Range(0f, Mathf.PI * 2f), d = Random.Range(2.6f, 4.5f);
                    part.transform.position = pilePos + new Vector3(Mathf.Cos(a) * d, 1.2f, Mathf.Sin(a) * d);
                }
            }

            for (int i = 0; i < piles.Count; i++) SetList(piles[i], "stash", stashes[i]);
        }

        static Vector3 RandomYardPos() =>
            new Vector3(Random.Range(-12f, 12f), 1.5f, Random.Range(-28f, -3f));

        // ---------------- Корпуса ----------------
        // Начало координат корпуса — его «пол» (там, где стоят руль и моторы), если не сказано иное.

        static void MakeBathtub(Transform parent, Vector3 pos)
        {
            var p = MakePart<Part>("Ванна", PartCategory.Hull, parent, pos, 60f, 99999f, 5000f, "Чугунная. Плавает, как ни странно.", 0.45f);
            Color white = new Color(0.95f, 0.95f, 0.97f);
            var t = p.transform;
            Prim(PrimitiveType.Cube, "Дно", t, new Vector3(0f, -0.15f, 0f), new Vector3(2f, 0.3f, 3f), white);
            Prim(PrimitiveType.Cube, "Борт_Л", t, new Vector3(-0.92f, 0.2f, 0f), new Vector3(0.16f, 0.5f, 3f), white);
            Prim(PrimitiveType.Cube, "Борт_П", t, new Vector3(0.92f, 0.2f, 0f), new Vector3(0.16f, 0.5f, 3f), white);
            Prim(PrimitiveType.Cube, "Борт_Пер", t, new Vector3(0f, 0.2f, 1.42f), new Vector3(2f, 0.5f, 0.16f), white);
            Prim(PrimitiveType.Cube, "Борт_Зад", t, new Vector3(0f, 0.2f, -1.42f), new Vector3(2f, 0.5f, 0.16f), white);
            AddHullPoints(t, 2f, 3f, floorY: 0f, topY: 0.5f);
            MakeVehicle(p, new Vector3(0f, 0.9f, -0.3f));
        }

        static void MakePallet(Transform parent, Vector3 pos)
        {
            var p = MakePart<Part>("Паллет", PartCategory.Hull, parent, pos, 20f, 99999f, 0f, "Лёгкий и плоский. Плавать не умеет.", 0.25f);
            Color wood = new Color(0.75f, 0.6f, 0.4f);
            var t = p.transform;
            Prim(PrimitiveType.Cube, "Основа", t, new Vector3(0f, -0.1f, 0f), new Vector3(1.6f, 0.2f, 2.4f), wood);
            for (int i = -1; i <= 1; i++)
                Prim(PrimitiveType.Cube, "Доска", t, new Vector3(i * 0.6f, 0.05f, 0f), new Vector3(0.3f, 0.1f, 2.4f), wood * 0.9f);
            AddHullPoints(t, 1.6f, 2.4f, floorY: 0.1f, topY: 0.5f);
            MakeVehicle(p, new Vector3(0f, 1.1f, -0.3f));
        }

        /// <summary>Магазинная тележка: уже с колёсиками (прикручены прямо в сцене), но хлипкая.</summary>
        static void MakeCart(Transform parent, Vector3 pos)
        {
            const float w = 0.7f, l = 1.0f, h = 0.55f;
            var p = MakePart<Part>("Тележка", PartCategory.Hull, parent, pos, 12f, 99999f, 0f,
                                   "Магазинная. Уже с колёсиками, но они отлетают на первой кочке.", 0.12f);
            Color wire = new Color(0.75f, 0.77f, 0.8f);
            var t = p.transform;
            const float bar = 0.03f;
            Prim(PrimitiveType.Cube, "Дно", t, new Vector3(0f, -bar / 2f, 0f), new Vector3(w, bar, l), wire);
            Prim(PrimitiveType.Cube, "Стенка_Л", t, new Vector3(-w / 2f, h / 2f, 0f), new Vector3(bar, h, l), wire);
            Prim(PrimitiveType.Cube, "Стенка_П", t, new Vector3(w / 2f, h / 2f, 0f), new Vector3(bar, h, l), wire);
            Prim(PrimitiveType.Cube, "Стенка_Пер", t, new Vector3(0f, h / 2f, l / 2f), new Vector3(w, h, bar), wire);
            Prim(PrimitiveType.Cube, "Стенка_Зад", t, new Vector3(0f, h * 0.35f, -l / 2f), new Vector3(w, h * 0.7f, bar), wire);
            // Решётка — тонкие прутья на боках, для вида.
            for (int i = 1; i < 5; i++)
            {
                float z = -l / 2f + i * l / 5f;
                Prim(PrimitiveType.Cube, "Прут", t, new Vector3(-w / 2f - 0.005f, h / 2f, z), new Vector3(0.01f, h, 0.015f), wire * 0.8f, false);
                Prim(PrimitiveType.Cube, "Прут", t, new Vector3(w / 2f + 0.005f, h / 2f, z), new Vector3(0.01f, h, 0.015f), wire * 0.8f, false);
            }
            Prim(PrimitiveType.Cube, "Ручка", t, new Vector3(0f, h + 0.1f, -l / 2f - 0.15f), new Vector3(w, 0.04f, 0.04f), new Color(0.9f, 0.15f, 0.1f));
            Prim(PrimitiveType.Cube, "Рама", t, new Vector3(0f, -0.2f, 0f), new Vector3(w * 0.9f, 0.04f, l * 0.9f), wire * 0.7f);

            AddHullPoints(t, w, l, floorY: 0f, topY: h, wheelY: -0.3f, wheelsUnder: true, wheelPairs: 2, engines: 1, driverZ: -0.15f);
            MakeVehicle(p, new Vector3(0f, 0.9f, -0.15f));

            // Колёсики уже стоят: прикручиваем их прямо в сцене, машина зарегистрирует их на старте.
            foreach (var slot in new[] { "AP_Колесо_ПЛ", "AP_Колесо_ПП", "AP_Колесо_ЗЛ", "AP_Колесо_ЗП" })
            {
                var wheel = MakeWheel(parent, "Колёсико тележки", "Крутится во все стороны, кроме нужной.", 1.5f, 0.12f, 300f, 300f,
                                      new Color(0.2f, 0.2f, 0.22f), thickness: 0.04f, breakImpulse: 200f, rolling: 4f);
                PreMount(wheel, t.Find(slot));
            }
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
            AddHullPoints(t, w, l, floorY: 0f, topY: 0.4f);
            MakeVehicle(p, new Vector3(0f, 0.95f, -0.3f));
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
            AddHullPoints(t, w, l, floorY: 0f, topY: 0.4f, wheelY: -h / 2f);
            MakeVehicle(p, new Vector3(0f, 1.0f, -0.3f));
        }

        /// <summary>
        /// Места крепления на корпусе. Порядок точек важен для сети (индекс в Part.Points) — у всех одна сцена, так что ок.
        /// floorY — высота «пола», где стоят руль и моторы; topY — высота верхних точек кузова.
        /// wheelsUnder — колёса под корпусом (тележка), иначе — по бокам снаружи.
        /// </summary>
        static void AddHullPoints(Transform hull, float width, float length, float floorY, float topY,
                                  float wheelY = 0f, bool wheelsUnder = false, int wheelPairs = 3, int engines = 2,
                                  float driverZ = -0.3f)
        {
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

            // Руль — перед сиденьем (сиденье примерно на z = -0.3).
            Point(hull, "Руль", new Vector3(0f, floorY + 0.1f, Mathf.Min(0.6f, length / 2f - 0.15f)), SlotType.Steering);

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

            // Салон: кресла внутри корпуса, на полу рядом с водителем (он на z = driverZ, по центру).
            // Передняя пара — по бокам от руля, задняя — по бокам от водителя. Только если корпус
            // достаточно широкий: в шкаф и тележку рядом с водителем никто не влезет.
            if (width >= 1.4f)
            {
                float innerX = Mathf.Min(width / 2f - 0.3f, 0.6f);
                float frontZ = Mathf.Min(0.6f, length / 2f - 0.15f) - 0.15f;
                Point(hull, "Салон_ПЛ", new Vector3(-innerX, floorY, frontZ), SlotType.Seat);
                Point(hull, "Салон_ПП", new Vector3(innerX, floorY, frontZ), SlotType.Seat);
                Point(hull, "Салон_ЗЛ", new Vector3(-innerX, floorY, driverZ), SlotType.Seat);
                Point(hull, "Салон_ЗП", new Vector3(innerX, floorY, driverZ), SlotType.Seat);
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

        enum WheelStyle { Tire, Bicycle, Manhole, Ring }

        /// <param name="thickness">Половина ширины шины.</param>
        static WheelPart MakeWheel(Transform parent, string name, string description, float mass, float radius,
                                   float traction, float grip, Color color, WheelStyle style = WheelStyle.Tire,
                                   float thickness = 0.12f, float breakImpulse = 600f, float rolling = 3f,
                                   float buoyancy = 0f, float paddle = 0.5f)
        {
            var p = MakePart<WheelPart>(name, PartCategory.Drive, parent, RandomYardPos(), mass, breakImpulse, buoyancy, description);
            // Коллайдер в форме шины (плоский цилиндр): свободное колесо катится как колесо, а не как мяч.
            // Размер как у видимой шины. На машине машину держит луч-подвеска,
            // а коллайдер работает упором только при жёстком приземлении.
            AddTireCollider(p.transform, radius, Mathf.Max(thickness * 2f, 0.05f));

            var pivot = new GameObject("Pivot").transform;
            pivot.SetParent(p.transform, false);
            switch (style)
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
                               float breakImpulse, Color color, Vector3 size)
        {
            var p = MakePart<EnginePart>(name, PartCategory.Drive, parent, RandomYardPos(), mass, breakImpulse, 0f, description);
            p.gameObject.AddComponent<BoxCollider>().size = size;
            var body = Prim(PrimitiveType.Cube, "Корпус", p.transform, Vector3.zero, size, color, false);
            Prim(PrimitiveType.Cylinder, "Выхлоп", body.transform, new Vector3(0.3f, 0.6f, -0.3f), new Vector3(0.15f, 0.3f, 0.15f), Color.gray, false);
            Set(p, "power", power);
            Set(p, "shaker", body.transform);
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
        }

        static void MakeSteering(Transform parent)
        {
            var p = MakePart<SteeringPart>("Руль", PartCategory.Control, parent, RandomYardPos(), 4f, 400f, 0f, "Без него колёса смотрят строго вперёд.");
            p.gameObject.AddComponent<BoxCollider>().size = new Vector3(0.6f, 0.7f, 0.6f);
            Prim(PrimitiveType.Cylinder, "Стойка", p.transform, new Vector3(0f, 0f, 0f), new Vector3(0.1f, 0.3f, 0.1f), Color.gray, false);
            var wheel = new GameObject("Баранка").transform;
            wheel.SetParent(p.transform, false);
            wheel.localPosition = new Vector3(0f, 0.32f, 0f);
            Prim(PrimitiveType.Cylinder, "Обод", wheel, Vector3.zero, new Vector3(0.55f, 0.03f, 0.55f), Color.black, false);
            Prim(PrimitiveType.Cube, "Спица", wheel, new Vector3(0f, 0.04f, 0f), new Vector3(0.5f, 0.04f, 0.08f), Color.red, false);
            Set(p, "wheelVisual", wheel);
        }

        static void MakeUmbrella(Transform parent)
        {
            var p = MakePart<UmbrellaPart>("Зонт (пробел)", PartCategory.Special, parent, RandomYardPos(), 3f, 300f, 0f, "Мягкая посадка после трамплина.", 0.2f);
            var col = p.gameObject.AddComponent<CapsuleCollider>();
            col.height = 1.4f; col.radius = 0.15f; col.center = new Vector3(0f, 0.6f, 0f);
            Prim(PrimitiveType.Cylinder, "Ручка", p.transform, new Vector3(0f, 0.6f, 0f), new Vector3(0.05f, 0.7f, 0.05f), Color.black, false);
            var canopy = Prim(PrimitiveType.Sphere, "Купол", p.transform, new Vector3(0f, 1.3f, 0f), new Vector3(0.5f, 0.25f, 0.5f), new Color(0.9f, 0.2f, 0.5f), false);
            Set(p, "canopy", canopy.transform);
        }

        static void MakeDuck(Transform parent)
        {
            var p = MakePart<Part>("Утка-клаксон (бесполезна)", PartCategory.Special, parent, RandomYardPos(), 1f, 99999f, 300f, "Ничего не делает. Но все её берут.");
            p.gameObject.AddComponent<SphereCollider>().radius = 0.3f;
            Prim(PrimitiveType.Sphere, "Тело", p.transform, Vector3.zero, Vector3.one * 0.6f, Color.yellow, false);
            Prim(PrimitiveType.Sphere, "Голова", p.transform, new Vector3(0f, 0.35f, 0.2f), Vector3.one * 0.35f, Color.yellow, false);
            Prim(PrimitiveType.Cube, "Клюв", p.transform, new Vector3(0f, 0.33f, 0.4f), new Vector3(0.15f, 0.06f, 0.15f), new Color(1f, 0.5f, 0f), false);
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

        static void Point(Transform owner, string name, Vector3 localPos,
                          SlotType slot = SlotType.Body, bool steers = false)
        {
            var go = new GameObject("AP_" + name);
            go.transform.SetParent(owner, false);
            go.transform.localPosition = localPos;
            var ap = go.AddComponent<AttachPoint>();
            SetEnum(ap, "slot", (int)slot);
            Set(ap, "steers", steers);
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
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            go.GetComponent<Renderer>().sharedMaterial = new Material(shader) { color = color };
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
