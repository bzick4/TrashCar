using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace JunkRace
{
    /// <summary>
    /// Esc / Start. Соло — пауза (время стоит). Кооп — только меню: мир идёт дальше, а свой игрок
    /// не двигается и машину не ведёт (газ и руль — ноль), пока меню открыто.
    /// </summary>
    public class InGameMenu : MonoBehaviour
    {
        public static bool IsOpen { get; private set; }

        /// <summary>Управление игроком выключено: открыто меню или окно обучения.</summary>
        public static bool Blocked => IsOpen || Tutorial.ModalOpen;

        void Start() => SetOpen(false);

        void OnDestroy()
        {
            hud?.Destroy();
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
            if (!open) soundPage = false;
            bool free = open || Tutorial.ModalOpen;   // под меню могло быть окно обучения — ему нужна мышь
            Cursor.lockState = free ? CursorLockMode.None : CursorLockMode.Locked;
            Cursor.visible = free;
            // В коопе мир не останавливаем: другие игроки продолжают.
            if (GameSession.Mode == GameMode.Solo) Time.timeScale = open ? 0f : 1f;
        }

        // ======================= UGUI =======================

        RectTransform canvas, panelContent;
        bool soundPage;
        bool builtOpen, builtSound;
        UiPainter hud;
        Ui.UiButton scaleButton, fpsButton;

        void LateUpdate()
        {
            // Подпись в коопе: справа вверху, всегда.
            hud ??= new UiPainter("Кооп", 5);
            hud.Begin();
            if (GameSession.Mode == GameMode.Coop && CoopSession.Instance != null)
                hud.Label(new Rect(Screen.width - 620, 20, 600, 30),
                    $"КООП · {CoopSession.Instance.ServerName} · игроков: {CoopSession.Instance.Players.Count}",
                    new UiPainter.Style(20, Color.white, TextAnchor.UpperRight, wrap: false));
            hud.End();

            if (IsOpen != builtOpen || (IsOpen && soundPage != builtSound)) Rebuild();
            if (IsOpen && !soundPage)
            {
                int q = QualitySetup.ScaleIndex;
                string scaleText = q == 0 ? $"Авто ({Mathf.RoundToInt(QualitySetup.EffectiveScale * 100f)}%)" : QualitySetup.ScaleNames[q];
                if (scaleButton != null) scaleButton.Text.text = $"Чёткость: {scaleText}";
                if (fpsButton != null) fpsButton.Text.text = $"Лимит кадров: {QualitySetup.FpsNames[QualitySetup.FpsIndex]}";
            }
        }

        void Rebuild()
        {
            builtOpen = IsOpen;
            builtSound = soundPage;
            if (canvas == null)
            {
                canvas = Ui.Canvas("Меню_паузы", 20, transform);
                // Затемнение мира под панелью.
                Ui.Stretch(Ui.Image("Затемнение", canvas, null, new Color(0.12f, 0.11f, 0.10f, 0.6f)).rectTransform).GetComponent<Graphic>().raycastTarget = true;
                var panel = Ui.Node("Панель", canvas);
                panel.anchorMin = panel.anchorMax = new Vector2(0.5f, 1f);
                panel.pivot = new Vector2(0.5f, 1f);
                panel.anchoredPosition = new Vector2(0f, -110f);
                panel.sizeDelta = new Vector2(620f, 880f);
                var art = Ui.Load("panel");
                Ui.Plate(panel, art, new RectOffset(140, 140, 140, 140), new Color(0.20f, 0.18f, 0.16f, 0.97f));
                float inset = art != null ? 74f : 40f;
                panelContent = Ui.Stretch(Ui.Node("Содержимое", panel), inset, inset, inset, inset);
            }
            canvas.gameObject.SetActive(IsOpen);
            if (!IsOpen) return;

            Ui.Clear(panelContent);
            scaleButton = fpsButton = null;
            var content = Ui.Stretch(Ui.Node(soundPage ? "Звук" : "Меню", panelContent));
            Ui.Column(content, 0f).childForceExpandWidth = true;

            if (soundPage)
            {
                Ui.Header(content, "Звук");
                Ui.Space(content, 16f);
                Ui.VolumeSliders(content, 472f);
                Ui.Space(content, 0f, true);
                Ui.Button(content, "Назад", "back", Ui.Big, () => { GameAudio.Save(); soundPage = false; });
                return;
            }

            Ui.Header(content, GameSession.Mode == GameMode.Solo ? "Пауза" : "Меню");
            if (GameSession.Mode != GameMode.Solo) Ui.Hint(content, "Игра идёт, остальные играют дальше");
            Ui.Space(content, 20f);
            Ui.Button(content, "Продолжить", null, Ui.Big, () => SetOpen(false));
            Ui.Space(content, 12f);
            if (GameSession.IsTutorial)
            {
                Ui.Button(content, "Пропустить обучение", null, Ui.Big, Tutorial.Skip);
                Ui.Space(content, 12f);
            }
            else if (GameSession.CanRestart)
            {
                Ui.Button(content, GameSession.Mode == GameMode.Solo ? "Заново" : "Заново (у всех)", null, Ui.Big, GameSession.RestartTrack);
                Ui.Space(content, 12f);
            }
            // Масштаб рендера: на 4K «Авто» рендерит в 75% — почти незаметно, а FPS заметно выше.
            scaleButton = Ui.Button(content, "Чёткость", null, Ui.Big,
                () => QualitySetup.ScaleIndex = (QualitySetup.ScaleIndex + 1) % QualitySetup.ScaleNames.Length);
            Ui.Space(content, 12f);
            fpsButton = Ui.Button(content, "Лимит кадров", null, Ui.Big,
                () => QualitySetup.FpsIndex = (QualitySetup.FpsIndex + 1) % QualitySetup.FpsNames.Length);
            Ui.Space(content, 12f);
            Ui.Button(content, "Звук", null, Ui.Big, () => soundPage = true);
            Ui.Space(content, 12f);
            Ui.Button(content, "В меню", "back", Ui.Big, () =>
            {
                if (CoopSession.Instance != null) CoopSession.Instance.LeaveAndReturnToMenu();
                else GameSession.ReturnToMenu();
            });
        }

    }
}
