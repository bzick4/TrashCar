using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace JunkRace
{
    /// <summary>
    /// Тестовая трасса (меню «JunkRace → Тестовая трасса»): в одиночной игре на сборке сама собирает
    /// случайную машину — свободный корпус рядом с игроком, колёса, мотор, руль, кресло из деталей свалки
    /// (и из куч) — и сажает игрока за руль. Дальше G — и в путь. В обычной игре не работает.
    /// </summary>
    public class TestCarBuilder : MonoBehaviour
    {
        public static void Begin()
        {
            if (!RouteGenerator.TestRoute || GameSession.Mode != GameMode.Solo || GameSession.IsDevRoom || GameSession.IsTutorial) return;
            if (FindFirstObjectByType<TestCarBuilder>() != null) return;
            new GameObject("Тестовая машина").AddComponent<TestCarBuilder>();
        }

        IEnumerator Start()
        {
            // Ждём сборку: свалка разложена, взаимодействие открыто, игрок есть.
            while (RaceTimer.Instance == null || RaceTimer.Instance.Phase != MatchPhase.Build || RaceTimer.InteractionLocked
                   || !(World.Local is MonoBehaviour)) yield return null;
            yield return new WaitForSeconds(0.5f);
            Build();
            Destroy(gameObject);
        }

        static List<AttachPoint> PointsOf(Part owner) => AttachPoint.All.Where(p => p != null && p.Owner == owner).ToList();

        void Build()
        {
            var rnd = new System.Random();
            var me = ((MonoBehaviour)World.Local).transform;

            // Свободный корпус, на который встают хотя бы 3 колеса; лучше — с мотором и рулём.
            var hulls = Vehicle.All.Where(v => v != null && v.RootPart != null && v.PartCount <= 1 && !v.RootPart.IsCarried
                                               && PointsOf(v.RootPart).Count(p => p.Slot == SlotType.Wheel) >= 3)
                                   .OrderByDescending(v => PointsOf(v.RootPart).Select(p => p.Slot).Distinct().Count())
                                   .ThenBy(_ => rnd.Next()).ToList();
            if (hulls.Count == 0) { Debug.LogWarning("[JunkRace] Тестовая машина: нет подходящего корпуса"); return; }
            var best = PointsOf(hulls[0].RootPart).Select(p => p.Slot).Distinct().Count();
            var pool = hulls.Where(v => PointsOf(v.RootPart).Select(p => p.Slot).Distinct().Count() == best).ToList();
            var car = pool[rnd.Next(pool.Count)];

            // Корпус — рядом с игроком, носом туда же, куда смотрит игрок.
            Vector3 fwd = Vector3.ProjectOnPlane(me.forward, Vector3.up).normalized;
            if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
            Vector3 at = me.position + fwd * 5f + Vector3.up * 1.2f;
            var rot = Quaternion.LookRotation(fwd, Vector3.up);
            car.transform.SetPositionAndRotation(at, rot);
            if (car.Body != null)
            {
                car.Body.position = at; car.Body.rotation = rot;
                car.Body.linearVelocity = Vector3.zero; car.Body.angularVelocity = Vector3.zero;
            }

            // Детали: всё свободное на свалке (спрятанное в кучах — тоже), кроме корпусов.
            var free = FindObjectsByType<Part>(FindObjectsSortMode.None)
                .Where(p => p != null && p.Vehicle == null && p.MountedOn == null && !p.IsCarried && p.GetComponent<Vehicle>() == null)
                .OrderBy(_ => rnd.Next()).ToList();

            int attached = 0;
            var order = new[] { SlotType.Wheel, SlotType.Engine, SlotType.Steering, SlotType.Seat };
            foreach (var slot in order)
                foreach (var point in PointsOf(car.RootPart).Where(p => p.Slot == slot && !p.Occupied))
                {
                    var part = free.FirstOrDefault(p => p.NativeSlot == slot && car.CanAttach(p, point));
                    if (part == null) continue;
                    if (part.IsHidden) part.Reveal(point.transform.position, point.transform.rotation, Vector3.zero);
                    if (car.Attach(part, point)) { free.Remove(part); attached++; }
                }

            // Мотора нет места — пробуем любую свободную точку.
            if (!car.Parts.Any(p => p.NativeSlot == SlotType.Engine))
                foreach (var point in PointsOf(car.RootPart).Where(p => !p.Occupied))
                {
                    var engine = free.FirstOrDefault(p => p.NativeSlot == SlotType.Engine && car.CanAttach(p, point));
                    if (engine == null) continue;
                    if (engine.IsHidden) engine.Reveal(point.transform.position, point.transform.rotation, Vector3.zero);
                    if (car.Attach(engine, point)) { free.Remove(engine); attached++; break; }
                }

            World.ServerEnter(World.LocalId, car);
            Debug.Log($"[JunkRace] Тестовая машина: «{car.RootPart.DisplayName}», деталей {attached}: " +
                      string.Join(", ", car.Parts.Select(p => p.DisplayName)));
        }
    }
}
