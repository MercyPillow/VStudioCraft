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
    }

    internal static class BlockData
    {
        // "Solid" means blocks player movement / raycast targeting / new-block
        // placement. Water reads as non-solid so you can wade and target blocks
        // through it.
        public static bool IsSolid(BlockType t)
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
                default:
                    return BlockTextures.TileStone;
            }
        }
    }
}
