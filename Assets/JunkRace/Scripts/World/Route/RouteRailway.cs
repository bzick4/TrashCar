using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Rendering;

namespace JunkRace
{
    /// <summary>Железная дорога вдоль сгенерированной трассы. Поезд движется по общему времени хоста.</summary>
    public sealed class RouteRailway : MonoBehaviour
    {
        const float CrossingApproach = 28f;
        const float MaxRailGrade = 0.075f;
        const float CrossingGradeLimit = 0.01f;
        const float RoadScanStep = 1.5f;
        const float RoadBucketSize = 24f;
        const float RailSurfaceOffset = 0.035f;
        const float YardClearance = 18f;
        const float YardApproachSide = 95f;
        const float YardApproachBack = 280f;
        const float TrackStep = 3f;
        const float TrainScale = 2.6f;
        const float CarSpacing = 8.7f;
        const float TrackSpacing = 5.5f;
        const int TrackCount = 3;
        const int ReverseTrack = 2;
        const float SecondEntryDistance = 105f;
        const int WagonsPerTrain = 9;
        const float Speed = 76.5f;
        const float MaxDetour = 34f;
        const float SpineSmoothing = 36f;
        const float ServiceInterval = 8.5f;
        const float ServiceSpacing = Speed * ServiceInterval;

        readonly RoutePath rail = new RoutePath("Железная дорога");
        readonly List<RouteSample> crossings = new List<RouteSample>(5);
        readonly float[] entries = new float[TrackCount];
        readonly float[] crossingOffsets = new float[TrackCount];
        readonly List<Rigidbody[]> services = new List<Rigidbody[]>();
        readonly List<Mesh> trackMeshes = new List<Mesh>();
        Mesh trackMesh;
        (GameObject engine, GameObject wagon)[] trainModels;
        Material material, concrete;
        bool loop, networked;
        float phase;
        float secondEntryLimit = SecondEntryDistance;
        double soloStart;

        public int CrossingCount => crossings.Count;

        internal enum CrossingSite { Blocked, Surface, AboveTunnel }

        internal static RouteRailway Plan(Transform parent, RouteMap map, List<RoutePath> reversed, float ground,
                                          Func<Vector3, float, CrossingSite> crossingSite, int seed, RaceSize size)
        {
            var track = Resources.Load<GameObject>("Railway/spline-track");
            var models = new[]
            {
                (LoadTrainModel("train-locomotive-a"), LoadTrainModel("train-carriage-coal")),
                (LoadTrainModel("train-electric-city-a"), LoadTrainModel("train-electric-city-b")),
                (LoadTrainModel("train-locomotive-b"), LoadTrainModel("train-carriage-tank"))
            };
            var palette = Resources.Load<Texture2D>("Railway/Textures/colormap");
            var trackFilter = track != null ? track.GetComponentInChildren<MeshFilter>() : null;
            if (trackFilter == null || trackFilter.sharedMesh == null || !trackFilter.sharedMesh.isReadable
                || Array.Exists(models, model => model.Item1 == null || model.Item2 == null) || palette == null)
            {
                Debug.LogError("JunkRace: не удалось загрузить меши или палитру железной дороги.");
                return null;
            }

            // Ворота свалки стоят в начале трассы. Держим весь путь впереди ворот
            // с запасом на габарит состава, включая дуги и замыкание кольца.
            float minTrackZ = map.Start.Pos.z + YardClearance;
            var spine = BuildSpine(map, reversed, minTrackZ, out var openRoad);
            if (spine.Length < 80f) return null;
            int target = size == RaceSize.Long ? 5 : size == RaceSize.Medium ? 4 : 2;

            var go = new GameObject("Железная дорога");
            go.transform.SetParent(parent, false);
            var railway = go.AddComponent<RouteRailway>();
            railway.trackMesh = trackFilter.sharedMesh;
            railway.trainModels = models;
            railway.networked = GameSession.Mode != GameMode.Solo;
            railway.phase = Mathf.Abs(seed % 97);
            railway.material = new Material(Shader.Find("JunkRace/Toon")) { name = "Палитра железной дороги" };
            railway.material.SetTexture("_BaseMap", palette);
            railway.material.SetFloat("_NoiseStrength", 0f);
            railway.concrete = new Material(railway.material.shader) { color = new Color(0.48f, 0.43f, 0.38f) };
            railway.PlanSafeTrack(map, spine, openRoad, crossingSite, target, ground, minTrackZ);
            return railway;
        }

        void PlanSafeTrack(RouteMap map, RoutePath spine, List<bool> openRoad,
                           Func<Vector3, float, CrossingSite> crossingSite, int target, float ground, float minTrackZ)
        {
            var road = IndexRoad(map);
            int minimum = target == 4 ? 3 : target;
            var best = new List<RouteSample>();
            var bestCrossings = new List<RouteSample>();
            bool bestLoop = false;
            int count = target;
            while (count > 0)
            {
                var selected = PickCrossings(spine, openRoad, crossingSite, count);
                count = selected.Count;
                if (count == 0) break;
                loop = map.Circuit && count % 2 == 0;
                bool found = false;
                for (int side = -1; side <= 1; side += 2)
                {
                    ResetLayout();
                    LaySpine(map, spine, selected, ground, minTrackZ, side);
                    if (!IsTrackClear(road)) continue;
                    if (count >= minimum)
                    {
                        PlanEntries(map);
                        return;
                    }
                    best.AddRange(rail.Samples);
                    bestCrossings.AddRange(crossings);
                    bestLoop = loop;
                    found = true;
                    break;
                }
                if (found) break;
                count--;
            }
            // На широких кольцах более короткий поперечный участок остаётся между ветками
            // и не цепляет вторую сторону трассы той же дугой.
            // На спринте короткие поперечные участки соединяются длинными продольными дугами.
            float[] reaches = map.Circuit ? new[] { 104f, 82f, 68f, 56f } : new[] { 56f, 48f, 42f };
            foreach (float reach in reaches)
            {
                if (!TryFlatConnectors(map, spine, road, crossingSite, ground, target, bestCrossings.Count, reach)) continue;
                if (crossings.Count >= minimum)
                {
                    PlanEntries(map);
                    return;
                }
                best.Clear();
                bestCrossings.Clear();
                best.AddRange(rail.Samples);
                bestCrossings.AddRange(crossings);
                bestLoop = loop;
            }
            if (best.Count == 0)
            {
                if (!map.Circuit) LayRoadside(map, road, ground);
                if (map.Circuit || !IsTrackClear(road)) LayOutsideRoad(map, ground);
            }
            else
            {
                ResetLayout();
                foreach (var sample in best) rail.Add(sample);
                crossings.AddRange(bestCrossings);
                loop = bestLoop;
            }
            PlanEntries(map);
        }

