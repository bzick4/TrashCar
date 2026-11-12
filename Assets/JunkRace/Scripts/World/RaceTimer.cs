using System;
using UnityEngine;

namespace JunkRace
{
    /// <summary>
    /// Таймер заезда: стартует, когда кто-то сел за руль. Финиш засчитывает хост и рассылает время всем.
    /// </summary>
    public class RaceTimer : MonoBehaviour
    {
        public static RaceTimer Instance { get; private set; }
        /// <summary>(время, сколько деталей доехало). Только на авторитете.</summary>
        public static event Action<float, int> Finished;

        float startTime = -1f, finishedElapsed = -1f;

        public bool IsFinished => finishedElapsed >= 0f;
        public int PartsAtFinish { get; private set; }
        public float Elapsed => startTime < 0f ? -1f : IsFinished ? finishedElapsed : Time.time - startTime;

        void Awake() => Instance = this;
        void OnEnable() => Vehicle.AnyDriverEntered += OnDriverEntered;
        void OnDisable() => Vehicle.AnyDriverEntered -= OnDriverEntered;
        void OnDestroy() { if (Instance == this) Instance = null; }

        void OnDriverEntered(Vehicle v)
        {
            if (startTime < 0f) startTime = Time.time;
        }

        public void Finish(Vehicle v)
        {
            if (startTime < 0f || IsFinished) return;
            ApplyFinish(Time.time - startTime, v.PartCount);
            Finished?.Invoke(finishedElapsed, PartsAtFinish);
        }

        public void ApplyStart(float elapsed)
        {
            if (startTime < 0f) startTime = Time.time - elapsed;
        }

        public void ApplyFinish(float elapsed, int parts)
        {
            if (startTime < 0f) startTime = Time.time - elapsed;
            finishedElapsed = elapsed;
            PartsAtFinish = parts;
        }

        void OnGUI()
        {
            if (InGameMenu.IsOpen) return;
            var style = new GUIStyle(GUI.skin.label) { fontSize = 28, fontStyle = FontStyle.Bold };
            if (startTime < 0f)
            {
                GUI.Label(new Rect(20, 20, 900, 40), "Собери машину и садись за руль (F)", style);
                return;
            }
            float t = Elapsed;
            GUI.Label(new Rect(20, 20, 500, 40), $"{(int)(t / 60):00}:{t % 60:00.0}", style);
            if (IsFinished)
                GUI.Label(new Rect(20, 60, 900, 40), $"ФИНИШ! Доехало деталей: {PartsAtFinish}", style);
        }
    }
}
