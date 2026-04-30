using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using VStudioCraft.Game;

// Phase 7 (Open to LAN) — ServerHub moved from VStudioCraft.Server to
// VStudioCraft.Net so the standalone client can host an in-process
// server alongside its singleplayer World. The dedicated server's
// `Program.cs` keeps using ServerHub via the same `using VStudioCraft.Net`
// import; nothing else changed about its behaviour.
namespace VStudioCraft.Net
{
    // Top-level server orchestrator. Owns:
    //   - the simulated World
    //   - the TcpListener that accepts new clients
    //   - the list of connected sessions (each in one of: AwaitingLogin,
    //     InGame, Dead)
    //   - the per-tick "drain inbound, advance state, send outbound" loop
    //     that Program.cs calls from inside its 20 Hz tick.
    //
    // Phase 2 scope:
    //   - Accept TCP connections
    //   - Handshake: client sends LoginRequest, server replies with
    //     LoginResponse and queues an initial chunk burst (5x5 around
    //     spawn, paced ~5 chunks per tick so we don't blow out the send
    //     buffer on join)
    //   - Server accepts the client's PlayerPosLook stream but doesn't
    //     act on it yet (no player entity in World yet — that lands in
    //     Phase 3 along with the dig/place packets)
    //   - Server-initiated KeepAlive every 10 s so dead sockets get
    //     surfaced even when traffic is otherwise quiet
    //
    // Threading: Accept runs on a dedicated background thread so the
    // tick loop never blocks on socket accept. Per-session reads run on
    // their own background threads (NetSession.ReadLoop). All session
    // state mutation happens on the tick thread inside Tick(); accept
    // and read threads only enqueue/dequeue thread-safe primitives.
    internal sealed class ServerHub
    {
        // Default port. 25566 (one above Notch's 25565) so a real Minecraft
        // server on the same host doesn't conflict during dev. Configurable
        // via constructor for tests / multi-server hosting.
        public const int DefaultPort = 25566;

        // Phase 4 — drift-correction cadence. Every N ticks each tracked
        // entity gets a full EntityTeleport instead of a delta packet so
        // accumulated float-rounding can't slowly walk replicas off the
        // authoritative position. 20 ticks = 1 s; matches the cadence
        // Alpha used.
        private const int EntityTeleportEveryTicks = 20;

        // Threshold below which we don't bother emitting a move packet
        // at all. 1 mm — well below visible motion at any rendering
        // distance, sized to suppress jitter from the network round-
        // tripping a player who's standing still while WASD-pressing
        // into a wall.
        private const float MoveEpsilon = 0.001f;
        // Minimum yaw/pitch change in degrees that earns a Look broadcast.
        // 0.5° is roughly one screen pixel of arc at typical FOV; smaller
        // changes are imperceptible.
        private const float LookEpsilon = 0.5f;

        // How many chunks to send per tick during the initial join burst.
        // 5 keeps the per-tick byte volume under ~30 KiB (gzipped) which
        // a 1 Mbps client can drain in a single 50 ms tick. Above this
        // the client send buffer fills and Send() starts blocking the
        // tick thread.
        private const int ChunksPerTick = 5;

        // Application-level keepalive cadence. Below the 30 s some routers
        // use as their idle-flow cutoff, well above the 1 s tick.
        private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(10);

        // Server view radius — how far around the spawn (and later, around
        // each player) we send chunks. Matches the client-side default
        // (GameRenderer.ViewDistanceChunks=6) so the client always has
        // mesh data for what it's about to render.
        private const int ServerViewRadius = 6;

        private readonly World _world;
        private readonly TcpListener _listener;
        private readonly Thread _acceptThread;
        private volatile bool _stopping;

        // Per-session state lives in ServerClient; this list is mutated
        // only on the tick thread, but the accept thread enqueues new
        // arrivals via _pendingNew under a short lock.
        private readonly List<ServerClient> _clients = new List<ServerClient>();
        private readonly object _pendingLock = new object();
        private readonly List<NetSession> _pendingNew = new List<NetSession>();

        private int _nextEntityId = 1;