        void ResetLayout()
        {
            rail.Samples.Clear();
            rail.Dist.Clear();
            crossings.Clear();
        }

        bool TryFlatConnectors(RouteMap map, RoutePath spine, Dictionary<int, List<RoadStrip>> road,
                               Func<Vector3, float, CrossingSite> crossingSite, float ground, int target, int existing, float reach)
        {
            // Сначала проверяем каждый прямой переезд независимо. Безопасные переезды
            // соединяем широкими дугами поочерёдно с двух внешних сторон трассы.
            var candidates = new List<RouteSample>();
            loop = false;
            for (int i = 1; i + 1 < spine.Samples.Count; i++)
            {
                var sample = spine.Samples[i];
                if (sample.Pos.z < map.Start.Pos.z + 95f || Mathf.Abs(sample.Forward.z) < 0.72f ||
                    !FlatCrossing(spine, i) || crossingSite(sample.Pos, 8f) == CrossingSite.Blocked) continue;
                if (candidates.Exists(site => Mathf.Abs(site.Pos.z - sample.Pos.z) < 14f)) continue;
                ResetLayout();
                AddStraightCrossing(sample, 1, ground, reach);
                if (IsTrackClear(road)) candidates.Add(sample);
            }

            candidates.Sort((a, b) => a.Pos.z.CompareTo(b.Pos.z));
            int minimum = map.Circuit ? 1 : 2;
            int widest = map.Circuit ? 140 : 215;
            int tightest = map.Circuit ? 65 : 140;
            for (int length = Mathf.Min(target, candidates.Count); length >= minimum && length > existing; length--)
            {
                for (int spacing = widest; spacing >= tightest; spacing -= 15)
                {
                    var selected = new List<RouteSample>();
                    int cursor = 0;
                    for (int i = 0; i < length; i++)
                    {
                        float aim = Mathf.Lerp(candidates[0].Pos.z, candidates[candidates.Count - 1].Pos.z,
                            (i + 0.5f) / length);
                        while (cursor + 1 < candidates.Count &&
                               candidates[cursor + 1].Pos.z <= candidates[candidates.Count - 1].Pos.z - (length - i - 1) * spacing &&
                               Mathf.Abs(candidates[cursor + 1].Pos.z - aim) < Mathf.Abs(candidates[cursor].Pos.z - aim))
                            cursor++;
                        if (cursor >= candidates.Count) break;
                        var site = candidates[cursor++];
                        selected.Add(site);
                        while (cursor < candidates.Count && candidates[cursor].Pos.z < site.Pos.z + spacing) cursor++;
                    }
                    if (selected.Count != length) continue;

                    for (int direction = -1; direction <= 1; direction += 2)
                    {
                        ResetLayout();
                        int heading = direction;
                        foreach (var site in selected)
                        {
                            if (rail.Samples.Count > 0) ConnectCrossings(site, heading, ground, reach);
                            AddStraightCrossing(site, heading, ground, reach);
                            crossings.Add(site);
                            heading = -heading;
                        }
                        if (!map.Circuit && rail.Last.Pos.z - rail.Samples[0].Pos.z < rail.Length * 0.6f) continue;
                        if (!IsTrackClear(road)) continue;
                        secondEntryLimit = Mathf.Min(54f, reach - 38f);
                        return true;
                    }
                }
            }
            return false;
        }

        void AddStraightCrossing(RouteSample crossing, int direction, float ground, float reach)
        {
            for (float along = -reach; along <= reach; along += 2f)
            {
                var sample = crossing;
                sample.Pos += Vector3.right * (along * direction);
                sample.Pos.y = Mathf.Lerp(crossing.Pos.y, ground,
                    Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(22f, reach - 8f, Mathf.Abs(along)))) + RailSurfaceOffset;
                sample.Forward = Vector3.right * direction;
                rail.Add(sample);
            }
        }

        void ConnectCrossings(RouteSample next, int direction, float ground, float reach)
        {
            var from = rail.Last.Pos;
            var to = next.Pos - Vector3.right * (reach * direction);
            to.y = ground + RailSurfaceOffset;
            Vector3 outward = -Vector3.right * direction;
            float length = Mathf.Clamp((to.z - from.z) * 0.38f, 34f, 48f);
            var a = from + outward * length;
            var b = to + outward * length;
            int steps = Mathf.CeilToInt((Vector3.Distance(from, to) + length) / TrackStep);
            for (int i = 1; i < steps; i++)
            {
                float t = i / (float)steps;
                var sample = next;
                var ab = Vector3.Lerp(a, b, t);
                sample.Pos = Vector3.Lerp(Vector3.Lerp(Vector3.Lerp(from, a, t), ab, t),
                    Vector3.Lerp(ab, Vector3.Lerp(b, to, t), t), t);
                rail.Add(sample);
            }
        }

        static GameObject LoadTrainModel(string name) => Resources.Load<GameObject>("Railway/" + name);

        static float TrackOffset(int index) => (index - 1) * TrackSpacing;

        static RoutePath BuildSpine(RouteMap map, List<RoutePath> reversed, float minTrackZ, out List<bool> openRoad)
        {
            var paths = new List<RoutePath>();
            foreach (var path in map.Paths)
                if (path.BuildMesh && path.Samples.Count > 1 && (!map.Circuit || path.StartProgress >= map.LapStart - 0.05f))
                    paths.Add(path);

            // У двух веток развилки одинаковый прогресс. Берём проезжую, без разрывов.
            var gaps = new Dictionary<RoutePath, int>();
            foreach (var path in paths)
            {
                int blocked = 0;
                foreach (var sample in path.Samples) if (!sample.Road) blocked++;
                gaps.Add(path, blocked);
            }
            paths.Sort((a, b) =>
            {
                int start = a.StartProgress.CompareTo(b.StartProgress);
                if (start != 0) return start;
                int blocked = gaps[a].CompareTo(gaps[b]);
                return blocked != 0 ? blocked : string.CompareOrdinal(a.Name, b.Name);
            });

            var spine = new RoutePath("Маршрут вдоль трассы");
            openRoad = new List<bool>();
            float previousStart = float.NegativeInfinity;
            foreach (var path in paths)
            {
                if (path.StartProgress - previousStart < 0.05f) continue;
                previousStart = path.StartProgress;
                bool reverse = reversed.Contains(path);
                bool mainRoad = path.Name == "Дорога" || path.Name.StartsWith("Кольцо", StringComparison.Ordinal);
                for (int i = 0; i < path.Samples.Count; i++)
                {
                    var sample = path.Samples[reverse ? path.Samples.Count - 1 - i : i];
                    if (!map.Circuit && spine.Samples.Count == 0 && sample.Pos.z < minTrackZ) continue;
                    if (spine.Samples.Count != 0 && (spine.Last.Pos - sample.Pos).sqrMagnitude < 0.001f) continue;
                    spine.Add(sample);
                    openRoad.Add(mainRoad && i >= 4 && i < path.Samples.Count - 4);
                }
            }

            spine.SmoothForwards();
            return spine;
        }

