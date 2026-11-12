using System;
using UnityEngine.InputSystem;

namespace JunkRace
{
    /// <summary>
    /// Управление на новой Input System, собранное в коде (без .inputactions ассета),
    /// чтобы прототип заводился без настройки. Клавиатура+мышь и геймпад.
    /// Позже легко заменить на сгенерированный класс из .inputactions.
    /// </summary>
    public sealed class GameControls : IDisposable
    {
        public readonly InputActionMap Camera = new InputActionMap("Camera");
        public readonly InputActionMap OnFoot = new InputActionMap("OnFoot");
        public readonly InputActionMap Driving = new InputActionMap("Driving");

        // Camera (всегда включена)
        public readonly InputAction LookMouse, LookStick, Stats, ToggleView;

        // Пешком
        public readonly InputAction Move, Jump, Grab, Enter;

        // За рулём
        public readonly InputAction Throttle, Steer, Special, Exit, Flip;

        public GameControls()
        {
            LookMouse = Camera.AddAction("LookMouse", InputActionType.Value, "<Mouse>/delta");
            LookStick = Camera.AddAction("LookStick", InputActionType.Value, "<Gamepad>/rightStick");
            Stats = Camera.AddAction("Stats", InputActionType.Button, "<Keyboard>/tab");
            Stats.AddBinding("<Gamepad>/dpad/up");
            ToggleView = Camera.AddAction("ToggleView", InputActionType.Button, "<Keyboard>/v");
            ToggleView.AddBinding("<Gamepad>/rightStickPress");

            Move = OnFoot.AddAction("Move", InputActionType.Value);
            Move.AddCompositeBinding("2DVector")
                .With("Up", "<Keyboard>/w").With("Down", "<Keyboard>/s")
                .With("Left", "<Keyboard>/a").With("Right", "<Keyboard>/d");
            Move.AddBinding("<Gamepad>/leftStick");

            Jump = OnFoot.AddAction("Jump", InputActionType.Button, "<Keyboard>/space");
            Jump.AddBinding("<Gamepad>/buttonSouth");

            Grab = OnFoot.AddAction("Grab", InputActionType.Button, "<Keyboard>/e");
            Grab.AddBinding("<Gamepad>/buttonWest");

            Enter = OnFoot.AddAction("Enter", InputActionType.Button, "<Keyboard>/f");
            Enter.AddBinding("<Gamepad>/buttonNorth");

            Throttle = Driving.AddAction("Throttle", InputActionType.Value);
            Throttle.AddCompositeBinding("1DAxis")
                .With("Positive", "<Keyboard>/w").With("Negative", "<Keyboard>/s");
            Throttle.AddCompositeBinding("1DAxis")
                .With("Positive", "<Gamepad>/rightTrigger").With("Negative", "<Gamepad>/leftTrigger");

            Steer = Driving.AddAction("Steer", InputActionType.Value);
            Steer.AddCompositeBinding("1DAxis")
                .With("Positive", "<Keyboard>/d").With("Negative", "<Keyboard>/a");
            Steer.AddBinding("<Gamepad>/leftStick/x");

            Special = Driving.AddAction("Special", InputActionType.Button, "<Keyboard>/space");
            Special.AddBinding("<Gamepad>/buttonSouth");

            Exit = Driving.AddAction("Exit", InputActionType.Button, "<Keyboard>/f");
            Exit.AddBinding("<Gamepad>/buttonNorth");

            Flip = Driving.AddAction("Flip", InputActionType.Button, "<Keyboard>/r");
            Flip.AddBinding("<Gamepad>/select");

            Camera.Enable();
            SetDriving(false);
        }

        public void SetDriving(bool driving)
        {
            if (driving) { OnFoot.Disable(); Driving.Enable(); }
            else { Driving.Disable(); OnFoot.Enable(); }
        }

        public void Dispose()
        {
            Camera.Dispose();
            OnFoot.Dispose();
            Driving.Dispose();
        }
    }

    /// <summary>Игрок как источник нажатий для машины.</summary>
    public sealed class PlayerPartInput : IPartInput
    {
        readonly GameControls controls;
        public PlayerPartInput(GameControls controls) => this.controls = controls;

        public float Throttle => controls.Throttle.ReadValue<float>();
        public float Steer => controls.Steer.ReadValue<float>();
        public bool Special => controls.Special.IsPressed();
    }
}
