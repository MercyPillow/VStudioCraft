using System;

namespace VStudioCraft.Game
{
    // Tier 8 #51 V12 — Alpha-canonical (Beta 1.7.3) terrain density sampler.
    //
    // Both Overworld and Nether dimensions in Alpha 1.2.0 / Beta 1.7.3
    // share the same density-field algorithm: sample a coarse 5×5×17
    // grid of 3D-noise-blended density per chunk, trilinearly
    // interpolate to per-cell 16×16×128 values, then map density →
    // solid / liquid / air per cell. This sampler centralises that
    // pipeline so both dimensions get identical-quality terrain
    // shape (cliffs, overhangs, floating chunks, deep oceans / lava
    // seas) without each generator re-implementing the noise blend.
    //
    // Algorithm reference: Spottedleaf/OldGenerator's port of the
    // Beta 1.7.3 ChunkProviderHell173.java and ChunkProviderGenerate173.java
    // sources. Eight Perlin instances:
    //   * 2× 16-octave low-density noise fields (low / high)
    //   * 1× 8-octave selector noise — interpolates between low and high
    //   * 1× 10-octave scale + 1× 16-octave depth — vertical envelope shape
    //   * 2× 4-octave surface decoration (sand, gravel)
    //   * 1× 4-octave stone (overworld surface block selection)
    //
    // The sampler is stateless after construction (Noise instances are
    // immutable). World owns one instance per seed; chunk-gen workers
    // share it concurrently (each Noise's `_p` permutation table is
    // read-only after the constructor's shuffle).
    internal sealed class AlphaTerrainNoiseSampler
    {
        public enum Mode { Overworld, Nether }

        // 8 noise instances. Seed-derived so deterministic per world.
        private readonly Noise _selector;   // 8 octaves
        private readonly Noise _low;        // 16 octaves
        private readonly Noise _high;       // 16 octaves
        private readonly Noise _scale;      // 10 octaves — depth scale modulator
        private readonly Noise _depth;      // 16 octaves — depth offset modulator
        private readonly Noise _sandGravel; // 4 octaves
        private readonly Noise _stone;      // 4 octaves
        // Reused for height noise (cover-depth scan in overworld surface pass).
        private readonly Noise _heightNoise; // 4 octaves

        // Frequency constants — tuned for our 2D/3D Perlin which has
        // unit-spaced lattice cells, NOT Beta's NoiseGeneratorPerlin
        // which has its own scale convention. The Beta literal scale
        // values (684.412, 2053.236) interpret as "input multiplier per
        // grid sample" in their octave system — they don't translate
        // directly to our per-cell-frequency model. Instead, we pick
        // frequencies that produce features at the canonical Alpha
        // scale: large smooth mountains ~64 blocks across, vertical
        // structure varying every ~32 blocks.
        //
        // Lattice cell sizes (= 1 / freq):
        //   * Density (low/high): 64 horizontal, 32 vertical → mountain-
        //     scale features that actually vary across a 16-block chunk.
        //   * Selector: smaller lattice (16h / 8v) so it switches
        //     between low and high noise rapidly, adding small-scale
        //     jagged variation overlaid on the big shapes.
        //   * Depth/scale modulator: ~256-block lattice for slow
        //     regional bias (tall/short terrain).
        private const float HorizontalFreq      = 1f / 64f;
        // Vertical lattice raised to 64 blocks so the noise has strong
        // vertical coherence — a column that's "solid" at one Y stays
        // solid for many cells above. This is what lets netherrack
        // pillars extend tall from the cavern floor (sea level) up to
        // the ceiling. Previously 1/32 produced features that were
        // ~32 blocks tall, which broke long pillars in half visually.
        private const float VerticalFreq        = 1f / 64f;
        private const float SelectorHorizontal  = 1f / 16f;
        private const float SelectorVertical    = 1f / 8f;
        // Three-tier height variation. Each contributes to the
        // per-column "natural surface Y" at a different scale.
        //   * DepthFreq (1/80): broad regional bias — mountain ranges
        //     vs lowlands, ~5-chunk scale. Reduced from 1/128 so
        //     individual ocean basins span ~80 blocks instead of ~250
        //     — water bodies feel like lakes/seas rather than the
        //     "huge water areas" the user reported.
        //   * HillsFreq (1/48): mountain-peak scale — individual peaks
        //     and valleys ~3 chunks across.
        //   * TextureFreq (1/16): per-column small-scale roughness —
        //     1-chunk-wide knobs and folds.
        private const float DepthFreq           = 1f / 80f;
        private const float HillsFreq           = 1f / 48f;
        private const float TextureFreq         = 1f / 16f;
        private const float SurfaceFreq         = 1f / 32f;