        public ServerHub(World world, int port = DefaultPort)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));

            // KI-1 fix — pre-generate the spawn view-radius window
            // (13×13 = 169 chunks at ServerViewRadius=6) at boot so the
            // first connecting client doesn't trigger 144 chunks of
            // on-demand gen inside its first ~10 ticks. Without this,
            // a fresh server logs 1 s of tick lag on every cold join
            // because TerrainGenerator.Generate + LightCalculator are
            // ~10–20 ms each at 5 chunks/tick.
            //
            // Cost: ~1.5–2 s added to server startup, paid ONCE at boot.
            // Subsequent joins reuse the already-generated chunks; the
            // sliding chunk window also re-uses them when other players
            // walk away then back. A clean trade — startup is one-shot,
            // join lag affects every player.
            PregenerateSpawnArea();

            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();
            _acceptThread = new Thread(AcceptLoop)
            {
                IsBackground = true,
                Name = "NetServer-Accept",
            };
        }

        // Generate every chunk in the spawn view-radius window so the
        // first client to join finds them already present. Spawn is at
        // (0, _, 0); we generate the same 13×13 ring SlideChunkWindow
        // would request anyway on first PlayerPosLook, just up-front.
        private void PregenerateSpawnArea()
        {
            int spawnCx = 0;
            int spawnCz = 0;
            int generated = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int dz = -ServerViewRadius; dz <= ServerViewRadius; dz++)
            for (int dx = -ServerViewRadius; dx <= ServerViewRadius; dx++)
            {
                int cx = spawnCx + dx;
                int cz = spawnCz + dz;
                if (_world.HasChunk(cx, cz)) continue;
                var chunk = new Chunk(cx, cz);
                TerrainGenerator.Generate(chunk, _world.Noise);
                LightCalculator.RecomputeChunk(chunk);
                _world.InstallGeneratedChunk(chunk);
                generated++;
            }
            sw.Stop();
            if (generated > 0)
            {
                Console.WriteLine($"[server] pre-generated {generated} spawn chunks in {sw.ElapsedMilliseconds} ms");
            }
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public int ConnectedCount => _clients.Count;

        public void Start()
        {
            _acceptThread.Start();
        }

        public void Stop()
        {
            _stopping = true;
            try { _listener.Stop(); } catch { /* ignored */ }
            // Politely disconnect each client. The read threads will exit
            // when the socket closes; we don't bother joining them
            // (they're background threads, the process is exiting).
            foreach (var c in _clients)
            {
                c.Session.Send(PacketIds.Disconnect, w => new DisconnectPacket { Reason = "server stopping" }.Write(w));
                c.Session.Disconnect("server stopping");
            }
            _clients.Clear();
        }

        // Called once per tick — handles networking only (drain inbound,
        // broadcast state changes accumulated this tick). Two callers:
        //
        //   - Dedicated server (`Program.RunTickLoop`): pairs Tick() with
        //     SimulateTick() to also drive world simulation each tick.
        //   - Open-to-LAN host (`GameRenderer.TickHubNetwork`): calls
        //     ONLY Tick(); the host's RenderLoop drives world simulation
        //     in the SP path. Calling SimulateTick() here would double-
        //     tick mobs / fluid / crops at frame rate.
        //
        // Order within the tick:
        //   1. Promote pending sockets (handoff from accept thread)
        //   2. Drain inbound queues per client (login, dig, place, pos)
        //      — block edits may flip cells, accumulating BlockChange
        //      records in World._pendingBlockChanges
        //   3. Advance per-client streaming state (chunks, keepalive)
        //   4. Broadcast queued BlockChange records to clients in range
        //   5. Broadcast entity updates (players + mobs)
        //   6. Reap dead sessions
        public void Tick()
        {
            PromotePendingClients();

            for (int i = 0; i < _clients.Count; i++)
            {
                var client = _clients[i];
                if (client.Session.IsDead) continue;
                if (client.Session.IsLoopback) continue; // host has no inbound socket
                DrainInbound(client);
            }

            for (int i = 0; i < _clients.Count; i++)
            {
                var client = _clients[i];
                if (client.Session.IsDead) continue;
                if (client.Session.IsLoopback) continue; // host doesn't need chunks shipped to itself
                AdvanceState(client);
            }

            // 4. Drain world-level block changes accumulated this tick
            //    (player dig/place + future fluid spread / sugar cane
            //    growth) and route them to the clients whose tracked-
            //    chunks set contains the cell. A change OUTSIDE every
            //    client's view radius is dropped silently — the next
            //    ChunkLoad to that area will carry the new bytes.
            BroadcastPendingBlockChanges();

            // 5. Phase 4/5 — per-pair entity replication. Players + mobs.
            //    Reads positions from World / _clients; doesn't simulate.
            BroadcastEntityUpdates();
            BroadcastMobUpdates();
            BroadcastHostileUpdates();

            // 6. Phase 6c polish — furnace progress to clients with an
            //    open furnace window. Drives the visible cook timer
            //    and fuel-burn animation friend-side. Only iterates
            //    clients' OpenWindows (not all furnaces in the world)
            //    so the cost is O(open windows) per tick.
            BroadcastOpenFurnaceUpdates();

            for (int i = _clients.Count - 1; i >= 0; i--)
            {
                if (_clients[i].Session.IsDead)
                {
                    var dead = _clients[i];
                    Console.WriteLine($"[server] {dead.Label} disconnected: {dead.Session.DeadReason}");
                    // Phase 4 — tell every other client to remove the
                    // departing player's entity replica. Cheap broadcast;
                    // the client filters silently if it never tracked this
                    // EntityId (e.g. another disconnect arrived before
                    // they ever entered each other's view).
                    for (int j = 0; j < _clients.Count; j++)
                    {
                        if (j == i) continue;
                        var other = _clients[j];
                        if (other.Session.IsDead) continue;
                        if (other.TrackedEntities.Remove(dead.EntityId))
                        {
                            other.Session.Send(PacketIds.EntityDespawn, w => new EntityDespawnPacket
                            {
                                EntityId = dead.EntityId,
                            }.Write(w));
                        }
                    }
                    _clients.RemoveAt(i);
                }
            }
        }

        // Drive the server-side world simulation passes that aren't tied
        // to a specific client connection. ONLY called by the dedicated
        // server (`Program.RunTickLoop`); the open-to-LAN host's
        // RenderLoop already runs equivalent passes (`TickPassives`,
        // `TickHostiles`, `World.TickMobSpawns`, `FluidTick.Tick`,
        // `TickRandomCrops`) on the host's own update path, and we'd
        // double-tick if we also ran them here.
        public void SimulateTick(float dtSeconds)
        {
            FluidTick.Tick(_world);
            _world.TickRandomCrops(dtSeconds);
            // Mob spawn anchored on the spawn origin for now; per-player
            // spawn anchors are an open follow-up tracked alongside KI-2.
            _world.TickMobSpawns(dtSeconds, OpenTK.Vector3.Zero, skySubtract: 0);
            TickPassiveMobs();
            TickHostileMobs();
        }

        // ---- Open-to-LAN host wiring -----------------------------------
        // The host (a real player on the same machine) doesn't have a
        // socket. EnableLocalHost installs a phantom ServerClient with
        // a loopback NetSession so the existing per-pair broadcast loops
        // include the host as a TARGET (so connecting players see the
        // host's entity moves) without wasting cycles formatting packets
        // for the host as a VIEWER.

        private ServerClient _hostClient;

        public bool HasLocalHost => _hostClient != null;

        public void EnableLocalHost(string username, OpenTK.Vector3 spawnPos, float yaw, float pitch)
        {
            if (_hostClient != null) return;

            var session = NetSession.CreateLoopback(username);
            _hostClient = new ServerClient(session)
            {
                EntityId = _nextEntityId++,
                Username = username,
                Phase = ClientPhase.InGame,
                SpawnX = (int)Math.Floor(spawnPos.X),
                SpawnY = (int)Math.Floor(spawnPos.Y),
                SpawnZ = (int)Math.Floor(spawnPos.Z),
                HasReportedPos = true,
                LastReportedX = spawnPos.X,
                LastReportedY = spawnPos.Y,
                LastReportedZ = spawnPos.Z,
                LastReportedYaw = yaw,
                LastReportedPitch = pitch,
                WindowCx = (int)Math.Floor(spawnPos.X / Chunk.SizeX),
                WindowCz = (int)Math.Floor(spawnPos.Z / Chunk.SizeZ),
            };
            _clients.Add(_hostClient);
            Console.WriteLine($"[lan-host] {username} hosting (eid {_hostClient.EntityId})");
        }

        // Called from the host's RenderLoop each frame. Updates the
        // phantom ServerClient's last-reported pose so the next
        // BroadcastEntityUpdates tick emits the correct deltas to
        // remote viewers.
        public void UpdateLocalHostPose(OpenTK.Vector3 pos, float yaw, float pitch)
        {
            if (_hostClient == null) return;
            _hostClient.LastReportedX = pos.X;
            _hostClient.LastReportedY = pos.Y;
            _hostClient.LastReportedZ = pos.Z;
            _hostClient.LastReportedYaw = yaw;
            _hostClient.LastReportedPitch = pitch;
        }

        // Phase 8 — return one PersistedPlayer per non-loopback,
        // logged-in client, for inclusion in the v13 player table on
        // save. The loopback host is skipped because the host's pose
        // and inventory persist through the existing v9 header /
        // singleplayer path (CameraPos, Health, plus the
        // host-renderer's own Player.Inventory which the SP path
        // doesn't currently serialise — that's a TODO that lands with
        // 6b-extended). Friends fall through this path.
        //
        // We snapshot CURRENTLY-CONNECTED clients only. A friend who
        // disconnected mid-session before save would not be persisted
        // in this version. Phase 8 follow-up: keep a "recently
        // disconnected" cache for ~5 min so a brief reconnect doesn't
        // wipe their state.
        public List<WorldSaveFormat.PersistedPlayer> SnapshotPlayers()
        {
            var snap = new List<WorldSaveFormat.PersistedPlayer>(_clients.Count);
            for (int i = 0; i < _clients.Count; i++)
            {
                var c = _clients[i];
                if (c.Session.IsDead) continue;
                if (c.Session.IsLoopback) continue;
                if (c.Phase == ClientPhase.AwaitingLogin) continue;
                if (string.IsNullOrEmpty(c.Username)) continue;
                // Copy slots (the source array is mutated each tick by
                // pickup logic — defensive copy ensures the on-disk
                // record reflects this exact moment).
                var inv = new ItemStack[Inventory.TotalSlots];
                Array.Copy(c.ServerInventory.Slots, inv, Inventory.TotalSlots);
                snap.Add(new WorldSaveFormat.PersistedPlayer
                {
                    Username = c.Username,
                    X = c.LastReportedX,
                    Y = c.LastReportedY,
                    Z = c.LastReportedZ,
                    Yaw = c.LastReportedYaw,
                    Pitch = c.LastReportedPitch,
                    // Health isn't yet server-authoritative for players
                    // (Phase 6c work). Persist a default — actual
                    // damage and respawn will land alongside that.
                    Health = 20,
                    HeldSlot = c.HeldSlot,
                    Inventory = inv,
                });
            }
            return snap;
        }

        // Phase 8 — restore loaded player records into a lookup the
        // login handler consults. Called by the host's LoadFromFile
        // path (via GameRenderer) before OpenToLan, so a friend
        // reconnecting after a load gets their saved inventory back.
        // Defensive: ignores empty / whitespace usernames.
        private Dictionary<string, WorldSaveFormat.PersistedPlayer> _persistedPlayers;
        public void InstallPersistedPlayers(Dictionary<string, WorldSaveFormat.PersistedPlayer> table)
        {
            _persistedPlayers = table;
        }

        // Phase 8.d — admin console accessors. Read-only enumeration
        // of currently-connected non-loopback clients, identified by
        // username + endpoint for the `list` command. Kick disconnects
        // by username with a server-side reason; matches the friend
        // disconnect pipeline so EntityDespawn etc. fire correctly.
        public IEnumerable<(string username, string endpoint, int eid)> ListClients()
        {
            foreach (var c in _clients)
            {
                if (c.Session.IsDead) continue;
                if (c.Session.IsLoopback) continue;
                yield return (c.Username ?? "<unnamed>", c.Session.RemoteEndpoint, c.EntityId);
            }
        }

        public bool KickByUsername(string username, string reason)
        {
            if (string.IsNullOrEmpty(username)) return false;
            for (int i = 0; i < _clients.Count; i++)
            {
                var c = _clients[i];
                if (c.Session.IsDead) continue;
                if (c.Session.IsLoopback) continue;
                if (!string.Equals(c.Username, username, StringComparison.Ordinal)) continue;
                c.Session.Send(PacketIds.Disconnect, w => new DisconnectPacket
                {
                    Reason = reason ?? "kicked by op",
                }.Write(w));
                c.Session.Disconnect($"kicked: {reason ?? "by op"}");
                return true;
            }
            return false;
        }

        // ---- Phase 5c: lifecycle broadcasts driven by the host -----------
        //
        // The host's existing _drops list lives on GameRenderer (singleplayer
        // path). Rather than refactor every drop-spawn site to know about
        // the network, we let GameRenderer call these helpers each tick to
        // diff its drop list and emit the right packets. Same shape applies
        // to projectiles (Phase 5d).

        public int AllocateEntityId() => _nextEntityId++;

        // Phase 5d — projectile spawn broadcast. Same shape and per-
        // viewer chunk-range filter as BroadcastItemSpawn, with a
        // type byte instead of an item stack. The friend's render
        // path dispatches on the type tag from the spawn so an arrow
        // keeps drawing as an arrow even after ten RelMove updates.
        public void BroadcastProjectileSpawn(int eid, byte projectileType, OpenTK.Vector3 pos, OpenTK.Vector3 vel)
        {
            int cx = (int)Math.Floor(pos.X / Chunk.SizeX);
            int cz = (int)Math.Floor(pos.Z / Chunk.SizeZ);
            for (int v = 0; v < _clients.Count; v++)
            {
                var viewer = _clients[v];
                if (viewer.Session.IsDead) continue;
                if (viewer.Session.IsLoopback) continue;
                if (viewer.Phase == ClientPhase.AwaitingLogin) continue;
                if (!viewer.TrackedChunks.Contains((cx, cz))) continue;
                viewer.TrackedEntities.Add(eid);
                int eidCap = eid;
                byte typeCap = projectileType;
                viewer.Session.Send(PacketIds.ProjectileSpawn, w => new ProjectileSpawnPacket
                {
                    EntityId = eidCap,
                    ProjectileType = typeCap,
                    X = pos.X, Y = pos.Y, Z = pos.Z,
                    Vx = vel.X, Vy = vel.Y, Vz = vel.Z,
                }.Write(w));
            }
        }

        // Broadcast a drop spawn to every viewer whose tracked-chunks set
        // contains the drop's cell. Skips the loopback host (no point
        // shipping to ourselves). Adds the eid to each receiving viewer's
        // TrackedEntities so subsequent RelMove / Despawn packets pass
        // their existing per-viewer "are we tracking this entity?" gate.
        public void BroadcastItemSpawn(int eid, OpenTK.Vector3 pos, OpenTK.Vector3 vel, byte itemType, byte itemCount)
        {
            int cx = (int)Math.Floor(pos.X / Chunk.SizeX);
            int cz = (int)Math.Floor(pos.Z / Chunk.SizeZ);
            for (int v = 0; v < _clients.Count; v++)
            {
                var viewer = _clients[v];
                if (viewer.Session.IsDead) continue;
                if (viewer.Session.IsLoopback) continue;
                if (viewer.Phase == ClientPhase.AwaitingLogin) continue;
                if (!viewer.TrackedChunks.Contains((cx, cz))) continue;
                viewer.TrackedEntities.Add(eid);
                int eidCap = eid;
                viewer.Session.Send(PacketIds.ItemSpawn, w => new ItemSpawnPacket
                {
                    EntityId  = eidCap,
                    X = pos.X, Y = pos.Y, Z = pos.Z,
                    Vx = vel.X, Vy = vel.Y, Vz = vel.Z,
                    ItemType = itemType, ItemCount = itemCount,
                }.Write(w));
            }
        }

        // Despawn an entity-by-id from every viewer that tracks it. Used
        // for drops on pickup / age-out, and for projectiles on impact.
        // Generic enough to belong here rather than being drop-specific.
        public void BroadcastEntityDespawn(int eid)
        {
            for (int v = 0; v < _clients.Count; v++)
            {
                var viewer = _clients[v];
                if (viewer.Session.IsDead) continue;
                if (viewer.Session.IsLoopback) continue;
                if (!viewer.TrackedEntities.Remove(eid)) continue;
                int eidCap = eid;
                viewer.Session.Send(PacketIds.EntityDespawn, w => new EntityDespawnPacket
                {
                    EntityId = eidCap,
                }.Write(w));
            }
        }

        // Phase 6a — drop pickup for connected friends. Host's existing
        // SP-side TickDrops handles the host's own pickup; this method
        // handles friends. Walks every (drop, friend) pair: if the
        // friend's AABB overlap brings them within DroppedItem.PickupRadius
        // and the drop is past its spawn cooldown, the drop is added to
        // the friend's ServerInventory and an InventoryUpdate packet is
        // shipped. The drop's NetworkId is appended to the returned
        // list so the caller can despawn it from the host's _drops on
        // the same tick.
        //
        // No-op when no friend has a tracked position. The host's
        // loopback client is skipped — the host's SP path picks up
        // their own drops via the existing _drops.Remove logic in
        // GameRenderer.TickDrops.
        public void ProcessFriendDropPickups(List<DroppedItem> drops, List<int> pickedUp)
        {
            pickedUp.Clear();
            if (drops.Count == 0) return;
            float radiusSq = DroppedItem.PickupRadius * DroppedItem.PickupRadius;

            for (int di = 0; di < drops.Count; di++)
            {
                var d = drops[di];
                // Skip drops still in their post-spawn cooldown — same
                // gate the host's TickDrops uses so a friend can't
                // pick up a drop the host just spawned and was about
                // to pick up themselves.
                if (d.PickupCooldownSec > 0f) continue;
                if (d.Stack.IsEmpty) continue;
                if (d.NetworkId == 0) continue; // not yet broadcast — let it stabilise

                for (int ci = 0; ci < _clients.Count; ci++)
                {
                    var client = _clients[ci];
                    if (client.Session.IsDead) continue;
                    if (client.Session.IsLoopback) continue; // host handled by SP path
                    if (client.Phase == ClientPhase.AwaitingLogin) continue;
                    if (!client.HasReportedPos) continue;

                    // 2D-ish overlap — match the host's TickDrops which
                    // uses a sphere centred at the player feet+1m. Eye
                    // height is irrelevant for pickup; what matters is
                    // sweeping the player's body cylinder against the
                    // drop's centre.
                    double dx = d.Position.X - client.LastReportedX;
                    double dy = d.Position.Y - (client.LastReportedY + 1.0);
                    double dz = d.Position.Z - client.LastReportedZ;
                    double sq = dx * dx + dy * dy + dz * dz;
                    if (sq > radiusSq) continue;

                    var leftover = client.ServerInventory.TryAdd(d.Stack);
                    // Find which slot(s) ended up with the new content
                    // and ship updates. Since TryAdd doesn't tell us
                    // which slots changed, we ship the whole inventory
                    // in this minimal version — small at 49 slots, and
                    // pickup events are infrequent. The protocol
                    // payload is 3 bytes/slot so a full inventory
                    // refresh is 147 bytes, trivial.
                    SendFullInventory(client);

                    // If TryAdd consumed everything, despawn the drop.
                    // If there was leftover, the drop sticks around with
                    // the leftover stack.
                    if (leftover.IsEmpty)
                    {
                        pickedUp.Add(d.NetworkId);
                        BroadcastEntityDespawn(d.NetworkId);
                        break; // drop is gone; next drop
                    }
                    else
                    {
                        d.Stack = leftover;
                        // Don't break — another player might be in range
                        // for the leftover stack on the next iteration
                        // (rare but legit if two friends pile on a
                        // drop). Continue checking other clients.
                    }
                }
            }
        }

        // Ship every populated slot of a client's ServerInventory as a
        // sequence of InventoryUpdatePacket sends. Used after pickup
        // (granularity-trade described in ProcessFriendDropPickups) and
        // on login to prime the friend with their starting state.
        public void SendFullInventory(ServerClient client)
        {
            if (client.Session.IsDead) return;
            if (client.Session.IsLoopback) return;
            var inv = client.ServerInventory;
            for (byte slot = 0; slot < Inventory.TotalSlots; slot++)
            {
                var s = inv.Slots[slot];
                byte type = (byte)s.Type;
                byte count = (byte)(s.IsEmpty ? 0 : s.Count);
                byte slotCap = slot;
                client.Session.Send(PacketIds.InventoryUpdate, w => new InventoryUpdatePacket
                {
                    Slot = slotCap,
                    ItemType = type,
                    ItemCount = count,
                }.Write(w));
            }
            // Phase 6b-extended — also ship the cursor stack via the
            // 0xFF sentinel slot. The friend's UI draws the cursor as
            // a floating stack while the inventory panel is open;
            // without this, server-driven cursor mutations (LMB pickup,
            // RMB split) wouldn't be visible.
            SendCursor(client);
        }

        // Cursor-only update — used after a single click that didn't
        // change non-cursor slots (rare but possible: LMB on a slot
        // identical to cursor with stack-cap headroom is a no-op).
        // Cheap (3 bytes); no point gating on equality.
        public void SendCursor(ServerClient client)
        {
            if (client.Session.IsDead) return;
            if (client.Session.IsLoopback) return;
            var c = client.ServerInventory.Cursor;
            byte type = (byte)c.Type;
            byte count = (byte)(c.IsEmpty ? 0 : c.Count);
            client.Session.Send(PacketIds.InventoryUpdate, w => new InventoryUpdatePacket
            {
                Slot = 0xFF,
                ItemType = type,
                ItemCount = count,
            }.Write(w));
        }

        // Phase 5e — entity health change broadcast. Used for hurt-flash
        // sync: when a tracked mob takes damage on the host, every
        // viewer that tracks it receives the new health so their replica
        // can mirror the flash. Host-only path; the dedicated server's
        // mob simulation can also use it once damage delivery lands
        // there. Skips the loopback host (the SP path already saw the
        // hurt locally — same reasoning as ItemSpawn).
        public void BroadcastEntityHealth(int eid, short health)
        {
            for (int v = 0; v < _clients.Count; v++)
            {
                var viewer = _clients[v];
                if (viewer.Session.IsDead) continue;
                if (viewer.Session.IsLoopback) continue;
                if (!viewer.TrackedEntities.Contains(eid)) continue;
                int eidCap = eid;
                short hCap = health;
                viewer.Session.Send(PacketIds.EntityHealth, w => new EntityHealthPacket
                {
                    EntityId = eidCap,
                    Health = hCap,
                }.Write(w));
            }
        }

        // Cheap relative move broadcast for entities the host is driving
        // (drops, projectiles). Caller is responsible for tracking the
        // last-broadcast position; we just ship the delta. Skips clients
        // not tracking the entity (they'll get a fresh ItemSpawn / spawn
        // packet when the entity enters their chunk window).
        public void BroadcastEntityRelMove(int eid, float dx, float dy, float dz)
        {
            for (int v = 0; v < _clients.Count; v++)
            {
                var viewer = _clients[v];
                if (viewer.Session.IsDead) continue;
                if (viewer.Session.IsLoopback) continue;
                if (!viewer.TrackedEntities.Contains(eid)) continue;
                int eidCap = eid;
                float dxCap = dx, dyCap = dy, dzCap = dz;
                viewer.Session.Send(PacketIds.EntityRelMove, w => new EntityRelMovePacket
                {
                    EntityId = eidCap,
                    Dx = dxCap, Dy = dyCap, Dz = dzCap,
                }.Write(w));
            }
        }

        private void PromotePendingClients()
        {
            lock (_pendingLock)
            {
                if (_pendingNew.Count == 0) return;
                foreach (var session in _pendingNew)
                {
                    var client = new ServerClient(session)
                    {
                        EntityId = _nextEntityId++,
                    };
                    _clients.Add(client);
                    Console.WriteLine($"[server] connection from {session.RemoteEndpoint} (eid {client.EntityId})");
                }
                _pendingNew.Clear();
            }
        }

        private void DrainInbound(ServerClient client)
        {
            while (client.Session.TryDequeueInbound(out var pkt))
            {
                switch (pkt.Id)
                {
                    case PacketIds.KeepAlive:
                        // Client is alive — nothing to do, just record.
                        client.LastKeepAliveFromPeer = DateTime.UtcNow;
                        break;

                    case PacketIds.LoginRequest:
                        HandleLogin(client, pkt.Login);
                        break;

                    case PacketIds.PlayerPosLook:
                        // Phase 3 — record the reported position so the
                        // chunk-window-streaming pass in AdvanceState can
                        // see when the player has moved into a new chunk
                        // and ship the deltas. Phase 4 will validate the
                        // position against last-tick + max walk speed
                        // and snap with PlayerPosLookCorrect on outliers.
                        if (client.Phase != ClientPhase.AwaitingLogin)
                        {
                            client.LastReportedX = pkt.PlayerPosLook.X;
                            client.LastReportedY = pkt.PlayerPosLook.Y;
                            client.LastReportedZ = pkt.PlayerPosLook.Z;
                            client.LastReportedYaw = pkt.PlayerPosLook.Yaw;
                            client.LastReportedPitch = pkt.PlayerPosLook.Pitch;
                            client.HasReportedPos = true;
                        }
                        break;

                    case PacketIds.PlayerDigStart:
                        HandleDig(client, pkt.PlayerDig);
                        break;

                    case PacketIds.PlayerPlace:
                        HandlePlace(client, pkt.PlayerPlace);
                        break;

                    case PacketIds.PlayerHeldSlot:
                        // Phase 6b — friend changed selected hotbar slot.
                        // Just record; future PlayerUseItem / PlayerDropItem
                        // handlers consult HeldSlot to find the affected
                        // ServerInventory slot.
                        if (pkt.PlayerHeldSlot.Slot < Inventory.HotbarCount)
                            client.HeldSlot = pkt.PlayerHeldSlot.Slot;
                        break;

                    case PacketIds.PlayerDropItem:
                        HandleDropItem(client, pkt.PlayerDropItem);
                        break;

                    case PacketIds.PlayerUseItem:
                        HandleUseItem(client);
                        break;

                    case PacketIds.InventoryClick:
                        HandleInventoryClick(client, pkt.InventoryClick);
                        break;

                    case PacketIds.PlayerInteractBlock:
                        HandleInteractBlock(client, pkt.PlayerInteractBlock);
                        break;

                    case PacketIds.WindowClick:
                        HandleWindowClick(client, pkt.WindowClick);
                        break;

                    case PacketIds.CloseWindow:
                        HandleCloseWindow(client, pkt.CloseWindow);
                        break;

                    case PacketIds.Disconnect:
                        client.Session.Disconnect($"client said: {pkt.Disconnect.Reason}");
                        break;

                    default:
                        // Phase-2 server doesn't expect any other inbound
                        // packets yet. Tolerate them silently so a
                        // forward-compatible Phase-3 client doesn't get
                        // booted by a Phase-2 server. Real protocol-error
                        // handling lives in NetSession.ReadLoop, which
                        // already disconnects on unknown IDs at parse time.
                        break;
                }
            }
        }

        private void HandleLogin(ServerClient client, LoginRequestPacket login)
        {
            if (client.Phase != ClientPhase.AwaitingLogin)
            {
                client.Session.Disconnect("duplicate LoginRequest");
                return;
            }

            if (login.ProtocolVersion != PacketIds.ProtocolVersion)
            {
                var reason = $"protocol mismatch: server={PacketIds.ProtocolVersion}, client={login.ProtocolVersion}";
                client.Session.Send(PacketIds.Disconnect, w => new DisconnectPacket { Reason = reason }.Write(w));
                client.Session.Disconnect(reason);
                return;
            }

            // Username is "offline mode": trust the client. The plan
            // explicitly chose this — no auth server, no shared secret.
            // Sanity-check the length so a malicious client can't cause
            // mojibake or fill the log with garbage.
            var username = (login.Username ?? "").Trim();
            if (username.Length == 0 || username.Length > 16)
            {
                var reason = $"invalid username (length {username.Length})";
                client.Session.Send(PacketIds.Disconnect, w => new DisconnectPacket { Reason = reason }.Write(w));
                client.Session.Disconnect(reason);
                return;
            }

            client.Username = username;
            client.Session.Label = username;
            client.Phase = ClientPhase.LoggingIn;

            // Phase 8 — restore saved state if we have a record for
            // this username. Falls back to default spawn / empty
            // inventory when no record exists (fresh user, or a v12
            // load with no v13 player table).
            int spawnX = 0, spawnY = 80, spawnZ = 0;
            if (_persistedPlayers != null
                && _persistedPlayers.TryGetValue(username, out var saved))
            {
                spawnX = (int)Math.Floor(saved.X);
                spawnY = (int)Math.Floor(saved.Y);
                spawnZ = (int)Math.Floor(saved.Z);
                client.LastReportedX = saved.X;
                client.LastReportedY = saved.Y;
                client.LastReportedZ = saved.Z;
                client.LastReportedYaw = saved.Yaw;
                client.LastReportedPitch = saved.Pitch;
                client.HasReportedPos = true;
                if (saved.HeldSlot >= 0 && saved.HeldSlot < Inventory.HotbarCount)
                    client.HeldSlot = (byte)saved.HeldSlot;
                if (saved.Inventory != null)
                {
                    int n = Math.Min(saved.Inventory.Length, Inventory.TotalSlots);
                    for (int i = 0; i < n; i++)
                        client.ServerInventory.Slots[i] = saved.Inventory[i];
                }
            }
            client.SpawnX = spawnX;
            client.SpawnY = spawnY;
            client.SpawnZ = spawnZ;

            client.Session.Send(PacketIds.LoginResponse, w =>
            {
                new LoginResponsePacket
                {
                    EntityId = client.EntityId,
                    Seed     = _world.Seed,
                    GameMode = (byte)0, // Survival; Phase 8 reads from save
                    SpawnX   = spawnX,
                    SpawnY   = spawnY,
                    SpawnZ   = spawnZ,
                }.Write(w);
            });

            // Queue up the initial chunk window. We don't blast all of it
            // on this tick — AdvanceState ships ChunksPerTick per call so
            // the send buffer doesn't choke on join.
            QueueInitialChunks(client);

            Console.WriteLine($"[server] {username} logged in from {client.Session.RemoteEndpoint} as eid {client.EntityId}");
        }

        private void QueueInitialChunks(ServerClient client)
        {
            int spawnCx = client.SpawnX >> 4;
            int spawnCz = client.SpawnZ >> 4;

            // Spiral outward from spawn so the chunks under and immediately
            // around the player arrive first. The client renders as soon as
            // it has the chunks the camera is in — sending the spawn chunk
            // last would stretch the "loading…" delay needlessly.
            for (int r = 0; r <= ServerViewRadius; r++)
            {
                for (int dz = -r; dz <= r; dz++)
                for (int dx = -r; dx <= r; dx++)
                {
                    // Only emit cells on the ring at radius r — inner rings
                    // were enqueued by previous outer-loop iterations.
                    int ring = Math.Max(Math.Abs(dx), Math.Abs(dz));
                    if (ring != r) continue;
                    var key = (spawnCx + dx, spawnCz + dz);
                    client.PendingChunkSends.Enqueue(key);
                    // Phase A.3 — start gzipping in the background so
                    // ChunksPerTick draws from the cache instead of
                    // blocking the tick thread on inline gzip when
                    // the queue gets popped a few ticks from now.
                    PrefetchChunkCompression(key.Item1, key.Item2);
                }
            }
        }

        private void AdvanceState(ServerClient client)
        {
            // Phase 3 — slide the per-client chunk window when the
            // player crosses chunk boundaries. Computes the new desired
            // window, queues fresh chunks for streaming, and emits
            // ChunkUnload for chunks that scrolled off the back. Runs
            // before the per-tick send-budget so a player walking fast
            // never has the queue empty out before recompute.
            if (client.Phase == ClientPhase.InGame && client.HasReportedPos)
            {
                int curCx = (int)Math.Floor(client.LastReportedX / Chunk.SizeX);
                int curCz = (int)Math.Floor(client.LastReportedZ / Chunk.SizeZ);
                if (curCx != client.WindowCx || curCz != client.WindowCz)
                {
                    SlideChunkWindow(client, curCx, curCz);
                    client.WindowCx = curCx;
                    client.WindowCz = curCz;
                }
            }

            // Send pending chunks (ChunksPerTick max). On login, this
            // drains the spawn-window burst queued by HandleLogin; in
            // steady state, this drains whatever SlideChunkWindow added.
            int sent = 0;
            while (sent < ChunksPerTick && client.PendingChunkSends.Count > 0)
            {
                var (cx, cz) = client.PendingChunkSends.Dequeue();
                SendChunk(client, cx, cz);
                sent++;
            }

            if (client.Phase == ClientPhase.LoggingIn && client.PendingChunkSends.Count == 0)
            {
                client.Phase = ClientPhase.InGame;
                // Initialize the window cursor at the spawn so the first
                // PlayerPosLook in the same chunk doesn't trip a useless
                // SlideChunkWindow recompute.
                client.WindowCx = client.SpawnX >> 4;
                client.WindowCz = client.SpawnZ >> 4;
                // Phase 6a — prime the friend with their server-side
                // inventory state. For a fresh connection this is all
                // empty slots, which both wipes whatever local default
                // the friend's Player constructor populated AND signals
                // "I'm in charge now" so subsequent pickup updates
                // overlay onto a known-empty start state.
                SendFullInventory(client);
                Console.WriteLine($"[server] {client.Label} fully streamed (entered InGame)");
            }

            // Application-level keepalive. We send one whenever the gap
            // since our last outbound exceeds the interval — cheap, no
            // per-client timer needed.
            if (DateTime.UtcNow - client.LastKeepAliveSent > KeepAliveInterval)
            {
                client.Session.Send(PacketIds.KeepAlive);
                client.LastKeepAliveSent = DateTime.UtcNow;
            }
        }

        // Compute the new desired view-radius window around (newCx, newCz),
        // diff against the client's current TrackedChunks set, queue fresh
        // chunks for streaming, and emit ChunkUnload for chunks that
        // scrolled off the back.
        //
        // We don't care about ordering on add (the spawn-window spiral is
        // only relevant on first join, when the camera has nothing to
        // render); steady-state add order has no visual effect because
        // the meshes are already drawn for the cells we're keeping.
        private void SlideChunkWindow(ServerClient client, int newCx, int newCz)
        {
            // Build the new desired set first so we can diff cleanly
            // without N^2 scanning. Sized for the typical 13×13 = 169.
            var desired = new HashSet<(int, int)>();
            for (int dz = -ServerViewRadius; dz <= ServerViewRadius; dz++)
            for (int dx = -ServerViewRadius; dx <= ServerViewRadius; dx++)
                desired.Add((newCx + dx, newCz + dz));

            // Unload anything we used to track but don't anymore. Iterate
            // a copy because we're mutating TrackedChunks inside the loop.
            foreach (var key in new List<(int, int)>(client.TrackedChunks))
            {
                if (desired.Contains(key)) continue;
                client.TrackedChunks.Remove(key);
                client.Session.Send(PacketIds.ChunkUnload, w => new ChunkUnloadPacket
                {
                    ChunkX = key.Item1,
                    ChunkZ = key.Item2,
                }.Write(w));
            }

            // Queue fresh chunks. Skip cells already pending (queued by
            // login or a previous slide that hasn't drained yet) and
            // already tracked (we already shipped these).
            var alreadyPending = new HashSet<(int, int)>(client.PendingChunkSends);
            foreach (var key in desired)
            {
                if (client.TrackedChunks.Contains(key)) continue;
                if (alreadyPending.Contains(key)) continue;
                client.PendingChunkSends.Enqueue(key);
                // Phase A.3 — same prefetch hook as the spawn-ring
                // burst, so chunks visible "ahead" of the player are
                // gzipping in the background while the tick thread
                // drains older chunks at ChunksPerTick.
                PrefetchChunkCompression(key.Item1, key.Item2);
            }
        }

        // ---- intent handlers --------------------------------------------

        // Apply a creative-mode instant-break dig. Phase 5 will gate the
        // "finish" status on a server-side break-progress timer for
        // survival mode; for Phase 3 every dig is treated as instant.
        private void HandleDig(ServerClient client, PlayerDigPacket pkt)
        {
            // Accept dig/place during LoggingIn too — the player has the
            // spawn chunk by then and can already see what they're trying
            // to break, even if the chunk-burst tail is still streaming.
            // Only reject before login completes (AwaitingLogin) when we
            // don't yet have an EntityId and no inventory state.
            if (client.Phase == ClientPhase.AwaitingLogin) return;

            // Status 1 (cancel) is a no-op — we don't track per-player
            // dig progress yet, so cancelling has nothing to undo. Status
            // 0 (start) and 2 (finish) both currently apply the break:
            // creative-style instant break.
            if (pkt.Status == 1) return;

            if (!IsWithinReach(client, pkt.X, pkt.Y, pkt.Z))
            {
                // Reject silently; client will desync visually on this
                // cell until the next chunk re-stream. Phase 4 adds a
                // per-cell BlockChange "snap-back" that explicitly tells
                // the client "no, that block is still here".
                return;
            }

            // World.SetBlock(record:true) appends to _pendingBlockChanges
            // which the broadcast pass at the end of the tick drains.
            _world.SetBlock(pkt.X, pkt.Y, pkt.Z, BlockType.Air, record: true);
        }

        // Phase 6b — friend Q-drop. Server owns the friend's inventory;
        // it removes the right amount from the held hotbar slot and
        // queues a DroppedItem to be spawned via the host-driven sink
        // below. The actual world.Drops mutation happens through the
        // hook the host installs at OpenToLan time — ServerHub doesn't
        // touch _drops directly because that list lives on GameRenderer.
        public Action<OpenTK.Vector3, OpenTK.Vector3, ItemStack> SpawnDropHook;

        // Phase 6b — friend RMB → host spawns a projectile. Same hook
        // pattern as SpawnDropHook: ServerHub doesn't touch _thrown
        // directly because that list lives on GameRenderer; the host
        // installs the hook at OpenToLan time and the next
        // BroadcastLocalProjectilesDiff broadcasts the spawn to everyone.
        // BlockType is the projectile-kind hint (Snowball or Egg).
        public Action<OpenTK.Vector3, OpenTK.Vector3, BlockType> SpawnThrownHook;

        private void HandleDropItem(ServerClient client, PlayerDropItemPacket pkt)
        {
            if (client.Phase == ClientPhase.AwaitingLogin) return;
            if (SpawnDropHook == null) return;          // host hasn't wired the hook
            if (!client.HasReportedPos) return;

            int slotIdx = Inventory.HotbarStart + (client.HeldSlot & 0x07);
            var current = client.ServerInventory.Slots[slotIdx];
            if (current.IsEmpty) return;

            // Mode 0 = drop one item, Mode 1 = drop the whole stack.
            int dropCount = pkt.Mode == 1 ? current.Count : 1;
            if (dropCount <= 0) return;
            var dropStack = new ItemStack(current.Type, dropCount);

            int remaining = current.Count - dropCount;
            client.ServerInventory.Slots[slotIdx] = remaining > 0
                ? new ItemStack(current.Type, remaining)
                : ItemStack.Empty;

            // Toss origin — eye height + small forward offset so the
            // drop visibly arcs forward of the friend's body. Velocity
            // matches the host's Q-drop value so the friend's local-
            // looking trajectory and the host's view of the same drop
            // (via ItemSpawn replication) match.
            const float eyeHeight = 1.6f;
            float yawRad = client.LastReportedYaw * (float)Math.PI / 180f;
            float pitchRad = client.LastReportedPitch * (float)Math.PI / 180f;
            float fx = (float)(-Math.Sin(yawRad) * Math.Cos(pitchRad));
            float fy = (float)(-Math.Sin(pitchRad));
            float fz = (float)(-Math.Cos(yawRad) * Math.Cos(pitchRad));
            var origin = new OpenTK.Vector3(
                (float)(client.LastReportedX + fx * 0.4),
                (float)(client.LastReportedY + eyeHeight + fy * 0.4),
                (float)(client.LastReportedZ + fz * 0.4));
            // 4 m/s along forward gives a familiar Alpha-style toss arc.
            var vel = new OpenTK.Vector3(fx * 4f, fy * 4f + 0.2f, fz * 4f);

            SpawnDropHook(origin, vel, dropStack);

            // Friend's local view of their inventory needs the new state.
            // Cheap: send the one slot that changed.
            byte slotByte = (byte)slotIdx;
            byte typeByte = (byte)client.ServerInventory.Slots[slotIdx].Type;
            byte countByte = client.ServerInventory.Slots[slotIdx].IsEmpty
                ? (byte)0
                : (byte)client.ServerInventory.Slots[slotIdx].Count;
            client.Session.Send(PacketIds.InventoryUpdate, w => new InventoryUpdatePacket
            {
                Slot = slotByte,
                ItemType = typeByte,
                ItemCount = countByte,
            }.Write(w));
        }

        // Phase 6b-extended — friend clicked a slot in their inventory
        // panel (or outside it while holding the cursor stack). The
        // friend's local UI did the hit-test; we receive a slot index
        // + button + shift modifier. We run the same `Inventory.Handle*`
        // methods the host's local UI runs, mutating ServerInventory,
        // and ship the result back.
        //
        // For correctness we always ship the full inventory + cursor —
        // a shift-click can move stacks across many slots, and a
        // single-slot delta protocol would either need to enumerate
        // every changed slot or know which methods touch which slots.
        // The 49-slot+cursor burst is ~150 bytes and clicks are
        // infrequent; not worth optimising.
        //
        // 0xFF sentinel = "outside click while holding cursor" — toss
        // the cursor stack as a DroppedItem at the friend's pose.
        // Reuses the SpawnDropHook the host installed for Q-drop.
        private void HandleInventoryClick(ServerClient client, InventoryClickPacket pkt)
        {
            if (client.Phase == ClientPhase.AwaitingLogin) return;
            var inv = client.ServerInventory;

            if (pkt.Slot == 0xFF)
            {
                // Outside-click cursor toss. Honour only when there's
                // actually a cursor stack to drop — accidental
                // outside-clicks with empty cursor are common.
                if (inv.Cursor.IsEmpty || SpawnThrownHook == null && SpawnDropHook == null)
                {
                    return;
                }
                if (SpawnDropHook != null && client.HasReportedPos)
                {
                    var stack = inv.Cursor;
                    inv.Cursor = ItemStack.Empty;
                    // Same pose math as HandleDropItem so the toss arc
                    // matches a Q-drop visually.
                    const float eyeHeight = 1.6f;
                    float yawRad = client.LastReportedYaw * (float)Math.PI / 180f;
                    float pitchRad = client.LastReportedPitch * (float)Math.PI / 180f;
                    float fx = (float)(-Math.Sin(yawRad) * Math.Cos(pitchRad));
                    float fy = (float)(-Math.Sin(pitchRad));
                    float fz = (float)(-Math.Cos(yawRad) * Math.Cos(pitchRad));
                    var origin = new OpenTK.Vector3(
                        (float)(client.LastReportedX + fx * 0.4),
                        (float)(client.LastReportedY + eyeHeight + fy * 0.4),
                        (float)(client.LastReportedZ + fz * 0.4));
                    var vel = new OpenTK.Vector3(fx * 4f, fy * 4f + 0.2f, fz * 4f);
                    SpawnDropHook(origin, vel, stack);
                    SendCursor(client);
                }
                return;
            }

            if (pkt.Slot >= Inventory.TotalSlots) return;

            // Dispatch to the existing Inventory click logic. These
            // methods are the same code path the host's local UI calls
            // — running them on ServerInventory keeps the friend's
            // experience byte-identical to the host's where the rules
            // matter (stack-merge headroom, armor-slot type gating,
            // etc).
            int slot = pkt.Slot;
            if (pkt.Shift != 0)
            {
                // Phase 6c polish — shift-click cross-area routing.
                // If the client has a chest or furnace window open,
                // shift-click on a player slot tries to push the
                // stack INTO that window first (matching Alpha's
                // behaviour: shift-click in a chest moves the stack
                // toward the chest). Anything that doesn't fit falls
                // through to the regular main↔hotbar shift move.
                //
                // Picks the most-recently-opened window (highest
                // WindowId) when multiple are open. Crafting tables
                // are skipped — shift-click into a 9-slot input grid
                // is unintuitive and Alpha-incompatible (Alpha only
                // routes shift-click into containers, not crafting).
                OpenWindowState target = null;
                byte targetId = 0;
                foreach (var kv in client.OpenWindows)
                {
                    if (kv.Value.Kind == WindowKind.Chest
                     || kv.Value.Kind == WindowKind.Furnace)
                    {
                        if (target == null || kv.Key > targetId)
                        {
                            target = kv.Value;
                            targetId = kv.Key;
                        }
                    }
                }
                if (target != null && !inv.Slots[slot].IsEmpty)
                {
                    PushStackIntoWindow(ref inv.Slots[slot], target);
                    // If anything remains, the leftover takes the
                    // regular shift-click path (main↔hotbar).
                    if (!inv.Slots[slot].IsEmpty)
                    {
                        inv.HandleShiftClickSlot(slot);
                    }
                    // The mutation may have changed the window —
                    // ship a fresh TileEntityData to the clicker AND
                    // any other watchers of the same cell.
                    SendTileEntityData(client, target);
                    BroadcastTileUpdateToOtherViewers(client, target);
                    // Furnace: also write the snapshot back to the
                    // persistent entity since BuildFurnaceWindowSnapshot
                    // copied at open time.
                    if (target.Kind == WindowKind.Furnace && target.FurnaceRef != null)
                    {
                        target.FurnaceRef.Input = target.Slots[0];
                        target.FurnaceRef.Fuel = target.Slots[1];
                        target.FurnaceRef.Output = target.Slots[2];
                    }
                }
                else
                {
                    inv.HandleShiftClickSlot(slot);
                }
            }
            else if (pkt.Button == 1) // RMB
            {
                inv.HandleRightClickSlot(slot);
            }
            else // LMB (default)
            {
                inv.HandleLeftClickSlot(slot);
            }

            // Bandwidth note above — full burst, not a delta.
            SendFullInventory(client);
        }

        // Push a stack into the first slot(s) of a window's backing
        // array, top-up matching first then first-empty. Used by
        // shift-click cross-area routing (player → open window).
        // Furnace gets a slight refinement: only the input slot (0)
        // accepts arbitrary smelting input; fuel slot (1) accepts
        // only fuel-eligible items. Output slot (2) is read-only.
        // Chest accepts any item in any slot.
        private static void PushStackIntoWindow(ref ItemStack from, OpenWindowState target)
        {
            if (from.IsEmpty) return;

            int firstSlot = 0;
            int slotCount = target.Slots.Length;
            int lastSlot = slotCount - 1;

            // Furnace: target only the Input slot for non-fuel items.
            // Fuel slot routing requires a fuel lookup (FurnaceRecipes
            // has IsFuel-style helpers); for the polish pass we keep
            // it simple — let any item go into Input first, then Fuel
            // if the player explicitly shift-clicks a fuel-shaped
            // stack. Output never accepts shift-clicks.
            if (target.Kind == WindowKind.Furnace)
            {
                lastSlot = 1; // 0 = Input, 1 = Fuel; skip Output (2)
            }

            // Top-up matching slots first.
            for (int i = firstSlot; i <= lastSlot; i++)
            {
                if (from.IsEmpty) return;
                if (target.Slots[i].IsEmpty) continue;
                if (target.Slots[i].Type != from.Type) continue;
                int cap = ItemStack.MaxStackSizeFor(target.Slots[i].Type);
                int room = cap - target.Slots[i].Count;
                if (room <= 0) continue;
                int xfer = Math.Min(room, from.Count);
                target.Slots[i] = new ItemStack(target.Slots[i].Type, target.Slots[i].Count + xfer);
                int leftover = from.Count - xfer;
                from = leftover > 0 ? new ItemStack(from.Type, leftover) : ItemStack.Empty;
            }

            // First-empty fallback.
            for (int i = firstSlot; i <= lastSlot; i++)
            {
                if (from.IsEmpty) return;
                if (!target.Slots[i].IsEmpty) continue;
                target.Slots[i] = from;
                from = ItemStack.Empty;
                return;
            }
        }

        // ---- Phase 6c: window open / close / click --------------------

        private void HandleInteractBlock(ServerClient client, PlayerInteractBlockPacket pkt)
        {
            if (client.Phase == ClientPhase.AwaitingLogin) return;
            if (!IsWithinReach(client, pkt.X, pkt.Y, pkt.Z)) return;

            var cell = _world.GetBlock(pkt.X, pkt.Y, pkt.Z);

            // KI-3 — wooden-door RMB toggle. Iron doors fall through
            // to the default branch (Alpha redstone-only behaviour).
            // Mutates the per-chunk meta byte on BOTH halves (top +
            // bottom) so they animate as one unit, then records the
            // change in the world's journal so BroadcastPendingBlockChanges
            // ships the new meta to every viewer in range.
            if (cell == BlockType.WoodDoorBlockBottom || cell == BlockType.WoodDoorBlockTop)
            {
                ToggleWoodDoor(pkt.X, pkt.Y, pkt.Z, cell);
                return;
            }

            switch (cell)
            {
                case BlockType.Chest:
                {
                    var chest = _world.GetOrCreateChestEntity(pkt.X, pkt.Y, pkt.Z);
                    OpenWindow(client, WindowKind.Chest, pkt.X, pkt.Y, pkt.Z,
                        slotCount: ChestTileEntity.SlotCount,
                        chestRef: chest, furnaceRef: null,
                        backingSlots: chest.Slots);
                    break;
                }
                case BlockType.Furnace:
                case BlockType.LitFurnace:
                {
                    var furnace = _world.GetOrCreateFurnaceEntity(pkt.X, pkt.Y, pkt.Z);
                    // Furnace stores three named slots (Input, Fuel,
                    // Output) — flatten into a 3-element array for
                    // window indexing. Writes go back through a
                    // dedicated WindowSlotToFurnace helper at click
                    // time because the named-fields layout doesn't
                    // expose a settable array.
                    OpenWindow(client, WindowKind.Furnace, pkt.X, pkt.Y, pkt.Z,
                        slotCount: 3,
                        chestRef: null, furnaceRef: furnace,
                        backingSlots: BuildFurnaceWindowSnapshot(furnace));
                    break;
                }
                case BlockType.CraftingTable:
                {
                    // Crafting has no persistent tile entity — the
                    // 9 input slots + 1 output live on the OpenWindowState
                    // itself. Close-time commit returns leftovers to the
                    // player.
                    OpenWindow(client, WindowKind.CraftingTable, pkt.X, pkt.Y, pkt.Z,
                        slotCount: 10,
                        chestRef: null, furnaceRef: null,
                        backingSlots: new ItemStack[10]);
                    break;
                }
                default:
                    // Block isn't a window-bearing type — silent no-op.
                    // Friends can still get RMB-place behaviour through
                    // the existing PlayerPlace path which fires
                    // alongside this packet.
                    return;
            }
        }

        private static ItemStack[] BuildFurnaceWindowSnapshot(FurnaceTileEntity f) => new[]
        {
            f.Input, f.Fuel, f.Output,
        };

        // KI-3 — server-side door toggle. The block TYPE stays the same
        // (still a WoodDoorBlock half) but the per-cell meta byte's
        // open bit flips. We mirror GameRenderer.ToggleWoodDoor exactly:
        // read the meta from whichever half was clicked, flip the open
        // bit, stamp it on BOTH halves so they animate as one unit.
        // Both cells get journal-recorded so connecting viewers receive
        // a BlockChange with the new meta and rebuild their door
        // rendering.
        //
        // Iron doors are NOT routed here (Alpha redstone-only); the
        // caller's switch falls through for IronDoorBlock* cells.
        private void ToggleWoodDoor(int x, int y, int z, BlockType here)
        {
            int cx = x >> 4, cz = z >> 4;
            var chunk = _world.GetChunk(cx, cz);
            if (chunk == null) return;
            int lx = x - (cx << 4);
            int lz = z - (cz << 4);

            int otherY = BlockData.IsDoorBottom(here) ? y + 1 : y - 1;
            BlockType other = _world.GetBlock(x, otherY, z);

            byte meta = chunk.GetMeta(lx, y, lz);
            byte flipped = BlockData.DoorWithOpen(meta, !BlockData.DoorIsOpen(meta));
            chunk.SetMeta(lx, y, lz, flipped);
            _world.RecordMetaChange(x, y, z);
            if (BlockData.IsDoor(other))
            {
                chunk.SetMeta(lx, otherY, lz, flipped);
                _world.RecordMetaChange(x, otherY, z);
            }
        }

        private void OpenWindow(ServerClient client, byte kind, int x, int y, int z,
            int slotCount, ChestTileEntity chestRef, FurnaceTileEntity furnaceRef, ItemStack[] backingSlots)
        {
            byte id = client.NextWindowId++;
            // Wrap to skip 0 (player inventory) on overflow.
            if (id == 0) { client.NextWindowId = 2; id = 1; }

            var state = new OpenWindowState
            {
                WindowId = id,
                Kind = kind,
                CellX = x, CellY = y, CellZ = z,
                Slots = backingSlots,
                ChestRef = chestRef,
                FurnaceRef = furnaceRef,
            };
            client.OpenWindows[id] = state;

            client.Session.Send(PacketIds.OpenWindow, w => new OpenWindowPacket
            {
                WindowId = id,
                Kind = kind,
                SlotCount = (byte)slotCount,
                X = x, Y = y, Z = z,
            }.Write(w));

            SendTileEntityData(client, state);
        }

        private void SendTileEntityData(ServerClient client, OpenWindowState st)
        {
            if (client.Session.IsDead || client.Session.IsLoopback) return;
            byte slotCount = (byte)(st.Slots?.Length ?? 0);
            byte kind = st.Kind;
            byte windowId = st.WindowId;
            ItemStack[] slotsCopy = (ItemStack[])(st.Slots?.Clone() ?? new ItemStack[0]);

            int burnTime = 0, maxBurn = 0, cookProgress = 0;
            if (kind == WindowKind.Furnace && st.FurnaceRef != null)
            {
                burnTime = st.FurnaceRef.BurnTimeTicks;
                maxBurn = st.FurnaceRef.MaxBurnTimeTicks;
                cookProgress = st.FurnaceRef.CookProgressTicks;
            }

            client.Session.Send(PacketIds.TileEntityData, w => new TileEntityDataPacket
            {
                WindowId = windowId,
                Kind = kind,
                SlotCount = slotCount,
                Slots = slotsCopy,
                FurnaceBurnTime = burnTime,
                FurnaceMaxBurnTime = maxBurn,
                FurnaceCookProgress = cookProgress,
            }.Write(w));
        }

        private void HandleWindowClick(ServerClient client, WindowClickPacket pkt)
        {
            if (client.Phase == ClientPhase.AwaitingLogin) return;
            if (!client.OpenWindows.TryGetValue(pkt.WindowId, out var st)) return;
            if (pkt.Slot >= st.Slots.Length) return;

            // Build a temporary Inventory-like wrapper so we can reuse
            // the existing HandleLeft/Right/Shift methods. Since
            // Inventory's click logic operates on its `Slots[]` array
            // directly, we can hijack it by routing through a fresh
            // Inventory instance whose Slots reference our window
            // backing — except Inventory.Slots is `readonly`.
            //
            // Simpler: replicate the click semantics inline. The chest
            // / crafting / furnace surface is small enough that
            // rolling our own (LMB pickup/drop, RMB split/place-one,
            // shift-click transfer) is faster than reworking
            // Inventory's class shape.
            var inv = client.ServerInventory;
            ref ItemStack slot = ref st.Slots[pkt.Slot];
            if (pkt.Shift != 0)
            {
                // Shift-click: move stack between window and player
                // inventory. Direction is "FROM the slot you clicked
                // TO the other side."
                if (st.Kind == WindowKind.CraftingTable && pkt.Slot == 9)
                {
                    // Shift-click on crafting output — Phase 6c.4
                    // adds repeat-craft-while-possible. Single-craft
                    // for now: pretend it's a regular pickup.
                    DoCraftPickup(client, st, ref slot, intoCursor: false, intoInv: true);
                }
                else
                {
                    // Try to merge `slot` into the player inventory.
                    var moving = slot;
                    slot = ItemStack.Empty;
                    var remainder = inv.TryAdd(moving);
                    if (!remainder.IsEmpty)
                    {
                        // Couldn't fit it all — leftover goes back into
                        // the slot.
                        slot = remainder;
                    }
                }
            }
            else if (pkt.Button == 1) // RMB
            {
                if (st.Kind == WindowKind.CraftingTable && pkt.Slot == 9)
                {
                    DoCraftPickup(client, st, ref slot, intoCursor: true, intoInv: false);
                }
                else
                {
                    HandleWindowSlotRMB(ref slot, ref inv.Cursor);
                }
            }
            else // LMB
            {
                if (st.Kind == WindowKind.CraftingTable && pkt.Slot == 9)
                {
                    DoCraftPickup(client, st, ref slot, intoCursor: true, intoInv: false);
                }
                else
                {
                    HandleWindowSlotLMB(ref slot, ref inv.Cursor);
                }
            }

            // Crafting input slot mutated → recompute output. Output
            // slot itself is index 9; inputs are 0..8.
            if (st.Kind == WindowKind.CraftingTable && pkt.Slot < 9)
            {
                RecomputeCraftOutput(st);
            }

            // Furnace slots mutated → write back to the persistent
            // tile entity. The 3-slot snapshot we built at open time
            // is a copy, so writes need to flow through here.
            if (st.Kind == WindowKind.Furnace && st.FurnaceRef != null && pkt.Slot < 3)
            {
                if (pkt.Slot == 0) st.FurnaceRef.Input = st.Slots[0];
                else if (pkt.Slot == 1) st.FurnaceRef.Fuel = st.Slots[1];
                else if (pkt.Slot == 2) st.FurnaceRef.Output = st.Slots[2];
            }
            // Chest slots aliased the entity's array directly so
            // there's nothing to write back.

            // Reply: the window's slots + the player's inventory and
            // cursor (in case the click moved an item between the
            // two).
            SendTileEntityData(client, st);
            SendFullInventory(client);

            // Phase 6c polish — multi-friend chest sync. Chest contents
            // live on the persistent ChestTileEntity (this same object
            // is referenced by every viewer's window state). When one
            // friend clicks a chest slot, every OTHER client watching
            // the same cell needs a fresh TileEntityData so their view
            // doesn't go stale. Furnace state syncs the same way.
            // Crafting windows are per-client (no shared state), so
            // skipped.
            if (st.Kind == WindowKind.Chest || st.Kind == WindowKind.Furnace)
            {
                BroadcastTileUpdateToOtherViewers(client, st);
            }
        }

        // Phase 6c polish — per-tick furnace progress broadcast. For
        // each client with at least one open furnace window, compare
        // the FurnaceTileEntity's live state against the last-sent
        // snapshot stored on the OpenWindowState. Mismatches ship a
        // fresh TileEntityData so the friend's UI shows the cook
        // timer animating + the fuel sprite emptying.
        //
        // Slot changes are NOT diffed here — they're already covered
        // by HandleWindowClick's reply (when a click mutates the
        // furnace) and by the host's local SP path which also flows
        // through HandleWindowClick when the host has the furnace
        // open. So this pass is purely about the cook/burn timers.
        private void BroadcastOpenFurnaceUpdates()
        {
            for (int c = 0; c < _clients.Count; c++)
            {
                var client = _clients[c];
                if (client.Session.IsDead) continue;
                if (client.Session.IsLoopback) continue;
                if (client.OpenWindows.Count == 0) continue;
                foreach (var kv in client.OpenWindows)
                {
                    var ow = kv.Value;
                    if (ow.Kind != WindowKind.Furnace) continue;
                    if (ow.FurnaceRef == null) continue;
                    var f = ow.FurnaceRef;
                    bool changed = !ow.LastSentInitialized
                        || ow.LastSentBurnTime != f.BurnTimeTicks
                        || ow.LastSentMaxBurnTime != f.MaxBurnTimeTicks
                        || ow.LastSentCookProgress != f.CookProgressTicks;
                    if (!changed) continue;

                    // Refresh the slot snapshot too (fuel might have
                    // burned a stack down) so the resend reflects the
                    // current contents, not the open-time copy.
                    ow.Slots = BuildFurnaceWindowSnapshot(f);
                    SendTileEntityData(client, ow);
                    ow.LastSentBurnTime = f.BurnTimeTicks;
                    ow.LastSentMaxBurnTime = f.MaxBurnTimeTicks;
                    ow.LastSentCookProgress = f.CookProgressTicks;
                    ow.LastSentInitialized = true;
                }
            }
        }

        // Walk the client list, and for any client (other than the
        // clicker) whose open windows include a window pointing at the
        // same cell + kind, ship a fresh TileEntityData. Cell match,
        // not WindowId match — each client allocates its own per-client
        // window id, so two friends in the same chest have different
        // ids on the same cell.
        private void BroadcastTileUpdateToOtherViewers(ServerClient origin, OpenWindowState originSt)
        {
            for (int i = 0; i < _clients.Count; i++)
            {
                var other = _clients[i];
                if (ReferenceEquals(other, origin)) continue;
                if (other.Session.IsDead) continue;
                if (other.Session.IsLoopback) continue;
                foreach (var kv in other.OpenWindows)
                {
                    var ow = kv.Value;
                    if (ow.Kind != originSt.Kind) continue;
                    if (ow.CellX != originSt.CellX) continue;
                    if (ow.CellY != originSt.CellY) continue;
                    if (ow.CellZ != originSt.CellZ) continue;
                    // Re-snapshot the backing entity for the other
                    // viewer's window, since for furnaces the snapshot
                    // is a copy that needs refreshing from the live
                    // FurnaceTileEntity. Chest slots alias directly
                    // and don't need the snapshot rebuild, but the
                    // SendTileEntityData call below clones-on-write so
                    // doing it for both kinds is harmless and
                    // future-proof.
                    if (ow.Kind == WindowKind.Furnace && ow.FurnaceRef != null)
                    {
                        ow.Slots = BuildFurnaceWindowSnapshot(ow.FurnaceRef);
                    }
                    SendTileEntityData(other, ow);
                    break; // one window per cell per other-viewer is enough
                }
            }
        }

        // LMB on a window slot. Mirror of Inventory.HandleLeftClickSlot
        // but operating on a (slot, cursor) pair rather than the
        // player's full inventory.
        private static void HandleWindowSlotLMB(ref ItemStack slot, ref ItemStack cursor)
        {
            if (cursor.IsEmpty)
            {
                // Pick up entire slot.
                cursor = slot;
                slot = ItemStack.Empty;
            }
            else if (slot.IsEmpty)
            {
                // Drop entire cursor into empty slot.
                slot = cursor;
                cursor = ItemStack.Empty;
            }
            else if (slot.Type == cursor.Type)
            {
                // Same type — merge cursor into slot up to the cap.
                int cap = ItemStack.MaxStackSizeFor(slot.Type);
                int room = cap - slot.Count;
                int xfer = System.Math.Min(room, cursor.Count);
                if (xfer > 0)
                {
                    slot = new ItemStack(slot.Type, slot.Count + xfer);
                    int leftover = cursor.Count - xfer;
                    cursor = leftover > 0 ? new ItemStack(cursor.Type, leftover) : ItemStack.Empty;
                }
            }
            else
            {
                // Different types — swap.
                var tmp = slot;
                slot = cursor;
                cursor = tmp;
            }
        }

        // RMB on a window slot. Pick-up-half on empty cursor; place-one
        // on non-empty cursor; swap on type mismatch.
        private static void HandleWindowSlotRMB(ref ItemStack slot, ref ItemStack cursor)
        {
            if (cursor.IsEmpty)
            {
                // Pick up half the slot, rounded up.
                if (slot.IsEmpty) return;
                int half = (slot.Count + 1) / 2;
                cursor = new ItemStack(slot.Type, half);
                int remaining = slot.Count - half;
                slot = remaining > 0 ? new ItemStack(slot.Type, remaining) : ItemStack.Empty;
            }
            else if (slot.IsEmpty || (slot.Type == cursor.Type && slot.Count < ItemStack.MaxStackSizeFor(slot.Type)))
            {
                // Place one item from cursor into slot.
                if (slot.IsEmpty) slot = new ItemStack(cursor.Type, 1);
                else slot = new ItemStack(slot.Type, slot.Count + 1);
                int leftover = cursor.Count - 1;
                cursor = leftover > 0 ? new ItemStack(cursor.Type, leftover) : ItemStack.Empty;
            }
            else if (slot.Type != cursor.Type)
            {
                // Different types — swap (RMB swap matches LMB swap;
                // Alpha treats this as a no-op but the cleaner behaviour
                // is symmetry with LMB).
                var tmp = slot;
                slot = cursor;
                cursor = tmp;
            }
        }

        // Crafting helpers ------------------------------------------

        private static void RecomputeCraftOutput(OpenWindowState st)
        {
            // CraftingRecipes operates on a 9-element ItemStack input.
            // Match returns the resulting ItemStack or empty if no
            // recipe matches; we copy the inputs into a temp array
            // because Match might rotate / shift the grid internally.
            var input = new ItemStack[9];
            System.Array.Copy(st.Slots, 0, input, 0, 9);
            st.Slots[9] = CraftingRecipes.Match(input);
        }

        // Output-slot click. intoCursor=true picks the result into the
        // player's cursor (LMB / RMB on output); intoInv=true shift-
        // clicks into the player inventory. Either way, the inputs
        // are decremented by one each.
        private void DoCraftPickup(ServerClient client, OpenWindowState st, ref ItemStack outSlot, bool intoCursor, bool intoInv)
        {
            if (outSlot.IsEmpty) return;
            var inv = client.ServerInventory;

            if (intoCursor)
            {
                if (inv.Cursor.IsEmpty)
                {
                    inv.Cursor = outSlot;
                }
                else if (inv.Cursor.Type == outSlot.Type)
                {
                    int cap = ItemStack.MaxStackSizeFor(inv.Cursor.Type);
                    int room = cap - inv.Cursor.Count;
                    if (room < outSlot.Count) return; // can't take all of it
                    inv.Cursor = new ItemStack(inv.Cursor.Type, inv.Cursor.Count + outSlot.Count);
                }
                else
                {
                    return; // mismatched cursor type — Alpha refuses
                }
            }
            else if (intoInv)
            {
                var leftover = inv.TryAdd(outSlot);
                if (!leftover.IsEmpty)
                {
                    // No room — abort the craft.
                    return;
                }
            }

            // Decrement each input by one.
            for (int i = 0; i < 9; i++)
            {
                if (st.Slots[i].IsEmpty) continue;
                int newCount = st.Slots[i].Count - 1;
                st.Slots[i] = newCount > 0
                    ? new ItemStack(st.Slots[i].Type, newCount)
                    : ItemStack.Empty;
            }
            // Rerun the match — same grid pattern with one fewer of
            // each ingredient might still yield the same recipe (e.g.
            // 4 planks, only one consumed leaves 3 — no crafting
            // table is on a 2×2 recipe, but a 3-plank stack of slabs
            // could still match if the recipe is tolerant; the
            // recompute handles all cases uniformly).
            RecomputeCraftOutput(st);
        }

        private void HandleCloseWindow(ServerClient client, CloseWindowPacket pkt)
        {
            if (!client.OpenWindows.TryGetValue(pkt.WindowId, out var st)) return;
            client.OpenWindows.Remove(pkt.WindowId);

            // Crafting close: dump leftover inputs into player or onto
            // the floor. Cursor stack from any open window goes onto
            // the player (or to the floor if inventory is full).
            if (st.Kind == WindowKind.CraftingTable)
            {
                for (int i = 0; i < 9; i++)
                {
                    if (st.Slots[i].IsEmpty) continue;
                    var leftover = client.ServerInventory.TryAdd(st.Slots[i]);
                    if (!leftover.IsEmpty && SpawnDropHook != null && client.HasReportedPos)
                    {
                        SpawnDropHook(
                            new OpenTK.Vector3((float)client.LastReportedX, (float)(client.LastReportedY + 1.0), (float)client.LastReportedZ),
                            OpenTK.Vector3.Zero, leftover);
                    }
                }
            }

            // Cursor commit (any window kind). Tries to add to player
            // inventory; if no room, tossed at the player's feet.
            if (!client.ServerInventory.Cursor.IsEmpty)
            {
                var leftover = client.ServerInventory.TryAdd(client.ServerInventory.Cursor);
                client.ServerInventory.Cursor = ItemStack.Empty;
                if (!leftover.IsEmpty && SpawnDropHook != null && client.HasReportedPos)
                {
                    SpawnDropHook(
                        new OpenTK.Vector3((float)client.LastReportedX, (float)(client.LastReportedY + 1.0), (float)client.LastReportedZ),
                        OpenTK.Vector3.Zero, leftover);
                }
            }

            SendFullInventory(client);
        }

        // Phase 6b — friend RMB intent. Decode the held hotbar slot's
        // contents and decide what to do. Phase 6b ships the snowball
        // and egg paths only — both are single-shot intents that don't
        // need a draw-charge timer. Bow draw, bucket use, fishing rod
        // cast, and door toggle all need additional state plumbing
        // (Phase 6c).
        private void HandleUseItem(ServerClient client)
        {
            if (client.Phase == ClientPhase.AwaitingLogin) return;
            if (SpawnThrownHook == null) return;
            if (!client.HasReportedPos) return;

            int slotIdx = Inventory.HotbarStart + (client.HeldSlot & 0x07);
            var stack = client.ServerInventory.Slots[slotIdx];
            if (stack.IsEmpty) return;

            // Only the throwables this phase ships. Other types are a
            // silent no-op — the friend's RMB doesn't trigger anything
            // visible, which is honest about the not-yet-implemented
            // surface.
            bool isThrowable = stack.Type == BlockType.Snowball || stack.Type == BlockType.Egg;
            if (!isThrowable) return;

            // Toss origin + velocity match the host's existing snowball/
            // egg fire path: ~22 m/s along forward from eye height.
            const float eyeHeight = 1.6f;
            float yawRad = client.LastReportedYaw * (float)Math.PI / 180f;
            float pitchRad = client.LastReportedPitch * (float)Math.PI / 180f;
            float fx = (float)(-Math.Sin(yawRad) * Math.Cos(pitchRad));
            float fy = (float)(-Math.Sin(pitchRad));
            float fz = (float)(-Math.Cos(yawRad) * Math.Cos(pitchRad));
            var origin = new OpenTK.Vector3(
                (float)(client.LastReportedX + fx * 0.4),
                (float)(client.LastReportedY + eyeHeight + fy * 0.4),
                (float)(client.LastReportedZ + fz * 0.4));
            const float MuzzleSpeed = 22f;
            var vel = new OpenTK.Vector3(fx * MuzzleSpeed, fy * MuzzleSpeed, fz * MuzzleSpeed);

            SpawnThrownHook(origin, vel, stack.Type);

            // Decrement the friend's stack and ship the slot update.
            int newCount = stack.Count - 1;
            client.ServerInventory.Slots[slotIdx] = newCount > 0
                ? new ItemStack(stack.Type, newCount)
                : ItemStack.Empty;
            byte slotByte = (byte)slotIdx;
            byte typeByte = (byte)client.ServerInventory.Slots[slotIdx].Type;
            byte countByte = client.ServerInventory.Slots[slotIdx].IsEmpty
                ? (byte)0
                : (byte)client.ServerInventory.Slots[slotIdx].Count;
            client.Session.Send(PacketIds.InventoryUpdate, w => new InventoryUpdatePacket
            {
                Slot = slotByte,
                ItemType = typeByte,
                ItemCount = countByte,
            }.Write(w));
        }

        private void HandlePlace(ServerClient client, PlayerPlacePacket pkt)
        {
            if (client.Phase == ClientPhase.AwaitingLogin) return;

            // Translate (clicked-cell, face) -> (target-cell) — the
            // adjacent empty cell we actually place into.
            int tx = pkt.X, ty = pkt.Y, tz = pkt.Z;
            switch (pkt.Face)
            {
                case 0: ty -= 1; break; // -Y
                case 1: ty += 1; break; // +Y
                case 2: tz -= 1; break; // -Z
                case 3: tz += 1; break; // +Z
                case 4: tx -= 1; break; // -X
                case 5: tx += 1; break; // +X
                default: return;        // bogus face -> reject
            }

            if (!IsWithinReach(client, tx, ty, tz)) return;
            if (ty < 0 || ty >= Chunk.SizeY) return;

            // Only place if the target cell is currently air. The client
            // raycast already filters this, but a hostile or out-of-sync
            // client could send a place onto a solid cell — ignore.
            int cx = tx >> 4, cz = tz >> 4;
            int lx = ((tx % Chunk.SizeX) + Chunk.SizeX) % Chunk.SizeX;
            int lz = ((tz % Chunk.SizeZ) + Chunk.SizeZ) % Chunk.SizeZ;
            var chunk = _world.GetChunk(cx, cz);
            if (chunk == null) return;
            if (chunk.Get(lx, ty, lz) != BlockType.Air) return;

            // Trust the client-supplied block type for Phase 3 — Phase 6
            // cross-checks against the server's authoritative inventory.
            var t = (BlockType)pkt.BlockType;
            // Don't accept Air-as-place (would be a no-op anyway, but
            // explicit) or non-block items like tools / food. A simple
            // gate: only types whose enum value is in the placeable
            // range. The full validation table can land alongside the
            // inventory work.
            if (t == BlockType.Air) return;

            _world.SetBlock(tx, ty, tz, t, record: true);
        }

        // Cheap server-side reach check. Real Alpha allows ~5 blocks
        // (Minecraft.MAX_REACH_DISTANCE = 5.0). We use 6.0 as a tolerance
        // bump so a high-ping client whose camera was slightly past 5
        // when they clicked doesn't get rejected. Replaceable with a
        // real raycast in Phase 5 once survival timing matters more.
        private bool IsWithinReach(ServerClient client, int wx, int wy, int wz)
        {
            if (!client.HasReportedPos) return false;
            const double maxReach = 6.0;
            double dx = (wx + 0.5) - client.LastReportedX;
            double dy = (wy + 0.5) - (client.LastReportedY + 1.6); // eye height
            double dz = (wz + 0.5) - client.LastReportedZ;
            return dx * dx + dy * dy + dz * dz <= maxReach * maxReach;
        }

        // ---- broadcast --------------------------------------------------

        // Phase 4 — entity replication. For each pair (viewer, target) of
        // in-game clients (target != viewer), ensure the viewer knows
        // about the target's current position. Three sub-cases:
        //
        //   1. viewer hasn't seen target yet → EntitySpawn + EntityTeleport
        //      (the Spawn carries an absolute position, but we also send
        //      a Teleport on the immediately following tick so any
        //      delta-packet reuses a fresh anchor).
        //   2. viewer already tracks target, drift-correction is due,
        //      OR the position changed by more than the renderer's
        //      delta-precision → EntityTeleport (resets the anchor).
        //   3. position changed by a small amount → EntityRelMove or
        //      EntityRelMoveLook (cheaper packet, half the bytes).
        //
        // The sub-cases use a per-target ANCHOR shared across all viewers:
        // we don't need a per-(viewer, target) anchor because every viewer
        // sees the same broadcast, so they all reach the same end state.
        // Saves N^2 anchor storage and keeps the loop O(viewers × targets).
        private void BroadcastEntityUpdates()
        {
            // Step 1: each in-game client computes whether its anchor
            // moved this tick. We do this once per target before iterating
            // viewers so we know which packet shape (none / Look /
            // RelMove / RelMoveLook / Teleport) applies.
            for (int t = 0; t < _clients.Count; t++)
            {
                var target = _clients[t];
                if (target.Session.IsDead) continue;
                if (target.Phase == ClientPhase.AwaitingLogin) continue;
                if (!target.HasReportedPos) continue;

                target.TicksSinceTeleport++;

                if (!target.AnchorValid)
                {
                    // First broadcast for this target since spawn — ANCHOR
                    // to whatever position they last reported (the spawn
                    // position from LoginResponse, or whatever PlayerPosLook
                    // they sent first). The actual EntitySpawn for new
                    // viewers is shipped inside Step 2 below; this just
                    // primes the per-target anchor so deltas have a base.
                    target.AnchorX = target.LastReportedX;
                    target.AnchorY = target.LastReportedY;
                    target.AnchorZ = target.LastReportedZ;
                    target.AnchorYaw = target.LastReportedYaw;
                    target.AnchorPitch = target.LastReportedPitch;
                    target.AnchorValid = true;
                    target.TicksSinceTeleport = 0;
                }
            }

            // Step 2: pair-wise. For each viewer, ensure they have spawn
            // packets for every target whose chunk is in their tracked
            // window, and emit the per-target move/look/teleport packet.
            for (int v = 0; v < _clients.Count; v++)
            {
                var viewer = _clients[v];
                if (viewer.Session.IsDead) continue;
                if (viewer.Session.IsLoopback) continue; // don't ship to local host
                if (viewer.Phase == ClientPhase.AwaitingLogin) continue;

                for (int t = 0; t < _clients.Count; t++)
                {
                    if (t == v) continue;
                    var target = _clients[t];
                    if (target.Session.IsDead) continue;
                    if (target.Phase == ClientPhase.AwaitingLogin) continue;
                    if (!target.HasReportedPos) continue;

                    int targetCx = (int)Math.Floor(target.LastReportedX / Chunk.SizeX);
                    int targetCz = (int)Math.Floor(target.LastReportedZ / Chunk.SizeZ);
                    bool targetInView = viewer.TrackedChunks.Contains((targetCx, targetCz));

                    bool alreadyTracked = viewer.TrackedEntities.Contains(target.EntityId);

                    // Out of view: if we previously tracked them, drop them
                    // now via Despawn (player walked far enough that the
                    // other player should disappear). Either way, no
                    // further packets to emit this pair.
                    if (!targetInView)
                    {
                        if (alreadyTracked)
                        {
                            viewer.TrackedEntities.Remove(target.EntityId);
                            viewer.Session.Send(PacketIds.EntityDespawn, w => new EntityDespawnPacket
                            {
                                EntityId = target.EntityId,
                            }.Write(w));
                        }
                        continue;
                    }

                    // In view, not yet tracked: emit EntitySpawn followed
                    // by an immediate Teleport so the client has a clean
                    // absolute anchor before any deltas can arrive.
                    if (!alreadyTracked)
                    {
                        viewer.TrackedEntities.Add(target.EntityId);
                        var snap = target;
                        viewer.Session.Send(PacketIds.EntitySpawn, w => new EntitySpawnPacket
                        {
                            EntityId    = snap.EntityId,
                            EntityType  = EntityType.Player,
                            X           = snap.LastReportedX,
                            Y           = snap.LastReportedY,
                            Z           = snap.LastReportedZ,
                            Yaw         = snap.LastReportedYaw,
                            Pitch       = snap.LastReportedPitch,
                            DisplayName = snap.Username ?? "?",
                        }.Write(w));
                        // Skip the move-packet selection below for THIS
                        // pair this tick — Spawn carried the absolute,
                        // and the next tick will pick up where it left
                        // off with deltas relative to this position.
                        continue;
                    }

                    // Already tracked: emit the cheapest applicable update.
                    // We use the TARGET's per-tick anchor delta — same for
                    // every viewer — to decide which packet to send.
                    double dx = target.LastReportedX - target.AnchorX;
                    double dy = target.LastReportedY - target.AnchorY;
                    double dz = target.LastReportedZ - target.AnchorZ;
                    float dyaw = NormalizeDegrees(target.LastReportedYaw - target.AnchorYaw);
                    float dpitch = target.LastReportedPitch - target.AnchorPitch;

                    bool moveSig = Math.Abs(dx) > MoveEpsilon || Math.Abs(dy) > MoveEpsilon || Math.Abs(dz) > MoveEpsilon;
                    bool lookSig = Math.Abs(dyaw) > LookEpsilon || Math.Abs(dpitch) > LookEpsilon;
                    bool teleportDue = target.TicksSinceTeleport >= EntityTeleportEveryTicks;
                    // Float deltas overflow precision past ~16 blocks
                    // — beyond that we must Teleport regardless of the
                    // tick counter to avoid a single delta drifting the
                    // replica wildly off-position.
                    bool teleportFar = Math.Abs(dx) > 16 || Math.Abs(dy) > 16 || Math.Abs(dz) > 16;

                    if (teleportDue || teleportFar)
                    {
                        var snap = target;
                        viewer.Session.Send(PacketIds.EntityTeleport, w => new EntityTeleportPacket
                        {
                            EntityId = snap.EntityId,
                            X        = snap.LastReportedX,
                            Y        = snap.LastReportedY,
                            Z        = snap.LastReportedZ,
                            Yaw      = snap.LastReportedYaw,
                            Pitch    = snap.LastReportedPitch,
                        }.Write(w));
                    }
                    else if (moveSig && lookSig)
                    {
                        var snap = target;
                        float fdx = (float)dx, fdy = (float)dy, fdz = (float)dz;
                        viewer.Session.Send(PacketIds.EntityRelMoveLook, w => new EntityRelMoveLookPacket
                        {
                            EntityId = snap.EntityId,
                            Dx       = fdx,
                            Dy       = fdy,
                            Dz       = fdz,
                            Yaw      = snap.LastReportedYaw,
                            Pitch    = snap.LastReportedPitch,
                        }.Write(w));
                    }
                    else if (moveSig)
                    {
                        var snap = target;
                        float fdx = (float)dx, fdy = (float)dy, fdz = (float)dz;
                        viewer.Session.Send(PacketIds.EntityRelMove, w => new EntityRelMovePacket
                        {
                            EntityId = snap.EntityId,
                            Dx       = fdx,
                            Dy       = fdy,
                            Dz       = fdz,
                        }.Write(w));
                    }
                    else if (lookSig)
                    {
                        var snap = target;
                        viewer.Session.Send(PacketIds.EntityLook, w => new EntityLookPacket
                        {
                            EntityId = snap.EntityId,
                            Yaw      = snap.LastReportedYaw,
                            Pitch    = snap.LastReportedPitch,
                        }.Write(w));
                    }
                    // Else: nothing significant changed — silent.
                }
            }

            // Step 3: bake new anchors. Only happens once per tick across
            // all viewer pairs above so each delta packet was relative to
            // a stable per-target anchor. After baking, any teleport this
            // tick also resets the per-target tick counter.
            for (int t = 0; t < _clients.Count; t++)
            {
                var target = _clients[t];
                if (target.Session.IsDead) continue;
                if (!target.AnchorValid) continue;

                bool teleportedThisTick =
                    target.TicksSinceTeleport >= EntityTeleportEveryTicks
                    || Math.Abs(target.LastReportedX - target.AnchorX) > 16
                    || Math.Abs(target.LastReportedY - target.AnchorY) > 16
                    || Math.Abs(target.LastReportedZ - target.AnchorZ) > 16;

                target.AnchorX = target.LastReportedX;
                target.AnchorY = target.LastReportedY;
                target.AnchorZ = target.LastReportedZ;
                target.AnchorYaw = target.LastReportedYaw;
                target.AnchorPitch = target.LastReportedPitch;

                if (teleportedThisTick) target.TicksSinceTeleport = 0;
            }
        }

        // Phase 5b — no-op IPlayerDamageSink + IDropSink. Hostile mobs
        // require these on Update; for now they're inert because
        // server-side player health and item-drop replication aren't
        // yet wired (Phase 5c+ adds EntityHealth packets and a
        // server-side DroppedItem broadcast path). The mob behaves
        // correctly otherwise — wander, chase, melee-distance check —
        // it just can't actually damage anyone yet.
        private sealed class NoopServerSinks : IPlayerDamageSink, IDropSink
        {
            public static readonly NoopServerSinks Instance = new NoopServerSinks();
            public void DamagePlayer(int amount) { /* Phase 5c+ */ }
            public void SpawnDrop(OpenTK.Vector3 pos, BlockType item, int count, OpenTK.Vector3 velocity) { /* Phase 5c+ */ }
            public void SpawnHostile(HostileMob mob)
            {
                // Slimes in Alpha split into smaller copies on death;
                // this would need to be appended to _world.Hostiles.
                // Phase 5b: ignored. Re-evaluate when slimes become
                // actually hittable (currently a no-op damage sink
                // means slimes never die).
            }
        }

        // Find the closest connected player's position for a given mob
        // location, or Vector3.Zero if no player is connected (in which
        // case hostiles wander pacifically). The "closest" choice is
        // important: HostileMob.Update aggros if the player is within
        // DetectRange, so feeding the closest player gives the mob the
        // most realistic target. Fine for our 4-player cap; would need
        // a spatial index at scale.
        private OpenTK.Vector3 ClosestPlayerPosTo(OpenTK.Vector3 mobPos)
        {
            double bestSq = double.MaxValue;
            OpenTK.Vector3 best = OpenTK.Vector3.Zero;
            bool found = false;
            for (int i = 0; i < _clients.Count; i++)
            {
                var c = _clients[i];
                if (c.Session.IsDead) continue;
                if (c.Phase == ClientPhase.AwaitingLogin) continue;
                if (!c.HasReportedPos) continue;
                double dx = c.LastReportedX - mobPos.X;
                double dy = c.LastReportedY - mobPos.Y;
                double dz = c.LastReportedZ - mobPos.Z;
                double sq = dx * dx + dy * dy + dz * dz;
                if (sq < bestSq)
                {
                    bestSq = sq;
                    best = new OpenTK.Vector3((float)c.LastReportedX, (float)c.LastReportedY, (float)c.LastReportedZ);
                    found = true;
                }
            }
            // No player connected: return the spawn-area centre so
            // hostiles still gravity-tick somewhere visible. Sentinel
            // (0,0,0) is intentionally out of the spawn-radius detect
            // range so mobs fall back to wander rather than chasing
            // an empty point.
            return found ? best : OpenTK.Vector3.Zero;
        }

        private void TickHostileMobs()
        {
            var hostiles = _world.Hostiles;

            // Phase B.2 — parallel mob update on the server tick
            // thread. Server uses NoopServerSinks (DamagePlayer is a
            // no-op pending Phase 5c+, SpawnDrop is server-side
            // ignored), so no sink-lock is needed here. mob.Update
            // mutates per-instance state only and reads _world; the
            // serial dead-mob reap below handles broadcast +
            // RemoveAt because both touch shared server state
            // (DespawnMobFromAllViewers iterates _clients, which
            // mutates session send queues, and broadcast ordering
            // matters for entity-id reuse safety).
            int count = hostiles.Count;
            System.Threading.Tasks.Parallel.For(0, count, i =>
            {
                var mob = hostiles[i];
                if (mob.IsDead) return;
                var target = ClosestPlayerPosTo(mob.Position);
                mob.Update(0.05f, _world, target, NoopServerSinks.Instance);
                if (mob is Creeper creeper)
                {
                    creeper.TickFuse(0.05f, target, NoopServerSinks.Instance);
                }
            });

            // Serial reap — DespawnMobFromAllViewers iterates client
            // sessions and adds to per-session writer queues, which
            // are not safe to touch concurrently.
            for (int i = hostiles.Count - 1; i >= 0; i--)
            {
                var mob = hostiles[i];
                if (!mob.IsDead) continue;
                if (mob.NetworkId >= 0)
                {
                    DespawnMobFromAllViewers(mob.NetworkId);
                }
                hostiles.RemoveAt(i);
            }
        }

        // Phase 5 — server-side mob simulation. Drives the wander +
        // gravity + AABB integrator on each passive mob; reaps dead
        // mobs and ships an EntityDespawn for any that had been
        // broadcast. Chicken egg-lay is out of Phase 5a scope (needs
        // IDropSink wired through to server-side drop replication —
        // Phase 5c+ work).
        private void TickPassiveMobs()
        {
            var passives = _world.Passives;
            // Phase B.2 — parallel mob update mirroring TickHostileMobs.
            // PassiveMob.Update mutates per-instance state only and
            // reads _world; the broadcast pass runs after this method
            // returns so there's no concurrent socket-write contention.
            int count = passives.Count;
            System.Threading.Tasks.Parallel.For(0, count, i =>
            {
                var mob = passives[i];
                if (mob.IsDead) return;
                // 0.05 = 1 / 20 Hz tick. Hardcoded here rather than
                // pulling Program.TickSeconds (private to that class)
                // because the server tick rate is a hub-level invariant
                // that doesn't depend on Program's pacing.
                mob.Update(0.05f, _world);
            });

            // Serial reap — DespawnMobFromAllViewers touches per-session
            // writer queues which aren't safe to mutate concurrently.
            for (int i = passives.Count - 1; i >= 0; i--)
            {
                var mob = passives[i];
                if (!mob.IsDead) continue;
                if (mob.NetworkId >= 0)
                {
                    DespawnMobFromAllViewers(mob.NetworkId);
                }
                passives.RemoveAt(i);
            }
        }

        // Bookkeeping for Despawn-on-disconnect / Despawn-on-OOR. Each
        // viewer's TrackedEntities is keyed on the mob's NetworkId
        // (same set we use for player-entity spawns), so removal is a
        // simple Remove + Despawn-packet emit.
        private void DespawnMobFromAllViewers(int networkId)
        {
            for (int v = 0; v < _clients.Count; v++)
            {
                var viewer = _clients[v];
                if (viewer.Session.IsDead) continue;
                if (viewer.Session.IsLoopback) continue; // don't ship to local host
                if (viewer.Phase == ClientPhase.AwaitingLogin) continue;
                if (!viewer.TrackedEntities.Remove(networkId)) continue;
                viewer.Session.Send(PacketIds.EntityDespawn, w => new EntityDespawnPacket
                {
                    EntityId = networkId,
                }.Write(w));
            }
        }

        // Phase 5 — mob equivalent of BroadcastEntityUpdates. Walks the
        // passive list once, assigning a fresh NetworkId to any mob the
        // server hasn't broadcast yet, and emits the cheapest-applicable
        // update packet to each viewer whose tracked-chunks set includes
        // the mob's current chunk. Same anchor-and-delta machinery as
        // the player path; the per-mob anchor lives in the Entity base
        // class fields (AnchorX/Y/Z/Yaw/Pitch/AnchorValid).
        private void BroadcastMobUpdates()
        {
            var passives = _world.Passives;
            for (int i = 0; i < passives.Count; i++)
            {
                var mob = passives[i];
                byte type = MobEntityType(mob);
                if (type == byte.MaxValue) continue; // unknown mob kind; skip

                // Assign a NetworkId on first sight. We use the same
                // _nextEntityId counter as players so the server's id
                // space is unified — a server-assigned id is unique
                // across all connected clients and entities, no
                // collision is possible.
                if (mob.NetworkId < 0) mob.NetworkId = _nextEntityId++;

                int mcx = (int)Math.Floor(mob.Position.X / Chunk.SizeX);
                int mcz = (int)Math.Floor(mob.Position.Z / Chunk.SizeZ);

                if (!mob.AnchorValid)
                {
                    mob.AnchorX = mob.Position.X;
                    mob.AnchorY = mob.Position.Y;
                    mob.AnchorZ = mob.Position.Z;
                    mob.AnchorYaw = MobYaw(mob);
                    mob.AnchorPitch = 0f;
                    mob.AnchorValid = true;
                    mob.TicksSinceTeleport = 0;
                }
                mob.TicksSinceTeleport++;

                // Per-viewer packet selection. Same shape as
                // BroadcastEntityUpdates' inner loop, deduped wherever
                // the mob path differs (e.g. spawn packet carries the
                // mob's display-name slot blank — only players have
                // real names today).
                for (int v = 0; v < _clients.Count; v++)
                {
                    var viewer = _clients[v];
                    if (viewer.Session.IsDead) continue;
                    if (viewer.Session.IsLoopback) continue; // don't ship to local host
                    if (viewer.Phase == ClientPhase.AwaitingLogin) continue;

                    bool inView = viewer.TrackedChunks.Contains((mcx, mcz));
                    bool tracked = viewer.TrackedEntities.Contains(mob.NetworkId);

                    if (!inView)
                    {
                        if (tracked)
                        {
                            viewer.TrackedEntities.Remove(mob.NetworkId);
                            int idCap = mob.NetworkId;
                            viewer.Session.Send(PacketIds.EntityDespawn, w => new EntityDespawnPacket
                            {
                                EntityId = idCap,
                            }.Write(w));
                        }
                        continue;
                    }

                    if (!tracked)
                    {
                        viewer.TrackedEntities.Add(mob.NetworkId);
                        var snap = mob;
                        byte typeCap = type;
                        viewer.Session.Send(PacketIds.EntitySpawn, w => new EntitySpawnPacket
                        {
                            EntityId    = snap.NetworkId,
                            EntityType  = typeCap,
                            X           = snap.Position.X,
                            Y           = snap.Position.Y,
                            Z           = snap.Position.Z,
                            Yaw         = MobYaw(snap),
                            Pitch       = 0f,
                            DisplayName = string.Empty,
                        }.Write(w));
                        continue; // spawn carries the absolute; deltas resume next tick
                    }

                    double dx = mob.Position.X - mob.AnchorX;
                    double dy = mob.Position.Y - mob.AnchorY;
                    double dz = mob.Position.Z - mob.AnchorZ;
                    float curYaw = MobYaw(mob);
                    float dyaw = NormalizeDegrees(curYaw - mob.AnchorYaw);

                    bool moveSig = Math.Abs(dx) > MoveEpsilon || Math.Abs(dy) > MoveEpsilon || Math.Abs(dz) > MoveEpsilon;
                    bool lookSig = Math.Abs(dyaw) > LookEpsilon;
                    bool teleportDue = mob.TicksSinceTeleport >= EntityTeleportEveryTicks;
                    bool teleportFar = Math.Abs(dx) > 16 || Math.Abs(dy) > 16 || Math.Abs(dz) > 16;

                    if (teleportDue || teleportFar)
                    {
                        var snap = mob;
                        viewer.Session.Send(PacketIds.EntityTeleport, w => new EntityTeleportPacket
                        {
                            EntityId = snap.NetworkId,
                            X = snap.Position.X, Y = snap.Position.Y, Z = snap.Position.Z,
                            Yaw = MobYaw(snap), Pitch = 0f,
                        }.Write(w));
                    }
                    else if (moveSig && lookSig)
                    {
                        var snap = mob;
                        float fdx = (float)dx, fdy = (float)dy, fdz = (float)dz;
                        viewer.Session.Send(PacketIds.EntityRelMoveLook, w => new EntityRelMoveLookPacket
                        {
                            EntityId = snap.NetworkId,
                            Dx = fdx, Dy = fdy, Dz = fdz,
                            Yaw = MobYaw(snap), Pitch = 0f,
                        }.Write(w));
                    }
                    else if (moveSig)
                    {
                        var snap = mob;
                        float fdx = (float)dx, fdy = (float)dy, fdz = (float)dz;
                        viewer.Session.Send(PacketIds.EntityRelMove, w => new EntityRelMovePacket
                        {
                            EntityId = snap.NetworkId,
                            Dx = fdx, Dy = fdy, Dz = fdz,
                        }.Write(w));
                    }
                    else if (lookSig)
                    {
                        var snap = mob;
                        viewer.Session.Send(PacketIds.EntityLook, w => new EntityLookPacket
                        {
                            EntityId = snap.NetworkId,
                            Yaw = MobYaw(snap), Pitch = 0f,
                        }.Write(w));
                    }
                }

                // Phase 5e — emit EntityHealth if this mob took damage
                // since last broadcast. Damage means health decreased;
                // gain isn't signalled (Alpha doesn't render heal
                // flashes for other mobs). The first observation
                // primes LastBroadcastHealth without firing.
                if (mob.LastBroadcastHealth == int.MinValue)
                {
                    mob.LastBroadcastHealth = mob.Health;
                }
                else if (mob.Health < mob.LastBroadcastHealth)
                {
                    BroadcastEntityHealth(mob.NetworkId, (short)mob.Health);
                    mob.LastBroadcastHealth = mob.Health;
                }

                // Bake new anchors. Mirrors the third pass of the player
                // path. Reset TicksSinceTeleport when this tick fired a
                // Teleport so the 1-second cadence resumes from this
                // moment rather than from the previous Teleport.
                bool teleportedThisTick =
                    mob.TicksSinceTeleport >= EntityTeleportEveryTicks
                    || Math.Abs(mob.Position.X - mob.AnchorX) > 16
                    || Math.Abs(mob.Position.Y - mob.AnchorY) > 16
                    || Math.Abs(mob.Position.Z - mob.AnchorZ) > 16;

                mob.AnchorX = mob.Position.X;
                mob.AnchorY = mob.Position.Y;
                mob.AnchorZ = mob.Position.Z;
                mob.AnchorYaw = MobYaw(mob);
                mob.AnchorPitch = 0f;
                if (teleportedThisTick) mob.TicksSinceTeleport = 0;
            }
        }

        // Map a concrete PassiveMob subclass to its on-wire entity-type
        // tag. Returns 0xFF for unknown kinds (forward-compat). Calling
        // site filters those out and skips broadcast.
        private static byte MobEntityType(PassiveMob mob)
        {
            if (mob is Pig)     return EntityType.Pig;
            if (mob is Cow)     return EntityType.Cow;
            if (mob is Sheep)   return EntityType.Sheep;
            if (mob is Chicken) return EntityType.Chicken;
            return byte.MaxValue;
        }

        // Map a concrete HostileMob subclass to its on-wire entity-type
        // tag. Mirror of MobEntityType for the hostile bucket (16..19).
        private static byte HostileEntityType(HostileMob mob)
        {
            if (mob is Zombie)   return EntityType.Zombie;
            if (mob is Skeleton) return EntityType.Skeleton;
            if (mob is Spider)   return EntityType.Spider;
            if (mob is Creeper)  return EntityType.Creeper;
            return byte.MaxValue;
        }

        // Phase 5b — hostile-mob broadcast. Mirrors BroadcastMobUpdates
        // exactly, just over _world.Hostiles. The factor-shared helper
        // approach (one method, type-erased entity list) is tempting but
        // PassiveMob and HostileMob don't share a common Yaw / NetworkId
        // accessor signature past Entity, so the dedup'd version would
        // need a dispatch interface that's more code than the duplication.
        // If a third entity-list type lands (drops?), fold then.
        private void BroadcastHostileUpdates()
        {
            var hostiles = _world.Hostiles;
            for (int i = 0; i < hostiles.Count; i++)
            {
                var mob = hostiles[i];
                byte type = HostileEntityType(mob);
                if (type == byte.MaxValue) continue;

                if (mob.NetworkId < 0) mob.NetworkId = _nextEntityId++;

                int mcx = (int)Math.Floor(mob.Position.X / Chunk.SizeX);
                int mcz = (int)Math.Floor(mob.Position.Z / Chunk.SizeZ);

                if (!mob.AnchorValid)
                {
                    mob.AnchorX = mob.Position.X;
                    mob.AnchorY = mob.Position.Y;
                    mob.AnchorZ = mob.Position.Z;
                    mob.AnchorYaw = HostileYaw(mob);
                    mob.AnchorPitch = 0f;
                    mob.AnchorValid = true;
                    mob.TicksSinceTeleport = 0;
                }
                mob.TicksSinceTeleport++;

                for (int v = 0; v < _clients.Count; v++)
                {
                    var viewer = _clients[v];
                    if (viewer.Session.IsDead) continue;
                    if (viewer.Session.IsLoopback) continue; // don't ship to local host
                    if (viewer.Phase == ClientPhase.AwaitingLogin) continue;

                    bool inView = viewer.TrackedChunks.Contains((mcx, mcz));
                    bool tracked = viewer.TrackedEntities.Contains(mob.NetworkId);

                    if (!inView)
                    {
                        if (tracked)
                        {
                            viewer.TrackedEntities.Remove(mob.NetworkId);
                            int idCap = mob.NetworkId;
                            viewer.Session.Send(PacketIds.EntityDespawn, w => new EntityDespawnPacket
                            {
                                EntityId = idCap,
                            }.Write(w));
                        }
                        continue;
                    }

                    if (!tracked)
                    {
                        viewer.TrackedEntities.Add(mob.NetworkId);
                        var snap = mob;
                        byte typeCap = type;
                        viewer.Session.Send(PacketIds.EntitySpawn, w => new EntitySpawnPacket
                        {
                            EntityId    = snap.NetworkId,
                            EntityType  = typeCap,
                            X           = snap.Position.X,
                            Y           = snap.Position.Y,
                            Z           = snap.Position.Z,
                            Yaw         = HostileYaw(snap),
                            Pitch       = 0f,
                            DisplayName = string.Empty,
                        }.Write(w));
                        continue;
                    }

                    double dx = mob.Position.X - mob.AnchorX;
                    double dy = mob.Position.Y - mob.AnchorY;
                    double dz = mob.Position.Z - mob.AnchorZ;
                    float curYaw = HostileYaw(mob);
                    float dyaw = NormalizeDegrees(curYaw - mob.AnchorYaw);

                    bool moveSig = Math.Abs(dx) > MoveEpsilon || Math.Abs(dy) > MoveEpsilon || Math.Abs(dz) > MoveEpsilon;
                    bool lookSig = Math.Abs(dyaw) > LookEpsilon;
                    bool teleportDue = mob.TicksSinceTeleport >= EntityTeleportEveryTicks;
                    bool teleportFar = Math.Abs(dx) > 16 || Math.Abs(dy) > 16 || Math.Abs(dz) > 16;

                    if (teleportDue || teleportFar)
                    {
                        var snap = mob;
                        viewer.Session.Send(PacketIds.EntityTeleport, w => new EntityTeleportPacket
                        {
                            EntityId = snap.NetworkId,
                            X = snap.Position.X, Y = snap.Position.Y, Z = snap.Position.Z,
                            Yaw = HostileYaw(snap), Pitch = 0f,
                        }.Write(w));
                    }
                    else if (moveSig && lookSig)
                    {
                        var snap = mob;
                        float fdx = (float)dx, fdy = (float)dy, fdz = (float)dz;
                        viewer.Session.Send(PacketIds.EntityRelMoveLook, w => new EntityRelMoveLookPacket
                        {
                            EntityId = snap.NetworkId,
                            Dx = fdx, Dy = fdy, Dz = fdz,
                            Yaw = HostileYaw(snap), Pitch = 0f,
                        }.Write(w));
                    }
                    else if (moveSig)
                    {
                        var snap = mob;
                        float fdx = (float)dx, fdy = (float)dy, fdz = (float)dz;
                        viewer.Session.Send(PacketIds.EntityRelMove, w => new EntityRelMovePacket
                        {
                            EntityId = snap.NetworkId,
                            Dx = fdx, Dy = fdy, Dz = fdz,
                        }.Write(w));
                    }
                    else if (lookSig)
                    {
                        var snap = mob;
                        viewer.Session.Send(PacketIds.EntityLook, w => new EntityLookPacket
                        {
                            EntityId = snap.NetworkId,
                            Yaw = HostileYaw(snap), Pitch = 0f,
                        }.Write(w));
                    }
                }

                // Phase 5e — same hurt-flash diff as passive mobs.
                if (mob.LastBroadcastHealth == int.MinValue)
                {
                    mob.LastBroadcastHealth = mob.Health;
                }
                else if (mob.Health < mob.LastBroadcastHealth)
                {
                    BroadcastEntityHealth(mob.NetworkId, (short)mob.Health);
                    mob.LastBroadcastHealth = mob.Health;
                }

                bool teleportedThisTick =
                    mob.TicksSinceTeleport >= EntityTeleportEveryTicks
                    || Math.Abs(mob.Position.X - mob.AnchorX) > 16
                    || Math.Abs(mob.Position.Y - mob.AnchorY) > 16
                    || Math.Abs(mob.Position.Z - mob.AnchorZ) > 16;

                mob.AnchorX = mob.Position.X;
                mob.AnchorY = mob.Position.Y;
                mob.AnchorZ = mob.Position.Z;
                mob.AnchorYaw = HostileYaw(mob);
                mob.AnchorPitch = 0f;
                if (teleportedThisTick) mob.TicksSinceTeleport = 0;
            }
        }

        private static float HostileYaw(HostileMob mob)
        {
            return mob.Yaw * (180f / (float)Math.PI);
        }

        // PassiveMob.Yaw is in radians; the wire format uses degrees.
        // Convert here so the server-internal radians form doesn't leak
        // out and so the client receives a number it can hand straight
        // to its degree-based interpolator.
        private static float MobYaw(PassiveMob mob)
        {
            return mob.Yaw * (180f / (float)Math.PI);
        }

        // Wrap a yaw delta into [-180, 180] so a 359° → 1° change is
        // detected as +2°, not -358°.
        private static float NormalizeDegrees(float d)
        {
            while (d >  180f) d -= 360f;
            while (d < -180f) d += 360f;
            return d;
        }

        private void BroadcastPendingBlockChanges()
        {
            var changes = _world.PendingBlockChanges;
            if (changes.Count == 0) return;

            for (int i = 0; i < changes.Count; i++)
            {
                var rec = changes[i];
                int rcx = rec.X >> 4;
                int rcz = rec.Z >> 4;
                // Phase A.3 — invalidate any prefetched gzip blob for
                // this chunk. The blob captures a snapshot of
                // chunk.RawBlocks taken at prefetch time; if the chunk
                // is edited between prefetch and SendChunk's drain,
                // the snapshot is stale. Removing the cache entry
                // forces SendChunk to fall back to inline gzip on the
                // tick thread, which sees the post-edit bytes. New
                // clients tracking the chunk for the first time get a
                // fresh compression that includes the edit.
                _compressedChunkCache.TryRemove((rcx, rcz), out _);
                for (int c = 0; c < _clients.Count; c++)
                {
                    var client = _clients[c];
                    // LoggingIn clients can already have shipped chunks
                    // and made edits — they need the BlockChange too.
                    // Only AwaitingLogin (pre-handshake) is excluded.
                    if (client.Phase == ClientPhase.AwaitingLogin) continue;
                    if (!client.TrackedChunks.Contains((rcx, rcz))) continue;
                    client.Session.Send(PacketIds.BlockChange, w => new BlockChangePacket
                    {
                        X = rec.X,
                        Y = rec.Y,
                        Z = rec.Z,
                        BlockType = (byte)rec.NewType,
                        Meta = rec.Meta,
                    }.Write(w));
                }
            }

            _world.ClearPendingBlockChanges();
        }

        // KI-5 — guards on-demand chunk generation against double-work
        // when two clients' SendChunk calls overlap on the same (cx, cz)
        // before the first call finishes its TerrainGenerator pass.
        // Today the tick is single-threaded so this can't actually
        // happen, but Open-to-LAN puts hub.Tick on the render thread
        // alongside other chunk consumers (mesh worker, SP TickPassives)
        // and the cost of being defensive is one HashSet lookup. If a
        // race ever surfaces in profiling, this short-circuit kicks in.
        private readonly HashSet<(int, int)> _chunkGenInFlight = new HashSet<(int, int)>();

        // Phase A.3 of the parallelisation analysis — pre-compressed
        // ChunkLoad payloads, populated by Task.Run-spawned background
        // compressors and drained by SendChunk on the tick thread. The
        // initial-window burst (169 chunks at login) used to gzip every
        // chunk inline on the tick thread at ~50–200 µs each — small
        // per chunk, but the 20 Hz tick thread is also the heartbeat
        // for every other client's gameplay updates, so spreading the
        // gzip across the .NET ThreadPool keeps the tick rhythm
        // smoother. Cache key is (cx, cz). ConcurrentDictionary lets
        // SendChunk's TryRemove do the producer-consumer handoff
        // atomically without an explicit lock.
        //
        // Memory: each entry is ~6 KiB compressed; bounded by the
        // initial-window size (169) × concurrent clients. Empty in
        // steady state. SendChunk removes on consume, so no eviction
        // policy needed.
        private readonly ConcurrentDictionary<(int, int), byte[]> _compressedChunkCache
            = new ConcurrentDictionary<(int, int), byte[]>();
        // De-dupes prefetch tasks. Two clients enqueueing the same
        // chunk wouldn't kick off two compressions. Cleared once the
        // task lands its result in _compressedChunkCache.
        private readonly ConcurrentDictionary<(int, int), byte> _compressInFlight
            = new ConcurrentDictionary<(int, int), byte>();

        // Kick off background compression for a chunk if it's already
        // generated and not already cached or in flight. Called right
        // after a chunk gets enqueued onto a client's PendingChunkSends
        // — by the time AdvanceState pops it (potentially many ticks
        // later for chunks deep in the spawn-window queue), the
        // compressed bytes are usually already sitting in the cache.
        //
        // No-op if the chunk hasn't been generated yet (on-demand
        // server gen runs on the tick thread; SendChunk falls back to
        // inline compression for those).
        private void PrefetchChunkCompression(int cx, int cz)
        {
            var key = (cx, cz);
            // Already cached or scheduled — nothing to do.
            if (_compressedChunkCache.ContainsKey(key)) return;
            if (!_compressInFlight.TryAdd(key, 0)) return;

            // Snapshot the chunk reference up-front. If the chunk
            // isn't yet generated we bail and let SendChunk do the
            // (gen + gzip) inline path.
            var chunk = _world.GetChunk(cx, cz);
            if (chunk == null)
            {
                _compressInFlight.TryRemove(key, out _);
                return;
            }

            // Defensive copy of the block bytes — the chunk is shared
            // mutable state (player edits could write into it on the
            // tick thread while the compressor reads). 32 KiB memcpy
            // is ~5 µs, well below the gzip cost.
            var blocks = new byte[chunk.RawBlocks.Length];
            Buffer.BlockCopy(chunk.RawBlocks, 0, blocks, 0, blocks.Length);

            Task.Run(() =>
            {
                try
                {
                    byte[] compressed;
                    using (var ms = new MemoryStream(8192))
                    {
                        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
                        {
                            gz.Write(blocks, 0, blocks.Length);
                        }
                        compressed = ms.ToArray();
                    }
                    _compressedChunkCache[key] = compressed;
                }
                catch
                {
                    // Compression failure is harmless — SendChunk falls
                    // back to inline gzip on cache miss.
                }
                finally
                {
                    _compressInFlight.TryRemove(key, out _);
                }
            });
        }

        private void SendChunk(ServerClient client, int cx, int cz)
        {
            // Make sure the chunk is generated. Server-side world starts
            // with only the InitialRadiusChunks ring; spawn chunks beyond
            // that need on-demand generation. We use the same TerrainGenerator
            // + LightCalculator path the world used at boot so the bytes
            // match what a singleplayer-loaded world would have.
            var chunk = _world.GetChunk(cx, cz);
            if (chunk == null)
            {
                // Already generating elsewhere (Open-to-LAN host's job
                // worker, or another SendChunk pending later in the
                // same tick). Re-queue this client behind it so we
                // don't double-generate. Cheap because PendingChunkSends
                // is per-client and ChunkLoad is idempotent on receive.
                if (_chunkGenInFlight.Contains((cx, cz)))
                {
                    client.PendingChunkSends.Enqueue((cx, cz));
                    return;
                }
                _chunkGenInFlight.Add((cx, cz));
                try
                {
                    chunk = new Chunk(cx, cz);
                    TerrainGenerator.Generate(chunk, _world.Noise);
                    LightCalculator.RecomputeChunk(chunk);
                    _world.InstallGeneratedChunk(chunk);
                }
                finally
                {
                    _chunkGenInFlight.Remove((cx, cz));
                }
            }

            // Gzip the raw block bytes. ~6 KiB per chunk typical, vs 32 KiB
            // raw — worth the compression cost for the bandwidth savings,
            // especially on first-join when 169 chunks ship.
            //
            // Phase A.3 — check the prefetch cache first; if a background
            // task already produced the compressed bytes (the common case
            // for the spawn-ring burst), pop them and skip the inline
            // compression. Fallback path runs gzip on the tick thread for
            // chunks generated on demand or freshly edited since the
            // prefetch ran.
            byte[] compressed;
            if (!_compressedChunkCache.TryRemove((cx, cz), out compressed))
            {
                using (var ms = new MemoryStream(8192))
                {
                    using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
                    {
                        gz.Write(chunk.RawBlocks, 0, chunk.RawBlocks.Length);
                    }
                    compressed = ms.ToArray();
                }
            }

            client.Session.Send(PacketIds.ChunkLoad, w =>
            {
                new ChunkLoadPacket
                {
                    ChunkX = cx,
                    ChunkZ = cz,
                    CompressedBlocks = compressed,
                }.Write(w);
            });

            // Track that this client now has the chunk so future
            // BlockChange records inside it actually ship and so
            // SlideChunkWindow knows to ChunkUnload it on scroll-off.
            client.TrackedChunks.Add((cx, cz));
        }

        // Accept loop runs on its own thread. AcceptTcpClient blocks
        // until either a new client arrives or _listener.Stop() makes
        // it throw — that's how we exit cleanly during Stop().
        private void AcceptLoop()
        {
            while (!_stopping)
            {
                TcpClient tcp;
                try
                {
                    tcp = _listener.AcceptTcpClient();
                }
                catch (SocketException) when (_stopping)
                {
                    return; // expected: listener stopped
                }
                catch (ObjectDisposedException)
                {
                    return; // expected: listener stopped
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[server] accept failed: {ex.Message}");
                    Thread.Sleep(50); // avoid tight loop on a transient kernel error
                    continue;
                }

                var session = new NetSession(tcp);
                session.Start();
                lock (_pendingLock)
                {
                    _pendingNew.Add(session);
                }
            }
        }
    }

    internal enum ClientPhase
    {
        AwaitingLogin, // socket accepted, no LoginRequest yet
        LoggingIn,     // LoginResponse sent, initial chunk burst still in flight
        InGame,        // chunks done streaming; client sending PlayerPosLook
    }

    // Phase 6c — per-client open-window state. The Cell* coords let
    // close-time commit go to the right tile entity (chests / furnaces
    // back to World; crafting table input goes to the player or the
    // world floor). Slots is the BACKING storage:
    //   - Chest: aliases ChestTileEntity.Slots so writes land on the
    //     persisted entity; close is a no-op (state was always live).
    //   - Furnace: aliases FurnaceTileEntity slots [Input, Fuel, Output];
    //     close is a no-op.
    //   - CraftingTable: a fresh ItemStack[10] (9 input + 1 output)
    //     created at open time; close commits leftovers back to the
    //     player or drops them at the player's feet.
    internal sealed class OpenWindowState
    {
        public byte WindowId;
        public byte Kind;
        public int CellX, CellY, CellZ;
        public ItemStack[] Slots;        // window-local backing array
        public ChestTileEntity ChestRef; // null unless Kind == Chest
        public FurnaceTileEntity FurnaceRef; // null unless Kind == Furnace

        // Phase 6c polish — last-broadcast furnace state. Hub tick
        // compares these against the live FurnaceRef each iteration
        // and ships a fresh TileEntityData when anything changed,
        // so the friend's furnace UI sees the cook timer ticking
        // and the burn-fuel sprite count down.
        public int LastSentBurnTime;
        public int LastSentMaxBurnTime;
        public int LastSentCookProgress;
        public bool LastSentInitialized;
    }

    // Per-connection server state. Holds NetSession + the bookkeeping the
    // hub needs (entity id, username, login phase, pending chunk queue,
    // last reported pos for telemetry). Owned exclusively by the tick
    // thread once the client is promoted out of _pendingNew.
    internal sealed class ServerClient
    {
        public NetSession Session { get; }
        public int EntityId;
        public string Username;
        public string Label => Username ?? Session.RemoteEndpoint;
        public ClientPhase Phase = ClientPhase.AwaitingLogin;

        public int SpawnX, SpawnY, SpawnZ;
        public Queue<(int cx, int cz)> PendingChunkSends = new Queue<(int, int)>();

        // Phase 3 — what chunks does this client currently believe it
        // has loaded? Populated as SendChunk ships each one, drained as
        // SlideChunkWindow ships ChunkUnload. Used by
        // BroadcastPendingBlockChanges to filter "is this edit in their
        // view?" and by SlideChunkWindow itself to compute the diff.
        public HashSet<(int cx, int cz)> TrackedChunks = new HashSet<(int, int)>();

        // Phase 4 — what other-player EntityIds is this client currently
        // tracking? Populated by EntitySpawn, drained by EntityDespawn.
        // Per-tick entity broadcast iterates every (a, b) pair of
        // currently-connected clients and uses this set to decide whether
        // (a) needs an EntitySpawn for (b), or already-tracked (b) needs
        // an EntityRelMove / RelMoveLook / Teleport.
        public HashSet<int> TrackedEntities = new HashSet<int>();

        // Phase 4 — last position+look this client was told about for
        // ITS OWN entity (i.e. the values we last broadcast to OTHER
        // clients about us). Compared each tick against the new
        // LastReportedX/Y/Z to decide whether to emit a delta packet
        // and what kind. Distinct from LastReportedX/Y/Z which is the
        // raw client-side claim — we accept that into LastReported
        // immediately on receive but only ANCHOR (and broadcast) once
        // per tick.
        public double AnchorX, AnchorY, AnchorZ;
        public float AnchorYaw, AnchorPitch;
        public bool AnchorValid;
        // Tick counter since last full EntityTeleport. Once it crosses
        // EntityTeleportEveryTicks we send a fresh Teleport regardless
        // of delta size to defeat any accumulated rounding.
        public int TicksSinceTeleport;

        // Last chunk coords we computed a window around. Compared each
        // tick against the player's current chunk; differing values
        // trigger SlideChunkWindow. Initialised to a sentinel that
        // can't match a real spawn so the first PlayerPosLook always
        // forces a recompute (useful if the client teleports before
        // login completes, etc).
        public int WindowCx = int.MinValue;
        public int WindowCz = int.MinValue;

        public DateTime LastKeepAliveSent = DateTime.UtcNow;
        public DateTime LastKeepAliveFromPeer = DateTime.UtcNow;

        // Phase 6b — friend's currently-selected hotbar slot (0..8).
        // Synced from PlayerHeldSlot packets; resolved by PlayerDropItem
        // (drop the held slot's stack) and future PlayerUseItem (use
        // the held item — snowball, bucket, etc.). Defaults to 0 so a
        // freshly-connected friend who hasn't sent a slot packet yet
        // still has a sensible "selected slot" the server can reason
        // about.
        public byte HeldSlot;

        // Phase 6c — currently-open windows on this client (chest /
        // furnace / crafting table). Keyed on the per-client windowId
        // so a quick close+reopen gets a fresh slot. Multiple windows
        // open simultaneously isn't really supported by the friend's
        // UI but the server tolerates it (each click carries its
        // windowId, so the server can disambiguate).
        public Dictionary<byte, OpenWindowState> OpenWindows = new Dictionary<byte, OpenWindowState>();
        public byte NextWindowId = 1;

        // Phase 6a — server-authoritative inventory for this client. The
        // friend's local Player.Inventory mirrors this via InventoryUpdate
        // packets. The host's loopback ServerClient also has one but it's
        // unused (the host's SP path already mutates Player.Inventory
        // directly; the loopback session's Send is a no-op so the dual
        // path doesn't show up on the wire). Initialised empty;
        // population happens via drop pickup, future crafting, and
        // future chest withdrawal.
        public Inventory ServerInventory = new Inventory();

        public bool HasReportedPos;
        public double LastReportedX, LastReportedY, LastReportedZ;
        public float LastReportedYaw, LastReportedPitch;

        public ServerClient(NetSession session)
        {
            Session = session;
        }
    }
}
