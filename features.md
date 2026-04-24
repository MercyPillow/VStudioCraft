# Features vs. Minecraft Alpha 1.1.2_01

Audit of the current VStudioCraft codebase (`src/VStudioCraft/Game`, `UI`) against
Alpha 1.1.2_01 (released 2010-09-18). Items marked **Have** exist today; items
under **Missing** are the gap.

---

## Blocks

**Have**
- Air, Grass, Dirt, Stone, Sand (5 block IDs)
- Per-face textures (grass top / side / bottom split)
- Procedural 16x16 pixel-art atlas, nearest-neighbour sampled

**Missing**
- Bedrock (indestructible world floor)
- Gravel (gravity-affected)
- Cobblestone (drop of stone)
- Wooden planks (oak only in Alpha)
- Wood log / tree trunk
- Leaves (with transparency, decay when detached)
- Saplings (grow into trees)
- Glass
- Mossy cobblestone (dungeon-only in Alpha)
- Obsidian
- Bricks
- Sponge (Alpha had it as a solid cube)
- Wool (white + dyed variants)
- Torch (placed on floor/wall, emits light)
- TNT (primed on activation)
- Ladder
- Ice (slippery, melts in light)
- Snow layer + snow block
- Clay block
- Sugar cane / reeds
- Cactus (damages on contact)
- Pumpkin + jack-o'-lantern
- Red + brown mushroom
- Red + yellow flower (dandelion, rose)
- Tall grass? — **not in Alpha**; skip
- Water (source + flowing, with its famous 8-level falloff)
- Lava (source + flowing; ignites flammables)
- Fire (spreads and dies)
- Stone & wooden slab (single + double)
- Stone & wooden stairs
- Fences
- Wooden door, iron door
- Trapdoor — **added in Beta 1.6**; skip
- Sign (post + wall)
- Crafting table (workbench)
- Furnace (lit + unlit)
- Chest (with inventory)
- Ore blocks: coal, iron, gold, diamond, redstone, lapis (lapis was later — coal/iron/gold/diamond/redstone in Alpha)
- Mineral blocks: iron, gold, diamond
- Mob spawner (cage with flame)
- Portal (obsidian frame + purple fill) — **nether added in Alpha 1.2.0**; skip for 1.1.2_01
- Redstone wire, redstone torch, button, lever, pressure plate, repeater (repeater was Beta)
  - 1.1.2_01 scope: wire, torch, button, lever, pressure plate
- Dispenser
- Note block — **added Beta 1.2**; skip
- Bookshelf
- Cake — **added Beta 1.2**; skip
- Jukebox — **added Beta 1.2**; skip
- Bed — **Beta 1.3**; skip

## World generation

**Have**
- Single-octave heightmap (we call 4 octaves but with the default Noise)
- Grass/dirt/stone layered columns
- Sand in low-height columns (ad-hoc beaches, no water)
- 16 × 128 × 16 chunks, matching Alpha's dimensions
- Chunk streaming with radius + unload hysteresis
- Modified-chunk retention so edits survive unload/reload

**Missing**
- Sea level (Alpha: y = 63) and ocean filling all air blocks ≤ sea level with water
- Tree generation (oak), density varying by biome
- Cave systems (DFS tunnels with worm-style carving)
- Ravines — **added Beta 1.8**; skip
- Ore veins: coal, iron, gold, diamond, redstone (layered depth distributions)
- Dungeons (4×4 cobble rooms with spawner + 1–2 chests)
- Surface lava lakes and underground lava pools
- Water springs / lava springs embedded in cliff faces
- Snow/ice biomes (snow layer on top blocks, ice on water)
- Desert biomes (sand replaces grass/dirt, no trees, cacti)
- Clay patches in shallow water
- Gravel patches
- Pumpkin patches
- Flowers + mushroom scatter
- Sugar cane next to water
- Bedrock floor (1–4 random rows at y ≤ 4)
- Biome system (Alpha used Rainfall/Temperature maps via `OverworldGenerator`)
- Structures: no villages/strongholds in 1.1.2_01 — skip
- World spawn point selection (finds a grass block, not just "fall from sky")
- Reproducible seed chain per chunk feature (Alpha uses deterministic per-chunk RNG)

## Lighting

**Have**
- Directional sun + ambient term in the fragment shader
- Sun follows a day/dusk/night/dawn piecewise angle
- Sky colour interpolates between day/dusk/night

