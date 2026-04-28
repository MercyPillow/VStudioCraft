using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using OpenTK.Graphics.OpenGL;
// Aliased to avoid collision with OpenTK.Graphics.OpenGL.PixelFormat,
// which is used pervasively in this file for GL.TexImage3D etc.
using GdiPixelFormat = System.Drawing.Imaging.PixelFormat;
using GdiImageLockMode = System.Drawing.Imaging.ImageLockMode;

namespace VStudioCraft.Game
{
    // Procedural 16x16 tiles arranged as a 2D texture array, emulating Alpha
    // 1.1.2_01's coarse three-tone noise look. Palettes approximate the alpha
    // terrain.png, but no pixels are copied — every tile is regenerated from a
    // seeded RNG so the output is original art with an alpha-era feel.
    internal static class BlockTextures
    {
        public const int TileSize = 16;
        // Layers 0..37 are blocks (sourced from terrain.png in alpha mode,
        // procedurally generated otherwise). Layers 38..57 are tool icons
        // and 58..66 are non-tool ingredient items (Stick, Coal, ingots,
        // Diamond, Flint, ClayBall/Brick, Bowl). In alpha-textures mode
        // the entire 38..66 range is sliced out of a single alpha_tools.png
        // (Alpha packs tools and ingredients together at canonical Notch
        // coordinates); in procedural mode both ranges are synthesised
        // directly. Material order in the tool slice matches BlockType
        // enum: wood, stone, iron, diamond, gold.
        //
        // Layers 67+ are "tail block" tiles — additional block face textures
        // added after the tool/item slice was frozen. They source from
        // terrain.png in alpha mode (same as 0..37) and from procedural
        // generators in procedural mode. The tool/item slice loop bounds
        // itself with FirstTailBlockLayer so it doesn't try to read these
        // out of alpha_tools.png. Currently used for the multi-face
        // crafting table tiles (top + side). Adding more multi-face
        // blocks later (furnace, dispenser, jukebox, etc.) follows the
        // same pattern: append to the tail range.
        public const int BlockLayerCount = 38;
        public const int ToolLayerCount = 20;
        public const int ItemLayerCount = 9;
        public const int FirstItemLayer = BlockLayerCount + ToolLayerCount; // 58
        public const int FirstTailBlockLayer = FirstItemLayer + ItemLayerCount; // 67
        // 9 tail tiles: CraftingTableTop, CraftingTableSide, FurnaceTop,
        // FurnaceSide, FurnaceFront, FurnaceFrontLit, ChestTop,
        // ChestSide, ChestFront. Each new multi-face block gets a
        // contiguous block of layers appended here so the tool/item
        // slice (which assumes everything past it sources from
        // terrain.png) stays untouched.
        public const int TailBlockLayerCount = 9;
        // Tail item layers — appended past the tail blocks. Items added
        // after the original 9-item slice (Tier 3 #9 porkchops, Tier 3
        // #10 hostile-mob drops) live here so we don't have to renumber
        // the existing item / tail-block indices and break v7 saves.
        public const int FirstTailItemLayer = FirstTailBlockLayer + TailBlockLayerCount; // 76
        public const int TailItemLayerCount = 9;
        // Tier 4 #14 — Hoe tail-tool slice. Five hoes (wood/stone/iron/
        // diamond/gold) appended past the original tool range so we can
        // ship farming without renumbering swords/shovels/pickaxes/axes
        // (which would invalidate v7 saves and every recipe table coord).
        // Layout mirrors the canonical Alpha 1.1.2 hoes row in
        // alpha_tools.png — material order wood→gold left-to-right at
        // row index 8, immediately past the axes at row 7.
        public const int FirstTailTool2Layer = FirstTailItemLayer + TailItemLayerCount; // 85
        public const int TailTool2LayerCount = 5;
        // Tier 4 #14 — Farming tile pack appended past the hoe slice.
        // 1 farmland top + 8 wheat growth stages + 4 farming items
        // (seeds, harvested wheat, bread, mushroom stew). Wheat uses 8
        // stages so the metadata low-4-bits map directly to tile index
        // via GetWheatTileForStage; the mesher's cross-sprite path then
        // picks the right sub-tile per chunk cell.
        public const int FirstTailFarmLayer = FirstTailTool2Layer + TailTool2LayerCount; // 90
        public const int TailFarmLayerCount = 13;
        // Tier 4 #26 — Sugar cane pack appended past the farming slice.
        // 1 cross-sprite block tile (TileSugarCane) + 3 item tiles
        // (TileSugarCaneItem, TilePaper, TileBook). Block tile is the
        // green-stalk layer the mesher picks via Block.GetTileIndex; the
        // three item tiles are flat icons. Appended past the farm slice
        // so existing v8 saves stay byte-stable — no atlas index changes
        // for any pre-existing tile.
        public const int FirstTailCaneLayer = FirstTailFarmLayer + TailFarmLayerCount;  // 103
        public const int TailCaneLayerCount = 4;
        // Tier 4 #16 — Door tile pack appended past the cane slice. 4
        // block-half tiles (wood top/bottom + iron top/bottom) — the
        // mesher samples these for both faces of the thin slab — plus
        // 2 inventory-icon tiles for the WoodDoorItem / IronDoorItem
        // forms. Block-half tiles are full 16×16 art with the visible
        // rectangle aligned to the relevant edge of the cell (top half
        // is flush-top, bottom half is flush-bottom); item icons are
        // tall-rectangle silhouettes sized to read as a complete door.
        // Append-only past the cane slice so all existing v8 saves
        // stay byte-stable — no atlas index changes for any pre-
        // existing tile.
        public const int FirstTailDoorLayer = FirstTailCaneLayer + TailCaneLayerCount; // 107
        public const int TailDoorLayerCount = 6;
        // Tier 4 #17 — Bow combat companion items. Two new flat-sprite
        // icons: TileFlintAndSteel and TileApple. Append-only past the
        // door pack so existing v8 saves stay byte-stable. Both ship
        // procedural (the embedded alpha_tools.png coords for the two
        // sprites haven't been verified, so AlphaTileCoords carries
        // sentinels and the slice helpers fall through to the
        // procedural generators in either atlas mode).
        public const int FirstTailFireLayer = FirstTailDoorLayer + TailDoorLayerCount; // 113
        public const int TailFireLayerCount = 2;
        // Tier 4 #20 — Snowball throwable icon. Egg already has a
        // sprite at TileEgg=84 (Tier 3 #12); only the new snowball
        // needs an atlas slot. Append-only past the fire pack.
        public const int FirstTailThrowLayer = FirstTailFireLayer + TailFireLayerCount; // 115
        public const int TailThrowLayerCount = 1;
        // Tier 4 #15 — Bucket family icons (4 sprites: empty + water +
        // lava + milk). Procedural-only — same alpha_tools.png-coord-
        // unverified story as the door / fire / throw packs. Append-
        // only past the throw pack so existing v8 saves stay byte-
        // stable (atlas indices for every preceding tile are fixed).
        public const int FirstTailBucketLayer = FirstTailThrowLayer + TailThrowLayerCount; // 116
        public const int TailBucketLayerCount = 4;
        // Tier 4 #18 — Slimeball icon (Alpha 341). One sprite appended
        // past the bucket pack. Procedural-only — no verified
        // alpha_tools.png coord, sentinel below. Append-only past the
        // bucket pack so existing v8 saves stay byte-stable.
        public const int FirstTailSlimeLayer  = FirstTailBucketLayer + TailBucketLayerCount; // 120
        public const int TailSlimeLayerCount  = 1;
        // Tier 4 #22 — Compass icon (Alpha 345). One sprite appended
        // past the slime pack. Procedural-only — the atlas-base sprite
        // is a static "white dial face with N marker" image; the
        // rotating direction indicator is drawn as a TEXTUAL overlay
        // in GameRenderer.RenderHotbar (NOT baked here — would require
        // per-frame atlas mutation). Append-only past the slime pack
        // so existing v8 saves stay byte-stable.
        public const int FirstTailCompassLayer = FirstTailSlimeLayer + TailSlimeLayerCount;  // 121
        public const int TailCompassLayerCount = 1;
        // Tier 4 #21 — Saddle icon (Alpha 329). One sprite appended
        // past the compass pack. Procedural-only — no verified
        // alpha_tools.png coord, sentinel below. Append-only past the
        // compass pack so existing v8/v9 saves stay byte-stable (every
        // preceding atlas index is unchanged).
        public const int FirstTailSaddleLayer  = FirstTailCompassLayer + TailCompassLayerCount; // 122
        public const int TailSaddleLayerCount  = 1;
        // Tier 4 #23 — Fishing Rod icon (Alpha 346). One sprite
        // appended past the saddle pack. Procedural-only — no verified
        // alpha_tools.png coord, sentinel below. Append-only past the
        // saddle pack so existing v8/v9 saves stay byte-stable (every
        // preceding atlas index is unchanged).
        public const int FirstTailFishingRodLayer = FirstTailSaddleLayer + TailSaddleLayerCount; // 123
        public const int TailFishingRodLayerCount = 1;
        // Tier 4 #24 — Painting pack. 5 "art" tiles for the 5 painting
        // size variants (1×1, 1×2, 2×1, 2×2, 4×3) plus 1 inventory icon
        // for the held item. The variant-art tiles are sampled by
        // GameRenderer.RenderPaintings on the wall plane; the icon tile
        // is sampled by the standard hotbar/inventory paths via
        // Block.GetTileIndex. All 6 ship procedural — there's no
        // verified alpha_tools.png coord for the painting sprite OR for
        // the per-variant art (Alpha 1.1.2_01 packs ~26 painting
        // variants of various sizes into kz.png; we ship 5 representative
        // variants here, each procedurally painted as a recognisable
        // pattern). Append-only past the fishing-rod pack so existing
        // v8/v9 saves stay byte-stable. Note that painting art tiles are
        // 16×16 same as every other atlas slot — for sizes other than
        // 1×1 the renderer STRETCHES the tile across the full painting
        // rectangle. Quality is poor at 4×3 but functionally correct;
        // a per-variant native-resolution atlas would require relaxing
        // the fixed-tile-size atlas assumption (TODO).
        public const int FirstTailPaintingLayer = FirstTailFishingRodLayer + TailFishingRodLayerCount; // 124
        public const int TailPaintingLayerCount = 6;
        // Tier 4 #25 — Jukebox + Music Disc pack. 3 block-face tiles for
        // the Jukebox (top with disc-slot circle, side with dark plank
        // grain, bottom plain planks) plus 2 item-icon tiles for the
        // two music discs ("13" with a white "13" label, "cat" with a
        // cyan label so the two read distinctly in the hotbar). All
        // five ship procedural — Alpha 1.1.2 packs the jukebox tiles
        // in terrain.png at canonical coords (jukebox top at (10,4),
        // side at (11,4)) and the disc sprites in alpha_tools.png, but
        // we haven't verified those coords against the embedded sheets
        // here so the slicer stays out via sentinel coords and the
        // procedural generators paint the final tiles in both atlas
        // modes. Append-only past the painting pack so existing
        // v8/v9/v10 saves stay byte-stable.
        public const int FirstTailJukeboxLayer = FirstTailPaintingLayer + TailPaintingLayerCount; // 130
        public const int TailJukeboxLayerCount = 5;
        // Tier 4 #19 — Armor inventory icon pack. 20 procedural item
        // tiles (5 materials × 4 slots), appended past the jukebox pack
        // so existing v8..v11 atlas slicing stays stable. Per-material
        // colour story: Leather=brown, Chainmail=silver/grey, Iron=
        // light grey, Diamond=cyan/white, Gold=yellow. Per-slot shape:
        // helmet=hooded square, chestplate=tall body+sleeves
        // rectangle, leggings=H-shape, boots=two small squares.
        public const int FirstTailArmorLayer = FirstTailJukeboxLayer + TailJukeboxLayerCount; // 135
        public const int TailArmorLayerCount = 20;
        public const int LayerCount = FirstTailArmorLayer + TailArmorLayerCount;              // 155
        // Porkchop tile indices.
        public const int TileRawPorkchop    = 76;
        public const int TileCookedPorkchop = 77;
        // Hostile-mob drop tile indices (Tier 3 #10).
        public const int TileBow            = 78;
        public const int TileArrow          = 79;
        public const int TileString         = 80;
        public const int TileGunpowder      = 81;
        // Passive-mob drop tile indices (Tier 3 #12).
        public const int TileLeather        = 82;
        public const int TileFeather        = 83;
        public const int TileEgg            = 84;
        // Tier 4 #14 — Hoe tile indices (tail tool slice).
        public const int TileWoodHoe        = 85;
        public const int TileStoneHoe       = 86;
        public const int TileIronHoe        = 87;
        public const int TileDiamondHoe     = 88;
        public const int TileGoldHoe        = 89;
        // Tier 4 #14 — Farming tile indices. FarmlandTop is the only
        // distinct farmland face — sides/bottom reuse TileDirt via the
        // multi-face routing in Block.GetTileIndex. Wheat uses 8 stages
        // 0..7; stage 0 is freshly planted sprouts, stage 7 is
        // fully-grown ripe wheat ready to harvest.
        public const int TileFarmlandTop   = 90;
        public const int TileWheat0        = 91;
        public const int TileWheat1        = 92;
        public const int TileWheat2        = 93;
        public const int TileWheat3        = 94;
        public const int TileWheat4        = 95;
        public const int TileWheat5        = 96;
        public const int TileWheat6        = 97;
        public const int TileWheat7        = 98;
        public const int TileWheatSeeds    = 99;
        public const int TileWheatItem     = 100;
        public const int TileBread         = 101;
        public const int TileMushroomStew  = 102;
        // Tier 4 #26 — Sugar cane block + paper + book tiles. SugarCane
        // is the in-world cross-sprite tile (green stalk with darker
        // segment bands). SugarCaneItem is the harvested-cane item
        // sprite. Paper is an off-white sheet. Book is a brown leather
        // cover with paper edges. All four are procedural; the verified
        // alpha terrain.png coords for sugar cane (col 9 row 4) and the
        // alpha_tools.png coords for paper/book are wired with sentinels
        // for now — bumping them to real coords later is a one-line edit
        // per row in AlphaTileCoords.
        public const int TileSugarCane     = 103;
        public const int TileSugarCaneItem = 104;
        public const int TilePaper         = 105;
        public const int TileBook          = 106;
        // Tier 4 #16 — Door tiles. Block halves use half-height art
        // pinned to the relevant edge of the 16×16 tile so the slab
        // mesher can sample the full square and the texture self-aligns
        // to the visible 8-pixel slab strip. Wood + Iron variants use
        // matching silhouettes (cross-brace, hinge band, knob/handle)
        // with material-appropriate palettes — wood is plank-brown with
        // dark seams, iron is grey steel with dark hinge bolts.
        public const int TileWoodDoorTop    = 107;
        public const int TileWoodDoorBottom = 108;
        public const int TileIronDoorTop    = 109;
        public const int TileIronDoorBottom = 110;
        public const int TileWoodDoorItem   = 111;
        public const int TileIronDoorItem   = 112;
        // Tier 4 #17 — Flint and Steel + Apple icon tiles. Both are
        // procedural item sprites; layer indices follow the door
        // pack in append-only order.
        public const int TileFlintAndSteel  = 113;
        public const int TileApple          = 114;
        // Tier 4 #20 — Snowball icon (Alpha 332). Procedural — no
        // verified alpha_tools.png coord, sentinel entry below.
        public const int TileSnowball       = 115;
        // Tier 4 #15 — Bucket family icons (Alpha 325/326/327/335).
        // Procedural — no verified alpha_tools.png coords, sentinel
        // entries in AlphaTileCoords below. Each is a silver pail
        // silhouette differentiated by its rim/contents colour so a
        // glance at the hotbar tells the player which bucket they're
        // holding.
        public const int TileBucketEmpty    = 116;
        public const int TileBucketWater    = 117;
        public const int TileBucketLava     = 118;
        public const int TileBucketMilk     = 119;
        // Tier 4 #18 — Slimeball icon (Alpha 341). Procedural — no
        // verified alpha_tools.png coord, sentinel entry below. Drops
        // only from small Slime mobs; no recipe consumes it in Alpha
        // 1.1.2_01 (sticky pistons + magma cream + slime block all
        // post-date the era), so the sprite is purely visual.
        public const int TileSlimeball      = 120;
        // Tier 4 #22 — Compass icon (Alpha 345). Procedural — no
        // verified alpha_tools.png coord, sentinel entry below. The
        // base sprite is a static dial face; the rotating direction
        // marker (N/E/S/W text) is rendered live as an OVERLAY on
        // top of this tile by RenderHotbar, not baked into the atlas
        // tile. Recipe ships in Tier 8 #42 once redstone exists; for
        // now the item is creative-catalog only.
        public const int TileCompass        = 121;
        // Tier 4 #21 — Saddle icon (Alpha 329). Procedural — no
        // verified alpha_tools.png coord, sentinel entry below. Brown
        // leather pad with a darker hide outline and a metal-buckle pip
        // so a glance at the hotbar reads "saddle, not bread or
        // porkchop". Recipe doesn't exist in Alpha 1.1.2_01 (saddles
        // are dungeon loot only); item ships as a creative-catalog
        // entry until dungeons land in Tier 6 #32.
        public const int TileSaddle         = 122;
        // Tier 4 #23 — Fishing Rod icon (Alpha 346). Procedural — no
        // verified alpha_tools.png coord, sentinel entry below. Brown
        // rod shaft running corner-to-corner with a thin grey line
        // running off the tip and a small hook silhouette at the line
        // end so the icon reads as "fishing rod, not stick or
        // arrow". Cast/reel mechanic ships in Tier 4 #23; the line
        // entity itself (the Bobber) is rendered as a small white
        // cuboid in the world, not from an atlas tile.
        public const int TileFishingRod     = 123;
        // Tier 4 #24 — Painting art + icon tiles (Alpha 321). Five
        // art variants get one tile each (sizes baked into the
        // variant — placement / world-rect computation reads the
        // size from Painting.Width / Painting.Height); the icon
        // tile is the inventory thumbnail. Each variant gets a
        // visually-distinct procedural pattern (squiggle / gradient /
        // dots / cross / stripes) so the player can tell the five
        // variants apart at a glance even though the rectangles are
        // stretched-not-tiled at sizes > 1×1.
        public const int TilePainting1x1    = 124;
        public const int TilePainting1x2    = 125;
        public const int TilePainting2x1    = 126;
        public const int TilePainting2x2    = 127;
        public const int TilePainting4x3    = 128;
        public const int TilePaintingItem   = 129;
        // Tier 4 #25 — Jukebox + music disc tile indices. Jukebox top
        // shows a disc-slot inset circle on a plank base; side is a
        // darker plank panel with vertical seams; bottom reuses the
        // plank tile (we still allocate a dedicated layer rather than
        // routing to TilePlanks at GetTileIndex time, so a future
        // cosmetic tweak doesn't have to retrofit a multi-face
        // routing branch — costs 1 atlas tile, gains future
        // flexibility). Disc sprites: small black circular disc body
        // with a centred coloured label (white "13" for Disc13, cyan
        // line for DiscCat — Alpha distinguishes the two by colour
        // alone since the labels are too small to read at 16×16).
        public const int TileJukeboxTop     = 130;
        public const int TileJukeboxSide    = 131;
        public const int TileJukeboxBottom  = 132;
        public const int TileDisc13         = 133;
        public const int TileDiscCat        = 134;
        // Tier 4 #19 — Armor inventory icons. 20 layers (5 materials ×
        // 4 slots). All ship procedural — material-coloured silhouettes
        // shaped per slot (helmet=hooded square, chest=tall rectangle,
        // leggings=H-shape, boots=two small squares). Sentinel atlas
        // coords keep the slicer out of these layers in alpha-textures
        // mode. Append-only past TileDiscCat=134 keeps the existing
        // atlas slot indices stable.
        public const int TileLeatherHelmet       = 135;
        public const int TileLeatherChestplate   = 136;
        public const int TileLeatherLeggings     = 137;
        public const int TileLeatherBoots        = 138;
        public const int TileChainmailHelmet     = 139;
        public const int TileChainmailChestplate = 140;
        public const int TileChainmailLeggings   = 141;
        public const int TileChainmailBoots      = 142;
        public const int TileIronHelmet          = 143;
        public const int TileIronChestplate      = 144;
        public const int TileIronLeggings        = 145;
        public const int TileIronBoots           = 146;
        public const int TileDiamondHelmet       = 147;
        public const int TileDiamondChestplate   = 148;
        public const int TileDiamondLeggings     = 149;
        public const int TileDiamondBoots        = 150;
        public const int TileGoldHelmet          = 151;
        public const int TileGoldChestplate      = 152;
        public const int TileGoldLeggings        = 153;
        public const int TileGoldBoots           = 154;

        public const int TileGrassTop = 0;
        public const int TileGrassSide = 1;
        public const int TileDirt = 2;
        public const int TileStone = 3;
        public const int TileSand = 4;
        public const int TileCobblestone = 5;
        public const int TileBedrock = 6;
        public const int TileGravel = 7;
        public const int TileClay = 8;
        public const int TileCoalOre = 9;
        public const int TileIronOre = 10;
        public const int TileGoldOre = 11;
        public const int TileDiamondOre = 12;
        public const int TileRedstoneOre = 13;
        public const int TileLogTop = 14;
        public const int TileLogSide = 15;
        public const int TilePlanks = 16;
        public const int TileLeaves = 17;
        public const int TileWater = 18;
        public const int TileLava = 19;
        public const int TileGoldBlock = 20;
        public const int TileIronBlock = 21;
        public const int TileDiamondBlock = 22;
        public const int TileBricks = 23;
        public const int TileTntTop = 24;
        public const int TileTntBottom = 25;
        public const int TileTntSide = 26;
        public const int TileBookshelfSide = 27;
        public const int TileMossyCobblestone = 28;
        public const int TileObsidian = 29;
        public const int TileSponge = 30;
        public const int TileGlass = 31;
        public const int TileWool = 32;
        public const int TileTorch = 33;
        public const int TileDandelion = 34;
        public const int TileRose = 35;
        public const int TileBrownMushroom = 36;
        public const int TileRedMushroom = 37;

        // Tool layer indices. Order is material-major (wood, stone, iron,
        // diamond, gold within each kind), kind-grouped to mirror the
        // enum chunking in BlockType. Match GetTileIndex in Block.cs.
        public const int TileWoodSword      = 38;
        public const int TileStoneSword     = 39;
        public const int TileIronSword      = 40;
        public const int TileDiamondSword   = 41;
        public const int TileGoldSword      = 42;
        public const int TileWoodShovel     = 43;
        public const int TileStoneShovel    = 44;
        public const int TileIronShovel     = 45;
        public const int TileDiamondShovel  = 46;
        public const int TileGoldShovel     = 47;
        public const int TileWoodPickaxe    = 48;
        public const int TileStonePickaxe   = 49;
        public const int TileIronPickaxe    = 50;
        public const int TileDiamondPickaxe = 51;
        public const int TileGoldPickaxe    = 52;
        public const int TileWoodAxe        = 53;
        public const int TileStoneAxe       = 54;
        public const int TileIronAxe        = 55;
        public const int TileDiamondAxe     = 56;
        public const int TileGoldAxe        = 57;

        // Item layer indices (58..66). Always procedural — no PNG source
        // in either alpha terrain.png or alpha_tools.png. Order matches
        // BlockType enum: stick, coal, iron/gold ingot, diamond, flint,
        // clay ball/brick, bowl.
        public const int TileStick     = 58;
        public const int TileCoal      = 59;
        public const int TileIronIngot = 60;
        public const int TileGoldIngot = 61;
        public const int TileDiamond   = 62;
        public const int TileFlint     = 63;
        public const int TileClayBall  = 64;
        public const int TileClayBrick = 65;
        public const int TileBowl      = 66;

        // Tail block layers — additional block-face tiles appended past
        // the tool/item slice. These come from terrain.png (alpha mode)
        // or a procedural generator (procedural mode), NOT alpha_tools.png.
        public const int TileCraftingTableTop  = 67;
        public const int TileCraftingTableSide = 68;
        // Furnace face tiles. Top + bottom share one tile (a stone cap
        // with rim), the four sides share one tile (plain stone block
        // with iron edge banding), and the "front" face has its own
        // pair — unlit (a dark furnace mouth) and lit (the same mouth
        // with an orange/yellow glow). The render path swaps the side
        // texture between FurnaceFront and FurnaceFrontLit by switching
        // the underlying block id (Furnace ↔ LitFurnace), so we only
        // need one block-id per state, not metadata. Without an
        // orientation byte we draw the front on every side so the door
        // is always visible — Alpha shows it on a single face, but
        // metadata-orientation is a Tier 2 task and the all-faces look
        // is unmistakable.
        public const int TileFurnaceTop      = 69;
        public const int TileFurnaceSide     = 70;
        public const int TileFurnaceFront    = 71;
        public const int TileFurnaceFrontLit = 72;
        // Chest face tiles. Top is the planked lid seen from above with
        // the iron lock-plate visible. Side is the planked carcass with
        // a single horizontal iron band. Front carries the same band
        // plus the centred lock + door split. The bottom face reuses
        // TilePlanks (the underside of an Alpha chest is just planks),
        // so we only need three layers and route them through the
        // oriented mesher path so the front lands on the placer-facing
        // side and the other three sides draw TileChestSide.
        public const int TileChestTop   = 73;
        public const int TileChestSide  = 74;
        public const int TileChestFront = 75;

        // A 2D texture array — one layer per tile. Greedy meshing can emit merged
        // quads with UVs exceeding [0,1]; with a layered texture and Repeat wrap the
        // fragment shader just samples texelFetch-equivalent `texture(array, vec3(fract(uv), layer))`.
        public static int CreateAtlas()
        {
            int tex = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2DArray, tex);

            // Allocate storage for all layers up front.
            GL.TexImage3D(
                TextureTarget.Texture2DArray, 0, PixelInternalFormat.Rgba,
                TileSize, TileSize, LayerCount, 0,
                PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);

            var layerPixels = new byte[TileSize * TileSize * 4];
            UploadLayer(layerPixels, TileGrassTop, GenerateGrassTop);
            UploadLayer(layerPixels, TileGrassSide, GenerateGrassSide);
            UploadLayer(layerPixels, TileDirt, GenerateDirt);
            UploadLayer(layerPixels, TileStone, GenerateStone);
            UploadLayer(layerPixels, TileSand, GenerateSand);
            UploadLayer(layerPixels, TileCobblestone, GenerateCobblestone);
            UploadLayer(layerPixels, TileBedrock, GenerateBedrock);
            UploadLayer(layerPixels, TileGravel, GenerateGravel);
            UploadLayer(layerPixels, TileClay, GenerateClay);
            UploadLayer(layerPixels, TileCoalOre, GenerateCoalOre);
            UploadLayer(layerPixels, TileIronOre, GenerateIronOre);
            UploadLayer(layerPixels, TileGoldOre, GenerateGoldOre);
            UploadLayer(layerPixels, TileDiamondOre, GenerateDiamondOre);
            UploadLayer(layerPixels, TileRedstoneOre, GenerateRedstoneOre);
            UploadLayer(layerPixels, TileLogTop, GenerateLogTop);
            UploadLayer(layerPixels, TileLogSide, GenerateLogSide);
            UploadLayer(layerPixels, TilePlanks, GeneratePlanks);
            UploadLayer(layerPixels, TileLeaves, GenerateLeaves);
            UploadLayer(layerPixels, TileWater, GenerateWater);
            UploadLayer(layerPixels, TileLava, GenerateLava);
            UploadLayer(layerPixels, TileGoldBlock, GenerateGoldBlock);
            UploadLayer(layerPixels, TileIronBlock, GenerateIronBlock);
            UploadLayer(layerPixels, TileDiamondBlock, GenerateDiamondBlock);
            UploadLayer(layerPixels, TileBricks, GenerateBricks);
            UploadLayer(layerPixels, TileTntTop, GenerateTntTop);
            UploadLayer(layerPixels, TileTntBottom, GenerateTntBottom);
            UploadLayer(layerPixels, TileTntSide, GenerateTntSide);
            UploadLayer(layerPixels, TileBookshelfSide, GenerateBookshelfSide);
            UploadLayer(layerPixels, TileMossyCobblestone, GenerateMossyCobblestone);
            UploadLayer(layerPixels, TileObsidian, GenerateObsidian);
            UploadLayer(layerPixels, TileSponge, GenerateSponge);
            UploadLayer(layerPixels, TileGlass, GenerateGlass);
            UploadLayer(layerPixels, TileWool, GenerateWool);
            UploadLayer(layerPixels, TileTorch, GenerateTorch);
            UploadLayer(layerPixels, TileDandelion, GenerateDandelion);
            UploadLayer(layerPixels, TileRose, GenerateRose);
            UploadLayer(layerPixels, TileBrownMushroom, GenerateBrownMushroom);
            UploadLayer(layerPixels, TileRedMushroom, GenerateRedMushroom);

            // Tool layers — generate procedural pixel-art sprites that
            // match the procedural block aesthetic instead of pulling
            // from alpha_tools.png. The procedural atlas is the "no
            // assets, just code" mode, so the tools should look like
            // they were drawn by the same hand as the blocks. The PNG
            // path is still available via CreateAtlasFromAlphaTerrain
            // for players who prefer the canon Alpha art.
            GenerateProceduralToolLayers(layerPixels);

            // Item layers — always procedural (neither alpha PNG has
            // entries for the ingredient set). Same path used by the
            // alpha-textures atlas in CreateAtlasFromAlphaTerrain.
            GenerateProceduralItemLayers(layerPixels);

            // Tail block tiles — procedural CraftingTable face textures.
            // These come from terrain.png in alpha mode and from these
            // generators in procedural mode.
            UploadLayer(layerPixels, TileCraftingTableTop, GenerateCraftingTableTop);
            UploadLayer(layerPixels, TileCraftingTableSide, GenerateCraftingTableSide);
            UploadLayer(layerPixels, TileFurnaceTop, GenerateFurnaceTop);
            UploadLayer(layerPixels, TileFurnaceSide, GenerateFurnaceSide);
            UploadLayer(layerPixels, TileFurnaceFront, GenerateFurnaceFront);
            UploadLayer(layerPixels, TileFurnaceFrontLit, GenerateFurnaceFrontLit);
            UploadLayer(layerPixels, TileChestTop, GenerateChestTop);
            UploadLayer(layerPixels, TileChestSide, GenerateChestSide);
            UploadLayer(layerPixels, TileChestFront, GenerateChestFront);

            // Tier 4 #14 — Farming tail-block layers (FarmlandTop +
            // 8 wheat growth stages). Always procedural in the no-PNG
            // atlas; the alpha-textures atlas calls the same path
            // and then overlays the verified terrain.png coords on top.
            GenerateProceduralFarmingLayers(layerPixels);

            // Tier 4 #26 — Sugar cane in-world block tile. The 3 cane
            // ITEM tiles were already painted by the GenerateProcedural
            // ItemLayers call above (which is bounded by LayerCount
            // and so picks them up automatically once they're past the
            // farm slice).
            GenerateProceduralCaneLayers(layerPixels);

            // Tier 4 #16 — Door tile pack (4 block halves + 2 item
            // icons). Always procedural for now (sentinel coords in
            // AlphaTileCoords); the alpha-textures atlas calls the
            // same path so the icons stay readable in either mode.
            GenerateProceduralDoorLayers(layerPixels);

            // Tier 4 #17 — Flint and Steel + Apple icon pack. Same
            // procedural-always story as the door pack.
            GenerateProceduralFireLayers(layerPixels);

            // Tier 4 #20 — Snowball icon. Egg already painted by
            // GenerateProceduralItemLayers via TileEgg.
            GenerateProceduralThrowLayers(layerPixels);

            // Tier 4 #15 — Bucket icons (empty/water/lava/milk).
            // Procedural-only, same story as the throw pack.
            GenerateProceduralBucketLayers(layerPixels);

            // Tier 4 #18 — Slimeball icon. Procedural-only, same
            // sentinel-coord story as the bucket pack.
            GenerateProceduralSlimeLayers(layerPixels);

            // Tier 4 #22 — Compass dial face. Procedural-only, same
            // sentinel-coord story as the slime pack.
            GenerateProceduralCompassLayers(layerPixels);

            // Tier 4 #21 — Saddle sprite. Procedural-only, same
            // sentinel-coord story as the compass pack.
            GenerateProceduralSaddleLayers(layerPixels);

            // Tier 4 #23 — Fishing Rod sprite. Procedural-only, same
            // sentinel-coord story as the saddle pack.
            GenerateProceduralFishingRodLayers(layerPixels);

            // Tier 4 #24 — Painting art + icon. Five distinct
            // procedural patterns + one framed-picture inventory
            // icon. Procedural-only — Alpha's painting sheet
            // (kz.png) isn't bundled here.
            GenerateProceduralPaintingLayers(layerPixels);

            // Tier 4 #25 — Jukebox face tiles + music disc icons.
            // Procedural-always — same sentinel-coord story as the
            // painting pack. Five tiles total: 3 jukebox faces + 2
            // disc sprites.
            GenerateProceduralJukeboxLayers(layerPixels);

