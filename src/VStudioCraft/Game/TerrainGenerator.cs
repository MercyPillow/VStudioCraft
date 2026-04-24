namespace VStudioCraft.Game
{
    internal static class TerrainGenerator
    {
        public const int BaseHeight = 24;
        public const int HeightAmplitude = 14;
        // Columns whose surface sits at or below this height become sandy — a stand-in
        // for Alpha-style beaches until we have water to anchor them to a sea level.
        private const int BeachHeight = BaseHeight - 9;

        public static void Generate(Chunk chunk, Noise noise)
        {
            for (int x = 0; x < Chunk.SizeX; x++)
            for (int z = 0; z < Chunk.SizeZ; z++)
            {
                float wx = chunk.ChunkX * Chunk.SizeX + x;
                float wz = chunk.ChunkZ * Chunk.SizeZ + z;

                float n = noise.Octaves(wx * 0.02f, wz * 0.02f, 4);
                int height = BaseHeight + (int)(n * HeightAmplitude);
                if (height < 1) height = 1;
                if (height >= Chunk.SizeY) height = Chunk.SizeY - 1;

                bool sandy = height <= BeachHeight;
                for (int y = 0; y < height; y++)
                {
                    BlockType t;
                    if (sandy)
                    {
                        if (y >= height - 4) t = BlockType.Sand;
                        else t = BlockType.Stone;
                    }
                    else
                    {
                        if (y == height - 1) t = BlockType.Grass;
                        else if (y >= height - 4) t = BlockType.Dirt;
                        else t = BlockType.Stone;
                    }
                    chunk.Set(x, y, z, t);
                }
            }
        }
    }
}
