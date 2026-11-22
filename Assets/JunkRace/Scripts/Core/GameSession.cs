using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace JunkRace
{
    public enum GameMode { Solo, Coop, UltraCoop }

    /// <summary>Вид гонки: спринт — от свалки до финиша; кольцо — овал на хвостике от свалки, несколько кругов.</summary>
    public enum RaceKind : byte { Sprint, Circuit }

    /// <summary>Длина гонки (спринта) или одного круга (кольца).</summary>
    public enum RaceSize : byte { Short, Medium, Long }

    /// <summary>Форма кольца: овал или восьмёрка (ветки посередине пересекаются — одна по мосту над другой).</summary>
    public enum RaceShape : byte { Oval, Eight }

    /// <summary>Биом трассы: все вперемешку (рандом) или только один. Реки и тоннели есть в любом.</summary>
    public enum RaceBiome : byte { Random, Forest, Desert, Mountains, Snow }

    /// <summary>Общие настройки сессии, живут между сценами.</summary>
    public static class GameSession
    {
        public const string MenuScene = "JunkRaceMainMenu";
        public const string TrackScene = "JunkRaceTestTrack";
        public const int CoopMaxPlayers = 4;

        public static GameMode Mode = GameMode.Solo;

        // ---- Настройки гонки (соло — свои, в коопе — от хоста) ----
        public const int MaxLaps = 5;

        // Читаем сохранённое лениво: PlayerPrefs нельзя трогать из статического конструктора (его может вызвать сериализация).
        static bool raceLoaded;
        static RaceKind kind;
        static RaceSize size;
        static int laps;
        static RaceShape shape;
        static RaceBiome biome;

        static void LoadRace()
        {
            if (raceLoaded) return;
            raceLoaded = true;
            kind = (RaceKind)Mathf.Clamp(PlayerPrefs.GetInt("jr_race_kind", 0), 0, 1);
            size = (RaceSize)Mathf.Clamp(PlayerPrefs.GetInt("jr_race_size", 1), 0, 2);
            laps = Mathf.Clamp(PlayerPrefs.GetInt("jr_race_laps", 3), 1, MaxLaps);
            shape = (RaceShape)Mathf.Clamp(PlayerPrefs.GetInt("jr_race_shape", 0), 0, 1);
            biome = (RaceBiome)Mathf.Clamp(PlayerPrefs.GetInt("jr_race_biome", 0), 0, 4);
        }

        public static RaceKind Kind { get { LoadRace(); return kind; } set { LoadRace(); kind = value; } }
        public static RaceSize Size { get { LoadRace(); return size; } set { LoadRace(); size = value; } }
        public static int Laps { get { LoadRace(); return laps; } set { LoadRace(); laps = Mathf.Clamp(value, 1, MaxLaps); } }
        public static RaceShape Shape { get { LoadRace(); return shape; } set { LoadRace(); shape = value; } }
        /// <summary>Биом (в обучении — всегда лес).</summary>
        public static RaceBiome Biome { get { LoadRace(); return IsTutorial ? RaceBiome.Forest : biome; } set { LoadRace(); biome = value; } }

        /// <summary>Кольцо в этом заезде (в обучении — всегда спринт).</summary>
        public static bool IsCircuit => Kind == RaceKind.Circuit && !IsTutorial;

        public static void SaveRace()
        {
            PlayerPrefs.SetInt("jr_race_kind", (int)Kind);
            PlayerPrefs.SetInt("jr_race_size", (int)Size);
            PlayerPrefs.SetInt("jr_race_laps", Laps);
            PlayerPrefs.SetInt("jr_race_shape", (int)Shape);
            PlayerPrefs.SetInt("jr_race_biome", (int)biome);
            PlayerPrefs.Save();
        }

        /// <summary>Настройки с хоста (клиент): только на этот заезд, свои сохранённые не трогаем.</summary>
        public static void ApplyRace(RaceKind kind, RaceSize size, int laps, RaceShape shape, RaceBiome biome)
        {
            Biome = biome;
            Kind = kind;
            Size = size;
            Laps = Mathf.Clamp(laps, 1, MaxLaps);
            Shape = shape;
        }

        static readonly string[] SprintNames = { "Быстрый спринт", "Средний спринт", "Долгий спринт" };
        static readonly string[] CircuitNames = { "Быстрое кольцо", "Среднее кольцо", "Большое кольцо" };
        static readonly string[] SprintTimes = { "~2 мин", "3–4 мин", "5–6 мин" };
        static readonly string[] CircuitTimes = { "1–2 мин", "2,5–3 мин", "4–5 мин" };

        public static string SizeName(RaceKind kind, RaceSize size) => (kind == RaceKind.Sprint ? SprintNames : CircuitNames)[(int)size];
        /// <summary>Сколько примерно ехать: спринт — весь заезд, кольцо — один круг.</summary>
        public static string SizeTime(RaceKind kind, RaceSize size) => (kind == RaceKind.Sprint ? SprintTimes : CircuitTimes)[(int)size];

        /// <summary>«Среднее кольцо · 3 круга» — для списка серверов, лобби и HUD.</summary>
        public static string RaceLabel(RaceKind kind, RaceSize size, int laps, RaceShape shape, RaceBiome biome) =>
            SizeName(kind, size) + (kind == RaceKind.Circuit ? (shape == RaceShape.Eight ? " · восьмёрка" : "") + $" · {laps} {LapWord(laps)}" : "")
            + " · " + BiomeName(biome);

        public static string RaceLabel() => RaceLabel(Kind, Size, Laps, Shape, Biome);

        static readonly string[] BiomeNames = { "Рандом", "Лес", "Пустыня", "Горы", "Снег" };
        public static string BiomeName(RaceBiome b) => BiomeNames[(int)b];

        public static string LapWord(int n) => n % 10 == 1 && n % 100 != 11 ? "круг" : n % 10 >= 2 && n % 10 <= 4 && (n % 100 < 10 || n % 100 >= 20) ? "круга" : "кругов";

        /// <summary>Сообщение, которое меню покажет после возврата (например, «Хост вышел»).</summary>
        public static string PendingMessage;

        public static string PlayerName
        {
            get => PlayerPrefs.GetString("jr_player_name", "");
            set { PlayerPrefs.SetString("jr_player_name", value); PlayerPrefs.Save(); }
        }

        /// <summary>Тест-комната разработчика: все детали разложены рядами, сборка без таймера. Только соло.</summary>
        public static bool DevRoom;

        /// <summary>
        /// Тест-комната есть только в редакторе и в Development Build. В обычной сборке игры её нет:
        /// ни кнопки в меню, ни раскладки, ни подписей.
        /// </summary>
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        public const bool DevRoomAvailable = true;
#else
        public const bool DevRoomAvailable = false;
#endif

        /// <summary>Сейчас тест-комната (в сетевой игре — никогда).</summary>
        public static bool IsDevRoom
        {
            get
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                if (!DevRoom) return false;
                var nm = NetworkManager.Singleton;
                return nm == null || !nm.IsListening;
#else
                return false;
#endif
            }
        }

        /// <summary>Обучение: одиночная игра по шагам (Tutorial), без таймера сборки и с короткой трассой.</summary>
        public static bool Tutorial;
        public static bool IsTutorial => Tutorial && Mode == GameMode.Solo;

        /// <summary>Обучение пройдено или пропущено — при запуске игры больше не начинается само.</summary>
        public static bool TutorialDone
        {
            get => PlayerPrefs.GetInt("jr_tutorial_done", 0) == 1;
            set { PlayerPrefs.SetInt("jr_tutorial_done", value ? 1 : 0); PlayerPrefs.Save(); }
        }

        public static void StartTutorial()
        {
            Mode = GameMode.Solo;
            DevRoom = false;
            Tutorial = true;
            SceneManager.LoadScene(TrackScene);
        }

        public static void StartSolo()
        {
            Mode = GameMode.Solo;
            DevRoom = false;
            Tutorial = false;
            SceneManager.LoadScene(TrackScene);
        }

        public static void StartDevRoom()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            Mode = GameMode.Solo;
            DevRoom = true;
            Tutorial = false;
            SceneManager.LoadScene(TrackScene);
#endif
        }

        /// <summary>Хост (или соло) может перезапустить заезд.</summary>
        public static bool CanRestart
        {
            get
            {
                var nm = NetworkManager.Singleton;
                return nm == null || !nm.IsListening || nm.IsServer;
            }
        }

        /// <summary>
        /// Ещё заезд: трасса заново (новый сид, кучи и детали по-новому). В коопе хост грузит сцену всем
        /// через сетевой менеджер сцен — клиенты перезагружаются вместе с ним.
        /// </summary>
        public static void RestartTrack()
        {
            if (!CanRestart) return;
            Time.timeScale = 1f;
            var nm = NetworkManager.Singleton;
            if (nm != null && nm.IsListening && nm.SceneManager != null)
                nm.SceneManager.LoadScene(TrackScene, LoadSceneMode.Single);
            else
                SceneManager.LoadScene(SceneManager.GetActiveScene().name);
        }

        /// <summary>Выход в меню из любого места. Глушит сеть и убирает NetworkManager, чтобы в меню не было дубля.</summary>
        public static void ReturnToMenu(string message = null)
        {
            PendingMessage = message;
            DevRoom = false;
            Tutorial = false;
            raceLoaded = false;   // в коопе были настройки хоста — в меню снова свои
            Time.timeScale = 1f;

            var nm = NetworkManager.Singleton;
            if (nm != null)
            {
                if (nm.IsListening) nm.Shutdown();
                Object.Destroy(nm.gameObject);
            }
            SceneManager.LoadScene(MenuScene);
        }
    }
}
