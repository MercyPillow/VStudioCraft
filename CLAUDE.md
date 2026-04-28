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
- [x] **Phase 5** — Mob/drop/projectile replication
  - [x] **5a** — Passive mob replication (Pig/Cow/Sheep/Chicken). Server ticks AI, broadcasts spawn/move/despawn; client adds replicas to `_world.Passives` so the existing `RenderPassives` path draws them. Snap-to-position (no interp) — visible 50ms tick stutter is the first 5b polish item.
  - [x] **5b** — Hostile mob replication (Zombie/Skeleton/Spider/Creeper). Same shape; server uses closest-player as aggro target via `ClosestPlayerPosTo`. `NoopServerSinks` for `IPlayerDamageSink`/`IDropSink` so mobs simulate but don't deliver damage or drops yet (Phase 5c).
  - [x] **5c** — Drop replication: ✅ host's `_drops` list diff'd each tick by `BroadcastLocalDropsDiff`, friends see new drops via `ItemSpawnPacket` (0x27) + RelMove + Despawn. New `EntityType.DroppedItem`. ❌ Pickup-by-remote-players deferred to Phase 6 (needs inventory sync).
  - [x] **5d** — Projectile replication (host→friend): ✅ Arrow / Snowball / Egg / Bobber lifecycle replicated via new `ProjectileSpawnPacket` (0x28) + existing RelMove/Despawn. `EntityType.Arrow/Snowball/Egg/Bobber` constants. Generic `BroadcastLocalProjectilesDiff` runs alongside the drops diff each hub tick. Friend's `_arrows`/`_thrown`/`_bobbers` lists populate from inbound packets so existing render paths draw them. ❌ Friend → host RMB intents (`PlayerUseItem`) NOT yet shipped — friend's snowball/egg/bow press still no-ops because friend has no inventory state on the server (Phase 6).
  - [x] **5e** — Damage delivery (host→friend mobs): ✅ `EntityHealth` packet (0x26) ships when a tracked mob's health decreases on the host. Friend writes new health to their replica + bumps `HurtTimer` so the existing render hurt-flash lerp triggers. ❌ Server-authoritative player health (host's player getting hit by mobs, broadcasting to other players) deferred to alongside Phase 6 inventory.
  - [x] **5f** — All replicated entities now interpolate. Original mob path used `_mobInterpById`; widened to `_entityInterpById` and now covers drops + arrows + thrown projectiles + bobbers too. `EntityRelMove` feeds `EntityInterpState.ApplyRelMove`; per-frame writeback keys on which replica dict owns the eid (mobs get position+yaw, drops/projectiles position-only). 100 ms render-behind buffer matches `RemotePlayer` so a player walking past their own arrow tracks visually consistently.
  - [x] **5g** — ~~Walk-cycle phase for replicated mobs~~ — turns out moot; passive mobs (Pig/Cow/Sheep/Chicken) don't have walk-cycle animation in either SP or MP. Their legs are static cuboids. Closing without code change.
