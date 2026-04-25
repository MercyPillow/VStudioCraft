using System;
using System.Collections.Generic;

namespace VStudioCraft.Game
{
    // Source-driven fluid propagation, run every tick interval by the renderer.
    //
    // Alpha 1.1.2_01 fluids: water sources flow outward up to 7 cells
    // horizontally + arbitrarily downward; lava flows up to 3 cells in
    // overworld. Cells produced by spread are "flowing" variants — broken by
    // any block placed into them, vanish when the source is removed.
    //
    // What we implement here:
    //  - Sources (Water, Lava) flow downward into Air → produces FlowingWater /
    //    FlowingLava in the cell below.
    //  - Sources flow into the four horizontal neighbours up to a per-fluid
    //    horizontal reach (water=7, lava=3 cells away). Reach is encoded in
    //    the per-cell metadata's low 4 bits as "remaining range to spread".
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
    //  - Drain on source removal — once placed, flowing cells persist until
    //    manually broken.
    //  - Visual height variation — flowing cells render as full cubes here.
    //  - Water-meets-lava block formation (cobblestone / stone / obsidian).
    //  - Level-based animated textures.
    internal static class FluidTick
    {
        // Horizontal reach. Encoded into the per-cell metadata: a fresh
        // source-adjacent cell starts at WaterReach-1 (water) or LavaReach-1
        // (lava), each step decrements until 0 stops further horizontal
        // spread. Vertical fall always refreshes to full reach (matches
        // Alpha — water down a cliff fans out at full reach again).
        //
        // Alpha's water reach is 7 cells (lava 3). With the cliff-edge rule
        // (no horizontal spread when the cell below is air) the wider reach
        // no longer floods waterfalls down a column; it just gives ground
        // pools the authentic Alpha footprint.
        private const int WaterReach = 7;
        private const int LavaReach  = 3;


        // Result of a tick: which chunks had any block change, and which had
        // a *light-affecting* change (lava family). Water never changes light
        // because IsLightTransparent(Water)/IsLightTransparent(FlowingWater)
        // are both true — sky and block light flow through unchanged. So a
        // pure water tick can skip the full-chunk relight altogether.
        public struct TickResult
        {
            public HashSet<(int x, int z)> ChangedChunks;
            public HashSet<(int x, int z)> LightChangedChunks;
        }

        // Reusable scratch buffers — the tick fires four times a second, and
        // allocating fresh List/HashSet/Dictionary instances added to GC
        // pressure on the render thread. The state is owned by the static
        // class because the tick currently runs serialised on the render
        // thread; if we ever multi-thread it this becomes a TLS field.
        private static readonly List<(Chunk c, int lx, int y, int lz, byte block, byte meta)> _writes
            = new List<(Chunk, int, int, int, byte, byte)>(256);
        // Drain queue: cells whose upstream feed is gone this tick. Stored as a
        // separate list (not folded into _writes) because they have different
        // apply semantics — drains FORCE the cell to Air regardless of current
        // contents, while spread writes only fill genuinely-empty cells.
        private static readonly List<(Chunk c, int lx, int y, int lz, int group)> _drains
            = new List<(Chunk, int, int, int, int)>(64);
        private static readonly HashSet<Chunk> _producedWrites = new HashSet<Chunk>();
        private static readonly HashSet<Chunk> _producedLavaWrites = new HashSet<Chunk>();
        private static readonly List<Chunk> _activeChunks = new List<Chunk>(64);

