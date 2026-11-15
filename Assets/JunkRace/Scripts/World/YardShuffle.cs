using System.Collections.Generic;
using UnityEngine;

namespace JunkRace
{
    /// <summary>
    /// Каждый заезд кучи и лежащие детали раскладываются по свалке заново — по сиду трассы,
    /// так что у хоста и клиентов одинаково. Кучи не ставим на коридор старта (решётка), на место
    /// появления игроков и друг на друга; корпуса — тоже не на старт.
    /// Координаты — от арки старта (выезд из свалки), свалка позади неё: x ±21, z от -40 до 0.
    /// </summary>
    public static class YardShuffle
    {
        const float YardHalf = 21f, YardDepth = 40f;
        const float PileRadius = 4.5f;        // модель кучи ~7.6 м + запас
        const float PileGap = 9.5f;           // между центрами куч
        const float CorridorHalf = 6.5f;      // решётка ±3.5 м + полкорпуса + запас
        const float CorridorDepth = 22f;      // от арки назад: два ряда решётки и разгон
        const float PileReach = 5.3f;         // как у кучи (PileWidth / 2 + 1.5)

        // Место появления игроков (x от 0 до 7.5 при z = -32) и руль с креслом у старта.
        static readonly Rect Spawn = new Rect(-4f, -37f, 15f, 11f);

