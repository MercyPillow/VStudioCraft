using System;
using System.Net.Sockets;
using System.Threading;

namespace VStudioCraft.Net
{
    // Client-side network handle. Wraps a NetSession and adds the
    // login-handshake helper that GameRenderer.ConnectToServer drives:
    // synchronous Connect + handshake (so the caller can early-fail with
    // a friendly message), then transitions to async streaming where
    // the render loop drains inbound packets once per frame.
    //
    // This is intentionally NOT a peer to NetSession + ServerHub on the
    // client side. NetSession already does the read/write threading; the
    // client only needs:
    //   - Connect / handshake glue that returns the LoginResponse
    //   - Per-frame TryDequeueInbound for the renderer to drain
    //   - Convenience Send wrappers for the handful of outbound packets
    //     the client cares about (PlayerPosLook, KeepAlive, Disconnect)
    //
    // Lifetimes: created by GameRenderer.ConnectToServer on the render
    // thread, owned by GameRenderer until either the user disconnects or
    // the underlying session dies. On Dispose / disconnect the underlying
    // socket is closed which terminates the read thread; we don't bother
    // joining it (background thread, host process is the owner of last
    // resort).
    internal sealed class NetClient : IDisposable
    {
        private readonly TcpClient _tcp;
        private readonly NetSession _session;
        private LoginResponsePacket _loginResponse;
        private bool _loggedIn;
        private DateTime _lastKeepAliveSent = DateTime.UtcNow;

        // Application keepalive interval, mirrors ServerHub. Below the
        // ~30 s NAT idle cutoff, well above the per-frame cadence — one
        // packet every ~10 s is essentially free bandwidth.
        private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(10);

        public NetSession Session => _session;
        public LoginResponsePacket LoginResponse => _loginResponse;
        public bool IsConnected => !_session.IsDead;
        public string DisconnectReason => _session.DeadReason;

        private NetClient(TcpClient tcp, NetSession session)
        {
            _tcp = tcp;
            _session = session;
        }

        // Synchronous connect-and-handshake. Returns a ready-to-use
        // NetClient with .LoginResponse populated, or throws on failure
        // (connection refused, protocol mismatch, server-side reject).
        // Phase 7 will wire a non-blocking variant for the integrated
        // server path; today's blocking call is fine because the
        // standalone Connect button can show a "Connecting…" spinner
        // and tolerate a few hundred ms.
        public static NetClient Connect(string host, int port, string username, TimeSpan? timeout = null)
        {
            if (string.IsNullOrEmpty(host)) throw new ArgumentException("host required", nameof(host));
            if (string.IsNullOrEmpty(username)) throw new ArgumentException("username required", nameof(username));

            var tcp = new TcpClient();
            try
            {
                // Async connect with timeout. TcpClient.Connect's default
                // timeout is OS-dependent (often 20+ seconds), which is
                // far too long for a live UI. 5 s is enough to cover
                // realistic wide-area connect time without leaving the
                // user staring at a frozen menu.
                var to = timeout ?? TimeSpan.FromSeconds(5);
                var ar = tcp.BeginConnect(host, port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(to, exitContext: false))
                {
                    try { tcp.Close(); } catch { /* ignored */ }
                    throw new TimeoutException($"timed out connecting to {host}:{port}");
                }
                tcp.EndConnect(ar);
            }
            catch
            {
                try { tcp.Close(); } catch { /* ignored */ }
                throw;
            }

            var session = new NetSession(tcp) { Label = $"{host}:{port}" };
            session.Start();
            var client = new NetClient(tcp, session);
            client.PerformHandshake(username);
            return client;
        }

        private void PerformHandshake(string username)
        {
            // Send LoginRequest. The server replies with LoginResponse OR
            // Disconnect. We block here on the read thread's queue with a
            // short deadline because the rest of the client code assumes
            // .LoginResponse is populated by the time Connect returns.
            _session.Send(PacketIds.LoginRequest, w => new LoginRequestPacket
            {
                ProtocolVersion = PacketIds.ProtocolVersion,
                Username        = username,
            }.Write(w));

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                if (_session.IsDead)
                    throw new InvalidOperationException($"server hung up during handshake: {_session.DeadReason}");

                if (_session.TryDequeueInbound(out var pkt))
                {
                    switch (pkt.Id)
                    {
                        case PacketIds.LoginResponse:
                            _loginResponse = pkt.LoginResponse;
                            _loggedIn = true;
                            return;
                        case PacketIds.Disconnect:
                            throw new InvalidOperationException($"server rejected login: {pkt.Disconnect.Reason}");
                        case PacketIds.KeepAlive:
                            // Some servers send keepalives during the
                            // login window; absorb and keep waiting.
                            continue;
                        default:
                            throw new InvalidOperationException($"unexpected packet 0x{pkt.Id:X2} during handshake (expected LoginResponse)");
                    }
                }
                Thread.Sleep(5); // tiny yield; the read thread fills the queue
            }

            // Tear the session down before throwing — otherwise the
            // half-open socket leaks until GC.
            _session.Disconnect("handshake timeout");
            throw new TimeoutException("server didn't reply with LoginResponse within 5 s");
        }

        // Per-frame drain. The renderer calls this each frame; each call
        // pops one packet (returns false when queue is empty) so the
        // caller can interleave with rendering / input on the same thread
        // without ever blocking. Packets are returned by value so the
        // queue's struct semantics keep this allocation-free.
        public bool TryDequeue(out InboundPacket packet) => _session.TryDequeueInbound(out packet);

