using System.Collections.Generic;
using OpenTK;

namespace VStudioCraft.Game
{
    // Lightweight particle system for in-world cosmetic effects:
    //   * Block-break puffs — 6-8 tiny cubes spawned with the broken block's
    //     side texture, kicked outward with a brief gravity-bound life.
    //   * Splashes — water-tile particles fired up + outward when the player
    //     enters a water surface (or a drop hits one).
    //   * Lava bubbles — orange particles emitted from lava cells near the
    //     camera, rising slowly then popping out of existence.
    //   * Torch smoke — small dim particles drifting up from torch tips.
    //
    // Design notes:
    //   * Single flat list, capped at MaxParticles. New spawns past the cap
    //     overwrite the oldest entry by index — simple ring eviction. Keeps
    //     allocation bounded; never grows beyond the cap.
    //   * Each particle is rendered as a tiny scaled cube using the existing
    //     _multiFaceCubeShader + _breakCubeMesh pipeline. The cube spins on
    //     a fixed random axis so it reads as a tumbling chip rather than a
    //     static voxel — close enough to Alpha's 2D billboard look without
    //     needing a new shader / vertex format.
    //   * Update integrates velocity + gravity, fades alpha out over the
    //     last 30% of life, and kills expired particles. World collisions
    //     are ignored — particles are short-lived (~0.6-1.4s) and the
    //     visual cost of clipping through a block for a frame or two is
    //     less than a per-particle voxel sweep.
    //   * Ambient emitters (lava bubbles, torch smoke) are driven by the
    //     renderer scanning a small radius around the camera and probabilistically
    //     spawning per-cell. The particle system itself is purely passive.
    //
    // Threading: not thread-safe. Caller (GameRenderer) drives Update/Spawn
    // on the render thread; same thread renders. Matches the rest of the
    // game's no-threading convention.
    internal sealed class ParticleSystem
    {
        // 256 is generous — a single block break is ~8 particles, and
        // ambient emitters spawn 1 every few frames. Even in pathological
        // scenes (lava lake, ten-block break sweep) we cap out fine.
        public const int MaxParticles = 256;

        public struct Particle
        {
            public Vector3 Position;
            public Vector3 Velocity;
            public float   Age;
            public float   Lifetime;
            // Atlas tile layer to render on every face of the tiny cube.
            // For block-break puffs this is the broken block's side tile;
            // for water splashes it's the water tile; etc.
            public int     TileLayer;
            // Half-size of the cube in world units. 0.06-0.10 reads well
            // for break puffs without occluding the source block; bigger
            // for splashes (more visible against water).
            public float   Size;
            // Whether gravity (-9.8 m/s²) applies. Lava bubbles and torch
            // smoke float upward and ignore gravity.
            public bool    Gravity;
            // RGB tint (1,1,1 = atlas colour as-is). Used to warm up lava
            // bubbles to orange glow and dim torch smoke to grey.
            public float   TintR, TintG, TintB;
            // Random rotation axis + speed. Cube rotates around this axis
            // proportional to age, giving each particle its own tumble.
            public Vector3 SpinAxis;
            public float   SpinRate; // radians/sec
        }

        private readonly Particle[] _pool = new Particle[MaxParticles];
        private int _count;
        // Insert cursor for ring-eviction: when the pool is full, the next
        // spawn overwrites _writeCursor instead of allocating. Wraps mod
        // MaxParticles so eviction touches the oldest particles first.
        private int _writeCursor;

        private readonly System.Random _rng = new System.Random(0xC0FFEE);

        public int Count => _count;
        public Particle[] Pool => _pool;

        public void Clear()
        {
            _count = 0;
            _writeCursor = 0;
        }

        // Push a single fully-configured particle. Used by the higher-level
        // SpawnX helpers; callers building bespoke effects can call it
        // directly. Returns the index of the slot used (mostly useful for
        // debug overlays / tests).
        public int Spawn(Particle p)
        {
            if (_count < MaxParticles)
            {
                _pool[_count] = p;
                int idx = _count;
                _count++;
                return idx;
            }
            // Pool full — overwrite oldest. _writeCursor advances even on
            // eviction so consecutive evictions hit different slots, not
            // the same one repeatedly.
            int slot = _writeCursor;
            _pool[slot] = p;
            _writeCursor = (_writeCursor + 1) % MaxParticles;
            return slot;
        }

