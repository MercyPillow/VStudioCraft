using System;

namespace VStudioCraft.Game
{
    // Tier 8 #51 V12 — Alpha-canonical Nether dimension chunk generator.
    // Replaces the previous bespoke 2D-perturbation-mountain pass with
    // the Beta 1.7.3 / Alpha 1.2.0 algorithm: a 3D density field
    // (sampled via the shared AlphaTerrainNoiseSampler), per-cell
    // mapped to netherrack-or-lava-or-air. The result reads as the
    // canonical Halloween-update Nether — a deep lava ocean filling
    // the lower half of the dimension, 3D netherrack mass distributed
    // throughout (pillars, blobs, bridges, floating chunks), partial-
    // bedrock floor and ceiling.
    //
    //   y = 0          : Full bedrock floor
    //   y = 1..4       : Random partial bedrock (probability rises as y → 0)
    //   y = 1..63      : Netherrack-or-lava per density. Voids below sea
    //                    level fill with lava — the lava ocean.
    //   y = 64         : Sea level (lava ocean surface)
    //   y = 64..119    : Netherrack-or-air per density (3D blobs,
    //                    pillars, bridges, occasional floating chunks)
    //   y = 120..123   : Top fade — density biased toward air so the
    //                    cavern top is visibly clear before the cap
    //   y = 123..126   : Random partial bedrock ceiling
    //   y = 127        : Full bedrock cap
    //
    // Density sampling: 5×5×17 grid via AlphaTerrainNoiseSampler with
    // Mode.Nether envelope; trilinearly interpolated to per-cell values.
    // Sea level Y=64 follows Beta canonical (lava fills voids below it).
    //
    // Deterministic per-chunk RNG keyed on (seed, chunkX, chunkZ) for
    // surface decorations, fortresses, and glowstone clusters.
    internal static class NetherTerrainGenerator
    {
        public const int FloorY            = 0;
        public const int BedrockFloorTop   = 4;
        public const int LavaSeaLevel      = 64;
        // NetherrackTop kept as a public alias for back-compat with the
        // mob-spawn passes in World.cs. New value = lava sea level so
        // pigman / ghast / blaze hover-band arithmetic still produces
        // sensible Y ranges (sea+12 = ghast hover lo, etc.).
        public const int NetherrackTop     = LavaSeaLevel;
        // Bedrock sits directly on top of the netherrack ceiling. The
        // density envelope produces solid netherrack up to ~y=120, and
        // partial bedrock starts at y=122 (probability rising toward
        // y=127). User experience: digging straight up from the cavern,
        // the player breaks ~5..10 netherrack cells then hits bedrock.
        // CeilingFadeStart kept for compatibility with mob-spawn band
        // calculations in World.cs (ghast/blaze hover-altitude bands)
        // — it now marks "top of the open cavern" rather than a noise-
        // envelope fade boundary (the sampler skips top-fade for the
        // Nether so there's no actual fade region anymore).
        public const int CeilingFadeStart  = 110;
        public const int BedrockCeilingLo  = 122;
        public const int CeilingCapY       = 127;

        // Fortress: rarer per user preference. Was 1-in-2 grid spawn,
        // now 1-in-8. Floor moved up to mid-cavern (sea + 8 = 72).
        public const int FortressGridStep    = 8;
        public const int FortressSpawnDenom  = 8;
        public const int FortressFootprint   = 3;
        public const int FortressFloorY      = LavaSeaLevel + 8;     // 72
        public const int FortressWallTopY    = FortressFloorY + 4;   // 76
        public const int FortressCeilingY    = FortressFloorY + 5;   // 77