        // Run a single tick over every active chunk in the world. Returns
        // the set of chunk keys that mutated so the caller can mark them
        // dirty for remesh and re-light.
        public static TickResult Tick(World world)
        {
            _writes.Clear();
            _drains.Clear();
            _producedWrites.Clear();
            _producedLavaWrites.Clear();
            _activeChunks.Clear();

            // Snapshot the active chunks up-front. Iterating
            // ConcurrentDictionary while we set HasActiveFluid on neighbour
            // chunks is safe, but pre-snapshotting also makes the inner loop
            // hot-path branch-free on the chunk list.
            foreach (var chunk in world.Chunks)
            {
                if (chunk.HasActiveFluid) _activeChunks.Add(chunk);
            }

            for (int ci = 0; ci < _activeChunks.Count; ci++)
            {
                var chunk = _activeChunks[ci];
                ScanChunk(chunk, world);
            }

            // Apply staged spread writes first. We accept that two sources
            // writing into the same cell may overwrite each other —
            // last-write-wins, which produces the slight visual jitter you
            // see in Alpha when two streams meet (acceptable for V1).
            for (int wi = 0; wi < _writes.Count; wi++)
            {
                var w = _writes[wi];
                int idx = Chunk.Index(w.lx, w.y, w.lz);
                // Only overwrite air. A source already there should win;
                // placement during the tick can have already filled the
                // cell from another source's vertical spread.
                if (w.c.RawBlocks[idx] != (byte)BlockType.Air) continue;
                w.c.RawBlocks[idx] = w.block;
                w.c.RawMeta[idx] = w.meta;
                w.c.IsModified = true;
            }

            // Apply drain writes second. A drained cell pre-tick was a
            // flowing fluid with no upstream feeder — spread can't have
            // re-filled it (spread targets only pre-tick-air cells), so the
            // ordering question reduces to: do we want a flowing cell to
            // become Air this tick? Yes. The wave of drained cells advances
            // outward by one cell per tick, matching Alpha's "water recedes
            // step by step" feel after a source is removed.
            for (int di = 0; di < _drains.Count; di++)
            {
                var d = _drains[di];
                int idx = Chunk.Index(d.lx, d.y, d.lz);
                d.c.RawBlocks[idx] = (byte)BlockType.Air;
                d.c.RawMeta[idx] = 0;
                d.c.IsModified = true;
                d.c.HasActiveFluid = true;
                _producedWrites.Add(d.c);
                if (d.group == 2) _producedLavaWrites.Add(d.c);
            }

            // Build the result sets. Chunks that produced no writes AND
            // weren't written into by a neighbour go to inactive — their
            // fluid has reached steady state until something disturbs it.
            // (The "written into by neighbour" case is captured because
            //  SpreadHoriz adds the destination chunk to _producedWrites
            //  even when it isn't in _activeChunks.)
            var changed = new HashSet<(int x, int z)>();
            var lightChanged = new HashSet<(int x, int z)>();
            foreach (var c in _producedWrites) changed.Add((c.ChunkX, c.ChunkZ));
            foreach (var c in _producedLavaWrites) lightChanged.Add((c.ChunkX, c.ChunkZ));

            // Self-deactivate any active chunk that didn't produce/receive
            // any writes this tick — it's at steady state.
            for (int ci = 0; ci < _activeChunks.Count; ci++)
            {
                var c = _activeChunks[ci];
                if (!_producedWrites.Contains(c)) c.HasActiveFluid = false;
            }

            return new TickResult { ChangedChunks = changed, LightChangedChunks = lightChanged };
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

        private static void ScanChunk(Chunk chunk, World world)
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

                bool isSource = (b == BlockType.Water || b == BlockType.Lava);
                bool isFalling = !isSource && (meta[idx] & 0x10) != 0;
                int reach = isSource
                    ? (group == 1 ? WaterReach : LavaReach)
                    : (meta[idx] & 0x0F);

                // Orphan check: a non-source flowing cell with no upstream
                // feeder converts to Air. The wave of "no feeder anymore"
                // propagates one cell per tick, which is the visible Alpha
                // behaviour when you break a source — the puddle recedes.
                if (!isSource)
                {
                    if (!HasFeeder(chunk, world, x, y, z, group, isFalling, reach))
                    {
                        _drains.Add((chunk, x, y, z, group));
                        continue;
                    }
                }

                BlockType flowing = group == 1 ? BlockType.FlowingWater : BlockType.FlowingLava;

                // Vertical fall — highest priority. Tracks whether this cell
                // is currently over air so we know to skip its horizontal
                // spread below (water at a cliff edge / mid-fall shouldn't
                // fan out — it just drops into the gap).
                bool belowAir = false;
                if (y > 0)
                {
                    int belowIdx = Chunk.Index(x, y - 1, z);
                    var below = (BlockType)blocks[belowIdx];
                    if (below == BlockType.Air)
                    {
                        belowAir = true;
                        int fallReach = (group == 1 ? WaterReach : LavaReach);
                        byte fallMeta = (byte)((fallReach & 0x0F) | 0x10); // bit 4 = falling
                        StageWrite(chunk, x, y - 1, z, (byte)flowing, fallMeta, group);
                    }
                }

                // No solid ground below → no horizontal spread. This covers
                // both the falling-mid-air case (a column of falling cells
                // shouldn't fan out as it drops) and the cliff-edge case
                // (a horizontally-spreading cell that reaches the lip of a
                // cliff stops fanning out and just becomes a waterfall).
                // Once the falling column lands on solid ground, the landed
                // cell will see solid below on its NEXT tick and resume
                // normal horizontal spread out to WaterReach (4 cells).
                if (belowAir) continue;

                if (reach <= 0) continue;
                int outReach = reach - 1;
                if (outReach < 0) continue;
                byte outMeta = (byte)(outReach & 0x0F);
                SpreadHoriz(chunk, world, x, y, z, +1,  0, flowing, outMeta, group);
                SpreadHoriz(chunk, world, x, y, z, -1,  0, flowing, outMeta, group);
                SpreadHoriz(chunk, world, x, y, z,  0, +1, flowing, outMeta, group);
                SpreadHoriz(chunk, world, x, y, z,  0, -1, flowing, outMeta, group);
            }
        }

        private static void StageWrite(Chunk c, int lx, int y, int lz, byte block, byte meta, int group)
        {
            _writes.Add((c, lx, y, lz, block, meta));
            _producedWrites.Add(c);
            // Keep the chunk active for next tick — it just produced a fresh
            // boundary that will need follow-up propagation.
            c.HasActiveFluid = true;
            if (group == 2) _producedLavaWrites.Add(c);
        }

        private static void SpreadHoriz(
            Chunk chunk, World world,
            int x, int y, int z, int dx, int dz,
            BlockType flowing, byte outMeta, int group)
        {
            int nlx = x + dx;
            int nlz = z + dz;
            if ((uint)nlx < Chunk.SizeX && (uint)nlz < Chunk.SizeZ)
            {
                int idx = Chunk.Index(nlx, y, nlz);
                if (chunk.RawBlocks[idx] != (byte)BlockType.Air) return;
                StageWrite(chunk, nlx, y, nlz, (byte)flowing, outMeta, group);
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
            StageWrite(nc, xx, y, zz, (byte)flowing, outMeta, group);
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
