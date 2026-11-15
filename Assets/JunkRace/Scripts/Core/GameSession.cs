using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace JunkRace
{
    public enum GameMode { Solo, Coop, UltraCoop }

    /// <summary>Общие настройки сессии, живут между сценами.</summary>
    public static class GameSession
    {
        public const string MenuScene = "JunkRaceMainMenu";
        public const string TrackScene = "JunkRaceTestTrack";
        public const int CoopMaxPlayers = 4;

        public static GameMode Mode = GameMode.Solo;

        /// <summary>Сообщение, которое меню покажет после возврата (например, «Хост вышел»).</summary>
        public static string PendingMessage;

        public static string PlayerName
        {
            get => PlayerPrefs.GetString("jr_player_name", "");
            set { PlayerPrefs.SetString("jr_player_name", value); PlayerPrefs.Save(); }
        }

        public static void StartSolo()
        {
            Mode = GameMode.Solo;
            SceneManager.LoadScene(TrackScene);
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
