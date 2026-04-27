using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace VStudioCraft.Game
{
    internal sealed class World
    {
        // Small starter patch so the first frame isn't empty. Streaming fills in the rest.
        public const int InitialRadiusChunks = 2;  // 5x5 = 25 chunks

        // ConcurrentDictionary so chunk-mesh/gen workers can read the dict while
        // the render thread inserts completed chunks. Block-byte races during
        // meshing are benign (byte reads are atomic; the chunk stays dirty if an
        // edit happens mid-mesh and will be remeshed).
        private readonly ConcurrentDictionary<(int x, int z), Chunk> _chunks = new ConcurrentDictionary<(int x, int z), Chunk>();
        // Modified chunks that have been unloaded are held here so re-entering their area
        // restores the player's edits instead of regenerating them from noise.
        private readonly ConcurrentDictionary<(int x, int z), Chunk> _modified = new ConcurrentDictionary<(int x, int z), Chunk>();
        // Dirty set is only mutated on the render thread — no concurrency primitive needed.
        private readonly HashSet<(int x, int z)> _dirty = new HashSet<(int x, int z)>();
        // Tile entities keyed on absolute world coordinate. A furnace cell
        // and its FurnaceTileEntity entry have a 1:1 lifetime: the entry
        // is created when the player places a Furnace and removed when the
        // block is broken (or replaced with anything else). Keeping the
        // dict at world-level (rather than per-chunk) means entries
        // survive chunk unloading naturally — chunks don't need to carry
        // a serialised tile-entity sidecar; only the dict is persisted.
        private readonly Dictionary<(int x, int y, int z), FurnaceTileEntity> _furnaceEntities
            = new Dictionary<(int x, int y, int z), FurnaceTileEntity>();
        // Chest tile entities — same lifetime model as furnaces. The dict
        // is created on placement, mutated by the open-chest UI, drained
        // and removed when the block breaks. Persisted alongside the
        // furnace dict in WorldSaveFormat.
        private readonly Dictionary<(int x, int y, int z), ChestTileEntity> _chestEntities
            = new Dictionary<(int x, int y, int z), ChestTileEntity>();

        // Live passive-mob list. Pig was the first entity added in Tier 3
        // #9; Tier 3 #12 generalised the list to PassiveMob so Cow, Sheep
        // and Chicken share the same flat container. The list stays flat
        // (not chunk-bucketed) because the per-tick mob count is small
        // (caps in the tens for passives) and a flat scan is fine. The
        // renderer reads `Passives` each frame to draw the body cuboids
        // (dispatching on concrete type for shape); combat reads it to
        // scan for a click target before block-raycasting; spawning
        // appends new passives at chunk-gen time and via the live spawn
        // loop. We don't currently persist passives across save/load
        // (matches Alpha 1.1.2_01 behaviour for unloaded chunks —
        // entities outside the active radius despawn).
        private readonly List<PassiveMob> _passives = new List<PassiveMob>();
        public List<PassiveMob> Passives => _passives;

        // Hostile mob list (Tier 3 #10). Same flat-list shape as Passives —
        // small per-tick count, the four hostile types share a common
        // base (HostileMob) so a single list captures them all and the
        // renderer can dispatch on concrete type for the cuboid shape.
        // Spawn density gates on light < 7 (Alpha hostile rule) instead
        // of the passive rule's ≥ 9 — see SpawnHostilesInChunk. Like
        // pigs, we don't currently persist these across save/load.
        private readonly List<HostileMob> _hostiles = new List<HostileMob>();
        public List<HostileMob> Hostiles => _hostiles;

        private readonly Noise _noise;

        public int Seed { get; }
        public Noise Noise => _noise;

        private World(int seed)
        {
            Seed = seed;
            _noise = new Noise(seed);
        }

        public static World Generate(int seed)
        {
            var w = new World(seed);
            for (int cz = -InitialRadiusChunks; cz <= InitialRadiusChunks; cz++)
            for (int cx = -InitialRadiusChunks; cx <= InitialRadiusChunks; cx++)
            {
                var c = new Chunk(cx, cz);
                TerrainGenerator.Generate(c, w._noise);
                LightCalculator.RecomputeChunk(c);
                w._chunks[(cx, cz)] = c;
                // Initial spawn pass uses the same per-chunk hashed RNG
                // as the streaming path, so passives scattered in the
                // initial 5×5 patch stay deterministic for a given seed.
                w.SpawnPassivesInChunk(c);
                w.SpawnHostilesInChunk(c);
            }
            return w;
        }

        // Passive-spawn pass for a freshly generated chunk. Walks every
        // (lx, lz) column at a low density (per-column hashed RNG,
        // ~1-in-720 grass cells, matching Alpha's sparse passive-mob
        // spread), checks that the surface block is grass and the cell
        // directly above has block-light + sky-light ≥ 9 (Alpha rule for
        // passive spawns), then picks one of Pig / Cow / Sheep / Chicken
        // via a weighted draw and inserts it at the foot of the column.
        // The hashed RNG keys on (Seed, chunkX, chunkZ, lx, lz) so the
        // same chunk in the same world always gets the same passive set
        // even if it unloads + reloads.
        //
        // Species mix mirrors Alpha 1.1.2_01's overworld passive spawn
        // weights: 30% Pig, 30% Cow, 25% Sheep, 15% Chicken — pig and
        // cow are the most common, chickens the rarest.
        public void SpawnPassivesInChunk(Chunk c)
        {
            const int RareDenominator = 720; // ~1 chance per 720 grass cells (¼ of the original 180)
            int chunkBaseX = c.ChunkX * Chunk.SizeX;
            int chunkBaseZ = c.ChunkZ * Chunk.SizeZ;
            for (int lx = 0; lx < Chunk.SizeX; lx++)
            for (int lz = 0; lz < Chunk.SizeZ; lz++)
            {
                // Find the topmost solid surface column (skip air at top).
                int surfaceY = -1;
                for (int y = Chunk.SizeY - 1; y >= 0; y--)
                {
                    var b = c.Get(lx, y, lz);
                    if (b == BlockType.Air || BlockData.IsLightTransparent(b)) continue;
                    surfaceY = y;
                    break;
                }
                if (surfaceY < 0) continue;
                if (c.Get(lx, surfaceY, lz) != BlockType.Grass) continue;
                // Need 2 blocks of headroom for the pig (it's 0.9 m tall;
                // 1 air block clears the body, but we check 2 for safety
                // in case the wander walks it into a 1-block hop).
                if (surfaceY + 2 >= Chunk.SizeY) continue;
                if (c.Get(lx, surfaceY + 1, lz) != BlockType.Air) continue;
                if (c.Get(lx, surfaceY + 2, lz) != BlockType.Air) continue;

                // Hashed RNG: stable per (seed, world-x, world-z). Mixing
                // matches the per-column scheme TerrainGenerator uses for
                // flora so spawn positions are deterministic across loads.
                int wx = chunkBaseX + lx;
                int wz = chunkBaseZ + lz;
                int hash = unchecked((int)(
                    (uint)Seed * 0x9E3779B1u
                    ^ (uint)wx * 0x85EBCA77u
                    ^ (uint)wz * 0xC2B2AE3Du));
                hash = (hash ^ (hash >> 13)) * 0x5BD1E995;
                hash ^= hash >> 15;
                int bucket = (int)((uint)hash % (uint)RareDenominator);
                if (bucket != 0) continue;

                // Light gate: passive spawn requires the cell ABOVE the
                // grass (where the pig stands) to be at light ≥ 9. Sky
                // and block light combined.
                int sky = c.GetSkyLight(lx, surfaceY + 1, lz);
                int blk = c.GetBlockLight(lx, surfaceY + 1, lz);
                int eff = sky > blk ? sky : blk;
                if (eff < 9) continue;

                // Spawn at the centre of the cell, feet on the grass top.
                var spawnPos = new OpenTK.Vector3(
                    wx + 0.5f, surfaceY + 1f, wz + 0.5f);
                int mobSeed = hash ^ 0x55AA55AA;

                // Species-pick: derive a second hash so kind selection
                // doesn't share bits with the spawn-rate gate. Same trick
                // we use for hostile kinds.
                int kindHash = unchecked((int)((uint)hash * 0x85EBCA6Bu ^ 0x9E3779B1u));
                int kindRoll = (int)((uint)kindHash % 100u);
                PassiveMob mob;
                if      (kindRoll < 30) mob = new Pig(spawnPos,     mobSeed);
                else if (kindRoll < 60) mob = new Cow(spawnPos,     mobSeed);
                else if (kindRoll < 85) mob = new Sheep(spawnPos,   mobSeed);
                else                    mob = new Chicken(spawnPos, mobSeed);
                _passives.Add(mob);
            }
        }

        // Hostile-mob spawn pass for a freshly generated chunk (Tier 3
        // #10). Walks every column at low density (~1-in-360, twice as
        // dense as pigs because hostiles cluster around darkness rather
        // than scattering on grass). Surface block must be solid + non-
        // fluid + non-cube-flora; the cell above must have 2 blocks of
        // headroom; and the spawn-cell light level (max of sky + block)
        // must be ≤ 7 to match Alpha's hostile-spawn rule. Mob kind is
        // picked from the per-column hashed RNG with weights matching
        // Alpha's overworld distribution: 35% zombie, 25% skeleton,
        // 25% spider, 15% creeper.
        //
        // Continuous live spawning at runtime is roadmap-pending (the
        // separate "spawn loop" Tier 3 #11). For Tier 3 #10 we seed
        // the hostiles at chunk-gen and they persist until killed; this
        // is enough for the player to encounter them on first walk-out
        // and for the combat / drop / pathing systems to be exercised.
        public void SpawnHostilesInChunk(Chunk c)
        {
            const int RareDenominator = 360;
            int chunkBaseX = c.ChunkX * Chunk.SizeX;
            int chunkBaseZ = c.ChunkZ * Chunk.SizeZ;
            for (int lx = 0; lx < Chunk.SizeX; lx++)
            for (int lz = 0; lz < Chunk.SizeZ; lz++)
            {
                int surfaceY = -1;
                for (int y = Chunk.SizeY - 1; y >= 0; y--)
                {
                    var b = c.Get(lx, y, lz);
                    if (b == BlockType.Air || BlockData.IsLightTransparent(b)) continue;
                    surfaceY = y;
                    break;
                }
                if (surfaceY < 0) continue;
                var surface = c.Get(lx, surfaceY, lz);
                // Stand on solid land — no fluids, no flora cap.
                if (!BlockData.IsSolid(surface)) continue;
                if (surfaceY + 2 >= Chunk.SizeY) continue;
                if (c.Get(lx, surfaceY + 1, lz) != BlockType.Air) continue;
                if (c.Get(lx, surfaceY + 2, lz) != BlockType.Air) continue;

                int wx = chunkBaseX + lx;
                int wz = chunkBaseZ + lz;
                // Mix is offset from the pig hash so a column that gates
                // a pig spawn doesn't also gate a hostile (and vice
                // versa). The 0xC2B2AE3D third multiplier becomes
                // 0x27D4EB2D so the two streams are independent.
                int hash = unchecked((int)(
                    (uint)Seed * 0x9E3779B1u
                    ^ (uint)wx * 0x85EBCA77u
                    ^ (uint)wz * 0x27D4EB2Du));
                hash = (hash ^ (hash >> 13)) * 0x5BD1E995;
                hash ^= hash >> 15;
                int bucket = (int)((uint)hash % (uint)RareDenominator);
                if (bucket != 0) continue;

                // Light gate: hostile spawn requires the SPAWN cell
                // (the cell above the surface where the mob's feet
                // stand) to be at light ≤ 7. Combined sky + block.
                int sky = c.GetSkyLight(lx, surfaceY + 1, lz);
                int blk = c.GetBlockLight(lx, surfaceY + 1, lz);
                int eff = sky > blk ? sky : blk;
                if (eff > 7) continue;

                var spawnPos = new OpenTK.Vector3(
                    wx + 0.5f, surfaceY + 1f, wz + 0.5f);
                int mobSeed = hash ^ 0x33CC33CC;

                // Mob kind weighted draw — uses the next derivation of
                // the column hash so kind is deterministic per column.
                int kindHash = unchecked((int)((uint)hash * 0x85EBCA6Bu ^ 0xC2B2AE35u));
                int kindRoll = (int)((uint)kindHash % 100u);
                HostileMob mob;
                if      (kindRoll < 35) mob = new Zombie(spawnPos, mobSeed);
                else if (kindRoll < 60) mob = new Skeleton(spawnPos, mobSeed);
                else if (kindRoll < 85) mob = new Spider(spawnPos, mobSeed);
                else                    mob = new Creeper(spawnPos, mobSeed);
                _hostiles.Add(mob);
            }
        }

        // Live mob-spawn attempt loop (Tier 3 #11). Layered ON TOP of the
        // chunk-gen seed pass — that pass remains the deterministic, hashed
        // RNG-driven "what mobs are in a fresh chunk", and this method adds
        // the every-second top-up that keeps the world populated as the
        // player travels. Cadence: one batch every SpawnTickInterval seconds
        // (1 s — Alpha actually runs the spawn attempt every game tick at
        // 20 Hz, but our world is small and 1 s is more than fast enough to
        // refill what the player burns down).
        //
        // Each batch:
        //   1. Despawn — instant despawn at >128 blocks XZ from player
        //      (Alpha "hard cap"), stochastic 5%/sec despawn at 32..128
        //      blocks. Keeps the live-mob list from accumulating across
        //      a long traversal.
        //   2. Cap check — global passive cap (10) and hostile cap (70)
        //      mirror Alpha's defaults. If both are full, no spawn attempts
        //      this tick.
        //   3. Attempts — AttemptsPerTick random columns picked from
        //      chunks within SpawnRadiusChunks (6 chunks ≈ 96 blocks, the
        //      same band the renderer's been told to keep loaded). Each
        //      attempt:
        //        - Skip the column if the player is within MinSpawnDistance
        //          (24 blocks — Alpha doesn't spawn next to you)
        //        - Skip if the chunk's local mob count is already at
        //          PerChunkCap (4). Prevents stacking inside one chunk.
        //        - Pick the topmost solid surface, require 2 blocks of
        //          headroom, decide:
        //            light ≤ 7  → attempt hostile spawn (gates on global
        //                          hostile cap; weighted draw 35/25/25/15
        //                          across zombie/skeleton/spider/creeper)
        //            light ≥ 9 + grass surface → attempt passive (Pig)
        //                          spawn (gates on global passive cap)
        //          Light range 8 (the dead band between hostile-eligible
        //          and passive-eligible) is deliberately a no-spawn zone,
        //          matching Alpha.
        //
        // RNG: a single Random instance owned by the world. Spawn outcomes
        // are non-deterministic (don't replay across loads — fine, mobs
        // aren't persisted anyway). Callers pass the player position each
        // tick so World doesn't need a back-ref to Player.
        public const float SpawnTickInterval     = 1.0f;
        public const int   PassiveCap            = 10;
        public const int   HostileCap            = 70;
        public const int   PerChunkCap           = 4;
        public const int   SpawnRadiusChunks     = 6;
        public const int   AttemptsPerTick       = 12;
        public const int   MinSpawnDistance      = 24;
        public const int   StochasticDespawnDist = 32;
        public const int   InstantDespawnDist    = 128;

        private float _spawnTimer;
        private readonly Random _spawnRng = new Random();

        public void TickMobSpawns(float dt, OpenTK.Vector3 playerPos)
        {
            _spawnTimer -= dt;
            if (_spawnTimer > 0f) return;
            _spawnTimer = SpawnTickInterval;

            DespawnFarMobs(playerPos);

            int passiveCount = _passives.Count;
            int hostileCount = _hostiles.Count;
            bool passiveFull = passiveCount >= PassiveCap;
            bool hostileFull = hostileCount >= HostileCap;
            if (passiveFull && hostileFull) return;

            int playerCx = (int)Math.Floor(playerPos.X / (float)Chunk.SizeX);
            int playerCz = (int)Math.Floor(playerPos.Z / (float)Chunk.SizeZ);
            int minSpawnDistSq = MinSpawnDistance * MinSpawnDistance;

            for (int attempt = 0; attempt < AttemptsPerTick; attempt++)
            {
                // Pick a random chunk in the radius band around the player.
                int dx = _spawnRng.Next(-SpawnRadiusChunks, SpawnRadiusChunks + 1);
                int dz = _spawnRng.Next(-SpawnRadiusChunks, SpawnRadiusChunks + 1);
                int cx = playerCx + dx;
                int cz = playerCz + dz;
                var chunk = GetChunk(cx, cz);
                if (chunk == null) continue;

                // Per-chunk cap — count current mobs sitting inside this
                // chunk's XZ extent. Cheap on the small live list (well
                // under the global cap) and avoids the maintenance burden
                // of an incremental per-chunk counter.
                int chunkBaseX = cx * Chunk.SizeX;
                int chunkBaseZ = cz * Chunk.SizeZ;
                if (CountMobsInChunkXZ(chunkBaseX, chunkBaseZ) >= PerChunkCap) continue;

                int lx = _spawnRng.Next(Chunk.SizeX);
                int lz = _spawnRng.Next(Chunk.SizeZ);
                int wx = chunkBaseX + lx;
                int wz = chunkBaseZ + lz;

                // Min-distance gate — never spawn right under the player's
                // feet or in the cluster directly around the camera.
                float ddx = (wx + 0.5f) - playerPos.X;
                float ddz = (wz + 0.5f) - playerPos.Z;
                if (ddx * ddx + ddz * ddz < minSpawnDistSq) continue;

                // Topmost solid surface. Same rule as the gen-time spawn
                // passes: skip air + light-transparent caps (flora /
                // glass / leaves) so we land on real terrain.
                int surfaceY = -1;
                for (int y = Chunk.SizeY - 1; y >= 0; y--)
                {
                    var b = chunk.Get(lx, y, lz);
                    if (b == BlockType.Air || BlockData.IsLightTransparent(b)) continue;
                    surfaceY = y;
                    break;
                }
                if (surfaceY < 0) continue;
                var surface = chunk.Get(lx, surfaceY, lz);
                if (!BlockData.IsSolid(surface)) continue;
                if (surfaceY + 2 >= Chunk.SizeY) continue;
                if (chunk.Get(lx, surfaceY + 1, lz) != BlockType.Air) continue;
                if (chunk.Get(lx, surfaceY + 2, lz) != BlockType.Air) continue;

                int sky = chunk.GetSkyLight(lx, surfaceY + 1, lz);
                int blk = chunk.GetBlockLight(lx, surfaceY + 1, lz);
                int eff = sky > blk ? sky : blk;

                var spawnPos = new OpenTK.Vector3(wx + 0.5f, surfaceY + 1f, wz + 0.5f);
                int seedForMob = _spawnRng.Next();

                if (eff <= 7 && !hostileFull)
                {
                    // Hostile attempt — same weighted draw as the gen-time
                    // pass so live spawns and chunk-gen spawns share a
                    // species mix.
                    int kindRoll = _spawnRng.Next(100);
                    HostileMob mob;
                    if      (kindRoll < 35) mob = new Zombie(spawnPos,   seedForMob);
                    else if (kindRoll < 60) mob = new Skeleton(spawnPos, seedForMob);
                    else if (kindRoll < 85) mob = new Spider(spawnPos,   seedForMob);
                    else                    mob = new Creeper(spawnPos,  seedForMob);
                    _hostiles.Add(mob);
                    hostileCount++;
                    if (hostileCount >= HostileCap) hostileFull = true;
                }
                else if (eff >= 9 && surface == BlockType.Grass && !passiveFull)
                {
                    // Same 30/30/25/15 species mix as the chunk-gen pass.
                    int kindRoll = _spawnRng.Next(100);
                    PassiveMob mob;
                    if      (kindRoll < 30) mob = new Pig(spawnPos,     seedForMob);
                    else if (kindRoll < 60) mob = new Cow(spawnPos,     seedForMob);
                    else if (kindRoll < 85) mob = new Sheep(spawnPos,   seedForMob);
                    else                    mob = new Chicken(spawnPos, seedForMob);
                    _passives.Add(mob);
                    passiveCount++;
                    if (passiveCount >= PassiveCap) passiveFull = true;
                }
                // Light 8 (or grass-less light-≥9 surface) — no spawn.

                if (passiveFull && hostileFull) return;
            }
        }

        // Count mobs whose XZ position falls inside this chunk's XZ box.
        // Used by the per-chunk spawn cap. O(mobs) — mob list is small.
        private int CountMobsInChunkXZ(int chunkBaseX, int chunkBaseZ)
        {
            int n = 0;
            int xMin = chunkBaseX, xMax = chunkBaseX + Chunk.SizeX;
            int zMin = chunkBaseZ, zMax = chunkBaseZ + Chunk.SizeZ;
            for (int i = 0; i < _passives.Count; i++)
            {
                var p = _passives[i].Position;
                if (p.X >= xMin && p.X < xMax && p.Z >= zMin && p.Z < zMax) n++;
            }
            for (int i = 0; i < _hostiles.Count; i++)
            {
                var p = _hostiles[i].Position;
                if (p.X >= xMin && p.X < xMax && p.Z >= zMin && p.Z < zMax) n++;
            }
            return n;
        }

        // Despawn far mobs. Alpha 1.1.2 instant-despawns mobs > 128 blocks
        // from the player and stochastically despawns at 32..128. We mirror
        // both rules: the >128 cull runs every spawn tick, and a 5% per-tick
        // cull runs on the 32..128 band so mobs trail off as the player
        // walks rather than persisting indefinitely.
        private void DespawnFarMobs(OpenTK.Vector3 playerPos)
        {
            int instantSq    = InstantDespawnDist    * InstantDespawnDist;
            int stochasticSq = StochasticDespawnDist * StochasticDespawnDist;
            for (int i = _passives.Count - 1; i >= 0; i--)
            {
                var p = _passives[i].Position;
                float fx = p.X - playerPos.X, fz = p.Z - playerPos.Z;
                float dsq = fx * fx + fz * fz;
                if (dsq > instantSq) { _passives.RemoveAt(i); continue; }
                if (dsq > stochasticSq && _spawnRng.NextDouble() < 0.05) _passives.RemoveAt(i);
            }
            for (int i = _hostiles.Count - 1; i >= 0; i--)
            {
                var p = _hostiles[i].Position;
                float fx = p.X - playerPos.X, fz = p.Z - playerPos.Z;
                float dsq = fx * fx + fz * fz;
                if (dsq > instantSq) { _hostiles.RemoveAt(i); continue; }
                if (dsq > stochasticSq && _spawnRng.NextDouble() < 0.05) _hostiles.RemoveAt(i);
            }
        }

        public static World Empty(int seed) => new World(seed);

        // Streaming entry point. Generates the chunk if missing and marks it + its
        // 4 neighbours dirty so their edge faces can be re-culled against the new chunk.
        // Synchronous — kept for the rare case we need a chunk immediately (e.g. load).
        public bool EnsureChunk(int cx, int cz)
        {
            if (_chunks.ContainsKey((cx, cz))) return false;
            Chunk c;
            if (_modified.TryRemove((cx, cz), out c))
            {
                // Use the cached modified chunk verbatim.
            }
            else
            {
                c = new Chunk(cx, cz);
                TerrainGenerator.Generate(c, _noise);
                LightCalculator.RecomputeChunk(c);
            }
            _chunks[(cx, cz)] = c;
            MarkChunkAndNeighborsDirty(cx, cz);
            return true;
        }

        // Async path: a worker produced this chunk; install it and mark it dirty.
        // If a cached-modified version exists (player edits survived an unload),
        // prefer that and discard the freshly-generated copy.
        public bool InstallGeneratedChunk(Chunk chunk)
        {
            var key = (chunk.ChunkX, chunk.ChunkZ);
            if (_chunks.ContainsKey(key)) return false;
            if (_modified.TryRemove(key, out var cached)) chunk = cached;
            _chunks[key] = chunk;
            MarkChunkAndNeighborsDirty(chunk.ChunkX, chunk.ChunkZ);
            return true;
        }

        public bool HasChunk(int cx, int cz) => _chunks.ContainsKey((cx, cz));

        private void MarkChunkAndNeighborsDirty(int cx, int cz)
        {
            _dirty.Add((cx, cz));
            _dirty.Add((cx - 1, cz));
            _dirty.Add((cx + 1, cz));
            _dirty.Add((cx, cz - 1));
            _dirty.Add((cx, cz + 1));
        }

        // Remove a chunk from the active set. Modified chunks are kept in the side
        // dictionary; unmodified ones are discarded (regenerate identically later).
        public void UnloadChunk(int cx, int cz)
        {
            if (!_chunks.TryRemove((cx, cz), out var c)) return;
            if (c.IsModified) _modified[(cx, cz)] = c;
            _dirty.Add((cx - 1, cz));
            _dirty.Add((cx + 1, cz));
            _dirty.Add((cx, cz - 1));
            _dirty.Add((cx, cz + 1));
            _dirty.Remove((cx, cz));
        }

        // Save path — emit active + cached-modified chunks so edits survive a round trip.
        public IEnumerable<Chunk> AllChunksForPersistence()
        {
            foreach (var c in _chunks.Values) yield return c;
            foreach (var c in _modified.Values) yield return c;
        }

        public int PersistentChunkCount => _chunks.Count + _modified.Count;

        public IEnumerable<Chunk> Chunks => _chunks.Values;
        public int ChunkCount => _chunks.Count;
        public HashSet<(int x, int z)> DirtyChunks => _dirty;

        public void AddChunk(Chunk chunk)
        {
            _chunks[(chunk.ChunkX, chunk.ChunkZ)] = chunk;
        }

        public Chunk GetChunk(int cx, int cz)
        {
            _chunks.TryGetValue((cx, cz), out var c);
            return c;
        }

        public BlockType GetBlock(int wx, int wy, int wz)
        {
            if (wy < 0 || wy >= Chunk.SizeY) return BlockType.Air;
            int cx = (int)Math.Floor(wx / (float)Chunk.SizeX);
            int cz = (int)Math.Floor(wz / (float)Chunk.SizeZ);
            int lx = wx - cx * Chunk.SizeX;
            int lz = wz - cz * Chunk.SizeZ;
            var c = GetChunk(cx, cz);
            if (c == null) return BlockType.Air;
            return c.Get(lx, wy, lz);
        }

        public bool SetBlock(int wx, int wy, int wz, BlockType t)
        {
            if (wy < 0 || wy >= Chunk.SizeY) return false;
            int cx = (int)Math.Floor(wx / (float)Chunk.SizeX);
            int cz = (int)Math.Floor(wz / (float)Chunk.SizeZ);
            int lx = wx - cx * Chunk.SizeX;
            int lz = wz - cz * Chunk.SizeZ;
            var c = GetChunk(cx, cz);
            if (c == null) return false;
            var oldT = c.Get(lx, wy, lz);
            if (oldT == t) return false; // no-op edit; don't dirty anything
            c.Set(lx, wy, lz, t);
            c.IsModified = true;

            // Incremental light update — touches only the cells whose sky
            // or block light value actually changes (standard remove-then-add
            // BFS), and only marks chunks dirty whose light field was
            // modified. Replaces the previous 3×3 RecomputeRegion which
            // synchronously cleared and reflooded 9 chunks of light on every
            // click — that was the source of the place-block stutter.
            var touched = LightCalculator.UpdateAfterEdit(this, wx, wy, wz, oldT, t);
            foreach (var k in touched) _dirty.Add(k);
            // The edit chunk's mesh always needs rebuilding because its block
            // changed, even if no light value did (e.g. dirt → stone).
            _dirty.Add((cx, cz));
            // If the edit cell sits on a chunk boundary, the neighbour's
            // border faces may need re-culling against the new block. The
            // incremental light update only touches the neighbour if the
            // light field changed, so dirty it explicitly here.
            if (lx == 0) _dirty.Add((cx - 1, cz));
            else if (lx == Chunk.SizeX - 1) _dirty.Add((cx + 1, cz));
            if (lz == 0) _dirty.Add((cx, cz - 1));
            else if (lz == Chunk.SizeZ - 1) _dirty.Add((cx, cz + 1));

            // Re-engage fluid ticks on this chunk + its neighbours. The fluid
            // tick auto-deactivates chunks that have reached steady state, so
            // an edit (dig out a wall next to the ocean, place a new source,
            // etc.) needs to flip the flag back on or the next tick will
            // skip the chunk entirely.
            FluidTick.MarkActiveAroundEdit(this, cx, cz);
            return true;
        }

        public void MarkAllDirty()
        {
            foreach (var k in _chunks.Keys) _dirty.Add(k);
        }

        // ---- Furnace tile entities ----

        // Get-or-create a FurnaceTileEntity at (wx, wy, wz). Caller must
        // have already verified the block at the position is a Furnace
        // or LitFurnace; this method doesn't sanity-check, it just hands
        // back the persistent slot for that coordinate.
        public FurnaceTileEntity GetOrCreateFurnaceEntity(int wx, int wy, int wz)
        {
            var key = (wx, wy, wz);
            if (!_furnaceEntities.TryGetValue(key, out var fe))
            {
                fe = new FurnaceTileEntity();
                _furnaceEntities[key] = fe;
            }
            return fe;
        }

        // Look up a FurnaceTileEntity without creating one. Returns null
        // if no entity exists for the coordinate.
        public FurnaceTileEntity TryGetFurnaceEntity(int wx, int wy, int wz)
        {
            _furnaceEntities.TryGetValue((wx, wy, wz), out var fe);
            return fe;
        }

        // Remove the entity at the coordinate and return it (or null).
        // Used when a furnace block is broken so the caller can spill
        // its contents as drops.
        public FurnaceTileEntity RemoveFurnaceEntity(int wx, int wy, int wz)
        {
            var key = (wx, wy, wz);
            if (_furnaceEntities.TryGetValue(key, out var fe))
            {
                _furnaceEntities.Remove(key);
                return fe;
            }
            return null;
        }

        // Iterate all (coord, entity) pairs — used by the per-tick
        // furnace driver in GameRenderer and by save/load.
        public IEnumerable<KeyValuePair<(int x, int y, int z), FurnaceTileEntity>> FurnaceEntities
            => _furnaceEntities;

        // ---- Chest tile entities ----

        // Get-or-create a ChestTileEntity at (wx, wy, wz). Caller has
        // already verified the block at the position is a Chest; the
        // method just hands back (or installs) the persistent slot.
        public ChestTileEntity GetOrCreateChestEntity(int wx, int wy, int wz)
        {
            var key = (wx, wy, wz);
            if (!_chestEntities.TryGetValue(key, out var ce))
            {
                ce = new ChestTileEntity();
                _chestEntities[key] = ce;
            }
            return ce;
        }

        // Look up a ChestTileEntity without creating one. Returns null
        // if no entity exists for the coordinate (e.g. a freshly-placed
        // chest that the player hasn't opened yet doesn't allocate one
        // until interaction).
        public ChestTileEntity TryGetChestEntity(int wx, int wy, int wz)
        {
            _chestEntities.TryGetValue((wx, wy, wz), out var ce);
            return ce;
        }

        // Remove the entity at the coordinate and return it (or null).
        // Used when the chest block is broken so the caller can spill
        // contents as drops.
        public ChestTileEntity RemoveChestEntity(int wx, int wy, int wz)
        {
            var key = (wx, wy, wz);
            if (_chestEntities.TryGetValue(key, out var ce))
            {
                _chestEntities.Remove(key);
                return ce;
            }
            return null;
        }

        // Iterate all (coord, entity) pairs — used by save/load.
        public IEnumerable<KeyValuePair<(int x, int y, int z), ChestTileEntity>> ChestEntities
            => _chestEntities;
    }
}
