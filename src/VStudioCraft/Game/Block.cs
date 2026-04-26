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
                case BlockType.Bowl:      return 281;
                default:                  return -1;
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
                case BlockType.Bowl:      return "Bowl";
                default:                  return t.ToString();
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
                    return false;
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
        // placeable block. Cheap range check — tool ids occupy the
        // contiguous slice [WoodSword..GoldAxe]. Used by mesher,
        // placement, rendering and inventory paths to take the tool
        // branch without touching the per-block switch tables.
        public static bool IsTool(BlockType t)
            => (byte)t >= (byte)BlockType.WoodSword && (byte)t <= (byte)BlockType.GoldAxe;

        // True if this BlockType id refers to a non-placeable, non-tool
        // inventory item (Stick, Coal, ingots, gem, Flint, ClayBall /
        // Brick, Bowl). Same range-check pattern as IsTool — items
        // occupy the contiguous slice [Stick..Bowl]. Renderers,
        // placement, mesher, and inventory branches use this to take
        // the "flat sprite, no world cell" path identically to tools.
        public static bool IsItem(BlockType t)
            => (byte)t >= (byte)BlockType.Stick && (byte)t <= (byte)BlockType.Bowl;

        // "Targetable by raycast" — true for any block the player should be
        // able to LMB-break or RMB-place-against. Air and fluid families are
        // skipped (you raycast through both); torches and future cross-sprite
        // blocks (flowers, mushrooms) are targetable so the player can
        // interact even though they aren't collidable. Both fluid sources
        // (Water / Lava) and their flowing variants are non-targetable —
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
                    return 1.5f;
                case BlockType.WoodLog:
                case BlockType.Planks:
                case BlockType.Bookshelf:
                case BlockType.CraftingTable:
                case BlockType.Chest:
                    return 2f;
                case BlockType.Dirt:
                case BlockType.Grass:
                case BlockType.Sand:
                case BlockType.Gravel:
                case BlockType.Clay:
                    return 0.5f;
                case BlockType.Wool:
                    return 0.8f;
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
                case BlockType.Obsidian:
                    return BlockTextures.TileObsidian;
                case BlockType.Sponge:
                    return BlockTextures.TileSponge;
                case BlockType.Glass:
                    return BlockTextures.TileGlass;
                case BlockType.Wool:
                    return BlockTextures.TileWool;
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
                default:
                    return BlockTextures.TileStone;
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
    }

    // Tool kind drives which block family the tool is "effective" against
    // (faster break + drop eligibility for ores). None means "this stack
    // isn't a tool" — the helpers below return defensive defaults so a
    // bare-hand swing on stone still drops nothing without crashing the
    // break path. Hoes are intentionally absent: there's no farming yet
    // and the items.png hoe row is left unmapped.
    internal enum ToolKind { None, Sword, Shovel, Pickaxe, Axe }

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
                default: return block;
            }
        }
    }
}
