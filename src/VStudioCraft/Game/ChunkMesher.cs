using System;

namespace VStudioCraft.Game
{
    // Greedy voxel mesher (Mikola Lysenko-style). For each of the 6 face directions
    // we build a 2D slice mask keyed by "visible face of this tile" and pack the
    // largest rectangles we can out of same-keyed contiguous cells, emitting one
    // quad per rectangle. On an Alpha-style heightmap this typically cuts the
    // triangle count by 5-10x versus one-quad-per-block meshing.
    //
    // Not thread safe — callers serialize on this via a single meshing queue.
    internal sealed class ChunkMesher
    {
        // Flat vertex/index buffers reused across rebuilds so the hot path does no allocation.
        // Opaque stream — drawn in the depth-write pass.
        private float[] _verts = new float[1 << 14];
        private uint[] _indices = new uint[1 << 14];
        private int _vertFloats;
        private int _indexCount;

        // Transparent stream — drawn in a second pass with alpha blending.
        // Water (and eventually glass/leaves-fancy) go here.
        private float[] _tVerts = new float[1 << 12];
        private uint[] _tIndices = new uint[1 << 12];
        private int _tVertFloats;
        private int _tIndexCount;

        // Slice mask big enough for the largest face (SizeY × max(SizeX, SizeZ)).
        // Sign: positive = opaque face, negative = transparent face. 0 = empty.
        private readonly int[] _mask = new int[Math.Max(Chunk.SizeX, Chunk.SizeZ) * Chunk.SizeY];

        // Reusable corner scratch (per-quad), so EmitQuad doesn't allocate.
        private readonly float[] _c0 = new float[3];
        private readonly float[] _c1 = new float[3];
        private readonly float[] _c2 = new float[3];
        private readonly float[] _c3 = new float[3];

        public int VertexFloatCount => _vertFloats;
        public int IndexCount => _indexCount;
        public float[] Vertices => _verts;
        public uint[] Indices => _indices;

        public int TransparentVertexFloatCount => _tVertFloats;
        public int TransparentIndexCount => _tIndexCount;
        public float[] TransparentVertices => _tVerts;
        public uint[] TransparentIndices => _tIndices;

        public void Build(World world, Chunk chunk)
        {
            _vertFloats = 0;
            _indexCount = 0;
            _tVertFloats = 0;
            _tIndexCount = 0;

            int baseX = chunk.ChunkX * Chunk.SizeX;
            int baseZ = chunk.ChunkZ * Chunk.SizeZ;

            // Snapshot the four neighbour chunks once. GetBlock-through-World does a
            // dict lookup per call — with greedy meshing hitting ~Y*16 boundary reads
            // per chunk this is meaningful.
            var nxNeg = world.GetChunk(chunk.ChunkX - 1, chunk.ChunkZ);
            var nxPos = world.GetChunk(chunk.ChunkX + 1, chunk.ChunkZ);
            var nzNeg = world.GetChunk(chunk.ChunkX, chunk.ChunkZ - 1);
            var nzPos = world.GetChunk(chunk.ChunkX, chunk.ChunkZ + 1);

            // Six sweeps: axis X then Y then Z, each direction (+/-).
            Sweep(world, chunk, 0, +1, nxNeg, nxPos, nzNeg, nzPos, baseX, baseZ);
            Sweep(world, chunk, 0, -1, nxNeg, nxPos, nzNeg, nzPos, baseX, baseZ);
            Sweep(world, chunk, 1, +1, nxNeg, nxPos, nzNeg, nzPos, baseX, baseZ);
            Sweep(world, chunk, 1, -1, nxNeg, nxPos, nzNeg, nzPos, baseX, baseZ);
            Sweep(world, chunk, 2, +1, nxNeg, nxPos, nzNeg, nzPos, baseX, baseZ);
            Sweep(world, chunk, 2, -1, nxNeg, nxPos, nzNeg, nzPos, baseX, baseZ);

            // Model pass — scan for non-cube blocks and emit per-block sprite
            // geometry into the opaque stream (alpha-tested via shader discard).
            EmitModels(chunk, baseX, baseZ, nxNeg, nxPos, nzNeg, nzPos);

            // Fluid surface lids — for each non-falling flowing-fluid cell with
            // air directly above we drop the cube sweep's full-cube top face
            // (handled inside Sweep) and emit a custom inset top quad here at
            // a y proportional to the cell's remaining reach. The four corners
            // of each lid take the max water height of the four cells meeting
            // at that corner (Minecraft convention) — adjacent cells share the
            // same corner-cell set, so they compute identical heights at the
            // shared edge and the lids meet without a vertical gap.
            EmitFluidSurfaceLids(chunk, nxNeg, nxPos, nzNeg, nzPos, baseX, baseZ);
        }

        // Top-exposed flowing fluid cell? Source cells and falling cells stay
        // full-height (sources are full-cubes by definition; falling cells
        // visually need to fill the column they're streaming through, otherwise
        // a waterfall reads as floating disconnected slabs).
        internal static bool IsSurfaceFluid(Chunk chunk, int lx, int y, int lz)
        {
            int idx = Chunk.Index(lx, y, lz);
            var t = (BlockType)chunk.RawBlocks[idx];
            if (t != BlockType.FlowingWater && t != BlockType.FlowingLava) return false;
            if ((chunk.RawMeta[idx] & 0x10) != 0) return false;  // falling
            // Above out-of-world counts as air — surface.
            if (y + 1 >= Chunk.SizeY) return true;
            var above = (BlockType)chunk.RawBlocks[Chunk.Index(lx, y + 1, lz)];
            return above == BlockType.Air;
        }

        // Same predicate as IsSurfaceFluid but tolerates (lx, lz) outside the
        // center chunk's local range, fetching from the supplied neighbour
        // chunks. The cube sweep walks `slice` from 0..dAxis inclusive so the
        // source cell can be at lx=-1 / lx=Chunk.SizeX along its swept axis
        // — without this overload, surface-fluid cells that live one cell
        // across a chunk boundary fail the bounds-guarded IsSurfaceFluid call,
        // suppression is skipped, and the cube sweep emits a full-height side
        // face that overlaps the trapezoid the owning chunk drew on its own
        // edge. Visible as a square block of water sticking up out of a
        // shallow flow exactly where chunk seams sit.
        private static bool IsSurfaceFluidOrNeighbor(
            Chunk chunk, int lx, int y, int lz,
            Chunk nxNeg, Chunk nxPos, Chunk nzNeg, Chunk nzPos)
        {
            byte raw = BlockOrNeighbor(chunk, lx, y, lz, nxNeg, nxPos, nzNeg, nzPos);
            if (raw != (byte)BlockType.FlowingWater && raw != (byte)BlockType.FlowingLava)
                return false;
            byte meta = MetaOrNeighbor(chunk, lx, y, lz, nxNeg, nxPos, nzNeg, nzPos);
            if ((meta & 0x10) != 0) return false;            // falling
            if (y + 1 >= Chunk.SizeY) return true;           // top of world → surface
            byte above = BlockOrNeighbor(chunk, lx, y + 1, lz, nxNeg, nxPos, nzNeg, nzPos);
            return above == (byte)BlockType.Air;
        }

        // y position of the inset top face for a surface fluid cell. Reach
        // 0..6 maps to height 1/8..7/8 — far-from-source dribbles read as a
        // shallow puddle, fresh-from-source spread reads almost full.
        private static float SurfaceFluidHeight(byte meta)
        {
            int reach = meta & 0x0F;
            return (reach + 1) * (1f / 8f);
        }

        private void EmitFluidSurfaceLids(
            Chunk chunk,
            Chunk nxNeg, Chunk nxPos, Chunk nzNeg, Chunk nzPos,
            int baseX, int baseZ)
        {
            for (int x = 0; x < Chunk.SizeX; x++)
            for (int y = 0; y < Chunk.SizeY; y++)
            for (int z = 0; z < Chunk.SizeZ; z++)
            {
                if (!IsSurfaceFluid(chunk, x, y, z)) continue;
                int idx = Chunk.Index(x, y, z);
                var t = (BlockType)chunk.RawBlocks[idx];
                int group = BlockData.FluidGroup(t);

                // Each corner is shared with up to three diagonal/cardinal
                // neighbour cells. We compute its height as the max water
                // surface among the four cells that meet at that corner —
                // adjacent surface-fluid cells running this same calculation
                // see the same four-cell set at their shared corner and so
                // produce identical heights, which makes the slabs join
                // without the "stair step" gap between reach=N and reach=N-1
                // tiles.
                float hSW = CornerLidY(chunk, nxNeg, nxPos, nzNeg, nzPos, x,     y, z,     group);
                float hNW = CornerLidY(chunk, nxNeg, nxPos, nzNeg, nzPos, x,     y, z + 1, group);
                float hNE = CornerLidY(chunk, nxNeg, nxPos, nzNeg, nzPos, x + 1, y, z + 1, group);
                float hSE = CornerLidY(chunk, nxNeg, nxPos, nzNeg, nzPos, x + 1, y, z,     group);

                int layer = BlockData.GetTileIndex(t, 0);  // same tile all faces
                // Light at the air cell above (or full sky if at world top).
                int lightPacked = (y + 1 < Chunk.SizeY)
                    ? LightAt(chunk, x, y + 1, z)
                    : 15 * 16;
                bool transparent = (t == BlockType.FlowingWater);

                float wx = x + baseX, wz = z + baseZ, wy = y;
                EmitFluidLidQuad(wx, wz, hSW, hNW, hNE, hSE, layer, lightPacked, transparent);

                // Trapezoidal side faces for each air-facing edge. The cube
                // sweep's full-height side face was suppressed for these;
                // we emit one whose top tracks the sloped lid so the
                // end-cap respects the water level instead of being full-height.

                // +X east face (top corners SE and NE)
                if (BlockOrNeighbor(chunk, x + 1, y, z, nxNeg, nxPos, nzNeg, nzPos) == (byte)BlockType.Air)
                {
                    int lp = LightOrNeighbor(chunk, x + 1, y, z, nxNeg, nxPos, nzNeg, nzPos);
                    EmitFluidSideFace(
                        wx+1, wy,  wz,   0f, 0f,
                        wx+1, hSE, wz,   0f, hSE-wy,
                        wx+1, hNE, wz+1, 1f, hNE-wy,
                        wx+1, wy,  wz+1, 1f, 0f,
                        1f, 0f, 0f, layer, lp, transparent);
                }
                // -X west face (top corners SW and NW)
                if (BlockOrNeighbor(chunk, x - 1, y, z, nxNeg, nxPos, nzNeg, nzPos) == (byte)BlockType.Air)
                {
                    int lp = LightOrNeighbor(chunk, x - 1, y, z, nxNeg, nxPos, nzNeg, nzPos);
                    EmitFluidSideFace(
                        wx,  wy,  wz,   1f, 0f,
                        wx,  wy,  wz+1, 0f, 0f,
                        wx,  hNW, wz+1, 0f, hNW-wy,
                        wx,  hSW, wz,   1f, hSW-wy,
                        -1f, 0f, 0f, layer, lp, transparent);
                }
                // +Z north face (top corners NW and NE)
                if (BlockOrNeighbor(chunk, x, y, z + 1, nxNeg, nxPos, nzNeg, nzPos) == (byte)BlockType.Air)
                {
                    int lp = LightOrNeighbor(chunk, x, y, z + 1, nxNeg, nxPos, nzNeg, nzPos);
                    EmitFluidSideFace(
                        wx,   wy,  wz+1, 0f, 0f,
                        wx+1, wy,  wz+1, 1f, 0f,
                        wx+1, hNE, wz+1, 1f, hNE-wy,
                        wx,   hNW, wz+1, 0f, hNW-wy,
                        0f, 0f, 1f, layer, lp, transparent);
                }
                // -Z south face (top corners SW and SE)
                if (BlockOrNeighbor(chunk, x, y, z - 1, nxNeg, nxPos, nzNeg, nzPos) == (byte)BlockType.Air)
                {
                    int lp = LightOrNeighbor(chunk, x, y, z - 1, nxNeg, nxPos, nzNeg, nzPos);
                    EmitFluidSideFace(
                        wx+1, wy,  wz, 0f, 0f,
                        wx,   wy,  wz, 1f, 0f,
                        wx,   hSW, wz, 1f, hSW-wy,
                        wx+1, hSE, wz, 0f, hSE-wy,
                        0f, 0f, -1f, layer, lp, transparent);
                }
            }
        }

