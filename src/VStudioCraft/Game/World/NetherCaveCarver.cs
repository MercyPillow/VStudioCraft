using System;

namespace VStudioCraft.Game
{
    // Tier 8 #51 V13 — Beta 1.7.3 MapGenCavesHell173-equivalent cave
    // carver for the Nether. Generates branching tunnel networks that
    // wander through the netherrack mass, dropping into the lava ocean
    // and rising again. This is THE pass that gives Beta nether its
    // "hand-carved sprawling cavern" feel — without it, the density
    // field's natural smoothness reads as a uniform stone block with
    // no real variety.
    //
    // Algorithm (matches the Beta 1.7.3 source's MapGenCavesHell173):
    //   * Inspect this chunk + its 8 horizontal neighbours' anchor
    //     RNGs. For each anchor, roll the cave count (most common 0;
    //     occasional 1..10).
    //   * Each cave starts at a random (x, y, z) inside its anchor
    //     chunk. 25% of caves spawn as a 4-branch network (start
    //     immediately splits into 4 tunnels at perpendicular angles).
    //   * Each tunnel walks a parametric curve for a random length
    //     (~30..150 steps), updating yaw + pitch with damped jitter
    //     each step. Pitch is biased horizontal so tunnels don't
    //     bottom-out into the bedrock floor or punch through the
    //     ceiling.
    //   * At every step, carve a vertically-squashed sphere (radius
    //     scales by sin(k * π / length) so the tunnel tapers at both
    //     ends). Cells inside are converted to Air, replacing
    //     netherrack and (importantly) lava — tunnels are dry rooms
    //     that drain into the lava ocean only at openings.
    //   * Each step has a 25% chance to spawn a perpendicular branch.
    //
    // Cross-chunk continuity: each anchor chunk seeds its RNG from
    // (seed, anchorX, anchorZ) so the same tunnel path is computed
    // identically when carved from any neighbour. We only carve cells
    // that fall in THIS chunk's bounds, but the path extends across
    // chunk seams seamlessly.
    internal static class NetherCaveCarver
    {
        public const int CaveAnchorRadius = 1;     // 3×3 chunk neighbourhood
        public const int MinY = 1;                  // bedrock-floor +1
        public const int MaxY = 120;                // bedrock-ceiling -7

        public static void Carve(Chunk c, int seed)
        {
            // Pass 1 — chasms. Vertical tubes that slice through the
            // cavern floor from the open cavern (gy~64..88) down into
            // the lava ocean (gy~0..56). This is what gives the user-
            // requested "chasms leading to the lava lakes below" feel:
            // wandering in the open cavern, the player suddenly comes
            // across a vertical drop they could fall down into lava.
            // Without this pass, the cavern floor is mostly intact
            // netherrack on top of the lava and players never see the
            // lava unless they dig down.
            CarveChasms(c, seed);

            // Pass 2 — wandering tunnel networks (Beta-style branching
            // caves). Walk this chunk + 8 neighbours.
            for (int ndz = -CaveAnchorRadius; ndz <= CaveAnchorRadius; ndz++)
            for (int ndx = -CaveAnchorRadius; ndx <= CaveAnchorRadius; ndx++)
            {
                int anchorX = c.ChunkX + ndx;
                int anchorZ = c.ChunkZ + ndz;
                int hash = (int)((uint)seed * 0xCAB1E713u
                    + (uint)(anchorX * 0x4F1B5CD3)
                    + (uint)(anchorZ * 0xA3D7B891));
                var rng = new Random(hash);

                // Cave count — Beta canonical does a heavy 0-bias gate
                // (triple-nested rng + 1-in-5 outer gate = ~80% zero).
                // We want a more open cavern, so we drop the outer
                // 1-in-5 gate entirely — every chunk has 0..10 caves
                // with the triple-nested distribution still favouring
                // small counts. Plus we add a base cave count of 1 so
                // even "empty" chunks get a single tunnel passing
                // through.
                int caveCount = rng.Next(rng.Next(rng.Next(10) + 1) + 1) + 1;

                for (int n = 0; n < caveCount; n++)
                {
                    double startX = anchorX * Chunk.SizeX + rng.Next(Chunk.SizeX);
                    double startY = MinY + rng.Next(MaxY - MinY - 8);
                    double startZ = anchorZ * Chunk.SizeZ + rng.Next(Chunk.SizeZ);

                    // 25% of caves spawn 4 branches at perpendicular
                    // angles around the start (Beta canonical's
                    // 4-branch start network).
                    int branchCount = 1;
                    if (rng.Next(4) == 0)
                    {
                        // 4 branches around the start point — Beta
                        // does this by emitting 4 recursive walks at
                        // ±π/2 yaw offsets.
                        WalkTunnel(c, anchorX, anchorZ, hash, startX, startY, startZ,
                            yaw: (float)(rng.NextDouble() * Math.PI * 2.0),
                            pitch: 0f,
                            radius: 1.0f + (float)rng.NextDouble() * 6.0f);
                        WalkTunnel(c, anchorX, anchorZ, hash + 1, startX, startY, startZ,
                            yaw: (float)(rng.NextDouble() * Math.PI * 2.0),
                            pitch: 0f,
                            radius: 1.0f + (float)rng.NextDouble() * 6.0f);
                        branchCount = 2; // 2 more emitted in the loop below
                    }

                    for (int b = 0; b < branchCount; b++)
                    {
                        float yaw = (float)(rng.NextDouble() * Math.PI * 2.0);
                        float pitch = ((float)rng.NextDouble() - 0.5f) * 0.25f;
                        float radius = 1.0f + (float)rng.NextDouble() * 6.0f;
                        WalkTunnel(c, anchorX, anchorZ, hash + 100 + b,
                            startX, startY, startZ, yaw, pitch, radius);
                    }
                }
            }
        }

