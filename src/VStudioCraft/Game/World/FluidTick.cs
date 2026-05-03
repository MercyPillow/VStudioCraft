using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace VStudioCraft.Game
{
    // Source-driven fluid propagation, run every tick interval by the renderer.
    //
    // Water and lava share one code path here. The two fluids differ only in
    // their textures and the BlockType bytes their sources produce
    // (Water/FlowingWater vs Lava/FlowingLava); spread reach, fall behaviour,
    // drain rules, and per-tick performance characteristics are identical.
    // Earlier revisions had lava emit block-light, which forced a 3×3-chunk
    // RecomputeRegion every tick a flowing-lava cell advanced and stuttered
    // visibly on the render thread — fluid sim looked symmetric but lava paid
    // a much heavier post-tick bill. Lava now emits 0 like water, so both
    // fluids run identical machinery and a flowing river of either looks the
    // same on a perf trace.
    //
    // What we implement here:
    //  - Sources (Water, Lava) flow downward into Air → produces FlowingWater /
    //    FlowingLava in the cell below.
    //  - Sources flow into the four horizontal neighbours up to FluidReach
    //    cells away. Reach is encoded in the per-cell metadata's low 4 bits
    //    as "remaining range to spread".
    //  - Flowing fluid cells continue propagation: still spread downward
    //    (reach refreshed to fall again) and horizontally if their remaining
    //    range > 0.
    //
    // Performance gating: the tick is keyed on `Chunk.HasActiveFluid`. The
    // per-cell scan over all 16*128*16 cells of every loaded chunk ran on the
    // render thread and stuttered visibly even when nothing changed. We now
    // skip chunks whose fluid has reached steady state (every source bounded
    // by non-air neighbours) and only re-mark them active when an edit / tick
    // creates a new air-fluid boundary.
    //
    // What we DO NOT implement yet (logged in features.md):
    //  - Visual height variation for non-falling cells (flowing cells render
    //    as full cubes; the lid pass already handles surface fluid).
    //  - Level-based animated textures.
    //
    // Water-meets-lava block formation IS implemented — see the
    // `group == 2 && HasWaterNeighbour(...)` branch in ScanChunk (lava
    // source → obsidian; flowing lava + water below → stone; flowing
    // lava + water elsewhere → cobblestone). Earlier revisions of
    // this file claimed it was deferred; that note went stale once
    // Tier 6 #36 landed and is corrected here.
    internal static class FluidTick
    {
        // Horizontal reach, applied to both fluids. Encoded into the per-cell
        // metadata: a fresh source-adjacent cell starts at FluidReach-1, each
        // step decrements until 0 stops further horizontal spread. Vertical
        // fall always refreshes to full reach (water/lava down a cliff fans
        // out at full reach again).
        private const int FluidReach = 7;


        // Result of a tick: chunks that had any block change. Both fluids are
        // light-transparent and emit 0, so neither alters the light field —
        // there's no separate "light changed" set to track.
        public struct TickResult
        {
            public HashSet<(int x, int z)> ChangedChunks;
        }

        // Per-thread scratch buffers used by ScanChunk during a parallel
        // tick. Each thread that participates in the Parallel.ForEach
        // gets its own ScanState (via Parallel.ForEach's localInit), so
        // ScanChunk's appends to Writes / Drains / LavaConversions /
        // ProducedWrites never contend across threads. Tick() merges
        // every thread's state into the global apply-pass at the end.
        //
        // States are pooled in `_scanStatePool` (a ConcurrentBag) so a
        // tick reuses last tick's allocations rather than allocating
        // fresh lists every 50 ms. The pool is bounded only by the
        // peak parallelism we've ever hit (~ Environment.ProcessorCount).
        private sealed class ScanState
        {
            public readonly List<(Chunk c, int lx, int y, int lz, byte block, byte meta)> Writes
                = new List<(Chunk, int, int, int, byte, byte)>(256);
            public readonly List<(Chunk c, int lx, int y, int lz, int group)> Drains
                = new List<(Chunk, int, int, int, int)>(64);
            // Lava↔water contact conversions. Deferred to the serial
            // apply-pass because (a) the conversion mutates the chunk's
            // RawBlocks/RawMeta in place which would race with parallel
            // scans of neighbour chunks, and (b) the conversion's
            // follow-up LightCalculator.UpdateAfterEdit call is not
            // thread-safe (it walks 3×3 chunks of light arrays).
            public readonly List<(Chunk c, int lx, int y, int lz, BlockType oldT, BlockType newT)> LavaConversions
                = new List<(Chunk, int, int, int, BlockType, BlockType)>(8);
            public readonly HashSet<Chunk> ProducedWrites = new HashSet<Chunk>();

            public void Clear()
            {
                Writes.Clear();
                Drains.Clear();
                LavaConversions.Clear();
                ProducedWrites.Clear();
            }
        }

        private static readonly ConcurrentBag<ScanState> _scanStatePool = new ConcurrentBag<ScanState>();
        private static readonly List<Chunk> _activeChunks = new List<Chunk>(64);
        // Aggregated across all per-thread ScanStates after the
        // parallel scan completes; consumed by the (serial) apply
        // pass below. Cleared at the start of each Tick.
        private static readonly HashSet<Chunk> _producedWrites = new HashSet<Chunk>();

        // Run a single tick over every active chunk in the world. Returns
        // the set of chunk keys that mutated so the caller can mark them
        // dirty for remesh and re-light.
        public static TickResult Tick(World world)
        {
            _producedWrites.Clear();
            _activeChunks.Clear();

            // Snapshot the active chunks up-front. Iterating
            // ConcurrentDictionary while we set HasActiveFluid on neighbour
            // chunks is safe, but pre-snapshotting also makes the inner loop
            // hot-path branch-free on the chunk list.
            foreach (var chunk in world.Chunks)
            {
                if (chunk.HasActiveFluid) _activeChunks.Add(chunk);
            }

            // Phase B.1 of the parallelisation analysis — scan all
            // active chunks in parallel. Each thread takes its own
            // ScanState (rented from the pool) and ScanChunk only
            // appends to that state — no shared mutable state during
            // the scan. After the parallel scan completes we run the
            // existing apply-pass single-threaded against the merged
            // state. Sequential semantics differ in one edge case
            // (lava↔water conversion latency by one tick when the
            // conversion would have rippled into another active
            // chunk's same-tick scan), which is invisible to the
            // player; everything else (spread + drain) was already
            // staged in the original code so order doesn't matter.
            //
            // Materialising the per-thread states into a list lets
            // the merge phase iterate them deterministically.
            var threadStates = new ConcurrentBag<ScanState>();
            Parallel.ForEach(
                _activeChunks,
                () =>
                {
                    if (!_scanStatePool.TryTake(out var s)) s = new ScanState();
                    s.Clear();
                    return s;
                },
                (chunk, _, state) =>
                {
                    ScanChunk(chunk, world, state);
                    return state;
                },
                state => threadStates.Add(state));

            // Merge thread-local states into single lists for the
            // serial apply-pass below. The lists are local — the old
            // class-static _writes/_drains scratch buffers are gone.
            var allWrites = new List<(Chunk c, int lx, int y, int lz, byte block, byte meta)>(256);
            var allDrains = new List<(Chunk c, int lx, int y, int lz, int group)>(64);
            var allLava = new List<(Chunk c, int lx, int y, int lz, BlockType oldT, BlockType newT)>(8);
            foreach (var s in threadStates)
            {
                allWrites.AddRange(s.Writes);
                allDrains.AddRange(s.Drains);
                allLava.AddRange(s.LavaConversions);
                foreach (var c in s.ProducedWrites) _producedWrites.Add(c);
                _scanStatePool.Add(s); // return to pool for next tick
            }

            // Apply staged lava↔water conversions first. These mutate
            // blocks + meta unconditionally and run a 3×3 light update
            // per conversion. Order doesn't matter inside this list
            // because each conversion targets a unique cell (the
            // ScanChunk pass that produced it had `continue;`'d before
            // checking that cell again).
            for (int li = 0; li < allLava.Count; li++)
            {
                var lv = allLava[li];
                int idx = Chunk.Index(lv.lx, lv.y, lv.lz);
                lv.c.RawBlocks[idx] = (byte)lv.newT;
                lv.c.RawMeta[idx] = 0;
                lv.c.IsModified = true;
                _producedWrites.Add(lv.c);
                int wx = lv.c.ChunkX * Chunk.SizeX + lv.lx;
                int wz = lv.c.ChunkZ * Chunk.SizeZ + lv.lz;
                var touched = LightCalculator.UpdateAfterEdit(world, wx, lv.y, wz, lv.oldT, lv.newT);
                foreach (var k in touched)
                {
                    var nc = world.GetChunk(k.cx, k.cz);
                    if (nc != null) _producedWrites.Add(nc);
                }
            }

            // Apply staged spread writes next. We accept that two sources
            // writing into the same cell may overwrite each other —
            // last-write-wins, which produces the slight visual jitter you
            // see in Alpha when two streams meet (acceptable for V1).
            for (int wi = 0; wi < allWrites.Count; wi++)
            {
                var w = allWrites[wi];
                int idx = Chunk.Index(w.lx, w.y, w.lz);
                // Only overwrite air. A source already there should win;
                // placement during the tick can have already filled the
                // cell from another source's vertical spread.
                if (w.c.RawBlocks[idx] != (byte)BlockType.Air) continue;
                w.c.RawBlocks[idx] = w.block;
                w.c.RawMeta[idx] = w.meta;
                w.c.IsModified = true;
            }

            // Apply drain writes last. A drained cell pre-tick was a
            // flowing fluid with no upstream feeder — spread can't have
            // re-filled it (spread targets only pre-tick-air cells), so the
            // ordering question reduces to: do we want a flowing cell to
            // become Air this tick? Yes. The wave of drained cells advances
            // outward by one cell per tick, matching Alpha's "water recedes
            // step by step" feel after a source is removed.
            for (int di = 0; di < allDrains.Count; di++)
            {
                var d = allDrains[di];
                int idx = Chunk.Index(d.lx, d.y, d.lz);
                d.c.RawBlocks[idx] = (byte)BlockType.Air;
                d.c.RawMeta[idx] = 0;
                d.c.IsModified = true;
                d.c.HasActiveFluid = true;
                _producedWrites.Add(d.c);

                // If this drained cell sat on a chunk boundary, the cell
                // across that boundary (in the neighbour chunk) was very
                // likely depending on us as its horizontal feeder. We need
                // the neighbour to scan next tick so it notices the feeder
                // is gone and drains too — otherwise the receding-water
                // wave stalls at the chunk seam. Without this, a settled
                // neighbour chunk that produced no writes on tick 1 (when
                // the source was first broken) self-deactivates and never
                // re-engages, so the flow sitting in the neighbour chunk
                // stays put forever even after the originating chunk has
                // fully drained.
                // Only flip the neighbour's active flag — don't add it to
                // _producedWrites, because no block in the neighbour changed
                // this tick (we'd otherwise trigger a needless remesh +
                // relight on a chunk that's still visually identical). Next
                // tick the neighbour will scan and either drain a cell of
                // its own (which DOES go through _producedWrites + dirty)
                // or stay quiet and self-deactivate again.
                if (d.lx == 0)
                {
                    var n = world.GetChunk(d.c.ChunkX - 1, d.c.ChunkZ);
                    if (n != null) n.HasActiveFluid = true;
                }
                else if (d.lx == Chunk.SizeX - 1)
                {
                    var n = world.GetChunk(d.c.ChunkX + 1, d.c.ChunkZ);
                    if (n != null) n.HasActiveFluid = true;
                }
                if (d.lz == 0)
                {
                    var n = world.GetChunk(d.c.ChunkX, d.c.ChunkZ - 1);
                    if (n != null) n.HasActiveFluid = true;
                }
                else if (d.lz == Chunk.SizeZ - 1)
                {
                    var n = world.GetChunk(d.c.ChunkX, d.c.ChunkZ + 1);
                    if (n != null) n.HasActiveFluid = true;
                }
            }

            // Build the result set. Chunks that produced no writes AND
            // weren't written into by a neighbour go to inactive — their
            // fluid has reached steady state until something disturbs it.
            // (The "written into by neighbour" case is captured because
            //  SpreadHoriz adds the destination chunk to _producedWrites
            //  even when it isn't in _activeChunks.)
            var changed = new HashSet<(int x, int z)>();
            foreach (var c in _producedWrites) changed.Add((c.ChunkX, c.ChunkZ));

            // Self-deactivate any active chunk that didn't produce/receive
            // any writes this tick — it's at steady state.
            for (int ci = 0; ci < _activeChunks.Count; ci++)
            {
                var c = _activeChunks[ci];
                if (!_producedWrites.Contains(c)) c.HasActiveFluid = false;
            }

            return new TickResult { ChangedChunks = changed };
        }

        // Mark a chunk + its 4 neighbours as having active fluid. Called by
        // SetBlock so a player edit next to a body of water re-engages the
        // tick on those chunks even after they had self-deactivated.
        public static void MarkActiveAroundEdit(World world, int cx, int cz)
        {
            var c = world.GetChunk(cx, cz); if (c != null) c.HasActiveFluid = true;
            c = world.GetChunk(cx - 1, cz); if (c != null) c.HasActiveFluid = true;
            c = world.GetChunk(cx + 1, cz); if (c != null) c.HasActiveFluid = true;
            c = world.GetChunk(cx, cz - 1); if (c != null) c.HasActiveFluid = true;
            c = world.GetChunk(cx, cz + 1); if (c != null) c.HasActiveFluid = true;
        }

        private static void ScanChunk(Chunk chunk, World world, ScanState state)
        {
            var blocks = chunk.RawBlocks;
            var meta = chunk.RawMeta;
            for (int x = 0; x < Chunk.SizeX; x++)
            for (int y = 0; y < Chunk.SizeY; y++)
            for (int z = 0; z < Chunk.SizeZ; z++)
            {
                int idx = Chunk.Index(x, y, z);
                var b = (BlockType)blocks[idx];
                int group = BlockData.FluidGroup(b);
                if (group == 0) continue;

                // Tier 6 #36 — Water-meets-lava contact conversion.
                // Only LAVA cells convert; water always survives the
                // contact. Rules:
                //   Lava source     + any water adjacent     → Obsidian
                //   Flowing lava    + water DIRECTLY below   → Stone
                //   Flowing lava    + water elsewhere        → Cobblestone
                // The stone case captures the canonical "lava drip
                // landing on the surface of a pool freezes instantly"
                // outcome: a flowing-lava column descending into water
                // forms a stone cap rather than cobble. We check
                // "water below" first so a flowing-lava cell that has
                // BOTH water below AND water beside still resolves to
                // stone — the vertical drop dominates visually because
                // the lava is moving down into the contact, not
                // sideways.
                // Detected by scanning all 6 neighbours for a water-
                // group fluid. Slightly more aggressive than canonical
                // Alpha (which only converts on flowing-water trigger,
                // not source-water static contact) but visually
                // identical to the player AND much simpler than
                // tracking which side initiated the spread. Mutates in
                // place + continues so the converted cell skips the
                // rest of the fluid-processing pipeline for this tick.
                if (group == 2 && HasWaterNeighbour(chunk, world, x, y, z))
                {
                    BlockType newT;
                    if (b == BlockType.Lava)
                    {
                        newT = BlockType.Obsidian;
                    }
                    else
                    {
                        bool waterBelow = y > 0 && IsWaterAt(chunk, world, x, y - 1, z);
                        newT = waterBelow ? BlockType.Stone : BlockType.Cobblestone;
                    }
                    // Phase B.1 — stage the conversion (don't mutate in
                    // place) so concurrent ScanChunk threads scanning
                    // neighbouring chunks see a consistent snapshot of
                    // this chunk's blocks. The serial apply-pass at the
                    // end of Tick() runs the LightCalculator step. The
                    // observable difference vs. the sequential version
                    // is one tick of latency on lava↔water reactions
                    // when the conversion would have rippled into another
                    // chunk's same-tick scan — invisible to the player.
                    state.LavaConversions.Add((chunk, x, y, z, b, newT));
                    state.ProducedWrites.Add(chunk);
                    continue;
                }

                bool isSource = (b == BlockType.Water || b == BlockType.Lava);
                bool isFalling = !isSource && (meta[idx] & 0x10) != 0;
                int reach = isSource ? FluidReach : (meta[idx] & 0x0F);

                // Landing conversion: a falling cell that finds solid ground
                // beneath it stops being "falling" — it becomes the wellhead
                // of the puddle that fans out from the cliff base. We have to
                // clear the 0x10 bit explicitly here, otherwise neighbouring
                // puddle cells that look up to us via HasHorizFeeder reject
                // us (falling neighbours don't count as horizontal feeders),
                // so the entire ring around a waterfall base would drain on
                // each tick. With the bit cleared the landed cell is treated
                // as a normal non-falling spreader.
                if (!isSource && isFalling && y > 0)
                {
                    int belowIdx0 = Chunk.Index(x, y - 1, z);
                    var below0 = (BlockType)blocks[belowIdx0];
                    if (below0 != BlockType.Air && BlockData.FluidGroup(below0) != group)
                    {
                        meta[idx] &= 0x0F;        // strip falling bit
                        chunk.IsModified = true;
                        isFalling = false;
                    }
                }

                // Orphan check: a non-source flowing cell with no upstream
                // feeder converts to Air. The wave of "no feeder anymore"
                // propagates one cell per tick, which is the visible Alpha
                // behaviour when you break a source — the puddle recedes.
                if (!isSource)
                {
                    if (!HasFeeder(chunk, world, x, y, z, group, isFalling, reach))
                    {
                        state.Drains.Add((chunk, x, y, z, group));
                        continue;
                    }
                }

                BlockType flowing = group == 1 ? BlockType.FlowingWater : BlockType.FlowingLava;

                // Inspect the cell below. We need three pieces of info:
                //   belowAir       — drop another falling cell into it
                //   belowSameFluid — falling streams join the pool here
                //   (otherwise   — solid ground; treat normally)
                bool belowAir = false;
                bool belowSameFluid = false;
                if (y > 0)
                {
                    int belowIdx = Chunk.Index(x, y - 1, z);
                    var below = (BlockType)blocks[belowIdx];
                    if (below == BlockType.Air)
                    {
                        belowAir = true;
                        byte fallMeta = (byte)((FluidReach & 0x0F) | 0x10); // bit 4 = falling
                        StageWrite(state, chunk, x, y - 1, z, (byte)flowing, fallMeta, group);
                    }
                    else if (BlockData.FluidGroup(below) == group)
                    {
                        belowSameFluid = true;
                    }
                }

                // No solid ground below → no horizontal spread. This covers
                // both the falling-mid-air case (a column of falling cells
                // shouldn't fan out as it drops) and the cliff-edge case
                // (a horizontally-spreading cell that reaches the lip of a
                // cliff stops fanning out and just becomes a waterfall).
                // Once the falling column lands on solid ground, the landed
                // cell will see solid below on its NEXT tick and resume
                // normal horizontal spread.
                if (belowAir) continue;

                // Same-fluid directly below → never fan out horizontally.
                // The body below covers its own surface (its top layer
                // spreads on its own); letting this cell paint another
                // ring on top of it just floods outward one extra cell
                // per tick. Without this guard you see three symptoms:
                //   1. A falling stream lands on a pool and the impact
                //      cell re-spreads at reach=7 across the surface,
                //      then those new cells repeat next tick — the
                //      "expanding flood" the player sees.
                //   2. A source placed on top of a body (e.g. on the
                //      ocean surface) fans out at reach=7 across the
                //      water surface as a duplicated film.
                //   3. A non-falling flowing cell that has settled with
                //      same-fluid below would do the same as #2 on its
                //      next tick.
                // The cell itself stays — it still occupies its slot —
                // it just stops generating new horizontal neighbours.
                // The rule is intentionally agnostic to source/flowing
                // and to falling/non-falling: the only thing that
                // matters is "is there already a body below me", and if
                // yes, I am redundant for surface coverage.
                if (belowSameFluid) continue;

                if (reach <= 0) continue;
                int outReach = reach - 1;
                if (outReach < 0) continue;
                byte outMeta = (byte)(outReach & 0x0F);
                SpreadHoriz(state, chunk, world, x, y, z, +1,  0, flowing, outMeta, group);
                SpreadHoriz(state, chunk, world, x, y, z, -1,  0, flowing, outMeta, group);
                SpreadHoriz(state, chunk, world, x, y, z,  0, +1, flowing, outMeta, group);
                SpreadHoriz(state, chunk, world, x, y, z,  0, -1, flowing, outMeta, group);
            }
        }

        private static void StageWrite(ScanState state, Chunk c, int lx, int y, int lz, byte block, byte meta, int group)
        {
            state.Writes.Add((c, lx, y, lz, block, meta));
            state.ProducedWrites.Add(c);
            // Keep the chunk active for next tick — it just produced a fresh
            // boundary that will need follow-up propagation. Multiple threads
            // may write `true` to the same chunk's HasActiveFluid in the
            // parallel scan; the race is benign (every writer writes the
            // same value) and the bool write is atomic.
            c.HasActiveFluid = true;
            // group is unused now that water and lava share the same code
            // path; kept on the signature so the call sites stay symmetric
            // with future per-fluid hooks (e.g. water-meets-lava → stone).
            _ = group;
        }

        private static void SpreadHoriz(
            ScanState state, Chunk chunk, World world,
            int x, int y, int z, int dx, int dz,
            BlockType flowing, byte outMeta, int group)
        {
            int nlx = x + dx;
            int nlz = z + dz;
            if ((uint)nlx < Chunk.SizeX && (uint)nlz < Chunk.SizeZ)
            {
                int idx = Chunk.Index(nlx, y, nlz);
                if (chunk.RawBlocks[idx] != (byte)BlockType.Air) return;
                StageWrite(state, chunk, nlx, y, nlz, (byte)flowing, outMeta, group);
                return;
            }
            // Crossing into a neighbour chunk.
            int ncx = chunk.ChunkX, ncz = chunk.ChunkZ;
            int xx = nlx, zz = nlz;
            if (nlx < 0)              { ncx--; xx = Chunk.SizeX - 1; }
            else if (nlx >= Chunk.SizeX) { ncx++; xx = 0; }
            if (nlz < 0)              { ncz--; zz = Chunk.SizeZ - 1; }
            else if (nlz >= Chunk.SizeZ) { ncz++; zz = 0; }
            var nc = world.GetChunk(ncx, ncz);
            if (nc == null) return;
            int nidx = Chunk.Index(xx, y, zz);
            if (nc.RawBlocks[nidx] != (byte)BlockType.Air) return;
            StageWrite(state, nc, xx, y, zz, (byte)flowing, outMeta, group);
        }

        // Does this flowing cell have an upstream fluid feeder right now?
        // Sources never call this (they're self-feeding by definition). The
        // check looks one step "uphill" along the spread graph:
        //   - For falling cells: only the cell directly above counts. If it
        //     isn't a same-family fluid, the column has been broken upstream
        //     and this cell drains.
        //   - For non-falling spread cells: the cell directly above (vertical
        //     fall feeds horizontal spread) OR a horizontal neighbour with
        //     strictly greater reach (non-falling) OR an adjacent source.
        // We deliberately reject falling neighbours as a horizontal feeder —
        // a falling stream shouldn't sustain a sideways puddle on its own;
        // only the cell where the stream lands does that.
        private static bool HasFeeder(
            Chunk chunk, World world,
            int x, int y, int z, int group, bool falling, int reach)
        {
            // Vertical feeder: any same-family fluid directly above.
            if (y < Chunk.SizeY - 1)
            {
                int aboveIdx = Chunk.Index(x, y + 1, z);
                if (BlockData.FluidGroup((BlockType)chunk.RawBlocks[aboveIdx]) == group)
                    return true;
            }

            // Falling cells care only about the column above them.
            if (falling) return false;

            return HasHorizFeeder(chunk, world, x, y, z, +1,  0, group, reach)
                || HasHorizFeeder(chunk, world, x, y, z, -1,  0, group, reach)
                || HasHorizFeeder(chunk, world, x, y, z,  0, +1, group, reach)
                || HasHorizFeeder(chunk, world, x, y, z,  0, -1, group, reach);
        }

        // Tier 6 #36 — True iff any of the 6 axis-aligned neighbours
        // of (x,y,z) holds a water-group fluid (Water or FlowingWater).
        // Crosses chunk boundaries via world.GetChunk to avoid missing
        // contact at chunk seams. Cheap — at worst 6 cell reads + up
        // to 4 chunk lookups for boundary cells.
        private static bool HasWaterNeighbour(Chunk chunk, World world, int x, int y, int z)
        {
            if (y > 0 && IsWaterAt(chunk, world, x, y - 1, z)) return true;
            if (y < Chunk.SizeY - 1 && IsWaterAt(chunk, world, x, y + 1, z)) return true;
            if (IsWaterAt(chunk, world, x + 1, y, z)) return true;
            if (IsWaterAt(chunk, world, x - 1, y, z)) return true;
            if (IsWaterAt(chunk, world, x, y, z + 1)) return true;
            if (IsWaterAt(chunk, world, x, y, z - 1)) return true;
            return false;
        }

        private static bool IsWaterAt(Chunk chunk, World world, int lx, int y, int lz)
        {
            Chunk target = chunk;
            int xx = lx, zz = lz;
            if ((uint)lx >= Chunk.SizeX || (uint)lz >= Chunk.SizeZ)
            {
                int ncx = chunk.ChunkX, ncz = chunk.ChunkZ;
                if (lx < 0)              { ncx--; xx = Chunk.SizeX - 1; }
                else if (lx >= Chunk.SizeX) { ncx++; xx = 0; }
                if (lz < 0)              { ncz--; zz = Chunk.SizeZ - 1; }
                else if (lz >= Chunk.SizeZ) { ncz++; zz = 0; }
                target = world.GetChunk(ncx, ncz);
                if (target == null) return false;
            }
            var b = (BlockType)target.RawBlocks[Chunk.Index(xx, y, zz)];
            return b == BlockType.Water || b == BlockType.FlowingWater;
        }

        private static bool HasHorizFeeder(
            Chunk chunk, World world,
            int x, int y, int z, int dx, int dz,
            int group, int reach)
        {
            int nlx = x + dx, nlz = z + dz;
            Chunk target = chunk;
            int idx;
            if ((uint)nlx < Chunk.SizeX && (uint)nlz < Chunk.SizeZ)
            {
                idx = Chunk.Index(nlx, y, nlz);
            }
            else
            {
                int ncx = chunk.ChunkX, ncz = chunk.ChunkZ;
                int xx = nlx, zz = nlz;
                if (nlx < 0)              { ncx--; xx = Chunk.SizeX - 1; }
                else if (nlx >= Chunk.SizeX) { ncx++; xx = 0; }
                if (nlz < 0)              { ncz--; zz = Chunk.SizeZ - 1; }
                else if (nlz >= Chunk.SizeZ) { ncz++; zz = 0; }
                target = world.GetChunk(ncx, ncz);
                if (target == null) return false;
                idx = Chunk.Index(xx, y, zz);
            }

            var b = (BlockType)target.RawBlocks[idx];
            if (BlockData.FluidGroup(b) != group) return false;
            // Sources are always feeders.
            if (b == BlockType.Water || b == BlockType.Lava) return true;
            // Flowing neighbour must be non-falling AND strictly fresher
            // (greater reach) to feed us — otherwise two equal-reach cells
            // could prop each other up and survive the source's removal.
            byte nbMeta = target.RawMeta[idx];
            if ((nbMeta & 0x10) != 0) return false;          // falling, not a horiz feeder
            return (nbMeta & 0x0F) > reach;
        }
    }
}
