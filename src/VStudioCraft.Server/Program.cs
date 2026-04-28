using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Sockets;
using System.Threading;
using VStudioCraft.Game;
using VStudioCraft.Net;

namespace VStudioCraft.Server
{
    // Phase-1 dedicated-server entry point.
    //
    // What this does today:
    //   - Generates a fresh world (no save/load yet — Phase 8 wires that in).
    //   - Drives a fixed 20 Hz tick loop: fluid spread + crop growth + mob
    //     spawn off the world's spawn anchor (no players yet).
    //   - Prints a heartbeat once per second so you can tell it's alive.
    //
    // What this does NOT do yet:
    //   - Networking. There's no TcpListener — Phase 2 adds the socket loop,
    //     handshake, and chunk packets. Until then the server is a "headless
    //     simulator" that proves the Game\ sources compile and the World can
    //     tick without a GL context, audio engine, or input system.
    //
    // Why a fixed 20 Hz tick (vs the existing variable-dt frame loop in
    // GameHostControl.RenderLoop): Alpha 1.1.2_01 is 20 TPS. With server
    // authority, the client interpolates between server snapshots, so the
    // wall-clock cadence has to be predictable. 50 ms per tick lets crop
    // / fluid / mob-spawn timers stay frame-rate-independent and matches
    // the Notchian Alpha cadence exactly. See the "Game loop is frame-driven"
    // section of the multiplayer plan.
    internal static class Program
    {
        // 20 ticks/sec = 50 ms/tick. Don't change without auditing the crop
        // and fluid timers in World.cs / FluidTick.cs — they're written
        // against this cadence (TickRandomCrops uses dt = 0.05 implicitly
        // for the random-crop budget).
        private const double TickHz = 20.0;
        private const double TickSeconds = 1.0 / TickHz;
        private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(TickSeconds);

        private static volatile bool _stopRequested;

        private static int Main(string[] args)
        {
            int seed = ParseSeed(args);
            int port = ParsePort(args);
            Console.WriteLine($"[server] VStudioCraft headless server (Phase 2)");
            Console.WriteLine($"[server] Seed: {seed}, Port: {port}");

            // Ctrl+C cleanly drops out of the tick loop instead of killing
            // the process mid-tick. Phase 8 will hook autosave in here.
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                Console.WriteLine("[server] Stop requested (Ctrl+C). Finishing tick…");
                _stopRequested = true;
            };

            World world;
            try
            {
                var genWatch = Stopwatch.StartNew();
                world = World.Generate(seed);
                genWatch.Stop();
                Console.WriteLine($"[server] World generated in {genWatch.ElapsedMilliseconds} ms ({World.InitialRadiusChunks * 2 + 1}x{World.InitialRadiusChunks * 2 + 1} initial chunks).");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[server] FATAL: world generation failed: {ex}");
                return 1;
            }

            ServerHub hub;
            try
            {
                hub = new ServerHub(world, port);
                hub.Start();
                Console.WriteLine($"[server] Listening on 0.0.0.0:{hub.Port}");
            }
            // ReSharper disable once RedundantCatchClause — explicit error
            // path so the operator sees a precise startup failure (e.g.
            // port-in-use) instead of a generic stack trace.
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[server] FATAL: failed to start network listener on port {port}: {ex.Message}");
                return 3;
            }

            // --selftest exercises the protocol against ourselves on
            // loopback before / instead of running the steady-state tick
            // loop. Useful for CI ("does the handshake still work?")
            // without spinning up a real client. Runs on a background
            // thread so the server tick keeps draining and serving it.
            if (HasFlag(args, "--selftest"))
            {
                var selfTestThread = new Thread(() => RunSelfTest(hub.Port))
                {
                    IsBackground = true,
                    Name = "SelfTestClient",
                };
                selfTestThread.Start();
            }

