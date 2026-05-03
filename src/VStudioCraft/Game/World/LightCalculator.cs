using System;
using System.Collections.Generic;

namespace VStudioCraft.Game
{
    // Per-chunk lighting pass — Alpha-style 15-level flood fill for sky and block
    // light. Runs in two phases:
    //
    //   1. Sky light: every column's cells above the topmost light-blocker get
    //      level 15. Then a BFS spreads laterally, dropping 1 per step.
    //   2. Block light: emitter cells (lava and torches today) seed at their
    //      emission strength. Same BFS spreads outward.
    //
    // Two flavours:
    //   * RecomputeChunk(chunk)   — original single-chunk pass. Used during
    //     world generation and when a chunk is freshly streamed in. Border
    //     seams to neighbours aren't resolved here; that's an accepted gen-
    //     time limitation (matches Alpha's own gen seams).
    //   * RecomputeRegion(world, cx, cz) — 3×3 chunk pass with a unified BFS
    //     that crosses chunk borders. Used after the player edits a block, so
    //     a torch placed mid-chunk lights its neighbour chunks too instead of
    //     stopping abruptly at the seam.
    internal static class LightCalculator
    {
        // Pack/unpack a chunk-local position into a single int. Layout:
        //   bits  0..3   z   (4 bits, 0..15)
        //   bits  4..10  y   (7 bits, 0..127)
        //   bits 11..14  x   (4 bits, 0..15)
        // 15 bits total — easily fits in int with room to spare.
        private static int Pack(int x, int y, int z) => (x << 11) | (y << 4) | z;
        private static int UnpackX(int p) => (p >> 11) & 0xF;
        private static int UnpackY(int p) => (p >> 4) & 0x7F;
        private static int UnpackZ(int p) => p & 0xF;

        // Region pack — same as Pack but with a 4-bit chunk index in bits 15..18
        // identifying which of the up-to-9 chunks in the relight region the
        // position belongs to. Caller resolves the index → Chunk via the region's
        // chunk array.
        private static int PackR(int cIdx, int x, int y, int z) =>
            (cIdx << 15) | (x << 11) | (y << 4) | z;
        private static int UnpackRIdx(int p) => (p >> 15) & 0xF;

        // BFS scratch — per-thread reuse via [ThreadStatic] so worker threads
        // don't allocate every Recompute call. The chunk-job-system pool runs
        // one thread per worker; sharing across calls within a thread is safe.
        [ThreadStatic] private static Queue<int> _queue;

        public static void RecomputeChunk(Chunk chunk)
        {
            var light = chunk.RawLight;
            Array.Clear(light, 0, light.Length);

            var queue = _queue ?? (_queue = new Queue<int>(1024));
            queue.Clear();

            // --- Sky light: top-down seed ---
            // For each column, walk down setting level 15 until we hit something
            // that blocks light. Every seeded cell goes into the BFS frontier so
            // it can spread sideways into shaded areas (e.g. under overhangs).
            for (int x = 0; x < Chunk.SizeX; x++)
            for (int z = 0; z < Chunk.SizeZ; z++)
            {
                for (int y = Chunk.SizeY - 1; y >= 0; y--)
                {
                    var t = chunk.Get(x, y, z);
                    if (!BlockData.IsLightTransparent(t)) break;
                    chunk.SetSkyLight(x, y, z, 15);
                    queue.Enqueue(Pack(x, y, z));
                }
            }

            FloodFill(chunk, queue, isSky: true);

            // --- Block light: seed from emitters ---
            for (int x = 0; x < Chunk.SizeX; x++)
            for (int y = 0; y < Chunk.SizeY; y++)
            for (int z = 0; z < Chunk.SizeZ; z++)
            {
                var t = chunk.Get(x, y, z);
                int e = BlockData.LightEmission(t);
                if (e > 0)
                {
                    chunk.SetBlockLight(x, y, z, (byte)e);
                    queue.Enqueue(Pack(x, y, z));
                }
            }

            FloodFill(chunk, queue, isSky: false);
        }

        private static void FloodFill(Chunk chunk, Queue<int> queue, bool isSky)
        {
            while (queue.Count > 0)
            {
                int p = queue.Dequeue();
                int x = UnpackX(p);
                int y = UnpackY(p);
                int z = UnpackZ(p);
                int lvl = isSky ? chunk.GetSkyLight(x, y, z) : chunk.GetBlockLight(x, y, z);
                int next = lvl - 1;
                if (next <= 0) continue;

                TrySpread(chunk, x - 1, y, z, next, queue, isSky);
                TrySpread(chunk, x + 1, y, z, next, queue, isSky);
                TrySpread(chunk, x, y - 1, z, next, queue, isSky);
                TrySpread(chunk, x, y + 1, z, next, queue, isSky);
                TrySpread(chunk, x, y, z - 1, next, queue, isSky);
                TrySpread(chunk, x, y, z + 1, next, queue, isSky);
            }
        }

        private static void TrySpread(Chunk chunk, int x, int y, int z, int level,
            Queue<int> queue, bool isSky)
        {
            if ((uint)x >= Chunk.SizeX || (uint)y >= Chunk.SizeY || (uint)z >= Chunk.SizeZ)
                return;
            var t = chunk.Get(x, y, z);
            if (!BlockData.IsLightTransparent(t)) return;
            int existing = isSky ? chunk.GetSkyLight(x, y, z) : chunk.GetBlockLight(x, y, z);
            if (existing >= level) return;
            if (isSky) chunk.SetSkyLight(x, y, z, (byte)level);
            else chunk.SetBlockLight(x, y, z, (byte)level);
            queue.Enqueue(Pack(x, y, z));
        }