        // Returns the lid Y of a corner at chunk-local XZ (cx, cz), where the
        // central fluid cell sits with its bottom at world y. We sample the
        // four cells at (cx-1..cx, y, cz-1..cz) and only consider same-family
        // fluid contributions:
        //   - source / falling cell / column-filled (fluid above): 1.0
        //   - normal flowing cell: (reach+1)/8
        //   - anything else (air, solid block, cross-sprite): no contribution
        // Solid blocks are deliberately ignored so the water height tracks
        // only the fluid network — a stone wall next to a half-deep flow no
        // longer pulls the corner up to 1.0 ("clinging"), it just stays at
        // the surrounding water's level. The central cell is always one of
        // the four samples, so the result is at least its own height.
        private static float CornerLidY(
            Chunk chunk, Chunk nxNeg, Chunk nxPos, Chunk nzNeg, Chunk nzPos,
            int cx, int y, int cz, int group)
        {
            float maxH = 0f;
            bool sawFluid = false;
            for (int dx = -1; dx <= 0; dx++)
            for (int dz = -1; dz <= 0; dz++)
            {
                int sx = cx + dx, sz = cz + dz;
                byte raw = BlockOrNeighbor(chunk, sx, y, sz, nxNeg, nxPos, nzNeg, nzPos);
                if (raw == (byte)BlockType.Air) continue;
                var bt = (BlockType)raw;
                if (BlockData.FluidGroup(bt) != group) continue;

                float h;
                // Fluid in the cell above this sample → the column is
                // filled, surface is at the top of the cell.
                byte aboveRaw = BlockOrNeighbor(chunk, sx, y + 1, sz, nxNeg, nxPos, nzNeg, nzPos);
                if (aboveRaw != (byte)BlockType.Air
                    && BlockData.FluidGroup((BlockType)aboveRaw) == group)
                {
                    h = 1f;
                }
                else if (bt == BlockType.Water || bt == BlockType.Lava)
                {
                    // Source — full cube, surface at the top.
                    h = 1f;
                }
                else
                {
                    byte meta = MetaOrNeighbor(chunk, sx, y, sz, nxNeg, nxPos, nzNeg, nzPos);
                    if ((meta & 0x10) != 0) h = 1f;          // falling cell, fills column
                    else h = (((meta & 0x0F) + 1) * (1f / 8f));
                }
                if (h > maxH) maxH = h;
                sawFluid = true;
            }

            // Central cell is always sampled (dx=0, dz=0 hits the in-chunk
            // surface fluid cell), so sawFluid is normally true. Fallback to
            // the cell-bottom Y if for any reason no fluid was seen.
            if (!sawFluid) return y;
            return y + maxH;
        }

        private void EmitFluidLidQuad(
            float wx, float wz,
            float hSW, float hNW, float hNE, float hSE,
            int layer, int lightPacked, bool transparent)
        {
            int curVertFloats = transparent ? _tVertFloats : _vertFloats;
            uint baseIdx = (uint)(curVertFloats / Mesh.FloatsPerVertex);
            float light = lightPacked;
            // CCW when viewed from above so the geometric normal is +Y and
            // back-face culling keeps the lid visible from the sky. Corner
            // order SW → NW → NE → SE matches the cube sweep's top-face
            // winding (axis=1, dir=+1). With per-corner Y, the lid can be
            // sloped — the average of the four heights is still up-facing
            // because every corner is at most 1.0 above the cell base.
            AppendVert(transparent, wx + 0f, hSW, wz + 0f, 0f, 0f, 0f, 1f, 0f, layer, light);
            AppendVert(transparent, wx + 0f, hNW, wz + 1f, 0f, 1f, 0f, 1f, 0f, layer, light);
            AppendVert(transparent, wx + 1f, hNE, wz + 1f, 1f, 1f, 0f, 1f, 0f, layer, light);
            AppendVert(transparent, wx + 1f, hSE, wz + 0f, 1f, 0f, 0f, 1f, 0f, layer, light);
            AppendIndex(transparent, baseIdx + 0);
            AppendIndex(transparent, baseIdx + 1);
            AppendIndex(transparent, baseIdx + 2);
            AppendIndex(transparent, baseIdx + 0);
            AppendIndex(transparent, baseIdx + 2);
            AppendIndex(transparent, baseIdx + 3);
        }

        // Generic quad emitter for the four trapezoidal side faces of a surface
        // fluid cell. Caller provides all four corner positions and UVs; indices
        // always fan (0,1,2), (0,2,3). The normal is passed through to the shader
        // for face-shading — keep it perpendicular to the face plane.
        private void EmitFluidSideFace(
            float x0, float y0, float z0, float u0, float v0,
            float x1, float y1, float z1, float u1, float v1,
            float x2, float y2, float z2, float u2, float v2,
            float x3, float y3, float z3, float u3, float v3,
            float nx, float ny, float nz,
            int layer, int lightPacked, bool transparent)
        {
            uint baseIdx = (uint)((transparent ? _tVertFloats : _vertFloats) / Mesh.FloatsPerVertex);
            float light = lightPacked;
            AppendVert(transparent, x0, y0, z0, u0, v0, nx, ny, nz, layer, light);
            AppendVert(transparent, x1, y1, z1, u1, v1, nx, ny, nz, layer, light);
            AppendVert(transparent, x2, y2, z2, u2, v2, nx, ny, nz, layer, light);
            AppendVert(transparent, x3, y3, z3, u3, v3, nx, ny, nz, layer, light);
            AppendIndex(transparent, baseIdx + 0);
            AppendIndex(transparent, baseIdx + 1);
            AppendIndex(transparent, baseIdx + 2);
            AppendIndex(transparent, baseIdx + 0);
            AppendIndex(transparent, baseIdx + 2);
            AppendIndex(transparent, baseIdx + 3);
        }

        // Cross-sprite "X" model: two perpendicular vertical quads through the
        // cell centre, each rendered double-sided so the player sees the torch
        // from any angle. Used today by torches; flowers / mushrooms / tall
        // grass will reuse this geometry once their tiles are added.
        private void EmitModels(
            Chunk chunk, int baseX, int baseZ,
            Chunk nxNeg, Chunk nxPos, Chunk nzNeg, Chunk nzPos)
        {
            for (int x = 0; x < Chunk.SizeX; x++)
            for (int y = 0; y < Chunk.SizeY; y++)
            for (int z = 0; z < Chunk.SizeZ; z++)
            {
                byte b = chunk.RawBlocks[Chunk.Index(x, y, z)];
                if (b == (byte)BlockType.Air) continue;
                var t = (BlockType)b;
                if (BlockData.IsCubeShape(t)) continue;

                int layer = BlockData.GetTileIndex(t, 2);
                // Tier 4 #14 — Wheat picks its tile per-cell based on
                // the metadata growth stage in the low 4 bits, so each
                // wheat block in a chunk shows its own ripening state.
                // Other cross-sprite blocks (flowers, mushrooms,
                // saplings) keep the static layer above.
                if (t == BlockType.Wheat)
                {
                    byte meta = chunk.RawMeta[Chunk.Index(x, y, z)];
                    layer = BlockData.GetWheatTileForStage(meta);
                }
                int lightPacked = LightAt(chunk, x, y, z);
                if (BlockData.IsWallTorch(t))
                {
                    // Tilted 3D box — same 2×N×2 wood-column geometry
                    // as the floor torch, but rotated so the shaft
                    // axis runs from a base anchor on the wall up to
                    // a tip leaning toward the cell centre. Replaces
                    // the older tilted cross-sprite render so the
                    // wall torch reads as a true 3D pillar at any
                    // viewing angle, matching the floor torch.
                    EmitWallTorchBox(x + baseX, y, z + baseZ, t, lightPacked);
                }
                else if (BlockData.IsDoor(t))
                {
                    // Tier 4 #16 — Door slab. Read the per-cell metadata
                    // for facing + open + hinge state, then render a
                    // 3/16-deep quad pinned to the appropriate wall of
                    // the cell. The mesher is the only place that
                    // needs to interpret the open-flag for geometry —
                    // the placement / interact paths set the byte and
                    // re-mesh the chunk; nothing else inspects it.
                    byte meta = chunk.RawMeta[Chunk.Index(x, y, z)];
                    EmitDoorSlab(x + baseX, y, z + baseZ, t, meta, layer, lightPacked);
                }
                else if (t == BlockType.SnowBlock)
                {
                    // Tier 6 #37 Phase 4 — Snow layer. 1/8-tall slab
                    // pinned to the bottom of the cell, six box faces
                    // textured with TileSnow on every face. Same
                    // overall structure as EmitDoorSlab but with a
                    // fixed full-X/full-Z footprint and a 0.125 Y
                    // height.
                    EmitSnowLayer(x + baseX, y, z + baseZ, layer, lightPacked);
                }
                else if (t == BlockType.RedstoneWire)
                {
                    // Tier 8 #42 — Redstone wire reuses the 1/8 slab
                    // emitter for now (slab thickness is constant
                    // across the slab path; wire visually reads
                    // as a thin red strip on the floor). Per-direction
                    // routed wire variants are a follow-up needing
                    // neighbour-aware UV picking.
                    EmitSnowLayer(x + baseX, y, z + baseZ,
                        BlockTextures.TileRedstoneWire, lightPacked);
                }
                else if (t == BlockType.StonePressurePlate
                      || t == BlockType.WoodPressurePlate)
                {
                    // Tier 8 #42 — Pressure plates: 1/16 floor slab
                    // (slightly inset on X/Z by 1 pixel each side
                    // forming the canonical 14×1×14 footprint). The
                    // existing snow-layer 1/8 emitter is too thick
                    // and doesn't inset, so use the dedicated thin-
                    // slab path below.
                    int plateLayer = (t == BlockType.WoodPressurePlate)
                        ? BlockTextures.TilePlanks
                        : BlockTextures.TileStone;
                    EmitPressurePlate(x + baseX, y, z + baseZ, plateLayer, lightPacked);
                }
                else if (t == BlockType.Lever)
                {
                    EmitLeverBox(x + baseX, y, z + baseZ, lightPacked);
                }
                else if (t == BlockType.StoneButton)
                {
                    EmitButtonBox(x + baseX, y, z + baseZ, lightPacked);
                }
                else if (t == BlockType.Cactus)
                {
                    // Tier 6 #37 — Cactus 12×16×12 inset box with
                    // hash-overlap sides. Light is sampled per-face
                    // from the OUTWARD neighbour (same convention
                    // the cube mesher uses) — sampling at the cactus
                    // cell itself would always be black because the
                    // cell is light-blocking.
                    EmitCactusBox(
                        x + baseX, y, z + baseZ,
                        chunk, x, y, z,
                        nxNeg, nxPos, nzNeg, nzPos);
                }
                else if (t == BlockType.Torch)
                {
                    // Floor torch — 2×10×2 wood column standing in
                    // the centre of the cell. Replaces the older
                    // cross-sprite render so the torch reads as a
                    // proper 3D pillar, with the wood column visible
                    // from any angle (the cross-sprite had a
                    // washed-out look at oblique angles where both
                    // crossed quads showed the same flat sprite).
                    EmitTorchBox(x + baseX, y, z + baseZ, lightPacked);
                }
                else if (t == BlockType.SignPost)
                {
                    // Tier 8 #44 — Post sign. Read facing from the
                    // chunk's metadata byte (low 2 bits → cardinal
                    // direction the writing faces). The placement
                    // path stamps the meta from player yaw the same
                    // way doors and pumpkins do.
                    byte signMeta = chunk.RawMeta[Chunk.Index(x, y, z)];
                    EmitSignPost(x + baseX, y, z + baseZ, signMeta, lightPacked);
                }
                else if (t == BlockType.WallSign)
                {
                    byte signMeta = chunk.RawMeta[Chunk.Index(x, y, z)];
                    EmitWallSign(x + baseX, y, z + baseZ, signMeta, lightPacked);
                }
                else if (t == BlockType.Ladder)
                {
                    // Tier 8 #46 — Ladder is a thin wall-hugging
                    // double-sided quad. Meta low-2-bits = facing
                    // (which wall the ladder is attached to).
                    byte ladderMeta = chunk.RawMeta[Chunk.Index(x, y, z)];
                    EmitLadder(x + baseX, y, z + baseZ, ladderMeta, layer, lightPacked);
                }
                else
                {
                    EmitCrossSprite(x + baseX, y, z + baseZ, layer, lightPacked);
                }
            }
        }

