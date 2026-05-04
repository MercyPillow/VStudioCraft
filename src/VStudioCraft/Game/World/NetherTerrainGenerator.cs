using System;

namespace VStudioCraft.Game
{
    // Tier 8 #51 V11 — Nether dimension chunk generator. The shape is
    // intentionally NOT canonical Alpha 1.1.2 — Alpha's "flat plane
    // with a roof" reads as a single huge slab of terrain you walk
    // across. We re-shape it into a much taller enclosed cavern with
    // 3D mountainous netherrack rising out of a flat lava sea, lots
    // of bridges and tunnels between peaks, and a netherrack roof
    // sealing the volume. The dimension still has a bedrock floor /
    // bedrock cap and netherrack ceiling band — the "closed space"
    // requirement — but the interior reads as a sprawling cavern
    // landscape rather than a room.
    //
    //   y = 0          : Bedrock floor
    //   y = 1..10      : Solid netherrack base mass (the floor under the lava)
    //   y = 11..15     : Flat lava sea covering the entire chunk
    //                    surface — anything above it that isn't
    //                    netherrack is air, anything sticking up
    //                    through it is a mountain foot.
    //   y = 16..109    : Mountainous netherrack — generated from a
    //                    3D density field so peaks rise from the
    //                    lava with overhangs, ridges, and natural
    //                    horizontal "shelves" that read as bridges.
    //   y = 110..126   : Netherrack ceiling band (per-column varied
    //                    depth so the roof has visible relief).
    //   y = 127        : Bedrock cap.
    //
    // 3D density formulation: a per-column mountain peak height comes
    // from low-frequency 2D Perlin (octaves on world-x/world-z); a 3D
    // perturbation comes from two orthogonal 2D Perlin samples in the
    // (wx,y) and (y,wz) planes — averaging those two gives a cheap
    // 3D-coherent noise without needing a real 3D-noise table. The
    // peak height drives a vertical density gradient (positive below
    // the peak → solid, negative above → air); the perturbation adds
    // ±0.5 of fluctuation that punches tunnels through the mass and
    // lets stray netherrack chunks float as bridges in the gradient
    // band near the peak. Tuning notes:
    //   * mountain noise frequency 0.013 → peaks ~38 cells apart
    //   * perturbation frequency 0.045 → ~14-cell tunnel/blob scale
    //   * peak Y range [30, 95]: tallest peaks reach within 15 cells
    //     of the ceiling, shortest barely clear the lava sea
    //
    // Deterministic per-chunk RNG keyed on (seed, chunkX, chunkZ) so
    // the same Nether chunk regenerates identically across sessions.
    internal static class NetherTerrainGenerator
    {
        public const int FloorY            = 0;
        public const int NetherrackBaseTop = 10;   // top of solid floor mass
        public const int LavaSurfaceY      = 15;   // top of flat lava sea
        // Public alias: NetherrackTop is referenced by mob-spawn passes
        // in World.cs (pigman ground spawn, ghast/blaze hover bands).
        // Keeping it as the lava-sea surface preserves semantic — pigmen
        // spawn where a mountain base meets the lava, mob hover bands
        // sit in the cavern above.
        public const int NetherrackTop     = LavaSurfaceY;
        public const int CeilingBaseY      = 110;
        public const int CeilingCapY       = 127;

        // Mountain-density tuning constants (kept as named consts so
        // a future visual iteration can tweak them in one place).
        private const float MountainNoiseFreq      = 0.013f;
        private const float PerturbNoiseFreq       = 0.045f;
        private const int   MountainPeakBaseY      = 30;
        private const int   MountainPeakRange      = 65;     // peakY ∈ [Base, Base+Range] = [30, 95]
        private const float VerticalGradientScale  = 1f / 30f; // (peakY - y) * scale gives [-3, +3] across the gradient band
        private const float VerticalWeight         = 0.7f;
        private const float PerturbWeight          = 0.5f;

