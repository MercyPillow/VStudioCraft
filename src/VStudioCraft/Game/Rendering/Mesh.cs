using System;
using OpenTK.Graphics.OpenGL;

namespace VStudioCraft.Game
{
    internal sealed class Mesh : IDisposable
    {
        // Vertex layout: pos(3) + uv(2) + normal(3) + layer(1) + light(1) = 10 floats.
        // The trailing light float is packed sky*16 + block (0..255), unpacked in
        // the vertex shader so the fragment receives separate interpolated sky and
        // block contributions. Greedy meshing still keeps overall byte count well
        // below pre-greedy single-quad-per-block, so the +4 bytes is cheap.
        public const int FloatsPerVertex = 10;

        // Opaque stream — drawn normally with depth write + depth test.
        private int _vao;
        private int _vbo;
        private int _ebo;
        private int _indexCount;

        // Transparent stream — drawn in a second pass (blending on, depth write off).
        private int _tVao;
        private int _tVbo;
        private int _tEbo;
        private int _tIndexCount;

        public int IndexCount => _indexCount;
        public int TransparentIndexCount => _tIndexCount;

        // Upload both streams (either may be empty). vertFloats/indexCount are counts
        // actually in use in the scratch buffers; extra capacity is ignored.
        public void Upload(
            float[] vertices, int vertFloats, uint[] indices, int indexCount,
            float[] tVertices, int tVertFloats, uint[] tIndices, int tIndexCount)
        {
            UploadStream(ref _vao, ref _vbo, ref _ebo, vertices, vertFloats, indices, indexCount);
            UploadStream(ref _tVao, ref _tVbo, ref _tEbo, tVertices, tVertFloats, tIndices, tIndexCount);
            _indexCount = indexCount;
            _tIndexCount = tIndexCount;
        }

        private static void UploadStream(ref int vao, ref int vbo, ref int ebo,
            float[] vertices, int vertFloats, uint[] indices, int indexCount)
        {
            if (indexCount == 0)
            {
                // Leave any existing VAO/VBO/EBO resources alive (cheap) but mark them empty.
                // Caller sets _indexCount = 0 which short-circuits Draw.
                return;
            }
            // P5 of the chunk-streaming smoothness work — track whether
            // this stream is being initialised (first upload) so we can
            // skip the redundant VertexAttribPointer setup on every
            // subsequent re-upload. The VAO already remembers the
            // attribute layout from the first bind, so reissuing 5
            // VertexAttribPointer + 5 EnableVertexAttribArray on every
            // chunk remesh is pure GL driver overhead.
            bool freshVao = (vao == 0);
            if (vao == 0) vao = GL.GenVertexArray();
            if (vbo == 0) vbo = GL.GenBuffer();
            if (ebo == 0) ebo = GL.GenBuffer();

            GL.BindVertexArray(vao);

            // P5 — orphan the existing buffer before refilling it. By
            // calling BufferData with IntPtr.Zero (no data, just
            // size+usage) we tell the driver "the previous contents
            // are scratch", letting it allocate a fresh storage block
            // instead of waiting for any in-flight GPU work that
            // referenced the old contents to finish. Then the second
            // BufferData with the actual data fills the new block.
            // Without this, an upload immediately after a Draw of the
            // same VBO can stall the CPU on an implicit GPU sync.
            int vbBytes = vertFloats * sizeof(float);
            GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
            GL.BufferData(BufferTarget.ArrayBuffer, vbBytes, IntPtr.Zero, BufferUsageHint.StaticDraw);
            GL.BufferData(BufferTarget.ArrayBuffer, vbBytes, vertices, BufferUsageHint.StaticDraw);

            int ibBytes = indexCount * sizeof(uint);
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, ebo);
            GL.BufferData(BufferTarget.ElementArrayBuffer, ibBytes, IntPtr.Zero, BufferUsageHint.StaticDraw);
            GL.BufferData(BufferTarget.ElementArrayBuffer, ibBytes, indices, BufferUsageHint.StaticDraw);

            // First-bind only — the VAO records the attrib pointers
            // against the currently-bound VBO, and that record stays
            // valid across subsequent BufferData calls on the same VBO.
            // No need to reissue these on every remesh.
            if (freshVao)
            {
                const int stride = FloatsPerVertex * sizeof(float);
                GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);
                GL.EnableVertexAttribArray(0);
                GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));
                GL.EnableVertexAttribArray(1);
                GL.VertexAttribPointer(2, 3, VertexAttribPointerType.Float, false, stride, 5 * sizeof(float));
                GL.EnableVertexAttribArray(2);
                GL.VertexAttribPointer(3, 1, VertexAttribPointerType.Float, false, stride, 8 * sizeof(float));
                GL.EnableVertexAttribArray(3);
                GL.VertexAttribPointer(4, 1, VertexAttribPointerType.Float, false, stride, 9 * sizeof(float));
                GL.EnableVertexAttribArray(4);
            }

            GL.BindVertexArray(0);
        }

        public void Draw()
        {
            if (_indexCount == 0) return;
            GL.BindVertexArray(_vao);
            GL.DrawElements(PrimitiveType.Triangles, _indexCount, DrawElementsType.UnsignedInt, 0);
            // Intentionally do NOT BindVertexArray(0) here — every other
            // draw site in the renderer binds its own VAO before drawing,
            // so leaving the previous binding is harmless and saves one
            // GL crossing per chunk. The chunk pass alone draws ~100
            // visible chunks per frame; eliminating the trailing rebind
            // halves the BindVertexArray traffic for that pass.
        }

        public void DrawTransparent()
        {
            if (_tIndexCount == 0) return;
            GL.BindVertexArray(_tVao);
            GL.DrawElements(PrimitiveType.Triangles, _tIndexCount, DrawElementsType.UnsignedInt, 0);
        }

        public void Dispose()
        {
            if (_vao != 0) GL.DeleteVertexArray(_vao);
            if (_vbo != 0) GL.DeleteBuffer(_vbo);
            if (_ebo != 0) GL.DeleteBuffer(_ebo);
            if (_tVao != 0) GL.DeleteVertexArray(_tVao);
            if (_tVbo != 0) GL.DeleteBuffer(_tVbo);
            if (_tEbo != 0) GL.DeleteBuffer(_tEbo);
            _vao = _vbo = _ebo = _indexCount = 0;
            _tVao = _tVbo = _tEbo = _tIndexCount = 0;
        }
    }
}