        /// <summary>
        /// Раскладка свалки на заезд: лишние для этого числа игроков детали выключаем, кучи ставим по-новому,
        /// детали — в кучи или рядом с ними. Всё по сиду — у хоста и клиентов одинаково.
        /// </summary>
        public static void Apply(int seed, Vector3 gate, int players)
        {
            // 1. Сколько деталей: сцена собрана на четверых, лишнее выключаем.
            foreach (var ps in Object.FindObjectsByType<PlayerScaled>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                ps.gameObject.SetActive(ps.MinPlayers <= players);

            var piles = Sorted(Object.FindObjectsByType<JunkPile>(FindObjectsSortMode.None));
            // Неактивные тоже — чтобы номера (и случайность каждой детали) не зависели от числа игроков.
            var parts = Sorted(Object.FindObjectsByType<Part>(FindObjectsInactive.Include, FindObjectsSortMode.None));

            // 2. Кучи.
            var rng = new System.Random(seed ^ 0x51ED);
            var placed = new List<Vector2>();
            foreach (var pile in piles)
            {
                Vector2 p = PickPileSpot(rng, placed);
                placed.Add(p);
                var t = pile.transform;
                t.position = new Vector3(gate.x + p.x, t.position.y, gate.z + p.y);
                pile.ClearStash();
            }

            // 2б. Декорации: мелкие внутри (мимо куч, старта и спавна), крупные — за забором.
            obstacles.Clear();
            foreach (var p in placed) obstacles.Add((p, PileRadius));
            var props = Sorted(Object.FindObjectsByType<YardProp>(FindObjectsSortMode.None));
            var propRng = new System.Random(seed ^ 0x9A0F);
            foreach (var prop in props)
            {
                float r = Mathf.Max(0.3f, prop.Radius);
                Vector2 spot = prop.Big ? PickOutside(propRng, r) : PickInside(propRng, r);
                obstacles.Add((spot, r));
                var t = prop.transform;
                t.SetPositionAndRotation(new Vector3(gate.x + spot.x, t.position.y, gate.z + spot.y),
                                         Quaternion.Euler(0f, (float)propRng.NextDouble() * 360f, 0f));
            }

            // 3. Корпуса: на виду — по одному на игрока (но не меньше двух), остальные спрятаны в кучах.
            var hulls = new List<Part>();
            foreach (var part in parts)
                if (part.gameObject.activeInHierarchy && part.GetComponent<Vehicle>() != null && IsLoose(part)) hulls.Add(part);
            var hullRng = new System.Random(seed ^ 0x4011);
            for (int i = hulls.Count - 1; i > 0; i--)
            {
                int j = hullRng.Next(i + 1);
                (hulls[i], hulls[j]) = (hulls[j], hulls[i]);
            }
            var shownHulls = new HashSet<Part>();
            for (int i = 0; i < hulls.Count && i < Mathf.Max(2, players); i++) shownHulls.Add(hulls[i]);

            // 4. Детали. У каждой свой генератор (сид + номер): если у клиента какую-то пропустим, остальные не собьются.
            for (int i = 0; i < parts.Count; i++)
            {
                var part = parts[i];
                if (!part.gameObject.activeInHierarchy || !IsLoose(part)) continue;
                Vector2 local = new Vector2(part.transform.position.x - gate.x, part.transform.position.z - gate.z);
                if (Spawn.Contains(local)) continue;   // руль и кресло у старта — для быстрой сборки

                var r = new System.Random(seed * 31 + i * 7919);
                bool hull = part.GetComponent<Vehicle>() != null;
                bool hide = piles.Count > 0 && (hull ? !shownHulls.Contains(part) : r.NextDouble() < 0.5);
                if (hide)
                {
                    piles[r.Next(piles.Count)].Stash(part);
                    continue;
                }
                Vector2 spot = hull ? PickHullSpot(r, placed) : PickNearPile(r, placed);
                var pos = new Vector3(gate.x + spot.x, part.transform.position.y + 0.3f, gate.z + spot.y);
                var rot = Quaternion.Euler(0f, (float)r.NextDouble() * 360f, 0f);
                part.transform.SetPositionAndRotation(pos, rot);
                if (part.TryGetComponent(out Rigidbody rb))
                {
                    rb.position = pos;
                    rb.rotation = rot;
                    if (!rb.isKinematic) { rb.linearVelocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
                }
            }
            Physics.SyncTransforms();
        }

        /// <summary>Свободно лежит на свалке: не прикручена (и не стоит в сцене на другой детали), не в руках.</summary>
        static bool IsLoose(Part part)
        {
            if (part == null || part.IsCarried || part.MountedOn != null) return false;
            var parent = part.transform.parent;
            if (parent != null && parent.GetComponentInParent<Part>() != null) return false;   // колёсики тележки
            var v = part.GetComponent<Vehicle>();
            if (v != null && (v.PartCount > 1 || v.HasAnyone)) return false;   // уже строят — не трогаем
            if (v == null && part.Vehicle != null) return false;
            return true;
        }

        /// <summary>Что уже стоит на земле: кучи и декорации (центр, радиус).</summary>
        static readonly List<(Vector2 p, float r)> obstacles = new List<(Vector2, float)>();

        static bool HitsObstacle(Vector2 p, float radius, float gap = 0.5f)
        {
            foreach (var (c, r) in obstacles)
                if (Vector2.Distance(p, c) < r + radius + gap) return true;
            return false;
        }

        static Vector2 PickInside(System.Random rng, float r)
        {
            for (int tries = 0; tries < 150; tries++)
            {
                var p = RandomInYard(rng, r + 0.5f);
                if (BlocksStart(p, r) || HitsObstacle(p, r, 1f)) continue;
                return p;
            }
            return PickOutside(rng, r);   // внутри тесно — за забор
        }

        /// <summary>За забором: слева, справа или сзади свалки (впереди — дорога).</summary>
        static Vector2 PickOutside(System.Random rng, float r)
        {
            Vector2 p = default;
            for (int tries = 0; tries < 60; tries++)
            {
                float off = r + 1.5f + (float)rng.NextDouble() * 8f;
                switch (rng.Next(3))
                {
                    case 0: p = new Vector2(-YardHalf - off, Mathf.Lerp(-YardDepth - 6f, -4f, (float)rng.NextDouble())); break;
                    case 1: p = new Vector2(YardHalf + off, Mathf.Lerp(-YardDepth - 6f, -4f, (float)rng.NextDouble())); break;
                    default: p = new Vector2(Mathf.Lerp(-YardHalf - 8f, YardHalf + 8f, (float)rng.NextDouble()), -YardDepth - off); break;
                }
                if (!HitsObstacle(p, r, 1f)) return p;
            }
            return p;
        }

        static Vector2 PickPileSpot(System.Random rng, List<Vector2> placed)
        {
            Vector2 best = default;
            float bestScore = float.MinValue;
            for (int tries = 0; tries < 200; tries++)
            {
                var p = RandomInYard(rng, PileRadius);
                if (BlocksStart(p, PileRadius)) continue;
                float near = Nearest(p, placed);
                if (near >= PileGap) return p;
                if (near > bestScore) { bestScore = near; best = p; }   // тесно — хотя бы подальше от остальных
            }
            return bestScore > float.MinValue ? best : new Vector2(-YardHalf + PileRadius, -YardDepth + PileRadius);
        }

        static Vector2 PickHullSpot(System.Random rng, List<Vector2> piles)
        {
            for (int tries = 0; tries < 100; tries++)
            {
                var p = RandomInYard(rng, 2f);
                if (BlocksStart(p, 2f)) continue;
                if (Nearest(p, piles) < PileRadius + 2f || HitsObstacle(p, 2f)) continue;
                return p;
            }
            return RandomInYard(rng, 2f);
        }

        /// <summary>Мелочь — вокруг случайной кучи (как раньше), но не на старте.</summary>
        static Vector2 PickNearPile(System.Random rng, List<Vector2> piles)
        {
            for (int tries = 0; tries < 30; tries++)
            {
                Vector2 c = piles.Count > 0 ? piles[rng.Next(piles.Count)] : Vector2.zero;
                float a = (float)rng.NextDouble() * Mathf.PI * 2f;
                float d = PileReach - 0.5f + (float)rng.NextDouble() * 2f;
                var p = c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * d;
                if (!InYard(p, 1f) || BlocksStart(p, 0.5f) || HitsObstacle(p, 0.4f, 0f)) continue;
                return p;
            }
            return RandomInYard(rng, 1f);
        }

        /// <summary>Мешает старту: коридор решётки перед аркой или место появления игроков.</summary>
        static bool BlocksStart(Vector2 p, float radius)
        {
            if (Mathf.Abs(p.x) < CorridorHalf + radius && p.y > -CorridorDepth - radius) return true;
            var s = Spawn;
            return p.x > s.xMin - radius && p.x < s.xMax + radius && p.y > s.yMin - radius && p.y < s.yMax + radius;
        }

        static bool InYard(Vector2 p, float margin) =>
            Mathf.Abs(p.x) <= YardHalf - margin && p.y >= -YardDepth + margin && p.y <= -margin;

        static Vector2 RandomInYard(System.Random rng, float margin) => new Vector2(
            Mathf.Lerp(-YardHalf + margin, YardHalf - margin, (float)rng.NextDouble()),
            Mathf.Lerp(-YardDepth + margin, -margin, (float)rng.NextDouble()));

        static float Nearest(Vector2 p, List<Vector2> others)
        {
            float best = float.MaxValue;
            foreach (var o in others) best = Mathf.Min(best, Vector2.Distance(p, o));
            return best;
        }

        /// <summary>Порядок по иерархии — одинаковый у всех (сцена одна).</summary>
        static List<T> Sorted<T>(T[] items) where T : Component
        {
            var keyed = new List<(string key, T item)>(items.Length);
            foreach (var it in items) keyed.Add((JunkNet.HierarchyKey(it.transform), it));
            keyed.Sort((a, b) => string.CompareOrdinal(a.key, b.key));
            var list = new List<T>(keyed.Count);
            foreach (var (_, it) in keyed) list.Add(it);
            return list;
        }
    }
}
