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

            // Tier 8 #51 V9 — Nether ravines. Each chunk inspects its
            // own anchor + the 8 surrounding chunks' anchors; for any
            // anchor that rolled a ravine (1-in-12), we walk that
            // ravine's full path and carve the cells that land within
            // THIS chunk's bounds. The neighbour-walk pattern is what
            // makes ravines span chunk seams seamlessly — without it
            // a ravine that starts in chunk (0,0) and curves into
            // (1,0) would only carve in (0,0), leaving a sharp cliff
            // at the chunk boundary. Cost is bounded: 9 anchors ×
            // ~50 steps × ~500 cells/step ≈ 225k cell checks per
            // chunk worst case, cheap inside the parallel gen pass.
            //
            // Order matters: ravines run AFTER the netherrack mass
            // is placed (so we have rock to carve through) but BEFORE
            // lava lakes (so a ravine that cuts through the y=31
            // lava plane lets the lava settle into the ravine floor
            // instead of generating floating lava blobs above
            // carved-out air).
            CarveRavinesPass(c, seed);

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

        // V9 — Walk this chunk's anchor + its 8 horizontal neighbours.
        // For each anchor that rolled a ravine, simulate the full
        // ravine path and carve cells that land within THIS chunk's
        // bounds. Same RNG for the same anchor regardless of which
        // chunk is doing the carving, so neighbouring chunks agree
        // on the path geometry — the seam across chunk boundaries
        // is continuous.
        private static void CarveRavinesPass(Chunk c, int seed)
        {
            for (int ndz = -1; ndz <= 1; ndz++)
            for (int ndx = -1; ndx <= 1; ndx++)
            {
                int nx = c.ChunkX + ndx;
                int nz = c.ChunkZ + ndz;
                int hash = (int)((uint)seed * 0xA76B5C9Du
                    + (uint)(nx * 0xD7E2A831)
                    + (uint)(nz * 0x91A37FBD));
                var rng = new Random(hash);
                if (rng.Next(12) != 0) continue; // 1-in-12 chunks anchor a ravine

                // Random start point in the (nx, nz) chunk. Y bound
                // to mid-mass so ravines never clip the bedrock floor
                // or the ceiling band.
                float sx = nx * Chunk.SizeX + rng.Next(Chunk.SizeX);
                float sz = nz * Chunk.SizeZ + rng.Next(Chunk.SizeZ);
                float sy = 18f + rng.Next(28);   // y in 18..45
                float yaw = (float)(rng.NextDouble() * Math.PI * 2.0);

                int   steps        = 30 + rng.Next(31);              // 30..60 steps
                float radius       = 2.5f + (float)rng.NextDouble() * 1.5f;   // 2.5..4 horizontal
                float vertExtent   = 6.0f + (float)rng.NextDouble() * 4.0f;   // 6..10 vertical half-extent
                const float StepLen = 1.5f;
                const float YawJitter   = 0.30f;
                const float PitchJitter = 0.20f;

                float curX = sx, curY = sy, curZ = sz;
                for (int s = 0; s < steps; s++)
                {
                    yaw += ((float)rng.NextDouble() - 0.5f) * YawJitter;
                    float pitch = ((float)rng.NextDouble() - 0.5f) * PitchJitter;

                    CarveRavineSegment(c, curX, curY, curZ, radius, vertExtent);

                    float dx = (float)Math.Cos(yaw) * (float)Math.Cos(pitch);
                    float dy = (float)Math.Sin(pitch) * 0.4f; // damped vertical drift
                    float dz = (float)Math.Sin(yaw) * (float)Math.Cos(pitch);
                    curX += dx * StepLen;
                    curY += dy * StepLen;
                    curZ += dz * StepLen;

                    // Early-out if the path drifted far past the
                    // chunk we're carving into — saves the ellipsoid
                    // sweep on cells that can never land in this
                    // chunk's bounds. Rough bound: if the path is
                    // more than 16 + radius blocks outside the
                    // chunk, no further step's capsule can reach
                    // back into the chunk.
                    int chunkX0 = c.ChunkX * Chunk.SizeX;
                    int chunkZ0 = c.ChunkZ * Chunk.SizeZ;
                    float distX = curX < chunkX0 ? chunkX0 - curX
                                : curX > chunkX0 + Chunk.SizeX - 1 ? curX - (chunkX0 + Chunk.SizeX - 1)
                                : 0;
                    float distZ = curZ < chunkZ0 ? chunkZ0 - curZ
                                : curZ > chunkZ0 + Chunk.SizeZ - 1 ? curZ - (chunkZ0 + Chunk.SizeZ - 1)
                                : 0;
                    if (distX > radius + 1f && distZ > radius + 1f) break;
                    // Y bound: keep ravines inside the netherrack mass.
                    if (curY < 6f || curY > NetherrackTop - 4f) break;
                }
            }
        }

        // V9 — Carve one capsule (vertically-stretched ellipsoid)
        // centred at world coords (cx, cy, cz). Iterates only the
        // bounding box that intersects this chunk; cells outside the
        // chunk are clipped, cells inside whose ellipsoid distance
        // is ≤ 1 get cleared to Air. Bedrock is preserved (ravines
        // can't punch through the dimensional floor).
        private static void CarveRavineSegment(Chunk c, float cx, float cy, float cz, float radius, float vertExtent)
        {
            int chunkX0 = c.ChunkX * Chunk.SizeX;
            int chunkZ0 = c.ChunkZ * Chunk.SizeZ;
            int rXZ = (int)Math.Ceiling(radius);
            int rY  = (int)Math.Ceiling(vertExtent);
            int icx = (int)Math.Floor(cx);
            int icy = (int)Math.Floor(cy);
            int icz = (int)Math.Floor(cz);

            int loX = Math.Max(icx - rXZ, chunkX0);
            int hiX = Math.Min(icx + rXZ, chunkX0 + Chunk.SizeX - 1);
            int loZ = Math.Max(icz - rXZ, chunkZ0);
            int hiZ = Math.Min(icz + rXZ, chunkZ0 + Chunk.SizeZ - 1);
            int loY = Math.Max(icy - rY, FloorY + 1);
            int hiY = Math.Min(icy + rY, CeilingBaseY - 1);
            if (loX > hiX || loZ > hiZ || loY > hiY) return;

            float invRadius = 1f / radius;
            float invVert   = 1f / vertExtent;

            for (int wy = loY; wy <= hiY; wy++)
            {
                float fy = (wy - cy) * invVert;
                float fySq = fy * fy;
                if (fySq > 1f) continue;
                for (int wx = loX; wx <= hiX; wx++)
                {
                    float fx = (wx - cx) * invRadius;
                    float fxSq = fx * fx;
                    if (fxSq + fySq > 1f) continue;
                    for (int wz = loZ; wz <= hiZ; wz++)
                    {
                        float fz = (wz - cz) * invRadius;
                        if (fxSq + fySq + fz * fz > 1f) continue;
                        int lx = wx - chunkX0;
                        int lz = wz - chunkZ0;
                        var t = (BlockType)c.RawBlocks[Chunk.Index(lx, wy, lz)];
                        if (t == BlockType.Bedrock) continue;
                        c.Set(lx, wy, lz, BlockType.Air);
                    }
                }
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
