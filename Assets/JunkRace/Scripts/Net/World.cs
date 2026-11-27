using System.Collections.Generic;
using UnityEngine;

namespace JunkRace
{
    /// <summary>Игрок в мире: локальный (PlayerController) или чужой (RemoteAvatar).</summary>
    public interface IAvatar
    {
        ulong ClientId { get; }
        string DisplayName { get; }
        Transform HoldPoint { get; }
        Part Held { get; }
        Vehicle Driving { get; }
        /// <summary>Кресло, в котором сидит пассажиром (null — не сидит).</summary>
        SeatPart Seat { get; }
        IPartInput DriveInput { get; }
        void SetHeld(Part part);
        void SetDriving(Vehicle vehicle);
        /// <summary>Сесть в кресло / встать (seat = null). fling — скорость, с которой выбросило (сорвало кресло).</summary>
        void SetSeat(SeatPart seat, Vector3 fling);
        /// <summary>Выбросило из машины: полетел с этой скоростью.</summary>
        void Fling(Vector3 velocity);
    }

    /// <summary>Нажатия водителя, пришедшие по сети.</summary>
    public sealed class NetPartInput : IPartInput
    {
        public float Throttle { get; set; }
        public float Steer { get; set; }
        public bool Special { get; set; }
        public bool Handbrake { get; set; }
    }

    /// <summary>
    /// Единая точка изменения мира. Три слоя:
    ///   Request* — хочу что-то сделать (локальный игрок). Офлайн и на хосте выполняется сразу, на клиенте уходит хосту.
    ///   Server*  — хост проверяет и выполняет, рассылает событие.
    ///   Apply*   — само изменение. Выполняется везде: на хосте после проверки, на клиентах по событию.
    /// </summary>
    public static class World
    {
        public static JunkNet Net;          // null = офлайн
        public static IAvatar Local;

        static readonly Dictionary<ulong, IAvatar> remotes = new Dictionary<ulong, IAvatar>();

        public static ulong LocalId => Net != null ? Net.LocalClientId : 0UL;
        static bool IsRemoteClient => Net != null && !Net.IsServer;

        public static void RegisterRemote(IAvatar a) => remotes[a.ClientId] = a;
        public static void UnregisterRemote(ulong id) => remotes.Remove(id);
        public static void ClearRemotes() => remotes.Clear();

        public static IAvatar Get(ulong id)
        {
            if (Local != null && id == LocalId) return Local;
            return remotes.TryGetValue(id, out var a) ? a : null;
        }

        public static IEnumerable<IAvatar> All()
        {
            if (Local != null) yield return Local;
            foreach (var a in remotes.Values) yield return a;
        }

        // ================= Запросы локального игрока =================

        public static void RequestGrab(Part part)
        {
            if (Local == null || part == null) return;
            if (IsRemoteClient) Net.SendGrabRequest(part);
            else ServerGrab(LocalId, part);
        }

        public static void RequestRelease(AttachPoint target, Vector3 throwVelocity)
        {
            if (Local == null || Local.Held == null) return;
            if (IsRemoteClient) Net.SendReleaseRequest(target, throwVelocity);
            else ServerRelease(LocalId, target, throwVelocity);
        }

        /// <summary>Порылся в куче — выпало что-то или нет (у всех; id — кто искал, found — null если пусто).</summary>
        public static event System.Action<ulong, JunkPile, Part> SearchFinished;

        /// <summary>Поиск в куче закончен (таймер у игрока истёк) — что нашли, решает хост.</summary>
        public static void RequestSearch(JunkPile pile)
        {
            if (Local == null || pile == null) return;
            if (IsRemoteClient) Net.SendSearchRequest(pile);
            else ServerSearch(LocalId, pile);
        }

        public static void RequestEnter(Vehicle vehicle)
        {
            if (Local == null || vehicle == null) return;
            if (IsRemoteClient) Net.SendEnterRequest(vehicle);
            else ServerEnter(LocalId, vehicle);
        }

        /// <summary>Сесть пассажиром в кресло.</summary>
        public static void RequestSit(SeatPart seat)
        {
            if (Local == null || seat == null) return;
            if (IsRemoteClient) Net.SendSitRequest(seat);
            else ServerSit(LocalId, seat);
        }

