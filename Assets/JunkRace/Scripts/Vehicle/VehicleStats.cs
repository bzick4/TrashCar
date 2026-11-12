using System.Collections.Generic;
using UnityEngine;

namespace JunkRace
{
    /// <summary>Оценка того, как поедет машина из такого набора деталей.</summary>
    public struct VehicleEstimate
    {
        public float Mass;          // кг
        public float PowerHp;       // л.с.
        public float TopSpeedKmh;   // км/ч, на ровной дороге
        public float Accel;         // м/с² на малой скорости
        public int Wheels;
        public float SteerQuality;  // 0..1, качество руля
        public bool CanDrive;       // есть чем ехать
        public bool CanSteer;       // есть чем рулить
        public bool Floats;         // не утонет
    }

    /// <summary>
    /// Та же модель, что в Vehicle, только «на бумаге»: можно прикинуть машину с деталью и без неё.
    /// Считает по парам (деталь, слот): место решает, как деталь работает.
    /// Числа приблизительные, но порядок и направление (лучше/хуже) честные.
    /// </summary>
    public static class VehicleStats
    {
        public const float MinSpeedForPower = 3f;   // ниже этой скорости тяга мотора не растёт бесконечно

        public static VehicleEstimate Estimate(IEnumerable<(Part part, SlotType slot)> mounts)
        {
            float mass = 0f, power = 0f, thrust = 0f, drag = 0f, traction = 0f, rolling = 0f, buoyancy = 0f, steer = 0f;
            int wheels = 0;

            foreach (var (p, slot) in mounts)
            {
                if (p == null) continue;
                // Масса, воздух и плавучесть — в любом слоте.
                mass += p.Mass;
                drag += p.AirDrag;
                buoyancy += p.Buoyancy;
                switch (slot)
                {
                    case SlotType.Wheel:
                        wheels++;
                        traction += p.AsWheelTraction;
                        rolling += p.AsWheelRollingResistance;
                        break;
                    case SlotType.Steering:
                        steer += p.AsSteering;
                        break;
                    case SlotType.Engine:
                        power += p.AsEnginePower;
                        break;
                    default:
                        if (p is ThrusterPart t && !t.UsesSpecialButton) thrust += t.Thrust;
                        break;
                }
            }

            float Drive(float v) =>
                (wheels > 0 && power > 0f ? Mathf.Min(power / Mathf.Max(v, MinSpeedForPower), traction) : 0f) + thrust;
            float Net(float v) => Drive(v) - drag * v * v - rolling * v;

            float steerQuality = Mathf.Clamp01(steer);
            var est = new VehicleEstimate
            {
                Mass = mass,
                PowerHp = power / 735.5f,
                Wheels = wheels,
                SteerQuality = steerQuality,
                CanSteer = steerQuality > 0f,
                Floats = buoyancy > mass * Physics.gravity.magnitude,
                CanDrive = Drive(1f) > 0f,
            };

            if (est.CanDrive && mass > 0f)
            {
                // Максималка — где тяга сравнялась с сопротивлением. Ищем делением пополам.
                float lo = 0f, hi = 80f;
                if (Net(hi) > 0f) lo = hi;
                for (int i = 0; i < 30 && hi - lo > 0.01f; i++)
                {
                    float mid = (lo + hi) * 0.5f;
                    if (Net(mid) > 0f) lo = mid; else hi = mid;
                }
                est.TopSpeedKmh = lo * 3.6f;
                est.Accel = Mathf.Max(0f, Net(MinSpeedForPower)) / mass;
            }
            return est;
        }

        static readonly List<(Part, SlotType)> buffer = new List<(Part, SlotType)>();

        static void Fill(Vehicle vehicle)
        {
            buffer.Clear();
            foreach (var p in vehicle.Parts) buffer.Add((p, p.Slot));
        }

        public static VehicleEstimate Estimate(Vehicle vehicle)
        {
            Fill(vehicle);
            return Estimate(buffer);
        }

        /// <summary>Как поедет машина, если снять с неё эту деталь (и всё, что на ней висит).</summary>
        public static VehicleEstimate EstimateWithout(Vehicle vehicle, Part removed)
        {
            Fill(vehicle);
            buffer.RemoveAll(x => x.Item1 == removed || (x.Item1 != null && x.Item1.MountedOn != null && x.Item1.MountedOn.Owner == removed));
            return Estimate(buffer);
        }

        /// <summary>
        /// Как поедет машина, если прикрутить к ней ещё и эту деталь в эту точку.
        /// Точки нет — считаем, что деталь встанет в свой родной слот.
        /// </summary>
        public static VehicleEstimate EstimateWith(Vehicle vehicle, Part extra, AttachPoint point = null)
        {
            Fill(vehicle);
            if (extra != null && extra.Vehicle != vehicle)
                buffer.Add((extra, point != null ? point.Slot : extra.NativeSlot));
            return Estimate(buffer);
        }
    }
}