        // Wall-torch 3D box. 2×N×2 wood column (same cross-section as
        // the floor torch) tilted so the shaft axis runs from a base
        // anchor on the wall up to a tip leaning toward the cell
        // centre. Same shaft endpoints + lean angle as the older
        // cross-sprite version, just emitted as a real 6-face box.
        //
        // The mounting wall is the cell face opposite the torch's
        // facing direction. e.g. TorchEast (faces +X) is mounted on
        // the cell's -X face. The box's "right" axis runs along the
        // wall (perpendicular to facing), the "up" axis is the
        // tilted shaft, and the "fwd" axis is the cross product of
        // the two — perpendicular to both, mostly pointing out from
        // the wall in the same plane as the lean.
        //
        // UVs sample only the centre 2-pixel-wide column of the
        // torch tile (U:[7/16..9/16]) at full wood height
        // (V:[0..10/16]) — same trick as the floor torch, so the
        // wood texture renders correctly on the narrow side faces.
        private void EmitWallTorchBox(float wx, float wy, float wz, BlockType type, int lightPacked)
        {
            BlockFacing f = BlockData.WallTorchFacing(type);
            float dx = 0f, dz = 0f;
            switch (f)
            {
                case BlockFacing.East:  dx = +1f; break;
                case BlockFacing.West:  dx = -1f; break;
                case BlockFacing.South: dz = +1f; break;
                default:                dz = -1f; break; // North
            }

            // Same shaft endpoints as the cross-sprite version.
            // Base anchor sits 0.05 from the wall (slight overshoot
            // into the wall block, occluded by its solid faces).
            // Tip leans toward the cell centre (0.45 from the wall).
            float bx = wx + 0.5f - dx * 0.45f;
            float by = wy + 0.2f;
            float bz = wz + 0.5f - dz * 0.45f;
            float tx = wx + 0.5f - dx * 0.05f;
            float ty = wy + 0.9f;
            float tz = wz + 0.5f - dz * 0.05f;

            // 2-pixel-wide cross section: half = 1/16 along each
            // perpendicular axis.
            const float halfW = 1f / 16f;

            // Width axis (along the wall, perpendicular to facing).
            float wdx = -dz * halfW;
            float wdz = dx * halfW;

            // Shaft vector (base → tip).
            float sx = tx - bx, sy = ty - by, sz = tz - bz;

            // Depth axis: perpendicular to both shaft and width via
            // cross product, normalised then scaled to halfW. Mostly
            // points OUT of the wall in the lean plane.
            float wux = -dz, wuz = dx;             // unit-length width
            float dpx = sy * wuz;
            float dpy = sz * wux - sx * wuz;
            float dpz = -sy * wux;
            float dLen = (float)Math.Sqrt(dpx * dpx + dpy * dpy + dpz * dpz);
            if (dLen > 1e-6f) { dpx /= dLen; dpy /= dLen; dpz /= dLen; }
            dpx *= halfW; dpy *= halfW; dpz *= halfW;

            // 8 box corners. Naming: B = base, T = tip.
            // Suffix wd encodes (width sign, depth sign).
            float Bmm_x = bx - wdx - dpx, Bmm_y = by - dpy,        Bmm_z = bz - wdz - dpz;
            float Bmp_x = bx - wdx + dpx, Bmp_y = by + dpy,        Bmp_z = bz - wdz + dpz;
            float Bpm_x = bx + wdx - dpx, Bpm_y = by - dpy,        Bpm_z = bz + wdz - dpz;
            float Bpp_x = bx + wdx + dpx, Bpp_y = by + dpy,        Bpp_z = bz + wdz + dpz;
            float Tmm_x = tx - wdx - dpx, Tmm_y = ty - dpy,        Tmm_z = tz - wdz - dpz;
            float Tmp_x = tx - wdx + dpx, Tmp_y = ty + dpy,        Tmp_z = tz - wdz + dpz;
            float Tpm_x = tx + wdx - dpx, Tpm_y = ty - dpy,        Tpm_z = tz + wdz - dpz;
            float Tpp_x = tx + wdx + dpx, Tpp_y = ty + dpy,        Tpp_z = tz + wdz + dpz;

            int layer = BlockTextures.TileTorch;
            const float uColLo = 7f / 16f, uColHi = 9f / 16f;
            const float vBase  = 0f, vTop = 10f / 16f;
            const float vCapLo = 8f / 16f, vCapHi = 10f / 16f;
            const float vBotHi = 2f / 16f;

            // Face normals — used only for shading bias, the actual
            // outward normal of each face is implicit in the winding.
            // Force +Y on all so the brightest top-face shading bias
            // applies consistently across the tilted shaft (matches
            // the per-face shading the cross-sprite version used).
            const float nx = 0f, ny = 1f, nz = 0f;

            // The local box frame here is left-handed
            // (depth = shaft × width, so width × up = -depth instead
            // of the +depth a right-handed cyclic convention would
            // give). The original windings emitted faces CW from
            // outside — invisible front-facing-from-inside. Each
            // face below is wound the opposite of the obvious
            // (Bmm, Bmp, Tmp, Tmm)-style loop so the outward normal
            // per face actually points outward.

            // -W face (low width)
            EmitCrossQuad(
                Bmm_x, Bmm_y, Bmm_z, uColHi, vBase,
                Tmm_x, Tmm_y, Tmm_z, uColHi, vTop,
                Tmp_x, Tmp_y, Tmp_z, uColLo, vTop,
                Bmp_x, Bmp_y, Bmp_z, uColLo, vBase,
                nx, ny, nz, layer, lightPacked);
            // +W face (high width)
            EmitCrossQuad(
                Bpm_x, Bpm_y, Bpm_z, uColLo, vBase,
                Bpp_x, Bpp_y, Bpp_z, uColHi, vBase,
                Tpp_x, Tpp_y, Tpp_z, uColHi, vTop,
                Tpm_x, Tpm_y, Tpm_z, uColLo, vTop,
                nx, ny, nz, layer, lightPacked);
            // -D face (low depth)
            EmitCrossQuad(
                Bmm_x, Bmm_y, Bmm_z, uColLo, vBase,
                Bpm_x, Bpm_y, Bpm_z, uColHi, vBase,
                Tpm_x, Tpm_y, Tpm_z, uColHi, vTop,
                Tmm_x, Tmm_y, Tmm_z, uColLo, vTop,
                nx, ny, nz, layer, lightPacked);
            // +D face (high depth)
            EmitCrossQuad(
                Bmp_x, Bmp_y, Bmp_z, uColLo, vBase,
                Tmp_x, Tmp_y, Tmp_z, uColLo, vTop,
                Tpp_x, Tpp_y, Tpp_z, uColHi, vTop,
                Bpp_x, Bpp_y, Bpp_z, uColHi, vBase,
                nx, ny, nz, layer, lightPacked);
            // +U face (tip cap)
            EmitCrossQuad(
                Tmm_x, Tmm_y, Tmm_z, uColLo, vCapLo,
                Tpm_x, Tpm_y, Tpm_z, uColHi, vCapLo,
                Tpp_x, Tpp_y, Tpp_z, uColHi, vCapHi,
                Tmp_x, Tmp_y, Tmp_z, uColLo, vCapHi,
                nx, ny, nz, layer, lightPacked);
            // -U face (base)
            EmitCrossQuad(
                Bmm_x, Bmm_y, Bmm_z, uColLo, vBase,
                Bmp_x, Bmp_y, Bmp_z, uColLo, vBotHi,
                Bpp_x, Bpp_y, Bpp_z, uColHi, vBotHi,
                Bpm_x, Bpm_y, Bpm_z, uColHi, vBase,
                nx, ny, nz, layer, lightPacked);
        }

        // Tier 4 #16 — Door slab geometry. A door is a thin (3/16-deep)
        // axis-aligned box pinned to one wall of the cell, with the
        // wall picked by (facing, open) state from the metadata byte:
        //
        //   closed: the slab sits flush with the wall whose outward
        //           normal matches the door's facing — i.e. a North-
        //           facing door's slab is at z=0..3/16. The player
        //           crosses the threshold IN the facing direction.
        //
        //   open:   the door rotates 90° about the hinge edge, so the
        //           slab moves to a perpendicular wall. With hinge=left
        //           it rotates one way, hinge=right rotates the other.
        //           The hinge edge stays fixed; the free edge swings
        //           through the cell into the perpendicular wall.
        //
        // We render the slab as a true 6-face box (4 broad sides +
        // 2 thin edges) so the player can see the slab thickness when
        // viewed from oblique angles. All six faces sample the same
        // door tile (top half / bottom half is already encoded in the
        // BlockType); mismatched UVs on the thin sides aren't an
        // issue because they're 3/16 of a pixel of contiguous wood/
        // iron palette anyway.
        //
        // Normals point outward from the slab volume so the lighting
        // path picks the correct cell-light value per face. Each quad
        // is emitted ONCE — back-face culling + the meshed slab being
        // a closed box means the player only ever sees the outward
        // faces.
        // Tier 8 #46 — Ladder. Thin (1/16) double-sided quad pinned
        // to whichever wall the ladder's facing-meta says it hugs.
        // Single tile (TileLadder); the wood-frame rails + rungs are
        // alpha-cut on the source so the wall behind the ladder shows
        // through. Emit one quad on each side of the ladder plane so
        // the climber sees the rungs from inside the cell AND the
        // player on the far side of a thin wall would see them too.
        // (Cheaper than a 6-face box and avoids the back-face culling
        // gotcha the door slab originally tripped over.)
        private void EmitLadder(float wx, float wy, float wz, byte meta, int layer, int lightPacked)
        {
            BlockFacing f = (BlockFacing)(meta & 0x03);
            // Inset away from the wall by 1/16 so z-fighting doesn't
            // chase the wall texture through the alpha-cut gaps.
            const float pad = 1f / 16f;

            // Pick the plane the ladder sits on, and the two
            // outward-normal directions for the front + back quads.
            // The ladder plane is parallel to the wall — for an East-
            // facing ladder (mounted on the WEST wall of the cell, so
            // the climber faces East away from the wall), the plane
            // sits at x = wx + pad and the front-face normal points
            // +X (away from the wall). The back face's normal points
            // -X (into the wall). North-facing → wall is South, plane
            // at z = wz + 1 - pad, front normal -Z. Etc.
            float x0 = wx, x1 = wx + 1f;
            float y0 = wy, y1 = wy + 1f;
            float z0 = wz, z1 = wz + 1f;

            switch (f)
            {
                case BlockFacing.North: // climber faces North (-Z); ladder on +Z wall, plane at high Z.
                    z0 = z1 = wz + 1f - pad;
                    // Front face (visible from inside the cell, normal -Z)
                    EmitCrossQuad(
                        wx + 1f, y0, z0, 0f, 0f,
                        wx,      y0, z0, 1f, 0f,
                        wx,      y1, z0, 1f, 1f,
                        wx + 1f, y1, z0, 0f, 1f,
                        0f, 0f, -1f, layer, lightPacked);
                    // Back face (visible through the wall, normal +Z)
                    EmitCrossQuad(
                        wx,      y0, z0, 0f, 0f,
                        wx + 1f, y0, z0, 1f, 0f,
                        wx + 1f, y1, z0, 1f, 1f,
                        wx,      y1, z0, 0f, 1f,
                        0f, 0f, +1f, layer, lightPacked);
                    break;
                case BlockFacing.South: // climber faces South (+Z); ladder on -Z wall, plane at low Z.
                    z0 = z1 = wz + pad;
                    EmitCrossQuad(
                        wx,      y0, z0, 0f, 0f,
                        wx + 1f, y0, z0, 1f, 0f,
                        wx + 1f, y1, z0, 1f, 1f,
                        wx,      y1, z0, 0f, 1f,
                        0f, 0f, +1f, layer, lightPacked);
                    EmitCrossQuad(
                        wx + 1f, y0, z0, 0f, 0f,
                        wx,      y0, z0, 1f, 0f,
                        wx,      y1, z0, 1f, 1f,
                        wx + 1f, y1, z0, 0f, 1f,
                        0f, 0f, -1f, layer, lightPacked);
                    break;
                case BlockFacing.East: // climber faces East (+X); ladder on -X wall, plane at low X.
                    x0 = x1 = wx + pad;
                    EmitCrossQuad(
                        x0, y0, wz + 1f, 0f, 0f,
                        x0, y0, wz,      1f, 0f,
                        x0, y1, wz,      1f, 1f,
                        x0, y1, wz + 1f, 0f, 1f,
                        +1f, 0f, 0f, layer, lightPacked);
                    EmitCrossQuad(
                        x0, y0, wz,      0f, 0f,
                        x0, y0, wz + 1f, 1f, 0f,
                        x0, y1, wz + 1f, 1f, 1f,
                        x0, y1, wz,      0f, 1f,
                        -1f, 0f, 0f, layer, lightPacked);
                    break;
                default: // West — climber faces West (-X); ladder on +X wall, plane at high X.
                    x0 = x1 = wx + 1f - pad;
                    EmitCrossQuad(
                        x0, y0, wz,      0f, 0f,
                        x0, y0, wz + 1f, 1f, 0f,
                        x0, y1, wz + 1f, 1f, 1f,
                        x0, y1, wz,      0f, 1f,
                        -1f, 0f, 0f, layer, lightPacked);
                    EmitCrossQuad(
                        x0, y0, wz + 1f, 0f, 0f,
                        x0, y0, wz,      1f, 0f,
                        x0, y1, wz,      1f, 1f,
                        x0, y1, wz + 1f, 0f, 1f,
                        +1f, 0f, 0f, layer, lightPacked);
                    break;
            }
        }

