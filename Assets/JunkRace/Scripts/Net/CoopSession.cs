using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Multiplayer;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace JunkRace
{
    /// <summary>Сервер в списке.</summary>
    public struct ServerInfo
    {
        public string Id;
        public string Name;
        public bool HasPassword;
        public bool Started;
        public int Players;
        public int MaxPlayers;
        /// <summary>«Среднее кольцо · 3 круга» (пусто — старый сервер без этого свойства).</summary>
        public string Race;
        public bool IsFull => Players >= MaxPlayers;
    }

    /// <summary>
    /// Кооп через Unity Multiplayer Services: сессии (список, пароль, код) + Relay (игра через интернет без IP и портов).
    /// SDK сам запускает Netcode for GameObjects как хост или клиент — мы только просим создать / войти.
    /// Висит на объекте NetworkManager и живёт между сценами.
    /// </summary>
    [RequireComponent(typeof(NetworkManager), typeof(UnityTransport))]
    public class CoopSession : MonoBehaviour
    {
        /// <summary>
        /// Минимум для игрока. У Unity пароль сессии — от 8 символов, поэтому к введённому добавляем
        /// постоянную приставку (SessionPassword): хост и клиенты делают это одинаково.
        /// </summary>
        public const int MinPasswordLength = 4;
        public const int MaxPasswordLength = 32;
        const string PasswordPrefix = "JunkRace:";

        static string SessionPassword(string password) =>
            string.IsNullOrEmpty(password) ? null : PasswordPrefix + password;
        const string NameKey = "name";
        const string ReadyKey = "ready";
        const string PasswordFlagKey = "pw";
        const string VersionKey = "ver";
        const string RaceKey = "race";   // «вид/длина/круги» — видно в списке серверов

        /// <summary>
        /// Версия для сети: в списке видны только сервера той же версии (иначе старая сборка с itch
        /// зайдёт к новой — и всё развалится). Меняется сама с версией игры (Player Settings → Version);
        /// NetProtocol — поднять вручную, если поменялось что-то сетевое, а версию забыли.
        /// </summary>
        const int NetProtocol = 3;   // 2 — Relay по WebSocket (WSS); 3 — настройки гонки (спринт/кольцо) в состоянии
        public static string NetVersion => Application.version + "/" + NetProtocol;
        const float StartCountdown = 3f;

        public enum State { Idle, Busy, InLobby, InGame }

        public static CoopSession Instance { get; private set; }

        public State Current { get; private set; } = State.Idle;
        public string BusyText { get; private set; } = "";
        public bool IsHost => session != null && session.IsHost;
        public string ServerName => session != null ? session.Name : "";
        public string JoinCode => session != null ? session.Code : "";
        public IReadOnlyList<string> Players => players;
        /// <summary>Готовность игроков лобби — в том же порядке, что Players (хост первым).</summary>
        public IReadOnlyList<bool> PlayersReady => playersReady;
        readonly List<bool> playersReady = new List<bool>();
        /// <summary>Я нажал «Готов» (сразу, не дожидаясь ответа сервиса).</summary>
        public bool LocalReady { get; private set; }
        /// <summary>Все в лобби нажали «Готов».</summary>
        public bool AllReady { get; private set; }

        /// <summary>«Готов» / «Не готов» в лобби: пишем в свойства своего игрока сессии — видят все.</summary>
        public async void SetReady(bool ready)
        {
            if (session == null || Current != State.InLobby) return;
            LocalReady = ready;
            try
            {
                session.CurrentPlayer.SetProperty(ReadyKey, new PlayerProperty(ready ? "1" : "0", VisibilityPropertyOptions.Member));
                await session.SaveCurrentPlayerDataAsync();
            }
            catch (Exception e) { Debug.LogWarning($"[JunkRace] Готовность не сохранилась: {e.Message}"); }
        }
        /// <summary>Секунд до старта, -1 — отсчёта нет.</summary>
        public float CountdownLeft { get; private set; } = -1f;

        public event Action<string> Failed;

        NetworkManager nm;
        ISession session;
        Task<bool> signIn;
        bool startRequested;
        readonly List<string> players = new List<string>();

        void Awake()
        {
            Instance = this;
            nm = GetComponent<NetworkManager>();
            nm.NetworkConfig.ConnectionApproval = false;   // доступ решает сессия (пароль, лимит игроков)
            if (nm.NetworkConfig.NetworkTransport == null) nm.NetworkConfig.NetworkTransport = GetComponent<UnityTransport>();
            // Relay по WebSocket у всех (ПК, браузер, телефон) — иначе браузер не может играть с остальными.
            var transport = GetComponent<UnityTransport>();
            transport.UseWebSockets = true;
            // Очередь пакетов побольше: пока клиент грузит трассу, хост шлёт разом много объектов (детали,
            // кучи, машины). По UDP лишнее просто перепосылалось, а по WebSocket переполнение (128 по умолчанию)
            // рвёт связь с Relay — игрок вылетал в меню при загрузке.
            transport.MaxPacketQueueSize = 4096;
            DontDestroyOnLoad(gameObject);
            SceneManager.activeSceneChanged += OnSceneChanged;
        }

        void OnDestroy()
        {
            SceneManager.activeSceneChanged -= OnSceneChanged;
            Unsubscribe();
            if (Instance == this) Instance = null;
        }

        // ============================ Вход в сервисы ============================

        public Task<bool> EnsureSignedInAsync() => signIn ??= SignInAsync();

        async Task<bool> SignInAsync()
        {
            try
            {
                if (UnityServices.State != ServicesInitializationState.Initialized)
                {
                    var options = new InitializationOptions();
                    // Свой анонимный профиль на каждый запуск — чтобы две копии игры на одном компе были разными игроками.
                    options.SetProfile("p" + UnityEngine.Random.Range(0, 1_000_000));
                    await UnityServices.InitializeAsync(options);
                }
                if (!AuthenticationService.Instance.IsSignedIn)
                    await AuthenticationService.Instance.SignInAnonymouslyAsync();
                return true;
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                signIn = null;   // дадим попробовать ещё раз
                Failed?.Invoke("Не удалось войти в Unity Services. Проект привязан к Unity Cloud? (Edit → Project Settings → Services)");
                return false;
            }
        }

        // ============================ Список серверов ============================

        public async Task<List<ServerInfo>> QueryAsync()
        {
            var list = new List<ServerInfo>();
            if (!await EnsureSignedInAsync()) return list;
            try
            {
                var results = await MultiplayerService.Instance.QuerySessionsAsync(new QuerySessionsOptions());
                foreach (var s in results.Sessions)
                {
                    if (VersionOf(s.Properties) != NetVersion) continue;   // другая версия игры — не показываем
                    bool hasPassword = s.Properties != null
                        && s.Properties.TryGetValue(PasswordFlagKey, out var flag)
                        && flag != null && flag.Value == "1";
                    list.Add(new ServerInfo
                    {
                        Id = s.Id,
                        Name = s.Name,
                        HasPassword = hasPassword,
                        Started = s.IsLocked,
                        MaxPlayers = s.MaxPlayers,
                        Players = s.MaxPlayers - s.AvailableSlots,
                        Race = RaceLabelOf(s.Properties),
                    });
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[JunkRace] Не удалось получить список серверов: {e.Message}");
            }
            return list;
        }

        // ============================ Создать / войти ============================

        public async void Host(string playerName, string serverName, string password)
        {
            if (Current != State.Idle) return;
            SetBusy("Создаём сервер...");
            if (!await EnsureSignedInAsync()) { Current = State.Idle; return; }

            try
            {
                bool hasPassword = !string.IsNullOrEmpty(password);
                var options = new SessionOptions
                {
                    Name = string.IsNullOrWhiteSpace(serverName) ? $"Сервер {playerName}" : serverName.Trim(),
                    MaxPlayers = GameSession.CoopMaxPlayers,
                    IsPrivate = false,
                    Password = SessionPassword(password),
                    PlayerProperties = NameProperty(playerName),
                    SessionProperties = new Dictionary<string, SessionProperty>
                    {
                        { PasswordFlagKey, new SessionProperty(hasPassword ? "1" : "0", VisibilityPropertyOptions.Public) },
                        { VersionKey, new SessionProperty(NetVersion, VisibilityPropertyOptions.Public) },
                        { RaceKey, new SessionProperty(RaceCode(), VisibilityPropertyOptions.Public) },
                    },
                }.WithRelayNetwork().WithNetworkOptions(RelayNet());

                session = await WithRetry("Создаём сервер...", () => MultiplayerService.Instance.CreateSessionAsync(options));
                OnJoined();
            }
            catch (Exception e) { Fail(Friendly(e)); }
        }

        public async void Join(string playerName, ServerInfo info, string password)
        {
            if (Current != State.Idle) return;
            SetBusy($"Подключение к «{info.Name}»...");
            if (!await EnsureSignedInAsync()) { Current = State.Idle; return; }

            try
            {
                session = await WithRetry($"Подключение к «{info.Name}»...", () => MultiplayerService.Instance.JoinSessionByIdAsync(info.Id, JoinOptions(playerName, password)));
                OnJoined();
            }
            catch (Exception e) { Fail(Friendly(e)); }
        }

        public async void JoinByCode(string playerName, string code, string password)
        {
            if (Current != State.Idle) return;
            SetBusy("Подключение по коду...");
            if (!await EnsureSignedInAsync()) { Current = State.Idle; return; }

            try
            {
                session = await WithRetry("Подключение по коду...", () => MultiplayerService.Instance.JoinSessionByCodeAsync(code.Trim().ToUpperInvariant(), JoinOptions(playerName, password)));
                // По коду можно попасть к другой версии (в списке их не видно) — сразу выходим с понятной причиной.
                string theirs = VersionOf(session.Properties);
                if (theirs != NetVersion)
                {
                    await LeaveAsync();
                    Fail($"У хоста другая версия игры ({(string.IsNullOrEmpty(theirs) ? "старая" : theirs.Split('/')[0])}, у тебя {Application.version}). Обновитесь до одной версии.");
                    return;
                }
                OnJoined();
            }
            catch (Exception e) { Fail(Friendly(e)); }
        }

        /// <summary>Сколько игроков в сессии (вошли в лобби, даже если ещё не подключились по сети).</summary>
        public int SessionPlayerCount => session != null ? session.PlayerCount : 0;

        bool forceStart;
        /// <summary>Хост нажал «Начать сейчас», ждём, пока все из лобби подключатся по сети.</summary>
        public bool StartPending => forceStart;

        /// <summary>
        /// Хост: начать, не дожидаясь 4 игроков (для тестов). Но только когда все, кто уже в лобби,
        /// подключились по сети — иначе игрок, зашедший в лобби секунду назад, опоздает к старту.
        /// </summary>
        public void ForceStart()
        {
            if (IsHost && Current == State.InLobby) forceStart = true;
        }

        /// <summary>Все игроки лобби уже на связи (хост — тоже в списке подключений).</summary>
        bool AllConnected => nm != null && nm.IsServer && session != null && nm.ConnectedClientsIds.Count >= session.PlayerCount;

        public async void Leave() => await LeaveAsync();

        /// <summary>Выйти из сессии и вернуться в меню (для паузы в игре).</summary>
        public async void LeaveAndReturnToMenu(string message = null)
        {
            await LeaveAsync();
            GameSession.ReturnToMenu(message);
        }

        public async Task LeaveAsync()
        {
            var s = session;
            Unsubscribe();
            session = null;
            ResetLobby();
            Current = State.Idle;

            if (s != null)
            {
                try { await s.LeaveAsync(); }
                catch (Exception e) { Debug.LogWarning($"[JunkRace] LeaveAsync: {e.Message}"); }
            }
            if (nm != null && nm.IsListening) nm.Shutdown();
        }

        // ============================ Лобби ============================

        void Update()
        {
            if (session == null) return;

            players.Clear();
            playersReady.Clear();
            bool allReady = session.PlayerCount > 0;
            foreach (var p in session.Players)
            {
                string playerName = NameOf(p);
                bool ready = IsReady(p);
                if (!ready) allReady = false;
                if (p.Id == session.Host) { players.Insert(0, playerName); playersReady.Insert(0, ready); }   // хост первым
                else { players.Add(playerName); playersReady.Add(ready); }
            }
            AllReady = allReady;

            if (Current != State.InLobby) return;

            // Все нажали «Готов» и все на связи — короткий отсчёт и общий старт. Кто-то передумал — отсчёт сброшен.
            bool go = (allReady || forceStart) && AllConnected;
            if (!go) CountdownLeft = -1f;
            else if (CountdownLeft < 0f) CountdownLeft = StartCountdown;
            else CountdownLeft = Mathf.Max(0f, CountdownLeft - Time.unscaledDeltaTime);

            if (IsHost && go && CountdownLeft <= 0f)
                StartGame();
        }

        void StartGame()
        {
            forceStart = false;
            if (startRequested) return;
            startRequested = true;
            CountdownLeft = -1f;
            Current = State.InGame;
            LockSession();
            nm.SceneManager.LoadScene(GameSession.TrackScene, LoadSceneMode.Single);
        }

        async void LockSession()
        {
            try
            {
                var host = session.AsHost();
                host.IsLocked = true;   // в списке будет «идёт игра», новые не зайдут
                await host.SavePropertiesAsync();
            }
            catch (Exception e) { Debug.LogWarning($"[JunkRace] Не удалось закрыть сессию: {e.Message}"); }
        }

        void OnJoined()
        {
            session.RemovedFromSession += OnRemovedFromSession;
            session.Deleted += OnRemovedFromSession;
            nm.OnClientDisconnectCallback += OnNetworkDisconnect;
            GameSession.Mode = GameMode.Coop;
            ResetLobby();
            Current = State.InLobby;
        }

        void OnSceneChanged(Scene previous, Scene next)
        {
            if (session != null && next.name == GameSession.TrackScene) Current = State.InGame;
        }

        void OnRemovedFromSession() => Disconnected("Сервер закрыт.");

        void OnNetworkDisconnect(ulong clientId)
        {
            if (nm.IsServer) return;   // у хоста это просто ушёл кто-то из игроков
            Disconnected("Связь с хостом потеряна.");
        }

        async void Disconnected(string reason)
        {
            if (session == null) return;
            bool inGame = Current == State.InGame;
            await LeaveAsync();
            if (inGame) GameSession.ReturnToMenu(reason);
            else Failed?.Invoke(reason);
        }

        // ============================ Повтор при сбое связи ============================

        const int Attempts = 3;

        /// <summary>
        /// Запрос к сервису (создать / войти) с повтором: сервис Lobby иногда не отвечает за свои 10 секунд
        /// («Request timeout»), а со второго раза проходит. Остальные ошибки (пароль, сервер полон) — сразу наверх.
        /// </summary>
        async Task<T> WithRetry<T>(string busy, Func<Task<T>> request)
        {
            for (int attempt = 1; ; attempt++)
            {
                if (attempt > 1) SetBusy($"{busy} (попытка {attempt} из {Attempts})");
                try { return await request(); }
                catch (SessionException e) when (attempt < Attempts && IsTimeout(e))
                {
                    Debug.LogWarning($"[JunkRace] Сервис не ответил вовремя, пробуем ещё раз ({attempt}/{Attempts}): {e.Message}");
                    await Task.Yield();
                }
            }
        }

        static bool IsTimeout(Exception e) =>
            e.Message != null && (e.Message.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0
                               || e.Message.IndexOf("timed out", StringComparison.OrdinalIgnoreCase) >= 0);

        // ============================ Мелочи ============================

        void SetBusy(string text)
        {
            BusyText = text;
            Current = State.Busy;
        }

        void Fail(string reason)
        {
            Unsubscribe();
            session = null;
            ResetLobby();
            Current = State.Idle;
            Failed?.Invoke(reason);
        }

        void ResetLobby()
        {
            players.Clear();
            CountdownLeft = -1f;
            startRequested = false;
            forceStart = false;
            LocalReady = false;
            AllReady = false;
            playersReady.Clear();
        }

        void Unsubscribe()
        {
            if (session != null)
            {
                session.RemovedFromSession -= OnRemovedFromSession;
                session.Deleted -= OnRemovedFromSession;
            }
            if (nm != null) nm.OnClientDisconnectCallback -= OnNetworkDisconnect;
        }

        static JoinSessionOptions JoinOptions(string playerName, string password) => new JoinSessionOptions
        {
            Password = SessionPassword(password),
            PlayerProperties = NameProperty(playerName),
        }.WithNetworkOptions(RelayNet());

        /// <summary>
        /// Relay по защищённому WebSocket (WSS) у всех: браузер (itch открывает игру по https) умеет только его,
        /// а протокол у хоста и клиентов обязан совпадать — так ПК, браузер и телефон играют вместе.
        /// Раньше был UDP: DTLS падал при сборке Burst'ом в редакторе на Mac.
        /// </summary>
        static NetworkOptions RelayNet() => new NetworkOptions { RelayProtocol = RelayProtocol.WSS };

        static string RaceCode() => $"{(int)GameSession.Kind}/{(int)GameSession.Size}/{GameSession.Laps}/{(int)GameSession.Shape}/{(int)GameSession.Biome}";

        /// <summary>«Среднее кольцо · 3 круга» из свойства сервера (пусто — не указано).</summary>
        static string RaceLabelOf(IReadOnlyDictionary<string, SessionProperty> props)
        {
            if (props == null || !props.TryGetValue(RaceKey, out var p) || p == null || string.IsNullOrEmpty(p.Value)) return "";
            var parts = p.Value.Split('/');
            if (parts.Length != 5 || !int.TryParse(parts[0], out int k) || !int.TryParse(parts[1], out int sz) || !int.TryParse(parts[2], out int laps)
                || !int.TryParse(parts[3], out int shape) || !int.TryParse(parts[4], out int biome)) return "";
            return GameSession.RaceLabel((RaceKind)Mathf.Clamp(k, 0, 1), (RaceSize)Mathf.Clamp(sz, 0, 2), Mathf.Clamp(laps, 1, GameSession.MaxLaps),
                                         (RaceShape)Mathf.Clamp(shape, 0, 1), (RaceBiome)Mathf.Clamp(biome, 0, 4));
        }

        /// <summary>Гонка этого сервера (в лобби): у хоста — свои настройки, у клиента — из свойств сервера.</summary>
        public string RaceLabel => session != null ? RaceLabelOf(session.Properties) : "";

        static string VersionOf(IReadOnlyDictionary<string, SessionProperty> props) =>
            props != null && props.TryGetValue(VersionKey, out var v) && v != null ? v.Value : null;

        static Dictionary<string, PlayerProperty> NameProperty(string playerName)
        {
            string n = string.IsNullOrWhiteSpace(playerName) ? "Игрок" : playerName.Trim();
            if (n.Length > 16) n = n.Substring(0, 16);
            return new Dictionary<string, PlayerProperty>
            {
                { NameKey, new PlayerProperty(n, VisibilityPropertyOptions.Member) },
                { ReadyKey, new PlayerProperty("0", VisibilityPropertyOptions.Member) },
            };
        }

        static bool IsReady(IReadOnlyPlayer p) =>
            p.Properties != null && p.Properties.TryGetValue(ReadyKey, out var prop) && prop != null && prop.Value == "1";

        static string NameOf(IReadOnlyPlayer p)
        {
            if (p.Properties != null && p.Properties.TryGetValue(NameKey, out var prop) && prop != null && !string.IsNullOrEmpty(prop.Value))
                return prop.Value;
            return "Игрок";
        }

        static string Friendly(Exception e)
        {
            string m = e.Message ?? "";
            string low = m.ToLowerInvariant();
            if (low.Contains("password")) return "Неверный пароль.";
            if (low.Contains("full")) return "Сервер полон.";
            if (low.Contains("locked")) return "Игра уже началась.";
            if (low.Contains("not found")) return "Сервер больше не существует.";
            Debug.LogException(e);
            return "Ошибка сети: " + m;
        }
    }
}