        public static void RequestExit()
        {
            if (Local == null || (Local.Driving == null && Local.Seat == null)) return;
            if (IsRemoteClient) Net.SendSimpleRequest(JunkNet.ReqExit);
            else ServerExit(LocalId);
        }

        public static void RequestFlip()
        {
            if (Local == null || FlipTarget(Local) == null) return;
            if (IsRemoteClient) Net.SendSimpleRequest(JunkNet.ReqFlip);
            else ServerFlip(LocalId);
        }

        /// <summary>Клиент: эта загрузка у меня готова (свалка/трасса построены) — сообщить хосту.</summary>
        public static void SendReady()
        {
            if (IsRemoteClient) Net.SendSimpleRequest(JunkNet.ReqReady);
        }

        /// <summary>«Готов» на сборке (G): нажал или отменил.</summary>
        public static void RequestBuildReady(bool ready)
        {
            if (Local == null) return;
            if (IsRemoteClient) Net.SendBuildReady(ready);
            else ServerBuildReady(LocalId, ready);
        }

        public static void ServerBuildReady(ulong id, bool ready)
        {
            if (Get(id) == null || RaceTimer.Instance == null) return;
            RaceTimer.Instance.ServerSetBuildReady(id, ready);
        }

        /// <summary>Перевернуть корпус (в руках или на земле).</summary>
        public static void RequestFlipHull(HullFlip hull)
        {
            if (Local == null || hull == null) return;
            if (IsRemoteClient) Net.SendFlipHullRequest(hull);
            else ServerFlipHull(LocalId, hull);
        }

        /// <summary>Какую машину сбрасывает игрок: свою, за рулём; пассажир — если за рулём никого.</summary>
        static Vehicle FlipTarget(IAvatar a)
        {
            if (a.Driving != null) return a.Driving;
            var v = a.Seat != null ? a.Seat.Vehicle : null;
            return v != null && !v.HasDriver ? v : null;
        }

        // ================= Хозяин машины =================

        /// <summary>Хозяин сменился (у всех). Подписывается сеть — разослать клиентам.</summary>
        public static event System.Action<Vehicle> OwnerChanged;

        /// <summary>Игрок может строить / водить эту машину (ничья или его).</summary>
        public static bool MayUse(ulong id, Vehicle v) => v == null || v.MayUse(id);

        /// <summary>
        /// Хост: машина становится этого игрока. У игрока одна машина — прежнюю отпускаем (её может взять другой).
        /// </summary>
        static void ServerClaim(ulong id, Vehicle v)
        {
            if (v == null || v.OwnerId == id) return;
            foreach (var other in Vehicle.All)
                if (other != v && other.OwnerId == id) SetOwner(other, Vehicle.NoOwner);
            SetOwner(v, id);
        }

        /// <summary>Хост: игрок ушёл — его машина ничья.</summary>
        public static void ServerReleaseOwner(ulong id)
        {
            foreach (var v in Vehicle.All)
                if (v.OwnerId == id) SetOwner(v, Vehicle.NoOwner);
        }

        static void SetOwner(Vehicle v, ulong id)
        {
            v.ApplyOwner(id);
            OwnerChanged?.Invoke(v);
        }

        /// <summary>Имя хозяина для подписей («Ванна — Вася»).</summary>
        public static string OwnerName(Vehicle v)
        {
            if (v == null || !v.IsOwned) return null;
            var a = Get(v.OwnerId);
            return a != null ? a.DisplayName : "другой игрок";
        }

        // ================= Хост: проверка + выполнение =================

        public static void ServerGrab(ulong id, Part part)
        {
            var a = Get(id);
            if (a == null || part == null || a.Held != null || a.Driving != null || a.Seat != null || !part.CanBeCarried) return;
            if (RaceTimer.InteractionLocked) return;   // не все загрузились — фору не даём
            if (!MayUse(id, part.Vehicle)) return;   // чужую машину не разбирают и не уносят
            if (part.Vehicle is Vehicle recovering && recovering.RootPart == part && recovering.TrainRecoveryPending) return;
            // Прикрученную деталь сначала снимаем с машины. Vehicle поднимет PartDetached, JunkNet разошлёт
            // снятие раньше, чем «взял» (один надёжный упорядоченный канал), так что у клиентов порядок тот же.
            if (part.MountedOn != null && part.Vehicle != null) part.Vehicle.Detach(part, Vector3.zero);
            ApplyGrab(a, part);
            if (Net != null) Net.BroadcastGrab(id, part);
        }