        public AlphaTerrainNoiseSampler(int seed)
        {
            // Distinct seeds per noise instance so they don't correlate.
            // XOR with prime-ish constants to spread the bits.
            _selector   = new Noise(seed ^ unchecked((int)0x6E37B931));
            _low        = new Noise(seed ^ unchecked((int)0xC7E589FB));
            _high       = new Noise(seed ^ unchecked((int)0x5BD1E995));
            _scale      = new Noise(seed ^ unchecked((int)0xC6A4A793));
            _depth      = new Noise(seed ^ unchecked((int)0x71C4F39D));
            _sandGravel = new Noise(seed ^ unchecked((int)0xA76C8B5D));
            _stone      = new Noise(seed ^ unchecked((int)0x91A37FBD));
            _heightNoise = new Noise(seed ^ unchecked((int)0xD7E2A831));
        }

        // Sample the chunk's 5×5×17 density grid, then trilinearly
        // interpolate to per-cell 16×16×128 density. Output `density`
        // is indexed (x*256 + z*128 + y) so iterating y is the inner
        // loop — matches caller patterns that walk top-down.
        //
        // Beta 1.7.3 note: the Beta source uses (i*zLen + k)*yLen + j
        // ordering — same shape just with different axis ordering. We
        // use x-major because the existing Chunk.RawBlocks layout is
        // x-major.
        public void GenerateChunkDensity(double[] density, int chunkX, int chunkZ, Mode mode)
        {
            // Grid sample positions: 5×5 horizontal at (0,4,8,12,16),
            // 17 vertical at (0,8,16,...,128). Sample noise once at
            // each grid corner, then interpolate.
            const int GridX = 5;
            const int GridY = 17;
            const int GridZ = 5;
            var grid = new double[GridX * GridZ * GridY];
            SampleGrid(grid, chunkX, chunkZ, mode);

            // Trilinear interpolation: for each grid cell (4 wide × 8
            // tall × 4 deep), compute the 4×8×4 = 128 per-block density
            // values from the 8 corner samples.
            //
            // Inner-loop optimisation: cache the 4 vertical-edge
            // densities per (gx, gz) cell so the y-loop only does 4
            // lerps + 1 final lerp instead of 8.
            for (int gx = 0; gx < GridX - 1; gx++)
            for (int gz = 0; gz < GridZ - 1; gz++)
            {
                for (int gy = 0; gy < GridY - 1; gy++)
                {
                    // 8 corners of the grid cell
                    int idx000 = (gx * GridZ + gz) * GridY + gy;
                    double n000 = grid[idx000];
                    double n001 = grid[idx000 + 1];
                    int idx010 = ((gx) * GridZ + (gz + 1)) * GridY + gy;
                    double n010 = grid[idx010];
                    double n011 = grid[idx010 + 1];
                    int idx100 = ((gx + 1) * GridZ + gz) * GridY + gy;
                    double n100 = grid[idx100];
                    double n101 = grid[idx100 + 1];
                    int idx110 = ((gx + 1) * GridZ + (gz + 1)) * GridY + gy;
                    double n110 = grid[idx110];
                    double n111 = grid[idx110 + 1];

                    // Y-step deltas (8 cells per grid step in Y).
                    double dY000 = (n001 - n000) / 8.0;
                    double dY010 = (n011 - n010) / 8.0;
                    double dY100 = (n101 - n100) / 8.0;
                    double dY110 = (n111 - n110) / 8.0;
                    double v00 = n000, v01 = n010, v10 = n100, v11 = n110;

                    for (int dy = 0; dy < 8; dy++)
                    {
                        // X-step deltas at this y (4 cells per step in X).
                        double dX0 = (v10 - v00) / 4.0;
                        double dX1 = (v11 - v01) / 4.0;
                        double rowX0 = v00, rowX1 = v01;

                        for (int dx = 0; dx < 4; dx++)
                        {
                            // Z-step delta at this (x, y) (4 cells per step in Z).
                            double dZ = (rowX1 - rowX0) / 4.0;
                            double cell = rowX0;

                            int wx = gx * 4 + dx;
                            int wy = gy * 8 + dy;
                            for (int dz = 0; dz < 4; dz++)
                            {
                                int wz = gz * 4 + dz;
                                density[(wx * Chunk.SizeZ + wz) * Chunk.SizeY + wy] = cell;
                                cell += dZ;
                            }
                            rowX0 += dX0;
                            rowX1 += dX1;
                        }
                        v00 += dY000;
                        v01 += dY010;
                        v10 += dY100;
                        v11 += dY110;
                    }
                }
            }
        }

