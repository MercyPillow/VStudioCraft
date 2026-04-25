# Features vs. Minecraft Alpha 1.1.2_01

Audit of the current VStudioCraft codebase (`src/VStudioCraft/Game`, `UI`,
`src/VStudioCraft.Standalone`) against Alpha 1.1.2_01 (released 2010-09-18).
Items marked **Have** exist today; items under **Missing** are the gap.

Last updated after the fluid pass: water and lava sources now propagate
into adjacent air every 0.25s via a source-driven outflow tick. Dig into
the sea floor and the ocean floods in; place a source on a cliff and it
falls. FlowingWater / FlowingLava are first-class block types with a
per-cell metadata byte tracking remaining horizontal reach.

---

## Blocks

**Have** — 37 block IDs (`BlockType` enum)
- Air, Grass, Dirt, Stone, Sand
- Cobblestone, Bedrock, Gravel, Clay
- CoalOre, IronOre, GoldOre, DiamondOre, RedstoneOre
- WoodLog, Planks, Leaves
- Water (source) + FlowingWater (spread cell, water-tinted) — both transparent, share fluid family for face culling
- Lava (source) + FlowingLava (spread cell, emits 15 block-light) — full-block visual today, no churn animation yet
- GoldBlock, IronBlock, DiamondBlock
- Bricks, TNT, Bookshelf, MossyCobblestone, Obsidian, Sponge, Glass (opaque placeholder), Wool
- Torch (floor placement, emits 14 block-light, cross-sprite model, alpha-tested)
- Dandelion, Rose, BrownMushroom, RedMushroom, TallGrass — cross-sprite flora, non-collidable, raycast-targetable, light-transparent, scattered on grass during terrain gen
- Per-face textures (grass top / side / bottom; log top/side; TNT top/bottom/side; bookshelf)
- 39-layer procedural 16×16 pixel-art atlas, nearest-neighbour sampled
- Transparent-block routing to a second alpha-blended render pass (water today)
- Cross-sprite (X-shape) model path for non-cube blocks, alpha-tested in opaque pass via fragment-shader `discard`
- Per-block shape category (`IsCubeShape`) and raycast/collision separation (`IsRaycastTarget` vs `IsSolid`) so torches/flowers/grass are targetable but non-collidable

**Missing**
- Glass with real transparency (currently opaque placeholder)
- Ice (slippery, melts in light)
- Snow layer + snow block
- Torch wall placement (floor torches work; wall variants need a metadata byte for orientation)
- Torch fall on block-below removal (no block-update tick yet)
- TNT priming on activation (block exists but is inert)
- Ladder
- Sugar cane / reeds
- Cactus (damages on contact)
- Pumpkin + jack-o'-lantern
- Tall grass / flower / mushroom block-tick removal when the grass below is broken (today the sprite stays floating)
- Fluid side faces against glass or other non-opaque non-fluid blocks still use the cube sweep (full height). Only air-facing sides are replaced by the custom trapezoid; glass-adjacent water still shows a full-height side face at that boundary. Rare edge case, acceptable for V1.
- Water-meets-lava block formation (cobblestone / stone / obsidian)
- Water/lava textures animated (current tiles are static)
- Fire (spreads and dies)
- Stone & wooden slab (single + double)
- Stone & wooden stairs
- Fences
- Wooden door, iron door
- Sign (post + wall)
- Crafting table (workbench)
- Furnace (lit + unlit)
- Chest (with inventory)
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
- Glowstone, fire, jack-o'-lantern as block-light sources (Lava emits 15, Torch emits 14)
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
- Player only (no mob AI, no dropped items)

**Missing**
- Entity base class with tick, AABB, velocity, gravity, collision
- Passive mobs: cow, pig, sheep, chicken
- Hostile mobs: zombie, skeleton, spider, creeper, slime
- Mob AI / pathfinding
- Mob spawn cycles
- Drops on death
- Dropped-item entity (block drops on break — see _Inventory / items_; mob drops still missing since there are no mobs)
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

**Missing**
- Damage from suffocation, lava, fire, cactus
- Food-based healing
- Sneak (Shift) — prevents falling off edges
- Ladder climb
- On-fire state
- Hand-held item rendering in first-person
- Arm swing animation on attack
- Third-person camera (F5)
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

