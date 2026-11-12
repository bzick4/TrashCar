using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace JunkRace
{
    /// <summary>Пауза по Esc / Start: продолжить, заново (соло), в меню.</summary>
    public class InGameMenu : MonoBehaviour
    {
        public static bool IsOpen { get; private set; }

        void Start() => SetOpen(false);

        void OnDestroy()
        {
            IsOpen = false;
            Time.timeScale = 1f;
        }

        void Update()
        {
            bool toggle = (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
                       || (Gamepad.current != null && Gamepad.current.startButton.wasPressedThisFrame);
            if (toggle) SetOpen(!IsOpen);
        }

        void SetOpen(bool open)
        {
            IsOpen = open;
            Cursor.lockState = open ? CursorLockMode.None : CursorLockMode.Locked;
            Cursor.visible = open;
            // В коопе мир не останавливаем: другие игроки продолжают.
            if (GameSession.Mode == GameMode.Solo) Time.timeScale = open ? 0f : 1f;
        }

        void OnGUI()
        {
            if (GameSession.Mode == GameMode.Coop && CoopSession.Instance != null)
            {
                var hud = new GUIStyle(GUI.skin.label) { fontSize = 20, alignment = TextAnchor.UpperRight };
                GUI.Label(new Rect(Screen.width - 620, 20, 600, 30),
                    $"КООП · {CoopSession.Instance.ServerName} · игроков: {CoopSession.Instance.Players.Count}", hud);
            }

            if (!IsOpen) return;

            MenuStyles.Ensure();
            float scale = Screen.height / 1080f;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
            float w = Screen.width / scale;

            GUI.color = new Color(1f, 1f, 1f, 0.6f);
            GUI.DrawTexture(new Rect(0, 0, w, 1080), MenuStyles.BackgroundTex);
            GUI.color = Color.white;

            var panel = new Rect((w - 560f) / 2f, 260f, 560f, 520f);
            GUI.Box(panel, GUIContent.none, MenuStyles.Panel);
            GUILayout.BeginArea(new Rect(panel.x + 40, panel.y + 30, panel.width - 80, panel.height - 60));

            GUILayout.Label("Пауза", MenuStyles.Header);
            GUILayout.Space(20);
            if (GUILayout.Button("Продолжить", MenuStyles.Button)) SetOpen(false);
            GUILayout.Space(12);
            if (GameSession.Mode == GameMode.Solo && GUILayout.Button("Заново", MenuStyles.Button))
            {
                Time.timeScale = 1f;
                SceneManager.LoadScene(SceneManager.GetActiveScene().name);
            }
            GUILayout.Space(12);
            if (GUILayout.Button("В меню", MenuStyles.Button))
            {
                if (CoopSession.Instance != null) CoopSession.Instance.LeaveAndReturnToMenu();
                else GameSession.ReturnToMenu();
            }

            GUILayout.EndArea();
            GUI.matrix = Matrix4x4.identity;
        }
    }
}
