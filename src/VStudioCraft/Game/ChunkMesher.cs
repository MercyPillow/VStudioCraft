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
            EmitModels(chunk, baseX, baseZ);

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
        private void EmitModels(Chunk chunk, int baseX, int baseZ)
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
                    // Tilted billboard variant — the cross sprite is rotated
                    // and translated so the base sits flush against the
                    // wall and the tip leans out into the cell. Same atlas
                    // tile as a floor torch; geometry differs only.
                    EmitWallTorchSprite(x + baseX, y, z + baseZ, t, layer, lightPacked);
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
                else
                {
                    EmitCrossSprite(x + baseX, y, z + baseZ, layer, lightPacked);
                }
            }
        }

        // Wall-torch model: two perpendicular tilted planes (cross-sprite)
        // through a shaft running from a base anchor on the wall surface to
        // a tip pulled toward the cell centre. Differences from the floor
        // torch: the shaft is tilted ~30° away from the wall (matches Alpha
        // 1.1.2's wall-torch lean), and the cross is offset toward the
        // mounting wall so the base is visually attached to it.
        //
        // The mounting wall is the cell face opposite the torch's facing
        // direction. e.g. TorchEast (faces +X) is mounted on the cell's
        // -X face. Vertices that poke into the wall block are occluded by
        // its solid cube faces — a slight overshoot is intentional so the
        // base reads as "embedded" rather than floating in front of the
        // wall with a visible gap.
        //
        // Each plane is emitted twice (front + back winding) so back-face
        // culling doesn't hide either side, matching EmitCrossSprite. The
        // normal is forced to +Y so per-axis face shading picks the
        // brightest (top) bias — same trick as the floor torch.
        private void EmitWallTorchSprite(float wx, float wy, float wz, BlockType type, int layer, int lightPacked)
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

            // Base anchor on the wall (offset 0.05 from the wall face to
            // avoid z-fight) and tip pulled toward the cell centre. The
            // 0.7-unit shaft + ~0.4-unit horizontal tilt gives the
            // recognisable "torch leaning off a wall" silhouette without
            // crossing into the opposite-cell airspace.
            float bx = wx + 0.5f - dx * 0.45f;
            float by = wy + 0.2f;
            float bz = wz + 0.5f - dz * 0.45f;
            float tx = wx + 0.5f - dx * 0.05f;
            float ty = wy + 0.9f;
            float tz = wz + 0.5f - dz * 0.05f;

            // Half-width of the cross sprite (the torch tile is rendered
            // 0.5 units across — same effective scale as floor torches).
            const float halfW = 0.25f;

            // Width axis: perpendicular to facing, horizontal. Runs along
            // ±Z for east/west torches, along ±X for north/south torches.
            float wdx = -dz * halfW, wdz = dx * halfW;

            // Shaft vector (base → tip).
            float sx = tx - bx, sy = ty - by, sz = tz - bz;

            // Depth axis: perpendicular to both shaft and width via cross
            // product (with width's unit-length form). Provides the second
            // cross-sprite plane that gives the torch volume when viewed
            // along the wall's normal direction.
            float wux = -dz, wuz = dx; // unit-length width
            float dpx = sy * wuz;                  // sy*wuz - sz*0
            float dpy = sz * wux - sx * wuz;
            float dpz = -sy * wux;                 // sx*0 - sy*wux
            float dLen = (float)Math.Sqrt(dpx * dpx + dpy * dpy + dpz * dpz);
            if (dLen > 1e-6f) { dpx /= dLen; dpy /= dLen; dpz /= dLen; }
            dpx *= halfW; dpy *= halfW; dpz *= halfW;

            const float nx = 0f, ny = 1f, nz = 0f;

            // Plane A: shaft × width (the broad face seen along the wall).
            EmitCrossQuad(
                bx - wdx, by, bz - wdz,  0f, 0f,
                bx + wdx, by, bz + wdz,  1f, 0f,
                tx + wdx, ty, tz + wdz,  1f, 1f,
                tx - wdx, ty, tz - wdz,  0f, 1f,
                nx, ny, nz, layer, lightPacked);
            EmitCrossQuad( // back side
                bx + wdx, by, bz + wdz,  0f, 0f,
                bx - wdx, by, bz - wdz,  1f, 0f,
                tx - wdx, ty, tz - wdz,  1f, 1f,
                tx + wdx, ty, tz + wdz,  0f, 1f,
                nx, ny, nz, layer, lightPacked);

            // Plane B: shaft × depth (the narrow face seen looking back at
            // the wall along the facing direction).
            EmitCrossQuad(
                bx - dpx, by - dpy, bz - dpz,  0f, 0f,
                bx + dpx, by + dpy, bz + dpz,  1f, 0f,
                tx + dpx, ty + dpy, tz + dpz,  1f, 1f,
                tx - dpx, ty - dpy, tz - dpz,  0f, 1f,
                nx, ny, nz, layer, lightPacked);
            EmitCrossQuad( // back side
                bx + dpx, by + dpy, bz + dpz,  0f, 0f,
                bx - dpx, by - dpy, bz - dpz,  1f, 0f,
                tx - dpx, ty - dpy, tz - dpz,  1f, 1f,
                tx + dpx, ty + dpy, tz + dpz,  0f, 1f,
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

            // Six box faces. UVs sample the full tile [0..1] on the two
            // broad faces; the four thin edges sample a 3/16-wide UV
            // strip from the same tile so they pick up an in-palette
            // colour without obvious texture distortion.
            // -X face
            EmitCrossQuad(
                x0, y0, z1, 0f, 0f,
                x0, y0, z0, 1f, 0f,
                x0, y1, z0, 1f, 1f,
                x0, y1, z1, 0f, 1f,
                -1f, 0f, 0f, layer, lightPacked);
            // +X face
            EmitCrossQuad(
                x1, y0, z0, 0f, 0f,
                x1, y0, z1, 1f, 0f,
                x1, y1, z1, 1f, 1f,
                x1, y1, z0, 0f, 1f,
                +1f, 0f, 0f, layer, lightPacked);
            // -Z face
            EmitCrossQuad(
                x0, y0, z0, 0f, 0f,
                x1, y0, z0, 1f, 0f,
                x1, y1, z0, 1f, 1f,
                x0, y1, z0, 0f, 1f,
                0f, 0f, -1f, layer, lightPacked);
            // +Z face
            EmitCrossQuad(
                x1, y0, z1, 0f, 0f,
                x0, y0, z1, 1f, 0f,
                x0, y1, z1, 1f, 1f,
                x1, y1, z1, 0f, 1f,
                0f, 0f, +1f, layer, lightPacked);
            // +Y face (top edge of slab)
            EmitCrossQuad(
                x0, y1, z1, 0f, 0f,
                x1, y1, z1, 1f, 0f,
                x1, y1, z0, 1f, 1f,
                x0, y1, z0, 0f, 1f,
                0f, +1f, 0f, layer, lightPacked);
            // -Y face (bottom edge of slab)
            EmitCrossQuad(
                x0, y0, z0, 0f, 0f,
                x1, y0, z0, 1f, 0f,
                x1, y0, z1, 1f, 1f,
                x0, y0, z1, 0f, 1f,
                0f, -1f, 0f, layer, lightPacked);
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

            // -X face — reversed winding (CCW from -X viewpoint)
            EmitCrossQuad(
                x0, y0, z0, 0f, sideV0,
                x0, y0, z1, 1f, sideV0,
                x0, y1, z1, 1f, sideV1,
                x0, y1, z0, 0f, sideV1,
                -1f, 0f, 0f, layer, lightPacked);
            // +X face
            EmitCrossQuad(
                x1, y0, z0, 0f, sideV0,
                x1, y0, z1, 1f, sideV0,
                x1, y1, z1, 1f, sideV1,
                x1, y1, z0, 0f, sideV1,
                +1f, 0f, 0f, layer, lightPacked);
            // -Z face — reversed winding (CCW from -Z viewpoint)
            EmitCrossQuad(
                x1, y0, z0, 0f, sideV0,
                x0, y0, z0, 1f, sideV0,
                x0, y1, z0, 1f, sideV1,
                x1, y1, z0, 0f, sideV1,
                0f, 0f, -1f, layer, lightPacked);
            // +Z face
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