**Missing**
- Block-light propagation (torches, lava, fire — Alpha's 15-level flood-fill)
- Sky-light propagation (15 at sky-exposed columns, attenuated through translucent blocks)
- Per-vertex light value stored on chunk vertices (Alpha baked it into a light byte per vertex)
- Smooth lighting toggle (Alpha had an option; real smooth lighting came later but 1.1.2 had vertex-AO-ish interpolation)
- Darkening/re-meshing when a block is placed/broken changes neighbour light
- Moon light (low constant at night, 4 in Alpha)
- Underwater light attenuation

## Sky / weather

**Have**
- Flat sky colour that shifts with sun angle

**Missing**
- Sun and moon sprites as textured billboards
- Moon phases
- Star field at night
- 2D cloud plane at y = 108 (scrolling)
- Horizon gradient / "void fog"
- Distance fog (the familiar Alpha render-distance fade)
- Rain / snow — **added Beta 1.5**; skip

## Entities & mobs

**Have**
- None. Player is the only entity.

**Missing**
- Entity base class with tick, AABB, velocity, gravity, collision
- Passive mobs: cow, pig, sheep, chicken
- Hostile mobs: zombie, skeleton, spider, creeper, slime
- Spider AI (wall-climb), Creeper hiss+explode, Skeleton bow AI, Zombie swim/swarm
- Mob AI pathfinding (A* on block grid)
- Mob spawn cycles (light-level gated, day vs night, distance from player)
- Despawn logic (distance + time)
- Drops on death (meat, feathers, bones, arrows, string, gunpowder, rotten flesh (Alpha=raw meat), wool)
- Dropped-item entity with pickup, merging, 5-min despawn
- XP orbs — **added Beta 1.8**; skip
- Projectiles: arrow, snowball, egg
- Vehicles: minecart, boat
- Painting
- Lightning / weather entities — **Beta 1.5**; skip
- TNT-primed entity (with flash + 4s fuse)
- Squid (water mob) — **added Beta 1.2**; skip

## Player

**Have**
- AABB voxel-collision walker with sub-step integration
- Walk / sprint / jump / gravity / terminal velocity
- Mouse look (yaw+pitch, clamped)
- Block break + place via 8-block reach raycast
- 4-block "hotbar" (number keys)

**Missing**
- Health (10 hearts) + damage model (fall, drowning, suffocation, lava, fire, cactus, mob attacks, void)
- Regeneration at full hunger — **hunger is Beta 1.8**; instead Alpha regen is just time-based while fed? — Alpha: no regen without food; you eat to heal directly. Food-item-based healing.
- Respawn at world spawn on death
- Sneak (Shift) — slows movement, prevents falling off edges
- Sprint — **not in 1.1.2_01**; skip
- Swim physics (bobbing, slower movement, upward thrust on Space)
- Ladder climb physics
- Fall damage thresholds
- Drowning timer (air bubbles)
- On-fire state + damage over time
- Hand-held item rendering in first-person
- Arm swing animation on attack
- Third-person camera (F5)
- Crouch offset on eye height

## Inventory / items

**Have**
- Nothing. Current "hotbar" is just a `SelectedBlock` enum with number-key hotkeys.

**Missing**
- Item stack system (id + damage + count, max 64)
- Player inventory: 9 hotbar + 27 main + 4 armor + 1 cursor (36+4)
- Inventory UI (E key toggles; 2×2 crafting inside)
- Crafting table UI (3×3)
- Furnace UI (input / fuel / output) + smelt tick
- Chest UI (single + double-chest joining)
- Drop item (Q)
- Pick-block (middle mouse)
- Scroll-wheel hotbar cycling
- Drag-splitting stacks
- Shift-click transfer

## Items (the actual items, not blocks)

**Missing**
- Tools: wood / stone / iron / gold / diamond × pickaxe / shovel / axe / sword / hoe
- Tool durability + break animation
- Block-break time curve (hardness × tool tier × effective-tool bonus)
- Armor: leather / iron / gold / diamond × helmet / chest / legs / boots (chainmail was unobtainable)
- Armor durability + damage reduction
- Bow + arrows
- Flint and steel (places fire)
- Bucket (empty / water / lava / milk)
- Food items: raw/cooked pork, apple, bread, cake (cake is block), cookie — alpha had pork, apples, bread, golden apple? golden apple was Beta 1.1
  - 1.1.2_01 scope: raw + cooked pork, apple, bread, mushroom stew, cookie
- Ingredients: stick, string, feather, gunpowder, coal, iron/gold/diamond ingot, redstone dust, flint, wheat, sugar, egg, bone
- Dye (bone meal at minimum; multi-colour dyes were Beta)
- Compass, clock
- Fishing rod — **Alpha 1.2**? — it was in 1.1.2_01 yes
- Seeds + wheat crop
- Saddle (for pigs) — Alpha added this
- Painting item
- Minecart + powered/storage variants
- Boat item
- Record discs — **Beta 1.2**; skip
- Book — Alpha had book? book was in Alpha yes, used in bookshelf recipe
- Map — **added Beta 1.6**; skip

## HUD / UI

**Have**
- Status bar at bottom of VS tool window (FPS, controls, selected block)
- Crosshair
- Selection wire-outline on targeted block

**Missing**
- Hotbar strip (9 slots with selected highlight)
- Heart row (health)
- Air bubble row (drowning timer)
- Armor row
- Tool-durability bar on item icons
- Item-name popup (centered, fades after 2s on change)
- Bitmap ASCII font renderer
- Chat overlay / chat history
- F3 debug screen (coords, facing, biome, light level, chunk stats, FPS)
- Pause menu (Options, Save & Quit)
- Options screen (FOV, render distance, difficulty, music/sound volume, controls)
- Main menu + world select + world creation screen
- Loading / saving overlay

## Audio

**Have**
- Nothing.

**Missing**
- Background music tracks (Alpha had C418's calm/hal tracks)
- Ambient cave noises in dark areas
- Block-specific step sounds (stone, grass, wood, sand, gravel, snow)
- Block-break + place sounds
- Hit/grunt player sounds
- Mob sounds (each mob has idle / hurt / death / attack set)
- Splash sound on water entry
- Fire crackle, lava pop
- Bow draw + release, arrow impact
- UI click on button press

## Rendering details

**Have**
- Chunk meshing with face culling against opaque neighbours
- 16x16 nearest-neighbour texture atlas
- VAO/VBO/EBO per chunk mesh
- Distance-based chunk unload

**Missing**
- Frustum culling per chunk
- Transparent-block second pass (water, glass, ice) — sort back-to-front
- Animated textures (water, lava, fire, portal, destroy stages 0–9)
- Block-break progress overlay (10-frame crack texture)
- Dropped-item sprite (billboarded 2D texture or 3D block model)
- First-person held-item / arm renderer
- Tile-entity rendering (chest lid animation, furnace fire, sign text)
- Item drops on break (particles pop off)
- Particle system (block-hit puffs, smoke, fire flame, redstone dust, enchant glint — Alpha scope: hit, smoke, fire, drip, footsteps, splash)
- Fog uniform + per-fragment fog blend
- Underwater fog colour swap
- GUI texture sheet rendering (gui/gui.png, icons.png etc.)

## Controls

**Have**
- WASD, Space, Ctrl (sprint), Esc (release mouse), LMB break, RMB place, 1/2/3/4 hotbar

**Missing**
- Shift sneak
- Q drop
- E inventory
- T chat
- F1 HUD toggle, F2 screenshot, F3 debug, F5 third person, F8 mouse smoothing
- Middle-click pick-block
- Scroll wheel hotbar
- Configurable key bindings
- Gamepad? Alpha didn't support; skip

## Persistence

**Have**
- Custom gzipped binary world format (magic `VSC1`, version 2)
- Per-chunk `IsModified` flag so unmodified chunks don't bloat saves

**Missing**
- Alpha's `level.dat` (NBT) + `region/*.mcr` chunk files — **not required** for feature parity, only for interop
- Persistence of: entity list, inventory, health, time of day, game mode, weather state, spawn point, world name, generator seed-per-feature state
- Autosave on interval (every 30s in Alpha)
- Backup on world load failure

## Multiplayer

**Have**
- Nothing.

**Missing**
- Server mode (Alpha Server was 1.0.15+)
- TCP packet protocol (handshake, login, chunk, block-change, player-position, chat)
- Authentication stub
- Client-side interpolation of remote players

## Game modes

**Have**
- Permanent "creative-lite" (instant break, unlimited place of selected block, no health, no drops).

**Missing**
- Survival mode (finite inventory, block-break time, drops, damage, respawn)
- Creative mode was **added Beta 1.8** — Alpha 1.1.2_01 is only survival; skip
- Difficulty setting (peaceful / easy / normal / hard) — affects mob spawning and damage
- Hardcore — Beta; skip

## Miscellaneous Alpha-era systems

**Missing**
- Tile entities (chest, furnace, sign, mob spawner, dispenser, note block — last two are Beta)
- Redstone tick scheduler (separate update queue from block ticks)
- Random block ticks (grass spread, crop grow, leaf decay, ice melt, fire spread)
- Scheduled ticks (water/lava flow, redstone)
- Explosion algorithm (ray-based blast with block-resistance)
- Fire propagation algorithm
- Mob-spawn attempt loop per game tick
- Entity tracking & chunk-bucketing
- Achievement system — **added Beta 1.5**; skip

---

## Suggested next steps (rough order)

1. **Water + lava** as blocks with a flow tick — enables beaches to make sense, lakes, springs.
2. **Light propagation** (sky + block light) — unlocks torches, caves, mobs.
3. **Trees** — cheap win, big visual payoff; needs logs + leaves + saplings.
4. **Inventory + item stacks** — nothing below it works without items.
5. **Hotbar HUD + font rendering** — so you can see inventory state.
6. **Block hardness + mining time + drops** — turns creative-lite into alpha-lite survival.
7. **Mobs** (pig/zombie first) — simplest AI, validates the entity system.
8. **Caves + ores** — gives mining something to find.
9. **Crafting table + furnace** — recipe plumbing.
10. **Sound** — music + step sounds close the "it feels like minecraft" gap fast.

Each of the above is 200–1000 LoC of new code in this codebase's style; nothing is architecturally blocking.