        // ---- Region recompute (3×3 chunk neighbourhood) -------------------
        //
        // The single-chunk path produces visible seams the moment a torch is
        // placed mid-chunk: only the source chunk's light field is updated, so
        // the adjacent chunk stays dark on its near face. This pass relights a
        // (2*radius+1)² grid of chunks centered on the edit chunk in one
        // unified BFS so light from any emitter in the region reaches every
        // cell it can within the region's bounds.
        //
        // Radius=1 (3×3) is enough for any block-light source today: the max
        // emission level is 15 and chunks are 16 wide, so even a torch at a
        // chunk corner can't reach beyond the immediate ring of neighbours.
        //
        // Cost: roughly 9× a single-chunk relight. Edits are rare (player-
        // driven), so the absolute cost is fine. Streaming-time gen still uses
        // RecomputeChunk to keep terrain ingest cheap; mild gen-time seams
        // resolve the first time an edit lands nearby.
        //
        // Returns the list of (cx, cz) keys that were touched so the caller
        // can mark them all dirty for re-meshing.
        public static IReadOnlyList<(int cx, int cz)> RecomputeRegion(World world, int centerCx, int centerCz)
        {
            const int Radius = 1;
            const int Side = 2 * Radius + 1;          // 3
            const int Total = Side * Side;            // 9

            var chunks = new Chunk[Total];
            var keys = new List<(int cx, int cz)>(Total);
            for (int dz = -Radius; dz <= Radius; dz++)
            for (int dx = -Radius; dx <= Radius; dx++)
            {
                int idx = (dz + Radius) * Side + (dx + Radius);
                int cx = centerCx + dx;
                int cz = centerCz + dz;
                var c = world.GetChunk(cx, cz);
                chunks[idx] = c;
                if (c != null) keys.Add((cx, cz));
            }

            // Clear lights in present chunks. Missing chunks (region edge that
            // hasn't streamed in) just stay null — the BFS treats them as
            // out-of-region and stops at their boundary.
            for (int i = 0; i < Total; i++)
            {
                var c = chunks[i];
                if (c == null) continue;
                Array.Clear(c.RawLight, 0, c.RawLight.Length);
            }

            var queue = _queue ?? (_queue = new Queue<int>(1024));
            queue.Clear();

            // --- Sky light seed: top-down per column, in every present chunk ---
            for (int i = 0; i < Total; i++)
            {
                var c = chunks[i];
                if (c == null) continue;
                for (int x = 0; x < Chunk.SizeX; x++)
                for (int z = 0; z < Chunk.SizeZ; z++)
                {
                    for (int y = Chunk.SizeY - 1; y >= 0; y--)
                    {
                        var t = c.Get(x, y, z);
                        if (!BlockData.IsLightTransparent(t)) break;
                        c.SetSkyLight(x, y, z, 15);
                        queue.Enqueue(PackR(i, x, y, z));
                    }
                }
            }

            FloodFillRegion(chunks, Side, Radius, queue, isSky: true);

            // --- Block light seed: every emitter in every present chunk ---
            for (int i = 0; i < Total; i++)
            {
                var c = chunks[i];
                if (c == null) continue;
                for (int x = 0; x < Chunk.SizeX; x++)
                for (int y = 0; y < Chunk.SizeY; y++)
                for (int z = 0; z < Chunk.SizeZ; z++)
                {
                    var t = c.Get(x, y, z);
                    int e = BlockData.LightEmission(t);
                    if (e > 0)
                    {
                        c.SetBlockLight(x, y, z, (byte)e);
                        queue.Enqueue(PackR(i, x, y, z));
                    }
                }
            }

            FloodFillRegion(chunks, Side, Radius, queue, isSky: false);
            return keys;
        }

        private static void FloodFillRegion(Chunk[] chunks, int side, int radius,
            Queue<int> queue, bool isSky)
        {
            while (queue.Count > 0)
            {
                int p = queue.Dequeue();
                int cIdx = UnpackRIdx(p);
                int x = UnpackX(p);
                int y = UnpackY(p);
                int z = UnpackZ(p);
                var c = chunks[cIdx];
                int lvl = isSky ? c.GetSkyLight(x, y, z) : c.GetBlockLight(x, y, z);
                int next = lvl - 1;
                if (next <= 0) continue;

                TrySpreadRegion(chunks, side, radius, cIdx, x - 1, y, z, next, queue, isSky);
                TrySpreadRegion(chunks, side, radius, cIdx, x + 1, y, z, next, queue, isSky);
                TrySpreadRegion(chunks, side, radius, cIdx, x, y - 1, z, next, queue, isSky);
                TrySpreadRegion(chunks, side, radius, cIdx, x, y + 1, z, next, queue, isSky);
                TrySpreadRegion(chunks, side, radius, cIdx, x, y, z - 1, next, queue, isSky);
                TrySpreadRegion(chunks, side, radius, cIdx, x, y, z + 1, next, queue, isSky);
            }
        }