        public static void Generate(Chunk c, int seed)
        {
            // Per-chunk hashed RNG — used for the discrete features
            // (glowstone clusters, soul sand, gravel) where stochastic
            // count + position is fine. The deterministic 3D mountain
            // mass uses Noise (seeded per call) for reproducibility.
            int hash = (int)((uint)seed * 0x9E3779B1u
                + (uint)(c.ChunkX * 0x85EBCA77)
                + (uint)(c.ChunkZ * 0xC2B2AE3D));
            var rng = new Random(hash);
            // Noise instance is per-call — terrain gen runs once per
            // chunk-load (not per frame), so a 1KB perm-table allocation
            // is cheap and avoids a thread-static cache. Same seed for
            // every chunk so noise is continuous across chunk seams.
            var noise = new Noise(seed);

            // Bedrock floor + cap.
            for (int x = 0; x < Chunk.SizeX; x++)
            for (int z = 0; z < Chunk.SizeZ; z++)
            {
                c.Set(x, FloorY, z, BlockType.Bedrock);
                c.Set(x, CeilingCapY, z, BlockType.Bedrock);
            }

            // Solid netherrack base mass (y = 1..NetherrackBaseTop).
            // This is the "floor under the lava sea" — a continuous
            // rock layer the player can dig down into. No carving
            // happens here so a player digging straight down hits
            // bedrock predictably.
            for (int x = 0; x < Chunk.SizeX; x++)
            for (int z = 0; z < Chunk.SizeZ; z++)
            for (int y = FloorY + 1; y <= NetherrackBaseTop; y++)
                c.Set(x, y, z, BlockType.Netherrack);

            // Flat lava sea (y = NetherrackBaseTop+1..LavaSurfaceY).
            // Fills every column at the lava-sea altitudes. Mountain
            // generation below overwrites these cells where mountains
            // protrude above the lava, so the lava ends up reading as
            // a connected sea with islands rising out of it.
            for (int x = 0; x < Chunk.SizeX; x++)
            for (int z = 0; z < Chunk.SizeZ; z++)
            for (int y = NetherrackBaseTop + 1; y <= LavaSurfaceY; y++)
                c.Set(x, y, z, BlockType.Lava);

            // Mountainous netherrack mass (y = LavaSurfaceY+1..CeilingBaseY-1).
            // The 3D density field decides per-cell whether to place
            // netherrack or leave air. Because the lava sea was filled
            // first, ANY cell at or below LavaSurfaceY that we don't
            // overwrite stays as lava; mountain bases that intersect
            // the lava plane look like islands sticking out of it.
            for (int x = 0; x < Chunk.SizeX; x++)
            for (int z = 0; z < Chunk.SizeZ; z++)
            {
                int wx = c.ChunkX * Chunk.SizeX + x;
                int wz = c.ChunkZ * Chunk.SizeZ + z;
                // Per-column mountain peak height — low-frequency 2D
                // octave noise on world (x, z). Output [-1, 1] remapped
                // linearly to [MountainPeakBaseY, MountainPeakBaseY + Range].
                float mountainNoise = noise.Octaves(
                    wx * MountainNoiseFreq,
                    wz * MountainNoiseFreq,
                    octaves: 4, persistence: 0.5f, lacunarity: 2f);
                int peakY = MountainPeakBaseY
                    + (int)((mountainNoise + 1f) * 0.5f * MountainPeakRange);

                // Mountain bases extend down into the lava sea (so a
                // peak that pierces the lava plane has solid roots
                // visible from below). Carve from the lava floor up
                // through the cavern to the ceiling, evaluating
                // density per cell.
                for (int y = NetherrackBaseTop + 1; y < CeilingBaseY; y++)
                {
                    // Vertical gradient: positive deep in the mountain,
                    // 0 at the peak, negative above. The gradient is
                    // strong enough that a mountain interior is solidly
                    // netherrack but weak enough near the peak that
                    // perturbation noise can perforate the surface
                    // (creating tunnels and overhangs).
                    float yRel = (peakY - y) * VerticalGradientScale;

                    // 3D-coherent perturbation: average two orthogonal
                    // 2D Perlin samples on (wx, y) and (y, wz) planes.
                    // This is a cheap stand-in for true 3D noise —
                    // produces shelves, blobs, and bridge-like
                    // horizontal extents naturally because the (y, wz)
                    // term varies slowly when wz is constant, giving
                    // long horizontal coherence at fixed altitude.
                    float pxy = noise.Octaves(
                        wx * PerturbNoiseFreq, y * PerturbNoiseFreq,
                        octaves: 3, persistence: 0.5f, lacunarity: 2f);
                    float pyz = noise.Octaves(
                        y * PerturbNoiseFreq, wz * PerturbNoiseFreq,
                        octaves: 3, persistence: 0.5f, lacunarity: 2f);
                    float perturb = (pxy + pyz) * 0.5f;

                    float density = yRel * VerticalWeight + perturb * PerturbWeight;
                    if (density > 0f)
                        c.Set(x, y, z, BlockType.Netherrack);
                }
            }

            // Per-column variable ceiling top — each (x, z) drops the
            // ceiling 0..3 cells off the canonical CeilingBaseY for
            // visible relief on the underside of the roof. Hashed per-
            // column so neighbouring chunks line up at the seam.
            int[] ceilingTopForColumn = new int[Chunk.SizeX * Chunk.SizeZ];
            for (int x = 0; x < Chunk.SizeX; x++)
            for (int z = 0; z < Chunk.SizeZ; z++)
            {
                int wx = c.ChunkX * Chunk.SizeX + x;
                int wz = c.ChunkZ * Chunk.SizeZ + z;
                uint h = (uint)seed * 0x9E3779B1u
                       + (uint)(wx * 0x85EBCA77)
                       + (uint)(wz * 0xC2B2AE3D);
                int dropAmount = (int)(h % 4u); // 0..3 cells of descent
                int ceilingTop = CeilingBaseY - dropAmount;
                ceilingTopForColumn[x * Chunk.SizeZ + z] = ceilingTop;
            }

            // Ceiling band — fill from each column's ceilingTop up
            // to CeilingCapY-1. Overwrites any stray air cells the
            // mountain pass might have left in this band (mountain
            // density at high y is mostly negative, so without this
            // pass the cells above ceilingTop would all be air —
            // this re-asserts the netherrack lid).
            for (int x = 0; x < Chunk.SizeX; x++)
            for (int z = 0; z < Chunk.SizeZ; z++)
            {
                int ceilingTop = ceilingTopForColumn[x * Chunk.SizeZ + z];
                for (int y = ceilingTop; y < CeilingCapY; y++)
                    c.Set(x, y, z, BlockType.Netherrack);
            }

            // Nether ravines. Carves capsule-shaped passages through
            // the netherrack mass — the existing pass works fine with
            // the new mountain layout since it just clears any non-
            // bedrock cell within its swept volume. Tuned altitude
            // band so ravines spawn in the mountain region rather than
            // the (now-thin) base mass.
            CarveRavinesPass(c, seed);

            // Nether fortress structures. Sit just above the lava sea
            // in the lower-mountain altitude band; their interior
            // carve pass clears any mountain netherrack that would
            // otherwise fill the rooms.
            StampFortressesPass(c, seed);

            // Glowstone clusters anchored at the per-column ceiling
            // top — same algorithm as before, just driven off the new
            // ceilingTopForColumn array.
            int clusterCount = 2 + rng.Next(3);
            for (int i = 0; i < clusterCount; i++)
            {
                int cx = rng.Next(Chunk.SizeX);
                int cz = rng.Next(Chunk.SizeZ);
                int anchorY = ceilingTopForColumn[cx * Chunk.SizeZ + cz];
                int size = 4 + rng.Next(6);
                CarveGlowstoneCluster(c, cx, anchorY, cz, size, rng);
            }

            // Surface features (soul sand + gravel patches) on the
            // tops of mountain peaks. With the new 3D mountain mass
            // there's no fixed "surface y" — each column has a
            // different topmost-netherrack cell, so we scan top-down
            // from the ceiling band to find the actual surface and
            // sprinkle there.
            PlaceSurfaceFeatures(c, rng);
        }