        // Spawn a burst of break-puff particles for a block at world coords
        // (cell origin, NOT centre). Uses the block's side tile as the
        // particle texture so a stone break sprays grey, dirt sprays brown,
        // etc. Velocity is scattered in a hemisphere with extra upward
        // bias — particles burst out of the cell rather than hugging the
        // floor.
        public void SpawnBreakBurst(BlockType type, int wx, int wy, int wz)
        {
            // Skip Air and intangible types — there's no recognisable
            // texture to chip off of those.
            if (type == BlockType.Air) return;
            int layer = BlockData.GetTileIndex(type, 2); // side face tile
            // 8 particles is enough to look like a satisfying burst without
            // overwhelming the pool when the player breaks fast.
            const int Count = 8;
            for (int i = 0; i < Count; i++)
            {
                Spawn(new Particle
                {
                    Position = new Vector3(
                        wx + 0.2f + (float)_rng.NextDouble() * 0.6f,
                        wy + 0.2f + (float)_rng.NextDouble() * 0.6f,
                        wz + 0.2f + (float)_rng.NextDouble() * 0.6f),
                    Velocity = new Vector3(
                        ((float)_rng.NextDouble() - 0.5f) * 3.0f,
                        1.5f + (float)_rng.NextDouble() * 2.0f,
                        ((float)_rng.NextDouble() - 0.5f) * 3.0f),
                    Age = 0f,
                    Lifetime = 0.6f + (float)_rng.NextDouble() * 0.4f,
                    TileLayer = layer,
                    Size = 0.06f + (float)_rng.NextDouble() * 0.04f,
                    Gravity = true,
                    TintR = 1f, TintG = 1f, TintB = 1f,
                    SpinAxis = NormalisedRandomAxis(),
                    SpinRate = ((float)_rng.NextDouble() - 0.5f) * 12f,
                });
            }
        }

        // Spawn a splash burst — small cluster of water-tile particles
        // shot upward + outward from a point on a water surface.
        public void SpawnSplash(float wx, float wy, float wz)
        {
            int layer = BlockData.GetTileIndex(BlockType.Water, 2);
            const int Count = 10;
            for (int i = 0; i < Count; i++)
            {
                Spawn(new Particle
                {
                    Position = new Vector3(
                        wx + ((float)_rng.NextDouble() - 0.5f) * 0.6f,
                        wy + 0.05f,
                        wz + ((float)_rng.NextDouble() - 0.5f) * 0.6f),
                    Velocity = new Vector3(
                        ((float)_rng.NextDouble() - 0.5f) * 4.0f,
                        2.5f + (float)_rng.NextDouble() * 2.0f,
                        ((float)_rng.NextDouble() - 0.5f) * 4.0f),
                    Age = 0f,
                    Lifetime = 0.5f + (float)_rng.NextDouble() * 0.4f,
                    TileLayer = layer,
                    Size = 0.05f + (float)_rng.NextDouble() * 0.03f,
                    Gravity = true,
                    TintR = 1f, TintG = 1f, TintB = 1f,
                    SpinAxis = NormalisedRandomAxis(),
                    SpinRate = ((float)_rng.NextDouble() - 0.5f) * 8f,
                });
            }
        }

        // Spawn a single rising lava-bubble particle — orange-tinted, no
        // gravity, drifting up from a lava cell. Caller controls the
        // emission rate by gating on a per-cell probability.
        public void SpawnLavaBubble(float wx, float wy, float wz)
        {
            int layer = BlockData.GetTileIndex(BlockType.Lava, 2);
            Spawn(new Particle
            {
                Position = new Vector3(
                    wx + 0.2f + (float)_rng.NextDouble() * 0.6f,
                    wy + 0.85f,
                    wz + 0.2f + (float)_rng.NextDouble() * 0.6f),
                Velocity = new Vector3(
                    ((float)_rng.NextDouble() - 0.5f) * 0.3f,
                    0.4f + (float)_rng.NextDouble() * 0.3f,
                    ((float)_rng.NextDouble() - 0.5f) * 0.3f),
                Age = 0f,
                Lifetime = 1.2f + (float)_rng.NextDouble() * 0.6f,
                TileLayer = layer,
                Size = 0.07f,
                Gravity = false,
                TintR = 1.5f, TintG = 0.8f, TintB = 0.3f,
                SpinAxis = NormalisedRandomAxis(),
                SpinRate = ((float)_rng.NextDouble() - 0.5f) * 4f,
            });
        }

        // Spawn a torch-smoke particle — tiny dim-grey rising puff.
        // Caller passes the torch's flame-tip world position; the particle
        // spawns slightly above it and drifts up while fading. The exact
        // tile is the torch tile darkened via a low tint — the atlas
        // doesn't have a dedicated smoke tile yet and this is cheap.
        public void SpawnTorchSmoke(float wx, float wy, float wz)
        {
            int layer = BlockData.GetTileIndex(BlockType.Torch, 2);
            Spawn(new Particle
            {
                Position = new Vector3(
                    wx + ((float)_rng.NextDouble() - 0.5f) * 0.1f,
                    wy,
                    wz + ((float)_rng.NextDouble() - 0.5f) * 0.1f),
                Velocity = new Vector3(
                    ((float)_rng.NextDouble() - 0.5f) * 0.2f,
                    0.5f + (float)_rng.NextDouble() * 0.2f,
                    ((float)_rng.NextDouble() - 0.5f) * 0.2f),
                Age = 0f,
                Lifetime = 0.8f + (float)_rng.NextDouble() * 0.4f,
                TileLayer = layer,
                Size = 0.04f,
                Gravity = false,
                // Dark grey — drains the torch tile's bright orange to a
                // cooler "smoke" colour without needing a new texture.
                TintR = 0.3f, TintG = 0.3f, TintB = 0.3f,
                SpinAxis = NormalisedRandomAxis(),
                SpinRate = ((float)_rng.NextDouble() - 0.5f) * 3f,
            });
        }

