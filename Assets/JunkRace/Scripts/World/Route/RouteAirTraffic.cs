using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Rendering;

namespace JunkRace
{
    /// <summary>Декоративные полёты по общему времени матча. След вычисляется из маршрута самолёта.</summary>
    public sealed class RouteAirTraffic : MonoBehaviour
    {
        const float ZoneLength = 950f;
        const int TrailSamples = 24;
        const float LowSpeed = 56f;
        const float LowPeriod = 27f;
        const float LowHalfLength = 230f;
        const float FlybyHeight = 6.6f;
        const float FlybyApproach = 155f;
        const float HighSpeed = 105f;

        sealed class Zone
        {
            public Vector3 center;
            public Transform airliner;
            public LineRenderer contrail;
            public float phase;
        }

        readonly List<Zone> zones = new List<Zone>();
        readonly List<Material> materials = new List<Material>();
        readonly Vector3[] trailPoints = new Vector3[TrailSamples];
        RouteMap routeMap;
        HashSet<RoutePath> reversedPaths;
        Transform redPlane, airship;
        LineRenderer redSmoke;
        Vector3 airshipCenter, flybyCenter, flybyHeading;
        bool flybyHeadOn;
        float flybyStart = -LowPeriod, nextFlybyCheck;
        bool flybyActive;
        int flybyCount;
        double startTime;
        bool networked;
        int seed;

        internal static void Build(Transform parent, RouteMap map, List<RoutePath> reversed, int seed)
        {
            var root = new GameObject("Воздушное движение");
            root.transform.SetParent(parent, false);
            var traffic = root.AddComponent<RouteAirTraffic>();
            traffic.networked = GameSession.Mode != GameMode.Solo;
            traffic.seed = seed;
            traffic.BuildZones(map, reversed);
        }

        void BuildZones(RouteMap map, List<RoutePath> reversed)
        {
            var plane = Load("PUSHILIN_Plane");
            var ship = Load("Airship");
            var airliner = Load("Airplane");
            var trailShader = Resources.Load<Shader>("AirTraffic/AirTrail");
            var modelShader = Shader.Find("JunkRace/Toon");
            if (plane.model == null || ship.model == null || airliner.model == null ||
                plane.texture == null || ship.texture == null || airliner.texture == null ||
                trailShader == null || modelShader == null)
            {
                Debug.LogWarning("JunkRace: не удалось загрузить модели или шейдеры воздушного движения.");
                return;
            }

            var smokeMaterial = new Material(trailShader);
            materials.Add(smokeMaterial);
            var planeMaterial = Paint(modelShader, plane.texture);
            var shipMaterial = Paint(modelShader, ship.texture);
            var linerMaterial = Paint(modelShader, airliner.texture);

            float far = map.Start.Pos.z;
            routeMap = map;
            reversedPaths = new HashSet<RoutePath>(reversed);
            redPlane = Aircraft(plane.model, planeMaterial, "Низкий самолёт", 27f, reverse: true);
            redSmoke = Trail("Дымка самолёта", smokeMaterial, new Color(0.78f, 0.8f, 0.85f), 0.38f, 0.8f, 2.6f);
            airship = Aircraft(ship.model, shipMaterial, "Дирижабль", 80f);
            var route = new List<RouteSample>();
            foreach (var path in map.Paths)
            {
                if (!path.BuildMesh) continue;
                foreach (var sample in path.Samples)
                {
                    if (!sample.Road) continue;
                    far = Mathf.Max(far, sample.Pos.z);
                    route.Add(sample);
                }
            }

            int count = Mathf.Max(1, Mathf.CeilToInt((far - map.Start.Pos.z) / ZoneLength));
            float zoneLength = (far - map.Start.Pos.z) / count;
            for (int i = 0; i < count; i++)
            {
                float centerZ = map.Start.Pos.z + (i + 0.5f) * zoneLength;
                var anchor = map.Start;
                float best = float.PositiveInfinity;
                float top = map.Start.Pos.y;
                foreach (var sample in route)
                {
                    float distance = Mathf.Abs(sample.Pos.z - centerZ);
                    if (distance < zoneLength * 0.5f + 180f) top = Mathf.Max(top, sample.Pos.y);
                    if (distance >= best) continue;
                    anchor = sample;
                    best = distance;
                }
                zones.Add(new Zone
                {
                    phase = (seed % 71) * 0.27f + i * 19.7f,
                    center = new Vector3(anchor.Pos.x, top, anchor.Pos.z),
                    airliner = Aircraft(airliner.model, linerMaterial, "Высокий лайнер", 34f),
                    contrail = Trail("След лайнера", smokeMaterial, new Color(0.98f, 0.98f, 1f), 0.73f, 0.9f, 5.5f)
                });
            }
            airshipCenter = zones[zones.Count / 2].center;
        }

