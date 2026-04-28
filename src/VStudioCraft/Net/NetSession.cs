using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Threading;

namespace VStudioCraft.Net
{
    // One TCP connection's worth of state, used identically on both sides
    // of the wire (server uses one per accepted client; client uses one
    // for its single outbound socket — Phase 2c).
    //
    // Threading model:
    //   - A dedicated background thread runs ReadLoop, blocking on the
    //     network stream and parsing complete packets into the inbound
    //     queue. The tick thread (server) or render thread (client) drains
    //     the queue once per tick / frame and never blocks on the socket.
    //   - All sends go through Send(byte id, Action<PacketWriter> body).
    //     The body is serialised into a MemoryStream first, then the
    //     finished bytes are written to the network stream under a lock.
    //     This guarantees no two threads can interleave packet bytes on
    //     the wire — a concern once Phase 4 starts broadcasting entity
    //     packets from inside the tick while the keepalive thread also
    //     wants to send.
    //   - The session is one-shot: once Disconnected() fires, the read
    //     thread exits and Send becomes a no-op. The owner reaps it on
    //     the next tick by calling IsDead.
    //
    // Heartbeat / keepalive is the OWNER's responsibility — NetSession
    // doesn't own a timer. The server's tick loop sends KeepAlive every
    // ~10 s; the client mirrors. We rely on TCP RST / read-timeout to
    // surface dead sockets, with the keepalive providing application-
    // level liveness when TCP itself doesn't notice (NAT idling, etc).
    internal sealed class NetSession
    {
        // Inbound queue capped so a runaway sender can't OOM the host.
        // 1024 packets at ~100 B average = ~100 KiB; well below any
        // reasonable backpressure threshold and large enough that a
        // single-tick burst (one client's worth of player input + a
        // chunk request) never trips the cap.
        private const int MaxQueuedInbound = 1024;

        private readonly TcpClient _tcp;
        private readonly NetworkStream _stream;
        private readonly PacketReader _reader;
        private readonly PacketWriter _writer;
        private readonly object _writeLock = new object();
        private readonly ConcurrentQueue<InboundPacket> _inbound = new ConcurrentQueue<InboundPacket>();
        private Thread _readThread;
        private volatile bool _dead;
        private string _deadReason;

        // Optional human label for logging. Filled in once login succeeds
        // (server-side) or with the connect target (client-side).
        public string Label { get; set; }

        public bool IsDead => _dead;
        public string DeadReason => _deadReason;
        public string RemoteEndpoint { get; }

        // Phase 7 / Open-to-LAN — a loopback session is a stand-in for
        // the host themselves on an open-to-LAN server. The host has no
        // socket; their player position is updated each frame via
        // ServerHub.UpdateLocalHostPose. Send becomes a no-op (we don't
        // ship packets to ourselves) but the session is otherwise
        // a regular ServerClient.Session — broadcast loops can include
        // the host as a target so OTHER players see the host's entity
        // updates flow through the same EntitySpawn / RelMove pipeline.
        // Viewer-side loops in ServerHub explicitly skip IsLoopback so
        // we don't waste cycles formatting packets we'll throw away.
        public bool IsLoopback { get; }

        public NetSession(TcpClient tcp)
        {
            _tcp = tcp ?? throw new ArgumentNullException(nameof(tcp));
            // Disable Nagle for game traffic — we want the 50-ms-tick
            // packet bundle to hit the wire immediately, not wait up to
            // 200 ms for ACK coalescing. Slightly higher byte overhead in
            // exchange for reliable inter-tick latency.
            _tcp.NoDelay = true;
            _stream = _tcp.GetStream();
            _reader = new PacketReader(_stream);
            _writer = new PacketWriter(_stream);
            try { RemoteEndpoint = _tcp.Client.RemoteEndPoint?.ToString() ?? "<unknown>"; }
            catch { RemoteEndpoint = "<unknown>"; }
        }

