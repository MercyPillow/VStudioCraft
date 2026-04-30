using System;

namespace VStudioCraft.Game
{
    internal static class TerrainGenerator
    {
        // Tier 6 #31 — Surface height bumped from 24 → 64 so the
        // 128-tall world has a meaningful underground (~60 blocks
        // beneath the surface for caves / ores / dungeons) instead
        // of the previous ~20-block sliver. Amplitude bumped 14 → 20
        // so the larger headroom isn't wasted on flat plains. New
        // worlds generate at the new constants; existing saves load
        // their pre-existing block data unchanged (chunks are stored
        // as block ids, not regenerated from the noise function), so
        // the only legacy-world quirk is that chunks streamed in
        // AFTER world creation use the new constants — the
        // discontinuity sits at the edge of the original explored
        // area and is treated as accepted dev-time churn.
        public const int BaseHeight = 64;
        public const int HeightAmplitude = 20;
        // Ocean surface. Air between the terrain height and SeaLevel becomes water.
        public const int SeaLevel = BaseHeight - 2;
        // Columns whose top block sits at SeaLevel or one above get a sandy crown:
        // the water's edge reads as beach, and one-block islands still look right.
        private const int BeachHeight = SeaLevel + 1;

        // Canopy radius so trees that seed near a chunk edge can extend their
        // leaves into this chunk. Keep in lockstep with PlaceOakTree's 5x5 slab.
        private const int TreeBorder = 2;

        public static void Generate(Chunk chunk, Noise noise)
        {
            GenerateColumns(chunk, noise);
            GenerateBedrock(chunk, noise);
            // Caves run BEFORE the water and ore passes. Before water so caves
            // that nick the sea floor can be left as dry air without the water
            // pass flooding them (we cap carve Y below sea level as extra
            // insurance). Before ores so veins can seed in the fresh cave
            // walls rather than vanishing when we carve over them.
            GenerateCaves(chunk, noise);
            // Tier 6 #32 — Ravines. Long surface-piercing fissures
            // that span 3-5 chunks at a consistent angle. Seeded
            // from a coarse 96-block feature grid (every cell rolls
            // for "is there a ravine starting here? in what
            // direction?"); chunks query the small set of cells whose
            // ravines could reach into them and carve the slice that
            // falls in their bounds. Cross-chunk continuity is
            // automatic because every chunk sees the same grid + the
            // same seeded RNG. Runs after caves so caves intersect
            // ravines naturally; before water so ravines that pierce
            // sea level fill correctly during the water pass.
            GenerateRavines(chunk, noise);
            GenerateWater(chunk);
            GenerateOresAndPatches(chunk, noise);
            // Tier 6 #33 — Surface lava lakes + underground water/lava
            // pools + cliff-face springs. Runs AFTER water flooding so
            // surface lava sits on top of land instead of being
            // washed away by the sea pass; AFTER ores so a vein
            // doesn't get clobbered by a randomly-placed lake; BEFORE
            // flora so trees / flowers don't grow inside lake water
            // or on top of lava.
            GenerateFluidFeatures(chunk, noise);
            GenerateTrees(chunk, noise);
            GenerateFlora(chunk, noise);
            // Tier 6 #37 Phase 2 — Freeze the topmost water cell in
            // every column whose biome is Snow. Runs after fluid
            // features so cliff springs and underground pools in snow
            // chunks don't get re-frozen mid-cascade (only the top
            // sea-level layer matters visually). Skipped entirely for
            // non-Snow biomes via the per-column biome check.
            FreezeWaterInSnowBiome(chunk, noise);
            // Tier 6 #37 Phase 4 — Lay a 1/8 snow layer on every
            // grass column in the Snow biome. Runs LAST so the layer
            // sits above flora / trees / fluid features that already
            // populated the column.
            ScatterSnowLayer(chunk, noise);
            // Newly-generated chunks start "active" so the first fluid tick
            // gets a chance to propagate any source cells (terrain places
            // still water at sea level). After one no-op tick the flag will
            // self-clear and we stop paying the per-cell scan.
            chunk.HasActiveFluid = true;
        }

        // ---------- Pass 1: heightmap columns (stone / dirt / grass / sand). ----------

