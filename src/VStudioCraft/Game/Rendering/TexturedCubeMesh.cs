using System;
using OpenTK.Graphics.OpenGL;

namespace VStudioCraft.Game
{
    // Pos+UV mesh (5 floats per vertex). Used by the block-break crack overlay
    // to draw a slightly-inflated unit cube around the target block. The full
    // chunk Mesh class carries normals + light + atlas-layer too; for a single
    // overlay cube that's overkill, so we keep this one tiny.
    //
    // Attribute layout matches the crack-overlay shader:
    //   layout 0: aPos (3 floats)
    //   layout 1: aUV  (2 floats)
    internal sealed class TexturedCubeMesh : IDisposable
    {
        private int _vao;
        private int _vbo;

        public int VertexCount { get; private set; }
        public PrimitiveType Primitive { get; set; } = PrimitiveType.Triangles;

        public void Upload(float[] interleaved)
        {
            if (_vao == 0) _vao = GL.GenVertexArray();
            if (_vbo == 0) _vbo = GL.GenBuffer();

            GL.BindVertexArray(_vao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
            GL.BufferData(BufferTarget.ArrayBuffer, interleaved.Length * sizeof(float), interleaved, BufferUsageHint.StaticDraw);

            int stride = 5 * sizeof(float);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);
            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));
            GL.EnableVertexAttribArray(1);

            GL.BindVertexArray(0);
            VertexCount = interleaved.Length / 5;
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
