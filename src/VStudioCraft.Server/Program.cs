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

                    // Read packets until we've seen a LoginResponse and
                    // at least one ChunkLoad — that proves the full
                    // handshake -> stream path works.
                    bool sawLoginResponse = false;
                    int chunksReceived = 0;
                    int totalCompressedBytes = 0;
                    var deadline = DateTime.UtcNow.AddSeconds(5);
                    while (DateTime.UtcNow < deadline && chunksReceived < 5)
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
                                // Decompress to verify the payload is well-formed.
                                int decompressed = 0;
                                using (var ms = new MemoryStream(cl.CompressedBlocks))
                                using (var gz = new GZipStream(ms, CompressionMode.Decompress))
                                {
                                    var buf = new byte[Chunk.SizeX * Chunk.SizeY * Chunk.SizeZ];
                                    int read;
                                    while ((read = gz.Read(buf, 0, buf.Length)) > 0) decompressed += read;
                                }
                                if (decompressed != Chunk.SizeX * Chunk.SizeY * Chunk.SizeZ)
                                {
                                    throw new InvalidDataException($"chunk payload decompressed to {decompressed} bytes, expected {Chunk.SizeX * Chunk.SizeY * Chunk.SizeZ}");
                                }
                                break;
                            case PacketIds.Disconnect:
                                var d = DisconnectPacket.Read(r);
                                throw new IOException($"server disconnected: {d.Reason}");
                            default:
                                throw new InvalidDataException($"unexpected packet id 0x{id:X2}");
                        }
                    }

                    if (!sawLoginResponse) throw new InvalidDataException("never received LoginResponse");
                    if (chunksReceived == 0) throw new InvalidDataException("never received any ChunkLoad");

                    Console.WriteLine($"[selftest] OK: login + {chunksReceived} chunks ({totalCompressedBytes} compressed bytes)");

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
