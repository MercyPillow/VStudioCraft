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
        private float[] _verts = new float[1 << 14];
        private uint[] _indices = new uint[1 << 14];
        private int _vertFloats;
        private int _indexCount;

        // Slice mask big enough for the largest face (SizeY × max(SizeX, SizeZ)).
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

        public void Build(World world, Chunk chunk)
        {
            _vertFloats = 0;
            _indexCount = 0;

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
            Sweep(chunk, 0, +1, nxNeg, nxPos, nzNeg, nzPos, baseX, baseZ);
            Sweep(chunk, 0, -1, nxNeg, nxPos, nzNeg, nzPos, baseX, baseZ);
            Sweep(chunk, 1, +1, nxNeg, nxPos, nzNeg, nzPos, baseX, baseZ);
            Sweep(chunk, 1, -1, nxNeg, nxPos, nzNeg, nzPos, baseX, baseZ);
            Sweep(chunk, 2, +1, nxNeg, nxPos, nzNeg, nzPos, baseX, baseZ);
            Sweep(chunk, 2, -1, nxNeg, nxPos, nzNeg, nzPos, baseX, baseZ);
        }

        private static byte BlockAt(Chunk c, int lx, int y, int lz)
        {
            if ((uint)y >= Chunk.SizeY) return 0;
            if ((uint)lx < Chunk.SizeX && (uint)lz < Chunk.SizeZ)
                return c.RawBlocks[Chunk.Index(lx, y, lz)];
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
                    bool aSolid = a != (byte)BlockType.Air;
                    bool bSolid = b != (byte)BlockType.Air;

                    if (aSolid && !bSolid)
                    {
                        // Face of block `a` visible, pointing in `dir`.
                        int faceKind = FaceKindFor(axis, dir);
                        int layer = BlockData.GetTileIndex((BlockType)a, faceKind);
                        _mask[j * dU + i] = layer + 1;  // 0 = empty, so +1 bias
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

                        EmitQuad(axis, dir, slice, u, v, i, j, w, h, m - 1, baseX, baseZ);

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

            uint baseIdx = (uint)(_vertFloats / Mesh.FloatsPerVertex);

            if (dir > 0)
            {
                AppendVert(_c0[0] + baseX, _c0[1], _c0[2] + baseZ, 0,    0,    nx, ny, nz, layer);
                AppendVert(_c1[0] + baseX, _c1[1], _c1[2] + baseZ, uvX1, uvY1, nx, ny, nz, layer);
                AppendVert(_c2[0] + baseX, _c2[1], _c2[2] + baseZ, uvX2, uvY2, nx, ny, nz, layer);
                AppendVert(_c3[0] + baseX, _c3[1], _c3[2] + baseZ, uvX3, uvY3, nx, ny, nz, layer);
            }
            else
            {
                AppendVert(_c0[0] + baseX, _c0[1], _c0[2] + baseZ, 0,    0,    nx, ny, nz, layer);
                AppendVert(_c3[0] + baseX, _c3[1], _c3[2] + baseZ, uvX3, uvY3, nx, ny, nz, layer);
                AppendVert(_c2[0] + baseX, _c2[1], _c2[2] + baseZ, uvX2, uvY2, nx, ny, nz, layer);
                AppendVert(_c1[0] + baseX, _c1[1], _c1[2] + baseZ, uvX1, uvY1, nx, ny, nz, layer);
            }

            AppendIndex(baseIdx + 0);
            AppendIndex(baseIdx + 1);
            AppendIndex(baseIdx + 2);
            AppendIndex(baseIdx + 0);
            AppendIndex(baseIdx + 2);
            AppendIndex(baseIdx + 3);
        }

        private void AppendVert(
            float x, float y, float z,
            float u, float v,
            float nx, float ny, float nz,
            float layer)
        {
            if (_vertFloats + Mesh.FloatsPerVertex > _verts.Length)
            {
                Array.Resize(ref _verts, _verts.Length * 2);
            }
            _verts[_vertFloats++] = x;
            _verts[_vertFloats++] = y;
            _verts[_vertFloats++] = z;
            _verts[_vertFloats++] = u;
            _verts[_vertFloats++] = v;
            _verts[_vertFloats++] = nx;
            _verts[_vertFloats++] = ny;
            _verts[_vertFloats++] = nz;
            _verts[_vertFloats++] = layer;
        }

        private void AppendIndex(uint idx)
        {
            if (_indexCount + 1 > _indices.Length)
            {
                Array.Resize(ref _indices, _indices.Length * 2);
            }
            _indices[_indexCount++] = idx;
        }
    }
}
