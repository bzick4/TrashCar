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