        private static void GenerateColumns(Chunk chunk, Noise noise)
        {
            for (int x = 0; x < Chunk.SizeX; x++)
            for (int z = 0; z < Chunk.SizeZ; z++)
            {
                int wx = chunk.ChunkX * Chunk.SizeX + x;
                int wz = chunk.ChunkZ * Chunk.SizeZ + z;
                int height = SurfaceHeight(noise, wx, wz);
                bool sandy = height <= BeachHeight;
                // Tier 6 #37 — Biome-aware surface block. Coastal /
                // sub-sea-level columns always read as beach sand
                // (sandy=true) regardless of biome — the beach
                // override wins so a desert next to ocean still has
                // a clean shoreline. Inland (height > BeachHeight)
                // the biome dispatch picks the surface variant:
                //   Plains / Forest → Grass on Dirt
                //   Desert          → Sand on Sand (4 deep)
                //   Snow            → SnowBlock cap on Dirt (visual
                //                     reskin of grass; saplings won't
                //                     grow on snow but the player can
                //                     dig through to the dirt below)
                Biome biome = BiomeMap.Classify(noise, wx, wz);
                for (int y = 0; y < height; y++)
                {
                    BlockType t;
                    if (sandy)
                    {
                        if (y >= height - 4) t = BlockType.Sand;
                        else t = BlockType.Stone;
                    }
                    else if (biome == Biome.Desert)
                    {
                        if (y >= height - 4) t = BlockType.Sand;
                        else t = BlockType.Stone;
                    }
                    else if (biome == Biome.Snow)
                    {
                        // Phase 4 — Snow biome surface keeps grass
                        // underneath; the snow itself is a 1/8 LAYER
                        // placed at height (one above the grass top)
                        // by the post-column pass below. The grass
                        // cell itself is left intact so digging
                        // through the snow exposes a normal grass
                        // surface, and so the chunk mesher's snowy-
                        // grass-side dispatch (Grass + SnowBlock
                        // above → TileSnowyGrassSide) reads correctly.
                        if (y == height - 1)      t = BlockType.Grass;
                        else if (y >= height - 4) t = BlockType.Dirt;
                        else                      t = BlockType.Stone;
                    }
                    else
                    {
                        // Plains / Forest default — grass over dirt.
                        if (y == height - 1)      t = BlockType.Grass;
                        else if (y >= height - 4) t = BlockType.Dirt;
                        else                      t = BlockType.Stone;
                    }
                    chunk.Set(x, y, z, t);
                }
            }
        }

        // ---------- Pass 2: flood still water in every column from surface height up to sea level. ----------