        // Per-frame physics + lifetime sweep. Compacts the live-particle
        // prefix in place so render iteration touches only the live
        // entries — RemoveAt-style would be O(n²) for a worst-case
        // full pool flushing on the same frame.
        public void Update(float dt)
        {
            // Phase B.3 of the parallelisation analysis — split into a
            // parallel physics integration phase and a serial compaction
            // phase. The integration is per-particle pure (each particle
            // touches only its own slot), but the compaction is a
            // moving-write-cursor "swap-and-pop" that's inherently
            // serial. Parallelising only when the count is high enough
            // to amortise the Parallel.For startup (~10 µs); below the
            // threshold the loop runs in one pass to avoid a double-
            // traversal cost.
            const int ParallelThreshold = 64;

            if (_count >= ParallelThreshold)
            {
                int countLocal = _count;
                System.Threading.Tasks.Parallel.For(0, countLocal, i =>
                {
                    // Index-based access; can't use `ref var p` inside a
                    // lambda. Each thread writes only to _pool[i], so
                    // the parallel writes don't race.
                    _pool[i].Age += dt;
                    if (_pool[i].Age >= _pool[i].Lifetime) return;
                    if (_pool[i].Gravity)
                    {
                        // Gravity matches dropped-item physics (-20 m/s²
                        // capped); air-drag matches dropped-item drag rate.
                        _pool[i].Velocity.Y -= 20f * dt;
                        if (_pool[i].Velocity.Y < -20f) _pool[i].Velocity.Y = -20f;
                        float drag = (float)System.Math.Pow(0.95, dt * 60.0);
                        _pool[i].Velocity.X *= drag;
                        _pool[i].Velocity.Z *= drag;
                    }
                    _pool[i].Position += _pool[i].Velocity * dt;
                });
                // Serial compaction — same swap-and-pop as the legacy
                // path, but we only inspect Age vs Lifetime here since
                // the integration above already happened.
                int write = 0;
                for (int read = 0; read < _count; read++)
                {
                    if (_pool[read].Age >= _pool[read].Lifetime) continue;
                    if (write != read) _pool[write] = _pool[read];
                    write++;
                }
                _count = write;
            }
            else
            {
                // Below threshold: keep the legacy single-pass integrate-
                // and-compact loop. Parallel.For startup overhead would
                // exceed the work for tiny pools.
                int write = 0;
                for (int read = 0; read < _count; read++)
                {
                    ref var p = ref _pool[read];
                    p.Age += dt;
                    if (p.Age >= p.Lifetime) continue;
                    if (p.Gravity)
                    {
                        p.Velocity.Y -= 20f * dt;
                        if (p.Velocity.Y < -20f) p.Velocity.Y = -20f;
                        float drag = (float)System.Math.Pow(0.95, dt * 60.0);
                        p.Velocity.X *= drag;
                        p.Velocity.Z *= drag;
                    }
                    p.Position += p.Velocity * dt;
                    if (write != read) _pool[write] = p;
                    write++;
                }
                _count = write;
            }
            // Reset the eviction cursor when the pool is no longer full —
            // prevents stale wrap state from biasing eviction order if the
            // count climbs back to MaxParticles later.
            if (_count < MaxParticles) _writeCursor = 0;
        }

        // Random unit vector for per-particle spin axes. Rejection-sampled
        // from a cube — uniform-on-sphere is overkill for tumbling cubes,
        // and the bias from cube → sphere is invisible at this scale.
        // Retries on the (rare) zero vector to avoid NaN normalisation.
        private Vector3 NormalisedRandomAxis()
        {
            for (int i = 0; i < 4; i++)
            {
                float x = (float)_rng.NextDouble() * 2f - 1f;
                float y = (float)_rng.NextDouble() * 2f - 1f;
                float z = (float)_rng.NextDouble() * 2f - 1f;
                float lenSq = x * x + y * y + z * z;
                if (lenSq > 1e-4f)
                {
                    float invLen = 1f / (float)System.Math.Sqrt(lenSq);
                    return new Vector3(x * invLen, y * invLen, z * invLen);
                }
            }
            return Vector3.UnitY;
        }
    }
}