            // Tier 4 #19 — Armor inventory icons. 20 procedural sprites
            // covering all 5 materials × 4 slots. Procedural-always —
            // Alpha's per-piece icons live in a separate sheet
            // (gui/items.png) we don't currently embed, and the per-
            // material colour story is well-defined enough that the
            // generated silhouettes read at-a-glance even without the
            // canonical art.
            GenerateProceduralArmorLayers(layerPixels);

            GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
            GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
            GL.BindTexture(TextureTarget.Texture2DArray, 0);
            return tex;
        }

        // Slice each tool tile out of alpha_tools.png and upload it to its
        // destination layer. Caller has already bound the Texture2DArray.
        // No-op if the tools PNG can't be decoded (older builds without
        // AlphaToolsData, or a corrupt resource); the affected layers
        // simply stay at whatever was there before (transparent for a
        // freshly-allocated atlas).
        private static void UploadToolLayersFromAlphaTools(byte[] layerPixels)
        {
            if (!TryDecodeEmbeddedTools(out byte[] toolBgra, out int toolW, out int toolH))
                return;
            // Stops at FirstTailBlockLayer so the tail-block range (which
            // sources from terrain.png) doesn't get sliced out of the
            // tools PNG.
            for (int layer = BlockLayerCount; layer < FirstTailBlockLayer; layer++)
            {
                var (col, row) = AlphaTileCoords[layer];
                CopyTile(toolBgra, toolW, toolH, col, row, layerPixels);
                GL.TexSubImage3D(
                    TextureTarget.Texture2DArray, 0,
                    0, 0, layer,
                    TileSize, TileSize, 1,
                    PixelFormat.Rgba, PixelType.UnsignedByte, layerPixels);
            }
        }

        // Slice each tail-item tile (porkchops and any future appended
        // food/ingredient items) out of alpha_tools.png and upload it.
        // Same idempotent contract as UploadToolLayersFromAlphaTools — a
        // missing tools PNG just leaves the previously-painted procedural
        // pixels in place, so the atlas degrades gracefully instead of
        // turning porkchop icons into magenta error tiles. Bounds the
        // loop with FirstTailTool2Layer so the hoe range (which has its
        // own slice helper) doesn't get interpreted as items.
        private static void UploadTailItemLayersFromAlphaTools(byte[] layerPixels)
        {
            if (!TryDecodeEmbeddedTools(out byte[] toolBgra, out int toolW, out int toolH))
                return;
            for (int layer = FirstTailItemLayer; layer < FirstTailTool2Layer; layer++)
            {
                var (col, row) = AlphaTileCoords[layer];
                if (col < 0 || row < 0) continue; // sentinel — keep the procedural fill
                CopyTile(toolBgra, toolW, toolH, col, row, layerPixels);
                GL.TexSubImage3D(
                    TextureTarget.Texture2DArray, 0,
                    0, 0, layer,
                    TileSize, TileSize, 1,
                    PixelFormat.Rgba, PixelType.UnsignedByte, layerPixels);
            }
        }

        // Tier 4 #14 — Slice the 5 hoe tiles (tail-tool 2 range) out of
        // alpha_tools.png. Same idempotent contract as the original
        // tool slicer; a missing tools PNG leaves the procedural hoe
        // pixels in place. Sentinel-aware so we can ship a hoe without
        // a verified Alpha coord and still have it render correctly.
        private static void UploadTailTool2LayersFromAlphaTools(byte[] layerPixels)
        {
            if (!TryDecodeEmbeddedTools(out byte[] toolBgra, out int toolW, out int toolH))
                return;
            for (int layer = FirstTailTool2Layer; layer < FirstTailFarmLayer; layer++)
            {
                var (col, row) = AlphaTileCoords[layer];
                if (col < 0 || row < 0) continue;
                CopyTile(toolBgra, toolW, toolH, col, row, layerPixels);
                GL.TexSubImage3D(
                    TextureTarget.Texture2DArray, 0,
                    0, 0, layer,
                    TileSize, TileSize, 1,
                    PixelFormat.Rgba, PixelType.UnsignedByte, layerPixels);
            }
        }

        // Tier 4 #14 — Slice FarmlandTop + 8 wheat stages out of the
        // already-decoded terrain.png buffer. Farming items in the same
        // tail range have sentinel coords and stay procedural; only
        // the block-face tiles are overlaid here. Caller passes the
        // decoded terrain bgra so we don't re-decode. Bounded at
        // FirstTailCaneLayer (Tier 4 #26) so the cane slice doesn't get
        // pulled out of terrain.png by accident — it has its own
        // dedicated slicer.
        private static void UploadFarmingBlockLayersFromTerrain(byte[] bgra, int srcW, int srcH, byte[] layerPixels)
        {
            for (int layer = FirstTailFarmLayer; layer < FirstTailCaneLayer; layer++)
            {
                var (col, row) = AlphaTileCoords[layer];
                if (col < 0 || row < 0) continue; // farming items — keep procedural fill
                CopyTile(bgra, srcW, srcH, col, row, layerPixels);
                GL.TexSubImage3D(
                    TextureTarget.Texture2DArray, 0,
                    0, 0, layer,
                    TileSize, TileSize, 1,
                    PixelFormat.Rgba, PixelType.UnsignedByte, layerPixels);
            }
        }

        // True for tail layers (>= FirstTailCaneLayer) whose AlphaTileCoords
        // entry indexes into terrain.png rather than alpha_tools.png. Used
        // by UploadCaneBlockLayersFromTerrain to ignore item-icon layers
        // in the same range — without this gate the terrain slicer would
        // happily copy a grass-top tile into an armor slot if we put a
        // real (col,row) there. The list is whitelist-explicit rather
        // than range-based because the terrain-source vs tools-source
        // distinction is interleaved through the tail layers (door BLOCKS
        // are terrain, door ICONS are tools; jukebox FACES are terrain,
        // music DISCS are tools), so a single boundary doesn't fit.
        private static bool IsTailLayerTerrainSourced(int layer)
        {
            return layer == TileSugarCane
                || layer == TileWoodDoorTop  || layer == TileWoodDoorBottom
                || layer == TileIronDoorTop  || layer == TileIronDoorBottom
                || layer == TileJukeboxTop   || layer == TileJukeboxSide
                || layer == TileJukeboxBottom;
        }

        // Tier 4 #26 — Slice the SugarCane block tile out of terrain.png.
        // The three item tiles (SugarCaneItem / Paper / Book) live in
        // alpha_tools.png and use a separate slicer below; sentinel
        // entries in AlphaTileCoords skip them here. Caller passes the
        // decoded terrain bgra so we don't re-decode. Same idempotent
        // contract as UploadFarmingBlockLayersFromTerrain — a sentinel
        // coord just leaves the procedural pixels in place. Bounded to
        // terrain-source layers via IsTailLayerTerrainSourced so item
        // tiles in the same range (door icons, armor, music discs, etc.)
        // can carry alpha_tools.png coords without being misinterpreted
        // here; those go through UploadTailItemsFromAlphaTools below.
        private static void UploadCaneBlockLayersFromTerrain(byte[] bgra, int srcW, int srcH, byte[] layerPixels)
        {
            for (int layer = FirstTailCaneLayer; layer < LayerCount; layer++)
            {
                if (!IsTailLayerTerrainSourced(layer)) continue;
                var (col, row) = AlphaTileCoords[layer];
                if (col < 0 || row < 0) continue;
                CopyTile(bgra, srcW, srcH, col, row, layerPixels);
                GL.TexSubImage3D(
                    TextureTarget.Texture2DArray, 0,
                    0, 0, layer,
                    TileSize, TileSize, 1,
                    PixelFormat.Rgba, PixelType.UnsignedByte, layerPixels);
            }
        }

        // Tier 4 — Slice the post-cane tail-ITEM tiles out of alpha_tools.png.
        // Covers everything from FirstTailDoorLayer onward that ISN'T flagged
        // as terrain-sourced: door inventory icons, flint+steel, apple,
        // snowball, buckets, slimeball, compass, saddle, fishing rod,
        // paintings, music discs, and the 20 armor pieces. Same idempotent
        // contract as the other tools slicers — a missing alpha_tools.png
        // or a sentinel coord just leaves the procedural pixels in place,
        // so the atlas degrades gracefully (every layer was painted by
        // its procedural generator before this overlay runs).
        private static void UploadTailItemsFromAlphaTools(byte[] layerPixels)
        {
            if (!TryDecodeEmbeddedTools(out byte[] toolBgra, out int toolW, out int toolH))
                return;
            for (int layer = FirstTailDoorLayer; layer < LayerCount; layer++)
            {
                if (IsTailLayerTerrainSourced(layer)) continue;
                var (col, row) = AlphaTileCoords[layer];
                if (col < 0 || row < 0) continue;
                CopyTile(toolBgra, toolW, toolH, col, row, layerPixels);
                GL.TexSubImage3D(
                    TextureTarget.Texture2DArray, 0,
                    0, 0, layer,
                    TileSize, TileSize, 1,
                    PixelFormat.Rgba, PixelType.UnsignedByte, layerPixels);
            }
        }

        // Procedural tool atlas. Walks tool layers 38..57 and synthesises
        // a 16×16 sprite for each, deriving (kind, material) from the
        // AlphaTileCoords entry — the (col, row) we use for slicing the
        // PNG happens to also encode "row 4..7 = sword/shovel/pickaxe/axe"
        // and "col 0..4 = wood/stone/iron/diamond/gold", so the same
        // table drives both decoders. Each sprite is a wood handle
        // running diagonally bottom-left → upper-right with a metal head
        // at the upper-right end; the head shape branches per kind.
        // Material colours pick the head palette so iron looks pale,
        // gold looks yellow, etc.
        private static void GenerateProceduralToolLayers(byte[] layerPixels)
        {
            for (int layer = BlockLayerCount; layer < LayerCount; layer++)
            {
                Array.Clear(layerPixels, 0, layerPixels.Length);
                var (col, row) = AlphaTileCoords[layer];
                ToolKind kind;
                switch (row)
                {
                    case 4: kind = ToolKind.Sword;   break;
                    case 5: kind = ToolKind.Shovel;  break;
                    case 6: kind = ToolKind.Pickaxe; break;
                    case 7: kind = ToolKind.Axe;     break;
                    case 8: kind = ToolKind.Hoe;     break; // Tier 4 #14 hoes row.
                    default: kind = ToolKind.Sword;  break; // unreachable for tool rows
                }
                ToolMaterial mat;
                switch (col)
                {
                    case 0: mat = ToolMaterial.Wood;    break;
                    case 1: mat = ToolMaterial.Stone;   break;
                    case 2: mat = ToolMaterial.Iron;    break;
                    case 3: mat = ToolMaterial.Diamond; break;
                    case 4: mat = ToolMaterial.Gold;    break;
                    default: mat = ToolMaterial.Wood;   break;
                }
                GenerateTool(layerPixels, kind, mat, layer);
                GL.TexSubImage3D(
                    TextureTarget.Texture2DArray, 0,
                    0, 0, layer,
                    TileSize, TileSize, 1,
                    PixelFormat.Rgba, PixelType.UnsignedByte, layerPixels);
            }
        }

        // Material palette for tool heads. Three shades — base, highlight,
        // shadow — used by GenerateTool to give the head a chunky,
        // shaded look at 16×16 instead of a flat colour blob. Wood is
        // also used for the handle of every tool regardless of head
        // material so the silhouette reads "tool" before "X material".
        private static (byte r, byte g, byte b) MaterialBase(ToolMaterial m)
        {
            switch (m)
            {
                case ToolMaterial.Wood:    return (158, 113, 60);
                case ToolMaterial.Stone:   return (130, 130, 130);
                case ToolMaterial.Iron:    return (210, 210, 220);
                case ToolMaterial.Gold:    return (240, 210, 70);
                case ToolMaterial.Diamond: return (110, 225, 220);
            }
            return (200, 200, 200);
        }
        private static (byte r, byte g, byte b) MaterialHi(ToolMaterial m)
        {
            switch (m)
            {
                case ToolMaterial.Wood:    return (190, 145, 85);
                case ToolMaterial.Stone:   return (170, 170, 170);
                case ToolMaterial.Iron:    return (240, 240, 248);
                case ToolMaterial.Gold:    return (255, 235, 110);
                case ToolMaterial.Diamond: return (170, 255, 250);
            }
            return (235, 235, 235);
        }
        private static (byte r, byte g, byte b) MaterialLo(ToolMaterial m)
        {
            switch (m)
            {
                case ToolMaterial.Wood:    return (110, 75, 35);
                case ToolMaterial.Stone:   return (88, 88, 88);
                case ToolMaterial.Iron:    return (155, 155, 168);
                case ToolMaterial.Gold:    return (180, 150, 30);
                case ToolMaterial.Diamond: return (60, 165, 175);
            }
            return (140, 140, 140);
        }

        // Draw one tool sprite into the 16×16 layer buffer. The sprite is
        // composed of two parts:
        //   1. A wooden handle running diagonally from (4,11) up to
        //      (10,5), 1 px wide with a 1-px shadow track. Same on every
        //      tool so the silhouette reads tool-like.
        //   2. A material-coloured head at the upper-right end, shape
        //      branching on `kind`. Heads are drawn with three shades
        //      (lo edge / base body / hi inner) for the chunky pixel-art
        //      look the procedural blocks already use.
        // `seed` is the layer index — it gives each material+kind
        // combination its own deterministic jitter (subtle dithering on
        // the head body) so the 20 sprites don't look like 20 colour
        // swaps of the same drawing.
        private static void GenerateTool(byte[] pixels, ToolKind kind, ToolMaterial material, int seed)
        {
            // ---- handle (wood) -----------------------------------------
            // Diagonal stair-step from lower-left to upper-right, with a
            // shadow line below it for depth. Handle ends in a small
            // pommel at the lower-left corner.
            (byte r, byte g, byte b) wood   = (138, 95, 50);
            (byte r, byte g, byte b) woodHi = (172, 125, 70);
            (byte r, byte g, byte b) woodLo = (95, 60, 30);

            // (x, y) pairs for the handle core — runs from (4,11) to
            // (10,5). Each step is one pixel right and one pixel up.
            int[] hxs = { 4, 5, 6, 7, 8, 9, 10 };
            int[] hys = { 11, 10, 9, 8, 7, 6, 5 };
            for (int i = 0; i < hxs.Length; i++)
            {
                SetPixel(pixels, hxs[i], hys[i], wood.r, wood.g, wood.b);
                // Highlight pixel above-left of handle for dim 3D.
                if (hxs[i] - 1 >= 0)
                    SetPixel(pixels, hxs[i] - 1, hys[i], woodHi.r, woodHi.g, woodHi.b);
                // Shadow pixel below-right.
                if (hys[i] + 1 < TileSize)
                    SetPixel(pixels, hxs[i], hys[i] + 1, woodLo.r, woodLo.g, woodLo.b);
            }
            // Pommel cap at the handle end (3,12).
            SetPixel(pixels, 3, 12, woodLo.r, woodLo.g, woodLo.b);

            // ---- head --------------------------------------------------
            var baseC = MaterialBase(material);
            var hiC   = MaterialHi(material);
            var loC   = MaterialLo(material);
            var rng   = new Random(0x70 + seed);

            switch (kind)
            {
                case ToolKind.Sword:   DrawSwordHead(pixels, baseC, hiC, loC, rng);   break;
                case ToolKind.Shovel:  DrawShovelHead(pixels, baseC, hiC, loC, rng);  break;
                case ToolKind.Pickaxe: DrawPickaxeHead(pixels, baseC, hiC, loC, rng); break;
                case ToolKind.Axe:     DrawAxeHead(pixels, baseC, hiC, loC, rng);     break;
                case ToolKind.Hoe:     DrawHoeHead(pixels, baseC, hiC, loC, rng);     break;
            }
        }

        // Hoe head: short flat blade extending RIGHT-WARDS off the top of
        // the handle, perpendicular to the diagonal grip — the classic
        // L-shape that distinguishes a hoe from an axe (which has a
        // taller blade in the same direction). 3 pixels wide × 2 pixels
        // tall, anchored above the upper-right end of the handle at
        // x=11..13, y=3..4. Inner pixels are bright/base-shaded for
        // chunk; outer rim is dark for the silhouette read.
        private static void DrawHoeHead(byte[] pixels,
            (byte r, byte g, byte b) baseC,
            (byte r, byte g, byte b) hiC,
            (byte r, byte g, byte b) loC,
            Random rng)
        {
            // Top edge — three dark pixels forming the upper rim.
            SetPixel(pixels, 11, 3, loC.r, loC.g, loC.b);
            SetPixel(pixels, 12, 3, loC.r, loC.g, loC.b);
            SetPixel(pixels, 13, 3, loC.r, loC.g, loC.b);
            // Body — bright interior.
            SetPixel(pixels, 11, 4, hiC.r, hiC.g, hiC.b);
            SetPixel(pixels, 12, 4, baseC.r, baseC.g, baseC.b);
            SetPixel(pixels, 13, 4, baseC.r, baseC.g, baseC.b);
            // Bottom edge — single dark pixel rounding the right end.
            SetPixel(pixels, 13, 5, loC.r, loC.g, loC.b);
            SetPixel(pixels, 14, 4, loC.r, loC.g, loC.b);
        }

        // Sword head: long pointed blade running along the same diagonal
        // as the handle, with a small crossguard where the two meet.
        // Tip at (13,2), crossguard at (10,5)/(11,4)/(9,6) etc.
        private static void DrawSwordHead(byte[] pixels,
            (byte r, byte g, byte b) baseC,
            (byte r, byte g, byte b) hiC,
            (byte r, byte g, byte b) loC,
            Random rng)
        {
            // Crossguard — two pixels straddling the joint.
            SetPixel(pixels, 9, 4, loC.r, loC.g, loC.b);
            SetPixel(pixels, 11, 6, loC.r, loC.g, loC.b);

            // Blade — diagonal from (10,5) up to tip (13,2). Two pixels
            // wide for chunkiness; outer edge dim, inner edge bright.
            int[] bxs = { 10, 11, 12, 13 };
            int[] bys = { 5, 4, 3, 2 };
            for (int i = 0; i < bxs.Length; i++)
            {
                SetPixel(pixels, bxs[i], bys[i], hiC.r, hiC.g, hiC.b);
                if (bxs[i] + 1 < TileSize) SetPixel(pixels, bxs[i] + 1, bys[i], baseC.r, baseC.g, baseC.b);
                if (bys[i] - 1 >= 0)       SetPixel(pixels, bxs[i], bys[i] - 1, loC.r, loC.g, loC.b);
            }
            // Tip pixel — single point past (13,2).
            SetPixel(pixels, 14, 1, loC.r, loC.g, loC.b);
        }

        // Shovel head: small rectangular spade scoop above the handle joint.
        // ~3×3 with a tapered point, sits at (10..12, 2..4).
        private static void DrawShovelHead(byte[] pixels,
            (byte r, byte g, byte b) baseC,
            (byte r, byte g, byte b) hiC,
            (byte r, byte g, byte b) loC,
            Random rng)
        {
            // Top edge / shoulders.
            SetPixel(pixels, 10, 2, loC.r, loC.g, loC.b);
            SetPixel(pixels, 11, 2, baseC.r, baseC.g, baseC.b);
            SetPixel(pixels, 12, 2, loC.r, loC.g, loC.b);
            // Body.
            SetPixel(pixels, 10, 3, baseC.r, baseC.g, baseC.b);
            SetPixel(pixels, 11, 3, hiC.r, hiC.g, hiC.b);
            SetPixel(pixels, 12, 3, baseC.r, baseC.g, baseC.b);
            SetPixel(pixels, 10, 4, baseC.r, baseC.g, baseC.b);
            SetPixel(pixels, 11, 4, baseC.r, baseC.g, baseC.b);
            SetPixel(pixels, 12, 4, baseC.r, baseC.g, baseC.b);
            // Tapered tip toward the handle.
            SetPixel(pixels, 11, 5, loC.r, loC.g, loC.b);
        }

        // Pickaxe head: horizontal bar with two prongs flanking the handle
        // joint. Sits at y=3..4 from x=8..14 with the joint at (10,5).
        private static void DrawPickaxeHead(byte[] pixels,
            (byte r, byte g, byte b) baseC,
            (byte r, byte g, byte b) hiC,
            (byte r, byte g, byte b) loC,
            Random rng)
        {
            // Top edge (highlight band).
            for (int x = 8; x <= 14; x++)
                SetPixel(pixels, x, 3, hiC.r, hiC.g, hiC.b);
            // Bar body.
            for (int x = 8; x <= 14; x++)
                SetPixel(pixels, x, 4, baseC.r, baseC.g, baseC.b);
            // Prong tips — curve down at both ends to suggest pick points.
            SetPixel(pixels, 8, 5, loC.r, loC.g, loC.b);
            SetPixel(pixels, 14, 5, loC.r, loC.g, loC.b);
            // End caps darker.
            SetPixel(pixels, 8, 3, loC.r, loC.g, loC.b);
            SetPixel(pixels, 14, 3, loC.r, loC.g, loC.b);
            // Top spike on the far end (visual flourish for pickaxe shape).
            SetPixel(pixels, 13, 2, loC.r, loC.g, loC.b);
            SetPixel(pixels, 9, 2, loC.r, loC.g, loC.b);
        }

        // Axe head: asymmetric triangular blade widening on the far side
        // of the handle. Sits at x=10..14, y=2..5 with the bulk at the
        // upper-right corner.
        private static void DrawAxeHead(byte[] pixels,
            (byte r, byte g, byte b) baseC,
            (byte r, byte g, byte b) hiC,
            (byte r, byte g, byte b) loC,
            Random rng)
        {
            // Top wedge — narrow at the handle, wider at the far edge.
            SetPixel(pixels, 12, 2, loC.r, loC.g, loC.b);
            SetPixel(pixels, 13, 2, loC.r, loC.g, loC.b);
            SetPixel(pixels, 14, 2, loC.r, loC.g, loC.b);
            // Mid band — full width with highlight stripe.
            SetPixel(pixels, 11, 3, baseC.r, baseC.g, baseC.b);
            SetPixel(pixels, 12, 3, hiC.r, hiC.g, hiC.b);
            SetPixel(pixels, 13, 3, hiC.r, hiC.g, hiC.b);
            SetPixel(pixels, 14, 3, baseC.r, baseC.g, baseC.b);
            // Bottom band — body.
            SetPixel(pixels, 10, 4, baseC.r, baseC.g, baseC.b);
            SetPixel(pixels, 11, 4, baseC.r, baseC.g, baseC.b);
            SetPixel(pixels, 12, 4, baseC.r, baseC.g, baseC.b);
            SetPixel(pixels, 13, 4, baseC.r, baseC.g, baseC.b);
            SetPixel(pixels, 14, 4, loC.r, loC.g, loC.b);
            // Bottom edge tapers back to the handle.
            SetPixel(pixels, 11, 5, loC.r, loC.g, loC.b);
            SetPixel(pixels, 12, 5, loC.r, loC.g, loC.b);
        }

        // Walk item layers 58..66 and paint a 16×16 sprite per item.
        // Same shape as GenerateProceduralToolLayers: clear the layer
        // buffer, dispatch to the per-item generator, upload via
        // TexSubImage3D into the bound Texture2DArray. Items don't share
        // a base layout the way tools do (every tool reuses the diagonal
        // wood handle), so each generator is self-contained — sticks
        // are a vertical brown bar, coal is a black blob, ingots are
        // small loaf rectangles, etc. All 9 sprites are designed to
        // read at hotbar / inventory slot scales.
        private static void GenerateProceduralItemLayers(byte[] layerPixels)
        {
            UploadItem(layerPixels, TileStick,     GenerateStickItem);
            UploadItem(layerPixels, TileCoal,      GenerateCoalItem);
            UploadItem(layerPixels, TileIronIngot, GenerateIronIngotItem);
            UploadItem(layerPixels, TileGoldIngot, GenerateGoldIngotItem);
            UploadItem(layerPixels, TileDiamond,   GenerateDiamondItem);
            UploadItem(layerPixels, TileFlint,     GenerateFlintItem);
            UploadItem(layerPixels, TileClayBall,  GenerateClayBallItem);
            UploadItem(layerPixels, TileClayBrick, GenerateClayBrickItem);
            UploadItem(layerPixels, TileBowl,      GenerateBowlItem);
            // Tail-item porkchops (Tier 3 #9 — pig drop + furnace
            // smelt result). Same UploadItem path; layer indices live
            // past the tail-block range.
            UploadItem(layerPixels, TileRawPorkchop,    GenerateRawPorkchopItem);
            UploadItem(layerPixels, TileCookedPorkchop, GenerateCookedPorkchopItem);
            // Tail-item hostile-mob drops (Tier 3 #10). All four are
            // visual-only collectibles for now — Bow/Arrow gain combat
            // in Tier 4 #17, Gunpowder fuels TNT priming in Tier 8 #43,
            // String unlocks recipes alongside the bow.
            UploadItem(layerPixels, TileBow,       GenerateBowItem);
            UploadItem(layerPixels, TileArrow,     GenerateArrowItem);
            UploadItem(layerPixels, TileString,    GenerateStringItem);
            UploadItem(layerPixels, TileGunpowder, GenerateGunpowderItem);
            // Tail-item passive-mob drops (Tier 3 #12). Cow drops Leather,
            // chicken drops Feather + lays Egg. All three are visual
            // collectibles for the current era; Egg becomes a throwable
            // projectile in Tier 4 #20, and Leather feeds into the
            // armor recipes in Tier 4 #19.
            UploadItem(layerPixels, TileLeather, GenerateLeatherItem);
            UploadItem(layerPixels, TileFeather, GenerateFeatherItem);
            UploadItem(layerPixels, TileEgg,     GenerateEggItem);
            // Tier 4 #14 — farming items (seeds, wheat, bread, stew).
            // Same UploadItem path; tile indices live past the tail-tool
            // hoe slice. Procedural icons are always painted; the alpha
            // PNG path overlays them only if the verified Notch coords
            // are wired in AlphaTileCoords (currently sentinels — see
            // the (-1,-1) entries in that table).
            UploadItem(layerPixels, TileWheatSeeds,   GenerateWheatSeedsItem);
            UploadItem(layerPixels, TileWheatItem,    GenerateWheatItem);
            UploadItem(layerPixels, TileBread,        GenerateBreadItem);
            UploadItem(layerPixels, TileMushroomStew, GenerateMushroomStewItem);
            // Tier 4 #26 — Sugar cane drop, Paper, Book. The cane drop
            // shares the diagonal-stalk silhouette of the wheat-bundle
            // icon; paper is an off-white sheet; book is a brown
            // leather cover with paper edges. All three procedural —
            // alpha_tools.png coords haven't been wired (sentinel-only
            // in AlphaTileCoords) so the procedural pixels are the
            // canonical visuals for this V1.
            UploadItem(layerPixels, TileSugarCaneItem, GenerateSugarCaneItemIcon);
            UploadItem(layerPixels, TilePaper,         GeneratePaperItem);
            UploadItem(layerPixels, TileBook,          GenerateBookItem);
        }

        // Tier 4 #14 — Walk the farming-block layer range (FarmlandTop +
        // 8 wheat stages) and synthesise a 16×16 sprite per layer.
        // FarmlandTop is a tilled-dirt top face with horizontal furrows;
        // each wheat stage is a cross-sprite-friendly sketch of grain
        // shoots that grow taller and shift from green to gold as the
        // stage advances. The mesher's EmitCrossSprite path samples the
        // per-stage layer via Block.GetWheatTileForStage so each Wheat
        // block in a chunk shows its own growth state.
        private static void GenerateProceduralFarmingLayers(byte[] layerPixels)
        {
            UploadLayer(layerPixels, TileFarmlandTop, GenerateFarmlandTop);
            UploadLayer(layerPixels, TileWheat0, GenerateWheatStage0);
            UploadLayer(layerPixels, TileWheat1, GenerateWheatStage1);
            UploadLayer(layerPixels, TileWheat2, GenerateWheatStage2);
            UploadLayer(layerPixels, TileWheat3, GenerateWheatStage3);
            UploadLayer(layerPixels, TileWheat4, GenerateWheatStage4);
            UploadLayer(layerPixels, TileWheat5, GenerateWheatStage5);
            UploadLayer(layerPixels, TileWheat6, GenerateWheatStage6);
            UploadLayer(layerPixels, TileWheat7, GenerateWheatStage7);
        }

        // Tier 4 #26 — Procedural sugar cane block tile + the three
        // cane-related item icons. The block face uses UploadLayer
        // (no transparent clear — fully opaque cross-sprite). The three
        // items use UploadItem so the helper clears each tile to
        // transparent first (item icons need an alpha background so
        // they read as floating sprites in the hotbar). Called from
        // both atlas builders: CreateAtlas paints these as the canonical
        // look, CreateAtlasFromAlphaTerrain paints them as a guaranteed-
        // correct base before UploadCaneBlockLayersFromTerrain attempts
        // to overlay the verified terrain.png coord. Painting the item
        // sprites here (instead of relying on GenerateProceduralItemLayers,
        // which the alpha-textures path doesn't call) keeps the icons
        // visible in alpha-textures mode — without this, paper/book/
        // sugar-cane-item slots would stay transparent because their
        // AlphaTileCoords entries are sentinels.
        private static void GenerateProceduralCaneLayers(byte[] layerPixels)
        {
            UploadLayer(layerPixels, TileSugarCane, GenerateSugarCane);
            UploadItem(layerPixels, TileSugarCaneItem, GenerateSugarCaneItemIcon);
            UploadItem(layerPixels, TilePaper,         GeneratePaperItem);
            UploadItem(layerPixels, TileBook,          GenerateBookItem);
        }

        // Farmland top — tilled dirt. Same brown palette as TileDirt but
        // overlaid with three horizontal furrow lines (lighter ridges +
        // darker valleys) to read as plowed soil. Sides/bottom of the
        // farmland block reuse TileDirt via Block.GetTileIndex's
        // multi-face routing, so we only need the top face here.
        private static void GenerateFarmlandTop(byte[] pixels)
        {
            var rng = new Random(0xFA70);
            (byte r, byte g, byte b) dirt   = (134, 96, 67);
            (byte r, byte g, byte b) dirtHi = (164, 122, 88);
            (byte r, byte g, byte b) dirtLo = (98, 70, 48);
            // Base dirt fill — jittered for the noisy-soil look the
            // procedural Dirt tile already uses.
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
                SetJittered(pixels, x, y, dirt.r, dirt.g, dirt.b, 14, rng);
            // Three horizontal furrow bands. Each band is a 1-px ridge
            // (highlight) above a 1-px valley (shadow), giving a subtle
            // plowed-row pattern at hotbar scale.
            int[] ridgeRows = { 3, 8, 13 };
            foreach (int rr in ridgeRows)
            {
                for (int x = 0; x < TileSize; x++)
                {
                    SetPixel(pixels, x, rr, dirtHi.r, dirtHi.g, dirtHi.b);
                    if (rr + 1 < TileSize)
                        SetPixel(pixels, x, rr + 1, dirtLo.r, dirtLo.g, dirtLo.b);
                }
            }
        }

        // Wheat sprite drawer. Renders `count` vertical stalks across
        // the tile, each `height` pixels tall, palette-shifted from
        // green (young) to gold (ripe) per the `golden` flag. Stalks
        // are evenly spaced; their tops carry seed-cluster pixels for
        // the mid-late stages so they read as developing grain heads.
        private static void DrawWheatSprite(byte[] pixels, int height, bool golden, int seed)
        {
            var rng = new Random(seed);
            (byte r, byte g, byte b) stalk;
            (byte r, byte g, byte b) seedTop;
            (byte r, byte g, byte b) leaf;
            if (golden)
            {
                stalk   = (210, 180, 75);
                seedTop = (245, 225, 110);
                leaf    = (180, 145, 50);
            }
            else
            {
                stalk   = (105, 165, 70);
                seedTop = (175, 215, 95);
                leaf    = (75, 130, 50);
            }
            // Five stalks at regular x positions for a "field clump" read.
            int[] xs = { 2, 5, 8, 11, 14 };
            int baseY = 14; // stalk base sits one row above the bottom.
            int top = Math.Max(2, baseY - height);
            foreach (int x in xs)
            {
                for (int y = baseY; y >= top; y--)
                    SetPixel(pixels, x, y, stalk.r, stalk.g, stalk.b);
                // Seed cluster on top of each stalk for stages tall enough.
                if (height >= 5)
                {
                    SetPixel(pixels, x, top - 1, seedTop.r, seedTop.g, seedTop.b);
                    if (x - 1 >= 0) SetPixel(pixels, x - 1, top, leaf.r, leaf.g, leaf.b);
                    if (x + 1 < TileSize) SetPixel(pixels, x + 1, top, leaf.r, leaf.g, leaf.b);
                }
                // Leaf flicks for taller stages.
                if (height >= 7)
                {
                    int midY = (baseY + top) / 2;
                    if (rng.Next(2) == 0 && x - 1 >= 0)
                        SetPixel(pixels, x - 1, midY, leaf.r, leaf.g, leaf.b);
                    if (rng.Next(2) == 0 && x + 1 < TileSize)
                        SetPixel(pixels, x + 1, midY, leaf.r, leaf.g, leaf.b);
                }
            }
        }