        // Walk one tunnel along a parametric curve, carving cells
        // within a radius around each step's centre. The path uses
        // damped angle jitter so tunnels gracefully wind rather than
        // making sharp corners. Cells outside this chunk are skipped
        // (so neighbour chunks can carve their own slice of the same
        // path consistently).
        private static void WalkTunnel(Chunk c, int anchorX, int anchorZ, int subSeed,
            double curX, double curY, double curZ,
            float yaw, float pitch, float radius)
        {
            var rng = new Random(subSeed);
            // Length 30..150 steps (Beta uses up to 232; we cap shorter
            // because our 16×16 chunk is smaller relative to the path
            // and excess steps just waste cycles outside our bounds).
            int length = 30 + rng.Next(121);
            length -= rng.Next(length / 4);

            float yawDelta = 0f;
            float pitchDelta = 0f;

            for (int k = 0; k < length; k++)
            {
                // Per-step radius — taper at both ends via sin envelope.
                double radiusScale = 1.5 + Math.Sin(k * Math.PI / length) * radius;

                // Damped jitter on yaw + pitch — Beta canonical:
                //   yawDelta   *= 0.75, += (rand-rand)*rand*4
                //   pitchDelta *= 0.9,  += (rand-rand)*rand*2
                yawDelta   *= 0.75f;
                pitchDelta *= 0.9f;
                yawDelta   += ((float)rng.NextDouble() - (float)rng.NextDouble()) * (float)rng.NextDouble() * 4f;
                pitchDelta += ((float)rng.NextDouble() - (float)rng.NextDouble()) * (float)rng.NextDouble() * 4f; // 2→4: more vertical turns
                yaw   += yawDelta * 0.1f;
                // Reduced horizontal bias 0.7 → 0.92 so caves tunnel
                // upward and downward more freely — this is what
                // creates chasms connecting the cavern to the lava
                // ocean. 0.7 (Beta canonical) makes caves stay
                // horizontal and never bridge the cavern-floor /
                // lava-ocean boundary.
                pitch  = (pitch + pitchDelta * 0.1f) * 0.92f;

                // Step forward.
                double dx = Math.Cos(pitch) * Math.Cos(yaw);
                double dy = Math.Sin(pitch);
                double dz = Math.Cos(pitch) * Math.Sin(yaw);
                curX += dx;
                curY += dy;
                curZ += dz;

                // Carve a vertically-squashed sphere at this step.
                CarveSphere(c, curX, curY, curZ, radiusScale);

                // Y-bound — abort if path drifted out of the cavern.
                if (curY < MinY || curY > MaxY) return;
            }
        }