        // Spread a level into (x,y,z) where x/z may be outside [0..SizeX/Z).
        // The negative/over-bound case crosses a chunk boundary; we hop into
        // the neighbour-region chunk if it's in the relight grid, otherwise
        // we drop the spread (acts as the region boundary).
        private static void TrySpreadRegion(Chunk[] chunks, int side, int radius,
            int curIdx, int x, int y, int z, int level, Queue<int> queue, bool isSky)
        {
            if ((uint)y >= Chunk.SizeY) return;

            int dcx = 0, dcz = 0;
            if (x < 0) { dcx = -1; x += Chunk.SizeX; }
            else if (x >= Chunk.SizeX) { dcx = 1; x -= Chunk.SizeX; }
            if (z < 0) { dcz = -1; z += Chunk.SizeZ; }
            else if (z >= Chunk.SizeZ) { dcz = 1; z -= Chunk.SizeZ; }

            int curDx = (curIdx % side) - radius;
            int curDz = (curIdx / side) - radius;
            int newDx = curDx + dcx, newDz = curDz + dcz;
            if (newDx < -radius || newDx > radius || newDz < -radius || newDz > radius) return;
            int newIdx = (newDz + radius) * side + (newDx + radius);
            var c = chunks[newIdx];
            if (c == null) return;

            var t = c.Get(x, y, z);
            if (!BlockData.IsLightTransparent(t)) return;
            int existing = isSky ? c.GetSkyLight(x, y, z) : c.GetBlockLight(x, y, z);
            if (existing >= level) return;
            if (isSky) c.SetSkyLight(x, y, z, (byte)level);
            else c.SetBlockLight(x, y, z, (byte)level);
            queue.Enqueue(PackR(newIdx, x, y, z));
        }

        // ---- Cross-chunk seam propagation -------------------------------
        //
        // After a parallel per-chunk RecomputeChunk pass (the world-load
        // path), each chunk has correct light values from its OWN
        // emitters and sky exposure but ZERO light bleeding in from
        // neighbour chunks. A torch placed near a chunk seam doesn't
        // light the neighbour's near face; sky-light next to an
        // overhang on the seam doesn't fan out into the neighbour's
        // shaded cells.
        //
        // This pass fixes those seams in a single cross-chunk BFS:
        //   1. Seed the queue with every chunk-edge cell that has
        //      positive light (sky pass first, then block pass).
        //   2. Flood-fill across chunk boundaries via world.GetChunk,
        //      writing into neighbour chunks where the new level is
        //      strictly higher than what's already there.
        //
        // Cost is dominated by the seed scan, which is 4 × SizeY ×
        // SizeX cells per chunk = ~8 K reads per chunk; on a 169-chunk
        // world that's ~1.4 M cell reads + the BFS itself. Runs once
        // per world load (or per generated-but-unlit batch) — not on
        // a hot path.
        public static void PropagateAcrossSeams(World world)
        {
            var queue = new Queue<long>(8192);

            // === Sky-light pass ===
            foreach (var chunk in world.Chunks)
                SeedEdgesForSeamPass(chunk, queue, isSky: true);
            FloodAcrossSeams(world, queue, isSky: true);

            // === Block-light pass ===
            queue.Clear();
            foreach (var chunk in world.Chunks)
                SeedEdgesForSeamPass(chunk, queue, isSky: false);
            FloodAcrossSeams(world, queue, isSky: false);
        }

        // Targeted version for the chunk-streaming path. When a freshly-
        // generated chunk is installed beside one or more existing
        // chunks, the only seams that could have new light to
        // propagate are the four pairings (new ↔ each cardinal
        // neighbour). Seeding those pairs only — instead of every
        // chunk-edge in the world — keeps the per-install cost bounded
        // (~5 chunks of edge cells + the BFS) so the streaming budget
        // doesn't balloon when the player crosses into a fresh ring.
        public static void PropagateSeamsForChunkInstall(World world, Chunk newChunk)
        {
            if (newChunk == null) return;
            var queue = new Queue<long>(2048);

            // === Sky-light pass ===
            SeedInstallSeams(world, newChunk, queue, isSky: true);
            FloodAcrossSeams(world, queue, isSky: true);

            // === Block-light pass ===
            queue.Clear();
            SeedInstallSeams(world, newChunk, queue, isSky: false);
            FloodAcrossSeams(world, queue, isSky: false);
        }

        private static void SeedInstallSeams(World world, Chunk newChunk, Queue<long> queue, bool isSky)
        {
            // The new chunk has its single-chunk lighting from
            // RecomputeChunk. Seed all 4 of its edges so its light
            // can spill OUT into neighbour chunks.
            SeedEdgesForSeamPass(newChunk, queue, isSky);

            // Each existing neighbour also has potentially-higher
            // light at the edge facing the new chunk; seed those
            // mirror edges so light flows IN to the new chunk too.
            var west  = world.GetChunk(newChunk.ChunkX - 1, newChunk.ChunkZ);
            var east  = world.GetChunk(newChunk.ChunkX + 1, newChunk.ChunkZ);
            var north = world.GetChunk(newChunk.ChunkX,     newChunk.ChunkZ - 1);
            var south = world.GetChunk(newChunk.ChunkX,     newChunk.ChunkZ + 1);

            if (west  != null) SeedEdgeX(west,  Chunk.SizeX - 1, queue, isSky);
            if (east  != null) SeedEdgeX(east,  0,               queue, isSky);
            if (north != null) SeedEdgeZ(north, Chunk.SizeZ - 1, queue, isSky);
            if (south != null) SeedEdgeZ(south, 0,               queue, isSky);
        }