        // Wheat stages 0..7 — each calls DrawWheatSprite with a height
        // that grows linearly per stage and a palette that flips from
        // green to gold around stage 5 so the player sees the visible
        // ripening cue right before harvest. Stage 7 is the harvest-
        // ready golden field.
        private static void GenerateWheatStage0(byte[] p) { DrawWheatSprite(p, 2,  false, 0xA0); }
        private static void GenerateWheatStage1(byte[] p) { DrawWheatSprite(p, 3,  false, 0xA1); }
        private static void GenerateWheatStage2(byte[] p) { DrawWheatSprite(p, 5,  false, 0xA2); }
        private static void GenerateWheatStage3(byte[] p) { DrawWheatSprite(p, 6,  false, 0xA3); }
        private static void GenerateWheatStage4(byte[] p) { DrawWheatSprite(p, 8,  false, 0xA4); }
        private static void GenerateWheatStage5(byte[] p) { DrawWheatSprite(p, 9,  true,  0xA5); }
        private static void GenerateWheatStage6(byte[] p) { DrawWheatSprite(p, 10, true,  0xA6); }
        private static void GenerateWheatStage7(byte[] p) { DrawWheatSprite(p, 11, true,  0xA7); }

        // Tier 4 #26 — Sugar cane cross-sprite. Tall green stalks
        // running the full height of the tile, banded every ~3 pixels
        // with a darker segment notch (matches Alpha 1.1.2's distinctive
        // bamboo-jointed silhouette so it reads as cane and not just
        // tall grass). Fully fills vertically because the block is
        // single-tile and stacks of 2/3 tile vertically into a column.
        private static void GenerateSugarCane(byte[] pixels)
        {
            (byte r, byte g, byte b) stalk    = (160, 200, 110);
            (byte r, byte g, byte b) stalkHi  = (200, 235, 145);
            (byte r, byte g, byte b) stalkLo  = (115, 155, 75);
            (byte r, byte g, byte b) joint    = (90, 130, 60);
            // Three stalks across — left, center, right. Each is 1 px
            // wide with a highlight track and a shadow track for the
            // chunky 3D feel; segment notches every 4 px paint joint
            // pixels straight across the tile.
            int[] xs = { 4, 8, 12 };
            foreach (int x in xs)
            {
                for (int y = 0; y < TileSize; y++)
                {
                    SetPixel(pixels, x, y, stalk.r, stalk.g, stalk.b);
                    if (x - 1 >= 0)
                        SetPixel(pixels, x - 1, y, stalkHi.r, stalkHi.g, stalkHi.b);
                    if (x + 1 < TileSize)
                        SetPixel(pixels, x + 1, y, stalkLo.r, stalkLo.g, stalkLo.b);
                }
                // Joint bands at fixed y rows so all three stalks share
                // segment positions — reads as one connected cane plant.
                int[] joints = { 2, 6, 10, 14 };
                foreach (int yj in joints)
                {
                    SetPixel(pixels, x, yj, joint.r, joint.g, joint.b);
                    if (x - 1 >= 0) SetPixel(pixels, x - 1, yj, joint.r, joint.g, joint.b);
                    if (x + 1 < TileSize) SetPixel(pixels, x + 1, yj, joint.r, joint.g, joint.b);
                }
            }
        }

        // Tier 4 #26 — Sugar cane item icon. Single vertical stalk
        // centred in the tile with the same banded green look as the
        // in-world block, scaled down to icon size and given a slight
        // diagonal tilt so it reads as a held item rather than a
        // wallpaper-tiled fragment of the block sprite.
        private static void GenerateSugarCaneItemIcon(byte[] pixels)
        {
            (byte r, byte g, byte b) stalk    = (160, 200, 110);
            (byte r, byte g, byte b) stalkHi  = (200, 235, 145);
            (byte r, byte g, byte b) stalkLo  = (115, 155, 75);
            (byte r, byte g, byte b) joint    = (90, 130, 60);
            // Diagonal stalk from (5,12) up to (10,3) — same tilt as
            // the stick / wheat-bundle icons so the held-item silhouette
            // matches.
            int[] xs = { 5, 6, 7, 8, 9, 10 };
            int[] ys = { 12, 11, 9, 7, 5, 3 };
            for (int i = 0; i < xs.Length; i++)
            {
                int x = xs[i], y = ys[i];
                SetPixel(pixels, x, y, stalk.r, stalk.g, stalk.b);
                if (x - 1 >= 0) SetPixel(pixels, x - 1, y, stalkHi.r, stalkHi.g, stalkHi.b);
                if (x + 1 < TileSize) SetPixel(pixels, x + 1, y, stalkLo.r, stalkLo.g, stalkLo.b);
                // Fill the gap-y between this stalk pixel and the next
                // so the diagonal reads as continuous.
                if (i + 1 < xs.Length)
                {
                    int yMid = (y + ys[i + 1]) / 2;
                    SetPixel(pixels, x, yMid, stalk.r, stalk.g, stalk.b);
                }
            }
            // Two joint dots at the midpoints — visual cue for the
            // segmented-cane look at hotbar size.
            SetPixel(pixels, 7, 8, joint.r, joint.g, joint.b);
            SetPixel(pixels, 9, 4, joint.r, joint.g, joint.b);
        }

        // Tier 4 #26 — Paper sheet icon. Off-white slightly-skewed
        // rectangle with a folded corner, sitting roughly in the tile
        // centre. Reads as "paper" rather than "wool" because of the
        // hard sharp edges + the folded-corner crease.
        private static void GeneratePaperItem(byte[] pixels)
        {
            (byte r, byte g, byte b) sheet   = (235, 235, 220);
            (byte r, byte g, byte b) sheetHi = (252, 252, 245);
            (byte r, byte g, byte b) sheetLo = (180, 180, 160);
            // Body — 8 wide × 10 tall, centred a little above midline.
            for (int y = 3; y <= 12; y++)
            for (int x = 4; x <= 11; x++)
                SetPixel(pixels, x, y, sheet.r, sheet.g, sheet.b);
            // Bright top edge.
            for (int x = 4; x <= 11; x++)
                SetPixel(pixels, x, 3, sheetHi.r, sheetHi.g, sheetHi.b);
            // Dark bottom + right edges for the silhouette.
            for (int x = 4; x <= 11; x++)
                SetPixel(pixels, x, 12, sheetLo.r, sheetLo.g, sheetLo.b);
            for (int y = 3; y <= 12; y++)
                SetPixel(pixels, 11, y, sheetLo.r, sheetLo.g, sheetLo.b);
            // Folded corner — cut the upper-right tip with a triangle
            // of darker pixels so the sheet reads as tilted paper, not
            // a flat rectangle.
            SetPixel(pixels, 11, 3, sheetLo.r, sheetLo.g, sheetLo.b);
            SetPixel(pixels, 10, 3, sheetLo.r, sheetLo.g, sheetLo.b);
            SetPixel(pixels, 11, 4, sheetLo.r, sheetLo.g, sheetLo.b);
        }

        // Tier 4 #26 — Book icon. Brown leather cover with a paper
        // edge stripe along the right (the "pages" of a closed book)
        // and a thin gold-coloured spine band on the left. Same general
        // silhouette as paper but with the leather palette + spine
        // detail so the two icons are distinguishable at hotbar size.
        private static void GenerateBookItem(byte[] pixels)
        {
            (byte r, byte g, byte b) cover   = (140, 80, 50);
            (byte r, byte g, byte b) coverHi = (180, 110, 70);
            (byte r, byte g, byte b) coverLo = (90, 50, 30);
            (byte r, byte g, byte b) pages   = (235, 225, 195);
            (byte r, byte g, byte b) pageEdge = (180, 170, 140);
            (byte r, byte g, byte b) spine   = (200, 165, 60);
            // Body — leather cover, 7 wide × 10 tall.
            for (int y = 3; y <= 12; y++)
            for (int x = 4; x <= 10; x++)
                SetPixel(pixels, x, y, cover.r, cover.g, cover.b);
            // Bright top edge.
            for (int x = 4; x <= 10; x++)
                SetPixel(pixels, x, 3, coverHi.r, coverHi.g, coverHi.b);
            // Dark bottom edge.
            for (int x = 4; x <= 10; x++)
                SetPixel(pixels, x, 12, coverLo.r, coverLo.g, coverLo.b);
            // Spine — single column of gold pixels at x=4.
            for (int y = 4; y <= 11; y++)
                SetPixel(pixels, 4, y, spine.r, spine.g, spine.b);
            // Page edge — strip of cream-coloured paper protruding past
            // the right edge of the leather to show the closed pages.
            for (int y = 4; y <= 11; y++)
                SetPixel(pixels, 11, y, pages.r, pages.g, pages.b);
            // Page-edge top/bottom shadowed pixels.
            SetPixel(pixels, 11, 3, pageEdge.r, pageEdge.g, pageEdge.b);
            SetPixel(pixels, 11, 12, pageEdge.r, pageEdge.g, pageEdge.b);
        }

        // Tier 4 #16 — Door procedural pack. The two block-half tiles
        // (top + bottom) draw a 12-pixel-wide rectangle pinned to the
        // appropriate edge of the 16×16 cell so the slab mesher can
        // sample the FULL tile and the visible art aligns naturally
        // to the visible 8-pixel strip. The two side gutters (4 px
        // wide on the right, 0 on the left for hinge orientation) get
        // transparent pixels — the slab quad's UVs cover [0..1] in
        // both axes, so the 4-px gutter ensures the player sees a
        // door-shaped silhouette without bleed onto neighbouring
        // cells. Material is parameterised via a palette pair so
        // wood and iron can share the silhouette code.
        private static void DrawDoorBlockHalf(byte[] pixels, bool topHalf,
            (byte r, byte g, byte b) body,
            (byte r, byte g, byte b) hi,
            (byte r, byte g, byte b) lo,
            (byte r, byte g, byte b) band)
        {
            // Door body — 12 columns wide (x=2..13), full 16 rows tall
            // for the half-tile. Sides outside [2..13] stay
            // transparent so the slab silhouette reads as "door, not
            // full block".
            for (int y = 0; y < TileSize; y++)
            for (int x = 2; x <= 13; x++)
                SetPixel(pixels, x, y, body.r, body.g, body.b);
            // Vertical highlight on the leftmost interior column +
            // shadow on the rightmost. Gives the slab the chunky-3D
            // shading the rest of the atlas tiles use (planks, log,
            // bookshelf side).
            for (int y = 0; y < TileSize; y++)
            {
                SetPixel(pixels, 2, y, hi.r, hi.g, hi.b);
                SetPixel(pixels, 13, y, lo.r, lo.g, lo.b);
            }
            // Horizontal cross-brace bands. The TOP half gets a band
            // at y=2 (the top of the door's upper panel) + y=13 (just
            // above the seam between the two halves); the BOTTOM half
            // gets one at y=2 (just below the seam) + y=13 (kick-plate
            // base above the floor). Same colour band on both halves
            // so a stacked door reads as a continuous frame.
            int bandTop = topHalf ? 2 : 2;
            int bandBot = topHalf ? 13 : 13;
            for (int x = 2; x <= 13; x++)
            {
                SetPixel(pixels, x, bandTop, band.r, band.g, band.b);
                SetPixel(pixels, x, bandBot, band.r, band.g, band.b);
            }
            // Hinge bolts on the LEFT side at the band rows — two dark
            // dots inside the band, suggesting the metal pin that
            // holds the door to the frame. Same on both halves.
            SetPixel(pixels, 3, bandTop, lo.r, lo.g, lo.b);
            SetPixel(pixels, 3, bandBot, lo.r, lo.g, lo.b);
            if (topHalf)
            {
                // Top half — small inset window centred horizontally
                // at y=5..7 (cross-grille of darker pixels) so the
                // upper door panel reads as "door with peek-window"
                // even at hotbar zoom.
                for (int y = 5; y <= 7; y++)
                for (int x = 6; x <= 9; x++)
                    SetPixel(pixels, x, y, lo.r, lo.g, lo.b);
                // Window highlight pip.
                SetPixel(pixels, 7, 5, hi.r, hi.g, hi.b);
            }
            else
            {
                // Bottom half — round handle/knob on the RIGHT side
                // (the side opposite the hinge) at vertical centre.
                // Two-tone shading: bright pip for the highlight, dark
                // pixel below for the shadow that anchors the knob.
                SetPixel(pixels, 11, 7, hi.r, hi.g, hi.b);
                SetPixel(pixels, 11, 8, lo.r, lo.g, lo.b);
                SetPixel(pixels, 12, 7, body.r, body.g, body.b);
            }
        }

        private static void GenerateWoodDoorTop(byte[] pixels)
        {
            // Plank palette — same general hue family as TilePlanks
            // (warm orange-brown) so a wood door visually matches a
            // plank wall it's set into. Slightly darker overall than
            // the planks tile because the door has a recessed
            // panel/window detail that looks busier next to flat plank.
            DrawDoorBlockHalf(pixels, topHalf: true,
                body: (155, 110, 60),
                hi:   (190, 145, 90),
                lo:   (105, 70, 35),
                band: (130, 90, 45));
        }

        private static void GenerateWoodDoorBottom(byte[] pixels)
        {
            DrawDoorBlockHalf(pixels, topHalf: false,
                body: (155, 110, 60),
                hi:   (190, 145, 90),
                lo:   (105, 70, 35),
                band: (130, 90, 45));
        }

        private static void GenerateIronDoorTop(byte[] pixels)
        {
            // Iron palette — cool grey with a hint of blue tint. Same
            // general silhouette as the wood door so the player reads
            // both as "door"; the colour difference is the only thing
            // that needs to communicate "iron" at hotbar size.
            DrawDoorBlockHalf(pixels, topHalf: true,
                body: (180, 180, 195),
                hi:   (215, 215, 225),
                lo:   (115, 115, 130),
                band: (90, 90, 105));
        }

        private static void GenerateIronDoorBottom(byte[] pixels)
        {
            DrawDoorBlockHalf(pixels, topHalf: false,
                body: (180, 180, 195),
                hi:   (215, 215, 225),
                lo:   (115, 115, 130),
                band: (90, 90, 105));
        }

        // Door inventory icon — full-height door silhouette (top half
        // + bottom half stacked) drawn into a single 16×16 tile, just
        // squeezed vertically. Reads as "complete door" in the hotbar
        // so the player can tell wood from iron without placing it
        // first. Same body/highlight/shadow/band palette as the in-
        // world block halves so the icon and placed door visually
        // match.
        private static void DrawDoorItemIcon(byte[] pixels,
            (byte r, byte g, byte b) body,
            (byte r, byte g, byte b) hi,
            (byte r, byte g, byte b) lo,
            (byte r, byte g, byte b) band)
        {
            // Body — narrower than the block half (8 columns wide,
            // x=4..11) and full 16 rows tall so the icon reads as a
            // tall thin door silhouette rather than a square block.
            for (int y = 1; y <= 14; y++)
            for (int x = 4; x <= 11; x++)
                SetPixel(pixels, x, y, body.r, body.g, body.b);
            // Highlight + shadow vertical bars.
            for (int y = 1; y <= 14; y++)
            {
                SetPixel(pixels, 4, y, hi.r, hi.g, hi.b);
                SetPixel(pixels, 11, y, lo.r, lo.g, lo.b);
            }
            // Top + bottom + middle bands (the middle band marks the
            // half-block seam — same visual cue Alpha 1.1.2's door
            // icons use).
            for (int x = 4; x <= 11; x++)
            {
                SetPixel(pixels, x, 1, band.r, band.g, band.b);
                SetPixel(pixels, x, 14, band.r, band.g, band.b);
                SetPixel(pixels, x, 7, band.r, band.g, band.b);
            }
            // Window in the upper half (at y=3..5) + handle pip in
            // the lower half (at y=10..11). Mirrors the in-world
            // block half art so the icon is recognisable as the
            // same item.
            for (int y = 3; y <= 5; y++)
            for (int x = 7; x <= 9; x++)
                SetPixel(pixels, x, y, lo.r, lo.g, lo.b);
            SetPixel(pixels, 8, 3, hi.r, hi.g, hi.b);
            // Handle on the right at vertical-centre of the lower
            // half (y=10).
            SetPixel(pixels, 9, 10, hi.r, hi.g, hi.b);
            SetPixel(pixels, 9, 11, lo.r, lo.g, lo.b);
        }

        private static void GenerateWoodDoorItemIcon(byte[] pixels)
        {
            DrawDoorItemIcon(pixels,
                body: (155, 110, 60),
                hi:   (190, 145, 90),
                lo:   (105, 70, 35),
                band: (130, 90, 45));
        }

        private static void GenerateIronDoorItemIcon(byte[] pixels)
        {
            DrawDoorItemIcon(pixels,
                body: (180, 180, 195),
                hi:   (215, 215, 225),
                lo:   (115, 115, 130),
                band: (90, 90, 105));
        }

        // Tier 4 #16 — Procedural door layer painter. Called from both
        // the procedural-only atlas (CreateAtlas) and the alpha-textures
        // atlas (CreateAtlasFromAlphaTerrain) so doors render
        // identically in either mode. All 6 layers (4 block halves + 2
        // item icons) ship procedural for now; the AlphaTileCoords
        // sentinels mean the slice helpers won't overlay them.
        private static void GenerateProceduralDoorLayers(byte[] layerPixels)
        {
            UploadLayer(layerPixels, TileWoodDoorTop,    GenerateWoodDoorTop);
            UploadLayer(layerPixels, TileWoodDoorBottom, GenerateWoodDoorBottom);
            UploadLayer(layerPixels, TileIronDoorTop,    GenerateIronDoorTop);
            UploadLayer(layerPixels, TileIronDoorBottom, GenerateIronDoorBottom);
            UploadItem (layerPixels, TileWoodDoorItem,   GenerateWoodDoorItemIcon);
            UploadItem (layerPixels, TileIronDoorItem,   GenerateIronDoorItemIcon);
        }

        // Tier 4 #17 — Flint and Steel sprite. A small grey-steel
        // strike-bar diagonally crossing a brown flint chip. The
        // diagonal "+" silhouette reads as a fire-starter at hotbar
        // scale; the steel highlights pick up so the metal flicker
        // is visible against the dark flint.
        private static void GenerateFlintAndSteelItem(byte[] pixels)
        {
            (byte r, byte g, byte b) flint   = (60, 55, 50);
            (byte r, byte g, byte b) flintHi = (110, 100, 90);
            (byte r, byte g, byte b) steel   = (180, 180, 195);
            (byte r, byte g, byte b) steelHi = (230, 230, 240);
            (byte r, byte g, byte b) steelLo = (95, 95, 110);
            // Flint chip — bottom-left to mid-right diagonal blob.
            int[] fx = { 4, 5, 5, 6, 6, 7, 7, 8 };
            int[] fy = { 12, 11, 12, 11, 12, 10, 11, 10 };
            for (int i = 0; i < fx.Length; i++)
                SetPixel(pixels, fx[i], fy[i], flint.r, flint.g, flint.b);
            SetPixel(pixels, 5, 11, flintHi.r, flintHi.g, flintHi.b);
            SetPixel(pixels, 7, 10, flintHi.r, flintHi.g, flintHi.b);
            // Steel striker — diagonal bar from upper-right toward
            // the flint, two pixels wide for visual weight.
            for (int i = 0; i < 6; i++)
            {
                int x = 11 - i;
                int y = 4 + i;
                SetPixel(pixels, x, y, steel.r, steel.g, steel.b);
                SetPixel(pixels, x + 1, y, steelLo.r, steelLo.g, steelLo.b);
                SetPixel(pixels, x, y - 1, steelHi.r, steelHi.g, steelHi.b);
            }
            // Spark dots — three small bright pips between the
            // striker tip and the flint, hinting at "sparks fly".
            SetPixel(pixels, 8, 9, 255, 220, 100);
            SetPixel(pixels, 9, 8, 255, 200, 80);
        }

        // Tier 4 #17 — Apple sprite. A round red fruit with a small
        // brown stem and a green leaf pip. Reads at hotbar size as
        // unmistakeably "apple"; deep red body with a single
        // highlight pixel anchors the colour against the warmer
        // food items (porkchop / bread) so the player can pick the
        // apple out of a mixed inventory at a glance.
        private static void GenerateAppleItem(byte[] pixels)
        {
            (byte r, byte g, byte b) skin   = (205, 40, 40);
            (byte r, byte g, byte b) skinHi = (245, 110, 90);
            (byte r, byte g, byte b) skinLo = (130, 20, 20);
            (byte r, byte g, byte b) stem   = (90, 55, 25);
            (byte r, byte g, byte b) leaf   = (60, 145, 50);
            (byte r, byte g, byte b) leafHi = (120, 200, 100);
            // Apple body — rounded blob centred horizontally,
            // slightly squashed so it reads as fruit rather than
            // a sphere. Rows 5..12, columns 5..10, corners trimmed.
            for (int y = 5; y <= 12; y++)
            for (int x = 5; x <= 10; x++)
            {
                bool corner = (x == 5 || x == 10) && (y == 5 || y == 12);
                if (corner) continue;
                SetPixel(pixels, x, y, skin.r, skin.g, skin.b);
            }
            // Highlight bloom — bright pip on the upper-left of the
            // body for a glossy fruit read.
            SetPixel(pixels, 6, 6, skinHi.r, skinHi.g, skinHi.b);
            SetPixel(pixels, 7, 7, skinHi.r, skinHi.g, skinHi.b);
            // Shadow band — bottom-right edge.
            SetPixel(pixels, 10, 11, skinLo.r, skinLo.g, skinLo.b);
            SetPixel(pixels, 9, 12, skinLo.r, skinLo.g, skinLo.b);
            // Stem — thin brown stub at the top.
            SetPixel(pixels, 8, 4, stem.r, stem.g, stem.b);
            SetPixel(pixels, 8, 3, stem.r, stem.g, stem.b);
            // Leaf — small green wedge to the right of the stem.
            SetPixel(pixels, 9, 4, leaf.r, leaf.g, leaf.b);
            SetPixel(pixels, 10, 4, leafHi.r, leafHi.g, leafHi.b);
        }

        // Tier 4 #17 — Procedural fire-and-food layer painter. Called
        // from both the procedural-only atlas (CreateAtlas) and the
        // alpha-textures atlas (CreateAtlasFromAlphaTerrain). Same
        // fall-through pattern as the door layer painter — sentinel
        // entries in AlphaTileCoords mean these are always painted
        // procedurally regardless of atlas mode.
        private static void GenerateProceduralFireLayers(byte[] layerPixels)
        {
            UploadItem(layerPixels, TileFlintAndSteel, GenerateFlintAndSteelItem);
            UploadItem(layerPixels, TileApple,         GenerateAppleItem);
        }

        // Tier 4 #20 — Procedural throwable icon painter. Snowball is
        // the only new sprite; Egg's tile (TileEgg=84) was painted by
        // Tier 3 #12's drop pass. Same procedural-only story as the
        // fire pack — sentinel entries in AlphaTileCoords mean both
        // atlas modes paint identically here.
        private static void GenerateProceduralThrowLayers(byte[] layerPixels)
        {
            UploadItem(layerPixels, TileSnowball, GenerateSnowballItem);
        }

        // Tier 4 #15 — Procedural bucket icon painter. Four sprites:
        // empty, water, lava, milk. All four share the silver-pail
        // silhouette painted by GenerateBucketBody so the hotbar reads
        // as "same item, different contents" — only the contents fill
        // colour differs. Same procedural-only story as the throw pack
        // (sentinel coords mean both atlas modes paint identically).
        private static void GenerateProceduralBucketLayers(byte[] layerPixels)
        {
            UploadItem(layerPixels, TileBucketEmpty, GenerateBucketEmptyItem);
            UploadItem(layerPixels, TileBucketWater, GenerateBucketWaterItem);
            UploadItem(layerPixels, TileBucketLava,  GenerateBucketLavaItem);
            UploadItem(layerPixels, TileBucketMilk,  GenerateBucketMilkItem);
        }

        // Tier 4 #18 — Slimeball icon painter. Single sprite — small
        // green sphere with a brighter highlight pip and a darker
        // shadow pip so it reads as a 3D ball, not a flat disc. The
        // colour band (saturated lime → forest green) matches the
        // Slime mob body so a glance at the hotbar links the drop to
        // the mob it came from.
        private static void GenerateProceduralSlimeLayers(byte[] layerPixels)
        {
            UploadItem(layerPixels, TileSlimeball, GenerateSlimeballItem);
        }

        // Tier 4 #22 — Compass icon painter. Single sprite — light
        // grey circular dial face with a fixed "N" indicator at the
        // top so the player can read the dial as a compass at a
        // glance even before the live direction overlay paints. The
        // rotating arrow / direction marker is drawn separately by
        // RenderHotbar (per-frame text overlay tied to the player's
        // bearing); baking it into the atlas tile would require
        // mutating the texture every frame, which isn't worth the GL
        // cost for a one-letter glyph the HUD layer can already draw.
        private static void GenerateProceduralCompassLayers(byte[] layerPixels)
        {
            UploadItem(layerPixels, TileCompass, GenerateCompassItem);
        }

        // Tier 4 #21 — Saddle icon painter. Single sprite — small
        // brown leather pad with a darker hide outline, a horn at the
        // front (the raised pommel), and a tiny iron-buckle pip on
        // the side strap. Same procedural-only story as the slime /
        // compass packs (sentinel atlas coord), so both atlas modes
        // paint identically.
        private static void GenerateProceduralSaddleLayers(byte[] layerPixels)
        {
            UploadItem(layerPixels, TileSaddle, GenerateSaddleItem);
        }

        // Tier 4 #23 — Fishing Rod icon painter. Single sprite — a
        // brown rod shaft running corner-to-corner across the tile,
        // with a thin grey "line" trailing off the tip into a small
        // hook silhouette so the icon is unmistakably a rod (and not
        // a stick / arrow / bow). Same procedural-only story as the
        // saddle pack (sentinel atlas coord), so both atlas modes
        // paint identically.
        private static void GenerateProceduralFishingRodLayers(byte[] layerPixels)
        {
            UploadItem(layerPixels, TileFishingRod, GenerateFishingRodItem);
        }

        // Tier 4 #24 — Painting art + icon painter. Five 16×16 art
        // tiles each with a distinct procedural pattern (squiggle,
        // gradient, dots, cross, stripes) so the player can tell
        // the variants apart at a glance even when the renderer
        // stretches a single 16×16 tile across a 4×3-block wall
        // rectangle. Plus one inventory-icon tile (small framed
        // picture: brown wood frame around a colour splash). All
        // six ship procedural; their AlphaTileCoords entries are
        // sentinels so neither alpha-textures mode overlays them.
        private static void GenerateProceduralPaintingLayers(byte[] layerPixels)
        {
            UploadItem(layerPixels, TilePainting1x1, GeneratePainting1x1);
            UploadItem(layerPixels, TilePainting1x2, GeneratePainting1x2);
            UploadItem(layerPixels, TilePainting2x1, GeneratePainting2x1);
            UploadItem(layerPixels, TilePainting2x2, GeneratePainting2x2);
            UploadItem(layerPixels, TilePainting4x3, GeneratePainting4x3);
            UploadItem(layerPixels, TilePaintingItem, GeneratePaintingItem);
        }

        // Helper: paint a single-pixel-wide brown wooden frame
        // around the [0..TileSize-1] tile so each painting variant
        // shares the same frame look the Alpha sprites use. The
        // inset content (squiggle / gradient / etc.) sits in the
        // 14×14 inner area.
        private static void PaintWoodFrame(byte[] pixels)
        {
            (byte r, byte g, byte b) frame   = (95, 60, 30);
            (byte r, byte g, byte b) frameHi = (135, 90, 50);
            for (int x = 0; x < TileSize; x++)
            {
                SetPixel(pixels, x, 0, frameHi.r, frameHi.g, frameHi.b);
                SetPixel(pixels, x, TileSize - 1, frame.r, frame.g, frame.b);
            }
            for (int y = 0; y < TileSize; y++)
            {
                SetPixel(pixels, 0, y, frame.r, frame.g, frame.b);
                SetPixel(pixels, TileSize - 1, y, frameHi.r, frameHi.g, frameHi.b);
            }
        }

        // Variant 0 — squiggle. A red zig-zag walking from
        // bottom-left to upper-right across the inner area; reads
        // as a "wavy line" painting at 1×1 and a stretched
        // wave at larger sizes.
        private static void GeneratePainting1x1(byte[] pixels)
        {
            // Off-white canvas background.
            (byte r, byte g, byte b) bg = (235, 225, 200);
            for (int y = 1; y < TileSize - 1; y++)
            for (int x = 1; x < TileSize - 1; x++)
                SetPixel(pixels, x, y, bg.r, bg.g, bg.b);
            PaintWoodFrame(pixels);
            // Squiggle — sin-ish path approximated by integer ys.
            (byte r, byte g, byte b) line = (180, 50, 50);
            int[] ys = { 8, 6, 5, 4, 5, 7, 9, 11, 12, 11, 9, 7, 5, 6 };
            for (int i = 0; i < ys.Length; i++)
            {
                int x = 1 + i;
                int y = ys[i];
                if (x >= 1 && x < TileSize - 1 && y >= 1 && y < TileSize - 1)
                    SetPixel(pixels, x, y, line.r, line.g, line.b);
            }
        }

        // Variant 1 — vertical gradient. Four-band sky-to-ground
        // (blue → green) reads as a landscape painting and
        // stretches sensibly to a 1×2 (tall) rectangle.
        private static void GeneratePainting1x2(byte[] pixels)
        {
            (byte r, byte g, byte b) sky    = (110, 165, 230);
            (byte r, byte g, byte b) horizn = (190, 200, 180);
            (byte r, byte g, byte b) field  = (105, 165, 75);
            (byte r, byte g, byte b) earth  = (70, 105, 45);
            for (int y = 1; y < TileSize - 1; y++)
            {
                (byte r, byte g, byte b) c;
                if      (y < 5)  c = sky;
                else if (y < 8)  c = horizn;
                else if (y < 12) c = field;
                else             c = earth;
                for (int x = 1; x < TileSize - 1; x++)
                    SetPixel(pixels, x, y, c.r, c.g, c.b);
            }
            PaintWoodFrame(pixels);
        }

        // Variant 2 — dots / stars. Off-black background with a
        // scatter of bright pips; reads as a starfield, stretches
        // as a "panoramic night sky" at 2×1.
        private static void GeneratePainting2x1(byte[] pixels)
        {
            (byte r, byte g, byte b) bg   = (25, 30, 55);
            (byte r, byte g, byte b) pip  = (240, 235, 180);
            (byte r, byte g, byte b) pip2 = (180, 200, 240);
            for (int y = 1; y < TileSize - 1; y++)
            for (int x = 1; x < TileSize - 1; x++)
                SetPixel(pixels, x, y, bg.r, bg.g, bg.b);
            // Hand-placed pips so the pattern reads at 1×1
            // crops; each pip is a single pixel except for two
            // 2-pixel "bright stars".
            int[][] pips = {
                new[]{ 3, 3 }, new[]{ 6, 5 }, new[]{ 10, 4 }, new[]{ 13, 6 },
                new[]{ 4, 9 }, new[]{ 8, 11 }, new[]{ 12, 12 }, new[]{ 5, 13 },
                new[]{ 11, 8 }, new[]{ 14, 10 },
            };
            foreach (var p in pips)
                SetPixel(pixels, p[0], p[1], pip.r, pip.g, pip.b);
            // Two bigger highlights.
            SetPixel(pixels, 7, 4, pip2.r, pip2.g, pip2.b);
            SetPixel(pixels, 7, 5, pip2.r, pip2.g, pip2.b);
            SetPixel(pixels, 9, 9, pip2.r, pip2.g, pip2.b);
            SetPixel(pixels, 10, 9, pip2.r, pip2.g, pip2.b);
            PaintWoodFrame(pixels);
        }

        // Variant 3 — diagonal cross. Two crossed bars on a
        // light canvas. Reads as a "shield" / heraldic pattern;
        // stretches symmetrically at 2×2.
        private static void GeneratePainting2x2(byte[] pixels)
        {
            (byte r, byte g, byte b) bg   = (220, 215, 195);
            (byte r, byte g, byte b) bar1 = (185, 65, 65);
            (byte r, byte g, byte b) bar2 = (60, 95, 165);
            for (int y = 1; y < TileSize - 1; y++)
            for (int x = 1; x < TileSize - 1; x++)
                SetPixel(pixels, x, y, bg.r, bg.g, bg.b);
            // Diagonal `\` bar (red).
            for (int s = 1; s < TileSize - 1; s++)
            {
                int x = s;
                int y = s;
                if (x >= 1 && x < TileSize - 1 && y >= 1 && y < TileSize - 1)
                    SetPixel(pixels, x, y, bar1.r, bar1.g, bar1.b);
            }
            // Diagonal `/` bar (blue).
            for (int s = 1; s < TileSize - 1; s++)
            {
                int x = s;
                int y = (TileSize - 1) - s;
                if (x >= 1 && x < TileSize - 1 && y >= 1 && y < TileSize - 1)
                    SetPixel(pixels, x, y, bar2.r, bar2.g, bar2.b);
            }
            PaintWoodFrame(pixels);
        }