        private void EmitDoorSlab(float wx, float wy, float wz, BlockType type, byte meta, int layer, int lightPacked)
        {
            const float thick = 3f / 16f;
            BlockFacing f = BlockData.DoorFacing(meta);
            bool open = BlockData.DoorIsOpen(meta);
            bool hingeRight = BlockData.DoorHingeRight(meta);

            // Resolve which of the 4 walls the slab actually pins to.
            // When the door is OPEN, swing the wall pin 90° about the
            // hinge — direction of swing depends on hinge side.
            BlockFacing slabWall = f;
            if (open)
            {
                if (hingeRight)
                {
                    // Right-hinge swings counter-clockwise viewed from
                    // above (e.g. North-facing door swings to East).
                    switch (f)
                    {
                        case BlockFacing.North: slabWall = BlockFacing.East;  break;
                        case BlockFacing.East:  slabWall = BlockFacing.South; break;
                        case BlockFacing.South: slabWall = BlockFacing.West;  break;
                        default:                slabWall = BlockFacing.North; break; // West
                    }
                }
                else
                {
                    // Left-hinge swings clockwise viewed from above.
                    switch (f)
                    {
                        case BlockFacing.North: slabWall = BlockFacing.West;  break;
                        case BlockFacing.East:  slabWall = BlockFacing.North; break;
                        case BlockFacing.South: slabWall = BlockFacing.East;  break;
                        default:                slabWall = BlockFacing.South; break; // West
                    }
                }
            }

            // Build the slab AABB inside the cell. The slab fills the
            // full Y of the cell (top half + bottom half each render
            // their own slab over their own cell's Y range — stacked
            // they form the full 2-tall door silhouette).
            float x0, x1, z0, z1;
            switch (slabWall)
            {
                case BlockFacing.North:
                    x0 = wx + 0f;     x1 = wx + 1f;
                    z0 = wz + 0f;     z1 = wz + thick;
                    break;
                case BlockFacing.South:
                    x0 = wx + 0f;     x1 = wx + 1f;
                    z0 = wz + (1f - thick); z1 = wz + 1f;
                    break;
                case BlockFacing.East:
                    x0 = wx + (1f - thick); x1 = wx + 1f;
                    z0 = wz + 0f;     z1 = wz + 1f;
                    break;
                default: // West
                    x0 = wx + 0f;     x1 = wx + thick;
                    z0 = wz + 0f;     z1 = wz + 1f;
                    break;
            }
            float y0 = wy + 0f;
            float y1 = wy + 1f;

            // Six box faces with proper outward CCW winding (matches
            // the cube mesher's EmitQuad dir>0 / dir<0 split + the
            // snow-slab pattern). Earlier revisions used the dir>0
            // order on every face — the comment in EmitSnowLayer
            // calls this out explicitly: with back-face culling on,
            // the broad faces ended up CW from the outside view and
            // GL silently culled them, so the player saw THROUGH the
            // outer face onto the now-front-facing back face — the
            // canonical "door texture visible on the inside" bug.
            //
            // UVs: broad faces (1×1 in world space) sample the full
            // door tile [0..1]. Thin edges sample a 3/16-wide strip
            // — vertical edges pick the hinge column (U ∈ [0, 3/16],
            // a solid wood post on both wood + iron tiles), top /
            // bottom edges pick the frame band at the top of the
            // tile (V ∈ [1 − 3/16, 1] — PNG row 0 after CopyTile
            // flip = canonical Alpha door-frame band).
            //
            // Broad / thin assignment depends on slabWall:
            //   N/S-pinned (z range = thick) → ±Z broad, ±X / ±Y thin
            //   E/W-pinned (x range = thick) → ±X broad, ±Z / ±Y thin
            const float u3 = 3f / 16f;
            bool nsPinned = slabWall == BlockFacing.North || slabWall == BlockFacing.South;

            if (nsPinned)
            {
                // -Z broad face (dir<0): (X_min,Y_min) → (X_min,Y_max)
                // → (X_max,Y_max) → (X_max,Y_min)
                EmitCrossQuad(
                    x0, y0, z0, 0f, 0f,
                    x0, y1, z0, 0f, 1f,
                    x1, y1, z0, 1f, 1f,
                    x1, y0, z0, 1f, 0f,
                    0f, 0f, -1f, layer, lightPacked);
                // +Z broad face (dir>0): (X_min,Y_min) → (X_max,Y_min)
                // → (X_max,Y_max) → (X_min,Y_max)
                EmitCrossQuad(
                    x0, y0, z1, 0f, 0f,
                    x1, y0, z1, 1f, 0f,
                    x1, y1, z1, 1f, 1f,
                    x0, y1, z1, 0f, 1f,
                    0f, 0f, +1f, layer, lightPacked);
                // -X thin edge (dir<0): (Y_min,Z_min) → (Y_min,Z_max)
                // → (Y_max,Z_max) → (Y_max,Z_min). U axis = slab depth (Z),
                // V axis = vertical (Y) — full V, narrow U from hinge column.
                EmitCrossQuad(
                    x0, y0, z0, 0f, 0f,
                    x0, y0, z1, u3, 0f,
                    x0, y1, z1, u3, 1f,
                    x0, y1, z0, 0f, 1f,
                    -1f, 0f, 0f, layer, lightPacked);
                // +X thin edge (dir>0): (Y_min,Z_min) → (Y_max,Z_min)
                // → (Y_max,Z_max) → (Y_min,Z_max)
                EmitCrossQuad(
                    x1, y0, z0, 0f, 0f,
                    x1, y1, z0, 0f, 1f,
                    x1, y1, z1, u3, 1f,
                    x1, y0, z1, u3, 0f,
                    +1f, 0f, 0f, layer, lightPacked);
                // +Y thin edge (top of slab, dir>0): U axis = X (full),
                // V axis = slab depth (Z mapped to a narrow top-frame strip).
                EmitCrossQuad(
                    x0, y1, z1, 0f, 1f - u3,
                    x1, y1, z1, 1f, 1f - u3,
                    x1, y1, z0, 1f, 1f,
                    x0, y1, z0, 0f, 1f,
                    0f, +1f, 0f, layer, lightPacked);
                // -Y thin edge (bottom of slab, dir<0)
                EmitCrossQuad(
                    x0, y0, z0, 0f, 1f,
                    x1, y0, z0, 1f, 1f,
                    x1, y0, z1, 1f, 1f - u3,
                    x0, y0, z1, 0f, 1f - u3,
                    0f, -1f, 0f, layer, lightPacked);
            }
            else
            {
                // E/W-pinned: ±X broad. -X face (dir<0) — door front when
                // slabWall=West. (Y_min,Z_min) → (Y_min,Z_max) → (Y_max,Z_max)
                // → (Y_max,Z_min) with U=Z, V=Y for full upright tile.
                EmitCrossQuad(
                    x0, y0, z0, 0f, 0f,
                    x0, y0, z1, 1f, 0f,
                    x0, y1, z1, 1f, 1f,
                    x0, y1, z0, 0f, 1f,
                    -1f, 0f, 0f, layer, lightPacked);
                // +X broad face (dir>0)
                EmitCrossQuad(
                    x1, y0, z0, 0f, 0f,
                    x1, y1, z0, 0f, 1f,
                    x1, y1, z1, 1f, 1f,
                    x1, y0, z1, 1f, 0f,
                    +1f, 0f, 0f, layer, lightPacked);
                // -Z thin edge (dir<0): U axis = X (slab depth, narrow),
                // V axis = Y (full).
                EmitCrossQuad(
                    x0, y0, z0, 0f, 0f,
                    x0, y1, z0, 0f, 1f,
                    x1, y1, z0, u3, 1f,
                    x1, y0, z0, u3, 0f,
                    0f, 0f, -1f, layer, lightPacked);
                // +Z thin edge (dir>0)
                EmitCrossQuad(
                    x0, y0, z1, 0f, 0f,
                    x1, y0, z1, u3, 0f,
                    x1, y1, z1, u3, 1f,
                    x0, y1, z1, 0f, 1f,
                    0f, 0f, +1f, layer, lightPacked);
                // +Y thin edge (top of slab, dir>0): U axis = Z (full),
                // V axis = X mapped to a top-frame strip.
                EmitCrossQuad(
                    x0, y1, z1, 0f, 1f,
                    x1, y1, z1, 0f, 1f - u3,
                    x1, y1, z0, 1f, 1f - u3,
                    x0, y1, z0, 1f, 1f,
                    0f, +1f, 0f, layer, lightPacked);
                // -Y thin edge (bottom of slab, dir<0)
                EmitCrossQuad(
                    x0, y0, z0, 1f, 1f,
                    x1, y0, z0, 1f, 1f - u3,
                    x1, y0, z1, 0f, 1f - u3,
                    x0, y0, z1, 0f, 1f,
                    0f, -1f, 0f, layer, lightPacked);
            }
        }

        // Tier 6 #37 Phase 4 — Snow layer slab. 1×0.125×1 box pinned to
        // the cell bottom, fully filling X/Z so the cube footprint
        // matches the cell. Top + bottom faces use the full TileSnow
        // tile [0..1] UVs. The four lateral faces sample the FIRST
        // two pixel rows of the source PNG snow tile (the dense band
        // along the top of the canonical Alpha snow tile) so the
        // strip reads as solid snow rather than the see-through
        // half-empty bottom rows. CopyTile flips PNGs vertically
        // when slicing (PNG row 0 lands at dst row 15) so V=1.0 in
        // OpenGL maps to PNG row 0 — sampling V from (1 - 2/16) to
        // (1 - 1/256) picks up PNG rows 0..1 without crossing the
        // V=1 boundary where the Repeat wrap would land back on the
        // opposite (empty) edge.
        //
        // Winding flip on the side faces — for negative-direction
        // faces the cube mesher reverses the vertex order to keep
        // the outward face CCW (see EmitQuad's dir<0 branch). The
        // door slab's earlier face emit got away with the +dir order
        // on both signs because the door covers the whole cell
        // (camera typically inside a doorway sees the front-facing
        // side anyway), but the snow slab is a thin strip and any
        // back-facing strip shows as a transparent band. We emit
        // each side using positive-dir winding for +X / +Z and
        // reversed (CCW from outside) for -X / -Z.
        private void EmitSnowLayer(float wx, float wy, float wz, int layer, int lightPacked)
        {
            const float thick = 1f / 8f;
            float x0 = wx + 0f;
            float x1 = wx + 1f;
            float z0 = wz + 0f;
            float z1 = wz + 1f;
            float y0 = wy + 0f;
            float y1 = wy + thick;

            // V range for side faces — top 2 pixel rows of the
            // texture (first 2 rows of the source PNG after the
            // CopyTile flip). Stops one half-texel short of 1.0 so
            // the Repeat wrap mode doesn't bleed in the opposite
            // edge texel at the top vertex.
            const float sideV0 = 1f - 2f / 16f;             // 0.875
            const float sideV1 = 1f - 0.5f / 16f;           // ~0.969

            // Side-face windings mirror the cube mesher's `EmitQuad`
            // dir>0 / dir<0 split — for negative-direction faces the
            // vertex order is reversed so the outward face stays
            // CCW. Earlier revisions copied the door slab's order
            // (which used dir>0 winding for both +X and -X) and the
            // result was visible -X but back-facing +X.
            //
            // -X face (dir<0): traverse (Y_min, Z_min) → (Y_min, Z_max)
            // → (Y_max, Z_max) → (Y_max, Z_min)
            EmitCrossQuad(
                x0, y0, z0, 0f, sideV0,
                x0, y0, z1, 1f, sideV0,
                x0, y1, z1, 1f, sideV1,
                x0, y1, z0, 0f, sideV1,
                -1f, 0f, 0f, layer, lightPacked);
            // +X face (dir>0): traverse (Y_min, Z_min) → (Y_max, Z_min)
            // → (Y_max, Z_max) → (Y_min, Z_max)
            EmitCrossQuad(
                x1, y0, z0, 1f, sideV0,
                x1, y1, z0, 1f, sideV1,
                x1, y1, z1, 0f, sideV1,
                x1, y0, z1, 0f, sideV0,
                +1f, 0f, 0f, layer, lightPacked);
            // -Z face (dir<0): traverse (X_min, Y_min) → (X_min, Y_max)
            // → (X_max, Y_max) → (X_max, Y_min)
            EmitCrossQuad(
                x0, y0, z0, 0f, sideV0,
                x0, y1, z0, 0f, sideV1,
                x1, y1, z0, 1f, sideV1,
                x1, y0, z0, 1f, sideV0,
                0f, 0f, -1f, layer, lightPacked);
            // +Z face (dir>0): traverse (X_min, Y_min) → (X_max, Y_min)
            // → (X_max, Y_max) → (X_min, Y_max)
            EmitCrossQuad(
                x0, y0, z1, 0f, sideV0,
                x1, y0, z1, 1f, sideV0,
                x1, y1, z1, 1f, sideV1,
                x0, y1, z1, 0f, sideV1,
                0f, 0f, +1f, layer, lightPacked);
            // +Y face (top)
            EmitCrossQuad(
                x0, y1, z1, 0f, 0f,
                x1, y1, z1, 1f, 0f,
                x1, y1, z0, 1f, 1f,
                x0, y1, z0, 0f, 1f,
                0f, +1f, 0f, layer, lightPacked);
            // -Y face (bottom)
            EmitCrossQuad(
                x0, y0, z0, 0f, 0f,
                x1, y0, z0, 1f, 0f,
                x1, y0, z1, 1f, 1f,
                x0, y0, z1, 0f, 1f,
                0f, -1f, 0f, layer, lightPacked);
        }