**Missing**
- Right-click "split half" stack op in the inventory (currently both buttons run the left-click rules)
- Shift-click "move to other half" (hotbar ↔ main grid)
- Crafting table / furnace / chest UIs (no inventory beyond the player)
- Drop item via Q (only the GUI-toss path is wired)
- Pick-block (middle mouse)
- Armor slots (4 slots — the `Inventory` is 36+9 today; armor is unmodelled)
- Drop-vs-drop merging on the floor (each break spawns a fresh drop; they don't coalesce)

## Items (actual items, not blocks)

**Missing**
- Tools: wood / stone / iron / gold / diamond × pickaxe / shovel / axe / sword / hoe
- Tool durability
- Hardness-gated break time
- Armor + damage reduction
- Bow + arrows
- Flint and steel
- Bucket (empty / water / lava / milk)
- Food items (raw/cooked pork, apple, bread, mushroom stew, cookie)
- Ingredients (stick, string, feather, gunpowder, coal, ingots, redstone, flint, wheat, sugar, egg, bone)
- Bone-meal dye
- Compass, clock
- Fishing rod
- Seeds + wheat crop
- Saddle

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
- Options sub-menu (opened from pause-menu OPTIONS): SURVIVAL section with HUNGER BAR toggle (disabled in Creative). Esc pops Options back to the pause menu; BACK button does the same. Setting persists per-world in the save header.
- Viewport-aware UI scaling (`UiScale`): hotbar, survival HUD icons, inventory panel + slots, pause / options menus, and bitmap-font labels all multiply their base pixel sizes by a factor derived from viewport height (720 px reference, clamped 1.0×–2.0×). Tool-window size keeps the original look; fullscreen / 1080p+ grows the chrome and click rects together so layout and hit-testing stay in lockstep. The crosshair is intentionally exempt and stays at a fixed pixel size as an aiming reticle.

**Missing**
- Dynamic hunger decay + food items. Hunger sub-setting and EatFood scaffold (flat-heal vs. refill branch) exist; eating items, hunger drain on activity, and the food UX itself are pending.
- Armor row
- Tool-durability bar on item icons
- Item-name popup
- Chat overlay
- F3 debug screen
- More Options (render distance, brightness, controls, audio…)
- Main menu + world select + world creation screen

## Audio

**Have**
- Nothing.

**Missing**
- Background music tracks
- Ambient cave noises
- Block-specific step sounds
- Block-break + place sounds
- Hit/grunt player sounds
- Mob sounds
- Splash sound on water entry
- Fire crackle, lava pop
- UI click on button press

## Rendering details

**Have**
- Greedy chunk meshing (~5–10× fewer verts than naive)
- Face culling against opaque neighbours; internal water-water faces skipped
- Two-pass rendering: opaque first, then alpha-blended transparents (water) with depth-write off
- 16×16 nearest-neighbour texture atlas (39 layers)
- VAO/VBO/EBO per chunk mesh, separate VBOs for opaque + transparent streams
- Frustum culling per chunk (both passes)
- Dedicated render thread owning the GL context
- Off-thread meshing via `ChunkJobSystem`
- Distance-based chunk unload with hysteresis

**Missing**
- Animated textures (water ripple, lava churn, fire, portal, destroy stages 0–9)
- Block-break progress overlay (10-frame crack texture)
- Dropped-item sprite
- First-person held-item / arm renderer
- Tile-entity rendering (chest lid animation, furnace fire, sign text)
- Particle system (block-hit puffs, smoke, fire, drip, splash)
- Underwater fog colour swap (we only tint the framebuffer today; real Alpha uses a deep-blue fog uniform underwater)
- GUI texture sheet rendering

## Controls

**Have**
- WASD, Space, Ctrl (sprint), Esc (release mouse / close modal), LMB break, RMB place, 1–8 hotbar (Grass / Dirt / Stone / Sand / Torch / Dandelion / Rose / TallGrass)
- E opens / closes inventory (releases mouse-look, halts world ticks; Esc also closes it)
- F3 toggles Creative ↔ Survival (re-uses Alpha's F3 slot; debug screen pending)

**Missing**
- Shift sneak
- Q drop
- T chat
- F1 HUD toggle, F2 screenshot, F3 debug screen (currently used for mode toggle), F5 third person
- Middle-click pick-block
- Scroll wheel hotbar
- Configurable key bindings

## Persistence

**Have**
- Custom gzipped binary world format (magic `VSC1`, version 4)
- Per-chunk `IsModified` flag so unmodified chunks don't bloat saves
- Save includes player position + camera yaw/pitch + game mode + HP + HungerEnabled survival sub-setting
- v1/v2/v3 saves still load (missing fields default to Creative + full HP + hunger off)

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
- Random block ticks (grass spread, crop grow, leaf decay, ice melt)
- Scheduled ticks (water/lava flow, redstone)
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

## Suggested next steps (rough order)

1. ~~**Inventory + item stacks**~~ — done. `ItemStack`/`Inventory` model, click-to-move slot exchange, cursor stack, stack-count digits, drops on break with pickup, GUI-toss.
2. **Block hardness + mining time + drops** — break timing exists; drops now exist; per-block hardness tuning + tool-aware mining time still needed.
3. **Mobs** (pig/zombie first) — entity system + AI validated; zombie/creeper attacks hook straight into the existing Player.TakeDamage.
4. **Crafting table + furnace** — recipe plumbing.
5. **Sound** — music + step sounds close the "it feels like Minecraft" gap fast.

Each of the above is 200–1500 LoC of new code in this codebase's style; nothing is architecturally blocking.
