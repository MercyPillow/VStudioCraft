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
        TallGrass = 35,
        FlowingWater = 36,
        FlowingLava = 37,
    }

    internal static class BlockData
    {
        // "Solid" gates player COLLISION only — used by physics/swept AABB.
        // Non-solid: player walks straight through (Air, Water, Torch).
        // Raycast targeting and placement-cell occupancy use IsRaycastTarget /
        // IsCubeShape instead so a torch can be broken without colliding with
        // it as the player walks past.
        public static bool IsSolid(BlockType t)
        {
            switch (t)
            {
                case BlockType.Air:
                case BlockType.Water:
                case BlockType.FlowingWater:
                case BlockType.FlowingLava:
                case BlockType.Torch:
                case BlockType.Dandelion:
                case BlockType.Rose:
                case BlockType.BrownMushroom:
                case BlockType.RedMushroom:
                case BlockType.TallGrass:
                    return false;
                default:
                    return true;
            }
        }

        // "Targetable by raycast" — true for any block the player should be
        // able to LMB-break or RMB-place-against. Air and water are skipped
        // (you raycast through both); torches and future cross-sprite blocks
        // (flowers, mushrooms) are targetable so the player can interact even
        // though they aren't collidable.
        public static bool IsRaycastTarget(BlockType t)
        {
            switch (t)
            {
                case BlockType.Air:
                case BlockType.Water:
                case BlockType.FlowingWater:
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
            switch (t)
            {
                case BlockType.Torch:
                case BlockType.Dandelion:
                case BlockType.Rose:
                case BlockType.BrownMushroom:
                case BlockType.RedMushroom:
                case BlockType.TallGrass:
                    return false;
                default:
                    return true;
            }
        }

        // "Opaque" means "occludes the face of a neighbouring block." Used by
        // the greedy mesher to decide whether to emit a face. Water (and later
        // glass / leaves) are non-opaque so stone shows its face underwater.
        public static bool IsOpaque(BlockType t)
        {
            switch (t)
            {
                case BlockType.Air:
                case BlockType.Water:
                case BlockType.FlowingWater:
                case BlockType.FlowingLava:
                // Cross-sprite blocks don't fill the cell. If they were marked
                // opaque the cube sweep would cull the faces of the block
                // beneath them (so the grass under a torch loses its top face)
                // AND the four side neighbours (so you see through into the
                // chunk because their facing wall didn't get a quad emitted).
                case BlockType.Torch:
                case BlockType.Dandelion:
                case BlockType.Rose:
                case BlockType.BrownMushroom:
                case BlockType.RedMushroom:
                case BlockType.TallGrass:
                    return false;
                default:
                    return true;
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
            switch (t)
            {
                case BlockType.Air:
                case BlockType.Water:
                case BlockType.FlowingWater:
                case BlockType.FlowingLava:
                case BlockType.Glass:
                case BlockType.Leaves:
                case BlockType.Torch:
                case BlockType.Dandelion:
                case BlockType.Rose:
                case BlockType.BrownMushroom:
                case BlockType.RedMushroom:
                case BlockType.TallGrass:
                    return true;
                default:
                    return false;
            }
        }

        // 0..15 luminous emission. Lava glows full bright. Torches sit at 14 —
        // matches Alpha so a torch placed against a wall lights about 14 cells
        // before fading out, leaving the 15th cell almost dark. Glowstone/fire
        // slot in here.
        public static int LightEmission(BlockType t)
        {
            switch (t)
            {
                case BlockType.Lava:
                case BlockType.FlowingLava:
                    return 15;
                case BlockType.Torch:
                    return 14;
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
                    return 1.5f;
                case BlockType.WoodLog:
                case BlockType.Planks:
                case BlockType.Bookshelf:
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
                case BlockType.TallGrass:
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
                    return BlockTextures.TileTorch;
                case BlockType.Dandelion:
                    return BlockTextures.TileDandelion;
                case BlockType.Rose:
                    return BlockTextures.TileRose;
                case BlockType.BrownMushroom:
                    return BlockTextures.TileBrownMushroom;
                case BlockType.RedMushroom:
                    return BlockTextures.TileRedMushroom;
                case BlockType.TallGrass:
                    return BlockTextures.TileTallGrass;
                default:
                    return BlockTextures.TileStone;
            }
        }
    }
}