        // Tier 8 #42 — Generic axis-aligned sub-cube box. Used by
        // lever / button (and any future redstone primitive that
        // needs a small box at sub-cell extents). Single layer for
        // all six faces; UVs sample full [0..1] for top/bottom and
        // height-fractional for the sides so a non-cube-tall box
        // doesn't stretch its texture vertically. Light is sampled
        // at the source cell only (these blocks are non-light-
        // blocking, so the cell's own sky+block light is bright).
        private void EmitSubCubeBox(
            float x0, float y0, float z0,
            float x1, float y1, float z1,
            int layer, int lightPacked)
        {
            float vTop = y1 - y0;            // V-extent for side faces
            // -X face (dir<0)
            EmitCrossQuad(
                x0, y0, z0, 0f, 0f,
                x0, y0, z1, 1f, 0f,
                x0, y1, z1, 1f, vTop,
                x0, y1, z0, 0f, vTop,
                -1f, 0f, 0f, layer, lightPacked);
            // +X face (dir>0)
            EmitCrossQuad(
                x1, y0, z0, 1f, 0f,
                x1, y1, z0, 1f, vTop,
                x1, y1, z1, 0f, vTop,
                x1, y0, z1, 0f, 0f,
                +1f, 0f, 0f, layer, lightPacked);
            // -Z face (dir<0)
            EmitCrossQuad(
                x0, y0, z0, 0f, 0f,
                x0, y1, z0, 0f, vTop,
                x1, y1, z0, 1f, vTop,
                x1, y0, z0, 1f, 0f,
                0f, 0f, -1f, layer, lightPacked);
            // +Z face (dir>0)
            EmitCrossQuad(
                x0, y0, z1, 0f, 0f,
                x1, y0, z1, 1f, 0f,
                x1, y1, z1, 1f, vTop,
                x0, y1, z1, 0f, vTop,
                0f, 0f, +1f, layer, lightPacked);
            // +Y face (top)
            EmitCrossQuad(
                x0, y1, z1, 0f, 0f,
                x1, y1, z1, 1f, 0f,
                x1, y1, z0, 1f, 1f,
                x0, y1, z0, 0f, 1f,
                0f, +1f, 0f, layer, lightPacked);
            // -Y face (bottom)
            EmitCrossQuad(
                x0, y0, z0, 0f, 0f,
                x1, y0, z0, 1f, 0f,
                x1, y0, z1, 1f, 1f,
                x0, y0, z1, 0f, 1f,
                0f, -1f, 0f, layer, lightPacked);
        }

        // Tier 8 #42 — Pressure plate. 14×1×14 floor slab inset by 1
        // pixel on each horizontal side (matches Alpha's standard
        // pressure-plate footprint). Single-tile face for both
        // stone + wood variants; the variant is selected at the
        // dispatch site.
        private void EmitPressurePlate(float wx, float wy, float wz, int layer, int lightPacked)
        {
            float x0 = wx + 1f / 16f, x1 = wx + 15f / 16f;
            float z0 = wz + 1f / 16f, z1 = wz + 15f / 16f;
            float y0 = wy + 0f,        y1 = wy + 1f / 16f;
            EmitSubCubeBox(x0, y0, z0, x1, y1, z1, layer, lightPacked);
        }

        // Tier 8 #42 — Lever (simplified single-tile cube). 6×6×6
        // small box pinned to cell bottom. Real Alpha lever has a
        // distinct base + tilted handle; the simplified single-box
        // form trades visual fidelity for getting the placement /
        // power-source plumbing in. Future polish can replace this
        // with a proper two-piece mesh.
        private void EmitLeverBox(float wx, float wy, float wz, int lightPacked)
        {
            float x0 = wx + 5f / 16f, x1 = wx + 11f / 16f;
            float z0 = wz + 5f / 16f, z1 = wz + 11f / 16f;
            float y0 = wy + 0f,        y1 = wy + 6f / 16f;
            EmitSubCubeBox(x0, y0, z0, x1, y1, z1, BlockTextures.TileCobblestone, lightPacked);
        }

        // Tier 8 #42 — Stone button. 6×2×4 small recessed cuboid
        // sitting on the floor at cell centre. Same simplified
        // single-tile rendering as the lever for now.
        private void EmitButtonBox(float wx, float wy, float wz, int lightPacked)
        {
            float x0 = wx + 5f / 16f, x1 = wx + 11f / 16f;
            float z0 = wz + 6f / 16f, z1 = wz + 10f / 16f;
            float y0 = wy + 0f,        y1 = wy + 2f / 16f;
            EmitSubCubeBox(x0, y0, z0, x1, y1, z1, BlockTextures.TileStone, lightPacked);
        }

        // Tier 6 #37 — Cactus inset box with hash-overlap sides.
        // Two-pixel inset on each horizontal side (12×16×12 inside
        // the cell), but the four lateral faces extend the FULL
        // perpendicular axis instead of stopping at the inset
        // square — that's what makes the four faces visibly overlap
        // at the corners and read as a # pattern from above. The
        // side-face plane sits at x = 2/16 / x = 14/16 (or z), and
        // each face spans Y:[0..1] × the perpendicular axis at its
        // full [0..1] range. Top + bottom are at the 12×12 inset
        // square (matching the side-face plane positions) so the
        // top texture caps the central column visibly above the
        // overlapping side strips. Side-face windings follow the
        // dir>0 / dir<0 convention from the cube mesher's EmitQuad.
        private void EmitCactusBox(
            float wx, float wy, float wz,
            Chunk chunk, int cx, int cy, int cz,
            Chunk nxNeg, Chunk nxPos, Chunk nzNeg, Chunk nzPos)
        {
            // 1-pixel inset for the side-face PLANES (matches the
            // canonical Alpha 14×16×14 cactus). The top + bottom
            // span the same 14×14 footprint at 1/16..15/16, so the
            // top tile reads at the canonical Alpha cactus_top size
            // — earlier 2/16 inset made the top look 1 pixel too
            // small. Hash-overlap on the sides is preserved because
            // each side face still spans the FULL perpendicular
            // axis (z=0..1 for ±X, x=0..1 for ±Z), extending past
            // the top face's edges and producing the visible #
            // silhouette at corner viewing angles.
            const float inset = 1f / 16f;
            float xPN = wx + inset;
            float xPP = wx + 1f - inset;
            float zPN = wz + inset;
            float zPP = wz + 1f - inset;
            float xF0 = wx + 0f;
            float xF1 = wx + 1f;
            float zF0 = wz + 0f;
            float zF1 = wz + 1f;
            float y0 = wy + 0f;
            float y1 = wy + 1f;

            int sideLayer = BlockTextures.TileCactusSide;
            int topLayer  = BlockTextures.TileCactusTop;

            // Per-face neighbour-sampled lights — solid cactus cell
            // is light-blocking, so sampling AT the cactus cell would
            // give black faces. Use the same outward-cell convention
            // the cube mesher uses for normal blocks.
            int lXNeg = LightOrNeighbor(chunk, cx - 1, cy,     cz,     nxNeg, nxPos, nzNeg, nzPos);
            int lXPos = LightOrNeighbor(chunk, cx + 1, cy,     cz,     nxNeg, nxPos, nzNeg, nzPos);
            int lZNeg = LightOrNeighbor(chunk, cx,     cy,     cz - 1, nxNeg, nxPos, nzNeg, nzPos);
            int lZPos = LightOrNeighbor(chunk, cx,     cy,     cz + 1, nxNeg, nxPos, nzNeg, nzPos);
            int lYPos = LightOrNeighbor(chunk, cx,     cy + 1, cz,     nxNeg, nxPos, nzNeg, nzPos);
            int lYNeg = LightOrNeighbor(chunk, cx,     cy - 1, cz,     nxNeg, nxPos, nzNeg, nzPos);

            // -X face
            EmitCrossQuad(
                xPN, y0, zF0, 0f, 0f,
                xPN, y0, zF1, 1f, 0f,
                xPN, y1, zF1, 1f, 1f,
                xPN, y1, zF0, 0f, 1f,
                -1f, 0f, 0f, sideLayer, lXNeg);
            // +X face
            EmitCrossQuad(
                xPP, y0, zF0, 1f, 0f,
                xPP, y1, zF0, 1f, 1f,
                xPP, y1, zF1, 0f, 1f,
                xPP, y0, zF1, 0f, 0f,
                +1f, 0f, 0f, sideLayer, lXPos);
            // -Z face
            EmitCrossQuad(
                xF0, y0, zPN, 0f, 0f,
                xF0, y1, zPN, 0f, 1f,
                xF1, y1, zPN, 1f, 1f,
                xF1, y0, zPN, 1f, 0f,
                0f, 0f, -1f, sideLayer, lZNeg);
            // +Z face
            EmitCrossQuad(
                xF0, y0, zPP, 0f, 0f,
                xF1, y0, zPP, 1f, 0f,
                xF1, y1, zPP, 1f, 1f,
                xF0, y1, zPP, 0f, 1f,
                0f, 0f, +1f, sideLayer, lZPos);
            // +Y face (top) — spans full cell (16×16) so the
            // cactus_top tile reads at its native size and the top
            // visibly overhangs the inset side-face planes by 1
            // pixel on each side. Matches canonical Alpha; clipping
            // the top to the inset 14×14 made the tile appear 1
            // pixel too small at every viewing angle.
            EmitCrossQuad(
                xF0, y1, zF1, 0f, 0f,
                xF1, y1, zF1, 1f, 0f,
                xF1, y1, zF0, 1f, 1f,
                xF0, y1, zF0, 0f, 1f,
                0f, +1f, 0f, topLayer, lYPos);
            // -Y face (bottom) — also full cell so the underside
            // matches the top in extent. Reuses cactus_top since
            // the atlas only wires top + side; the bottom is
            // invisible while the cactus stands on sand and the
            // visual is fine for the rare floating-cactus case.
            EmitCrossQuad(
                xF0, y0, zF0, 0f, 0f,
                xF1, y0, zF0, 1f, 0f,
                xF1, y0, zF1, 1f, 1f,
                xF0, y0, zF1, 0f, 1f,
                0f, -1f, 0f, topLayer, lYNeg);
        }

        // Tier 6 — Floor torch as a 2×10×2 box standing in the
        // centre of the cell, replacing the cross-sprite render. UV
        // sampling uses only the centre 2×10 strip of the canonical
        // torch tile (U:[7/16..9/16], V:[0..10/16]) so the wood
        // column shows correctly on the box's narrow side faces;
        // sampling the full tile would compress all 16 source
        // columns into the 2-pixel face width and the wood pixels
        // would alias out. Top + bottom faces sample a small 2×2
        // wood region.
        //
        // Light: torches emit BlockLight=14 in the source cell, but
        // we want the rendered faces to read at full brightness on
        // their inset surfaces. Sampling at the source cell directly
        // (its own block-light) gives the natural glow without the
        // per-face neighbour-sample dance the cube mesher does.
        private void EmitTorchBox(float wx, float wy, float wz, int lightPacked)
        {
            const float colHalf = 1f / 16f;          // 2-pixel column → 1px each side of cell centre
            const float colTop  = 10f / 16f;         // 10 pixels tall
            float cx = wx + 0.5f;
            float cz = wz + 0.5f;
            float x0 = cx - colHalf;
            float x1 = cx + colHalf;
            float z0 = cz - colHalf;
            float z1 = cz + colHalf;
            float y0 = wy + 0f;
            float y1 = wy + colTop;

            int layer = BlockTextures.TileTorch;

            // Side-face UV: U samples the wood column (U=7/16..9/16),
            // V samples the wood height (V=0..10/16).
            const float uColLo = 7f / 16f;
            const float uColHi = 9f / 16f;
            const float vBase  = 0f;
            const float vTop   = 10f / 16f;

            // -X face (dir<0)
            EmitCrossQuad(
                x0, y0, z0, uColLo, vBase,
                x0, y0, z1, uColHi, vBase,
                x0, y1, z1, uColHi, vTop,
                x0, y1, z0, uColLo, vTop,
                -1f, 0f, 0f, layer, lightPacked);
            // +X face (dir>0)
            EmitCrossQuad(
                x1, y0, z0, uColLo, vBase,
                x1, y1, z0, uColLo, vTop,
                x1, y1, z1, uColHi, vTop,
                x1, y0, z1, uColHi, vBase,
                +1f, 0f, 0f, layer, lightPacked);
            // -Z face (dir<0)
            EmitCrossQuad(
                x0, y0, z0, uColLo, vBase,
                x0, y1, z0, uColLo, vTop,
                x1, y1, z0, uColHi, vTop,
                x1, y0, z0, uColHi, vBase,
                0f, 0f, -1f, layer, lightPacked);
            // +Z face (dir>0)
            EmitCrossQuad(
                x0, y0, z1, uColLo, vBase,
                x1, y0, z1, uColHi, vBase,
                x1, y1, z1, uColHi, vTop,
                x0, y1, z1, uColLo, vTop,
                0f, 0f, +1f, layer, lightPacked);
            // +Y face (top of wood column) — sample 2×2 of wood
            // just below the wood-flame transition.
            const float vCapLo = 8f / 16f;
            const float vCapHi = 10f / 16f;
            EmitCrossQuad(
                x0, y1, z1, uColLo, vCapLo,
                x1, y1, z1, uColHi, vCapLo,
                x1, y1, z0, uColHi, vCapHi,
                x0, y1, z0, uColLo, vCapHi,
                0f, +1f, 0f, layer, lightPacked);
            // -Y face (bottom) — sample 2×2 of the wood base.
            const float vBotLo = 0f;
            const float vBotHi = 2f / 16f;
            EmitCrossQuad(
                x0, y0, z0, uColLo, vBotLo,
                x1, y0, z0, uColHi, vBotLo,
                x1, y0, z1, uColHi, vBotHi,
                x0, y0, z1, uColLo, vBotHi,
                0f, -1f, 0f, layer, lightPacked);
        }