- **Phase 6** — Inventory + recipes + tile entities
  - [x] **6a** — Server-authoritative inventory + friend drop pickup. New `InventoryUpdatePacket` (0x51, single-slot) sent on login (full 49-slot prime) and on each pickup. `ServerClient.ServerInventory` is mutated by `ProcessFriendDropPickups` which scans (host's `_drops` × in-game friends) for AABB overlap, calls `Inventory.TryAdd`, ships updates, and despawns drops the host should remove. Friend-side handler writes the inbound slot into `Input.Inventory.Slots[]` so the existing inventory panel + hotbar render see it.
  - [x] **6b** — Friend → host action intents (minimal subset shipped):
    - [x] `PlayerHeldSlot` (0x44) — friend's hotbar selection synced via per-tick diff (no need to hook every 1..9 / wheel write).
    - [x] `PlayerDropItem` (0x45) — Q / Shift+Q intent. Server decrements `ServerInventory[hotbar+held]`, calls `SpawnDropHook` to add a `DroppedItem` to the host's `_drops`, ships InventoryUpdate. The drop then ships through the existing Phase 5c `BroadcastLocalDropsDiff` so the friend's own thrown drop appears via the same pipeline as host-thrown drops.
    - [x] `PlayerUseItem` (0x43) — friend RMB. Server resolves held slot; for Snowball/Egg it calls `SpawnThrownHook` which adds to the host's `_thrown` (broadcast via existing 5d projectile diff). Bow/bucket/fishing rod return silently (need charge state / target raycast — deferred).
    - [x] Full `InventoryClickPacket` (0x50). Friend's `HandleInventoryClick` does the local hit-test (server doesn't know screen layout), sends slot+button+shift, then `return`s without local mutation — server runs the same `Inventory.HandleLeftClickSlot` / `RightClickSlot` / `ShiftClickSlot` on `ServerInventory` and replies with a full inventory + cursor burst. `InventoryUpdate` (0x51) now uses `Slot=0xFF` as the cursor sentinel; outside-click with cursor sends `Slot=0xFF` upstream and the server tosses it as a `DroppedItem` via `SpawnDropHook`. Drag-deposit (RMB drag across slots) currently lands a single right-click on the first slot — proper drag is a per-tick stream of right-clicks; deferred as a small follow-up.
  - [ ] **6c** — Tile entities + crafting + chests/furnaces. See [Feature: Phase 6c (windowed inventories)](#feature-phase-6c-windowed-inventories) below for the full design.
- [x] **Phase 7** — "Open to LAN" — host an in-process server alongside running SP world. Working end-to-end: `--openlan[=PORT]` on Standalone starts a SP world and binds a listener; remote clients can `--connect host:port:user` and join. See [Feature: Open to LAN](#feature-open-to-lan) below for the full design.
- [x] **Phase 8** — Persistence v13 (per-username MP state appended to existing single-player save format) + dedicated-server autosave + admin console
  - **Save format**: bumped `WorldSaveFormat.CurrentVersion` from 12 → 13. Trailing block contains a `(playerCount, [{username, x, y, z, yaw, pitch, health, heldSlot, 49 ItemStacks}])` tuple. Pre-v13 saves load with an empty player table; SP saves get a 4-byte 0-count block (negligible overhead). Single-overload `Save(path, header, world)` still works (passes null player list); new `Save(path, header, world, IList<PersistedPlayer>)` for MP. Loader returns 3-tuple via new `LoadWithPlayers(path)`; existing `Load(path)` wrapper preserves the old 2-tuple call sites.
  - **ServerHub**: `SnapshotPlayers()` returns the current `_clients` state for the v13 block. `InstallPersistedPlayers(table)` is consulted in the login handler — friend's username matches a saved record, server restores their position + inventory + held slot. Loopback host is excluded from the table because the host's pose comes through the existing v9 `Header.CameraPos` path.
  - **Open-to-LAN host**: `LoadFromFile` parses the player table and stashes it for the next `OpenToLan` call. `SaveToFile` calls `hub.SnapshotPlayers()` so a save mid-LAN-session captures friends' state. `CloseLan` clears the stash so a subsequent fresh world doesn't carry stale records.
  - **Dedicated server**: new `--world=<path>` (default `world.voxworld` next to exe) and `--autosave=<minutes>` (default 5). Boot path: load if file exists else generate; saved seed wins over `--seed=`. Autosave runs after each tick when the elapsed timer hits the interval; final save on Ctrl+C. All three save paths (autosave / admin / Ctrl+C) serialise through `_saveLock`.
  - **Admin console**: background-thread `ReadLine` loop with `list / save / kick <user> / stop / help`. Skipped when stdin is redirected (selftest scenarios) or when running with `--selftest*` flags.

## Known issues / follow-ups

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

## Backlog

Things that are known to be missing/imperfect but explicitly NOT issues
for the current cooperative-LAN design point. Park here so they don't
clutter the live KI list, but stay findable if the design evolves.

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

## Feature: Phase 6c (windowed inventories)

Read this before starting work on Phase 6c. It's the design spec for
chests / furnaces / crafting tables in the Open-to-LAN scenario.
Roughly **400–600 LoC** total; not blocked on anything (Phase 6b
extended is the prerequisite and shipped).

### Goal

Friends can interact with shared world tile entities — chest, furnace,
crafting table — exactly like the host already does in the SP path:
right-click to open, drag/click items in and out, close to commit.
State persists in the world so a chest filled by one player is full
when another opens it.

### What already exists

- `World.GetOrCreateChestEntity` / `GetOrCreateFurnaceEntity` —
  per-cell tile-entity storage; persisted in v7+ saves; survives
  chunk unload via the world-level dict.
- `ChestTileEntity` (27 slots), `FurnaceTileEntity` (3 slots + cook
  timers), `CraftingScreen` (3×3 grid + output, lives only on the
  host's UI — there's no persistent crafting tile entity, the grid
  state lives on the open window).
- Host SP path handles all three via `TryInteract` / `InventoryScreen`
  — friend's `TryInteract` currently `return false`s in net mode for
  chests/furnaces and routes snowball/egg through `PlayerUseItem`.

### What's missing for friends

1. Way to open a window from a block click
2. Way to mirror the window's slots to the friend
3. Way to apply friend clicks to the window
4. Way to close the window and commit state

### Wire protocol additions

| ID | Name | Direction | Payload |
|----|------|-----------|---------|
| 0x52 | OpenWindow | S→C | `byte windowId`, `byte kind`, `byte slotCount`, `int x, y, z` |
| 0x53 | CloseWindow | both | `byte windowId` |
| 0x60 | TileEntityData | S→C | `byte windowId`, `byte slotCount`, `slotCount × ItemStack`, optional kind-specific tail (furnace cook progress + burn time) |

`kind` byte: 1=Chest, 2=Furnace, 3=CraftingTable. Reserved 0=PlayerInventory
(implicit window 0; not actually opened/closed via packet).

`InventoryClickPacket` (already 0x50) gains a new sentinel: `Slot >= 100`
means the click landed on the OPEN window's slot `Slot - 100`.
Implementation choice — I prefer a separate `WindowClickPacket` because
the slot-space differs and conflating them in one packet means the
server has to know "is window 1 open for this client" to disambiguate.
Either works; new packet is cleaner.

Recommended: extend `InventoryClickPacket` with a `byte WindowId` field
(0 = player inventory, 1+ = open window). Slots within each window are
local: chest window has slots 0..26 = chest contents, 27..62 = player
inventory (matches Alpha's layout convention where the player inventory
is appended at the bottom of every modal window).

Bump protocol version to 2 if this changes the existing `InventoryClick`
shape; otherwise add a new packet ID.

### Open / close lifecycle

1. Friend RMB on a Chest/Furnace/CraftingTable cell → `TryInteract`
   in net mode sends a new `PlayerInteractBlockPacket` (or extend
   existing `PlayerUseItem` with a target cell — better to add a new
   one because the semantics differ).
2. Server validates: cell exists, is a recognised window-bearing
   block, friend is within 6-block reach (same tolerance as Phase 3
   dig/place).
3. Server creates a `ServerClient.OpenWindow` state record:
   `{ WindowId, Kind, CellX/Y/Z, ServerSlots[] }`. WindowId is a
   per-client monotonic counter starting at 1 (window 0 is the
   implicit player inventory).
4. Server sends `OpenWindow(id, kind, slotCount, cell)` followed by
   `TileEntityData(id, slots, ...)` populating the initial state.
5. Friend's UI receives, opens the appropriate panel (chest, furnace,
   or crafting screen), populates from `TileEntityData`.
6. While open: clicks within the window slots use `InventoryClick`
   with `WindowId` set; player inventory clicks (slots 27..62 in
   the chest window) route to `ServerInventory` like today.
7. Server applies clicks to the right Inventory (chest's `Slots[]`,
   furnace's three slots, crafting grid) and ships back a fresh
   `TileEntityData` for window slots + `InventoryUpdate` for player
   slots.
8. Friend closes (Esc or click outside) → sends `CloseWindow(id)` →
   server commits any cursor stack to the player's main grid, drops
   leftover, removes the window record.

### Server-side state

- `ServerClient.OpenWindows` — `Dictionary<byte, OpenWindowState>` keyed
  on the per-client `windowId`. The state struct holds the cell
  coords (so close commits to the right tile entity) and a reference
  to the underlying `ChestTileEntity` / `FurnaceTileEntity`.
- `ServerClient.NextWindowId` — monotonic counter so a quick
  reopen-after-close gets a fresh id (avoids stale-window-id clicks
  landing in a recycled window).
- Crafting table: no persistent entity — the 9 input slots live on
  the `OpenWindowState` itself. On close, leftover input items get
  dumped into the player's inventory or dropped at the player's
  feet.

### Click semantics

Reuse `Inventory.HandleLeftClickSlot` etc. but pass the window's
backing `Inventory` instead of `ServerInventory`. The cursor stack
is shared across windows (same `ServerInventory.Cursor`) — picking
something up in a chest and clicking the player inventory drops it
there. This matches Alpha's behaviour.

For shift-click in a chest window:
- Shift-click in chest slot → move stack to player inventory
- Shift-click in player slot → move stack to chest

That's a tweak to `HandleShiftClickSlot` — currently the host's
implementation moves between hotbar ↔ main grid. Server-side override
or an extra parameter. Cleanest: add a `ShiftClickContext` enum (None,
ToContainer, FromContainer) parameter to the existing methods.

### Crafting

Recipes live in `CraftingRecipes.cs`. They're pure functions on a
`ItemStack[9]`. The window's input grid IS that array. After every
click that touches an input slot, server runs `CraftingRecipes.Match`
against the current grid; if a recipe matches, the output slot
contents become the recipe's result; otherwise the output slot is
empty.

Output-slot click consumes one of each input (the recipe's "consume"
function — already pure) and drops the result into the cursor.

### Furnace

Server already runs `TickFurnacesIfDue` in… wait — that's on
`GameRenderer`, not a hub. The dedicated server doesn't tick furnaces
today. Phase 6c needs:

- Move furnace tick into `World.TickFurnaces(dt)` so both SP and
  dedicated paths drive it.
- Server's `SimulateTick` calls it.
- When a furnace's cook progress changes, broadcast `TileEntityData`
  to any client whose window currently shows that cell.

### Estimated cost

| Piece | LoC |
|------|-----|
| New packets (OpenWindow, CloseWindow, TileEntityData, PlayerInteractBlock) | 100 |
| `ServerClient.OpenWindows` + `OpenWindowState` | 60 |
| Server-side dispatch on PlayerInteractBlock + open + populate | 80 |
| Server-side click routing (InventoryClick with WindowId) | 100 |
| Furnace move from renderer to World + server tick | 80 |
| Friend-side open/close window UI integration | 100 |
| Crafting table grid handling | 60 |
| **Total** | **~580** |

### Risks

1. **Click race**: Friend clicks slot, server processes, ships new
   state, friend's UI is mid-render with stale slot — may briefly show
   a flicker on slow networks. Mitigation: optimistic local update
   that gets overwritten by the server's authoritative reply (similar
   to how block-place doesn't predict on the client).
2. **Multiple players in the same chest**: Two friends open the same
   chest. Both see independent windows. They each see the chest's
   slot state at the time THEY opened. If A picks up an item and B
   sees it disappear from their copy, that needs a broadcast to all
   currently-open windows of that cell. Solution: chest state lives
   on the tile entity, server's click handler updates the entity in
   place, then broadcasts `TileEntityData` to every client with an
   open window pointing at that cell.
3. **Client disconnect mid-window**: Close-on-disconnect cleanup —
   commit cursor + crafting input to the player's inventory in the
   v13 player table at session end.

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
