using System;
using OpenTK.Graphics.OpenGL;

namespace VStudioCraft.Game
{
    internal sealed class Mesh : IDisposable
    {
        // Vertex layout: pos(3) + uv(2) + normal(3) + layer(1) = 9 floats.
        // Per-vertex cost went up by 4 bytes vs. the pre-greedy format, but greedy
        // meshing reduces vertex COUNT by ~5-10x, so total bytes drop sharply.
        public const int FloatsPerVertex = 9;

        private int _vao;
        private int _vbo;
        private int _ebo;
        private int _indexCount;

        public int IndexCount => _indexCount;

        // Upload a partially-filled scratch buffer. vertFloats is the number of floats
        // actually in use (multiple of FloatsPerVertex); indexCount is the number of uints.
        public void Upload(float[] vertices, int vertFloats, uint[] indices, int indexCount)
        {
            if (_vao == 0) _vao = GL.GenVertexArray();
            if (_vbo == 0) _vbo = GL.GenBuffer();
            if (_ebo == 0) _ebo = GL.GenBuffer();

            GL.BindVertexArray(_vao);

            GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
            GL.BufferData(BufferTarget.ArrayBuffer, vertFloats * sizeof(float), vertices, BufferUsageHint.StaticDraw);

            GL.BindBuffer(BufferTarget.ElementArrayBuffer, _ebo);
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

            GL.BindVertexArray(0);
            _indexCount = indexCount;
        }

        public void Draw()
        {
            if (_indexCount == 0) return;
            GL.BindVertexArray(_vao);
            GL.DrawElements(PrimitiveType.Triangles, _indexCount, DrawElementsType.UnsignedInt, 0);
            GL.BindVertexArray(0);
        }

        public void Dispose()
        {
            if (_vao != 0) GL.DeleteVertexArray(_vao);
            if (_vbo != 0) GL.DeleteBuffer(_vbo);
            if (_ebo != 0) GL.DeleteBuffer(_ebo);
            _vao = _vbo = _ebo = _indexCount = 0;
        }
    }
}
