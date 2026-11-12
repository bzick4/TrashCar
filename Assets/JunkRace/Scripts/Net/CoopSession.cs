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
        public const int MinPasswordLength = 8;   // ограничение Unity: пароль сессии 8–64 символа
        const string NameKey = "name";
        const string PasswordFlagKey = "pw";
        const float StartCountdown = 3f;

        public enum State { Idle, Busy, InLobby, InGame }

        public static CoopSession Instance { get; private set; }

        public State Current { get; private set; } = State.Idle;
        public string BusyText { get; private set; } = "";
        public bool IsHost => session != null && session.IsHost;
        public string ServerName => session != null ? session.Name : "";
        public string JoinCode => session != null ? session.Code : "";
        public IReadOnlyList<string> Players => players;
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
                    Password = hasPassword ? password : null,
                    PlayerProperties = NameProperty(playerName),
                    SessionProperties = new Dictionary<string, SessionProperty>
                    {
                        { PasswordFlagKey, new SessionProperty(hasPassword ? "1" : "0", VisibilityPropertyOptions.Public) },
                    },
                }.WithRelayNetwork();

                session = await MultiplayerService.Instance.CreateSessionAsync(options);
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
                session = await MultiplayerService.Instance.JoinSessionByIdAsync(info.Id, JoinOptions(playerName, password));
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
                session = await MultiplayerService.Instance.JoinSessionByCodeAsync(code.Trim().ToUpperInvariant(), JoinOptions(playerName, password));
                OnJoined();
            }
            catch (Exception e) { Fail(Friendly(e)); }
        }

        /// <summary>Хост: начать, не дожидаясь 4 игроков (для тестов).</summary>
        public void ForceStart()
        {
            if (IsHost && Current == State.InLobby) StartGame();
        }

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
            foreach (var p in session.Players)
            {
                string playerName = NameOf(p);
                if (p.Id == session.Host) players.Insert(0, playerName);   // хост первым
                else players.Add(playerName);
            }

            if (Current != State.InLobby) return;

            bool full = session.PlayerCount >= GameSession.CoopMaxPlayers;
            if (!full) CountdownLeft = -1f;
            else if (CountdownLeft < 0f) CountdownLeft = StartCountdown;
            else CountdownLeft = Mathf.Max(0f, CountdownLeft - Time.unscaledDeltaTime);

            // Стартуем, когда отсчёт кончился и все реально подключились по сети.
            if (IsHost && full && CountdownLeft <= 0f && nm.IsServer && nm.ConnectedClientsIds.Count >= session.PlayerCount)
                StartGame();
        }

        void StartGame()
        {
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
            Password = string.IsNullOrEmpty(password) ? null : password,
            PlayerProperties = NameProperty(playerName),
        };

        static Dictionary<string, PlayerProperty> NameProperty(string playerName)
        {
            string n = string.IsNullOrWhiteSpace(playerName) ? "Игрок" : playerName.Trim();
            if (n.Length > 16) n = n.Substring(0, 16);
            return new Dictionary<string, PlayerProperty>
            {
                { NameKey, new PlayerProperty(n, VisibilityPropertyOptions.Member) },
            };
        }

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