        public static void Generate(Chunk c, int seed, AlphaTerrainNoiseSampler sampler)
        {
            // Per-chunk hashed RNG for stochastic features.
            int hash = (int)((uint)seed * 0x9E3779B1u
                + (uint)(c.ChunkX * 0x85EBCA77)
                + (uint)(c.ChunkZ * 0xC2B2AE3D));
            var rng = new Random(hash);

            // 1. Sample density grid → per-cell density via trilinear
            //    interpolation. 32k doubles allocated per chunk; could
            //    be pooled if profiling shows allocator pressure (each
            //    is 256 KB, dropped at end-of-call).
            var density = new double[Chunk.SizeX * Chunk.SizeZ * Chunk.SizeY];
            sampler.GenerateChunkDensity(density, c.ChunkX, c.ChunkZ,
                AlphaTerrainNoiseSampler.Mode.Nether);

            // 2. Per-cell pass: bedrock cap/floor + density → block.
            //    Walks columns top-down so the bedrock probability
            //    ramp reads naturally (closer to extreme y → more
            //    likely bedrock).
            for (int x = 0; x < Chunk.SizeX; x++)
            for (int z = 0; z < Chunk.SizeZ; z++)
            {
                int densityBase = (x * Chunk.SizeZ + z) * Chunk.SizeY;
                for (int y = 0; y < Chunk.SizeY; y++)
                {
                    // Hard caps: bedrock cap at y=127, bedrock floor at y=0.
                    if (y == CeilingCapY) { c.Set(x, y, z, BlockType.Bedrock); continue; }
                    if (y == FloorY)      { c.Set(x, y, z, BlockType.Bedrock); continue; }

                    // Random partial bedrock ceiling. Closer to cap →
                    // more likely. Beta rule: bedrock if rng.Next(5) >=
                    // (CeilingCapY - y).
                    if (y >= BedrockCeilingLo)
                    {
                        if (rng.Next(5) >= CeilingCapY - y)
                        {
                            c.Set(x, y, z, BlockType.Bedrock);
                            continue;
                        }
                    }
                    // Random partial bedrock floor.
                    if (y <= BedrockFloorTop)
                    {
                        if (rng.Next(5) >= y)
                        {
                            c.Set(x, y, z, BlockType.Bedrock);
                            continue;
                        }
                    }

                    // Density-driven: solid netherrack vs liquid (lava
                    // below sea level) vs air.
                    double d = density[densityBase + y];
                    if (d > 0.0)
                    {
                        c.Set(x, y, z, BlockType.Netherrack);
                    }
                    else if (y < LavaSeaLevel)
                    {
                        c.Set(x, y, z, BlockType.Lava);
                    }
                    // else: air (Chunk default — no Set needed)
                }
            }

            // 3. Carve cave tunnel networks (MapGenCavesHell173-style).
            //    This is the key Beta-canonical pass that gives the
            //    Nether its sprawling-cavern feel — branching tunnels
            //    of varying radius wander through the netherrack mass,
            //    occasionally dropping into the lava ocean. Without
            //    this, the density-driven netherrack reads as one
            //    monolithic block with no real exploration variety.
            //    Runs BEFORE surface decoration so soul-sand / gravel
            //    can lay on cave-exposed netherrack tops too.
            NetherCaveCarver.Carve(c, seed);

            // 4. Carve ravines. Long capsule sweeps; less frequent
            //    than caves but more dramatic when they appear.
            CarveRavinesPass(c, seed);

            // 5. Surface decoration — soul sand and gravel patches on
            //    the topmost netherrack cell with air above, driven by
            //    the 4-octave sandGravel noise (Beta rule: noise +
            //    rng*0.2 > 0). Runs AFTER caves so the "top" includes
            //    cave-exposed surfaces.
            var sandNoise = new double[Chunk.SizeX * Chunk.SizeZ];
            var gravelNoise = new double[Chunk.SizeX * Chunk.SizeZ];
            sampler.SampleSurfaceNoise(sandNoise, gravelNoise, c.ChunkX, c.ChunkZ);
            PlaceSurfaceFeatures(c, rng, sandNoise, gravelNoise);

            // 6. Stamp fortresses (rarer than before — 1-in-8 grid).
            StampFortressesPass(c, seed);

            // 7. Glowstone clusters in the ceiling band. Per-column
            //    "ceiling top" comes from scanning down from the cap
            //    looking for the first netherrack cell with air below
            //    it (the underside of the roof from the cavern's POV).
            int clusterCount = 2 + rng.Next(3);
            for (int i = 0; i < clusterCount; i++)
            {
                int cx = rng.Next(Chunk.SizeX);
                int cz = rng.Next(Chunk.SizeZ);
                int anchorY = FindCeilingUndersideY(c, cx, cz);
                if (anchorY < 0) continue;
                int size = 4 + rng.Next(6);
                CarveGlowstoneCluster(c, cx, anchorY, cz, size, rng);
            }

            // 8. Lava-fall springs. 0..3 per chunk, anchored on a
            //    netherrack cell at Y=80..119 with an air column below
            //    (so the lava cascades through the cavern). The
            //    existing FluidTick handles the actual flow per game
            //    tick.
            int springCount = rng.Next(4); // 0..3
            for (int i = 0; i < springCount; i++)
            {
                int sx = rng.Next(Chunk.SizeX);
                int sz = rng.Next(Chunk.SizeZ);
                int sy = 80 + rng.Next(40); // 80..119
                if (sy >= CeilingFadeStart) continue;
                if ((BlockType)c.RawBlocks[Chunk.Index(sx, sy, sz)] != BlockType.Netherrack) continue;
                if ((BlockType)c.RawBlocks[Chunk.Index(sx, sy - 1, sz)] != BlockType.Air) continue;
                // Replace the netherrack with Lava — FluidTick will
                // cascade it downward each tick.
                c.Set(sx, sy, sz, BlockType.Lava);
            }
        }

