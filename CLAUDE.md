# CLAUDE.md — Multiplayer feature notes

This file tracks design notes, known issues, and follow-ups for the
multiplayer (feature 50) work. Originally spawned out of the Phase 2
implementation; updated as later phases land.

The detailed plan lives in `~/.claude/plans/do-an-analysis-of-abundant-crescent.md`.

## Project layout

```
src/
  VStudioCraft/                 # VSIX extension (canonical home of Game\ + Net\)
    Game/                       # Simulation + renderer + audio (28k LoC)
    Net/                        # Wire protocol + sessions  (Phase 2)
    UI/GameHostControl.xaml.cs  # WPF host with the render-thread loop
  VStudioCraft.Standalone/      # WPF console app — links Game\ + Net\
  VStudioCraft.Server/          # Headless dedicated server (Phase 1+)
    Program.cs                  # 20 Hz tick loop, --selftest, --port, --seed
    ServerHub.cs                # TcpListener + per-client state machine
```

The Server links `Game\*.cs` (with no exclusions) and `Net\*.cs`. It uses
the WindowsDesktop SDK + `UseWindowsForms=true` so the InputState /
BlockTextures / AudioEngine references compile — those modules are inert
at runtime because Server.Program never instantiates GameRenderer or calls
`AudioEngine.Initialize`.

## Build commands

```powershell
# Full solution build
& "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" VStudioCraft.sln /t:Build /v:minimal /nologo

# Just the headless server (faster iteration)
& "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" src\VStudioCraft.Server\VStudioCraft.Server.csproj /t:Build /v:minimal /nologo
```

## Server smoke tests

```powershell
# Phase 2/3 — single-client login, chunk burst, place + dig roundtrip
src\VStudioCraft.Server\bin\Debug\net472\VStudioCraft.Server.exe --selftest

# Phase 4 — two-client entity replication: spawn, move, despawn
src\VStudioCraft.Server\bin\Debug\net472\VStudioCraft.Server.exe --selftest-mp
```

Both run a server in-process, perform their loopback exercises, then
shut the server down and exit. Non-zero exit code = failure (see
`Environment.ExitCode` assignments in `Program.RunSelfTest*`).

## End-to-end multiplayer demos

### Dedicated server + remote client

```powershell
# Terminal 1 — dedicated server
src\VStudioCraft.Server\bin\Debug\net472\VStudioCraft.Server.exe --seed=4242

# Terminal 2 — Standalone client
src\VStudioCraft.Standalone\bin\Debug\net472\VStudioCraft.Standalone.exe --connect localhost:25566:tester
```

### Open to LAN (Phase 7)

```powershell
# Terminal 1 — host: starts SP world AND binds listener on 25566
src\VStudioCraft.Standalone\bin\Debug\net472\VStudioCraft.Standalone.exe --openlan=25566

# Terminal 2 — friend: joins the host's world
src\VStudioCraft.Standalone\bin\Debug\net472\VStudioCraft.Standalone.exe --connect localhost:25566:friend
```

The host runs full SP simulation locally (no network round-trip for solo
edits, no extra latency). The in-process server observes the host's
World and broadcasts its state to any connecting friends.

