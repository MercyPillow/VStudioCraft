using System;

namespace VStudioCraft.Game
{
    internal static class TerrainGenerator
    {
        public const int BaseHeight = 24;
        public const int HeightAmplitude = 14;
        // Columns whose surface sits at or below this height become sandy — a stand-in
        // for Alpha-style beaches until we have water to anchor them to a sea level.
        private const int BeachHeight = BaseHeight - 9;

        // Canopy radius so trees that seed near a chunk edge can extend their
        // leaves into this chunk. Keep in lockstep with PlaceOakTree's 5x5 slab.
        private const int TreeBorder = 2;

        public static void Generate(Chunk chunk, Noise noise)
        {
            GenerateColumns(chunk, noise);
            GenerateBedrock(chunk, noise);
            GenerateOresAndPatches(chunk, noise);
            GenerateTrees(chunk, noise);
        }

        // ---------- Pass 1: heightmap columns (stone / dirt / grass / sand). ----------

        private static void GenerateColumns(Chunk chunk, Noise noise)
        {
            for (int x = 0; x < Chunk.SizeX; x++)
            for (int z = 0; z < Chunk.SizeZ; z++)
            {
                int height = SurfaceHeight(noise, chunk.ChunkX * Chunk.SizeX + x, chunk.ChunkZ * Chunk.SizeZ + z);
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

        // Surface height for any world (x, z). Used by both column gen and the
        // tree pass (which needs to know surface height for columns it seeds
        // from neighbour chunks without having to read that chunk's blocks).
        public static int SurfaceHeight(Noise noise, int wx, int wz)
        {
            float n = noise.Octaves(wx * 0.02f, wz * 0.02f, 4);
            int height = BaseHeight + (int)(n * HeightAmplitude);
            if (height < 1) height = 1;
            if (height >= Chunk.SizeY) height = Chunk.SizeY - 1;
            return height;
        }

        // ---------- Pass 2: bedrock floor (y=0 always, then ragged up to y=3). ----------

        private static void GenerateBedrock(Chunk chunk, Noise noise)
        {
            var rng = ChunkRng(noise.Seed, chunk.ChunkX, chunk.ChunkZ, 0xBED);
            for (int x = 0; x < Chunk.SizeX; x++)
            for (int z = 0; z < Chunk.SizeZ; z++)
            {
                chunk.Set(x, 0, z, BlockType.Bedrock);
                // Diminishing probability for the three layers above so the
                // bedrock ceiling is ragged rather than a flat slab.
                if (rng.Next(5) < 3) chunk.Set(x, 1, z, BlockType.Bedrock); // ~60%
                if (rng.Next(5) < 2) chunk.Set(x, 2, z, BlockType.Bedrock); // ~40%
                if (rng.Next(5) < 1) chunk.Set(x, 3, z, BlockType.Bedrock); // ~20%
            }
        }

        // ---------- Pass 3: dirt/gravel patches + ore veins inside stone. ----------

        private static void GenerateOresAndPatches(Chunk chunk, Noise noise)
        {
            // Per-ore counts/sizes & y ranges tuned to evoke Alpha 1.1.2_01's
            // distribution — slightly lower density so testing worlds don't look
            // like Swiss cheese. Iron/gold/redstone/diamond ceilings match Alpha.
            GenerateVeins(chunk, noise, 0xD17, BlockType.Dirt,        count: 10, size: 14, yMin: 4,  yMax: Chunk.SizeY - 2, stoneOnly: true);
            GenerateVeins(chunk, noise, 0x6A6, BlockType.Gravel,      count: 8,  size: 14, yMin: 4,  yMax: Chunk.SizeY - 2, stoneOnly: true);
            GenerateVeins(chunk, noise, 0xC0A, BlockType.CoalOre,     count: 15, size: 8,  yMin: 4,  yMax: Chunk.SizeY - 2, stoneOnly: true);
            GenerateVeins(chunk, noise, 0x190, BlockType.IronOre,     count: 12, size: 6,  yMin: 4,  yMax: 64,              stoneOnly: true);
            GenerateVeins(chunk, noise, 0x601, BlockType.GoldOre,     count: 2,  size: 5,  yMin: 4,  yMax: 32,              stoneOnly: true);
            GenerateVeins(chunk, noise, 0xDED, BlockType.RedstoneOre, count: 6,  size: 5,  yMin: 4,  yMax: 16,              stoneOnly: true);
            GenerateVeins(chunk, noise, 0xD1A, BlockType.DiamondOre,  count: 1,  size: 5,  yMin: 4,  yMax: 16,              stoneOnly: true);
        }

        private static void GenerateVeins(Chunk chunk, Noise noise, int tag, BlockType replacement, int count, int size, int yMin, int yMax, bool stoneOnly)
        {
            var rng = ChunkRng(noise.Seed, chunk.ChunkX, chunk.ChunkZ, tag);
            byte replByte = (byte)replacement;
            for (int v = 0; v < count; v++)
            {
                int cx = rng.Next(Chunk.SizeX);
                int cy = yMin + rng.Next(Math.Max(1, yMax - yMin + 1));
                int cz = rng.Next(Chunk.SizeZ);

                // Random walk a short blob. Short length × many starts gives the
                // look of small clustered pockets instead of long stringy veins.
                for (int step = 0; step < size; step++)
                {
                    if ((uint)cx < Chunk.SizeX && (uint)cy < Chunk.SizeY && (uint)cz < Chunk.SizeZ)
                    {
                        int idx = Chunk.Index(cx, cy, cz);
                        if (!stoneOnly || chunk.RawBlocks[idx] == (byte)BlockType.Stone)
                            chunk.RawBlocks[idx] = replByte;
                    }
                    int dir = rng.Next(6);
                    if      (dir == 0) cx++;
                    else if (dir == 1) cx--;
                    else if (dir == 2) cy++;
                    else if (dir == 3) cy--;
                    else if (dir == 4) cz++;
                    else               cz--;
                }
            }
        }

        // ---------- Pass 4: oak trees, seeded deterministically per world column. ----------

        private static void GenerateTrees(Chunk chunk, Noise noise)
        {
            // Iterate every world column that could *seed* a tree whose canopy
            // overlaps this chunk. Each column's RNG is derived from the world
            // seed + (wx, wz) so the same tree always grows whether we enter
            // this chunk from the east or the west.
            int baseX = chunk.ChunkX * Chunk.SizeX;
            int baseZ = chunk.ChunkZ * Chunk.SizeZ;
            for (int wx = baseX - TreeBorder; wx < baseX + Chunk.SizeX + TreeBorder; wx++)
            for (int wz = baseZ - TreeBorder; wz < baseZ + Chunk.SizeZ + TreeBorder; wz++)
            {
                var colRng = new Random(ColumnHash(noise.Seed, wx, wz));

                // ~1/240 columns seed a tree. 256 cols/chunk → ~1.1 trees avg,
                // roughly matching the sparser oak scatter in Alpha's default biome.
                if (colRng.Next(240) != 0) continue;

                int surface = SurfaceHeight(noise, wx, wz);
                if (surface <= BeachHeight) continue;      // no trees on beach
                int groundY = surface - 1;                  // top grass block

                int trunkHeight = 4 + colRng.Next(3);       // 4, 5, or 6
                int topY = groundY + trunkHeight;
                if (topY + 1 >= Chunk.SizeY) continue;      // no headroom

                PlaceOakTree(chunk, wx, wz, groundY, trunkHeight, colRng);
            }
        }

        private static void PlaceOakTree(Chunk chunk, int wx, int wz, int groundY, int trunkHeight, Random rng)
        {
            int topY = groundY + trunkHeight;

            // Trunk: logs overwrite whatever's there (air, and the grass block
            // at groundY stays grass — we start logs at groundY + 1).
            for (int y = groundY + 1; y <= topY; y++)
                TrySetBlock(chunk, wx, y, wz, BlockType.WoodLog, overwrite: true);

            // Canopy slab (top-2 and top-1): 5×5, corners clipped randomly.
            for (int dy = -2; dy <= -1; dy++)
            for (int dx = -2; dx <= 2;  dx++)
            for (int dz = -2; dz <= 2;  dz++)
            {
                if (Math.Abs(dx) == 2 && Math.Abs(dz) == 2 && rng.Next(2) == 0) continue;
                TrySetBlock(chunk, wx + dx, topY + dy, wz + dz, BlockType.Leaves, overwrite: false);
            }

            // Top layer: 3×3 around the trunk tip (trunk log stays in the center).
            for (int dx = -1; dx <= 1; dx++)
            for (int dz = -1; dz <= 1; dz++)
                TrySetBlock(chunk, wx + dx, topY, wz + dz, BlockType.Leaves, overwrite: false);

            // Crown: single leaf above, plus the four adjacent positions, making
            // a plus-sign cap like Alpha's default oak shape.
            TrySetBlock(chunk, wx,     topY + 1, wz,     BlockType.Leaves, overwrite: false);
            TrySetBlock(chunk, wx + 1, topY + 1, wz,     BlockType.Leaves, overwrite: false);
            TrySetBlock(chunk, wx - 1, topY + 1, wz,     BlockType.Leaves, overwrite: false);
            TrySetBlock(chunk, wx,     topY + 1, wz + 1, BlockType.Leaves, overwrite: false);
            TrySetBlock(chunk, wx,     topY + 1, wz - 1, BlockType.Leaves, overwrite: false);
        }

        private static void TrySetBlock(Chunk chunk, int wx, int wy, int wz, BlockType t, bool overwrite)
        {
            int lx = wx - chunk.ChunkX * Chunk.SizeX;
            int lz = wz - chunk.ChunkZ * Chunk.SizeZ;
            if ((uint)lx >= Chunk.SizeX || (uint)lz >= Chunk.SizeZ) return;
            if ((uint)wy >= Chunk.SizeY) return;
            int idx = Chunk.Index(lx, wy, lz);
            if (overwrite || chunk.RawBlocks[idx] == (byte)BlockType.Air)
                chunk.RawBlocks[idx] = (byte)t;
        }

        // ---------- Deterministic hash helpers. ----------

        // Per-column hash — stable regardless of which chunk is being generated.
        // Lets the tree pass seed the same PRNG from any neighbouring chunk and
        // get the same canopy shape, so trees never straddle a seam.
        private static int ColumnHash(int seed, int wx, int wz)
        {
            unchecked
            {
                int h = seed;
                h = h * 73856093 ^ wx;
                h = h * 19349663 ^ wz;
                h ^= h >> 16;
                return h;
            }
        }

        // Per-chunk, per-pass RNG. `tag` disambiguates ore types / passes so
        // their random walks don't overlap each other's streams.
        private static Random ChunkRng(int seed, int cx, int cz, int tag)
        {
            unchecked
            {
                int h = seed;
                h = h * 73856093 ^ cx;
                h = h * 19349663 ^ cz;
                h = h * 83492791 ^ tag;
                h ^= h >> 16;
                return new Random(h);
            }
        }
    }
}
