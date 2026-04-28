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
        public void Tick()
        {
            // 1. Promote any newly-accepted sessions into the live list.
            //    The accept thread parks them in _pendingNew; we move them
            //    over here on the tick thread so all _clients mutations
            //    stay single-threaded.
            PromotePendingClients();

            // 2. Drain inbound queues and advance per-client state.
            for (int i = 0; i < _clients.Count; i++)
            {
                var client = _clients[i];
                if (client.Session.IsDead) continue;
                DrainInbound(client);
                AdvanceState(client);
            }

            // 3. Reap dead sessions. Iterating backwards lets us RemoveAt
            //    without shifting the unread tail.
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
                        // Phase 2 just records the latest reported pos so
                        // the heartbeat log can show "player at (x, y, z)".
                        // Phase 3+ feeds this into a server-side Player
                        // entity for chunk-window streaming and physics
                        // validation.
                        if (client.Phase == ClientPhase.InGame)
                        {
                            client.LastReportedX = pkt.PlayerPosLook.X;
                            client.LastReportedY = pkt.PlayerPosLook.Y;
                            client.LastReportedZ = pkt.PlayerPosLook.Z;
                            client.LastReportedYaw = pkt.PlayerPosLook.Yaw;
                            client.LastReportedPitch = pkt.PlayerPosLook.Pitch;
                            client.HasReportedPos = true;
                        }
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
            // Send pending chunks (ChunksPerTick max) once login completes.
            // First send transitions LoggingIn -> InGame so subsequent
            // PlayerPosLook packets count.
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