        // Sample the 5×5×17 grid — 425 cells × 3 noise samples each.
        // Per-cell algorithm matches ChunkProviderHell173.generateTerrainNoise
        // (Hell mode) and ChunkProviderGenerate173.generateTerrainNoise
        // (Overworld mode). The two share the same low/high/selector
        // blending but use very different vertical envelopes — Hell
        // uses a precomputed cosine-banded 1D envelope with cubic edge
        // penalties (the source of the iconic horizontal solid/sparse
        // strata in Beta nether), Overworld uses a per-column "natural
        // surface Y" with asymmetric below/above pull (the source of
        // the canonical "deep underground always solid, sharp surface
        // transition" feel).
        private void SampleGrid(double[] grid, int chunkX, int chunkZ, Mode mode)
        {
            const int GridX = 5;
            const int GridY = 17;
            const int GridZ = 5;
            int worldX0 = chunkX * 4;
            int worldZ0 = chunkZ * 4;

            // Hell: simple Y-gradient envelope — solid bias near the
            // floor, air bias near the ceiling, smooth transition through
            // the middle. The PRIOR version used Beta's cosine-banded
            // adouble1[] (cos(gy * π * 6 / GridY) * 2 + cubic edge
            // penalty) which creates 6 visible horizontal density
            // strata — the user reported this reads as "concentric
            // circles going up in layers", not Alpha-style. The cave
            // carve pass below provides the variation that makes the
            // cavern feel hand-carved rather than swelled by noise; we
            // don't need to bake it into the envelope.
            for (int gx = 0; gx < GridX; gx++)
            for (int gz = 0; gz < GridZ; gz++)
            {
                float wx = worldX0 + gx;
                float wz = worldZ0 + gz;

                // Per-column "natural surface Y" + "feature scale" —
                // Overworld only. Beta derives these from terrainNoise4
                // (depth) and terrainNoise5 (scale) with non-linear
                // remapping. We use a two-stage approach: a smooth
                // mid-frequency depth sample drives the regional
                // mountain-range bias, plus a higher-frequency jitter
                // sample adds per-column knobs and folds. Combined,
                // surfaceGY can range from ~y=24 (deep valleys) to
                // ~y=104 (mountain peaks), giving Alpha's dramatic
                // vertical variation.
                float surfaceGY = 8f;
                float scaleAmp  = 8f;
                if (mode == Mode.Overworld)
                {
                    // Three-tier height composition. Each term
                    // contributes additively, so peak mountains coincide
                    // with simultaneous "high depth" + "high hills" +
                    // "high texture" samples — a rare alignment, which
                    // is exactly what makes mountain peaks RARE and
                    // dramatic (vs uniform rolling).
                    float depth = _depth.Octaves(
                        wx * DepthFreq, wz * DepthFreq,
                        octaves: 6, persistence: 0.55f, lacunarity: 2f);
                    float hills = _scale.Octaves(
                        wx * HillsFreq, wz * HillsFreq,
                        octaves: 4, persistence: 0.55f, lacunarity: 2f);
                    float texture = _heightNoise.Octaves(
                        wx * TextureFreq, wz * TextureFreq,
                        octaves: 4, persistence: 0.5f, lacunarity: 2f);

                    // surfaceGY composition (grid-y, multiply by 8 for
                    // world-y):
                    //   base 9.5   → world y=76 (above sea level)
                    //   depth × 4  → ±32 world-y (regional bias)
                    //   hills × 3  → ±24 world-y (mountain peaks)
                    //   texture × 1.5 → ±12 world-y (per-column roughness)
                    // Total amplitude: ±8.5 grid-y = ±68 world-y.
                    //
                    // Base raised from 8.0 (mean = sea level → 50% ocean)
                    // to 9.5 (mean = world y=76 → only ~25% ocean) so
                    // the land-to-water ratio is more land-heavy.
                    // Mountain peaks still reach y=120ish via the
                    // combined depth+hills+texture sum on lucky columns;
                    // ocean basins still occur where depth swings
                    // strongly negative.
                    surfaceGY = 9.5f + depth * 4f + hills * 3f + texture * 1.5f;

                    // scaleAmp ∈ [3, 13]. Smaller = sharper surface
                    // transition (cliffs); larger = gentler hills.
                    // Sampled at the depth frequency so it varies on
                    // the same scale as regional terrain bias —
                    // mountain regions tend to have one scaleAmp,
                    // plains another.
                    float scaleSample = _scale.Octaves(
                        wx * DepthFreq + 7777f, wz * DepthFreq + 7777f,
                        octaves: 4, persistence: 0.5f, lacunarity: 2f);
                    scaleAmp = 8f + scaleSample * 5f;
                    if (scaleAmp < 3f) scaleAmp = 3f;
                }

                for (int gy = 0; gy < GridY; gy++)
                {
                    // World y at this grid sample (0..128).
                    float wy = gy * 8;

                    // Low / high / selector — true 3D Perlin samples
                    // (Beta canonical). Vertical scale is 3× horizontal.
                    float low = _low.Octaves3D(
                        wx * HorizontalFreq, wy * VerticalFreq, wz * HorizontalFreq,
                        octaves: 16, persistence: 0.5f, lacunarity: 2f);
                    float high = _high.Octaves3D(
                        wx * HorizontalFreq, wy * VerticalFreq, wz * HorizontalFreq,
                        octaves: 16, persistence: 0.5f, lacunarity: 2f);
                    // Selector at MUCH higher frequency (80× horizontal,
                    // 60× vertical) so it switches rapidly between
                    // picking low vs high.
                    float sel = _selector.Octaves3D(
                        wx * SelectorHorizontal, wy * SelectorVertical, wz * SelectorHorizontal,
                        octaves: 8, persistence: 0.5f, lacunarity: 2f);

                    // Blend low + high. Beta uses a 0..1 clamp:
                    //   selector / 10 + 1 / 2  (i.e. (sel+1)/2 in [0, 1]
                    //   when sel ∈ [-1, 1], unclamped at extremes).
                    // Octaves output is approximately [-1, 1] → t in
                    // [0, 1] for almost all samples. Outside that range,
                    // hard clamp to low or high.
                    float t = (sel + 1f) * 0.5f;
                    float density;
                    if (t < 0f)      density = low;
                    else if (t > 1f) density = high;
                    else             density = low + (high - low) * t;

                    // CRITICAL — un-normalise the noise. Octaves3D
                    // returns total / maxAmp, which gives a tight
                    // distribution (σ ≈ 0.12). With that distribution,
                    // an envelope of ±0.4 means only ~0.05% of cells
                    // cross zero, producing essentially zero pillars.
                    // Beta's noise is NOT normalised — it returns the
                    // raw octave sum (σ ≈ 0.5+), which is what allows
                    // its envelope values to actually carve a 25%-30%
                    // solid coverage in the cavern. Multiplying by 5
                    // here lifts σ to ≈ 0.6, restoring sane envelope
                    // semantics: an envelope of +0.4 now means ~25%
                    // of cells are positive (solid pillars), and an
                    // envelope of -0.3 means ~30% are negative
                    // (lava lakes). This is the math fix that makes
                    // the envelope tuning actually visible in the
                    // generated world.
                    density *= 5f;

                    // Vertical envelope — dimension-specific.
                    if (mode == Mode.Nether)
                    {
                        // Hell envelope V5 — designed for "massive lava
                        // lakes with land around the lava level, and
                        // pillars reaching from land up to the roof":
                        //   * gy=0..1: bedrock floor area, very solid
                        //   * gy=2..7: mostly NETHERRACK LAND (~70%)
                        //              with massive LAVA LAKES (~30%
                        //              of horizontal area). The lakes
                        //              are clustered (low-freq noise)
                        //              so they form recognisable
                        //              bodies of lava rather than
                        //              scattered single-cell pits.
                        //   * gy=8..13: open cavern with substantial
                        //              netherrack pillars / walls /
                        //              land bridges (~30% solid). The
                        //              strong vertical coherence
                        //              (1/64 lattice) means pillars
                        //              that start solid at sea level
                        //              extend up to the ceiling. Some
                        //              pillars meet the ceiling, some
                        //              are floating chunks, some are
                        //              broken-up walls — the variety
                        //              comes from per-column noise.
                        //   * gy=14..15: solid netherrack ceiling.
                        // The cave + chasm carve passes that follow
                        // open the dense lower mass into chasms reaching
                        // the lava ocean.
                        // Envelope values, given the density σ ≈ 0.6
                        // after the ×5 un-normalisation:
                        //   * envelope -10  → ~100% solid
                        //   * envelope -0.6 → ~85% solid (sea level)
                        //   * envelope +0.4 → ~25% solid (cavern pillars)
                        //   * envelope -10  → ~100% solid (ceiling)
                        // Sea-level envelope tightened from -0.3 to -0.6
                        // to halve lava lake coverage (~30% → ~15%).
                        // Effect: a stricter "noise must be very
                        // negative to become air-filled-with-lava"
                        // threshold means only the noise's deepest
                        // troughs become lakes, so the visible lakes
                        // are both fewer AND smaller (the previous
                        // 30% coverage included shallow-trough patches
                        // that fattened lakes around their edges).
                        float envelope;
                        if (gy <= 1)        envelope = -10f;     // bedrock-floor area, always solid
                        else if (gy <= 7)   envelope = -0.6f;    // ~85% land + ~15% lava lakes (halved)
                        else if (gy <= 13)  envelope =  0.4f;    // cavern with ~25% solid pillars/walls
                        else                envelope = -10f;     // solid ceiling
                        density -= envelope;

                        // Top-fade is INTENTIONALLY skipped for Nether
                        // — the bedrock cap (y=127) and the partial-
                        // bedrock band (y=122..126) handle the very
                        // top. If we ran the top-fade here it would
                        // pull density toward -10 at gy 14..16, leaving
                        // an air gap between the netherrack ceiling and
                        // the bedrock that the user noticed and
                        // disliked.
                        grid[(gx * GridZ + gz) * GridY + gy] = density;
                        continue;
                    }
                    else
                    {
                        // Overworld: per-column natural surface Y with
                        // asymmetric pull. Below-surface pull is strong
                        // (×4) so underground is reliably solid; above-
                        // surface pull is reduced (5/scaleAmp instead
                        // of 12) so noise can occasionally push density
                        // positive several grid steps above the natural
                        // surface — this is what creates mountain peaks
                        // rising tall above the rolling baseline.
                        float d9 = (gy - surfaceGY) * 5f / scaleAmp;
                        if (d9 < 0f) d9 *= 4f;
                        density -= d9;
                    }

                    // Top fade — force density toward -10 in the top
                    // 3 grid rows (gy > GridY - 4 = gy in {14, 15, 16}).
                    // For Hell this overrides the cubic-edge solid
                    // bias so the top of the cavern reads as air just
                    // below the bedrock cap. For Overworld this caps
                    // the world height at ~grid-y=14 (world y=112) so
                    // mountain peaks don't reach the build limit.
                    if (gy > GridY - 4)
                    {
                        float fadeT = (gy - (GridY - 4)) / 3f;
                        density = density * (1f - fadeT) + (-10f) * fadeT;
                    }

                    grid[(gx * GridZ + gz) * GridY + gy] = density;
                }
            }
        }