        // Tier 8 #44 — Sign-post geometry. Two sub-cell volumes:
        //   1. A 2×9×2 wood pole rising from cell bottom to roughly
        //      the cell's vertical midpoint (9/16). The pole sits on
        //      whatever solid block the sign was placed atop.
        //   2. A 12×7×1.5 board occupying the upper-back of the cell.
        //      The 1.5/16 thickness matches Alpha's near-flat sign
        //      profile; 12 wide × 7 tall is the plank-bearing area
        //      the player's typed text overlays onto.
        //
        // Both volumes sample the existing PlanksOak tile (per the
        // user's "use the wooden plank texture for the sign" choice).
        // Front-face UV on the board is reserved for the future text
        // overlay pass — for now the plank face shows through both
        // sides until the editor + 3D-text emitter ship.
        //
        // Facing comes from the low 2 bits of `meta`:
        //   0 = North (text faces -Z)
        //   1 = South (text faces +Z)
        //   2 = East  (text faces +X)
        //   3 = West  (text faces -X)
        //
        // Light: sample the cell's own packed sky+block byte for all
        // faces. The pole + board are tiny vs the cell so per-face
        // neighbour-sampling (the cube mesher's approach) would over-
        // dim them next to a shaded wall; falling back to the cell's
        // own lighting matches what EmitTorchBox does and reads as
        // "the sign is lit by the same air the player sees it through".
        private void EmitSignPost(float wx, float wy, float wz, byte meta, int lightPacked)
        {
            int facing = meta & 0x03;
            int layer = BlockTextures.TilePlanks;

            // ---- Pole (2×9×2 wood column) ----
            const float poleHalf = 1f / 16f;     // 2px square → 1px each side of cell-centre
            const float poleTop  = 9f / 16f;     // 9px tall → top edge sits at cell mid-Y
            float pcx = wx + 0.5f;
            float pcz = wz + 0.5f;
            float px0 = pcx - poleHalf;
            float px1 = pcx + poleHalf;
            float pz0 = pcz - poleHalf;
            float pz1 = pcz + poleHalf;
            float py0 = wy + 0f;
            float py1 = wy + poleTop;

            // Pole UVs: pick a 2×9-pixel slice of the plank tile so
            // the column shows wood grain. U=7..9 (centre pair),
            // V=0..9. Same convention EmitTorchBox uses.
            const float pUlo = 7f / 16f;
            const float pUhi = 9f / 16f;
            const float pVlo = 0f;
            const float pVhi = 9f / 16f;

            EmitCrossQuad(
                px0, py0, pz0, pUlo, pVlo,
                px0, py0, pz1, pUhi, pVlo,
                px0, py1, pz1, pUhi, pVhi,
                px0, py1, pz0, pUlo, pVhi,
                -1f, 0f, 0f, layer, lightPacked);
            EmitCrossQuad(
                px1, py0, pz0, pUlo, pVlo,
                px1, py1, pz0, pUlo, pVhi,
                px1, py1, pz1, pUhi, pVhi,
                px1, py0, pz1, pUhi, pVlo,
                +1f, 0f, 0f, layer, lightPacked);
            EmitCrossQuad(
                px0, py0, pz0, pUlo, pVlo,
                px0, py1, pz0, pUlo, pVhi,
                px1, py1, pz0, pUhi, pVhi,
                px1, py0, pz0, pUhi, pVlo,
                0f, 0f, -1f, layer, lightPacked);
            EmitCrossQuad(
                px0, py0, pz1, pUlo, pVlo,
                px1, py0, pz1, pUhi, pVlo,
                px1, py1, pz1, pUhi, pVhi,
                px0, py1, pz1, pUlo, pVhi,
                0f, 0f, +1f, layer, lightPacked);

            // ---- Board (12×7×1.5 wood plate) ----
            // Built in canonical "facing North" coordinates (board
            // wide along X, thin along Z, top half of cell), then
            // rotated by `facing` into the cell's actual orientation.
            // The 1.5/16 thickness is centered on the pole's Z mid-
            // line so the board straddles the pole symmetrically —
            // this is what Alpha does; the pole is part of the same
            // wood mass as the board.
            const float boardWideHalf = 6f / 16f;   // 12px wide → 6px each side of pole
            const float boardThickHalf = 0.75f / 16f;   // 1.5px thick → 0.75px each side
            const float boardTop = 16f / 16f;       // top of cell
            const float boardBot = 9f / 16f;        // continuous with pole top
            float bcx = wx + 0.5f;
            float bcz = wz + 0.5f;

            // Per-facing pre-rotated AABB. Picking the local axes
            // explicitly (instead of running a sin/cos rotation on
            // each vertex) keeps the emitter free of trig and easy
            // to read; for 4 cardinal facings the case-table is
            // strictly cheaper than a matrix multiply.
            float bx0, bx1, bz0, bz1;
            float fnx, fnz;             // outward normal of the FRONT face (where text goes)
            switch (facing)
            {
                case 1: // South — text faces +Z
                    bx0 = bcx - boardWideHalf;  bx1 = bcx + boardWideHalf;
                    bz0 = bcz - boardThickHalf; bz1 = bcz + boardThickHalf;
                    fnx = 0f; fnz = +1f;
                    break;
                case 2: // East — text faces +X
                    bx0 = bcx - boardThickHalf; bx1 = bcx + boardThickHalf;
                    bz0 = bcz - boardWideHalf;  bz1 = bcz + boardWideHalf;
                    fnx = +1f; fnz = 0f;
                    break;
                case 3: // West — text faces -X
                    bx0 = bcx - boardThickHalf; bx1 = bcx + boardThickHalf;
                    bz0 = bcz - boardWideHalf;  bz1 = bcz + boardWideHalf;
                    fnx = -1f; fnz = 0f;
                    break;
                default: // North (0) — text faces -Z
                    bx0 = bcx - boardWideHalf;  bx1 = bcx + boardWideHalf;
                    bz0 = bcz - boardThickHalf; bz1 = bcz + boardThickHalf;
                    fnx = 0f; fnz = -1f;
                    break;
            }
            float by0 = wy + boardBot;
            float by1 = wy + boardTop;

            EmitBoardBox(bx0, by0, bz0, bx1, by1, bz1, layer, lightPacked, fnx, fnz);
        }

        // Tier 8 #44 — Wall-sign geometry. A 12×7×1.5 board hugging
        // one of the four cell walls, oriented so the writing faces
        // OUTWARD (i.e. away from the wall it's mounted on). The
        // sign sits at the vertical midpoint of the cell (Y 4..11)
        // — Alpha's wall-sign placement.
        //
        // Facing semantics match the post sign (low 2 bits of meta):
        //   0 = North → mounted on +Z wall, text faces -Z
        //   1 = South → mounted on -Z wall, text faces +Z
        //   2 = East  → mounted on -X wall, text faces +X
        //   3 = West  → mounted on +X wall, text faces -X
        // (The placement path stores "facing of the WRITING" so the
        // wall and post variants share one facing convention.)
        private void EmitWallSign(float wx, float wy, float wz, byte meta, int lightPacked)
        {
            int facing = meta & 0x03;
            int layer = BlockTextures.TilePlanks;

            // Board AABB inside the cell. Width spans most of the
            // cell along the wall; thickness pins to the wall face;
            // height is the canonical Alpha 7-pixel band centred on
            // cell midline.
            const float thick = 1.5f / 16f;
            const float wideHalf = 6f / 16f;
            const float yLo = 4f / 16f;
            const float yHi = 12f / 16f;
            float cx = wx + 0.5f;
            float cz = wz + 0.5f;

            float bx0, bx1, bz0, bz1;
            float fnx, fnz;
            switch (facing)
            {
                case 1: // South — wall on -Z, board pinned to z=0..thick
                    bx0 = cx - wideHalf;  bx1 = cx + wideHalf;
                    bz0 = wz + 0f;        bz1 = wz + thick;
                    fnx = 0f; fnz = +1f;
                    break;
                case 2: // East — wall on -X, board pinned to x=0..thick
                    bx0 = wx + 0f;        bx1 = wx + thick;
                    bz0 = cz - wideHalf;  bz1 = cz + wideHalf;
                    fnx = +1f; fnz = 0f;
                    break;
                case 3: // West — wall on +X, board pinned to x=1-thick..1
                    bx0 = wx + (1f - thick); bx1 = wx + 1f;
                    bz0 = cz - wideHalf;     bz1 = cz + wideHalf;
                    fnx = -1f; fnz = 0f;
                    break;
                default: // North — wall on +Z, board pinned to z=1-thick..1
                    bx0 = cx - wideHalf;     bx1 = cx + wideHalf;
                    bz0 = wz + (1f - thick); bz1 = wz + 1f;
                    fnx = 0f; fnz = -1f;
                    break;
            }
            float by0 = wy + yLo;
            float by1 = wy + yHi;

            EmitBoardBox(bx0, by0, bz0, bx1, by1, bz1, layer, lightPacked, fnx, fnz);
        }

        // Helper — six-faced box with a known "front" direction.
        // Used by both EmitSignPost and EmitWallSign for the wood
        // board. UVs sample a 12×7-pixel patch of the plank tile
        // for the broad faces (visually a slice of plank wood that
        // reads as the board's grain) and a 12×1.5 / 1.5×7 strip for
        // the four thin edges. fnx/fnz aren't used to flip UVs (the
        // plank texture is symmetric), but documenting the front
        // direction in the signature keeps EmitSignPost / EmitWallSign
        // call sites self-explanatory and gives the future text-
        // overlay pass an obvious place to hook the front face.
        private void EmitBoardBox(
            float x0, float y0, float z0, float x1, float y1, float z1,
            int layer, int lightPacked, float fnx, float fnz)
        {
            // 12×7 plank patch UVs. Centred on the tile so the grain
            // visually frames the board. Fixed across all 4 facings
            // — plank texture is rotation-invariant, so re-mapping
            // would gain nothing.
            const float uLo = 2f / 16f;
            const float uHi = 14f / 16f;
            const float vLo = 4f / 16f;
            const float vHi = 11f / 16f;

            // Six box faces. CCW outward winding to play nicely with
            // back-face culling — same convention as EmitDoorSlab and
            // EmitCactusBox. The fnx/fnz parameter is informational
            // for now (no UV flip needed); the future text pass will
            // consult it to anchor glyph quads on the correct face.
            _ = fnx; _ = fnz;

            // -X face (dir<0)
            EmitCrossQuad(
                x0, y0, z0, uLo, vLo,
                x0, y0, z1, uHi, vLo,
                x0, y1, z1, uHi, vHi,
                x0, y1, z0, uLo, vHi,
                -1f, 0f, 0f, layer, lightPacked);
            // +X face (dir>0)
            EmitCrossQuad(
                x1, y0, z0, uLo, vLo,
                x1, y1, z0, uLo, vHi,
                x1, y1, z1, uHi, vHi,
                x1, y0, z1, uHi, vLo,
                +1f, 0f, 0f, layer, lightPacked);
            // -Z face (dir<0)
            EmitCrossQuad(
                x0, y0, z0, uLo, vLo,
                x0, y1, z0, uLo, vHi,
                x1, y1, z0, uHi, vHi,
                x1, y0, z0, uHi, vLo,
                0f, 0f, -1f, layer, lightPacked);
            // +Z face (dir>0)
            EmitCrossQuad(
                x0, y0, z1, uLo, vLo,
                x1, y0, z1, uHi, vLo,
                x1, y1, z1, uHi, vHi,
                x0, y1, z1, uLo, vHi,
                0f, 0f, +1f, layer, lightPacked);
            // +Y face (top)
            EmitCrossQuad(
                x0, y1, z1, uLo, vLo,
                x1, y1, z1, uHi, vLo,
                x1, y1, z0, uHi, vHi,
                x0, y1, z0, uLo, vHi,
                0f, +1f, 0f, layer, lightPacked);
            // -Y face (bottom)
            EmitCrossQuad(
                x0, y0, z0, uLo, vLo,
                x1, y0, z0, uHi, vLo,
                x1, y0, z1, uHi, vHi,
                x0, y0, z1, uLo, vHi,
                0f, -1f, 0f, layer, lightPacked);
        }