        public static void ServerRelease(ulong id, AttachPoint target, Vector3 throwVelocity)
        {
            var a = Get(id);
            if (a == null || a.Held == null) return;
            var part = a.Held;
            ApplyRelease(a, target != null ? Vector3.zero : throwVelocity);
            if (Net != null) Net.BroadcastRelease(id);

            // Vehicle.Attach сам поднимет событие PartAttached, JunkNet его разошлёт.
            // К чужой машине не прикручивают — деталь просто падает рядом.
            if (target != null && target.Owner != null && target.Owner.Vehicle != null && MayUse(id, target.Owner.Vehicle))
            {
                var v = target.Owner.Vehicle;
                if (v.Attach(part, target)) ServerClaim(id, v);
            }
        }

        public static void ServerSearch(ulong id, JunkPile pile)
        {
            var a = Get(id);
            if (a == null || pile == null) return;
            Vector3 searcher = a.HoldPoint != null ? a.HoldPoint.position : pile.transform.position;
            // С занятыми руками или из машины не роются — отвечаем «ничего», чтобы у игрока не завис поиск.
            Part found = a.Held == null && a.Driving == null && a.Seat == null && !RaceTimer.InteractionLocked
                         && pile.InReach(searcher, 3f) ? pile.Roll() : null;
            Vector3 pos = pile.PopPosition(searcher);
            ApplySearch(id, pile, found, pos, pile.PopVelocity(searcher));
            if (Net != null) Net.BroadcastSearch(id, pile, found, pos);
        }

        public static void ApplySearch(ulong id, JunkPile pile, Part found, Vector3 position, Vector3 velocity)
        {
            if (found != null && found.IsHidden)
                found.Reveal(position, Quaternion.Euler(0f, Random.Range(0f, 360f), 0f), velocity);
            SearchFinished?.Invoke(id, pile, found);
        }

        public static void ServerEnter(ulong id, Vehicle vehicle)
        {
            var a = Get(id);
            if (a == null || vehicle == null || a.Held != null || a.Driving != null || a.Seat != null || vehicle.HasDriver
                || vehicle.RootPart.IsCarried) return;   // в машину, которую несут, не садятся
            if (!MayUse(id, vehicle) || vehicle.TrainRecoveryPending) return;
            if (RaceTimer.InteractionLocked) return;
            ServerClaim(id, vehicle);
            ApplyEnter(a, vehicle);
            if (Net != null) Net.BroadcastDriver(vehicle, a);
        }

        public static void ServerExit(ulong id)
        {
            var a = Get(id);
            if (a == null) return;
            if (a.Driving != null)
            {
                var vehicle = a.Driving;
                ApplyExit(a);
                if (Net != null) Net.BroadcastDriver(vehicle, null);
            }
            else if (a.Seat != null)
            {
                var seat = a.Seat;
                ApplyLeaveSeat(a, Vector3.zero);
                if (Net != null) Net.BroadcastSeat(seat, null);
            }
        }

        public static void ServerSit(ulong id, SeatPart seat)
        {
            var a = Get(id);
            if (a == null || seat == null || a.Held != null || a.Driving != null || a.Seat != null || !seat.CanSit) return;
            if (RaceTimer.InteractionLocked) return;
            ApplySit(a, seat);
            if (Net != null) Net.BroadcastSeat(seat, a);
        }

        public static void ApplySit(IAvatar a, SeatPart seat)
        {
            if (a == null || seat == null || seat.Occupant != null || a.Seat != null) return;
            seat.Occupant = a;
            a.SetSeat(seat, Vector3.zero);
        }

        /// <summary>Встать с кресла (сам вышел — fling = 0; сорвало кресло — выбрасывает с этой скоростью).</summary>
        public static void ApplyLeaveSeat(IAvatar a, Vector3 fling)
        {
            if (a == null || a.Seat == null) return;
            a.Seat.Occupant = null;
            a.SetSeat(null, fling);
        }

        public static void ServerFlip(ulong id)
        {
            var a = Get(id);
            var v = a != null ? FlipTarget(a) : null;
            if (v != null) v.Flip();
        }