        static List<int> PickCrossings(RoutePath spine, List<bool> openRoad,
                                       Func<Vector3, float, CrossingSite> crossingSite, int target)
        {
            var best = new List<int>();
            for (int pass = 0; pass < 2; pass++)
            {
                float endMargin = pass == 0 ? 45f : 32f;
                float span = pass == 0 ? 9f : 5f;
                float separation = pass == 0 ? 72f : 56f;
                var candidates = new List<(int index, float distance, float cost, int tunnel)>();
                for (int i = 1; i < spine.Samples.Count - 1; i++)
                {
                    float distance = spine.Dist[i];
                    if (distance < endMargin || distance > spine.Length - endMargin) continue;
                    if (!FlatCrossing(spine, i)) continue;
                    var sample = spine.Samples[i];
                    var before = spine.At(distance - span);
                    var after = spine.At(distance + span);
                    if (!sample.Road || !before.Road || !after.Road || sample.Bridge || before.Bridge || after.Bridge)
                        continue;
                    var centerSite = crossingSite(sample.Pos, 7f);
                    var beforeSite = crossingSite(before.Pos, 2f);
                    var afterSite = crossingSite(after.Pos, 2f);
                    if (centerSite == CrossingSite.Blocked || beforeSite == CrossingSite.Blocked || afterSite == CrossingSite.Blocked)
                        continue;

                    int tunnel = centerSite == CrossingSite.AboveTunnel || beforeSite == CrossingSite.AboveTunnel
                        || afterSite == CrossingSite.AboveTunnel ? 1 : 0;
                    float grade = Mathf.Abs(sample.Pos.y - before.Pos.y) + Mathf.Abs(sample.Pos.y - after.Pos.y);
                    float turn = 1f - Vector3.Dot(before.Forward, after.Forward);
                    float cost = grade * 18f + turn * 45f + (openRoad[i] ? 0f : 60f);
                    candidates.Add((i, distance, cost, tunnel));
                }

                // Сначала максимизируем число переездов, затем подбираем ровные участки
                // с равномерным распределением по трассе и максимум одним переездом над тоннелем.
                var previous = new int[candidates.Count + 1];
                for (int i = 1, compatible = 0; i <= candidates.Count; i++)
                {
                    while (compatible + 1 < i && candidates[i - 1].distance - candidates[compatible].distance >= separation)
                        compatible++;
                    previous[i] = compatible;
                }

                var costs = new float[candidates.Count + 1, target + 1, 2];
                var take = new bool[candidates.Count + 1, target + 1, 2];
                costs[0, 0, 1] = float.PositiveInfinity;
                for (int count = 1; count <= target; count++)
                    costs[0, count, 0] = costs[0, count, 1] = float.PositiveInfinity;

                for (int i = 1; i <= candidates.Count; i++)
                {
                    var candidate = candidates[i - 1];
                    costs[i, 0, 1] = float.PositiveInfinity;
                    for (int count = 1; count <= target; count++)
                    {
                        float ideal = (count - 0.5f) / target;
                        float cost = candidate.cost + Mathf.Abs(candidate.distance / spine.Length - ideal) * 80f;
                        for (int tunnels = 0; tunnels <= 1; tunnels++)
                        {
                            float skip = costs[i - 1, count, tunnels];
                            float use = tunnels >= candidate.tunnel
                                ? costs[previous[i], count - 1, tunnels - candidate.tunnel] + cost
                                : float.PositiveInfinity;
                            take[i, count, tunnels] = use < skip;
                            costs[i, count, tunnels] = Mathf.Min(skip, use);
                        }
                    }
                }

                int last = candidates.Count;
                for (int count = target; count > best.Count; count--)
                {
                    int tunnels = costs[last, count, 1] < costs[last, count, 0] ? 1 : 0;
                    if (float.IsPositiveInfinity(costs[last, count, tunnels])) continue;
                    best.Clear();
                    for (int i = last, remaining = count; remaining > 0;)
                    {
                        if (!take[i, remaining, tunnels])
                        {
                            i--;
                            continue;
                        }
                        var candidate = candidates[i - 1];
                        best.Add(candidate.index);
                        tunnels -= candidate.tunnel;
                        i = previous[i];
                        remaining--;
                    }
                    best.Reverse();
                    break;
                }
                if (best.Count == target) break;
            }
            return best;
        }

        static bool FlatCrossing(RoutePath spine, int index)
        {
            // Проверяем обе стороны всей группы путей, а не только среднюю точку переезда.
            const float halfSpan = 20f;
            float from = spine.Dist[index] - halfSpan;
            float to = spine.Dist[index] + halfSpan;
            for (int i = index; i > 0 && spine.Dist[i] > from; i--)
                if (!FlatRoadSegment(spine.Samples[i - 1], spine.Samples[i])) return false;
            for (int i = index; i + 1 < spine.Samples.Count && spine.Dist[i] < to; i++)
                if (!FlatRoadSegment(spine.Samples[i], spine.Samples[i + 1])) return false;
            return true;
        }

        static bool FlatRoadSegment(RouteSample a, RouteSample b)
        {
            if (!a.Road || !b.Road || a.Bridge || b.Bridge) return false;
            Vector3 delta = b.Pos - a.Pos;
            float distanceSquared = delta.x * delta.x + delta.z * delta.z;
            return Mathf.Abs(delta.y) < Mathf.Sqrt(distanceSquared) * CrossingGradeLimit;
        }

        readonly struct RoadStrip
        {
            public readonly RouteSample A, B;
            public readonly Vector2 Start, Delta;
            public readonly float LengthSquared;
            public readonly bool Flat;

            public RoadStrip(RouteSample a, RouteSample b)
            {
                A = a;
                B = b;
                Start = new Vector2(a.Pos.x, a.Pos.z);
                Delta = new Vector2(b.Pos.x - a.Pos.x, b.Pos.z - a.Pos.z);
                LengthSquared = Delta.sqrMagnitude;
                Flat = FlatRoadSegment(a, b);
            }
        }

