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
    // What we DO NOT implement yet (logged in features.md):
    //  - Drain on source removal — once placed, flowing cells persist until
    //    manually broken. Alpha re-evaluates the source-network each tick;
    //    that BFS is straightforward but a meaningful chunk of code, deferred
    //    to a follow-up.
    //  - Visual height variation — flowing cells render as full cubes here.
    //  - Water-meets-lava block formation (cobblestone / stone / obsidian).
    //  - Cross-chunk propagation across worker-chunk boundaries — the tick
    //    only writes within a single chunk per scan, so flow across a chunk
    //    seam waits one extra tick (the neighbour picks it up next pass when
    //    its own scan sees the new flowing-water cell).
    internal static class FluidTick
    {
        // Horizontal reach. Encoded into the per-cell metadata: a fresh
        // source-adjacent cell starts at WaterReach-1 (water) or LavaReach-1
        // (lava), each step decrements until 0 stops further horizontal
        // spread. Vertical fall always refreshes to full reach (matches
        // Alpha — water down a cliff fans out at full reach again).
        private const int WaterReach = 7;
        private const int LavaReach  = 3;

        // Run a single tick over every loaded chunk in the world. Returns
        // the set of chunk keys that mutated so the caller can mark them
        // dirty for remesh and re-light.
        public static HashSet<(int x, int z)> Tick(World world)
        {
            var dirty = new HashSet<(int x, int z)>();
            // Stage all writes into a per-tick list and apply at the end so
            // an east-going scan doesn't bias the spread pattern.
            var writes = new List<(Chunk c, int lx, int y, int lz, byte block, byte meta)>();

            foreach (var chunk in world.Chunks)
            {
                // Skim block array once. Branchy inner loop, but at 16*128*16
                // = 32 768 cells per chunk and ~25 loaded chunks this runs in
                // < 1 ms per tick on real hardware. If it ever shows up in a
                // profile, gate by per-chunk "has fluid" flag.
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
                    int reach = isSource
                        ? (group == 1 ? WaterReach : LavaReach)
                        : (meta[idx] & 0x0F);

                    BlockType flowing = group == 1 ? BlockType.FlowingWater : BlockType.FlowingLava;

                    // -- Vertical fall: highest priority. If the cell below
                    //    this fluid is air, flood it with a fresh-reach
                    //    falling fluid cell.
                    if (y > 0)
                    {
                        int belowIdx = Chunk.Index(x, y - 1, z);
                        var below = (BlockType)blocks[belowIdx];
                        if (below == BlockType.Air)
                        {
                            int fallReach = (group == 1 ? WaterReach : LavaReach);
                            byte fallMeta = (byte)((fallReach & 0x0F) | 0x10); // bit 4 = falling
                            writes.Add((chunk, x, y - 1, z, (byte)flowing, fallMeta));
                            dirty.Add((chunk.ChunkX, chunk.ChunkZ));
                        }
                    }

                    // -- Horizontal spread: only if we still have reach left.
                    //    A "falling" flowing cell does NOT spread horizontally
                    //    (matches Alpha — falling water fans out only at the
                    //    bottom of the column when it lands on a solid).
                    if (reach <= 0) continue;
                    bool falling = !isSource && (meta[idx] & 0x10) != 0;
                    if (falling)
                    {
                        // Falling water that has a solid block below converts to
                        // a normal "spreading" cell with full reach. Otherwise
                        // skip horizontal spread.
                        if (y == 0) continue;
                        var below = (BlockType)blocks[Chunk.Index(x, y - 1, z)];
                        if (below == BlockType.Air) continue;
                        // Solid landing — fall through to horizontal spread
                        // below using the falling cell's full reach.
                    }

                    int outReach = reach - 1;
                    byte outMeta = (byte)(outReach & 0x0F);

                    // Try the four horizontal neighbours. World.GetBlock
                    // crosses chunk boundaries; if the neighbour is in a
                    // different chunk we still write into THAT chunk's local
                    // coords via WriteToWorld.
                    SpreadHoriz(chunk, world, x, y, z, +1,  0, flowing, outMeta, writes, dirty);
                    SpreadHoriz(chunk, world, x, y, z, -1,  0, flowing, outMeta, writes, dirty);
                    SpreadHoriz(chunk, world, x, y, z,  0, +1, flowing, outMeta, writes, dirty);
                    SpreadHoriz(chunk, world, x, y, z,  0, -1, flowing, outMeta, writes, dirty);
                }
            }

            // Apply staged writes. We accept that two sources writing into
            // the same cell may overwrite each other — last-write-wins, which
            // produces the slight visual jitter you see in Alpha when two
            // streams meet (acceptable for V1).
            foreach (var w in writes)
            {
                int idx = Chunk.Index(w.lx, w.y, w.lz);
                // Only overwrite air. A source already there should win;
                // placement during the tick can have already filled the
                // cell from another source's vertical spread.
                if (w.c.RawBlocks[idx] != (byte)BlockType.Air) continue;
                w.c.RawBlocks[idx] = w.block;
                w.c.RawMeta[idx] = w.meta;
                w.c.IsModified = true;
            }

            return dirty;
        }

        private static void SpreadHoriz(
            Chunk chunk, World world,
            int x, int y, int z, int dx, int dz,
            BlockType flowing, byte outMeta,
            List<(Chunk c, int lx, int y, int lz, byte block, byte meta)> writes,
            HashSet<(int x, int z)> dirty)
        {
            int nlx = x + dx;
            int nlz = z + dz;
            // Same chunk fast path.
            if ((uint)nlx < Chunk.SizeX && (uint)nlz < Chunk.SizeZ)
            {
                int idx = Chunk.Index(nlx, y, nlz);
                if (chunk.RawBlocks[idx] != (byte)BlockType.Air) return;
                writes.Add((chunk, nlx, y, nlz, (byte)flowing, outMeta));
                dirty.Add((chunk.ChunkX, chunk.ChunkZ));
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
            writes.Add((nc, xx, y, zz, (byte)flowing, outMeta));
            dirty.Add((ncx, ncz));
        }
    }
}
