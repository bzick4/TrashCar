using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace JunkRace
{
    /// <summary>
    /// Сеть трассы. Висит в сцене трассы; в соло сам выключается.
    ///
    /// Хост — авторитет: считает всю физику, проверяет действия, рассылает:
    ///   • JR_WORLD  20 Гц — позиции машин и свободных деталей (прикрученные едут вместе с машиной);
    ///   • JR_POSES  20 Гц — позиции игроков;
    ///   • JR_EVT    надёжно — взял / бросил / прикрутил / отвалилось / сел / вышел / финиш / порылся в куче;
    ///   • JR_FULL   один раз новому клиенту — всё текущее состояние.
    /// Клиент шлёт: JR_HELLO (имя), JR_POSE (где я), JR_IN (газ/руль), JR_REQ (хочу взять/прикрутить/сесть).
    ///
    /// Детали — обычные объекты сцены, их номер = порядок в иерархии (одинаков у всех, сцена одна).
    /// Поэтому ни NetworkObject, ни сетевые префабы не нужны.
    /// </summary>
    public class JunkNet : MonoBehaviour
    {
        const string MsgHello = "JR_HELLO", MsgReq = "JR_REQ", MsgPose = "JR_POSE", MsgInput = "JR_IN";
        const string MsgEvt = "JR_EVT", MsgRoster = "JR_ROSTER", MsgFull = "JR_FULL", MsgPoses = "JR_POSES", MsgWorld = "JR_WORLD";

        public const byte ReqGrab = 1, ReqRelease = 2, ReqEnter = 3, ReqExit = 4, ReqFlip = 5, ReqSearch = 6, ReqSit = 7;
        const byte EvtGrab = 1, EvtRelease = 2, EvtAttach = 3, EvtDetach = 4, EvtDriver = 5, EvtFinish = 6, EvtSearch = 7, EvtSeat = 8;

        const float SendInterval = 1f / 20f;
        const int SnapshotBudget = 36;   // ~36 × 30 байт — влезает в один UDP-пакет
        const ushort NoIndex = ushort.MaxValue;

        public static JunkNet Instance { get; private set; }
        public bool IsServer => nm != null && nm.IsServer;
        public ulong LocalClientId => nm != null ? nm.LocalClientId : 0UL;

        NetworkManager nm;
        CustomMessagingManager Msg => nm.CustomMessagingManager;

        readonly List<Part> parts = new List<Part>();
        readonly List<JunkPile> piles = new List<JunkPile>();
        readonly Dictionary<ulong, RemoteAvatar> ghosts = new Dictionary<ulong, RemoteAvatar>();
        readonly Dictionary<ulong, string> roster = new Dictionary<ulong, string>();
        readonly HashSet<ulong> readyClients = new HashSet<ulong>();
        readonly Dictionary<int, (Vector3 pos, Quaternion rot)> targets = new Dictionary<int, (Vector3, Quaternion)>();
        readonly List<Vehicle> vehicles = new List<Vehicle>();
        readonly List<int> snapshotList = new List<int>();

        float sendTimer;
        int sleepCursor;
        bool registered;

        // ============================ Жизненный цикл ============================

        void Awake()
        {
            BuildRegistry();
            nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsListening) { enabled = false; return; }   // соло

            Instance = this;
            World.Net = this;
            Authority.Provider = () =>
            {
                var n = NetworkManager.Singleton;
                return n == null || !n.IsListening || n.IsServer;
            };

            if (nm.IsServer)
            {
                Msg.RegisterNamedMessageHandler(MsgHello, OnHello);
                Msg.RegisterNamedMessageHandler(MsgReq, OnRequest);
                Msg.RegisterNamedMessageHandler(MsgPose, OnPose);
                Msg.RegisterNamedMessageHandler(MsgInput, OnInput);
                nm.OnClientDisconnectCallback += OnClientLeft;
            }
            else
            {
                Msg.RegisterNamedMessageHandler(MsgEvt, OnEvent);
                Msg.RegisterNamedMessageHandler(MsgRoster, OnRoster);
                Msg.RegisterNamedMessageHandler(MsgFull, OnFull);
                Msg.RegisterNamedMessageHandler(MsgPoses, OnPoses);
                Msg.RegisterNamedMessageHandler(MsgWorld, OnWorld);
            }
            registered = true;
        }

        void Start()
        {
            if (!enabled) return;

            vehicles.AddRange(FindObjectsByType<Vehicle>(FindObjectsSortMode.None));

            if (IsServer)
            {
                foreach (var v in vehicles)
                {
                    v.PartAttached += BroadcastAttach;
                    v.PartDetached += BroadcastDetach;
                }
                RaceTimer.Finished += BroadcastFinish;
                roster[NetworkManager.ServerClientId] = SafeName(GameSession.PlayerName);
            }
            else
            {
                // У клиента физики нет: всё двигает хост, мы только показываем.
                foreach (var p in parts)
                    if (p.TryGetComponent(out Rigidbody rb)) MakeClientBody(rb);

                using var w = new FastBufferWriter(64, Allocator.Temp, 256);
                w.WriteValueSafe(SafeName(GameSession.PlayerName));
                Msg.SendNamedMessage(MsgHello, NetworkManager.ServerClientId, w, NetworkDelivery.ReliableSequenced);
            }

            // Разводим игроков по разным точкам старта.
            if (World.Local is PlayerController local)
                local.Teleport(local.transform.position + Vector3.right * (LocalClientId % 4) * 2.5f);
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (World.Net == this) World.Net = null;
            Authority.Provider = () => true;
            RaceTimer.Finished -= BroadcastFinish;
            World.ClearRemotes();

            if (!registered || nm == null) return;
            nm.OnClientDisconnectCallback -= OnClientLeft;
            var m = nm.CustomMessagingManager;
            if (m == null) return;
            foreach (var name in new[] { MsgHello, MsgReq, MsgPose, MsgInput, MsgEvt, MsgRoster, MsgFull, MsgPoses, MsgWorld })
                m.UnregisterNamedMessageHandler(name);
        }

        void Update()
        {
            sendTimer += Time.unscaledDeltaTime;
            if (sendTimer >= SendInterval)
            {
                sendTimer = 0f;
                if (IsServer) { SendPoses(); SendWorld(); }
                else { SendMyPose(); SendMyInput(); }
            }

            if (!IsServer) FollowTargets();
        }

        // ============================ Реестр деталей ============================

        void BuildRegistry()
        {
            var all = FindObjectsByType<Part>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            var keyed = new List<(string key, Part part)>(all.Length);
            foreach (var p in all) keyed.Add((HierarchyKey(p.transform), p));
            keyed.Sort((a, b) => string.CompareOrdinal(a.key, b.key));
            parts.Clear();
            foreach (var (_, p) in keyed) { p.NetIndex = parts.Count; parts.Add(p); }

            // Кучи мусора — так же, по порядку в иерархии.
            var allPiles = FindObjectsByType<JunkPile>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            var keyedPiles = new List<(string key, JunkPile pile)>(allPiles.Length);
            foreach (var pl in allPiles) keyedPiles.Add((HierarchyKey(pl.transform), pl));
            keyedPiles.Sort((a, b) => string.CompareOrdinal(a.key, b.key));
            piles.Clear();
            foreach (var (_, pl) in keyedPiles) { pl.NetIndex = piles.Count; piles.Add(pl); }
        }

        JunkPile PileAt(int index) => index >= 0 && index < piles.Count ? piles[index] : null;

        static string HierarchyKey(Transform t)
        {
            var stack = new List<string>();
            for (var cur = t; cur != null; cur = cur.parent) stack.Add(cur.GetSiblingIndex().ToString("D4"));
            stack.Reverse();
            return string.Join("/", stack);
        }

        Part PartAt(int index) => index >= 0 && index < parts.Count ? parts[index] : null;

        AttachPoint PointAt(int ownerIndex, int pointIndex)
        {
            var owner = PartAt(ownerIndex);
            if (owner == null || owner.Points == null || pointIndex < 0 || pointIndex >= owner.Points.Length) return null;
            return owner.Points[pointIndex];
        }

        Vehicle VehicleAt(int index)
        {
            var p = PartAt(index);
            return p != null ? p.GetComponent<Vehicle>() : null;
        }

        static int PointIndex(AttachPoint point) =>
            point != null && point.Owner != null ? Array.IndexOf(point.Owner.Points, point) : -1;

        static void MakeClientBody(Rigidbody rb)
        {
            rb.isKinematic = true;
            rb.interpolation = RigidbodyInterpolation.None;
        }

        // ============================ Клиент → хост ============================

        public void SendGrabRequest(Part part)
        {
            using var w = new FastBufferWriter(16, Allocator.Temp);
            w.WriteValueSafe(ReqGrab);
            w.WriteValueSafe((ushort)part.NetIndex);
            Msg.SendNamedMessage(MsgReq, NetworkManager.ServerClientId, w, NetworkDelivery.ReliableSequenced);
        }

        public void SendReleaseRequest(AttachPoint target, Vector3 throwVelocity)
        {
            using var w = new FastBufferWriter(32, Allocator.Temp);
            w.WriteValueSafe(ReqRelease);
            bool has = target != null && target.Owner != null;
            w.WriteValueSafe(has);
            w.WriteValueSafe(has ? (ushort)target.Owner.NetIndex : NoIndex);
            w.WriteValueSafe((byte)(has ? PointIndex(target) : 0));
            w.WriteValueSafe(throwVelocity);
            Msg.SendNamedMessage(MsgReq, NetworkManager.ServerClientId, w, NetworkDelivery.ReliableSequenced);
        }

        public void SendEnterRequest(Vehicle vehicle)
        {
            using var w = new FastBufferWriter(16, Allocator.Temp);
            w.WriteValueSafe(ReqEnter);
            w.WriteValueSafe((ushort)vehicle.RootPart.NetIndex);
            Msg.SendNamedMessage(MsgReq, NetworkManager.ServerClientId, w, NetworkDelivery.ReliableSequenced);
        }

        public void SendSitRequest(SeatPart seat)
        {
            using var w = new FastBufferWriter(16, Allocator.Temp);
            w.WriteValueSafe(ReqSit);
            w.WriteValueSafe((ushort)seat.NetIndex);
            Msg.SendNamedMessage(MsgReq, NetworkManager.ServerClientId, w, NetworkDelivery.ReliableSequenced);
        }

        public void SendSearchRequest(JunkPile pile)
        {
            using var w = new FastBufferWriter(16, Allocator.Temp);
            w.WriteValueSafe(ReqSearch);
            w.WriteValueSafe((ushort)pile.NetIndex);
            Msg.SendNamedMessage(MsgReq, NetworkManager.ServerClientId, w, NetworkDelivery.ReliableSequenced);
        }

        public void SendSimpleRequest(byte type)
        {
            using var w = new FastBufferWriter(8, Allocator.Temp);
            w.WriteValueSafe(type);
            Msg.SendNamedMessage(MsgReq, NetworkManager.ServerClientId, w, NetworkDelivery.ReliableSequenced);
        }

        void SendMyPose()
        {
            if (!(World.Local is PlayerController me)) return;
            using var w = new FastBufferWriter(32, Allocator.Temp);
            w.WriteValueSafe(me.transform.position);
            w.WriteValueSafe(me.transform.eulerAngles.y);
            Msg.SendNamedMessage(MsgPose, NetworkManager.ServerClientId, w, NetworkDelivery.UnreliableSequenced);
        }

        void SendMyInput()
        {
            var me = World.Local;
            if (me == null || me.Driving == null) return;
            var input = me.DriveInput;
            using var w = new FastBufferWriter(16, Allocator.Temp);
            w.WriteValueSafe(input.Throttle);
            w.WriteValueSafe(input.Steer);
            w.WriteValueSafe(input.Special);
            Msg.SendNamedMessage(MsgInput, NetworkManager.ServerClientId, w, NetworkDelivery.UnreliableSequenced);
        }

        // ============================ Хост: приём ============================

        void OnHello(ulong sender, FastBufferReader r)
        {
            r.ReadValueSafe(out string playerName);
            roster[sender] = SafeName(playerName);
            readyClients.Add(sender);
            EnsureGhost(sender, roster[sender]);
            BroadcastRoster();
            SendFull(sender);
        }

        void OnRequest(ulong sender, FastBufferReader r)
        {
            r.ReadValueSafe(out byte type);
            switch (type)
            {
                case ReqGrab:
                    r.ReadValueSafe(out ushort partIndex);
                    World.ServerGrab(sender, PartAt(partIndex));
                    break;
                case ReqRelease:
                    r.ReadValueSafe(out bool has);
                    r.ReadValueSafe(out ushort owner);
                    r.ReadValueSafe(out byte pointIndex);
                    r.ReadValueSafe(out Vector3 velocity);
                    World.ServerRelease(sender, has ? PointAt(owner, pointIndex) : null, Vector3.ClampMagnitude(velocity, 15f));
                    break;
                case ReqEnter:
                    r.ReadValueSafe(out ushort vehicleIndex);
                    World.ServerEnter(sender, VehicleAt(vehicleIndex));
                    break;
                case ReqExit: World.ServerExit(sender); break;
                case ReqFlip: World.ServerFlip(sender); break;
                case ReqSearch:
                    r.ReadValueSafe(out ushort pileIndex);
                    World.ServerSearch(sender, PileAt(pileIndex));
                    break;
                case ReqSit:
                    r.ReadValueSafe(out ushort seatIndex);
                    World.ServerSit(sender, PartAt(seatIndex) as SeatPart);
                    break;
            }
        }

        void OnPose(ulong sender, FastBufferReader r)
        {
            r.ReadValueSafe(out Vector3 pos);
            r.ReadValueSafe(out float yaw);
            if (ghosts.TryGetValue(sender, out var g)) g.SetPose(pos, yaw);
        }

        void OnInput(ulong sender, FastBufferReader r)
        {
            r.ReadValueSafe(out float throttle);
            r.ReadValueSafe(out float steer);
            r.ReadValueSafe(out bool special);
            if (!ghosts.TryGetValue(sender, out var g)) return;
            g.NetInput.Throttle = Mathf.Clamp(throttle, -1f, 1f);
            g.NetInput.Steer = Mathf.Clamp(steer, -1f, 1f);
            g.NetInput.Special = special;
        }

        void OnClientLeft(ulong clientId)
        {
            if (clientId == NetworkManager.ServerClientId) return;
            if (ghosts.TryGetValue(clientId, out var g))
            {
                if (g.Held != null) { World.ApplyRelease(g, Vector3.zero); BroadcastRelease(clientId); }
                if (g.Driving != null) { var v = g.Driving; World.ApplyExit(g); BroadcastDriver(v, null); }
                if (g.Seat != null) { var s = g.Seat; World.ApplyLeaveSeat(g, Vector3.zero); BroadcastSeat(s, null); }
            }
            RemoveGhost(clientId);
            roster.Remove(clientId);
            readyClients.Remove(clientId);
            BroadcastRoster();
        }

        // ============================ Хост: рассылка ============================

        void SendToClients(string message, FastBufferWriter w, NetworkDelivery delivery)
        {
            foreach (ulong id in readyClients)
                Msg.SendNamedMessage(message, id, w, delivery);
        }

        void SendPoses()
        {
            if (readyClients.Count == 0) return;
            using var w = new FastBufferWriter(256, Allocator.Temp);
            int count = (World.Local != null ? 1 : 0) + ghosts.Count;
            w.WriteValueSafe(count);
            if (World.Local is PlayerController me)
            {
                w.WriteValueSafe(LocalClientId);
                w.WriteValueSafe(me.transform.position);
                w.WriteValueSafe(me.transform.eulerAngles.y);
            }
            foreach (var kv in ghosts)
            {
                w.WriteValueSafe(kv.Key);
                w.WriteValueSafe(kv.Value.transform.position);
                w.WriteValueSafe(kv.Value.transform.eulerAngles.y);
            }
            SendToClients(MsgPoses, w, NetworkDelivery.UnreliableSequenced);
        }

        void SendWorld()
        {
            if (readyClients.Count == 0) return;

            // Сначала всё, что движется, потом по кругу немного спящих — чтобы клиент точно знал, где они лежат.
            snapshotList.Clear();
            for (int i = 0; i < parts.Count && snapshotList.Count < SnapshotBudget; i++)
                if (IsFreeBody(parts[i], out var rb) && !rb.IsSleeping()) snapshotList.Add(i);

            int sleepingSlots = Mathf.Max(4, SnapshotBudget - snapshotList.Count);
            for (int n = 0; n < parts.Count && sleepingSlots > 0 && snapshotList.Count < SnapshotBudget + 4; n++)
            {
                sleepCursor = (sleepCursor + 1) % parts.Count;
                if (IsFreeBody(parts[sleepCursor], out var rb) && rb.IsSleeping()) { snapshotList.Add(sleepCursor); sleepingSlots--; }
            }

            using var w = new FastBufferWriter(1300, Allocator.Temp);
            w.WriteValueSafe((ushort)snapshotList.Count);
            foreach (int i in snapshotList)
            {
                var t = parts[i].transform;
                w.WriteValueSafe((ushort)i);
                w.WriteValueSafe(t.position);
                w.WriteValueSafe(t.rotation);
            }
            SendToClients(MsgWorld, w, NetworkDelivery.UnreliableSequenced);
        }

        static bool IsFreeBody(Part p, out Rigidbody rb)
        {
            rb = null;
            if (p == null || p.MountedOn != null || p.IsCarried || p.IsHidden) return false;
            return p.TryGetComponent(out rb);
        }

        void BroadcastRoster()
        {
            using var w = new FastBufferWriter(256, Allocator.Temp, 2048);
            w.WriteValueSafe(roster.Count);
            foreach (var kv in roster)
            {
                w.WriteValueSafe(kv.Key);
                w.WriteValueSafe(kv.Value);
            }
            SendToClients(MsgRoster, w, NetworkDelivery.ReliableSequenced);
        }

        public void BroadcastGrab(ulong clientId, Part part)
        {
            using var w = new FastBufferWriter(16, Allocator.Temp);
            w.WriteValueSafe(EvtGrab);
            w.WriteValueSafe(clientId);
            w.WriteValueSafe((ushort)part.NetIndex);
            SendToClients(MsgEvt, w, NetworkDelivery.ReliableSequenced);
        }

        public void BroadcastRelease(ulong clientId)
        {
            using var w = new FastBufferWriter(16, Allocator.Temp);
            w.WriteValueSafe(EvtRelease);
            w.WriteValueSafe(clientId);
            SendToClients(MsgEvt, w, NetworkDelivery.ReliableSequenced);
        }

        void BroadcastAttach(Part part)
        {
            if (part.MountedOn == null) return;
            using var w = new FastBufferWriter(16, Allocator.Temp);
            w.WriteValueSafe(EvtAttach);
            w.WriteValueSafe((ushort)part.NetIndex);
            w.WriteValueSafe((ushort)part.MountedOn.Owner.NetIndex);
            w.WriteValueSafe((byte)PointIndex(part.MountedOn));
            SendToClients(MsgEvt, w, NetworkDelivery.ReliableSequenced);
        }

        /// <summary>Кто сел в кресло (avatar = null — кресло освободилось).</summary>
        public void BroadcastSeat(SeatPart seat, IAvatar avatar)
        {
            using var w = new FastBufferWriter(24, Allocator.Temp);
            w.WriteValueSafe(EvtSeat);
            w.WriteValueSafe((ushort)seat.NetIndex);
            w.WriteValueSafe(avatar != null);
            w.WriteValueSafe(avatar != null ? avatar.ClientId : 0UL);
            SendToClients(MsgEvt, w, NetworkDelivery.ReliableSequenced);
        }

        public void BroadcastSearch(ulong clientId, JunkPile pile, Part found, Vector3 position)
        {
            using var w = new FastBufferWriter(40, Allocator.Temp);
            w.WriteValueSafe(EvtSearch);
            w.WriteValueSafe(clientId);
            w.WriteValueSafe((ushort)pile.NetIndex);
            w.WriteValueSafe(found != null ? (ushort)found.NetIndex : NoIndex);
            w.WriteValueSafe(position);
            SendToClients(MsgEvt, w, NetworkDelivery.ReliableSequenced);
        }

        void BroadcastDetach(Part part)
        {
            using var w = new FastBufferWriter(16, Allocator.Temp);
            w.WriteValueSafe(EvtDetach);
            w.WriteValueSafe((ushort)part.NetIndex);
            SendToClients(MsgEvt, w, NetworkDelivery.ReliableSequenced);
        }

        public void BroadcastDriver(Vehicle vehicle, IAvatar driver)
        {
            using var w = new FastBufferWriter(24, Allocator.Temp);
            w.WriteValueSafe(EvtDriver);
            w.WriteValueSafe((ushort)vehicle.RootPart.NetIndex);
            w.WriteValueSafe(driver != null);
            w.WriteValueSafe(driver != null ? driver.ClientId : 0UL);
            SendToClients(MsgEvt, w, NetworkDelivery.ReliableSequenced);
        }

        void BroadcastFinish(float elapsed, int partCount)
        {
            using var w = new FastBufferWriter(16, Allocator.Temp);
            w.WriteValueSafe(EvtFinish);
            w.WriteValueSafe(elapsed);
            w.WriteValueSafe(partCount);
            SendToClients(MsgEvt, w, NetworkDelivery.ReliableSequenced);
        }

        /// <summary>Всё текущее состояние — новому клиенту.</summary>
        void SendFull(ulong clientId)
        {
            using var w = new FastBufferWriter(512, Allocator.Temp, 16384);

            var timer = RaceTimer.Instance;
            w.WriteValueSafe(timer != null ? timer.Elapsed : -1f);
            w.WriteValueSafe(timer != null && timer.IsFinished);
            w.WriteValueSafe(timer != null ? timer.PartsAtFinish : 0);

            // Уже найденные в кучах детали (до прикреплений: найденную могли и прикрутить).
            var revealed = new List<Part>();
            foreach (var p in parts) if (p.WasStashed && !p.IsHidden) revealed.Add(p);
            w.WriteValueSafe(revealed.Count);
            foreach (var p in revealed)
            {
                w.WriteValueSafe((ushort)p.NetIndex);
                w.WriteValueSafe(p.transform.position);
                w.WriteValueSafe(p.transform.rotation);
            }

            // Прикрепления: в порядке прикручивания, значит родитель всегда раньше ребёнка.
            var attached = new List<Part>();
            foreach (var v in vehicles)
                foreach (var p in v.Parts)
                    if (p.MountedOn != null) attached.Add(p);
            w.WriteValueSafe(attached.Count);
            foreach (var p in attached)
            {
                w.WriteValueSafe((ushort)p.NetIndex);
                w.WriteValueSafe((ushort)p.MountedOn.Owner.NetIndex);
                w.WriteValueSafe((byte)PointIndex(p.MountedOn));
            }

            var carriers = new List<IAvatar>();
            var drivers = new List<IAvatar>();
            foreach (var a in World.All())
            {
                if (a.Held != null) carriers.Add(a);
                if (a.Driving != null) drivers.Add(a);
            }
            w.WriteValueSafe(carriers.Count);
            foreach (var a in carriers) { w.WriteValueSafe(a.ClientId); w.WriteValueSafe((ushort)a.Held.NetIndex); }
            w.WriteValueSafe(drivers.Count);
            foreach (var a in drivers) { w.WriteValueSafe(a.ClientId); w.WriteValueSafe((ushort)a.Driving.RootPart.NetIndex); }

            var passengers = new List<IAvatar>();
            foreach (var a in World.All()) if (a.Seat != null) passengers.Add(a);
            w.WriteValueSafe(passengers.Count);
            foreach (var a in passengers) { w.WriteValueSafe(a.ClientId); w.WriteValueSafe((ushort)a.Seat.NetIndex); }

            Msg.SendNamedMessage(MsgFull, clientId, w, NetworkDelivery.ReliableFragmentedSequenced);
        }

        // ============================ Клиент: приём ============================

        void OnRoster(ulong sender, FastBufferReader r)
        {
            r.ReadValueSafe(out int count);
            var present = new HashSet<ulong>();
            for (int i = 0; i < count; i++)
            {
                r.ReadValueSafe(out ulong id);
                r.ReadValueSafe(out string playerName);
                present.Add(id);
                roster[id] = playerName;
                if (id != LocalClientId) EnsureGhost(id, playerName);
            }

            var gone = new List<ulong>();
            foreach (var id in ghosts.Keys) if (!present.Contains(id)) gone.Add(id);
            foreach (var id in gone)
            {
                var g = ghosts[id];
                if (g.Held != null) World.ApplyRelease(g, Vector3.zero);
                if (g.Driving != null) World.ApplyExit(g);
                if (g.Seat != null) World.ApplyLeaveSeat(g, Vector3.zero);
                RemoveGhost(id);
                roster.Remove(id);
            }
        }

        void OnEvent(ulong sender, FastBufferReader r)
        {
            r.ReadValueSafe(out byte type);
            switch (type)
            {
                case EvtGrab:
                {
                    r.ReadValueSafe(out ulong id);
                    r.ReadValueSafe(out ushort partIndex);
                    var a = World.Get(id);
                    var p = PartAt(partIndex);
                    if (a != null && p != null && a.Held == null) World.ApplyGrab(a, p);
                    break;
                }
                case EvtRelease:
                {
                    r.ReadValueSafe(out ulong id);
                    var a = World.Get(id);
                    if (a != null) World.ApplyRelease(a, Vector3.zero);
                    break;
                }
                case EvtAttach:
                {
                    r.ReadValueSafe(out ushort partIndex);
                    r.ReadValueSafe(out ushort owner);
                    r.ReadValueSafe(out byte pointIndex);
                    ApplyAttachLocal(PartAt(partIndex), PointAt(owner, pointIndex));
                    break;
                }
                case EvtDetach:
                {
                    r.ReadValueSafe(out ushort partIndex);
                    var p = PartAt(partIndex);
                    if (p != null && p.Vehicle != null && p.Vehicle.RootPart != p) p.Vehicle.ApplyDetach(p, Vector3.zero);
                    break;
                }
                case EvtDriver:
                {
                    r.ReadValueSafe(out ushort vehicleIndex);
                    r.ReadValueSafe(out bool has);
                    r.ReadValueSafe(out ulong id);
                    var v = VehicleAt(vehicleIndex);
                    if (v == null) break;
                    World.ApplyExitVehicle(v);
                    var a = has ? World.Get(id) : null;
                    if (a != null) World.ApplyEnter(a, v);
                    break;
                }
                case EvtSeat:
                {
                    r.ReadValueSafe(out ushort seatIndex);
                    r.ReadValueSafe(out bool has);
                    r.ReadValueSafe(out ulong id);
                    if (!(PartAt(seatIndex) is SeatPart seat)) break;
                    if (seat.Occupant != null) World.ApplyLeaveSeat(seat.Occupant, Vector3.zero);
                    if (has) World.ApplySit(World.Get(id), seat);
                    break;
                }
                case EvtSearch:
                {
                    r.ReadValueSafe(out ulong id);
                    r.ReadValueSafe(out ushort pileIndex);
                    r.ReadValueSafe(out ushort partIndex);
                    r.ReadValueSafe(out Vector3 position);
                    World.ApplySearch(id, PileAt(pileIndex), partIndex == NoIndex ? null : PartAt(partIndex), position, Vector3.zero);
                    break;
                }
                case EvtFinish:
                {
                    r.ReadValueSafe(out float elapsed);
                    r.ReadValueSafe(out int partCount);
                    if (RaceTimer.Instance) RaceTimer.Instance.ApplyFinish(elapsed, partCount);
                    break;
                }
            }
        }

        void OnFull(ulong sender, FastBufferReader r)
        {
            r.ReadValueSafe(out float elapsed);
            r.ReadValueSafe(out bool finished);
            r.ReadValueSafe(out int partsAtFinish);
            if (RaceTimer.Instance && elapsed >= 0f)
            {
                if (finished) RaceTimer.Instance.ApplyFinish(elapsed, partsAtFinish);
                else RaceTimer.Instance.ApplyStart(elapsed);
            }

            r.ReadValueSafe(out int revealedCount);
            for (int i = 0; i < revealedCount; i++)
            {
                r.ReadValueSafe(out ushort partIndex);
                r.ReadValueSafe(out Vector3 pos);
                r.ReadValueSafe(out Quaternion rot);
                var p = PartAt(partIndex);
                if (p != null) p.Reveal(pos, rot, Vector3.zero);
            }

            r.ReadValueSafe(out int attachCount);
            for (int i = 0; i < attachCount; i++)
            {
                r.ReadValueSafe(out ushort partIndex);
                r.ReadValueSafe(out ushort owner);
                r.ReadValueSafe(out byte pointIndex);
                ApplyAttachLocal(PartAt(partIndex), PointAt(owner, pointIndex));
            }

            r.ReadValueSafe(out int carryCount);
            for (int i = 0; i < carryCount; i++)
            {
                r.ReadValueSafe(out ulong id);
                r.ReadValueSafe(out ushort partIndex);
                var a = World.Get(id);
                var p = PartAt(partIndex);
                if (a != null && p != null && a.Held == null) World.ApplyGrab(a, p);
            }

            r.ReadValueSafe(out int driverCount);
            for (int i = 0; i < driverCount; i++)
            {
                r.ReadValueSafe(out ulong id);
                r.ReadValueSafe(out ushort vehicleIndex);
                var a = World.Get(id);
                var v = VehicleAt(vehicleIndex);
                if (a != null && v != null && a.Driving == null) World.ApplyEnter(a, v);
            }

            r.ReadValueSafe(out int passengerCount);
            for (int i = 0; i < passengerCount; i++)
            {
                r.ReadValueSafe(out ulong id);
                r.ReadValueSafe(out ushort seatIndex);
                if (PartAt(seatIndex) is SeatPart seat) World.ApplySit(World.Get(id), seat);
            }
        }

        void OnPoses(ulong sender, FastBufferReader r)
        {
            r.ReadValueSafe(out int count);
            for (int i = 0; i < count; i++)
            {
                r.ReadValueSafe(out ulong id);
                r.ReadValueSafe(out Vector3 pos);
                r.ReadValueSafe(out float yaw);
                if (id != LocalClientId && ghosts.TryGetValue(id, out var g)) g.SetPose(pos, yaw);
            }
        }

        void OnWorld(ulong sender, FastBufferReader r)
        {
            r.ReadValueSafe(out ushort count);
            for (int i = 0; i < count; i++)
            {
                r.ReadValueSafe(out ushort index);
                r.ReadValueSafe(out Vector3 pos);
                r.ReadValueSafe(out Quaternion rot);
                targets[index] = (pos, rot);
            }
        }

        void ApplyAttachLocal(Part part, AttachPoint point)
        {
            if (part == null || point == null || point.Owner == null || point.Owner.Vehicle == null) return;
            if (part.MountedOn == point) return;
            if (part.IsCarried)
                foreach (var a in World.All()) if (a.Held == part) { World.ApplyRelease(a, Vector3.zero); break; }
            if (part.Vehicle != null) part.Vehicle.ApplyDetach(part, Vector3.zero);
            point.Owner.Vehicle.ApplyAttach(part, point);
            targets.Remove(part.NetIndex);
        }

        /// <summary>Плавно подтягиваем свободные тела к позициям с хоста.</summary>
        void FollowTargets()
        {
            float k = 1f - Mathf.Exp(-15f * Time.unscaledDeltaTime);
            foreach (var kv in targets)
            {
                var p = PartAt(kv.Key);
                if (p == null || p.MountedOn != null || p.IsCarried) continue;
                var t = p.transform;
                var (pos, rot) = kv.Value;
                if ((t.position - pos).sqrMagnitude > 25f) t.SetPositionAndRotation(pos, rot);
                else t.SetPositionAndRotation(Vector3.Lerp(t.position, pos, k), Quaternion.Slerp(t.rotation, rot, k));
            }
        }

        // ============================ Призраки ============================

        void EnsureGhost(ulong id, string displayName)
        {
            if (id == LocalClientId) return;
            if (ghosts.TryGetValue(id, out var g)) { g.DisplayName = displayName; return; }
            g = RemoteAvatar.Create(id, displayName);
            ghosts[id] = g;
            World.RegisterRemote(g);
        }

        void RemoveGhost(ulong id)
        {
            if (!ghosts.TryGetValue(id, out var g)) return;
            ghosts.Remove(id);
            World.UnregisterRemote(id);
            if (g != null) Destroy(g.gameObject);
        }

        static string SafeName(string n)
        {
            n = string.IsNullOrWhiteSpace(n) ? "Игрок" : n.Trim();
            return n.Length > 16 ? n.Substring(0, 16) : n;
        }
    }
}