        static Dictionary<int, List<RoadStrip>> IndexRoad(RouteMap map)
        {
            var index = new Dictionary<int, List<RoadStrip>>();
            foreach (var path in map.Paths)
            {
                if (!path.BuildMesh) continue;
                for (int i = 1; i < path.Samples.Count; i++)
                {
                    var a = path.Samples[i - 1];
                    var b = path.Samples[i];
                    if (!a.Road || !b.Road) continue;
                    var strip = new RoadStrip(a, b);
                    if (strip.LengthSquared < 0.0001f) continue;
                    float margin = Mathf.Max(a.HalfWidth, b.HalfWidth) + 2f;
                    int first = Mathf.FloorToInt((Mathf.Min(a.Pos.z, b.Pos.z) - margin) / RoadBucketSize);
                    int last = Mathf.FloorToInt((Mathf.Max(a.Pos.z, b.Pos.z) + margin) / RoadBucketSize);
                    for (int cell = first; cell <= last; cell++)
                    {
                        if (!index.TryGetValue(cell, out var strips)) index[cell] = strips = new List<RoadStrip>();
                        strips.Add(strip);
                    }
                }
            }
            return index;
        }

        bool IsTrackClear(Dictionary<int, List<RoadStrip>> road)
        {
            int steps = Mathf.CeilToInt(rail.Length / RoadScanStep);
            var positions = new Vector2[TrackCount][];
            for (int lane = 0; lane < TrackCount; lane++)
            {
                positions[lane] = new Vector2[steps + 1];
                for (int i = 0; i <= steps; i++)
                {
                    var point = TrackPosition(rail.Length * i / steps, TrackOffset(lane));
                    var flat = new Vector2(point.x, point.z);
                    positions[lane][i] = flat;
                    if (!road.TryGetValue(Mathf.FloorToInt(point.z / RoadBucketSize), out var strips)) continue;
                    foreach (var strip in strips)
                    {
                        float t = Mathf.Clamp01(Vector2.Dot(flat - strip.Start, strip.Delta) / strip.LengthSquared);
                        // Центры последней секции рельса находятся примерно в метре от наружного рельса.
                        float width = Mathf.Lerp(strip.A.HalfWidth, strip.B.HalfWidth, t) + 1.45f;
                        if ((flat - strip.Start - strip.Delta * t).sqrMagnitude >= width * width) continue;
                        if (!strip.Flat || Mathf.Abs(point.y - Mathf.Lerp(strip.A.Pos.y, strip.B.Pos.y, t)) > 0.26f)
                            return false;
                    }
                }
            }

            // После смещения крайнего пути проверяем петли и пересечения между соседними путями.
            const int window = 74;
            for (int lane = 0; lane < TrackCount; lane++)
                for (int other = lane; other < TrackCount; other++)
                    for (int i = 0; i < steps; i++)
                        for (int j = lane == other ? i + 2 : Mathf.Max(0, i - window);
                             j < steps && j <= i + window; j++)
                            if (SegmentsCross(positions[lane][i], positions[lane][i + 1],
                                              positions[other][j], positions[other][j + 1]))
                                return false;
            return true;
        }

        static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;