        // Carve cells within a vertically-squashed sphere centred at
        // (cx, cy, cz). Only cells inside this chunk's bounds get
        // touched; the path's neighbour-walk pattern handles cross-
        // chunk continuity. Bedrock is preserved (caves can't punch
        // through floor or ceiling caps).
        private static void CarveSphere(Chunk c, double cx, double cy, double cz, double radius)
        {
            int chunkX0 = c.ChunkX * Chunk.SizeX;
            int chunkZ0 = c.ChunkZ * Chunk.SizeZ;

            // AABB of the squashed sphere (vertical squashed by 0.5).
            int rXZ = (int)Math.Ceiling(radius);
            int rY  = (int)Math.Ceiling(radius * 0.5);

            int loX = Math.Max((int)Math.Floor(cx - rXZ), chunkX0);
            int hiX = Math.Min((int)Math.Floor(cx + rXZ), chunkX0 + Chunk.SizeX - 1);
            int loZ = Math.Max((int)Math.Floor(cz - rXZ), chunkZ0);
            int hiZ = Math.Min((int)Math.Floor(cz + rXZ), chunkZ0 + Chunk.SizeZ - 1);
            int loY = Math.Max((int)Math.Floor(cy - rY), MinY);
            int hiY = Math.Min((int)Math.Floor(cy + rY), MaxY - 1);
            if (loX > hiX || loZ > hiZ || loY > hiY) return;

            double invR  = 1.0 / radius;
            double invRY = 2.0 / radius;

            for (int wy = loY; wy <= hiY; wy++)
            {
                double fy = (wy + 0.5 - cy) * invRY;
                double fySq = fy * fy;
                if (fySq >= 1.0) continue;
                for (int wx = loX; wx <= hiX; wx++)
                {
                    double fx = (wx + 0.5 - cx) * invR;
                    double fxSq = fx * fx;
                    if (fxSq + fySq >= 1.0) continue;
                    for (int wz = loZ; wz <= hiZ; wz++)
                    {
                        double fz = (wz + 0.5 - cz) * invR;
                        if (fxSq + fySq + fz * fz >= 1.0) continue;
                        int lx = wx - chunkX0;
                        int lz = wz - chunkZ0;
                        var t = (BlockType)c.RawBlocks[Chunk.Index(lx, wy, lz)];
                        if (t == BlockType.Bedrock) continue;
                        c.Set(lx, wy, lz, BlockType.Air);
                    }
                }
            }
        }

        // Chasm pass — narrow vertical tubes carved from the open
        // cavern (above sea level) down through the netherrack mass
        // into the lava ocean. Each anchor chunk rolls 0..2 chasms.
        // Walk neighbours so chasms span chunk seams cleanly.
        private static void CarveChasms(Chunk c, int seed)
        {
            for (int ndz = -CaveAnchorRadius; ndz <= CaveAnchorRadius; ndz++)
            for (int ndx = -CaveAnchorRadius; ndx <= CaveAnchorRadius; ndx++)
            {
                int anchorX = c.ChunkX + ndx;
                int anchorZ = c.ChunkZ + ndz;
                int hash = (int)((uint)seed * 0xC4A57E91u
                    + (uint)(anchorX * 0xB39DA4F7)
                    + (uint)(anchorZ * 0x84A1C2D5));
                var rng = new Random(hash);

                int chasmCount = rng.Next(3); // 0..2 per anchor chunk
                for (int n = 0; n < chasmCount; n++)
                {
                    // Chasm center column (world coords).
                    double cx = anchorX * Chunk.SizeX + rng.Next(Chunk.SizeX);
                    double cz = anchorZ * Chunk.SizeZ + rng.Next(Chunk.SizeZ);
                    // Chasm width: small (2..4 blocks) so chasms read
                    // as cracks, not big open shafts.
                    double radius = 2.0 + rng.NextDouble() * 2.0;
                    // Mild horizontal drift across the vertical span
                    // so chasms aren't perfectly straight tubes.
                    double driftX = (rng.NextDouble() - 0.5) * 0.4;
                    double driftZ = (rng.NextDouble() - 0.5) * 0.4;
                    // Top of chasm — the open cavern floor (gy~64..88).
                    int yTop    = 80 + rng.Next(16);   // y in [80, 95]
                    // Bottom — well into the lava ocean (gy~4..15).
                    int yBottom = 6  + rng.Next(8);    // y in [6, 13]

                    for (int wy = yTop; wy >= yBottom; wy--)
                    {
                        double scaleAtY = 1.0;
                        // Taper to a point at the very top + bottom
                        // so the chasm has a "crack" silhouette.
                        if (wy > yTop - 4) scaleAtY = (yTop - wy) / 4.0;
                        else if (wy < yBottom + 2) scaleAtY = (wy - yBottom) / 2.0;
                        if (scaleAtY < 0.1) scaleAtY = 0.1;
                        CarveSphere(c, cx, wy, cz, radius * scaleAtY);
                        cx += driftX;
                        cz += driftZ;
                    }
                }
            }
        }
    }
}
