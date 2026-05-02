# Features vs. Minecraft Alpha 1.1.2_01

Audit of the current VStudioCraft codebase (`src/VStudioCraft/Game`, `UI`,
`src/VStudioCraft.Standalone`) against Alpha 1.1.2_01 (released 2010-09-18).
Items marked **Have** exist today; items under **Missing** are the gap.

Last updated after Tier 3 #12 — Cow / Sheep / Chicken (the rest of
the Alpha passive mob roster), plus two follow-up fixes to the
hostile-spawn pipeline: (a) the live spawn loop now consumes a
time-of-day-modulated **sky-light subtraction** (0 at noon, ramping
to 11 at midnight via `GameRenderer.SkyDarknessSubtract`) when
gating hostile spawns, so surface hostiles actually appear at dusk
instead of the previous "every cell reads sky=15" lockout; (b)
both the chunk-gen seed pass and the live spawn loop now do a
**direct cave-Y sample** (random Y in [8, 56], gated on solid+2air
in place) alongside the topmost-solid surface scan — without this,
columns with no caves walked the cave probe down through air and
rediscovered the surface, so cave hostiles were never rolled. The
melee hit also gained a `|dy| ≤ 1.5` vertical reach gate (chase
stays XZ-only so mobs still aggro vertically, but the actual
attack now requires the player to be within ~1 body-length on Y),
fixing the report of a cave creeper landing hits up through the
rock floor.