        private void EmitCrossSprite(float wx, float wy, float wz, int layer, int lightPacked)
        {
            // Two diagonal planes through the cell centre. Each plane is
            // emitted twice with opposite winding so back-face culling doesn't
            // hide either side. Normals point straight up so per-axis face
            // shading reads the brightest (top) bias — matches how Alpha
            // shades cross sprites.
            //
            // Vertex coords are 0..1 within the cell, offset by (wx, wy, wz).
            // UV runs 0..1 across the full diagonal so the whole tile shows.
            const float ny = 1f, nx = 0f, nz = 0f;

            // Plane 1: from (0, *, 0) to (1, *, 1) (NW-SE diagonal).
            EmitCrossQuad(
                wx + 0f, wy + 0f, wz + 0f,  0f, 0f,
                wx + 1f, wy + 0f, wz + 1f,  1f, 0f,
                wx + 1f, wy + 1f, wz + 1f,  1f, 1f,
                wx + 0f, wy + 1f, wz + 0f,  0f, 1f,
                nx, ny, nz, layer, lightPacked);
            // Plane 1 back side.
            EmitCrossQuad(
                wx + 1f, wy + 0f, wz + 1f,  0f, 0f,
                wx + 0f, wy + 0f, wz + 0f,  1f, 0f,
                wx + 0f, wy + 1f, wz + 0f,  1f, 1f,
                wx + 1f, wy + 1f, wz + 1f,  0f, 1f,
                nx, ny, nz, layer, lightPacked);
            // Plane 2: from (0, *, 1) to (1, *, 0) (NE-SW diagonal).
            EmitCrossQuad(
                wx + 0f, wy + 0f, wz + 1f,  0f, 0f,
                wx + 1f, wy + 0f, wz + 0f,  1f, 0f,
                wx + 1f, wy + 1f, wz + 0f,  1f, 1f,
                wx + 0f, wy + 1f, wz + 1f,  0f, 1f,
                nx, ny, nz, layer, lightPacked);
            // Plane 2 back side.
            EmitCrossQuad(
                wx + 1f, wy + 0f, wz + 0f,  0f, 0f,
                wx + 0f, wy + 0f, wz + 1f,  1f, 0f,
                wx + 0f, wy + 1f, wz + 1f,  1f, 1f,
                wx + 1f, wy + 1f, wz + 0f,  0f, 1f,
                nx, ny, nz, layer, lightPacked);
        }

        private void EmitCrossQuad(
            float x0, float y0, float z0, float u0, float v0,
            float x1, float y1, float z1, float u1, float v1,
            float x2, float y2, float z2, float u2, float v2,
            float x3, float y3, float z3, float u3, float v3,
            float nx, float ny, float nz,
            int layer, int lightPacked)
        {
            uint baseIdx = (uint)(_vertFloats / Mesh.FloatsPerVertex);
            float light = lightPacked;
            AppendVert(false, x0, y0, z0, u0, v0, nx, ny, nz, layer, light);
            AppendVert(false, x1, y1, z1, u1, v1, nx, ny, nz, layer, light);
            AppendVert(false, x2, y2, z2, u2, v2, nx, ny, nz, layer, light);
            AppendVert(false, x3, y3, z3, u3, v3, nx, ny, nz, layer, light);
            AppendIndex(false, baseIdx + 0);
            AppendIndex(false, baseIdx + 1);
            AppendIndex(false, baseIdx + 2);
            AppendIndex(false, baseIdx + 0);
            AppendIndex(false, baseIdx + 2);
            AppendIndex(false, baseIdx + 3);
        }

        private static byte BlockAt(Chunk c, int lx, int y, int lz)
        {
            if ((uint)y >= Chunk.SizeY) return 0;
            if ((uint)lx < Chunk.SizeX && (uint)lz < Chunk.SizeZ)
                return c.RawBlocks[Chunk.Index(lx, y, lz)];
            return 0;
        }

        // Returns packed sky*16 + block (0..255). Out-of-bounds above the world
        // gets full sky (15*16 + 0 = 240); below the world gets darkness (0).
        // Crossing a missing chunk falls back to "lit", same as we treat the
        // top of the world — this keeps the chunk border bright instead of
        // gating it black until the neighbour generates.
        private static int LightAt(Chunk c, int lx, int y, int lz)
        {
            if (y >= Chunk.SizeY) return 15 * 16;
            if (y < 0) return 0;
            if ((uint)lx < Chunk.SizeX && (uint)lz < Chunk.SizeZ)
            {
                byte b = c.RawLight[Chunk.Index(lx, y, lz)];
                int sky = (b >> 4) & 0xF;
                int blk = b & 0xF;
                return sky * 16 + blk;
            }
            return 15 * 16;
        }

        private static int LightOrNeighbor(
            Chunk center, int lx, int y, int lz,
            Chunk nxNeg, Chunk nxPos, Chunk nzNeg, Chunk nzPos)
        {
            if (y >= Chunk.SizeY) return 15 * 16;
            if (y < 0) return 0;
            if ((uint)lx < Chunk.SizeX && (uint)lz < Chunk.SizeZ)
                return LightAt(center, lx, y, lz);
            if (lx < 0)
            {
                if (nxNeg == null) return 15 * 16;
                return LightAt(nxNeg, Chunk.SizeX + lx, y, lz);
            }
            if (lx >= Chunk.SizeX)
            {
                if (nxPos == null) return 15 * 16;
                return LightAt(nxPos, lx - Chunk.SizeX, y, lz);
            }
            if (lz < 0)
            {
                if (nzNeg == null) return 15 * 16;
                return LightAt(nzNeg, lx, y, Chunk.SizeZ + lz);
            }
            if (lz >= Chunk.SizeZ)
            {
                if (nzPos == null) return 15 * 16;
                return LightAt(nzPos, lx, y, lz - Chunk.SizeZ);
            }
            return 15 * 16;
        }

        // Fetch a meta byte at chunk-local coords, mirroring BlockOrNeighbor.
        // Used by the fluid lid pass to read the reach / falling bits of
        // diagonal neighbours when computing corner heights. Out-of-world
        // returns 0 (treated as the default "no extra data" by callers).
        private static byte MetaOrNeighbor(
            Chunk center, int lx, int y, int lz,
            Chunk nxNeg, Chunk nxPos, Chunk nzNeg, Chunk nzPos)
        {
            if ((uint)y >= Chunk.SizeY) return 0;

            if ((uint)lx < Chunk.SizeX && (uint)lz < Chunk.SizeZ)
                return center.RawMeta[Chunk.Index(lx, y, lz)];

            if (lx < 0)
            {
                if (nxNeg == null) return 0;
                if ((uint)lz < Chunk.SizeZ) return nxNeg.RawMeta[Chunk.Index(Chunk.SizeX + lx, y, lz)];
                return 0;
            }
            if (lx >= Chunk.SizeX)
            {
                if (nxPos == null) return 0;
                if ((uint)lz < Chunk.SizeZ) return nxPos.RawMeta[Chunk.Index(lx - Chunk.SizeX, y, lz)];
                return 0;
            }
            if (lz < 0)
            {
                if (nzNeg == null) return 0;
                return nzNeg.RawMeta[Chunk.Index(lx, y, Chunk.SizeZ + lz)];
            }
            if (lz >= Chunk.SizeZ)
            {
                if (nzPos == null) return 0;
                return nzPos.RawMeta[Chunk.Index(lx, y, lz - Chunk.SizeZ)];
            }
            return 0;
        }

        // Fetch a block at chunk-local coords, crossing into a neighbour chunk if the
        // local coord is out of [0, Size). y is never crossed (world is a single slab).
        private static byte BlockOrNeighbor(
            Chunk center, int lx, int y, int lz,
            Chunk nxNeg, Chunk nxPos, Chunk nzNeg, Chunk nzPos)
        {
            if ((uint)y >= Chunk.SizeY) return 0;

            if ((uint)lx < Chunk.SizeX && (uint)lz < Chunk.SizeZ)
                return center.RawBlocks[Chunk.Index(lx, y, lz)];

            if (lx < 0)
            {
                if (nxNeg == null) return 0;
                return BlockAt(nxNeg, Chunk.SizeX + lx, y, lz);
            }
            if (lx >= Chunk.SizeX)
            {
                if (nxPos == null) return 0;
                return BlockAt(nxPos, lx - Chunk.SizeX, y, lz);
            }
            if (lz < 0)
            {
                if (nzNeg == null) return 0;
                return BlockAt(nzNeg, lx, y, Chunk.SizeZ + lz);
            }
            if (lz >= Chunk.SizeZ)
            {
                if (nzPos == null) return 0;
                return BlockAt(nzPos, lx, y, lz - Chunk.SizeZ);
            }
            return 0;
        }

