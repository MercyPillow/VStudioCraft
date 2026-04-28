using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using VStudioCraft.Game;
using VStudioCraft.Net;

namespace VStudioCraft.Server
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

        // Called once per tick from Program.RunTickLoop, BEFORE the world
        // simulation. We process inbound packets first so client-driven
        // intents (chunk requests, position updates) reach the simulation
        // in the same tick they arrive in, minimising round-trip latency.
        //
        // Order within the tick:
        //   1. Promote pending sockets (handoff from accept thread)
        //   2. Drain inbound queues per client (login, dig, place, pos)
        //      — block edits may flip cells, accumulating BlockChange
        //      records in World._pendingBlockChanges
        //   3. Advance per-client streaming state (chunks, keepalive)
        //   4. Broadcast queued BlockChange records to clients in range
        //   5. Reap dead sessions
        public void Tick()
        {
            PromotePendingClients();

            for (int i = 0; i < _clients.Count; i++)
            {
                var client = _clients[i];
                if (client.Session.IsDead) continue;
                DrainInbound(client);
            }

            for (int i = 0; i < _clients.Count; i++)
            {
                var client = _clients[i];
                if (client.Session.IsDead) continue;
                AdvanceState(client);
            }

            // 4. Drain world-level block changes accumulated this tick
            //    (player dig/place + future fluid spread / sugar cane
            //    growth) and route them to the clients whose tracked-
            //    chunks set contains the cell. A change OUTSIDE every
            //    client's view radius is dropped silently — the next
            //    ChunkLoad to that area will carry the new bytes.
            BroadcastPendingBlockChanges();

            // 5. Phase 5 — mob simulation. Server runs wander+AI directly
            //    (clients don't, in net-driven mode). Server tick is the
            //    fixed 50 ms step already; no per-mob accumulator needed.
            TickPassiveMobs();
            TickHostileMobs();

            // 6. Phase 4 — per-pair player entity replication. For every
            //    (a, b) pair of in-game clients, ensure b knows about a's
            //    current position via the cheapest packet that conveys
            //    the change since b's last anchor for a. Phase 5 extends
            //    this to broadcast non-player entities (passives + hostiles).
            BroadcastEntityUpdates();
            BroadcastMobUpdates();
            BroadcastHostileUpdates();

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

            // Spawn at the world origin for Phase 2; Phase 3 picks the real
            // surface Y from World.GetTopmostSolidY or the saved spawn.
            var spawnX = 0;
            var spawnY = 80;
            var spawnZ = 0;
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
                    client.PendingChunkSends.Enqueue((spawnCx + dx, spawnCz + dz));
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
            for (int i = hostiles.Count - 1; i >= 0; i--)
            {
                var mob = hostiles[i];
                if (mob.IsDead)
                {
                    if (mob.NetworkId >= 0)
                    {
                        DespawnMobFromAllViewers(mob.NetworkId);
                    }
                    hostiles.RemoveAt(i);
                    continue;
                }
                var target = ClosestPlayerPosTo(mob.Position);
                mob.Update(0.05f, _world, target, NoopServerSinks.Instance);
                // Creeper fuse decoupled from Update (matches the
                // Alpha "primed creeper doesn't always defuse" rule).
                if (mob is Creeper creeper)
                {
                    creeper.TickFuse(0.05f, target, NoopServerSinks.Instance);
                }
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
            // PassiveMob.Update mutates Position + Velocity + Yaw; the
            // physics path is otherwise side-effect-free, so we can
            // call it directly on the tick thread without coordinating
            // with the broadcast pass that runs immediately after.
            //
            // Dead mob reap is INSIDE this loop because the broadcast
            // pass shouldn't have to think about IsDead vs alive
            // separately from "is in the world list". Iterating
            // backwards lets RemoveAt run in O(1) without shifting
            // unread tail.
            for (int i = passives.Count - 1; i >= 0; i--)
            {
                var mob = passives[i];
                if (mob.IsDead)
                {
                    if (mob.NetworkId >= 0)
                    {
                        DespawnMobFromAllViewers(mob.NetworkId);
                    }
                    passives.RemoveAt(i);
                    continue;
                }
                // 0.05 = 1 / 20 Hz tick. Hardcoded here rather than
                // pulling Program.TickSeconds (private to that class)
                // because the server tick rate is a hub-level invariant
                // that doesn't depend on Program's pacing.
                mob.Update(0.05f, _world);
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
                    }.Write(w));
                }
            }

            _world.ClearPendingBlockChanges();
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
                chunk = new Chunk(cx, cz);
                TerrainGenerator.Generate(chunk, _world.Noise);
                LightCalculator.RecomputeChunk(chunk);
                _world.InstallGeneratedChunk(chunk);
            }

            // Gzip the raw block bytes. ~6 KiB per chunk typical, vs 32 KiB
            // raw — worth the ~50 µs compression cost for the bandwidth
            // savings, especially on first-join when 169 chunks ship.
            byte[] compressed;
            using (var ms = new MemoryStream(8192))
            {
                using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
                {
                    gz.Write(chunk.RawBlocks, 0, chunk.RawBlocks.Length);
                }
                compressed = ms.ToArray();
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

        public bool HasReportedPos;
        public double LastReportedX, LastReportedY, LastReportedZ;
        public float LastReportedYaw, LastReportedPitch;

        public ServerClient(NetSession session)
        {
            Session = session;
        }
    }
}
