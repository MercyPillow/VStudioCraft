using OpenTK;

namespace VStudioCraft.Game
{
    // View-frustum in world space, expressed as 6 outward-facing planes. We extract
    // the planes from the clip matrix by taking linear combinations of its rows
    // (Gribb & Hartmann). Matches OpenTK's row-vector multiplication convention
    // (vp = view * proj), which produces the same clip-space as GLSL's proj * view * pos.
    internal struct Frustum
    {
        // Packed as 6 × (normalX, normalY, normalZ, d). Inside half-space when
        // n · p + d >= 0.
        private float _l0, _l1, _l2, _l3;  // left
        private float _r0, _r1, _r2, _r3;  // right
        private float _b0, _b1, _b2, _b3;  // bottom
        private float _t0, _t1, _t2, _t3;  // top
        private float _n0, _n1, _n2, _n3;  // near
        private float _f0, _f1, _f2, _f3;  // far

        public void UpdateFromViewProj(ref Matrix4 vp)
        {
            // Row access: OpenTK stores Matrix4 row-major, M.M<row><col> (1-indexed).
            // Plane extraction (OpenTK row-vector convention):
            //   left:   col0 + col3 (≥0 inside)
            //   right:  col3 - col0
            //   bottom: col1 + col3
            //   top:    col3 - col1
            //   near:   col2 + col3
            //   far:    col3 - col2
            SetPlane(out _l0, out _l1, out _l2, out _l3, vp.M14 + vp.M11, vp.M24 + vp.M21, vp.M34 + vp.M31, vp.M44 + vp.M41);
            SetPlane(out _r0, out _r1, out _r2, out _r3, vp.M14 - vp.M11, vp.M24 - vp.M21, vp.M34 - vp.M31, vp.M44 - vp.M41);
            SetPlane(out _b0, out _b1, out _b2, out _b3, vp.M14 + vp.M12, vp.M24 + vp.M22, vp.M34 + vp.M32, vp.M44 + vp.M42);
            SetPlane(out _t0, out _t1, out _t2, out _t3, vp.M14 - vp.M12, vp.M24 - vp.M22, vp.M34 - vp.M32, vp.M44 - vp.M42);
            SetPlane(out _n0, out _n1, out _n2, out _n3, vp.M14 + vp.M13, vp.M24 + vp.M23, vp.M34 + vp.M33, vp.M44 + vp.M43);
            SetPlane(out _f0, out _f1, out _f2, out _f3, vp.M14 - vp.M13, vp.M24 - vp.M23, vp.M34 - vp.M33, vp.M44 - vp.M43);
        }

        private static void SetPlane(out float a, out float b, out float c, out float d, float ax, float ay, float az, float aw)
        {
            float len = (float)System.Math.Sqrt(ax * ax + ay * ay + az * az);
            if (len < 1e-9f) { a = b = c = d = 0; return; }
            float inv = 1f / len;
            a = ax * inv; b = ay * inv; c = az * inv; d = aw * inv;
        }

        // AABB vs frustum using the "positive vertex" test: for each plane, pick the
        // AABB corner most aligned with the plane's normal; if it's on the negative
        // side, the whole AABB is outside.
        public bool Intersects(float minX, float minY, float minZ, float maxX, float maxY, float maxZ)
        {
            if (!PlaneIn(_l0, _l1, _l2, _l3, minX, minY, minZ, maxX, maxY, maxZ)) return false;
            if (!PlaneIn(_r0, _r1, _r2, _r3, minX, minY, minZ, maxX, maxY, maxZ)) return false;
            if (!PlaneIn(_b0, _b1, _b2, _b3, minX, minY, minZ, maxX, maxY, maxZ)) return false;
            if (!PlaneIn(_t0, _t1, _t2, _t3, minX, minY, minZ, maxX, maxY, maxZ)) return false;
            if (!PlaneIn(_n0, _n1, _n2, _n3, minX, minY, minZ, maxX, maxY, maxZ)) return false;
            if (!PlaneIn(_f0, _f1, _f2, _f3, minX, minY, minZ, maxX, maxY, maxZ)) return false;
            return true;
        }

        private static bool PlaneIn(
            float a, float b, float c, float d,
            float minX, float minY, float minZ, float maxX, float maxY, float maxZ)
        {
            float px = a >= 0 ? maxX : minX;
            float py = b >= 0 ? maxY : minY;
            float pz = c >= 0 ? maxZ : minZ;
            return a * px + b * py + c * pz + d >= 0;
        }
    }
}
