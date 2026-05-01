using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace VStudioCraft.Game
{
    // Phase 3 — record of one server-authored cell change, queued in
    // World._pendingBlockChanges and drained by ServerHub each tick to
    // emit BlockChange packets. Compact value type so a tick that
    // reshapes a cliff face doesn't churn the GC.
    internal struct BlockChangeRecord
    {
        public int X, Y, Z;
        public BlockType NewType;
        // Phase 8+ — KI-3 closure. Doors flip the open bit on the per-
        // cell metadata byte without changing the BlockType, so the
        // change journal carries Meta alongside Type. Non-door changes
        // store the cell's current Meta (0 for most blocks) so the
        // wire packet always has a consistent shape.
        public byte Meta;

        public BlockChangeRecord(int x, int y, int z, BlockType t, byte meta)
        {
            X = x; Y = y; Z = z; NewType = t; Meta = meta;
        }
    }

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
        //
        // ConcurrentDictionary (not Dictionary) because dungeon generation
        // now runs on chunk-worker threads (P1 of the chunk-streaming
        // smoothness work). GenerateDungeonsInChunk registers chest
        // entities at the chunk's chest corner, and multiple workers can
        // be generating different chunks at the same time. The render-
        // thread reads / mutates from chest-open UI happen at a different
        // moment and CD's TryGetValue/indexer-set/TryRemove handle the
        // worker-vs-render race cleanly with no perceptible cost (these
        // dicts are tiny — order of dozens of entries in a typical world).
        private readonly System.Collections.Concurrent.ConcurrentDictionary<(int x, int y, int z), ChestTileEntity> _chestEntities
            = new System.Collections.Concurrent.ConcurrentDictionary<(int x, int y, int z), ChestTileEntity>();
        // Jukebox tile entities — same lifetime model as furnaces /
        // chests. Created when a disc is first inserted (a freshly
        // placed empty jukebox doesn't allocate one until interact);
        // removed when the block breaks. Persisted alongside the
        // furnace + chest dicts in WorldSaveFormat (Tier 4 #25 bumps
        // the save version to v11 with a new trailing block).
        private readonly Dictionary<(int x, int y, int z), JukeboxTileEntity> _jukeboxEntities
            = new Dictionary<(int x, int y, int z), JukeboxTileEntity>();

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

        // Tier 4 #24 — Painting entities. Same flat-list shape as
        // Passives/Hostiles — small per-world count (paintings never
        // tick or move, so the list only grows on player placement
        // and shrinks on player break). Persisted via WorldSaveFormat
        // v10. Spawn list is exposed read/write so GameRenderer can
        // append on RMB-place and remove on LMB-pick; the renderer
        // also iterates it each frame to draw.
        private readonly List<Painting> _paintings = new List<Painting>();
        public List<Painting> Paintings => _paintings;

        private readonly Noise _noise;

        public int Seed { get; }
        public Noise Noise => _noise;

        // Phase 3 — server-side change journal. Every successful SetBlock
        // (with `record:true` — the default) appends a record here; the
        // ServerHub drains the list at the end of each tick and emits one
        // BlockChange packet per record to each client whose tracked-chunk
        // set contains the cell.
        //
        // Client-side Worlds (multiplayer replicas) call SetBlock with
        // `record:false` when applying inbound BlockChange packets so the
        // list never grows on a client that isn't draining it.
        //
        // We deliberately don't deduplicate on the way in — if the same
        // cell flips A→B→A inside one tick, both records ship. The end-
        // state matches anyway and dedup'd records would mask intermediate
        // states the client mesher might want to render briefly (e.g.
        // explosion debris). If pathological churn becomes a problem,
        // a per-cell coalesce dictionary slots in here cleanly.
        private readonly List<BlockChangeRecord> _pendingBlockChanges = new List<BlockChangeRecord>();

        public IReadOnlyList<BlockChangeRecord> PendingBlockChanges => _pendingBlockChanges;
        public void ClearPendingBlockChanges() => _pendingBlockChanges.Clear();

        // Record a BlockChange for a cell whose META mutated but whose
        // TYPE stayed the same — door toggle is the canonical case.
        // SetBlock's "if (oldT == t) return false; // no-op" early-out
        // would skip the journal append in this scenario, so callers
        // that mutated meta directly via Chunk.SetMeta poke the
        // journal manually here. Reads the cell's current type +
        // meta so the broadcast carries the live values.
        public void RecordMetaChange(int wx, int wy, int wz)
        {
            if (wy < 0 || wy >= Chunk.SizeY) return;
            int cx = (int)Math.Floor(wx / (float)Chunk.SizeX);
            int cz = (int)Math.Floor(wz / (float)Chunk.SizeZ);
            int lx = wx - cx * Chunk.SizeX;
            int lz = wz - cz * Chunk.SizeZ;
            var c = GetChunk(cx, cz);
            if (c == null) return;
            var t = c.Get(lx, wy, lz);
            byte meta = c.GetMeta(lx, wy, lz);
            _pendingBlockChanges.Add(new BlockChangeRecord(wx, wy, wz, t, meta));
        }

        private World(int seed)
        {
            Seed = seed;
            _noise = new Noise(seed);
        }

        // Construct a World with no chunks. Used by the multiplayer client
        // path: after handshake the client knows the seed but has no terrain
        // until ChunkLoad packets arrive. Same Noise initialisation as
        // Generate(seed) so any client-side code that consults World.Noise
        // (currently the visible-bedrock checker) gives the same answers
        // the server's noise gives. The list of chunks stays empty until
        // ApplyChunkLoad starts installing replicated chunks.
        public static World CreateEmpty(int seed) => new World(seed);

        public static World Generate(int seed)
        {
            var w = new World(seed);

            // P4 of the chunk-streaming smoothness work — parallelise
            // the 5×5 initial-ring generation. Each chunk's gen is
            // independent: terrain noise is per-cell-pure, dungeon
            // gen mutates only the chunk's own blocks + the chest
            // dict (now ConcurrentDictionary, see P1), and the
            // mob-spawn compute is read-only on the chunk plus a
            // local List<>. The single-threaded version was a
            // ~200 ms render-thread freeze at world entry; spreading
            // across all cores typically brings that under ~50 ms,
            // and the loading screen behind it hides the rest.
            //
            // Spawn intents accumulate per-chunk into thread-local
            // lists then merge into _passives / _hostiles in a final
            // serial pass — sidesteps the otherwise-needed lock on
            // those Lists. Order of merge doesn't matter; the spawn
            // RNG is per-column-deterministic so the resulting
            // _passives / _hostiles content is identical to the
            // sequential version (just possibly in a different
            // List<> order — no caller relies on the order).
            int side = InitialRadiusChunks * 2 + 1;
            int total = side * side;
            var chunks = new Chunk[total];
            var passiveBuckets = new List<PassiveMob>[total];
            var hostileBuckets = new List<HostileMob>[total];

            System.Threading.Tasks.Parallel.For(0, total, i =>
            {
                int dz = (i / side) - InitialRadiusChunks;
                int dx = (i % side) - InitialRadiusChunks;
                var c = new Chunk(dx, dz);
                TerrainGenerator.Generate(c, w._noise);
                // Tier 6 #32 — Dungeons run after terrain (so caves
                // exist and we can be selective about which chunks
                // get them) but before LightCalculator (so the
                // dungeon's interior is included in the initial
                // sky-light propagation).
                w.GenerateDungeonsInChunk(c);
                LightCalculator.RecomputeChunk(c);

                var pl = new List<PassiveMob>();
                var hl = new List<HostileMob>();
                w.ComputePassiveSpawnsForChunk(c, pl);
                w.ComputeHostileSpawnsForChunk(c, hl);

                chunks[i] = c;
                passiveBuckets[i] = pl;
                hostileBuckets[i] = hl;
            });

            // Serial merge — installs and spawn-list extend.
            for (int i = 0; i < total; i++)
            {
                var c = chunks[i];
                w._chunks[(c.ChunkX, c.ChunkZ)] = c;
                if (passiveBuckets[i].Count > 0) w._passives.AddRange(passiveBuckets[i]);
                if (hostileBuckets[i].Count > 0) w._hostiles.AddRange(hostileBuckets[i]);
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
        // Tier 6 #32 — Dungeon generation. Runs once per chunk after
        // TerrainGenerator (so caves/ores have already cut the stone),
        // before SpawnPassives (so a fresh dungeon's chest counts
        // against any nearby loot table). Each chunk rolls 8 attempts
        // at a random underground position; if it hits a clean 5×4×5
        // stone box, a cobble dungeon goes there. The spawner block
        // is decorative for now — gameplay tick is a follow-up.
        //
        // Deterministic per-chunk RNG (seed + chunk coords) so a
        // given seed always generates dungeons in the same places,
        // matching how passive-spawn + flora gen stay reproducible.
        public void GenerateDungeonsInChunk(Chunk c)
        {
            int hash = (int)((uint)Seed * 1103515245u + (uint)(c.ChunkX * 0x68b1f6) + (uint)(c.ChunkZ * 0x6f1c43));
            var rng = new Random(hash);
            const int Attempts = 8;
            for (int i = 0; i < Attempts; i++)
            {
                // Position: leave a 1-block margin from chunk edges so
                // the 5×4×5 box fits without crossing into a neighbour.
                // Y range biases toward the lower-half of the cave-
                // eligible band (matches Alpha — dungeons are deep).
                int lx = 1 + rng.Next(Chunk.SizeX - 6);
                int lz = 1 + rng.Next(Chunk.SizeZ - 6);
                int ly = 8 + rng.Next(System.Math.Max(1, TerrainGenerator.SeaLevel - 14));

                if (TryPlaceDungeon(c, lx, ly, lz, rng))
                {
                    // One dungeon per chunk is plenty — Alpha lets two
                    // overlap occasionally but the result is messy.
                    return;
                }
            }
        }

        // Attempt to place a 5-wide × 4-tall × 5-deep cobble dungeon
        // at chunk-local (lx, ly, lz). The box's footprint is the
        // outer cobble shell (5×5 floor + 5×5 ceiling + 4 1-block-
        // tall walls); the interior 3×2×3 is hollowed to air.
        // Returns true if placement succeeded.
        private bool TryPlaceDungeon(Chunk c, int lx, int ly, int lz, Random rng)
        {
            // Bounds check (defensive; caller already left margin).
            if (lx < 0 || ly < 0 || lz < 0) return false;
            if (lx + 5 > Chunk.SizeX) return false;
            if (lz + 5 > Chunk.SizeZ) return false;
            if (ly + 4 > Chunk.SizeY) return false;

            // Eligibility: at least 80 % of the box's volume must
            // currently be solid stone-or-dirt-or-gravel (i.e. we're
            // carving into the underground, not floating in the air).
            // Empty cells are tolerated up to 20 % so a dungeon that
            // pokes into an existing cave still spawns — that's how
            // the player typically discovers them.
            int solidCount = 0;
            int totalCells = 5 * 4 * 5;
            for (int dx = 0; dx < 5; dx++)
            for (int dy = 0; dy < 4; dy++)
            for (int dz = 0; dz < 5; dz++)
            {
                var t = c.Get(lx + dx, ly + dy, lz + dz);
                if (t == BlockType.Stone || t == BlockType.Dirt
                    || t == BlockType.Gravel || t == BlockType.Cobblestone
                    || t == BlockType.MossyCobblestone)
                {
                    solidCount++;
                }
            }
            if (solidCount < (totalCells * 80) / 100) return false;

            // Pass 1 — wall cells: cobblestone with ~25% mossy.
            // Pass 2 — interior cells (dx 1..3, dy 1..2, dz 1..3): air.
            for (int dx = 0; dx < 5; dx++)
            for (int dy = 0; dy < 4; dy++)
            for (int dz = 0; dz < 5; dz++)
            {
                bool isInterior = dx >= 1 && dx <= 3 && dy >= 1 && dy <= 2 && dz >= 1 && dz <= 3;
                if (isInterior)
                {
                    c.Set(lx + dx, ly + dy, lz + dz, BlockType.Air);
                }
                else
                {
                    bool mossy = rng.Next(4) == 0;
                    c.Set(lx + dx, ly + dy, lz + dz,
                        mossy ? BlockType.MossyCobblestone : BlockType.Cobblestone);
                }
            }

            // Spawner at the centre of the floor + 1 (so it sits on
            // the floor block, with the cage block at floor+1).
            int spX = lx + 2;
            int spY = ly + 1;
            int spZ = lz + 2;
            c.Set(spX, spY, spZ, BlockType.MobSpawner);

            // Chest at a random wall corner (interior side). Pick from
            // the 4 corners of the bottom-row interior.
            var corners = new (int, int)[]
            {
                (lx + 1, lz + 1),
                (lx + 3, lz + 1),
                (lx + 1, lz + 3),
                (lx + 3, lz + 3),
            };
            var (chestX, chestZ) = corners[rng.Next(corners.Length)];
            int chestY = ly + 1;
            c.Set(chestX, chestY, chestZ, BlockType.Chest);
            // Tile-entity registration uses world coords. Chest gets
            // a deterministic-per-position random loot fill; chunk-
            // local (chestX, chestZ) → world (worldChestX, worldChestZ)
            // via chunk origin.
            int worldChestX = c.ChunkX * Chunk.SizeX + chestX;
            int worldChestZ = c.ChunkZ * Chunk.SizeZ + chestZ;
            var ce = GetOrCreateChestEntity(worldChestX, chestY, worldChestZ);
            FillDungeonChestLoot(ce, rng);
            return true;
        }

        // Tier 6 #32 — Dungeon chest loot. Random selection of the
        // common Alpha 1.1.2_01 dungeon-chest items (bread, wheat,
        // sticks, gunpowder, string, iron ingot, bucket, saddle).
        // Chest has 27 slots; we fill 4..7 random ones with random
        // counts. Saddle + bucket are weighted rare to match Alpha.
        private static void FillDungeonChestLoot(ChestTileEntity ce, Random rng)
        {
            int slotCount = 4 + rng.Next(4); // 4..7
            for (int s = 0; s < slotCount; s++)
            {
                int slotIdx = rng.Next(ce.Slots.Length);
                if (!ce.Slots[slotIdx].IsEmpty) continue;
                int roll = rng.Next(100);
                BlockType item;
                int count;
                if (roll < 20)      { item = BlockType.Bread;      count = 1 + rng.Next(3); }
                else if (roll < 40) { item = BlockType.WheatItem;  count = 1 + rng.Next(4); }
                else if (roll < 60) { item = BlockType.Stick;      count = 1 + rng.Next(8); }
                else if (roll < 75) { item = BlockType.Gunpowder;  count = 1 + rng.Next(4); }
                else if (roll < 88) { item = BlockType.String;     count = 1 + rng.Next(4); }
                else if (roll < 96) { item = BlockType.IronIngot;  count = 1 + rng.Next(3); }
                else if (roll < 99) { item = BlockType.BucketEmpty; count = 1; }
                else                { item = BlockType.Saddle;     count = 1; }
                ce.Slots[slotIdx] = new ItemStack(item, count);
            }
        }

        // Render-thread shim: keep the historical signature so existing
        // callers (World.Generate's initial 5×5 ring) don't have to know
        // the internal split. The actual work is done by
        // ComputePassiveSpawnsForChunk so chunk workers can call it
        // off-thread and ship the resulting mob list back via GenResult.
        public void SpawnPassivesInChunk(Chunk c)
        {
            ComputePassiveSpawnsForChunk(c, _passives);
        }

        // Pure-compute variant: same logic as the historical method, but
        // appends new mobs to the caller-supplied list instead of
        // touching `_passives` directly. Safe to call from any thread —
        // it only reads the chunk's blocks (the chunk has no other
        // observer at gen time, and even after install the block reads
        // are byte-aligned and harmless to race).
        //
        // The chunk worker uses this with a fresh List<PassiveMob>
        // and stashes the list on GenResult; UpdateStreaming on the
        // render thread does a single AddRange(_passives, intents)
        // when the chunk install is "fresh" (not a cached-modified
        // reinstall — see InstallGeneratedChunk).
        public void ComputePassiveSpawnsForChunk(Chunk c, List<PassiveMob> output)
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
                output.Add(mob);
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
        // Continuous live spawning at runtime is layered on top of this
        // pass (TickMobSpawns). Chunk-gen runs two passes: a SURFACE pass
        // walks every column at low density and drops a hostile if the
        // topmost-solid cell is dark enough (mostly pre-night), and a
        // CAVE pass samples a handful of subterranean Y values per
        // column for cave hostiles. Without the cave pass, freshly
        // generated chunks are devoid of underground hostiles until the
        // live-spawn loop happens to roll one — that's a long wait,
        // and breaks the "dig down into a cave and find mobs already
        // there" feel the Alpha world gives.
        // Render-thread shim — same idiom as SpawnPassivesInChunk.
        // Defers to ComputeHostileSpawnsForChunk so chunk workers can
        // compute the freshly-generated chunk's hostile + slime spawn
        // set off-thread and ship the result to the render thread for
        // a single AddRange installation.
        public void SpawnHostilesInChunk(Chunk c)
        {
            ComputeHostileSpawnsForChunk(c, _hostiles);
        }

        // Pure-compute variant — see ComputePassiveSpawnsForChunk for
        // the rationale. Walks the chunk's columns, runs both surface
        // and cave passes, and runs the per-chunk slime-chunk pass.
        // Output is appended to (not cleared) so the same buffer can
        // accumulate spawns from multiple chunks if needed.
        public void ComputeHostileSpawnsForChunk(Chunk c, List<HostileMob> output)
        {
            const int SurfaceRareDenominator = 360;
            // Cave attempts per column. Per-column hashed RNG draws this
            // many distinct Ys in the cave band; each Y is gated
            // independently. 6 attempts × 16×16 columns × the cave
            // fraction adds up to a small handful of cave hostiles per
            // chunk, which matches the rate Alpha generates them at.
            const int CaveAttemptsPerColumn = 6;
            int chunkBaseX = c.ChunkX * Chunk.SizeX;
            int chunkBaseZ = c.ChunkZ * Chunk.SizeZ;
            for (int lx = 0; lx < Chunk.SizeX; lx++)
            for (int lz = 0; lz < Chunk.SizeZ; lz++)
            {
                int wx = chunkBaseX + lx;
                int wz = chunkBaseZ + lz;
                // Per-column hash — same scheme as passive spawns but
                // offset so the two streams are independent.
                int hash = unchecked((int)(
                    (uint)Seed * 0x9E3779B1u
                    ^ (uint)wx * 0x85EBCA77u
                    ^ (uint)wz * 0x27D4EB2Du));
                hash = (hash ^ (hash >> 13)) * 0x5BD1E995;
                hash ^= hash >> 15;

                // ---- SURFACE pass ----
                int surfaceY = -1;
                for (int y = Chunk.SizeY - 1; y >= 0; y--)
                {
                    var b = c.Get(lx, y, lz);
                    if (b == BlockType.Air || BlockData.IsLightTransparent(b)) continue;
                    surfaceY = y;
                    break;
                }
                if (surfaceY >= 0)
                {
                    var surface = c.Get(lx, surfaceY, lz);
                    bool surfaceSpawnable =
                        BlockData.IsSolid(surface) &&
                        surfaceY + 2 < Chunk.SizeY &&
                        c.Get(lx, surfaceY + 1, lz) == BlockType.Air &&
                        c.Get(lx, surfaceY + 2, lz) == BlockType.Air;
                    if (surfaceSpawnable)
                    {
                        int bucket = (int)((uint)hash % (uint)SurfaceRareDenominator);
                        if (bucket == 0)
                        {
                            int sky = c.GetSkyLight(lx, surfaceY + 1, lz);
                            int blk = c.GetBlockLight(lx, surfaceY + 1, lz);
                            int eff = sky > blk ? sky : blk;
                            if (eff <= 7)
                            {
                                int mobSeed = hash ^ 0x33CC33CC;
                                int kindHash = unchecked((int)((uint)hash * 0x85EBCA6Bu ^ 0xC2B2AE35u));
                                int kindRoll = (int)((uint)kindHash % 100u);
                                var spawnPos = new OpenTK.Vector3(
                                    wx + 0.5f, surfaceY + 1f, wz + 0.5f);
                                HostileMob mob;
                                if      (kindRoll < 35) mob = new Zombie(spawnPos, mobSeed);
                                else if (kindRoll < 60) mob = new Skeleton(spawnPos, mobSeed);
                                else if (kindRoll < 85) mob = new Spider(spawnPos, mobSeed);
                                else                    mob = new Creeper(spawnPos, mobSeed);
                                output.Add(mob);
                            }
                        }
                    }
                }

                // ---- CAVE pass ----
                // Take CaveAttemptsPerColumn random Ys in the cave band.
                // Each draw is its own derivation of the column hash so
                // an attempt that fails doesn't leak entropy into the
                // next attempt's roll. Each successful Y gets its own
                // light gate + kind roll.
                uint caveHash = unchecked((uint)hash * 0xC2B2AE3Du);
                for (int attempt = 0; attempt < CaveAttemptsPerColumn; attempt++)
                {
                    caveHash = unchecked(caveHash * 0x85EBCA6Bu + 0x9E3779B1u);
                    // Independent roll for whether this attempt fires at all.
                    // Match the surface density so cave + surface together
                    // produce a similar overall hostile budget per chunk.
                    if ((caveHash & 0xFFu) >= 32u) continue;

                    int yRange = CaveSampleMaxY - CaveSampleMinY + 1;
                    int yCandidate = CaveSampleMinY + (int)((caveHash >> 8) % (uint)yRange);
                    if (yCandidate + 2 >= Chunk.SizeY) continue;
                    if (!BlockData.IsSolid(c.Get(lx, yCandidate, lz))) continue;
                    if (c.Get(lx, yCandidate + 1, lz) != BlockType.Air) continue;
                    if (c.Get(lx, yCandidate + 2, lz) != BlockType.Air) continue;

                    int sky = c.GetSkyLight(lx, yCandidate + 1, lz);
                    int blk = c.GetBlockLight(lx, yCandidate + 1, lz);
                    int eff = sky > blk ? sky : blk;
                    if (eff > 7) continue;

                    int mobSeed = unchecked((int)caveHash) ^ 0x33CC33CC;
                    int kindRoll = (int)((caveHash >> 16) % 100u);
                    var spawnPos = new OpenTK.Vector3(
                        wx + 0.5f, yCandidate + 1f, wz + 0.5f);
                    HostileMob mob;
                    if      (kindRoll < 35) mob = new Zombie(spawnPos, mobSeed);
                    else if (kindRoll < 60) mob = new Skeleton(spawnPos, mobSeed);
                    else if (kindRoll < 85) mob = new Spider(spawnPos, mobSeed);
                    else                    mob = new Creeper(spawnPos, mobSeed);
                    output.Add(mob);
                }
            }

            // ---- SLIME pass ----
            // Tier 4 #18 — Slimes spawn in a tiny rare subset of chunks
            // ("slime chunks") regardless of light, at low Y. The chunk
            // selector is a deterministic hash of (chunkX, chunkZ) so
            // a given world-seed reliably has the SAME slime chunks
            // every load — players who learn one chunk's slime address
            // can return to it. Mask of 0x3FF gives a ~1-in-1024 hit
            // rate, which is plenty rare; the alternative of "every
            // chunk has a small chance" would scatter slimes
            // everywhere and dilute the "find the slime cave" niche
            // that's central to the Alpha slime-farming idiom.
            //
            // Per slime-chunk we attempt SlimeAttempts spawns, each
            // sampling a random Y in [0, SlimeMaxY). Floor + 2-air-
            // headroom required (Big slime is 2.0 tall — we still
            // demand 2 air blocks above the floor, which is enough for
            // size=1 placement; oversize-on-a-tight-pocket clipping is
            // resolved by the entity's IntegrateMotion the same way it
            // is for any AABB-versus-cell collision elsewhere). NO
            // light gate — this is the whole reason slimes are a
            // distinct species.
            //
            // V1 always spawns Big (size=2) slimes. Alpha 1.1.2_01
            // actually picks size in {0,1,2} uniformly per spawn, but
            // a fresh slime chunk producing 4 medium + small slimes
            // immediately overwhelms the local mob count once each
            // Big-slime-equivalent splits into 2..4 children. Big-only
            // keeps the chunk-gen population tractable; the runtime
            // split path produces the smaller variants organically.
            uint slimeChunkHash = unchecked(
                (uint)c.ChunkX * 0x1F1F1F1Fu + (uint)c.ChunkZ * 0x91E10DA5u);
            if ((slimeChunkHash & SlimeChunkMask) == 0u)
            {
                uint slimeRng = unchecked(slimeChunkHash * 0x9E3779B1u + (uint)Seed);
                for (int attempt = 0; attempt < SlimeAttempts; attempt++)
                {
                    slimeRng = unchecked(slimeRng * 0x85EBCA6Bu + 0xC2B2AE3Du);
                    int slx = (int)((slimeRng >> 4) & 0xFu);   // 0..15
                    slimeRng = unchecked(slimeRng * 0x85EBCA6Bu + 0xC2B2AE3Du);
                    int slz = (int)((slimeRng >> 4) & 0xFu);   // 0..15
                    slimeRng = unchecked(slimeRng * 0x85EBCA6Bu + 0xC2B2AE3Du);
                    int syCandidate = (int)((slimeRng >> 8) % (uint)SlimeMaxY);
                    if (syCandidate + 2 >= Chunk.SizeY) continue;
                    if (!BlockData.IsSolid(c.Get(slx, syCandidate, slz))) continue;
                    if (c.Get(slx, syCandidate + 1, slz) != BlockType.Air) continue;
                    if (c.Get(slx, syCandidate + 2, slz) != BlockType.Air) continue;

                    int slimeMobSeed = unchecked((int)slimeRng) ^ 0x5151AA55;
                    var slimeSpawn = new OpenTK.Vector3(
                        chunkBaseX + slx + 0.5f, syCandidate + 1f, chunkBaseZ + slz + 0.5f);
                    output.Add(new Slime(slimeSpawn, slimeMobSeed, /*size:*/2));
                }
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
        // Cave-attempt Y band — random subterranean Y picked uniformly in
        // [CaveSampleMinY, CaveSampleMaxY] for the cave-spawn branch. The
        // band straddles Alpha 1.1.2's typical cave depth: bottom of 8
        // skips bedrock + the immediate-bedrock slab, top of 56 reaches
        // up to the surface band where occasional skylit caverns sit.
        // Most attempts in this band fail (column is solid or the cell
        // isn't a 1-block-floor + 2-air pocket) — that's intended; the
        // attempts are cheap and the cave fraction × attempts is enough
        // to keep cave hostiles topped up.
        public const int   CaveSampleMinY        = 8;
        public const int   CaveSampleMaxY        = 56;
        // Tier 4 #18 — Slime chunk-gen pass. SlimeChunkMask is the
        // bitmask the (chunkX, chunkZ) hash is AND'd against; a result
        // of 0 means the chunk is a "slime chunk". 0x3FF (10 bits)
        // gives a 1-in-1024 hit rate, which is plenty rare so the
        // slime-farming niche stays a deliberate find. SlimeAttempts
        // is the per-slime-chunk spawn quota; 4 is enough that a hit
        // chunk feels populated without drowning in slimes (each
        // big-slime-equivalent splits into 2..4 children on death,
        // multiplying the population organically).
        public const uint  SlimeChunkMask        = 0x3FFu;
        public const int   SlimeAttempts         = 4;
        public const int   SlimeMaxY             = 40;

        private float _spawnTimer;
        private readonly Random _spawnRng = new Random();

        // Tier 4 #14 — Wheat random-tick state. Crop growth is much
        // slower than mob spawn ticks (real-world minutes per stage,
        // not seconds), so we keep a separate timer + RNG. Each tick
        // walks a small set of loaded chunks, samples a few cells per
        // chunk, and promotes Wheat-on-Farmland by one stage if the
        // sky+block light at that cell is at least 9 (Alpha rule).
        // Light gate matches the canon — wheat in a torchlit indoor
        // farm grows; wheat in a dark cave doesn't.
        private float _cropTimer;
        private readonly Random _cropRng = new Random(0xCB07);
        public const float CropTickInterval = 5.0f;

        // Tier 6 #34 — Fire propagation state. Same rate-limiter shape
        // as the crop tick — every ~1s we sample a small set of loaded
        // chunks, walk a few random cells in each, and for every Fire
        // block found we roll for spread to a flammable neighbour and
        // a die-out chance. Spread targets must be Air with a flammable
        // (wood/planks/leaves/wool/tallgrass) block adjacent. Fire over
        // a non-flammable surface (e.g. cobble) eventually dies out;
        // Fire over a flammable surface persists much longer (Alpha
        // rule — netherrack-style "eternal fire" is netherrack-only,
        // which we don't have, so all fire is mortal here).
        private float _fireTimer;
        private readonly Random _fireRng = new Random(0xF1AE);
        public const float FireTickInterval = 1.0f;
        private const int FireChunksPerTick = 6;
        private const int FireCellsPerChunk = 8;

        // Tier 6 #35 — Falling sand / gravel physics. Edit-driven
        // queue: SetBlock enqueues affected coordinates when the new
        // block is sand/gravel (might fall) or when the new block
        // exposes a cell beneath sand/gravel (the cell above might
        // now fall). TickFallingPhysics drains the queue every
        // FallStepInterval seconds, swapping unsupported sand/gravel
        // down one cell. After a successful fall the new lower
        // coordinate is re-enqueued so the block keeps falling. We
        // skip the entity-based "FallingSand entity 70" approach
        // Alpha shipped — the cell-swap reads identically to the
        // player at 10 cells/sec and avoids spawning short-lived
        // entities that we'd then have to net-replicate.
        private readonly HashSet<(int x, int y, int z)> _pendingFallChecks = new HashSet<(int, int, int)>();
        private float _fallStepTimer;
        public const float FallStepInterval = 0.10f;  // candidate-scan cadence; the entity itself moves continuously

        // Live falling-block entities — spawned by TickFallingPhysics
        // when an unsupported sand/gravel is detected, despawned by
        // UpdateFallingBlocks when the entity reaches a solid floor.
        // Kept as a readonly list reference so GameRenderer can
        // iterate without copying. Mutations happen on the same
        // thread (host SP / server tick loop) — no concurrent access.
        private readonly List<FallingBlockEntity> _fallingBlocks = new List<FallingBlockEntity>();
        public IReadOnlyList<FallingBlockEntity> FallingBlocks => _fallingBlocks;

        // Tier 8 #43 — Live primed-TNT entities. Spawned when the
        // player ignites a placed Tnt block with Flint & Steel; each
        // counts down a 4-second fuse and is removed when the host
        // renderer's per-frame tick runs the explosion algorithm
        // (which writes the block damage via SetBlock and scatters
        // drops). Same pattern as the falling-block list.
        private readonly List<PrimedTntEntity> _primedTnt = new List<PrimedTntEntity>();
        public IReadOnlyList<PrimedTntEntity> PrimedTnt => _primedTnt;
        public void AddPrimedTnt(PrimedTntEntity e) => _primedTnt.Add(e);
        public void RemovePrimedTntAt(int index) => _primedTnt.RemoveAt(index);
        // Per-tick: 4 chunks, 6 cells each = 24 sample chances. With
        // ~1/12 promotion probability per sampled wheat cell, a single
        // wheat block walks through stages 0..7 in ~6 minutes of real
        // time on average — slow enough to feel like farming, fast
        // enough to not need an AFK loop to see results.
        private const int CropChunksPerTick = 4;
        private const int CropCellsPerChunk = 6;

        public void TickMobSpawns(float dt, OpenTK.Vector3 playerPos, int skySubtract)
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

                // Pick a candidate Y for this attempt. Half the attempts
                // probe the surface (topmost solid — covers passive
                // spawns on grass + surface hostile spawns at night
                // once skySubtract is applied). The other half sample
                // a random subterranean Y *directly* for cave hostile
                // spawns: pick yCandidate in [8, CaveSampleMaxY], check
                // whether (lx, yCandidate, lz) is itself a valid floor
                // (solid block with 2 air blocks above). If not, the
                // attempt skips — we deliberately do NOT walk down to
                // find the next floor, because walking down from above
                // ground in a column with no caves just rediscovers the
                // surface (every block above ground is air, every block
                // below ground until the surface tile is also air, and
                // the first solid hit IS the surface). Direct sampling
                // means most attempts fail (column is solid rock or air
                // at that Y) but the 50/50 cave budget × AttemptsPerTick
                // × the world's cave fraction adds up to a steady cave-
                // hostile drip.
                int floorY;
                if (_spawnRng.NextDouble() < 0.5)
                {
                    floorY = TopmostSolidY(chunk, lx, lz);
                    if (floorY < 0) continue;
                    if (floorY + 2 >= Chunk.SizeY) continue;
                    if (chunk.Get(lx, floorY + 1, lz) != BlockType.Air) continue;
                    if (chunk.Get(lx, floorY + 2, lz) != BlockType.Air) continue;
                }
                else
                {
                    int yCandidate = _spawnRng.Next(CaveSampleMinY, CaveSampleMaxY + 1);
                    if (yCandidate + 2 >= Chunk.SizeY) continue;
                    if (!BlockData.IsSolid(chunk.Get(lx, yCandidate, lz))) continue;
                    if (chunk.Get(lx, yCandidate + 1, lz) != BlockType.Air) continue;
                    if (chunk.Get(lx, yCandidate + 2, lz) != BlockType.Air) continue;
                    floorY = yCandidate;
                }

                var surfaceBlock = chunk.Get(lx, floorY, lz);
                if (!BlockData.IsSolid(surfaceBlock)) continue;

                int sky = chunk.GetSkyLight(lx, floorY + 1, lz);
                int blk = chunk.GetBlockLight(lx, floorY + 1, lz);
                // Apply Alpha-style sky-light attenuation: stored sky=15
                // on every surface cell, but the time-of-day clock ramps
                // skySubtract from 0 (noon) to 11 (midnight). Effective
                // brightness for the spawn gate is then max(sky - sub, blk).
                int effSky = sky - skySubtract;
                if (effSky < 0) effSky = 0;
                int eff = effSky > blk ? effSky : blk;

                var spawnPos = new OpenTK.Vector3(wx + 0.5f, floorY + 1f, wz + 0.5f);
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
                else if (eff >= 9 && surfaceBlock == BlockType.Grass && !passiveFull)
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

        // Topmost solid block in a column — skip air + light-transparent
        // caps so we land on real terrain (not glass / flora / leaves).
        // Returns -1 if the column has no solid block.
        private static int TopmostSolidY(Chunk c, int lx, int lz)
        {
            for (int y = Chunk.SizeY - 1; y >= 0; y--)
            {
                var b = c.Get(lx, y, lz);
                if (b == BlockType.Air || BlockData.IsLightTransparent(b)) continue;
                return y;
            }
            return -1;
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

        // Tier 4 #14 — Wheat growth random tick. Walks a small set of
        // loaded chunks per call, samples random cells, and promotes
        // any Wheat cell that's standing on Farmland and seeing
        // sky+block light ≥ 9. Stage promotion is metadata + 1 (clamped
        // at 7); the chunk is marked dirty so the mesher rebuilds with
        // the new stage's tile. Light-gating matches Alpha — torch-lit
        // indoor farms work; dark caves don't.
        public void TickRandomCrops(float dt)
        {
            _cropTimer -= dt;
            if (_cropTimer > 0f) return;
            _cropTimer = CropTickInterval;

            // Snapshot chunk keys so we don't enumerate a concurrent
            // dictionary that worker jobs are still inserting into.
            var keys = new List<(int x, int z)>(_chunks.Count);
            foreach (var k in _chunks.Keys) keys.Add(k);
            if (keys.Count == 0) return;

            int chunksToSample = Math.Min(CropChunksPerTick, keys.Count);
            for (int c = 0; c < chunksToSample; c++)
            {
                var key = keys[_cropRng.Next(keys.Count)];
                if (!_chunks.TryGetValue(key, out var chunk)) continue;

                for (int s = 0; s < CropCellsPerChunk; s++)
                {
                    int lx = _cropRng.Next(Chunk.SizeX);
                    int lz = _cropRng.Next(Chunk.SizeZ);
                    int ly = _cropRng.Next(Chunk.SizeY);
                    int idx = Chunk.Index(lx, ly, lz);

                    // Tier 4 #26 — Sugar cane vertical growth. Branches
                    // out before the wheat path because cane has its own
                    // gating (no farmland, no light) and a different
                    // outcome (place a new SugarCane block in the cell
                    // above, instead of incrementing a metadata stage in
                    // the cell itself). Alpha rule: when the cell above
                    // is air AND the cane stack is shorter than 3, roll
                    // a 1/3 chance to grow another cane on top.
                    if (chunk.RawBlocks[idx] == (byte)BlockType.SugarCane)
                    {
                        if (ly + 1 >= Chunk.SizeY) continue;
                        int aboveIdx = Chunk.Index(lx, ly + 1, lz);
                        if (chunk.RawBlocks[aboveIdx] != (byte)BlockType.Air) continue;
                        // Count cane below so we cap at a 3-tall stack.
                        // If THIS cell already has 2 canes beneath it,
                        // it's the third cane and growth would push the
                        // stack to 4. Walk down counting cane until air
                        // or a non-cane is hit.
                        int caneBelow = 0;
                        for (int dy = ly - 1; dy >= 0; dy--)
                        {
                            int bidx = Chunk.Index(lx, dy, lz);
                            if (chunk.RawBlocks[bidx] != (byte)BlockType.SugarCane) break;
                            caneBelow++;
                        }
                        if (caneBelow >= 2) continue; // already at max height
                        // 1/3 probability gate. Same probability ladder
                        // as the wheat 1/12 above, just looser because
                        // cane only has 3 stages of vertical growth
                        // versus wheat's 8 stages of metadata growth.
                        if (_cropRng.Next(3) != 0) continue;
                        chunk.RawBlocks[aboveIdx] = (byte)BlockType.SugarCane;
                        chunk.IsModified = true;
                        _dirty.Add(key);
                        continue;
                    }

                    // Tier 8 #47 — Sapling growth. When a sapling cell
                    // is sampled with enough light AND clear vertical
                    // headroom, roll 1/40 to grow into an oak. Average
                    // wall-clock time is ~5-10 min depending on how
                    // many other saplings share the random sample
                    // pool — close to Alpha's natural growth rate.
                    if (chunk.RawBlocks[idx] == (byte)BlockType.Sapling)
                    {
                        // Light gate (same threshold as wheat).
                        byte lp = chunk.RawLight[idx];
                        int skyL = (lp >> 4) & 0xF;
                        int blkL = lp & 0xF;
                        if (skyL < 9 && blkL < 9) continue;
                        // Probability gate.
                        if (_cropRng.Next(40) != 0) continue;
                        int wx = key.x * Chunk.SizeX + lx;
                        int wz = key.z * Chunk.SizeZ + lz;
                        TryGrowOakTree(wx, ly, wz, _cropRng);
                        continue;
                    }

                    if (chunk.RawBlocks[idx] != (byte)BlockType.Wheat) continue;

                    // Light gate. Block-light overrides darkness in
                    // torch-lit indoor farms; sky-light is the natural
                    // outdoor source. Either ≥ 9 lets the crop grow.
                    byte lightPacked = chunk.RawLight[idx];
                    int sky = (lightPacked >> 4) & 0xF;
                    int blk = lightPacked & 0xF;
                    if (sky < 9 && blk < 9) continue;

                    // Farmland support — wheat falls/dies if the cell
                    // below isn't farmland. We don't kill it here, but
                    // we do require it for growth. (Cleanup of orphan
                    // wheat is left to a future tier — it costs nothing
                    // to leave it standing in the meantime; the player
                    // can break it manually.)
                    if (ly == 0) continue;
                    int belowIdx = Chunk.Index(lx, ly - 1, lz);
                    if (chunk.RawBlocks[belowIdx] != (byte)BlockType.Farmland) continue;

                    // Promote stage. Probability gate keeps the average
                    // stage-promotion interval comfortably above one
                    // tick — see CropChunksPerTick comment for the
                    // resulting end-to-end growth time.
                    if (_cropRng.Next(12) != 0) continue;

                    byte meta = chunk.RawMeta[idx];
                    int stage = meta & 0x0F;
                    if (stage >= 7) continue;
                    chunk.RawMeta[idx] = (byte)((meta & 0xF0) | (stage + 1));
                    chunk.IsModified = true;
                    _dirty.Add(key);
                }
            }
        }

        // Tier 6 #34 — Fire propagation tick. Walks a small random set
        // of loaded chunks; in each, samples random cells for Fire
        // blocks. For every Fire found, rolls for spread to one
        // adjacent air cell that has a flammable neighbour (so it
        // doesn't spread into open air), and rolls for die-out (the
        // probability is biased by whether the block beneath is
        // flammable — Fire over Planks lasts longer than Fire over
        // Cobble, matching Alpha's "fire on flammable" rule).
        public void TickFire(float dt)
        {
            _fireTimer -= dt;
            if (_fireTimer > 0f) return;
            _fireTimer = FireTickInterval;

            var keys = new List<(int x, int z)>(_chunks.Count);
            foreach (var k in _chunks.Keys) keys.Add(k);
            if (keys.Count == 0) return;

            int chunksToSample = Math.Min(FireChunksPerTick, keys.Count);
            for (int c = 0; c < chunksToSample; c++)
            {
                var key = keys[_fireRng.Next(keys.Count)];
                if (!_chunks.TryGetValue(key, out var chunk)) continue;

                for (int s = 0; s < FireCellsPerChunk; s++)
                {
                    int lx = _fireRng.Next(Chunk.SizeX);
                    int lz = _fireRng.Next(Chunk.SizeZ);
                    int ly = _fireRng.Next(Chunk.SizeY);
                    int idx = Chunk.Index(lx, ly, lz);
                    if (chunk.RawBlocks[idx] != (byte)BlockType.Fire) continue;

                    int wx = key.x * Chunk.SizeX + lx;
                    int wy = ly;
                    int wz = key.z * Chunk.SizeZ + lz;

                    // Die-out roll. Fire on a flammable substrate gets
                    // a low extinguish chance; fire on non-flammable
                    // (or air below) gets a higher one. Without rain /
                    // age tracking we just stochastically sunset.
                    var below = ly > 0
                        ? (BlockType)chunk.RawBlocks[Chunk.Index(lx, ly - 1, lz)]
                        : BlockType.Air;
                    bool standsOnFlammable = IsFlammable(below);
                    int dieDenom = standsOnFlammable ? 40 : 8;
                    if (_fireRng.Next(dieDenom) == 0)
                    {
                        SetBlock(wx, wy, wz, BlockType.Air);
                        continue;
                    }

                    // Spread roll. Pick one of the 6 axis-aligned
                    // neighbours; if it's air AND has at least one
                    // flammable neighbour itself (so fire doesn't
                    // travel through empty space), set it on fire.
                    if (_fireRng.Next(3) != 0) continue;
                    int dir = _fireRng.Next(6);
                    int nx = wx, ny = wy, nz = wz;
                    switch (dir)
                    {
                        case 0: nx -= 1; break;
                        case 1: nx += 1; break;
                        case 2: ny -= 1; break;
                        case 3: ny += 1; break;
                        case 4: nz -= 1; break;
                        case 5: nz += 1; break;
                    }
                    if (ny < 0 || ny >= Chunk.SizeY) continue;
                    if (GetBlock(nx, ny, nz) != BlockType.Air) continue;
                    if (!HasFlammableNeighbour(nx, ny, nz)) continue;
                    SetBlock(nx, ny, nz, BlockType.Fire);
                }
            }
        }

        // Flammability table — matches Alpha's set: wood, planks,
        // leaves, wool, tallgrass, bookshelf. Cane and flowers count
        // too (they're plant matter, instant-break already, fire
        // burning them is purely cosmetic). Doors/fences NOT included
        // because their hitboxes complicate visuals; Alpha treats
        // those as flammable but we keep the rule simple.
        private static bool IsFlammable(BlockType t)
        {
            switch (t)
            {
                case BlockType.WoodLog:
                case BlockType.Planks:
                case BlockType.Leaves:
                case BlockType.Wool:
                case BlockType.Bookshelf:
                case BlockType.SugarCane:
                case BlockType.Dandelion:
                case BlockType.Rose:
                    return true;
                default:
                    return false;
            }
        }

        private bool HasFlammableNeighbour(int wx, int wy, int wz)
        {
            if (wy > 0 && IsFlammable(GetBlock(wx, wy - 1, wz))) return true;
            if (wy + 1 < Chunk.SizeY && IsFlammable(GetBlock(wx, wy + 1, wz))) return true;
            if (IsFlammable(GetBlock(wx - 1, wy, wz))) return true;
            if (IsFlammable(GetBlock(wx + 1, wy, wz))) return true;
            if (IsFlammable(GetBlock(wx, wy, wz - 1))) return true;
            if (IsFlammable(GetBlock(wx, wy, wz + 1))) return true;
            return false;
        }

        // Tier 6 #35 — Falling-physics tick. Drains the pending-fall
        // queue every FallStepInterval seconds and spawns a
        // FallingBlockEntity for each unsupported sand/gravel cell.
        // The entity itself does the smooth descent (UpdateFallingBlocks
        // runs every frame); this method just decides "should this
        // cell be falling right now?". Spawn-cadence rate-limit
        // means cascaded stacks (5 sand on dirt, dig dirt) start
        // their falls 100ms apart instead of all at once — visually
        // pleasing trickle rather than a synchronised plummet.
        public void TickFallingPhysics(float dt)
        {
            _fallStepTimer -= dt;
            if (_fallStepTimer > 0f) return;
            _fallStepTimer = FallStepInterval;

            if (_pendingFallChecks.Count == 0) return;

            var batch = new List<(int x, int y, int z)>(_pendingFallChecks);
            _pendingFallChecks.Clear();

            foreach (var (wx, wy, wz) in batch)
            {
                if (wy <= 0) continue;
                var here = GetBlock(wx, wy, wz);
                if (here != BlockType.Sand && here != BlockType.Gravel) continue;

                var below = GetBlock(wx, wy - 1, wz);
                if (below != BlockType.Air) continue;

                // Convert the cell to a flying entity. SetBlock(Air)
                // re-fires the hook, which checks the cell ABOVE this
                // one — so a column of sand naturally peels itself off
                // 100ms at a time as each one's support disappears.
                SetBlock(wx, wy, wz, BlockType.Air);
                _fallingBlocks.Add(new FallingBlockEntity
                {
                    X = wx, Z = wz, Y = wy, VelY = 0f, Type = here,
                });
            }
        }

        // Per-frame integration step for live falling entities.
        // Walks the list back-to-front so the in-place removal of a
        // landed entity doesn't shift indices we're still going to
        // iterate. On landing, SetBlock at the landing Y does the
        // commit AND re-fires the chain — if another sand was queued
        // on top of this column it'll be detected by next tick's
        // TickFallingPhysics through the standard pending-checks
        // path.
        public void UpdateFallingBlocks(float dt)
        {
            if (_fallingBlocks.Count == 0) return;
            for (int i = _fallingBlocks.Count - 1; i >= 0; i--)
            {
                var fb = _fallingBlocks[i];
                if (fb.Update(this, dt) == FallingBlockEntity.StepResult.LandAt)
                {
                    // Commit at the landing Y. If the destination is
                    // already non-air (someone placed a block in the
                    // landing cell while we were mid-fall) we drop
                    // the falling block silently — match Alpha, where
                    // a falling-sand entity that lands inside a solid
                    // is just lost.
                    if (GetBlock(fb.X, fb.LandedY, fb.Z) == BlockType.Air)
                        SetBlock(fb.X, fb.LandedY, fb.Z, fb.Type);
                    _fallingBlocks.RemoveAt(i);
                }
            }
        }

        // Tier 8 #42 — Redstone tick driver. Cadence-rate-limits the
        // RedstonePowerSystem.Tick(world) call so the per-tick BFS
        // only runs at 10 Hz regardless of frame-rate. Player cell
        // is passed in so pressure plates can detect overlap without
        // a generic entity-AABB query.
        private float _redstoneTimer;
        public void TickRedstone(float dt, int playerCellX, int playerCellY, int playerCellZ)
        {
            _redstoneTimer -= dt;
            if (_redstoneTimer > 0f) return;
            _redstoneTimer = RedstonePowerSystem.TickInterval;

            RedstonePowerSystem.SetPlayerCell(playerCellX, playerCellY, playerCellZ);
            RedstonePowerSystem.Tick(this);
        }

        // Tier 8 #47 — Runtime oak growth. Called from TickRandomCrops
        // when a sapling's RNG rolls successful growth. Replaces the
        // sapling cell with a trunk + canopy via SetBlock so each
        // placement triggers light updates + chunk-dirty marks
        // (the chunk-gen-time PlaceOakTree path writes RawBlocks
        // directly because lighting hasn't been computed yet —
        // not safe at runtime). Returns true on success; false if
        // there's not enough vertical clearance.
        public bool TryGrowOakTree(int wx, int wy, int wz, Random rng)
        {
            int trunkHeight = 4 + rng.Next(3);   // 4..6
            int topY = wy + trunkHeight;
            if (topY + 1 >= Chunk.SizeY) return false;

            // Check headroom — the trunk + 2-cell canopy halo above
            // it must all be passable (Air or replaceable).
            for (int y = wy + 1; y <= topY + 1; y++)
            for (int dx = -2; dx <= 2; dx++)
            for (int dz = -2; dz <= 2; dz++)
            {
                if (y > topY && (Math.Abs(dx) > 1 || Math.Abs(dz) > 1)) continue;
                var t = GetBlock(wx + dx, y, wz + dz);
                if (t != BlockType.Air && t != BlockType.Leaves
                    && t != BlockType.Sapling) return false;
            }

            // Trunk — base replaces the sapling itself.
            for (int y = wy; y <= topY; y++)
                SetBlock(wx, y, wz, BlockType.WoodLog);

            // Canopy — top-2 + top-1 layers, 5×5 with corner clip.
            for (int dy = -2; dy <= -1; dy++)
            for (int dx = -2; dx <= 2; dx++)
            for (int dz = -2; dz <= 2; dz++)
            {
                if (Math.Abs(dx) == 2 && Math.Abs(dz) == 2 && rng.Next(2) == 0) continue;
                if (dx == 0 && dz == 0) continue;          // trunk passes through
                int x = wx + dx, y = topY + dy, z = wz + dz;
                if (GetBlock(x, y, z) == BlockType.Air)
                    SetBlock(x, y, z, BlockType.Leaves);
            }
            // Top 3×3 leaf disc around the trunk tip.
            for (int dx = -1; dx <= 1; dx++)
            for (int dz = -1; dz <= 1; dz++)
            {
                if (dx == 0 && dz == 0) continue;
                int x = wx + dx, z = wz + dz;
                if (GetBlock(x, topY, z) == BlockType.Air)
                    SetBlock(x, topY, z, BlockType.Leaves);
            }
            // Plus-sign crown one cell above the trunk top.
            if (GetBlock(wx, topY + 1, wz) == BlockType.Air)
                SetBlock(wx, topY + 1, wz, BlockType.Leaves);
            if (GetBlock(wx + 1, topY + 1, wz) == BlockType.Air)
                SetBlock(wx + 1, topY + 1, wz, BlockType.Leaves);
            if (GetBlock(wx - 1, topY + 1, wz) == BlockType.Air)
                SetBlock(wx - 1, topY + 1, wz, BlockType.Leaves);
            if (GetBlock(wx, topY + 1, wz + 1) == BlockType.Air)
                SetBlock(wx, topY + 1, wz + 1, BlockType.Leaves);
            if (GetBlock(wx, topY + 1, wz - 1) == BlockType.Air)
                SetBlock(wx, topY + 1, wz - 1, BlockType.Leaves);

            return true;
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
                // Tier 6 #32 — Dungeon gen between terrain + light.
                // Both this synchronous path and the async streaming
                // path now mutate `_chestEntities` (a ConcurrentDictionary
                // since the chunk-streaming smoothness work moved
                // dungeon gen onto chunk worker threads), so the
                // chest-entity registration is thread-safe regardless
                // of which thread we're on here.
                GenerateDungeonsInChunk(c);
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
            // Tier 8 #42 perf — Populate the redstone registry for
            // any in-cache redstone blocks (player edits that
            // survived an unload, or chunks loaded from a save).
            // Cheap on chunks with no redstone (one byte compare
            // per cell, no allocation).
            RedstonePowerSystem.RegisterChunk(chunk);
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

        // Phase 3 — `record` distinguishes server-authored mutations
        // (default true; appends to the change journal so ServerHub can
        // broadcast) from client-authored mutations applying an inbound
        // BlockChange (false; the change came FROM the server, no need
        // to echo it back). Existing call-sites all keep the default,
        // matching the prior single-arg signature.
        public bool SetBlock(int wx, int wy, int wz, BlockType t, bool record = true)
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
            if (record)
            {
                // Sample the cell's META alongside its new TYPE so the
                // wire packet has both. For most edits Meta is 0; door
                // halves at placement time stamp facing+open into Meta
                // before SetBlock returns, so reading post-Set is the
                // right moment to capture the final value.
                byte meta = c.GetMeta(lx, wy, lz);
                _pendingBlockChanges.Add(new BlockChangeRecord(wx, wy, wz, t, meta));
            }

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

            // Tier 8 #42 perf — Maintain the redstone power system's
            // sparse cell registry incrementally. Without this hook
            // the per-tick simulation falls back to a full-world
            // walk, which crashed the game tick to ~4 fps for users
            // with no redstone placed at all.
            RedstonePowerSystem.OnBlockChanged(wx, wy, wz, oldT, t);

            // Tier 6 #35 — Falling-physics enqueue. Two cases trigger
            // a fall check: (a) the new block IS sand/gravel and might
            // need to fall if its support is missing; (b) the new
            // block opens up a cell beneath an existing sand/gravel,
            // which now needs to fall. Case (b) covers digging out
            // the support pillar from under a sand column. Case (a)
            // covers placing sand mid-air or restoring a save.
            if (t == BlockType.Sand || t == BlockType.Gravel)
                _pendingFallChecks.Add((wx, wy, wz));
            if (t == BlockType.Air && wy + 1 < Chunk.SizeY)
            {
                var above = (BlockType)c.RawBlocks[Chunk.Index(lx, wy + 1, lz)];
                if (above == BlockType.Sand || above == BlockType.Gravel)
                    _pendingFallChecks.Add((wx, wy + 1, wz));
            }
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
        // contents as drops. ConcurrentDictionary's TryRemove returns
        // the removed value atomically — no double-lookup needed.
        public ChestTileEntity RemoveChestEntity(int wx, int wy, int wz)
        {
            _chestEntities.TryRemove((wx, wy, wz), out var ce);
            return ce;
        }

        // Iterate all (coord, entity) pairs — used by save/load.
        public IEnumerable<KeyValuePair<(int x, int y, int z), ChestTileEntity>> ChestEntities
            => _chestEntities;

        // ---- Jukebox tile entities ----

        // Get-or-create a JukeboxTileEntity at (wx, wy, wz). Caller has
        // already verified the block at the position is a Jukebox; the
        // method just hands back (or installs) the persistent slot.
        // Used by the insert path when the player slots a disc into
        // a previously-untouched jukebox.
        public JukeboxTileEntity GetOrCreateJukeboxEntity(int wx, int wy, int wz)
        {
            var key = (wx, wy, wz);
            if (!_jukeboxEntities.TryGetValue(key, out var je))
            {
                je = new JukeboxTileEntity();
                _jukeboxEntities[key] = je;
            }
            return je;
        }

        // Look up a JukeboxTileEntity without creating one. Returns null
        // if no entity exists for the coordinate (e.g. a freshly-placed
        // jukebox with no disc inserted yet doesn't allocate one until
        // interact). Callers that just want to know "is there a disc
        // loaded here?" can null-check this.
        public JukeboxTileEntity TryGetJukeboxEntity(int wx, int wy, int wz)
        {
            _jukeboxEntities.TryGetValue((wx, wy, wz), out var je);
            return je;
        }

        // Remove the entity at the coordinate and return it (or null).
        // Used when the jukebox block is broken so the caller can drop
        // the inserted disc as a DroppedItem (eject-on-break — Alpha
        // behaviour: breaking a jukebox with a disc loaded ejects the
        // disc, doesn't destroy it).
        public JukeboxTileEntity RemoveJukeboxEntity(int wx, int wy, int wz)
        {
            var key = (wx, wy, wz);
            if (_jukeboxEntities.TryGetValue(key, out var je))
            {
                _jukeboxEntities.Remove(key);
                return je;
            }
            return null;
        }

        // Iterate all (coord, entity) pairs — used by save/load (v11+).
        public IEnumerable<KeyValuePair<(int x, int y, int z), JukeboxTileEntity>> JukeboxEntities
            => _jukeboxEntities;
    }
}