        // For each column, scan from below the ceiling band down to
        // the lava sea looking for the first netherrack cell that has
        // air directly above it — that's the column's "mountain top".
        // Roll soul-sand and gravel patches there. Cells without a
        // mountain top above the lava sea (columns where the cavern
        // is empty) get nothing.
        private static void PlaceSurfaceFeatures(Chunk c, Random rng)
        {
            for (int x = 0; x < Chunk.SizeX; x++)
            for (int z = 0; z < Chunk.SizeZ; z++)
            {
                int top = -1;
                for (int y = CeilingBaseY - 1; y > LavaSurfaceY; y--)
                {
                    var here  = (BlockType)c.RawBlocks[Chunk.Index(x, y, z)];
                    if (here != BlockType.Netherrack) continue;
                    var above = (BlockType)c.RawBlocks[Chunk.Index(x, y + 1, z)];
                    if (above != BlockType.Air) continue;
                    top = y;
                    break;
                }
                if (top < 0) continue;
                // 1-in-12 chance soul sand, 1-in-18 chance gravel.
                // Independent rolls so the same column can host both
                // (rare but visually fine).
                int roll = rng.Next(36);
                if (roll < 3)
                    c.Set(x, top, z, BlockType.SoulSand);
                else if (roll < 5)
                    c.Set(x, top, z, BlockType.Gravel);
            }
        }