The original `Pig` class refactored
into an abstract `PassiveMob` base (mirrors the `HostileMob` pattern)
that hoists wander AI + gravity + hurt-flash + per-instance RNG out
of every passive subclass; concrete subclasses tune `MaxHealth`,
`WalkSpeed`, hitbox dims, and per-mob death drops. **Cow** —
quadruped 0.9×1.4 hitbox, HP 10, walk 1.0, drops 0..2 **Leather (334)**
+ 1..3 Raw Porkchop (Alpha-era cows shared the pig drop; Raw Beef
wasn't added until Beta 1.8). **Sheep** — quadruped 0.9×1.3, HP 8,
walk 1.0, drops 1 Wool block on death (single white wool — colour
variants and shears arrived later). **Chicken** — small biped
0.4×0.7, HP 4, walk 1.6, drops 0..2 **Feather (288)** on death
and lays **Egg (344)** every 5..10 min via a per-instance
`EggTimer` ticked alongside the base wander (renderer threads
the `IDropSink` through the chicken-only `TickEggLay` so the
base `PassiveMob.Update` stays sink-free). `World._pigs` /
`Pigs` renamed to `_passives` / `Passives`; chunk-gen + live
spawn passes now do a 30/30/25/15 weighted draw across
Pig / Cow / Sheep / Chicken on every passive-eligible column.
Renderer dispatches per-type cuboid bodies through the existing
`_overlayShader` — cow gets a brown hide + pale underbelly +
white horns, sheep gets a wool torso + bare-skin head, chicken
gets a feathered torso with a yellow beak + red comb + two
visible legs.

Earlier: Tier 3 #11 — light-level-gated live spawn loop. On top
of the deterministic chunk-gen seed pass, `World.TickMobSpawns`
runs every 1 s and tops up the live mob population as the player
travels. Each tick: instant-despawn every mob >128 blocks XZ from
the player + 5%/tick stochastic despawn at 32..128 blocks (Alpha
"mob keep-alive radius"); enforce global caps (PassiveCap = 10,
HostileCap = 70 — Alpha defaults); for up to 12 random columns
drawn from chunks within 6 of the player, gate by per-chunk cap
(4) + min spawn distance (24 blocks) + surface/headroom/light,
then dispatch by light level (≤7 → weighted-draw hostile; ≥9 +
grass → weighted-draw passive; 8 is a no-spawn dead band).

Earlier: Tier 3 #10 — first hostile mobs. Abstract `HostileMob`
base extends `Entity` with shared chase-AI plumbing (aggro inside
`DetectRange`, atan2-yaw + normalised-XZ steering, 1-block
auto-jump, melee `TryAttack` gated by per-instance cooldown,
wander fallback). Four concrete subclasses: Zombie (HP 20, no
drops — Alpha 1.1.2 was drop-empty), Skeleton (HP 20, drops 0..2
**Arrow (262)** + 1-in-3 **Bow (261)** — combat deferred to Tier
4 #17), Spider (HP 16, walk 1.6, drops 0..2 **String (287)**),
Creeper (1.5 s fuse via `OnAttacked` override + `TickFuse`,
6 HP detonation, drops 0..2 **Gunpowder (289)** on player-kill).
`World.SpawnHostilesInChunk` seeds them at terrain-gen with a
1-in-360 hashed gate + light ≤ 7. Renderer dispatches per-type
cuboid rigs through `_overlayShader`. `IPlayerDamageSink` and
`IDropSink` shims decouple `HostileMob` from the renderer.

Earlier: Tier 3 #9 entity framework + first passive mob — abstract
`Entity` base hoists AABB sub-step physics out of `Player`; `Pig`
ships with wander AI, 10 HP + hurt-flash, terrain-gen spawn on
grass with light ≥ 9. LMB melee added with the Alpha damage table;
dead pigs drop 1..3 **Raw Porkchop (319)**, smeltable into
**Cooked Porkchop (320)** via a new furnace recipe.

Earlier: Tier 2 visible-polish pass — wall torches + torch-fall
(id-space orientation at BlockType 70..73 — chunk metadata isn't
persisted, so orientation lives in the id; 5-cell unsupported sweep
after every break), a 256-particle system reusing the existing
multi-face cube shader (block-break puffs / water splash / lava
bubbles / torch smoke, ambient emission at 10 Hz across a 6-block
sweep), first-person held-item rendering in the bottom-right (3-face
iso for cubes, flat sprite for tools/items/torches/flora) with a
sin(πt) arm-swing animation off `Player.SwingTimer`, and a 0.45 s
red hurt overlay driven by `Player.HurtTimer`.

Earlier: ItemType + ingredient-set pass — 9 non-block items (Stick,
Coal, Iron/Gold Ingot, Diamond gem, Flint, Clay Ball/Brick, Bowl)
ship as new BlockType entries 58..66 alongside an `ItemType`
static-class wrapper that exposes Alpha 1.1.2 numeric ids (263..341)
for save / network parity. `BlockData.IsItem` ranges the item slice
the same way `IsTool` does; renderers, mesher, placement, and
inventory all fall through identically (non-solid, non-cube,
non-opaque, flat-sprite icon, RMB rejected). Coal Ore now drops Coal,
Diamond Ore drops the Diamond gem, Gravel has a 10% Flint chance,
Clay drops 4 Clay Balls. Item icons live alongside the tool icons in
the embedded `alpha_tools.png` at canonical Notch coordinates (Coal
(7,0), Iron Ingot (7,1), Gold Ingot (7,2), Diamond (7,3), Flint
(6,0), Clay Brick (6,1), Stick (5,3), Clay Ball (9,3), Bowl (7,4))
— the alpha-textures atlas slices them out in the same loop that
already handles tools, while procedural mode synthesises 16×16
pixel-art equivalents.

Earlier: tools (wood / stone / iron / gold / diamond × pickaxe /
shovel / axe / sword) ship as 20 BlockType entries (38..57) sourced
from a base64-inlined `alpha_tools.png`. Mining is now hardness ×
tool-speed gated, with RequiredTier drop gating, durability decrement
on break, and a 3-stop green→yellow→red durability bar drawn across
hotbar / inventory / cursor. Creative-inventory panel grew by the
search-bar zone so all 4 catalog rows are fully visible (was
clipping at ~3.5 rows).

---

## Blocks

**Have** — 41 block IDs (`BlockType` enum)
- Air, Grass, Dirt, Stone, Sand
- Cobblestone, Bedrock, Gravel, Clay
- CoalOre, IronOre, GoldOre, DiamondOre, RedstoneOre
- WoodLog, Planks, Leaves
- Water (source) + FlowingWater (spread cell, water-tinted) — both transparent, share fluid family for face culling
- Lava (source) + FlowingLava (spread cell) — shares the water tick code path verbatim (same reach, same drain rules); emission left at 0 so a flowing lava stream doesn't force per-tick relights and stutter the render thread
- GoldBlock, IronBlock, DiamondBlock
- Bricks, TNT, Bookshelf, MossyCobblestone, Obsidian, Sponge, Wool
- Glass — alpha-tested cube (binary alpha): non-opaque so neighbours' faces against it survive, internal glass-vs-glass faces deliberately retained so a stack of glass shows the inner pane (otherwise the stack would collapse into a hollow shell). Routes through the opaque stream's `discard` shader (alpha < 0.5) rather than the alpha-blended transparent stream — correct technique for binary-alpha textures, no back-to-front sort cost. Light propagates through it (`IsLightTransparent`).
- Torch — floor + wall placement, emits 14 block-light, cross-sprite model, alpha-tested. Wall orientation lives in the BlockType id-space (TorchEast / TorchWest / TorchSouth / TorchNorth at ids 70..73) rather than per-cell metadata, since chunk metadata isn't persisted; placement reads the raycast hit's face normal and routes to the matching variant (a hit on a +X face places a TorchEast etc., a +Y floor hit places the regular Torch, a -Y ceiling hit is rejected). The mesher emits a tilted billboard for wall variants — base offset 0.45 toward the wall + lifted 0.2, tip offset 0.05 from cell centre + lifted 0.9 — using a depth axis from the cross product of the shaft and width vectors so two perpendicular cross-sprite planes flicker correctly. Breaking any wall variant drops a generic Torch (`BlockData.DropFor`) so the player only ever sees / holds one torch item. **Torch-fall**: after every break that removes a block, the renderer scans the 5 cells that could have a torch leaning on the broken cell (the cell above + four horizontal neighbours pointing inward) and pops any unsupported torch into a drop. Bounded recursion is safe because torches don't act as supports themselves.
- Dandelion, Rose, BrownMushroom, RedMushroom — cross-sprite flora, non-collidable, raycast-targetable, light-transparent, scattered on grass during terrain gen
- Crafting table (workbench) — planks-base block with a 3×3 grid texture on top and tool-silhouette sides; RMB opens the 3×3 crafting screen (`TryInteract` → `_isCraftingOpen`); axe-required tier; drops itself when broken
- Furnace + LitFurnace — paired blocks at IDs 67/68 with stone-cap top, iron-banded stone-wall sides, and a recessed-mouth front face (lit variant adds an orange/yellow glow inside the mouth). Crafted from 8 cobblestone in a U-shape on the crafting bench. RMB opens the 3-slot smelter screen (input / fuel / output) overlaid on the player inventory; `FurnaceTileEntity` carries the per-block input/fuel/output stacks plus burn / cook timers and a cardinal `Facing` byte. The world ticks every entity at 20 Hz: fuel drains 1 tick/tick (Coal 1600 = 80 s, Wood 300 = 15 s, Stick 100 = 5 s), the cook-progress counter advances while burning + smeltable, and at 200 ticks (10 s) one input → one output. Entity transitions between burning and idle automatically swap the world block between Furnace and LitFurnace, so the lit-front glow is observable from outside without needing the screen open. Smelting recipes match Alpha: IronOre→IronIngot, GoldOre→GoldIngot, Sand→Glass, Cobblestone→Stone, ClayBall→ClayBrick, RawPorkchop→CookedPorkchop. Breaking a furnace spills any contents as `DroppedItem`s and removes the entity. Persisted in the world save format (v6) with a count + per-entity (x, y, z, input, fuel, output, burnTime, maxBurnTime, cookProgress, facing) record. **Directionality**: at placement the front face is oriented to face the player who placed it (cardinal opposite of `Camera.Forward`); the mesher reads `FurnaceTileEntity.Facing` and routes that one side to the front tile while the other three sides show the plain side tile. The furnace-top tile is missing from the embedded `terrain.png` slot Alpha used (14, 3), so the atlas is patched at upload time to procedurally generate it from the side palette (stone-wall jitter + iron banding wrapped around all four edges + a 4×4 vent in the centre)
- Chest — single-chest container at ID 69 with a planks-base block, iron-banded oriented front (lock plate + keyhole), plain plank sides, and a lid-plate top tile. Crafted from 8 planks in a U-shape on the crafting bench (centre slot empty, matches Alpha 1.1.2). RMB opens the chest screen (`ChestScreen`) — a 9×3 chest grid stacked over the player's main+hotbar inventory, same world-halt + cursor-release lifecycle as the inventory / crafting / furnace modals. `ChestTileEntity` owns a flat 27-slot `ItemStack[]` plus a cardinal `Facing` byte; the world keeps a `Dictionary<(x,y,z), ChestTileEntity>` so chest contents survive chunk unload/reload. Click handling routes through `HandleChestClick`: chest slots use the same Alpha pick / drop / swap / merge rules the player inventory uses (cursor stack is shared); shift-click on a player slot top-ups matching chest stacks then fills the first empty chest slot. Breaking a chest spills every non-empty slot as `DroppedItem`s in row-major order (top row first) plus the chest block itself, then removes the entity and auto-closes the screen if it was open. **Directionality**: at placement the front (locked) face is oriented to the cardinal opposite of `Camera.Forward` so it always faces the placer; the mesher reads `ChestTileEntity.Facing` and routes that one side to the front tile while the other three sides show the plain plank-side tile. Persisted in the world save format (v7) with a count + per-entity (x, y, z, facing, 27 ItemStacks) record appended after the furnace block — pre-v7 saves load with an empty chest table and zero chest blocks (the BlockType range simply wasn't populated before this version).
- Per-face textures (grass top / side / bottom; log top/side; TNT top/bottom/side; bookshelf; crafting-table top/side; furnace top/side/front + lit-front variant — front is selected per-face based on `FurnaceTileEntity.Facing`; chest top/side/front — front is selected per-face based on `ChestTileEntity.Facing`)
- 38-block + 20-tool + 9-item + 9-tail-block (crafting-table top/side, furnace top/side/front/lit-front, chest top/side/front) + 9-tail-item (raw + cooked porkchop, bow, arrow, string, gunpowder, leather, feather, egg) procedural 16×16 pixel-art atlas, nearest-neighbour sampled
- Transparent-block routing to a second alpha-blended render pass (water today)
- Cross-sprite (X-shape) model path for non-cube blocks, alpha-tested in opaque pass via fragment-shader `discard`
- Per-block shape category (`IsCubeShape`) and raycast/collision separation (`IsRaycastTarget` vs `IsSolid`) so torches/flowers/grass are targetable but non-collidable

**Missing**
- Ice (slippery, melts in light)
- Snow layer + snow block
- TNT priming on activation (block exists but is inert)
- Ladder
- Sugar cane / reeds
- Cactus (damages on contact)
- Pumpkin + jack-o'-lantern
- Tall grass / flower / mushroom block-tick removal when the grass below is broken (today the sprite stays floating)
- Falling sand / gravel physics (unsupported sand or gravel should convert to a falling-block entity, fall under gravity, and resettle as a placed block)
- Sponge actually absorbing nearby water (Alpha quirk — currently sponge is an inert block)
- Fluid side faces against glass or other non-opaque non-fluid blocks still use the cube sweep (full height). Only air-facing sides are replaced by the custom trapezoid; glass-adjacent water still shows a full-height side face at that boundary. Rare edge case, acceptable for V1.
- Water-meets-lava block formation (cobblestone / stone / obsidian)
- Water/lava textures animated (current tiles are static)
- Fire (spreads and dies)
- Stone & wooden slab (single + double)
- Stone & wooden stairs
- Fences
- Wooden door, iron door
- Sign (post + wall)
- Mob spawner (cage with flame)
- Redstone wire, redstone torch, button, lever, pressure plate
- Dispenser

## World generation

**Have**
- 4-octave Perlin heightmap (`Noise.Octaves`)
- Grass/dirt/stone layered columns with sand shoreline
- Beach logic pegged to sea level (`BeachHeight = SeaLevel + 1`)
- Sea level (`SeaLevel = 22`) with still-water fill of every air block ≤ sea level
- 16 × 128 × 16 chunks, matching Alpha's dimensions
- Chunk streaming with radius + unload hysteresis (view=6, unload=9)
- Modified-chunk retention so edits survive unload/reload
- Bedrock floor: y=0 always + ragged y=1..3 at ~60/40/20% per column
- Ore veins: Dirt, Gravel, Coal, Iron, Gold, Redstone, Diamond — Alpha-tuned Y ranges (Iron ≤64, Gold ≤32, Redstone/Diamond ≤16), random-walk placement, stone-only replacement
- Oak trees with canopy that overlaps across chunk seams (deterministic per-column RNG means same tree regardless of which chunk is generated first)
- Surface flora scatter: dandelion / rose / brown + red mushroom / tall grass placed on grass surfaces with a per-column hashed RNG (~1 sprite per 16 grass columns, weighted toward tall grass and flowers, mushrooms rare). Skipped on beach and over carved cave openings.
- Fluid simulation: source-driven outflow tick every 0.25s. Sources flow downward into Air at full reach, horizontally up to 7 cells (water, matching Alpha) or 3 cells (lava). Falling cells (those over an air gap) skip horizontal spread that tick — waterfalls stay narrow on the way down and only fan out at the base, which lets the wider 7-cell reach work without flooding columns. Per-cell 5-bit metadata (4-bit reach + falling marker) carries the flow state. Per-chunk `HasActiveFluid` flag self-deactivates steady-state chunks so a settled ocean costs nothing per tick; player edits re-engage the flag on the chunk + 4 neighbours. Light is recomputed only for chunks where lava propagated (water is light-transparent).
- Fluid drain on source removal: every tick each non-source flowing cell checks for a feeder (same-family fluid above OR a horizontal neighbour that is a source / non-falling flowing cell with strictly greater reach). No feeder → cell drains to Air. The drain wave advances one cell per tick, so breaking a source produces an Alpha-style receding puddle instead of a permanent footprint.
- Fluid level rendering: top-exposed flowing cells (non-source, non-falling, air directly above) emit a sloped top quad whose four corners take the max water height (`(reach+1)/8`, or 1.0 for sources / falling / column-filled cells) of the four cells meeting at that corner. Adjacent surface cells share the same four-cell set at their shared corner, so the lids meet flush and step-wise reach drops render as a continuous slope instead of a stair. Solid-block neighbours are ignored — the surface tracks only the fluid network, so a stone wall next to a half-depth flow doesn't pull the corner up to the wall ("clinging").
- Worm-style cave systems — each chunk scans a 5×5 neighbourhood for worm starts, so tunnels cross seams seamlessly. Parabola-tapered radius (1.8..3.6 blocks) with dampened yaw/pitch drift. Carves only stone/dirt/gravel/grass — bedrock, water, and sand (beaches) are preserved. Y-capped below `SeaLevel - 4` and below `surface - 5` per column so caves never break out to the surface or the ocean floor.
- Off-thread terrain generation via `ChunkJobSystem` worker pool (2–3 workers)
- Reproducible seed chain per chunk feature (per-chunk and per-column hashed RNG)

**Missing**
- Ravines (long vertical slashes — Alpha got them in Beta, but worth including)
- Dungeons (4×4 cobble rooms with spawner + 1–2 chests)
- Surface lava lakes and underground lava pools
- Water/lava springs embedded in cliff faces
- Snow/ice biomes (snow layer on top blocks, ice on water)
- Desert biomes (no trees, cacti)
- Pumpkin patches
- Sugar cane next to water
- Biome system (Alpha used Rainfall/Temperature maps via `OverworldGenerator`)
- World spawn-point selection (currently spawn is always at origin, high in the air)

## Lighting

**Have**
- Directional sun + ambient term in the fragment shader
- Sun follows a day/dusk/night/dawn piecewise angle on a 7-minute cycle
- Sky colour interpolates between day/dusk/night
- 4-bit sky + 4-bit block light per block (packed byte per cell, parallel to the block array)
- Sky-light propagation: column top-down seed at level 15 down to first opaque block, then BFS lateral spread (Alpha-style 15-level flood fill)
- Block-light propagation: lava emits 15, BFS fills outward through air/water/glass/leaves
- Per-vertex light baked into the chunk vertex stream (10th float, packed `sky*16 + block`); greedy mesher key includes light so quads don't merge across light boundaries
- Re-light on block place/break (full-chunk recompute on the affected chunk + neighbours when the edit touches a border)
- Day/night attenuates outdoor sky contribution (`uSkyLightLevel`: 1.0 at noon, ~0.18 at midnight) while indoor block-light stays constant
- Block-light is warm-tinted (torch/lava amber) vs. sky-light's neutral white so light sources read distinct
- Per-axis face shading (top 1.0 / sides 0.78 / bottom 0.55) preserved on top of the per-cell light

**Missing**
- Smooth lighting / vertex-AO (currently flat-lit per quad — corners aren't sampled separately)
- Cross-chunk light propagation (today each chunk lights independently; small seams resolve when both sides relight, but a true "light leaks across chunk borders" pass would eliminate them)
- Incremental relight (Alpha's "decreased + increased" queue pair) — currently we full-recompute the chunk on every edit
- Underwater light attenuation (water passes light losslessly today; Alpha attenuates a few levels)
- Glowstone, fire, jack-o'-lantern as block-light sources (Torch emits 14; Lava is intentionally non-emissive — see FluidTick header)
- Persisted light (currently recomputed deterministically on load — saves space but costs ~1 ms per chunk on world entry)

## Sky / weather

**Have**
- Flat sky colour that shifts with sun angle
- Day/night cycle animates `uSunDir` every frame
- Sun and moon procedural billboard sprites (camera-aligned quads on a 100-unit celestial sphere; fade smoothly around horizon)
- Craters on the moon (fixed pattern — no real phases yet, just a static face)
- Star field — 500 procedurally scattered points on the celestial sphere, fade in at dusk and out at dawn, rotate with sky
- 2D cloud plane at y=108 (32×32 grid, wind-driven UV scroll, fog-tinted, tinted dusk/night by sun altitude)
- Distance fog — 48-block falloff band ending just inside the unload radius, colour matches current sky so chunks blend instead of popping

**Missing**
- Real moon phases (8-frame texture sheet cycled across game-day count)
- Horizon gradient / "void fog" (sky is still a single flat clear-colour)
- Rain / snow particles + weather cycle
- Lightning flashes
- Alternate biome sky tints

## Entities & mobs

**Have**
- Abstract `Entity` base class shared with `Player` — owns the AABB sub-step integrator (`MaxSubStep = 0.05f`), axis-by-axis voxel collision (`MoveAxis`), `IsStandingOnSomething`, and the world-collision check. `Player` derives from it (subtype-specific `HalfWidth`/`Height` inherited as virtual instance fields, kept as `public new const` static aliases for external static-style refs). Future entities (mobs, projectiles, vehicles) plug in by subclassing.
- **Pig** — first concrete mob. 10 HP, 0.45 × 0.9 footprint, 1.2 m/s walk speed. Wander AI on a 5 s timer (60% walk / 40% idle, random yaw on each interval) with the entity-base physics (gravity 28 m/s², terminal 78, full ground collision against the voxel world). Hurt timer (0.30 s) tints the body texture from healthy pink (0.96, 0.55, 0.65) to red (1.00, 0.30, 0.30) on `TakeDamage`. Spawns during chunk terrain-gen — per-column hashed RNG with a 1-in-180 gate scans for grass-topped columns with 2 blocks of headroom and either sky-light ≥ 9 or block-light ≥ 9 (Alpha's passive-spawn light rule). Spawn fires both during the synchronous initial gen pass and on chunk install from off-thread workers (so worker-thread mutations don't race with `World`).
- Pig render — procedural cuboid model (body, head, snout, four legs) drawn through the existing `_overlayShader` + `_breakCubeMesh` pipe with per-cuboid solid-colour `uColor`; cheap, no new shader or mesh, and the swung-rig pivot follows the entity's yaw + position.
- **Hostile mobs — Zombie / Skeleton / Spider / Creeper.** Shared `HostileMob` abstract base extending `Entity` owns the chase plumbing (atan2 yaw, normalised XZ steering, single-block auto-jump on a 1-block lip, melee `TryAttack` gated by per-instance `AttackCooldown`, wander fallback outside `DetectRange`). Each subclass tunes HP / walk speed / attack range+damage / drops. Spawn during chunk terrain-gen via `World.SpawnHostilesInChunk` (per-column 1-in-360 hashed RNG; gate at light ≤ 7 — Alpha hostile rule; weighted draw 35% Zombie / 25% Skeleton / 25% Spider / 15% Creeper). Hostile vs. player damage routes through the new `IPlayerDamageSink` shim (`GameRenderer` implements directly + null-guards `Player.TakeDamage`); death drops route through `IDropSink.SpawnDrop` so per-mob `SpawnDeathDrops` can append items without a hard ref to the renderer. **Zombie** — humanoid, HP 20, walk 1.0 m/s, dmg 2, no drops (Alpha 1.1.2 zombies dropped nothing — rotten flesh was Beta). **Skeleton** — humanoid, HP 20, walk 1.1, dmg 2 (melee-only for V1; bow combat is Tier 4 #17), drops 0..2 Arrow + 1-in-3 Bow. **Spider** — short+wide arachnid (HalfWidth 0.7, Height 0.9), HP 16, walk 1.6, dmg 2, drops 0..2 String. (Wall-climb deferred — vertical pathing slated for the path-planner overhaul.) **Creeper** — slim humanoid silhouette, HP 20, walk 1.05, no melee. First contact starts a 1.5 s fuse via `OnAttacked` override; `TickFuse` advances the timer independently from chase (so the fuse keeps counting down even if the player walks slightly out of detect range), defuses if the player retreats past 2× `AttackRange`, detonates for a flat 6 HP hit on the player + sets `Health=0` + `DetonatedThisFrame`. Player-kill drops 0..2 Gunpowder; explosion-death drops nothing (matches "explosion eats the corpse" Alpha behaviour). Real blast-block damage is roadmap-deferred to Tier 8 #43.
- Hostile mob render — same procedural-cuboid pipe pigs use (`_overlayShader` + `_breakCubeMesh`). Type-dispatched in `RenderHostiles`: humanoid head+torso+arms+legs for zombie/skeleton, low oval body + cephalothorax + 8 stubby legs + two red eyes for spider, tall slim torso + 4 short legs + small head for creeper. Hurt flash lerps the body colour toward red over `HostileMob.HurtFlashSeconds` (0.30 s). Creeper additionally lerps body colour from green toward white as `FuseTimer` counts down — the white peak hits the frame before detonation.
- LMB melee on mobs — `TryHitMob` runs before `TryBreak`'s block hit so a mob in front of the cursor takes priority. Damage table (`MeleeDamageForHeldItem`) follows Alpha 1.1.2: bare-hand 1; sword wood 5 / stone 6 / iron 7 / gold 5 / diamond 8; axe -2 from sword tier; pickaxe -3; shovel -4; clamped to ≥1. Scans pigs and hostiles together, picks the closest by ray-AABB distance, blocked by walls. Held tool's durability ticks one use per hit (`DamageHeldTool(1)`). Arm-swing fires (`Player.TriggerSwing`) and the place SFX bank's wool-thwack stub plays as a placeholder mob-hit cue. Death drops route per-mob: pigs → 1..3 Raw Porkchop, hostiles → their `SpawnDeathDrops` implementation, both through the `DroppedItem` pickup loop.
- Dropped-item entity — block + mob drops bob, spin, fall under gravity, settle, and get picked up; one entry per `_drops` list entry, ticked in `TickDrops`.
- **Live mob-spawn attempt loop** (Tier 3 #11). Layered on top of the chunk-gen seed pass — `World.TickMobSpawns` runs every `SpawnTickInterval` (1 s) and tops up the live mob population as the player travels. Each tick: instant-despawn every mob > `InstantDespawnDist` (128 blocks) XZ from the player + 5%/tick stochastic despawn at `StochasticDespawnDist..InstantDespawnDist` (32..128 blocks), enforce global caps (`PassiveCap` 10, `HostileCap` 70 — Alpha defaults), then for `AttemptsPerTick` (12) random columns drawn from chunks within `SpawnRadiusChunks` (6) of the player gate by per-chunk cap (`PerChunkCap` 4 — prevents stacking) + min spawn distance (`MinSpawnDistance` 24 blocks — Alpha never spawns next to the player) + surface/headroom rules. Light dispatch: ≤ 7 → weighted hostile draw (35/25/25/15 zombie/skeleton/spider/creeper, mirroring the chunk-gen mix); ≥ 9 with grass surface → weighted passive draw (30/30/25/15 pig/cow/sheep/chicken — see Tier 3 #12); light = 8 is a deliberate dead band (Alpha behaviour). World owns its own `Random` for the dice rolls; `GameRenderer.TickMobSpawns(dt)` is a one-line wrapper that hands the player position to the world and is gated by the same pause / modal logic as `TickPassives` / `TickHostiles`.
- **Cow / Sheep / Chicken — the rest of the Alpha passive mob roster** (Tier 3 #12). Refactored the original `Pig` class into an abstract `PassiveMob` base extending `Entity` that hoists wander AI + gravity + hurt-flash + per-instance RNG out of every passive subclass; concrete subclasses tune `MaxHealth`, `WalkSpeed`, hitbox dims, and per-mob death drops via the shared `IDropSink`. `World._pigs` / `Pigs` renamed to `_passives` / `Passives`; chunk-gen + live spawn passes now do a 30/30/25/15 weighted draw across Pig / Cow / Sheep / Chicken on every passive-eligible column. Renderer dispatches per-type cuboid bodies through the existing `_overlayShader`. **Cow** — quadruped 0.9 wide × 1.4 tall hitbox, HP 10, walk 1.0, drops 0..2 Leather (334) + 1..3 Raw Porkchop (Alpha-era cows shared the pig drop — Raw Beef wasn't added until Beta 1.8 / 2011-09-15, so the Alpha-1.1.2 cow drop is leather + raw porkchop). Body is a brown hide torso with a pale underbelly patch, two bone-white horns sticking forward from the head, and slightly thicker legs than the pig. **Sheep** — quadruped 0.9 wide × 1.3 tall, HP 8, walk 1.0, drops 1 Wool block on death (single white wool — colour variants and shears arrived later, so the Alpha-1.1.2 sheep just drops one default Wool). Body is a chunky off-white wool torso with a small bare-skin head sticking forward and four short skin-coloured legs. **Chicken** — small biped 0.4 wide × 0.7 tall, HP 4, walk 1.6 (skittish, faster than the quadrupeds), drops 0..2 Feather (288) on death and lays Egg (344) every 5..10 min while alive. Egg-lay is a per-instance `EggTimer` countdown ticked alongside the base wander; the renderer threads the `IDropSink` through a chicken-only `TickEggLay` so the base `PassiveMob.Update` signature stays sink-free. Body is an off-white feathered torso with a yellow beak + red comb on top, two stubby wings on the sides, and two skinny yellow legs underneath. Items shipped alongside the mobs: **Leather (334)** + **Feather (288)** + **Egg (344)** live as BlockType entries 80..82, extending the `BlockData.IsItem` slice to 74..82 (Egg is now the highest-id BlockType). Procedural-mode art ships per-icon (Leather = tan 9×8 hide rectangle with stitching, Feather = diagonal rachis with barbs, Egg = cream oval with highlight); atlas-mode coords are placeholder `(-1, -1)` sentinels for now so procedural always wins. Egg projectile behaviour is roadmap-deferred to Tier 4 #20.

**Missing**
- Mob AI / pathfinding — chase is straight-line steering + 1-block auto-jump (no A*); wander is a 5 s yaw-pick. No path queries, no real terrain navigation.
- Slime mob (the fifth Alpha hostile) — splits-on-hit semantics, low-Y light-independent spawn. Slated for Tier 4 #18.
- Spider wall-climb (Alpha spiders climb walls — vertical pathing is a bigger surface than the chase plumbing handles today)
- Density-curve / pack spawning — Alpha clusters spawns of the same kind in a 1..4-mob "pack" near the seed cell. Our spawn pass is one mob per attempt, which gives a flatter distribution than vanilla.
- Projectiles: arrow, snowball, egg
- Vehicles: minecart, boat
- Painting
- TNT-primed entity

## Player

**Have**
- AABB voxel-collision walker with sub-step integration
- Walk / jump / gravity / terminal velocity
- Mouse look (yaw+pitch, clamped)
- Block break + place via 8-block reach raycast
- Water and lava are both non-solid (you walk / fall through either cleanly), non-raycast-targetable, and replaced when you place a block into them. Both source variants are treated symmetrically with their flowing variants for collision, face culling, and light propagation.
- Swim physics: in water gravity drops to ~28% of normal, terminal speed clamps to ±3..4.5 m/s, horizontal velocity halves; Space accelerates upward at 22 m/s² so tap-tap-tap brings you back to the surface
- Camera bob while swimming — gentle vertical sway scaled by horizontal speed, eased back to zero on exit
- Health (20 HP = 10 hearts, Alpha-style) in survival mode
- Air supply (20 = 10 bubbles, depletes in 15 s of head-submerged time, refills instantly on surfacing)
- Fall damage (Alpha formula: `max(0, distance - 3)` half-hearts, cancelled if landing in water)
- Drowning damage (2 HP every 1 s once air runs out)
- Void damage (4 HP every 0.5 s below y=-16)
- Respawn on death (teleport to spawn, restore full HP + air)
- Hand-held item rendering in first-person — the currently-selected hotbar stack renders as a HUD-layer gizmo in the bottom-right corner (`RenderHeldItem`). Cube-shaped blocks use the same 3-face iso renderer as the inventory icons (`RenderBlockIcon3D`); tools, items, torches, and flora use the flat-sprite path (`DrawFlatSpriteIcon`). Empty hotbar slots render nothing. Drives the swing animation off `Player.SwingTimer`.
- Arm-swing animation — `Player.TriggerSwing()` resets a 0.30 s decay timer; the held-item gizmo applies a sin(πt) half-pulse pose that dips the icon down + slightly inward at peak and eases it back to rest. Triggered on every block-break attempt (`TryBreak`) and continuously while LMB is held — once the timer drains the next held-frame retriggers, so chopping a long-mining block animates the whole way through.
- Third-person Steve model + F5 toggle (Tier 3 #12) — `GameRenderer.ThirdPersonMode` flips the camera from first-person eye to a 3-block pull-back along `-Forward` (terrain-clipped via a 0.1-block step-march so the camera tucks against walls instead of poking through), and the renderer draws a Steve-coloured humanoid rig (skin-tone head/arms, cyan shirt, indigo pants, dark boots, brown hair, eye + mouth detail) at the player's feet. Walk-cycle phase advances on horizontal speed each frame; legs + arms pivot at hip / shoulder by `sin(phase) × WalkAmplitude × walkFrac`. Head pitches independently with the camera's vertical look angle (dampened to 0.8×) so Steve looks up + down without tipping the body. Right arm gets an extra forward-arc hit while `Player.SwingTimer` is active so attacks read in third-person. First-person held-item gizmo is suppressed while the toggle is on. F5 hops between modes.

**Missing**
- Damage from suffocation, lava, fire, cactus
- Food-based healing
- Sneak (Shift) — prevents falling off edges
- Ladder climb
- On-fire state
- Death screen with respawn button (currently instant respawn)

## Inventory / items

**Have**
- 9-slot hotbar with selected highlight (number keys 1–9 cycle slots)
- Hotbar HUD chrome rendered procedurally (`HotbarTextures`): bar background, selected highlight ring, 5×7 bitmap font sheet covering uppercase + digits + basic punctuation
- 3-face block icons (`RenderBlockIcon3D`) — true 3D cube rendered into the slot rectangle with -45° yaw + 30° pitch, drawn through the same multi-face cube shader the world drops use, so a hotbar icon looks like a "paused drop" with proper top + side + side textures (cross-sprite blocks like torches and flowers stay on the flat-sprite path)
- Dropped items in the world also render through the multi-face cube shader, so a broken grass cube on the ground shows grass-top up + dirt-bottom + grass-side around (instead of side-tile on every face)
- Selected block name rendered above the bar using the bitmap font (CamelCase → "Flowing Water" pretty-print)

**Have (continued)**
- Inventory screen (E key) — modal panel with title bar, 4×9 main grid, and a hotbar row backed by the live `Input.Inventory`. Panel chrome is bumped ~20% over the old hotbar dimensions (55-px slots, 44-px icons) so it reads as the bar's bigger sibling. Same world-halt + cursor-release lifecycle as the pause menu (`IsWorldHalted = IsPaused || IsInventoryOpen` flag in `GameRenderer`). Layout lives in `InventoryScreen` so click hit-testing tracks rendering exactly.
- Creative-mode inventory variant — when `GameMode == Creative`, the main grid is replaced with a search bar at the top + scrollable catalog of every placeable BlockType (`CreativeCatalog`, excludes Air / FlowingWater / FlowingLava). Type to filter (case-insensitive substring match against the friendly name); mouse wheel / PageUp / PageDown scroll through overflow rows; clicking a catalog tile fills the cursor with a full stack (RMB picks half). The hotbar row stays live below so the player can see (and click into) their loadout while picking. Survival mode keeps the standard 4×9 grid + hotbar with the normal pick/drop/swap rules.
- `ItemStack` (BlockType + Count, max 64, Air/0 = Empty) and `Inventory` (36 main + 9 hotbar + 1 cursor = 45 slots). The hotbar shares slot indices 36..44 with the in-world bar so a single `Slots[]` array drives both views.
- Inventory slot click handling — left-click runs Alpha's standard pick / drop / swap / merge exchange between the cursor stack and the clicked slot (via `Inventory.HandleLeftClickSlot`). Cursor stack follows the mouse and renders above every slot.
- Stack-count digits drawn in the bottom-right of each non-empty slot (`DrawStackCount` — scale-2 bitmap font, dark drop-shadow). Hotbar HUD shows them too.
- Block drops on break (survival): the broken block spawns a 0.25-block `DroppedItem` that bobs + spins, falls under gravity, settles on the floor, and gets picked up when the player walks within 1.5 blocks (`TickDrops`). `Inventory.TryAdd` runs Alpha's two-pass merge-then-fill (hotbar first, then main grid), with leftover staying in the world for re-pickup.
- Toss-from-cursor — clicking outside the inventory panel with a non-empty cursor lobs the stack into the world as a drop (`TossCursorStack`), with a 1 s pickup cooldown so the throw isn't instantly re-grabbed.
- Scroll-wheel hotbar cycling — wheel up/down moves the selected hotbar slot left/right (1 slot per Windows 120-unit notch), gated to gameplay (no wheel scroll while a modal is up).
- Crafting screen — RMB on a CraftingTable block opens a 3×3 input grid + result slot stacked over the player's main+hotbar inventory (`CraftingScreen` layout helper, `_isCraftingOpen` modal flag ORs into `IsWorldHalted`). Recipe registry (`CraftingRecipes`) covers Alpha 1.1.2_01 staples — sticks (2 planks vertical → 4 sticks), crafting table (2×2 planks → 1 table), bowls (V-shape planks → 4 bowls), torches (coal + stick → 4 torches), bricks block (2×2 clay brick → 1 bricks), planks (1 wood log shapeless → 4 planks), and all 20 tools (5 materials × pickaxe/shovel/axe/sword) — with shaped matching that scans every (rOff, cOff) sub-rectangle of the 3×3 (so a 2×2 recipe matches in any quadrant) plus shapeless multiset matching as a fallback. Output slot recomputes after every grid mutation; LMB takes a single batch into the cursor (consumes one of every input), shift-click crafts as many batches as fit in the inventory in one click. Closing the screen (Esc) flushes the grid + cursor into the player inventory and tosses any leftovers as world drops (matches Alpha "close-with-stuff-in-grid" behaviour). Same world-halt + cursor-release + slot-rect-shared-with-clickrouter pattern as the inventory screen.

**Missing**
- Right-click "split half" stack op in the inventory (currently both buttons run the left-click rules)
- Right-click drag spread (Alpha distributes one item per slot you drag the cursor over while RMB is held)
- Shift-click "move to other half" (hotbar ↔ main grid)
- Drop item via Q (only the GUI-toss path is wired)
- Pick-block (middle mouse)
- Armor slots (4 slots — the `Inventory` is 36+9 today; armor is unmodelled)
- Drop-vs-drop merging on the floor (each break spawns a fresh drop; they don't coalesce)

## Items (actual items, not blocks)

**Have**
- Tools: wood / stone / iron / gold / diamond × pickaxe / shovel / axe / sword. Atlas-textures mode pulls them from a base64-inlined `alpha_tools.png` (atlas layers 38..57); procedural-textures mode synthesises a 16×16 sprite per tool from code (`GenerateProceduralToolLayers` — wood handle on a diagonal + per-kind metal head with material-specific 3-shade palette). Either way the icons render through the existing `DrawFlatSpriteIcon` path so the inventory shows real 2D sprites rather than cubes.
- Tool durability (per-stack `short` field on `ItemStack`; ticks +1 per successful break, stack clears at MaxDurability — Wood 60, Stone 132, Iron 251, Gold 33, Diamond 1562). Damaged tools never auto-stack: ItemStack equality / SameKindAs include durability.
- Hardness-gated break time (per-block hardness × tool-class multiplier — Wood 2×, Stone 4×, Iron 6×, Diamond 8×, Gold 12×; bare-hand 1×). RequiredKind + RequiredTier in `ToolData` gate ore drops: stone-family blocks need a pickaxe of correct tier, otherwise the block breaks and yields nothing. Stone breaks into Cobblestone when harvest-eligible.
- Non-block ItemType layer — 9 ingredient items (Stick / Coal / Iron Ingot / Gold Ingot / Diamond gem / Flint / Clay Ball / Clay Brick / Bowl) live as BlockType entries 58..66 (same id-space trick tools use). `BlockData.IsItem(t)` range-checks the slice; mesher / placement / collision / lighting all branch through it the same way they branch through `IsTool`. RMB on an item stack is rejected by `TryPlace`. The parallel `ItemType` static class exposes both the strongly-typed `BlockType` constants (`ItemType.Stick`) and Alpha 1.1.2 numeric ids (`ItemType.AlphaId(t)` returns 280 for Stick, 263 for Coal, etc.) for upcoming save / multiplayer work. Item icons live alongside the tool icons in the embedded `alpha_tools.png` at canonical Notch coordinates — Coal (7,0), Flint (6,0), Iron Ingot (7,1), Clay Brick (6,1), Gold Ingot (7,2), Stick (5,3), Diamond (7,3), Clay Ball (9,3), Bowl (7,4) — so the alpha-textures atlas slices items in the same loop that already handles tools (`UploadToolLayersFromAlphaTools` walks layers 38..66 with one decode of the PNG). Procedural mode synthesises 16×16 pixel-art equivalents (`GenerateProceduralItemLayers`).
- Item drops: Coal Ore drops Coal (item, not the ore block) when broken with a wood-tier+ pickaxe; Diamond Ore drops the Diamond gem when broken with iron-tier+ pickaxe; Gravel has a 1-in-10 chance to drop Flint instead of the gravel block (matching Alpha); Clay always drops 4 Clay Balls (never the clay block itself). The 4 balls scatter as 4 separate `DroppedItem` entities so the pile fans out instead of stacking on the spot. Iron / Gold ores drop the ore block — players turn them into ingots by smelting in a furnace (see Blocks → Furnace).
- **Raw Porkchop (319)** + **Cooked Porkchop (320)** — first food items, shipped alongside the Pig in Tier 3 #9. Live as BlockType entries 74..75 (the same id-space trick tools and ingredients use, past the wall-torch range). `BlockData.IsItem` is now a two-slice check that covers both the original 58..66 range and the new 74..82 mob-drop slice. Atlas-textures mode pulls them from `alpha_tools.png` at canonical Notch coords (raw at (7,5), cooked at (8,5)) via `UploadTailItemLayersFromAlphaTools`, which mirrors the existing tool-slice loop but ranges past the tail-block layers; procedural mode synthesises 16×16 pixel-art equivalents (`GenerateRawPorkchopItem` paints a pink slab with marbling, `GenerateCookedPorkchopItem` browns the crust + tans the interior). Furnace smelting recipe `RawPorkchop → CookedPorkchop` ships in the same registry update, taking the standard 200-tick (10 s) cook time. Eating is still pending — the food UX (Tier 4 #14) hooks them up to the hunger / heal path.
- **Bow (261)** + **Arrow (262)** + **String (287)** + **Gunpowder (289)** — hostile-mob drop items shipped with Tier 3 #10. Live as BlockType entries 76..79, extending the `BlockData.IsItem` porkchop slice from 74..75 to 74..79 (later widened again by Tier 3 #12 to 74..82). Procedural-mode art ships per-icon: Bow (tan curved limb + thin diagonal bowstring), Arrow (diagonal shaft + iron-grey arrowhead + cream fletch), String (cream tangle ring with cross-knot in the centre), Gunpowder (dark-grey heap with sparkle grains). Atlas-mode coords are placeholder sentinels `(-1, -1)` for now, so procedural always wins until the canonical Alpha tile coords are wired; `UploadTailItemLayersFromAlphaTools` already handles the sentinel case by skipping the slice. Combat behaviour is roadmap-deferred — bow/arrow ship as inert collectibles until Tier 4 #17 (bow combat + arrow projectile entity); gunpowder ships inert until TNT priming in Tier 8 #43. String currently has no recipes wired (bow recipe + fishing rod recipe both gate on Tier 4 #17 / #23). Mob-drop sources: skeleton drops 0..2 arrows + 1-in-3 bow on death; spider drops 0..2 string; creeper drops 0..2 gunpowder on player-kill (explosion-death drops nothing).
- **Leather (334)** + **Feather (288)** + **Egg (344)** — passive-mob drop items shipped with Tier 3 #12 (Cow / Sheep / Chicken). Live as BlockType entries 80..82, extending the mob-drop slice to 74..82 (Egg is now the highest-id BlockType). Procedural-mode art: Leather is a tan 9×8 hide rectangle with a dashed stitching line; Feather is a thin diagonal cream rachis with paired barbs along its length; Egg is a cream oval with a soft highlight on the upper-left. Atlas-mode coords are `(-1, -1)` sentinels (procedural always wins until canonical Alpha coords are wired). Combat / cooking behaviour is roadmap-deferred — leather has no recipes (the leather-armor crafting hook gates on Tier 4 #19); egg is currently inert (egg-throw projectile gates on Tier 4 #20); feather has no recipes (arrow recipe gates on Tier 4 #17). Mob-drop sources: cow drops 0..2 leather + 1..3 raw porkchop on death (Alpha-era beef-less drop); chicken drops 0..2 feathers on death and lays 1 egg every 5..10 min while alive.

**Missing** — Alpha 1.1.2_01 ships 88 distinct non-block items (IDs 256–346 plus 2256/2257). We currently have 38 (20 tools + 9 ingredients + 2 porkchop + 4 mob-drop collectibles + 3 passive-mob drops). This list covers the remaining 50. Items added in later versions (Cookie, Sugar, Bone, Bone-meal, Clock, Cake, Cocoa Beans, dyes, Ink Sac, Lapis, Map, Golden Apple, Raw/Cooked Cod, Glowstone Dust) are intentionally excluded — they're post-1.1.2_01.

- **Tools — hoes (5)**: Wooden, Stone, Iron, Diamond, Gold. The hoe row in alpha_tools.png is already in the atlas image, just not mapped to BlockType entries (row gets used once farming lands).
- **Combat (1)**: Flint and Steel (259). (Bow + Arrow are in `**Have**` as inert collectibles; combat hookup is Tier 4 #17.)
- **Ingredients / materials from mob drops (1)**: Slimeball (341). Ships with the slime mob in Tier 4 #18. (String + Gunpowder + Feather + Leather are in `**Have**` as inert collectibles, dropped by spider / creeper / chicken / cow respectively.)
- **Sugar-cane derivatives (2)**: Paper (339), Book (340). Ship with sugar cane in Tier 8.
- **Containers (4)**: Bucket empty (325), Water Bucket (326), Lava Bucket (327), Milk Bucket (335).
- **Food (4)**: Apple (260), Mushroom Stew (282), Wheat (296), Bread (297). (Raw Porkchop (319) + Cooked Porkchop (320) shipped with Tier 3 #9.)
- **Farming (1)**: Wheat Seeds (295).
- **Armor (20)**: Leather / Chainmail / Iron / Diamond / Gold × Helmet / Chestplate / Leggings / Boots (IDs 298–317). Chainmail is uncraftable in Alpha — only obtainable via mob drops, and even then bugged — but the item IDs exist.
- **Placeable item-forms (4)**: Painting (321), Sign item (323), Wooden Door item (324), Iron Door item (330). These are item entries with their own IDs separate from the block they place.
- **Vehicles (5)**: Minecart (328), Storage Minecart with Chest (342), Powered Minecart with Furnace (343), Boat (333), Saddle (329).
- **Projectiles / misc (4)**: Snowball (332), Egg (344), Compass (345), Fishing Rod (346).
- **Redstone item-form (1)**: Redstone Dust (331). The block-form (redstone wire) is also missing — see Blocks → Missing.
- **Music discs (2)**: Music Disc "13" (2256), Music Disc "cat" (2257).

## HUD / UI

**Have**
- Status bar at bottom of VS tool window (FPS, game mode, HP, controls, selected block)
- Crosshair
- Selection wire-outline on targeted block
- Submerged screen tint: blue + 0.55 alpha for water, thick orange + 0.80 alpha for lava (Alpha 1.1.2 made lava nearly opaque from the inside)
- Survival HUD layout: `| hearts | gap | (hunger bar) |` — heart row right-anchored to `width/3`, hunger row left-anchored to `2×width/3`. Hunger row only renders when the per-world `HungerEnabled` survival sub-setting is on (off by default)
- Heart sprites in classic `<3` style (two-circles + V-taper construction, highlight on upper-left bump, shade on lower-right) with full / half / empty states
- Drumstick sprites (meat ellipse + bone capsule + knob) for hunger bar, same full / half / empty states
- Bubble sprites for the air row — full circle / shrunken popping bubble / transparent empty; row only renders while air < max
- Sprite shader + procedural `HudTextures` sheet (reusable brick for all future HUD icons)
- Pause menu (`GAME MENU`): BACK TO GAME / OPTIONS / SAVE / QUIT, with hover highlight and bitmap-font labels
- Options sub-menu (opened from pause-menu OPTIONS): SURVIVAL section with HUNGER BAR toggle (disabled in Creative); GRAPHICS section with ALPHA TEXTURES toggle; AUDIO section with `ALL SOUND` and `MUSIC` percentage sliders (click-to-set, drag-to-adjust). Esc pops Options back to the pause menu; BACK button does the same. Hunger persists per-world in the save header; alpha-textures + audio volumes persist per-user in `HKCU\Software\VStudioCraft`.
- Viewport-aware UI scaling (`UiScale`): hotbar, survival HUD icons, inventory panel + slots, pause / options menus, and bitmap-font labels all multiply their base pixel sizes by a factor derived from viewport height (720 px reference, clamped 1.0×–2.0×). Tool-window size keeps the original look; fullscreen / 1080p+ grows the chrome and click rects together so layout and hit-testing stay in lockstep. The crosshair is intentionally exempt and stays at a fixed pixel size as an aiming reticle.

**Have (cont.)**
- Tool-durability bar on item icons (3-stop green→yellow→red ramp pinned to the bottom of every slot — hotbar HUD, inventory grid, creative hotbar row, and cursor stack while dragging). Hidden on pristine tools so a fresh pickaxe doesn't show a green stripe.
- Hurt overlay — full-screen red wash (`RenderHurtOverlay`) when the player takes damage. `Player.TakeDamage` refreshes a 0.45 s `HurtTimer`; the overlay's alpha ramps from 0.40 down to 0 over the timer's life so the flash is brightest the frame it triggers and fades smoothly. All damage paths (fall, void, drowning, future contact damage) route through `TakeDamage` so the single hook covers everything.

**Missing**
- Dynamic hunger decay + food items. Hunger sub-setting and EatFood scaffold (flat-heal vs. refill branch) exist; eating items, hunger drain on activity, and the food UX itself are pending.
- Armor row
- Item-name popup (Alpha shows the held item's name briefly when you switch hotbar slots; we only show it persistently)
- Chat overlay
- Death screen with respawn button (currently we instant-respawn — see Player → Missing)
- F3 debug screen
- More Options (render distance, brightness, controls…)
- Main menu + world select + world creation screen

## Audio

**Have**
- OpenAL backend (`AudioEngine`, OpenTK 3.3.3 bindings) with a 16-source pool and round-robin eviction. **OpenAL Soft 1.23.1 (`openal32.dll`) is bundled inside the VSIX** at `Native\openal32.dll`; `AudioEngine.Initialize` `LoadLibrary`s it from the extension folder before any AL DllImport, so audio works on hosts with no system-wide OpenAL install (the default for clean Visual Studio machines). Defensive init still applies: if `LoadLibrary` or `AudioContext()` fails for any reason, the engine falls into a permanently-muted state with the failure reason exposed via `AudioEngine.InitFailureReason` for future diagnostics, and the renderer keeps working without sound.
- Procedural SFX bank (`SfxBank`) — every cue is synthesised at startup as 22 050 Hz mono 16-bit PCM with seeded white-noise + IIR filters (LowPass / HighPass / BandPass) + attack-decay envelopes. No embedded audio assets ship in the VSIX.
- Per-material categorisation (`BlockMaterial`: Stone / Wood / Dirt / Sand / Glass / Cloth / Leaves) drives break / place / step variants for every solid block.
- Block break + place sounds, fired from both creative and survival paths in `GameRenderer.TryBreak` / `UpdateBreakProgress` / `TryPlace`.
- Per-material footsteps emitted by a horizontal-distance accumulator (`StepIntervalBlocks ≈ 1.85`) on `GameRenderer.UpdatePlayer`; suspended while airborne or submerged. Material picked from the block under the player's feet.
- Fall thud (60 Hz body sine + LP-noise impact) when `Player.LastFallDistance ≥ 2 blocks`, gain scales linearly 0.3 → 1.0 across 0…12 blocks.
- Water-entry splash (BP noise 300–2500 Hz with 8 Hz amplitude warble), fired on the `!WasSubmergedPrev → WasInWater` edge.
- Pickup chime (rising sine 600 → 950 Hz, 100 ms) when an inventory absorption succeeds in `TickDrops`.
- UI click on every actionable pause / options menu hit and on every modal mouse-down (inventory, crafting, furnace, chest).
- Per-call pitch jitter (±6 %) on every cue so repeat plays don't loop the same waveform.

**Missing**
- Background music tracks
- Ambient cave noises
- Hit/grunt player sounds
- Mob sounds
- Fire crackle, lava pop

## Rendering details

**Have**
- Greedy chunk meshing (~5–10× fewer verts than naive)
- Face culling against opaque neighbours; internal water-water faces skipped
- Two-pass rendering: opaque first, then alpha-blended transparents (water) with depth-write off
- 16×16 nearest-neighbour texture atlas (85 layers — 38 blocks + 20 tools + 9 items + 9 tail blocks + 9 tail items)
- VAO/VBO/EBO per chunk mesh, separate VBOs for opaque + transparent streams
- Frustum culling per chunk (both passes)
- Dedicated render thread owning the GL context
- Off-thread meshing via `ChunkJobSystem`
- Distance-based chunk unload with hysteresis
- Particle system (`ParticleSystem`) — 256-particle pool with ring-buffer eviction. Each particle is a tumbling tinted cube rendered through the existing `_multiFaceCubeShader` + `_breakCubeMesh` (no separate billboard shader) with per-particle uTint that fades to zero over the last 30% of life. Update applies gravity (-20 m/s² capped) + drag in-place with a compaction pass. Spawn helpers cover the canonical Alpha cues: `SpawnBreakBurst` (8 particles tinted from the broken block's side tile, fired on every successful break + at each progress milestone of a long mine), `SpawnSplash` (10 water-tinted particles on the `!WasInWater → WasInWater` edge), `SpawnLavaBubble` (orange tint, no gravity — emitted from lava cells in a 6-block sweep at 10 Hz with 5% per-cell probability), `SpawnTorchSmoke` (dim grey wisp at 3% per-cell probability under the same sweep). Update + ambient emission gated through `TickDrops` so a paused world freezes the particle field; cleared on world transition.
- First-person held-item renderer (`RenderHeldItem`) — the player's selected hotbar stack draws as a HUD-layer gizmo in the bottom-right corner with a sin(πt) swing animation (see Player → Have). Layered before the survival HUD + hotbar so the chrome sits on top, matching Alpha's behaviour where the held tool is partially hidden behind the hotbar.

**Missing**
- Animated textures (water ripple, lava churn, fire, portal, destroy stages 0–9)
- Block-break progress overlay (10-frame crack texture)
- Dropped-item sprite
- Tile-entity rendering (chest lid animation, furnace fire, sign text)
- Underwater fog colour swap (we only tint the framebuffer today; real Alpha uses a deep-blue fog uniform underwater)
- GUI texture sheet rendering

## Controls

**Have**
- WASD, Space, Ctrl (sprint), Esc (release mouse / close modal), LMB break, RMB place, 1–8 hotbar (Grass / Dirt / Stone / Sand / Torch / Dandelion / Rose / Cobblestone)
- E opens / closes inventory (releases mouse-look, halts world ticks; Esc also closes it)
- F3 toggles Creative ↔ Survival (re-uses Alpha's F3 slot; debug screen pending)

**Missing**
- Shift sneak
- Q drop
- T chat
- F1 HUD toggle, F2 screenshot, F3 debug screen (currently used for mode toggle)
- Middle-click pick-block
- Scroll wheel hotbar
- Configurable key bindings

## Persistence

**Have**
- Custom gzipped binary world format (magic `VSC1`, version 7)
- Per-chunk `IsModified` flag so unmodified chunks don't bloat saves
- Save includes player position + camera yaw/pitch + game mode + HP + HungerEnabled survival sub-setting + furnace tile entities (v5/v6) + chest tile entities (v7)
- v1..v6 saves still load (missing fields default to Creative + full HP + hunger off; pre-v5 worlds load with no furnace entities; pre-v6 furnaces default to North-facing; pre-v7 worlds load with no chest entities)

**Missing**
- Persistence of: time of day, seed-per-feature state
- Autosave on interval
- Backup on world load failure

## Multiplayer

**Have**
- Nothing.

**Missing**
- Server mode, TCP protocol, authentication, client-side interpolation

## Game modes

**Have**
- Creative mode — instant break, unlimited place of selected block, no damage taken (default).
- Survival mode — takes fall damage + void damage + drowning, 20 HP (10 hearts), instant respawn at spawn point on death.
- Survival sub-setting (Options → Survival → Hunger Bar): when on, drumstick row renders + slow health regen kicks in at ≥70% hunger (1 HP every 4 s). When off (default), hunger UI is hidden and `EatFood(amount)` flat-heals instead. The flag is per-world and persisted in the save header.
- F3 toggles between modes at runtime; mode is persisted to save files.

**Missing**
- Survival-specific: finite inventory, block-break time, drops on break
- Difficulty setting (peaceful / easy / normal / hard)
- Drowning, suffocation, lava, fire, cactus damage sources

## Miscellaneous Alpha-era systems

**Missing**
- Tile entities (chest, furnace, sign, mob spawner)
- Random block ticks (grass spread, crop grow, leaf decay, ice melt, falling sand/gravel detach)
- Scheduled ticks (water/lava flow, redstone, torch fall)
- Explosion algorithm (ray-based blast with block-resistance)
- Fire propagation
- Mob-spawn attempt loop per game tick
- Entity tracking & chunk-bucketing

## Infrastructure

**Have**
- VS extension (`VStudioCraft.vsix`) hosting the game in a tool window
- Standalone WPF harness (`VStudioCraft.Standalone`) for iterating without reinstalling the VSIX
- OpenGL 3.3 Core via OpenTK, GLControl inside a WindowsFormsHost
- Dedicated render thread with cross-thread `InputState` + `ConcurrentDictionary` chunk storage
- `ChunkJobSystem` worker pool for terrain gen + meshing off the render thread
- Per-column deterministic hashed RNG for reproducible gen across chunk borders

---

## Roadmap (prioritised by impact-per-effort)

Tiered so each one is a coherent swim lane — pick a tier, ship the items in
order, move on. Most Tier 1–4 items are 200–1500 LoC of new code in this
codebase's style with no architectural blockers; Tiers 5+ start touching
multiple subsystems at once.
	
### Tier 8 — Late-Alpha systems

45. **Slabs + stairs** — Stairs are Alpha 1.0.5_01 canonical; slabs are Beta 1.3 but the user has explicitly opted to keep them in scope (sub-block geometry is general infrastructure that pairs naturally with stairs). Sub-block metadata byte + non-cube collision shape.
46. **Fences / ladders** — Alpha 1.0.14 / 1.0.17.
49. **Dispenser** — Alpha 1.0.16. 9-slot tile entity facing outward; redstone signal pops one item from a random slot. Reuses chest's tile-entity infrastructure plus furnace-style facing metadata.
50. **Bone + Bone Meal** — Alpha 1.0.14. Skeleton drop + crafted dye. Bone meal forces a wheat / sapling / etc. to advance one growth stage on right-click.
51. **Halloween Update block set** — Alpha 1.1.0 (Oct 30, 2010), in scope for our 1.1.2_01 target. Pumpkin already shipped (Tier 6 polish). Remaining:
    - **Jack-o-lantern** — lit variant of Pumpkin, placed via Flint+Steel right-click on a regular Pumpkin. Emits light.
    - **Netherrack** — soft red rock; lava can spread through it.
    - **Soul Sand** — slows player movement, reduces fall damage.
    - **Glowstone** — emits light=15, breaks into Glowstone Dust.
    - **Nether portal** — 4×5 obsidian frame ignited by Flint+Steel; teleports the player to a parallel Nether dimension. Largest item on this list — likely splits into its own swim-lane.
    - **Slimes** — small/medium/large mob variants spawning in slime chunks. Drops Slimeball (already in code as an item).

### Tier 9 — Infrastructure + completion

53. **Configurable key bindings + autosave + backup-on-load-failure**.
54. **Minecart (328) + Boat (333) + rail blocks** — Alpha 1.0.13 / 1.0.16 vehicles. Minecart on rails, boat on water; each is a ridable entity. Storage Minecart (Beta 1.5) and Powered Minecart (Beta 1.5) explicitly DROPPED — out of scope for Alpha 1.1.2_01.
	
### Tier 10 — Optional Features
51. **Smooth lighting / vertex AO** — Per-corner light sample at mesh time for ambient occlusion in cave/overhang corners.
52. **Animated water / lava textures** — moved to be last, original attempts edited the texture, instead of animating it. -> Frame-cycle a procedurally generated atlas-array layer so the surface shimmers / churns instead of staring back like wallpaper.
53. **Surface lava lakes** — Originally attempted under Tier 6 #33 alongside underground pools + cliff springs but removed: the flatness gate (sparse 8-point perimeter probe + windowed Y scan) plus stone-border ring still cost a ~300-FPS dip during chunk-stream-in because the cost lands on the chunk-job thread right when the renderer is also uploading meshes. Underground pools were kept because their pre-checks are O(1) cell reads. Revisiting needs either: (a) a precomputed per-chunk heightmap that this pass can sample without scanning Y at all, or (b) deferring lake placement to a separate post-gen pass that runs off the hot path. Visual payoff is small (occasional surface lava blob); only worth tackling once one of those infrastructure pieces lands for another reason.
