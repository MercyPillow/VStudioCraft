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
        // Ocean — explicit water-body biome. Sampled from its own
        // low-frequency noise field independent of temp/rain. Columns
        // classified as Ocean get a very low surface Y (= deep seafloor)
        // and a sand/gravel/dirt seabed surface block instead of grass.
        // Used to compensate for Plains/Desert never naturally dipping
        // below sea level under the new biome-amplitude profiles —
        // without this, the world is almost all land.
        Ocean  = 4,
    }

    internal static class BiomeMap
    {
        // Frequencies — Tuned to give Snow + Desert regions enough
        // geographic extent that a real mountain range / sand sea
        // can form inside them.
        //
        //   * TempFreq 0.003 → ~333-block lattice. Snow biomes (cold
        //     extremes of the temperature field) reliably span 4+
        //     chunks radius (= 64+ blocks radius) per the user
        //     request — most snow regions are 150-300 blocks across,
        //     plenty of room for a tall mountain range with multiple
        //     peaks visible from the centre.
        //   * RainFreq 0.020 → ~50-block lattice. Deserts span
        //     ~100-200 blocks.
        //
        // Same noise field shared with AlphaTerrainNoiseSampler so
        // the cold-bias mountain pass aligns precisely.
        public const float TempFreq = 0.003f;
        public const float RainFreq = 0.020f;

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

        // Ocean — independent noise field, low frequency (~100-block
        // lattice) so individual oceans are sea-sized, not lake-sized.
        // OceanThreshold 0.4 with σ ≈ 0.4 gives ~16% ocean coverage —
        // "a little rare" as requested. Sampled with its own offset
        // on the world's main Noise instance so it's independent of
        // temp/rain (an ocean can be cold or warm regardless).
        public const float OceanFreq      = 0.01f;
        public const int   OceanOffset    = 25_000;
        public const float OceanThreshold = 0.4f;

        public static Biome Classify(Noise noise, int wx, int wz)
        {
            // Ocean check FIRST — overrides temp/rain biome
            // classification. A column where the ocean field reads
            // strongly positive is water regardless of climate.
            float ocean = noise.Octaves((wx + OceanOffset) * OceanFreq, (wz + OceanOffset) * OceanFreq, BiomeOctaves);
            if (ocean > OceanThreshold) return Biome.Ocean;

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