        static (GameObject model, Texture2D texture) Load(string name) =>
            (Resources.Load<GameObject>("AirTraffic/" + name), Resources.Load<Texture2D>("AirTraffic/" + name + "-Texture"));

        Material Paint(Shader shader, Texture texture)
        {
            var material = new Material(shader);
            material.SetTexture("_BaseMap", texture);
            material.SetFloat("_NoiseStrength", 0f);
            materials.Add(material);
            return material;
        }

        Transform Aircraft(GameObject model, Material material, string label, float size, bool reverse = false)
        {
            var pivot = new GameObject(label).transform;
            pivot.SetParent(transform, false);
            var art = Instantiate(model, pivot).transform;
            if (reverse) art.localRotation = Quaternion.Euler(0f, 180f, 0f);
            var renderers = art.GetComponentsInChildren<MeshRenderer>();
            var bounds = renderers[0].bounds;
            foreach (var renderer in renderers)
            {
                bounds.Encapsulate(renderer.bounds);
                renderer.sharedMaterial = material;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
            }
            float scale = size / Mathf.Max(bounds.size.x, bounds.size.z);
            art.localScale *= scale;
            art.localPosition = -pivot.InverseTransformPoint(bounds.center) * scale;
            pivot.gameObject.SetActive(false);
            return pivot;
        }

        LineRenderer Trail(string label, Material material, Color color, float alpha, float width, float tailWidth)
        {
            var trail = new GameObject(label).AddComponent<LineRenderer>();
            trail.transform.SetParent(transform, false);
            trail.sharedMaterial = material;
            trail.useWorldSpace = true;
            trail.shadowCastingMode = ShadowCastingMode.Off;
            trail.receiveShadows = false;
            trail.positionCount = TrailSamples;
            trail.widthCurve = AnimationCurve.Linear(0f, width, 1f, tailWidth);
            var gradient = new Gradient();
            gradient.SetKeys(new[] { new GradientColorKey(color, 0f), new GradientColorKey(color, 1f) },
                new[] { new GradientAlphaKey(alpha, 0f), new GradientAlphaKey(alpha * 0.65f, 0.65f), new GradientAlphaKey(0f, 1f) });
            trail.colorGradient = gradient;
            trail.enabled = false;
            return trail;
        }

        void OnEnable() => startTime = Time.timeAsDouble;

        void LateUpdate()
        {
            var timer = RaceTimer.Instance;
            float raceElapsed = timer != null && timer.Phase == MatchPhase.Race ? timer.Elapsed : -1f;
            if (!networked)
                Animate(Time.timeAsDouble - startTime, raceElapsed);
            else if (NetworkManager.Singleton is { IsListening: true } net && (net.IsServer || net.IsConnectedClient))
                Animate(net.ServerTime.Time, raceElapsed);
        }

        void Animate(double clock, float raceElapsed)
        {
            if (airship == null) return;
            float angle = (float)(clock % 150.0) * (2f * Mathf.PI / 150f);
            var drift = new Vector3(170f * Mathf.Cos(angle), 4f * Mathf.Sin(angle * 2f), 105f * Mathf.Sin(angle));
            var heading = new Vector3(-170f * Mathf.Sin(angle), 0f, 105f * Mathf.Cos(angle));
            if (!airship.gameObject.activeSelf) airship.gameObject.SetActive(true);
            airship.SetPositionAndRotation(airshipCenter + new Vector3(250f, 115f, 80f) + drift, Quaternion.LookRotation(heading));

            foreach (var zone in zones)
            {
                double time = clock + zone.phase;
                var high = new Vector3(0.87f, 0f, 0.49f).normalized;
                var highCenter = zone.center + new Vector3(-90f, 78f, 140f);
                Fly(zone.airliner, zone.contrail, highCenter, high, (float)((time + 11d) % 65d),
                    HighSpeed, 1450f, 14f, 15f);
            }

            UpdateFlyby(raceElapsed);
        }

