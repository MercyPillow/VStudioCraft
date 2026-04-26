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
            }
            return w;
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
