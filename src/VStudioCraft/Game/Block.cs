namespace VStudioCraft.Game
{
    internal enum BlockType : byte
    {
        Air = 0,
        Grass = 1,
        Dirt = 2,
        Stone = 3,
        Sand = 4,
        Cobblestone = 5,
        Bedrock = 6,
        Gravel = 7,
        Clay = 8,
        CoalOre = 9,
        IronOre = 10,
        GoldOre = 11,
        DiamondOre = 12,
        RedstoneOre = 13,
        WoodLog = 14,
        Planks = 15,
        Leaves = 16,
        Water = 17,
        Lava = 18,
        GoldBlock = 19,
        IronBlock = 20,
        DiamondBlock = 21,
        Bricks = 22,
        Tnt = 23,
        Bookshelf = 24,
        MossyCobblestone = 25,
        Obsidian = 26,
        Sponge = 27,
        Glass = 28,
        Wool = 29,
        Torch = 30,
        Dandelion = 31,
        Rose = 32,
        BrownMushroom = 33,
        RedMushroom = 34,
        // CraftingTable slots into the previously-vacant id 35. Placed
        // here (not appended after items) so the enum stays "blocks
        // first, then tools 38+, then items 58+" — pushing tool ids
        // would invalidate every save the project has produced. The
        // open slot was always intended for a block, this just spends
        // it. Texture is multi-face: planks on the bottom, work-bench
        // top on top, tool-rack side on the four sides.
        CraftingTable = 35,
        FlowingWater = 36,
        FlowingLava = 37,

        // Tools — non-block items that share the BlockType id space so the
        // existing ItemStack / inventory / save plumbing keeps working
        // without a separate ItemId enum. Marked non-solid, non-targetable,
        // non-cube, non-opaque and excluded from placement (TryPlace
        // rejects IsTool stacks). Renderers route them through the flat-
        // sprite path because tool sprites are 2D, not cubes. Order is
        // material-major so a quick `(byte)t - WoodSword` chunked into 5s
        // recovers material; kind is the chunk index.
        WoodSword     = 38,
        StoneSword    = 39,
        IronSword     = 40,
        DiamondSword  = 41,
        GoldSword     = 42,
        WoodShovel    = 43,
        StoneShovel   = 44,
        IronShovel    = 45,
        DiamondShovel = 46,
        GoldShovel    = 47,
        WoodPickaxe   = 48,
        StonePickaxe  = 49,
        IronPickaxe   = 50,
        DiamondPickaxe= 51,
        GoldPickaxe   = 52,
        WoodAxe       = 53,
        StoneAxe      = 54,
        IronAxe       = 55,
        DiamondAxe    = 56,
        GoldAxe       = 57,

        // Items — non-placeable, non-tool inventory entries (the Alpha
        // 1.1.2 "ingredient" set: sticks, coal, ingots, gem, flint, clay
        // ball + brick, bowl). Same trick as tools: live in the BlockType
        // id space so ItemStack / Inventory / save plumbing stays
        // unchanged. Alpha numeric ids (256+) are preserved as comments
        // and exposed via ItemType.AlphaId for future save-format work.
        // BlockData.IsItem range-checks this slice the way IsTool does
        // for tools; renderers / placement / mesher all fall through
        // identically (non-solid, non-cube, non-opaque, flat-sprite icon).
        Stick      = 58, // Alpha 280
        Coal       = 59, // Alpha 263
        IronIngot  = 60, // Alpha 265
        GoldIngot  = 61, // Alpha 266
        Diamond    = 62, // Alpha 264
        Flint      = 63, // Alpha 318
        ClayBall   = 64, // Alpha 337
        ClayBrick  = 65, // Alpha 336
        Bowl       = 66, // Alpha 281

        // Tail blocks — appended past the tools+items slice. Adding new
        // block ids here doesn't shift IsTool / IsItem ranges (both are
        // bounded above by Bowl=66), so all existing save files keep
        // loading without a migration. Furnace is the unlit / idle state;
        // LitFurnace is the actively-smelting variant — the tile-entity
        // tick swaps the world cell between them as fuel burns down.
        // Both share a tile-entity (per-position input/fuel/output stacks
        // + cook/burn timers) keyed on world coordinate.
        Furnace    = 67,
        LitFurnace = 68,

        // Chest — wood-planks chest with iron banding. Holds a 27-slot
        // inventory stored in a per-position ChestTileEntity (same
        // dictionary-on-World pattern as Furnace). Same id-space rule:
        // appended past the tools+items slice so existing saves stay
        // valid. Alpha-style "single chest" only — double-chest pairing
        // (two adjacent chests merging into a 54-slot inventory) is not
        // implemented; each chest is independent.
        Chest      = 69,

        // Wall-torch variants. The default Torch (id 30) is the floor
        // placement; the four cardinal wall variants encode their own
        // "facing" directly in the BlockType so we don't need a per-cell
        // metadata byte (chunk metadata is already used by the fluid sim
        // and isn't persisted, which would lose torch orientation across
        // save/load). Same id-space trick the tools / items / furnace /
        // chest blocks use: appended past the tail so existing saves
        // load identically. Facing semantics match BlockFacing — the
        // cardinal direction the torch's flame POINTS (i.e. away from
        // the supporting wall, into the air). All four variants share
        // the floor torch's tile, drop a generic Torch when broken,
        // and emit the same 14 light. The mesher branches in EmitModels
        // to draw a tilted billboard against the wall instead of the
        // upright X cross used for the floor variant.
        TorchEast  = 70, // mounted on west wall, points east  (+X)
        TorchWest  = 71, // mounted on east wall, points west  (-X)
        TorchSouth = 72, // mounted on north wall, points south (+Z)
        TorchNorth = 73, // mounted on south wall, points north (-Z)

        // Tier 3 #9 — Pig drops. Both live in the BlockType id-space the
        // same way tools and ingredient items do (see ItemType wrapper
        // below): they're flagged via BlockData.IsItem so placement /
        // mesher / collision treat them as non-block items. Append-only
        // past TorchNorth so v7 saves continue to load.
        RawPorkchop    = 74, // Alpha 319
        CookedPorkchop = 75, // Alpha 320

        // Tier 3 #10 — Hostile mob drops. All four ship as inert
        // collectibles for now — Bow + Arrow get real combat in
        // Tier 4 #17, Gunpowder fuels TNT priming in Tier 8 #43, and
        // String unlocks the Bow recipe + Fishing Rod (Tier 4). They
        // live in the BlockType id-space the same way other items do
        // (IsItem branches the mesher / placement / collision paths).
        // Append-only past CookedPorkchop so v7 saves continue to load.
        Bow       = 76, // Alpha 261
        Arrow     = 77, // Alpha 262
        String    = 78, // Alpha 287
        Gunpowder = 79, // Alpha 289

        // Tier 3 #12 — Cow / Sheep / Chicken passive-mob drops. Cow drops
        // Leather + Raw Porkchop (the Alpha 1.1.2_01 era — beef wasn't
        // added until Beta 1.8 so cows shared the pig drop), Chicken
        // drops Feather on death and lays Egg every ~5 min while alive,
        // Sheep drops Wool (block, already exists). Same id-space trick
        // as the other items. Append-only past Gunpowder so existing
        // saves continue to load.
        Leather = 80, // Alpha 334
        Feather = 81, // Alpha 288
        Egg     = 82, // Alpha 344

        // Tier 4 #14 — Hoes (5 materials). Appended past Egg=82 instead
        // of being slotted into the canonical [WoodSword..GoldAxe] tool
        // slice because that slice was frozen at id 57 long before hoes
        // landed; renumbering would shift Stick=58 and every subsequent
        // item / block, breaking every existing v7 save. The trade-off
        // is that the contiguous "tool slice" the original IsTool /
        // GetKind / GetMaterial helpers range-checked is now two ranges
        // ([WoodSword..GoldAxe] + [WoodHoe..GoldHoe]) — the helpers below
        // OR the second range in. Material order matches the rest of the
        // tool ladder (wood, stone, iron, diamond, gold), Alpha numeric
        // ids 290..294. Hoes function ONLY as a Farmland-tilling RMB
        // tool — they don't speed-mine any block and don't gate any
        // drop, just transform Grass / Dirt → Farmland under
        // GameRenderer.TryInteract.
        WoodHoe    = 83, // Alpha 290
        StoneHoe   = 84, // Alpha 291
        IronHoe    = 85, // Alpha 292
        DiamondHoe = 86, // Alpha 293
        GoldHoe    = 87, // Alpha 294

        // Tier 4 #14 — Farming blocks. Farmland is what hoes till grass
        // and dirt into; only Farmland accepts WheatSeeds. Wheat is the
        // crop block itself — cross-sprite (like flowers) with 8 growth
        // stages 0..7 stored in chunk metadata low-4-bits and ticked
        // probabilistically by the world tick (light ≥ 9 + RNG advance
        // a stage; ~30–60s real time to reach stage 7). Append-only
        // past the hoe slice so all existing saves stay valid.
        Farmland = 88,
        Wheat    = 89,

        // Tier 4 #14 — Items. WheatSeeds drops from breaking Wheat at
        // any stage and (rarely) from breaking a Grass block bare-
        // handed; it's the seed the player plants on Farmland to start
        // a new crop. Wheat (the item) drops from breaking a stage-7
        // Wheat block. Bread is the 3-wheat-in-a-row craft. MushroomStew
        // is shapeless { Bowl, BrownMushroom, RedMushroom } and is the
        // first food that returns its container item (Bowl) to the
        // inventory on consume. Alpha numeric ids: 295/296/297/282.
        // Same id-space trick as the rest of the items.
        WheatSeeds   = 90, // Alpha 295
        WheatItem    = 91, // Alpha 296
        Bread        = 92, // Alpha 297
        MushroomStew = 93, // Alpha 282

        // Tier 4 #26 — Sugar cane block + paper/book items. Sugar cane is
        // a cross-sprite multi-block-tall plant that grows next to water
        // (Alpha behaviour). The water-adjacency rule is applied at
        // PLACEMENT time only — once a cane is placed, the random tick
        // grows it upward without re-checking water (matches Alpha 1.1.2
        // exactly: real Alpha had no per-tick moisture/water gate for
        // cane growth, only a "needs water within 1 cell of the bottom
        // block at placement" rule). Cap height is 3, same as canon.
        // SugarCaneItem is the harvested-cane item the player gets back
        // when the in-world block breaks; Paper + Book are the crafting
        // outputs (3 cane → 3 paper, 3 paper → 1 book). Append-only past
        // the Tier 4 #14 farming items so existing v8 saves keep loading.
        SugarCane     = 94,
        SugarCaneItem = 95, // Alpha 338
        Paper         = 96, // Alpha 339
        Book          = 97, // Alpha 340

        // Tier 4 #16 — Wooden + Iron doors. Two block ids per material
        // (top + bottom half) is the simplest correct geometry for a
        // 2-tall block: Alpha used a metadata bit for top/bottom, but
        // dedicating separate enum slots keeps the render/collision
        // switch tables straight without a per-cell metadata read for
        // half-id resolution. The four block ids share a SINGLE
        // metadata byte that encodes mutable state across both halves:
        //   bit 0 (mask 0x01): open flag (0=closed, 1=open)
        //   bits 1..2 (mask 0x06, >>1): facing — direction the closed
        //                               door's OUTWARD normal points
        //                               (0=North, 1=East, 2=South, 3=West)
        //   bit 3 (mask 0x08): hinge side (0=left, 1=right)
        // Both halves of a door must keep their open/facing/hinge bits
        // synchronised — TryInteract toggles open on both. The block
        // ids themselves are not orientation-specific, unlike the
        // wall-torch family — encoding facing in the BlockType would
        // explode the enum (4 facings × 2 open/closed × 2 halves = 16
        // ids per material), so we use Chunk._meta + WriteSparseMeta
        // persistence (the same path Wheat uses for stage). Append-
        // only past Book=97 so existing v8 saves keep loading without
        // an id remap.
        WoodDoorBlockBottom = 98,
        WoodDoorBlockTop    = 99,
        IronDoorBlockBottom = 100,
        IronDoorBlockTop    = 101,
        // Tier 4 #16 — Door ITEMS. The player crafts / picks up the
        // item form; placement spawns the two block halves. Match
        // Alpha numeric ids 324 (wooden) and 330 (iron). Same
        // BlockType id-space trick the rest of the items use; IsItem
        // range below is extended to cover [WoodDoorItem..IronDoorItem]
        // so renderers / placement / mesher take the flat-sprite
        // branch.
        WoodDoorItem = 102, // Alpha 324
        IronDoorItem = 103, // Alpha 330

        // Tier 4 #17 — Flint and Steel + Apple. Both are simple item
        // ids appended past IronDoorItem so existing v8 saves stay
        // byte-stable (no enum renumber). FlintAndSteel is the bow's
        // companion fire-starter; Alpha used it on TNT (prime → fuse)
        // and on solid blocks (place a Fire block adjacent to the
        // clicked face). Both downstream targets are roadmap-deferred
        // (TNT priming = Tier 8 #43, Fire block = Tier 6 #34), so
        // V1 RMB does nothing — this is a faithful staged drop, not
        // a bug. Apple is a 4-HP food item that drops rarely (~0.5%)
        // from breaking oak leaves; same eat-on-RMB shape as Bread /
        // RawPorkchop / CookedPorkchop.
        FlintAndSteel = 104, // Alpha 259
        Apple         = 105, // Alpha 260

        // Tier 4 #20 — Snowball. Throwable RMB projectile (Alpha 332).
        // Egg already exists at id 82 (Tier 3 #12 — chicken lays them);
        // this entry adds Snowball as a NEW item, and the projectile
        // entity ThrownProjectile carries both kinds at runtime.
        // Snowball stack-cap is 16 in Alpha (vs 64 default) — see
        // ItemStack.MaxStackSizeFor for the exception. V1 Snowball is a
        // creative-catalog-only entry: Alpha obtained it via shovel-on-
        // snow, but snow blocks are roadmap-deferred (Tier 6/8). NO
        // recipe ships with this tier; the catalog gives creative
        // players one and survival has no obtain path until snow lands.
        // Append-only past Apple=105 so existing v8 saves stay byte-
        // stable (same trick every preceding tier used).
        Snowball = 106, // Alpha 332

        // Tier 4 #15 — Buckets. Empty bucket scoops Water/Lava sources or
        // milks a Cow on RMB; filled bucket places its source back into
        // the world. All four ids ship as items (IsItem range extends to
        // BucketMilk), and the filled three are stack-cap-1 in Alpha
        // 1.1.2_01 so a player can't carry an unlimited fluid reservoir
        // in a single slot — the carry cost is what makes ferrying lava
        // up from cave-level a meaningful trip. Append-only past
        // Snowball=106 so existing v8 saves stay byte-stable.
        BucketEmpty = 107, // Alpha 325
        BucketWater = 108, // Alpha 326
        BucketLava  = 109, // Alpha 327
        BucketMilk  = 110, // Alpha 335

        // Tier 4 #18 — Slimeball. Drops only from small slimes; the in-
        // world Slime mob is the obtain path. No recipes consume it
        // (sticky pistons + magma cream + slime block all post-date
        // Alpha 1.1.2_01), so the item is a cosmetic collectible kept
        // for completeness — same "audited Alpha id, no downstream
        // craft" story as Snowball. Append-only past BucketMilk=110 so
        // existing v8 saves stay byte-stable.
        Slimeball   = 111, // Alpha 341

        // Tier 4 #22 — Compass (Alpha 345). Held item; small textual
        // direction marker on the hotbar slot points toward world
        // spawn from anywhere on the map. The Alpha craft is 4 iron
        // ingots in a + with a single redstone in the middle; redstone
        // dust doesn't exist yet (Tier 8 #42) so the item ships as a
        // creative-catalog-only entry — the recipe is deferred until
        // redstone arrives. Append-only past Slimeball=111 so existing
        // v8 saves stay byte-stable; v9 of the save format adds the
        // world-spawn vector (see WorldSaveFormat) so a freshly-loaded
        // save can point the compass at the same spawn the player
        // originally appeared at, not just wherever they happened to be
        // when the save was written.
        Compass     = 112, // Alpha 345

        // Tier 4 #21 — Saddle (Alpha 329). Held item; RMB on a Pig
        // equips it (sets Pig.Saddled), and RMB on a saddled pig (with
        // a non-saddle held stack) mounts the player. Alpha saddles are
        // unstackable (one slot per saddle) and obtained ONLY from
        // dungeon chests — there's no craft recipe in Alpha 1.1.2_01.
        // Dungeons don't exist yet (Tier 6 #32), so until they ship the
        // saddle is a creative-catalog-only entry. Append-only past
        // Compass=112 so existing v8/v9 saves stay byte-stable.
        Saddle      = 113, // Alpha 329

        // Tier 4 #23 — Fishing Rod (Alpha 346). Held item; RMB casts a
        // Bobber entity at the camera-forward raycast endpoint (or
        // 5 blocks ahead if the ray misses). Second RMB while the rod
        // has an active bobber reels it in — if a catch landed (random
        // 5..30s timer) the player gets one Raw Porkchop, otherwise
        // nothing. Unstackable (one rod per slot — Alpha behaviour;
        // damage values would distinguish two rods anyway). Append-only
        // past Saddle=113 so existing v8/v9 saves stay byte-stable.
        FishingRod  = 114, // Alpha 346

        // Tier 4 #24 — Painting (Alpha 321). Held item; RMB on a wall
        // mounts a Painting entity onto the air-side cell adjacent to
        // the targeted block. The painting is a flat textured rectangle
        // in the wall plane (not a block — it occupies no cell, the
        // player walks through it the same way they do a torch). V1
        // ships 5 painting variants (1×1, 1×2, 2×1, 2×2, 4×3) chosen at
        // random on placement; auto-sizing to the largest available
        // rectangle is a polish TODO. Paintings persist via World's
        // Paintings list (see WorldSaveFormat v10). Append-only past
        // FishingRod=114 so existing v8/v9 saves stay byte-stable.
        Painting    = 115, // Alpha 321

        // Tier 4 #25 — Jukebox (Alpha 1.0.14, id 84). Solid cube block.
        // Stores an inserted Music Disc (Disc13 / DiscCat) via a
        // JukeboxTileEntity keyed by world coord — same tile-entity
        // pattern Furnace and Chest use. RMB with a disc in hand inserts
        // the disc + starts streaming the matching music track; RMB
        // again with no disc held ejects the disc as a DroppedItem and
        // stops the music. Append-only past Painting=115 so existing
        // v8/v9/v10 saves stay byte-stable.
        Jukebox     = 116, // Alpha 84

        // Tier 4 #25 — Music Discs. Two variants ship in Alpha 1.1.2_01:
        // "13" (eerie static) at id 2256 and "cat" (mellow synth) at id
        // 2257. Disc13 / DiscCat are the held items the player slots
        // into a Jukebox. Stack-cap 1 (matches Alpha — each disc has a
        // distinct numeric id, they never stacked even before durability
        // metadata distinguished them). No Alpha recipe — discs are
        // dungeon-loot only; ship as creative-only entries until dungeons
        // arrive in Tier 6 #32. Append-only past Jukebox=116.
        Disc13      = 117, // Alpha 2256
        DiscCat     = 118, // Alpha 2257

        // Tier 4 #19 — Armor. 20 pieces in 5 materials × 4 slots
        // (Helmet/Chestplate/Leggings/Boots). Held items, not placeable
        // blocks; equipped via the four armor slots appended to the
        // inventory at indices 45..48 (see Inventory.ArmorStart). Each
        // piece reduces incoming damage by a flat point value (see
        // BlockData.GetArmorReduction); the per-tier sums match Alpha
        // 1.1.2_01: leather=7, chain=12, iron=15, diamond=20, gold=11.
        //
        // Chainmail (Alpha 302..305) is mob-drop-ONLY — no recipe, no
        // creative-catalog gap. Zombie / Skeleton roll a 0.5 % chance
        // per piece on death (see HostileMobs SpawnDeathDrops). The
        // Alpha numeric ids are baked into ItemType.AlphaId for future
        // multiplayer-protocol parity.
        //
        // The block-id slice is contiguous and ordered material-major,
        // slot-minor (Helmet, Chestplate, Leggings, Boots) so
        // GetArmorSlot can compute the slot from `((byte)t -
        // LeatherHelmet) % 4` without a switch. Append-only past
        // DiscCat=118 keeps existing v8..v11 saves byte-stable.
        LeatherHelmet      = 119, // Alpha 298
        LeatherChestplate  = 120, // Alpha 299
        LeatherLeggings    = 121, // Alpha 300
        LeatherBoots       = 122, // Alpha 301
        ChainmailHelmet    = 123, // Alpha 302 (mob-drop only)
        ChainmailChestplate= 124, // Alpha 303 (mob-drop only)
        ChainmailLeggings  = 125, // Alpha 304 (mob-drop only)
        ChainmailBoots     = 126, // Alpha 305 (mob-drop only)
        IronHelmet         = 127, // Alpha 306
        IronChestplate     = 128, // Alpha 307
        IronLeggings       = 129, // Alpha 308
        IronBoots          = 130, // Alpha 309
        DiamondHelmet      = 131, // Alpha 310
        DiamondChestplate  = 132, // Alpha 311
        DiamondLeggings    = 133, // Alpha 312
        DiamondBoots       = 134, // Alpha 313
        GoldHelmet         = 135, // Alpha 314
        GoldChestplate     = 136, // Alpha 315
        GoldLeggings       = 137, // Alpha 316
        GoldBoots          = 138, // Alpha 317

        // Tier 6 #32 — Mob spawner block. Placed by the dungeon
        // generator at the centre of each cobble room. Alpha 1.1.2
        // numeric id is 52. Append-only past GoldBoots=138 keeps
        // existing v8..v13 saves byte-stable. Functional spawning
        // behaviour is a follow-up; the block currently sits as a
        // decorative cage cube the player can break (drops nothing —
        // matches Alpha — and is not obtainable from the catalog).
        MobSpawner         = 139, // Alpha 52

        // Tier 6 #34 — Fire block. Placed by Flint and Steel; lives
        // in air cells above flammable blocks. Cross-sprite render
        // (like flowers), non-solid, instant-break, emits light=14.
        // Spreads via a per-tick random walk to neighbouring flammable
        // cells; eventually goes out unless adjacent to lava (which
        // re-ignites it). Damages the player on contact (cosmetic
        // damage tick — handled in ApplySurvivalDamage).
        Fire               = 140, // Alpha 51
        // Tier 6 #37 — Snow biome surface block. Full opaque cube of
        // packed snow used as the topmost surface in the Snow biome.
        // Distinct from grass-with-a-snow-layer (which Alpha had as a
        // separate "snow layer" 1/8-block partial occluder) — we use
        // a full cube to keep the block list small and the chunk
        // mesher simple. SilkTouch-only drop (no item form yet).
        SnowBlock          = 141, // Alpha 80
        // Tier 6 #37 — Desert biome flora. Cactus is a 3-tall column
        // (placed by terrain gen, the player can stack-break it but
        // it doesn't auto-grow yet — Phase 3+). Ice is a translucent
        // cube that caps water surfaces in the Snow biome. DeadBush
        // is a cross-sprite plant scattered sparsely on desert sand
        // (matches Alpha's brown twigs).
        Cactus             = 142, // Alpha 81
        Ice                = 143, // Alpha 79
        // Tier 6 #37 — Pumpkin patch block (re-added after the
        // earlier removal). Halloween Update / Alpha 1.1.0 added it,
        // which is in scope for our Alpha 1.1.2_01 target. Plain
        // orange-ridged cube with a stem-on-top tile; the carved
        // jack-o-lantern variant is a separate id Alpha had at 91 —
        // Tier 8 #51 will wire that as the lit variant.
        Pumpkin            = 144, // Alpha 86
        // Tier 8 #47 — Sapling. Cross-sprite plant placed on
        // grass/dirt; grows into an oak tree over a randomised
        // tick window (TickSaplings inside World). Drops from
        // leaves at ~5 % per break (Alpha rate), and the same id
        // serves as both the placeable block and the carryable
        // item — Alpha kept those unified for sapling.
        Sapling            = 145, // Alpha 6
        // Tier 8 #48 — Note Block. Right-click increments the per-
        // cell pitch (0..24, stored in low 5 bits of meta) and
        // plays a click placeholder sound. Real procedural pitched
        // audio is a follow-up; without redstone (Tier 8 #42 still
        // pending) the only trigger is the right-click itself,
        // which mirrors Alpha's "click to advance + play" coupling.
        NoteBlock          = 146, // Alpha 25
        // Tier 8 #42 — Redstone primitives, first slice. The torch is
        // implemented in two block types: RedstoneTorchOn (emits
        // light = 7, the canonical Alpha glow) and RedstoneTorchOff
        // (zero emission, dark red sprite). Without the power-prop
        // simulation those state transitions never fire on their
        // own — the torch placed by hand is permanently the "on"
        // variant — but having both block types in the enum means
        // the simulation pass landing later is a one-line SetBlock
        // swap rather than another save-format bump. RedstoneDust
        // is the inventory item that wires + torches both craft
        // from / drop as.
        RedstoneTorchOn    = 147, // Alpha 76
        RedstoneTorchOff   = 148, // Alpha 75
        RedstoneDust       = 149, // Alpha 331
        // Tier 8 #42 — Redstone Wire (placed-block form). Lies flat
        // on top of its supporting block as a 1-pixel slab. Drops
        // as RedstoneDust item when broken; placed by right-clicking
        // a RedstoneDust onto the top face of a solid block. Power
        // propagation simulation is the follow-up — for now the
        // wire is a passive visual without signal flow.
        RedstoneWire       = 150, // Alpha 55
        // Tier 8 #42 — Input blocks. All five drive the power
        // propagation system: lever (toggle on/off), buttons (press
        // for ~10 ticks then release), pressure plates (pressed
        // while any entity stands on them). Metadata low bit = on/
        // pressed state; the power BFS reads this each tick and
        // pushes 15-level signal into adjacent wires.
        Lever              = 151, // Alpha 69
        StoneButton        = 152, // Alpha 77 (Wood Button was Beta-era — out of scope)
        StonePressurePlate = 153, // Alpha 70
        WoodPressurePlate  = 154, // Alpha 72

        // Tier 8 #44 — Signs. Three entries cover the full feature:
        //   SignPost (Alpha 63) — free-standing sign-on-a-post block
        //     placed on top of a solid block. Renders as a 2×16×2
        //     pole + a 16×8×1 board oriented to one of 16 yaw steps.
        //     We expose 4 cardinal facings (matching the existing
        //     BlockFacing enum) — finer rotation is a future polish
        //     item if the canonical Alpha 16-step rotation matters
        //     more than the implementation simplicity.
        //   WallSign (Alpha 68) — wall-mounted sign placed on the
        //     side face of a block. Renders as a 16×8×1.5 board
        //     hugging the supporting wall, oriented along the same
        //     BlockFacing space.
        //   SignItem (Alpha 323) — the inventory item the player
        //     crafts and holds. Right-clicking with one held places
        //     either SignPost (top face) or WallSign (side face),
        //     opens the editor, and consumes one from the stack.
        //
        // All three are append-only past WoodPressurePlate=154 so
        // existing v8..v13 saves stay byte-stable. Pre-v14 worlds
        // load with no signs in the world (the BlockType range
        // wasn't populated, so chunk byte streams couldn't reference
        // these ids); v14 adds a trailing per-sign tile-entity block
        // to persist the typed text + facing alongside the existing
        // furnace/chest/jukebox tail blocks.
        //
        // Texture: per user direction, signs reuse the existing
        // PlanksOak atlas tile rather than carrying a sign-specific
        // texture. The board, post, and wall plate all sample the
        // plank tile so a placed sign looks like a panel of the same
        // wood the rest of the world's planks-grade structures use.
        SignPost           = 155, // Alpha 63
        WallSign           = 156, // Alpha 68
        SignItem           = 157, // Alpha 323

        // Tier 8 #50 — Bone + Bone Meal. Skeleton drop + crafted
        // dye. Bone is the canonical Alpha 1.0.14 skeleton drop
        // (1..2 per kill); 1 Bone crafts shapelessly into 3 Bone
        // Meal. Right-clicking a wheat block / sapling with Bone
        // Meal advances its growth stage — wheat ticks one stage
        // closer to ripe; saplings have a 50% chance to grow into
        // a tree immediately.
        //
        // Both are pure inventory items (no in-world block form),
        // so they fold into the existing IsItem range past
        // SignItem with no per-block IsCube / IsSolid / IsOpaque
        // branches needed.
        Bone               = 158, // Alpha 352
        BoneMeal           = 159, // Alpha 351 (variant 15)

        // Tier 8 #46 — Ladder. Wall-mounted climbing block placed
        // on the side face of a solid block. Per-cell metadata
        // low-2-bits stores the BlockFacing of the wall the ladder
        // is attached to (0=North, 1=East, 2=South, 3=West) — same
        // packing convention every other facing-aware block uses.
        // Player physics overrides gravity when the player AABB
        // overlaps a ladder cell: hold Space → climb up, hold
        // Sneak → climb down, neither → slow descent. Drops as a
        // Ladder block on break and crafts from 7 sticks in an
        // H-shape (rails on cols 0 + 2, rungs on col 1 rows 0-2).
        Ladder             = 160, // Alpha 65

        // Tier 8 #46 part 2 — Wooden Fence. Alpha 85 — placed-block
        // form is a 4×16×4 wood post pinned at the cell centre with
        // 2×3×8 connection arms reaching toward each neighbouring
        // fence / solid full-cube block. The mesher samples the four
        // horizontal neighbours and emits an arm only on sides that
        // actually have a connector — the player can walk diagonally
        // through a row of fences so a single picket doesn't form a
        // 4-arm cross of geometry that protrudes into empty cells.
        //
        // Player-collision uses the same central post as a partial
        // AABB; jumping a single fence is technically possible with a
        // running start (the v1 collision is full-cell-height = 16/16
        // rather than the canonical 24/16 that extends into the cell
        // above). The 1.5-cell height is a follow-up; v1 ships
        // 16/16 for simplicity.
        //
        // Texture: reuses TilePlanks for every face of every box —
        // canonical Alpha shipped fences with the planks tile too.
        Fence              = 161, // Alpha 85

        // Tier 8 #51 — Halloween Update: Jack-o-lantern. Lit pumpkin
        // variant created by right-clicking a placed Pumpkin block
        // with Flint and Steel held. Emits light=15 (same as the
        // brightest sources in Alpha — torch is 14, this is a hair
        // brighter so a Halloween-themed cave reads visibly different
        // from a torch-lit one). Per-cell metadata low-2-bits stores
        // a BlockFacing for which side the carved face points (the
        // side the player was on when they ignited the pumpkin) —
        // the mesher's GetTileIndexForOriented samples
        // TileJackOLanternFront on that face and TilePumpkinSide
        // on the other three sides + bottom, mirroring the
        // furnace's front/side dispatch.
        //
        // Drops as a JackOLantern block (not a Pumpkin) when broken,
        // so the lit state survives mining-and-replacing — no need
        // to re-flint after every move.
        JackOLantern       = 162, // Alpha 91

        // Tier 8 #45 V1 — Half-block slabs. Four material variants
        // mirror canonical Alpha 1.0.5_01's slab metadata family
        // (44:0=Stone, 44:2=Wood, 44:3=Cobblestone, 44:4=Brick) but
        // we use four distinct BlockTypes rather than a metadata-
        // discriminated single id because per-cell metadata in this
        // codebase already encodes facing for several other block
        // types — keeping slabs in their own id space avoids a
        // facing-vs-material collision in the meta byte.
        //
        // All four variants render as a 1×0.5×1 sub-cube pinned to
        // the cell bottom (no top-half / upside-down slabs in V1 —
        // that's a Beta 1.3 mechanic which we'll add when stairs
        // ship in #45 V2). Collision matches the visual mesh, so
        // the player auto-steps onto a slab without jumping
        // (MaxAutoStepHeight = 0.55 covers it).
        //
        // Stairs (Alpha 53 + 67) are deferred to #45 V2 — they need
        // a multi-AABB collision system for their L-shape, which is
        // a bigger refactor than the slab work itself.
        StoneSlab          = 163, // Alpha 44:0
        CobblestoneSlab    = 164, // Alpha 44:3
        BrickSlab          = 165, // Alpha 44:4
        WoodSlab           = 166, // Alpha 44:2

        // Tier 8 #51 — Halloween Update: Glowstone block + dust.
        //
        // Glowstone is a full opaque cube emitting light=15 (the
        // brightest fixed source in Alpha; matches Jack-o-lantern).
        // In canonical Alpha 1.1.2_01 it generated only in the
        // Nether, which we haven't shipped yet — so for now the
        // block is creative-catalog only on the inventory side. The
        // crafting loop still works: place + break a glowstone block
        // (drops 2..4 dust), then 4 dust → 1 block in a 2×2 craft.
        //
        // GlowstoneDust is a pure inventory item — drops from a
        // broken glowstone block, crafts back into one. Folds into
        // the existing IsItem range past the slabs with no per-block
        // mesher / collision branches needed.
        Glowstone          = 167, // Alpha 89
        GlowstoneDust      = 168, // Alpha 348

        // Tier 8 #45 V2 — Wooden + Cobblestone stairs. Canonical
        // Alpha 1.0.5_01 staircase blocks. Per-cell metadata
        // low-2-bits stores the facing direction (the side the
        // upper half-step sits on — i.e., the direction the player
        // ascends UP when climbing). 0=North, 1=East, 2=South,
        // 3=West using the standard BlockFacing enum.
        //
        // Geometry is an L-shape from the side: a 1×0.5×1 lower
        // step (full cell footprint, half height) PLUS a
        // 0.5×0.5×1 (or 1×0.5×0.5) upper step on the back half.
        // Collision uses two AABBs per cell — see
        // BlockData.TryGetExtraCollisionAabb.
        //
        // Player auto-step (MaxAutoStepHeight = 0.55) covers both
        // the lower step (0.5 tall) and the lower → upper jump
        // within the same cell, so a continuous staircase climbs
        // smoothly without the player having to jump on each step.
        WoodStairs         = 169, // Alpha 53
        CobblestoneStairs  = 170, // Alpha 67

        // Tier 8 #49 V1 — Dispenser. 9-slot tile entity facing
        // outward; redstone signal pops one item from a random
        // non-empty slot and ejects it as a DroppedItem in the
        // facing direction. Reuses the chest tile-entity dictionary
        // pattern (per-position persistent state on World) plus
        // furnace-style facing on the entity (mesher reads facing
        // for the front-face tile via GetTileIndexForOriented).
        //
        // Texture: front face = canonical Alpha terrain.png (14, 2)
        // (the "loaded crossbow" silhouette); 3 lateral sides
        // reuse the furnace side panel; top + bottom reuse the
        // furnace top tile (stone cap with iron vent).
        //
        // V1 ships block + facing + crafting + redstone-driven
        // ejection + drop-on-break (with inventory spill). V2
        // polish: a 3×3 inventory UI so the player can manually
        // load items rather than relying on creative + future
        // hopper-equivalent tiers.
        Dispenser          = 171, // Alpha 23

        // Tier 8 #51 — Halloween Update: Netherrack. Soft red rock
        // that generates the bulk of the Nether dimension's terrain.
        // V1 ships the block — proper texture, low hardness (axe-
        // bypassable like stone), drops itself on break, full cube
        // shape. The Alpha-canonical "lava and fire never burn out
        // when adjacent to netherrack" behaviour is V2 polish: it
        // requires the fluid sim to special-case the block (currently
        // lava spreads identically through all flowable cells), and
        // the fire system to skip the burn-out timer when the
        // supporting block is netherrack.
        //
        // No natural source until the nether dimension lands; V1 is
        // creative-catalog-only on the obtain side, mirroring how
        // glowstone shipped without a vanilla spawn route.
        Netherrack         = 172, // Alpha 87

        // Tier 8 #51 — Halloween Update: Soul Sand. Brown haunted-
        // looking sand variant native to the Nether. 14/16 tall (the
        // top 2/16 is air — the player visually sinks into it) with
        // a horizontal-velocity slowdown that drops the player's
        // speed to ~40% while standing on it. The slowdown is the
        // signature gameplay feature; the slight subsidence is
        // visual polish that pairs with it.
        //
        // Drops itself on break. Hardness 0.5 (soft like sand).
        // No natural source until the nether dimension lands;
        // creative-catalog-only on the obtain side, mirroring
        // glowstone + netherrack.
        SoulSand           = 173, // Alpha 88
    }

    // Parallel "ItemType" surface — a static class rather than a
    // standalone enum because the underlying values still live in the
    // BlockType id space (so existing ItemStack / Inventory / save code
    // doesn't fork). Use ItemType.Stick at call sites that want to read
    // "I'm spawning a Stick *item*"; functionally identical to
    // BlockType.Stick. AlphaId / Name centralise the metadata that's
    // specifically item-shaped (numeric Alpha id, display name).
    internal static class ItemType
    {
        public const BlockType Stick     = BlockType.Stick;
        public const BlockType Coal      = BlockType.Coal;
        public const BlockType IronIngot = BlockType.IronIngot;
        public const BlockType GoldIngot = BlockType.GoldIngot;
        public const BlockType Diamond   = BlockType.Diamond;
        public const BlockType Flint     = BlockType.Flint;
        public const BlockType ClayBall  = BlockType.ClayBall;
        public const BlockType ClayBrick = BlockType.ClayBrick;
        public const BlockType Bowl      = BlockType.Bowl;
        public const BlockType RawPorkchop    = BlockType.RawPorkchop;
        public const BlockType CookedPorkchop = BlockType.CookedPorkchop;
        public const BlockType Bow            = BlockType.Bow;
        public const BlockType Arrow          = BlockType.Arrow;
        public const BlockType String         = BlockType.String;
        public const BlockType Gunpowder      = BlockType.Gunpowder;
        public const BlockType Leather        = BlockType.Leather;
        public const BlockType Feather        = BlockType.Feather;
        public const BlockType Egg            = BlockType.Egg;
        // Tier 4 #14 — farming items. WheatSeeds plants on Farmland;
        // WheatItem is the harvested grain (the BlockType is named
        // WheatItem to distinguish it from the in-world Wheat block);
        // Bread + MushroomStew are the food outputs. MushroomStew is
        // the first item with container-return semantics (consuming
        // restores the Bowl) — the renderer's eat path special-cases it.
        public const BlockType WheatSeeds   = BlockType.WheatSeeds;
        public const BlockType WheatItem    = BlockType.WheatItem;
        public const BlockType Bread        = BlockType.Bread;
        public const BlockType MushroomStew = BlockType.MushroomStew;
        // Tier 4 #26 — Sugar cane harvested item + Paper + Book.
        // SugarCaneItem is the drop from breaking an in-world SugarCane
        // block; Paper crafts from 3 cane in a row, Book from 3 paper
        // shapeless. All three live in the BlockType id space the same
        // way every other item does (IsItem branches the mesher /
        // placement / collision paths).
        public const BlockType SugarCaneItem = BlockType.SugarCaneItem;
        public const BlockType Paper         = BlockType.Paper;
        public const BlockType Book          = BlockType.Book;
        // Tier 4 #16 — Door items. Wood + Iron only (matches Alpha
        // 1.1.2_01 — diamond/gold doors weren't a thing). Placement
        // spawns two block halves per door; this item is what the
        // player crafts, picks up, and what drops on break.
        public const BlockType WoodDoorItem = BlockType.WoodDoorItem;
        public const BlockType IronDoorItem = BlockType.IronDoorItem;
        // Tier 4 #17 — Bow combat ammo / food / flint-and-steel.
        // Bow + Arrow already existed in the enum at Tier 3 #10 as
        // inert collectibles; #17 wires them into real projectile
        // combat. FlintAndSteel + Apple are appended past
        // IronDoorItem.
        public const BlockType FlintAndSteel = BlockType.FlintAndSteel;
        public const BlockType Apple         = BlockType.Apple;
        // Tier 4 #20 — Snowball throwable. Egg (already on the list as
        // BlockType.Egg) shares the projectile path but doesn't need a
        // new alias here — it kept its Tier 3 #12 alias.
        public const BlockType Snowball      = BlockType.Snowball;
        // Tier 4 #15 — Bucket family. Empty bucket fills with Water /
        // Lava sources or with Milk via RMB on a Cow; filled buckets
        // dispense back into the world (Milk is currently a no-op until
        // potion effects arrive — see TryInteract for the rationale).
        public const BlockType BucketEmpty   = BlockType.BucketEmpty;
        public const BlockType BucketWater   = BlockType.BucketWater;
        public const BlockType BucketLava    = BlockType.BucketLava;
        public const BlockType BucketMilk    = BlockType.BucketMilk;
        // Tier 4 #18 — Slimeball drop from small slimes.
        public const BlockType Slimeball     = BlockType.Slimeball;
        // Tier 4 #22 — Compass. Recipe-deferred until Tier 8 #42
        // ships redstone dust; until then the item is creative-only.
        public const BlockType Compass       = BlockType.Compass;
        // Tier 4 #21 — Saddle. Alpha 1.1.2_01 has NO craft for saddles
        // — only dungeon-chest loot. Dungeons land in Tier 6 #32, so
        // until then the saddle ships as a creative-catalog-only entry
        // and the recipe gap is documented in features.md.
        public const BlockType Saddle        = BlockType.Saddle;
        // Tier 4 #23 — Fishing Rod. Recipe is the Alpha-canonical
        // diagonal stick + string ladder; cast/reel mechanic lives
        // entirely in GameRenderer.TryInteract + the Bobber entity
        // (no world block, no tile-entity, no save data — all
        // ephemeral state on the player).
        public const BlockType FishingRod    = BlockType.FishingRod;
        // Tier 4 #24 — Painting. Held item; RMB-on-wall installs a
        // Painting entity (see Game/Painting.cs). The placement /
        // break / persist plumbing lives in GameRenderer and World;
        // ItemType just exposes the constant + Alpha-id + display
        // name the way every other item does. Recipe is the canonical
        // 8-stick frame around 1 wool (CraftingRecipes).
        public const BlockType Painting      = BlockType.Painting;
        // Tier 4 #25 — Music discs (Alpha 2256 / 2257). The Jukebox
        // BLOCK isn't aliased here (it's a placeable block, not an
        // item form — IsItem stays false for it); the two disc IDs
        // ARE held items so they get aliases the same way every other
        // ItemType entry does.
        public const BlockType Disc13        = BlockType.Disc13;
        public const BlockType DiscCat       = BlockType.DiscCat;
        // Tier 4 #19 — Armor. 20 pieces (5 materials × 4 slots). Aliased
        // here the same way every other ItemType entry is so the
        // ItemType.* surface stays a complete catalogue of held-item
        // ids. Chainmail is included even though it has no recipe —
        // the alias is what mob-drop spawns + creative-catalog gating
        // reference, and there's no functional difference between an
        // alias for a craftable item vs a drop-only one.
        public const BlockType LeatherHelmet       = BlockType.LeatherHelmet;
        public const BlockType LeatherChestplate   = BlockType.LeatherChestplate;
        public const BlockType LeatherLeggings     = BlockType.LeatherLeggings;
        public const BlockType LeatherBoots        = BlockType.LeatherBoots;
        public const BlockType ChainmailHelmet     = BlockType.ChainmailHelmet;
        public const BlockType ChainmailChestplate = BlockType.ChainmailChestplate;
        public const BlockType ChainmailLeggings   = BlockType.ChainmailLeggings;
        public const BlockType ChainmailBoots      = BlockType.ChainmailBoots;
        public const BlockType IronHelmet          = BlockType.IronHelmet;
        public const BlockType IronChestplate      = BlockType.IronChestplate;
        public const BlockType IronLeggings        = BlockType.IronLeggings;
        public const BlockType IronBoots           = BlockType.IronBoots;
        public const BlockType DiamondHelmet       = BlockType.DiamondHelmet;
        public const BlockType DiamondChestplate   = BlockType.DiamondChestplate;
        public const BlockType DiamondLeggings     = BlockType.DiamondLeggings;
        public const BlockType DiamondBoots        = BlockType.DiamondBoots;
        public const BlockType GoldHelmet          = BlockType.GoldHelmet;
        public const BlockType GoldChestplate      = BlockType.GoldChestplate;
        public const BlockType GoldLeggings        = BlockType.GoldLeggings;
        public const BlockType GoldBoots           = BlockType.GoldBoots;
        // Tier 8 #44 — Sign item. Held by the player after crafting (6
        // planks + 1 stick → 1 sign), placed via RMB on a top face
        // (becomes SignPost) or side face (becomes WallSign). The two
        // in-world block ids (BlockType.SignPost / BlockType.WallSign)
        // are NOT in the ItemType surface — they only ever exist in
        // chunk byte streams, never in the player's inventory. The
        // SignItem alias is the only sign-shaped name code outside
        // the placement path needs to know.
        public const BlockType SignItem            = BlockType.SignItem;
        // Tier 8 #50 — Bone + Bone Meal. Both are inventory-only
        // items (no in-world block form). Bone drops from skeletons
        // 1..2 per kill; 1 Bone crafts shapelessly to 3 Bone Meal;
        // Bone Meal right-click on a wheat block advances its
        // growth stage, on a sapling rolls a 50% chance to grow it
        // into a tree on the spot.
        public const BlockType Bone                = BlockType.Bone;
        public const BlockType BoneMeal            = BlockType.BoneMeal;
        // Tier 8 #51 — Glowstone Dust item alias (block form is in
        // BlockType only — the dust item is what crafts into the
        // glowing block and drops from breaking one).
        public const BlockType GlowstoneDust       = BlockType.GlowstoneDust;

        // Alpha 1.1.2_01 numeric item id (256..346 + 2256/2257). Returns
        // -1 for non-items. Not yet used at runtime — kept for the
        // save-format work in Tier 9 #49 (autosave / backup) and for
        // future multiplayer-protocol parity (Tier 9 #51).
        public static int AlphaId(BlockType t)
        {
            switch (t)
            {
                case BlockType.Stick:     return 280;
                case BlockType.Coal:      return 263;
                case BlockType.IronIngot: return 265;
                case BlockType.GoldIngot: return 266;
                case BlockType.Diamond:   return 264;
                case BlockType.Flint:     return 318;
                case BlockType.ClayBall:  return 337;
                case BlockType.ClayBrick: return 336;
                case BlockType.Bowl:           return 281;
                case BlockType.RawPorkchop:    return 319;
                case BlockType.CookedPorkchop: return 320;
                case BlockType.Bow:            return 261;
                case BlockType.Arrow:          return 262;
                case BlockType.String:         return 287;
                case BlockType.Gunpowder:      return 289;
                case BlockType.Leather:        return 334;
                case BlockType.Feather:        return 288;
                case BlockType.Egg:            return 344;
                // Tier 4 #14 — hoes + farming items.
                case BlockType.WoodHoe:        return 290;
                case BlockType.StoneHoe:       return 291;
                case BlockType.IronHoe:        return 292;
                case BlockType.DiamondHoe:     return 293;
                case BlockType.GoldHoe:        return 294;
                case BlockType.WheatSeeds:     return 295;
                case BlockType.WheatItem:      return 296;
                case BlockType.Bread:          return 297;
                case BlockType.MushroomStew:   return 282;
                // Tier 4 #26 — Sugar cane drop + paper + book.
                case BlockType.SugarCaneItem:  return 338;
                case BlockType.Paper:          return 339;
                case BlockType.Book:           return 340;
                // Tier 4 #16 — door items. Alpha numeric ids 324
                // (wooden) and 330 (iron); kept here for save-format
                // and future multiplayer-protocol parity work.
                case BlockType.WoodDoorItem:   return 324;
                case BlockType.IronDoorItem:   return 330;
                // Tier 4 #17 — flint+steel + apple. Alpha numeric ids
                // 259 (FlintAndSteel) and 260 (Apple); kept here for
                // save-format and future multiplayer-protocol parity.
                case BlockType.FlintAndSteel:  return 259;
                case BlockType.Apple:          return 260;
                // Tier 4 #20 — Snowball throwable. Alpha numeric id 332;
                // Egg's Alpha id (344) is already returned by the
                // BlockType.Egg case earlier in the switch.
                case BlockType.Snowball:       return 332;
                // Tier 4 #15 — Bucket family. Alpha numeric ids 325
                // (empty), 326 (water), 327 (lava), 335 (milk).
                case BlockType.BucketEmpty:    return 325;
                case BlockType.BucketWater:    return 326;
                case BlockType.BucketLava:     return 327;
                case BlockType.BucketMilk:     return 335;
                // Tier 4 #18 — Slimeball. Alpha numeric id 341.
                case BlockType.Slimeball:      return 341;
                // Tier 4 #22 — Compass. Alpha numeric id 345.
                case BlockType.Compass:        return 345;
                // Tier 4 #21 — Saddle. Alpha numeric id 329.
                case BlockType.Saddle:         return 329;
                // Tier 4 #23 — Fishing Rod. Alpha numeric id 346.
                case BlockType.FishingRod:     return 346;
                // Tier 4 #24 — Painting. Alpha numeric id 321.
                case BlockType.Painting:       return 321;
                // Tier 4 #25 — Music discs. Alpha numeric ids 2256
                // ("13") and 2257 ("cat"). These are the only ids in
                // the four-digit space we currently track; future
                // Alpha discs (released post-1.1.2) would extend this
                // list. The Jukebox BLOCK has Alpha id 84, but it's
                // not in the IsItem range so AlphaId() is never
                // called for it.
                case BlockType.Disc13:         return 2256;
                case BlockType.DiscCat:        return 2257;
                // Tier 4 #19 — Armor. Alpha numeric ids 298..317
                // contiguous in the canonical order leather → chain →
                // iron → diamond → gold (helmet/chest/leg/boot inside
                // each material). Kept here for save-format / future
                // multiplayer-protocol parity.
                case BlockType.LeatherHelmet:       return 298;
                case BlockType.LeatherChestplate:   return 299;
                case BlockType.LeatherLeggings:     return 300;
                case BlockType.LeatherBoots:        return 301;
                case BlockType.ChainmailHelmet:     return 302;
                case BlockType.ChainmailChestplate: return 303;
                case BlockType.ChainmailLeggings:   return 304;
                case BlockType.ChainmailBoots:      return 305;
                case BlockType.IronHelmet:          return 306;
                case BlockType.IronChestplate:      return 307;
                case BlockType.IronLeggings:        return 308;
                case BlockType.IronBoots:           return 309;
                case BlockType.DiamondHelmet:       return 310;
                case BlockType.DiamondChestplate:   return 311;
                case BlockType.DiamondLeggings:     return 312;
                case BlockType.DiamondBoots:        return 313;
                case BlockType.GoldHelmet:          return 314;
                case BlockType.GoldChestplate:      return 315;
                case BlockType.GoldLeggings:        return 316;
                case BlockType.GoldBoots:           return 317;
                // Tier 8 #44 — Sign item. Alpha numeric id 323. The
                // in-world SignPost (id 63) / WallSign (id 68) blocks
                // aren't items so they never reach AlphaId; only the
                // SignItem alias does.
                case BlockType.SignItem:            return 323;
                // Tier 8 #50 — Bone (canonical Alpha 352) and Bone
                // Meal (canonical Alpha 351:15 — same id 351 with
                // metadata 15 in real Alpha, but our codebase
                // doesn't represent dye-meta variants, so Bone Meal
                // gets its own id and we expose 351 here).
                case BlockType.Bone:                return 352;
                case BlockType.BoneMeal:            return 351;
                // Tier 8 #51 — Glowstone Dust at canonical Alpha 348.
                // The block form (Alpha 89) isn't an item, so AlphaId
                // returns -1 for it via the default fall-through —
                // same convention every other placeable-block form
                // uses (e.g. WoodDoorBlockBottom is non-item).
                case BlockType.GlowstoneDust:       return 348;
                default:                       return -1;
            }
        }

        // Friendly display name with a space between the Camel-case
        // halves where the auto-splitter in CreativeCatalog wouldn't
        // catch it ("IronIngot" → "Iron Ingot"). For uniformity the
        // item names go through a single source of truth here so the
        // creative catalog, hotbar label, and tooltips all agree.
        public static string Name(BlockType t)
        {
            switch (t)
            {
                case BlockType.Stick:     return "Stick";
                case BlockType.Coal:      return "Coal";
                case BlockType.IronIngot: return "Iron Ingot";
                case BlockType.GoldIngot: return "Gold Ingot";
                case BlockType.Diamond:   return "Diamond";
                case BlockType.Flint:     return "Flint";
                case BlockType.ClayBall:  return "Clay Ball";
                case BlockType.ClayBrick: return "Clay Brick";
                case BlockType.Bowl:           return "Bowl";
                case BlockType.RawPorkchop:    return "Raw Porkchop";
                case BlockType.CookedPorkchop: return "Cooked Porkchop";
                case BlockType.Bow:            return "Bow";
                case BlockType.Arrow:          return "Arrow";
                case BlockType.String:         return "String";
                case BlockType.Gunpowder:      return "Gunpowder";
                case BlockType.Leather:        return "Leather";
                case BlockType.Feather:        return "Feather";
                case BlockType.Egg:            return "Egg";
                case BlockType.WoodHoe:        return "Wooden Hoe";
                case BlockType.StoneHoe:       return "Stone Hoe";
                case BlockType.IronHoe:        return "Iron Hoe";
                case BlockType.DiamondHoe:     return "Diamond Hoe";
                case BlockType.GoldHoe:        return "Gold Hoe";
                case BlockType.WheatSeeds:     return "Seeds";
                case BlockType.WheatItem:      return "Wheat";
                case BlockType.Bread:          return "Bread";
                case BlockType.MushroomStew:   return "Mushroom Stew";
                case BlockType.SugarCaneItem:  return "Sugar Cane";
                case BlockType.Paper:          return "Paper";
                case BlockType.Book:           return "Book";
                // Tier 4 #16 — Door item names. Wood/Iron only —
                // matches the Alpha 1.1.2_01 craft set; the player
                // never sees the per-half block ids in any UI, so
                // names are only needed for the item form.
                case BlockType.WoodDoorItem:   return "Wooden Door";
                case BlockType.IronDoorItem:   return "Iron Door";
                case BlockType.FlintAndSteel:  return "Flint and Steel";
                case BlockType.Apple:          return "Apple";
                case BlockType.Snowball:       return "Snowball";
                // Tier 4 #15 — Bucket family. Empty bucket is just
                // "Bucket" (no qualifier — matches Alpha tooltip);
                // filled variants get a "<contents> Bucket" name.
                case BlockType.BucketEmpty:    return "Bucket";
                case BlockType.BucketWater:    return "Water Bucket";
                case BlockType.BucketLava:     return "Lava Bucket";
                case BlockType.BucketMilk:     return "Milk Bucket";
                case BlockType.Slimeball:      return "Slimeball";
                case BlockType.Compass:        return "Compass";
                case BlockType.Saddle:         return "Saddle";
                case BlockType.FishingRod:     return "Fishing Rod";
                case BlockType.Painting:       return "Painting";
                // Tier 4 #25 — Music disc display names. Alpha
                // 1.1.2_01 inventory tooltips use the form
                // "Music Disc - <track>" (with the title-cased track
                // name). Match that here so tooltips and the
                // creative catalog read canonically.
                case BlockType.Disc13:         return "Music Disc - 13";
                case BlockType.DiscCat:        return "Music Disc - cat";
                // Tier 4 #19 — Armor display names. Alpha 1.1.2_01
                // tooltips use "<Material> <Slot>" with both halves
                // capitalised and a single space (e.g. "Iron Chestplate"
                // — never "Iron-Chestplate" or "IronChestplate").
                case BlockType.LeatherHelmet:       return "Leather Cap";
                case BlockType.LeatherChestplate:   return "Leather Tunic";
                case BlockType.LeatherLeggings:     return "Leather Pants";
                case BlockType.LeatherBoots:        return "Leather Boots";
                case BlockType.ChainmailHelmet:     return "Chain Helmet";
                case BlockType.ChainmailChestplate: return "Chain Chestplate";
                case BlockType.ChainmailLeggings:   return "Chain Leggings";
                case BlockType.ChainmailBoots:      return "Chain Boots";
                case BlockType.IronHelmet:          return "Iron Helmet";
                case BlockType.IronChestplate:      return "Iron Chestplate";
                case BlockType.IronLeggings:        return "Iron Leggings";
                case BlockType.IronBoots:           return "Iron Boots";
                case BlockType.DiamondHelmet:       return "Diamond Helmet";
                case BlockType.DiamondChestplate:   return "Diamond Chestplate";
                case BlockType.DiamondLeggings:     return "Diamond Leggings";
                case BlockType.DiamondBoots:        return "Diamond Boots";
                case BlockType.GoldHelmet:          return "Gold Helmet";
                case BlockType.GoldChestplate:      return "Gold Chestplate";
                case BlockType.GoldLeggings:        return "Gold Leggings";
                case BlockType.GoldBoots:           return "Gold Boots";
                // Tier 8 #44 — Sign item display name. Alpha tooltip
                // is just "Sign" (no qualifier — the in-world post vs
                // wall variant is decided at placement, not by the
                // item form, so the inventory shows the same name
                // regardless of which face the player ends up using).
                case BlockType.SignItem:            return "Sign";
                // Tier 8 #50 — Bone Meal needs the space inserted
                // explicitly; Bone reads fine as a single word
                // and falls through to the ToString default.
                case BlockType.BoneMeal:            return "Bone Meal";
                // Tier 8 #51 — Glowstone Dust + Glowstone block both
                // need their friendly names since the auto-splitter
                // would render them as "Glowstone Dust" and just
                // "Glowstone" anyway via ToString — but the explicit
                // entries keep the inventory tooltip stable if the
                // enum is ever renamed.
                case BlockType.GlowstoneDust:       return "Glowstone Dust";
                case BlockType.Glowstone:           return "Glowstone";
                default:                       return t.ToString();
            }
        }
    }

    internal static class BlockData
    {
        // "Solid" gates player COLLISION only — used by physics/swept AABB.
        // Non-solid: player walks straight through (Air, fluids, Torch).
        // Raycast targeting and placement-cell occupancy use IsRaycastTarget /
        // IsCubeShape instead so a torch can be broken without colliding with
        // it as the player walks past.
        //
        // Both fluid families (water + lava) include the source AND flowing
        // variants here. Without Lava in the list the source cell would
        // behave as a solid floor under the player while the flowing cells
        // beside it are walk-through, leading to "lava floor, lava waterfall
        // is fine" weirdness. Same shape as water — burning damage will hook
        // in via a separate per-tick fluid-contact check, not via collision.
        public static bool IsSolid(BlockType t)
        {
            if (IsTool(t) || IsItem(t)) return false;
            if (IsTorch(t)) return false;
            switch (t)
            {
                case BlockType.Air:
                case BlockType.Water:
                case BlockType.FlowingWater:
                case BlockType.Lava:
                case BlockType.FlowingLava:
                case BlockType.Dandelion:
                case BlockType.Rose:
                case BlockType.BrownMushroom:
                case BlockType.RedMushroom:
                // Tier 4 #14 — Wheat is a cross-sprite crop (not a
                // cube), the player walks straight through it the same
                // way they walk through flowers. Farmland IS a full
                // cube and falls through to the default solid branch.
                case BlockType.Wheat:
                // Tier 4 #26 — Sugar cane is a cross-sprite plant the
                // player walks through, same shape as wheat / flowers.
                // The block is non-solid even though it can stack
                // vertically (each cell of the stack is independently
                // walk-through).
                case BlockType.SugarCane:
                // Tier 8 #47 — Sapling is a cross-sprite plant; the
                // player walks straight through it the same way they
                // walk through flowers.
                case BlockType.Sapling:
                // Tier 8 #42 — Redstone torches behave like regular
                // torches: cross-sprite, non-solid, light-transparent.
                case BlockType.RedstoneTorchOn:
                case BlockType.RedstoneTorchOff:
                // Tier 8 #42 — Redstone wire is a 1/16 floor slab,
                // never blocks player movement. Walking through it
                // is the canonical Alpha behaviour.
                case BlockType.RedstoneWire:
                // Tier 8 #42 — Lever, button, pressure plates: all
                // non-solid for player movement (walk through / stand
                // on top — the pressure plates are walkable but the
                // collision is handled by the partial AABB, so
                // IsSolid = false here lets the player phase through
                // the cell's empty volume above the slab).
                case BlockType.Lever:
                case BlockType.StoneButton:
                case BlockType.StonePressurePlate:
                case BlockType.WoodPressurePlate:
                // Tier 8 #44 — Signs (post + wall variant). Walk-
                // through in Alpha so the swept AABB skips them; the
                // pole is a thin 2/16-wide square that the player
                // glides past.
                case BlockType.SignPost:
                case BlockType.WallSign:
                // Tier 8 #46 — Ladder. Walk-through (you can stand
                // INSIDE the ladder cell while climbing) — collision
                // is gated entirely by the climb path in player
                // physics, not by the cell being solid. The ladder
                // cell still raycast-targets so LMB breaks it.
                case BlockType.Ladder:
                // Tier 6 #34 — Fire is non-solid; the player walks
                // straight through it (taking damage via the
                // per-tick fluid-contact check pattern rather than
                // collision).
                case BlockType.Fire:
                    return false;
                // Tier 6 #37 Phase 4 — SnowBlock IS solid; its
                // collision shape is a 1/8-tall slab pinned to the
                // cell bottom, returned by GetCollisionAabb. The
                // entity-collision code consults the AABB instead of
                // assuming a full cube — falls through the default
                // `return true` here so the cell is considered
                // collidable, with the AABB function carving out the
                // actual extent.
                //
                // Future blocks (torches, flowers, mushrooms) can
                // adopt the same approach: return true here, return
                // their actual sub-cell AABB from GetCollisionAabb.
                // Currently those still return false and rely on the
                // fast-path "non-solid → no collision" branch in
                // Collides — they don't have visible collision
                // surfaces yet, the player just walks through.
                default:
                    return true;
            }
        }

        // True for any torch placement — floor or wall. Used by the mesher
        // (cross-sprite vs tilted-wall-billboard branch), placement, drops,
        // and torch-fall (ScanTorchFallAround) so a single helper covers
        // every variant. Floor-only callers should compare against
        // BlockType.Torch directly.
        public static bool IsTorch(BlockType t)
            => t == BlockType.Torch || IsWallTorch(t);

        // True only for the wall-mounted variants. The mesher uses this to
        // pick the tilted billboard model and the placement code uses it
        // to read the BlockFacing back out of the block id (TorchFacing).
        public static bool IsWallTorch(BlockType t)
            => t == BlockType.TorchEast || t == BlockType.TorchWest
            || t == BlockType.TorchSouth || t == BlockType.TorchNorth;

        // Cardinal direction a wall torch's flame points (i.e. away from
        // the wall it's attached to). For floor torches and non-torches
        // the return value is meaningless — callers should gate on
        // IsWallTorch first.
        public static BlockFacing WallTorchFacing(BlockType t)
        {
            switch (t)
            {
                case BlockType.TorchEast:  return BlockFacing.East;
                case BlockType.TorchWest:  return BlockFacing.West;
                case BlockType.TorchSouth: return BlockFacing.South;
                case BlockType.TorchNorth: return BlockFacing.North;
                default:                   return BlockFacing.North;
            }
        }

        // Inverse of WallTorchFacing: build the wall-torch BlockType for a
        // given facing. Used by TryPlace to translate the raycast hit's
        // face normal into the appropriate wall variant.
        public static BlockType WallTorchFor(BlockFacing facing)
        {
            switch (facing)
            {
                case BlockFacing.East:  return BlockType.TorchEast;
                case BlockFacing.West:  return BlockType.TorchWest;
                case BlockFacing.South: return BlockType.TorchSouth;
                default:                return BlockType.TorchNorth;
            }
        }

        // True if this BlockType id refers to a tool item rather than a
        // placeable block. The tool ids originally occupied a single
        // contiguous slice [WoodSword..GoldAxe], but Tier 4 #14 added
        // hoes after Egg (couldn't be slotted in [38..57] without
        // shifting Stick=58 and breaking every existing v7 save), so
        // this helper now ORs the hoe range [WoodHoe..GoldHoe] in. Cheap
        // pair of range checks — used by mesher, placement, rendering
        // and inventory paths to take the tool branch without touching
        // the per-block switch tables.
        public static bool IsTool(BlockType t)
            => ((byte)t >= (byte)BlockType.WoodSword && (byte)t <= (byte)BlockType.GoldAxe)
            || ((byte)t >= (byte)BlockType.WoodHoe   && (byte)t <= (byte)BlockType.GoldHoe);

        // Tier 4 #19 — True if this BlockType id refers to one of the 20
        // armor pieces (LeatherHelmet=119 .. GoldBoots=138). The slice
        // is intentionally contiguous and ordered material-major,
        // slot-minor so the slot index can be derived without a switch
        // (see GetArmorSlot below). Used by InventoryScreen to gate
        // drag-drop into the four armor slots — only matching armor
        // pieces may be deposited, attempts to drop a stick into the
        // helmet slot are rejected with a no-op swap.
        public static bool IsArmor(BlockType t)
            => (byte)t >= (byte)BlockType.LeatherHelmet
            && (byte)t <= (byte)BlockType.GoldBoots;

        // Tier 4 #19 — Slot index for an armor piece: 0=Helmet,
        // 1=Chestplate, 2=Leggings, 3=Boots. Returns -1 for non-armor
        // ids. The 20-id slice is laid out in the canonical Alpha order
        // (helmet/chest/leg/boot inside each material) so the slot is
        // simply `(id - LeatherHelmet) % 4`. The InventoryScreen uses
        // this to enforce the per-slot type gate when the player
        // drag-drops a piece — pieces fall straight into the matching
        // slot and bounce off the other three.
        public static int GetArmorSlot(BlockType t)
        {
            if (!IsArmor(t)) return -1;
            return ((byte)t - (byte)BlockType.LeatherHelmet) % 4;
        }

        // Tier 5 #27 — Map a placed-block id to the inventory item-form
        // that should land on the hotbar when middle-clicking it.
        // Most blocks self-map (Stone places & picks as Stone), but a
        // few multi-id features expose a different "item" id from the
        // "in-world block" id:
        //   WoodDoorBlockTop / WoodDoorBlockBottom → WoodDoorItem
        //   IronDoorBlockTop / IronDoorBlockBottom → IronDoorItem
        //   Wheat (any growth stage)               → WheatSeeds (you replant)
        //   SugarCane                              → SugarCaneItem
        // Anything not in the switch self-maps. Used by
        // GameRenderer.TryPickBlock; isolated here so future multi-id
        // features (cake, beds, etc.) get a one-line addition.
        public static BlockType PickBlockItemFor(BlockType placed)
        {
            switch (placed)
            {
                case BlockType.WoodDoorBlockTop:
                case BlockType.WoodDoorBlockBottom: return BlockType.WoodDoorItem;
                case BlockType.IronDoorBlockTop:
                case BlockType.IronDoorBlockBottom: return BlockType.IronDoorItem;
                case BlockType.Wheat:               return BlockType.WheatSeeds;
                case BlockType.SugarCane:           return BlockType.SugarCaneItem;
                default: return placed;
            }
        }

        // Tier 4 #19 — Per-piece flat damage reduction (in HP points)
        // for the Alpha 1.1.2_01 armor formula. The damage formula
        // applied at the Player.TakeDamage call site is:
        //     effective = max(1, originalDamage * (1 - sumReduction/25))
        // capped at 80 % reduction (i.e. a fully-armored player still
        // takes at least 20 % of incoming damage, rounded up to 1).
        // The per-tier sums match the canonical Alpha values:
        //     leather   = 1+3+2+1 = 7
        //     chainmail = 2+5+4+1 = 12
        //     iron      = 2+6+5+2 = 15
        //     diamond   = 3+8+6+3 = 20
        //     gold      = 2+5+3+1 = 11
        // Diamond reaches the 20-point cap; chain/iron/leather/gold
        // sit below it. Returns 0 for non-armor ids so the
        // GetTotalArmorReduction summer can skip the IsArmor gate at
        // its call sites.
        public static int GetArmorReduction(BlockType t)
        {
            switch (t)
            {
                case BlockType.LeatherHelmet:       return 1;
                case BlockType.LeatherChestplate:   return 3;
                case BlockType.LeatherLeggings:     return 2;
                case BlockType.LeatherBoots:        return 1;
                case BlockType.ChainmailHelmet:     return 2;
                case BlockType.ChainmailChestplate: return 5;
                case BlockType.ChainmailLeggings:   return 4;
                case BlockType.ChainmailBoots:      return 1;
                case BlockType.IronHelmet:          return 2;
                case BlockType.IronChestplate:      return 6;
                case BlockType.IronLeggings:        return 5;
                case BlockType.IronBoots:           return 2;
                case BlockType.DiamondHelmet:       return 3;
                case BlockType.DiamondChestplate:   return 8;
                case BlockType.DiamondLeggings:     return 6;
                case BlockType.DiamondBoots:        return 3;
                case BlockType.GoldHelmet:          return 2;
                case BlockType.GoldChestplate:      return 5;
                case BlockType.GoldLeggings:        return 3;
                case BlockType.GoldBoots:           return 1;
                default:                            return 0;
            }
        }

        // True if this BlockType id refers to a non-placeable, non-tool
        // inventory item (Stick, Coal, ingots, gem, Flint, ClayBall /
        // Brick, Bowl, RawPorkchop..Egg). The original ingredient
        // slice [Stick..Bowl] is contiguous, but Tier 3 #9 appended
        // porkchops past the wall-torch ids (70..73), Tier 3 #10
        // appended Bow/Arrow/String/Gunpowder past the porkchops, and
        // Tier 3 #12 appended Leather/Feather/Egg past Gunpowder — so
        // the range check is two slices [Stick..Bowl] +
        // [RawPorkchop..Egg]. New items added past Egg automatically
        // extend the second slice, no edit needed here.
        public static bool IsItem(BlockType t)
            => ((byte)t >= (byte)BlockType.Stick       && (byte)t <= (byte)BlockType.Bowl)
            || ((byte)t >= (byte)BlockType.RawPorkchop && (byte)t <= (byte)BlockType.Egg)
            // Tier 4 #14 — farming items (WheatSeeds, WheatItem, Bread,
            // MushroomStew) live past the hoe slice. Append-only past
            // this range automatically extends IsItem — bumped in
            // Tier 4 #26 to include SugarCaneItem / Paper / Book.
            // Note that SugarCane (the in-world block at id 94) sits
            // BETWEEN MushroomStew and SugarCaneItem; the slice gap
            // skips it because it's a placeable block, not an item,
            // and falls through to the explicit per-block IsCubeShape
            // / IsSolid / IsOpaque branches below.
            || ((byte)t >= (byte)BlockType.SugarCaneItem && (byte)t <= (byte)BlockType.Book)
            || ((byte)t >= (byte)BlockType.WheatSeeds  && (byte)t <= (byte)BlockType.MushroomStew)
            // Tier 4 #16 — Door items. Block halves (98..101) sit
            // BETWEEN Book and the door items, but they're placeable
            // in-world blocks (raycast-targetable, mesher-rendered)
            // — so the slice intentionally JUMPS over them and only
            // covers the two item ids (102..103). Adding the ranges
            // separately keeps the per-id branches in IsSolid /
            // IsCubeShape / IsOpaque from being short-circuited by
            // the item early-out.
            || ((byte)t >= (byte)BlockType.WoodDoorItem && (byte)t <= (byte)BlockType.IronDoorItem)
            // Tier 4 #17 — FlintAndSteel + Apple. Both are non-
            // placeable, non-tool items appended past IronDoorItem;
            // future appended item ids extend this range.
            // Tier 4 #20 — Snowball appended past Apple. The slice
            // simply grows the upper bound; same trick every preceding
            // item-pack used.
            // Tier 4 #15 — Bucket family (empty/water/lava/milk) appended
            // past Snowball. Slice upper bound bumps to BucketMilk=110;
            // future appended item ids continue to extend it.
            // Tier 4 #18 — Slimeball appended past BucketMilk. Slice
            // upper bound bumps to Slimeball=111.
            // Tier 4 #22 — Compass appended past Slimeball. Slice upper
            // bound bumps to Compass=112; future appended item ids
            // continue to extend it.
            // Tier 4 #21 — Saddle appended past Compass. Slice upper
            // bound bumps to Saddle=113.
            // Tier 4 #23 — Fishing Rod appended past Saddle. Slice
            // upper bound bumps to FishingRod=114.
            // Tier 4 #24 — Painting appended past FishingRod. Slice
            // upper bound bumps to Painting=115.
            // Tier 4 #25 — Jukebox=116 sits between Painting (last item)
            // and Disc13/DiscCat (first new items). The Jukebox is a
            // PLACEABLE BLOCK, not an item, so the slice splits in two:
            // [FlintAndSteel..Painting] keeps the original items, and
            // [Disc13..DiscCat] adds the two music-disc item ids past
            // the Jukebox block. Append-only past DiscCat=118 keeps
            // existing v8/v9/v10 saves byte-stable.
            || ((byte)t >= (byte)BlockType.FlintAndSteel && (byte)t <= (byte)BlockType.Painting)
            // Tier 4 #19 — Armor pieces (LeatherHelmet..GoldBoots)
            // appended past the discs. Slice upper bound bumps to
            // GoldBoots=138; all 20 ids are held items (placement /
            // collision branches treat them as non-cube items the same
            // way every other item id behaves) and fold cleanly into
            // this contiguous range.
            || ((byte)t >= (byte)BlockType.Disc13       && (byte)t <= (byte)BlockType.GoldBoots)
            // Tier 8 #42 — RedstoneDust item. Standalone id past the
            // RedstoneTorchOn / Off block pair so the per-block
            // IsSolid / IsCubeShape branches still catch the torch
            // ids correctly.
            || t == BlockType.RedstoneDust
            // Tier 8 #44 — Sign item. Standalone id past the
            // SignPost / WallSign in-world blocks so the per-block
            // mesher / collision branches still catch the two block
            // ids correctly. The two block ids never appear in the
            // player's inventory, only the SignItem alias does.
            || t == BlockType.SignItem
            // Tier 8 #50 — Bone + Bone Meal. Pure items appended
            // past SignItem; no in-world block form so they fold
            // straight into the IsItem slice.
            || t == BlockType.Bone
            || t == BlockType.BoneMeal
            // Tier 8 #51 — Glowstone Dust. Block form (Glowstone)
            // is a regular cube and falls through to IsCubeShape /
            // IsSolid / IsOpaque defaults; this is just the item.
            || t == BlockType.GlowstoneDust;

        // "Targetable by raycast" — true for any block the player should be
        // able to LMB-break or RMB-place-against. Air and fluid families are
        // skipped (you raycast through both); torches and future cross-sprite
        // blocks (flowers, mushrooms) are targetable so the player can
        // interact even though they aren't collidable. Both fluid sources
        // (Water / Lava) and their flowing variants are non-targetable —
        // Tier 6 #37 Phase 4 — Per-block collision AABB inside the
        // unit cell, in fractional [0..1] coordinates. Caller adds
        // the cell's world position to get the world-space AABB. The
        // default is the full cube — every existing solid block falls
        // through to that, so the change is invisible to the cube
        // mesh / fluid sim / etc. SnowBlock returns a 1/8-tall slab
        // pinned to the cell bottom so the player can stand ON the
        // snow at Y = cellY + 0.125 instead of phasing through to
        // the grass below.
        //
        // The intent is that future sub-cell blocks (slabs / stairs /
        // partial torches / flower hitboxes) fill out additional
        // cases here; their geometry pass already lives in
        // EmitModels, this just gives them a matching collision
        // shape. Non-solid blocks (IsSolid==false) skip this lookup
        // entirely in the fast path inside Entity.Collides.
        public static (float minX, float minY, float minZ, float maxX, float maxY, float maxZ) GetCollisionAabb(BlockType t)
        {
            switch (t)
            {
                case BlockType.SnowBlock:
                    return (0f, 0f, 0f, 1f, 1f / 8f, 1f);
                // Tier 6 #37 Phase 4 — Cross-sprite flora hitboxes.
                // Flowers and mushrooms render as crossed quads
                // through the cell centre but only take up a fraction
                // of the cell visually. The selection wireframe + the
                // click ray now match what the player actually sees:
                // a slim 0.4-wide tower at the cell base, 0.6 tall
                // for flowers (taller stem) and 0.5 tall for the
                // shorter mushrooms. IsSolid stays false for these so
                // the player walks straight through — only the
                // raycast / outline use this AABB.
                case BlockType.Dandelion:
                case BlockType.Rose:
                    return (0.3f, 0f, 0.3f, 0.7f, 0.6f, 0.7f);
                case BlockType.BrownMushroom:
                case BlockType.RedMushroom:
                    return (0.3f, 0f, 0.3f, 0.7f, 0.5f, 0.7f);
                // Tier 8 #47 — Sapling small seedling (~0.3×0.4×0.3).
                case BlockType.Sapling:
                    return (0.35f, 0f, 0.35f, 0.65f, 0.4f, 0.65f);
                // Tier 8 #42 — Redstone torch shares the regular-
                // torch hitbox: small column at cell centre, ~0.6
                // tall.
                case BlockType.RedstoneTorchOn:
                case BlockType.RedstoneTorchOff:
                    return (0.4f, 0f, 0.4f, 0.6f, 0.6f, 0.6f);
                // Tier 8 #42 — Redstone wire: 1×1/16×1 floor slab so
                // the click area + selection wireframe match the
                // visible thin red line on the floor.
                case BlockType.RedstoneWire:
                    return (0f, 0f, 0f, 1f, 1f / 16f, 1f);
                // Lever — small block at the bottom of the cell.
                // 6/16 wide × 6/16 tall × 6/16 deep, centred.
                case BlockType.Lever:
                    return (5f / 16f, 0f, 5f / 16f, 11f / 16f, 6f / 16f, 11f / 16f);
                // Stone button — small recessed cuboid on the floor.
                // 6/16 wide × 2/16 tall × 4/16 deep.
                case BlockType.StoneButton:
                    return (5f / 16f, 0f, 6f / 16f, 11f / 16f, 2f / 16f, 10f / 16f);
                // Pressure plates — 14/16 footprint, 1/16 thick (the
                // border 1-pixel ring is implied air around the
                // visible plate face). Same dimensions for stone +
                // wood.
                case BlockType.StonePressurePlate:
                case BlockType.WoodPressurePlate:
                    return (1f / 16f, 0f, 1f / 16f, 15f / 16f, 1f / 16f, 15f / 16f);
                // Tier 6 — Torch hitboxes match the 2-pixel-wide
                // wood column rendered by the chunk mesher. Floor
                // torch: a 2/16 × 10/16 × 2/16 tower at the cell
                // centre. Wall torches: an axis-aligned bounding
                // box around the tilted shaft (base at the wall,
                // tip leaning toward cell centre, Y from 0.2 to
                // 0.9). All torches stay non-solid so the player
                // walks through them — the AABB is purely for
                // selection wireframe + raycast click area.
                case BlockType.Torch:
                    return (7f / 16f, 0f, 7f / 16f, 9f / 16f, 10f / 16f, 9f / 16f);
                // Wall torch on the -X wall (faces +X / leans east).
                case BlockType.TorchEast:
                    return (0f, 0.2f, 7f / 16f, 0.5f, 0.9f, 9f / 16f);
                // Wall torch on the +X wall (faces -X / leans west).
                case BlockType.TorchWest:
                    return (0.5f, 0.2f, 7f / 16f, 1f, 0.9f, 9f / 16f);
                // Wall torch on the -Z wall (faces +Z / leans south).
                case BlockType.TorchSouth:
                    return (7f / 16f, 0.2f, 0f, 9f / 16f, 0.9f, 0.5f);
                // Wall torch on the +Z wall (faces -Z / leans north).
                case BlockType.TorchNorth:
                    return (7f / 16f, 0.2f, 0.5f, 9f / 16f, 0.9f, 1f);
                // Tier 6 #37 — Cactus is 16×14×14 (full height, 1
                // pixel inset on each horizontal side). The visual
                // mesh emits the four side faces at the inset plane
                // (x = ±1/16 from the cell edge) but extends each
                // face FULL on the perpendicular axis, producing the
                // canonical Alpha hash-shape overlap at the corners.
                // The collision AABB matches the 14×14 column.
                case BlockType.Cactus:
                    return (1f / 16f, 0f, 1f / 16f, 15f / 16f, 1f, 15f / 16f);
                // Tier 8 #46 part 2 — Fence: 4×16×4 post pinned at
                // cell centre. The connection arms are visual-only;
                // collision gates on the central post so the player
                // can walk diagonally past a fence-corner without
                // catching on a 2/16-wide arm sticking out.
                case BlockType.Fence:
                    return (6f / 16f, 0f, 6f / 16f, 10f / 16f, 1f, 10f / 16f);
                // Tier 8 #45 V1 — Slabs: full 1×0.5×1 footprint,
                // pinned to the cell bottom. Auto-step (max 0.55)
                // covers the half-block lift so the player walks
                // onto slabs naturally without jumping.
                case BlockType.StoneSlab:
                case BlockType.CobblestoneSlab:
                case BlockType.BrickSlab:
                case BlockType.WoodSlab:
                    return (0f, 0f, 0f, 1f, 0.5f, 1f);
                // Tier 8 #45 V2 — Stairs: lower step is the same
                // 1×0.5×1 lower-half slab regardless of facing.
                // The upper step is a separate AABB returned by
                // TryGetExtraCollisionAabb (keyed on per-cell meta
                // facing). Entity.Collides + Raycast both check
                // the extra box so collision + clicks land on
                // the full L-shape.
                case BlockType.WoodStairs:
                case BlockType.CobblestoneStairs:
                    return (0f, 0f, 0f, 1f, 0.5f, 1f);
                // Tier 8 #51 — Soul Sand: 1×0.875×1 footprint pinned
                // to the cell bottom. The 2/16 missing slice at the
                // top makes the player visually "sink" and is the
                // visual cue that pairs with the horizontal
                // velocity slowdown applied in Player.Update.
                case BlockType.SoulSand:
                    return (0f, 0f, 0f, 1f, 14f / 16f, 1f);
                default:
                    return (0f, 0f, 0f, 1f, 1f, 1f);
            }
        }

        // Tier 8 #45 V2 — Secondary collision AABB. Currently only
        // stairs return a second box; future multi-AABB blocks
        // (slab + slab merging, fence with cap, etc.) extend here.
        // Returns true + fills `box` with the upper-half step's
        // extents based on the meta byte's facing. The box is in
        // the same fractional [0..1] cell-space as the primary AABB.
        public static bool TryGetExtraCollisionAabb(
            BlockType t, byte meta,
            out (float minX, float minY, float minZ, float maxX, float maxY, float maxZ) box)
        {
            if (IsStair(t))
            {
                BlockFacing f = (BlockFacing)(meta & 0x03);
                // The upper step sits on the BACK half of the cell —
                // i.e., the side OPPOSITE to where the player stands
                // when facing the stair. For Stair facing East
                // (player ascends toward +X, lower step is on -X
                // side / west, upper step is on +X side / east).
                switch (f)
                {
                    case BlockFacing.East:  box = (0.5f, 0.5f, 0f,   1f,  1f, 1f); return true;
                    case BlockFacing.West:  box = (0f,   0.5f, 0f,   0.5f, 1f, 1f); return true;
                    case BlockFacing.South: box = (0f,   0.5f, 0.5f, 1f,  1f, 1f); return true;
                    default: /*North*/      box = (0f,   0.5f, 0f,   1f,  1f, 0.5f); return true;
                }
            }
            box = default;
            return false;
        }

        // Tier 6 #37 — Inventory / hotbar / held-view icon dispatch.
        // The icon paths used to gate "show as 3D cube vs flat sprite"
        // on IsCubeShape, but that breaks for sub-cube blocks that
        // are visually still cube-LIKE (Cactus is an inset 14×16×14
        // box, but the icon should still read as a 3D block, not a
        // flat sprite). This helper lets the cube-icon path opt in
        // those cases without affecting the chunk mesher's IsCubeShape
        // branch (which gates "does the cube sweep emit faces for
        // this block").
        //
        // Snow layer is INTENTIONALLY excluded — its 1/8 height makes
        // a full-cube icon look wrong, and the flat-sprite icon
        // (using TileSnow as a square) reads as a snow tile.
        public static bool RendersAsCubeIcon(BlockType t)
        {
            if (IsCubeShape(t)) return true;
            switch (t)
            {
                case BlockType.Cactus:
                    return true;
                default:
                    return false;
            }
        }

        // Pumpkin is IsCubeShape=true (regular cube), so it falls
        // through the IsCubeShape branch above without needing a
        // dedicated case here.

        // Tier 8 #43 — Blast resistance. True for blocks that a TNT
        // explosion (radius ~4) cannot break. Alpha's actual model
        // is a numeric resistance value compared against the per-
        // cell explosion intensity along a ray; for the V1 simulation
        // we just split blocks into "TNT can break" and "TNT can't
        // break" — Bedrock + Obsidian on the indestructible side,
        // everything else (including water / lava sources, but the
        // explosion code skips fluids separately) destroyable.
        public static bool IsBlastResistant(BlockType t)
        {
            switch (t)
            {
                case BlockType.Bedrock:
                case BlockType.Obsidian:
                    return true;
                default:
                    return false;
            }
        }

        // matches Alpha (you can't punch out a fluid source by clicking it).
        public static bool IsRaycastTarget(BlockType t)
        {
            if (IsTool(t) || IsItem(t)) return false;
            switch (t)
            {
                case BlockType.Air:
                case BlockType.Water:
                case BlockType.FlowingWater:
                case BlockType.Lava:
                case BlockType.FlowingLava:
                    return false;
                default:
                    return true;
            }
        }

        // "Cube shape" — true for the standard 1x1x1 voxel block that the
        // greedy mesher emits as cube faces. Non-cube blocks (Torch today,
        // flowers/mushrooms/ladders later) are skipped by the cube sweep and
        // emitted by a separate model-pass in ChunkMesher.
        public static bool IsCubeShape(BlockType t)
        {
            if (IsTool(t) || IsItem(t)) return false;
            if (IsTorch(t)) return false;
            switch (t)
            {
                case BlockType.Dandelion:
                case BlockType.Rose:
                case BlockType.BrownMushroom:
                case BlockType.RedMushroom:
                // Wheat — cross-sprite, not cube. Mesher takes the
                // EmitModels branch (same path as flowers) and reads
                // the per-cell metadata low-4-bits to pick the
                // TileWheat0..TileWheat7 stage tile.
                case BlockType.Wheat:
                // Tier 4 #26 — Sugar cane is also a cross-sprite plant.
                // Mesher routes it through EmitCrossSprite via the
                // standard non-cube path (same as flowers/wheat).
                case BlockType.SugarCane:
                // Tier 8 #47 — Sapling renders as a cross-sprite, same
                // mesher path as flowers / wheat.
                case BlockType.Sapling:
                // Tier 8 #42 — Redstone torches use the cross-sprite
                // path same as regular torches.
                case BlockType.RedstoneTorchOn:
                case BlockType.RedstoneTorchOff:
                // Tier 8 #42 — Redstone wire renders as a 1/16 floor
                // slab — handled by a custom mesher branch
                // (EmitRedstoneWire) rather than the cube sweep.
                case BlockType.RedstoneWire:
                // Tier 8 #42 — Lever / button / pressure plate all
                // render as sub-cube meshes, not full cubes.
                case BlockType.Lever:
                case BlockType.StoneButton:
                case BlockType.StonePressurePlate:
                case BlockType.WoodPressurePlate:
                // Tier 8 #44 — Sign post / wall sign aren't full cubes —
                // mesher dispatches to EmitSignPost / EmitWallSign.
                case BlockType.SignPost:
                case BlockType.WallSign:
                // Tier 8 #46 — Ladder is a thin wall-hugging quad,
                // routed through EmitLadder in the EmitModels pass.
                case BlockType.Ladder:
                // Tier 8 #46 part 2 — Fence is a 4×16×4 post + arms;
                // mesher routes through EmitFence in the EmitModels
                // pass with neighbour sampling.
                case BlockType.Fence:
                // Tier 8 #45 V1 — Slabs are 1×0.5×1 half-cubes pinned
                // to the cell bottom; mesher routes through
                // EmitSlab in the EmitModels pass.
                case BlockType.StoneSlab:
                case BlockType.CobblestoneSlab:
                case BlockType.BrickSlab:
                case BlockType.WoodSlab:
                // Tier 8 #45 V2 — Stairs are an L-shape made from
                // two sub-cube boxes; mesher routes through
                // EmitStair in the EmitModels pass.
                case BlockType.WoodStairs:
                case BlockType.CobblestoneStairs:
                // Tier 8 #51 — Soul Sand is a 14/16-tall sub-cube;
                // mesher routes through EmitModels with EmitSubCubeBox.
                case BlockType.SoulSand:
                // Tier 6 #34 — Fire renders as a cross-sprite (two
                // crossed quads showing the flame from any angle),
                // same path as flowers / wheat / sugar cane.
                case BlockType.Fire:
                // Tier 6 #37 Phase 4 — SnowBlock is a 1/8-tall layer,
                // not a full cube. Routed through the mesher's slab
                // emitter (EmitSnowLayer).
                case BlockType.SnowBlock:
                // Tier 6 #37 — Cactus is an inset 14×16×14 box, not
                // a full cube. Routed through the mesher's custom
                // box emitter (EmitCactusBox).
                case BlockType.Cactus:
                    return false;
                // Tier 4 #16 — Door halves are a thin slab (3/16-deep
                // quad against the wall face), not a full 1×1×1 cube.
                // Marking them non-cube keeps the cube sweep from
                // emitting bogus full-cube faces around them and
                // routes them through the EmitModels branch in
                // ChunkMesher (same dispatch path as wall torches).
                case BlockType.WoodDoorBlockBottom:
                case BlockType.WoodDoorBlockTop:
                case BlockType.IronDoorBlockBottom:
                case BlockType.IronDoorBlockTop:
                    return false;
                default:
                    return true;
            }
        }

        // "Opaque" means "occludes the face of a neighbouring block." Used by
        // the greedy mesher to decide whether to emit a face. Water is
        // non-opaque so stone shows its face underwater. Leaves and glass are
        // non-opaque alpha-tested cubes — we render every face against
        // anything (including another leaf or glass cube) so the cut-out
        // regions in the front face reveal the leaves / panes / trunk / sky
        // behind it, and trees + glass walls read with depth instead of as a
        // single hollow shell. Inter-self face emission is forced on by the
        // mesher even though `a == b` (see internalTransparent override and
        // IsAlphaTestedCube).
        public static bool IsOpaque(BlockType t)
        {
            if (IsTool(t) || IsItem(t)) return false;
            if (IsTorch(t)) return false;
            switch (t)
            {
                case BlockType.Air:
                case BlockType.Water:
                case BlockType.FlowingWater:
                case BlockType.Lava:
                case BlockType.FlowingLava:
                case BlockType.Leaves:
                case BlockType.Glass:
                // Cross-sprite blocks don't fill the cell. If they were marked
                // opaque the cube sweep would cull the faces of the block
                // beneath them (so the grass under a torch loses its top face)
                // AND the four side neighbours (so you see through into the
                // chunk because their facing wall didn't get a quad emitted).
                // Torches (floor + wall) are handled by the IsTorch early-out
                // above so we don't list them here per-variant.
                case BlockType.Dandelion:
                case BlockType.Rose:
                case BlockType.BrownMushroom:
                case BlockType.RedMushroom:
                // Wheat — cross-sprite, doesn't fill the cell, so it's
                // non-opaque the same way flowers and mushrooms are.
                case BlockType.Wheat:
                // Tier 4 #26 — Sugar cane same shape as wheat: the
                // cross-sprite doesn't fill the cell, so adjacent
                // block faces must still emit (otherwise the block
                // beneath the cane loses its top face and the cell
                // walls disappear).
                case BlockType.SugarCane:
                // Tier 8 #47 — Sapling cross-sprite, doesn't fill
                // the cell so neighbouring cube faces must still emit.
                case BlockType.Sapling:
                // Tier 8 #42 — Redstone torch sub-cell volume —
                // adjacent cube faces must still emit, same as
                // regular torches.
                case BlockType.RedstoneTorchOn:
                case BlockType.RedstoneTorchOff:
                // Wire 1/16 slab — adjacent cube faces below + sides
                // must still emit so the cell reads correctly.
                case BlockType.RedstoneWire:
                // Lever / button / pressure plate sub-cell volumes —
                // adjacent cube faces must still emit.
                case BlockType.Lever:
                case BlockType.StoneButton:
                case BlockType.StonePressurePlate:
                case BlockType.WoodPressurePlate:
                // Tier 8 #44 — Signs are sub-cell geometry; their
                // adjacent neighbours need full faces drawn (a sign
                // on a stone wall must show the stone face beside
                // it, not have the stone face culled away as if a
                // cube were there).
                case BlockType.SignPost:
                case BlockType.WallSign:
                // Tier 8 #46 — Ladder is a thin wall-hugging quad,
                // doesn't occlude the supporting wall face behind
                // it (otherwise the wall would punch a square
                // shadow through the ladder's open silhouette).
                case BlockType.Ladder:
                // Tier 8 #46 part 2 — Fence is a slim post + arms,
                // most of the cell volume is empty. Adjacent cube
                // faces must still emit (a stone wall next to a
                // fence post should still show its full face beside
                // the post, not have it culled away as if a cube
                // were there).
                case BlockType.Fence:
                // Tier 8 #45 V1 — Slabs fill only the bottom half of
                // their cell; the top half is air. The cube above
                // would otherwise lose its bottom face (culled
                // against what it thinks is a full cube), so slabs
                // are non-opaque to keep that face emitted.
                case BlockType.StoneSlab:
                case BlockType.CobblestoneSlab:
                case BlockType.BrickSlab:
                case BlockType.WoodSlab:
                // Tier 8 #45 V2 — Stairs leave one of the four
                // 0.5×0.5×1 quadrants of their upper half as air,
                // plus part of the lower-front half above the
                // step. Adjacent cube faces must still emit so
                // those air-exposed corners aren't culled away.
                case BlockType.WoodStairs:
                case BlockType.CobblestoneStairs:
                // Tier 8 #51 — Soul Sand: top 2/16 of the cell is
                // air (the player visually sinks). The cube above
                // would otherwise lose its bottom face — same
                // dynamic as snow / slabs — so soul sand is
                // non-opaque to keep that face emitted.
                case BlockType.SoulSand:
                    return false;
                // Tier 4 #16 — Doors are thin slabs and don't fill the
                // cell; the four neighbouring cube faces (and the
                // top/bottom of the cell) must still emit, so the
                // door is non-opaque the same way wall torches and
                // cross-sprites are. Without this the block ABOVE the
                // door's top half loses its bottom face when culled
                // against the door cell.
                case BlockType.WoodDoorBlockBottom:
                case BlockType.WoodDoorBlockTop:
                case BlockType.IronDoorBlockBottom:
                case BlockType.IronDoorBlockTop:
                // Tier 6 #37 Phase 4 — Snow layer is a 1/8-tall slab,
                // it doesn't fill the cell. If it were marked opaque
                // the cube sweep would cull adjacent block faces
                // against it (a dirt cube touching a snow layer would
                // lose its facing side face — the player would see
                // straight through the dirt). Same reasoning as the
                // door / cross-sprite entries above. The snow's own
                // 6-box geometry is emitted by EmitSnowLayer.
                case BlockType.SnowBlock:
                // Tier 6 #37 — Cactus is an inset 14×16×14 box; if
                // marked opaque the cube sweep would cull adjacent
                // block faces against it (a dirt cube touching a
                // cactus would lose its facing side, so the player
                // would see straight through the dirt at the 1/16
                // gap on either side of the cactus). Same reasoning
                // as the door / snow / cross-sprite entries.
                case BlockType.Cactus:
                    return false;
                default:
                    return true;
            }
        }

        // True for non-opaque cube blocks whose texture has binary alpha
        // (gaps + solid pixels) and which therefore render through the
        // OPAQUE stream's `discard` rather than the alpha-blended transparent
        // stream. The mesher uses this for two things:
        //   1. Inner-self faces (leaf-vs-leaf, glass-vs-glass) are always
        //      emitted so adjacent blocks layer through each other.
        //   2. The face stays on the opaque stream — back-to-front sorting
        //      is unnecessary because alpha-discard is order-independent.
        // Water/lava are non-opaque too, but their alpha is gradient
        // (≈160), so they need true blending and stay off this list.
        public static bool IsAlphaTestedCube(BlockType t)
        {
            switch (t)
            {
                case BlockType.Leaves:
                case BlockType.Glass:
                    return true;
                default:
                    return false;
            }
        }

        // True for any block in the same fluid family — water sources and
        // flowing water both count as "water", lava and flowing lava both
        // count as "lava". The mesher uses this to skip internal faces
        // between fluid cells of the same kind.
        public static int FluidGroup(BlockType t)
        {
            switch (t)
            {
                case BlockType.Water:
                case BlockType.FlowingWater:
                    return 1;
                case BlockType.Lava:
                case BlockType.FlowingLava:
                    return 2;
                default:
                    return 0;
            }
        }

        // "LightTransparent" controls whether sky / block light propagates through
        // a cell during flood-fill. Air passes everything; water passes light
        // (Alpha treated water as light-transmissive with no extra attenuation —
        // we can later bump per-step decay if it turns out too bright). Glass and
        // leaves are visually opaque-ish but never block light. Torches occupy
        // a sub-cell volume so light still flows through their cell.
        public static bool IsLightTransparent(BlockType t)
        {
            if (IsTool(t) || IsItem(t)) return true;
            if (IsTorch(t)) return true;
            switch (t)
            {
                case BlockType.Air:
                case BlockType.Water:
                case BlockType.FlowingWater:
                case BlockType.Lava:
                case BlockType.FlowingLava:
                case BlockType.Glass:
                case BlockType.Leaves:
                case BlockType.Dandelion:
                case BlockType.Rose:
                case BlockType.BrownMushroom:
                case BlockType.RedMushroom:
                // Wheat is light-transparent for the same reason as the
                // other cross-sprite blocks: the cell isn't fully filled.
                case BlockType.Wheat:
                // Tier 4 #26 — Sugar cane: cross-sprite, light passes
                // straight through. Without this a 3-tall cane stack
                // would cast a dark shadow column underneath it like a
                // solid cube does.
                case BlockType.SugarCane:
                // Tier 8 #47 — Sapling is a cross-sprite, light passes
                // straight through.
                case BlockType.Sapling:
                // Tier 8 #42 — Redstone torches: light flows past the
                // sub-cell volume the same way it does through
                // regular torches.
                case BlockType.RedstoneTorchOn:
                case BlockType.RedstoneTorchOff:
                // Wire is a 1/16 slab, the cell is mostly air.
                case BlockType.RedstoneWire:
                // Lever / button / pressure plate — sub-cell volumes
                // with mostly air, light propagates through the cell.
                case BlockType.Lever:
                case BlockType.StoneButton:
                case BlockType.StonePressurePlate:
                case BlockType.WoodPressurePlate:
                // Tier 8 #44 — Signs are sub-cell wood; light passes
                // through the cell on every side except the thin
                // board / post slab. Without this, a sign sitting in
                // sunlight would cast a 1m black square shadow on
                // adjacent ground.
                case BlockType.SignPost:
                case BlockType.WallSign:
                // Tier 8 #46 — Ladder: thin wall-hugging quad,
                // light passes through everything except the
                // single rung-pattern face.
                case BlockType.Ladder:
                // Tier 6 #37 — Ice is translucent (matches Alpha — a
                // pond covered in ice still has the bed visible
                // through the surface). SnowBlock is a 1/8 slab so
                // the cell is mostly air — light propagates straight
                // through. Cactus is a 14×16×14 inset column with
                // the corner regions of the cell empty — light
                // through those gaps must reach the sand below, or
                // a cactus in sunlight would cast a 1m black square
                // shadow on its supporting block.
                case BlockType.Ice:
                case BlockType.SnowBlock:
                case BlockType.Cactus:
                // Tier 4 #16 — Door halves don't fill the cell; light
                // must propagate through them (otherwise a closed
                // door would cast a dark column the height of the
                // doorway, leaving the room behind it unlit). This
                // matches Alpha — wooden doors never blocked light;
                // light flowed through the cell as if it were air
                // for the sky/block flood-fill purposes.
                case BlockType.WoodDoorBlockBottom:
                case BlockType.WoodDoorBlockTop:
                case BlockType.IronDoorBlockBottom:
                case BlockType.IronDoorBlockTop:
                // Tier 6 #34 — Fire is a cross-sprite emitter — the
                // sub-cell volume is mostly air, and the flame is
                // ITSELF the light source. Light has to pass through
                // the cell so the emission propagates to neighbours.
                case BlockType.Fire:
                // Tier 8 #46 part 2 — Fence: 4×16×4 post + thin arms,
                // most of the cell volume is air. Without this, a
                // fence in daylight would render its mesh dark
                // because the cell's own light value (mesher samples
                // there) would be 0 from the BFS treating the cell
                // as opaque.
                case BlockType.Fence:
                // Tier 8 #45 V1 — Slabs fill the bottom half only;
                // the top half is air through which light flows.
                // Same dark-mesh symptom as fence without this.
                case BlockType.StoneSlab:
                case BlockType.CobblestoneSlab:
                case BlockType.BrickSlab:
                case BlockType.WoodSlab:
                // Tier 8 #45 V2 — Stairs leave one of the four
                // 0.5×0.5×1 quadrants of their upper half as air
                // plus part of the lower-front above the step;
                // light flows through those open volumes the same
                // way it does for slabs.
                case BlockType.WoodStairs:
                case BlockType.CobblestoneStairs:
                // Tier 8 #51 — Soul Sand: top 2/16 of the cell is
                // air, so light can flow through. Without this, the
                // BFS would treat soul sand as opaque and the mesh's
                // own cell light samples to 0 — the block would
                // render dark even in daylight.
                case BlockType.SoulSand:
                    return true;
                default:
                    return false;
            }
        }

        // 0..15 luminous emission. Torches sit at 14 — matches Alpha so a
        // torch placed against a wall lights about 14 cells before fading
        // out, leaving the 15th cell almost dark. Glowstone/fire slot in here.
        //
        // Lava deliberately emits 0 here despite Alpha 1.1.2_01's vanilla
        // value of 15: making lava a light source forced a 3×3-chunk
        // RecomputeRegion every tick a flowing-lava cell advanced, which
        // stuttered visibly on the render thread. Water (light-transparent,
        // emission 0) had no such hit. The fluid sim is otherwise identical
        // for both fluids — keeping the lighting path identical too is the
        // simplest way to make lava perform like water. Lava still reads as
        // lit because its own tile is bright; it just doesn't propagate
        // brightness to neighbours.
        public static int LightEmission(BlockType t)
        {
            // All torch placements (floor + 4 wall variants) emit 14 — same
            // value Alpha used so a torch reaches ~14 cells before fading
            // out. The IsTorch early-out covers every variant with one check
            // so adding another mounted orientation later doesn't need a
            // case here.
            if (IsTorch(t)) return 14;
            switch (t)
            {
                // A burning furnace casts a warm glow about half the
                // reach of a torch in Alpha. Putting it at 13 keeps the
                // smelt indoors lit without competing with torches as
                // the primary cave light source. Idle (BlockType.Furnace)
                // emits nothing — only LitFurnace, set by the smelt
                // tick whenever there's fuel burning.
                case BlockType.LitFurnace:
                    return 13;
                // Tier 6 #34 — Fire emits 14, same as a torch. Brightly
                // illuminates the area around it so a player walking
                // through a corridor sees flames visibly cast light.
                case BlockType.Fire:
                    return 14;
                // Tier 8 #42 — Redstone torch emits 7 in Alpha (about
                // half the reach of a regular torch). The dim red
                // glow is the canonical "I'm a redstone signal"
                // visual cue.
                case BlockType.RedstoneTorchOn:
                    return 7;
                // Tier 8 #51 — Jack-o-lantern emits 15 (one above
                // the canonical torch level — Alpha 1.1.2's brightest
                // block-light source). A row of jack-o-lanterns reads
                // as a clearly Halloween-decorated path.
                case BlockType.JackOLantern:
                    return 15;
                // Tier 8 #51 — Glowstone block emits the same 15 as
                // the jack-o-lantern. In canonical Alpha 1.1.2_01 the
                // block was the *primary* nether-roof light source so
                // it lights through itself like a glowing crystal —
                // matches the brightest fixed-source convention here.
                case BlockType.Glowstone:
                    return 15;
                default:
                    return 0;
            }
        }

        // Bare-hand break time in seconds. Matches Alpha 1.1.2's hardness
        // table reasonably (no proper tools yet, so the values here are the
        // worst-case "punch through with your fist" times). A negative value
        // means unbreakable. Hardness 0 breaks instantly the first frame the
        // hold-LMB raycast lands on the block.
        //
        // Used by the survival break-progress system; creative mode skips
        // the timer and breaks instantly on click.
        public static float Hardness(BlockType t)
        {
            if (IsTorch(t)) return 0f;
            switch (t)
            {
                case BlockType.Bedrock:
                    return -1f;
                case BlockType.Obsidian:
                    return 50f;
                case BlockType.IronBlock:
                case BlockType.DiamondBlock:
                case BlockType.GoldBlock:
                // Tier 4 #16 — Iron door hardness 5.0 (matches Alpha
                // 1.1.2_01 — same as iron block, reflecting the
                // material). Like the wood door, both halves share
                // the value; the break path cascades.
                case BlockType.IronDoorBlockBottom:
                case BlockType.IronDoorBlockTop:
                    return 5f;
                case BlockType.IronOre:
                case BlockType.DiamondOre:
                case BlockType.GoldOre:
                case BlockType.RedstoneOre:
                case BlockType.CoalOre:
                    return 3f;
                case BlockType.Cobblestone:
                case BlockType.MossyCobblestone:
                case BlockType.Stone:
                case BlockType.Bricks:
                case BlockType.Furnace:
                case BlockType.LitFurnace:
                // Tier 6 #32 — MobSpawner shares cobble hardness (5
                // in canonical Alpha; we use 1.5 for the same break-
                // feel as cobble since dungeon mining is the only
                // use case and Alpha's 5 felt slow). Block drops
                // nothing — IsSolid + IsRaycastTarget get default
                // true via the cube branch below.
                case BlockType.MobSpawner:
                    return 1.5f;
                case BlockType.WoodLog:
                case BlockType.Planks:
                case BlockType.Bookshelf:
                case BlockType.CraftingTable:
                case BlockType.Chest:
                // Tier 4 #16 — Wooden door hardness 2.0 (matches Alpha
                // 1.1.2_01 — same as planks, since the door IS a
                // plank construct). Both halves share the value;
                // breaking either half cascades to the other in the
                // GameRenderer break path so the timer just needs to
                // be the per-half value.
                case BlockType.WoodDoorBlockBottom:
                case BlockType.WoodDoorBlockTop:
                // Tier 4 #25 — Jukebox hardness 2.0 (matches Alpha
                // 1.1.2_01 — same value as planks/bookshelf, since the
                // jukebox is a plank-and-disc construct rather than
                // stone). Bare-handed mining still works; an axe
                // would speed it up but tool/material gating isn't
                // wired yet.
                case BlockType.Jukebox:
                    return 2f;
                case BlockType.Dirt:
                case BlockType.Grass:
                case BlockType.Sand:
                case BlockType.Gravel:
                case BlockType.Clay:
                    return 0.5f;
                // Farmland sits between dirt (0.5) and stone — Alpha
                // gave tilled soil a slightly higher break time than
                // dirt, presumably because the block is "compacted" by
                // the hoe pass. 0.6 keeps it as a quick break either
                // way (a single shovel swing).
                case BlockType.Farmland:
                    return 0.6f;
                case BlockType.Wool:
                    return 0.8f;
                // Tier 8 #51 — Netherrack: Alpha hardness 0.4. Soft
                // red rock; faster to mine than stone (1.5) but
                // slower than dirt (0.5). Bare-hand mining works;
                // a wooden pickaxe is fastest.
                case BlockType.Netherrack:
                    return 0.4f;
                // Tier 8 #51 — Soul Sand: Alpha hardness 0.5 (same
                // as regular sand). Bare-hand or shovel both work.
                case BlockType.SoulSand:
                    return 0.5f;
                // Tier 6 #37 — Snow block. Quick to break (Alpha
                // hardness 0.2 — single shovel swing). No tool gate;
                // hand also works.
                case BlockType.SnowBlock:
                    return 0.2f;
                // Tier 6 #37 — Cactus (Alpha 0.4, soft like wood
                // sapling). Ice (0.5, slightly tougher than snow but
                // still pickaxe-light). Pumpkin (1.0, axe-friendly).
                case BlockType.Cactus:
                    return 0.4f;
                case BlockType.Ice:
                    return 0.5f;
                case BlockType.Pumpkin:
                    return 1.0f;
                // Tier 8 #47 — Sapling instant-break (Alpha hardness 0).
                case BlockType.Sapling:
                    return 0f;
                // Tier 8 #48 — Note Block hardness 0.8 (Alpha — same as
                // wool, breaks fastest with an axe but bare-hand works).
                case BlockType.NoteBlock:
                    return 0.8f;
                // Tier 8 #42 — Redstone torch / wire instant-break.
                case BlockType.RedstoneTorchOn:
                case BlockType.RedstoneTorchOff:
                case BlockType.RedstoneWire:
                    return 0f;
                // Lever / buttons / plates: 0.5 hardness (Alpha).
                case BlockType.Lever:
                case BlockType.StoneButton:
                case BlockType.StonePressurePlate:
                case BlockType.WoodPressurePlate:
                    return 0.5f;
                // Tier 8 #44 — Signs are wood; Alpha hardness is 1.0s
                // by hand. Same as Planks (which signs are made from).
                case BlockType.SignPost:
                case BlockType.WallSign:
                    return 1.0f;
                case BlockType.Glass:
                case BlockType.Sponge:
                    return 0.3f;
                case BlockType.Leaves:
                    return 0.2f;
                case BlockType.Tnt:
                    return 0f;
                case BlockType.Torch:
                case BlockType.Dandelion:
                case BlockType.Rose:
                case BlockType.BrownMushroom:
                case BlockType.RedMushroom:
                // Wheat at any growth stage breaks instantly (Alpha
                // hardness 0). Drops are a custom branch in
                // SpawnBreakDrop — stage-7 drops 1 wheat + 0..3 seeds,
                // earlier stages drop a single seed.
                case BlockType.Wheat:
                // Tier 6 #34 — Fire breaks instantly with bare hands
                // (matches Alpha — punch out a fire to put it out).
                case BlockType.Fire:
                // Tier 4 #26 — Sugar cane breaks instantly bare-handed
                // (Alpha hardness 0). Drop is one SugarCaneItem per
                // cell broken; if the BOTTOM cell of a 2/3-tall stack
                // is broken, the cells above cascade-break (same
                // unsupported-block pattern as torches).
                case BlockType.SugarCane:
                    return 0f;
                // Air/fluids aren't raycast-targetable so callers shouldn't
                // hit this; return 0 anyway as a defensive default.
                default:
                    return 0f;
            }
        }

        // faceKind: 0 = top, 1 = bottom, 2 = side. Returns the atlas tile index.
        public static int GetTileIndex(BlockType t, int faceKind)
        {
            switch (t)
            {
                case BlockType.Grass:
                    if (faceKind == 0) return BlockTextures.TileGrassTop;
                    if (faceKind == 1) return BlockTextures.TileDirt;
                    return BlockTextures.TileGrassSide;
                case BlockType.Dirt:
                    return BlockTextures.TileDirt;
                case BlockType.Stone:
                    return BlockTextures.TileStone;
                case BlockType.Sand:
                    return BlockTextures.TileSand;
                case BlockType.Cobblestone:
                    return BlockTextures.TileCobblestone;
                case BlockType.Bedrock:
                    return BlockTextures.TileBedrock;
                case BlockType.Gravel:
                    return BlockTextures.TileGravel;
                case BlockType.Clay:
                    return BlockTextures.TileClay;
                case BlockType.CoalOre:
                    return BlockTextures.TileCoalOre;
                case BlockType.IronOre:
                    return BlockTextures.TileIronOre;
                case BlockType.GoldOre:
                    return BlockTextures.TileGoldOre;
                case BlockType.DiamondOre:
                    return BlockTextures.TileDiamondOre;
                case BlockType.RedstoneOre:
                    return BlockTextures.TileRedstoneOre;
                case BlockType.WoodLog:
                    if (faceKind == 0 || faceKind == 1) return BlockTextures.TileLogTop;
                    return BlockTextures.TileLogSide;
                case BlockType.Planks:
                    return BlockTextures.TilePlanks;
                case BlockType.Leaves:
                    return BlockTextures.TileLeaves;
                case BlockType.Water:
                case BlockType.FlowingWater:
                    return BlockTextures.TileWater;
                case BlockType.Lava:
                case BlockType.FlowingLava:
                    return BlockTextures.TileLava;
                case BlockType.GoldBlock:
                    return BlockTextures.TileGoldBlock;
                case BlockType.IronBlock:
                    return BlockTextures.TileIronBlock;
                case BlockType.DiamondBlock:
                    return BlockTextures.TileDiamondBlock;
                case BlockType.Bricks:
                    return BlockTextures.TileBricks;
                case BlockType.Tnt:
                    if (faceKind == 0) return BlockTextures.TileTntTop;
                    if (faceKind == 1) return BlockTextures.TileTntBottom;
                    return BlockTextures.TileTntSide;
                case BlockType.Bookshelf:
                    if (faceKind == 0 || faceKind == 1) return BlockTextures.TilePlanks;
                    return BlockTextures.TileBookshelfSide;
                case BlockType.CraftingTable:
                    // Top: work-bench grid. Bottom: plain planks (crafting
                    // table sits on a plank base in Alpha). Sides: the
                    // tool-rack art with hammer/saw silhouettes.
                    if (faceKind == 0) return BlockTextures.TileCraftingTableTop;
                    if (faceKind == 1) return BlockTextures.TilePlanks;
                    return BlockTextures.TileCraftingTableSide;
                case BlockType.Furnace:
                    // Default (un-oriented) lookup — used by inventory
                    // icons, drop sprites, and any callsite that doesn't
                    // know the per-block facing. Top/bottom share the
                    // stone-cap-with-vent tile; sides show the dark
                    // furnace mouth so the door is recognisable on the
                    // dropped/icon form.
                    //
                    // The mesher uses GetTileIndexForOriented() instead
                    // so each side picks between front/lit-front and
                    // plain side based on the placed entity's Facing.
                    if (faceKind == 0 || faceKind == 1) return BlockTextures.TileFurnaceTop;
                    return BlockTextures.TileFurnaceFront;
                case BlockType.LitFurnace:
                    // Same layout as Furnace; sides show the lit front
                    // tile so the player can tell at a glance that the
                    // furnace is actively burning fuel. The world tick
                    // swaps Furnace ↔ LitFurnace by SetBlock — there's
                    // no animated texture path needed.
                    if (faceKind == 0 || faceKind == 1) return BlockTextures.TileFurnaceTop;
                    return BlockTextures.TileFurnaceFrontLit;
                case BlockType.Chest:
                    // Chest face layout matches Alpha: a planked top
                    // with a metal-bound lid silhouette, four wood-
                    // banded sides (no door yet — single-chest mode
                    // uses one side tile for all four faces here in
                    // the un-oriented lookup; the mesher branches into
                    // GetTileIndexForOriented to swap the front face
                    // for the latch-and-keyhole tile based on
                    // ChestTileEntity.Facing). Bottom is plain planks
                    // because a chest sits on a plank base in Alpha.
                    if (faceKind == 0) return BlockTextures.TileChestTop;
                    if (faceKind == 1) return BlockTextures.TilePlanks;
                    return BlockTextures.TileChestFront;
                case BlockType.MossyCobblestone:
                    return BlockTextures.TileMossyCobblestone;
                case BlockType.MobSpawner:
                    return BlockTextures.TileMobSpawner;
                case BlockType.Fire:
                    return BlockTextures.TileFire;
                case BlockType.Obsidian:
                    return BlockTextures.TileObsidian;
                case BlockType.Sponge:
                    return BlockTextures.TileSponge;
                case BlockType.Glass:
                    return BlockTextures.TileGlass;
                case BlockType.Wool:
                    return BlockTextures.TileWool;
                case BlockType.SnowBlock:
                    return BlockTextures.TileSnow;
                case BlockType.Cactus:
                    // Top face (faceKind 0) shows the cactus crown
                    // with concentric ridges; sides + bottom share
                    // the spiny green column tile. Matches Alpha's
                    // 3-face layout (top, bottom, side) collapsed
                    // here to top vs side because we don't draw a
                    // distinct bottom tile.
                    if (faceKind == 0) return BlockTextures.TileCactusTop;
                    return BlockTextures.TileCactusSide;
                case BlockType.Ice:
                    return BlockTextures.TileIce;
                case BlockType.Sapling:
                    return BlockTextures.TileSapling;
                case BlockType.NoteBlock:
                    return BlockTextures.TileNoteBlock;
                case BlockType.RedstoneTorchOn:
                    return BlockTextures.TileRedstoneTorchOn;
                case BlockType.RedstoneTorchOff:
                    return BlockTextures.TileRedstoneTorchOff;
                case BlockType.RedstoneDust:
                    return BlockTextures.TileRedstoneDust;
                case BlockType.RedstoneWire:
                    return BlockTextures.TileRedstoneWire;
                case BlockType.Lever:
                    // Cobblestone-coloured base; the small lever
                    // protrusion above it shares the same tile in
                    // this simplified single-tile pass.
                    return BlockTextures.TileCobblestone;
                case BlockType.StoneButton:
                    // Inventory / held-icon tile only — the placed
                    // block's faces are sampled directly from
                    // TileStone by EmitButtonBox in ChunkMesher,
                    // bypassing this lookup. Routing GetTileIndex
                    // to the dedicated TileStoneButtonItem keeps
                    // DrawFlatSpriteIcon's side-tile fetch on the
                    // canonical Alpha button sprite without
                    // affecting the in-world cuboid.
                    return BlockTextures.TileStoneButtonItem;
                case BlockType.StonePressurePlate:
                    return BlockTextures.TileStone;
                case BlockType.WoodPressurePlate:
                    return BlockTextures.TilePlanks;
                // Tier 8 #44 — Signs reuse the existing PlanksOak
                // tile (per-feature direction; no sign-specific atlas
                // entry). The mesher's EmitSignPost / EmitWallSign
                // sample this index directly for the board, post,
                // and back faces; the player-typed text overlays the
                // front face as a separate quad batch driven by the
                // HUD font atlas.
                case BlockType.SignPost:
                case BlockType.WallSign:
                case BlockType.SignItem:
                    return BlockTextures.TilePlanks;
                // Tier 8 #50 — Bone + Bone Meal item icons. Both
                // procedural; no terrain.png / alpha_tools.png slot
                // wired up yet (their canonical Alpha tile coords
                // are on alpha_tools.png at items col 9..10 / row 1
                // but those slots overlap existing food sprites in
                // this build's bundled atlas — we ship procedural
                // sprites instead).
                case BlockType.Bone:
                    return BlockTextures.TileBone;
                case BlockType.BoneMeal:
                    return BlockTextures.TileBoneMeal;
                // Tier 8 #46 — Ladder uses the canonical Alpha
                // ladder tile from terrain.png at (3, 5) — see
                // BlockTextures.TileLadder.
                case BlockType.Ladder:
                    return BlockTextures.TileLadder;
                // Tier 8 #46 part 2 — Fence reuses TilePlanks for
                // every face of every box (post + arms). Canonical
                // Alpha shipped fences with the planks tile too.
                case BlockType.Fence:
                    return BlockTextures.TilePlanks;
                // Tier 8 #45 V1 — Slabs reuse the source material's
                // tiles. StoneSlab → TileStone, CobblestoneSlab →
                // TileCobblestone, BrickSlab → TileBricks, WoodSlab
                // → TilePlanks (the slab is wood-PLANK-coloured;
                // canonical Alpha called it "Wooden Slab" but the
                // texture is the planks tile, not the log tile).
                case BlockType.StoneSlab:
                    return BlockTextures.TileStone;
                case BlockType.CobblestoneSlab:
                    return BlockTextures.TileCobblestone;
                case BlockType.BrickSlab:
                    return BlockTextures.TileBricks;
                case BlockType.WoodSlab:
                    return BlockTextures.TilePlanks;
                // Tier 8 #51 — Glowstone block. Single tile on every
                // face; canonical Alpha terrain.png slot at (9, 6).
                case BlockType.Glowstone:
                    return BlockTextures.TileGlowstone;
                // Tier 8 #45 V2 — Stairs reuse the source material's
                // tile on every face. WoodStairs → TilePlanks (the
                // Alpha "wooden stair" was plank-coloured, not log-
                // coloured), CobblestoneStairs → TileCobblestone.
                case BlockType.WoodStairs:
                    return BlockTextures.TilePlanks;
                case BlockType.CobblestoneStairs:
                    return BlockTextures.TileCobblestone;
                // Tier 8 #49 V1 — Un-oriented Dispenser lookup. Used
                // by inventory icons / drop sprites where facing
                // isn't meaningful: top + bottom = furnace top
                // (stone cap), all four sides default to the
                // dispenser front so the held / dropped item reads
                // distinctively (the in-world block uses the
                // oriented lookup so only ONE side shows the front
                // tile, matching canonical Alpha).
                case BlockType.Dispenser:
                    if (faceKind == 0 || faceKind == 1) return BlockTextures.TileFurnaceTop;
                    return BlockTextures.TileDispenserFront;
                // Tier 8 #51 — Netherrack. Single tile on every face
                // — canonical Alpha terrain.png slot at (7, 6) — a
                // mottled red-rock pattern.
                case BlockType.Netherrack:
                    return BlockTextures.TileNetherrack;
                // Tier 8 #51 — Soul Sand. Single tile on every face;
                // canonical Alpha terrain.png slot at (8, 6) — a
                // brown sand with darker pits resembling spectral
                // faces.
                case BlockType.SoulSand:
                    return BlockTextures.TileSoulSand;
                // Tier 8 #51 — Glowstone Dust item icon. Procedural;
                // a small pile of bright yellow grain, similar in
                // shape to bone meal but in glowstone-yellow tones.
                case BlockType.GlowstoneDust:
                    return BlockTextures.TileGlowstoneDust;
                case BlockType.Pumpkin:
                    // Top face = stem patch on a brown crown tile.
                    // Bottom shares the side tile (the bottom of a
                    // pumpkin sitting on grass is invisible; reusing
                    // side avoids a third atlas slot). Sides use the
                    // canonical orange-ridge tile.
                    if (faceKind == 0) return BlockTextures.TilePumpkinTop;
                    return BlockTextures.TilePumpkinSide;
                // Tier 8 #51 — Un-oriented Jack-o-lantern lookup.
                // Used by inventory icons / drop sprites where the
                // facing isn't meaningful: top = stem, all four
                // sides + bottom default to the carved face so the
                // dropped/held form looks distinctive (the player
                // shouldn't be unable to tell jack-o-lantern apart
                // from a regular pumpkin in their inventory). The
                // mesher uses GetTileIndexForOriented instead so
                // the in-world block correctly shows three plain
                // sides + one carved face.
                case BlockType.JackOLantern:
                    if (faceKind == 0) return BlockTextures.TilePumpkinTop;
                    return BlockTextures.TileJackOLanternFront;
                case BlockType.Torch:
                case BlockType.TorchEast:
                case BlockType.TorchWest:
                case BlockType.TorchSouth:
                case BlockType.TorchNorth:
                    return BlockTextures.TileTorch;
                case BlockType.Dandelion:
                    return BlockTextures.TileDandelion;
                case BlockType.Rose:
                    return BlockTextures.TileRose;
                case BlockType.BrownMushroom:
                    return BlockTextures.TileBrownMushroom;
                case BlockType.RedMushroom:
                    return BlockTextures.TileRedMushroom;
                case BlockType.WoodSword:      return BlockTextures.TileWoodSword;
                case BlockType.StoneSword:     return BlockTextures.TileStoneSword;
                case BlockType.IronSword:      return BlockTextures.TileIronSword;
                case BlockType.DiamondSword:   return BlockTextures.TileDiamondSword;
                case BlockType.GoldSword:      return BlockTextures.TileGoldSword;
                case BlockType.WoodShovel:     return BlockTextures.TileWoodShovel;
                case BlockType.StoneShovel:    return BlockTextures.TileStoneShovel;
                case BlockType.IronShovel:     return BlockTextures.TileIronShovel;
                case BlockType.DiamondShovel:  return BlockTextures.TileDiamondShovel;
                case BlockType.GoldShovel:     return BlockTextures.TileGoldShovel;
                case BlockType.WoodPickaxe:    return BlockTextures.TileWoodPickaxe;
                case BlockType.StonePickaxe:   return BlockTextures.TileStonePickaxe;
                case BlockType.IronPickaxe:    return BlockTextures.TileIronPickaxe;
                case BlockType.DiamondPickaxe: return BlockTextures.TileDiamondPickaxe;
                case BlockType.GoldPickaxe:    return BlockTextures.TileGoldPickaxe;
                case BlockType.WoodAxe:        return BlockTextures.TileWoodAxe;
                case BlockType.StoneAxe:       return BlockTextures.TileStoneAxe;
                case BlockType.IronAxe:        return BlockTextures.TileIronAxe;
                case BlockType.DiamondAxe:     return BlockTextures.TileDiamondAxe;
                case BlockType.GoldAxe:        return BlockTextures.TileGoldAxe;
                case BlockType.Stick:          return BlockTextures.TileStick;
                case BlockType.Coal:           return BlockTextures.TileCoal;
                case BlockType.IronIngot:      return BlockTextures.TileIronIngot;
                case BlockType.GoldIngot:      return BlockTextures.TileGoldIngot;
                case BlockType.Diamond:        return BlockTextures.TileDiamond;
                case BlockType.Flint:          return BlockTextures.TileFlint;
                case BlockType.ClayBall:       return BlockTextures.TileClayBall;
                case BlockType.ClayBrick:      return BlockTextures.TileClayBrick;
                case BlockType.Bowl:           return BlockTextures.TileBowl;
                case BlockType.RawPorkchop:    return BlockTextures.TileRawPorkchop;
                case BlockType.CookedPorkchop: return BlockTextures.TileCookedPorkchop;
                case BlockType.Bow:            return BlockTextures.TileBow;
                case BlockType.Arrow:          return BlockTextures.TileArrow;
                case BlockType.String:         return BlockTextures.TileString;
                case BlockType.Gunpowder:      return BlockTextures.TileGunpowder;
                case BlockType.Leather:        return BlockTextures.TileLeather;
                case BlockType.Feather:        return BlockTextures.TileFeather;
                case BlockType.Egg:            return BlockTextures.TileEgg;
                // Tier 4 #14 — hoe icons. Material order matches the
                // rest of the tool ladder.
                case BlockType.WoodHoe:        return BlockTextures.TileWoodHoe;
                case BlockType.StoneHoe:       return BlockTextures.TileStoneHoe;
                case BlockType.IronHoe:        return BlockTextures.TileIronHoe;
                case BlockType.DiamondHoe:     return BlockTextures.TileDiamondHoe;
                case BlockType.GoldHoe:        return BlockTextures.TileGoldHoe;
                // Farmland: top face shows the dry-tilled-soil tile,
                // sides + bottom share the dirt tile (Alpha: only the
                // top of farmland looks different — the sides are the
                // same texture as plain dirt).
                case BlockType.Farmland:
                    if (faceKind == 0) return BlockTextures.TileFarmlandTop;
                    return BlockTextures.TileDirt;
                // Wheat — default to the stage-0 tile here. The
                // mesher uses GetWheatTileForStage to pick the actual
                // tile per growth stage by reading per-cell metadata.
                // Anything that calls into the un-oriented lookup
                // (e.g. inventory icon for the in-world block) gets
                // stage 0 (sprouts) which is the safest "wheat" read.
                case BlockType.Wheat:          return BlockTextures.TileWheat0;
                // Farming items.
                case BlockType.WheatSeeds:     return BlockTextures.TileWheatSeeds;
                case BlockType.WheatItem:      return BlockTextures.TileWheatItem;
                case BlockType.Bread:          return BlockTextures.TileBread;
                case BlockType.MushroomStew:   return BlockTextures.TileMushroomStew;
                // Tier 4 #26 — Sugar cane block + paper/book items.
                // The block is cross-sprite so all face kinds map to
                // the same green-stalk tile; the mesher's EmitCrossSprite
                // path renders both diagonal planes from this layer.
                case BlockType.SugarCane:      return BlockTextures.TileSugarCane;
                case BlockType.SugarCaneItem:  return BlockTextures.TileSugarCaneItem;
                case BlockType.Paper:          return BlockTextures.TilePaper;
                case BlockType.Book:           return BlockTextures.TileBook;
                // Tier 4 #16 — Door block tiles. Each half has its own
                // tile (top half shows the cross-brace + window, bottom
                // shows the kick-plate + hinge band). The mesher's
                // EmitDoorSlab path uses these for both faces of the
                // slab quad — the texture itself is two-sided so the
                // hinge is on the correct visual side regardless of
                // which face the player views from. Item icons get
                // their own dedicated tiles (TileWoodDoorItem /
                // TileIronDoorItem) — Alpha 1.1.2 used full-height
                // door icons in the inventory, not the half-block art.
                case BlockType.WoodDoorBlockBottom: return BlockTextures.TileWoodDoorBottom;
                case BlockType.WoodDoorBlockTop:    return BlockTextures.TileWoodDoorTop;
                case BlockType.IronDoorBlockBottom: return BlockTextures.TileIronDoorBottom;
                case BlockType.IronDoorBlockTop:    return BlockTextures.TileIronDoorTop;
                case BlockType.WoodDoorItem:        return BlockTextures.TileWoodDoorItem;
                case BlockType.IronDoorItem:        return BlockTextures.TileIronDoorItem;
                // Tier 4 #17 — Flint and Steel + Apple icons. Both
                // are flat-sprite items, same render path as every
                // other ingredient — sprite tile sampled by the
                // hotbar / dropped-item / inventory-icon shaders.
                case BlockType.FlintAndSteel:       return BlockTextures.TileFlintAndSteel;
                case BlockType.Apple:               return BlockTextures.TileApple;
                // Tier 4 #20 — Snowball icon. Same flat-sprite path as
                // every other ingredient item; Egg's tile (TileEgg) is
                // already wired via the BlockType.Egg case above.
                case BlockType.Snowball:            return BlockTextures.TileSnowball;
                // Tier 4 #15 — Bucket family icons. Each has its own
                // flat-sprite tile — silver pail silhouette plus a
                // contents-coloured rim (water=blue, lava=orange,
                // milk=white). All four ship procedural; canonical
                // alpha_tools.png coords are sentinel.
                case BlockType.BucketEmpty:         return BlockTextures.TileBucketEmpty;
                case BlockType.BucketWater:         return BlockTextures.TileBucketWater;
                case BlockType.BucketLava:          return BlockTextures.TileBucketLava;
                case BlockType.BucketMilk:          return BlockTextures.TileBucketMilk;
                // Tier 4 #18 — Slimeball icon. Procedural — small green
                // sphere sprite painted in BlockTextures.
                case BlockType.Slimeball:           return BlockTextures.TileSlimeball;
                // Tier 4 #22 — Compass icon. Procedural — light grey
                // dial face with a fixed N marker at the top. The
                // direction-pointing arrow is rendered as a TEXTUAL
                // OVERLAY ("N"/"E"/"S"/"W") at hotbar render time on
                // top of this base sprite, NOT baked into the atlas
                // tile (would require per-frame atlas mutation).
                case BlockType.Compass:             return BlockTextures.TileCompass;
                // Tier 4 #21 — Saddle icon. Procedural — small brown
                // leather saddle silhouette. No verified alpha_tools.png
                // coord; the sentinel entry in AlphaTileCoords keeps the
                // slicer from overlaying garbage.
                case BlockType.Saddle:              return BlockTextures.TileSaddle;
                // Tier 4 #23 — Fishing Rod icon. Procedural — small
                // brown rod with a diagonal line and a hook at the
                // tip; sentinel atlas coord keeps the slicer out of
                // this layer in alpha-textures mode.
                case BlockType.FishingRod:          return BlockTextures.TileFishingRod;
                // Tier 4 #24 — Painting inventory icon. Procedural —
                // a small framed picture sprite (brown wood frame +
                // splash of colour inside). The on-wall art tiles
                // (TilePainting1x1..4x3) are separate atlas slots and
                // are sampled by GameRenderer.RenderPaintings, not by
                // this hotbar/inventory path.
                case BlockType.Painting:            return BlockTextures.TilePaintingItem;
                // Tier 4 #25 — Jukebox face tiles. Top tile shows the
                // disc-slot circle inset in a plank surface; sides are
                // a darker plank-with-darker-grain panel; bottom is
                // plain planks (matches the Furnace / Chest convention
                // — bottom faces don't get unique art).
                case BlockType.Jukebox:
                    if (faceKind == 0) return BlockTextures.TileJukeboxTop;
                    if (faceKind == 1) return BlockTextures.TileJukeboxBottom;
                    return BlockTextures.TileJukeboxSide;
                // Tier 4 #25 — Disc icons. Both are flat-sprite items;
                // hotbar / inventory / dropped-item paths sample these
                // through the standard non-block GetTileIndex branch.
                case BlockType.Disc13:              return BlockTextures.TileDisc13;
                case BlockType.DiscCat:             return BlockTextures.TileDiscCat;
                // Tier 4 #19 — Armor icons. Each piece gets its own
                // procedural sprite (per-material colour × per-slot
                // shape). Sentinel atlas coords keep the alpha-textures
                // slicer from overlaying garbage where the canonical
                // armor sheets aren't yet wired.
                case BlockType.LeatherHelmet:       return BlockTextures.TileLeatherHelmet;
                case BlockType.LeatherChestplate:   return BlockTextures.TileLeatherChestplate;
                case BlockType.LeatherLeggings:     return BlockTextures.TileLeatherLeggings;
                case BlockType.LeatherBoots:        return BlockTextures.TileLeatherBoots;
                case BlockType.ChainmailHelmet:     return BlockTextures.TileChainmailHelmet;
                case BlockType.ChainmailChestplate: return BlockTextures.TileChainmailChestplate;
                case BlockType.ChainmailLeggings:   return BlockTextures.TileChainmailLeggings;
                case BlockType.ChainmailBoots:      return BlockTextures.TileChainmailBoots;
                case BlockType.IronHelmet:          return BlockTextures.TileIronHelmet;
                case BlockType.IronChestplate:      return BlockTextures.TileIronChestplate;
                case BlockType.IronLeggings:        return BlockTextures.TileIronLeggings;
                case BlockType.IronBoots:           return BlockTextures.TileIronBoots;
                case BlockType.DiamondHelmet:       return BlockTextures.TileDiamondHelmet;
                case BlockType.DiamondChestplate:   return BlockTextures.TileDiamondChestplate;
                case BlockType.DiamondLeggings:     return BlockTextures.TileDiamondLeggings;
                case BlockType.DiamondBoots:        return BlockTextures.TileDiamondBoots;
                case BlockType.GoldHelmet:          return BlockTextures.TileGoldHelmet;
                case BlockType.GoldChestplate:      return BlockTextures.TileGoldChestplate;
                case BlockType.GoldLeggings:        return BlockTextures.TileGoldLeggings;
                case BlockType.GoldBoots:           return BlockTextures.TileGoldBoots;
                default:
                    return BlockTextures.TileStone;
            }
        }

        // Tier 4 #14 — Wheat tile per growth stage. Maps the per-cell
        // metadata low-4-bits (clamped to 0..7) to the corresponding
        // TileWheat0..TileWheat7 layer index. Called from the mesher's
        // EmitModels branch so each Wheat block in a chunk picks its
        // own tile — adjacent fully-grown wheat (stage 7) renders with
        // ripe golden tops while a freshly planted neighbour stays as
        // green sprouts.
        public static int GetWheatTileForStage(byte meta)
        {
            int stage = meta & 0x0F;
            if (stage < 0) stage = 0;
            if (stage > 7) stage = 7;
            switch (stage)
            {
                case 0: return BlockTextures.TileWheat0;
                case 1: return BlockTextures.TileWheat1;
                case 2: return BlockTextures.TileWheat2;
                case 3: return BlockTextures.TileWheat3;
                case 4: return BlockTextures.TileWheat4;
                case 5: return BlockTextures.TileWheat5;
                case 6: return BlockTextures.TileWheat6;
                default: return BlockTextures.TileWheat7;
            }
        }

        // Oriented-face tile lookup. Used by the mesher when the block
        // type has a "front" face whose orientation depends on a per-
        // block facing (Furnace / LitFurnace / Chest). For all other
        // types the un-oriented GetTileIndex is fine and this function
        // falls back to it.
        //
        // axis/dir match the mesher's sweep semantics: axis 0 = X,
        // axis 1 = Y, axis 2 = Z; dir is +1 or -1.
        //
        // For furnaces:
        //   * top/bottom faces (axis 1) → TileFurnaceTop
        //   * the side face whose outward normal matches `facing` →
        //     the front tile (lit or unlit per block id)
        //   * the other three side faces → TileFurnaceSide
        // For chests:
        //   * top face → TileChestTop (lid)
        //   * bottom face → TilePlanks
        //   * the side face whose outward normal matches `facing` →
        //     TileChestFront (latch + keyhole)
        //   * the other three side faces → TileChestSide
        public static int GetTileIndexForOriented(
            BlockType t, int axis, int dir, BlockFacing facing)
        {
            if (t == BlockType.Furnace || t == BlockType.LitFurnace)
            {
                if (axis == 1) return BlockTextures.TileFurnaceTop;
                int frontTile = (t == BlockType.LitFurnace)
                    ? BlockTextures.TileFurnaceFrontLit
                    : BlockTextures.TileFurnaceFront;
                return IsFacingFront(axis, dir, facing)
                    ? frontTile
                    : BlockTextures.TileFurnaceSide;
            }
            if (t == BlockType.Chest)
            {
                if (axis == 1)
                    return dir > 0 ? BlockTextures.TileChestTop : BlockTextures.TilePlanks;
                return IsFacingFront(axis, dir, facing)
                    ? BlockTextures.TileChestFront
                    : BlockTextures.TileChestSide;
            }
            // Tier 8 #51 — Jack-o-lantern matches pumpkin on every
            // face EXCEPT the front-facing lateral side, which
            // shows the carved + lit face. Top = pumpkin top,
            // bottom = pumpkin side (same shortcut Pumpkin uses
            // for its bottom — the underside of a placed pumpkin /
            // jack-o-lantern is rarely visible and reusing the side
            // tile saves an atlas slot). 3 of the 4 lateral faces
            // show TilePumpkinSide; the face matching `facing` shows
            // TileJackOLanternFront.
            if (t == BlockType.JackOLantern)
            {
                if (axis == 1 && dir > 0) return BlockTextures.TilePumpkinTop;
                if (axis == 1)            return BlockTextures.TilePumpkinSide;
                return IsFacingFront(axis, dir, facing)
                    ? BlockTextures.TileJackOLanternFront
                    : BlockTextures.TilePumpkinSide;
            }
            // Tier 8 #49 V1 — Dispenser. Top + bottom = furnace top
            // (stone cap with iron vent); 3 of the 4 lateral faces
            // reuse the furnace side panel; the face matching
            // `facing` shows the dispenser-front tile (canonical
            // Alpha "loaded crossbow" silhouette).
            if (t == BlockType.Dispenser)
            {
                if (axis == 1) return BlockTextures.TileFurnaceTop;
                return IsFacingFront(axis, dir, facing)
                    ? BlockTextures.TileDispenserFront
                    : BlockTextures.TileFurnaceSide;
            }
            return GetTileIndex(t, ChunkMesherFaceKind(axis, dir));
        }

        // Compare a face's (axis, dir) outward normal to the cardinal
        // direction encoded by `facing`. Cardinal mapping:
        //   North = -Z (axis 2, dir -1)
        //   South = +Z (axis 2, dir +1)
        //   East  = +X (axis 0, dir +1)
        //   West  = -X (axis 0, dir -1)
        private static bool IsFacingFront(int axis, int dir, BlockFacing facing)
        {
            int fAxis, fDir;
            switch (facing)
            {
                case BlockFacing.East:  fAxis = 0; fDir = +1; break;
                case BlockFacing.West:  fAxis = 0; fDir = -1; break;
                case BlockFacing.South: fAxis = 2; fDir = +1; break;
                default: /* North */    fAxis = 2; fDir = -1; break;
            }
            return axis == fAxis && dir == fDir;
        }

        private static int ChunkMesherFaceKind(int axis, int dir)
        {
            // Match ChunkMesher.FaceKindFor: top=0, bottom=1, side=2.
            if (axis == 1) return dir > 0 ? 0 : 1;
            return 2;
        }

        // Tier 4 #16 — Door predicate helpers. The break path, mesher,
        // and interact path all need to ask "is this any door half?"
        // / "is this a top half / bottom half?" without listing all
        // four block ids in switch-cases at every site. Centralised
        // here so adding a new door material in the future (e.g.
        // gold / diamond if the project ever extends past Alpha
        // parity) only needs an edit here.
        public static bool IsDoor(BlockType t)
            => t == BlockType.WoodDoorBlockBottom
            || t == BlockType.WoodDoorBlockTop
            || t == BlockType.IronDoorBlockBottom
            || t == BlockType.IronDoorBlockTop;

        // Tier 8 #45 V1 — True for any of the 4 slab variants. Used
        // by mesher / placement / collision dispatch to take the
        // half-cube branch without listing all four ids at every
        // site. Adding a new slab material in the future (sandstone
        // slab, etc.) only needs an edit here.
        public static bool IsSlab(BlockType t)
            => t == BlockType.StoneSlab
            || t == BlockType.CobblestoneSlab
            || t == BlockType.BrickSlab
            || t == BlockType.WoodSlab;

        // Tier 8 #45 V2 — True for any stair variant. Used by mesher /
        // placement / collision dispatch to take the L-shape branch
        // without listing both ids at every site.
        public static bool IsStair(BlockType t)
            => t == BlockType.WoodStairs
            || t == BlockType.CobblestoneStairs;

        public static bool IsDoorBottom(BlockType t)
            => t == BlockType.WoodDoorBlockBottom
            || t == BlockType.IronDoorBlockBottom;

        public static bool IsDoorTop(BlockType t)
            => t == BlockType.WoodDoorBlockTop
            || t == BlockType.IronDoorBlockTop;

        // Given a door BlockType (any half), return the BOTTOM half
        // for the same material. Used by the break/interact paths
        // when the player hits the top half — they need to find the
        // matching bottom cell to remove or toggle. Returns Air for
        // non-door inputs as a defensive default.
        public static BlockType DoorBottomFor(BlockType t)
        {
            switch (t)
            {
                case BlockType.WoodDoorBlockBottom:
                case BlockType.WoodDoorBlockTop:
                    return BlockType.WoodDoorBlockBottom;
                case BlockType.IronDoorBlockBottom:
                case BlockType.IronDoorBlockTop:
                    return BlockType.IronDoorBlockBottom;
                default:
                    return BlockType.Air;
            }
        }

        // Inverse: given any door half, return the TOP half for the
        // same material. Same use-cases as DoorBottomFor.
        public static BlockType DoorTopFor(BlockType t)
        {
            switch (t)
            {
                case BlockType.WoodDoorBlockBottom:
                case BlockType.WoodDoorBlockTop:
                    return BlockType.WoodDoorBlockTop;
                case BlockType.IronDoorBlockBottom:
                case BlockType.IronDoorBlockTop:
                    return BlockType.IronDoorBlockTop;
                default:
                    return BlockType.Air;
            }
        }

        // Translate a door block (any half/material) to the ITEM that
        // drops when broken. Both wood halves drop a single
        // WoodDoorItem (the player gets the door back as one piece —
        // Alpha behaviour); both iron halves drop IronDoorItem. The
        // GameRenderer break path calls DoorBottomFor first to remove
        // both halves, then this helper for the single drop.
        public static BlockType DoorDropItem(BlockType t)
        {
            switch (t)
            {
                case BlockType.WoodDoorBlockBottom:
                case BlockType.WoodDoorBlockTop:
                    return BlockType.WoodDoorItem;
                case BlockType.IronDoorBlockBottom:
                case BlockType.IronDoorBlockTop:
                    return BlockType.IronDoorItem;
                default:
                    return BlockType.Air;
            }
        }

        // Translate a held door ITEM to the BOTTOM block half it
        // should spawn on placement. The TOP half is always
        // DoorTopFor of the bottom — only one branch needed.
        public static BlockType DoorBottomBlockForItem(BlockType item)
        {
            if (item == BlockType.WoodDoorItem) return BlockType.WoodDoorBlockBottom;
            if (item == BlockType.IronDoorItem) return BlockType.IronDoorBlockBottom;
            return BlockType.Air;
        }

        // Door metadata byte format (per spec):
        //   bit 0      (mask 0x01): open flag (0=closed, 1=open)
        //   bits 1..2  (mask 0x06, >>1): facing (0=N, 1=E, 2=S, 3=W)
        //   bit 3      (mask 0x08): hinge side (0=left, 1=right)
        // The metadata is stored in Chunk._meta and persisted via
        // WriteSparseMeta (filtered to door block ids alongside
        // Wheat). Both halves of a door MUST keep their bytes in
        // sync — the interact / placement code writes both cells
        // with the same byte. Helpers below pack and unpack the
        // bits so the renderer / interact / placement paths don't
        // touch the bit layout directly.
        public const byte DoorMetaOpenBit  = 0x01;
        public const byte DoorMetaHingeBit = 0x08;

        public static bool DoorIsOpen(byte meta)  => (meta & DoorMetaOpenBit)  != 0;
        public static bool DoorHingeRight(byte meta) => (meta & DoorMetaHingeBit) != 0;

        public static BlockFacing DoorFacing(byte meta)
        {
            int f = (meta >> 1) & 0x03;
            switch (f)
            {
                case 0: return BlockFacing.North;
                case 1: return BlockFacing.East;
                case 2: return BlockFacing.South;
                default: return BlockFacing.West;
            }
        }

        public static byte DoorPackMeta(BlockFacing facing, bool open, bool hingeRight)
        {
            int f;
            switch (facing)
            {
                case BlockFacing.East:  f = 1; break;
                case BlockFacing.South: f = 2; break;
                case BlockFacing.West:  f = 3; break;
                default:                f = 0; break; // North
            }
            byte b = (byte)((f & 0x03) << 1);
            if (open) b |= DoorMetaOpenBit;
            if (hingeRight) b |= DoorMetaHingeBit;
            return b;
        }

        public static byte DoorWithOpen(byte meta, bool open)
            => (byte)(open ? (meta | DoorMetaOpenBit) : (meta & ~DoorMetaOpenBit));
    }

    // Tool kind drives which block family the tool is "effective" against
    // (faster break + drop eligibility for ores). None means "this stack
    // isn't a tool" — the helpers below return defensive defaults so a
    // bare-hand swing on stone still drops nothing without crashing the
    // break path. Hoe joined the kind set in Tier 4 #14; it doesn't
    // speed-mine any block and isn't a RequiredKind for anything — it
    // only acts as the "till on RMB" interactor (see
    // GameRenderer.TryInteract).
    internal enum ToolKind { None, Sword, Shovel, Pickaxe, Axe, Hoe }

    // Tier ladder for harvest eligibility. Wood and Gold sit at the same
    // tier (Alpha quirk: gold mines fast but as poorly as wood); Stone is
    // tier 2; Iron tier 3; Diamond tier 4. CanHarvest checks
    // tool tier >= block requirement.
    internal enum ToolMaterial { None, Wood, Stone, Iron, Diamond, Gold }

    internal static class ToolData
    {
        // Vanilla Alpha durability values, rounded to the closest 16-bit
        // integer. Gold is the most fragile despite mining the fastest;
        // Diamond outlasts every other tier by a wide margin.
        public static short MaxDurability(BlockType t)
        {
            switch (GetMaterial(t))
            {
                case ToolMaterial.Wood:    return 60;
                case ToolMaterial.Stone:   return 132;
                case ToolMaterial.Iron:    return 251;
                case ToolMaterial.Gold:    return 33;
                case ToolMaterial.Diamond: return 1562;
                default:                   return 0;
            }
        }

        public static ToolKind GetKind(BlockType t)
        {
            if (!BlockData.IsTool(t)) return ToolKind.None;
            // Hoes were appended past the original [WoodSword..GoldAxe]
            // tool slice so the chunked-into-5 arithmetic doesn't reach
            // them — branch on the hoe range first.
            if ((byte)t >= (byte)BlockType.WoodHoe && (byte)t <= (byte)BlockType.GoldHoe)
                return ToolKind.Hoe;
            int chunk = ((byte)t - (byte)BlockType.WoodSword) / 5;
            switch (chunk)
            {
                case 0: return ToolKind.Sword;
                case 1: return ToolKind.Shovel;
                case 2: return ToolKind.Pickaxe;
                case 3: return ToolKind.Axe;
                default: return ToolKind.None;
            }
        }

        public static ToolMaterial GetMaterial(BlockType t)
        {
            if (!BlockData.IsTool(t)) return ToolMaterial.None;
            // Hoe slice — same wood/stone/iron/diamond/gold ordering as
            // the rest of the tool ladder; we just rebase to WoodHoe.
            if ((byte)t >= (byte)BlockType.WoodHoe && (byte)t <= (byte)BlockType.GoldHoe)
            {
                int hidx = (byte)t - (byte)BlockType.WoodHoe;
                switch (hidx)
                {
                    case 0: return ToolMaterial.Wood;
                    case 1: return ToolMaterial.Stone;
                    case 2: return ToolMaterial.Iron;
                    case 3: return ToolMaterial.Diamond;
                    case 4: return ToolMaterial.Gold;
                }
                return ToolMaterial.None;
            }
            int idx = ((byte)t - (byte)BlockType.WoodSword) % 5;
            switch (idx)
            {
                case 0: return ToolMaterial.Wood;
                case 1: return ToolMaterial.Stone;
                case 2: return ToolMaterial.Iron;
                case 3: return ToolMaterial.Diamond;
                case 4: return ToolMaterial.Gold;
                default: return ToolMaterial.None;
            }
        }

        // Tier numbers used by both the tool's harvest level (the highest
        // tier it can mine) and the block's required-tier (the minimum
        // tier needed for a drop). Gold sits at tier 1 — it's faster than
        // wood but not "stronger" in the harvest-tier sense, matching
        // Alpha's "wood/gold mines stone but not iron" rule.
        public static int Tier(ToolMaterial m)
        {
            switch (m)
            {
                case ToolMaterial.Wood:    return 1;
                case ToolMaterial.Gold:    return 1;
                case ToolMaterial.Stone:   return 2;
                case ToolMaterial.Iron:    return 3;
                case ToolMaterial.Diamond: return 4;
                default:                   return 0;
            }
        }

        // Minimum tier a pickaxe needs to drop the broken block.
        // Non-pickaxe-required blocks return 0 here — caller still checks
        // RequiredKind separately (e.g. dirt requires no tool kind, but
        // shovels are faster, so it's drop-allowed regardless).
        public static int RequiredTier(BlockType block)
        {
            switch (block)
            {
                case BlockType.Stone:
                case BlockType.Cobblestone:
                case BlockType.MossyCobblestone:
                case BlockType.CoalOre:
                case BlockType.Bricks:
                case BlockType.Sponge: // not really, but a stone pick feels right
                case BlockType.Furnace:
                case BlockType.LitFurnace:
                    return 1;
                case BlockType.IronOre:
                case BlockType.IronBlock:
                    return 2;
                case BlockType.GoldOre:
                case BlockType.DiamondOre:
                case BlockType.RedstoneOre:
                case BlockType.GoldBlock:
                case BlockType.DiamondBlock:
                    return 3;
                case BlockType.Obsidian:
                    return 4;
                default:
                    return 0;
            }
        }

        // Which tool kind is "correct" for the block — i.e. the kind
        // whose effectiveness multiplier applies AND whose presence makes
        // the block drop. Multiple kinds can be effective on a single
        // block in vanilla, but Alpha keeps the rules simple: stone-y
        // blocks need a pickaxe, dirt-y blocks like a shovel, wood-y
        // blocks like an axe. Returns None when the block has no
        // preferred tool — anything goes (or nothing required).
        public static ToolKind RequiredKind(BlockType block)
        {
            switch (block)
            {
                case BlockType.Stone:
                case BlockType.Cobblestone:
                case BlockType.MossyCobblestone:
                case BlockType.Bricks:
                case BlockType.Obsidian:
                case BlockType.CoalOre:
                case BlockType.IronOre:
                case BlockType.GoldOre:
                case BlockType.DiamondOre:
                case BlockType.RedstoneOre:
                case BlockType.GoldBlock:
                case BlockType.IronBlock:
                case BlockType.DiamondBlock:
                case BlockType.Furnace:
                case BlockType.LitFurnace:
                    return ToolKind.Pickaxe;
                case BlockType.Dirt:
                case BlockType.Grass:
                case BlockType.Sand:
                case BlockType.Gravel:
                case BlockType.Clay:
                    return ToolKind.Shovel;
                case BlockType.WoodLog:
                case BlockType.Planks:
                case BlockType.Bookshelf:
                case BlockType.CraftingTable:
                case BlockType.Chest:
                    return ToolKind.Axe;
                default:
                    return ToolKind.None;
            }
        }

        // Per-material break-speed multiplier when the right kind of tool
        // is held against an effective block. Gold is dramatically fastest
        // but its low durability + tier-1 harvest mean it's a niche
        // choice for fast cobble runs, not a real upgrade path. Anything
        // not effective falls back to 1× (bare-hand speed).
        public static float SpeedMultiplier(BlockType tool, BlockType block)
        {
            if (!BlockData.IsTool(tool)) return 1f;
            var kind = GetKind(tool);
            var required = RequiredKind(block);
            // Sword: 1.5× on cobwebs/leaves; we treat leaves as effective
            // so saplings/vines collection isn't a chore. Otherwise no
            // speed bonus (and a small 1× to keep the durability tick).
            if (kind == ToolKind.Sword)
            {
                if (block == BlockType.Leaves) return 1.5f;
                return 1f;
            }
            if (required != ToolKind.None && required != kind) return 1f;
            switch (GetMaterial(tool))
            {
                case ToolMaterial.Wood:    return 2f;
                case ToolMaterial.Stone:   return 4f;
                case ToolMaterial.Iron:    return 6f;
                case ToolMaterial.Diamond: return 8f;
                case ToolMaterial.Gold:    return 12f;
                default:                   return 1f;
            }
        }

        // Drop eligibility rule for a block broken with the given tool
        // (which may be Air / a non-tool stack). Mirrors Alpha:
        //   * Blocks with no required tier (dirt, sand, gravel, wood,
        //     planks, leaves, ...) always drop, even bare-handed —
        //     RequiredKind on those is just a "preferred for speed"
        //     hint that SpeedMultiplier reads. Bare-hand mining is
        //     slow but yields the block.
        //   * Blocks with a required tier (stone, ores, obsidian, the
        //     metal blocks) gate the drop on tool kind + tier: the
        //     tool must be the right kind (pickaxe in practice) AND
        //     its material tier must be at or above the block's.
        // Returns true if the break should drop an item; false means
        // "broke but yielded nothing" (the silent-stone outcome).
        public static bool CanHarvest(BlockType tool, BlockType block)
        {
            int reqTier = RequiredTier(block);
            if (reqTier <= 0) return true;
            var required = RequiredKind(block);
            var kind = GetKind(tool);
            if (kind != required) return false;
            int tier = Tier(GetMaterial(tool));
            return tier >= reqTier;
        }

        // Translate a block being broken into the BlockType that should
        // drop. Stone drops Cobblestone (when harvest-eligible); ores drop
        // their raw form; everything else drops itself. Caller is
        // responsible for the CanHarvest gate before calling this.
        public static BlockType DropFor(BlockType block)
        {
            switch (block)
            {
                case BlockType.Stone: return BlockType.Cobblestone;
                // Ore-to-item drops. Vanilla Alpha drops the *item* form
                // for coal and diamond ores (no smelting needed); iron
                // and gold ores drop the ore block and require furnace
                // smelting to become ingots — the furnace tick (now
                // landed) consumes the ore block and produces an ingot.
                case BlockType.CoalOre:    return BlockType.Coal;
                case BlockType.DiamondOre: return BlockType.Diamond;
                // Tier 8 #51 — Glowstone block breaks into Glowstone
                // Dust, NOT a glowstone block back. Same shape as
                // ore-to-item drops above. Quantity (2..4) is
                // randomised by the survival drop loop in GameRenderer
                // — DropFor is single-item, the multi-drop happens at
                // the call site via the existing GetDropQuantity hook
                // (see Tier 6 #37 for the same pattern with snow
                // layers dropping snowballs).
                case BlockType.Glowstone:  return BlockType.GlowstoneDust;
                // A broken LitFurnace drops the un-lit Furnace item — the
                // burning state is part of the tile entity, not the
                // dropped item. Tile-entity teardown (in GameRenderer)
                // also spills any in-progress contents as separate
                // drops, so the player keeps anything they had cooking.
                case BlockType.LitFurnace: return BlockType.Furnace;
                // All wall-torch variants drop the generic floor-torch
                // item — the orientation only matters while the block is
                // placed in the world. Picking it back up gives you a
                // fungible Torch you can place however you like next.
                case BlockType.TorchEast:
                case BlockType.TorchWest:
                case BlockType.TorchSouth:
                case BlockType.TorchNorth:
                    return BlockType.Torch;
                // Tier 4 #16 — Doors drop the item form, not the block
                // half. The break path collapses both halves into a
                // single item drop (see ScanDoorCascadeOnBreak in
                // GameRenderer), but DropFor still needs to map each
                // half to its item so the survival drop pipeline
                // routes correctly when the cascade scan finds only
                // ONE half left (e.g. the other was already air-broken
                // by some other agent — falls back to a single drop).
                case BlockType.WoodDoorBlockBottom:
                case BlockType.WoodDoorBlockTop:
                    return BlockType.WoodDoorItem;
                case BlockType.IronDoorBlockBottom:
                case BlockType.IronDoorBlockTop:
                    return BlockType.IronDoorItem;
                // Tier 8 #42 — Both redstone torch states drop the
                // canonical "lit" variant — Alpha gives back the
                // active item form when you mine either, mirroring
                // how a wall-torch break gives back the floor torch.
                case BlockType.RedstoneTorchOff:
                    return BlockType.RedstoneTorchOn;
                // Wire breaks back to the dust item — a placed wire
                // is just visually-routed dust; you get back the raw
                // ingredient.
                case BlockType.RedstoneWire:
                    return BlockType.RedstoneDust;
                // Tier 8 #44 — Both sign block variants drop the
                // generic SignItem; the player gets back a fungible
                // sign they can re-place in any orientation. The
                // break path also tears down the tile entity (text +
                // facing are NOT preserved across break + replace —
                // matches Alpha behaviour and intuitive reset).
                case BlockType.SignPost:
                case BlockType.WallSign:
                    return BlockType.SignItem;
                default: return block;
            }
        }
    }
}