        private static void SeedEdgeX(Chunk chunk, int x, Queue<long> queue, bool isSky)
        {
            int baseX = chunk.ChunkX * Chunk.SizeX;
            int baseZ = chunk.ChunkZ * Chunk.SizeZ;
            for (int y = 0; y < Chunk.SizeY; y++)
            for (int z = 0; z < Chunk.SizeZ; z++)
            {
                int l = isSky ? chunk.GetSkyLight(x, y, z) : chunk.GetBlockLight(x, y, z);
                if (l > 1) queue.Enqueue(PackWorld(baseX + x, y, baseZ + z));
            }
        }

        private static void SeedEdgeZ(Chunk chunk, int z, Queue<long> queue, bool isSky)
        {
            int baseX = chunk.ChunkX * Chunk.SizeX;
            int baseZ = chunk.ChunkZ * Chunk.SizeZ;
            for (int y = 0; y < Chunk.SizeY; y++)
            for (int x = 0; x < Chunk.SizeX; x++)
            {
                int l = isSky ? chunk.GetSkyLight(x, y, z) : chunk.GetBlockLight(x, y, z);
                if (l > 1) queue.Enqueue(PackWorld(baseX + x, y, baseZ + z));
            }
        }

        // Pack a world-space cell coord into a 64-bit key. We expect
        // chunk coords to fit within ±2^19 (-524288..524287 worlds —
        // 8 million blocks per axis), well outside any reasonable
        // play range. wy fits in 7 bits (0..127). 21+7+21 = 49 bits.
        private static long PackWorld(int wx, int wy, int wz)
            => ((long)(wx & 0x1FFFFF))
             | ((long)(wy & 0x7F) << 21)
             | ((long)(wz & 0x1FFFFF) << 28);
        private static int UnpackWX(long p) => SignExtend21((int)(p & 0x1FFFFF));
        private static int UnpackWY(long p) => (int)((p >> 21) & 0x7F);
        private static int UnpackWZ(long p) => SignExtend21((int)((p >> 28) & 0x1FFFFF));
        private static int SignExtend21(int v) => (v & 0x100000) != 0 ? v | unchecked((int)0xFFE00000) : v;

        private static void SeedEdgesForSeamPass(Chunk chunk, Queue<long> queue, bool isSky)
        {
            int baseX = chunk.ChunkX * Chunk.SizeX;
            int baseZ = chunk.ChunkZ * Chunk.SizeZ;
            for (int y = 0; y < Chunk.SizeY; y++)
            {
                for (int z = 0; z < Chunk.SizeZ; z++)
                {
                    int lLo = isSky ? chunk.GetSkyLight(0, y, z) : chunk.GetBlockLight(0, y, z);
                    if (lLo > 1) queue.Enqueue(PackWorld(baseX, y, baseZ + z));
                    int lHi = isSky ? chunk.GetSkyLight(Chunk.SizeX - 1, y, z) : chunk.GetBlockLight(Chunk.SizeX - 1, y, z);
                    if (lHi > 1) queue.Enqueue(PackWorld(baseX + Chunk.SizeX - 1, y, baseZ + z));
                }
                for (int x = 0; x < Chunk.SizeX; x++)
                {
                    int lLo = isSky ? chunk.GetSkyLight(x, y, 0) : chunk.GetBlockLight(x, y, 0);
                    if (lLo > 1) queue.Enqueue(PackWorld(baseX + x, y, baseZ));
                    int lHi = isSky ? chunk.GetSkyLight(x, y, Chunk.SizeZ - 1) : chunk.GetBlockLight(x, y, Chunk.SizeZ - 1);
                    if (lHi > 1) queue.Enqueue(PackWorld(baseX + x, y, baseZ + Chunk.SizeZ - 1));
                }
            }
        }

        private static void FloodAcrossSeams(World world, Queue<long> queue, bool isSky)
        {
            while (queue.Count > 0)
            {
                long packed = queue.Dequeue();
                int wx = UnpackWX(packed);
                int wy = UnpackWY(packed);
                int wz = UnpackWZ(packed);
                int level = WorldGetLight(world, wx, wy, wz, isSky);
                int next = level - 1;
                if (next <= 0) continue;
                TrySpreadWorld(world, wx - 1, wy,     wz,     next, queue, isSky);
                TrySpreadWorld(world, wx + 1, wy,     wz,     next, queue, isSky);
                TrySpreadWorld(world, wx,     wy - 1, wz,     next, queue, isSky);
                TrySpreadWorld(world, wx,     wy + 1, wz,     next, queue, isSky);
                TrySpreadWorld(world, wx,     wy,     wz - 1, next, queue, isSky);
                TrySpreadWorld(world, wx,     wy,     wz + 1, next, queue, isSky);
            }
        }

        private static int WorldGetLight(World world, int wx, int wy, int wz, bool isSky)
        {
            if ((uint)wy >= Chunk.SizeY) return 0;
            int cx = (int)Math.Floor(wx / (float)Chunk.SizeX);
            int cz = (int)Math.Floor(wz / (float)Chunk.SizeZ);
            int lx = wx - cx * Chunk.SizeX;
            int lz = wz - cz * Chunk.SizeZ;
            var c = world.GetChunk(cx, cz);
            if (c == null) return 0;
            return isSky ? c.GetSkyLight(lx, wy, lz) : c.GetBlockLight(lx, wy, lz);
        }