            // Phase 4 — multiplayer-specific smoke test. Opens TWO
            // TcpClients on loopback (alice + bob), drives both through
            // login + a few PlayerPosLook ticks, then confirms alice's
            // inbound stream contains an EntitySpawn for bob followed
            // by at least one EntityRelMove / RelMoveLook / Teleport
            // matching the position bob is sending.
            if (HasFlag(args, "--selftest-mp"))
            {
                var t = new Thread(() => RunSelfTestMp(hub.Port))
                {
                    IsBackground = true,
                    Name = "SelfTestMp",
                };
                t.Start();
            }

            try
            {
                RunTickLoop(world, hub);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[server] FATAL: tick loop crashed: {ex}");
                hub.Stop();
                return 2;
            }

            hub.Stop();
            Console.WriteLine("[server] Stopped.");
            return 0;
        }

        private static bool HasFlag(string[] args, string flag)
        {
            for (int i = 0; i < args.Length; i++)
                if (args[i] == flag) return true;
            return false;
        }

        // Loopback handshake exerciser. Connects to the server we just
        // started, performs LoginRequest -> LoginResponse, drains the
        // initial chunk burst, and prints a short summary. Exits the
        // process on completion so this doubles as a CI smoke test.
        private static void RunSelfTest(int port)
        {
            try
            {
                Thread.Sleep(200); // give the listener a moment to bind
                Console.WriteLine($"[selftest] connecting to 127.0.0.1:{port}");
                using (var tcp = new TcpClient())
                {
                    tcp.Connect("127.0.0.1", port);
                    tcp.NoDelay = true;
                    var stream = tcp.GetStream();
                    var w = new PacketWriter(stream);
                    var r = new PacketReader(stream);

                    // --- Login --------------------------------------------
                    w.WriteByte(PacketIds.LoginRequest);
                    new LoginRequestPacket
                    {
                        ProtocolVersion = PacketIds.ProtocolVersion,
                        Username        = "selftest",
                    }.Write(w);
                    Console.WriteLine("[selftest] sent LoginRequest");

                    // Phase 1: login + initial chunk burst. Phase 2: send a
                    // PlayerPosLook so the server flags us InGame, then a
                    // PlayerPlace + PlayerDig to confirm the full intent →
                    // broadcast → BlockChange loop end-to-end.
                    bool sawLoginResponse = false;
                    int chunksReceived = 0;
                    int totalCompressedBytes = 0;
                    int spawnSurfaceY = -1;
                    byte[] spawnChunkBlocks = null;
                    var deadline = DateTime.UtcNow.AddSeconds(5);
                    while (DateTime.UtcNow < deadline && chunksReceived < 25)
                    {
                        byte id = r.ReadByte();
                        switch (id)
                        {
                            case PacketIds.KeepAlive:
                                break;
                            case PacketIds.LoginResponse:
                                var lr = LoginResponsePacket.Read(r);
                                Console.WriteLine($"[selftest] LoginResponse: eid={lr.EntityId} seed={lr.Seed} mode={lr.GameMode} spawn=({lr.SpawnX},{lr.SpawnY},{lr.SpawnZ})");
                                sawLoginResponse = true;
                                break;
                            case PacketIds.ChunkLoad:
                                var cl = ChunkLoadPacket.Read(r);
                                chunksReceived++;
                                totalCompressedBytes += cl.CompressedBlocks.Length;
                                var raw = new byte[Chunk.SizeX * Chunk.SizeY * Chunk.SizeZ];
                                int rd = 0;
                                using (var ms = new MemoryStream(cl.CompressedBlocks))
                                using (var gz = new GZipStream(ms, CompressionMode.Decompress))
                                {
                                    int n;
                                    while (rd < raw.Length && (n = gz.Read(raw, rd, raw.Length - rd)) > 0) rd += n;
                                }
                                if (rd != raw.Length) throw new InvalidDataException($"chunk payload decompressed to {rd} bytes, expected {raw.Length}");
                                // Cache the (0,0) chunk so we can scan a
                                // surface column for the dig/place test.
                                if (cl.ChunkX == 0 && cl.ChunkZ == 0)
                                {
                                    spawnChunkBlocks = raw;
                                    spawnSurfaceY = FindTopSolidY(raw, lx: 8, lz: 8);
                                }
                                break;
                            case PacketIds.Disconnect:
                                var d = DisconnectPacket.Read(r);
                                throw new IOException($"server disconnected: {d.Reason}");
                            // Block changes & chunk unloads can race in
                            // during the burst — drain harmlessly.
                            case PacketIds.BlockChange: BlockChangePacket.Read(r); break;
                            case PacketIds.ChunkUnload: ChunkUnloadPacket.Read(r); break;
                            // Phase 5 — once mobs replicate from the
                            // pre-generated spawn area, entity packets
                            // can arrive during the chunk burst. Drain
                            // them harmlessly; this selftest validates
                            // the chunk path, not the entity path
                            // (--selftest-mp is the entity-path test).
                            case PacketIds.EntitySpawn: EntitySpawnPacket.Read(r); break;
                            case PacketIds.EntityRelMove: EntityRelMovePacket.Read(r); break;
                            case PacketIds.EntityLook: EntityLookPacket.Read(r); break;
                            case PacketIds.EntityRelMoveLook: EntityRelMoveLookPacket.Read(r); break;
                            case PacketIds.EntityTeleport: EntityTeleportPacket.Read(r); break;
                            case PacketIds.EntityDespawn: EntityDespawnPacket.Read(r); break;
                            default:
                                throw new InvalidDataException($"unexpected packet id 0x{id:X2}");
                        }
                    }

                    if (!sawLoginResponse) throw new InvalidDataException("never received LoginResponse");
                    if (chunksReceived == 0) throw new InvalidDataException("never received any ChunkLoad");
                    if (spawnChunkBlocks == null) throw new InvalidDataException("never received the spawn (0,0) chunk");
                    if (spawnSurfaceY < 0) throw new InvalidDataException($"no solid block in spawn column at lx=8, lz=8 (chunk seems empty)");

                    Console.WriteLine($"[selftest] login + {chunksReceived} chunks ({totalCompressedBytes} compressed bytes); surface at y={spawnSurfaceY}");

                    // --- Phase 3: dig + place loopback test --------------
                    // Position ourselves above the surface so reach checks
                    // accept the click. World coords for chunk (0,0)
                    // local (8,8) are world (8, _, 8).
                    int wx = 8, wz = 8;
                    int sy = spawnSurfaceY;
                    w.WriteByte(PacketIds.PlayerPosLook);
                    new PlayerPosLookPacket
                    {
                        X = wx + 0.5, Y = sy + 1, Z = wz + 0.5,
                        Yaw = 0f, Pitch = 0f, OnGround = true,
                    }.Write(w);

                    // Place a Stone (BlockType=1) ON TOP of the surface
                    // block: click cell = (wx, sy, wz), face=1 (+Y top).
                    // Server resolves placement target = (wx, sy+1, wz).
                    const byte StoneType = 1;
                    w.WriteByte(PacketIds.PlayerPlace);
                    new PlayerPlacePacket
                    {
                        X = wx, Y = sy, Z = wz, Face = 1, BlockType = StoneType,
                    }.Write(w);

                    // Drain packets until we see BlockChange at (wx, sy+1, wz)
                    // with type Stone — proves the place reached the world
                    // and the broadcast loop works.
                    int placeTargetY = sy + 1;
                    bool sawPlace = false;
                    var placeDeadline = DateTime.UtcNow.AddSeconds(2);
                    while (DateTime.UtcNow < placeDeadline && !sawPlace)
                    {
                        byte id2 = r.ReadByte();
                        switch (id2)
                        {
                            case PacketIds.KeepAlive: break;
                            case PacketIds.ChunkLoad: ChunkLoadPacket.Read(r); break;
                            case PacketIds.ChunkUnload: ChunkUnloadPacket.Read(r); break;
                            case PacketIds.EntitySpawn: EntitySpawnPacket.Read(r); break;
                            case PacketIds.EntityRelMove: EntityRelMovePacket.Read(r); break;
                            case PacketIds.EntityLook: EntityLookPacket.Read(r); break;
                            case PacketIds.EntityRelMoveLook: EntityRelMoveLookPacket.Read(r); break;
                            case PacketIds.EntityTeleport: EntityTeleportPacket.Read(r); break;
                            case PacketIds.EntityDespawn: EntityDespawnPacket.Read(r); break;
                            case PacketIds.BlockChange:
                                var bc = BlockChangePacket.Read(r);
                                if (bc.X == wx && bc.Y == placeTargetY && bc.Z == wz && bc.BlockType == StoneType) sawPlace = true;
                                break;
                            case PacketIds.Disconnect:
                                var d2 = DisconnectPacket.Read(r);
                                throw new IOException($"server disconnected during place: {d2.Reason}");
                            default:
                                throw new InvalidDataException($"unexpected packet id 0x{id2:X2}");
                        }
                    }
                    if (!sawPlace) throw new InvalidDataException($"never received BlockChange confirming place at ({wx}, {placeTargetY}, {wz})");
                    Console.WriteLine($"[selftest] place OK: stone at ({wx}, {placeTargetY}, {wz})");

                    // Now dig the cell we just placed. Server should
                    // SetBlock(Air) and broadcast BlockChange with type=0.
                    w.WriteByte(PacketIds.PlayerDigStart);
                    new PlayerDigPacket
                    {
                        Status = 0, X = wx, Y = placeTargetY, Z = wz, Face = 1,
                    }.Write(w);
                    bool sawDig = false;
                    var digDeadline = DateTime.UtcNow.AddSeconds(2);
                    while (DateTime.UtcNow < digDeadline && !sawDig)
                    {
                        byte id3 = r.ReadByte();
                        switch (id3)
                        {
                            case PacketIds.KeepAlive: break;
                            case PacketIds.ChunkLoad: ChunkLoadPacket.Read(r); break;
                            case PacketIds.ChunkUnload: ChunkUnloadPacket.Read(r); break;
                            case PacketIds.EntitySpawn: EntitySpawnPacket.Read(r); break;
                            case PacketIds.EntityRelMove: EntityRelMovePacket.Read(r); break;
                            case PacketIds.EntityLook: EntityLookPacket.Read(r); break;
                            case PacketIds.EntityRelMoveLook: EntityRelMoveLookPacket.Read(r); break;
                            case PacketIds.EntityTeleport: EntityTeleportPacket.Read(r); break;
                            case PacketIds.EntityDespawn: EntityDespawnPacket.Read(r); break;
                            case PacketIds.BlockChange:
                                var bc2 = BlockChangePacket.Read(r);
                                if (bc2.X == wx && bc2.Y == placeTargetY && bc2.Z == wz && bc2.BlockType == 0) sawDig = true;
                                break;
                            case PacketIds.Disconnect:
                                var d3 = DisconnectPacket.Read(r);
                                throw new IOException($"server disconnected during dig: {d3.Reason}");
                            default:
                                throw new InvalidDataException($"unexpected packet id 0x{id3:X2}");
                        }
                    }
                    if (!sawDig) throw new InvalidDataException($"never received BlockChange confirming dig at ({wx}, {placeTargetY}, {wz})");
                    Console.WriteLine($"[selftest] dig OK: air at ({wx}, {placeTargetY}, {wz})");

                    Console.WriteLine($"[selftest] OK: login + chunks + place + dig roundtrip");

                    // Send orderly Disconnect so the server logs a clean exit.
                    w.WriteByte(PacketIds.Disconnect);
                    new DisconnectPacket { Reason = "selftest done" }.Write(w);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[selftest] FAIL: {ex.GetType().Name}: {ex.Message}");
                Environment.ExitCode = 4;
            }
            finally
            {
                _stopRequested = true;
            }
        }

        // Open a TcpClient + perform login, returning a (tcp, reader, writer,
        // entityId) tuple. Helper for --selftest-mp; not used elsewhere.
        private static (TcpClient tcp, PacketReader r, PacketWriter w, int eid) ConnectAndLogin(int port, string user)
        {
            var tcp = new TcpClient();
            tcp.Connect("127.0.0.1", port);
            tcp.NoDelay = true;
            var s = tcp.GetStream();
            var w = new PacketWriter(s);
            var r = new PacketReader(s);
            w.WriteByte(PacketIds.LoginRequest);
            new LoginRequestPacket { ProtocolVersion = PacketIds.ProtocolVersion, Username = user }.Write(w);
            int eid = -1;
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                byte id = r.ReadByte();
                switch (id)
                {
                    case PacketIds.LoginResponse:
                        var lr = LoginResponsePacket.Read(r);
                        eid = lr.EntityId;
                        return (tcp, r, w, eid);
                    case PacketIds.KeepAlive: break;
                    case PacketIds.Disconnect:
                        var d = DisconnectPacket.Read(r);
                        throw new IOException($"login refused: {d.Reason}");
                    default:
                        throw new InvalidDataException($"unexpected packet 0x{id:X2} during login");
                }
            }
            throw new TimeoutException("login timeout");
        }

        // Drain inbound packets without acting on them, returning when
        // we either see the predicate match or the deadline expires.
        // Returns the matched packet info via out.
        private static bool DrainUntil(PacketReader r, DateTime deadline, Func<byte, bool> predicate, out byte matchedId)
        {
            matchedId = 0;
            while (DateTime.UtcNow < deadline)
            {
                byte id = r.ReadByte();
                bool match = predicate(id);
                // Always consume the payload so the stream stays aligned.
                switch (id)
                {
                    case PacketIds.KeepAlive: break;
                    case PacketIds.ChunkLoad: ChunkLoadPacket.Read(r); break;
                    case PacketIds.ChunkUnload: ChunkUnloadPacket.Read(r); break;
                    case PacketIds.BlockChange: BlockChangePacket.Read(r); break;
                    case PacketIds.EntitySpawn: EntitySpawnPacket.Read(r); break;
                    case PacketIds.EntityRelMove: EntityRelMovePacket.Read(r); break;
                    case PacketIds.EntityLook: EntityLookPacket.Read(r); break;
                    case PacketIds.EntityRelMoveLook: EntityRelMoveLookPacket.Read(r); break;
                    case PacketIds.EntityTeleport: EntityTeleportPacket.Read(r); break;
                    case PacketIds.EntityDespawn: EntityDespawnPacket.Read(r); break;
                    case PacketIds.Disconnect:
                        var d = DisconnectPacket.Read(r);
                        throw new IOException($"server disconnected: {d.Reason}");
                    default:
                        throw new InvalidDataException($"unexpected packet 0x{id:X2}");
                }
                if (match)
                {
                    matchedId = id;
                    return true;
                }
            }
            return false;
        }

        private static void RunSelfTestMp(int port)
        {
            try
            {
                Thread.Sleep(200);
                Console.WriteLine($"[selftest-mp] connecting alice + bob to 127.0.0.1:{port}");
                var (atcp, ar, aw, aeid) = ConnectAndLogin(port, "alice");
                using (atcp)
                {
                    var (btcp, br, bw, beid) = ConnectAndLogin(port, "bob");
                    using (btcp)
                    {
                        Console.WriteLine($"[selftest-mp] alice eid={aeid}, bob eid={beid}");

                        // Both players ship a PlayerPosLook so the server
                        // marks them past AwaitingLogin (HasReportedPos=true)
                        // and starts considering them for entity broadcast.
                        // We use slightly different positions so each is in
                        // the other's chunk window from the get-go.
                        for (int i = 0; i < 5; i++)
                        {
                            aw.WriteByte(PacketIds.PlayerPosLook);
                            new PlayerPosLookPacket { X = 0.5,  Y = 80, Z = 0.5,  Yaw = 0,  Pitch = 0, OnGround = true }.Write(aw);
                            bw.WriteByte(PacketIds.PlayerPosLook);
                            new PlayerPosLookPacket { X = 4.5,  Y = 80, Z = 4.5,  Yaw = 90, Pitch = 0, OnGround = true }.Write(bw);
                            Thread.Sleep(60); // > 1 server tick so packets land between ticks
                        }

                        // Verify alice received an EntitySpawn for bob.
                        // We flood-drain alice's inbound stream up to a 3 s
                        // deadline looking for the spawn id.
                        int seenSpawnFor = -1;
                        var deadline = DateTime.UtcNow.AddSeconds(3);
                        while (DateTime.UtcNow < deadline && seenSpawnFor != beid)
                        {
                            byte id = ar.ReadByte();
                            switch (id)
                            {
                                case PacketIds.KeepAlive: break;
                                case PacketIds.ChunkLoad: ChunkLoadPacket.Read(ar); break;
                                case PacketIds.ChunkUnload: ChunkUnloadPacket.Read(ar); break;
                                case PacketIds.BlockChange: BlockChangePacket.Read(ar); break;
                                case PacketIds.EntitySpawn:
                                    var es = EntitySpawnPacket.Read(ar);
                                    if (es.EntityId == beid && es.DisplayName == "bob") seenSpawnFor = es.EntityId;
                                    break;
                                case PacketIds.EntityRelMove: EntityRelMovePacket.Read(ar); break;
                                case PacketIds.EntityLook: EntityLookPacket.Read(ar); break;
                                case PacketIds.EntityRelMoveLook: EntityRelMoveLookPacket.Read(ar); break;
                                case PacketIds.EntityTeleport: EntityTeleportPacket.Read(ar); break;
                                case PacketIds.EntityDespawn: EntityDespawnPacket.Read(ar); break;
                                default: throw new InvalidDataException($"unexpected 0x{id:X2}");
                            }
                        }
                        if (seenSpawnFor != beid)
                            throw new InvalidDataException($"alice never received EntitySpawn for bob (eid={beid})");
                        Console.WriteLine($"[selftest-mp] alice sees EntitySpawn for bob (eid={beid})");

                        // Now move bob and verify alice gets a corresponding
                        // entity-update packet.
                        for (int i = 0; i < 10; i++)
                        {
                            bw.WriteByte(PacketIds.PlayerPosLook);
                            new PlayerPosLookPacket
                            {
                                X = 4.5 + i * 0.3, Y = 80, Z = 4.5,
                                Yaw = 90 + i * 5, Pitch = 0, OnGround = true,
                            }.Write(bw);
                            Thread.Sleep(60);
                        }

                        bool sawMove = DrainUntil(ar, DateTime.UtcNow.AddSeconds(3),
                            id => id == PacketIds.EntityRelMove
                               || id == PacketIds.EntityRelMoveLook
                               || id == PacketIds.EntityTeleport
                               || id == PacketIds.EntityLook,
                            out var movId);
                        if (!sawMove) throw new InvalidDataException("alice never received an entity-update packet for bob's motion");
                        Console.WriteLine($"[selftest-mp] alice sees entity update (id=0x{movId:X2}) for bob's move");

                        // Phase 5 — verify mob replication. The server's
                        // initial 5×5 spawn pass populates _passives with
                        // a few pigs/cows/sheep/chickens; once bob's chunk
                        // window contains those mobs, bob should receive
                        // EntitySpawn packets with EntityType in [1..4].
                        bool sawMobSpawn = false;
                        var mobDeadline = DateTime.UtcNow.AddSeconds(3);
                        while (DateTime.UtcNow < mobDeadline && !sawMobSpawn)
                        {
                            byte mid = br.ReadByte();
                            switch (mid)
                            {
                                case PacketIds.KeepAlive: break;
                                case PacketIds.ChunkLoad: ChunkLoadPacket.Read(br); break;
                                case PacketIds.ChunkUnload: ChunkUnloadPacket.Read(br); break;
                                case PacketIds.BlockChange: BlockChangePacket.Read(br); break;
                                case PacketIds.EntitySpawn:
                                    var es = EntitySpawnPacket.Read(br);
                                    // Pig=1, Cow=2, Sheep=3, Chicken=4
                                    if (es.EntityType >= 1 && es.EntityType <= 4) sawMobSpawn = true;
                                    break;
                                case PacketIds.EntityRelMove: EntityRelMovePacket.Read(br); break;
                                case PacketIds.EntityLook: EntityLookPacket.Read(br); break;
                                case PacketIds.EntityRelMoveLook: EntityRelMoveLookPacket.Read(br); break;
                                case PacketIds.EntityTeleport: EntityTeleportPacket.Read(br); break;
                                case PacketIds.EntityDespawn: EntityDespawnPacket.Read(br); break;
                                default: throw new InvalidDataException($"unexpected 0x{mid:X2}");
                            }
                        }
                        if (!sawMobSpawn) throw new InvalidDataException("bob never received EntitySpawn for any passive mob");
                        Console.WriteLine($"[selftest-mp] bob sees EntitySpawn for a passive mob");

                        // Disconnect bob and verify alice gets EntityDespawn.
                        bw.WriteByte(PacketIds.Disconnect);
                        new DisconnectPacket { Reason = "selftest-mp bob done" }.Write(bw);
                        bool sawDespawn = DrainUntil(ar, DateTime.UtcNow.AddSeconds(3),
                            id => id == PacketIds.EntityDespawn,
                            out _);
                        if (!sawDespawn) throw new InvalidDataException("alice never received EntityDespawn after bob disconnected");
                        Console.WriteLine($"[selftest-mp] alice sees EntityDespawn for bob");

                        Console.WriteLine($"[selftest-mp] OK: spawn + move + despawn + mob replication");

                        aw.WriteByte(PacketIds.Disconnect);
                        new DisconnectPacket { Reason = "selftest-mp alice done" }.Write(aw);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[selftest-mp] FAIL: {ex.GetType().Name}: {ex.Message}");
                Environment.ExitCode = 5;
            }
            finally
            {
                _stopRequested = true;
            }
        }

        // Find topmost solid (non-air) block in a column inside a raw
        // chunk byte buffer. Indexing matches Chunk.Index: (lx*SizeY+y)*SizeZ+lz.
        // Returns -1 if the entire column is air (shouldn't happen for
        // any normal terrain, but guards against an empty world.)
        private static int FindTopSolidY(byte[] raw, int lx, int lz)
        {
            for (int y = Chunk.SizeY - 1; y >= 0; y--)
            {
                int idx = (lx * Chunk.SizeY + y) * Chunk.SizeZ + lz;
                if (raw[idx] != 0) return y; // BlockType.Air == 0
            }
            return -1;
        }

        // --port=N or --port N. Falls back to ServerHub.DefaultPort.
        private static int ParsePort(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                var a = args[i];
                if (a.StartsWith("--port=", StringComparison.Ordinal)
                    && int.TryParse(a.Substring("--port=".Length), out var p)) return p;
                if (a == "--port" && i + 1 < args.Length
                    && int.TryParse(args[i + 1], out var p2)) return p2;
            }
            return ServerHub.DefaultPort;
        }

        // Tries to read a seed from --seed=N or the first positional arg.
        // Falls back to a time-based seed so a casual launch doesn't always
        // generate the same world.
        private static int ParseSeed(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                var a = args[i];
                if (a.StartsWith("--seed=", StringComparison.Ordinal)
                    && int.TryParse(a.Substring("--seed=".Length), out var s)) return s;
                if (a == "--seed" && i + 1 < args.Length
                    && int.TryParse(args[i + 1], out var s2)) return s2;
                if (int.TryParse(a, out var s3)) return s3;
            }
            return Environment.TickCount;
        }

        // Fixed-timestep tick loop with a sleep-based pacer.
        //
        // We deliberately avoid Thread.Sleep(50) on its own — Windows timer
        // resolution is ~15 ms by default, so a naive Sleep drifts. Instead
        // we compute the next tick's deadline from the loop start and sleep
        // up to (but not past) it, then yield. Phase 7 (integrated server)
        // will replace this with the same pacer running on a thread inside
        // the client process.
        private static void RunTickLoop(World world, ServerHub hub)
        {
            // Anchor for mob-spawn / fluid simulation. Today there's no
            // player connected, so we tick with the world origin as the
            // "interest point". When Phase 2 adds player sessions, this
            // becomes a per-player loop and the spawn / fluid passes use
            // the union of all players' positions. SkySubtract=0 (full
            // daylight) for the same reason — without a real day/night
            // driver, hostile mobs would never spawn. Phase 5 plugs the
            // real value in.
            var anchor = OpenTK.Vector3.Zero;
            const int skySubtract = 0;

            long tickCount = 0;
            var startedAt = Stopwatch.StartNew();
            var nextTickAt = startedAt.Elapsed;
            var lastHeartbeatTick = 0L;

            Console.WriteLine($"[server] Tick loop started ({TickHz:F0} Hz). Ctrl+C to stop.");

            // Tracks the worst-case sim cost across the heartbeat window so
            // the once-per-second log shows the headroom-eating tick, not
            // the average. Reset at every heartbeat fire.
            var worstSimMs = 0.0;

            while (!_stopRequested)
            {
                var tickStart = startedAt.Elapsed;

                // --- network: drain inbound, advance per-client state -------
                // Runs before the world sim so client intents (chunk requests,
                // PlayerPosLook) can influence this tick's simulation rather
                // than waiting for the next one. Phase 3+ adds dig / place
                // packets, which absolutely must land before the world tick
                // applies their effects.
                hub.Tick();

                // --- simulation ---------------------------------------------
                // These three are the world-level passes that don't require a
                // player today. Everything else (entity physics, pickups,
                // damage, projectiles) currently lives in GameHostControl.
                // RenderLoop and will move into a unified World.Tick in
                // Phase 3.
                FluidTick.Tick(world);
                world.TickRandomCrops((float)TickSeconds);
                world.TickMobSpawns((float)TickSeconds, anchor, skySubtract);

                tickCount++;

                // Capture sim cost BEFORE we sleep — otherwise the heartbeat
                // measurement is dominated by the pacer sleep and reports
                // ~50 ms regardless of actual load. This is the value we
                // care about for "how much budget is left" decisions.
                var simMs = (startedAt.Elapsed - tickStart).TotalMilliseconds;
                if (simMs > worstSimMs) worstSimMs = simMs;

                // --- pacing -------------------------------------------------
                // Deadline-based sleep so dropped ticks don't compound into
                // long-term drift. If a tick overruns we don't try to
                // "catch up" by running multiple ticks back-to-back — that
                // would just snowball under load and starve the OS.
                nextTickAt += TickInterval;
                var slack = nextTickAt - startedAt.Elapsed;
                if (slack > TimeSpan.Zero)
                {
                    Thread.Sleep(slack);
                }
                else if (slack < TimeSpan.FromSeconds(-1))
                {
                    // We're more than a second behind. Reset the deadline
                    // so we don't busy-loop trying to catch up forever.
                    Console.WriteLine($"[server] Tick lag {(-slack).TotalMilliseconds:F0} ms — resetting pacer.");
                    nextTickAt = startedAt.Elapsed;
                }

                // --- heartbeat ----------------------------------------------
                // Once per second so an operator can tell the loop is alive
                // and how loaded it is. We report worst-case sim cost in
                // the window (not average) because a single 49 ms tick
                // matters more than an average of 5 ms — that's the tick
                // that's about to start dropping under load.
                if (tickCount - lastHeartbeatTick >= (long)TickHz)
                {
                    Console.WriteLine($"[server] tick {tickCount,-7} sim_max={worstSimMs,5:F1} ms chunks={world.ChunkCount} clients={hub.ConnectedCount}");
                    lastHeartbeatTick = tickCount;
                    worstSimMs = 0.0;
                }
            }
        }
    }
}
