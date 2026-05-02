using System;

namespace VStudioCraft.Game
{
    // Tier 8 #51 V4 — Nether dimension chunk generator. Replaces
    // TerrainGenerator + dungeon + cave passes for chunks that live
    // in the Nether dimension. The shape is canonical Alpha 1.1.2_01:
    //
    //   y = 0       : Bedrock floor
    //   y = 1..63   : Netherrack mass with carved lava lakes around y=31
    //   y = 31      : Surface lava lakes (small puddles inside the mass)
    //   y = 64..119 : Hollow cavernous open space
    //   y = 120..126: Netherrack ceiling with glowstone clusters punched in
    //   y = 127     : Bedrock ceiling cap
    //
    // Soul-sand patches are scattered along the netherrack surface
    // (where the mass meets the hollow cavern) so a player walking
    // the floor occasionally hits a slow-down patch. Glowstone is
    // the primary illumination — clusters of 3..6 cells punched into
    // the ceiling, giving the Nether its distinctive dim-amber lighting.
    //
    // Deterministic per-chunk RNG keyed on (seed, chunkX, chunkZ) so
    // the same Nether chunk regenerates identically across sessions
    // (matters even though V4 part 1 doesn't persist the Nether to
    // disk — a player who teleports out and back in within the same
    // session sees the same terrain rather than a fresh roll).
    internal static class NetherTerrainGenerator
    {
        public const int FloorY        = 0;
        public const int NetherrackTop = 64;   // top of the solid netherrack mass
        public const int LavaLakeY     = 31;
        public const int CeilingBaseY  = 120;
        public const int CeilingCapY   = 127;

        public static void Generate(Chunk c, int seed)
        {
            // Per-chunk hashed RNG — same shape as the overworld
            // dungeon-gen / passive-spawn pattern.
            int hash = (int)((uint)seed * 0x9E3779B1u
                + (uint)(c.ChunkX * 0x85EBCA77)
                + (uint)(c.ChunkZ * 0xC2B2AE3D));
            var rng = new Random(hash);

            // Bedrock floor + cap.
            for (int x = 0; x < Chunk.SizeX; x++)
            for (int z = 0; z < Chunk.SizeZ; z++)
            {
                c.Set(x, FloorY, z, BlockType.Bedrock);
                c.Set(x, CeilingCapY, z, BlockType.Bedrock);
            }

            // Netherrack mass: y=1..NetherrackTop, full fill.
            for (int x = 0; x < Chunk.SizeX; x++)
            for (int z = 0; z < Chunk.SizeZ; z++)
            for (int y = FloorY + 1; y <= NetherrackTop; y++)
                c.Set(x, y, z, BlockType.Netherrack);

            // Ceiling band: y=CeilingBaseY..CeilingCapY-1, full fill of netherrack.
            for (int x = 0; x < Chunk.SizeX; x++)
            for (int z = 0; z < Chunk.SizeZ; z++)
            for (int y = CeilingBaseY; y < CeilingCapY; y++)
                c.Set(x, y, z, BlockType.Netherrack);

            // Lava lakes at y=LavaLakeY: carve a few small puddles
            // into the netherrack mass and fill them with lava. Each
            // chunk rolls 0..2 lakes; lake size 2..4 cells radius.
            int lakeCount = rng.Next(0, 3);
            for (int i = 0; i < lakeCount; i++)
            {
                int cx = rng.Next(Chunk.SizeX);
                int cz = rng.Next(Chunk.SizeZ);
                int radius = 2 + rng.Next(3);
                CarveLavaPuddle(c, cx, LavaLakeY, cz, radius);
            }

            // Glowstone clusters at the bottom of the ceiling band
            // (y = CeilingBaseY) — clusters of 3..6 cells punched
            // into the ceiling so the bottom of the netherrack
            // ceiling looks like illuminated stalactites. Roll a few
            // per chunk so the cavern is reasonably lit.
            int clusterCount = 1 + rng.Next(3);
            for (int i = 0; i < clusterCount; i++)
            {
                int cx = rng.Next(Chunk.SizeX);
                int cz = rng.Next(Chunk.SizeZ);
                int size = 3 + rng.Next(4);
                CarveGlowstoneCluster(c, cx, CeilingBaseY, cz, size, rng);
            }

            // Soul-sand patches on the netherrack mass surface.
            // Per-column roll: 1-in-12 chance the y=NetherrackTop
            // cell becomes Soul Sand instead of Netherrack. Sparse
            // enough that walking is normally fast; the player
            // hits the slowdown occasionally as a feature.
            for (int x = 0; x < Chunk.SizeX; x++)
            for (int z = 0; z < Chunk.SizeZ; z++)
            {
                if (rng.Next(12) == 0)
                    c.Set(x, NetherrackTop, z, BlockType.SoulSand);
            }
        }

        // Carve a small lava puddle: replace netherrack within
        // `radius` cells of (cx, cy, cz) with Lava. Uses a square
        // metric (the visible result reads like a flat-bottomed
        // pool with ragged edges due to the chunk-boundary clipping).
        private static void CarveLavaPuddle(Chunk c, int cx, int cy, int cz, int radius)
        {
            for (int dx = -radius; dx <= radius; dx++)
            for (int dz = -radius; dz <= radius; dz++)
            {
                int x = cx + dx, z = cz + dz;
                if (x < 0 || x >= Chunk.SizeX || z < 0 || z >= Chunk.SizeZ) continue;
                int sq = dx * dx + dz * dz;
                if (sq > radius * radius) continue;
                c.Set(x, cy, z, BlockType.Lava);
            }
        }

        // Punch a glowstone cluster into the ceiling — random walk
        // from (cx, cy, cz) for `size` steps, replacing each visited
        // cell with Glowstone. The walk biases slightly downward so
        // the cluster hangs visibly off the ceiling rather than just
        // sitting flat.
        private static void CarveGlowstoneCluster(Chunk c, int cx, int cy, int cz, int size, Random rng)
        {
            int x = cx, y = cy, z = cz;
            for (int i = 0; i < size; i++)
            {
                if (x < 0 || x >= Chunk.SizeX || z < 0 || z >= Chunk.SizeZ) return;
                if (y < CeilingBaseY || y >= CeilingCapY) return;
                c.Set(x, y, z, BlockType.Glowstone);
                int dir = rng.Next(5);
                switch (dir)
                {
                    case 0: x++; break;
                    case 1: x--; break;
                    case 2: z++; break;
                    case 3: z--; break;
                    case 4: y--; break; // down-bias — cluster hangs off the ceiling
                }
            }
        }
    }
}
