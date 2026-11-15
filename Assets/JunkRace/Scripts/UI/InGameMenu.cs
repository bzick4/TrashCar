using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace JunkRace
{
    /// <summary>
    /// Esc / Start. Соло — пауза (время стоит). Кооп — только меню: мир идёт дальше, а свой игрок
    /// не двигается и машину не ведёт (газ и руль — ноль), пока меню открыто.
    /// </summary>
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
            if (toggle && !RaceTimer.IsLoading) SetOpen(!IsOpen);
        }

        void SetOpen(bool open)
        {
            IsOpen = open;
            Cursor.lockState = open ? CursorLockMode.None : CursorLockMode.Locked;
            Cursor.visible = open;
            // В коопе мир не останавливаем: другие игроки продолжают.
            if (GameSession.Mode == GameMode.Solo) Time.timeScale = open ? 0f : 1f;
        }

        // Стили — один раз: новый GUIStyle в каждом OnGUI копит нативную память и тормозит со временем.
        static GUIStyle hudStyle;

        void OnGUI()
        {
            if (GameSession.Mode == GameMode.Coop && CoopSession.Instance != null)
            {
                hudStyle ??= new GUIStyle(GUI.skin.label) { fontSize = 20, alignment = TextAnchor.UpperRight };
                var hud = hudStyle;
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

            var panel = new Rect((w - 560f) / 2f, 200f, 560f, 720f);
            GUI.Box(panel, GUIContent.none, MenuStyles.Panel);
            GUILayout.BeginArea(new Rect(panel.x + 40, panel.y + 30, panel.width - 80, panel.height - 60));

            GUILayout.Label(GameSession.Mode == GameMode.Solo ? "Пауза" : "Меню", MenuStyles.Header);
            if (GameSession.Mode != GameMode.Solo) GUILayout.Label("Игра идёт, остальные играют дальше", MenuStyles.Hint);
            GUILayout.Space(20);
            if (GUILayout.Button("Продолжить", MenuStyles.Button)) SetOpen(false);
            GUILayout.Space(12);
            if (GameSession.CanRestart && GUILayout.Button(GameSession.Mode == GameMode.Solo ? "Заново" : "Заново (у всех)", MenuStyles.Button))
                GameSession.RestartTrack();
            GUILayout.Space(12);
            // Масштаб рендера: на 4K «Авто» рендерит в 75% — почти незаметно, а FPS заметно выше.
            int q = QualitySetup.ScaleIndex;
            string scaleText = q == 0 ? $"Авто ({Mathf.RoundToInt(QualitySetup.EffectiveScale * 100f)}%)" : QualitySetup.ScaleNames[q];
            if (GUILayout.Button($"Чёткость: {scaleText}", MenuStyles.Button))
                QualitySetup.ScaleIndex = (q + 1) % QualitySetup.ScaleNames.Length;
            GUILayout.Space(12);
            int f = QualitySetup.FpsIndex;
            if (GUILayout.Button($"Лимит кадров: {QualitySetup.FpsNames[f]}", MenuStyles.Button))
                QualitySetup.FpsIndex = (f + 1) % QualitySetup.FpsNames.Length;
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
