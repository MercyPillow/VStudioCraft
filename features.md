# Features vs. Minecraft Alpha 1.1.2_01

Audit of the current VStudioCraft codebase (`src/VStudioCraft/Game`, `UI`,
`src/VStudioCraft.Standalone`) against Alpha 1.1.2_01 (released 2010-09-18).
Items marked **Have** exist today; items under **Missing** are the gap.

Last updated after surface flora: dandelion, rose, brown + red mushroom
and tall grass — five new cross-sprite blocks scattered deterministically
across grass surfaces in the terrain pass, all sharing the alpha-tested
opaque pass introduced for torches.

---

## Blocks

**Have** — 35 block IDs (`BlockType` enum)
- Air, Grass, Dirt, Stone, Sand
- Cobblestone, Bedrock, Gravel, Clay
- CoalOre, IronOre, GoldOre, DiamondOre, RedstoneOre
- WoodLog, Planks, Leaves
- Water (still, transparent), Lava (block only, no flow)
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
- Water/lava flowing variants (8-level falloff)
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
- Dropped-item entity
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
- Water is non-solid (you can walk / fall through it — swim physics not yet)
- Health (20 HP = 10 hearts, Alpha-style) in survival mode
- Fall damage (Alpha formula: `max(0, distance - 3)` half-hearts, cancelled if landing in water)
- Void damage (4 HP every 0.5 s below y=-16)
- Respawn on death (teleport to spawn, restore full HP)

**Missing**
- Damage from drowning, suffocation, lava, fire, cactus
- Food-based healing
- Sneak (Shift) — prevents falling off edges
- Swim physics (bobbing, slower movement, upward thrust on Space, drowning timer)
- Ladder climb
- On-fire state
- Hand-held item rendering in first-person
- Arm swing animation on attack
- Third-person camera (F5)
- Death screen with respawn button (currently instant respawn)

## Inventory / items

**Have**
- `SelectedBlock` enum toggled by number keys (creative-lite hotbar)

**Missing**
- Item stack system (id + damage + count, max 64)
- Player inventory (9 hotbar + 27 main + 4 armor + 1 cursor)
- Inventory UI (E key)
- Crafting table / furnace / chest UIs
- Drop item (Q)
- Pick-block (middle mouse)
- Scroll-wheel hotbar cycling

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
- Watery blue overlay when camera is inside water
- Survival HUD layout: `| hearts | gap | hunger bar |` — heart row right-anchored to `width/3`, hunger row left-anchored to `2×width/3`
- Heart sprites in classic `<3` style (two-circles + V-taper construction, highlight on upper-left bump, shade on lower-right) with full / half / empty states
- Drumstick sprites (meat ellipse + bone capsule + knob) for hunger bar, same full / half / empty states
- Sprite shader + procedural `HudTextures` sheet (reusable brick for all future HUD icons)

**Missing**
- Hotbar strip (9 slots with selected highlight)
- Air bubble row (drowning timer)
- Dynamic hunger decay + food items (hunger currently pinned at max — scaffolding only)
- Armor row
- Tool-durability bar on item icons
- Item-name popup
- Bitmap font renderer
- Chat overlay
- F3 debug screen
- Pause menu
- Options screen
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
- WASD, Space, Ctrl (sprint), Esc (release mouse), LMB break, RMB place, 1–8 hotbar (Grass / Dirt / Stone / Sand / Torch / Dandelion / Rose / TallGrass)
- F3 toggles Creative ↔ Survival (re-uses Alpha's F3 slot; debug screen pending)

**Missing**
- Shift sneak
- Q drop
- E inventory
- T chat
- F1 HUD toggle, F2 screenshot, F3 debug screen (currently used for mode toggle), F5 third person
- Middle-click pick-block
- Scroll wheel hotbar
- Configurable key bindings

## Persistence

**Have**
- Custom gzipped binary world format (magic `VSC1`, version 3)
- Per-chunk `IsModified` flag so unmodified chunks don't bloat saves
- Save includes player position + camera yaw/pitch + game mode + HP
- v1/v2 saves still load (missing fields default to Creative + full HP)

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
- Survival mode — takes fall damage + void damage, 20 HP (10 hearts), instant respawn at spawn point on death.
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

1. **Flowing water / lava** — promote the current still-water to a proper fluid with 8-level falloff ticks.
2. **Swim physics + drowning timer** — now that water + survival-HP exist, the player should bob in it and lose air underwater.
3. **Hotbar HUD + bitmap font** — reuse the new sprite shader + HudTextures pattern; prerequisite for real inventory.
4. **Inventory + item stacks** — the "items instead of block-enum" jump.
5. **Block hardness + mining time + drops** — turns creative-lite into alpha-lite survival.
6. **Mobs** (pig/zombie first) — entity system + AI validated; zombie/creeper attacks hook straight into the existing Player.TakeDamage.
7. **Crafting table + furnace** — recipe plumbing.
8. **Sound** — music + step sounds close the "it feels like Minecraft" gap fast.

Each of the above is 200–1500 LoC of new code in this codebase's style; nothing is architecturally blocking.
