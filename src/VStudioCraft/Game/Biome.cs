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
        // Feature-scale: biome boundaries vary on ~256-block patches.
        // 1/256 is the spatial frequency we feed Perlin so adjacent
        // chunks (16 blocks each) read very similar values, with
        // long-distance variation for the actual biome transitions.
        private const float BiomeScale = 1f / 256f;

        // Octave count for the temperature / rainfall channels. 2
        // octaves give a soft transition zone with a touch of noise
        // detail at the borders so biome edges aren't perfect circles.
        private const int BiomeOctaves = 2;

        // Decorrelation offsets — sampled far apart on the same Perlin
        // field so the two channels look independent without needing a
        // second seeded permutation table.
        private const int TempOffset = 10_000;
        private const int RainOffset = -10_000;

        public static Biome Classify(Noise noise, int wx, int wz)
        {
            float temp = noise.Octaves((wx + TempOffset) * BiomeScale, (wz + TempOffset) * BiomeScale, BiomeOctaves);
            float rain = noise.Octaves((wx + RainOffset) * BiomeScale, (wz + RainOffset) * BiomeScale, BiomeOctaves);

            // Thresholds tuned so Plains is the most common biome
            // (default-ish climate), with Snow + Desert as the
            // extremes and Forest as the wet-mild zone. Octaves()
            // returns approx [-1, +1].
            if (temp < -0.35f)                    return Biome.Snow;
            if (temp >  0.35f && rain < -0.10f)   return Biome.Desert;
            if (rain >  0.20f)                    return Biome.Forest;
            return Biome.Plains;
        }
    }
}