        // Loopback constructor — for the open-to-LAN host's phantom
        // ServerClient. _tcp / _stream / _reader / _writer all stay null;
        // Send and the read loop short-circuit on IsLoopback before
        // touching them.
        private NetSession(string label)
        {
            IsLoopback = true;
            RemoteEndpoint = "<loopback>";
            Label = label;
        }

        public static NetSession CreateLoopback(string label = "host")
            => new NetSession(label);

        public void Start()
        {
            // Loopback sessions have no socket and no read thread —
            // packets are pushed in directly by the host's RenderLoop
            // via ServerHub helpers, not parsed off a stream.
            if (IsLoopback) return;

            _readThread = new Thread(ReadLoop)
            {
                IsBackground = true,
                Name = $"NetSession-Read-{RemoteEndpoint}",
            };
            _readThread.Start();
        }

        public bool TryDequeueInbound(out InboundPacket packet)
            => _inbound.TryDequeue(out packet);

        // Atomic outbound send. The body delegate writes packet-specific
        // payload to the temporary writer; we then dump the resulting
        // bytes (ID byte + payload) to the network stream in one Write
        // call under the lock so the wire never sees a half-packet.
        //
        // No-ops if the session is already dead — handy for "broadcast
        // to everyone" loops that don't want to special-case disconnects.
        public void Send(byte packetId, Action<PacketWriter> body)
        {
            if (_dead) return;
            // Loopback host: drop the send. We don't ship packets to
            // ourselves; the host's local sim already has the state
            // these packets would convey. ServerHub's viewer-side
            // broadcast loops also short-circuit on IsLoopback so this
            // is a defensive guard rather than the primary path.
            if (IsLoopback) return;
            byte[] frame;
            using (var ms = new MemoryStream(64))
            {
                ms.WriteByte(packetId);
                if (body != null)
                {
                    var bodyWriter = new PacketWriter(ms);
                    body(bodyWriter);
                }
                frame = ms.ToArray();
            }

            try
            {
                lock (_writeLock)
                {
                    if (_dead) return;
                    _stream.Write(frame, 0, frame.Length);
                }
            }
            catch (Exception ex)
            {
                MarkDead($"send failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // Convenience for parameterless packets (KeepAlive).
        public void Send(byte packetId) => Send(packetId, null);

        // Closes the socket from the owning side. Best-effort: errors
        // during close are swallowed because we're already shutting down.
        public void Disconnect(string reason)
        {
            MarkDead(reason);
            // Loopback session has no socket to close; null-guard so
            // CloseLan doesn't NRE.
            if (_tcp != null)
            {
                try { _tcp.Close(); } catch { /* ignored */ }
            }
        }

        private void MarkDead(string reason)
        {
            if (_dead) return;
            _dead = true;
            _deadReason = reason;
        }

        // Read loop runs forever on its dedicated thread. Each iteration
        // reads one full packet (ID + payload) and enqueues the typed
        // result. Any read error transitions the session to dead and
        // exits the loop; the owner notices on the next tick.
        private void ReadLoop()
        {
            try
            {
                while (!_dead)
                {
                    byte id = _reader.ReadByte();
                    var packet = ParseInbound(id);

                    // Backpressure guard. If the consumer never drains we
                    // refuse to grow forever; instead we tear the session
                    // down so the caller's next tick sees IsDead and
                    // reaps it. This is a hostile-client / starved-tick
                    // safety net, not a normal-flow control.
                    if (_inbound.Count >= MaxQueuedInbound)
                    {
                        MarkDead($"inbound queue overflow ({MaxQueuedInbound})");
                        return;
                    }
                    _inbound.Enqueue(packet);
                }
            }
            catch (EndOfStreamException)
            {
                MarkDead("peer closed connection");
            }
            catch (IOException ex)
            {
                MarkDead($"io: {ex.Message}");
            }
            catch (Exception ex)
            {
                MarkDead($"protocol: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // Read the type-specific payload for a known packet ID. Throws
        // InvalidDataException on unknown IDs so a hostile or out-of-sync
        // client triggers an immediate disconnect rather than reading
        // garbage as the next packet.
        private InboundPacket ParseInbound(byte id)
        {
            switch (id)
            {
                case PacketIds.KeepAlive:
                    return new InboundPacket { Id = id };

                case PacketIds.LoginRequest:
                    return new InboundPacket { Id = id, Login = LoginRequestPacket.Read(_reader) };

                case PacketIds.LoginResponse:
                    return new InboundPacket { Id = id, LoginResponse = LoginResponsePacket.Read(_reader) };

                case PacketIds.Disconnect:
                    return new InboundPacket { Id = id, Disconnect = DisconnectPacket.Read(_reader) };

                case PacketIds.PlayerPosLook:
                    return new InboundPacket { Id = id, PlayerPosLook = PlayerPosLookPacket.Read(_reader) };

                case PacketIds.ChunkLoad:
                    return new InboundPacket { Id = id, ChunkLoad = ChunkLoadPacket.Read(_reader) };

                case PacketIds.ChunkUnload:
                    return new InboundPacket { Id = id, ChunkUnload = ChunkUnloadPacket.Read(_reader) };

                case PacketIds.BlockChange:
                    return new InboundPacket { Id = id, BlockChange = BlockChangePacket.Read(_reader) };

                case PacketIds.PlayerDigStart:
                    return new InboundPacket { Id = id, PlayerDig = PlayerDigPacket.Read(_reader) };

                case PacketIds.PlayerPlace:
                    return new InboundPacket { Id = id, PlayerPlace = PlayerPlacePacket.Read(_reader) };

                case PacketIds.EntitySpawn:
                    return new InboundPacket { Id = id, EntitySpawn = EntitySpawnPacket.Read(_reader) };

                case PacketIds.EntityRelMove:
                    return new InboundPacket { Id = id, EntityRelMove = EntityRelMovePacket.Read(_reader) };

                case PacketIds.EntityLook:
                    return new InboundPacket { Id = id, EntityLook = EntityLookPacket.Read(_reader) };

                case PacketIds.EntityRelMoveLook:
                    return new InboundPacket { Id = id, EntityRelMoveLook = EntityRelMoveLookPacket.Read(_reader) };

                case PacketIds.EntityTeleport:
                    return new InboundPacket { Id = id, EntityTeleport = EntityTeleportPacket.Read(_reader) };

                case PacketIds.EntityDespawn:
                    return new InboundPacket { Id = id, EntityDespawn = EntityDespawnPacket.Read(_reader) };

                default:
                    throw new InvalidDataException($"Unknown packet id 0x{id:X2}");
            }
        }
    }

    // Tagged union over the Phase-2 inbound packet types. Only one of the
    // payload fields is populated per instance (matching Id). Using a
    // struct keeps the queue allocation-free on the hot path; the unused
    // fields cost a few hundred bytes of struct size, which is fine for
    // the queue depth we cap at.
    internal struct InboundPacket
    {
        public byte Id;
        public LoginRequestPacket    Login;
        public LoginResponsePacket   LoginResponse;
        public DisconnectPacket      Disconnect;
        public PlayerPosLookPacket   PlayerPosLook;
        public ChunkLoadPacket       ChunkLoad;
        public ChunkUnloadPacket     ChunkUnload;
        public BlockChangePacket     BlockChange;
        public PlayerDigPacket       PlayerDig;
        public PlayerPlacePacket     PlayerPlace;
        public EntitySpawnPacket     EntitySpawn;
        public EntityRelMovePacket   EntityRelMove;
        public EntityLookPacket      EntityLook;
        public EntityRelMoveLookPacket EntityRelMoveLook;
        public EntityTeleportPacket  EntityTeleport;
        public EntityDespawnPacket   EntityDespawn;
    }
}