        // Periodic outbound. Called every render-frame by GameRenderer;
        // sends only when the gap exceeds KeepAliveInterval so the wire
        // doesn't fill with spam. Mirrors ServerHub's own keepalive loop.
        public void MaybeSendKeepAlive()
        {
            if (!_loggedIn) return;
            if (DateTime.UtcNow - _lastKeepAliveSent < KeepAliveInterval) return;
            _session.Send(PacketIds.KeepAlive);
            _lastKeepAliveSent = DateTime.UtcNow;
        }

        // Send the local player's position+look upstream. GameRenderer
        // throttles this to ~20 Hz (matching the server tick) — sending
        // every render frame at 1500 fps would drown the wire and the
        // server's input queue.
        public void SendPosLook(double x, double y, double z, float yaw, float pitch, bool onGround)
        {
            if (!_loggedIn) return;
            _session.Send(PacketIds.PlayerPosLook, w => new PlayerPosLookPacket
            {
                X = x, Y = y, Z = z,
                Yaw = yaw, Pitch = pitch,
                OnGround = onGround,
            }.Write(w));
        }

        // Phase 3 — outbound dig intent. status=0 (start) is the only
        // value Phase 3 emits today (creative-mode instant break). The
        // server treats status=0 and status=2 (finish) identically for
        // now; status=1 (cancel) lands when survival break-progress
        // ships in Phase 5.
        public void SendDig(byte status, int x, int y, int z, byte face)
        {
            if (!_loggedIn) return;
            _session.Send(PacketIds.PlayerDigStart, w => new PlayerDigPacket
            {
                Status = status, X = x, Y = y, Z = z, Face = face,
            }.Write(w));
        }

        // Phase 3 — outbound place intent. (x,y,z) is the cell the player
        // clicked ON; the server resolves the actual placement target
        // using the face normal. blockType is the type the client
        // believes is in their hand (server cross-checks in Phase 6).
        public void SendPlace(int x, int y, int z, byte face, byte blockType)
        {
            if (!_loggedIn) return;
            _session.Send(PacketIds.PlayerPlace, w => new PlayerPlacePacket
            {
                X = x, Y = y, Z = z, Face = face, BlockType = blockType,
            }.Write(w));
        }

        // Phase 6b — friend changes hotbar slot via 1..9 keys or scroll
        // wheel. Throttling not needed (single-byte body, infrequent);
        // the local UI already debounces wheel events.
        public void SendHeldSlot(byte slot)
        {
            if (!_loggedIn) return;
            _session.Send(PacketIds.PlayerHeldSlot, w => new PlayerHeldSlotPacket
            {
                Slot = slot,
            }.Write(w));
        }

        // Phase 6b — friend Q-drop intent. mode = 0 single, 1 stack
        // (Shift+Q on the host). Server resolves which slot from the
        // most-recent PlayerHeldSlot it received.
        public void SendDropItem(byte mode)
        {
            if (!_loggedIn) return;
            _session.Send(PacketIds.PlayerDropItem, w => new PlayerDropItemPacket
            {
                Mode = mode,
            }.Write(w));
        }

        // Phase 6b-extended — friend clicked an inventory slot. Slot
        // 0xFF is the outside-click sentinel that drops the cursor
        // stack. Server will reply with InventoryUpdate burst (49
        // slots + cursor).
        public void SendInventoryClick(byte slot, byte button, bool shift)
        {
            if (!_loggedIn) return;
            _session.Send(PacketIds.InventoryClick, w => new InventoryClickPacket
            {
                Slot = slot,
                Button = button,
                Shift = (byte)(shift ? 1 : 0),
            }.Write(w));
        }

        // Phase 6c — friend RMB on a tile-entity-bearing block. Server
        // validates the cell and the block kind, allocates a windowId,
        // and replies with OpenWindow + TileEntityData.
        public void SendInteractBlock(int x, int y, int z)
        {
            if (!_loggedIn) return;
            _session.Send(PacketIds.PlayerInteractBlock, w => new PlayerInteractBlockPacket
            {
                X = x, Y = y, Z = z,
            }.Write(w));
        }

        // Phase 6c — friend clicked a slot inside an open window
        // (chest / furnace / crafting). Distinct from
        // SendInventoryClick which targets the player's own inventory.
        public void SendWindowClick(byte windowId, byte slot, byte button, bool shift)
        {
            if (!_loggedIn) return;
            _session.Send(PacketIds.WindowClick, w => new WindowClickPacket
            {
                WindowId = windowId,
                Slot = slot,
                Button = button,
                Shift = (byte)(shift ? 1 : 0),
            }.Write(w));
        }

        // Phase 6c — friend hit Esc / clicked away → close the window.
        // Server commits the cursor stack into the player's main grid
        // (or drops at feet) and removes the window state record.
        public void SendCloseWindow(byte windowId)
        {
            if (!_loggedIn) return;
            _session.Send(PacketIds.CloseWindow, w => new CloseWindowPacket
            {
                WindowId = windowId,
            }.Write(w));
        }

        // Phase 6b — friend RMB intent. Server uses the friend's last-
        // known position + held hotbar slot to decide what to do
        // (snowball / egg throw shipped; bow / bucket / fishing rod
        // deferred to Phase 6c+).
        public void SendUseItem()
        {
            if (!_loggedIn) return;
            _session.Send(PacketIds.PlayerUseItem);
        }

        public void Disconnect(string reason)
        {
            // Best-effort orderly hangup so the server logs a clean exit
            // before the socket actually closes. If the session is
            // already dead, Send is a no-op.
            _session.Send(PacketIds.Disconnect, w => new DisconnectPacket { Reason = reason ?? "" }.Write(w));
            _session.Disconnect(reason ?? "client disconnect");
        }

        public void Dispose()
        {
            _session.Disconnect("client disposed");
            try { _tcp.Close(); } catch { /* ignored */ }
        }
    }
}
