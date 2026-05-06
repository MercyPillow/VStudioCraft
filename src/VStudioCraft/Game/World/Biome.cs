namespace VStudioCraft.Game
{
    // Tier 6 #37 — Biome classifier. Two-axis (temperature, rainfall)
    // noise determines which biome a column belongs to. Both axes are
    // sampled from the same Noise instance at far-apart offsets so the
    // two streams are effectively decorrelated without needing a
    // second permutation table — Perlin noise sampled 10,000 cells
    // apart looks independent.
    //
    // Biomes are coarse — feature size ~256 blocks — so a player can
    // walk for several minutes in one biome before a transition. This
    // matches Alpha 1.1.2_01's biome scale (large patches, soft
    // gradients) rather than later versions' tighter biome borders.
    //
    // Surface-block / tree-density / flora rules are dispatched on
    // this enum inside TerrainGenerator. New biomes (e.g. swamp,
    // jungle) plug in by extending the enum + adding cases in those
    // dispatchers. Saved chunks store concrete blocks, not the biome
    // id, so changing the classifier later only affects newly-
    // generated chunks (existing terrain is permanent once written).
    internal enum Biome : byte
    {
        Plains = 0,
        Forest = 1,
        Desert = 2,
        Snow   = 3,
    }

    internal static class BiomeMap
    {
        // Frequencies — Beta canonical for rainfall (0.05 / 20-block
        // lattice). Temperature lowered to 0.012 (~80-block lattice)
        // so snow biomes are LARGER than plains/forest patches when
        // they appear — matches the user request for "rarer + larger
        // snow biomes". Same field shared with AlphaTerrainNoiseSampler
        // so the cold-bias mountain pass aligns: a column that
        // classifies as Snow here also gets the surface-Y upward bias
        // there, making snow biomes reliably mountainous.
        public const float TempFreq = 0.012f;
        public const float RainFreq = 0.05f;

        // Octave count — Beta uses 4 octaves for temp and rainfall.
        public const int BiomeOctaves = 4;

        // Decorrelation offsets — sampled far apart on the same Perlin
        // field so the two channels look independent without needing a
        // second seeded permutation table. Public so AlphaSampler can
        // sample the SAME temperature field for its mountain bias.
        public const int TempOffset = 10_000;
        public const int RainOffset = -10_000;

        // Snow threshold — temperature below this value classifies as
        // Snow biome. Lowered from -0.35 to -0.55 so snow is RARER:
        // with σ ≈ 0.4 for 4-octave Perlin the previous threshold gave
        // ~20% snow coverage; the new threshold gives ~8%. Combined
        // with the lower temp frequency (bigger lattice cells), the
        // result is large infrequent snow regions instead of small
        // common ones.
        public const float SnowThreshold = -0.55f;

        public static Biome Classify(Noise noise, int wx, int wz)
        {
            float temp = noise.Octaves((wx + TempOffset) * TempFreq, (wz + TempOffset) * TempFreq, BiomeOctaves);
            float rain = noise.Octaves((wx + RainOffset) * RainFreq, (wz + RainOffset) * RainFreq, BiomeOctaves);

            // Thresholds tuned so Plains is the most common biome
            // (default-ish climate), with Snow + Desert as the
            // extremes and Forest as the wet-mild zone. Octaves()
            // returns approx [-1, +1].
            if (temp < SnowThreshold)             return Biome.Snow;
            if (temp >  0.35f && rain < -0.10f)   return Biome.Desert;
            if (rain >  0.20f)                    return Biome.Forest;
            return Biome.Plains;
        }
    }
}
