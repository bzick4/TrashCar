using System.Collections.Generic;
using UnityEngine;

namespace JunkRace
{
    public enum Biome { Forest, Mountains, Snow, Desert }

    /// <summary>Точка осевой линии дороги.</summary>
    public struct RouteSample
    {
        public Vector3 Pos;
        public Vector3 Forward;   // горизонтальное направление дороги
        public float HalfWidth;
        public bool Road;         // false — дороги нет (пропасть, которую надо перепрыгнуть)
        public Biome Biome;
        public bool Bridge;       // мост над рекой: тонкий настил, перила, опоры

        public Vector3 Right => new Vector3(Forward.z, 0f, -Forward.x);
    }

    /// <summary>Один путь: основная дорога или ветка развилки.</summary>
    public class RoutePath
    {
        public readonly string Name;
        /// <summary>Строить ли дорогу (у свалки своя земля — только для сброса и проверки).</summary>
        public bool BuildMesh = true;
        public readonly List<RouteSample> Samples = new List<RouteSample>();
        /// <summary>Пройденное расстояние до каждой точки (по горизонтали).</summary>
        public readonly List<float> Dist = new List<float>();

        /// <summary>Прогресс по всей трассе в начале этого пути (м от старта; свалка — отрицательный).</summary>
        public float StartProgress;
        /// <summary>Сколько прогресса даёт метр пути (у петляющей ветки развилки — меньше 1).</summary>
        public float ProgressScale = 1f;

        public RoutePath(string name) => Name = name;
        public float Length => Dist.Count > 0 ? Dist[Dist.Count - 1] : 0f;
        public RouteSample Last => Samples[Samples.Count - 1];

        public void Add(RouteSample s)
        {
            if (Samples.Count == 0) Dist.Add(0f);
            else
            {
                Vector3 a = Samples[Samples.Count - 1].Pos, b = s.Pos;
                a.y = b.y = 0f;
                Dist.Add(Dist[Dist.Count - 1] + Vector3.Distance(a, b));
            }
            Samples.Add(s);
        }

        /// <summary>Направления — по соседям, чтобы сечения дороги шли плавно.</summary>
        /// <summary>
        /// Направление на концах пути, заданное явно (стык развилки): края площадки и веток лежат
        /// на одной прямой — без щели и «ступеньки» на шве.
        /// </summary>
        public Vector3? StartForward, EndForward;

        public void SmoothForwards()
        {
            int n = Samples.Count;
            if (n < 2) return;
            for (int i = 0; i < n; i++)
            {
                Vector3 d = Samples[Mathf.Min(i + 1, n - 1)].Pos - Samples[Mathf.Max(i - 1, 0)].Pos;
                d.y = 0f;
                if (i == 0 && StartForward.HasValue) d = StartForward.Value;
                if (i == n - 1 && EndForward.HasValue) d = EndForward.Value;
                if (d.sqrMagnitude < 1e-6f) continue;
                var s = Samples[i];
                s.Forward = d.normalized;
                Samples[i] = s;
            }
        }