        // Variant 4 — vertical stripes. Five colour bands the
        // width of the inner area; reads as an abstract striped
        // painting and stretches reasonably at 4×3 (the largest
        // variant, where stretching is most visible). Bands sit
        // at columns 1..14 (3px each) inside the frame.
        private static void GeneratePainting4x3(byte[] pixels)
        {
            (byte r, byte g, byte b)[] bands = {
                (200, 70, 70),
                (210, 175, 80),
                (110, 175, 95),
                (75, 130, 195),
                (165, 105, 200),
            };
            // Background (in case the bands don't tile perfectly).
            (byte r, byte g, byte b) bg = (60, 55, 50);
            for (int y = 1; y < TileSize - 1; y++)
            for (int x = 1; x < TileSize - 1; x++)
                SetPixel(pixels, x, y, bg.r, bg.g, bg.b);
            for (int x = 1; x < TileSize - 1; x++)
            {
                int idx = (x - 1) * bands.Length / (TileSize - 2);
                if (idx < 0) idx = 0;
                if (idx >= bands.Length) idx = bands.Length - 1;
                var c = bands[idx];
                for (int y = 1; y < TileSize - 1; y++)
                    SetPixel(pixels, x, y, c.r, c.g, c.b);
            }
            PaintWoodFrame(pixels);
        }

        // Inventory-icon variant — small framed picture sprite.
        // Brown wooden frame around a 12×8 inner canvas with a
        // simple "horizon + sun" motif so the icon reads as
        // "painting" at a glance in the hotbar / inventory.
        // Smaller sprite (centred) than the art tiles so the
        // wood frame visually dominates — it's the held item
        // version, not the art on the wall.
        private static void GeneratePaintingItem(byte[] pixels)
        {
            (byte r, byte g, byte b) frame   = (90, 55, 28);
            (byte r, byte g, byte b) frameHi = (135, 90, 50);
            (byte r, byte g, byte b) sky     = (115, 170, 230);
            (byte r, byte g, byte b) ground  = (110, 165, 80);
            (byte r, byte g, byte b) sun     = (235, 215, 90);
            // Outer frame: cols 2..13, rows 3..12 (a 12×10 picture).
            int x0 = 2, x1 = 13, y0 = 3, y1 = 12;
            // Frame fill — top + bottom rows + side cols.
            for (int x = x0; x <= x1; x++)
            {
                SetPixel(pixels, x, y0, frameHi.r, frameHi.g, frameHi.b);
                SetPixel(pixels, x, y1, frame.r, frame.g, frame.b);
            }
            for (int y = y0; y <= y1; y++)
            {
                SetPixel(pixels, x0, y, frame.r, frame.g, frame.b);
                SetPixel(pixels, x1, y, frameHi.r, frameHi.g, frameHi.b);
            }
            // Inner canvas (sky + ground) 10×8 at cols 3..12, rows 4..11.
            for (int y = y0 + 1; y < y1; y++)
            for (int x = x0 + 1; x < x1; x++)
            {
                bool isSky = y < (y0 + 1 + (y1 - y0 - 1) / 2);
                var c = isSky ? sky : ground;
                SetPixel(pixels, x, y, c.r, c.g, c.b);
            }
            // Sun pip.
            SetPixel(pixels, x1 - 2, y0 + 2, sun.r, sun.g, sun.b);
            SetPixel(pixels, x1 - 1, y0 + 2, sun.r, sun.g, sun.b);
            SetPixel(pixels, x1 - 2, y0 + 3, sun.r, sun.g, sun.b);
        }

        // Tier 4 #25 — Jukebox + music disc procedural pack. 3 jukebox
        // face tiles + 2 disc-icon tiles. All five paint in both atlas
        // modes — the canonical Alpha terrain.png coords for the
        // jukebox haven't been verified against the embedded sheet,
        // and Alpha doesn't pack disc icons in either bundled PNG, so
        // procedural is the guaranteed-correct path.
        private static void GenerateProceduralJukeboxLayers(byte[] layerPixels)
        {
            UploadLayer(layerPixels, TileJukeboxTop,    GenerateJukeboxTop);
            UploadLayer(layerPixels, TileJukeboxSide,   GenerateJukeboxSide);
            UploadLayer(layerPixels, TileJukeboxBottom, GenerateJukeboxBottom);
            UploadItem(layerPixels,  TileDisc13,        GenerateDisc13Item);
            UploadItem(layerPixels,  TileDiscCat,       GenerateDiscCatItem);
        }

        // Jukebox top — plank base with a centred dark circle (the disc
        // slot) so the player can tell at a glance which face accepts
        // an inserted disc. The circle is filled black/dark-brown to
        // suggest a recess; a single highlight pixel at the centre
        // hints that the slot is the focal point.
        private static void GenerateJukeboxTop(byte[] pixels)
        {
            // Same plank base as GeneratePlanks but with a slightly
            // darker tone so the lid reads as polished cabinet wood
            // rather than bare planks (matches the chest-top palette
            // shift — same trick).
            var rng = new Random(0x57B0);
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
            {
                bool groove = (y % 4 == 0);
                byte r = groove ? (byte)100 : (byte)138;
                byte g = groove ? (byte)68  : (byte)98;
                byte b = groove ? (byte)36  : (byte)56;
                SetJittered(pixels, x, y, r, g, b, 6, rng);
            }
            // Disc-slot circle: a 6-pixel-radius dark recess centred
            // on the tile. We rasterise it as the set of pixels whose
            // squared distance to (cx,cy)=(7.5,7.5) is < r^2. Using
            // 7.5 instead of 7 keeps the circle visually centred on
            // an even-pixel grid (TileSize=16 has no exact centre).
            float cx = 7.5f, cy = 7.5f;
            float rIn  = 4.2f * 4.2f;
            float rRim = 5.2f * 5.2f;
            (byte r, byte g, byte b) recess  = (30, 22, 14);
            (byte r, byte g, byte b) rim     = (60, 42, 24);
            (byte r, byte g, byte b) labelHi = (180, 165, 130);
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
            {
                float dx = x - cx, dy = y - cy;
                float d2 = dx*dx + dy*dy;
                if (d2 < rIn)       SetPixel(pixels, x, y, recess.r, recess.g, recess.b);
                else if (d2 < rRim) SetPixel(pixels, x, y, rim.r, rim.g, rim.b);
            }
            // Tiny centre highlight — a single off-white pixel at
            // the spindle so the slot reads as "disc goes here, not
            // just a hole".
            SetPixel(pixels, 7, 7, labelHi.r, labelHi.g, labelHi.b);
            SetPixel(pixels, 8, 7, labelHi.r, labelHi.g, labelHi.b);
        }

        // Jukebox side — darker plank panel with vertical seams so the
        // four lateral faces read as a polished cabinet rather than
        // generic planks. Distinguishes the jukebox from a chest at a
        // glance (chests have horizontal iron bands; the jukebox has
        // vertical wood seams).
        private static void GenerateJukeboxSide(byte[] pixels)
        {
            var rng = new Random(0x57B1);
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
            {
                // Vertical seams every 4 pixels (instead of horizontal
                // grooves) so the side reads as upright plank slats.
                bool seam = (x % 4 == 0);
                byte r = seam ? (byte)82  : (byte)115;
                byte g = seam ? (byte)56  : (byte)82;
                byte b = seam ? (byte)28  : (byte)44;
                SetJittered(pixels, x, y, r, g, b, 5, rng);
            }
            // A subtle horizontal trim at the top edge so the cabinet
            // has visible joinery — a single line of slightly lighter
            // wood to suggest a moulding strip.
            for (int x = 0; x < TileSize; x++)
            {
                SetPixel(pixels, x, 1, 145, 105, 60);
            }
        }

        // Jukebox bottom — plain planks. Matches the chest convention
        // where the bottom face is uneventful (the player rarely sees
        // it). Allocating its own atlas tile (rather than routing to
        // TilePlanks) gives future cosmetic tweaks somewhere to land
        // without retrofitting a multi-face routing branch.
        private static void GenerateJukeboxBottom(byte[] pixels)
        {
            var rng = new Random(0x57B2);
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
            {
                bool groove = (y % 4 == 0);
                byte r = groove ? (byte)123 : (byte)160;
                byte g = groove ? (byte)91  : (byte)124;
                byte b = groove ? (byte)52  : (byte)74;
                SetJittered(pixels, x, y, r, g, b, 7, rng);
            }
        }

        // Music disc "13" — black vinyl disc with a white "13" centred
        // label. The label colour is the only thing that distinguishes
        // the two discs at hotbar size (16×16 won't render readable
        // text), so the colour choice matters: white reads as the
        // canonical "13" disc art (Alpha shows it with a small white
        // square label).
        private static void GenerateDisc13Item(byte[] pixels)
        {
            PaintDiscBody(pixels, label: (235, 235, 235));
            // Two-pixel "13" hint — a left vertical line + right two
            // dots. Doesn't actually spell the digits at this size,
            // but the silhouette differs enough from the cat label
            // to be distinct.
            SetPixel(pixels, 7, 7, 235, 235, 235);
            SetPixel(pixels, 7, 8, 235, 235, 235);
            SetPixel(pixels, 9, 7, 235, 235, 235);
            SetPixel(pixels, 9, 8, 235, 235, 235);
        }

        // Music disc "cat" — black vinyl disc with a cyan label. Same
        // shape painter as Disc13; only the label colour differs so
        // the two discs are visually distinct in the hotbar.
        private static void GenerateDiscCatItem(byte[] pixels)
        {
            PaintDiscBody(pixels, label: (90, 200, 210));
            // A small horizontal "ear" hint on the cat label — two
            // pixel dots left + right of centre. Doesn't render as
            // a recognisable cat, just differentiates from the "13"
            // dot pattern.
            SetPixel(pixels, 6, 6, 90, 200, 210);
            SetPixel(pixels, 10, 6, 90, 200, 210);
        }

        // Helper: paint a circular black-vinyl disc body with a
        // smaller centred coloured label. Shared between the Disc13
        // and DiscCat painters so the silhouette is identical and the
        // only difference is the label colour. Disc fills 5..6 pixel
        // radius circle; label fills the inner 2-pixel radius.
        private static void PaintDiscBody(byte[] pixels, (byte r, byte g, byte b) label)
        {
            (byte r, byte g, byte b) vinyl     = (15, 15, 18);
            (byte r, byte g, byte b) vinylRim  = (40, 40, 45);
            float cx = 7.5f, cy = 7.5f;
            float rOuter = 6.0f * 6.0f;
            float rRim   = 5.0f * 5.0f;
            float rLabel = 2.5f * 2.5f;
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
            {
                float dx = x - cx, dy = y - cy;
                float d2 = dx*dx + dy*dy;
                if (d2 < rLabel)       SetPixel(pixels, x, y, label.r, label.g, label.b);
                else if (d2 < rRim)    SetPixel(pixels, x, y, vinyl.r, vinyl.g, vinyl.b);
                else if (d2 < rOuter)  SetPixel(pixels, x, y, vinylRim.r, vinylRim.g, vinylRim.b);
            }
        }

        // Tier 4 #23 — Fishing Rod sprite. 16×16 pixel painter:
        //   - Rod shaft: a brown diagonal from (2,13) to (10,5),
        //     widened to a 2-pixel band so the shaft reads as
        //     thicker than the line. The shaft tapers visually
        //     from butt (lower-left) to tip (upper-right) by
        //     dropping the highlight band over the upper half.
        //   - Wrap band: a single dark-leather pixel near the
        //     butt (col 3-4, row 12) hints at a hand-grip wrap.
        //   - Line: a thin off-white diagonal trailing from the
        //     tip (10,5) up to (13,2) — drawn as single pixels
        //     so it visually reads as a fine fishing line, not a
        //     solid bar.
        //   - Hook: a tiny J-shape at the line end ((13,2),
        //     (14,3), (14,4), (13,4)) — three iron-grey pixels
        //     that read as a bent hook even at 16×16.
        // Palette mirrors the Bow sprite's wood tone (warm brown)
        // so a glance at the hotbar groups them visually as the
        // two "stick + string" tools.
        private static void GenerateFishingRodItem(byte[] pixels)
        {
            (byte r, byte g, byte b) wood   = (130, 80, 40);
            (byte r, byte g, byte b) woodHi = (170, 110, 60);
            (byte r, byte g, byte b) woodLo = (85,  50, 25);
            (byte r, byte g, byte b) line   = (230, 225, 215);
            (byte r, byte g, byte b) hook   = (170, 170, 175);
            // Diagonal rod shaft: walks from (2,13) up-right to
            // (10,5). At each step paint a 2-pixel-thick band
            // (the cell + its right neighbour) so the shaft has
            // visible width. Highlight on the upper-right pixel
            // and shadow on the lower-left for a hint of round.
            for (int s = 0; s <= 8; s++)
            {
                int x = 2 + s;
                int y = 13 - s;
                SetPixel(pixels, x,     y,     wood.r,   wood.g,   wood.b);
                SetPixel(pixels, x + 1, y,     woodHi.r, woodHi.g, woodHi.b);
                SetPixel(pixels, x,     y + 1, woodLo.r, woodLo.g, woodLo.b);
            }
            // Grip-wrap band near the butt — a darker pip so the
            // lower-left end reads as the handle, not just a
            // continuation of the shaft.
            SetPixel(pixels, 3, 12, woodLo.r, woodLo.g, woodLo.b);
            SetPixel(pixels, 4, 12, woodLo.r, woodLo.g, woodLo.b);
            // Line trailing off the tip. Three single pixels
            // walking up-right toward the hook.
            SetPixel(pixels, 11, 4, line.r, line.g, line.b);
            SetPixel(pixels, 12, 3, line.r, line.g, line.b);
            SetPixel(pixels, 13, 2, line.r, line.g, line.b);
            // Hook — small J at the line end.
            SetPixel(pixels, 14, 3, hook.r, hook.g, hook.b);
            SetPixel(pixels, 14, 4, hook.r, hook.g, hook.b);
            SetPixel(pixels, 13, 4, hook.r, hook.g, hook.b);
        }

        // Tier 4 #21 — Saddle sprite. 16×16 pixel painter:
        //   - Main pad: a horizontally-elongated brown rectangle
        //     (rows 7..11, cols 3..12). Reads as the seat the player
        //     sits on top of the pig.
        //   - Outline: one-pixel darker rim around the pad so the
        //     silhouette pops against any hotbar background colour.
        //   - Pommel: a tiny raised hump on the front edge (cols 7..8,
        //     row 6) — the saddle's horn, what the rider grips.
        //   - Buckle: single iron-grey pixel pair on the right side
        //     strap (col 11, row 9) so the pad reads as a strapped
        //     saddle, not just a generic brown blob.
        // Palette is muted — saturated browns clash with the warmer
        // pinks of the porkchop and bread tiles, so the saddle gets a
        // colder, drabber leather hue to keep the hotbar legible.
        private static void GenerateSaddleItem(byte[] pixels)
        {
            (byte r, byte g, byte b) leather   = (115, 75, 40);
            (byte r, byte g, byte b) leatherHi = (155, 105, 60);
            (byte r, byte g, byte b) leatherLo = (75, 45, 22);
            (byte r, byte g, byte b) buckle    = (180, 180, 185);
            // Pad fill.
            for (int y = 7; y <= 11; y++)
            for (int x = 3; x <= 12; x++)
                SetPixel(pixels, x, y, leather.r, leather.g, leather.b);
            // Top highlight band along the upper edge of the pad.
            for (int x = 4; x <= 11; x++)
                SetPixel(pixels, x, 7, leatherHi.r, leatherHi.g, leatherHi.b);
            // Bottom shadow band along the lower edge of the pad.
            for (int x = 4; x <= 11; x++)
                SetPixel(pixels, x, 11, leatherLo.r, leatherLo.g, leatherLo.b);
            // Outline ring.
            for (int x = 3; x <= 12; x++)
            {
                SetPixel(pixels, x, 6,  leatherLo.r, leatherLo.g, leatherLo.b);
                SetPixel(pixels, x, 12, leatherLo.r, leatherLo.g, leatherLo.b);
            }
            for (int y = 7; y <= 11; y++)
            {
                SetPixel(pixels, 2,  y, leatherLo.r, leatherLo.g, leatherLo.b);
                SetPixel(pixels, 13, y, leatherLo.r, leatherLo.g, leatherLo.b);
            }
            // Pommel hump — slight raise above the pad's front edge.
            SetPixel(pixels, 7, 5, leatherLo.r, leatherLo.g, leatherLo.b);
            SetPixel(pixels, 8, 5, leatherLo.r, leatherLo.g, leatherLo.b);
            SetPixel(pixels, 7, 6, leatherHi.r, leatherHi.g, leatherHi.b);
            SetPixel(pixels, 8, 6, leatherHi.r, leatherHi.g, leatherHi.b);
            // Buckle pip on the right-side strap.
            SetPixel(pixels, 11, 9, buckle.r, buckle.g, buckle.b);
            SetPixel(pixels, 11, 10, buckle.r, buckle.g, buckle.b);
        }

        // Tier 4 #22 — Compass dial face. 16×16 sprite: light grey
        // outer ring with a darker bezel pip, off-white centre disc,
        // and a small red "N" marker pip at the top of the ring so
        // the dial is unmistakably oriented even with no overlay.
        // Same low-key palette the Alpha sprite used (greyscale dial
        // with a single accent colour). The HUD overlay paints the
        // live direction letter on TOP of this tile at draw time.
        private static void GenerateCompassItem(byte[] pixels)
        {
            (byte r, byte g, byte b) bezel = (95, 95, 100);
            (byte r, byte g, byte b) face  = (220, 220, 215);
            (byte r, byte g, byte b) inner = (245, 240, 230);
            (byte r, byte g, byte b) nMark = (200, 60, 60);
            // Bezel ring (12-px diameter approximated by a rounded
            // square between [3..12] inclusive with corners trimmed).
            for (int y = 3; y <= 12; y++)
            for (int x = 3; x <= 12; x++)
            {
                bool corner = (x == 3 || x == 12) && (y == 3 || y == 12);
                if (corner) continue;
                bool edge = (x == 3 || x == 12 || y == 3 || y == 12);
                if (edge) SetPixel(pixels, x, y, bezel.r, bezel.g, bezel.b);
                else      SetPixel(pixels, x, y, face.r,  face.g,  face.b);
            }
            // Inner highlight disc — slightly brighter centre so the
            // face reads as a polished dial, not a flat disc.
            for (int y = 6; y <= 9; y++)
            for (int x = 6; x <= 9; x++)
                SetPixel(pixels, x, y, inner.r, inner.g, inner.b);
            // Fixed "N" marker pip at the top of the ring — two red
            // pixels so it survives the 1× hotbar scaling without
            // disappearing. The live direction letter from RenderHotbar
            // is drawn larger and centred, so this pip stays visible
            // beneath it as the cardinal-N reference.
            SetPixel(pixels, 7, 4, nMark.r, nMark.g, nMark.b);
            SetPixel(pixels, 8, 4, nMark.r, nMark.g, nMark.b);
        }

        // Tier 4 #18 — Slimeball sprite. Centred 6×6 rounded square
        // (corners trimmed) on transparent so the silhouette reads as
        // a soft sphere. Saturated lime body, bright highlight on the
        // upper-left, darker shadow band on the lower-right. Same
        // shape language as the snowball sprite — different palette
        // distinguishes them at a glance even though both are
        // throwable-sized round items.
        private static void GenerateSlimeballItem(byte[] pixels)
        {
            (byte r, byte g, byte b) slime   = (90, 175, 90);
            (byte r, byte g, byte b) slimeHi = (160, 230, 130);
            (byte r, byte g, byte b) slimeLo = (45, 110, 50);
            for (int y = 5; y <= 10; y++)
            for (int x = 5; x <= 10; x++)
            {
                bool corner = (x == 5 || x == 10) && (y == 5 || y == 10);
                if (corner) continue;
                SetPixel(pixels, x, y, slime.r, slime.g, slime.b);
            }
            // Highlight bloom — bright top-left.
            SetPixel(pixels, 6, 6, slimeHi.r, slimeHi.g, slimeHi.b);
            SetPixel(pixels, 7, 6, slimeHi.r, slimeHi.g, slimeHi.b);
            SetPixel(pixels, 6, 7, slimeHi.r, slimeHi.g, slimeHi.b);
            // Shadow band — bottom-right edge.
            SetPixel(pixels, 9, 10, slimeLo.r, slimeLo.g, slimeLo.b);
            SetPixel(pixels, 10, 9, slimeLo.r, slimeLo.g, slimeLo.b);
            SetPixel(pixels, 10, 10, slimeLo.r, slimeLo.g, slimeLo.b);
        }

        // Tier 4 #15 — Bucket silhouette painter. Iron pail viewed
        // head-on: a trapezoid body (wide rim, narrow base) with a
        // curved handle arching over the top. Silver-grey palette
        // with a darker band along the bottom and a brighter
        // highlight on the upper-left so it reads as a metal vessel
        // and not a flat rectangle. The body's interior is left as
        // a hole so callers can fill it with a contents colour
        // (water/lava/milk) after this routine runs.
        private static void GenerateBucketBody(byte[] pixels)
        {
            (byte r, byte g, byte b) iron     = (170, 175, 180);
            (byte r, byte g, byte b) ironHi   = (215, 220, 225);
            (byte r, byte g, byte b) ironLo   = (90, 95, 100);
            // Handle — single-pixel arch from (4,4) up to (8..7,2)
            // and back down to (11,4). Drawn first so the rim
            // overdraws where they meet (rim wins visually).
            SetPixel(pixels, 4, 4, ironLo.r, ironLo.g, ironLo.b);
            SetPixel(pixels, 5, 3, ironLo.r, ironLo.g, ironLo.b);
            SetPixel(pixels, 6, 2, ironLo.r, ironLo.g, ironLo.b);
            SetPixel(pixels, 7, 2, ironLo.r, ironLo.g, ironLo.b);
            SetPixel(pixels, 8, 2, ironLo.r, ironLo.g, ironLo.b);
            SetPixel(pixels, 9, 2, ironLo.r, ironLo.g, ironLo.b);
            SetPixel(pixels, 10, 3, ironLo.r, ironLo.g, ironLo.b);
            SetPixel(pixels, 11, 4, ironLo.r, ironLo.g, ironLo.b);
            // Rim — top edge of pail, full width.
            for (int x = 3; x <= 12; x++)
                SetPixel(pixels, x, 4, iron.r, iron.g, iron.b);
            // Rim highlight — bright pip on the upper-left so the
            // pail catches a "light from above-left" suggestion.
            SetPixel(pixels, 4, 4, ironHi.r, ironHi.g, ironHi.b);
            SetPixel(pixels, 5, 4, ironHi.r, ironHi.g, ironHi.b);
            // Side walls — trapezoid, narrowing toward the base.
            // Left wall: drops from (3,5) to (5,12); right wall:
            // drops from (12,5) to (10,12).
            for (int y = 5; y <= 12; y++)
            {
                int leftX  = 3 + ((y - 5) / 4); // 3,3,3,3,4,4,4,4
                int rightX = 12 - ((y - 5) / 4);
                SetPixel(pixels, leftX,  y, iron.r, iron.g, iron.b);
                SetPixel(pixels, rightX, y, iron.r, iron.g, iron.b);
            }
            // Base — bottom edge of pail.
            for (int x = 5; x <= 10; x++)
                SetPixel(pixels, x, 12, iron.r, iron.g, iron.b);
            // Base shadow — one row up so the pail reads as a 3D
            // cup, not a flat sheet.
            for (int x = 5; x <= 10; x++)
                SetPixel(pixels, x, 11, ironLo.r, ironLo.g, ironLo.b);
        }

        // Helper — flood-fill the pail's interior with `fill` (BGR
        // order via SetPixel). Bounds match the trapezoid in
        // GenerateBucketBody: rim row 5..11, narrowing-cup interior.
        // Only writes pixels within the inner cup so the silver
        // silhouette stays visible around the contents.
        private static void FillBucketInterior(byte[] pixels, byte fr, byte fg, byte fb)
        {
            for (int y = 5; y <= 11; y++)
            {
                int leftX  = 4 + ((y - 5) / 4);
                int rightX = 11 - ((y - 5) / 4);
                for (int x = leftX; x <= rightX; x++)
                    SetPixel(pixels, x, y, fr, fg, fb);
            }
        }

        // Tier 4 #15 — Empty bucket sprite. Silver pail silhouette
        // with no contents — interior shows transparent pixels (the
        // pail looks "empty"). The body painter alone is enough; no
        // fill call.
        private static void GenerateBucketEmptyItem(byte[] pixels)
        {
            GenerateBucketBody(pixels);
        }

        // Tier 4 #15 — Water bucket sprite. Pail silhouette with a
        // blue contents fill — same blue as the in-world water tile
        // so the player visually links the bucket to the source it
        // was scooped from.
        private static void GenerateBucketWaterItem(byte[] pixels)
        {
            GenerateBucketBody(pixels);
            FillBucketInterior(pixels, 65, 105, 200);
        }

        // Tier 4 #15 — Lava bucket sprite. Pail silhouette with an
        // orange-red contents fill matching the in-world lava tile.
        private static void GenerateBucketLavaItem(byte[] pixels)
        {
            GenerateBucketBody(pixels);
            FillBucketInterior(pixels, 220, 100, 30);
        }

        // Tier 4 #15 — Milk bucket sprite. Pail silhouette with a
        // creamy-white contents fill — lighter than the empty
        // pail's grey rim so the contents read as "filled" not
        // "empty" at a glance.
        private static void GenerateBucketMilkItem(byte[] pixels)
        {
            GenerateBucketBody(pixels);
            FillBucketInterior(pixels, 245, 245, 240);
        }

        // Tier 4 #20 — Snowball sprite. A small white-ish circle on
        // transparent — reads as a packed snow handful at hotbar
        // scale. Slight blue tint on the shadow side anchors the
        // colour against the warmer ingredient items so the player
        // can pick a snowball out of a mixed inventory at a glance
        // (without it the white blob looks like a paper / cloth
        // sprite). Centred body 6×6 with rounded corners; one bright
        // highlight pip and one cool-shadow pip.
        private static void GenerateSnowballItem(byte[] pixels)
        {
            (byte r, byte g, byte b) snow   = (235, 240, 250);
            (byte r, byte g, byte b) snowHi = (255, 255, 255);
            (byte r, byte g, byte b) snowLo = (175, 195, 220);
            // Body — 6×6 rounded square (corners trimmed).
            for (int y = 5; y <= 10; y++)
            for (int x = 5; x <= 10; x++)
            {
                bool corner = (x == 5 || x == 10) && (y == 5 || y == 10);
                if (corner) continue;
                SetPixel(pixels, x, y, snow.r, snow.g, snow.b);
            }
            // Highlight bloom — bright top-left.
            SetPixel(pixels, 6, 6, snowHi.r, snowHi.g, snowHi.b);
            SetPixel(pixels, 7, 6, snowHi.r, snowHi.g, snowHi.b);
            SetPixel(pixels, 6, 7, snowHi.r, snowHi.g, snowHi.b);
            // Cool-shadow band on the bottom-right edge so the
            // snowball reads as a 3D ball, not a flat disc.
            SetPixel(pixels, 9, 10, snowLo.r, snowLo.g, snowLo.b);
            SetPixel(pixels, 10, 9, snowLo.r, snowLo.g, snowLo.b);
            SetPixel(pixels, 10, 10, snowLo.r, snowLo.g, snowLo.b);
        }

        // Wheat seeds item — small green/brown cluster of grain pellets
        // centred in the tile. Reads as a handful of seeds at hotbar
        // scale: a 4×3 dotted oval with two-tone shading.
        private static void GenerateWheatSeedsItem(byte[] pixels)
        {
            (byte r, byte g, byte b) hull = (140, 110, 55);
            (byte r, byte g, byte b) hullHi = (190, 160, 80);
            (byte r, byte g, byte b) hullLo = (95, 70, 35);
            // Pellet positions — six small dots forming a loose pile.
            int[] sx = { 5, 7, 9, 6, 8, 10 };
            int[] sy = { 8, 7, 8, 10, 10, 9 };
            for (int i = 0; i < sx.Length; i++)
            {
                SetPixel(pixels, sx[i], sy[i], hull.r, hull.g, hull.b);
                SetPixel(pixels, sx[i], sy[i] - 1, hullHi.r, hullHi.g, hullHi.b);
                if (sy[i] + 1 < TileSize)
                    SetPixel(pixels, sx[i], sy[i] + 1, hullLo.r, hullLo.g, hullLo.b);
            }
        }

        // Wheat item — bundled stack of golden stalks, the harvested
        // form. Vertical bar with seed-cluster cap and a bright
        // highlight stripe so it reads as a wheat-bundle icon.
        private static void GenerateWheatItem(byte[] pixels)
        {
            (byte r, byte g, byte b) stalk   = (210, 180, 75);
            (byte r, byte g, byte b) stalkHi = (245, 225, 110);
            (byte r, byte g, byte b) leaf    = (180, 145, 50);
            // Three vertical stalks, taller than the field sprite.
            int[] xs = { 6, 8, 10 };
            foreach (int x in xs)
            {
                for (int y = 4; y <= 12; y++)
                    SetPixel(pixels, x, y, stalk.r, stalk.g, stalk.b);
                // Seed cap at top.
                SetPixel(pixels, x, 3, stalkHi.r, stalkHi.g, stalkHi.b);
                if (x - 1 >= 0) SetPixel(pixels, x - 1, 4, leaf.r, leaf.g, leaf.b);
                if (x + 1 < TileSize) SetPixel(pixels, x + 1, 4, leaf.r, leaf.g, leaf.b);
            }
            // Tie-band across the middle for the "bundle" read.
            for (int x = 5; x <= 11; x++)
                SetPixel(pixels, x, 9, leaf.r, leaf.g, leaf.b);
        }

        // Bread loaf — rounded golden-brown rectangle with crust shading.
        // Same general silhouette as the cooked porkchop but smaller
        // and more uniformly coloured so it reads as "loaf" not "meat".
        private static void GenerateBreadItem(byte[] pixels)
        {
            (byte r, byte g, byte b) crust   = (185, 130, 70);
            (byte r, byte g, byte b) crustHi = (220, 165, 95);
            (byte r, byte g, byte b) crustLo = (130, 85, 40);
            (byte r, byte g, byte b) crumb   = (235, 195, 130);
            for (int y = 5; y <= 10; y++)
            for (int x = 3; x <= 12; x++)
            {
                bool edge = (x == 3 || x == 12 || y == 5 || y == 10);
                if (edge) SetPixel(pixels, x, y, crustLo.r, crustLo.g, crustLo.b);
                else      SetPixel(pixels, x, y, crust.r,   crust.g,   crust.b);
            }
            // Bright crust top.
            for (int x = 4; x <= 11; x++)
                SetPixel(pixels, x, 6, crustHi.r, crustHi.g, crustHi.b);
            // Crumb highlight stripe through the middle.
            for (int x = 5; x <= 10; x++)
                SetPixel(pixels, x, 8, crumb.r, crumb.g, crumb.b);
        }

        // Mushroom stew — wooden bowl with a brown stew level and tiny
        // mushroom cap garnish. Reuses the bowl silhouette from
        // GenerateBowlItem (rim + curved sides) but fills the interior
        // with a stewed-mushroom palette instead of the empty hollow.
        private static void GenerateMushroomStewItem(byte[] pixels)
        {
            (byte r, byte g, byte b) wood   = (158, 113, 60);
            (byte r, byte g, byte b) woodHi = (190, 145, 85);
            (byte r, byte g, byte b) woodLo = (110, 75, 35);
            (byte r, byte g, byte b) stew   = (135, 90, 55);
            (byte r, byte g, byte b) stewHi = (175, 130, 90);
            (byte r, byte g, byte b) capRed = (200, 60, 55);
            (byte r, byte g, byte b) capWht = (235, 225, 200);
            // Rim — top of the bowl, two pixels tall, full width.
            for (int x = 3; x <= 12; x++)
            {
                SetPixel(pixels, x, 7, woodHi.r, woodHi.g, woodHi.b);
                SetPixel(pixels, x, 8, wood.r,   wood.g,   wood.b);
            }
            // Stew interior — replaces the hollow inset with a brown stew level.
            for (int x = 4; x <= 11; x++)
                SetPixel(pixels, x, 8, stew.r, stew.g, stew.b);
            // Stew highlight ripple.
            for (int x = 5; x <= 10; x++)
                SetPixel(pixels, x, 7, stewHi.r, stewHi.g, stewHi.b);
            // Mushroom cap garnish — a tiny red dot with white spot
            // sitting on the stew surface, upper-left of centre.
            SetPixel(pixels, 6, 7, capRed.r, capRed.g, capRed.b);
            SetPixel(pixels, 7, 7, capWht.r, capWht.g, capWht.b);
            // Curved sides — sloped inward as we go down.
            int[] leftEdge  = { 4, 5, 6 };
            int[] rightEdge = { 11, 10, 9 };
            int[] yRows     = { 9, 10, 11 };
            for (int i = 0; i < yRows.Length; i++)
            {
                int y = yRows[i];
                int xL = leftEdge[i];
                int xR = rightEdge[i];
                SetPixel(pixels, xL, y, woodLo.r, woodLo.g, woodLo.b);
                SetPixel(pixels, xR, y, woodLo.r, woodLo.g, woodLo.b);
                for (int x = xL + 1; x < xR; x++)
                    SetPixel(pixels, x, y, wood.r, wood.g, wood.b);
            }
        }

