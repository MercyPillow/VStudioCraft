namespace VStudioCraft.Game
{
    internal enum BlockType : byte
    {
        Air = 0,
        Grass = 1,
        Dirt = 2,
        Stone = 3,
        Sand = 4,
    }

    internal static class BlockData
    {
        public static bool IsSolid(BlockType t) => t != BlockType.Air;

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
                default:
                    return BlockTextures.TileStone;
            }
        }
    }
}
