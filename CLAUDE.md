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

## End-to-end multiplayer demo

```powershell
# Terminal 1 — dedicated server
src\VStudioCraft.Server\bin\Debug\net472\VStudioCraft.Server.exe --seed=4242

# Terminal 2 — Standalone client
src\VStudioCraft.Standalone\bin\Debug\net472\VStudioCraft.Standalone.exe --connect localhost:25566:tester
```

`--connect` accepts `host`, `host:port`, or `host:port:username`. Port
defaults to 25566 (one above Notch's 25565); username defaults to
`$env:USERNAME`.

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
  - [ ] **5c** — Drops (`DroppedItem`) replication, server-side death-drop spawning, item pickup on player AABB
  - [ ] **5d** — Projectile replication (Arrow, ThrownProjectile/Snowball/Egg, Bobber) and `PlayerUseItem` (0x43) RMB-intent packet
  - [ ] **5e** — Damage delivery: `EntityHealth` packet, server-side `Player` health tracking, hurt-flash sync
  - [ ] **5f** — Mob/drop interpolation polish (currently snap-to-position; want lerp like RemotePlayer)
- [ ] **Phase 6** — Inventory click protocol + server-side recipes + tile-entity sync
- [ ] **Phase 7** — Integrated server for SP (in-process loopback)
- [ ] **Phase 8** — Persistence v10 (per-username state) + admin console + autosave

## Known issues / follow-ups

### KI-1 — Tick lag spike on first connect

**Symptom**: When the first client joins a fresh server, the server logs
`Tick lag 1021 ms — resetting pacer.` immediately after, and `sim_max`
spikes to ~120 ms for two ticks.

**Cause**: Server boots with only the InitialRadiusChunks (5×5 = 25)
chunks generated. A new client needs the 13×13 spawn-view-radius window
(169 chunks). The 144 missing chunks are generated on-demand inside
`ServerHub.SendChunk` via `TerrainGenerator.Generate +
LightCalculator.RecomputeChunk`, which is ~10–20 ms per chunk. Combined
with the 5-chunks-per-tick send pace, the first ~10 ticks each pay the
full per-chunk gen cost.

**Fix options**:
1. Pre-generate the spawn 13×13 ring at server boot (during `World
   .Generate`). Adds ~1.5 s to startup, removes the join spike entirely.
2. Move chunk gen onto a worker thread (mirror the client's
   `ChunkJobSystem`). Sends start delayed by ~50 ms but tick stays at
   20 Hz throughout.

**Ranking**: option 1 is simpler and the right choice for now — startup
time is one-shot, join lag affects every player.

**When to do it**: Phase 3 (touching server-side world streaming anyway)
or as a small PR before Phase 4. Not blocking.

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

### KI-7 — Mob replicas snap-to-position (no interpolation)

**Symptom (Phase 5a/b)**: Replicated passive + hostile mobs visibly
stutter at the server tick boundary (every 50 ms). Walk cycle still
runs because PassiveMob.Update is called on the client too — wait, no
it isn't. Check.

**Cause**: client's `EntityRelMove` handler writes `mob.Position +=
delta` directly. With ticks at 20 Hz and rendering at 1500+ fps, the
mob teleports by ~5cm every 50ms instead of moving smoothly.
Compare to `RemotePlayer` which lerps over a snapshot pair.

**Fix (Phase 5f)**: factor out the `RemotePlayer` snapshot/interp
machinery into a generic helper that can wrap a `PassiveMob` /
`HostileMob`. Either store snapshot pairs sidecar-style in a
`Dictionary<int, EntityInterpState>` on `GameRenderer`, or thread
prev/curr fields directly through `Entity`. Sidecar is less invasive
and matches the way `RemotePlayer` already lives separately from
the player class.

### KI-4 — No reconnect / error UX on broken socket

**Symptom**: If the server drops mid-session (process kill, network blip),
`DrainNetwork` notices `IsConnected==false`, sets `_netClient=null`, and…
the user is left staring at a frozen world replica with no error message.

**Cause**: No "session lost" event surfaced to the host.

**Fix**: Add a `Disconnected(reason)` event on `GameRenderer`,
subscribed by `MainWindow` to show a dialog and route back to the menu.
Low-priority polish; do alongside Phase 7 (when integrated SP makes
abrupt disconnects rarer in the common case).

### KI-5 — Chunk gen on demand can race with `_world.GetChunk`

**Symptom**: None observed yet, but the path
`GetChunk → null → new Chunk → TerrainGenerator.Generate →
InstallGeneratedChunk` in `ServerHub.SendChunk` runs on the tick thread
while the World's `_chunks` is a `ConcurrentDictionary`. Concurrent
streams could double-generate the same chunk.

**Cause**: No "chunk generation in flight" guard server-side.

**Fix**: Track in-flight chunk gens in a `HashSet<(int,int)>` keyed on
chunk coords; `SendChunk` becomes idempotent. Folds naturally into
KI-1's worker-thread fix if we go that route. Address before Phase 4
when multiple clients stream concurrently.

### KI-6 — `--connect` can't reach a private LAN host without explicit IP

**Symptom**: `--connect localhost` works; `--connect ServerMachine`
might not depending on local DNS / hosts file.

**Cause**: `TcpClient.BeginConnect(string, int, ...)` uses standard
hostname resolution; this isn't actually a bug, just a docs/ux note for
when we add a proper "Connect to Server" dialog in Phase 7+.

**Fix**: Document. Eventually: an IP-or-hostname text field with format
hints in the connect dialog.

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