        // Find the underside-of-ceiling Y for column (x, z). Scan from
        // the bedrock-ceiling line down, looking for the first
        // netherrack cell whose cell-below is air. That's where a
        // glowstone cluster should hang from. Returns -1 if none.
        private static int FindCeilingUndersideY(Chunk c, int x, int z)
        {
            for (int y = BedrockCeilingLo - 1; y > LavaSeaLevel; y--)
            {
                var here = (BlockType)c.RawBlocks[Chunk.Index(x, y, z)];
                if (here != BlockType.Netherrack) continue;
                var below = (BlockType)c.RawBlocks[Chunk.Index(x, y - 1, z)];
                if (below != BlockType.Air) continue;
                return y;
            }
            return -1;
        }

        // For each column, find the topmost netherrack cell with air
        // above it and roll soul sand / gravel by the noise+rng
        // formula. Above-lava only — columns that don't reach above
        // the lava sea (deep underwater lava) get nothing.
        private static void PlaceSurfaceFeatures(Chunk c, Random rng,
            double[] sandNoise, double[] gravelNoise)
        {
            for (int x = 0; x < Chunk.SizeX; x++)
            for (int z = 0; z < Chunk.SizeZ; z++)
            {
                int top = -1;
                for (int y = CeilingFadeStart - 1; y > LavaSeaLevel; y--)
                {
                    var here = (BlockType)c.RawBlocks[Chunk.Index(x, y, z)];
                    if (here != BlockType.Netherrack) continue;
                    var above = (BlockType)c.RawBlocks[Chunk.Index(x, y + 1, z)];
                    if (above != BlockType.Air) continue;
                    top = y;
                    break;
                }
                if (top < 0) continue;
                int idx = x * Chunk.SizeZ + z;
                bool sand = sandNoise[idx] + rng.NextDouble() * 0.2 > 0.0;
                bool gravel = gravelNoise[idx] + rng.NextDouble() * 0.2 > 0.0;
                // Sand wins on ties (matches Beta order).
                if (sand)
                {
                    c.Set(x, top, z, BlockType.SoulSand);
                    // 2..3 cell deep patches (Beta-canonical for soul sand)
                    if (top - 1 > LavaSeaLevel
                        && (BlockType)c.RawBlocks[Chunk.Index(x, top - 1, z)] == BlockType.Netherrack)
                        c.Set(x, top - 1, z, BlockType.SoulSand);
                }
                else if (gravel)
                {
                    c.Set(x, top, z, BlockType.Gravel);
                }
            }
        }

        // Walk this chunk + 8 horizontal neighbours; each anchor's RNG
        // determines whether a ravine starts there. Ravines are capsule
        // sweeps that carve cells within their radius to air. Same
        // shape as before, just with the new altitude band.
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
                if (rng.Next(12) != 0) continue;

                float sx = nx * Chunk.SizeX + rng.Next(Chunk.SizeX);
                float sz = nz * Chunk.SizeZ + rng.Next(Chunk.SizeZ);
                int   yMin = LavaSeaLevel + 8;
                int   yMax = CeilingFadeStart - 12;
                float sy = yMin + rng.Next(yMax - yMin);
                float yaw = (float)(rng.NextDouble() * Math.PI * 2.0);

                int   steps        = 30 + rng.Next(31);
                float radius       = 2.5f + (float)rng.NextDouble() * 1.5f;
                float vertExtent   = 6.0f + (float)rng.NextDouble() * 4.0f;
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
                    float dy = (float)Math.Sin(pitch) * 0.4f;
                    float dz = (float)Math.Sin(yaw) * (float)Math.Cos(pitch);
                    curX += dx * StepLen;
                    curY += dy * StepLen;
                    curZ += dz * StepLen;

