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
                    return false;
                default:
                    return true;
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
                    return 15;
                case BlockType.Torch:
                    return 14;
                default:
                    return 0;
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
                    return BlockTextures.TileWater;
                case BlockType.Lava:
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