`--connect` accepts `host`, `host:port`, or `host:port:username`. Port
defaults to 25566 (one above Notch's 25565); username defaults to
`$env:USERNAME`. `--openlan` accepts an optional port (default 25566).

## Phase status

- [x] **Phase 1** — Server project + headless World boot at 20 Hz
- [x] **Phase 2a** — Codec + 6 packets (KeepAlive, LoginRequest/Response, Disconnect, PlayerPosLook, ChunkLoad)
- [x] **Phase 2b** — TcpListener + NetSession + login handshake + chunk burst
- [x] **Phase 2c** — Client-side NetClient + GameRenderer net-driven mode
- [x] **Phase 3** — Funnel block mutations through `World.SetBlock`; dig/place intent packets; `BlockChange` broadcast; per-player chunk-window slide + `ChunkUnload`
- [x] **Phase 4** — Entity replication (spawn/despawn/relmove/look/relmovelook/teleport) + `RemotePlayer` snapshot interpolation (100 ms render-behind buffer) + Steve rig rendering for remote players
- **Phase 5** — Mob/drop/projectile replication
  - [x] **5a** — Passive mob replication (Pig/Cow/Sheep/Chicken). Server ticks AI, broadcasts spawn/move/despawn; client adds replicas to `_world.Passives` so the existing `RenderPassives` path draws them. Snap-to-position (no interp) — visible 50ms tick stutter is the first 5b polish item.
  - [x] **5b** — Hostile mob replication (Zombie/Skeleton/Spider/Creeper). Same shape; server uses closest-player as aggro target via `ClosestPlayerPosTo`. `NoopServerSinks` for `IPlayerDamageSink`/`IDropSink` so mobs simulate but don't deliver damage or drops yet (Phase 5c).
  - **5c** — Drop replication: ✅ host's `_drops` list diff'd each tick by `BroadcastLocalDropsDiff`, friends see new drops via `ItemSpawnPacket` (0x27) + RelMove + Despawn. New `EntityType.DroppedItem`. ❌ Pickup-by-remote-players deferred to Phase 6 (needs inventory sync).
  - [ ] **5d** — Projectile replication (Arrow, ThrownProjectile/Snowball/Egg, Bobber) and `PlayerUseItem` (0x43) RMB-intent packet
  - [ ] **5e** — Damage delivery: `EntityHealth` packet, server-side `Player` health tracking, hurt-flash sync
  - [ ] **5f** — Mob/drop interpolation polish (currently snap-to-position; want lerp like RemotePlayer)
  - **5g** — ~~Walk-cycle phase for replicated mobs~~ — turns out moot; passive mobs (Pig/Cow/Sheep/Chicken) don't have walk-cycle animation in either SP or MP. Their legs are static cuboids. Closing without code change.
- [ ] **Phase 6** — Inventory click protocol + server-side recipes + tile-entity sync
- [x] **Phase 7** — "Open to LAN" — host an in-process server alongside running SP world. Working end-to-end: `--openlan[=PORT]` on Standalone starts a SP world and binds a listener; remote clients can `--connect host:port:user` and join. See [Feature: Open to LAN](#feature-open-to-lan) below for the full design.
- [ ] **Phase 8** — Persistence v10 (per-username state) + admin console + autosave

## Known issues / follow-ups

### KI-2 — Server-side player physics validation still missing (now Phase 5+)

**Symptom**: Server trusts client-reported position. A modified client
could send positions implying flight, noclip, or arbitrary teleportation
and the server would accept all of it (within the 6-block reach
tolerance for dig/place but otherwise unrestricted).

**Phase 3 update**: chunk-window streaming follows reported position.

**Phase 4 update**: entity replication now broadcasts the trusted
position to other clients. Position is still purely client-claimed —
only chunk-window streaming and reach-checked dig/place gate on it.

**Still missing**: actual rate-check / AABB physics validation against
last-tick. The MP demo works fine for cooperative play (the originally
chosen design point); this is an anti-cheat polish item, not a blocker.

**Fix (deferred to Phase 5+)**: introduce a server-side `Player`
entity per `ServerClient` with the same AABB-vs-block integrator the
standalone uses (`Entity.IntegrateMotion`). Compare inbound positions
against `last + maxStep`, snap back via a new `PlayerPosLookCorrect`
packet (0x11, already reserved in PacketIds) on outliers.

### KI-3 — TryInteract still a no-op in net-driven mode (Phase 5d/6)

**Symptom**: Connect to a server, right-click a door / crafting table /
chest / furnace — nothing happens. Block break and block place both
work as of Phase 3.

**Cause**: TryInteract handles many distinct intents (door toggle,
snowball throw, bucket use, fishing rod cast, chest/furnace/crafting
open).

**Fix**: Phase 5d adds `PlayerUseItem` (snowball, egg, bucket, fishing
rod). Phase 6 adds `OpenWindow` and the chest/furnace open path. Door
toggle is a small extra variant.

(Phase 3 update — 2026-04-28 — break/place no longer no-ops; they ship
`PlayerDigStart` and `PlayerPlace` and roundtrip a `BlockChange`.)

### KI-6 — `--connect` can't reach a private LAN host without explicit IP

**Symptom**: `--connect localhost` works; `--connect ServerMachine`
might not depending on local DNS / hosts file.

**Cause**: `TcpClient.BeginConnect(string, int, ...)` uses standard
hostname resolution; this isn't actually a bug, just a docs/ux note for
when we add a proper "Connect to Server" dialog in Phase 7+.

**Fix**: Document. Eventually: an IP-or-hostname text field with format
hints in the connect dialog.

## Feature: Open to LAN

The "Open to LAN" feature lets a player who's already in a singleplayer
world expose that exact world on a TCP port so others on their network
can join, mirroring Minecraft's well-known `Open to LAN` button. This
section is the design spec — read this before starting implementation.

### Goal

A player has a singleplayer session running. They press a button (or hit
a keybinding from the pause menu). An in-process TCP server starts on
some port using the *same* `World` instance that's already loaded.
Friends type `--connect host:port:username` (or use the multiplayer
connect screen) and join. Both the host and remote players see each
other's movements, block edits, and the same mobs.

When the host quits to title or saves, the server stops; remote clients
get a `Disconnect` packet, then return to their menu.

### Why this shape (vs the original "Phase 7" loopback rewrite)

The original Phase 7 plan was "make SP a degenerate MP — host runs an
in-process server, connects to it via loopback, all gameplay flows
through net packets." That unifies code paths but is a much bigger
refactor: every direct-world-access path in `GameRenderer` (block
edits, tick loops, raycasts) would have to go through the wire even
in SP, which adds round-trip latency to local play and rebuilds the
input handling.

"Open to LAN" is the smaller and more useful design:

- SP keeps its existing direct-world path. No new latency for solo play.
- The server just *observes* the existing world and broadcasts state
  changes to whatever remote clients connect.
- The local host doesn't go through the network. They mutate `World`
  directly via `TryBreak`/`TryPlace`; the change journal that
  `BroadcastPendingBlockChanges` already drains carries those edits
  to remote clients automatically.

This matches what real Minecraft does, and reuses every primitive
already shipped in Phases 2 → 5.

### Prerequisites (all already shipped)

- `ServerHub` taking a `World` reference rather than owning it ✓
- `World.SetBlock` change journal (`_pendingBlockChanges`) ✓
- `BroadcastPendingBlockChanges` ✓
- `BroadcastEntityUpdates` (for player-vs-player position broadcast) ✓
- `BroadcastMobUpdates` / `BroadcastHostileUpdates` ✓
- Per-client `TrackedChunks` + `SlideChunkWindow` ✓
- `EntityInterpState` for smooth remote-player rendering ✓
- All client-side `EntitySpawn`/`Move`/`Despawn` handlers ✓

### What needs to be added

1. **Skip world simulation in the hub** when a local host is driving the
   world. Today `Program.cs` (the dedicated server) calls
   `FluidTick.Tick` / `world.TickRandomCrops` / `world.TickMobSpawns` /
   `hub.TickPassiveMobs` / `hub.TickHostileMobs` from its tick loop. In
   open-to-LAN mode the host's `RenderLoop` is already running
   `TickPassives` / `TickHostiles` / `world.TickMobSpawns` (gated only
   by `!netDriven`). We must not double-tick.

   Easiest: split `ServerHub.Tick` into two methods:

   ```csharp
   public void Tick()       // network only — drain inbound, broadcast
   public void SimulateTick() // world ticks (mobs, fluid, crops)
   ```

   Dedicated server (`Program.cs`) calls both. Open-to-LAN mode calls
   only `Tick()` from the host's RenderLoop, AFTER the host's existing
   simulation calls have completed for the frame. The world ticks once
   per frame from the SP path; the hub broadcast picks up whatever
   that produced.

2. **Make the local host visible to remote clients.** Today
   `BroadcastEntityUpdates` walks `_clients` (real `ServerClient`
   instances with sockets). The host has no socket. Two options:

   a. **Phantom client** (recommended). Add a virtual `ServerClient`
      whose `NetSession` is a no-op that discards `Send` calls. The
      existing per-pair broadcast loops include it as both viewer and
      target without any change. Pros: zero special-cases in the
      broadcast paths. Cons: an extra `ServerClient` instance for every
      open-to-LAN session and a stub NetSession to maintain.

   b. **Sidecar `HostPlayerSnapshot`**. A struct on `ServerHub` holding
      `(eid, name, pos, yaw, pitch, lastReportedDirty)`. The
      broadcast loops add a special-case pass that emits the host's
      packets to each viewer. Pros: no fake objects. Cons: every
      broadcast path grows a parallel "and also the host" branch.

   Pick **option (a)**. The phantom-client approach falls out of the
   existing code naturally; we'd have to reimplement most of
   `BroadcastEntityUpdates`'s spawn-on-join / despawn-on-OOR logic for
   the sidecar otherwise.

   Implementation sketch for the phantom client:

   ```csharp
   // In ServerHub:
   private ServerClient _hostClient; // null in dedicated mode

   public void EnableLocalHost(string username, OpenTK.Vector3 spawnPos)
   {
       _hostClient = new ServerClient(new LoopbackNetSession())
       {
           EntityId = _nextEntityId++,
           Username = username,
           Phase = ClientPhase.InGame,
           SpawnX = (int)spawnPos.X,
           SpawnY = (int)spawnPos.Y,
           SpawnZ = (int)spawnPos.Z,
           HasReportedPos = true,
           LastReportedX = spawnPos.X,
           LastReportedY = spawnPos.Y,
           LastReportedZ = spawnPos.Z,
       };
       _clients.Add(_hostClient);
   }

   // Host calls each frame from RenderLoop:
   public void UpdateLocalHostPose(OpenTK.Vector3 pos, float yaw, float pitch, bool onGround)
   {
       if (_hostClient == null) return;
       _hostClient.LastReportedX = pos.X;
       _hostClient.LastReportedY = pos.Y;
       _hostClient.LastReportedZ = pos.Z;
       _hostClient.LastReportedYaw = yaw;
       _hostClient.LastReportedPitch = pitch;
   }
   ```

   `LoopbackNetSession` is a tiny subclass that overrides `Send` to a
   no-op and reports `IsDead = false`. The existing broadcast loops
   already filter `if (viewer.Session.IsDead) continue` — they won't
   try to send to the host. The host's `TrackedChunks` set is also
   never populated (we don't ship chunks to ourselves) so the
   `BlockChange` broadcast filter
   `if (!client.TrackedChunks.Contains(...)) continue` correctly skips
   the host without any change.

   Caveat: the host's `TrackedEntities` set IS populated naturally by
   the broadcast loop — we'd ship a phantom EntitySpawn for every
   remote client to the host's null sink. That's wasted work but
   harmless. Optional optimisation: add an `IsLoopback` flag on
   `ServerClient` and skip the inner-loop body for it; not needed for
   correctness.

3. **API on `GameRenderer` and `GameHostControl`.** Mirrors the existing
   `ConnectToServer` / `DisconnectFromServer` shape:

   ```csharp
   // GameRenderer:
   public bool IsHostingLan => _serverHub != null;
   public int  HostedPort   => _serverHub?.Port ?? 0;
   public void OpenToLan(int port = 0, string username = null);
   public void CloseLan();

   // GameHostControl:
   public void OpenToLan(int port = 0, string username = null);
   public void CloseLan();
   public event Action<int> LanOpened;   // fires with bound port
   public event Action      LanClosed;
   ```

   `port = 0` asks the OS to pick a free port (`new TcpListener(IPAddress.Any, 0)`
   then read `LocalEndpoint`); `LanOpened` carries the resolved port
   so the UI can display it. Username defaults to whatever the host
   chose at world create / `$env:USERNAME`.

4. **Per-frame host-pose update from RenderLoop.** Right after the
   existing `_renderer.UpdatePlayer(dt)` call in `GameHostControl.RenderLoop`,
   if the host has a hub running, push the local player's pose into
   it: `_renderer.PushHostPoseToHub()`. This keeps the phantom client's
   `LastReported*` fields fresh so the next `BroadcastEntityUpdates`
   tick emits the correct deltas to remote viewers.

5. **Hub tick driver.** Open-to-LAN mode runs the hub's network tick
   from the host's RenderLoop too, at 20 Hz:

   ```csharp
   // In RenderLoop, alongside DrainNetwork:
   if (_renderer.IsHostingLan)
   {
       _hubTickAccumulator += dt;
       if (_hubTickAccumulator >= 0.05f)
       {
           _hubTickAccumulator -= 0.05f;
           _renderer.TickHubNetwork();
       }
   }
   ```

   `TickHubNetwork()` calls `hub.Tick()` (network-only — drain inbound,
   broadcast) but NOT the simulation passes. The host's existing
   `RenderLoop` mob/fluid/crop calls handle simulation already.

6. **UI hook.** Pause menu gains an "Open to LAN" item that toggles
   open/close. When open, an HUD chip (similar to the F3 overlay)
   shows `Hosting on :NNNN  Players: K`. When closed, returns to
   normal SP. The user is already building these screens
   (`MultiplayerConnectScreen` etc. in the VSIX csproj); the
   open-to-LAN button slots into `PauseMenu` alongside Save / Quit.

### Threading

Host runs simulation on the render thread (today's path). Hub's
accept thread runs on its own background thread (existing behaviour).
NetSession read threads run per-connection on background threads.

The render thread:
- mutates `World` (block edits, mob ticks)
- calls `hub.Tick()` at 20 Hz which reads
  `world.PendingBlockChanges` and walks `_clients`

The accept thread only mutates `_pendingNew` (under a lock) — same as
in dedicated mode.

The read threads only mutate their own session's inbound queue —
already thread-safe via `ConcurrentQueue`.

There's a small new race: the host's render thread writes
`mob.Position` from `TickPassives` while the hub's broadcast
(also on render thread) reads it. Both happen on the SAME thread
sequentially within one `RenderLoop` iteration, so no actual
concurrency. The scary-sounding races (network broadcast vs world
mutation) all serialize through the render thread's sequential calls.

### What doesn't need to change

- `World` — already supports both server-driven (record:true SetBlocks)
  and replica usage.
- `ServerHub` per-client logic (login, chunk window slide, broadcast)
  — works unchanged for both real and phantom clients.
- `NetSession` — `LoopbackNetSession` is a small subclass, but the
  base class is fine.
- All packet types — wire format unchanged.
- Client-side handlers — a remote player joining an open-to-LAN host
  sees the same packets they'd see joining a dedicated server, because
  the host runs the same broadcast logic.

### Estimated cost

Roughly **150–250 LoC** spread across:
- `Net\LoopbackNetSession.cs` (new, ~40 LoC)
- `Server\ServerHub.cs` — `EnableLocalHost`, split `Tick()` and
  `SimulateTick()`, optional `IsLoopback` shortcut on
  `ServerClient` (~50 LoC)
- `Game\GameRenderer.cs` — `_serverHub` field, `OpenToLan` /
  `CloseLan` / `IsHostingLan`, `PushHostPoseToHub` /
  `TickHubNetwork` per-frame helpers (~80 LoC)
- `UI\GameHostControl.xaml.cs` — pass-through methods + events,
  pause-menu button wiring + HUD chip (~40 LoC)
- `Server\Program.cs` — split tick loop to call `Tick()` plus
  `SimulateTick()` separately so dedicated mode behaves the same
  (~10 LoC)

No new packet types, no protocol version bump.

### Verification steps when this gets built

1. **SP unaffected**: launch Standalone, no `--connect` and no LAN
   open — block edits and mobs feel identical to today (no extra
   per-frame allocations or net-thread hops).

2. **Open + remote joins**: launch Standalone A → start a SP world →
   pause → "Open to LAN" → HUD shows port. Launch Standalone B →
   `--connect localhost:NNNN:friend`. B sees A walking, A sees B
   walking. Both can place/break blocks; both see each other's edits.
   Mobs visible on both screens.

3. **Close while remote connected**: from A, "Close LAN". B receives
   `Disconnect{reason="lan host closed"}` and returns to title.

4. **Host save/quit closes LAN**: from A, save & quit to title. LAN
   stops automatically; B disconnected with a clean message.

5. **Two remotes**: launch a third Standalone C, also `--connect`. A,
   B, C all see each other.

6. **Reconnect after host close**: B disconnected, A reopens LAN, B
   reconnects. New `EntityId` allocated; A sees B as a fresh entity.

### Risks

- **`SetBlock` recursion if a remote dig triggers a mob spawn or
  fluid update.** Today's `World.SetBlock` records the change; the
  fluid tick + mob spawn paths run from the host's RenderLoop. If
  those paths call `SetBlock` themselves (they do — fluid spread is
  a SetBlock cascade), additional records accumulate, and the
  next hub tick broadcasts those too. That's actually desired — remote
  clients see fluid spreading from a host's edit. No special handling
  needed; the change journal carries everything.

- **Host-as-phantom-client bugs.** A null-NetSession that crashes
  inside the broadcast loop would silently break broadcasts to real
  clients. Mitigation: the `LoopbackNetSession.Send` override is a
  one-line no-op; defensive null-check on `_hostClient.Session` in
  every broadcast path that already filters `IsDead`.

- **Port already in use.** With `port = 0` we let the OS pick;
  caller can also pass an explicit port, which can fail. `OpenToLan`
  surfaces this via the existing `ConnectFailed` event pattern (or a
  parallel `LanOpenFailed`) so the UI can show a dialog.

### Out of scope for the initial Open-to-LAN cut

- Internet-facing public hosting (UPnP, port forwarding) — strictly
  LAN; we just bind to `IPAddress.Any` and trust the network.
- Authentication beyond username (unchanged from dedicated server —
  offline-mode trust).
- World save while LAN is open — works today via the same Save path,
  but a freeze-the-world-during-save mechanism is a polish item.
- Whitelist / op / kick — admin tooling lands with Phase 8.

## Architectural notes worth preserving

### Why one canonical `Net\` source instead of a `VStudioCraft.Net` assembly

The codebase already uses the "canonical-in-VSIX-project, linked-everywhere"
idiom for `Game\*.cs`. Replicating it for `Net\*.cs` keeps the build graph
flat — three consumers (Server, Standalone, VSIX), zero project references
between source-shared assemblies, no version-skew risk between Net and
the Game types it doesn't actually reference. If we ever want to ship Net
separately (e.g. third-party server tooling), promoting the folder to a
csproj is a 10-minute change.

### Why server-authoritative with no client prediction

Per the user's choice when scoping the feature: matches Alpha 1.1.2
behaviour, simplest correct option, easiest to reason about. The downside
(block edits feel laggy on high ping) is acceptable for a hobby project
where most play is loopback or LAN. Phase 4 adds entity interp so other
players' movement looks smooth despite this; the local player's own
movement runs predictively (camera tracks input directly) but position
authority still lives server-side.

### Why big-endian wire format despite no Alpha-client compat

Hex-dumped packet bytes read in the same order as the C# field
declarations, which makes handshake bugs trivial to spot by eye in
Wireshark. Costs ~0 perf on x86/x64 (one `bswap` per primitive). The
Alpha-shape table at the top of the multiplayer plan reads directly as
the `PacketIds` source.

### Why `internal` types crossing the Net/Game/Server boundary work

All three projects compile the canonical `Game\*.cs` and `Net\*.cs`
sources directly into their own assembly via `<Compile Include>` (with
`<Link>` for path display). There's no inter-assembly reference, so
`internal` access from `ServerHub` to `World` works as in-assembly
internal access. If we ever do split into separate assemblies, we'd
either need `[InternalsVisibleTo]` or to widen specific surfaces to
`public`.