        static bool SegmentsCross(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
        {
            Vector2 along = b - a, across = d - c;
            float denominator = Cross(along, across);
            if (Mathf.Abs(denominator) < 0.001f) return false;
            float t = Cross(c - a, across) / denominator;
            float u = Cross(c - a, along) / denominator;
            return t > 0.001f && t < 0.999f && u > 0.001f && u < 0.999f;
        }

        void LayRoadside(RouteMap map, Dictionary<int, List<RoadStrip>> road, float ground)
        {
            ResetLayout();
            loop = false;
            const float step = 6f;
            const int radius = 6;
            float startZ = map.Start.Pos.z - YardApproachBack;
            float endZ = map.Start.Pos.z + 200f;
            foreach (var path in map.Paths)
                if (path.BuildMesh)
                    foreach (var sample in path.Samples)
                        endZ = Mathf.Max(endZ, sample.Pos.z + 55f);

            int count = Mathf.CeilToInt((endZ - startZ) / step) + 1;
            var edge = new float[count];
            var envelope = new float[count];
            float left = map.Start.Pos.x - YardApproachSide;
            for (int i = 0; i < count; i++)
            {
                float z = startZ + i * step;
                int cell = Mathf.FloorToInt(z / RoadBucketSize);
                if (road.TryGetValue(cell, out var strips))
                {
                    left = float.PositiveInfinity;
                    foreach (var strip in strips)
                        left = Mathf.Min(left, Mathf.Min(strip.A.Pos.x - strip.A.HalfWidth, strip.B.Pos.x - strip.B.HalfWidth));
                    left -= 17f;
                }
                if (z < map.Start.Pos.z + YardClearance) left = Mathf.Min(left, map.Start.Pos.x - 50f);
                edge[i] = left;
            }

            // Огибающая остаётся снаружи обочины даже после сглаживания.
            for (int i = 0; i < count; i++)
            {
                envelope[i] = edge[i];
                for (int j = Mathf.Max(0, i - radius); j <= Mathf.Min(count - 1, i + radius); j++)
                    envelope[i] = Mathf.Min(envelope[i], edge[j]);
            }

            for (int i = 0; i < count; i++)
            {
                float x = 0f;
                for (int offset = -radius; offset <= radius; offset++)
                    x += envelope[Mathf.Clamp(i + offset, 0, count - 1)];
                rail.Add(new RouteSample
                {
                    Pos = new Vector3(x / (radius * 2 + 1), ground + RailSurfaceOffset, startZ + i * step),
                    Forward = Vector3.forward
                });
            }
            rail.SmoothForwards();
        }

        void LayOutsideRoad(RouteMap map, float ground)
        {
            // Если рельеф не позволяет проложить безопасные переезды, оставляем поезда сбоку от карты.
            loop = false;
            crossings.Clear();
            rail.Samples.Clear();
            rail.Dist.Clear();
            float minX = map.Start.Pos.x - YardApproachSide;
            float maxZ = map.Start.Pos.z + 200f;
            foreach (var path in map.Paths)
                if (path.BuildMesh)
                    foreach (var sample in path.Samples)
                    {
                        minX = Mathf.Min(minX, sample.Pos.x - sample.HalfWidth - 36f);
                        maxZ = Mathf.Max(maxZ, sample.Pos.z + 65f);
                    }
            var start = new Vector3(minX, ground + RailSurfaceOffset, map.Start.Pos.z - YardApproachBack);
            rail.Add(new RouteSample { Pos = start, Forward = Vector3.forward });
            rail.Add(new RouteSample { Pos = new Vector3(minX, start.y, maxZ), Forward = Vector3.forward });
        }

        void LaySpine(RouteMap map, RoutePath spine, List<int> crossingIndices, float ground, float minTrackZ, float initialSide)
        {
            var track = new RoutePath("Путь за воротами");
            var anchors = new List<(int index, float height)> { (0, ground) };
            for (int i = 0; i < spine.Samples.Count; i++)
            {
                var sample = spine.Samples[i];
                float d = spine.Dist[i];
                float side = initialSide;
                float toCrossing = float.PositiveInfinity;
                bool atCrossing = false;
                foreach (int crossing in crossingIndices)
                {
                    float relative = d - spine.Dist[crossing];
                    toCrossing = Mathf.Min(toCrossing, Mathf.Abs(relative));
                    side *= 1f - 2f * Mathf.SmoothStep(0f, 1f, (relative + CrossingApproach) / (2f * CrossingApproach));
                    if (crossing != i) continue;
                    crossings.Add(sample);
                    atCrossing = true;
                }

                // Дорога может резко петлять, а рельсы берут среднее направление на длинном участке.
                var before = spine.At(d - SpineSmoothing).Pos;
                var after = spine.At(d + SpineSmoothing).Pos;
                var tangent = Vector3.ProjectOnPlane(after - before, Vector3.up).normalized;
                var center = (before + after + sample.Pos * 2f) * 0.25f;
                float blend = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0f, 38f, toCrossing));
                float detour = MaxDetour * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(50f, 150f, toCrossing));
                float endBlend = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0f, 90f, Mathf.Min(d, spine.Length - d)));
                var horizontal = Vector3.Lerp(sample.Pos, center, blend * endBlend);
                var right = Vector3.Cross(Vector3.up, tangent);
                var incoming = Vector3.ProjectOnPlane(sample.Pos - before, Vector3.up).normalized;
                var outgoing = Vector3.ProjectOnPlane(after - sample.Pos, Vector3.up).normalized;
                float turn = Vector3.Dot(outgoing - incoming, right);
                float offset = (sample.HalfWidth + 13f + detour * endBlend) * side;
                // На внутренней стороне поворота большой отступ может увезти пути за центр
                // кривизны и развернуть их назад. Оставляем место и для крайнего пути.
                if (offset * turn > 0f)
                {
                    float radius = (Mathf.Min(d, SpineSmoothing) + Mathf.Min(spine.Length - d, SpineSmoothing))
                        / (2f * Mathf.Abs(turn));
                    offset = Mathf.Sign(offset) * Mathf.Min(Mathf.Abs(offset), Mathf.Max(4f, radius - 15f));
                }
                horizontal += right * offset;
                sample.Pos.x = horizontal.x;
                sample.Pos.z = horizontal.z;
                sample.Pos.z = Mathf.Max(minTrackZ, sample.Pos.z);
                Vector3 delta = track.Samples.Count == 0 ? Vector3.zero : sample.Pos - track.Last.Pos;
                delta.y = 0f;
                if (track.Samples.Count == 0 || atCrossing || Vector3.Dot(delta, tangent) > 0.02f)
                    track.Add(sample);
                if (atCrossing) anchors.Add((track.Samples.Count - 1, sample.Pos.y));
            }

            anchors.Add((track.Samples.Count - 1, ground));
            track = SmoothTrack(SmoothTrack(track, anchors, minTrackZ), anchors, minTrackZ);
            var heightAnchors = new List<(float distance, float height)>();
            foreach (var anchor in anchors)
                heightAnchors.Add((track.Dist[anchor.index], anchor.height));
            int heightSegment = 0;
            for (int i = 0; i < track.Samples.Count; i++)
            {
                float distance = track.Dist[i];
                while (distance > heightAnchors[heightSegment + 1].distance) heightSegment++;
                var sample = track.Samples[i];
                sample.Pos.y = RailHeight(distance, heightAnchors[heightSegment], heightAnchors[heightSegment + 1], ground)
                    + RailSurfaceOffset;
                track.Samples[i] = sample;
            }

            track.SmoothForwards();
            if (!loop)
                AddYardApproach(map.Start.Pos, track.Samples[0], ground, map.Circuit);
            foreach (var sample in track.Samples) rail.Add(sample);

            var last = track.Last;
            if (loop)
                rail.Add(rail.Samples[0]);
            else if (!map.Circuit)
            {
                last.Pos += last.Forward * 50f;
                last.Pos.z = Mathf.Max(minTrackZ, last.Pos.z);
                rail.Add(last);
            }

            rail.SmoothForwards();
        }

        static RoutePath SmoothTrack(RoutePath source, List<(int index, float height)> anchors, float minTrackZ)
        {
            var result = new RoutePath(source.Name);
            int section = 0;
            for (int i = 0; i < source.Samples.Count; i++)
            {
                if (i > anchors[section + 1].index) section++;
                var sample = source.Samples[i];
                float d = source.Dist[i];
                float edge = Mathf.Min(d - source.Dist[anchors[section].index],
                                       source.Dist[anchors[section + 1].index] - d);
                var average = (source.At(d - 30f).Pos + source.At(d + 30f).Pos
                    + 2f * (source.At(d - 15f).Pos + source.At(d + 15f).Pos) + 3f * sample.Pos) / 9f;
                float weight = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0f, 22f, edge));
                sample.Pos = Vector3.Lerp(sample.Pos, average, weight);
                sample.Pos.z = Mathf.Max(minTrackZ, sample.Pos.z);
                result.Add(sample);
            }
            return result;
        }

        static float RailHeight(float distance, (float distance, float height) from,
                                (float distance, float height) to, float ground)
        {
            // На переезде уровень задаёт дорога. Между переездами пути идут по ровному
            // полотну или одним плавным уклоном, если для двух спусков мало места.
            float fromRamp = Mathf.Abs(from.height - ground) * 1.5f / MaxRailGrade;
            float toRamp = Mathf.Abs(to.height - ground) * 1.5f / MaxRailGrade;
            if (to.distance - from.distance < fromRamp + toRamp)
                return Mathf.SmoothStep(from.height, to.height, Mathf.InverseLerp(from.distance, to.distance, distance));

            float fromEnd = from.distance + fromRamp;
            float toStart = to.distance - toRamp;
            if (distance < fromEnd)
                return Mathf.SmoothStep(from.height, ground, Mathf.InverseLerp(from.distance, fromEnd, distance));
            if (distance > toStart)
                return Mathf.SmoothStep(ground, to.height, Mathf.InverseLerp(toStart, to.distance, distance));
            return ground;
        }

        void PlanEntries(RouteMap map)
        {
            if (loop)
            {
                // Три разных укрытия на кольце, по возможности вдали от дороги и друг от друга.
                for (int track = 0; track < TrackCount; track++)
                {
                    float target = rail.Length * (track + 0.5f) / TrackCount;
                    float best = float.NegativeInfinity;
                    entries[track] = target;
                    for (int i = 1; i + 1 < rail.Samples.Count; i += 3)
                    {
                        float along = rail.Dist[i];
                        if (Mathf.Abs(along - target) > rail.Length / (TrackCount * 3f)) continue;
                        var position = TrackPosition(along, TrackOffset(track));
                        if (!map.Nearest(position, true, out var road)) continue;
                        float score = road.Distance - road.Sample.HalfWidth - Mathf.Abs(along - target) * 0.03f;
                        if (score <= best) continue;
                        best = score;
                        entries[track] = along;
                    }
                }
            }
            else
            {
                entries[1] = Mathf.Min(secondEntryLimit, rail.Length * 0.29f);
                entries[ReverseTrack] = map.Circuit ? FindReverseEntry(map) : rail.Length;
            }

            if (crossings.Count == 0) return;
            // Сохраняем разнесение составов у среднего переезда, независимо от точки начала рейса.
            Vector3 reference = crossings[crossings.Count / 2].Pos;
            float nearest = float.PositiveInfinity;
            float crossingDistance = 0f;
            for (int i = 0; i < rail.Samples.Count; i++)
            {
                float distance = (rail.Samples[i].Pos - reference).sqrMagnitude;
                if (distance >= nearest) continue;
                nearest = distance;
                crossingDistance = rail.Dist[i];
            }

            for (int track = 0; track < TrackCount; track++)
            {
                float offset = (crossingDistance - entries[track]) * Direction(track);
                crossingOffsets[track] = loop ? Mathf.Repeat(offset, rail.Length) : offset;
            }
        }

        float FindReverseEntry(RouteMap map)
        {
            // На разомкнутом кольце дальний конец снова рядом с выездом.
            // Укрытие встречного состава должно стоять после последнего переезда и вне дороги.
            float earliest = rail.Length - 155f;
            if (crossings.Count > 0)
            {
                Vector3 lastCrossing = crossings[crossings.Count - 1].Pos;
                float best = float.PositiveInfinity;
                for (int i = 0; i < rail.Samples.Count; i++)
                {
                    float distance = (rail.Samples[i].Pos - lastCrossing).sqrMagnitude;
                    if (distance >= best) continue;
                    earliest = Mathf.Max(rail.Length - 155f, rail.Dist[i] + CrossingApproach + 10f);
                    best = distance;
                }
            }

            float entry = rail.Length;
            float bestClearance = float.NegativeInfinity;
            for (float along = rail.Length; along >= earliest; along -= TrackStep)
            {
                var position = TrackPosition(along, TrackOffset(ReverseTrack));
                if (!map.Nearest(position, true, out var road)) continue;
                float clearance = road.Distance - road.Sample.HalfWidth;
                if (clearance >= 11f) return along;
                if (clearance <= bestClearance) continue;
                bestClearance = clearance;
                entry = along;
            }
            return entry;
        }

        static int Direction(int track) => track == ReverseTrack ? -1 : 1;

        void AddYardApproach(Vector3 yardGate, RouteSample join, float ground, bool circuit)
        {
            // Линия идёт за пределами двора и начинает поворачивать к трассе уже у ворот.
            float side = circuit ? 1f : Mathf.Sign(join.Pos.x - yardGate.x);
            var far = new Vector3(yardGate.x + side * YardApproachSide, ground + RailSurfaceOffset, yardGate.z - YardApproachBack);
            var bend = far + Vector3.forward * 250f;
            var control = bend + Vector3.forward * (circuit ? 66f : 20f);
            var joinControl = join.Pos - join.Forward * 28f;
            joinControl.z = Mathf.Max(yardGate.z - 10f, joinControl.z);

            for (float distance = 0f; distance < 250f; distance += TrackStep)
            {
                var straight = join;
                straight.Pos = far + Vector3.forward * distance;
                rail.Add(straight);
            }
            for (int i = 0; i < 48; i++)
            {
                float t = i / 48f;
                var sample = join;
                var a = Vector3.Lerp(bend, control, t);
                var b = Vector3.Lerp(control, joinControl, t);
                var c = Vector3.Lerp(joinControl, join.Pos, t);
                sample.Pos = Vector3.Lerp(Vector3.Lerp(a, b, t), Vector3.Lerp(b, c, t), t);
                float nearGate = Mathf.InverseLerp(yardGate.z + YardClearance, yardGate.z + 6f, sample.Pos.z);
                float sideClearance = (YardShuffle.YardHalf + 12f) * Mathf.SmoothStep(0f, 1f, nearGate);
                float lateral = (sample.Pos.x - yardGate.x) * side;
                if (lateral < sideClearance && nearGate > 0f)
                    sample.Pos.x = yardGate.x + side * sideClearance;
                rail.Add(sample);
            }
        }

        void BuildEntryCover(int track)
        {
            TrainPose(track, 0f, out var position, out var rotation);
            rotation = Quaternion.LookRotation(Vector3.ProjectOnPlane(rotation * Vector3.forward, Vector3.up));
            var cover = new GameObject("Укрытие железнодорожного въезда");
            cover.transform.SetParent(transform, false);
            cover.transform.SetPositionAndRotation(position, rotation);
            const float width = 5.2f;
            CoverPiece(cover.transform, new Vector3(0f, 4f, 0f), new Vector3(width, 8f, 16f));
            if (loop) return;
            CoverPiece(cover.transform, new Vector3(0f, 7.5f, 16f), new Vector3(width, 2f, 16f));
            CoverPiece(cover.transform, new Vector3(-width / 2f + 0.4f, 3.5f, 16f), new Vector3(0.8f, 7f, 16f));
            CoverPiece(cover.transform, new Vector3(width / 2f - 0.4f, 3.5f, 16f), new Vector3(0.8f, 7f, 16f));
        }

        void CoverPiece(Transform cover, Vector3 position, Vector3 size)
        {
            var piece = GameObject.CreatePrimitive(PrimitiveType.Cube);
            piece.transform.SetParent(cover, false);
            piece.transform.localPosition = position;
            piece.transform.localScale = size;
            piece.GetComponent<MeshRenderer>().sharedMaterial = concrete;
            var collider = piece.GetComponent<Collider>();
            collider.enabled = false;
            Destroy(collider);
        }

        internal void ReserveGround(List<Bounds> areas)
        {
            foreach (var sample in rail.Samples)
                areas.Add(new Bounds(sample.Pos, new Vector3(18f, 0f, 18f)));
        }

        internal bool Obstructs(Bounds decoration)
        {
            // Поезд проходит на несколько метров выше рельсов. Проверяем основание скалы,
            // даже если её центр слишком высоко, чтобы считать его точкой на земле.
            decoration.Expand(new Vector3(16f, 5f, 16f));
            decoration.center -= Vector3.up * 2f;
            var min = decoration.min;
            var max = decoration.max;
            for (int i = 0; i + 1 < rail.Samples.Count; i++)
            {
                var a = rail.Samples[i].Pos;
                var b = rail.Samples[i + 1].Pos;
                if (Mathf.Max(a.x, b.x) < min.x || Mathf.Min(a.x, b.x) > max.x
                    || Mathf.Max(a.z, b.z) < min.z || Mathf.Min(a.z, b.z) > max.z
                    || Mathf.Max(a.y, b.y) < min.y || Mathf.Min(a.y, b.y) > max.y) continue;
                if (decoration.Contains(a) || decoration.Contains(b)) return true;
                var delta = b - a;
                float length = delta.magnitude;
                if (length > 0.001f && decoration.IntersectRay(new Ray(a, delta / length), out float distance)
                    && distance <= length) return true;
            }
            return false;
        }

        internal IEnumerator Build(RouteMap map, float ground)
        {
            for (int i = 0; i < TrackCount; i++)
            {
                var build = BuildTrack(map, ground, i);
                while (build.MoveNext()) yield return null;
                BuildEntryCover(i);
            }

            foreach (var crossing in crossings) BuildCrossbucks(crossing);
            int rotations = Mathf.CeilToInt((rail.Length + WagonsPerTrain * CarSpacing) / (ServiceSpacing * TrackCount));
            for (int i = 0; i < rotations * TrackCount; i++)
            {
                var model = trainModels[i % TrackCount];
                var train = new Rigidbody[WagonsPerTrain + 1];
                train[0] = MakeCar(model.engine);
                for (int car = 1; car < train.Length; car++)
                    train[car] = MakeCar(model.wagon);
                services.Add(train);
            }
        }

        IEnumerator BuildTrack(RouteMap map, float ground, int track)
        {
            var sourceVertices = trackMesh.vertices;
            var sourceNormals = trackMesh.normals;
            var sourceUv = trackMesh.uv;
            var sourceTriangles = trackMesh.triangles;
            var bounds = trackMesh.bounds;
            var pillars = new List<Bounds>();
            float trackOffset = TrackOffset(track);
            float trackStart = !loop && Direction(track) > 0 ? entries[track] : 0f;
            float length = TravelLength(track);
            int tiles = Mathf.CeilToInt(length / TrackStep);
            float tileLength = length / tiles;
            for (int first = 0; first < tiles; first += 32)
            {
                int count = Mathf.Min(32, tiles - first);
                int verticesPerTile = sourceVertices.Length + 4;
                int indicesPerTile = sourceTriangles.Length + 6;
                var vertices = new Vector3[count * verticesPerTile];
                var normals = new Vector3[vertices.Length];
                var uv = new Vector2[vertices.Length];
                var indices = new int[count * indicesPerTile];
                for (int tile = 0; tile < count; tile++)
                {
                    int start = tile * verticesPerTile;
                    for (int i = 0; i < sourceVertices.Length; i++)
                    {
                        var v = sourceVertices[i];
                        float along = trackStart + tileLength * (first + tile + Mathf.InverseLerp(bounds.min.z, bounds.max.z, v.z));
                        Pose(along, trackOffset, out var pos, out var rot);
                        vertices[start + i] = pos + rot * new Vector3(v.x * TrainScale, (v.y - bounds.min.y) * 0.95f, 0f);
                        normals[start + i] = rot * sourceNormals[i];
                        uv[start + i] = sourceUv[i];
                    }
                    for (int i = 0; i < sourceTriangles.Length; i++)
                        indices[tile * indicesPerTile + i] = start + sourceTriangles[i];
                    float middle = trackStart + tileLength * (first + tile + 0.5f);
                    Pose(middle, trackOffset, out var sleeper, out var sleeperRotation);
                    int v0 = start + sourceVertices.Length;
                    Vector3 width = sleeperRotation * Vector3.right * 1.14f;
                    Vector3 depth = sleeperRotation * Vector3.forward * 0.17f;
                    vertices[v0] = sleeper - width - depth;
                    vertices[v0 + 1] = sleeper - width + depth;
                    vertices[v0 + 2] = sleeper + width + depth;
                    vertices[v0 + 3] = sleeper + width - depth;
                    for (int i = 0; i < 4; i++)
                    {
                        normals[v0 + i] = sleeperRotation * Vector3.up;
                        uv[v0 + i] = new Vector2(0.34f, 0.39f);
                    }
                    int index = tile * indicesPerTile + sourceTriangles.Length;
                    indices[index] = v0; indices[index + 1] = v0 + 1; indices[index + 2] = v0 + 2;
                    indices[index + 3] = v0; indices[index + 4] = v0 + 2; indices[index + 5] = v0 + 3;

                    if ((first + tile) % 4 == 0 && sleeper.y > ground + 0.8f && map.IsClear(sleeper, 2f))
                    {
                        float height = sleeper.y - ground + 1f;
                        pillars.Add(new Bounds(sleeper - Vector3.up * (height * 0.5f), new Vector3(2.15f, height, 0.65f)));
                    }
                }

                var mesh = new Mesh { name = "Рельсы" };
                mesh.vertices = vertices;
                mesh.normals = normals;
                mesh.uv = uv;
                mesh.triangles = indices;
                trackMeshes.Add(mesh);
                var chunk = new GameObject("Рельсы");
                chunk.layer = gameObject.layer;
                chunk.transform.SetParent(transform, false);
                chunk.AddComponent<MeshFilter>().sharedMesh = mesh;
                var renderer = chunk.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = material;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                yield return null;
            }

            foreach (var pillar in pillars)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.name = "Опора путей";
                go.layer = gameObject.layer;
                go.transform.SetParent(transform, false);
                go.transform.position = pillar.center;
                go.transform.localScale = pillar.size;
                go.GetComponent<MeshRenderer>().sharedMaterial = concrete;
                var collider = go.GetComponent<Collider>();
                collider.enabled = false;
                Destroy(collider);
            }
        }

        void BuildCrossbucks(RouteSample crossing)
        {
            for (int side = -1; side <= 1; side += 2)
            {
                var post = new GameObject("Знак железнодорожного переезда");
                post.transform.SetParent(transform, false);
                post.transform.SetPositionAndRotation(
                    crossing.Pos + crossing.Right * (crossing.HalfWidth + 1.2f) * side - crossing.Forward * 15f * side,
                    Quaternion.LookRotation(crossing.Forward));

                SignPiece(post.transform, new Vector3(0f, 1.05f, 0f), new Vector3(0.11f, 2.1f, 0.11f), 0f);
                SignPiece(post.transform, Vector3.up * 2.15f, new Vector3(1.2f, 0.17f, 0.08f), 40f);
                SignPiece(post.transform, Vector3.up * 2.15f, new Vector3(1.2f, 0.17f, 0.08f), -40f);
            }
        }

        void SignPiece(Transform parent, Vector3 center, Vector3 size, float angle)
        {
            var piece = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var collider = piece.GetComponent<Collider>();
            collider.enabled = false;
            Destroy(collider);
            piece.transform.SetParent(parent, false);
            piece.transform.localPosition = center;
            piece.transform.localScale = size;
            piece.transform.localRotation = Quaternion.Euler(0f, 0f, angle);
        }

        Rigidbody MakeCar(GameObject source)
        {
            var car = Instantiate(source, transform);
            car.name = source.name;
            car.transform.localScale = Vector3.one * TrainScale;
            var renderers = car.GetComponentsInChildren<MeshRenderer>(true);
            var bounds = renderers[0].bounds;
            foreach (var renderer in renderers)
            {
                bounds.Encapsulate(renderer.bounds);
                var materials = renderer.sharedMaterials;
                for (int i = 0; i < materials.Length; i++) materials[i] = material;
                renderer.sharedMaterials = materials;
            }

            var body = car.AddComponent<Rigidbody>();
            body.isKinematic = true;
            body.detectCollisions = !networked || NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer;
            if (body.detectCollisions)
            {
                var box = car.AddComponent<BoxCollider>();
                box.center = car.transform.InverseTransformPoint(bounds.center);
                box.size = bounds.size / TrainScale * 0.88f;
                body.interpolation = RigidbodyInterpolation.Interpolate;
                body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            }
            car.SetActive(false);
            return body;
        }

        void OnEnable() => soloStart = Time.timeAsDouble;

        void FixedUpdate()
        {
            if (!networked)
                DriveServices(Time.fixedTimeAsDouble - soloStart, true);
            else if (NetworkManager.Singleton is { IsListening: true, IsServer: true } net)
                DriveServices(net.ServerTime.Time, true);
        }

        void Update()
        {
            if (networked && NetworkManager.Singleton is { IsListening: true, IsServer: false, IsConnectedClient: true } net)
                DriveServices(net.ServerTime.Time, false);
        }

        void DriveServices(double clock, bool simulatePhysics)
        {
            if (services.Count == 0) return;
            float cycleLength = ServiceSpacing * services.Count;
            for (int i = 0; i < services.Count; i++)
            {
                int track = i % TrackCount;
                double distance = clock * Speed + phase - ServiceSpacing * i + crossingOffsets[track];
                float head = (float)(distance - Math.Floor(distance / cycleLength) * cycleLength);
                var train = services[i];
                for (int car = 0; car < train.Length; car++)
                {
                    float along = head - car * CarSpacing;
                    DriveCar(train[car], along, track, simulatePhysics);
                }
            }
        }

        void DriveCar(Rigidbody car, float along, int track, bool simulatePhysics)
        {
            if (along < 0f || along > TravelLength(track))
            {
                if (car.gameObject.activeSelf) car.gameObject.SetActive(false);
                return;
            }

            TrainPose(track, along, out var position, out var rotation);
            position += Vector3.up * 0.1f;
            if (!simulatePhysics)
            {
                car.transform.SetPositionAndRotation(position, rotation);
                if (!car.gameObject.activeSelf) car.gameObject.SetActive(true);
            }
            else if (!car.gameObject.activeSelf || (car.position - position).sqrMagnitude > 100f)
            {
                car.position = position;
                car.rotation = rotation;
                car.gameObject.SetActive(true);
            }
            else
            {
                car.MovePosition(position);
                car.MoveRotation(rotation);
            }
        }

        float TravelLength(int track) =>
            loop ? rail.Length : Direction(track) < 0 ? entries[track] : rail.Length - entries[track];

        void TrainPose(int track, float along, out Vector3 position, out Quaternion rotation)
        {
            int direction = Direction(track);
            Pose(entries[track] + direction * along, TrackOffset(track), out position, out rotation);
            if (direction < 0) rotation *= Quaternion.Euler(0f, 180f, 0f);
        }

        Vector3 TrackPosition(float along, float trackOffset)
        {
            float distance = loop ? Mathf.Repeat(along, rail.Length) : along;
            var tangent = RoutePoint(distance + 6f) - RoutePoint(distance - 6f);
            tangent.y = 0f;
            var right = Vector3.Cross(Vector3.up, tangent.normalized);
            return rail.At(distance).Pos + right * trackOffset;
        }

        Vector3 RoutePoint(float along) => rail.At(loop ? Mathf.Repeat(along, rail.Length) : along).Pos;

        void Pose(float along, float trackOffset, out Vector3 position, out Quaternion rotation)
        {
            position = TrackPosition(along, trackOffset);
            var forward = TrackPosition(along + 1.4f, trackOffset) - TrackPosition(along - 1.4f, trackOffset);
            rotation = Quaternion.LookRotation(forward.sqrMagnitude > 0.0001f ? forward : rail.At(along).Forward);
        }

        void OnDestroy()
        {
            foreach (var mesh in trackMeshes) Destroy(mesh);
            if (material != null) Destroy(material);
            if (concrete != null) Destroy(concrete);
        }
    }
}