        private static void TrySpreadWorld(World world, int wx, int wy, int wz, int level, Queue<long> queue, bool isSky)
        {
            if ((uint)wy >= Chunk.SizeY) return;
            int cx = (int)Math.Floor(wx / (float)Chunk.SizeX);
            int cz = (int)Math.Floor(wz / (float)Chunk.SizeZ);
            int lx = wx - cx * Chunk.SizeX;
            int lz = wz - cz * Chunk.SizeZ;
            var c = world.GetChunk(cx, cz);
            if (c == null) return;
            var t = c.Get(lx, wy, lz);
            if (!BlockData.IsLightTransparent(t)) return;
            int existing = isSky ? c.GetSkyLight(lx, wy, lz) : c.GetBlockLight(lx, wy, lz);
            if (existing >= level) return;
            if (isSky) c.SetSkyLight(lx, wy, lz, (byte)level);
            else c.SetBlockLight(lx, wy, lz, (byte)level);
            queue.Enqueue(PackWorld(wx, wy, wz));
        }

        // ---- Incremental update after a single block edit ----------------
        //
        // The 3×3 RecomputeRegion path is correct but synchronous-on-render-
        // thread-expensive: clearing 9 chunks' light arrays and reflooding
        // sky+block light from scratch costs ~5-15 ms per click. That stutter
        // is what the player feels when placing torches. Most edits change
        // very few light cells, so we run an incremental BFS instead — the
        // standard "remove then add" algorithm — that only touches cells
        // whose light value actually has to change.
        //
        // Returns the set of chunks whose light field was modified, so the
        // caller can mark just those for re-meshing.
        public static IReadOnlyCollection<(int cx, int cz)> UpdateAfterEdit(
            World world, int wx, int wy, int wz, BlockType oldT, BlockType newT)
        {
            var dirty = new HashSet<(int cx, int cz)>();

            bool oldTrans = BlockData.IsLightTransparent(oldT);
            bool newTrans = BlockData.IsLightTransparent(newT);
            int oldEmit = BlockData.LightEmission(oldT);
            int newEmit = BlockData.LightEmission(newT);

            // Common no-op case: replacing one opaque non-emitter with another
            // (dirt → stone) or one transparent non-emitter with another (water →
            // flowing water). Light field unchanged.
            if (oldTrans == newTrans && oldEmit == newEmit) return dirty;

            // Block light pass — always runs on emission OR opacity change since
            // both can affect block-light propagation through the cell.
            UpdateBlockLightAtEdit(world, wx, wy, wz, oldEmit, newEmit, newTrans, dirty);

            // Sky light pass — only opacity changes affect sky light.
            if (oldTrans != newTrans)
                UpdateSkyLightAtEdit(world, wx, wy, wz, newTrans, dirty);

            return dirty;
        }

        // Block-light incremental update at a single edit cell.
        //   1. Capture the cell's current block light, then zero it.
        //   2. Remove-BFS at level=oldLevel, cascading darkening to neighbours
        //      that were lit by us. Brighter neighbours (independent sources)
        //      go into addQ for re-flood.
        //   3. If the new block emits, seed the cell at its emission and add
        //      to addQ.
        //   4. If the cell BECAME transparent (opaque block removed), also
        //      seed every bright neighbour into addQ so their light can
        //      flow into the new air space. Without this seed the new air
        //      cell stays at blockLight=0 even when surrounded by torch-
        //      lit neighbours — was the source of "broken block stays
        //      dark in a lit corridor" bug. Mirrors the equivalent
        //      seeding path on the sky-light side.
        //   5. Add-BFS to re-flood from independent sources, the new
        //      emitter (if any), and the bright-neighbour seeds.
        private static void UpdateBlockLightAtEdit(World world, int wx, int wy, int wz,
            int oldEmit, int newEmit, bool newTrans, HashSet<(int cx, int cz)> dirty)
        {
            var removeQ = new Queue<(int x, int y, int z, int oldLevel)>();
            var addQ = new Queue<(int x, int y, int z)>();

            int currentLight = GetBlockLightW(world, wx, wy, wz);

            // Always reset the edit cell's block light. It's invalid: either
            // the cell is now opaque (no light), or transparent and needs to
            // be re-derived from neighbours / new emitter.
            SetBlockLightW(world, wx, wy, wz, 0, dirty);

            if (currentLight > 0)
                removeQ.Enqueue((wx, wy, wz, currentLight));

            ProcessRemoveBlock(world, removeQ, addQ, dirty);

            // Seed a new emitter if the new block luminates. Note we do
            // NOT gate on `newTrans` here — opaque emitters (Glowstone,
            // Jack-o-lantern, LitFurnace) seed their own cell's block-
            // light at full emission level, then the BFS dequeues the
            // cell and TrySpread carries the (level-1) value to each
            // light-transparent neighbour. The destination's opacity
            // is what gates spread, not the source's. The earlier
            // `&& newTrans` gate caused glowstone-on-place to silently
            // never light up because the block isn't IsLightTransparent;
            // loading a world via RecomputeChunk worked because that
            // path seeds without an opacity check.
            if (newEmit > 0)
            {
                SetBlockLightW(world, wx, wy, wz, (byte)newEmit, dirty);
                addQ.Enqueue((wx, wy, wz));
            }

            // If the cell just became transparent, seed neighbour light
            // sources so block-light flows into the new air space.
            // GetBlockLightW > 1 ensures the neighbour has enough headroom
            // (light - 1) to actually propagate into us.
            if (newTrans)
            {
                TryEnqueueAddSeedBlock(world, wx - 1, wy, wz, addQ);
                TryEnqueueAddSeedBlock(world, wx + 1, wy, wz, addQ);
                TryEnqueueAddSeedBlock(world, wx, wy - 1, wz, addQ);
                TryEnqueueAddSeedBlock(world, wx, wy + 1, wz, addQ);
                TryEnqueueAddSeedBlock(world, wx, wy, wz - 1, addQ);
                TryEnqueueAddSeedBlock(world, wx, wy, wz + 1, addQ);
            }

            ProcessAddBlock(world, addQ, dirty);
        }