        private static void GenerateWater(Chunk chunk)
        {
            for (int x = 0; x < Chunk.SizeX; x++)
            for (int z = 0; z < Chunk.SizeZ; z++)
            {
                // Find the column's top solid block. Columns generated by Pass 1
                // have contiguous solids from y=0 to height-1 (sand or grass on
                // top), so we can scan up from SeaLevel-1 until we hit a solid.
                for (int y = SeaLevel - 1; y >= 0; y--)
                {
                    int idx = Chunk.Index(x, y, z);
                    if (chunk.RawBlocks[idx] != (byte)BlockType.Air) break;
                    chunk.RawBlocks[idx] = (byte)BlockType.Water;
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

        // ---------- Pass 2.5: cave systems. ----------

        // Radius in chunks to scan for worm starts that might tunnel into
        // this chunk. A worm can be ~100 blocks long = ~6 chunks, but in
        // practice most segments land near the start. 2 chunks = 32 blocks
        // of reach each direction is plenty while keeping per-chunk gen
        // cost bounded (≤25 neighbour chunks × a handful of worms).
        private const int CaveChunkRadius = 2;
        // Cave carving never touches the top N blocks of any column, so
        // the surface stays intact. Alpha's caves occasionally punch
        // through to open air — we trade that for simplicity because
        // water flooding through unsealed caves is an open problem until
        // fluid ticks exist.
        private const int CaveRoofClearance = 5;
        // Absolute hard ceiling in blocks: keeps caves away from sea
        // level even on tall terrain (roof clearance alone isn't enough
        // on mountainous columns beside the ocean).
        private const int CaveMaxY = SeaLevel - 4;

        private static void GenerateCaves(Chunk chunk, Noise noise)
        {
            // Scan every potential worm start in a (2r+1)² neighbourhood of
            // chunks. Each neighbour's RNG is reseeded deterministically so a
            // worm emitted from chunk (ncx, ncz) carves the same path no matter
            // which chunk triggers its generation.
            for (int dcx = -CaveChunkRadius; dcx <= CaveChunkRadius; dcx++)
            for (int dcz = -CaveChunkRadius; dcz <= CaveChunkRadius; dcz++)
            {
                int ncx = chunk.ChunkX + dcx;
                int ncz = chunk.ChunkZ + dcz;
                var rng = ChunkRng(noise.Seed, ncx, ncz, 0xCA7E);

                // Most chunks seed 0–1 worms. ~1/8 chunks seed a "big" system
                // with up to 3 worms from roughly the same origin, giving the
                // branching cave-network feel. Rates tuned low so early testing
                // worlds don't look like a sponge.
                int wormCount = rng.Next(8) == 0 ? 2 + rng.Next(2) : (rng.Next(3) == 0 ? 1 : 0);

                for (int w = 0; w < wormCount; w++)
                {
                    // Worm start in the seeding chunk's world-space volume.
                    double sx = ncx * Chunk.SizeX + rng.Next(Chunk.SizeX);
                    double sz = ncz * Chunk.SizeZ + rng.Next(Chunk.SizeZ);
                    // Bias Y downward (y² distribution) so caves cluster low
                    // where stone dominates; occasional shallow ones still
                    // happen when the random draw is unlucky-high.
                    double u = rng.NextDouble();
                    double sy = 6 + u * u * (CaveMaxY - 6);

                    double yaw = rng.NextDouble() * Math.PI * 2.0;
                    double pitch = (rng.NextDouble() - 0.5) * 0.6; // mostly-horizontal
                    double radius = 1.8 + rng.NextDouble() * 1.8;  // 1.8..3.6 blocks
                    int length = 30 + rng.Next(90);                 // 30..119 segments
                    // Yaw/pitch drift per step. Small values give sinuous
                    // tunnels; larger would make tight worms that coil back.
                    double yawDrift = 0.0;
                    double pitchDrift = 0.0;

                    CarveWorm(chunk, noise, rng, sx, sy, sz, yaw, pitch, radius, length,
                              ref yawDrift, ref pitchDrift);
                }
            }
        }

        private static void CarveWorm(Chunk chunk, Noise noise, Random rng,
            double sx, double sy, double sz,
            double yaw, double pitch, double baseRadius, int length,
            ref double yawDrift, ref double pitchDrift)
        {
            int baseX = chunk.ChunkX * Chunk.SizeX;
            int baseZ = chunk.ChunkZ * Chunk.SizeZ;

            double x = sx, y = sy, z = sz;

            for (int step = 0; step < length; step++)
            {
                // Taper the radius near the ends so tunnels close naturally
                // rather than stopping at a flat wall.
                double t = (double)step / length;
                double taper = 1.0 - (2.0 * t - 1.0) * (2.0 * t - 1.0); // parabola peaks at 1.0 mid-worm
                double r = baseRadius * (0.5 + 0.5 * taper);

                // Only carve the slab intersecting THIS chunk — saves 90% of
                // the work for worms that originate 2 chunks away.
                int bx0 = (int)Math.Floor(x - r) - baseX;
                int bx1 = (int)Math.Floor(x + r) - baseX;
                int by0 = (int)Math.Floor(y - r);
                int by1 = (int)Math.Floor(y + r);
                int bz0 = (int)Math.Floor(z - r) - baseZ;
                int bz1 = (int)Math.Floor(z + r) - baseZ;

                if (bx1 >= 0 && bx0 < Chunk.SizeX && bz1 >= 0 && bz0 < Chunk.SizeZ &&
                    by1 >= 0 && by0 < Chunk.SizeY)
                {
                    if (bx0 < 0) bx0 = 0; if (bx1 >= Chunk.SizeX) bx1 = Chunk.SizeX - 1;
                    if (bz0 < 0) bz0 = 0; if (bz1 >= Chunk.SizeZ) bz1 = Chunk.SizeZ - 1;
                    if (by0 < 2) by0 = 2;   // never carve the bedrock floor at y<2
                    if (by1 > CaveMaxY) by1 = CaveMaxY;

                    double rr = r * r;
                    for (int lx = bx0; lx <= bx1; lx++)
                    for (int lz = bz0; lz <= bz1; lz++)
                    {
                        // Per-column roof guard: keep CaveRoofClearance blocks
                        // of solid between the cave and the surface so we
                        // don't pop the grass.
                        int wx = baseX + lx;
                        int wz = baseZ + lz;
                        int surface = SurfaceHeight(noise, wx, wz);
                        int colMaxY = Math.Min(surface - CaveRoofClearance, CaveMaxY);
                        if (colMaxY < by0) continue;

                        double ddx = (baseX + lx + 0.5) - x;
                        double ddz = (baseZ + lz + 0.5) - z;
                        double dxz = ddx * ddx + ddz * ddz;

                        int yHi = by1 < colMaxY ? by1 : colMaxY;
                        for (int ly = by0; ly <= yHi; ly++)
                        {
                            double ddy = (ly + 0.5) - y;
                            if (dxz + ddy * ddy > rr) continue;

                            int idx = Chunk.Index(lx, ly, lz);
                            byte b = chunk.RawBlocks[idx];
                            // Carve only through natural stone-family blocks.
                            // Keep bedrock (safety) and anything already air/
                            // water/sand so beaches + caves don't nick.
                            if (b == (byte)BlockType.Stone ||
                                b == (byte)BlockType.Dirt ||
                                b == (byte)BlockType.Gravel ||
                                b == (byte)BlockType.Grass)
                            {
                                chunk.RawBlocks[idx] = (byte)BlockType.Air;
                            }
                        }
                    }
                }

                // Advance one block of arc length.
                double cosP = Math.Cos(pitch);
                x += cosP * Math.Cos(yaw);
                z += cosP * Math.Sin(yaw);
                y += Math.Sin(pitch);

                // Drift: smooth random walk on yaw/pitch so tunnels curve
                // instead of zig-zag. Dampen the drift accumulator each step
                // to keep pitch close to horizontal on average.
                yawDrift   = yawDrift   * 0.75 + (rng.NextDouble() - 0.5) * 0.20;
                pitchDrift = pitchDrift * 0.75 + (rng.NextDouble() - 0.5) * 0.10;
                yaw   += yawDrift;
                pitch += pitchDrift;
                // Pull pitch toward zero so worms stay roughly at depth.
                pitch *= 0.92;

                // Early-out if the worm has wandered out of sensible Y range.
                if (y < 3 || y > CaveMaxY + 2) break;
            }
        }

        // ---------- Pass 2.5: ravines (cross-chunk via feature grid). ----------
        //
        // Tier 6 #32 — Long surface-piercing fissures. Cross-chunk
        // continuity is the key challenge: a single ravine spans
        // multiple chunks, but terrain generation is per-chunk, and
        // chunks may generate in any order (initial radius vs.
        // streaming). Solution is a coarse "feature grid" — quantize
        // world space into RavineCellSize-sided cells, deterministically
        // seed each cell with `(seed, gx, gz)`, and have every chunk
        // query the small set of cells whose ravines could reach it.
        // Each cell's ravine has stable parameters (origin, angle,
        // length) so the same ravine appears identically regardless
        // of which chunk first triggered the carve.
        private const int RavineCellSize = 96;
        private const int RavineMaxLen = 110;
        private const int RavineMaxHalfWidth = 4;
        // 1 in N feature cells contains a ravine. Higher = rarer.
        private const int RavineSpawnDenominator = 8;

        private static void GenerateRavines(Chunk chunk, Noise noise)
        {
            int chunkBaseX = chunk.ChunkX * Chunk.SizeX;
            int chunkBaseZ = chunk.ChunkZ * Chunk.SizeZ;

            // The set of feature cells whose ravines could reach into
            // this chunk: any cell within (MaxLen + MaxHalfWidth + a
            // small safety margin) of the chunk's bounds. Floor-divide
            // for negative coords so the grid stays aligned across
            // the world origin.
            int reach = RavineMaxLen + RavineMaxHalfWidth + 2;
            int gxMin = FloorDiv(chunkBaseX - reach, RavineCellSize);
            int gxMax = FloorDiv(chunkBaseX + Chunk.SizeX + reach, RavineCellSize);
            int gzMin = FloorDiv(chunkBaseZ - reach, RavineCellSize);
            int gzMax = FloorDiv(chunkBaseZ + Chunk.SizeZ + reach, RavineCellSize);

            for (int gz = gzMin; gz <= gzMax; gz++)
            for (int gx = gxMin; gx <= gxMax; gx++)
            {
                // Per-cell deterministic RNG. Mix the world seed with
                // the cell's grid coords so neighbouring cells'
                // ravines aren't correlated; same seed always produces
                // the same ravines.
                int hash = unchecked(noise.Seed
                    + gx * (int)0x9E3779B1
                    + gz * (int)0x85EBCA77
                    + (int)0xC2B2AE35);
                var rng = new Random(hash);
                if (rng.Next(RavineSpawnDenominator) != 0) continue;

                // Ravine parameters — origin inside the cell, random
                // direction, random length within bounds.
                double startX = gx * RavineCellSize + rng.Next(RavineCellSize);
                double startZ = gz * RavineCellSize + rng.Next(RavineCellSize);
                double angle = rng.NextDouble() * Math.PI * 2.0;
                int length = 60 + rng.Next(RavineMaxLen - 60);  // 60..MaxLen-1
                int halfWidthMax = 2 + rng.Next(RavineMaxHalfWidth - 1); // 2..MaxHalfWidth
                int floorY = 8 + rng.Next(5);   // 8..12 — bedrock buffer
                // Ceiling above the highest possible surface so the
                // ravine actually pierces the surface from above
                // rather than ending just below it.
                int ceilY = BaseHeight + HeightAmplitude + 2;

                CarveRavineSlice(chunk, chunkBaseX, chunkBaseZ,
                    startX, startZ, angle, length, halfWidthMax, floorY, ceilY);
            }
        }

        // Walk the ravine's centerline at unit-step intervals, carving
        // the elliptical cross-section at each step. Width tapers
        // toward both ends via a sin(πt) profile so the ravine reads
        // as a fissure with rounded ends rather than a hard rectangle.
        private static void CarveRavineSlice(Chunk chunk,
            int chunkBaseX, int chunkBaseZ,
            double startX, double startZ,
            double angle, int length, int halfWidthMax,
            int floorY, int ceilY)
        {
            double dx = Math.Cos(angle);
            double dz = Math.Sin(angle);
            for (int s = 0; s <= length; s++)
            {
                double cx = startX + s * dx;
                double cz = startZ + s * dz;

                // Width tapers via sin(πt) — 0 at ends, 1 at midpoint.
                double t = (double)s / length;
                double widthFactor = Math.Sin(t * Math.PI);
                int halfWidth = (int)Math.Round(halfWidthMax * widthFactor);
                if (halfWidth < 1) continue;

                // Cull steps outside the chunk's reach early — saves
                // the per-cell carve loop on the >90% of steps that
                // don't intersect this chunk.
                int boxLeft = (int)Math.Floor(cx) - halfWidth;
                int boxRight = (int)Math.Floor(cx) + halfWidth;
                int boxTop = (int)Math.Floor(cz) - halfWidth;
                int boxBottom = (int)Math.Floor(cz) + halfWidth;
                if (boxRight < chunkBaseX || boxLeft >= chunkBaseX + Chunk.SizeX) continue;
                if (boxBottom < chunkBaseZ || boxTop >= chunkBaseZ + Chunk.SizeZ) continue;

                // Elliptical disc carve. Iterate world coords; map to
                // chunk-local; skip cells outside this chunk's bounds.
                int hwSqr = halfWidth * halfWidth;
                for (int wz = boxTop; wz <= boxBottom; wz++)
                for (int wx = boxLeft; wx <= boxRight; wx++)
                {
                    int ddx = wx - (int)Math.Floor(cx);
                    int ddz = wz - (int)Math.Floor(cz);
                    if (ddx * ddx + ddz * ddz > hwSqr) continue;

                    int lx = wx - chunkBaseX;
                    int lz = wz - chunkBaseZ;
                    if ((uint)lx >= Chunk.SizeX || (uint)lz >= Chunk.SizeZ) continue;

                    for (int y = floorY; y <= ceilY && y < Chunk.SizeY; y++)
                    {
                        int idx = Chunk.Index(lx, y, lz);
                        byte b = chunk.RawBlocks[idx];
                        // Carve only through natural rock-family
                        // blocks. Skip bedrock (safety), already-air,
                        // and water (don't drain the sea into the
                        // ravine — water pass handles flooding any
                        // sub-sea-level air).
                        if (b == (byte)BlockType.Stone ||
                            b == (byte)BlockType.Dirt ||
                            b == (byte)BlockType.Gravel ||
                            b == (byte)BlockType.Grass ||
                            b == (byte)BlockType.Sand ||
                            b == (byte)BlockType.CoalOre ||
                            b == (byte)BlockType.IronOre ||
                            b == (byte)BlockType.GoldOre ||
                            b == (byte)BlockType.RedstoneOre ||
                            b == (byte)BlockType.DiamondOre)
                        {
                            chunk.RawBlocks[idx] = (byte)BlockType.Air;
                        }
                    }
                }
            }
        }

        // Floor division that handles negative numerators correctly.
        // C#'s `/` truncates toward zero, so e.g. -33 / 96 == 0 —
        // wrong for grid alignment across the world origin. We need
        // FloorDiv(-33, 96) == -1.
        private static int FloorDiv(int a, int b)
        {
            int q = a / b;
            if ((a ^ b) < 0 && q * b != a) q -= 1;
            return q;
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

        // ---------- Pass 3.5: fluid features (lakes, pools, springs). ----------
        //
        // Tier 6 #33 — Three flavors of decorative fluid:
        //   * Surface lava lakes — rare disc-shaped pools of lava
        //     placed on top of land far from the sea. Visible from
        //     a distance; gives the overworld dramatic landmarks.
        //   * Underground pools — water OR lava pockets sitting on
        //     cave floors. Common; adds atmosphere to spelunking
        //     and gates risky paths with hazard fluid.
        //   * Cliff-face springs — single source water blocks
        //     embedded in stone walls so the existing FluidTick
        //     spreads them into visible cascades down a cliff.
        //
        // All three deterministic per (seed, chunk coords) so the
        // same world generates the same features every time. Fluid
        // sources are placed as source blocks (Water / Lava); the
        // FluidTick handles flow, evaporation, and water-meets-lava
        // contact (Tier 6 #36 — separate ship).
        private static void GenerateFluidFeatures(Chunk chunk, Noise noise)
        {
            // Surface lava lakes were removed — even with the windowed
            // flatness probe the cost of finding a flat enough patch
            // showed up as a ~300-FPS dip during chunk-stream-in. The
            // visual payoff (occasional surface lava blob) wasn't
            // worth the spike. Underground pools cover the "lava
            // somewhere on the map" itch and run cheaply because
            // their pre-checks are O(1) cell reads.

            // Underground pools — 0..2 attempts per chunk; each
            // looks for a flat cave floor and pools water or lava.
            var poolRng = ChunkRng(noise.Seed, chunk.ChunkX, chunk.ChunkZ, 0x9001);
            int poolAttempts = poolRng.Next(3);
            for (int i = 0; i < poolAttempts; i++)
                TryPlaceUndergroundPool(chunk, poolRng);

            // Cliff-face springs — ~1 in 8 chunks tries to place one
            // (subject to finding a valid wall cell). Springs are
            // small visual flourishes; the fluid tick handles the
            // cascade.
            var springRng = ChunkRng(noise.Seed, chunk.ChunkX, chunk.ChunkZ, 0xCF12);
            if (springRng.Next(8) == 0)
                TryPlaceCliffSpring(chunk, springRng);
        }

        // Underground pool — pick a random Y in the cave-eligible
        // band, find an air cell with a stone floor, scoop a small
        // bowl, fill with water (shallower) or lava (deeper). Skips
        // gracefully when the random spot doesn't satisfy the floor +
        // air-above check, which is most of the time.
        private static void TryPlaceUndergroundPool(Chunk chunk, Random rng)
        {
            int x = 2 + rng.Next(Chunk.SizeX - 4);
            int z = 2 + rng.Next(Chunk.SizeZ - 4);
            int y = 6 + rng.Next(Math.Max(1, SeaLevel - 8));
            // Need: cell at (x,y,z) air, floor at (x,y-1,z) stone,
            // ceiling at (x,y+1,z) air (so the player can see it).
            if (y <= 0 || y >= Chunk.SizeY - 1) return;
            var hereT = (BlockType)chunk.RawBlocks[Chunk.Index(x, y, z)];
            if (hereT != BlockType.Air) return;
            var floorT = (BlockType)chunk.RawBlocks[Chunk.Index(x, y - 1, z)];
            if (floorT != BlockType.Stone) return;
            var ceilT = (BlockType)chunk.RawBlocks[Chunk.Index(x, y + 1, z)];
            if (ceilT != BlockType.Air) return;

            // Lava if we're deep (Y < 16); water if shallower.
            // Matches Alpha 1.1.2_01 — lava lakes deep, water pools
            // closer to the surface.
            BlockType fluid = y < 16 ? BlockType.Lava : BlockType.Water;
            int radius = 1 + rng.Next(2); // 1..2
            for (int dz = -radius; dz <= radius; dz++)
            for (int dx = -radius; dx <= radius; dx++)
            {
                if (dx * dx + dz * dz > radius * radius) continue;
                int px = x + dx, pz = z + dz;
                if ((uint)px >= Chunk.SizeX || (uint)pz >= Chunk.SizeZ) continue;
                int idx = Chunk.Index(px, y, pz);
                // Only fill cells that are currently air; don't
                // overwrite cave walls or stone the cave gen left.
                if (chunk.RawBlocks[idx] != (byte)BlockType.Air) continue;
                // Floor under each pool cell needs to be solid so the
                // fluid doesn't drain through. Patch holes with stone.
                int floorIdx = Chunk.Index(px, y - 1, pz);
                var below = (BlockType)chunk.RawBlocks[floorIdx];
                if (below != BlockType.Stone && below != BlockType.Cobblestone
                    && below != BlockType.MossyCobblestone && below != BlockType.Dirt
                    && below != BlockType.Gravel)
                {
                    chunk.RawBlocks[floorIdx] = (byte)BlockType.Stone;
                }
                chunk.RawBlocks[idx] = (byte)fluid;
            }
        }

        // Cliff-face spring — find a stone cell at the chunk's edge
        // exposure (a stone block with at least one horizontal
        // face open to air, AND solid stone above + below so the
        // spring sticks out from a wall rather than the top of a
        // hill). Replace the stone with a water source; FluidTick
        // will spread it into a visible cascade.
        private static void TryPlaceCliffSpring(Chunk chunk, Random rng)
        {
            // Try up to 12 random positions; bail if none qualify.
            for (int attempt = 0; attempt < 12; attempt++)
            {
                int x = 1 + rng.Next(Chunk.SizeX - 2);
                int z = 1 + rng.Next(Chunk.SizeZ - 2);
                // Y range: above sea level but well below max (need
                // exposed stone on a hillside).
                int y = SeaLevel + 4 + rng.Next(Math.Max(1, BaseHeight + HeightAmplitude - SeaLevel - 6));
                if (y <= 0 || y >= Chunk.SizeY - 1) continue;
                var hereT = (BlockType)chunk.RawBlocks[Chunk.Index(x, y, z)];
                if (hereT != BlockType.Stone) continue;
                var aboveT = (BlockType)chunk.RawBlocks[Chunk.Index(x, y + 1, z)];
                if (aboveT != BlockType.Stone && aboveT != BlockType.Dirt && aboveT != BlockType.Grass) continue;
                var belowT = (BlockType)chunk.RawBlocks[Chunk.Index(x, y - 1, z)];
                if (belowT != BlockType.Stone) continue;
                // Need at least one horizontal face exposed to air.
                bool exposed =
                       IsAirAt(chunk, x - 1, y, z)
                    || IsAirAt(chunk, x + 1, y, z)
                    || IsAirAt(chunk, x, y, z - 1)
                    || IsAirAt(chunk, x, y, z + 1);
                if (!exposed) continue;
                chunk.RawBlocks[Chunk.Index(x, y, z)] = (byte)BlockType.Water;
                return;
            }
        }

        private static bool IsAirAt(Chunk chunk, int x, int y, int z)
        {
            if ((uint)x >= Chunk.SizeX || (uint)z >= Chunk.SizeZ) return false;
            if (y < 0 || y >= Chunk.SizeY) return false;
            return chunk.RawBlocks[Chunk.Index(x, y, z)] == (byte)BlockType.Air;
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

                // Tier 6 #37 — Biome-gated tree density.
                //   Forest → 1/60  (≈ 4× plains scatter)
                //   Plains → 1/240 (canonical Alpha-default)
                //   Desert → no trees (sand columns can't grow oaks)
                //   Snow   → 1/360 (sparse snowy oaks for variety)
                Biome biome = BiomeMap.Classify(noise, wx, wz);
                int density;
                switch (biome)
                {
                    case Biome.Forest: density = 60;  break;
                    case Biome.Desert: continue;
                    case Biome.Snow:   density = 360; break;
                    default:           density = 240; break; // Plains
                }
                if (colRng.Next(density) != 0) continue;

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

        // ---------- Pass 5: surface flora (flowers, mushrooms, tall grass). ----------

        // Deterministic per-column scatter on grass surfaces. We iterate over
        // exactly the chunk's own columns (not a border) because each flora
        // block is a single 1-block sprite that fits in its column — there's
        // no canopy to bleed into a neighbour. The per-column hash uses the
        // same seeding scheme as trees, with a different tag, so the same
        // block never lands in the same cell as a tree trunk on regen.
        private static void GenerateFlora(Chunk chunk, Noise noise)
        {
            int baseX = chunk.ChunkX * Chunk.SizeX;
            int baseZ = chunk.ChunkZ * Chunk.SizeZ;

            for (int lx = 0; lx < Chunk.SizeX; lx++)
            for (int lz = 0; lz < Chunk.SizeZ; lz++)
            {
                int wx = baseX + lx;
                int wz = baseZ + lz;
                // Different tag to keep this stream independent of the tree
                // RNG, while still being deterministic per (seed, wx, wz).
                var rng = new Random(unchecked(ColumnHash(noise.Seed, wx, wz) * (int)0x9E3779B1));

                // Tier 6 #37 — Flora dispatched by biome.
                //   Plains / Forest → flowers + mushrooms (the
                //                      original code path, kept below).
                //   Desert          → cacti (3-tall) + dead bushes
                //                      (cross-sprite). Routed to a
                //                      dedicated branch — desert flora
                //                      sits on Sand, not Grass, so the
                //                      same support-block check below
                //                      can't be reused.
                //   Snow            → no flora yet (saplings on snow
                //                      planned for a later phase).
                Biome biome = BiomeMap.Classify(noise, wx, wz);
                if (biome == Biome.Snow) continue;
                if (biome == Biome.Desert)
                {
                    PlaceDesertFlora(chunk, lx, lz, wx, wz, noise, rng);
                    continue;
                }

                // ~1/16 chance to place SOMETHING. Scaled high enough that a
                // grass field reads as "decorated" without becoming a meadow.
                if (rng.Next(16) != 0) continue;

                int surface = SurfaceHeight(noise, wx, wz);
                if (surface <= BeachHeight) continue;          // skip beach/ocean
                int placeY = surface;                            // one above ground
                if (placeY >= Chunk.SizeY) continue;
                int groundY = surface - 1;

                // The cell directly below must be grass and the placement
                // cell itself must currently be air. This skips cave openings
                // (where the surface column was carved away) and tree/leaf
                // squares we can't see through to the ground for.
                int placeIdx = Chunk.Index(lx, placeY, lz);
                int groundIdx = Chunk.Index(lx, groundY, lz);
                if (chunk.RawBlocks[placeIdx] != (byte)BlockType.Air) continue;
                if (chunk.RawBlocks[groundIdx] != (byte)BlockType.Grass) continue;

                // Roll the type. Alpha 1.1.2_01 had no tall grass yet — the
                // surface flora set is just flowers + mushrooms. Dandelions
                // dominate (commonest in alpha grass plains), roses next,
                // mushrooms rare (alpha placed brown/red ones mostly in dim
                // places — we still surface-spawn a few so the world isn't
                // barren of them until we add cave-spawn).
                BlockType pick;
                int r = rng.Next(100);
                if (r < 55)      pick = BlockType.Dandelion;
                else if (r < 90) pick = BlockType.Rose;
                else if (r < 96) pick = BlockType.BrownMushroom;
                else             pick = BlockType.RedMushroom;

                chunk.RawBlocks[placeIdx] = (byte)pick;
            }
        }

        // Tier 6 #37 Phase 2 — Freeze water surfaces in Snow biomes.
        // Walks every column of the chunk, checks if its biome is
        // Snow, and if so converts the topmost Water cell (at sea
        // level) to Ice. We freeze ONLY the surface cell — the water
        // body underneath stays liquid so the player can dig through
        // the ice and find a normal pond beneath. Skips chunks whose
        // entire footprint is non-Snow biome to avoid the per-column
        // sample on common cases.
        private static void FreezeWaterInSnowBiome(Chunk chunk, Noise noise)
        {
            for (int x = 0; x < Chunk.SizeX; x++)
            for (int z = 0; z < Chunk.SizeZ; z++)
            {
                int wx = chunk.ChunkX * Chunk.SizeX + x;
                int wz = chunk.ChunkZ * Chunk.SizeZ + z;
                if (BiomeMap.Classify(noise, wx, wz) != Biome.Snow) continue;
                // Find the topmost water cell (sea-level surface)
                // and freeze it. The column's water cells are
                // contiguous from surfaceHeight up to SeaLevel-1, so
                // SeaLevel-1 itself is the topmost wet cell when the
                // column is below water. If it isn't water, the
                // column is land — skip.
                int topWaterY = SeaLevel - 1;
                int idx = Chunk.Index(x, topWaterY, z);
                if (chunk.RawBlocks[idx] != (byte)BlockType.Water) continue;
                chunk.RawBlocks[idx] = (byte)BlockType.Ice;
            }
        }

        // Tier 6 #37 Phase 4 — Scatter a 1/8-tall SnowBlock layer
        // on top of every Grass/Dirt surface column in a Snow-biome
        // chunk. Skipped for non-Snow columns; trivially cheap when
        // the chunk is entirely outside Snow because the per-column
        // biome classify is the only work done.
        private static void ScatterSnowLayer(Chunk chunk, Noise noise)
        {
            for (int x = 0; x < Chunk.SizeX; x++)
            for (int z = 0; z < Chunk.SizeZ; z++)
            {
                int wx = chunk.ChunkX * Chunk.SizeX + x;
                int wz = chunk.ChunkZ * Chunk.SizeZ + z;
                if (BiomeMap.Classify(noise, wx, wz) != Biome.Snow) continue;
                int surface = SurfaceHeight(noise, wx, wz);
                if (surface <= BeachHeight) continue;
                int placeY = surface;                   // one above ground
                if (placeY >= Chunk.SizeY) continue;
                int placeIdx = Chunk.Index(x, placeY, z);
                int groundIdx = Chunk.Index(x, surface - 1, z);
                // Bail if the placement cell isn't bare air — never
                // overwrite a tree trunk (WoodLog) or canopy block
                // (Leaves) sitting at the surface. Same shape as
                // GenerateFlora's "must be air" guard.
                if (chunk.RawBlocks[placeIdx] != (byte)BlockType.Air) continue;
                // Ground must be a normal grass / dirt cap. Catches
                // the cave-roof / ravine-edge case where the noise-
                // derived surface column was carved away.
                var ground = (BlockType)chunk.RawBlocks[groundIdx];
                if (ground != BlockType.Grass && ground != BlockType.Dirt) continue;
                // Bail if a tree's trunk or canopy occupies the cell
                // directly above the placement cell. Catches trees
                // seeded from neighbour-chunk columns whose lowest
                // log lands at placeY: the tree's REAL trunk base
                // is at placeY (not placeY + 1) and the existing
                // placeIdx air-check above already covers that, but
                // when a neighbour-seeded tree's CANOPY drapes over
                // a snow column with placeY one cell BELOW the
                // canopy band — i.e. a leaf at placeY+1 — we still
                // don't want to lay snow on the bare grass beneath
                // it. Visually that reads as "snow falling through
                // the canopy gaps" which is the exact symptom the
                // user reported. One-cell scan is enough; deeper
                // is overkill (the canopy slab is contiguous).
                int aboveIdx = Chunk.Index(x, placeY + 1, z);
                if (placeY + 1 < Chunk.SizeY)
                {
                    var above = (BlockType)chunk.RawBlocks[aboveIdx];
                    if (above == BlockType.WoodLog || above == BlockType.Leaves) continue;
                }
                chunk.RawBlocks[placeIdx] = (byte)BlockType.SnowBlock;
            }
        }

        // Tier 6 #37 Phase 2 — Desert flora. Places a 3-tall cactus
        // column on a Sand surface. Dead bush is omitted because it
        // wasn't in Alpha 1.1.2_01 (Beta-era addition); the desert
        // reads as bare sand with occasional cactus landmarks. Both
        // require a Sand surface block + 3 cells of clear air above
        // for the column's headroom.
        private static void PlaceDesertFlora(
            Chunk chunk, int lx, int lz, int wx, int wz, Noise noise, Random rng)
        {
            int surface = SurfaceHeight(noise, wx, wz);
            if (surface <= BeachHeight) return;                    // beach skip (sand at sea level — leave bare)
            int placeY = surface;
            if (placeY >= Chunk.SizeY) return;
            int groundY = surface - 1;

            int placeIdx  = Chunk.Index(lx, placeY,  lz);
            int groundIdx = Chunk.Index(lx, groundY, lz);
            if (chunk.RawBlocks[placeIdx] != (byte)BlockType.Air) return;
            if (chunk.RawBlocks[groundIdx] != (byte)BlockType.Sand) return;

            // 1/80 chance per column. Probability chosen so a 16×16
            // chunk averages <1 cactus — they read as landmarks
            // rather than scenery clutter.
            if (rng.Next(80) != 0) return;

            // Cactus stack — needs 3 cells of clear headroom.
            if (placeY + 2 >= Chunk.SizeY) return;
            int idx0 = placeIdx;
            int idx1 = Chunk.Index(lx, placeY + 1, lz);
            int idx2 = Chunk.Index(lx, placeY + 2, lz);
            if (chunk.RawBlocks[idx1] != (byte)BlockType.Air) return;
            if (chunk.RawBlocks[idx2] != (byte)BlockType.Air) return;
            chunk.RawBlocks[idx0] = (byte)BlockType.Cactus;
            chunk.RawBlocks[idx1] = (byte)BlockType.Cactus;
            chunk.RawBlocks[idx2] = (byte)BlockType.Cactus;
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
