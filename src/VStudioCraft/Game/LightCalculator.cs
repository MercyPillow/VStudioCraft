using System;
using System.Collections.Generic;

namespace VStudioCraft.Game
{
    // Per-chunk lighting pass — Alpha-style 15-level flood fill for sky and block
    // light. Runs in two phases:
    //
    //   1. Sky light: every column's cells above the topmost light-blocker get
    //      level 15. Then a BFS spreads laterally, dropping 1 per step.
    //   2. Block light: emitter cells (lava today; torches/fire later) seed at
    //      their emission strength. Same BFS spreads outward.
    //
    // Cross-chunk seams are intentionally not addressed here — neighbours are
    // relit when they're regenerated, and the mesher samples neighbour-chunk
    // light when emitting border faces. This keeps the per-chunk pass cheap and
    // independent (good for the worker thread that runs after TerrainGenerator),
    // at the cost of mild seams across borders that re-resolve once both sides
    // settle. Good enough for the alpha look; classic Alpha had visible seams too.
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
    }
}