        // Local helper mirroring UploadLayer (which is private elsewhere
        // in this file) but scoped to the item slice. Clears the layer
        // buffer first so item generators can assume a fully transparent
        // background and only paint pixels they care about — matches the
        // tool generator convention.
        private static void UploadItem(byte[] layerPixels, int layer, Action<byte[]> generator)
        {
            Array.Clear(layerPixels, 0, layerPixels.Length);
            generator(layerPixels);
            GL.TexSubImage3D(
                TextureTarget.Texture2DArray, 0,
                0, 0, layer,
                TileSize, TileSize, 1,
                PixelFormat.Rgba, PixelType.UnsignedByte, layerPixels);
        }

        // Stick — a wooden bar running diagonally from lower-left to
        // upper-right, mirroring the tool-handle silhouette so a stick
        // reads as "the handle without the head". 1 px core with hi/lo
        // tracks for chunky 3D feel.
        private static void GenerateStickItem(byte[] pixels)
        {
            (byte r, byte g, byte b) wood   = (138, 95, 50);
            (byte r, byte g, byte b) woodHi = (172, 125, 70);
            (byte r, byte g, byte b) woodLo = (95, 60, 30);
            int[] xs = { 4, 5, 6, 7, 8, 9, 10, 11 };
            int[] ys = { 12, 11, 10, 9, 8, 7, 6, 5 };
            for (int i = 0; i < xs.Length; i++)
            {
                SetPixel(pixels, xs[i], ys[i], wood.r, wood.g, wood.b);
                if (xs[i] - 1 >= 0)
                    SetPixel(pixels, xs[i] - 1, ys[i], woodHi.r, woodHi.g, woodHi.b);
                if (ys[i] + 1 < TileSize)
                    SetPixel(pixels, xs[i], ys[i] + 1, woodLo.r, woodLo.g, woodLo.b);
            }
            // End caps — slightly darker pommel + tip so the stick's
            // ends read as terminations rather than running off-tile.
            SetPixel(pixels, 3, 13, woodLo.r, woodLo.g, woodLo.b);
            SetPixel(pixels, 12, 4, woodLo.r, woodLo.g, woodLo.b);
        }

        // Coal — irregular black lump centred in the tile, 4-shade
        // jittered fill so it reads as a chunky charcoal blob rather
        // than a solid black square.
        private static void GenerateCoalItem(byte[] pixels)
        {
            var rng = new Random(0xC0A1);
            // Lump silhouette — hand-drawn approximation of a roundish
            // chunk with a few flat planes. Pixels marked 1 are interior.
            int[,] mask = new int[TileSize, TileSize];
            int[] rows = { 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0 };
            int[] xMin = { 0, 0, 0, 4, 3, 3, 2, 2, 3, 3, 4, 4, 5, 0, 0, 0 };
            int[] xMax = { 0, 0, 0, 11, 12, 13, 13, 13, 13, 12, 12, 11, 10, 0, 0, 0 };
            for (int y = 0; y < TileSize; y++)
            {
                if (rows[y] == 0) continue;
                for (int x = xMin[y]; x <= xMax[y]; x++) mask[x, y] = 1;
            }
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
            {
                if (mask[x, y] == 0) continue;
                // Rim pixels (mask edges) get the darkest shade, body
                // pixels jitter between 3 mid-greys so the lump looks
                // textured instead of flat. 1-in-7 sparkle pixels add
                // a faint highlight, evoking facet glints.
                bool rim = mask[Math.Max(0, x - 1), y] == 0
                        || mask[Math.Min(TileSize - 1, x + 1), y] == 0
                        || mask[x, Math.Max(0, y - 1)] == 0
                        || mask[x, Math.Min(TileSize - 1, y + 1)] == 0;
                byte v;
                if (rim) v = 18;
                else if (rng.Next(7) == 0) v = 90;
                else v = (byte)(40 + rng.Next(20));
                SetPixel(pixels, x, y, v, v, v);
            }
        }

        // Iron ingot — small pale-silver loaf, two-row band shape with
        // a brighter top edge for 3D. Same general silhouette as a
        // bullion bar — narrower at the top, wider at the bottom.
        private static void GenerateIronIngotItem(byte[] pixels)
        {
            DrawIngot(pixels, baseR: 215, baseG: 215, baseB: 222,
                              hiR:   245, hiG:   245, hiB:   250,
                              loR:   145, loG:   145, loB:   158, seed: 0x1B07);
        }

        // Gold ingot — same silhouette as iron, yellow palette.
        private static void GenerateGoldIngotItem(byte[] pixels)
        {
            DrawIngot(pixels, baseR: 240, baseG: 205, baseB: 60,
                              hiR:   255, hiG:   235, hiB:   110,
                              loR:   175, loG:   140, loB:   25, seed: 0x6011);
        }

        // Shared ingot drawer — trapezoidal bar centred in the tile,
        // 3-shade banding. Top row sits at y=6 (narrow), middle rows
        // y=7..9 (full body), bottom row y=10 (slightly narrower than
        // body so the shape tapers, suggesting a moulded ingot).
        private static void DrawIngot(byte[] pixels,
            byte baseR, byte baseG, byte baseB,
            byte hiR,   byte hiG,   byte hiB,
            byte loR,   byte loG,   byte loB,
            int seed)
        {
            var rng = new Random(seed);
            // Top edge — narrow + bright.
            for (int x = 5; x <= 10; x++) SetPixel(pixels, x, 6, hiR, hiG, hiB);
            // Body rows.
            for (int x = 4; x <= 11; x++) SetJittered(pixels, x, 7, baseR, baseG, baseB, 6, rng);
            for (int x = 4; x <= 11; x++) SetJittered(pixels, x, 8, baseR, baseG, baseB, 6, rng);
            for (int x = 4; x <= 11; x++) SetJittered(pixels, x, 9, baseR, baseG, baseB, 6, rng);
            // Bottom edge — narrow + dark for the moulded look.
            for (int x = 5; x <= 10; x++) SetPixel(pixels, x, 10, loR, loG, loB);
            // Side ticks — a single dark pixel at each corner to round
            // the silhouette and sell the trapezoid shape.
            SetPixel(pixels, 4, 7, loR, loG, loB);
            SetPixel(pixels, 11, 9, loR, loG, loB);
        }

        // Diamond — small cyan rhombus with a bright facet on the
        // upper-right. 4 pixels tall, 5 pixels wide, kite-shaped to
        // suggest a cut gem.
        private static void GenerateDiamondItem(byte[] pixels)
        {
            (byte r, byte g, byte b) baseC = (90, 220, 230);
            (byte r, byte g, byte b) hiC   = (190, 255, 255);
            (byte r, byte g, byte b) loC   = (40, 160, 180);

            // Top tip
            SetPixel(pixels, 8, 5, hiC.r, hiC.g, hiC.b);
            // Row 6: 3 wide
            SetPixel(pixels, 7, 6, baseC.r, baseC.g, baseC.b);
            SetPixel(pixels, 8, 6, hiC.r,   hiC.g,   hiC.b);
            SetPixel(pixels, 9, 6, baseC.r, baseC.g, baseC.b);
            // Row 7: 5 wide — widest band
            SetPixel(pixels, 6, 7, baseC.r, baseC.g, baseC.b);
            SetPixel(pixels, 7, 7, hiC.r,   hiC.g,   hiC.b);
            SetPixel(pixels, 8, 7, baseC.r, baseC.g, baseC.b);
            SetPixel(pixels, 9, 7, baseC.r, baseC.g, baseC.b);
            SetPixel(pixels, 10,7, loC.r,   loC.g,   loC.b);
            // Row 8: 3 wide
            SetPixel(pixels, 7, 8, baseC.r, baseC.g, baseC.b);
            SetPixel(pixels, 8, 8, baseC.r, baseC.g, baseC.b);
            SetPixel(pixels, 9, 8, loC.r,   loC.g,   loC.b);
            // Row 9: bottom tip
            SetPixel(pixels, 8, 9, loC.r, loC.g, loC.b);
        }

        // Flint — angular dark-grey shard. Asymmetric polygon to read
        // as a knapped flake rather than a regular shape.
        private static void GenerateFlintItem(byte[] pixels)
        {
            var rng = new Random(0xF11A);
            (byte r, byte g, byte b) baseC = (75, 75, 80);
            (byte r, byte g, byte b) hiC   = (130, 130, 135);
            (byte r, byte g, byte b) loC   = (40, 40, 45);

            // Hand-painted shard outline. y0 is top, the shape leans
            // right and tapers toward the bottom.
            int[][] rows = new int[][]
            {
                new[] { 7, 8 },                  // y=4
                new[] { 6, 7, 8, 9 },            // y=5
                new[] { 5, 6, 7, 8, 9, 10 },     // y=6
                new[] { 4, 5, 6, 7, 8, 9, 10 },  // y=7
                new[] { 4, 5, 6, 7, 8, 9, 10, 11 }, // y=8
                new[] { 5, 6, 7, 8, 9, 10 },     // y=9
                new[] { 6, 7, 8, 9 },            // y=10
                new[] { 7, 8 },                  // y=11
            };
            for (int i = 0; i < rows.Length; i++)
            {
                int y = 4 + i;
                foreach (int x in rows[i])
                {
                    // Top-left edge highlights, bottom-right edge dark.
                    bool isTopLeft = (x == rows[i][0]) || (i < 2);
                    bool isBottomRight = (x == rows[i][rows[i].Length - 1]) || (i >= rows.Length - 2);
                    if (isTopLeft && !isBottomRight)
                        SetPixel(pixels, x, y, hiC.r, hiC.g, hiC.b);
                    else if (isBottomRight)
                        SetPixel(pixels, x, y, loC.r, loC.g, loC.b);
                    else
                        SetJittered(pixels, x, y, baseC.r, baseC.g, baseC.b, 8, rng);
                }
            }
        }

        // Clay ball — round pale-grey ball with shading. Roughly a 6×6
        // disc anchored centre-tile with a brighter top-left highlight
        // and a darker bottom-right shadow.
        private static void GenerateClayBallItem(byte[] pixels)
        {
            (byte r, byte g, byte b) baseC = (185, 195, 210);
            (byte r, byte g, byte b) hiC   = (220, 225, 235);
            (byte r, byte g, byte b) loC   = (130, 145, 160);

            // 6×6 disc with corners cut.
            int cx = 8, cy = 8, r = 3;
            for (int y = cy - r; y <= cy + r; y++)
            for (int x = cx - r; x <= cx + r; x++)
            {
                int dx = x - cx, dy = y - cy;
                if (dx * dx + dy * dy > r * r + 1) continue;
                // Highlight on upper-left, shadow on lower-right.
                if (dx + dy <= -2)
                    SetPixel(pixels, x, y, hiC.r, hiC.g, hiC.b);
                else if (dx + dy >= 3)
                    SetPixel(pixels, x, y, loC.r, loC.g, loC.b);
                else
                    SetPixel(pixels, x, y, baseC.r, baseC.g, baseC.b);
            }
        }

        // Clay brick — small reddish-orange rectangle with mortar-grey
        // edges. Same hue as the Bricks block tile so the ingredient
        // visually maps to its smelt-input source.
        private static void GenerateClayBrickItem(byte[] pixels)
        {
            (byte r, byte g, byte b) baseC = (180, 100, 75);
            (byte r, byte g, byte b) hiC   = (210, 130, 100);
            (byte r, byte g, byte b) loC   = (130, 70, 50);
            var rng = new Random(0xB71D);
            // Rectangle 8 wide × 4 tall, centred.
            for (int y = 6; y <= 9; y++)
            for (int x = 4; x <= 11; x++)
            {
                bool top    = (y == 6);
                bool bottom = (y == 9);
                bool left   = (x == 4);
                bool right  = (x == 11);
                if (top || left)
                    SetPixel(pixels, x, y, hiC.r, hiC.g, hiC.b);
                else if (bottom || right)
                    SetPixel(pixels, x, y, loC.r, loC.g, loC.b);
                else
                    SetJittered(pixels, x, y, baseC.r, baseC.g, baseC.b, 10, rng);
            }
        }

        // Bowl — wooden half-circle with a darker rim. Rendered as an
        // open-topped vessel: rim along the top, sloped sides, flat
        // bottom. Reads as "bowl" at hotbar size despite the small
        // canvas.
        private static void GenerateBowlItem(byte[] pixels)
        {
            (byte r, byte g, byte b) wood   = (158, 113, 60);
            (byte r, byte g, byte b) woodHi = (190, 145, 85);
            (byte r, byte g, byte b) woodLo = (110, 75, 35);
            (byte r, byte g, byte b) inside = (90, 60, 28);

            // Rim — top of the bowl, two pixels tall, full width.
            for (int x = 3; x <= 12; x++)
            {
                SetPixel(pixels, x, 7, woodHi.r, woodHi.g, woodHi.b);
                SetPixel(pixels, x, 8, wood.r,   wood.g,   wood.b);
            }
            // Hollow interior — darker brown, slight inset.
            for (int x = 4; x <= 11; x++) SetPixel(pixels, x, 8, inside.r, inside.g, inside.b);
            // Curved sides — sloped inward as we go down.
            int[] leftEdge  = { 4, 5, 6 };
            int[] rightEdge = { 11, 10, 9 };
            int[] yRows     = { 9, 10, 11 };
            for (int i = 0; i < yRows.Length; i++)
            {
                int y = yRows[i];
                int xL = leftEdge[i];
                int xR = rightEdge[i];
                SetPixel(pixels, xL, y, woodLo.r, woodLo.g, woodLo.b);
                SetPixel(pixels, xR, y, woodLo.r, woodLo.g, woodLo.b);
                for (int x = xL + 1; x < xR; x++)
                    SetPixel(pixels, x, y, wood.r, wood.g, wood.b);
            }
            // Bottom highlight — single bright pixel along the inner
            // bottom rim so the bowl reads as concave.
            SetPixel(pixels, 7, 11, woodHi.r, woodHi.g, woodHi.b);
            SetPixel(pixels, 8, 11, woodHi.r, woodHi.g, woodHi.b);
        }

        // Raw porkchop — pink slab of meat with a darker inset and a
        // light-cream marbling streak. Sits in a 12×7 area centred on
        // the tile so it reads as a chunky cut at hotbar scale.
        private static void GenerateRawPorkchopItem(byte[] pixels)
        {
            (byte r, byte g, byte b) skin    = (235, 145, 145);
            (byte r, byte g, byte b) skinHi  = (250, 175, 175);
            (byte r, byte g, byte b) skinLo  = (180, 95, 105);
            (byte r, byte g, byte b) marbling = (245, 220, 200);
            for (int y = 5; y <= 11; y++)
            for (int x = 2; x <= 13; x++)
            {
                bool edge = (x == 2 || x == 13 || y == 5 || y == 11);
                if (edge) SetPixel(pixels, x, y, skinLo.r, skinLo.g, skinLo.b);
                else      SetPixel(pixels, x, y, skin.r,   skin.g,   skin.b);
            }
            // Highlight bar across the upper inside.
            for (int x = 4; x <= 11; x++)
                SetPixel(pixels, x, 6, skinHi.r, skinHi.g, skinHi.b);
            // Marbling streak — one bright cream line across the middle.
            for (int x = 5; x <= 10; x++)
                SetPixel(pixels, x, 8, marbling.r, marbling.g, marbling.b);
        }

        // Cooked porkchop — the same silhouette, but darker / browner
        // (cooked exterior) with a tan interior so the cooked vs. raw
        // contrast reads even at small scale. Crisper edge ring.
        private static void GenerateCookedPorkchopItem(byte[] pixels)
        {
            (byte r, byte g, byte b) crust   = (130, 78, 38);
            (byte r, byte g, byte b) crustHi = (170, 105, 55);
            (byte r, byte g, byte b) crustLo = (78, 42, 18);
            (byte r, byte g, byte b) inside  = (200, 140, 90);
            (byte r, byte g, byte b) insideHi = (225, 175, 125);
            for (int y = 5; y <= 11; y++)
            for (int x = 2; x <= 13; x++)
            {
                bool edge = (x == 2 || x == 13 || y == 5 || y == 11);
                if (edge) SetPixel(pixels, x, y, crustLo.r, crustLo.g, crustLo.b);
                else      SetPixel(pixels, x, y, crust.r,   crust.g,   crust.b);
            }
            // Tan interior fill.
            for (int y = 7; y <= 9; y++)
            for (int x = 4; x <= 11; x++)
                SetPixel(pixels, x, y, inside.r, inside.g, inside.b);
            // Lighter highlight across the top of the interior.
            for (int x = 5; x <= 10; x++)
                SetPixel(pixels, x, 7, insideHi.r, insideHi.g, insideHi.b);
            // Crisp top ridge.
            for (int x = 4; x <= 11; x++)
                SetPixel(pixels, x, 6, crustHi.r, crustHi.g, crustHi.b);
        }

        // Bow item — tan curved limb on a bottom-left → top-right
        // diagonal with a string running between the limb tips. Reads
        // as a drawn longbow at hotbar scale.
        private static void GenerateBowItem(byte[] pixels)
        {
            (byte r, byte g, byte b) wood    = (140, 90, 40);
            (byte r, byte g, byte b) woodHi  = (180, 130, 70);
            (byte r, byte g, byte b) woodLo  = (90, 55, 22);
            (byte r, byte g, byte b) str     = (235, 230, 215);
            // Limb arc — a 9-pixel curve along a /-diagonal, fattened
            // in the middle so it doesn't look like a stick.
            int[] limbX = { 11, 10,  9,  8,  7,  6,  5,  4,  3 };
            int[] limbY = {  3,  3,  4,  5,  6,  7,  8,  9,  9 };
            for (int i = 0; i < limbX.Length; i++)
            {
                int x = limbX[i], y = limbY[i];
                SetPixel(pixels, x,     y,     wood.r,   wood.g,   wood.b);
                SetPixel(pixels, x - 1, y,     woodHi.r, woodHi.g, woodHi.b);
                SetPixel(pixels, x,     y + 1, woodLo.r, woodLo.g, woodLo.b);
            }
            // Bowstring — a straight diagonal line from upper limb tip
            // to lower limb tip, one pixel inside the limb.
            int[] strX = { 11, 10,  9,  8,  7,  6,  5,  4,  3 };
            int[] strY = {  4,  5,  6,  7,  8,  9, 10, 10, 10 };
            for (int i = 0; i < strX.Length; i++)
                SetPixel(pixels, strX[i], strY[i], str.r, str.g, str.b);
        }

        // Arrow item — wood shaft on a /-diagonal, grey iron point at
        // the upper-right tip, and a 3-feather fletch at the lower-left
        // tail. The fletch reads as 2 stacked white pixels offset slightly.
        private static void GenerateArrowItem(byte[] pixels)
        {
            (byte r, byte g, byte b) shaft   = (160, 110, 60);
            (byte r, byte g, byte b) shaftHi = (200, 155, 100);
            (byte r, byte g, byte b) point   = (190, 195, 200);
            (byte r, byte g, byte b) pointHi = (235, 240, 245);
            (byte r, byte g, byte b) fletch  = (240, 240, 240);
            (byte r, byte g, byte b) fletchLo = (180, 180, 180);
            // Diagonal shaft.
            for (int i = 0; i < 8; i++)
            {
                int x = 4 + i, y = 11 - i;
                SetPixel(pixels, x, y, shaft.r, shaft.g, shaft.b);
                if (i > 0 && i < 7)
                    SetPixel(pixels, x, y - 1, shaftHi.r, shaftHi.g, shaftHi.b);
            }
            // Iron arrowhead — a 3-pixel triangle off the upper-right
            // shaft tip.
            SetPixel(pixels, 12, 3, point.r,   point.g,   point.b);
            SetPixel(pixels, 13, 3, pointHi.r, pointHi.g, pointHi.b);
            SetPixel(pixels, 12, 2, point.r,   point.g,   point.b);
            SetPixel(pixels, 13, 2, point.r,   point.g,   point.b);
            SetPixel(pixels, 13, 4, point.r,   point.g,   point.b);
            // Fletch — three feather pixels at the lower-left tail.
            SetPixel(pixels, 3, 12, fletch.r,   fletch.g,   fletch.b);
            SetPixel(pixels, 2, 12, fletchLo.r, fletchLo.g, fletchLo.b);
            SetPixel(pixels, 3, 13, fletchLo.r, fletchLo.g, fletchLo.b);
            SetPixel(pixels, 2, 13, fletch.r,   fletch.g,   fletch.b);
        }

        // String item — a tangled cream-coloured loop. Drawn as an
        // ellipse-ish ring with a cross-knot in the middle so it doesn't
        // look like an empty O at small scale.
        private static void GenerateStringItem(byte[] pixels)
        {
            (byte r, byte g, byte b) str   = (240, 235, 220);
            (byte r, byte g, byte b) strLo = (185, 180, 165);
            // Ring — top + bottom + sides.
            int[] ringX = { 6, 7, 8, 9, 5, 10, 4, 11, 4, 11, 5, 10, 6, 7, 8, 9 };
            int[] ringY = { 3, 3, 3, 3, 4, 4,  6, 6,  9, 9, 11, 11, 12, 12, 12, 12 };
            for (int i = 0; i < ringX.Length; i++)
                SetPixel(pixels, ringX[i], ringY[i], str.r, str.g, str.b);
            // Inner shading ring.
            int[] inX = { 5, 6, 9, 10, 4, 11, 5, 10 };
            int[] inY = { 5, 5, 5,  5, 7,  8, 11, 11 };
            for (int i = 0; i < inX.Length; i++)
                SetPixel(pixels, inX[i], inY[i], strLo.r, strLo.g, strLo.b);
            // Cross-knot in the middle so the loop reads as tangled.
            SetPixel(pixels, 7, 7, str.r, str.g, str.b);
            SetPixel(pixels, 8, 8, str.r, str.g, str.b);
            SetPixel(pixels, 7, 8, strLo.r, strLo.g, strLo.b);
            SetPixel(pixels, 8, 7, strLo.r, strLo.g, strLo.b);
        }

        // Gunpowder item — a dark grey-black mound with bright sparkle
        // pixels scattered through it (Alpha gunpowder is a coarse pile
        // with a few highlight grains). Sits low in the tile like a heap.
        private static void GenerateGunpowderItem(byte[] pixels)
        {
            (byte r, byte g, byte b) ash    = (55, 55, 60);
            (byte r, byte g, byte b) ashHi  = (95, 95, 100);
            (byte r, byte g, byte b) ashLo  = (28, 28, 32);
            (byte r, byte g, byte b) spark  = (220, 215, 195);
            // Heap silhouette — wider at the bottom, tapering up.
            // Row 11 (bottom): wide.
            for (int x = 3; x <= 12; x++) SetPixel(pixels, x, 11, ashLo.r, ashLo.g, ashLo.b);
            // Row 10: still wide, slight curve.
            for (int x = 3; x <= 12; x++) SetPixel(pixels, x, 10, ash.r, ash.g, ash.b);
            // Row 9: pull in.
            for (int x = 4; x <= 11; x++) SetPixel(pixels, x, 9, ash.r, ash.g, ash.b);
            // Row 8: narrower.
            for (int x = 5; x <= 10; x++) SetPixel(pixels, x, 8, ash.r, ash.g, ash.b);
            // Row 7: tip.
            for (int x = 6; x <= 9; x++)  SetPixel(pixels, x, 7, ashHi.r, ashHi.g, ashHi.b);
            // Row 6: cap.
            SetPixel(pixels, 7, 6, ashHi.r, ashHi.g, ashHi.b);
            SetPixel(pixels, 8, 6, ashHi.r, ashHi.g, ashHi.b);
            // Sparkle grains — a few bright pixels scattered through.
            SetPixel(pixels, 5, 9,  spark.r, spark.g, spark.b);
            SetPixel(pixels, 9, 10, spark.r, spark.g, spark.b);
            SetPixel(pixels, 7, 8,  spark.r, spark.g, spark.b);
            SetPixel(pixels, 11, 11, spark.r, spark.g, spark.b);
        }

        // Leather item — a tan rectangle with stitching pattern, reading
        // as a flat folded hide. Drops from cows. Mid-tile centred so it
        // looks like a held item rather than ground litter.
        private static void GenerateLeatherItem(byte[] pixels)
        {
            (byte r, byte g, byte b) tan    = (170, 110,  72);
            (byte r, byte g, byte b) tanHi  = (200, 142,  92);
            (byte r, byte g, byte b) tanLo  = (122,  80,  52);
            (byte r, byte g, byte b) stitch = ( 90,  62,  40);
            // 9×8 hide rectangle, centred ~(7,8).
            for (int y = 4; y <= 11; y++)
            for (int x = 4; x <= 12; x++)
                SetPixel(pixels, x, y, tan.r, tan.g, tan.b);
            // Top-edge highlight.
            for (int x = 4; x <= 12; x++) SetPixel(pixels, x, 4, tanHi.r, tanHi.g, tanHi.b);
            // Bottom-edge shadow.
            for (int x = 4; x <= 12; x++) SetPixel(pixels, x, 11, tanLo.r, tanLo.g, tanLo.b);
            // Stitched border — left/right edges + dashed inner row.
            for (int y = 5; y <= 10; y++)
            {
                SetPixel(pixels, 4, y, tanLo.r, tanLo.g, tanLo.b);
                SetPixel(pixels, 12, y, tanLo.r, tanLo.g, tanLo.b);
            }
            // Two horizontal dashed stitch rows so it reads "tanned".
            int[] stitchX = { 5, 7, 9, 11 };
            foreach (var sx in stitchX)
            {
                SetPixel(pixels, sx, 6, stitch.r, stitch.g, stitch.b);
                SetPixel(pixels, sx, 9, stitch.r, stitch.g, stitch.b);
            }
        }

        // Feather item — diagonal quill with cream-coloured barbs and a
        // dark central rachis. Drops from chickens.
        private static void GenerateFeatherItem(byte[] pixels)
        {
            (byte r, byte g, byte b) barb    = (245, 245, 230);
            (byte r, byte g, byte b) barbLo  = (200, 200, 180);
            (byte r, byte g, byte b) rachis  = (160, 130, 100);
            (byte r, byte g, byte b) shadow  = ( 90,  78,  60);
            // Diagonal rachis from top-right to bottom-left, 9 cells long.
            int[] rx = { 11, 10, 9, 8, 7, 6, 5, 4, 3 };
            int[] ry = {  3,  4, 5, 6, 7, 8, 9, 10, 11 };
            for (int i = 0; i < rx.Length; i++)
                SetPixel(pixels, rx[i], ry[i], rachis.r, rachis.g, rachis.b);
            // Barbs flair off both sides of the rachis. Upper side =
            // brighter (catches the light), lower side = shaded.
            for (int i = 1; i < rx.Length - 1; i++)
            {
                int x = rx[i], y = ry[i];
                if (x + 1 < 16) SetPixel(pixels, x + 1, y - 1, barb.r, barb.g, barb.b);
                if (y + 1 < 16) SetPixel(pixels, x - 1, y + 1, barbLo.r, barbLo.g, barbLo.b);
            }
            // Wider tip near the top of the feather.
            SetPixel(pixels, 12, 2, barb.r, barb.g, barb.b);
            SetPixel(pixels, 11, 2, barb.r, barb.g, barb.b);
            SetPixel(pixels, 10, 3, barb.r, barb.g, barb.b);
            // Quill base shadow at the bottom-left.
            SetPixel(pixels, 2, 12, shadow.r, shadow.g, shadow.b);
            SetPixel(pixels, 3, 12, shadow.r, shadow.g, shadow.b);
        }

        // Egg item — egg-shaped oval with cream colour and a soft
        // highlight. Laid by chickens; in this era it just sits in the
        // inventory (throwing comes with Tier 4 #20).
        private static void GenerateEggItem(byte[] pixels)
        {
            (byte r, byte g, byte b) shell    = (240, 232, 208);
            (byte r, byte g, byte b) shellHi  = (255, 250, 235);
            (byte r, byte g, byte b) shellLo  = (195, 185, 160);
            (byte r, byte g, byte b) shellBot = (165, 155, 130);
            // Oval — taller than wide, top narrower than bottom.
            // Row 4: tip (2 pixels).
            SetPixel(pixels, 7, 4, shell.r, shell.g, shell.b);
            SetPixel(pixels, 8, 4, shell.r, shell.g, shell.b);
            // Row 5: 4 wide.
            for (int x = 6; x <= 9; x++) SetPixel(pixels, x, 5, shell.r, shell.g, shell.b);
            // Row 6: 6 wide.
            for (int x = 5; x <= 10; x++) SetPixel(pixels, x, 6, shell.r, shell.g, shell.b);
            // Rows 7..10: 6 wide, full body.
            for (int y = 7; y <= 10; y++)
            for (int x = 5; x <= 10; x++)
                SetPixel(pixels, x, y, shell.r, shell.g, shell.b);
            // Row 11: 6 wide, base.
            for (int x = 5; x <= 10; x++) SetPixel(pixels, x, 11, shellLo.r, shellLo.g, shellLo.b);
            // Row 12: 4 wide, rounded bottom.
            for (int x = 6; x <= 9; x++) SetPixel(pixels, x, 12, shellBot.r, shellBot.g, shellBot.b);
            // Highlight on the upper-left so it reads as a sphere.
            SetPixel(pixels, 6, 6, shellHi.r, shellHi.g, shellHi.b);
            SetPixel(pixels, 6, 7, shellHi.r, shellHi.g, shellHi.b);
            SetPixel(pixels, 7, 6, shellHi.r, shellHi.g, shellHi.b);
            // Bottom-right shading.
            SetPixel(pixels, 9, 10, shellLo.r, shellLo.g, shellLo.b);
            SetPixel(pixels, 10, 10, shellLo.r, shellLo.g, shellLo.b);
        }

