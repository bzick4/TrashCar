#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System.Collections.Generic;
using UnityEngine;

namespace JunkRace
{
    /// <summary>Тест-комната: подписи над свободно лежащими деталями поблизости (имя и категория).</summary>
    public class DevRoomLabels : MonoBehaviour
    {

        const float Range = 12f;
        readonly List<Part> parts = new List<Part>();
        float nextScan;
        UiPainter paint;   // через UGUI: IMGUI в Linear выбеливал цвета
        static readonly UiPainter.Style Style = new UiPainter.Style(15, Color.white, TextAnchor.MiddleCenter, true, false);

        void Update()
        {
            if (Time.time < nextScan) return;
            nextScan = Time.time + 1f;
            parts.Clear();
            parts.AddRange(FindObjectsByType<Part>(FindObjectsSortMode.None));
        }

        void LateUpdate()
        {
            paint ??= new UiPainter("Тест-комната", 14);
            paint.Begin();
            DrawLabels();
            paint.End();
        }

        void OnDestroy() => paint?.Destroy();

        void DrawLabels()
        {
            if (!GameSession.IsDevRoom || InGameMenu.IsOpen) return;
            var cam = Camera.main;
            if (cam == null) return;
            foreach (var p in parts)
            {
                if (p == null || p.IsCarried || p.IsHidden || p.MountedOn != null) continue;
                var v = p.GetComponent<Vehicle>();
                if (v != null && v.PartCount > 1) continue;   // уже строят — подпись мешает
                Vector3 at = p.transform.position + Vector3.up * (v != null ? 1.4f : 0.9f);
                float d = Vector3.Distance(cam.transform.position, at);
                if (d > Range) continue;
                Vector3 s = cam.WorldToScreenPoint(at);
                if (s.z <= 0f) continue;
                var rect = new Rect(s.x - 120f, Screen.height - s.y - 24f, 240f, 40f);
                string text = p.DisplayName + "\n<size=12>" + p.CategoryName + "</size>";
                paint.Label(new Rect(rect.x + 1f, rect.y + 1f, rect.width, rect.height), text, Style.With(new Color(0f, 0f, 0f, 0.7f)));
                paint.Label(rect, text, Style);
            }
        }
    }
}
#endif
