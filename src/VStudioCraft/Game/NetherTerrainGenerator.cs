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

            // Tier 8 #51 V6 — Per-column varied ceiling height. Each
            // (x, z) column rolls a 0..3 cell descent off the
            // canonical CeilingBaseY, producing visible "valleys"
            // and "peaks" in the netherrack ceiling so the cavern
            // doesn't read as a flat-top prism. Hashed per-column
            // so neighbouring chunks line up at their seam without
            // a noise pass.
            int[] ceilingTopForColumn = new int[Chunk.SizeX * Chunk.SizeZ];
            for (int x = 0; x < Chunk.SizeX; x++)
            for (int z = 0; z < Chunk.SizeZ; z++)
            {
                // Per-cell hash on the WORLD coords so chunk seams
                // match. Same prime-multiply mixer the chunk-level
                // hash uses.
                int wx = c.ChunkX * Chunk.SizeX + x;
                int wz = c.ChunkZ * Chunk.SizeZ + z;
                uint h = (uint)seed * 0x9E3779B1u
                       + (uint)(wx * 0x85EBCA77)
                       + (uint)(wz * 0xC2B2AE3D);
                int dropAmount = (int)(h % 4u); // 0..3 cells of descent
                int ceilingTop = CeilingBaseY - dropAmount;
                ceilingTopForColumn[x * Chunk.SizeZ + z] = ceilingTop;
            }

            // Netherrack mass: y=1..NetherrackTop, full fill.
            for (int x = 0; x < Chunk.SizeX; x++)
            for (int z = 0; z < Chunk.SizeZ; z++)
            for (int y = FloorY + 1; y <= NetherrackTop; y++)
                c.Set(x, y, z, BlockType.Netherrack);

            // Ceiling band: per-column variable top — fill from each
            // column's ceilingTop up to CeilingCapY-1.
            for (int x = 0; x < Chunk.SizeX; x++)
            for (int z = 0; z < Chunk.SizeZ; z++)
            {
                int ceilingTop = ceilingTopForColumn[x * Chunk.SizeZ + z];
                for (int y = ceilingTop; y < CeilingCapY; y++)
                    c.Set(x, y, z, BlockType.Netherrack);
            }

            // Lava lakes at y=LavaLakeY. V6: bumped from 0..2 to
            // 1..3 lakes per chunk and lake radius from 2..4 to
            // 3..6 — wider visible lava seas matching canonical
            // Alpha imagery. Lakes occasionally carve through the
            // mass surface (cells visible from above) since the
            // post-pass also cuts a few floor holes.
            int lakeCount = 1 + rng.Next(3);
            for (int i = 0; i < lakeCount; i++)
            {
                int cx = rng.Next(Chunk.SizeX);
                int cz = rng.Next(Chunk.SizeZ);
                int radius = 3 + rng.Next(4);
                CarveLavaPuddle(c, cx, LavaLakeY, cz, radius);
            }

            // V6 — Surface lava lakes. Independent of the deep ones
            // above. Each chunk rolls 0..1 surface lake centred at
            // y=NetherrackTop with a smaller radius so the lake
            // reads as a contained pool the player can fall into.
            if (rng.Next(3) == 0)
            {
                int slx = rng.Next(Chunk.SizeX);
                int slz = rng.Next(Chunk.SizeZ);
                int slr = 2 + rng.Next(3);
                CarveSurfaceLavaPool(c, slx, NetherrackTop, slz, slr);
            }

            // Glowstone clusters near the ceiling. V6: cluster size
            // bumped from 3..6 to 4..9 cells, and we anchor each
            // cluster at the COLUMN'S actual ceiling top (not the
            // flat CeilingBaseY) so glowstone stalactites hang off
            // the highest visible ceiling cells.
            int clusterCount = 2 + rng.Next(3);
            for (int i = 0; i < clusterCount; i++)
            {
                int cx = rng.Next(Chunk.SizeX);
                int cz = rng.Next(Chunk.SizeZ);
                int anchorY = ceilingTopForColumn[cx * Chunk.SizeZ + cz];
                int size = 4 + rng.Next(6);
                CarveGlowstoneCluster(c, cx, anchorY, cz, size, rng);
            }

            // Soul-sand patches on the netherrack mass surface.
            // V6: occasionally cluster 4..9 cells together so a patch
            // reads as a recognisable obstacle field rather than
            // sparse single-cell speckles. Also bumped per-column
            // chance from 1-in-12 to 1-in-9 so patches are denser.
            for (int x = 0; x < Chunk.SizeX; x++)
            for (int z = 0; z < Chunk.SizeZ; z++)
            {
                if (rng.Next(9) == 0)
                    c.Set(x, NetherrackTop, z, BlockType.SoulSand);
            }
            // V6 — Soul-sand cluster pass. 0..2 clusters per chunk,
            // 4..9 cells each, random walk like the glowstone pass.
            int sandClusters = rng.Next(0, 3);
            for (int i = 0; i < sandClusters; i++)
            {
                int cx = rng.Next(Chunk.SizeX);
                int cz = rng.Next(Chunk.SizeZ);
                int size = 4 + rng.Next(6);
                CarveSoulSandPatch(c, cx, NetherrackTop, cz, size, rng);
            }

            // V6 — Gravel patches on the netherrack surface. Same
            // canonical Alpha quirk: gravel deposits scattered
            // through the netherrack. 0..1 patch per chunk, 3..7
            // cells, surface-level only (y = NetherrackTop).
            if (rng.Next(2) == 0)
            {
                int gx = rng.Next(Chunk.SizeX);
                int gz = rng.Next(Chunk.SizeZ);
                int size = 3 + rng.Next(5);
                CarveGravelPatch(c, gx, NetherrackTop, gz, size, rng);
            }
        }

        // V6 — Surface lava pool: same shape as the deep puddle but
        // also clears the netherrack ABOVE so the lava is visible /
        // walkable-into from the cavern. Carves 1 cell up from the
        // surface to make it a true sunken pool.
        private static void CarveSurfaceLavaPool(Chunk c, int cx, int cy, int cz, int radius)
        {
            for (int dx = -radius; dx <= radius; dx++)
            for (int dz = -radius; dz <= radius; dz++)
            {
                int x = cx + dx, z = cz + dz;
                if (x < 0 || x >= Chunk.SizeX || z < 0 || z >= Chunk.SizeZ) continue;
                int sq = dx * dx + dz * dz;
                if (sq > radius * radius) continue;
                c.Set(x, cy, z, BlockType.Lava);
                // Clear the cell above so the pool reads as open
                // (otherwise the netherrack column above would
                // hide the lava entirely).
                if (cy + 1 < Chunk.SizeY)
                    c.Set(x, cy + 1, z, BlockType.Air);
            }
        }

        // V6 — Soul-sand cluster: random walk replacing surface
        // netherrack with soul sand. Same shape as the glowstone
        // walker but anchored at the surface y (not the ceiling)
        // and biased toward staying flat (no down-bias).
        private static void CarveSoulSandPatch(Chunk c, int cx, int cy, int cz, int size, Random rng)
        {
            int x = cx, z = cz;
            for (int i = 0; i < size; i++)
            {
                if (x < 0 || x >= Chunk.SizeX || z < 0 || z >= Chunk.SizeZ) return;
                c.Set(x, cy, z, BlockType.SoulSand);
                int dir = rng.Next(4);
                switch (dir)
                {
                    case 0: x++; break;
                    case 1: x--; break;
                    case 2: z++; break;
                    case 3: z--; break;
                }
            }
        }

        // V6 — Gravel patch: same flat-walk shape as soul sand but
        // replacing the surface with Gravel. Adds visual variety
        // to the netherrack basin without needing a full ravine
        // gen pass.
        private static void CarveGravelPatch(Chunk c, int cx, int cy, int cz, int size, Random rng)
        {
            int x = cx, z = cz;
            for (int i = 0; i < size; i++)
            {
                if (x < 0 || x >= Chunk.SizeX || z < 0 || z >= Chunk.SizeZ) return;
                c.Set(x, cy, z, BlockType.Gravel);
                int dir = rng.Next(4);
                switch (dir)
                {
                    case 0: x++; break;
                    case 1: x--; break;
                    case 2: z++; break;
                    case 3: z--; break;
                }
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
