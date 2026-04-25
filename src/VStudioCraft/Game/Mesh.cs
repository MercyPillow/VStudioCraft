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
            if (vao == 0) vao = GL.GenVertexArray();
            if (vbo == 0) vbo = GL.GenBuffer();
            if (ebo == 0) ebo = GL.GenBuffer();

            GL.BindVertexArray(vao);

            GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
            GL.BufferData(BufferTarget.ArrayBuffer, vertFloats * sizeof(float), vertices, BufferUsageHint.StaticDraw);

            GL.BindBuffer(BufferTarget.ElementArrayBuffer, ebo);
            GL.BufferData(BufferTarget.ElementArrayBuffer, indexCount * sizeof(uint), indices, BufferUsageHint.StaticDraw);

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

            GL.BindVertexArray(0);
        }

        public void Draw()
        {
            if (_indexCount == 0) return;
            GL.BindVertexArray(_vao);
            GL.DrawElements(PrimitiveType.Triangles, _indexCount, DrawElementsType.UnsignedInt, 0);
            GL.BindVertexArray(0);
        }

        public void DrawTransparent()
        {
            if (_tIndexCount == 0) return;
            GL.BindVertexArray(_tVao);
            GL.DrawElements(PrimitiveType.Triangles, _tIndexCount, DrawElementsType.UnsignedInt, 0);
            GL.BindVertexArray(0);
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