                    int chunkX0 = c.ChunkX * Chunk.SizeX;
                    int chunkZ0 = c.ChunkZ * Chunk.SizeZ;
                    float distX = curX < chunkX0 ? chunkX0 - curX
                                : curX > chunkX0 + Chunk.SizeX - 1 ? curX - (chunkX0 + Chunk.SizeX - 1)
                                : 0;
                    float distZ = curZ < chunkZ0 ? chunkZ0 - curZ
                                : curZ > chunkZ0 + Chunk.SizeZ - 1 ? curZ - (chunkZ0 + Chunk.SizeZ - 1)
                                : 0;
                    if (distX > radius + 1f && distZ > radius + 1f) break;
                    if (curY < (float)(LavaSeaLevel - 30)
                     || curY > (float)(CeilingFadeStart - 4)) break;
                }
            }
        }

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
            int hiY = Math.Min(icy + rY, CeilingFadeStart - 1);
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

        private static void StampFortressesPass(Chunk c, int seed)
        {
            for (int axOff = 0; axOff < FortressFootprint; axOff++)
            for (int azOff = 0; azOff < FortressFootprint; azOff++)
            {
                int ax = c.ChunkX - axOff;
                int az = c.ChunkZ - azOff;
                if ((ax & (FortressGridStep - 1)) != 0) continue;
                if ((az & (FortressGridStep - 1)) != 0) continue;

                int hash = (int)((uint)seed * 0x71C4F39Du
                    + (uint)(ax * 0xA1B7C5E3)
                    + (uint)(az * 0x5F8B2D71));
                var rng = new Random(hash);
                if (rng.Next(FortressSpawnDenom) != 0) continue; // 1-in-FortressSpawnDenom

                StampFortressIntoChunk(c, ax, az, rng);
            }
        }

        private static void StampFortressIntoChunk(Chunk c, int anchorChunkX, int anchorChunkZ, Random rng)
        {
            int wx0 = anchorChunkX * Chunk.SizeX;
            int wz0 = anchorChunkZ * Chunk.SizeZ;
            int wx1 = wx0 + Chunk.SizeX * FortressFootprint - 1;
            int wz1 = wz0 + Chunk.SizeZ * FortressFootprint - 1;

            int chunkX0 = c.ChunkX * Chunk.SizeX;
            int chunkZ0 = c.ChunkZ * Chunk.SizeZ;
            int chunkX1 = chunkX0 + Chunk.SizeX - 1;
            int chunkZ1 = chunkZ0 + Chunk.SizeZ - 1;

            int loX = Math.Max(wx0, chunkX0);
            int hiX = Math.Min(wx1, chunkX1);
            int loZ = Math.Max(wz0, chunkZ0);
            int hiZ = Math.Min(wz1, chunkZ1);
            if (loX > hiX || loZ > hiZ) return;

            int centreX = (wx0 + wx1) / 2;
            int centreZ = (wz0 + wz1) / 2;
            const int doorHalfWidth = 2;

            for (int wx = loX; wx <= hiX; wx++)
            for (int wz = loZ; wz <= hiZ; wz++)
            {
                int lx = wx - chunkX0;
                int lz = wz - chunkZ0;
                bool atWestWall  = wx == wx0;
                bool atEastWall  = wx == wx1;
                bool atNorthWall = wz == wz0;
                bool atSouthWall = wz == wz1;
                bool onPerimeter = atWestWall || atEastWall || atNorthWall || atSouthWall;

                bool inDoorNS = (Math.Abs(wx - centreX) <= doorHalfWidth) && (atNorthWall || atSouthWall);
                bool inDoorEW = (Math.Abs(wz - centreZ) <= doorHalfWidth) && (atWestWall  || atEastWall);

                c.Set(lx, FortressFloorY, lz, BlockType.NetherBrick);

                if (onPerimeter)
                {
                    for (int wy = FortressFloorY + 1; wy <= FortressWallTopY; wy++)
                    {
                        bool isLintel = wy == FortressWallTopY;
                        if ((inDoorNS || inDoorEW) && !isLintel)
                            c.Set(lx, wy, lz, BlockType.Air);
                        else
                            c.Set(lx, wy, lz, BlockType.NetherBrick);
                    }
                }
                else
                {
                    for (int wy = FortressFloorY + 1; wy <= FortressWallTopY; wy++)
                    {
                        var t = (BlockType)c.RawBlocks[Chunk.Index(lx, wy, lz)];
                        if (t != BlockType.Bedrock)
                            c.Set(lx, wy, lz, BlockType.Air);
                    }
                }

                c.Set(lx, FortressCeilingY, lz, BlockType.NetherBrick);
            }

            if (centreX >= chunkX0 && centreX <= chunkX1
             && centreZ >= chunkZ0 && centreZ <= chunkZ1)
            {
                int lcx = centreX - chunkX0;
                int lcz = centreZ - chunkZ0;
                c.Set(lcx, FortressFloorY + 1, lcz, BlockType.NetherBrick);
                c.Set(lcx, FortressFloorY + 2, lcz, BlockType.MobSpawner);
            }
        }

        // Random walk from (cx, cy, cz) replacing visited cells with
        // glowstone. Biases downward so the cluster hangs visibly off
        // the ceiling rather than sitting flat on its face. Bound to
        // the ceiling band so the walk can't escape.
        private static void CarveGlowstoneCluster(Chunk c, int cx, int cy, int cz, int size, Random rng)
        {
            int x = cx, y = cy, z = cz;
            for (int i = 0; i < size; i++)
            {
                if (x < 0 || x >= Chunk.SizeX || z < 0 || z >= Chunk.SizeZ) return;
                if (y < CeilingFadeStart - 8 || y >= CeilingCapY) return;
                c.Set(x, y, z, BlockType.Glowstone);
                int dir = rng.Next(5);
                switch (dir)
                {
                    case 0: x++; break;
                    case 1: x--; break;
                    case 2: z++; break;
                    case 3: z--; break;
                    case 4: y--; break;
                }
            }
        }
    }
}