        // Per-column 2D noise for surface decoration: sand vs gravel
        // vs stone overlay. Output arrays are 16×16 (256 entries),
        // indexed (x * 16 + z). Used by both dimensions: Nether uses
        // sand → SoulSand, gravel → Gravel; Overworld uses sand →
        // beach Sand, gravel → riverbed Gravel.
        public void SampleSurfaceNoise(double[] sand, double[] gravel, int chunkX, int chunkZ)
        {
            int worldX0 = chunkX * Chunk.SizeX;
            int worldZ0 = chunkZ * Chunk.SizeZ;
            for (int x = 0; x < Chunk.SizeX; x++)
            for (int z = 0; z < Chunk.SizeZ; z++)
            {
                float wx = worldX0 + x;
                float wz = worldZ0 + z;
                sand[x * Chunk.SizeZ + z] = _sandGravel.Octaves(
                    wx * SurfaceFreq, wz * SurfaceFreq,
                    octaves: 4, persistence: 0.5f, lacunarity: 2f);
                gravel[x * Chunk.SizeZ + z] = _sandGravel.Octaves(
                    wx * SurfaceFreq + 1234f, wz * SurfaceFreq + 1234f,
                    octaves: 4, persistence: 0.5f, lacunarity: 2f);
            }
        }

        // Per-column "cover depth" noise — drives how thick the dirt
        // band below the surface block is in the Overworld.
        // Beta formula: cover = heightNoise/3 + 3 + rng*0.25, giving
        // a ~3..7 cell range. We just expose the raw octave; caller
        // applies the divide and offset.
        public void SampleHeightNoise(double[] height, int chunkX, int chunkZ)
        {
            int worldX0 = chunkX * Chunk.SizeX;
            int worldZ0 = chunkZ * Chunk.SizeZ;
            for (int x = 0; x < Chunk.SizeX; x++)
            for (int z = 0; z < Chunk.SizeZ; z++)
            {
                float wx = worldX0 + x;
                float wz = worldZ0 + z;
                height[x * Chunk.SizeZ + z] = _heightNoise.Octaves(
                    wx * SurfaceFreq, wz * SurfaceFreq,
                    octaves: 4, persistence: 0.5f, lacunarity: 2f);
            }
        }
    }
}