        // Walk this chunk's anchor + its 8 horizontal neighbours.
        // For each anchor that rolled a ravine, simulate the full
        // ravine path and carve cells that land within THIS chunk's
        // bounds. Same seed-driven path for the same anchor across
        // chunks, so the ravine geometry is continuous at chunk seams.
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

                // Random start point in the (nx, nz) chunk, pitched
                // into the mountain altitude band so ravines carve
                // visible features through the cavern walls. Y range
                // ([LavaSurfaceY+8, CeilingBaseY-12]) keeps starts
                // safely inside the mountain mass.
                float sx = nx * Chunk.SizeX + rng.Next(Chunk.SizeX);
                float sz = nz * Chunk.SizeZ + rng.Next(Chunk.SizeZ);
                int   yMin = LavaSurfaceY + 8;
                int   yMax = CeilingBaseY - 12;
                float sy = yMin + rng.Next(yMax - yMin);
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
                    // Y bound: keep ravines inside the cavern (above
                    // the lava sea, below the ceiling band).
                    if (curY < (float)(LavaSurfaceY + 2)
                     || curY > (float)(CeilingBaseY - 4)) break;
                }
            }
        }

        // Carve one capsule (vertically-stretched ellipsoid) centred
        // at world coords (cx, cy, cz). Iterates only the bounding box
        // intersecting this chunk; cells whose ellipsoid distance ≤ 1
        // get cleared to Air. Bedrock is preserved (ravines can't
        // punch through the dimensional floor or cap).
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
            int loY = Math.Max(icy - rY, LavaSurfaceY + 1);
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

        // Fortress anchor + stamp pass. For each of the 9 candidate
        // anchor chunk positions that could overlap this chunk's
        // bounds, check if it lands on the 8×8 anchor grid AND rolled
        // a fortress, and stamp the fortress's intersection with this
        // chunk if so. Same neighbour-walk pattern the ravine pass
        // uses to keep multi-chunk structures continuous across seams.
        //
        // Fortresses sit just above the lava sea in the lower-mountain
        // altitude band — small enough to not touch the ceiling, low
        // enough that the player can clearly see them while flying
        // through the cavern, and the interior pass clears mountain
        // netherrack so the rooms are usable.
        public const int FortressFloorY    = LavaSurfaceY + 5;     // 20 — just above lava
        public const int FortressWallTopY  = FortressFloorY + 4;   // 24 — walls 4 tall
        public const int FortressCeilingY  = FortressFloorY + 5;   // 25 — single-thick brick ceiling
        public const int FortressGridStep  = 8;                    // anchor every 8 chunks
        public const int FortressFootprint = 3;                    // 3×3 chunks footprint

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
                if (rng.Next(2) != 0) continue;

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

        // Punch a glowstone cluster into the ceiling — random walk
        // from (cx, cy, cz) for `size` steps, replacing each visited
        // cell with Glowstone. Biases slightly downward so the
        // cluster hangs visibly off the ceiling rather than sitting
        // flat. Bound to the ceiling band so the walk can't escape
        // into the open cavern.
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
                    case 4: y--; break;
                }
            }
        }
    }
}