        // Tile coordinates in Alpha 1.1.2_01's terrain.png. Format is
        // (col, row), each cell 16×16 pixels in a 16×16 grid (256×256
        // total). The embedded PNG is sliced once at atlas-build time
        // and uploaded layer-per-layer into the same Texture2DArray
        // shape as the procedural atlas, so the rest of the renderer
        // doesn't need to know which one is active.
        //
        // Coordinates picked to match the canonical Alpha layout. A few
        // procedural tiles don't have a perfect 1:1 in vanilla Alpha
        // (clay, redstone ore, tall grass) — those use the closest
        // semantic match from the same era.
        // Tool tile coords reference alpha_tools.png (NOT terrain.png).
        // Layers 38..57 use the parallel ToolLayer flag below; CopyTile
        // is called against a different source buffer for those layers
        // when the alpha-textures path is active. Procedural mode has no
        // tool art and just leaves those layers blank/magenta — the
        // tools-PNG decode runs unconditionally because the procedural
        // atlas still wants the same total layer count to keep the
        // shader's sampler bindings stable.
        private static readonly (int col, int row)[] AlphaTileCoords = new (int, int)[LayerCount]
        {
            /* TileGrassTop          */ (0, 0),
            /* TileGrassSide         */ (3, 0),
            /* TileDirt              */ (2, 0),
            /* TileStone             */ (1, 0),
            /* TileSand              */ (2, 1),
            /* TileCobblestone       */ (0, 1),
            /* TileBedrock           */ (1, 1),
            /* TileGravel            */ (3, 1),
            /* TileClay              */ (8, 4),
            /* TileCoalOre           */ (2, 2),
            /* TileIronOre           */ (1, 2),
            /* TileGoldOre           */ (0, 2),
            /* TileDiamondOre        */ (2, 3),
            /* TileRedstoneOre       */ (3, 3),
            /* TileLogTop            */ (5, 1),
            /* TileLogSide           */ (4, 1),
            /* TilePlanks            */ (4, 0),
            /* TileLeaves            */ (4, 3),
            /* TileWater             */ (15, 13),
            /* TileLava              */ (15, 15),
            /* TileGoldBlock         */ (7, 1),
            /* TileIronBlock         */ (6, 1),
            /* TileDiamondBlock      */ (8, 1),
            /* TileBricks            */ (7, 0),
            /* TileTntTop            */ (9, 0),
            /* TileTntBottom         */ (10, 0),
            /* TileTntSide           */ (8, 0),
            /* TileBookshelfSide     */ (3, 2),
            /* TileMossyCobblestone  */ (4, 2),
            /* TileObsidian          */ (5, 2),
            /* TileSponge            */ (0, 3),
            /* TileGlass             */ (1, 3),
            /* TileWool              */ (0, 4),
            /* TileTorch             */ (0, 5),
            /* TileDandelion         */ (13, 0),
            /* TileRose              */ (12, 0),
            /* TileBrownMushroom     */ (13, 1),
            /* TileRedMushroom       */ (12, 1),

            // Tools — coordinates into alpha_tools.png. Standard Alpha
            // items.png layout: row 4 = swords, row 5 = shovels, row 6
            // = pickaxes, row 7 = axes; cols 0..4 = wood, stone, iron,
            // diamond, gold (left-to-right material order).
            /* TileWoodSword         */ (0, 4),
            /* TileStoneSword        */ (1, 4),
            /* TileIronSword         */ (2, 4),
            /* TileDiamondSword      */ (3, 4),
            /* TileGoldSword         */ (4, 4),
            /* TileWoodShovel        */ (0, 5),
            /* TileStoneShovel       */ (1, 5),
            /* TileIronShovel        */ (2, 5),
            /* TileDiamondShovel     */ (3, 5),
            /* TileGoldShovel        */ (4, 5),
            /* TileWoodPickaxe       */ (0, 6),
            /* TileStonePickaxe      */ (1, 6),
            /* TileIronPickaxe       */ (2, 6),
            /* TileDiamondPickaxe    */ (3, 6),
            /* TileGoldPickaxe       */ (4, 6),
            /* TileWoodAxe           */ (0, 7),
            /* TileStoneAxe          */ (1, 7),
            /* TileIronAxe           */ (2, 7),
            /* TileDiamondAxe        */ (3, 7),
            /* TileGoldAxe           */ (4, 7),

            // Items — coordinates into alpha_tools.png (the same 256×256
            // sheet that holds the tool icons). Alpha 1.1.2 packs ALL
            // craftable items into a single PNG; the columns past col 4
            // (which holds the gold-tier tools) house the ingredient
            // sprites. Verified against the embedded sheet: e.g. Coal
            // at (7,0) reads pure black, Diamond at (7,3) reads cyan,
            // GoldIngot at (7,2) reads yellow.
            /* TileStick             */ (5, 3),
            /* TileCoal              */ (7, 0),
            /* TileIronIngot         */ (7, 1),
            /* TileGoldIngot         */ (7, 2),
            /* TileDiamond           */ (7, 3),
            /* TileFlint             */ (6, 0),
            /* TileClayBall          */ (9, 3),
            /* TileClayBrick         */ (6, 1),
            /* TileBowl              */ (7, 4),

            // Tail blocks — coordinates into terrain.png (NOT alpha_tools).
            // Crafting table top is the work-bench grid at (11,2); side
            // is the tool-rack art at (11,3). The bottom face reuses
            // TilePlanks (4,0) at the GetTileIndex lookup so we don't
            // need a third entry.
            /* TileCraftingTableTop  */ (11, 2),
            /* TileCraftingTableSide */ (11, 3),
            // Furnace face tiles in Alpha 1.1.2's terrain.png. Side at
            // (13,2) is the iron-banded stone wall panel. Front-off at
            // (12,2) shows the dark furnace mouth; front-on at (13,3) is
            // the same mouth with an orange/yellow glow lit inside.
            // The top face is NOT present in this build's terrain.png
            // (Alpha shipped it but the embedded resource here is missing
            // that tile), so TileFurnaceTop is always generated
            // procedurally — even in the alpha-textures atlas path.
            // The (-1,-1) sentinel below documents that and trips a
            // runtime assert if anything tries to slice it. See
            // CreateAtlasFromAlphaTerrain for the procedural-override.
            /* TileFurnaceTop        */ (-1, -1),
            /* TileFurnaceSide       */ (13, 2),
            /* TileFurnaceFront      */ (12, 2),
            /* TileFurnaceFrontLit   */ (13, 3),
            // Chest tiles in Alpha 1.1.2's terrain.png. Top at (9,1) is
            // the planked lid with the iron lock-plate; side at (10,1)
            // is the planked carcass with a horizontal iron band;
            // front at (11,1) adds the door split + centred lock to
            // the band. The bottom face reuses TilePlanks (4,0) via
            // GetTileIndex, so no fourth entry is needed.
            /* TileChestTop          */ (9, 1),
            /* TileChestSide         */ (10, 1),
            /* TileChestFront        */ (11, 1),
            // Tail items (Tier 3 #9 — pig drops). Both porkchop tiles
            // are sourced from alpha_tools.png at the canonical Notch
            // coords for the food row: raw at (7,5) and cooked at (8,5).
            // Procedural mode synthesises substitutes; see
            // GenerateRawPorkchopItem / GenerateCookedPorkchopItem.
            /* TileRawPorkchop       */ (7, 5),
            /* TileCookedPorkchop    */ (8, 5),
            // Tail items (Tier 3 #10 — hostile-mob drops). Sentinel
            // (-1,-1) coords so the alpha-textures slice loop skips
            // them and the procedural pixels (GenerateBowItem etc.)
            // stay. Canonical Alpha items.png coords haven't been
            // verified against the embedded sheet — better to ship
            // procedural art that's guaranteed-correct than slice a
            // wrong tile and end up with a stone-shovel icon for "Bow".
            // Bumping these to real coords later is one-line edits.
            // Hostile-mob drops in alpha_tools.png. Verified against the
            // embedded sheet: bow (5,1) directly under flint+steel; arrow
            // (5,2) directly under bow; string (8,0) right of coal;
            // gunpowder (8,2) two rows under string.
            /* TileBow               */ (5, 1),
            /* TileArrow             */ (5, 2),
            /* TileString            */ (8, 0),
            /* TileGunpowder         */ (8, 2),
            // Tail items (Tier 3 #12 — passive-mob drops). Same sentinel
            // pattern as the hostile-mob drops above. Cow drops Leather
            // (Alpha id 334), chicken drops Feather (288) + lays Egg
            // (344). Procedural icons always win until the canonical
            // alpha-tools coords are wired.
            // Passive-mob drops in alpha_tools.png. Verified against the
            // embedded sheet: leather (7,6) under bowl + steak; feather
            // (8,1) right of bow on the bow row; egg (12,0) far right
            // of the helmet row.
            /* TileLeather           */ (7, 6),
            /* TileFeather           */ (8, 1),
            /* TileEgg               */ (12, 0),
            // Tier 4 #14 — Hoes. Canonical Alpha 1.1.2 alpha_tools.png
            // packs hoes in the row immediately past axes (row 7), so
            // wood..gold sit at (0..4, 8). Material order matches the
            // existing tool slice — wood/stone/iron/diamond/gold left
            // to right.
            /* TileWoodHoe           */ (0, 8),
            /* TileStoneHoe          */ (1, 8),
            /* TileIronHoe           */ (2, 8),
            /* TileDiamondHoe        */ (3, 8),
            /* TileGoldHoe           */ (4, 8),
            // Tier 4 #14 — Farming tiles. FarmlandTop is the tilled-dirt
            // top face in terrain.png at (7,5) in canonical Alpha — a
            // brown row of furrows. Wheat stages 0..7 occupy a single
            // row at (8..15, 5) — eight cross-sprite sub-tiles ranging
            // from green sprout (col 8) to ripe golden grain (col 15).
            // The four farming items (seeds, wheat, bread, stew) live
            // in alpha_tools.png — exact coords haven't been verified
            // against the embedded sheet so we use the (-1,-1) sentinel
            // and rely on procedural icons. Bumping these to real
            // coords later is one-line edits per row.
            /* TileFarmlandTop       */ (7, 5),
            /* TileWheat0            */ (8, 5),
            /* TileWheat1            */ (9, 5),
            /* TileWheat2            */ (10, 5),
            /* TileWheat3            */ (11, 5),
            /* TileWheat4            */ (12, 5),
            /* TileWheat5            */ (13, 5),
            /* TileWheat6            */ (14, 5),
            /* TileWheat7            */ (15, 5),
            // Farming items in alpha_tools.png. Verified: seeds (9,0)
            // right of string in the helmet row; wheat item (9,1) under
            // seeds (golden harvested stalks); bread (9,2) under wheat
            // (brown loaf); stew (8,4) right of bowl on the bowl row
            // (filled bowl with a pink/red mushroom mix).
            /* TileWheatSeeds        */ (9, 0),
            /* TileWheatItem         */ (9, 1),
            /* TileBread             */ (9, 2),
            /* TileMushroomStew      */ (8, 4),
            // Tier 4 #26 — Sugar cane block + paper/book items. The
            // canonical Alpha 1.1.2 sugar cane terrain.png coord is
            // (9, 4) — a green-stalk cross-sprite tile in the same row
            // as the cactus + clay tiles. Paper and Book live in
            // alpha_tools.png; the verified coords haven't been
            // confirmed against the embedded sheet so we use the
            // (-1,-1) sentinel and rely on procedural icons. Bumping
            // these to real coords later is one-line edits per row.
            /* TileSugarCane         */ (9, 4),
            // Sugar cane / paper / book items in alpha_tools.png. Verified:
            // sugar cane item (11,1) right of "picture" on the bow row;
            // paper (10,3) under "sign" on the diamond row; book (11,3)
            // right of paper.
            /* TileSugarCaneItem     */ (11, 1),
            /* TilePaper             */ (10, 3),
            /* TileBook              */ (11, 3),
            // Tier 4 #16 — Door block-half tiles in terrain.png.
            // Canonical Alpha 1.1.2_01 layout packs the four door halves
            // in a 2×2 sub-grid at rows 5..6 cols 1..2: wood top (1,5),
            // wood bottom (1,6), iron top (2,5), iron bottom (2,6).
            // Verified visually against the embedded alpha_terrain.png.
            // The 2 inventory icons (TileWoodDoorItem / TileIronDoorItem)
            // live in alpha_tools.png at unverified coords and stay
            // procedural for now — the in-world block tiles are the
            // ones you stare at, the icons are just a slot in the hotbar
            // and procedural is "good enough".
            /* TileWoodDoorTop       */ (1, 5),
            /* TileWoodDoorBottom    */ (1, 6),
            /* TileIronDoorTop       */ (2, 5),
            /* TileIronDoorBottom    */ (2, 6),
            // Door inventory icons in alpha_tools.png. Verified: wooden
            // door (11,2) right of "sign" on the gunpowder row; iron
            // door (12,2) immediately right of the wooden door.
            /* TileWoodDoorItem      */ (11, 2),
            /* TileIronDoorItem      */ (12, 2),
            // Tier 4 #17 — FlintAndSteel + Apple icons in alpha_tools.png.
            // Flint and steel sits at (5, 0) — the cell immediately right
            // of the gold helmet, with the long brown handle + steel
            // striker silhouette. Verified visually against the embedded
            // sheet. Apple's coord hasn't been pinned (could be (9,0) or
            // (10,0) depending on whether the row-0 right-half packs
            // feather/string or arrow at col 8); leave sentinel until
            // verified rather than risk slicing the wrong tile.
            /* TileFlintAndSteel     */ (5, 0),
            /* TileApple             */ (10, 0),
            // Tier 4 #20 — Snowball throwable icon. Procedural-only,
            // same story as the fire pack — alpha_tools.png coord
            // unverified, sentinel keeps the slicer from overlaying
            // garbage. Egg's atlas tile lives at TileEgg=84 (Tier 3
            // #12) and is unaffected.
            /* TileSnowball          */ (14, 0),
            // Tier 4 #15 — Bucket icons. Procedural-only, same
            // sentinel story as the throw pack — alpha_tools.png
            // coords for the four bucket sprites haven't been
            // verified against the embedded sheet, so the slicer
            // stays out and the procedural generators do the
            // painting. Bumping these to real coords later is per-
            // row one-line edits.
            // Bucket family in alpha_tools.png — four contiguous tiles on
            // the bowl row starting at col 10: empty (10,4), water (11,4),
            // lava (12,4), milk (13,4). Verified visually against the
            // embedded sheet (silver pail silhouettes differentiated by
            // fill colour).
            /* TileBucketEmpty       */ (10, 4),
            /* TileBucketWater       */ (11, 4),
            /* TileBucketLava        */ (12, 4),
            /* TileBucketMilk        */ (13, 4),
            // Tier 4 #18 — Slimeball icon. Procedural-only, same
            // sentinel story as the bucket pack — alpha_tools.png
            // coord unverified, sentinel keeps the slicer from
            // overlaying garbage and the procedural generator paints
            // the final tile in both atlas modes.
            /* TileSlimeball         */ (14, 1),
            // Tier 4 #22 — Compass dial-face icon. Procedural-only —
            // the rotating direction marker is rendered live as a
            // TEXTUAL overlay on top of this base tile by RenderHotbar
            // (per-frame atlas mutation isn't worth the cost), so the
            // baked tile is just a fixed dial face. Sentinel keeps
            // the slicer out of this layer in both atlas modes.
            /* TileCompass           */ (6, 3),
            // Tier 4 #21 — Saddle icon. Procedural-only, same sentinel
            // story as the compass pack — alpha_tools.png coord for
            // the saddle sprite hasn't been verified against the
            // embedded sheet. The procedural generator paints a small
            // brown leather pad in both atlas modes.
            /* TileSaddle            */ (8, 6),
            // Tier 4 #23 — Fishing Rod icon. Procedural-only, same
            // sentinel story as the saddle pack — alpha_tools.png
            // coord for the rod sprite hasn't been verified against
            // the embedded sheet. The procedural generator paints
            // the diagonal rod + line + hook silhouette in both
            // atlas modes.
            /* TileFishingRod        */ (5, 4),
            // Tier 4 #24 — Painting art + icon. Procedural-only —
            // Alpha 1.1.2_01 packs the 26 painting variants into a
            // dedicated kz.png sheet (NOT in alpha_tools.png), and
            // the embedded resource bundle here doesn't ship that
            // sheet. Sentinels keep the slicer out of these layers
            // in alpha-textures mode; the procedural generators paint
            // the 5 art tiles + 1 icon tile in both atlas modes.
            // Painting "art" tiles (1×1..4×3) live in Alpha's separate
            // kz.png sheet (NOT in alpha_tools.png), and we don't embed
            // kz.png — those five stay sentinel/procedural. The
            // INVENTORY ICON, however, is the "picture / item frame"
            // sprite at (10, 1) in alpha_tools.png — verified against
            // the embedded sheet, just right of "wheet" on the bow row.
            /* TilePainting1x1       */ (-1, -1),
            /* TilePainting1x2       */ (-1, -1),
            /* TilePainting2x1       */ (-1, -1),
            /* TilePainting2x2       */ (-1, -1),
            /* TilePainting4x3       */ (-1, -1),
            /* TilePaintingItem      */ (10, 1),
            // Tier 4 #25 — Jukebox face tiles in terrain.png. Canonical
            // Alpha 1.1.2_01 layout puts the jukebox side at (10, 4)
            // (vertical-plank panel) and the top at (11, 4) (planks with
            // a centred disc-slot circle). Verified visually against the
            // embedded alpha_terrain.png. Bottom is reused from planks
            // via a multi-face routing in Block.GetTileIndex (the
            // dedicated layer here just stays procedural / sentinel and
            // is never sampled). Disc icons would live in alpha_tools.png
            // at unverified coords — sentinel, stays procedural.
            /* TileJukeboxTop        */ (11, 4),
            /* TileJukeboxSide       */ (10, 4),
            /* TileJukeboxBottom     */ (-1, -1),
            /* TileDisc13            */ (0, 15),
            /* TileDiscCat           */ (1, 15),
            // Tier 4 #19 — Armor inventory icons in alpha_tools.png.
            // Canonical Alpha 1.1.2_01 layout packs the 20 armor pieces
            // in a clean 5×4 grid at rows 0..3, cols 0..4: rows are
            // helmet/chestplate/leggings/boots; cols are leather/
            // chainmail/iron/diamond/gold (wood-tier ordering matches
            // the existing tool slice for consistency). Verified
            // visually against the embedded alpha_tools.png — the 5×4
            // grid at the top-left of the sheet is unmistakeable.
            /* TileLeatherHelmet       */ (0, 0),
            /* TileLeatherChestplate   */ (0, 1),
            /* TileLeatherLeggings     */ (0, 2),
            /* TileLeatherBoots        */ (0, 3),
            /* TileChainmailHelmet     */ (1, 0),
            /* TileChainmailChestplate */ (1, 1),
            /* TileChainmailLeggings   */ (1, 2),
            /* TileChainmailBoots      */ (1, 3),
            /* TileIronHelmet          */ (2, 0),
            /* TileIronChestplate      */ (2, 1),
            /* TileIronLeggings        */ (2, 2),
            /* TileIronBoots           */ (2, 3),
            /* TileDiamondHelmet       */ (3, 0),
            /* TileDiamondChestplate   */ (3, 1),
            /* TileDiamondLeggings     */ (3, 2),
            /* TileDiamondBoots        */ (3, 3),
            /* TileGoldHelmet          */ (4, 0),
            /* TileGoldChestplate      */ (4, 1),
            /* TileGoldLeggings        */ (4, 2),
            /* TileGoldBoots           */ (4, 3),
        };

        // True for layers whose source PNG is alpha_tools.png; false for
        // layers sourced from terrain.png. CreateAtlasFromAlphaTerrain
        // selects the right source buffer per layer using this flag.
        private static bool IsToolLayer(int layer) => layer >= BlockLayerCount;

        // Image-based atlas: read the embedded Alpha terrain.png, slice
        // it into 16×16 tiles using AlphaTileCoords, and upload one
        // layer per tile in the same order as CreateAtlas. The result
        // is a drop-in replacement for the procedural atlas — same
        // dimensions, same layer count, same wrap/filter — so callers
        // can swap between them at runtime without needing to rebuild
        // shaders or vertex layouts.
        //
        // Returns 0 if the embedded resource isn't present (caller is
        // expected to fall back to the procedural atlas) — this keeps
        // older deployed builds that haven't had the PNG re-embedded
        // from going black-screen. Any other failure (decoder fault,
        // GL upload error) propagates as an exception.
        public static int CreateAtlasFromAlphaTerrain()
        {
            byte[] bgra; int srcW, srcH;
            if (!TryDecodeEmbeddedTerrain(out bgra, out srcW, out srcH))
                return 0;

            int tex = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2DArray, tex);
            GL.TexImage3D(
                TextureTarget.Texture2DArray, 0, PixelInternalFormat.Rgba,
                TileSize, TileSize, LayerCount, 0,
                PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);

            var layerPixels = new byte[TileSize * TileSize * 4];
            // Block layers from terrain.png (the bgra buffer just decoded).
            for (int layer = 0; layer < BlockLayerCount; layer++)
            {
                var (col, row) = AlphaTileCoords[layer];
                CopyTile(bgra, srcW, srcH, col, row, layerPixels);
                GL.TexSubImage3D(
                    TextureTarget.Texture2DArray, 0,
                    0, 0, layer,
                    TileSize, TileSize, 1,
                    PixelFormat.Rgba, PixelType.UnsignedByte, layerPixels);
            }
            // Tool layers from alpha_tools.png — separately decoded to its
            // own cached buffer so the two atlases stay independent
            // (terrain can be hot-swappable in the future without
            // disturbing tool icons).
            // Tool AND item layers (38..66) share alpha_tools.png as their
            // source — Alpha packs swords/shovels/pickaxes/axes plus all
            // ingredient sprites (coal, diamond, ingots, stick, flint,
            // clay ball/brick, bowl) into the same 256×256 sheet, so a
            // single decode + slice loop covers both ranges.
            UploadToolLayersFromAlphaTools(layerPixels);

            // Tail block layers (67+) — sourced from terrain.png, same as
            // the 0..37 range. Done after the tool slice so the loop ranges
            // stay non-overlapping. AlphaTileCoords entries for these layers
            // index into terrain.png coordinates.
            //
            // TileFurnaceTop is the one exception: this build's embedded
            // terrain.png doesn't carry the furnace-top tile, so its
            // AlphaTileCoords entry is the (-1,-1) sentinel. We skip it
            // in the slice loop and fill it from the procedural
            // GenerateFurnaceTop below — that generator paints a top
            // built from the same stone-wall + iron-banding palette as
            // the side, so the four lateral faces and the cap read as
            // one continuous block.
            for (int layer = FirstTailBlockLayer; layer < FirstTailItemLayer; layer++)
            {
                if (layer == TileFurnaceTop) continue;
                var (col, row) = AlphaTileCoords[layer];
                CopyTile(bgra, srcW, srcH, col, row, layerPixels);
                GL.TexSubImage3D(
                    TextureTarget.Texture2DArray, 0,
                    0, 0, layer,
                    TileSize, TileSize, 1,
                    PixelFormat.Rgba, PixelType.UnsignedByte, layerPixels);
            }
            // Procedural furnace-top override (see comment above). Uses
            // the same UploadLayer path as the procedural atlas — the
            // Texture2DArray is still bound to `tex` at this point.
            UploadLayer(layerPixels, TileFurnaceTop, GenerateFurnaceTop);

            // Tail-item layers (76+, Tier 3 #9 porkchops). Sourced from
            // alpha_tools.png — Notch packs the food row into the same
            // sheet as tools/items at canonical (col, row) coordinates.
            // Falls back silently if the embedded PNG is missing (the
            // procedural path painted these slots; they'll just stay
            // procedural in the otherwise-Alpha atlas).
            UploadTailItemLayersFromAlphaTools(layerPixels);

            // Tier 4 #14 — Procedural farming layers always paint first
            // (FarmlandTop + 8 wheat stages + 4 farming items) so the
            // atlas has guaranteed-correct sprites even if the alpha
            // PNG slice fails or is missing the verified coords.
            GenerateProceduralFarmingLayers(layerPixels);
            // Then overlay the tilled-dirt + wheat stages from
            // terrain.png (canonical Alpha coords are wired in
            // AlphaTileCoords). Farming items (seeds/wheat/bread/stew)
            // stay procedural — their AlphaTileCoords entries are
            // sentinels because we haven't verified the alpha_tools.png
            // food-row coords for them yet.
            UploadFarmingBlockLayersFromTerrain(bgra, srcW, srcH, layerPixels);
            // Hoes from alpha_tools.png — same idempotent slicer as
            // UploadToolLayersFromAlphaTools but scoped to the tail-tool
            // 2 range (85..89). A missing tools PNG just leaves the
            // procedural hoe pixels in place.
            UploadTailTool2LayersFromAlphaTools(layerPixels);

            // Tier 4 #26 — Sugar cane block layer + paper/book item
            // sprites. Procedural paints first so the atlas has
            // guaranteed-correct sprites even if the alpha PNG slice
            // misses or has sentinel coords. Then UploadCane... attempts
            // to overlay the in-world cane stalk from terrain.png at
            // (col 9, row 4); paper/book have sentinel coords so they
            // stay procedural in the alpha atlas (matching how the
            // farming items stay procedural — we haven't verified the
            // alpha_tools.png coords for them yet).
            GenerateProceduralCaneLayers(layerPixels);
            UploadCaneBlockLayersFromTerrain(bgra, srcW, srcH, layerPixels);

            // Tier 4 #16 — Door tiles. All 6 layers (4 block halves +
            // 2 inventory icons) ship procedural for now; their
            // AlphaTileCoords entries are sentinels so there's no
            // overlay step to call. Painting here in the alpha-atlas
            // path mirrors the cane / farming-item handling — without
            // this call the door tile slots would be transparent in
            // alpha-textures mode while the procedural-only atlas
            // (CreateAtlas) would render them correctly.
            GenerateProceduralDoorLayers(layerPixels);

            // Tier 4 #17 — Flint and Steel + Apple icons, same
            // procedural-always story as the door pack.
            GenerateProceduralFireLayers(layerPixels);

            // Tier 4 #20 — Snowball icon. Egg's tile is shared with the
            // Tier 3 #12 drop tile (TileEgg=84), painted by the
            // Tail-item alpha-tools slice above; only Snowball needs
            // an explicit paint here.
            GenerateProceduralThrowLayers(layerPixels);

            // Tier 4 #15 — Bucket icons (empty/water/lava/milk). All
            // four ship procedural; sentinel coords keep the slicer
            // from overlaying garbage in alpha-textures mode.
            GenerateProceduralBucketLayers(layerPixels);

            // Tier 4 #18 — Slimeball icon. Procedural-always — same
            // sentinel-coord story as the bucket pack.
            GenerateProceduralSlimeLayers(layerPixels);

            // Tier 4 #22 — Compass dial face. Procedural-always —
            // same sentinel-coord story as the slime pack.
            GenerateProceduralCompassLayers(layerPixels);

            // Tier 4 #21 — Saddle sprite. Procedural-always — same
            // sentinel-coord story as the compass pack.
            GenerateProceduralSaddleLayers(layerPixels);

            // Tier 4 #23 — Fishing Rod sprite. Procedural-always —
            // same sentinel-coord story as the saddle pack.
            GenerateProceduralFishingRodLayers(layerPixels);

            // Tier 4 #24 — Painting art + icon. Procedural-always —
            // same sentinel-coord story as the fishing-rod pack.
            GenerateProceduralPaintingLayers(layerPixels);

            // Tier 4 #25 — Jukebox + disc pack. Jukebox face tiles get
            // overlaid from terrain.png by UploadCaneBlockLayersFromTerrain
            // above (the slicer's range now includes them via the
            // verified (10,4) / (11,4) coords); music disc icons stay
            // procedural until the alpha_tools.png coords are verified.
            GenerateProceduralJukeboxLayers(layerPixels);

            // Tier 4 #19 — Armor inventory icons. 20 procedural sprites
            // painted as a fallback; the alpha_tools.png slice below
            // overlays the real Alpha 1.1.2 sprites at the canonical
            // 5×4 grid (rows 0..3, cols 0..4) so the icons match the
            // rest of the alpha-textures atlas.
            GenerateProceduralArmorLayers(layerPixels);

            // Tier 4 — Overlay alpha_tools.png coords for ALL tail items
            // past the door pack: door inventory icons, flint+steel,
            // apple, snowball, buckets, slimeball, compass, saddle,
            // fishing rod, paintings, music discs, and all 20 armor
            // pieces. Each layer's AlphaTileCoords entry decides whether
            // it gets sliced (real coord) or stays procedural (sentinel).
            // Must run LAST in the alpha-textures path so a verified
            // tile always wins over the procedural fallback that ran
            // earlier; idempotent on a missing alpha_tools.png (every
            // affected layer was already painted procedurally).
            UploadTailItemsFromAlphaTools(layerPixels);

            GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
            GL.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
            GL.BindTexture(TextureTarget.Texture2DArray, 0);
            return tex;
        }

        // Cached decoded buffer. The atlas builder runs on the GL render
        // thread, but WPF imaging has thread-affinity quirks — the safest
        // way to dodge them is to decode once and reuse the byte buffer
        // for any subsequent rebuild. The cache is process-scoped: every
        // world-load on the same VS session gets the already-decoded
        // pixels with no PNG decoder pass at all.
        private static byte[] s_cachedBgra;
        private static int s_cachedW, s_cachedH;
        private static bool s_decodeAttempted;
        private static bool s_decodeFailed;

        // Parallel decode cache for alpha_tools.png. Tools are independent
        // from terrain (loaded from a different base64 constant) but
        // reuse the same decode path — the only divergence is the source
        // bytes. A separate cache avoids re-decoding on every atlas
        // rebuild and keeps the two PNG buffers from clobbering each
        // other's lifetimes.
        private static byte[] s_toolsCachedBgra;
        private static int s_toolsCachedW, s_toolsCachedH;
        private static bool s_toolsDecodeFailed;
        // Surfaced through AlphaTerrainStatus so we can show the actual
        // failure reason in the Options label instead of a generic
        // "UNAVAILABLE" — we were guessing at the failure cause and
        // chasing the wrong fix; a visible message turns this into
        // a one-click diagnosis next time it happens.
        private static string s_decodeStatus;

        // Decode the embedded PNG into a tightly-packed BGRA byte array.
        // WPF's BitmapDecoder is the simplest decoder we already have a
        // reference to (PresentationCore); System.Drawing would also
        // work but adds platform baggage. The PNG ships in the assembly
        // as a manifest resource — preferred name is
        // "VStudioCraft.Assets.alpha_terrain.png" (set via the project's
        // <LogicalName>) but we tolerate the default-derived name too,
        // which is what some build paths or older csproj edits produce.
        //
        // Returns false if no candidate resource is found in the
        // assembly's manifest, or if the decoder throws (a stale build
        // or unusual WPF host setup) — caller falls back to procedural
        // art rather than crashing the render thread.
        internal static bool TryDecodeEmbeddedTerrain(out byte[] bgra, out int width, out int height)
        {
            // Fast path: reuse cached buffer once we've decoded once.
            if (s_cachedBgra != null)
            {
                bgra = s_cachedBgra; width = s_cachedW; height = s_cachedH;
                return true;
            }
            // Don't keep retrying a decode that's already failed — once
            // is enough; subsequent calls just take the fallback.
            if (s_decodeFailed)
            {
                bgra = null; width = 0; height = 0;
                return false;
            }
            s_decodeAttempted = true;

            bgra = null; width = 0; height = 0;
            try
            {
                bool ok = TryDecodeEmbeddedTerrainCore(out bgra, out width, out height);
                if (!ok && s_decodeStatus == null)
                    s_decodeStatus = "no resource";
                return ok;
            }
            catch (Exception ex)
            {
                // Capture the type+first-line of the message so the
                // Options label can surface it. We don't include the
                // full message because it may contain paths/locale text
                // that won't fit; the type alone narrows the cause to
                // GDI+ vs IO vs reflection vs something stranger.
                s_decodeFailed = true;
                s_decodeStatus = ex.GetType().Name;
                bgra = null; width = 0; height = 0;
                return false;
            }
        }

        // GDI+ decoder. We previously used WPF's BitmapDecoder, but in
        // some VS hosting setups it threw mid-decode (CopyPixels on a
        // non-Dispatcher thread, or codec initialisation that hadn't
        // run yet) and silently fell back to procedural. System.Drawing
        // is already referenced for the WinForms host, has no thread
        // affinity and no Dispatcher dependency, and locks the bitmap's
        // pixel data directly so we get raw BGRA out the other side
        // without going through WPF's imaging stack.
        //
        // Source-of-bytes precedence:
        //   1. AlphaTerrainData.Base64 — a string constant compiled into
        //      the assembly's IL. This is the primary source because
        //      <EmbeddedResource> turned out to be fragile in some
        //      developer VS configurations (the resource silently went
        //      missing from the deployed DLL); a const string is
        //      something the compiler simply cannot drop.
        //   2. Embedded manifest resource — kept as a secondary source
        //      so existing builds that still ship the resource keep
        //      working, and so a future regenerated PNG can be picked
        //      up from the resource without re-running the codegen
        //      script for the base64 constant.
        private static bool TryDecodeEmbeddedTerrainCore(out byte[] bgra, out int width, out int height)
        {
            bgra = null; width = 0; height = 0;

            byte[] pngBytes = TryLoadPngBytes();
            if (pngBytes == null) return false;

            using (var src = new MemoryStream(pngBytes))
            using (var bmp = new Bitmap(src))
            {
                width  = bmp.Width;
                height = bmp.Height;

                var rect = new Rectangle(0, 0, width, height);
                var data = bmp.LockBits(rect, GdiImageLockMode.ReadOnly, GdiPixelFormat.Format32bppArgb);
                try
                {
                    int dstStride = width * 4;
                    bgra = new byte[dstStride * height];
                    for (int y = 0; y < height; y++)
                    {
                        IntPtr rowPtr = IntPtr.Add(data.Scan0, y * data.Stride);
                        Marshal.Copy(rowPtr, bgra, y * dstStride, dstStride);
                    }
                }
                finally
                {
                    bmp.UnlockBits(data);
                }
            }

            // Cache the decoded buffer so subsequent toggles reuse it
            // instead of re-running the decoder.
            s_cachedBgra = bgra;
            s_cachedW = width;
            s_cachedH = height;
            return true;
        }

        // Decode alpha_tools.png from the inlined AlphaToolsData.Base64
        // constant. Same GDI+ path as the terrain decoder — kept separate
        // because the tools sheet has its own size (256×256) and its own
        // failure mode (older builds without AlphaToolsData simply skip
        // tool icons rather than going blank for everything).
        private static bool TryDecodeEmbeddedTools(out byte[] bgra, out int width, out int height)
        {
            if (s_toolsCachedBgra != null)
            {
                bgra = s_toolsCachedBgra; width = s_toolsCachedW; height = s_toolsCachedH;
                return true;
            }
            if (s_toolsDecodeFailed)
            {
                bgra = null; width = 0; height = 0; return false;
            }

            bgra = null; width = 0; height = 0;
            try
            {
                string b64 = AlphaToolsData.Base64;
                if (string.IsNullOrEmpty(b64))
                {
                    s_toolsDecodeFailed = true;
                    return false;
                }
                byte[] pngBytes = Convert.FromBase64String(b64);

                using (var src = new MemoryStream(pngBytes))
                using (var bmp = new Bitmap(src))
                {
                    width = bmp.Width;
                    height = bmp.Height;
                    var rect = new Rectangle(0, 0, width, height);
                    var data = bmp.LockBits(rect, GdiImageLockMode.ReadOnly, GdiPixelFormat.Format32bppArgb);
                    try
                    {
                        int dstStride = width * 4;
                        bgra = new byte[dstStride * height];
                        for (int y = 0; y < height; y++)
                        {
                            IntPtr rowPtr = IntPtr.Add(data.Scan0, y * data.Stride);
                            Marshal.Copy(rowPtr, bgra, y * dstStride, dstStride);
                        }
                    }
                    finally
                    {
                        bmp.UnlockBits(data);
                    }
                }
                s_toolsCachedBgra = bgra;
                s_toolsCachedW = width;
                s_toolsCachedH = height;
                return true;
            }
            catch
            {
                s_toolsDecodeFailed = true;
                bgra = null; width = 0; height = 0;
                return false;
            }
        }

        // Resolve PNG bytes from the most reliable source available.
        // Returns null only if both the inlined base64 constant and
        // the embedded manifest resource are absent — at which point
        // we genuinely have no terrain data and the caller should
        // fall back to procedural.
        private static byte[] TryLoadPngBytes()
        {
            // 1. Inline base64 constant — present unconditionally in
            //    every build because it's IL, not a resource. This
            //    path makes the toggle work regardless of whatever
            //    VS configuration ate the embedded resource last time.
            try
            {
                var b64 = AlphaTerrainData.Base64;
                if (!string.IsNullOrEmpty(b64))
                    return Convert.FromBase64String(b64);
            }
            catch
            {
                // Fall through to manifest resource path — extremely
                // unlikely (the constant is valid base64 by codegen
                // construction) but we don't want a corrupted constant
                // to take out the whole alpha-textures option.
            }

            // 2. Manifest resource fallback (legacy / belt-and-braces).
            var asm = Assembly.GetExecutingAssembly();

            // Resource-name resolution order:
            //   1. The LogicalName we set in the csproj (preferred).
            //   2. Default-mapped name (RootNamespace + path), in case
            //      LogicalName wasn't picked up by the build.
            //   3. Anything containing "alpha_terrain.png" — last-ditch
            //      heuristic so a future rename still works without
            //      touching this code.
            string[] candidates =
            {
                "VStudioCraft.Assets.alpha_terrain.png",
                "VStudioCraft.Game.Assets.alpha_terrain.png",
            };
            string match = null;
            var allNames = asm.GetManifestResourceNames();
            foreach (var c in candidates)
            {
                foreach (var n in allNames)
                {
                    if (string.Equals(n, c, StringComparison.OrdinalIgnoreCase))
                    {
                        match = n;
                        break;
                    }
                }
                if (match != null) break;
            }
            if (match == null)
            {
                foreach (var n in allNames)
                {
                    if (n.IndexOf("alpha_terrain", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        match = n;
                        break;
                    }
                }
            }
            if (match == null) return null;

            try
            {
                using (var s = asm.GetManifestResourceStream(match))
                {
                    if (s == null) return null;
                    using (var ms = new MemoryStream())
                    {
                        s.CopyTo(ms);
                        return ms.ToArray();
                    }
                }
            }
            catch
            {
                return null;
            }
        }

        // Eagerly decode the embedded terrain on a known-good thread
        // (typically the WPF UI thread at world-load time). Calling this
        // before any toggle ensures the cache is populated, so the GL
        // render-thread rebuild later just reads the cached BGRA buffer
        // and never touches the WPF imaging stack itself. Safe to call
        // multiple times — it's a no-op once the cache is populated.
        public static void PrewarmEmbeddedTerrain()
        {
            if (s_cachedBgra != null || s_decodeFailed) return;
            byte[] _; int __, ___;
            TryDecodeEmbeddedTerrain(out _, out __, out ___);
        }

        // Diagnostic surface for the Options menu. After PrewarmEmbeddedTerrain
        // runs (or after the first toggle attempt), this reflects whether the
        // alpha terrain.png is actually usable — false here means the toggle
        // is a no-op and the user is silently getting procedural in both
        // ON and OFF positions, which is exactly the "I enable it but
        // nothing changes" symptom we're trying to make legible.
        public static bool AlphaTerrainAvailable => s_cachedBgra != null;
        public static bool AlphaTerrainAttempted => s_decodeAttempted || s_cachedBgra != null;
        public static bool AlphaTerrainFailed => s_decodeFailed;
        // Short human-readable status (exception type, "no resource",
        // or null when not yet attempted). Surfaced in OptionsMenu
        // when the toggle would otherwise show a misleading state.
        public static string AlphaTerrainStatus => s_decodeStatus;

        // Copy a single 16×16 tile from the source BGRA buffer at
        // (col*16, row*16) into a tightly-packed RGBA buffer suitable
        // for GL upload. Two transforms happen here:
        //
        //   1. BGRA → RGBA channel swap (PNG decoder gives us BGRA;
        //      GL upload expects RGBA in our pipeline).
        //
        //   2. Vertical flip — PNG row 0 is the *top* of the source
        //      tile, but the procedural atlas (and therefore the mesh
        //      UVs that drive the renderer) treats data row 0 as the
        //      *bottom* of the rendered face (see comments on
        //      GenerateGrassSide / GenerateTorch — the green fringe
        //      and flame head sit at high y to land at the *top* of
        //      a face). Without this flip, terrain.png sliced tiles
        //      would render upside-down: grass-side fringe at the
        //      bottom, torches pointing down, flowers inverted.
        internal static void CopyTile(byte[] srcBgra, int srcW, int srcH, int col, int row, byte[] dstRgba)
        {
            int x0 = col * TileSize;
            int y0 = row * TileSize;
            // Bounds check: an out-of-range coordinate (e.g. someone
            // edits AlphaTileCoords past 16×16) should produce a magenta
            // tile rather than crash, so the bad mapping is visible
            // in-game rather than silently failing.
            if (x0 < 0 || y0 < 0 || x0 + TileSize > srcW || y0 + TileSize > srcH)
            {
                for (int i = 0; i < dstRgba.Length; i += 4)
                {
                    dstRgba[i + 0] = 255;
                    dstRgba[i + 1] = 0;
                    dstRgba[i + 2] = 255;
                    dstRgba[i + 3] = 255;
                }
                return;
            }

            int srcStride = srcW * 4;
            for (int ty = 0; ty < TileSize; ty++)
            {
                // Read the source row in PNG order (top-to-bottom)…
                int srcRow = (y0 + ty) * srcStride + x0 * 4;
                // …but write to the dst row that mirrors it vertically,
                // so high-y in source ends up at low-y in dst (= bottom
                // of rendered face), matching procedural convention.
                int dstY = TileSize - 1 - ty;
                int dstRow = dstY * TileSize * 4;
                for (int tx = 0; tx < TileSize; tx++)
                {
                    byte b = srcBgra[srcRow + tx * 4 + 0];
                    byte g = srcBgra[srcRow + tx * 4 + 1];
                    byte r = srcBgra[srcRow + tx * 4 + 2];
                    byte a = srcBgra[srcRow + tx * 4 + 3];
                    dstRgba[dstRow + tx * 4 + 0] = r;
                    dstRgba[dstRow + tx * 4 + 1] = g;
                    dstRgba[dstRow + tx * 4 + 2] = b;
                    dstRgba[dstRow + tx * 4 + 3] = a;
                }
            }
        }

        private delegate void LayerFiller(byte[] pixels);

        private static void UploadLayer(byte[] pixels, int layer, LayerFiller fill)
        {
            Array.Clear(pixels, 0, pixels.Length);
            fill(pixels);
            GL.TexSubImage3D(
                TextureTarget.Texture2DArray, 0,
                0, 0, layer,
                TileSize, TileSize, 1,
                PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
        }

        private static void SetPixel(byte[] p, int px, int py, byte r, byte g, byte b, byte a = 255)
        {
            int idx = (py * TileSize + px) * 4;
            p[idx] = r;
            p[idx + 1] = g;
            p[idx + 2] = b;
            p[idx + 3] = a;
        }

        private static byte Clamp(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);

        private static (byte r, byte g, byte b) Pick(Random rng, (byte r, byte g, byte b)[] palette, int[] weights)
        {
            int total = 0;
            for (int i = 0; i < weights.Length; i++) total += weights[i];
            int pick = rng.Next(total);
            int acc = 0;
            for (int i = 0; i < weights.Length; i++)
            {
                acc += weights[i];
                if (pick < acc) return palette[i];
            }
            return palette[palette.Length - 1];
        }

        private static void NoiseFill(byte[] pixels, int seed, (byte r, byte g, byte b)[] palette, int[] weights)
        {
            var rng = new Random(seed);
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
            {
                var (r, g, b) = Pick(rng, palette, weights);
                SetPixel(pixels, x, y, r, g, b);
            }
        }

        // Pixel-level random jitter around a base color — used for structured tiles
        // (planks, bricks, logs) where the shape is deterministic but we still want
        // the gritty per-pixel variation typical of alpha terrain.
        private static void SetJittered(byte[] pixels, int x, int y, byte r, byte g, byte b, int jitter, Random rng)
        {
            int j = rng.Next(jitter * 2 + 1) - jitter;
            SetPixel(pixels, x, y, Clamp(r + j), Clamp(g + j), Clamp(b + j));
        }

        private static void GenerateDirt(byte[] pixels)
        {
            var rng = new Random(0x0D17);
            var palette = new (byte, byte, byte)[]
            {
                (134, 96, 67),
                (107, 76, 48),
                (156, 116, 80),
                (92, 63, 38),
            };
            var weights = new[] { 10, 5, 4, 1 };
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
            {
                var (r, g, b) = Pick(rng, palette, weights);
                SetPixel(pixels, x, y, r, g, b);
            }
        }

        private static void GenerateStone(byte[] pixels)
        {
            var rng = new Random(0x570E);
            var palette = new (byte, byte, byte)[]
            {
                (124, 124, 124),
                (108, 108, 108),
                (140, 140, 140),
                (92, 92, 92),
            };
            var weights = new[] { 12, 5, 4, 1 };
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
            {
                var (r, g, b) = Pick(rng, palette, weights);
                SetPixel(pixels, x, y, r, g, b);
            }
        }

        private static void GenerateGrassTop(byte[] pixels)
        {
            var rng = new Random(0x6A55);
            var palette = new (byte, byte, byte)[]
            {
                (93, 150, 57),
                (76, 126, 44),
                (110, 170, 70),
                (63, 108, 36),
            };
            var weights = new[] { 11, 5, 4, 1 };
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
            {
                var (r, g, b) = Pick(rng, palette, weights);
                SetPixel(pixels, x, y, r, g, b);
            }
        }

        private static void GenerateSand(byte[] pixels)
        {
            var rng = new Random(0x5A4D);
            var palette = new (byte, byte, byte)[]
            {
                (219, 209, 150),
                (206, 194, 132),
                (232, 223, 170),
                (190, 176, 116),
            };
            var weights = new[] { 14, 4, 3, 1 };
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
            {
                var (r, g, b) = Pick(rng, palette, weights);
                SetPixel(pixels, x, y, r, g, b);
            }
        }

        private static void GenerateGrassSide(byte[] pixels)
        {
            var dirtRng = new Random(0x51DE);
            var dirtPalette = new (byte, byte, byte)[]
            {
                (134, 96, 67),
                (107, 76, 48),
                (156, 116, 80),
                (92, 63, 38),
            };
            var dirtWeights = new[] { 10, 5, 4, 1 };
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
            {
                var (r, g, b) = Pick(dirtRng, dirtPalette, dirtWeights);
                SetPixel(pixels, x, y, r, g, b);
            }

            // Green overhang along the top edge. v=0 is the bottom of the texture,
            // so the green fringe lives at high y to render along the top of the face.
            var grassRng = new Random(0x67A5);
            var grassPalette = new (byte, byte, byte)[]
            {
                (93, 150, 57),
                (76, 126, 44),
                (110, 170, 70),
                (63, 108, 36),
            };
            var grassWeights = new[] { 11, 5, 4, 1 };

            var fringe = new int[TileSize];
            for (int x = 0; x < TileSize; x++) fringe[x] = 2 + grassRng.Next(4);

            for (int x = 0; x < TileSize; x++)
            {
                int depth = fringe[x];
                for (int d = 0; d < depth; d++)
                {
                    int y = TileSize - 1 - d;
                    var (r, g, b) = Pick(grassRng, grassPalette, grassWeights);
                    SetPixel(pixels, x, y, r, g, b);
                }
            }
        }

        private static void GenerateCobblestone(byte[] pixels)
        {
            var rng = new Random(0xCB15);
            var light = new (byte, byte, byte)[]
            {
                (140, 140, 140),
                (156, 156, 156),
                (124, 124, 124),
                (168, 168, 168),
            };
            var lightWeights = new[] { 10, 6, 5, 2 };
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
            {
                var (r, g, b) = Pick(rng, light, lightWeights);
                SetPixel(pixels, x, y, r, g, b);
            }
            // Scatter darker "cracks" — short clusters mimicking cobble pits.
            var dark = new (byte, byte, byte)[]
            {
                (72, 72, 72),
                (56, 56, 56),
                (88, 88, 88),
            };
            var darkWeights = new[] { 6, 3, 2 };
            for (int n = 0; n < 22; n++)
            {
                int cx = rng.Next(TileSize);
                int cy = rng.Next(TileSize);
                int r = 1 + rng.Next(2);
                for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                {
                    if (dx * dx + dy * dy > r * r) continue;
                    if (rng.Next(3) == 0) continue;
                    int x = ((cx + dx) % TileSize + TileSize) % TileSize;
                    int y = ((cy + dy) % TileSize + TileSize) % TileSize;
                    var (dr, dg, db) = Pick(rng, dark, darkWeights);
                    SetPixel(pixels, x, y, dr, dg, db);
                }
            }
        }

        private static void GenerateBedrock(byte[] pixels)
        {
            var palette = new (byte, byte, byte)[]
            {
                (56, 56, 56),
                (40, 40, 40),
                (72, 72, 72),
                (28, 28, 28),
            };
            NoiseFill(pixels, 0xBED0, palette, new[] { 12, 6, 4, 2 });
        }

        private static void GenerateGravel(byte[] pixels)
        {
            var palette = new (byte, byte, byte)[]
            {
                (140, 134, 126),
                (112, 105, 95),
                (160, 150, 138),
                (88, 78, 64),
                (168, 142, 110),
            };
            NoiseFill(pixels, 0x61A4, palette, new[] { 10, 6, 4, 3, 2 });
        }

        private static void GenerateClay(byte[] pixels)
        {
            var palette = new (byte, byte, byte)[]
            {
                (166, 170, 186),
                (150, 154, 172),
                (180, 184, 198),
                (138, 142, 162),
            };
            NoiseFill(pixels, 0xC1A7, palette, new[] { 12, 5, 3, 2 });
        }

        // Shared helper: stone base + scattered ore clusters. Each ore picks a
        // palette + a distinct seed so clusters land in different spots.
        private static void GenerateOre(byte[] pixels, int seed, (byte r, byte g, byte b)[] orePalette, int[] oreWeights)
        {
            var stonePalette = new (byte, byte, byte)[]
            {
                (124, 124, 124),
                (108, 108, 108),
                (140, 140, 140),
                (92, 92, 92),
            };
            var stoneWeights = new[] { 12, 5, 4, 1 };
            var rng = new Random(seed);
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
            {
                var (r, g, b) = Pick(rng, stonePalette, stoneWeights);
                SetPixel(pixels, x, y, r, g, b);
            }
            int clusters = 3 + rng.Next(3);
            for (int n = 0; n < clusters; n++)
            {
                int cx = rng.Next(TileSize);
                int cy = rng.Next(TileSize);
                int r = 1 + rng.Next(2);
                for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                {
                    if (dx * dx + dy * dy > r * r) continue;
                    if (rng.Next(4) == 0) continue;
                    int x = ((cx + dx) % TileSize + TileSize) % TileSize;
                    int y = ((cy + dy) % TileSize + TileSize) % TileSize;
                    var (or_, og, ob) = Pick(rng, orePalette, oreWeights);
                    SetPixel(pixels, x, y, or_, og, ob);
                }
            }
        }

        private static void GenerateCoalOre(byte[] pixels)
        {
            var palette = new (byte, byte, byte)[]
            {
                (30, 30, 30),
                (48, 48, 48),
                (18, 18, 18),
            };
            GenerateOre(pixels, 0xC0A1, palette, new[] { 10, 5, 3 });
        }

        private static void GenerateIronOre(byte[] pixels)
        {
            var palette = new (byte, byte, byte)[]
            {
                (186, 150, 120),
                (166, 130, 100),
                (210, 170, 140),
            };
            GenerateOre(pixels, 0x1201, palette, new[] { 10, 5, 3 });
        }

        private static void GenerateGoldOre(byte[] pixels)
        {
            var palette = new (byte, byte, byte)[]
            {
                (240, 214, 78),
                (220, 190, 58),
                (252, 232, 120),
            };
            GenerateOre(pixels, 0x601D, palette, new[] { 10, 5, 3 });
        }

        private static void GenerateDiamondOre(byte[] pixels)
        {
            var palette = new (byte, byte, byte)[]
            {
                (120, 230, 230),
                (90, 200, 210),
                (180, 250, 250),
            };
            GenerateOre(pixels, 0x0D1A, palette, new[] { 10, 5, 3 });
        }

        private static void GenerateRedstoneOre(byte[] pixels)
        {
            var palette = new (byte, byte, byte)[]
            {
                (200, 40, 30),
                (230, 60, 50),
                (160, 20, 14),
            };
            GenerateOre(pixels, 0xDEA5, palette, new[] { 10, 5, 3 });
        }

        private static void GenerateLogTop(byte[] pixels)
        {
            // Alpha log tops are a pale-tan field with darker concentric rings
            // and a dark heartwood pip. Sample radius from slightly off-centre
            // so the rings don't look like a perfect target.
            var rng = new Random(0x106);
            float cx = TileSize / 2f - 0.5f + 0.4f;
            float cy = TileSize / 2f - 0.5f - 0.3f;
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
            {
                float dx = x - cx, dy = y - cy;
                float d = (float)Math.Sqrt(dx * dx + dy * dy);
                byte r, g, b;
                // Tight dark core (heartwood), then alternating light field and
                // thin dark ring lines at integer radii.
                float frac = d - (float)Math.Floor(d);
                bool onRing = frac < 0.35f && d > 1.2f;
                if (d < 1.3f)      { r = 82;  g = 58; b = 30; }  // pip
                else if (onRing)   { r = 128; g = 92; b = 50; }  // ring line
                else               { r = 188; g = 150; b = 96; } // field
                SetJittered(pixels, x, y, r, g, b, 7, rng);
            }
        }

        private static void GenerateLogSide(byte[] pixels)
        {
            // Alpha oak bark: warm brown field with vertical grooves, scattered
            // dark cracks, and a couple of knots. The stripes give the "tall bark"
            // read; the cracks and knots stop it looking like a barcode.
            var rng = new Random(0x5106);

            // Base: per-column tone so adjacent pixels in a column share a shade.
            // This mimics bark fibres better than purely random noise.
            byte[] columnR = new byte[TileSize];
            byte[] columnG = new byte[TileSize];
            byte[] columnB = new byte[TileSize];
            for (int x = 0; x < TileSize; x++)
            {
                int stripe = x % 4;
                byte r, g, b;
                if (stripe == 0)      { r = 84;  g = 60;  b = 32; }   // groove
                else if (stripe == 1) { r = 118; g = 88;  b = 50; }
                else if (stripe == 2) { r = 134; g = 100; b = 58; }
                else                  { r = 108; g = 80;  b = 46; }
                columnR[x] = r; columnG[x] = g; columnB[x] = b;
            }
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
                SetJittered(pixels, x, y, columnR[x], columnG[x], columnB[x], 6, rng);

            // Short horizontal cracks — 1-3px long, dark, scattered.
            for (int n = 0; n < 14; n++)
            {
                int cx = rng.Next(TileSize);
                int cy = rng.Next(TileSize);
                int len = 1 + rng.Next(3);
                for (int d = 0; d < len; d++)
                {
                    int x = (cx + d) % TileSize;
                    SetJittered(pixels, x, cy, 58, 42, 22, 5, rng);
                }
            }

            // A couple of knots: small dark blobs with a lighter ring.
            for (int n = 0; n < 2; n++)
            {
                int kx = rng.Next(TileSize);
                int ky = 2 + rng.Next(TileSize - 4);
                SetPixel(pixels, kx, ky, 52, 36, 18);
                int kxp = (kx + 1) % TileSize;
                int kxm = (kx - 1 + TileSize) % TileSize;
                SetJittered(pixels, kxp, ky, 96, 70, 40, 5, rng);
                SetJittered(pixels, kxm, ky, 96, 70, 40, 5, rng);
                SetJittered(pixels, kx, (ky + 1) % TileSize, 96, 70, 40, 5, rng);
                SetJittered(pixels, kx, (ky - 1 + TileSize) % TileSize, 96, 70, 40, 5, rng);
            }
        }

        private static void GeneratePlanks(byte[] pixels)
        {
            // Four-row-tall planks: one-pixel darker groove at the top of each plank.
            var rng = new Random(0xB12C);
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
            {
                bool groove = (y % 4 == 0);
                byte r = groove ? (byte)123 : (byte)160;
                byte g = groove ? (byte)91 : (byte)124;
                byte b = groove ? (byte)52 : (byte)74;
                SetJittered(pixels, x, y, r, g, b, 7, rng);
            }
        }

        private static void GenerateLeaves(byte[] pixels)
        {
            // Fancy-graphics-style alpha-tested leaves: dense green noise
            // punched through with ~25% holes so adjacent leaf blocks read
            // through each other as a layered canopy. Matches the alpha (4,3)
            // tile's silhouette behaviour — the shader's `if (tex.a < 0.5)
            // discard;` does the cut-out, no blending. Without the holes the
            // canopy collapsed to a single solid hull and every block behind
            // the front face was invisible.
            var rng = new Random(0x1EAF);
            var palette = new (byte, byte, byte)[]
            {
                (48, 92, 30),
                (40, 78, 24),
                (62, 108, 38),
                (30, 60, 16),
                (20, 40, 10),
            };
            var weights = new[] { 10, 6, 3, 4, 2 };
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
            {
                // Roughly 1-in-4 cells punch through entirely. The choice
                // is deterministic per-tile (seeded RNG) so two adjacent
                // leaf blocks share the same hole pattern — that makes the
                // layered effect read as foliage rather than a moiré.
                bool hole = rng.Next(4) == 0;
                if (hole)
                {
                    SetPixel(pixels, x, y, 0, 0, 0, 0);
                    continue;
                }
                var (r, g, b) = Pick(rng, palette, weights);
                SetPixel(pixels, x, y, r, g, b, 255);
            }
        }

        private static void GenerateWater(byte[] pixels)
        {
            // Alpha still water: calm bluish noise with ~160 alpha so stone and sand
            // below read through. Blending is enabled in the renderer's transparent pass.
            var rng = new Random(0xAA7E);
            var palette = new (byte, byte, byte)[]
            {
                (56, 92, 204),
                (42, 78, 184),
                (78, 120, 220),
                (32, 60, 160),
            };
            var weights = new[] { 12, 5, 4, 2 };
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
            {
                var (r, g, b) = Pick(rng, palette, weights);
                SetPixel(pixels, x, y, r, g, b, a: 160);
            }
        }

        private static void GenerateLava(byte[] pixels)
        {
            var palette = new (byte, byte, byte)[]
            {
                (214, 78, 22),
                (184, 52, 12),
                (240, 140, 40),
                (250, 200, 60),
            };
            NoiseFill(pixels, 0x1A7A, palette, new[] { 10, 6, 4, 2 });
        }

        private static void GenerateGoldBlock(byte[] pixels)
        {
            var palette = new (byte, byte, byte)[]
            {
                (240, 220, 60),
                (220, 196, 40),
                (252, 240, 120),
                (190, 170, 24),
            };
            NoiseFill(pixels, 0x901D, palette, new[] { 12, 5, 3, 2 });
        }

        private static void GenerateIronBlock(byte[] pixels)
        {
            var palette = new (byte, byte, byte)[]
            {
                (220, 220, 220),
                (196, 196, 196),
                (240, 240, 240),
                (170, 170, 170),
            };
            NoiseFill(pixels, 0x1AB0, palette, new[] { 12, 5, 3, 2 });
        }

        private static void GenerateDiamondBlock(byte[] pixels)
        {
            var palette = new (byte, byte, byte)[]
            {
                (90, 210, 220),
                (60, 180, 200),
                (140, 240, 240),
                (40, 150, 180),
            };
            NoiseFill(pixels, 0xD1A0, palette, new[] { 12, 5, 3, 2 });
        }

        private static void GenerateBricks(byte[] pixels)
        {
            // Four-row brick courses, offset by 4 columns on odd rows. Mortar is
            // one pixel along the bottom of each row and one pixel at each brick
            // boundary; bricks themselves are red-brown with per-pixel jitter.
            var rng = new Random(0xB71C);
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
            {
                int row = y / 4;
                bool mortarY = (y % 4 == 0);
                int rowOffset = (row % 2 == 0) ? 0 : 4;
                bool mortarX = ((x + rowOffset) % 8 == 0);
                bool mortar = mortarY || mortarX;
                if (mortar)
                    SetJittered(pixels, x, y, 170, 170, 170, 6, rng);
                else
                    SetJittered(pixels, x, y, 150, 82, 62, 10, rng);
            }
        }

        private static void GenerateTntTop(byte[] pixels)
        {
            // Red with a small grey detonator cap dead-centre.
            var rng = new Random(0x0717);
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
                SetJittered(pixels, x, y, 200, 40, 40, 9, rng);
            for (int y = 7; y <= 8; y++)
            for (int x = 7; x <= 8; x++)
                SetPixel(pixels, x, y, 90, 90, 90);
        }

        private static void GenerateTntBottom(byte[] pixels)
        {
            // Sandy tan — matches alpha's "fuse base" bottom.
            var rng = new Random(0x071B);
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
                SetJittered(pixels, x, y, 206, 180, 102, 8, rng);
        }

        private static void GenerateTntSide(byte[] pixels)
        {
            // Red body with a pale "TNT" label band along the top.
            var rng = new Random(0x0715);
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
            {
                byte r, g, b;
                if (y >= 12) { r = 220; g = 220; b = 210; }
                else if (y >= 5 && y <= 10 && (x == 2 || x == 7 || x == 13)) { r = 130; g = 24; b = 24; }
                else { r = 200; g = 40; b = 40; }
                SetJittered(pixels, x, y, r, g, b, 8, rng);
            }
        }

        private static void GenerateBookshelfSide(byte[] pixels)
        {
            // Planks along the top two and bottom two rows; middle 12 rows are
            // vertical book spines in rotating colors with dark gutters between.
            var rng = new Random(0xB00C);
            var bookColors = new (byte, byte, byte)[]
            {
                (172, 60, 50),
                (70, 110, 180),
                (60, 130, 80),
                (180, 160, 80),
                (90, 70, 160),
                (120, 80, 50),
            };
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
            {
                if (y < 2 || y >= 14)
                {
                    bool groove = (y == 0 || y == 15);
                    byte r = groove ? (byte)123 : (byte)160;
                    byte g = groove ? (byte)91 : (byte)124;
                    byte b = groove ? (byte)52 : (byte)74;
                    SetJittered(pixels, x, y, r, g, b, 6, rng);
                }
                else
                {
                    bool gutter = (x % 3 == 0);
                    if (gutter)
                    {
                        SetPixel(pixels, x, y, 40, 30, 20);
                    }
                    else
                    {
                        int spine = (x / 3) + (y < 8 ? 0 : 3);
                        var c = bookColors[(spine * 7 + 3) % bookColors.Length];
                        SetJittered(pixels, x, y, c.Item1, c.Item2, c.Item3, 7, rng);
                    }
                }
            }
        }

        // Crafting table top — plank base with a 3×3 grid scratched in,
        // hinting at the work surface. The grid is drawn with a slightly
        // darker tint (not pure black) so it reads at a glance without
        // shouting "IDE icon" at the player.
        private static void GenerateCraftingTableTop(byte[] pixels)
        {
            // Start from a planks-style base.
            var rng = new Random(0xC2A1);
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
            {
                bool groove = (y % 4 == 0);
                byte r = groove ? (byte)110 : (byte)148;
                byte g = groove ? (byte)80 : (byte)112;
                byte b = groove ? (byte)44 : (byte)64;
                SetJittered(pixels, x, y, r, g, b, 5, rng);
            }
            // Overlay the 3×3 work grid: vertical bars at x=5 and x=10,
            // horizontal bars at y=5 and y=10. 1px wide, dark brown.
            for (int x = 0; x < TileSize; x++)
            {
                SetPixel(pixels, x, 5, 60, 38, 18);
                SetPixel(pixels, x, 10, 60, 38, 18);
            }
            for (int y = 0; y < TileSize; y++)
            {
                SetPixel(pixels, 5, y, 60, 38, 18);
                SetPixel(pixels, 10, y, 60, 38, 18);
            }
            // Outer rim — frames the work surface against the side art.
            for (int i = 0; i < TileSize; i++)
            {
                SetPixel(pixels, i, 0, 88, 60, 30);
                SetPixel(pixels, i, TileSize - 1, 88, 60, 30);
                SetPixel(pixels, 0, i, 88, 60, 30);
                SetPixel(pixels, TileSize - 1, i, 88, 60, 30);
            }
        }

        // Crafting table side — plank base with two simple tool
        // silhouettes (a saw blade outline + a hammer head) drawn in
        // dark brown. Doesn't need to be readable at micro-scale; it
        // just needs to NOT look like plain planks so the player can
        // tell the block apart from a normal Planks block at a glance.
        private static void GenerateCraftingTableSide(byte[] pixels)
        {
            var rng = new Random(0xC2B2);
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
            {
                bool groove = (y % 4 == 0);
                byte r = groove ? (byte)110 : (byte)148;
                byte g = groove ? (byte)80 : (byte)112;
                byte b = groove ? (byte)44 : (byte)64;
                SetJittered(pixels, x, y, r, g, b, 5, rng);
            }
            // Hammer (top-left): rectangular head + diagonal handle.
            // Head: x=2..6, y=2..4
            for (int y = 2; y <= 4; y++)
            for (int x = 2; x <= 6; x++)
                SetPixel(pixels, x, y, 60, 38, 18);
            // Handle: diagonal from (5,4) toward (10,9).
            for (int i = 0; i < 6; i++)
                SetPixel(pixels, 5 + i, 4 + i, 80, 50, 22);

            // Saw blade (bottom-right): triangular silhouette.
            // Spine: y=12, x=8..14
            for (int x = 8; x <= 14; x++)
                SetPixel(pixels, x, 12, 60, 38, 18);
            // Teeth: alternating below the spine
            for (int x = 8; x <= 14; x += 2)
                SetPixel(pixels, x, 13, 60, 38, 18);
            // Handle: y=10..11, x=14
            SetPixel(pixels, 14, 10, 80, 50, 22);
            SetPixel(pixels, 14, 11, 80, 50, 22);
        }

        // Furnace top — derived from the side palette so the cap reads
        // as the same material as the four lateral faces. Stone-wall
        // jitter base, iron banding around all four edges (looking down
        // onto the block we see the rim from every direction, not just
        // top/bottom), and a darker recessed vent hole in the centre
        // that lets smoke out. Built deliberately to look like a 90°
        // rotation of the side art with a vent overlay; that way an
        // observer glancing at a furnace from above never sees a seam
        // between the cap and the walls.
        //
        // This generator is invoked from BOTH atlas paths (procedural
        // CreateAtlas and alpha-art CreateAtlasFromAlphaTerrain) because
        // this build's embedded terrain.png is missing the furnace-top
        // tile entirely.
        private static void GenerateFurnaceTop(byte[] pixels)
        {
            // Same RNG seed shape as GenerateFurnaceSide so the noise
            // grain looks like it came off the same stamping machine
            // (different seed value to avoid identical pixels — that
            // would make the top read as the side, just rotated).
            var rng = new Random(0xF0A1);
            // Stone-wall base — same RGB and jitter amount as
            // GenerateFurnaceSide.
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
            {
                SetJittered(pixels, x, y, 130, 130, 130, 14, rng);
            }
            // Iron banding on ALL four edges (2 px deep) — the side
            // tile only bands top/bottom because its left/right edges
            // tile against the next side tile. The top face has no
            // neighbour to tile against, so the banding wraps the
            // whole rim.
            for (int i = 0; i < TileSize; i++)
            {
                // Top and bottom rows.
                SetPixel(pixels, i, 0, 86, 86, 86);
                SetPixel(pixels, i, 1, 100, 100, 100);
                SetPixel(pixels, i, TileSize - 2, 100, 100, 100);
                SetPixel(pixels, i, TileSize - 1, 86, 86, 86);
                // Left and right columns. Skip the corners we already
                // wrote above so the corner shade matches the
                // top/bottom band rather than getting overwritten.
                if (i >= 2 && i <= TileSize - 3)
                {
                    SetPixel(pixels, 0, i, 86, 86, 86);
                    SetPixel(pixels, 1, i, 100, 100, 100);
                    SetPixel(pixels, TileSize - 2, i, 100, 100, 100);
                    SetPixel(pixels, TileSize - 1, i, 86, 86, 86);
                }
            }
            // Rivet dots — three on each edge, placed at the same x/y
            // offsets the side tile uses (3, 8, 13) so the bands read
            // as continuous fasteners around the block.
            for (int s = 3; s < TileSize; s += 5)
            {
                SetPixel(pixels, s, 0, 180, 180, 180);
                SetPixel(pixels, s, TileSize - 1, 180, 180, 180);
                SetPixel(pixels, 0, s, 180, 180, 180);
                SetPixel(pixels, TileSize - 1, s, 180, 180, 180);
            }
            // Central vent hole — 4×4 near-black square in the middle
            // (6..9). The smelter has to vent somewhere; this is the
            // only Alpha cue that the top differs from a wall panel.
            for (int y = 6; y <= 9; y++)
            for (int x = 6; x <= 9; x++)
            {
                SetPixel(pixels, x, y, 24, 24, 22);
            }
            // Vent rim — one-pixel highlight on the upper/left edges
            // and shadow on the lower/right so the hole reads as
            // recessed rather than painted on. Matches the lighting
            // direction used by other recessed details (e.g. the
            // furnace mouth on the front face).
            for (int x = 5; x <= 10; x++)
            {
                SetPixel(pixels, x, 5, 160, 160, 160);
                SetPixel(pixels, x, 10, 80, 80, 80);
            }
            for (int y = 5; y <= 10; y++)
            {
                SetPixel(pixels, 5, y, 160, 160, 160);
                SetPixel(pixels, 10, y, 80, 80, 80);
            }
        }

        // Furnace side — plain stone wall with horizontal iron banding
        // top and bottom. The banding hints at structural reinforcement
        // and lets the side read distinct from a plain Stone block.
        private static void GenerateFurnaceSide(byte[] pixels)
        {
            var rng = new Random(0xF0B2);
            // Stone wall base.
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
            {
                SetJittered(pixels, x, y, 130, 130, 130, 14, rng);
            }
            // Iron-grey horizontal bands at the top and bottom edges (2 px
            // each). These are the "rivetted plate" detail Alpha shows on
            // the furnace's vertical flanks — without orientation
            // metadata they apply to all four sides, which still reads
            // as "metal-edged stone" from any angle.
            for (int x = 0; x < TileSize; x++)
            {
                SetPixel(pixels, x, 0, 86, 86, 86);
                SetPixel(pixels, x, 1, 100, 100, 100);
                SetPixel(pixels, x, TileSize - 2, 100, 100, 100);
                SetPixel(pixels, x, TileSize - 1, 86, 86, 86);
            }
            // Rivet dots — three evenly-spaced highlights on each iron
            // band so the surface reads as fastened plate.
            for (int x = 3; x < TileSize; x += 5)
            {
                SetPixel(pixels, x, 0, 180, 180, 180);
                SetPixel(pixels, x, TileSize - 1, 180, 180, 180);
            }
        }

        // Furnace front (unlit) — stone wall with a recessed dark mouth
        // in the lower-middle. The mouth is the smelter door; lit
        // version below adds the orange/yellow flame glow inside.
        // Shared base palette with the side tile so the four lateral
        // faces blend into a single block silhouette before the door
        // detail is overlaid.
        private static void GenerateFurnaceFront(byte[] pixels)
        {
            var rng = new Random(0xF0C3);
            // Stone wall base — same recipe as the side tile so the
            // edges blend.
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
            {
                SetJittered(pixels, x, y, 130, 130, 130, 14, rng);
            }
            // Iron rim banding (top and bottom) — keeps the four-sides
            // silhouette consistent with FurnaceSide.
            for (int x = 0; x < TileSize; x++)
            {
                SetPixel(pixels, x, 0, 86, 86, 86);
                SetPixel(pixels, x, 1, 100, 100, 100);
                SetPixel(pixels, x, TileSize - 2, 100, 100, 100);
                SetPixel(pixels, x, TileSize - 1, 86, 86, 86);
            }
            // Furnace mouth — recessed dark opening at (4..11, 5..12).
            // Two-tone: outer frame medium-dark, inner cavity near-black.
            for (int y = 5; y <= 12; y++)
            for (int x = 4; x <= 11; x++)
            {
                SetPixel(pixels, x, y, 24, 24, 22);
            }
            // Frame — slightly lighter ring so the cavity reads as
            // recessed rather than painted on.
            for (int x = 4; x <= 11; x++)
            {
                SetPixel(pixels, x, 5, 70, 70, 68);
                SetPixel(pixels, x, 12, 70, 70, 68);
            }
            for (int y = 5; y <= 12; y++)
            {
                SetPixel(pixels, 4, y, 70, 70, 68);
                SetPixel(pixels, 11, y, 70, 70, 68);
            }
        }

        // Furnace front (lit) — same base as FurnaceFront with orange
        // and yellow pixels filling the mouth cavity to convey active
        // smelting. The lit-state block (BlockType.LitFurnace) swaps in
        // when the per-position tile entity has fuel burning; switching
        // back to Furnace clears the glow without needing a per-tick
        // texture change.
        private static void GenerateFurnaceFrontLit(byte[] pixels)
        {
            // Start with the unlit version so the frame, banding, and
            // cavity outline all match exactly.
            GenerateFurnaceFront(pixels);
            var rng = new Random(0xF0D4);
            // Fill the cavity (interior of the frame at x=5..10, y=6..11)
            // with a flame palette: bottom-third hot orange, middle
            // yellow, top dim red — gives the impression of flames
            // licking up inside the mouth.
            for (int y = 6; y <= 11; y++)
            for (int x = 5; x <= 10; x++)
            {
                byte r, g, b;
                if (y >= 9)
                {
                    // Hot core near the bottom — orange-red.
                    r = 255; g = 130; b = 30;
                }
                else if (y >= 7)
                {
                    // Mid flame — yellow-orange.
                    r = 248; g = 188; b = 60;
                }
                else
                {
                    // Top flame tips — dim red shading into smoke.
                    r = 168; g = 60; b = 18;
                }
                SetJittered(pixels, x, y, r, g, b, 16, rng);
            }
        }

        // Chest top — planked lid with a small iron lock-plate centred
        // on the front edge. The plate's "front" edge in the texture is
        // the +Z edge in world space; that lines up with the default
        // BlockFacing so a chest placed under the standard south-facing
        // default reads correctly.
        private static void GenerateChestTop(byte[] pixels)
        {
            // Plank base — same recipe as GeneratePlanks but a slightly
            // darker tone so the lid reads as "treated wood" rather than
            // bare planks.
            var rng = new Random(0xCE51);
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
            {
                bool groove = (y % 4 == 0);
                byte r = groove ? (byte)115 : (byte)150;
                byte g = groove ? (byte)82  : (byte)112;
                byte b = groove ? (byte)44  : (byte)64;
                SetJittered(pixels, x, y, r, g, b, 6, rng);
            }
            // Iron lock-plate: a small rectangle straddling the front
            // edge of the lid (front = high y, since the side/front
            // tiles place the band high too — see GenerateChestSide).
            // Plate sits at x=6..9, y=11..13.
            for (int y = 11; y <= 13; y++)
            for (int x = 6; x <= 9; x++)
            {
                SetPixel(pixels, x, y, 110, 110, 110);
            }
            // Plate highlight (top edge) and shadow (bottom edge).
            for (int x = 6; x <= 9; x++)
            {
                SetPixel(pixels, x, 11, 150, 150, 150);
                SetPixel(pixels, x, 13, 70, 70, 70);
            }
            // A single keyhole pixel at the centre of the plate.
            SetPixel(pixels, 7, 12, 30, 30, 30);
            SetPixel(pixels, 8, 12, 30, 30, 30);
        }

        // Chest side — plank carcass with a single horizontal iron
        // band across the upper third (y=4..5). No lock or door split,
        // so the side reads as "the back/sides of a wooden box".
        private static void GenerateChestSide(byte[] pixels)
        {
            var rng = new Random(0xCE62);
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
            {
                bool groove = (y % 4 == 0);
                byte r = groove ? (byte)115 : (byte)150;
                byte g = groove ? (byte)82  : (byte)112;
                byte b = groove ? (byte)44  : (byte)64;
                SetJittered(pixels, x, y, r, g, b, 6, rng);
            }
            // Iron band — 2 pixels tall at y=4..5, with shadow on the
            // lower edge so the band reads as a strap rather than a
            // painted line.
            for (int x = 0; x < TileSize; x++)
            {
                SetPixel(pixels, x, 4, 130, 130, 130);
                SetPixel(pixels, x, 5, 96, 96, 96);
            }
            // Three rivets at evenly spaced offsets on the band.
            for (int x = 2; x < TileSize; x += 5)
            {
                SetPixel(pixels, x, 4, 180, 180, 180);
            }
        }

        // Chest front — same plank base + iron band as the side, plus
        // a vertical door split down the centre column and a small
        // iron lock straddling the band at the door split. This is
        // the face that lands on the side of the chest facing the
        // placer (handled by GetTileIndexForOriented).
        private static void GenerateChestFront(byte[] pixels)
        {
            var rng = new Random(0xCE73);
            // Plank base.
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
            {
                bool groove = (y % 4 == 0);
                byte r = groove ? (byte)115 : (byte)150;
                byte g = groove ? (byte)82  : (byte)112;
                byte b = groove ? (byte)44  : (byte)64;
                SetJittered(pixels, x, y, r, g, b, 6, rng);
            }
            // Iron band — same coordinates as the side so the band
            // wraps continuously around the chest.
            for (int x = 0; x < TileSize; x++)
            {
                SetPixel(pixels, x, 4, 130, 130, 130);
                SetPixel(pixels, x, 5, 96, 96, 96);
            }
            for (int x = 2; x < TileSize; x += 5)
            {
                SetPixel(pixels, x, 4, 180, 180, 180);
            }
            // Door split: a 1px dark seam down the middle of the
            // lower two-thirds (y=6..15, x=8). Skip the band rows so
            // the seam doesn't break the band silhouette.
            for (int y = 6; y < TileSize; y++)
            {
                SetPixel(pixels, 8, y, 70, 46, 24);
            }
            // Iron lock — 4×3 plate centred on the door split,
            // straddling the band so the lock visually fastens the
            // band in place. Plate at x=6..9, y=6..8.
            for (int y = 6; y <= 8; y++)
            for (int x = 6; x <= 9; x++)
            {
                SetPixel(pixels, x, y, 110, 110, 110);
            }
            // Plate highlight + shadow.
            for (int x = 6; x <= 9; x++)
            {
                SetPixel(pixels, x, 6, 150, 150, 150);
                SetPixel(pixels, x, 8, 70, 70, 70);
            }
            // Keyhole — two near-black pixels at the plate centre.
            SetPixel(pixels, 7, 7, 30, 30, 30);
            SetPixel(pixels, 8, 7, 30, 30, 30);
        }

        private static void GenerateMossyCobblestone(byte[] pixels)
        {
            // Start from cobblestone, then tint scattered pixels toward green.
            GenerateCobblestone(pixels);
            var rng = new Random(0xC0005);
            for (int n = 0; n < 24; n++)
            {
                int cx = rng.Next(TileSize);
                int cy = rng.Next(TileSize);
                int r = 1 + rng.Next(2);
                for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                {
                    if (dx * dx + dy * dy > r * r) continue;
                    if (rng.Next(3) == 0) continue;
                    int x = ((cx + dx) % TileSize + TileSize) % TileSize;
                    int y = ((cy + dy) % TileSize + TileSize) % TileSize;
                    int idx = (y * TileSize + x) * 4;
                    byte curR = pixels[idx];
                    byte curG = pixels[idx + 1];
                    byte curB = pixels[idx + 2];
                    byte mossR = (byte)(curR * 3 / 8);
                    byte mossG = Clamp(curG * 4 / 5 + 40);
                    byte mossB = (byte)(curB * 3 / 8);
                    SetPixel(pixels, x, y, mossR, mossG, mossB);
                }
            }
        }

        private static void GenerateObsidian(byte[] pixels)
        {
            var palette = new (byte, byte, byte)[]
            {
                (26, 20, 42),
                (38, 30, 58),
                (16, 12, 28),
                (52, 42, 76),
            };
            NoiseFill(pixels, 0x0B51, palette, new[] { 12, 6, 4, 2 });
        }

        private static void GenerateSponge(byte[] pixels)
        {
            var rng = new Random(0x5906);
            var palette = new (byte, byte, byte)[]
            {
                (210, 190, 70),
                (180, 164, 50),
                (230, 212, 100),
            };
            var weights = new[] { 10, 5, 3 };
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
            {
                var (r, g, b) = Pick(rng, palette, weights);
                SetPixel(pixels, x, y, r, g, b);
            }
            // Scatter dark pore pixels.
            for (int n = 0; n < 20; n++)
            {
                int cx = rng.Next(TileSize);
                int cy = rng.Next(TileSize);
                SetPixel(pixels, cx, cy, 100, 80, 30);
            }
        }

        private static void GenerateGlass(byte[] pixels)
        {
            // Alpha-tested glass: opaque pale frame, fully transparent
            // interior. The fragment shader's `if (tex.a < 0.5) discard;`
            // cuts the centre out at draw time, leaving just the 1-pixel
            // border visible — matches Alpha 1.1.2_01's canonical glass
            // tile (clear pane outlined in a muted frame). Combined with
            // the mesher's IsAlphaTestedCube routing, adjacent glass blocks
            // emit their shared face so a row of windows reads as a stack
            // of frames the player can see through, not a hollow shell.
            //
            // Two pixels in from each edge is left as frame as well so the
            // outline is a touch thicker and reads cleanly at 16×16 nearest-
            // filter — a 1-px border alone disappears at distance.
            var rng = new Random(0x61A5);
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
            {
                bool outer = (x == 0 || y == 0 || x == TileSize - 1 || y == TileSize - 1);
                bool inner = (x == 1 || y == 1 || x == TileSize - 2 || y == TileSize - 2);
                if (outer)
                {
                    SetJittered(pixels, x, y, 190, 200, 210, 6, rng);
                }
                else if (inner)
                {
                    // Slight highlight on the inner ring — sells the bevel.
                    SetJittered(pixels, x, y, 220, 228, 236, 5, rng);
                }
                else
                {
                    // Discarded by the shader; the alpha=0 makes it transparent.
                    SetPixel(pixels, x, y, 0, 0, 0, 0);
                }
            }
        }

        private static void GenerateWool(byte[] pixels)
        {
            var palette = new (byte, byte, byte)[]
            {
                (232, 232, 228),
                (218, 218, 214),
                (244, 244, 240),
            };
            NoiseFill(pixels, 0x5011, palette, new[] { 12, 5, 4 });
        }

        private static void GenerateTorch(byte[] pixels)
        {
            // Pixel-art torch on a transparent background. The fragment shader
            // does an alpha-test discard at < 0.5 so any pixel left at alpha=0
            // disappears — no blending needed.
            //
            // Coordinate convention: y=0 is the bottom of the texture, which is
            // also the bottom of the rendered cross-sprite face. The wooden
            // shaft runs y=0..7 in the centre, the flame burns y=8..12.
            var rng = new Random(0x707C);

            // Base: fully transparent.
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
                SetPixel(pixels, x, y, 0, 0, 0, 0);

            // Wooden shaft — 2px wide, centred on x=7..8, height 0..7.
            for (int y = 0; y <= 7; y++)
            {
                // Slight per-row jitter so it doesn't read as a straight bar.
                SetJittered(pixels, 7, y, 138, 96, 54, 8, rng);
                SetJittered(pixels, 8, y, 118, 80, 42, 8, rng);
            }

            // Flame head — three short rows of warming colour. Hottest core
            // (white-yellow) sits at the centre, surrounded by orange, red,
            // and finally a faint outer halo so the silhouette is more than
            // a perfect rectangle.
            //
            // y=8 (base of flame): orange/red, slightly wider than shaft.
            SetPixel(pixels, 6, 8, 220, 100, 30, 220);
            SetPixel(pixels, 7, 8, 250, 200, 60, 255);
            SetPixel(pixels, 8, 8, 250, 200, 60, 255);
            SetPixel(pixels, 9, 8, 220, 100, 30, 220);
            // y=9: bright yellow centre, orange outer.
            SetPixel(pixels, 6, 9, 230, 130, 40, 200);
            SetPixel(pixels, 7, 9, 252, 232, 130, 255);
            SetPixel(pixels, 8, 9, 252, 232, 130, 255);
            SetPixel(pixels, 9, 9, 230, 130, 40, 200);
            // y=10: yellow tapering, red shoulders.
            SetPixel(pixels, 7, 10, 252, 220, 110, 255);
            SetPixel(pixels, 8, 10, 252, 220, 110, 255);
            SetPixel(pixels, 6, 10, 200, 70, 24, 160);
            SetPixel(pixels, 9, 10, 200, 70, 24, 160);
            // y=11: small orange tip.
            SetPixel(pixels, 7, 11, 240, 160, 50, 220);
            SetPixel(pixels, 8, 11, 240, 160, 50, 220);
            // y=12: faint outer glow above the flame.
            SetPixel(pixels, 7, 12, 220, 110, 30, 140);
            SetPixel(pixels, 8, 12, 220, 110, 30, 140);
        }

        // ----- Cross-sprite flora tiles -----
        // All five tiles share the same convention as Torch: y=0 is the bottom
        // of the rendered face, transparent background, the fragment shader
        // discards alpha < 0.5 so they live in the opaque pass with no blending.

        private static void GenerateDandelion(byte[] pixels)
        {
            // Tall green stem with a small yellow flower-head sitting on top
            // and a pair of leaf nubs partway up the stem. Alpha-era dandelions
            // were a 4-5 pixel cluster of yellow on a single-pixel stem.
            var rng = new Random(0xDA4D);
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
                SetPixel(pixels, x, y, 0, 0, 0, 0);

            // Stem y=1..10 at x=7
            for (int y = 1; y <= 10; y++)
                SetJittered(pixels, 7, y, 80, 130, 50, 8, rng);
            // Pair of leaf nubs midway.
            SetJittered(pixels, 6, 4, 70, 120, 44, 8, rng);
            SetJittered(pixels, 8, 6, 70, 120, 44, 8, rng);

            // Yellow flower head: 3x3 cluster centred at (7, 11), with the
            // outer corners slightly dimmer to soften the silhouette.
            SetPixel(pixels, 7, 11, 248, 220, 60, 255);
            SetPixel(pixels, 6, 11, 230, 196, 50, 255);
            SetPixel(pixels, 8, 11, 230, 196, 50, 255);
            SetPixel(pixels, 7, 10, 230, 196, 50, 255);
            SetPixel(pixels, 7, 12, 252, 232, 90, 255);
            SetPixel(pixels, 6, 12, 220, 180, 40, 220);
            SetPixel(pixels, 8, 12, 220, 180, 40, 220);
            SetPixel(pixels, 6, 10, 200, 160, 30, 180);
            SetPixel(pixels, 8, 10, 200, 160, 30, 180);
        }

        private static void GenerateRose(byte[] pixels)
        {
            // Single tall stem with red bloom petals on top. Rose in alpha was
            // visibly a vertical green stick with a red triangular flower.
            var rng = new Random(0x6053);
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
                SetPixel(pixels, x, y, 0, 0, 0, 0);

            // Stem y=0..9 at x=7
            for (int y = 0; y <= 9; y++)
                SetJittered(pixels, 7, y, 70, 120, 44, 8, rng);
            // Single leaf jutting out partway up.
            SetJittered(pixels, 8, 5, 86, 140, 56, 8, rng);
            SetJittered(pixels, 6, 3, 86, 140, 56, 8, rng);

            // Red bloom: a triangular cluster of petals above the stem.
            // Inner is brighter red, outer is darker maroon for shading.
            SetPixel(pixels, 7, 10, 220, 50, 50, 255);
            SetPixel(pixels, 6, 10, 180, 30, 30, 255);
            SetPixel(pixels, 8, 10, 180, 30, 30, 255);
            SetPixel(pixels, 7, 11, 240, 70, 70, 255);
            SetPixel(pixels, 6, 11, 220, 50, 50, 255);
            SetPixel(pixels, 8, 11, 220, 50, 50, 255);
            SetPixel(pixels, 5, 11, 160, 24, 24, 220);
            SetPixel(pixels, 9, 11, 160, 24, 24, 220);
            SetPixel(pixels, 7, 12, 252, 100, 90, 255);
            SetPixel(pixels, 6, 12, 220, 50, 50, 220);
            SetPixel(pixels, 8, 12, 220, 50, 50, 220);
            SetPixel(pixels, 7, 13, 200, 36, 36, 200);
            // Tiny yellow centre pollen pip.
            SetPixel(pixels, 7, 12, 252, 220, 80, 255);
        }

        private static void GenerateBrownMushroom(byte[] pixels)
        {
            // Squat stem with a dome cap. Brown is the "harmless" mushroom in
            // alpha, slightly smaller than the red one.
            var rng = new Random(0xB607);
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
                SetPixel(pixels, x, y, 0, 0, 0, 0);

            // Stem (cream) y=0..4 at x=7..8
            for (int y = 0; y <= 4; y++)
            {
                SetJittered(pixels, 7, y, 220, 210, 184, 6, rng);
                SetJittered(pixels, 8, y, 200, 190, 164, 6, rng);
            }

            // Cap: brown dome roughly 5 wide, 3 tall at y=5..7
            // Row y=5 (cap underside): darker brown shadow
            for (int x = 6; x <= 9; x++)
                SetJittered(pixels, x, 5, 92, 64, 40, 6, rng);
            // Row y=6 (cap body): mid brown
            for (int x = 5; x <= 10; x++)
                SetJittered(pixels, x, 6, 150, 108, 70, 8, rng);
            // Row y=7 (cap top): lighter highlight
            for (int x = 6; x <= 9; x++)
                SetJittered(pixels, x, 7, 180, 140, 96, 8, rng);
            // Row y=8: tip
            SetJittered(pixels, 7, 8, 160, 120, 80, 6, rng);
            SetJittered(pixels, 8, 8, 160, 120, 80, 6, rng);
        }

        private static void GenerateRedMushroom(byte[] pixels)
        {
            // Same skeleton as the brown mushroom but with a taller red cap
            // and white spots on top — the "amanita" silhouette.
            var rng = new Random(0xED70);
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
                SetPixel(pixels, x, y, 0, 0, 0, 0);

            // Stem y=0..4 at x=7..8
            for (int y = 0; y <= 4; y++)
            {
                SetJittered(pixels, 7, y, 230, 220, 196, 5, rng);
                SetJittered(pixels, 8, y, 210, 200, 176, 5, rng);
            }

            // Cap underside (gills) y=5
            for (int x = 6; x <= 9; x++)
                SetJittered(pixels, x, 5, 220, 200, 184, 5, rng);
            // Cap body y=6..8 in red
            for (int x = 5; x <= 10; x++)
                SetJittered(pixels, x, 6, 200, 36, 36, 8, rng);
            for (int x = 5; x <= 10; x++)
                SetJittered(pixels, x, 7, 220, 50, 50, 8, rng);
            for (int x = 6; x <= 9; x++)
                SetJittered(pixels, x, 8, 200, 36, 36, 8, rng);
            SetJittered(pixels, 7, 9, 180, 24, 24, 6, rng);
            SetJittered(pixels, 8, 9, 180, 24, 24, 6, rng);

            // White spots scattered on the cap.
            SetPixel(pixels, 6, 7, 240, 240, 232, 255);
            SetPixel(pixels, 9, 7, 240, 240, 232, 255);
            SetPixel(pixels, 8, 6, 240, 240, 232, 255);
            SetPixel(pixels, 7, 8, 240, 240, 232, 255);
        }

        // Tier 4 #19 — Armor inventory icon pack. 20 procedural item
        // tiles painted as material-coloured silhouettes shaped per
        // slot. Per-material palette (base, highlight, shadow):
        //   Leather   = brown ramps
        //   Chainmail = silver-grey ramps
        //   Iron      = light-grey ramps with a subtle blue tint
        //   Diamond   = cyan-white ramps
        //   Gold      = saturated yellow ramps
        // Per-slot silhouette functions paint a generic shape that the
        // material palette overlays on. The five materials reuse the
        // same four shape functions with different palettes — same
        // trick the tool ladder uses for sword/pickaxe/axe/shovel.
        private static void GenerateProceduralArmorLayers(byte[] layerPixels)
        {
            // Leather — warm brown.
            var leather   = ((byte)138, (byte)92,  (byte)50);
            var leatherHi = ((byte)175, (byte)125, (byte)75);
            var leatherLo = ((byte)92,  (byte)56,  (byte)28);
            UploadItem(layerPixels, TileLeatherHelmet,
                p => PaintArmorHelmet(p, leather, leatherHi, leatherLo));
            UploadItem(layerPixels, TileLeatherChestplate,
                p => PaintArmorChestplate(p, leather, leatherHi, leatherLo));
            UploadItem(layerPixels, TileLeatherLeggings,
                p => PaintArmorLeggings(p, leather, leatherHi, leatherLo));
            UploadItem(layerPixels, TileLeatherBoots,
                p => PaintArmorBoots(p, leather, leatherHi, leatherLo));
            // Chainmail — neutral silver grey, slightly cooler than iron
            // so the two read as distinct in the inventory.
            var chain   = ((byte)140, (byte)140, (byte)148);
            var chainHi = ((byte)190, (byte)190, (byte)200);
            var chainLo = ((byte)90,  (byte)90,  (byte)100);
            UploadItem(layerPixels, TileChainmailHelmet,
                p => PaintArmorHelmet(p, chain, chainHi, chainLo));
            UploadItem(layerPixels, TileChainmailChestplate,
                p => PaintArmorChestplate(p, chain, chainHi, chainLo));
            UploadItem(layerPixels, TileChainmailLeggings,
                p => PaintArmorLeggings(p, chain, chainHi, chainLo));
            UploadItem(layerPixels, TileChainmailBoots,
                p => PaintArmorBoots(p, chain, chainHi, chainLo));
            // Iron — slightly warmer / lighter than chain.
            var iron   = ((byte)180, (byte)180, (byte)180);
            var ironHi = ((byte)225, (byte)225, (byte)225);
            var ironLo = ((byte)120, (byte)120, (byte)120);
            UploadItem(layerPixels, TileIronHelmet,
                p => PaintArmorHelmet(p, iron, ironHi, ironLo));
            UploadItem(layerPixels, TileIronChestplate,
                p => PaintArmorChestplate(p, iron, ironHi, ironLo));
            UploadItem(layerPixels, TileIronLeggings,
                p => PaintArmorLeggings(p, iron, ironHi, ironLo));
            UploadItem(layerPixels, TileIronBoots,
                p => PaintArmorBoots(p, iron, ironHi, ironLo));
            // Diamond — pale cyan-white. Brighter than iron at the
            // highlights so the gem-tier reads as premium.
            var diamond   = ((byte)110, (byte)225, (byte)215);
            var diamondHi = ((byte)200, (byte)250, (byte)245);
            var diamondLo = ((byte)60,  (byte)160, (byte)155);
            UploadItem(layerPixels, TileDiamondHelmet,
                p => PaintArmorHelmet(p, diamond, diamondHi, diamondLo));
            UploadItem(layerPixels, TileDiamondChestplate,
                p => PaintArmorChestplate(p, diamond, diamondHi, diamondLo));
            UploadItem(layerPixels, TileDiamondLeggings,
                p => PaintArmorLeggings(p, diamond, diamondHi, diamondLo));
            UploadItem(layerPixels, TileDiamondBoots,
                p => PaintArmorBoots(p, diamond, diamondHi, diamondLo));
            // Gold — saturated yellow with warmer highlights.
            var gold   = ((byte)230, (byte)200, (byte)55);
            var goldHi = ((byte)252, (byte)238, (byte)130);
            var goldLo = ((byte)165, (byte)130, (byte)25);
            UploadItem(layerPixels, TileGoldHelmet,
                p => PaintArmorHelmet(p, gold, goldHi, goldLo));
            UploadItem(layerPixels, TileGoldChestplate,
                p => PaintArmorChestplate(p, gold, goldHi, goldLo));
            UploadItem(layerPixels, TileGoldLeggings,
                p => PaintArmorLeggings(p, gold, goldHi, goldLo));
            UploadItem(layerPixels, TileGoldBoots,
                p => PaintArmorBoots(p, gold, goldHi, goldLo));
        }

        // Helmet silhouette — a hooded square spanning the top half of
        // the tile. Top edge is rounded by trimming the upper corners
        // so the helmet reads as a curved cap rather than a square
        // block. A small dark visor strip in the lower third gives
        // it readable face-protection geometry.
        private static void PaintArmorHelmet(byte[] pixels,
            (byte r, byte g, byte b) baseC,
            (byte r, byte g, byte b) hi,
            (byte r, byte g, byte b) lo)
        {
            for (int y = 3; y <= 10; y++)
            for (int x = 4; x <= 11; x++)
            {
                // Trim the upper-left and upper-right corner pixels so
                // the crown reads as rounded.
                if (y == 3 && (x == 4 || x == 11)) continue;
                SetPixel(pixels, x, y, baseC.r, baseC.g, baseC.b);
            }
            // Highlight band along the top of the crown.
            for (int x = 5; x <= 10; x++)
                SetPixel(pixels, x, 4, hi.r, hi.g, hi.b);
            // Shadow band along the bottom rim.
            for (int x = 4; x <= 11; x++)
                SetPixel(pixels, x, 10, lo.r, lo.g, lo.b);
            // Visor strip — darker band across the eye line so the
            // helmet has readable face-aperture geometry.
            for (int x = 5; x <= 10; x++)
                SetPixel(pixels, x, 8, lo.r, lo.g, lo.b);
        }

        // Chestplate silhouette — tall body rectangle (4 wide × 8
        // tall) with two narrow shoulder/sleeve columns flanking it.
        // The neckline is dropped one pixel at the top centre so the
        // shape reads as a sleeveless tunic rather than a solid block.
        private static void PaintArmorChestplate(byte[] pixels,
            (byte r, byte g, byte b) baseC,
            (byte r, byte g, byte b) hi,
            (byte r, byte g, byte b) lo)
        {
            // Body fill — 6 wide × 8 tall, centred.
            for (int y = 4; y <= 12; y++)
            for (int x = 5; x <= 10; x++)
                SetPixel(pixels, x, y, baseC.r, baseC.g, baseC.b);
            // Sleeve columns flanking the body.
            for (int y = 5; y <= 9; y++)
            {
                SetPixel(pixels, 4,  y, baseC.r, baseC.g, baseC.b);
                SetPixel(pixels, 11, y, baseC.r, baseC.g, baseC.b);
            }
            // Neckline notch — clear the top centre two pixels so the
            // shoulders read as separate from a hood.
            SetPixel(pixels, 7, 4, 0, 0, 0, 0);
            SetPixel(pixels, 8, 4, 0, 0, 0, 0);
            // Highlight band along the shoulder line.
            for (int x = 5; x <= 10; x++)
                SetPixel(pixels, x, 5, hi.r, hi.g, hi.b);
            // Shadow band along the bottom hem.
            for (int x = 5; x <= 10; x++)
                SetPixel(pixels, x, 12, lo.r, lo.g, lo.b);
        }

        // Leggings silhouette — H-shape: two narrow vertical bars (the
        // legs) joined by a small belt strip at the top.
        private static void PaintArmorLeggings(byte[] pixels,
            (byte r, byte g, byte b) baseC,
            (byte r, byte g, byte b) hi,
            (byte r, byte g, byte b) lo)
        {
            // Belt strip — 6 wide × 2 tall at the top.
            for (int y = 4; y <= 5; y++)
            for (int x = 5; x <= 10; x++)
                SetPixel(pixels, x, y, baseC.r, baseC.g, baseC.b);
            // Left leg bar.
            for (int y = 6; y <= 12; y++)
            for (int x = 5; x <= 7; x++)
                SetPixel(pixels, x, y, baseC.r, baseC.g, baseC.b);
            // Right leg bar.
            for (int y = 6; y <= 12; y++)
            for (int x = 8; x <= 10; x++)
                SetPixel(pixels, x, y, baseC.r, baseC.g, baseC.b);
            // Highlight along the belt top.
            for (int x = 5; x <= 10; x++)
                SetPixel(pixels, x, 4, hi.r, hi.g, hi.b);
            // Shadow at the leg cuffs.
            for (int x = 5; x <= 7; x++)
                SetPixel(pixels, x, 12, lo.r, lo.g, lo.b);
            for (int x = 8; x <= 10; x++)
                SetPixel(pixels, x, 12, lo.r, lo.g, lo.b);
        }

        // Boots silhouette — two small squares side by side, each
        // shaped like a boot (taller than wide, slight forward toe).
        private static void PaintArmorBoots(byte[] pixels,
            (byte r, byte g, byte b) baseC,
            (byte r, byte g, byte b) hi,
            (byte r, byte g, byte b) lo)
        {
            // Left boot — col 4..7, row 8..12.
            for (int y = 8; y <= 12; y++)
            for (int x = 4; x <= 7; x++)
                SetPixel(pixels, x, y, baseC.r, baseC.g, baseC.b);
            // Right boot — col 8..11, row 8..12.
            for (int y = 8; y <= 12; y++)
            for (int x = 8; x <= 11; x++)
                SetPixel(pixels, x, y, baseC.r, baseC.g, baseC.b);
            // Highlight bands along the top of each boot.
            for (int x = 4; x <= 7; x++)
                SetPixel(pixels, x, 8, hi.r, hi.g, hi.b);
            for (int x = 8; x <= 11; x++)
                SetPixel(pixels, x, 8, hi.r, hi.g, hi.b);
            // Shadow soles at the bottom of each boot.
            for (int x = 4; x <= 7; x++)
                SetPixel(pixels, x, 12, lo.r, lo.g, lo.b);
            for (int x = 8; x <= 11; x++)
                SetPixel(pixels, x, 12, lo.r, lo.g, lo.b);
        }

    }
}
