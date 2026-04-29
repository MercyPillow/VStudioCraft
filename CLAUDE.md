# CLAUDE.md — Multiplayer feature notes

This file used to track the multiplayer feature (feature 50) phase by
phase. Phases 1 → 8 + all of 6 (inventory, click protocol, windowed
inventories, chest/furnace/crafting + their multi-friend sync) are now
shipped — what remains is a short list of small open items, a small
"deferred-by-design" backlog, and architectural notes worth preserving.

The original detailed plan lives in
`~/.claude/plans/do-an-analysis-of-abundant-crescent.md` if you need
the full historical record.

## Project layout

```
src/
  VStudioCraft/                 # VSIX extension (canonical home of Game\ + Net\)
    Game/                       # Simulation + renderer + audio
    Net/                        # Wire protocol + sessions
    UI/GameHostControl.xaml.cs  # WPF host with the render-thread loop
  VStudioCraft.Standalone/      # WPF console app — links Game\ + Net\
  VStudioCraft.Server/          # Headless dedicated server
    Program.cs                  # 20 Hz tick loop, --selftest, --port, --seed,
                                # --world, --autosave, admin console
    (links Game\ + Net\ from the VSIX project)
```

The Server uses the WindowsDesktop SDK with both `<UseWindowsForms>true`
and `<UseWPF>true` so InputState / BlockTextures / AudioEngine /
AnimatedBackground all compile via the linked `Game\*.cs` glob — those
modules are inert at runtime because Server.Program never instantiates
GameRenderer or calls `AudioEngine.Initialize`.

## Build commands

```powershell
# Full solution build
& "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" VStudioCraft.sln /t:Build /v:minimal /nologo

# Just the headless server (faster iteration)
& "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" src\VStudioCraft.Server\VStudioCraft.Server.csproj /t:Build /v:minimal /nologo
```

## Server smoke tests

```powershell
# Single-client login, chunk burst, place + dig roundtrip
src\VStudioCraft.Server\bin\Debug\net472\VStudioCraft.Server.exe --selftest

# Two-client entity replication: spawn, move, despawn
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

### Open to LAN

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

The pause menu has an `OPEN TO LAN` / `CLOSE LAN` button (label flips on
state). When hosting, a green chip top-right shows `HOSTING :PORT  N PLAYERS`.

## Open work

(none — multiplayer feature shipped end-to-end. Backlog items below
are explicitly deferred-by-design.)

## Backlog

Things that are known to be missing/imperfect but explicitly NOT
issues for the current cooperative-LAN design point. Park here so they
don't clutter the live KI list, but stay findable if the design
evolves.

### Server-side player physics validation (anti-cheat)

Server trusts client-reported position. A modified client could send
positions implying flight, noclip, or arbitrary teleportation and the
server would accept all of it (within the 6-block reach tolerance for
dig/place but otherwise unrestricted).

This is an anti-cheat hardening item. The MP demo works fine for
cooperative LAN play (the originally chosen design point) — there's
no incentive to cheat against your friends, and trusted offline
clients on a trusted LAN don't need server-side validation.

**Promote to live KI when**: opening to internet-facing hosting, or
running a server where players might be motivated to cheat.

**Implementation sketch (when revived)**: server-side `Player`
entity per `ServerClient` with the same AABB-vs-block integrator the
standalone uses (`Entity.IntegrateMotion`). Compare inbound positions
against `last + maxStep`, snap back via the already-reserved
`PlayerPosLookCorrect` packet (0x11) on outliers.

### RMB drag-deposit across multiple slots

Current friend-side RMB drag across slots in the inventory panel
deposits a single item into the first slot only. Real Alpha streams a
right-click event per slot the cursor crosses while held; replicating
that means a small per-tick loop on the client that emits one
`InventoryClick(button=RMB)` per newly-touched slot, with the existing
server-side handler doing the rest.

Low impact (most clicks are LMB or shift-click), so parked.

### Server-side bow draw + bucket use + fishing rod cast

`PlayerUseItem` (0x43) currently dispatches on the friend's held
hotbar item: snowball/egg → spawn `ThrownProjectile`. Bow needs draw-
charge state (the server tracks press/release timing and computes
arrow muzzle velocity from the held duration). Bucket needs a
target-cell raycast (place water/lava, or scoop from a fluid cell).
Fishing rod needs a target-cell raycast for the bobber drop.

All three are mechanical extensions of the existing `PlayerUseItem`
handler — none of them blocked on protocol changes.

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
where most play is loopback or LAN. Entity interp makes other players'
movement look smooth despite this; the local player's own movement runs
predictively (camera tracks input directly) but position authority still
lives server-side.

### Why big-endian wire format despite no Alpha-client compat

Hex-dumped packet bytes read in the same order as the C# field
declarations, which makes handshake bugs trivial to spot by eye in
Wireshark. Costs ~0 perf on x86/x64 (one `bswap` per primitive). The
Alpha-shape table at the top of the original multiplayer plan reads
directly as the `PacketIds` source.

### Why `internal` types crossing the Net/Game/Server boundary work

All three projects compile the canonical `Game\*.cs` and `Net\*.cs`
sources directly into their own assembly via `<Compile Include>` (with
`<Link>` for path display). There's no inter-assembly reference, so
`internal` access from `ServerHub` to `World` works as in-assembly
internal access. If we ever do split into separate assemblies, we'd
either need `[InternalsVisibleTo]` or to widen specific surfaces to
`public`.

### Open-to-LAN host as phantom client

The host has no socket, but `BroadcastEntityUpdates` etc. iterate
`_clients`. `EnableLocalHost` adds a virtual `ServerClient` whose
`NetSession` is `IsLoopback=true`; `Send` is a no-op, viewer-side
broadcast loops `continue` on `IsLoopback`. The host shows up to other
clients as a real player (entity spawn, move, despawn) without any
special-case branches in the broadcast paths.

`UpdateLocalHostPose` is called from `RenderLoop` each frame to keep the
phantom client's `LastReportedX/Y/Z` fresh; `Tick()` (network-only)
runs at 20 Hz from the same RenderLoop alongside the existing SP
simulation, while `SimulateTick()` (mob AI / fluid / crops) is only
called by the dedicated server's `Program.RunTickLoop` since SP path
already runs equivalent passes locally.

### Save format extension idiom

Every save-format version bump appends a new trailing block at the end
of the file. Pre-v(N) readers stop at EOF before the new block; v(N)+
readers parse the new section. This keeps backward compat without
needing per-version branches in the existing reader code. Current
version is **v13** (multiplayer player table appended after the v12
time-of-day float); see `WorldSaveFormat.CurrentVersion` for the live
value.