        void UpdateFlyby(float raceElapsed)
        {
            // Пролёт — локальный эффект: в коопе игроки могут быть на разных участках.
            var driver = World.Local?.Driving;
            var observer = driver != null ? driver.transform : World.Local?.HoldPoint;
            if (observer == null || raceElapsed < 0f || InTunnel(observer.position))
            {
                flybyActive = false;
                nextFlybyCheck = 0f;
            }
            else if (raceElapsed >= nextFlybyCheck)
            {
                nextFlybyCheck = raceElapsed + 0.2f;
                bool onRoad = routeMap.Nearest(observer.position, true, out var hit)
                    && hit.Path.BuildMesh && !InTunnel(hit.Sample.Pos);
                float progress = onRoad ? FlybyProgress(routeMap, hit) : -1f;
                if (progress < 0.2f || progress > 0.8f) flybyActive = false;
                else if (raceElapsed - flybyStart >= LowPeriod)
                {
                    flybyHeadOn = ((seed + flybyCount++) & 1) == 0;
                    flybyStart = raceElapsed;
                    flybyActive = true;
                    AimFlyby(hit, driver, LowHalfLength / LowSpeed, 1f);
                }
                else if (flybyActive && raceElapsed - flybyStart < 2.5f)
                {
                    // Пока самолёт далеко, поправляем точку встречи по движению игрока.
                    float remaining = LowHalfLength / LowSpeed - (raceElapsed - flybyStart);
                    AimFlyby(hit, driver, remaining, 0.35f);
                }
            }

            if (flybyActive)
                Fly(redPlane, redSmoke, flybyCenter, flybyHeading, raceElapsed - flybyStart,
                    LowSpeed, LowHalfLength, 13f, flybyCount % 3 != 0 ? 3.8f : 0f, 28f);
            else
            {
                redPlane.gameObject.SetActive(false);
                redSmoke.enabled = false;
            }
        }

        void AimFlyby(RouteMap.Hit hit, Vehicle driver, float secondsToPass, float blend)
        {
            int sign = reversedPaths.Contains(hit.Path) ? -1 : 1;
            float speed = driver != null ? Mathf.Clamp(driver.ForwardSpeed, 0f, 40f) : 0f;
            var target = hit.Path.At(hit.Along + speed * secondsToPass * sign);
            if (!target.Road) target = hit.Sample;
            if (InTunnel(target.Pos))
            {
                flybyActive = false;
                return;
            }
            var forward = Vector3.ProjectOnPlane(target.Forward * sign, Vector3.up);
            if (forward.sqrMagnitude < 0.001f) forward = Vector3.forward;
            var heading = forward.normalized * (flybyHeadOn ? -1f : 1f);
            var center = target.Pos;
            center.y = Mathf.Max(center.y, routeMap.Start.Pos.y) + FlybyHeight;
            flybyCenter = Vector3.Lerp(flybyCenter, center, blend);
            flybyHeading = blend >= 1f ? heading : Vector3.Slerp(flybyHeading, heading, blend);
        }

        static bool InTunnel(Vector3 position) =>
            RouteGenerator.Instance != null && RouteGenerator.Instance.InsideTunnel(position);

        static float FlybyProgress(RouteMap map, RouteMap.Hit hit)
        {
            float progress = hit.Path.StartProgress + hit.Along * hit.Path.ProgressScale;
            if (!map.Circuit) return map.FinishProgress > 0f ? progress / map.FinishProgress : -1f;
            if (progress < map.LapStart || map.LapLength <= 0f) return -1f;
            return Mathf.Repeat(progress - map.LapLine, map.LapLength) / map.LapLength;
        }

        void Fly(Transform aircraft, LineRenderer trail, Vector3 center, Vector3 heading,
                 float time, float speed, float halfLength, float exhaust, float lifetime, float approachLift = 0f)
        {
            float duration = 2f * halfLength / speed;
            bool flying = time >= 0f && time <= duration;
            if (aircraft.gameObject.activeSelf != flying) aircraft.gameObject.SetActive(flying);
            if (flying)
            {
                float along = time * speed - halfLength;
                var ahead = FlightPoint(center, heading, along + 2f, approachLift);
                var behind = FlightPoint(center, heading, along - 2f, approachLift);
                aircraft.SetPositionAndRotation(FlightPoint(center, heading, along, approachLift),
                    Quaternion.LookRotation(ahead - behind));
            }
            bool leaveTrail = lifetime > 0f && time > 0f && time < duration + lifetime;
            trail.enabled = leaveTrail;
            if (!leaveTrail) return;

            float newest = Mathf.Min(time, duration);
            float oldest = Mathf.Max(0f, time - lifetime);
            var wind = Vector3.Cross(Vector3.up, heading);
            for (int i = 0; i < TrailSamples; i++)
            {
                float emitted = Mathf.Lerp(newest, oldest, i / (TrailSamples - 1f));
                float age = time - emitted;
                trailPoints[i] = FlightPoint(center, heading, emitted * speed - halfLength - exhaust, approachLift)
                    + wind * (age * 0.9f) + Vector3.up * (age * 0.15f);
            }
            trail.SetPositions(trailPoints);
            trail.widthMultiplier = 1f - Mathf.Clamp01((time - duration) / lifetime);
        }

        static Vector3 FlightPoint(Vector3 center, Vector3 heading, float along, float approachLift)
        {
            // На дальней дистанции самолёт выше, перед дорогой снижается и снова набирает высоту.
            float lift = approachLift * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(34f, FlybyApproach, Mathf.Abs(along)));
            return center + heading * along + Vector3.up * lift;
        }

        void OnDestroy()
        {
            foreach (var material in materials) Destroy(material);
        }
    }
}