        private static void TryEnqueueAddSeedBlock(World w, int wx, int wy, int wz, Queue<(int, int, int)> addQ)
        {
            if (wy < 0 || wy >= Chunk.SizeY) return;
            if (GetBlockLightW(w, wx, wy, wz) > 1) addQ.Enqueue((wx, wy, wz));
        }

        // Sky-light incremental update at a single edit cell.
        //   - Placed opaque (newTrans=false): cell loses its sky-light, and any
        //     directly-sky-lit cells in the column below also fall into shadow.
        //     Walk down the column zeroing direct-sky cells and feeding them
        //     into the remove BFS.
        //   - Removed opaque (newTrans=true): cell can now receive sky. If the
        //     column above is fully transparent, the cell and any shadowed
        //     direct-sky cells below become sky=15. Otherwise the cell is just
        //     a new transparent slot waiting for lateral spread.
        private static void UpdateSkyLightAtEdit(World world, int wx, int wy, int wz,
            bool newTrans, HashSet<(int cx, int cz)> dirty)
        {
            var removeQ = new Queue<(int x, int y, int z, int oldLevel)>();
            var addQ = new Queue<(int x, int y, int z)>();

            if (!newTrans)
            {
                // Just placed an opaque block. The edit cell and any direct-sky
                // cells below it lose their sky-light.
                int oldSky = GetSkyLightW(world, wx, wy, wz);
                if (oldSky > 0)
                {
                    SetSkyLightW(world, wx, wy, wz, 0, dirty);
                    removeQ.Enqueue((wx, wy, wz, oldSky));
                }
                // Walk down the column. Sky-light==15 implies direct sky access
                // (lateral spread caps at 14), so any 15 cell below us was lit
                // by the same column we just blocked. Stop at the first opaque
                // cell or the first non-15 transparent cell (its level came
                // from lateral spread elsewhere — unaffected by our edit).
                for (int y = wy - 1; y >= 0; y--)
                {
                    if (!IsCellLightTransparent(world, wx, y, wz)) break;
                    int sky = GetSkyLightW(world, wx, y, wz);
                    if (sky == 15)
                    {
                        SetSkyLightW(world, wx, y, wz, 0, dirty);
                        removeQ.Enqueue((wx, y, wz, 15));
                    }
                    else break;
                }
            }
            else
            {
                // Just removed an opaque block — the edit cell becomes a
                // transparent slot. Determine if it has direct sky access by
                // walking up the column.
                bool hasDirectSky = true;
                for (int y = wy + 1; y < Chunk.SizeY; y++)
                {
                    if (!IsCellLightTransparent(world, wx, y, wz)) { hasDirectSky = false; break; }
                }

                if (hasDirectSky)
                {
                    // Cell and any transparent cells directly below now get
                    // direct sky=15. Walk down setting & enqueuing.
                    SetSkyLightW(world, wx, wy, wz, 15, dirty);
                    addQ.Enqueue((wx, wy, wz));
                    for (int y = wy - 1; y >= 0; y--)
                    {
                        if (!IsCellLightTransparent(world, wx, y, wz)) break;
                        int sky = GetSkyLightW(world, wx, y, wz);
                        if (sky < 15)
                        {
                            SetSkyLightW(world, wx, y, wz, 15, dirty);
                            addQ.Enqueue((wx, y, wz));
                        }
                        else break;
                    }
                }
                else
                {
                    // No direct sky overhead. Cell starts at sky=0 but can
                    // receive lateral light. Seed bright neighbours into addQ
                    // so they re-propagate through the new opening.
                    TryEnqueueAddSeed(world, wx - 1, wy, wz, addQ);
                    TryEnqueueAddSeed(world, wx + 1, wy, wz, addQ);
                    TryEnqueueAddSeed(world, wx, wy - 1, wz, addQ);
                    TryEnqueueAddSeed(world, wx, wy + 1, wz, addQ);
                    TryEnqueueAddSeed(world, wx, wy, wz - 1, addQ);
                    TryEnqueueAddSeed(world, wx, wy, wz + 1, addQ);
                }
            }

            ProcessRemoveSky(world, removeQ, addQ, dirty);
            ProcessAddSky(world, addQ, dirty);
        }