        // Dimensions along (u, v, axis). We slice perpendicular to `axis` and tile faces
        // in the (u, v) plane. u = (axis+1)%3, v = (axis+2)%3.
        private void Sweep(
            World world,
            Chunk chunk, int axis, int dir,
            Chunk nxNeg, Chunk nxPos, Chunk nzNeg, Chunk nzPos,
            int baseX, int baseZ)
        {
            int u = (axis + 1) % 3;
            int v = (axis + 2) % 3;

            int dAxis = DimSize(axis);
            int dU = DimSize(u);
            int dV = DimSize(v);

            // Walk each slice plane (0..dAxis). A face exists between block[slice-1] and
            // block[slice] along the axis; we skip the trivial slice==0 case for dir=+1
            // by handling both ends explicitly inside BlockOrNeighbor.
            for (int slice = 0; slice <= dAxis; slice++)
            {
                // Build mask for this slice.
                for (int j = 0; j < dV; j++)
                for (int i = 0; i < dU; i++)
                {
                    int cx = 0, cy = 0, cz = 0;
                    SetAxis(ref cx, ref cy, ref cz, axis, dir > 0 ? slice - 1 : slice);
                    SetAxis(ref cx, ref cy, ref cz, u, i);
                    SetAxis(ref cx, ref cy, ref cz, v, j);
                    byte a = BlockOrNeighbor(chunk, cx, cy, cz, nxNeg, nxPos, nzNeg, nzPos);

                    int nx = 0, ny = 0, nz = 0;
                    SetAxis(ref nx, ref ny, ref nz, axis, dir > 0 ? slice : slice - 1);
                    SetAxis(ref nx, ref ny, ref nz, u, i);
                    SetAxis(ref nx, ref ny, ref nz, v, j);
                    byte b = BlockOrNeighbor(chunk, nx, ny, nz, nxNeg, nxPos, nzNeg, nzPos);

                    // "a" is the source block whose face points in `dir`, "b" is the neighbour.
                    // Emit face iff a is not air, b is not opaque (doesn't occlude),
                    // and it's not an internal transparent-to-same-transparent boundary
                    // (e.g. water-water — we don't want inner water faces).
                    bool aAir = a == (byte)BlockType.Air;
                    bool aOpaque = !aAir && BlockData.IsOpaque((BlockType)a);
                    bool bOpaque = b != (byte)BlockType.Air && BlockData.IsOpaque((BlockType)b);
                    // Non-cube blocks (torches, flowers, mushrooms) get their
                    // geometry from the model pass after the sweeps, so the
                    // cube sweep must not emit a face when `a` is one. They
                    // already never occlude a neighbour because IsOpaque is
                    // false, so b doesn't need a separate filter.
                    bool aCube = !aAir && BlockData.IsCubeShape((BlockType)a);
                    // Internal-face skip: same byte (e.g. water-water) OR same
                    // fluid family (water source vs flowing water — both share
                    // group 1, lava + flowing lava share group 2). Without the
                    // family check the inner faces between a sea-source cell
                    // and the falling-water cell that springs out of it would
                    // form a visible square in the water.
                    //
                    // Alpha-tested cubes (leaves, glass) are deliberately
                    // exempt: they're non-opaque (so neighbours' faces against
                    // them are kept) but they DO want their inner-vs-inner
                    // face emitted, because the cut-out regions in the front
                    // face are what let you see the back face. Skipping the
                    // shared face would collapse a tree canopy or a stack of
                    // glass into a single hollow shell.
                    bool aIsAlphaTested = !aAir && BlockData.IsAlphaTestedCube((BlockType)a);
                    bool internalTransparent = !aOpaque && !aIsAlphaTested && (
                        a == b ||
                        (a != (byte)BlockType.Air && b != (byte)BlockType.Air &&
                         BlockData.FluidGroup((BlockType)a) != 0 &&
                         BlockData.FluidGroup((BlockType)a) == BlockData.FluidGroup((BlockType)b)));
                    // Surface flowing-fluid cells get custom geometry from
                    // EmitFluidSurfaceLids — suppress the cube sweep so the two
                    // don't z-fight.
                    //   Top face (+Y):  always replaced by the sloped lid quad.
                    //   Side faces (±X/Z) against air: replaced by a trapezoidal
                    //     quad whose top edge follows the corner heights of the
                    //     lid (so the end-cap is not full-height).
                    //   Bottom / opaque-neighbour faces: cube sweep handles
                    //     those unchanged.
                    // Use the neighbour-aware predicate: along the swept axis
                    // the source cell may live one step into the adjacent
                    // chunk (slice 0 / slice dAxis). Without that, a surface-
                    // fluid cell pressed against a chunk seam loses its
                    // suppression in the neighbour's sweep and the neighbour
                    // emits a full-height side face on top of the owning
                    // chunk's trapezoid.
                    if (aCube
                        && IsSurfaceFluidOrNeighbor(chunk, cx, cy, cz, nxNeg, nxPos, nzNeg, nzPos))
                    {
                        if (axis == 1 && dir > 0)
                            aCube = false;   // top — always replaced
                        else if ((axis == 0 || axis == 2) && b == (byte)BlockType.Air)
                            aCube = false;   // air-facing side — replaced by trapezoid
                    }
                    if (!aAir && aCube && !bOpaque && !internalTransparent)
                    {
                        // Face of block `a` visible, pointing in `dir`. Light is
                        // sampled at the air-side cell (the `b` neighbour cell at
                        // (nx, ny, nz)) — that's the face-illuminating cell, not
                        // the solid block itself. Pack into the mask key so greedy
                        // merging stops at light-boundary cells; otherwise a wall
                        // partly in cave-shadow and partly in sun would merge into
                        // one quad with a single intermediate brightness.
                        int faceKind = FaceKindFor(axis, dir);
                        int layer;
                        if (a == (byte)BlockType.Furnace || a == (byte)BlockType.LitFurnace)
                        {
                            // Oriented furnace face: world-space coord of the source
                            // cell is needed to look up the entity's facing. cx/cy/cz
                            // are local to `chunk`; convert back to absolute coords.
                            int wx = cx + baseX;
                            int wy = cy;
                            int wz = cz + baseZ;
                            BlockFacing facing = BlockFacing.North;
                            var fe = world.TryGetFurnaceEntity(wx, wy, wz);
                            if (fe != null) facing = fe.Facing;
                            layer = BlockData.GetTileIndexForOriented((BlockType)a, axis, dir, facing);
                        }
                        else if (a == (byte)BlockType.Chest)
                        {
                            // Oriented chest face: same lookup pattern as
                            // furnace, but in the chest entity dict. A
                            // chest with no entity yet (e.g. legacy save)
                            // defaults to North facing.
                            int wx = cx + baseX;
                            int wy = cy;
                            int wz = cz + baseZ;
                            BlockFacing facing = BlockFacing.North;
                            var ce = world.TryGetChestEntity(wx, wy, wz);
                            if (ce != null) facing = ce.Facing;
                            layer = BlockData.GetTileIndexForOriented((BlockType)a, axis, dir, facing);
                        }
                        else
                        {
                            layer = BlockData.GetTileIndex((BlockType)a, faceKind);
                            // Tier 6 #37 Phase 4 — Snowy-grass face
                            // override. When a Grass / Dirt cell has a
                            // SnowBlock layer directly above, swap the
                            // side / top face tiles. Bounds-guarded
                            // because the cube sweep can place (cx,
                            // cy, cz) at chunk-local indices outside
                            // [0, Size) (the source block was read via
                            // BlockOrNeighbor which crosses chunks);
                            // only the in-chunk case can safely call
                            // Chunk.Index on this chunk's array.
                            // For the cross-chunk case the neighbour
                            // chunk's mesh handles its own dispatch.
                            if ((a == (byte)BlockType.Grass || a == (byte)BlockType.Dirt)
                                && (uint)cx < Chunk.SizeX
                                && (uint)cz < Chunk.SizeZ
                                && cy + 1 < Chunk.SizeY)
                            {
                                var aboveB = chunk.RawBlocks[Chunk.Index(cx, cy + 1, cz)];
                                if (aboveB == (byte)BlockType.SnowBlock)
                                {
                                    if (faceKind == 0)      layer = BlockTextures.TileSnow;
                                    else if (faceKind != 1) layer = BlockTextures.TileSnowyGrassSide;
                                }
                            }
                        }
                        int lightPacked = LightOrNeighbor(chunk, nx, ny, nz, nxNeg, nxPos, nzNeg, nzPos);

                        // Layout, all in the positive int range (sign bit reserved
                        // for transparent flag below):
                        //   bits  0..15  layer+1   (0 = empty, +1 bias)
                        //   bits 16..23  light     (sky*16 + block, 0..255)
                        // Light occupies a full byte so the sky-or-block max can
                        // round-trip cleanly.
                        int key = ((layer + 1) & 0xFFFF) | ((lightPacked & 0xFF) << 16);
                        // Negate to route into the alpha-blended transparent
                        // stream. Alpha-tested cubes (leaves, glass) are
                        // non-opaque but still belong on the opaque stream —
                        // their texture is binary alpha (gaps + solid pixels)
                        // so the shader's `discard` is the right model, not
                        // back-to-front blending.
                        if (!aOpaque && !aIsAlphaTested) key = -key;
                        _mask[j * dU + i] = key;
                    }
                    else
                    {
                        _mask[j * dU + i] = 0;
                    }
                }

                // Greedy-pack rectangles out of the mask.
                for (int j = 0; j < dV; j++)
                {
                    int i = 0;
                    while (i < dU)
                    {
                        int m = _mask[j * dU + i];
                        if (m == 0) { i++; continue; }

                        int w = 1;
                        while (i + w < dU && _mask[j * dU + i + w] == m) w++;

                        int h = 1;
                        bool done = false;
                        while (!done && j + h < dV)
                        {
                            for (int k = 0; k < w; k++)
                            {
                                if (_mask[(j + h) * dU + i + k] != m) { done = true; break; }
                            }
                            if (!done) h++;
                        }

                        int abs = m > 0 ? m : -m;
                        int layer = (abs & 0xFFFF) - 1;
                        int light = (abs >> 16) & 0xFF;
                        bool transparent = m < 0;
                        EmitQuad(axis, dir, slice, u, v, i, j, w, h, layer, light, transparent, baseX, baseZ);

                        for (int hh = 0; hh < h; hh++)
                        for (int ww = 0; ww < w; ww++)
                        {
                            _mask[(j + hh) * dU + i + ww] = 0;
                        }
                        i += w;
                    }
                }
            }
        }

        private static int DimSize(int axis)
        {
            switch (axis)
            {
                case 0: return Chunk.SizeX;
                case 1: return Chunk.SizeY;
                default: return Chunk.SizeZ;
            }
        }

        private static void SetAxis(ref int x, ref int y, ref int z, int axis, int value)
        {
            if (axis == 0) x = value;
            else if (axis == 1) y = value;
            else z = value;
        }

        private static int FaceKindFor(int axis, int dir)
        {
            // faceKind: 0 = top, 1 = bottom, 2 = side (matches BlockData.GetTileIndex contract).
            if (axis == 1) return dir > 0 ? 0 : 1;
            return 2;
        }

        private void EmitQuad(
            int axis, int dir, int slice, int u, int v,
            int i, int j, int w, int h, int layer,
            int light,
            bool transparent,
            int baseX, int baseZ)
        {
            // Reusable corner scratch (reset each call).
            _c0[0] = _c0[1] = _c0[2] = 0f;
            _c1[0] = _c1[1] = _c1[2] = 0f;
            _c2[0] = _c2[1] = _c2[2] = 0f;
            _c3[0] = _c3[1] = _c3[2] = 0f;
            _c0[axis] = slice; _c0[u] = i;       _c0[v] = j;
            _c1[axis] = slice; _c1[u] = i + w;   _c1[v] = j;
            _c2[axis] = slice; _c2[u] = i + w;   _c2[v] = j + h;
            _c3[axis] = slice; _c3[u] = i;       _c3[v] = j + h;

            float nx = 0, ny = 0, nz = 0;
            if (axis == 0) nx = dir;
            else if (axis == 1) ny = dir;
            else nz = dir;

            // With cyclic u=(axis+1)%3, v=(axis+2)%3 the (c0, c1, c2, c3) winding has
            // normal +axis. For dir<0 we reverse to (c0, c3, c2, c1) so the outward
            // face is still CCW (and back-face culling keeps it visible).
            //
            // UV axis mapping: for faces we want texture-V aligned to world Y so the
            // grass-side fringe always falls along the top of the face. For axis=0,
            // u=Y and v=Z natively, so we must swap (u,v) → (UV.x, UV.y). For other
            // axes the cyclic mapping already places horizontal-first, vertical-second.
            float uvX1, uvY1, uvX2, uvY2, uvX3, uvY3;
            if (axis == 0)
            {
                uvX1 = 0; uvY1 = w;   // c1 is at (u=+w, v=0) → UV (0, w) after swap
                uvX2 = h; uvY2 = w;   // c2 at (u=+w, v=+h) → UV (h, w)
                uvX3 = h; uvY3 = 0;   // c3 at (u=0, v=+h) → UV (h, 0)
            }
            else
            {
                uvX1 = w; uvY1 = 0;
                uvX2 = w; uvY2 = h;
                uvX3 = 0; uvY3 = h;
            }

            int curVertFloats = transparent ? _tVertFloats : _vertFloats;
            uint baseIdx = (uint)(curVertFloats / Mesh.FloatsPerVertex);

            if (dir > 0)
            {
                AppendVert(transparent, _c0[0] + baseX, _c0[1], _c0[2] + baseZ, 0,    0,    nx, ny, nz, layer, light);
                AppendVert(transparent, _c1[0] + baseX, _c1[1], _c1[2] + baseZ, uvX1, uvY1, nx, ny, nz, layer, light);
                AppendVert(transparent, _c2[0] + baseX, _c2[1], _c2[2] + baseZ, uvX2, uvY2, nx, ny, nz, layer, light);
                AppendVert(transparent, _c3[0] + baseX, _c3[1], _c3[2] + baseZ, uvX3, uvY3, nx, ny, nz, layer, light);
            }
            else
            {
                AppendVert(transparent, _c0[0] + baseX, _c0[1], _c0[2] + baseZ, 0,    0,    nx, ny, nz, layer, light);
                AppendVert(transparent, _c3[0] + baseX, _c3[1], _c3[2] + baseZ, uvX3, uvY3, nx, ny, nz, layer, light);
                AppendVert(transparent, _c2[0] + baseX, _c2[1], _c2[2] + baseZ, uvX2, uvY2, nx, ny, nz, layer, light);
                AppendVert(transparent, _c1[0] + baseX, _c1[1], _c1[2] + baseZ, uvX1, uvY1, nx, ny, nz, layer, light);
            }

            AppendIndex(transparent, baseIdx + 0);
            AppendIndex(transparent, baseIdx + 1);
            AppendIndex(transparent, baseIdx + 2);
            AppendIndex(transparent, baseIdx + 0);
            AppendIndex(transparent, baseIdx + 2);
            AppendIndex(transparent, baseIdx + 3);
        }

        private void AppendVert(
            bool transparent,
            float x, float y, float z,
            float u, float v,
            float nx, float ny, float nz,
            float layer,
            float light)
        {
            if (transparent)
            {
                if (_tVertFloats + Mesh.FloatsPerVertex > _tVerts.Length)
                    Array.Resize(ref _tVerts, _tVerts.Length * 2);
                _tVerts[_tVertFloats++] = x;
                _tVerts[_tVertFloats++] = y;
                _tVerts[_tVertFloats++] = z;
                _tVerts[_tVertFloats++] = u;
                _tVerts[_tVertFloats++] = v;
                _tVerts[_tVertFloats++] = nx;
                _tVerts[_tVertFloats++] = ny;
                _tVerts[_tVertFloats++] = nz;
                _tVerts[_tVertFloats++] = layer;
                _tVerts[_tVertFloats++] = light;
            }
            else
            {
                if (_vertFloats + Mesh.FloatsPerVertex > _verts.Length)
                    Array.Resize(ref _verts, _verts.Length * 2);
                _verts[_vertFloats++] = x;
                _verts[_vertFloats++] = y;
                _verts[_vertFloats++] = z;
                _verts[_vertFloats++] = u;
                _verts[_vertFloats++] = v;
                _verts[_vertFloats++] = nx;
                _verts[_vertFloats++] = ny;
                _verts[_vertFloats++] = nz;
                _verts[_vertFloats++] = layer;
                _verts[_vertFloats++] = light;
            }
        }

        private void AppendIndex(bool transparent, uint idx)
        {
            if (transparent)
            {
                if (_tIndexCount + 1 > _tIndices.Length)
                    Array.Resize(ref _tIndices, _tIndices.Length * 2);
                _tIndices[_tIndexCount++] = idx;
            }
            else
            {
                if (_indexCount + 1 > _indices.Length)
                    Array.Resize(ref _indices, _indices.Length * 2);
                _indices[_indexCount++] = idx;
            }
        }
    }
}
