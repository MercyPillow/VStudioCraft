using System;

namespace VStudioCraft.Game
{
    // Ken Perlin's improved 2D noise with octave summation (fBm).
    internal sealed class Noise
    {
        private readonly int[] _p = new int[512];

        public int Seed { get; }

        public Noise(int seed)
        {
            Seed = seed;
            var rng = new Random(seed);
            var perm = new int[256];
            for (int i = 0; i < 256; i++) perm[i] = i;
            for (int i = 255; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                int t = perm[i]; perm[i] = perm[j]; perm[j] = t;
            }
            for (int i = 0; i < 512; i++) _p[i] = perm[i & 255];
        }

        private static float Fade(float t) => t * t * t * (t * (t * 6 - 15) + 10);
        private static float Lerp(float t, float a, float b) => a + t * (b - a);

        private static float Grad(int hash, float x, float y)
        {
            int h = hash & 7;
            float u = h < 4 ? x : y;
            float v = h < 4 ? y : x;
            return ((h & 1) != 0 ? -u : u) + ((h & 2) != 0 ? -2f * v : 2f * v);
        }

        public float Perlin2D(float x, float y)
        {
            int X = (int)Math.Floor(x) & 255;
            int Y = (int)Math.Floor(y) & 255;
            x -= (float)Math.Floor(x);
            y -= (float)Math.Floor(y);
            float u = Fade(x);
            float v = Fade(y);
            int A = _p[X] + Y;
            int B = _p[X + 1] + Y;
            return Lerp(v,
                Lerp(u, Grad(_p[A], x, y),       Grad(_p[B], x - 1, y)),
                Lerp(u, Grad(_p[A + 1], x, y - 1), Grad(_p[B + 1], x - 1, y - 1)));
        }

        public float Octaves(float x, float y, int octaves, float persistence = 0.5f, float lacunarity = 2f)
        {
            float total = 0f;
            float amplitude = 1f;
            float frequency = 1f;
            float maxAmp = 0f;
            for (int i = 0; i < octaves; i++)
            {
                total += Perlin2D(x * frequency, y * frequency) * amplitude;
                maxAmp += amplitude;
                amplitude *= persistence;
                frequency *= lacunarity;
            }
            return total / maxAmp;
        }

        // Tier 8 #51 V12 — 3D Perlin noise (Ken Perlin's improved variant).
        // Needed for the Beta-canonical Alpha terrain generator: density
        // sampled per (x, y, z) so we get true overhangs / floating
        // chunks / cliffs, vs. the per-column heightmap that 2D-only
        // noise can produce. Same permutation table as Perlin2D — they
        // share `_p` so a single Noise(seed) instance gives consistent
        // 2D and 3D fields.
        private static float Grad3(int hash, float x, float y, float z)
        {
            // Standard 12-direction gradient set from Perlin's paper.
            int h = hash & 15;
            float u = h < 8 ? x : y;
            float v = h < 4 ? y : (h == 12 || h == 14 ? x : z);
            return ((h & 1) == 0 ? u : -u) + ((h & 2) == 0 ? v : -v);
        }

        public float Perlin3D(float x, float y, float z)
        {
            int X = (int)Math.Floor(x) & 255;
            int Y = (int)Math.Floor(y) & 255;
            int Z = (int)Math.Floor(z) & 255;
            x -= (float)Math.Floor(x);
            y -= (float)Math.Floor(y);
            z -= (float)Math.Floor(z);
            float u = Fade(x);
            float v = Fade(y);
            float w = Fade(z);
            int A  = _p[X] + Y;
            int AA = _p[A] + Z;
            int AB = _p[A + 1] + Z;
            int B  = _p[X + 1] + Y;
            int BA = _p[B] + Z;
            int BB = _p[B + 1] + Z;
            return Lerp(w,
                Lerp(v,
                    Lerp(u, Grad3(_p[AA],     x,     y,     z),
                            Grad3(_p[BA],     x - 1, y,     z)),
                    Lerp(u, Grad3(_p[AB],     x,     y - 1, z),
                            Grad3(_p[BB],     x - 1, y - 1, z))),
                Lerp(v,
                    Lerp(u, Grad3(_p[AA + 1], x,     y,     z - 1),
                            Grad3(_p[BA + 1], x - 1, y,     z - 1)),
                    Lerp(u, Grad3(_p[AB + 1], x,     y - 1, z - 1),
                            Grad3(_p[BB + 1], x - 1, y - 1, z - 1))));
        }

        public float Octaves3D(float x, float y, float z, int octaves,
            float persistence = 0.5f, float lacunarity = 2f)
        {
            float total = 0f;
            float amplitude = 1f;
            float frequency = 1f;
            float maxAmp = 0f;
            for (int i = 0; i < octaves; i++)
            {
                total += Perlin3D(x * frequency, y * frequency, z * frequency) * amplitude;
                maxAmp += amplitude;
                amplitude *= persistence;
                frequency *= lacunarity;
            }
            return total / maxAmp;
        }
    }
}