        /// <summary>Точка на расстоянии d от начала пути (с интерполяцией).</summary>
        public RouteSample At(float d)
        {
            int n = Samples.Count;
            if (n == 0) return default;
            if (d <= 0f) return Samples[0];
            if (d >= Length) return Samples[n - 1];
            int lo = 0, hi = n - 1;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) / 2;
                if (Dist[mid] <= d) lo = mid; else hi = mid;
            }
            float span = Dist[hi] - Dist[lo];
            float t = span > 1e-5f ? (d - Dist[lo]) / span : 0f;
            return Lerp(Samples[lo], Samples[hi], t);
        }

        public static RouteSample Lerp(RouteSample a, RouteSample b, float t)
        {
            Vector3 f = Vector3.Lerp(a.Forward, b.Forward, t);
            return new RouteSample
            {
                Pos = Vector3.Lerp(a.Pos, b.Pos, t),
                Forward = f.sqrMagnitude > 1e-6f ? f.normalized : a.Forward,
                HalfWidth = Mathf.Lerp(a.HalfWidth, b.HalfWidth, t),
                Road = a.Road && b.Road,
                Biome = t < 0.5f ? a.Biome : b.Biome,
                Bridge = a.Bridge && b.Bridge,
            };
        }
    }

    /// <summary>
    /// Карта маршрута: все пути с осевыми линиями. Отвечает на вопросы «где ближайшая дорога»,
    /// «машина съехала с трассы?» и «куда поставить машину при сбросе».
    /// </summary>
    public class RouteMap
    {
        /// <summary>Маршрут текущей сцены (null — маршрута нет, работает старая тестовая трасса).</summary>
        public static RouteMap Current;

        public readonly List<RoutePath> Paths = new List<RoutePath>();
        /// <summary>Сколько можно уехать за край дороги, прежде чем вернёт обратно, м.</summary>
        public float OffMargin = 6f;
        /// <summary>Насколько ниже дороги — уже «упал» (с горной дороги, в пропасть), м.</summary>
        public float FallDepth = 6f;

        public struct Hit
        {
            public RoutePath Path;
            public float Along;       // расстояние по пути
            public RouteSample Sample;
            public float Lateral;     // смещение вбок от оси (+ вправо)
            public float Distance;    // |смещение| по горизонтали
            public float Below;       // насколько точка ниже дороги
        }

        public RoutePath NewPath(string name)
        {
            var p = new RoutePath(name);
            Paths.Add(p);
            return p;
        }

        /// <summary>Ближайшее место на маршруте. roadOnly — только там, где есть дорога (не пропасть).</summary>
        public bool Nearest(Vector3 p, bool roadOnly, out Hit hit)
        {
            hit = default;
            float bestScore = float.MaxValue;
            bool found = false;
            foreach (var path in Paths)
            {
                var s = path.Samples;
                for (int i = 0; i + 1 < s.Count; i++)
                {
                    var a = s[i];
                    var b = s[i + 1];
                    if (roadOnly && !(a.Road && b.Road)) continue;
                    float ax = a.Pos.x, az = a.Pos.z, dx = b.Pos.x - ax, dz = b.Pos.z - az;
                    float len2 = dx * dx + dz * dz;
                    float t = len2 > 1e-6f ? Mathf.Clamp01(((p.x - ax) * dx + (p.z - az) * dz) / len2) : 0f;
                    float cx = ax + dx * t, cz = az + dz * t;
                    float ex = p.x - cx, ez = p.z - cz;
                    float dist = Mathf.Sqrt(ex * ex + ez * ez);
                    float cy = Mathf.Lerp(a.Pos.y, b.Pos.y, t);
                    // По высоте тоже: на развилке под горной веткой ближе «своя» дорога внизу.
                    float score = dist + Mathf.Max(0f, Mathf.Abs(cy - p.y) - 4f) * 0.5f;
                    if (score >= bestScore) continue;
                    bestScore = score;
                    found = true;
                    var sample = RoutePath.Lerp(a, b, t);
                    hit.Path = path;
                    hit.Along = Mathf.Lerp(path.Dist[i], path.Dist[i + 1], t);
                    hit.Sample = sample;
                    hit.Lateral = ex * sample.Right.x + ez * sample.Right.z;
                    hit.Distance = dist;
                    hit.Below = cy - p.y;
                }
            }
            return found;
        }

        /// <summary>Где на трассе финиш (прогресс, м от старта). У кольца — линия круга.</summary>
        public float FinishProgress;

        // ---- Кольцо ----
        /// <summary>Трасса — кольцо (овал на хвостике от свалки).</summary>
        public bool Circuit;
        /// <summary>Прогресс у развилки: с него начинается круг (до него — хвостик от свалки).</summary>
        public float LapStart;
        /// <summary>Длина круга, м.</summary>
        public float LapLength;
        /// <summary>Прогресс линии круга (арка чуть дальше развилки).</summary>
        public float LapLine;
        /// <summary>Чекпоинты: расстояние по кругу от линии, м, по порядку.</summary>
        public float[] Checkpoints;

        /// <summary>
        /// Где на круге: метры от линии круга по ходу, 0..LapLength. Отрицательное — ещё на хвостике (до развилки).
        /// </summary>
        public float LapProgress(Vector3 p)
        {
            float pr = Progress(p);
            if (pr == float.MinValue) return float.MinValue;
            if (pr < LapStart - 1f) return pr - LapStart - LapLength;   // хвостик и свалка: «до круга»
            return Mathf.Repeat(pr - LapLine, LapLength);
        }

        /// <summary>Точка круга в d метрах от линии по ходу.</summary>
        public RouteSample AtLap(float d)
        {
            float pr = LapStart + Mathf.Repeat(LapLine - LapStart + d, LapLength);
            foreach (var path in Paths)
            {
                if (path.Length <= 0f || path.ProgressScale <= 0f) continue;
                float local = (pr - path.StartProgress) / path.ProgressScale;
                if (path.StartProgress >= LapStart - 0.5f && local >= 0f && local <= path.Length) return path.At(local);
            }
            return Start;
        }
        /// <summary>Первая точка дороги после свалки (старт).</summary>
        public RouteSample Start;

        /// <summary>Сколько проехано по трассе от старта, м — для мест в гонке.</summary>
        public float Progress(Vector3 p)
        {
            if (!Nearest(p, false, out var h)) return float.MinValue;
            return h.Path.StartProgress + h.Along * h.Path.ProgressScale;
        }

        /// <summary>Стартовая решётка: место i перед аркой старта, ряды по два.</summary>
        public void GridSlot(int i, out Vector3 pos, out Vector3 forward)
        {
            forward = Start.Forward;
            int row = i / 2, col = i % 2;
            pos = Start.Pos - forward * (8f + row * 7f) + Start.Right * (col == 0 ? -3.5f : 3.5f);
        }

        /// <summary>Машина съехала с трассы: далеко вбок или сильно ниже дороги.</summary>
        public bool IsOff(Vector3 p)
        {
            if (!Nearest(p, false, out var h)) return false;
            return h.Distance > h.Sample.HalfWidth + OffMargin || h.Below > FallDepth;
        }

        /// <summary>
        /// Едет по дороге (с допуском tolerance за край): для проверки на срезание. Над пропастью
        /// прыжка — тоже «по дороге» (в полёте над ней), если не упал заметно ниже.
        /// </summary>
        public bool OnRoad(Vector3 p, float tolerance)
        {
            if (!Nearest(p, false, out var h)) return true;
            return h.Distance <= h.Sample.HalfWidth + tolerance && h.Below < FallDepth;
        }

        /// <summary>Свободно ли место (для деревьев и камней): дорога не ближе clearance от её края.</summary>
        public bool IsClear(Vector3 p, float clearance)
        {
            if (!Nearest(p, false, out var h)) return true;
            return h.Distance > h.Sample.HalfWidth + clearance;
        }

        /// <summary>
        /// Куда поставить машину при сбросе: ближайшая дорога, на back метров назад,
        /// с прежним смещением вбок, но не ближе edgeMargin к краю. Пропасть пропускаем.
        /// </summary>
        public bool ResetPose(Vector3 from, float back, float edgeMargin, out Vector3 pos, out Vector3 forward)
        {
            pos = from;
            forward = Vector3.forward;
            if (!Nearest(from, true, out var h)) return false;
            var path = h.Path;
            float along = Mathf.Max(0f, h.Along - back);
            var s = path.At(along);
            // Назад до настоящей дороги: не в пропасть и не в реку (ущелье ниже земли — можно, там сухо).
            for (int i = 0; i < 60 && (!s.Road || WaterZone.TryGetDepth(s.Pos + Vector3.up * 0.3f, out _)) && along > 0f; i++)
            {
                along = Mathf.Max(0f, along - 1f);
                s = path.At(along);
            }
            // Ближе к середине: прежнее смещение вбок у развилки ставило машину прямо на край щели между ветками.
            float limit = Mathf.Min(Mathf.Max(0f, s.HalfWidth - edgeMargin), s.HalfWidth * 0.3f);
            pos = s.Pos + s.Right * Mathf.Clamp(h.Lateral, -limit, limit);
            forward = s.Forward;
            return true;
        }
    }
}