        /// <summary>
        /// Старт гонки: игрок с машиной — за руль своей (бросает деталь из рук, встаёт с кресла).
        /// Возвращает машину или null, если её нет (тогда игрок смотрит гонку пешком).
        /// </summary>
        public static Vehicle ServerSeatForRace(IAvatar a)
        {
            if (a == null) return null;
            if (a.Driving != null) { ServerClaim(a.ClientId, a.Driving); return a.Driving; }

            Vehicle own = null;
            foreach (var v in Vehicle.All) if (v.OwnerId == a.ClientId) { own = v; break; }
            if (own == null || own.HasDriver || own.RootPart.IsHidden) return null;

            if (a.Held != null)
            {
                ApplyRelease(a, Vector3.zero);
                if (Net != null) Net.BroadcastRelease(a.ClientId);
            }
            if (own.RootPart.IsCarried) return null;   // его машину несёт кто-то другой
            if (a.Seat != null)
            {
                var seat = a.Seat;
                ApplyLeaveSeat(a, Vector3.zero);
                if (Net != null) Net.BroadcastSeat(seat, null);
            }
            ApplyEnter(a, own);
            if (Net != null) Net.BroadcastDriver(own, a);
            return own;
        }

        internal static IAvatar DriverOf(Vehicle vehicle)
        {
            foreach (var avatar in All())
                if (avatar.Driving == vehicle) return avatar;
            return null;
        }

        internal static void ServerRestoreDriver(ulong id, Vehicle vehicle)
        {
            var avatar = Get(id);
            if (avatar == null || avatar.Driving == vehicle || vehicle.HasDriver) return;

            if (avatar.Driving != null || avatar.Seat != null) ServerExit(id);
            if (avatar.Held != null) ServerRelease(id, null, Vector3.zero);
            ApplyEnter(avatar, vehicle);
            Net?.BroadcastDriver(vehicle, avatar);
        }

        /// <summary>Хост: водителя выбросило ударом — выходит из машины и летит по инерции.</summary>
        public static void ServerEjectDriver(Vehicle vehicle, Vector3 fling)
        {
            if (vehicle == null) return;
            var driver = DriverOf(vehicle);
            if (driver == null) return;
            ApplyEject(driver, fling);
            if (Net != null) Net.BroadcastEject(vehicle, driver, fling);
        }

        public static void ApplyEject(IAvatar a, Vector3 fling)
        {
            if (a == null || a.Driving == null) return;
            ApplyExit(a);
            a.Fling(fling);
        }

        public static void ServerFlipHull(ulong id, HullFlip hull)
        {
            var a = Get(id);
            if (a == null || hull == null || a.Driving != null || a.Seat != null || RaceTimer.InteractionLocked) return;
            var part = hull.GetComponent<Part>();
            if (part == null || part.IsHidden) return;
            var v = part.Vehicle;
            if (!MayUse(id, v)) return;
            if (v != null && v.HasAnyone) return;   // с людьми внутри не переворачиваем
            if (part.IsCarried && a.Held != part) return;   // чужой в руках
            bool flipped = !hull.Flipped;
            hull.ApplyFlipped(flipped);
            if (Net != null) Net.BroadcastFlipHull(hull, flipped);
        }

        // ================= Применение (везде) =================

        public static void ApplyGrab(IAvatar a, Part part)
        {
            a.SetHeld(part);
            part.BeginCarry(a.HoldPoint);
        }

        public static void ApplyRelease(IAvatar a, Vector3 throwVelocity)
        {
            var part = a.Held;
            if (part == null) return;
            a.SetHeld(null);
            part.EndCarry(throwVelocity);
        }

        public static void ApplyEnter(IAvatar a, Vehicle vehicle)
        {
            vehicle.SetDriver(a.DriveInput);
            a.SetDriving(vehicle);
        }

        public static void ApplyExit(IAvatar a)
        {
            var vehicle = a.Driving;
            if (vehicle == null) return;
            vehicle.SetDriver(null);
            a.SetDriving(null);
        }

        public static void ApplyExitVehicle(Vehicle vehicle)
        {
            var driver = DriverOf(vehicle);
            if (driver != null) ApplyExit(driver);
            else vehicle.SetDriver(null);
        }
    }
}
