using System;
using OpenTK.Graphics.OpenGL;

namespace VStudioCraft.Game
{
    // Textured cuboid mesh for player-skin rendering. 24 vertices (4 per
    // face) carrying pos(3) + uv(2), 36 indices (2 triangles per face).
    // The mesh is built once at GL-init time and drawn many times per
    // frame with different per-instance transforms (rig pose, swing
    // animation), so it lives in a static VBO.
    //
    // The cuboid is centred on the X/Z axes and sits with feet at Y=0
    // (i.e. local origin = bottom-centre). Caller positions it via the
    // rig-to-world transform; the per-cuboid offset is applied to the
    // bottom-centre, not the volumetric centre, so the values match
    // intuition for "Y=0.75 means hips".
    //
    // UV mapping follows the canonical Minecraft skin layout — for a
    // body part of pixel size (w_px × h_px × d_px) anchored at skin
    // pixel (u0, v0), each face draws its rectangle of the texture with
    // the orientation a cube unfolded onto the texture would have. See
    // the BuildBodyPart docstring for the per-face pixel coords.
    internal sealed class SkinCuboidMesh : IDisposable
    {
        private int _vao;
        private int _vbo;
        private int _ebo;
        private int _indexCount;

        public int IndexCount => _indexCount;

        // Build a textured cuboid for one humanoid body part.
        //
        //   wWorld, hWorld, dWorld — cuboid dimensions in world units
        //                            (1 m = 1 block in this engine).
        //   u0Px, v0Px              — top-left of the body part's region
        //                            in skin pixel coords. e.g. (0,0)
        //                            for the head, (16,16) for body.
        //   wPx, hPx, dPx           — body part's pixel dimensions in
        //                            the skin (head = 8,8,8; body =
        //                            8,12,4; arm/leg = 4,12,4).
        //   texW, texH              — full skin texture size (64,64 or
        //                            64,32). Used to convert pixel
        //                            coords to UV.
        //   mirror                  — true → swap right/left face UVs
        //                            and horizontally flip front/back
        //                            UVs, so the right-arm texture
        //                            shows "correctly" when this mesh
        //                            is positioned as the LEFT arm
        //                            (the Alpha-era convention: left
        //                            limbs reuse right-limb textures
        //                            mirrored).
        public static SkinCuboidMesh BuildBodyPart(
            float wWorld, float hWorld, float dWorld,
            int u0Px, int v0Px, int wPx, int hPx, int dPx,
            int texW, int texH, bool mirror)
        {
            // Half-extents centred on X/Z; feet at Y=0.
            float hx = wWorld * 0.5f;
            float hz = dWorld * 0.5f;
            float y0 = 0f;
            float y1 = hWorld;

            // Pixel-coord rectangles for each face (top-left + size in
            // pixels). The "unfolded cube" layout puts the side faces
            // in a horizontal strip at v0+dPx (height = hPx), with the
            // top + bottom strip just above (v0..v0+dPx, height = dPx).
            //
            //  +-----+-----+--------+-----+
            //  |     |TOP  |BOTTOM  |     |    (v0..v0+dPx)
            //  +-----+-----+--------+-----+
            //  |RIGHT|FRONT|LEFT    |BACK |    (v0+dPx..v0+dPx+hPx)
            //  +-----+-----+--------+-----+
            //  ^     ^     ^        ^     ^
            //  u0    u0+dPx u0+dPx  u0+   u0+
            //               +wPx    2dPx  2dPx
            //                       +wPx  +2wPx
            int sideRowV = v0Px + dPx;
            int rightU   = u0Px;
            int frontU   = u0Px + dPx;
            int leftU    = u0Px + dPx + wPx;
            int backU    = u0Px + 2 * dPx + wPx;
            int topU     = u0Px + dPx;
            int bottomU  = u0Px + dPx + wPx;

            // For mirrored cuboids (left limbs in classic format), the
            // visible "outside" face of the left arm is the +X face,
            // but the canonical texture for that face lives in the
            // right-arm strip. Swap +X/-X face UV regions and flip
            // front/back horizontally so the model still reads
            // correctly when rendered at the LEFT arm position.
            int rightFaceU = mirror ? leftU  : rightU;
            int leftFaceU  = mirror ? rightU : leftU;

            // Convert pixel rects to UV [0,1] coords.
            float invW = 1f / texW;
            float invH = 1f / texH;
            float pxU = invW;  // 1-pixel UV step in U
            float pxV = invH;  // 1-pixel UV step in V

            // Pre-compute the 6 face UV rectangles as (uMin, vMin, uMax, vMax).
            // V coords: pixel y=0 is the top of the texture, and our
            // upload sends row 0 first, so UV v=0 corresponds to texture
            // y=0 (the top). Don't V-flip.
            float frU0 = frontU * pxU,    frV0 = sideRowV * pxV,    frU1 = (frontU + wPx) * pxU,    frV1 = (sideRowV + hPx) * pxV;
            float bkU0 = backU * pxU,     bkV0 = sideRowV * pxV,    bkU1 = (backU + wPx) * pxU,     bkV1 = (sideRowV + hPx) * pxV;
            float rtU0 = rightFaceU * pxU,rtV0 = sideRowV * pxV,    rtU1 = (rightFaceU + dPx) * pxU,rtV1 = (sideRowV + hPx) * pxV;
            float ltU0 = leftFaceU * pxU, ltV0 = sideRowV * pxV,    ltU1 = (leftFaceU + dPx) * pxU, ltV1 = (sideRowV + hPx) * pxV;
            float tpU0 = topU * pxU,      tpV0 = v0Px * pxV,        tpU1 = (topU + wPx) * pxU,      tpV1 = (v0Px + dPx) * pxV;
            float btU0 = bottomU * pxU,   btV0 = v0Px * pxV,        btU1 = (bottomU + wPx) * pxU,   btV1 = (v0Px + dPx) * pxV;

            // For mirrored, flip front/back UV horizontally so the
            // chest-side details (the "front" texture) appear on the
            // arm-outside face correctly.
            if (mirror)
            {
                float t;
                t = frU0; frU0 = frU1; frU1 = t;
                t = bkU0; bkU0 = bkU1; bkU1 = t;
            }

            // Vertex layout per vertex: x, y, z, u, v (5 floats).
            // Faces wound CCW when viewed from outside, so back-face
            // culling (which is on for the world pass) hides interior
            // surfaces correctly. UVs map "natural reading order" on
            // each face: top-left corner of the texture rect → top-
            // left corner of the face when looking AT it from outside.
            //
            // Vertex order in each face:
            //   v0 = top-left (uMin, vMin)
            //   v1 = bottom-left (uMin, vMax)
            //   v2 = bottom-right (uMax, vMax)
            //   v3 = top-right (uMax, vMin)
            //
            // Indices: (v0, v1, v2), (v0, v2, v3) — CCW from outside.

            float[] verts = new float[24 * 5];
            int idx = 0;
            void Put(float x, float y, float z, float u, float v)
            {
                verts[idx++] = x; verts[idx++] = y; verts[idx++] = z;
                verts[idx++] = u; verts[idx++] = v;
            }

            // FRONT face (+Z). Looking from +Z toward origin, +X is to
            // the LEFT (because the rig faces away from camera and we
            // want the texture's right-edge to map to the rig's right).
            // Actually no — looking at the rig from +Z (the camera side
            // when player faces away), +X in world is to the viewer's
            // LEFT. Standard MC convention: front face's UV +U direction
            // matches +X in rig space. So:
            //   top-left of UV → (-X,+Y,+Z), top-right → (+X,+Y,+Z),
            //   bottom-left → (-X,0,+Z), bottom-right → (+X,0,+Z).
            Put(-hx, y1, +hz, frU0, frV0); // 0: TL
            Put(-hx, y0, +hz, frU0, frV1); // 1: BL
            Put(+hx, y0, +hz, frU1, frV1); // 2: BR
            Put(+hx, y1, +hz, frU1, frV0); // 3: TR

            // BACK face (-Z). Viewed from -Z (looking toward +Z),
            // +X in rig space is to the viewer's RIGHT. UV's +U
            // matches viewer's right → +X in rig space.
            // Wait — Minecraft mirrors the back: the texture's
            // +U maps to -X. Standard layout: when standing
            // BEHIND the rig, the back panel's left edge is the
            // viewer's left = rig's +X. So back face UVs:
            //   top-left → (+X,+Y,-Z), top-right → (-X,+Y,-Z).
            Put(+hx, y1, -hz, bkU0, bkV0); // 4
            Put(+hx, y0, -hz, bkU0, bkV1); // 5
            Put(-hx, y0, -hz, bkU1, bkV1); // 6
            Put(-hx, y1, -hz, bkU1, bkV0); // 7

            // RIGHT face (+X) — the rig's right side. Viewed from +X,
            // +Z (front) is to the viewer's LEFT. UV +U maps to -Z (so
            // texture goes back-to-front).
            // RIGHT face (+X). Viewed from outside (+X side, looking
            // toward −X) with up=+Y, the right-hand-rule view basis
            // gives viewer-right = forward × up = −X × +Y = −Z. So
            // viewer-right = world −Z, viewer-left = world +Z.
            // Texture orientation:
            //   top-left  (uMin,vMin) → viewer-left+up   = (+X,+Y,+Z)
            //   top-right (uMax,vMin) → viewer-right+up  = (+X,+Y,−Z)
            //   bot-left  (uMin,vMax) → viewer-left+down = (+X, 0,+Z)
            //   bot-right (uMax,vMax) → viewer-right+down= (+X, 0,−Z)
            // For CCW-from-outside winding, list vertices in increasing
            // 2D-screen-angle order: TR(45°)→TL(135°)→BL(225°)→BR(315°).
            Put(+hx, y1, -hz, rtU1, rtV0); //  8: TR
            Put(+hx, y1, +hz, rtU0, rtV0); //  9: TL
            Put(+hx, y0, +hz, rtU0, rtV1); // 10: BL
            Put(+hx, y0, -hz, rtU1, rtV1); // 11: BR

            // LEFT face (−X). Viewed from outside (−X side, looking +X)
            // with up=+Y, viewer-right = forward × up = +X × +Y = +Z.
            // So viewer-right = world +Z, viewer-left = world −Z.
            // Texture orientation:
            //   top-left  → (−X,+Y,−Z), top-right → (−X,+Y,+Z)
            //   bot-left  → (−X, 0,−Z), bot-right → (−X, 0,+Z)
            // Same CCW-from-outside ordering as the right face.
            Put(-hx, y1, +hz, ltU1, ltV0); // 12: TR
            Put(-hx, y1, -hz, ltU0, ltV0); // 13: TL
            Put(-hx, y0, -hz, ltU0, ltV1); // 14: BL
            Put(-hx, y0, +hz, ltU1, ltV1); // 15: BR

            // TOP face (+Y). Viewed from above looking down, +X is
            // right and +Z (front) points TOWARD the viewer's chin
            // (i.e. "down" in 2D sense). Standard MC: top face UV
            // +U → +X, +V → +Z.
            //   top-left = (-X, +Y, -Z), top-right = (+X, +Y, -Z),
            //   bottom-left = (-X, +Y, +Z), bottom-right = (+X, +Y, +Z).
            Put(-hx, y1, -hz, tpU0, tpV0); // 16
            Put(-hx, y1, +hz, tpU0, tpV1); // 17
            Put(+hx, y1, +hz, tpU1, tpV1); // 18
            Put(+hx, y1, -hz, tpU1, tpV0); // 19

            // BOTTOM face (-Y). Viewed from below looking up, +X is
            // right and +Z is "up" in 2D. UV +U → +X, +V → -Z (so the
            // bottom face is mirrored along Z relative to the top —
            // matches the canonical skin "bottom is upside-down" rule).
            //   top-left = (-X, 0, +Z), top-right = (+X, 0, +Z),
            //   bottom-left = (-X, 0, -Z), bottom-right = (+X, 0, -Z).
            Put(-hx, y0, +hz, btU0, btV0); // 20
            Put(-hx, y0, -hz, btU0, btV1); // 21
            Put(+hx, y0, -hz, btU1, btV1); // 22
            Put(+hx, y0, +hz, btU1, btV0); // 23

            // Index buffer — 6 faces × 2 triangles × 3 indices = 36.
            // CCW winding when viewed from outside.
            uint[] indices = new uint[36];
            int ii = 0;
            for (uint f = 0; f < 6; f++)
            {
                uint b = f * 4;
                indices[ii++] = b + 0; indices[ii++] = b + 1; indices[ii++] = b + 2;
                indices[ii++] = b + 0; indices[ii++] = b + 2; indices[ii++] = b + 3;
            }

            var m = new SkinCuboidMesh();
            m.Upload(verts, indices);
            return m;
        }

        private void Upload(float[] verts, uint[] indices)
        {
            if (_vao == 0) _vao = GL.GenVertexArray();
            if (_vbo == 0) _vbo = GL.GenBuffer();
            if (_ebo == 0) _ebo = GL.GenBuffer();

            GL.BindVertexArray(_vao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
            GL.BufferData(BufferTarget.ArrayBuffer, verts.Length * sizeof(float), verts, BufferUsageHint.StaticDraw);
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, _ebo);
            GL.BufferData(BufferTarget.ElementArrayBuffer, indices.Length * sizeof(uint), indices, BufferUsageHint.StaticDraw);

            const int stride = 5 * sizeof(float);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);
            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));
            GL.EnableVertexAttribArray(1);

            GL.BindVertexArray(0);
            _indexCount = indices.Length;
        }

        public void Draw()
        {
            if (_indexCount == 0) return;
            GL.BindVertexArray(_vao);
            GL.DrawElements(PrimitiveType.Triangles, _indexCount, DrawElementsType.UnsignedInt, 0);
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