        private static void TryEnqueueAddSeed(World w, int wx, int wy, int wz, Queue<(int, int, int)> addQ)
        {
            if (wy < 0 || wy >= Chunk.SizeY) return;
            if (GetSkyLightW(w, wx, wy, wz) > 1) addQ.Enqueue((wx, wy, wz));
        }

        // Standard "remove" BFS — for each neighbour:
        //   * neighbour < parentLevel  → it was lit by us; cascade-darken,
        //     enqueue at its old level.
        //   * neighbour ≥ parentLevel  → it's an independent source (or fed by
        //     one); enqueue into addQ so re-flood propagates from it.
        private static void ProcessRemoveBlock(World world,
            Queue<(int x, int y, int z, int oldLevel)> removeQ,
            Queue<(int x, int y, int z)> addQ,
            HashSet<(int cx, int cz)> dirty)
        {
            while (removeQ.Count > 0)
            {
                var p = removeQ.Dequeue();
                int x = p.x, y = p.y, z = p.z, lvl = p.oldLevel;
                TryRemoveNeighbourBlock(world, x - 1, y, z, lvl, removeQ, addQ, dirty);
                TryRemoveNeighbourBlock(world, x + 1, y, z, lvl, removeQ, addQ, dirty);
                TryRemoveNeighbourBlock(world, x, y - 1, z, lvl, removeQ, addQ, dirty);
                TryRemoveNeighbourBlock(world, x, y + 1, z, lvl, removeQ, addQ, dirty);
                TryRemoveNeighbourBlock(world, x, y, z - 1, lvl, removeQ, addQ, dirty);
                TryRemoveNeighbourBlock(world, x, y, z + 1, lvl, removeQ, addQ, dirty);
            }
        }

        private static void TryRemoveNeighbourBlock(World world, int wx, int wy, int wz,
            int parentLevel,
            Queue<(int x, int y, int z, int oldLevel)> removeQ,
            Queue<(int x, int y, int z)> addQ,
            HashSet<(int cx, int cz)> dirty)
        {
            if (wy < 0 || wy >= Chunk.SizeY) return;
            int neighbourLight = GetBlockLightW(world, wx, wy, wz);
            if (neighbourLight == 0) return;
            if (neighbourLight < parentLevel)
            {
                SetBlockLightW(world, wx, wy, wz, 0, dirty);
                removeQ.Enqueue((wx, wy, wz, neighbourLight));
            }
            else
            {
                // Brighter than the level being removed — independent of us.
                addQ.Enqueue((wx, wy, wz));
            }
        }

        private static void ProcessAddBlock(World world,
            Queue<(int x, int y, int z)> addQ, HashSet<(int cx, int cz)> dirty)
        {
            while (addQ.Count > 0)
            {
                var p = addQ.Dequeue();
                int lvl = GetBlockLightW(world, p.x, p.y, p.z);
                int next = lvl - 1;
                if (next <= 0) continue;
                TryAddNeighbourBlock(world, p.x - 1, p.y, p.z, next, addQ, dirty);
                TryAddNeighbourBlock(world, p.x + 1, p.y, p.z, next, addQ, dirty);
                TryAddNeighbourBlock(world, p.x, p.y - 1, p.z, next, addQ, dirty);
                TryAddNeighbourBlock(world, p.x, p.y + 1, p.z, next, addQ, dirty);
                TryAddNeighbourBlock(world, p.x, p.y, p.z - 1, next, addQ, dirty);
                TryAddNeighbourBlock(world, p.x, p.y, p.z + 1, next, addQ, dirty);
            }
        }

        private static void TryAddNeighbourBlock(World world, int wx, int wy, int wz,
            int level, Queue<(int x, int y, int z)> addQ, HashSet<(int cx, int cz)> dirty)
        {
            if (wy < 0 || wy >= Chunk.SizeY) return;
            if (!IsCellLightTransparent(world, wx, wy, wz)) return;
            int existing = GetBlockLightW(world, wx, wy, wz);
            if (existing >= level) return;
            SetBlockLightW(world, wx, wy, wz, (byte)level, dirty);
            addQ.Enqueue((wx, wy, wz));
        }

        // Mirror of the block-light remove/add BFS for sky-light. The only
        // difference is which channel we read/write in the chunk.
        private static void ProcessRemoveSky(World world,
            Queue<(int x, int y, int z, int oldLevel)> removeQ,
            Queue<(int x, int y, int z)> addQ,
            HashSet<(int cx, int cz)> dirty)
        {
            while (removeQ.Count > 0)
            {
                var p = removeQ.Dequeue();
                int x = p.x, y = p.y, z = p.z, lvl = p.oldLevel;
                TryRemoveNeighbourSky(world, x - 1, y, z, lvl, removeQ, addQ, dirty);
                TryRemoveNeighbourSky(world, x + 1, y, z, lvl, removeQ, addQ, dirty);
                TryRemoveNeighbourSky(world, x, y - 1, z, lvl, removeQ, addQ, dirty);
                TryRemoveNeighbourSky(world, x, y + 1, z, lvl, removeQ, addQ, dirty);
                TryRemoveNeighbourSky(world, x, y, z - 1, lvl, removeQ, addQ, dirty);
                TryRemoveNeighbourSky(world, x, y, z + 1, lvl, removeQ, addQ, dirty);
            }
        }

