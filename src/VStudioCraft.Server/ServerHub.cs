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
            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();
            _acceptThread = new Thread(AcceptLoop)
            {
                IsBackground = true,
                Name = "NetServer-Accept",
            };
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

            for (int i = _clients.Count - 1; i >= 0; i--)
            {
                if (_clients[i].Session.IsDead)
                {
                    Console.WriteLine($"[server] {_clients[i].Label} disconnected: {_clients[i].Session.DeadReason}");
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
