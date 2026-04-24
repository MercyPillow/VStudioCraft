using System;
using OpenTK.Graphics.OpenGL;

namespace VStudioCraft.Game
{
    // Position-only mesh (3 floats per vertex). Primitive is configurable so the
    // same class serves both the crosshair triangles and the selection-outline lines.
    internal sealed class OverlayMesh : IDisposable
    {
        private int _vao;
        private int _vbo;

        public int VertexCount { get; private set; }
        public PrimitiveType Primitive { get; set; } = PrimitiveType.Triangles;

        public void Upload(float[] positions)
        {
            if (_vao == 0) _vao = GL.GenVertexArray();
            if (_vbo == 0) _vbo = GL.GenBuffer();

            GL.BindVertexArray(_vao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
            GL.BufferData(BufferTarget.ArrayBuffer, positions.Length * sizeof(float), positions, BufferUsageHint.StaticDraw);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0);
            GL.EnableVertexAttribArray(0);
            GL.BindVertexArray(0);
            VertexCount = positions.Length / 3;
        }

        public void Draw()
        {
            if (VertexCount == 0) return;
            GL.BindVertexArray(_vao);
            GL.DrawArrays(Primitive, 0, VertexCount);
            GL.BindVertexArray(0);
        }

        public void Dispose()
        {
            if (_vao != 0) GL.DeleteVertexArray(_vao);
            if (_vbo != 0) GL.DeleteBuffer(_vbo);
            _vao = _vbo = VertexCount = 0;
        }
    }
}