        private static void TryRemoveNeighbourSky(World world, int wx, int wy, int wz,
            int parentLevel,
            Queue<(int x, int y, int z, int oldLevel)> removeQ,
            Queue<(int x, int y, int z)> addQ,
            HashSet<(int cx, int cz)> dirty)
        {
            if (wy < 0 || wy >= Chunk.SizeY) return;
            int neighbourLight = GetSkyLightW(world, wx, wy, wz);
            if (neighbourLight == 0) return;
            if (neighbourLight < parentLevel)
            {
                SetSkyLightW(world, wx, wy, wz, 0, dirty);
                removeQ.Enqueue((wx, wy, wz, neighbourLight));
            }
            else
            {
                addQ.Enqueue((wx, wy, wz));
            }
        }

        private static void ProcessAddSky(World world,
            Queue<(int x, int y, int z)> addQ, HashSet<(int cx, int cz)> dirty)
        {
            while (addQ.Count > 0)
            {
                var p = addQ.Dequeue();
                int lvl = GetSkyLightW(world, p.x, p.y, p.z);
                int next = lvl - 1;
                if (next <= 0) continue;
                TryAddNeighbourSky(world, p.x - 1, p.y, p.z, next, addQ, dirty);
                TryAddNeighbourSky(world, p.x + 1, p.y, p.z, next, addQ, dirty);
                TryAddNeighbourSky(world, p.x, p.y - 1, p.z, next, addQ, dirty);
                TryAddNeighbourSky(world, p.x, p.y + 1, p.z, next, addQ, dirty);
                TryAddNeighbourSky(world, p.x, p.y, p.z - 1, next, addQ, dirty);
                TryAddNeighbourSky(world, p.x, p.y, p.z + 1, next, addQ, dirty);
            }
        }

        private static void TryAddNeighbourSky(World world, int wx, int wy, int wz,
            int level, Queue<(int x, int y, int z)> addQ, HashSet<(int cx, int cz)> dirty)
        {
            if (wy < 0 || wy >= Chunk.SizeY) return;
            if (!IsCellLightTransparent(world, wx, wy, wz)) return;
            int existing = GetSkyLightW(world, wx, wy, wz);
            if (existing >= level) return;
            SetSkyLightW(world, wx, wy, wz, (byte)level, dirty);
            addQ.Enqueue((wx, wy, wz));
        }

        // World-coord light accessors — hop the chunk lookup so the BFS can
        // freely cross chunk boundaries. Missing chunks (chunk hasn't streamed
        // in) are treated as opaque-sky-zero so the BFS naturally stops at
        // the loaded-region boundary, the same behaviour as RecomputeRegion.
        private static byte GetBlockLightW(World w, int wx, int wy, int wz)
        {
            if (wy < 0 || wy >= Chunk.SizeY) return 0;
            int cx = (int)Math.Floor(wx / (float)Chunk.SizeX);
            int cz = (int)Math.Floor(wz / (float)Chunk.SizeZ);
            var c = w.GetChunk(cx, cz);
            if (c == null) return 0;
            int lx = wx - cx * Chunk.SizeX;
            int lz = wz - cz * Chunk.SizeZ;
            return c.GetBlockLight(lx, wy, lz);
        }

        private static byte GetSkyLightW(World w, int wx, int wy, int wz)
        {
            if (wy < 0 || wy >= Chunk.SizeY) return 0;
            int cx = (int)Math.Floor(wx / (float)Chunk.SizeX);
            int cz = (int)Math.Floor(wz / (float)Chunk.SizeZ);
            var c = w.GetChunk(cx, cz);
            if (c == null) return 0;
            int lx = wx - cx * Chunk.SizeX;
            int lz = wz - cz * Chunk.SizeZ;
            return c.GetSkyLight(lx, wy, lz);
        }

        private static void SetBlockLightW(World w, int wx, int wy, int wz, byte v,
            HashSet<(int cx, int cz)> dirty)
        {
            if (wy < 0 || wy >= Chunk.SizeY) return;
            int cx = (int)Math.Floor(wx / (float)Chunk.SizeX);
            int cz = (int)Math.Floor(wz / (float)Chunk.SizeZ);
            var c = w.GetChunk(cx, cz);
            if (c == null) return;
            int lx = wx - cx * Chunk.SizeX;
            int lz = wz - cz * Chunk.SizeZ;
            c.SetBlockLight(lx, wy, lz, v);
            dirty.Add((cx, cz));
        }

        private static void SetSkyLightW(World w, int wx, int wy, int wz, byte v,
            HashSet<(int cx, int cz)> dirty)
        {
            if (wy < 0 || wy >= Chunk.SizeY) return;
            int cx = (int)Math.Floor(wx / (float)Chunk.SizeX);
            int cz = (int)Math.Floor(wz / (float)Chunk.SizeZ);
            var c = w.GetChunk(cx, cz);
            if (c == null) return;
            int lx = wx - cx * Chunk.SizeX;
            int lz = wz - cz * Chunk.SizeZ;
            c.SetSkyLight(lx, wy, lz, v);
            dirty.Add((cx, cz));
        }

        private static bool IsCellLightTransparent(World w, int wx, int wy, int wz)
        {
            if (wy < 0 || wy >= Chunk.SizeY) return true;
            return BlockData.IsLightTransparent(w.GetBlock(wx, wy, wz));
        }
    }
}
